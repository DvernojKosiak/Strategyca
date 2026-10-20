using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Запас понтонных секций (путь Военного дела). Секции делает Кузня мастера (вкладка «Снаряжение»),
/// лежат в Ратуше. Из них сапёры собирают переправы через реку (Crossing, кнопка «Переправы» [P]).
/// </summary>
public static class PontoonStock
{
    public const int Max = 40;
    // Цена изготовления одной партии (PerBatch секций) в Кузне
    public const int CostGold = 10, CostWood = 14, CostPlanks = 3;
    public const int PerBatch = 2;
    public const float CraftSeconds = 25f;

    public static int Count { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Count = 0; }

    public static void Add(int n) { Count = Mathf.Clamp(Count + Mathf.Max(0, n), 0, Max); }

    public static bool Take(int n)
    {
        if (n <= 0) return true;
        if (Count < n) return false;
        Count -= n;
        return true;
    }

    static int Planks()
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, RefinedKind.Planks, QualityTier.Medium) : 0;
    }

    /// <summary>Чего не хватает на партию секций ("" - всего хватает).</summary>
    public static string Missing()
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return "нет казны";
        var p = new List<string>();
        if (rm.gold < CostGold) p.Add($"золота {CostGold - rm.gold}");
        if (rm.wood < CostWood) p.Add($"дерева {CostWood - rm.wood}");
        int pl = Planks();
        if (pl < CostPlanks) p.Add($"досок {CostPlanks - pl}");
        return string.Join(", ", p);
    }

    public static bool Pay()
    {
        if (Missing() != "") return false;
        ResourceManager.Instance.Spend(CostGold, CostWood, 0, 0);
        UpgradeRules.SpendAtLeast(TownHallStorage.Instance, RefinedKind.Planks, QualityTier.Medium, CostPlanks);
        return true;
    }

    public static void Refund()
    {
        var rm = ResourceManager.Instance;
        if (rm != null) rm.Add(CostGold, CostWood, 0, 0);
        var st = TownHallStorage.Instance;
        if (st != null) st.AddPlanks(QualityTier.Medium, CostPlanks);
    }

    public static string CostText => $"{CostGold} золота, {CostWood} дерева, {CostPlanks} доски → {PerBatch} секции, {CraftSeconds:F0} с";
}

/// <summary>
/// Переправа через реку (путь Военного дела): линия понтонных секций поперёк реки, от берега до берега.
///  - Сколько секций нужно - столько клеток воды на линии (широкое место - больше секций).
///  - Сапёры ставят секции по очереди, от одного берега, по SectionSeconds каждая.
///    Пока ставят, враг рядом не даёт работать, а секцию, которую ставят, топчет и ломает -
///    сломанную секцию заменяют новой из запаса (нет запаса - переправа ждёт).
///  - Пока переправа не собрана целиком, по ней не пройти. Готовая - солдаты (и враги!) идут по ней
///    в одну линию, без замедления брода. Мирные жители по понтонам не ходят - им нужен мост.
///  - «Снять» - секции возвращаются в запас (кроме сломанных).
/// Только своя карта. Проходимость считает GridNavigation (Open, Cells).
/// </summary>
public class Crossing : MonoBehaviour
{
    public static readonly List<Crossing> All = new List<Crossing>();

    public const int MaxLength = 10;          // самая длинная переправа (клеток воды)
    public const float SectionSeconds = 6f;   // сколько ставят одну секцию
    const float SectionHp = 40f;              // прочность секции, пока её ставят
    const float TrampleRadius = 1.3f;
    const float TrampleDps = 30f;             // сколько снимает каждый враг вплотную в секунду
    const float WorkBlockRadius = 2.5f;       // враг ближе - сапёры не работают

    public readonly List<Vector2Int> Cells = new List<Vector2Int>();
    public bool Horizontal { get; private set; }
    int built;                 // сколько секций уже стоит
    float progress;            // ход текущей секции 0..1
    float sectionHp = SectionHp;
    int held;                  // секций при переправе (поставленные + запас для неё)
    bool wasOpen;

