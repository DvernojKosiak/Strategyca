using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Дозоры (ветка военного дела «Мастер маскировки»).
///  - Разведчик в маскировочной накидке (CitizenAgent.HasCloak) уходит «В дозор» к точке, которую игрок
///    выбрал на своей карте. По дороге его замечают только вплотную (CitizenAgent.NoticeRange).
///  - Сел в дозор (hiddenLookout): враги и лазутчики соседей его не видят, игрок - видит.
///    Он рассеивает туман на FogOfWar.LookoutVision клеток и предупреждает о войсках соседей:
///    кто прошёл рядом, а войско, идущее с его стороны карты, замечает раньше (RivalManager, WarnBonus).
///  - Дозоры - не ближе MinSpacing клеток друг к другу, не больше MaxLookouts, вне своей зелёной зоны.
///  - Паёк RationFood еды из казны - на RationDays игровых дня. Кончился - разведчик голодным идёт в казарму.
///    Сняли раньше («Вернуть») - остаток пайка вернётся в казну.
/// Окно: кнопка «Дозоры [L]» слева внизу (видна только этой ветке). ЛКМ по карте - посадить дозор,
/// Shift - ещё один, ПКМ / Esc - отмена. BuildingPlacer спрашивает CamoScouts.Blocks, чтобы клик не уходил на карту.
/// Ветку сменили (админ) - дозорные возвращаются домой.
/// </summary>
public class CamoScouts : MonoBehaviour
{
    public static CamoScouts Instance { get; private set; }

    // ---------- Числа (можно менять) ----------
    public const float MinSpacing = 8f;       // между дозорами (клеток)
    public const int MaxLookouts = 4;
    public const int RationFood = 12;         // паёк из казны
    public const float RationDays = 3f;       // на сколько игровых дней хватает пайка
    public const float WatchRadius = 10f;     // с какого расстояния дозорный замечает идущее мимо войско
    public const float CoverDepth = 14f;      // дозор не дальше стольких клеток от края карты стережёт эту сторону
    public const float WarnFirst = 20f;       // столько секунд раньше предупреждает первый дозор на стороне
    public const float WarnExtra = 6f;        // ... и каждый следующий
    public const float WarnMax = 32f;
    const float EnemyClear = 4f;              // не сажать ближе к врагу
    const float ArmyRepeat = 60f;             // об одном войске один дозорный напоминает не чаще

    const float PanelW = 380f;
    const float LookRowH = 48f;
    static readonly Color LeafColor = new Color(0.45f, 0.85f, 0.35f);

    bool panelOpen, uiButton, placing, wasCamo;
    int lastBlockFrame = -10;
    string problem = "";
    CitizenAgent hoverScout;          // кого пошлём в точку под курсором
    float scanTimer;

    // Предупреждения: о каком войске / лазутчике какой дозорный уже сообщал
    readonly Dictionary<string, float> armyWarned = new Dictionary<string, float>();
    readonly HashSet<EnemyAgent> spiesSeen = new HashSet<EnemyAgent>();

    // Круги обзора на карте (пока открыто окно или ставим дозор)
    GameObject ghostRoot;
    SpriteRenderer cursorDisc, cursorDot;
    readonly List<SpriteRenderer> discs = new List<SpriteRenderer>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("CamoScouts");
        DontDestroyOnLoad(go);
        go.AddComponent<CamoScouts>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // Тревога (WarManager.RaiseAlarm): свернуть окно, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()
    {
        if (!panelOpen && !placing) return false;
        panelOpen = false;
        StopPlacing();
        return true;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (ghostRoot != null) Destroy(ghostRoot);
    }

    // ---------- Для других ----------

    /// <summary>Курсор над окном дозоров или идёт выбор места: карта кликов не получает.</summary>
    public static bool Blocks(Vector2 gp)
    {
        CamoScouts m = Instance;
        if (m == null) return false;
        return m.placing || m.lastBlockFrame == Time.frameCount || m.OverUI(gp);
    }

    /// <summary>Сейчас идёт выбор места для дозора.</summary>
    public static bool Placing => Instance != null && Instance.placing;

