using UnityEngine;

public enum GateMode
{
    Friendly,   // свои проходят, враги - нет (по умолчанию)
    Open,       // открыты для всех - враги тоже проходят (например, заманить их под башни)
    Locked      // заперты для всех - и жители, и солдаты остаются по свою сторону
}

/// <summary>
/// Ворота в стене (1 клетка). Как и стену, их можно сломать - тогда остаётся проходимый завал.
///  - "Свои проходят": створки сами распахиваются, когда подходит наш житель или солдат,
///    и закрываются за ним. Для врагов это стена: чтобы пройти, они должны их сломать.
///  - "Открыты всем": проходят все, враги тоже.
///  - "Заперты": не проходит никто.
/// Проходимость считает GridNavigation (по BlocksEnemies / BlocksEveryone), здесь - режим, створки и панель.
/// Добавляется на здание "Ворота" в BuildingPlacer.
/// </summary>
public class GateController : MonoBehaviour
{
    GateMode mode = GateMode.Friendly;
    BuildingInstance bld;

    public GateMode Mode => mode;
    BuildingInstance B => bld != null ? bld : (bld = GetComponent<BuildingInstance>());

    public bool BlocksEveryone => B != null && !B.IsRuined && mode == GateMode.Locked;
    public bool BlocksEnemies => B != null && !B.IsRuined && mode != GateMode.Open;

    public void SetMode(GateMode m)
    {
        if (mode == m) return;
        mode = m;
        GridNavigation.MarkDirty(); // проходимость изменилась
    }

    // ---------- Створки ----------

    GameObject doorRoot;
    SpriteRenderer leafL, leafR, bolt;
    float openAmount;        // 0 - закрыты, 1 - распахнуты (плавно)
    bool wantOpen;
    float scanTimer;

    static readonly Color DoorColor = new Color(0.5f, 0.33f, 0.16f);
    static readonly Color LockedColor = new Color(0.35f, 0.24f, 0.12f);
    static readonly Color BoltColor = new Color(0.2f, 0.2f, 0.22f);

    void Start()
    {
        bld = GetComponent<BuildingInstance>();
        doorRoot = new GameObject("GateDoor");
        leafL = MakePart("LeafL", 1);
        leafR = MakePart("LeafR", 1);
        bolt = MakePart("Bolt", 2);
        bolt.color = BoltColor;
    }

    SpriteRenderer MakePart(string n, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(doorRoot.transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.sortingOrder = order;
        return sr;
    }

    void OnEnable() { if (doorRoot != null) doorRoot.SetActive(true); }
    void OnDisable() { if (doorRoot != null) doorRoot.SetActive(false); }  // ворота переносят
    void OnDestroy() { if (doorRoot != null) Destroy(doorRoot); }

    void Update()
    {
        if (B == null || doorRoot == null) return;

        // Кто рядом: свои - открываем (если режим позволяет), в режиме "открыты всем" - открыто всегда
        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = 0.15f;
            Vector2 c = CellCenter();
            switch (mode)
            {
                case GateMode.Open: wantOpen = true; break;
                case GateMode.Locked: wantOpen = false; break;
                default: wantOpen = FriendlyNear(c, 1.4f); break;
            }
        }

        openAmount = Mathf.MoveTowards(openAmount, wantOpen ? 1f : 0f, Time.deltaTime * 4f);
        UpdateLook();
    }

    Vector2 CellCenter()
    {
        Vector2Int o = B.OriginCell;
        Vector2Int sz = B.type != null ? B.type.size : Vector2Int.one;
        return new Vector2(o.x + sz.x / 2f, o.y + sz.y / 2f);
    }

    static bool FriendlyNear(Vector2 p, float r)
    {
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.IsInsideBuilding) continue;
            if (Vector2.Distance(a.transform.position, p) <= r) return true;
        }
        return false;
    }

    void UpdateLook()
    {
        bool ruined = B.IsRuined;
        leafL.enabled = leafR.enabled = !ruined;
        bolt.enabled = !ruined && mode == GateMode.Locked;
        if (ruined) return;

        Vector2 c = CellCenter();
        doorRoot.transform.position = new Vector3(c.x, c.y, 0f);

        // Закрыто: две створки по 0.4 встык. Открыто: прижаты к краям тонкими полосками.
        float w = Mathf.Lerp(0.4f, 0.1f, openAmount);
        float off = Mathf.Lerp(0.2f, 0.4f, openAmount);
        leafL.transform.localPosition = new Vector3(-off, 0f, 0f);
        leafR.transform.localPosition = new Vector3(off, 0f, 0f);
        leafL.transform.localScale = leafR.transform.localScale = new Vector3(w, 0.8f, 1f);

        Color col = mode == GateMode.Locked ? LockedColor : DoorColor;
        leafL.color = leafR.color = col;

        bolt.transform.localPosition = Vector3.zero;
        bolt.transform.localScale = new Vector3(0.85f, 0.14f, 1f);
    }

    // ---------- Панель ----------

    public string StateText
    {
        get
        {
            if (B.IsRuined) return UiKit.Col(UiKit.BadHex, "Разрушены — через завал проходят все");
            switch (mode)
            {
                case GateMode.Open: return UiKit.Col(UiKit.WarnHex, "Открыты для всех — враги тоже пройдут");
                case GateMode.Locked: return UiKit.Col(UiKit.BadHex, "Заперты — не проходит никто");
                default: return openAmount > 0.5f ? UiKit.Col(UiKit.GoodHex, "Открыты: проходят свои") : "Закрыты, откроются перед своими";
            }
        }
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = B;
        float cx = x + UiKit.Pad, cw = w - UiKit.Pad * 2f;

        float stateY = UiKit.TitleH + 6f;
        float modeY = stateY + UiKit.RowH + 2f;
        float upgradeY = modeY + UiKit.SectionH + 30f + 6f;
        float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"Ворота  {UiKit.Col(UiKit.MutedHex, "· Ур. " + b.level)}");

        GUI.Label(new Rect(cx, y + stateY, cw, UiKit.RowH), StateText);

        // Режим: три переключателя, пояснение - в подсказке
        string[] names = { "Свои", "Всем", "Заперты" };
        GateMode[] modes = { GateMode.Friendly, GateMode.Open, GateMode.Locked };
        string[] tips =
        {
            "Свои проходят: створки сами открываются перед жителями и солдатами. Для врагов ворота — стена: чтобы пройти, их надо сломать.",
            "Открыты всем: враги проходят, не ломая ворота. Можно заманить их под башни.",
            "Заперты: жители и солдаты не выйдут и не войдут. Враги будут ломать ворота, как стену."
        };
        int cur = 0;
        for (int i = 0; i < 3; i++) if (mode == modes[i]) cur = i;

        float by = UiKit.Section(cx, y + modeY, cw, "Режим");
        Rect tabs = new Rect(cx, by, cw, 28f);
        int pick = UiKit.Tabs(tabs, names, cur);
        if (pick != cur) SetMode(modes[pick]);
        float tw = cw / 3f;
        for (int i = 0; i < 3; i++) UiKit.Tip(new Rect(cx + i * tw, by, tw, 28f), tips[i]);

        float uy = UiKit.Section(cx, y + upgradeY, cw, "Улучшение");
        UpgradeRules.DrawUpgradeSection(cx, uy, cw, b);
        return new Rect(x, y, w + 5f, h);
    }
}