    public bool Open => built >= Cells.Count && Cells.Count > 0;
    public int Built => built;
    public int Need => Cells.Count;
    public bool WaitingSection => !Open && held <= built;
    public bool Paused { get; private set; }
    public float Progress => Open ? 1f : (built + progress) / Mathf.Max(1, Cells.Count);
    public Vector2 Center
    {
        get
        {
            if (Cells.Count == 0) return transform.position;
            Vector2 a = Cell(Cells[0]), b = Cell(Cells[Cells.Count - 1]);
            return (a + b) * 0.5f;
        }
    }

    readonly List<SpriteRenderer> planks = new List<SpriteRenderer>();
    readonly List<SpriteRenderer> ropes = new List<SpriteRenderer>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { All.Clear(); }

    static Vector2 Cell(Vector2Int c) { return new Vector2(c.x + 0.5f, c.y + 0.5f); }

    public static bool Occupies(Vector2Int c)
    {
        foreach (var cr in All)
            if (cr != null && cr.Cells.Contains(c)) return true;
        return false;
    }

    /// <summary>Поставить переправу: секции (cells.Count) уже взяты из запаса.</summary>
    public static Crossing Create(List<Vector2Int> cells, bool horizontal)
    {
        GameObject go = new GameObject("Crossing");
        Crossing c = go.AddComponent<Crossing>();
        c.Cells.AddRange(cells);
        c.Horizontal = horizontal;
        c.held = cells.Count;
        c.transform.position = c.Center;
        c.BuildLook();
        return c;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (wasOpen) GridNavigation.MarkDirty();
    }

    /// <summary>Разобрать: секции (кроме сломанных) - обратно в запас Ратуши.</summary>
    public void Dismantle()
    {
        PontoonStock.Add(held);
        WarManager.Notify($"Переправу разобрали: {held} секций вернулись в запас", true);
        held = 0;
        Destroy(gameObject);
    }