    /// <summary>Сколько разведчиков сидит в дозоре прямо сейчас.</summary>
    public static int PostedCount
    {
        get
        {
            int n = 0;
            foreach (var a in CitizenAgent.All) if (a != null && !a.IsDead && a.LookoutPosted) n++;
            return n;
        }
    }

    /// <summary>
    /// На сколько секунд раньше дозоры заметят войско, идущее со стороны side (0 запад, 1 восток, 2 юг, 3 север).
    /// who - имя первого дозорного на этой стороне. 0 - там никого нет.
    /// </summary>
    public static float WarnBonus(int side, out string who)
    {
        who = "";
        var nav = GridNavigation.Instance;
        if (nav == null || !DevelopmentPath.IsCamo) return 0f;
        float h = nav.mapSize / 2f;
        int n = 0;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.LookoutPosted) continue;
            Vector2 p = a.LookoutPoint;
            float d;
            switch (side)
            {
                case 0: d = p.x + h; break;
                case 1: d = h - p.x; break;
                case 2: d = p.y + h; break;
                default: d = h - p.y; break;
            }
            if (d > CoverDepth) continue;
            if (n == 0) who = a.CitizenName;
            n++;
        }
        if (n == 0) return 0f;
        return Mathf.Min(WarnMax, WarnFirst + WarnExtra * (n - 1));
    }

    /// <summary>Паёк в секундах (RationDays игровых суток по часам GameClock).</summary>
    public static float RationSeconds
    {
        get
        {
            GameClock c = GameClock.Instance;
            float day = c != null ? (c.dayMinutes + c.nightMinutes) * 60f : 1800f;
            return Mathf.Max(60f, day * RationDays);
        }
    }

    /// <summary>Время пайка по-человечески: «2 д 5 ч» / «7 ч» (игровые сутки - 24 ч).</summary>
    public static string RationText(float seconds)
    {
        GameClock c = GameClock.Instance;
        float day = c != null ? (c.dayMinutes + c.nightMinutes) * 60f : 1800f;
        float hours = Mathf.Max(0f, seconds) / Mathf.Max(1f, day) * 24f;
        int d = Mathf.FloorToInt(hours / 24f);
        int hh = Mathf.FloorToInt(hours - d * 24f);
        if (d > 0) return $"{d} д {hh} ч";
        if (hh > 0) return $"{hh} ч";
        return "<1 ч";
    }

    /// <summary>Разведчик может уйти в дозор: в накидке, свободен, не в бою.</summary>
    public static bool CanSend(CitizenAgent a)
    {
        return a != null && !a.IsDead && a.IsScout && a.HasCloak && !a.IsOnExpedition && !a.IsDeparting
            && !a.InCombat && !a.IsSupplying && !a.IsLyingInBed && a.isActiveAndEnabled;
    }

    /// <summary>Ближайший к точке свободный разведчик в накидке (null - нет).</summary>
    public static CitizenAgent PickScout(Vector2 near)
    {
        CitizenAgent best = null;
        float bestD = float.MaxValue;
        foreach (var a in CitizenAgent.All)
        {
            if (!CanSend(a)) continue;
            float d = Vector2.Distance(a.transform.position, near);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    static int ActiveCount()
    {
        int n = 0;
        foreach (var a in CitizenAgent.All) if (a != null && !a.IsDead && a.IsLookout && !a.LookoutReturning) n++;
        return n;
    }

    /// <summary>Почему здесь нельзя посадить дозор ("" - можно).</summary>
    public static string PlaceProblem(Vector2 p)
    {
        if (!DevelopmentPath.IsCamo) return "Нужна ветка «Мастер маскировки»";
        var nav = GridNavigation.Instance;
        if (nav == null) return "Нет карты";
        Vector2Int c = nav.WorldToCell(p);
        if (!nav.InBounds(c)) return "За краем карты";
        if (nav.IsWaterCell(c)) return "В воде дозор не сидит";
        if (nav.IsBlocked(c)) return "Место занято";
        Territory t = Territory.Instance;
        if (t != null && t.InZone(c)) return "Своя зона и так видна — нужно за её краем";
        if (EnemyAgent.AnyWithin(p, EnemyClear)) return "Рядом враги";
        int count = 0;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.IsLookout || a.LookoutReturning) continue;
            count++;
            if (Vector2.Distance(a.LookoutPoint, p) < MinSpacing) return $"Близко к другому дозору: нужно {MinSpacing:F0} клеток";
        }
        if (count >= MaxLookouts) return $"Не больше {MaxLookouts} дозоров";
        var rm = ResourceManager.Instance;
        if (rm == null || rm.food < RationFood) return $"Нужно {RationFood} еды на паёк";
        if (PickScout(p) == null) return "Нет свободного разведчика в накидке";
        return "";
    }

    /// <summary>Послать разведчика a в дозор к точке p (паёк - из казны). false - не вышло.</summary>
    public static bool Send(CitizenAgent a, Vector2 p)
    {
        var rm = ResourceManager.Instance;
        if (!CanSend(a) || rm == null || !rm.HasEnough(0, 0, 0, RationFood)) return false;
        if (!a.BeginLookout(p, RationSeconds, RationFood)) return false;
        rm.Spend(0, 0, 0, RationFood);
        CombatFx.Ring(p, 1.2f, new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.7f), 0.5f);
        WarManager.Notify($"{a.UnitName} {a.CitizenName} уходит в дозор: паёк {RationFood} еды, ~{RationDays:F0} дн.", true);
        return true;
    }

    // ---------- Окно: где и когда ----------

    static bool CanUse()
    {
        return DevelopmentPath.IsCamo && !RaidManager.ViewingBattle;
    }

    static Rect ButtonRect => new Rect(10f, Screen.height - 50f, 180f, 40f);

    static List<CitizenAgent> Lookouts()
    {
        var list = new List<CitizenAgent>();
        foreach (var a in CitizenAgent.All) if (a != null && !a.IsDead && a.IsLookout) list.Add(a);
        return list;
    }

    Rect PanelRect()
    {
        int n = Mathf.Max(1, Lookouts().Count);
        float h = UiKit.WindowH(22f + 38f + UiKit.SectionH + n * LookRowH);
        return new Rect(10f, Mathf.Max(40f, ButtonRect.y - 6f - h), PanelW, h);
    }

    bool OverUI(Vector2 gp)
    {
        if (!uiButton) return false;
        return ButtonRect.Contains(gp) || (panelOpen && PanelRect().Contains(gp));
    }

    static Vector2 MouseWorld()
    {
        Camera cam = Camera.main;
        Vector2 s = Mouse.current.position.ReadValue();
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        return new Vector2(w.x, w.y);
    }

    // ---------- Кадр ----------

    void Update()
    {
        // Ветку сменили (админ) - все дозорные возвращаются
        bool camo = DevelopmentPath.IsCamo;
        if (wasCamo && !camo)
        {
            int n = 0;
            foreach (var a in Lookouts()) if (!a.LookoutReturning) { a.RecallLookout(); n++; }
            if (n > 0) WarManager.Notify("Без ветки маскировки дозоры сняты — разведчики идут домой", false);
        }
        wasCamo = camo;

        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = 0.5f;
            if (camo) Watch();
        }

        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        bool can = CanUse() && mouse != null && Camera.main != null;
        uiButton = can;
        if (!can) { panelOpen = false; StopPlacing(); ShowGhosts(false); return; }

        if (kb != null && kb.lKey.wasPressedThisFrame)
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

        Vector2 world = MouseWorld();
        if (placing)
        {
            if (mouse.rightButton.wasPressedThisFrame) { StopPlacing(); ShowGhosts(false); return; }
            problem = PlaceProblem(world);
            hoverScout = problem == "" ? PickScout(world) : null;

            if (mouse.leftButton.wasPressedThisFrame && !overUI)
            {
                if (problem != "") WarManager.Notify(problem, false);
                else if (Send(hoverScout, world))
                {
                    bool keep = kb != null && kb.shiftKey.isPressed;
                    if (!keep) StopPlacing();
                }
            }
        }

        ShowGhosts(placing || panelOpen);
        if (placing || panelOpen) UpdateGhosts(world);
    }

    void StartPlacing()
    {
        placing = true;
        panelOpen = false;
        problem = "";
    }

    void StopPlacing()
    {
        placing = false;
        hoverScout = null;
        problem = "";
    }

    // ---------- Дозорные смотрят ----------

    void Watch()
    {
        // Чистим память о старых войсках
        if (armyWarned.Count > 32) armyWarned.Clear();
        spiesSeen.RemoveWhere(e => !EnemyAgent.Alive(e));

        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.LookoutPosted) continue;
            Vector2 at = a.LookoutPoint;

            EnemyAgent first = null;
            int count = 0;
            foreach (var e in EnemyAgent.All)
            {
                if (!EnemyAgent.Alive(e)) continue;
                if (Vector2.Distance(e.Position, at) > WatchRadius) continue;
                if (e.IsSpy)
                {
                    if (spiesSeen.Add(e))
                    {
                        string from = e.Army != null ? e.Army.RivalName : "соседа";
                        WarManager.Notify($"Дозор ({a.CitizenName}): мимо крадётся лазутчик {from}", false);
                        CombatFx.Ring(at, 1.6f, new Color(1f, 0.75f, 0.3f, 0.7f), 0.6f);
                    }
                    continue;
                }
                if (first == null) first = e;
                count++;
            }
            if (first == null) continue;

            string army = first.Army != null ? first.Army.RivalName : "врага";
            string key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a) + ":" + (first.Army != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(first.Army).ToString() : "wave");
            if (armyWarned.TryGetValue(key, out float t) && Time.time - t < ArmyRepeat) continue;
            armyWarned[key] = Time.time;

            Vector2 home = Vector2.zero;
            var pm = PopulationManager.Instance;
            if (pm != null && pm.Hall != null) home = pm.Hall.Center;
            bool toTown = Vector2.Distance(first.OrderPoint, home) < Vector2.Distance(first.Position, home);
            WarManager.Notify($"Дозор ({a.CitizenName}): мимо идёт войско {army} — {count} бойц." + (toTown ? " Держат путь к городу!" : ""), false);
            CombatFx.Ring(at, 1.8f, new Color(1f, 0.5f, 0.35f, 0.75f), 0.7f);
        }
    }

    // ---------- Круги обзора на карте ----------

    void ShowGhosts(bool on)
    {
        if (!on)
        {
            if (ghostRoot != null) ghostRoot.SetActive(false);
            return;
        }
        if (ghostRoot == null)
        {
            ghostRoot = new GameObject("CamoScoutsGhost");
            cursorDisc = MakeDisc("Cursor", 62);
            cursorDot = MakeDisc("Dot", 63);
        }
        if (!ghostRoot.activeSelf) ghostRoot.SetActive(true);
    }

    SpriteRenderer MakeDisc(string n, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(ghostRoot.transform, false);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = CombatFx.Circle();
        r.sortingOrder = order;   // поверх тумана (60): место видно и в облаках
        return r;
    }

    void UpdateGhosts(Vector2 cursor)
    {
        if (ghostRoot == null) return;
        float vis = FogOfWar.LookoutVision * 2f;

        // Где ставим: зелёный - можно, красный - нельзя
        bool ok = problem == "";
        cursorDisc.enabled = cursorDot.enabled = placing;
        if (placing)
        {
            cursorDisc.transform.position = new Vector3(cursor.x, cursor.y, 0f);
            cursorDisc.transform.localScale = new Vector3(vis, vis, 1f);
            cursorDisc.color = ok ? new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.13f) : new Color(1f, 0.35f, 0.3f, 0.12f);
            cursorDot.transform.position = new Vector3(cursor.x, cursor.y, 0f);
            cursorDot.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
            cursorDot.color = ok ? new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.9f) : new Color(1f, 0.35f, 0.3f, 0.9f);
        }

        // Уже стоящие дозоры: обзор и (пока ставим) запретный круг вокруг них
        var list = Lookouts();
        while (discs.Count < list.Count * 2) discs.Add(MakeDisc("Look", 61));
        for (int i = 0; i < discs.Count; i++) discs[i].enabled = false;
        for (int i = 0; i < list.Count; i++)
        {
            CitizenAgent a = list[i];
            if (a.LookoutReturning) continue;
            Vector2 p = a.LookoutPoint;
            SpriteRenderer v = discs[i * 2];
            v.enabled = true;
            v.transform.position = new Vector3(p.x, p.y, 0f);
            v.transform.localScale = new Vector3(vis, vis, 1f);
            v.color = a.LookoutPosted ? new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.08f) : new Color(0.7f, 0.85f, 1f, 0.07f);
            if (!placing) continue;
            SpriteRenderer s = discs[i * 2 + 1];
            s.enabled = true;
            s.transform.position = new Vector3(p.x, p.y, 0f);
            s.transform.localScale = new Vector3(MinSpacing * 2f, MinSpacing * 2f, 1f);
            s.color = new Color(1f, 0.6f, 0.3f, 0.08f);
        }
    }

    // ---------- Интерфейс ----------

    void OnGUI()
    {
        UiKit.Apply();
        if (!uiButton || MarketWindow.IsOpen) return;
        DrawMarks();

        var list = Lookouts();
        int posted = 0;
        foreach (var a in list) if (a.LookoutPosted) posted++;

        Rect br = ButtonRect;
        string label;
        UiKit.Kind kind;
        if (placing) { label = "Отмена  [L]"; kind = UiKit.Kind.Primary; }
        else if (panelOpen) { label = "Дозоры ▴"; kind = UiKit.Kind.Primary; }
        else { label = $"◉ Дозоры ({posted}/{MaxLookouts})"; kind = UiKit.Kind.Normal; }
        if (UiKit.Button(br, label, kind, placing ? "Отменить выбор места (ПКМ / Esc)" :
                "Разведчики в накидках сидят в дозоре: видят далеко, враги их не замечают [L]"))
        {
            if (placing) StopPlacing();
            else panelOpen = !panelOpen;
        }

        if (placing) { DrawTip(); return; }
        if (!panelOpen) return;

        Rect r = PanelRect();
        Rect c = UiKit.Window(r, "Дозоры разведчиков", out bool close);
        if (close) { panelOpen = false; return; }
        float x = c.x, w = c.width, y = c.y;

        int free = 0, noCloak = 0;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.IsScout || a.IsOnExpedition) continue;
            if (CanSend(a)) free++;
            else if (!a.HasCloak) noCloak++;
        }

        Rect sr = new Rect(x, y, w, 20f);
        GUI.Label(sr, UiKit.Col(UiKit.MutedHex, "Свободны в накидке: ") + UiKit.B(free.ToString()) +
            (noCloak > 0 ? UiKit.Col(UiKit.MutedHex, $"  · без накидки: {noCloak}") : "") + "  " + UiKit.Col(UiKit.InfoHex, "?"), UiKit.Body);
        UiKit.Tip(sr, $"В дозор идёт только Разведчик в маскировочной накидке (Оружейная, выдать в казарме).\n" +
            $"Сидя в дозоре, он невидим для врагов и лазутчиков, видит на {FogOfWar.LookoutVision:F0} клеток и предупреждает о войсках соседей: " +
            $"войско с его стороны карты (до {CoverDepth:F0} кл. от края) замечает на {WarnFirst:F0}+ с раньше.\n" +
            $"Паёк: {RationFood} еды на ~{RationDays:F0} дн. Кончился — голодным идёт в казарму. " +
            $"Между дозорами не меньше {MinSpacing:F0} клеток, только вне своей зоны.");
        y += 22f;

        var rm = ResourceManager.Instance;
        bool food = rm != null && rm.food >= RationFood;
        bool room = ActiveCount() < MaxLookouts;
        bool canStart = free > 0 && food && room;
        string why = free == 0 ? (noCloak > 0 ? "Наденьте на разведчика накидку (казарма)" : "Нет свободного разведчика в накидке")
                   : (!room ? $"Не больше {MaxLookouts} дозоров" : (!food ? $"Нужно {RationFood} еды на паёк" : "ЛКМ по карте — место дозора, Shift — ещё один"));
        if (UiKit.Button(new Rect(x, y, w, 30f), $"В дозор  ·  паёк {UiKit.Col(UiKit.FoodHex, RationFood + " еды")}", canStart, UiKit.Kind.Primary, why))
            StartPlacing();
        y += 38f;

        y = UiKit.Section(x, y, w, "Дозоры");
        if (list.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, LookRowH), UiKit.Col(UiKit.MutedHex, "Никого. Выберите место за краем своей зоны — лучше у края карты, откуда приходят соседи."), UiKit.MutedStyle);
            return;
        }

        GUIStyle st = new GUIStyle(UiKit.Small) { fontSize = 12, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, richText = true };
        for (int i = 0; i < list.Count; i++)
        {
            CitizenAgent a = list[i];
            Rect lr = new Rect(x, y, w - 132f, 22f);
            string line = $"{UiKit.B(a.CitizenName)} — {a.LookoutStatus}";
            GUI.Label(lr, line, st);
            UiKit.Tip(lr, $"{a.UnitName} {a.CitizenName}: {a.LookoutStatus}\nЗдоровье {Mathf.RoundToInt(a.HpFraction * 100f)}%");

            if (UiKit.Button(new Rect(x + w - 128f, y, 60f, 22f), "Найти", UiKit.Kind.Ghost, "Показать на карте") && CameraController.Instance != null)
                CameraController.Instance.FocusOn(a.LookoutReturning ? (Vector2)a.transform.position : a.LookoutPoint);
            bool canBack = !a.LookoutReturning;
            if (UiKit.Button(new Rect(x + w - 64f, y, 64f, 22f), "Вернуть", canBack, UiKit.Kind.Danger,
                    canBack ? "Снять с дозора: остаток пайка вернётся в казну" : "Уже идёт домой"))
                a.RecallLookout();

            float frac = a.LookoutRationFrac;
            Color col = frac > 0.5f ? UiKit.Good : (frac > 0.2f ? UiKit.Warn : UiKit.Bad);
            string barText = a.LookoutReturning ? (a.LookoutHungry ? "паёк съеден" : "возвращается") : "паёк: " + RationText(a.LookoutRationLeft);
            Rect bar = new Rect(x, y + 25f, w, 14f);
            UiKit.Bar(bar, frac, col, barText);
            UiKit.Tip(bar, "Сколько ещё продержится в дозоре (игровое время)");
            y += LookRowH;
        }
    }

    void DrawTip()
    {
        if (Mouse.current == null) return;
        Vector2 m = Mouse.current.position.ReadValue();
        string text = problem == ""
            ? UiKit.B("Дозор") + (hoverScout != null ? UiKit.Col(UiKit.MutedHex, $"  ({hoverScout.CitizenName})") : "") +
              $"\nЛКМ — посадить, обзор {FogOfWar.LookoutVision:F0} кл.\n" + UiKit.Col(UiKit.MutedHex, "Shift — ещё один, ПКМ / Esc — отмена")
            : UiKit.B("Дозор") + "\n" + UiKit.Col(UiKit.BadHex, problem) + "\n" + UiKit.Col(UiKit.MutedHex, "ПКМ / Esc — отмена");
        float w = 300f, h = 66f;
        Rect r = new Rect(Mathf.Min(m.x + 20f, Screen.width - w - 4f), Mathf.Min(Screen.height - m.y + 20f, Screen.height - h - 4f), w, h);
        GUI.Box(r, GUIContent.none);
        GUIStyle st = new GUIStyle(UiKit.Small) { fontSize = 12 };
        st.normal.textColor = UiKit.Text;
        GUI.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, r.height - 10f), text, st);
    }

    // Над дозорными (видно только игроку): листок-метка и полоска пайка
    void DrawMarks()
    {
        if (Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        float ppu = Screen.height / (2f * cam.orthographicSize);
        Color old = GUI.color;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.LookoutPosted) continue;
            Vector3 sp = cam.WorldToScreenPoint((Vector3)a.LookoutPoint + Vector3.up * 0.75f);
            if (sp.z < 0f) continue;
            float w = Mathf.Clamp(ppu * 1.1f, 20f, 60f), h = 4f;
            Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y - h / 2f, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
            float frac = a.LookoutRationFrac;
            GUI.color = frac > 0.5f ? new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.9f) : (frac > 0.2f ? UiKit.Warn : UiKit.Bad);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * frac, r.height), Texture2D.whiteTexture);
            // Ромбик-«глаз» над полоской
            GUI.color = new Color(LeafColor.r, LeafColor.g, LeafColor.b, 0.85f);
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, new Vector2(sp.x, r.y - 6f));
            GUI.DrawTexture(new Rect(sp.x - 3f, r.y - 9f, 6f, 6f), Texture2D.whiteTexture);
            GUI.matrix = m;
        }
        GUI.color = old;
    }
}
