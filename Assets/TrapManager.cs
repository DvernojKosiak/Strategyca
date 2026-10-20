using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Меню и размещение ловушек (см. Trap, Traps.cs).
///  - Кнопка «Ловушки [T]» справа внизу (над «Заслонами», если те видны). Есть дома, когда в запасе Ратуши
///    есть ловушки или они уже стоят, и на карте набега (разведка / бой), если боевые ловушки взяли с собой.
///  - Обычные: только дома, только в мирное время и только в своей зелёной зоне.
///  - Боевые: дома - во время войны (по всей своей карте); в набеге - из взятого с собой, рядом со своими
///    бойцами, но не в зелёной зоне врага (внутри кольца стен деревни и у самих стен).
///  - Выбрали ловушку - призрак у курсора: ЛКМ - поставить, Shift - ставить ещё, ПКМ / Esc - отмена.
///    Нельзя: на занятые клетки, в тумане, рядом с врагами, вплотную к другой ловушке, сверх лимита.
/// Создаётся в BuildingPlacer.Start. Мышь над меню / в режиме размещения не кликает по карте:
/// BuildingPlacer и RaidManager спрашивают TrapManager.Blocks.
/// </summary>
public class TrapManager : MonoBehaviour
{
    public static TrapManager Instance { get; private set; }

    const float PanelW = 380f;
    const float PlaceFromUnits = 16f;   // на набеге: не дальше стольких клеток от своих бойцов
    const float EnemyClear = 4f;        // не ближе стольких клеток к врагу
    const float TrapSpacing = 0.9f;     // между ловушками
    const int MaxRows = 8;
    const float GroupRowH = 36f;        // строка вида ловушек в меню
    const float PlacedRowH = 26f;       // строка поставленной ловушки

    TrapKit placing;                    // образец выбранной группы (null - не размещаем)
    bool placingRaid;
    bool panelOpen, uiButton, isRaidView;
    int lastBlockFrame = -10;
    string problem = "";
    Trap hover;

    GameObject ghost;
    readonly List<SpriteRenderer> ghostSprites = new List<SpriteRenderer>();
    readonly List<Color> ghostBase = new List<Color>();
    SpriteRenderer ghostRange;

    public static bool Placing => Instance != null && Instance.placing != null;

    /// <summary>Курсор над меню ловушек или идёт размещение: карта кликов не получает.</summary>
    public static bool Blocks(Vector2 guiPoint)
    {
        TrapManager m = Instance;
        if (m == null) return false;
        return m.placing != null || m.lastBlockFrame == Time.frameCount || m.OverUI(guiPoint);
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
        DestroyGhost();
    }

    // ---------- Когда можно ----------

    static bool RaidPhaseOk()
    {
        RaidManager rm = RaidManager.Instance;
        return rm != null && (rm.State == RaidManager.Phase.Scouting || rm.State == RaidManager.Phase.Battle);
    }

    static bool CanUse(bool raidView)
    {
        if (raidView) return RaidPhaseOk() && (TrapStock.RaidCount > 0 || Trap.CountOn(true) > 0);
        return TrapStock.Hall.Count > 0 || Trap.CountOn(false) > 0;
    }

    // Кнопка «Заслоны» видна в тех же условиях, что в FieldPostManager - тогда наша кнопка выше неё
    static bool PostsButtonShown(bool raidView) { return raidView ? RaidPhaseOk() : WarManager.IsWar; }

    Rect ButtonRect => new Rect(Screen.width - 190f, Screen.height - (PostsButtonShown(isRaidView) ? 146f : 98f), 180f, 40f);

    List<KeyValuePair<TrapKit, int>> Groups()
    {
        return isRaidView ? TrapStock.Groups(TrapStock.RaidKit, null) : TrapStock.Groups(TrapStock.Hall, null);
    }

    List<Trap> Placed()
    {
        var list = new List<Trap>();
        foreach (var t in Trap.All) if (t != null && t.Alive && t.Raid == isRaidView) list.Add(t);
        return list;
    }