    void Update()
    {
        if (Open)
        {
            if (!wasOpen)
            {
                wasOpen = true;
                GridNavigation.MarkDirty();
                WarManager.Notify("Переправа готова: солдаты переходят реку по понтонам", true);
                UpdateLook();
            }
            return;
        }

        Vector2 at = Cell(Cells[built]);

        // Враги топчут секцию, которую ставят
        int stomp = 0;
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, at) <= TrampleRadius) stomp++;
        if (stomp > 0 && held > built)
        {
            sectionHp -= stomp * TrampleDps * Time.deltaTime;
            if (sectionHp <= 0f)
            {
                held--;                     // секция сломана
                progress = 0f;
                sectionHp = SectionHp;
                CombatFx.Splash(at, 0.6f);
                WarManager.Notify("Враги сломали понтонную секцию, пока её ставили", false);
            }
        }

        // Нужна новая секция взамен сломанной
        if (held <= built)
        {
            if (PontoonStock.Take(1)) held++;
            else { Paused = true; UpdateLook(); return; }
        }

        Paused = EnemyAgent.AnyWithin(at, WorkBlockRadius);
        if (!Paused)
        {
            progress += Time.deltaTime / SectionSeconds;
            if (progress >= 1f)
            {
                progress = 0f;
                sectionHp = SectionHp;
                built++;
            }
        }
        UpdateLook();
    }

    // ---------- Вид ----------

    static readonly Color Wood = new Color(0.58f, 0.42f, 0.24f);
    static readonly Color Rope = new Color(0.85f, 0.78f, 0.55f);

    void BuildLook()
    {
        foreach (var c in Cells)
        {
            SpriteRenderer p = Part("Plank", Cell(c), Horizontal ? new Vector2(0.98f, 0.62f) : new Vector2(0.62f, 0.98f), Wood, -9);
            planks.Add(p);
        }
        // Канаты вдоль переправы (видны, когда готова)
        Vector2 a = Cell(Cells[0]), b = Cell(Cells[Cells.Count - 1]);
        Vector2 mid = (a + b) * 0.5f;
        float len = Vector2.Distance(a, b) + 1f;
        Vector2 side = Horizontal ? new Vector2(0f, 0.33f) : new Vector2(0.33f, 0f);
        Vector2 size = Horizontal ? new Vector2(len, 0.05f) : new Vector2(0.05f, len);
        ropes.Add(Part("Rope", mid + side, size, Rope, -8));
        ropes.Add(Part("Rope", mid - side, size, Rope, -8));
        UpdateLook();
    }

    SpriteRenderer Part(string n, Vector2 pos, Vector2 size, Color col, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(transform, true);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = CombatFx.Square();
        r.color = col;
        r.sortingOrder = order;
        return r;
    }

    void UpdateLook()
    {
        for (int i = 0; i < planks.Count; i++)
        {
            SpriteRenderer p = planks[i];
            if (p == null) continue;
            Color c = Wood;
            if (i < built) c.a = 1f;
            else if (i == built && !Open) c = Color.Lerp(new Color(0.9f, 0.7f, 0.3f, 0.35f), new Color(Wood.r, Wood.g, Wood.b, 0.9f), progress);
            else c.a = 0.18f;
            if (i < built && i % 2 == 1) c *= 0.88f;   // полоски досок
            c.a = i < built ? 1f : c.a;
            p.color = c;
        }
        foreach (var r in ropes) if (r != null) r.enabled = Open;
    }

    public string StatusText
    {
        get
        {
            if (Open) return UiKit.Col(UiKit.GoodHex, "готова");
            if (WaitingSection) return UiKit.Col(UiKit.BadHex, "сломали секцию — нужна ещё (запас пуст)");
            if (Paused) return UiKit.Col(UiKit.WarnHex, "враги рядом — сапёры ждут");
            return $"ставят: {built}/{Cells.Count} ({Progress * 100f:F0}%)";
        }
    }
}

/// <summary>
/// Кнопка «Переправы» [P] слева от «В бой!» и размещение переправ (см. Crossing).
/// Видна дома, если на карте есть река и выбран путь Военного дела (или в запасе есть секции / стоят переправы).
/// Размещение: навести на воду - линия от берега до берега; R - повернуть (поперёк / вдоль), ЛКМ - поставить,
/// ПКМ / Esc - отмена. BuildingPlacer спрашивает CrossingManager.Blocks, чтобы клик не уходил на карту.
/// </summary>
public class CrossingManager : MonoBehaviour
{
    public static CrossingManager Instance { get; private set; }

    const float PanelW = 360f;
    const int MaxRows = 6;

    bool panelOpen, uiButton, placing, horizontal = true, autoAxis = true;
    int lastBlockFrame = -10;
    string problem = "";
    readonly List<Vector2Int> line = new List<Vector2Int>();
    readonly List<SpriteRenderer> ghost = new List<SpriteRenderer>();
    GameObject ghostRoot;

