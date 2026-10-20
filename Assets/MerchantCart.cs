using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Обоз купца (или гильдии): повозка с лошадью и возницей. Всё, что продано и куплено на рынке, едет на ней.
///  1. Купец едет из своего города (ArrivalDelay секунд за кадром). Пока в городе война - ждёт за краем карты.
///  2. Появляется на случайном краю карты и едет к двери Торгового дома.
///     Привёз покупку, а Торгового дома нет - едет к Ратуше и сгружает там.
///     Едет забрать проданное, а Торговый дом закрыт (руины) - ждёт; не дождался - сделка срывается (onFail).
///  3. У двери - погрузка / разгрузка LoadSeconds, тут срабатывает onArrive (марки, товар).
///  4. Уезжает за край карты и исчезает.
/// Враги обоз не трогают (купцов охраняют). Если valid() стал false, обоз молча разворачивается.
/// </summary>
public class MerchantCart : MonoBehaviour
{
    public static readonly List<MerchantCart> All = new List<MerchantCart>();

    const float Speed = 2.2f;
    const float LoadSeconds = 2.5f;
    const float WaitClosed = 120f;     // сколько ждать у закрытого Торгового дома

    enum Mode { Away, Inbound, Loading, Outbound }

    Mode mode;
    float timer, doorTimer, waitClosed, face = 1f;
    Vector2 edge, doorPoint, lastPos;
    bool ford;   // с этого края до цели не доехать по суше (реки без моста) - обоз переходит реку вброд, медленно
    bool needMarket;                  // true - едет ЗАБРАТЬ товар (только к Торговому дому)
    System.Func<bool> valid;
    System.Action<BuildingInstance> onArrive;
    System.Action onFail;
    BuildingInstance target;
    GameObject gfx;
    readonly GridMover mover = new GridMover();

    public Merchant Who { get; private set; }
    public string Purpose { get; private set; }