    Rect PanelRect()
    {
        int g = Mathf.Min(MaxRows, Mathf.Max(1, Groups().Count));
        int n = Mathf.Min(MaxRows, Placed().Count);
        float h = UiKit.WindowH(g * GroupRowH + (n > 0 ? UiKit.SectionH + n * PlacedRowH : 0f) + 4f + UiKit.RowH);   // как в DrawPanel
        float top = ButtonRect.y - 6f - h;
        return new Rect(Screen.width - PanelW - 10f, Mathf.Max(40f, top), PanelW, h);
    }

    bool OverUI(Vector2 gp)
    {
        if (!uiButton) return false;
        return ButtonRect.Contains(gp) || (panelOpen && PanelRect().Contains(gp));
    }

    // ---------- Правила ----------

    /// <summary>Почему эту ловушку сейчас нельзя ставить вообще ("" - можно).</summary>
    static string ModeProblem(TrapKit k, bool raid)
    {
        if (raid)
        {
            if (!RaidPhaseOk()) return "Сейчас не бой";
            if (!k.combat) return "В набег берут только боевые";
            if (Trap.CountOn(true, true) >= Trap.MaxCombatRaid) return $"Не больше {Trap.MaxCombatRaid} ловушек на поле";
            return "";
        }
        if (k.combat)
        {
            if (!WarManager.IsWar) return "Боевые ставят только в бою (во время войны)";
            if (Trap.CountOn(false, true) >= Trap.MaxCombatHome) return $"Не больше {Trap.MaxCombatHome} боевых в городе";
            return "";
        }
        if (WarManager.IsWar) return "Обычные ставят в мирное время — в бою нужны боевые";
        if (Trap.CountOn(false, false) >= Trap.MaxOrdinary) return $"Не больше {Trap.MaxOrdinary} обычных в городе";
        return "";
    }

    // Зелёная зона врага на карте набега: деревня внутри кольца стен и полоса у стен
    public static int EnemyZoneRadius { get { return BotVillage.WallRing + 3; } }

    public static bool InEnemyZone(Vector2Int localCell) { return EnemyZoneDistance(localCell) <= 0; }

    /// <summary>Сколько клеток от клетки карты набега до зелёной зоны врага (0 - уже в ней). Нужна и Полевой башне.</summary>
    public static int EnemyZoneDistance(Vector2Int localCell)
    {
        Vector2Int c = BotVillage.Center;
        int cheb = Mathf.Max(Mathf.Abs(localCell.x - c.x), Mathf.Abs(localCell.y - c.y));
        return Mathf.Max(0, cheb - EnemyZoneRadius);
    }

    string CheckPlace(TrapKit k, Vector2 p, bool raid)
    {
        string mp = ModeProblem(k, raid);
        if (mp != "") return mp;

        foreach (var t in Trap.All)
            if (t != null && t.Alive && t.Raid == raid && Vector2.Distance(t.Position, p) < TrapSpacing) return "Вплотную к другой ловушке";

        if (!FogOfWar.IsVisible(p)) return "Эту точку не видно (туман войны)";

        if (raid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null) return "Нет карты";
            Vector2Int c = map.WorldToCell(p);
            if (!map.InBounds(c) || map.IsBlocked(c)) return "Здесь занято или край карты";
            if (InEnemyZone(c)) return "Это зелёная зона врага — ловушки там не поставить";
            if (RaidUnit.NearestOf(false, p, EnemyClear) != null) return "Рядом защитники — не дадут ставить";
            if (RaidUnit.NearestOf(true, p, PlaceFromUnits) == null) return "Слишком далеко от ваших бойцов (ставят они)";
            return "";
        }