    public static bool Blocks(Vector2 gp)
    {
        CrossingManager m = Instance;
        if (m == null) return false;
        return m.placing || m.lastBlockFrame == Time.frameCount || m.OverUI(gp);
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // Тревога (WarManager.RaiseAlarm): свернуть окно, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()
    {
        if (!panelOpen) return false;
        panelOpen = false;
        return true;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (ghostRoot != null) Destroy(ghostRoot);
    }

    static bool CanUse()
    {
        if (!Rivers.Any || RaidManager.ViewingBattle) return false;
        return DevelopmentPath.IsWar || PontoonStock.Count > 0 || Crossing.All.Count > 0;
    }

    static Rect ButtonRect => new Rect(Screen.width - 380f, Screen.height - 50f, 180f, 40f);

    Rect PanelRect()
    {
        int n = Mathf.Min(MaxRows, Crossing.All.Count);
        float h = UiKit.WindowH(22f + 38f + (n > 0 ? UiKit.SectionH + n * 28f : 0f));
        return new Rect(Screen.width - PanelW - 200f, Mathf.Max(40f, ButtonRect.y - 6f - h), PanelW, h);
    }

    bool OverUI(Vector2 gp)
    {
        if (!uiButton) return false;
        return ButtonRect.Contains(gp) || (panelOpen && PanelRect().Contains(gp));
    }

    // ---------- Линия переправы ----------

    // От клетки в воде - в обе стороны по оси до берега. false - не дошли до берега (слишком широко / край карты).
    static bool BuildLine(Vector2Int start, bool horiz, List<Vector2Int> cells, out string why)
    {
        cells.Clear();
        why = "";
        var nav = GridNavigation.Instance;
        if (nav == null) { why = "Нет карты"; return false; }
        if (!Rivers.IsWater(start)) { why = "Наведите на реку"; return false; }

        Vector2Int dir = horiz ? new Vector2Int(1, 0) : new Vector2Int(0, 1);
        Vector2Int a = start, b = start;
        for (int i = 0; i <= Crossing.MaxLength && Rivers.IsWater(a - dir); i++) a -= dir;
        for (int i = 0; i <= Crossing.MaxLength && Rivers.IsWater(b + dir); i++) b += dir;
        Vector2Int landA = a - dir, landB = b + dir;
        if (!nav.InBounds(landA) || !nav.InBounds(landB)) { why = "Переправа упирается в край карты"; return false; }
        if (Rivers.IsWater(landA) || Rivers.IsWater(landB)) { why = $"Слишком широко: не больше {Crossing.MaxLength} секций"; return false; }

        for (Vector2Int c = a; ; c += dir)
        {
            cells.Add(c);
            if (c == b) break;
        }
        if (cells.Count > Crossing.MaxLength) { why = $"Слишком широко: не больше {Crossing.MaxLength} секций"; return false; }
        return true;
    }

    string Check(List<Vector2Int> cells)
    {
        var nav = GridNavigation.Instance;
        foreach (var c in cells)
        {
            if (!nav.IsWaterCell(c)) return "Здесь уже мост";
            if (Crossing.Occupies(c)) return "Здесь уже переправа";
        }
        if (PontoonStock.Count < cells.Count) return $"Нужно секций: {cells.Count}, в запасе {PontoonStock.Count} (Кузня мастера)";
        Vector2 mid = (Center(cells[0]) + Center(cells[cells.Count - 1])) * 0.5f;
        if (EnemyAgent.AnyWithin(mid, 3f)) return "Рядом враги — не дадут ставить";
        return "";
    }

    static Vector2 Center(Vector2Int c) { return new Vector2(c.x + 0.5f, c.y + 0.5f); }

    static Vector2Int MouseCell()
    {
        Camera cam = Camera.main;
        Vector2 s = Mouse.current.position.ReadValue();
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        return new Vector2Int(Mathf.FloorToInt(w.x), Mathf.FloorToInt(w.y));
    }

    // ---------- Кадр ----------

    void Update()
    {
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        if (mouse == null || Camera.main == null) return;

        bool can = CanUse();
        uiButton = can;
        if (!can) { panelOpen = false; StopPlacing(); return; }

        if (kb != null && kb.pKey.wasPressedThisFrame)
        {
            if (placing) StopPlacing();
            else panelOpen = !panelOpen;
        }

        Vector2 sp = mouse.position.ReadValue();
        Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
        bool overUI = OverUI(gp);
        if (overUI) CameraController.BlockZoom();
        if (placing || overUI) lastBlockFrame = Time.frameCount;

        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (placing) StopPlacing();
            else panelOpen = false;
        }

        if (!placing) return;
        if (mouse.rightButton.wasPressedThisFrame) { StopPlacing(); return; }
        if (kb != null && kb.rKey.wasPressedThisFrame) { autoAxis = false; horizontal = !horizontal; }

        Vector2Int cell = MouseCell();
        // Авто: выбираем более короткую линию (поперёк реки)
        if (autoAxis && Rivers.IsWater(cell))
        {
            var h = new List<Vector2Int>();
            var v = new List<Vector2Int>();
            bool okH = BuildLine(cell, true, h, out _);
            bool okV = BuildLine(cell, false, v, out _);
            if (okH && (!okV || h.Count <= v.Count)) horizontal = true;
            else if (okV) horizontal = false;
        }
        bool ok = BuildLine(cell, horizontal, line, out string why);
        problem = ok ? Check(line) : why;
        UpdateGhost(problem == "");

        if (mouse.leftButton.wasPressedThisFrame && !overUI)
        {
            if (problem != "") { WarManager.Notify(problem, false); return; }
            if (!PontoonStock.Take(line.Count)) return;
            Crossing.Create(line, horizontal);
            WarManager.Notify($"Сапёры ставят переправу: {line.Count} секций, ~{line.Count * Crossing.SectionSeconds:F0} с. Пока ставят — враги могут её сломать!", true);
            bool keep = kb != null && kb.shiftKey.isPressed;
            if (!keep) StopPlacing();
        }
    }

