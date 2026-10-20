using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Станция собирателей. Рабочие здания - собиратели (3 / 6 / 9 по уровням).
///  - В рабочее время собиратель выходит со станции к ягодному кусту (чаще всего вне города, см. BerryField),
///    собирает ягоды, при необходимости заходит ещё к соседнему кусту и возвращается.
///    Принесённые ягоды ложатся на склад станции (вместимость 40 за уровень).
///  - Со склада ягоды, как и любую добычу, уносят в Ратушу: кнопкой "Собрать", "Собрать всё" в меню города
///    или курьерами Курьерской. В Ратуше они идут на Кухню (похлёбка, см. KitchenController).
///  - В войну, ночью и в выходные собиратели в поход не выходят. Склад полон - тоже ждут.
///  - Ур.2 / Ур.3: больше собирателей, больше ягод за поход, дальше ходят и быстрее собирают.
/// Сам поход выполняет CitizenAgent (файл CitizenAgent.Gatherer.cs).
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class BerryStation : MonoBehaviour
{
    public const string TypeName = "Станция собирателей";

    public static readonly List<BerryStation> All = new List<BerryStation>();

    // Параметры по уровням (индекс = уровень - 1)
    static readonly int[] CarryByLevel = { 8, 11, 15 };          // сколько ягод собиратель приносит за поход
    static readonly float[] RadiusByLevel = { 18f, 22f, 27f };   // как далеко от станции ходят
    static readonly float[] PickByLevel = { 5f, 4.5f, 3.5f };    // секунд на сбор у куста

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public int Carry => CarryByLevel[Lvl];
    public float Radius => RadiusByLevel[Lvl];
    public float PickSeconds => PickByLevel[Lvl];

    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;
    public bool IsFull => Building != null && Building.storedBerries >= Building.Capacity;

    // Можно выходить в поход: станция цела, есть рабочие, мир и на складе есть место
    public bool CanSendOut => Works && Building.isProducing && !WarManager.IsWar && (!IsFull || HerbsWanted);

    // ---------- Травы (путь Магии, станция Ур.2+) ----------
    // Травы лежат на складе станции (Building.storedHerbs) и уходят в Ратушу так же, как ягоды:
    // кнопка «Собрать», рабочий станции, Курьерская. Зельеварня берёт их уже из казны Ратуши.
    public int Herbs => Building != null ? Building.storedHerbs : 0;
    public int HerbCapacity => 20 * Mathf.Clamp(Building != null ? Building.level : 1, 1, 3);
    public bool HerbsEnabled => DevelopmentPath.IsMagic && Building != null && Building.level >= 2;
    public bool HerbsWanted => HerbsEnabled && Herbs < HerbCapacity;
    public int HerbCarry => 2 + Mathf.Clamp(Building != null ? Building.level : 1, 1, 3);   // трав за поход
    public const float HerbTripShare = 0.65f;   // преимущественно ходят за травами
    public int HerbsTotal { get; private set; }

    public void DepositHerbs(int n)
    {
        if (n <= 0 || Building == null) return;
        int put = Mathf.Min(n, Mathf.Max(0, HerbCapacity - Building.storedHerbs));
        Building.storedHerbs += put;
        HerbsTotal += put;
    }

    public static int TotalHerbs
    {
        get
        {
            int s = 0;
            foreach (var st in All) if (st != null && st.Works) s += st.Herbs;
            return s;
        }
    }

    /// <summary>Точка в лесу в радиусе сбора, куда можно дойти (null-аналог: false).</summary>
    public bool TryFindForestSpot(GridNavigation nav, out Vector2 spot)
    {
        spot = Vector2.zero;
        if (nav == null || Building == null) return false;
        float r = Radius + 3f;
        for (int i = 0; i < 40; i++)
        {
            Vector2 p = Building.Center + Random.insideUnitCircle * r;
            Vector2Int c = nav.WorldToCell(p);
            if (!nav.InBounds(c) || nav.IsBlocked(c) || !ResourceZones.IsForest(c)) continue;
            spot = nav.CellCenter(c);
            return true;
        }
        return false;
    }

    public int GatheredTotal { get; private set; }
    public int TripsDone { get; private set; }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        UpdateRadiusCircle();
    }

    /// <summary>Собиратель принёс ягоды. Что не влезло на склад - пропадает.</summary>
    public void Deposit(int n)
    {
        if (n <= 0 || Building == null) return;
        int room = Mathf.Max(0, Building.Capacity - Building.storedBerries);
        int put = Mathf.Min(room, n);
        Building.storedBerries += put;
        GatheredTotal += put;
        TripsDone++;
    }

    int GatherersOut()
    {
        int n = 0;
        foreach (var a in CitizenAgent.All)
            if (a != null && !a.IsDead && ReferenceEquals(a.Workplace, Building) && a.IsGathering) n++;
        return n;
    }

    // Сбор: за ягодами идёт житель-курьер (без Ратуши - мгновенно)
    static void Collect(BuildingInstance b)
    {
        if (b == null || b.StoredTotal <= 0) return;
        var pm = PopulationManager.Instance;
        if (pm == null || pm.Hall == null) b.CollectToTreasury();
        else if (!pm.DispatchCourier(b)) WarManager.Notify("Нет свободных жителей, чтобы унести ягоды", false);
    }

    static void CollectAll()
    {
        foreach (var s in All)
            if (s != null && s.Works && !s.Building.courierOnTheWay) Collect(s.Building);
    }

    // ---------- Круг, куда ходят собиратели (виден, пока выбрано здание) ----------

    LineRenderer circle;
    int shownFrame = -10;

    void UpdateRadiusCircle()
    {
        bool show = Time.frameCount - shownFrame <= 2 && Works;
        if (!show) { if (circle != null) circle.enabled = false; return; }

        if (circle == null)
        {
            GameObject go = new GameObject("GatherRadius");
            circle = go.AddComponent<LineRenderer>();
            circle.useWorldSpace = true;
            circle.loop = true;
            circle.material = new Material(Shader.Find("Sprites/Default"));
            circle.widthMultiplier = 0.08f;
            circle.startColor = circle.endColor = new Color(0.95f, 0.45f, 0.7f, 0.5f);
            circle.sortingOrder = -40;
            circle.positionCount = 72;
        }

        circle.enabled = true;
        Vector2 c = Building.Center;
        float r = Radius;
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72;
            circle.SetPosition(i, new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f));
        }
    }

    void OnDestroy()
    {
        if (circle != null) Destroy(circle.gameObject);
    }

    // ---------- Панель здания ----------

    public Rect DrawPanel(float x, float y, float w)
    {
        shownFrame = Time.frameCount;
        BuildingInstance b = Building;
        int lvl = b.level;
        float cx = x + UiKit.Pad, cw = w - UiKit.Pad * 2f;

        // Что не так - сразу под шапкой
        string status = "";
        if (!b.isWorking) status = UiKit.Col(UiKit.BadHex, b.StatusText);
        else if (WarManager.IsWar) status = UiKit.Col(UiKit.BadHex, "Война — собиратели не выходят за ягодами");
        else if (IsFull) status = UiKit.Col(UiKit.WarnHex, "Склад полон — нужно унести ягоды в Ратушу");
        float statusH = status != "" ? UiKit.TextH(status, cw, UiKit.Small) + 4f : 0f;

        string details =
            $"Собрано всего: {GatheredTotal}, походов: {TripsDone}" +
            (HerbsEnabled ? $"\nТрав за поход: {HerbCarry}, собрано: {HerbsTotal} — уносят в Ратушу вместе с ягодами" : "") +
            "\n" + UiKit.Col(UiKit.MutedHex, "Собиратели ходят к ягодным кустам, чаще всего за городом, и приносят ягоды сюда. " +
            "В Ратушу их уносят кнопкой «Собрать» или курьеры Курьерской. Ягоды нужны Кухне.");
        bool open = UiKit.IsOpen("berry-more");
        float detailsH = open ? UiKit.TextH(details, cw, UiKit.Small) + 4f : 0f;

        float barY = UiKit.TitleH + 6f + statusH;
        float rowsY = barY + 24f;
        float btnY = rowsY + UiKit.RowH * (HerbsEnabled ? 3f : 2f) + 4f;
        float workersY = btnY + UiKit.BtnH + 6f;
        float foldY = workersY + 30f;
        float upgradeY = foldY + UiKit.FoldH + detailsH + 4f;
        float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName}  {UiKit.Col(UiKit.MutedHex, "· Ур. " + lvl)}");
        if (status != "") GUI.Label(new Rect(cx, y + UiKit.TitleH + 6f, cw, statusH), status, UiKit.Small);

        // Склад ягод
        float frac = b.Capacity > 0 ? b.storedBerries / (float)b.Capacity : 0f;
        UiKit.Bar(new Rect(cx, y + barY, cw, 18f), frac, IsFull ? UiKit.Warn : new Color(0.85f, 0.45f, 0.65f), $"Ягоды {b.storedBerries}/{b.Capacity}");

        int bushes = BerryField.CountNear(b.Center, Radius, out int onBushes);
        float cy = y + rowsY;
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "В походе", $"{GatherersOut()} из {b.workers}  {UiKit.Col(UiKit.MutedHex, "· по " + Carry + " ягод")}");
        cy += UiKit.RowH;
        Rect br = new Rect(cx, cy, cw, UiKit.RowH);
        UiKit.Row(br, $"Кустов в {Radius:F0} кл.", $"{bushes}  {UiKit.Col(UiKit.MutedHex, "· ягод " + onBushes)}");
        UiKit.Tip(br, "Ягодные кусты в радиусе сбора станции и сколько ягод на них сейчас.");
        cy += UiKit.RowH;
        if (HerbsEnabled)
            UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Травы", UiKit.Col("#90E060", $"{Herbs}/{HerbCapacity}"));

        // --- Сбор ---
        float halfW = (cw - 6f) / 2f;
        bool canHere = b.StoredTotal > 0 && !b.courierOnTheWay;
        string hereLabel = b.courierOnTheWay ? "Курьер идёт..." : "Собрать";
        string hereTip = b.courierOnTheWay ? "Курьер уже идёт за ягодами" : (b.StoredTotal > 0 ? "Курьер отнесёт ягоды отсюда в Ратушу" : "Склад пуст");
        if (UiKit.Button(new Rect(cx, y + btnY, halfW, UiKit.BtnH), hereLabel, canHere, UiKit.Kind.Primary, hereTip)) Collect(b);
        if (UiKit.Button(new Rect(cx + halfW + 6f, y + btnY, halfW, UiKit.BtnH), "Со всех", UiKit.Kind.Normal, "Собрать ягоды со всех станций")) CollectAll();

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(cx, y + workersY, b);

        if (UiKit.Fold(new Rect(cx, y + foldY, cw, UiKit.FoldH), "berry-more", "Подробнее"))
            GUI.Label(new Rect(cx, y + foldY + UiKit.FoldH, cw, detailsH), details, UiKit.Small);

        float uy = UiKit.Section(cx, y + upgradeY, cw, "Улучшение");
        UpgradeRules.DrawUpgradeSection(cx, uy, cw, b);
        return new Rect(x, y, w + 5f, h);
    }
}