        GridNavigation nav = GridNavigation.Instance;
        if (nav == null) return "Нет карты";
        Vector2Int cell = nav.WorldToCell(p);
        if (!nav.InBounds(cell) || nav.IsBlocked(cell)) return "Здесь занято (здание, стена) или край карты";
        if (!k.combat)
        {
            Territory tr = Territory.Instance;
            if (tr != null && !tr.InZone(new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y))))
                return "Обычные ловушки — только в своей зелёной зоне";
        }
        if (EnemyAgent.AnyWithin(p, EnemyClear)) return "Рядом враги — не дадут ставить";
        return "";
    }

    // ---------- Размещение ----------

    static Vector2 Snap(Vector2 w) { return new Vector2(Mathf.Floor(w.x) + 0.5f, Mathf.Floor(w.y) + 0.5f); }

    static Vector2 MouseWorld()
    {
        Camera cam = Camera.main;
        Vector2 s = Mouse.current.position.ReadValue();
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        return new Vector2(w.x, w.y);
    }

    int StockOf(TrapKit sample, bool raid)
    {
        int n = 0;
        foreach (var k in raid ? TrapStock.RaidKit : TrapStock.Hall) if (k.SameAs(sample)) n++;
        return n;
    }

    void BeginPlacing(TrapKit sample)
    {
        placing = sample;
        placingRaid = isRaidView;
        panelOpen = false;
        MakeGhost(sample);
    }

    void StopPlacing()
    {
        placing = null;
        DestroyGhost();
    }

    void MakeGhost(TrapKit k)
    {
        DestroyGhost();
        ghost = new GameObject("TrapGhost");
        Trap.MakeLook(ghost.transform, k, ghostSprites);
        foreach (var sr in ghostSprites)
        {
            ghostBase.Add(sr.color);
            sr.sortingOrder += 10;
        }
        // Круг, который задевает ловушка
        float r = k.Stats.radius;
        GameObject ring = new GameObject("Range");
        ring.transform.SetParent(ghost.transform, false);
        ring.transform.localScale = new Vector3(r * 2f, r * 2f, 1f);
        ghostRange = ring.AddComponent<SpriteRenderer>();
        ghostRange.sprite = CombatFx.Circle();
        ghostRange.sortingOrder = -30;
    }

    void DestroyGhost()
    {
        if (ghost != null) Destroy(ghost);
        ghost = null;
        ghostRange = null;
        ghostSprites.Clear();
        ghostBase.Clear();
    }

    void UpdateGhost(Vector2 p, bool ok)
    {
        if (ghost == null) return;
        ghost.transform.position = new Vector3(p.x, p.y, 0f);
        Color tint = ok ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.35f, 0.35f);
        for (int i = 0; i < ghostSprites.Count; i++)
        {
            Color c = Color.Lerp(ghostBase[i], tint, 0.55f);
            c.a = 0.65f;
            ghostSprites[i].color = c;
        }
        if (ghostRange != null) ghostRange.color = ok ? new Color(1f, 0.6f, 0.3f, 0.14f) : new Color(1f, 0.3f, 0.3f, 0.1f);
    }

    void PlaceNow(Vector2 p)
    {
        TrapKit k = TrapStock.TakeLike(placingRaid ? TrapStock.RaidKit : TrapStock.Hall, placing);
        if (k == null)
        {
            WarManager.Notify("Таких ловушек больше нет", false);
            return;
        }
        Trap.Create(k, p, placingRaid);
        WarManager.Notify(k.combat ? $"{k.Name}: ставят, ~{k.Stats.installSeconds:F0} с (пока враги не рядом). Пока ставят — хрупкая!"
                                   : $"{k.Name} поставлена", true);
    }

    // ---------- Кадр ----------

    void Update()
    {
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        if (mouse == null || Camera.main == null) return;

        isRaidView = RaidManager.ViewingBattle;
        bool can = CanUse(isRaidView);
        uiButton = can;
        if (!can)
        {
            panelOpen = false;
            StopPlacing();
            hover = null;
            return;
        }
        if (placing != null && placingRaid != isRaidView) StopPlacing();   // переключили камеру на другую карту

        if (kb != null && kb.tKey.wasPressedThisFrame)
        {
            if (placing != null) StopPlacing();
            else panelOpen = !panelOpen;
        }

        Vector2 sp = mouse.position.ReadValue();
        Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
        bool overUI = OverUI(gp);
        if (overUI) CameraController.BlockZoom();
        if (placing != null || overUI) lastBlockFrame = Time.frameCount;

        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (placing != null) StopPlacing();
            else panelOpen = false;
        }

        Vector2 world = MouseWorld();
        hover = null;

        if (placing != null)
        {
            if (mouse.rightButton.wasPressedThisFrame) { StopPlacing(); return; }

            Vector2 p = Snap(world);
            problem = StockOf(placing, placingRaid) <= 0 ? "Таких ловушек больше нет" : CheckPlace(placing, p, placingRaid);
            UpdateGhost(p, problem == "");

            if (mouse.leftButton.wasPressedThisFrame && !overUI)
            {
                if (problem == "")
                {
                    PlaceNow(p);
                    bool keep = kb != null && kb.shiftKey.isPressed;
                    if (!keep || StockOf(placing, placingRaid) <= 0) StopPlacing();
                }
                else WarManager.Notify(problem, false);
            }
            return;
        }

        if (!overUI) hover = Trap.At(world, isRaidView);
    }

    // ---------- Интерфейс ----------

    void OnGUI()
    {
        UiKit.Apply();
        DrawBars();
        if (!uiButton) return;
        if (!isRaidView && MarketWindow.IsOpen) return;

        Rect br = ButtonRect;
        string label;
        UiKit.Kind kind;
        if (placing != null) { label = "Отмена  [T]"; kind = UiKit.Kind.Danger; }
        else if (panelOpen) { label = "Закрыть  [T]"; kind = UiKit.Kind.Normal; }
        else { label = "Ловушки  [T]"; kind = UiKit.Kind.Primary; }
        GUIStyle big = new GUIStyle(UiKit.ButtonStyle(kind)) { fontSize = 14, fontStyle = FontStyle.Bold };
        if (GUI.Button(br, label, big))
        {
            if (placing != null) StopPlacing();
            else panelOpen = !panelOpen;
        }

        if (panelOpen && placing == null) DrawPanel();
        if (placing != null) DrawCursorTip();
        else if (hover != null && hover.Alive) DrawHoverTip(hover.InfoText);
    }

    void DrawPanel()
    {
        Rect r = PanelRect();
        string where = isRaidView ? "поле боя" : (WarManager.IsWar ? "оборона" : "мирное время");
        Rect c = UiKit.Window(r, "Ловушки  " + UiKit.Col(UiKit.MutedHex, "· " + where), out bool close);
        if (close) { panelOpen = false; return; }

        GUIStyle small = new GUIStyle(UiKit.Small) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        GUIStyle body = new GUIStyle(UiKit.Body) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        float x = c.x, w = c.width, cy = c.y;

        var groups = Groups();
        if (groups.Count == 0)
        {
            Rect er = new Rect(x, cy, w, GroupRowH);
            GUI.Label(er, UiKit.Col(UiKit.MutedHex, isRaidView ? "Все взятые ловушки уже на поле" : "Запас ловушек пуст"), body);
            if (!isRaidView) UiKit.Tip(er, "Обычные собирает Инженерный двор (или купите на рынке), боевые делает Кузня мастера.");
            cy += GroupRowH;
        }
        for (int i = 0; i < Mathf.Min(groups.Count, MaxRows); i++)
        {
            TrapKit k = groups[i].Key;
            int count = groups[i].Value;
            string mp = ModeProblem(k, isRaidView);
            Rect lr = new Rect(x, cy, w - 110f, GroupRowH - 4f);
            string text = $"<b>{k.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"×{count}");
            if (mp != "") text += "\n" + UiKit.Col(UiKit.WarnHex, mp);
            GUI.Label(lr, text, new GUIStyle(small) { wordWrap = true, fontSize = mp != "" ? 11 : 13 });
            UiKit.Tip(lr, Traps.Details(k));
            if (UiKit.Button(new Rect(x + w - 104f, cy + 3f, 104f, GroupRowH - 10f), "Поставить", mp == "", UiKit.Kind.Primary,
                    mp == "" ? "Выбрать место на карте (ЛКМ - поставить, Shift - ещё, ПКМ / Esc - отмена)\n" + Traps.Details(k) : mp))
                BeginPlacing(k);
            cy += GroupRowH;
        }

        var placed = Placed();
        if (placed.Count > 0)
        {
            int ord = Trap.CountOn(isRaidView, false), cmb = Trap.CountOn(isRaidView, true);
            string limits = isRaidView ? $"{cmb}/{Trap.MaxCombatRaid}" : $"обычных {ord}/{Trap.MaxOrdinary} · боевых {cmb}/{Trap.MaxCombatHome}";
            float secY = cy;
            cy = UiKit.Section(x, cy, w, "Стоят");
            GUI.Label(new Rect(x, secY, w, UiKit.SectionH - 4f), limits,
                new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.LowerRight, wordWrap = false, fontSize = 11 });
            Trap toRemove = null;
            for (int i = 0; i < Mathf.Min(placed.Count, MaxRows); i++)
            {
                Trap t = placed[i];
                string s = !t.Armed ? (t.Combat ? $"ставится {t.Progress * 100f:F0}%" : "взводится")
                         : (t.Rearming ? "перевзвод" : $"взведена, сраб. {t.Charges}");
                GUI.Label(new Rect(x, cy, w - 140f, PlacedRowH - 2f), $"<b>{Traps.RecipeOf(t.Kit.type).name}</b> " + UiKit.Col(UiKit.MutedHex, s), small);
                if (UiKit.Button(new Rect(x + w - 134f, cy, 56f, 22f), "Найти", UiKit.Kind.Normal, "Показать на карте") && CameraController.Instance != null)
                    CameraController.Instance.FocusOn(t.Position);
                if (UiKit.Button(new Rect(x + w - 74f, cy, 74f, 22f), "Убрать", UiKit.Kind.Danger, "Снять ловушку - вернётся в запас")) toRemove = t;
                cy += PlacedRowH;
            }
            if (toRemove != null)
            {
                toRemove.Dismantle(true);
                WarManager.Notify("Ловушку сняли — вернулась в запас", true);
            }
        }

        cy += 4f;
        Rect hr = new Rect(x, cy, w, UiKit.RowH);
        GUI.Label(hr, UiKit.Col(UiKit.InfoHex, "?") + UiKit.Col(UiKit.MutedHex, " Где и когда ставить"), small);
        UiKit.Tip(hr, "Обычные - дома в мирное время, в своей зелёной зоне. Боевые - в бою: дома во время войны и в набеге " +
                      "(не в зелёной зоне врага); ставятся долго и, пока ставятся, хрупкие. " +
                      "Нельзя: на занятые клетки, в тумане, рядом с врагами, вплотную к другой ловушке. T - открыть / закрыть.");
    }

    void DrawCursorTip()
    {
        string reason = problem == ""
            ? UiKit.Col(UiKit.GoodHex, "ЛКМ — поставить") + UiKit.Col(UiKit.MutedHex, "   Shift — ещё")
            : UiKit.Col(UiKit.BadHex, problem);
        DrawHoverTip($"<b>{placing.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"есть: {StockOf(placing, placingRaid)}") + "\n" +
                     $"{reason}\n" + UiKit.Col(UiKit.MutedHex, "ПКМ / Esc — отмена"));
    }

    void DrawHoverTip(string text)
    {
        if (Mouse.current == null) return;
        Vector2 m = Mouse.current.position.ReadValue();
        GUIStyle st = new GUIStyle(UiKit.Body) { wordWrap = true };
        float w = 300f;
        float h = st.CalcHeight(new GUIContent(text), w - 16f) + 10f;
        Rect r = new Rect(Mathf.Min(m.x + 20f, Screen.width - w - 4f), Mathf.Min(Screen.height - m.y + 20f, Screen.height - h - 4f), w, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 8f, r.y + 5f, w - 16f, h - 10f), text, st);
    }

    // Полоски над ловушками, которые ставятся: установка (оранжевая) и прочность (если топчут)
    void DrawBars()
    {
        if (Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        float ppu = Screen.height / (2f * cam.orthographicSize);

        foreach (var t in Trap.All)
        {
            if (t == null || !t.Alive || t.Armed || t.Raid != RaidManager.ViewingBattle) continue;
            Vector2 at = t.Position + Vector2.up * 0.55f;
            float w = Mathf.Clamp(ppu * 0.9f, 22f, 60f);
            Bar(cam, at, w, t.Progress, new Color(1f, 0.65f, 0.2f), 0f);
            if (t.Combat && t.Hp < t.MaxHp - 0.5f)
            {
                float f = t.Hp / Mathf.Max(1f, t.MaxHp);
                Bar(cam, at, w, f, Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.35f, 0.9f, 0.35f), f), 7f);
            }
        }
    }

    static void Bar(Camera cam, Vector2 world, float w, float frac, Color fill, float dy)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;
        float h = Mathf.Clamp(w * 0.1f, 3f, 6f);
        Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y - h / 2f - dy, w, h);
        if (r.xMax < 0f || r.x > Screen.width || r.yMax < 0f || r.y > Screen.height) return;
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }
}