    void StartPlacing()
    {
        placing = true;
        autoAxis = true;
        panelOpen = false;
        if (ghostRoot == null) ghostRoot = new GameObject("CrossingGhost");
    }

    void StopPlacing()
    {
        placing = false;
        line.Clear();
        if (ghostRoot != null) Destroy(ghostRoot);
        ghostRoot = null;
        ghost.Clear();
    }

    void UpdateGhost(bool ok)
    {
        if (ghostRoot == null) ghostRoot = new GameObject("CrossingGhost");
        while (ghost.Count < line.Count)
        {
            GameObject go = new GameObject("G");
            go.transform.SetParent(ghostRoot.transform, false);
            SpriteRenderer r = go.AddComponent<SpriteRenderer>();
            r.sprite = CombatFx.Square();
            r.sortingOrder = 30;
            ghost.Add(r);
        }
        Color col = ok ? new Color(0.4f, 1f, 0.4f, 0.55f) : new Color(1f, 0.35f, 0.35f, 0.5f);
        for (int i = 0; i < ghost.Count; i++)
        {
            bool on = i < line.Count;
            ghost[i].enabled = on;
            if (!on) continue;
            ghost[i].transform.position = new Vector3(line[i].x + 0.5f, line[i].y + 0.5f, 0f);
            ghost[i].transform.localScale = horizontal ? new Vector3(0.95f, 0.6f, 1f) : new Vector3(0.6f, 0.95f, 1f);
            ghost[i].color = col;
        }
    }

    // ---------- Интерфейс ----------