    /// <summary>
    /// Отправить обоз. delay - сколько едет до края карты. needMarket - забрать товар (нужен Торговый дом).
    /// valid - сделка ещё в силе. onArrive(здание, куда приехал) - погрузка/разгрузка. onFail - сделка сорвалась.
    /// </summary>
    public static MerchantCart Send(Merchant who, float delay, bool needMarket, string purpose,
                                    System.Func<bool> valid, System.Action<BuildingInstance> onArrive, System.Action onFail)
    {
        GameObject go = new GameObject("MerchantCart");
        MerchantCart c = go.AddComponent<MerchantCart>();
        c.Who = who;
        c.Purpose = purpose;
        c.needMarket = needMarket;
        c.valid = valid;
        c.onArrive = onArrive;
        c.onFail = onFail;
        c.mode = Mode.Away;
        c.timer = delay;
        c.Build(who.color);
        c.SetVisible(false);
        return c;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public string StatusText
    {
        get
        {
            switch (mode)
            {
                case Mode.Away:
                    if (timer <= 0f && WarManager.IsWar) return "ждёт конца войны за краем карты";
                    if (timer <= 0f && needMarket && !TradeGuild.Open) return "ждёт, пока откроется Торговый дом";
                    return $"в дороге, ещё ~{Mathf.CeilToInt(Mathf.Max(0f, timer))} с";
                case Mode.Inbound: return waitClosed > 0f ? "ждёт у закрытого Торгового дома" : "едет по городу";
                case Mode.Loading: return needMarket ? "грузит товар" : "разгружает товар";
                default: return "уезжает";
            }
        }
    }

    // ---------- Внешний вид ----------

    void Build(Color cover)
    {
        gfx = new GameObject("Gfx");
        gfx.transform.SetParent(transform, false);
        Part("Body", new Vector2(0f, 0f), new Vector2(0.62f, 0.38f), new Color(0.5f, 0.33f, 0.17f), 11);
        Part("Cover", new Vector2(-0.04f, 0.12f), new Vector2(0.44f, 0.3f), cover, 12);
        Part("Wheel", new Vector2(0.16f, -0.19f), new Vector2(0.16f, 0.16f), new Color(0.15f, 0.1f, 0.06f), 12);
        Part("Wheel2", new Vector2(-0.2f, -0.19f), new Vector2(0.16f, 0.16f), new Color(0.15f, 0.1f, 0.06f), 12);
        Part("Horse", new Vector2(0.56f, 0f), new Vector2(0.34f, 0.2f), new Color(0.4f, 0.26f, 0.13f), 12);
        Part("Head", new Vector2(0.76f, 0.1f), new Vector2(0.13f, 0.13f), new Color(0.3f, 0.19f, 0.1f), 13);
        Part("Driver", new Vector2(0.24f, 0.24f), new Vector2(0.17f, 0.17f), new Color(0.95f, 0.85f, 0.7f), 14);
        Part("Hat", new Vector2(0.24f, 0.35f), new Vector2(0.2f, 0.06f), cover * 0.7f, 15);
    }

    void Part(string n, Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(gfx.transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        c.a = 1f;
        sr.color = c;
        sr.sortingOrder = order;
    }

    void SetVisible(bool v) { if (gfx != null) gfx.SetActive(v); }

    // ---------- Куда ехать ----------

    BuildingInstance PickTarget()
    {
        if (TradeGuild.Open) return TradeGuild.House.Building;
        if (needMarket) return null;                       // забрать товар можно только из Торгового дома
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    // ---------- Движение ----------

    void Update()
    {
        // Вброд обоз ищет путь и шагает только когда иначе не доехать (см. ford)
        bool was = GridNavigation.WaderQuery;
        GridNavigation.WaderQuery = ford;
        try { Tick(); }
        finally { GridNavigation.WaderQuery = was; }
    }

    void Tick()
    {
        var nav = GridNavigation.Instance;
        float dt = Time.deltaTime;

        if (mode != Mode.Outbound && valid != null && !valid())
        {
            // Сделка отменена - тихо уезжаем
            onArrive = null;
            onFail = null;
            if (mode == Mode.Away) { Destroy(gameObject); return; }
            mode = Mode.Outbound;
            mover.Clear();
        }

        Vector2 pos = transform.position;
        if (Mathf.Abs(pos.x - lastPos.x) > 0.002f)
        {
            face = pos.x > lastPos.x ? 1f : -1f;
            if (gfx != null) gfx.transform.localScale = new Vector3(face, 1f, 1f);
        }
        lastPos = pos;

        switch (mode)
        {
            case Mode.Away:
            {
                timer -= dt;
                if (timer > 0f) return;
                if (WarManager.IsWar) return;   // в войну купцы в город не заходят

                target = PickTarget();
                if (target == null)
                {
                    if (needMarket)
                    {
                        waitClosed += dt;
                        if (waitClosed > WaitClosed) Fail();
                        return;
                    }
                    Arrive(null);   // ни Торгового дома, ни Ратуши - сделка закрывается сразу
                    return;
                }
                waitClosed = 0f;
                if (nav == null) { Arrive(target); return; }

                edge = RandomEdge(nav, target, out ford);
                transform.position = new Vector3(edge.x, edge.y, 0f);
                lastPos = edge;
                SetVisible(true);
                mover.Clear();
                mover.ResetFails();
                doorTimer = 0f;
                mode = Mode.Inbound;
                return;
            }

            case Mode.Inbound:
            {
                BuildingInstance t = PickTarget();
                if (t == null)
                {
                    // Торговый дом закрылся, пока ехали: ждём на месте
                    waitClosed += dt;
                    if (waitClosed > WaitClosed) { Fail(); }
                    return;
                }
                waitClosed = 0f;
                if (t != target) { target = t; doorTimer = 0f; mover.Clear(); }

                doorTimer -= dt;
                if (doorTimer <= 0f)
                {
                    doorTimer = 2f;
                    doorPoint = DoorPoint(nav, target, transform.position);
                }
                bool arrived = mover.Step(transform, nav, doorPoint, Speed, 0.3f);
                if (arrived || mover.Fails >= 6)
                {
                    mode = Mode.Loading;
                    timer = LoadSeconds;
                    mover.Clear();
                }
                return;
            }

            case Mode.Loading:
            {
                timer -= dt;
                if (timer > 0f) return;
                Arrive(target);
                return;
            }

            case Mode.Outbound:
            {
                if (nav == null) { Destroy(gameObject); return; }
                bool done = mover.Step(transform, nav, edge, Speed, 0.3f);
                if (done || mover.Fails >= 6 || EdgeDistance(transform.position, nav) < 1.0f) Destroy(gameObject);
                return;
            }
        }
    }

    void Arrive(BuildingInstance at)
    {
        System.Action<BuildingInstance> cb = onArrive;
        onArrive = null;
        onFail = null;
        if (cb != null) cb(at);

        if (!gfx.activeSelf) { Destroy(gameObject); return; }
        mode = Mode.Outbound;
        mover.Clear();
        mover.ResetFails();
    }

    void Fail()
    {
        System.Action cb = onFail;
        onFail = null;
        onArrive = null;
        if (cb != null) cb();
        if (gfx == null || !gfx.activeSelf) { Destroy(gameObject); return; }
        mode = Mode.Outbound;
        mover.Clear();
    }

    void OnDestroy()
    {
        // Обоз удалили, не закончив дело (смена сцены и т.п.) - сделка срывается, товары не теряются
        if (onFail != null) { System.Action cb = onFail; onFail = null; cb(); }
    }

    // ---------- Геометрия ----------

    static Vector2 DoorPoint(GridNavigation nav, BuildingInstance b, Vector2 from)
    {
        if (nav != null && b != null && nav.TryGetDoorCell(b, nav.WorldToCell(from), out Vector2Int d)) return nav.CellCenter(d);
        return b != null ? b.Center : from;
    }

    // Случайный край карты, с которого до цели можно доехать по суше (по мостам).
    // Реки делят карту: если такого края нет, needFord = true - обоз пойдёт вброд.
    static Vector2 RandomEdge(GridNavigation nav, BuildingInstance target, out bool needFord)
    {
        needFord = false;
        if (target == null || !Rivers.Any) return RandomEdge(nav);

        Vector2Int from = nav.WorldToCell(target.Center);
        Vector2Int door = nav.TryGetDoorCell(target, from, out Vector2Int d) ? d : from;
        int region = nav.RegionAt(door);
        if (region == 0) return RandomEdge(nav);

        for (int i = 0; i < 16; i++)
        {
            Vector2 e = RandomEdge(nav);
            if (nav.RegionAt(nav.WorldToCell(e)) == region) return e;
        }
        needFord = true;
        return RandomEdge(nav);
    }

    // Случайная точка на краю карты (купцы едут с разных сторон)
    static Vector2 RandomEdge(GridNavigation nav)
    {
        float h = nav.mapSize / 2f - 0.5f;
        float t = Random.Range(-h * 0.8f, h * 0.8f);
        Vector2 e;
        switch (Random.Range(0, 4))
        {
            case 0: e = new Vector2(-h, t); break;
            case 1: e = new Vector2(h, t); break;
            case 2: e = new Vector2(t, -h); break;
            default: e = new Vector2(t, h); break;
        }
        Vector2Int c = nav.WorldToCell(e);
        if (nav.IsBlocked(c)) c = nav.NearestFree(c);
        return nav.CellCenter(c);
    }

    static float EdgeDistance(Vector2 p, GridNavigation nav)
    {
        float h = nav.mapSize / 2f;
        return Mathf.Min(Mathf.Min(p.x + h, h - p.x), Mathf.Min(p.y + h, h - p.y));
    }
}