    void OnGUI()
    {
        UiKit.Apply();
        if (!uiButton || MarketWindow.IsOpen) return;
        DrawBars();

        Rect br = ButtonRect;
        string label;
        UiKit.Kind kind;
        if (placing) { label = "Отмена  [P]"; kind = UiKit.Kind.Primary; }
        else if (panelOpen) { label = "Переправы ▴"; kind = UiKit.Kind.Primary; }
        else { label = $"≋ Переправы ({PontoonStock.Count})"; kind = UiKit.Kind.Normal; }
        if (UiKit.Button(br, label, kind, placing ? "Отменить установку (ПКМ / Esc)" : $"Переправы через реку [P]. Секций в запасе: {PontoonStock.Count}"))
        {
            if (placing) StopPlacing();
            else panelOpen = !panelOpen;
        }

        if (placing) { DrawTip(); return; }
        if (!panelOpen) return;

        Rect r = PanelRect();
        Rect c = UiKit.Window(r, "Переправы через реку", out bool close);
        if (close) { panelOpen = false; return; }
        float x = c.x, w = c.width, y = c.y;

        Rect sr = new Rect(x, y, w, 20f);
        GUI.Label(sr, UiKit.Col(UiKit.MutedHex, "Секций в запасе ") + UiKit.B($"{PontoonStock.Count}") +
            UiKit.Col(UiKit.MutedHex, $" / {PontoonStock.Max}") + "  " + UiKit.Col(UiKit.InfoHex, "?"), UiKit.Body);
        UiKit.Tip(sr, "Понтонные секции делает Кузня мастера, хранятся в Ратуше.\n" +
            "Секций нужно столько, сколько клеток воды поперёк реки. " +
            "Готовая переправа — только для солдат (и врагов!), мирным нужен мост.");
        y += 22f;

        if (UiKit.Button(new Rect(x, y, w, 30f), "Поставить переправу", PontoonStock.Count > 0, UiKit.Kind.Primary,
                PontoonStock.Count > 0 ? "Навести на реку, ЛКМ — поставить, R — повернуть" : "Нет секций — изготовьте в Кузне мастера"))
            StartPlacing();
        y += 38f;

        if (Crossing.All.Count == 0) return;
        y = UiKit.Section(x, y, w, "Стоят");
        var list = new List<Crossing>(Crossing.All);
        for (int i = 0; i < list.Count && i < MaxRows; i++)
        {
            Crossing cr = list[i];
            if (cr == null) continue;
            GUIStyle st = new GUIStyle(UiKit.Small) { fontSize = 12, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
            Rect lr = new Rect(x, y, w - 132f, 24f);
            GUI.Label(lr, $"{i + 1}. {cr.Need} секц. — {cr.StatusText}", st);
            UiKit.Tip(lr, $"{cr.Need} секц. — {cr.StatusText}");
            if (UiKit.Button(new Rect(x + w - 126f, y, 60f, 24f), "Найти", UiKit.Kind.Normal, "Показать на карте") && CameraController.Instance != null)
                CameraController.Instance.FocusOn(cr.Center);
            if (UiKit.Button(new Rect(x + w - 62f, y, 62f, 24f), "Снять", UiKit.Kind.Danger, "Разобрать: секции вернутся в запас (кроме сломанных)")) cr.Dismantle();
            y += 28f;
        }
    }

    void DrawTip()
    {
        Vector2 m = Mouse.current.position.ReadValue();
        string text = problem == ""
            ? UiKit.B($"Переправа: {line.Count} секц.") + UiKit.Col(UiKit.MutedHex, $"  (в запасе {PontoonStock.Count})") +
              "\nЛКМ — поставить, R — повернуть\n" + UiKit.Col(UiKit.MutedHex, "ПКМ / Esc — отмена")
            : UiKit.B("Переправа") + "\n" + UiKit.Col(UiKit.BadHex, problem) + "\n" + UiKit.Col(UiKit.MutedHex, "R — повернуть, ПКМ / Esc — отмена");
        float w = 300f, h = 66f;
        Rect r = new Rect(Mathf.Min(m.x + 20f, Screen.width - w - 4f), Mathf.Min(Screen.height - m.y + 20f, Screen.height - h - 4f), w, h);
        GUI.Box(r, GUIContent.none);
        GUIStyle st = new GUIStyle(UiKit.Small) { fontSize = 12 };
        st.normal.textColor = UiKit.Text;
        GUI.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, r.height - 10f), text, st);
    }

    // Полоски хода установки над переправами, которые ещё ставят
    void DrawBars()
    {
        if (Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        float ppu = Screen.height / (2f * cam.orthographicSize);
        foreach (var c in Crossing.All)
        {
            if (c == null || c.Open) continue;
            Vector3 sp = cam.WorldToScreenPoint(c.Center + Vector2.up * 0.9f);
            if (sp.z < 0f) continue;
            float w = Mathf.Clamp(ppu * 1.6f, 24f, 90f), h = 5f;
            Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y - h / 2f, w, h);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
            GUI.color = c.Paused ? new Color(1f, 0.55f, 0.3f) : new Color(0.55f, 0.8f, 1f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(c.Progress), r.height), Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
