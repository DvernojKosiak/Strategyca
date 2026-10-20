using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Меню и размещение полевых укреплений (см. FieldPost).
///  - Кнопка "Заслоны [B]" справа внизу (над кнопкой "В бой!") есть только когда это имеет смысл:
///    дома во время войны или на карте набега (разведка / бой).
///  - Укрепления НЕ покупаются: их изготавливает Кузня мастера (путь Военного дела) и они лежат в запасе Ратуши.
///    Дома берётся вещь из этого запаса, на набеге - из того, что взяли с собой в окне «В бой!».
///    Исключение - Быстрый заслон (ветка «Военный строитель»): ставится прямо отсюда за дерево из казны.
///  - Полевая башня: не ближе 6 клеток к зелёной зоне врага (при размещении зона подсвечивается красным).
///  - Заслон на колёсах: в списке «Стоят» кнопка «Катить» - потом ЛКМ по точке, куда катить (ПКМ / Esc - отмена).
///    Его берёт ближайший свободный боец (FieldPost.OrderMove).
///  - Выбрали вид - призрак у курсора: ЛКМ - поставить, R - повернуть на 45°, Shift - ставить несколько,
///    ПКМ / Esc - отмена. Зелёный призрак - можно, красный - нельзя (причина в подсказке у курсора).
///  - Стенка непроходима, поэтому нельзя ставить на бойцов. Можно ставить на руины зданий.
///    Нельзя: на занятые клетки, в тумане, рядом с врагами, на набеге - далеко от своих бойцов
///    (укрепление строят они), больше MaxPerMap штук на карту, если нет вещи в запасе.
/// Создаётся в BuildingPlacer.Start. Мышь над меню / в режиме размещения не должна кликать по карте:
/// для этого BuildingPlacer и RaidManager спрашивают FieldPostManager.Blocks.
/// </summary>
public class FieldPostManager : MonoBehaviour
{
    public static FieldPostManager Instance { get; private set; }

    const float PanelW = 350f;
    const float PlaceFromUnits = 16f;   // на набеге: не дальше стольких клеток от своих бойцов
    const float EnemyClear = 4f;        // не ближе стольких клеток к врагу
    const float SpecRowH = 40f;         // строка вида укрепления в меню
    const float PlacedRowH = 26f;       // строка поставленного укрепления

    int placing = -1;
    bool placingRaid;
    FieldPost movingPost;               // выбираем, куда катить этот заслон на колёсах
    readonly List<GameObject> zoneMarks = new List<GameObject>();   // подсветка зелёной зоны врага (башня)
    float angle;
    bool panelOpen, uiButton, isRaidView;
    int lastBlockFrame = -10;
    string problem = "";
    FieldPost hover;

    GameObject ghost;
    readonly List<SpriteRenderer> ghostSprites = new List<SpriteRenderer>();
    readonly List<Color> ghostBase = new List<Color>();

    public static bool Placing { get { return Instance != null && (Instance.placing >= 0 || Instance.movingPost != null); } }

    /// <summary>Курсор над меню укреплений или идёт размещение: карта кликов не получает.</summary>
    public static bool Blocks(Vector2 guiPoint)
    {
        FieldPostManager m = Instance;
        if (m == null) return false;
        return m.placing >= 0 || m.movingPost != null || m.lastBlockFrame == Time.frameCount || m.OverUI(guiPoint);
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

    static bool CanUse()
    {
        if (RaidManager.ViewingBattle)
        {
            RaidManager rm = RaidManager.Instance;
            return rm != null && (rm.State == RaidManager.Phase.Scouting || rm.State == RaidManager.Phase.Battle);
        }
        return WarManager.IsWar;
    }

    static Rect ButtonRect { get { return new Rect(Screen.width - 190f, Screen.height - 98f, 180f, 40f); } }

    Rect PanelRect()
    {
        int n = FieldPost.CountOn(isRaidView);
        int kinds = Mathf.Max(1, ShownKinds(isRaidView).Count);
        float h = UiKit.WindowH(kinds * SpecRowH + (n > 0 ? UiKit.SectionH + n * PlacedRowH : 0f) + 4f + UiKit.RowH);   // как в DrawPanel
        return new Rect(Screen.width - PanelW - 10f, Screen.height - 104f - h, PanelW, h);
    }

    bool OverUI(Vector2 gp)
    {
        if (!uiButton) return false;
        return ButtonRect.Contains(gp) || (panelOpen && PanelRect().Contains(gp));
    }

    // ---------- Запас ----------

    // Какие виды показывать в меню: доступные ветке сейчас; изготовленные раньше (ветку сменили) - пока есть в запасе
    static List<int> ShownKinds(bool raid)
    {
        var list = new List<int>();
        for (int k = 0; k < FieldPost.Specs.Length; k++)
        {
            bool allowed = FieldPost.Allowed((PostKind)k);
            if (allowed || (FieldPost.Specs[k].crafted && Stock(k, raid) > 0)) list.Add(k);
        }
        return list;
    }

    // Сколько таких укреплений можно поставить: дома - запас Ратуши, на набеге - то, что взяли с собой.
    // Быстрый заслон - сколько хватит дерева в казне (и дома, и в набеге: его рубят на месте)
    static int Stock(int kind, bool raid)
    {
        if (!FieldPost.Specs[kind].crafted)
            return FieldPost.Allowed((PostKind)kind) ? FieldPost.AffordableQuick() : 0;
        if (raid)
        {
            RaidManager rm = RaidManager.Instance;
            return rm != null ? rm.KitCount((PostKind)kind) : 0;
        }
        return FieldGear.Count((PostKind)kind);
    }

    static string NoStockText(int kind, bool raid)
    {
        FieldPost.Spec s = FieldPost.Specs[kind];
        if (!s.crafted)
            return FieldPost.Allowed((PostKind)kind) ? $"не хватает дерева (нужно {s.wood})" : FieldPost.NeedText((PostKind)kind);
        return raid ? "не взяли с собой (окно «В бой!»)" : "нет в запасе — изготовьте в Кузне мастера";
    }

    // ---------- Размещение ----------

    // Укрепления ставятся по центрам клеток
    static Vector2 Snap(Vector2 w) { return new Vector2(Mathf.Floor(w.x) + 0.5f, Mathf.Floor(w.y) + 0.5f); }

    static Vector2 MouseWorld()
    {
        Camera cam = Camera.main;
        Vector2 s = Mouse.current.position.ReadValue();
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        return new Vector2(w.x, w.y);
    }

    // На клетку можно поставить стенку: она свободна или это руины здания, которое ещё не начали отстраивать
    static bool Buildable(Vector2Int c, bool raid)
    {
        if (raid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null || !map.InBounds(c)) return false;
            if (!map.IsBlocked(c)) return true;
            if (map.IsTree(c)) return false;
            foreach (var b in RaidBuilding.All)
            {
                if (b == null) continue;
                if (c.x >= b.OriginCell.x && c.x < b.OriginCell.x + b.SizeCells.x &&
                    c.y >= b.OriginCell.y && c.y < b.OriginCell.y + b.SizeCells.y) return b.IsRuined;
            }
            return false;
        }

        GridNavigation nav = GridNavigation.Instance;
        if (nav == null || !nav.InBounds(c)) return false;
        if (!nav.IsBlocked(c)) return true;
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null) continue;
            Vector2Int o = b.OriginCell, sz = b.type.size;
            if (c.x >= o.x && c.x < o.x + sz.x && c.y >= o.y && c.y < o.y + sz.y) return b.IsRuined && !b.RebuildPaid;
        }
        return false;
    }

    // Почему сюда нельзя поставить ("" - можно)
    string CheckPlace(int kind, Vector2 p, bool raid)
    {
        FieldPost.Spec s = FieldPost.Specs[kind];
        float ang = s.IsTower ? 0f : angle;
        if (FieldPost.CountOn(raid) >= FieldPost.MaxPerMap) return $"Не больше {FieldPost.MaxPerMap} укреплений на карте — сверните лишнее";
        if (s.IsTower && FieldPost.CountOn(raid, PostKind.FieldTower) >= FieldPost.MaxTowers) return $"Не больше {FieldPost.MaxTowers} полевых башен на карте";
        if (Stock(kind, raid) <= 0) return s.name + ": " + NoStockText(kind, raid);

        List<Vector2Int> cells = FieldPost.CellsFor(s, p, ang, raid);
        foreach (var c in cells)
            if (!Buildable(c, raid)) return "Здесь занято или край карты";

        float a = ang * Mathf.Deg2Rad;
        Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (s.length * 0.5f);
        foreach (var o in FieldPost.All)
        {
            if (o == null || !o.Alive || o.Raid != raid) continue;
            if (o.DistanceTo(p) < 1.8f || o.DistanceTo(p - d) < 1.2f || o.DistanceTo(p + d) < 1.2f) return "Слишком близко к другому укреплению";
            foreach (var c in o.Cells) if (cells.Contains(c)) return "Слишком близко к другому укреплению";
        }

        if (!FogOfWar.IsVisible(p)) return "Эту точку не видно (туман войны)";

        // Полевая башня - не ближе 6 клеток к зелёной зоне врага
        if (s.IsTower)
        {
            string zone = FieldPost.EnemyZoneProblem(p, raid);
            if (zone != "") return zone;
        }

        // Стенка непроходима: на ней не должно стоять бойцов
        var set = new HashSet<Vector2Int>(cells);
        if (raid)
        {
            RaidMap map = RaidMap.Instance;
            foreach (var u in RaidUnit.All)
                if (u != null && u.Alive && map != null && set.Contains(map.WorldToCell(u.Position))) return "Бойцы стоят на месте стенки — отойдите";
            if (RaidUnit.NearestOf(false, p, EnemyClear) != null) return "Рядом защитники — не дадут строить";
            if (RaidUnit.NearestOf(true, p, PlaceFromUnits) == null) return "Слишком далеко от ваших бойцов (строят они)";
        }
        else
        {
            GridNavigation nav = GridNavigation.Instance;
            if (nav != null)
            {
                foreach (var ca in CitizenAgent.All)
                    if (ca != null && !ca.IsDead && !ca.IsInsideBuilding && set.Contains(nav.WorldToCell(ca.transform.position))) return "Бойцы стоят на месте стенки — отойдите";
                foreach (var en in EnemyAgent.All)
                    if (EnemyAgent.Alive(en) && set.Contains(nav.WorldToCell(en.Position))) return "Враг стоит на месте стенки";
            }
            if (EnemyAgent.AnyWithin(p, EnemyClear)) return "Рядом враги — не дадут строить";
        }
        return "";
    }

    void BeginPlacing(int kind)
    {
        placing = kind;
        placingRaid = isRaidView;
        panelOpen = false;
        MakeGhost(kind);
    }

    void StopPlacing()
    {
        placing = -1;
        DestroyGhost();
    }

    void MakeGhost(int kind)
    {
        DestroyGhost();
        FieldPost.Spec s = FieldPost.Specs[kind];
        ghost = new GameObject("FieldPostGhost");
        FieldPost.MakeLook(ghost.transform, s, ghostSprites);
        foreach (var sr in ghostSprites)
        {
            ghostBase.Add(sr.color);
            sr.sortingOrder += 10;
        }

        if (s.IsTower)
        {
            // Круг, докуда стреляет башня
            GameObject rr = new GameObject("TowerRange");
            rr.transform.SetParent(ghost.transform, false);
            rr.transform.localScale = new Vector3(s.towerRange * 2f, s.towerRange * 2f, 1f);
            SpriteRenderer rsr = rr.AddComponent<SpriteRenderer>();
            rsr.sprite = CombatFx.Circle();
            rsr.color = new Color(1f, 0.85f, 0.4f, 0.1f);
            rsr.sortingOrder = -30;
            MakeZoneMarks();
        }

        if (s.healRadius > 0f)
        {
            // Круг, в котором бойцы лечатся
            GameObject ring = new GameObject("Range");
            ring.transform.SetParent(ghost.transform, false);
            ring.transform.localScale = new Vector3(s.healRadius * 2f, s.healRadius * 2f, 1f);
            SpriteRenderer sr = ring.AddComponent<SpriteRenderer>();
            sr.sprite = CombatFx.Circle();
            sr.color = new Color(0.45f, 1f, 0.7f, 0.13f);
            sr.sortingOrder = -30;
        }
    }

    void DestroyGhost()
    {
        if (ghost != null) Destroy(ghost);
        ghost = null;
        ghostSprites.Clear();
        ghostBase.Clear();
        foreach (var z in zoneMarks) if (z != null) Destroy(z);
        zoneMarks.Clear();
    }

    // Подсветка для Полевой башни: зелёная зона врага (красным) и полоса EnemyZoneClear вокруг неё (бледнее)
    void MakeZoneMarks()
    {
        GameObject Mark(Vector2 at, float size, bool round, Color c, int order)
        {
            GameObject go = new GameObject("EnemyZoneMark");
            go.transform.position = new Vector3(at.x, at.y, 0f);
            go.transform.localScale = new Vector3(size, size, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = round ? CombatFx.Circle() : CombatFx.Square();
            sr.color = c;
            sr.sortingOrder = order;
            zoneMarks.Add(go);
            return go;
        }

        Color zone = new Color(1f, 0.25f, 0.2f, 0.16f), clear = new Color(1f, 0.45f, 0.3f, 0.07f);
        float pad = FieldPost.EnemyZoneClear;
        if (placingRaid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null) return;
            Vector2 c = map.CellCenter(BotVillage.Center);
            float r = TrapManager.EnemyZoneRadius;
            Mark(c, (r + pad) * 2f + 1f, false, clear, -29);
            Mark(c, r * 2f + 1f, false, zone, -28);
            return;
        }
        foreach (var cp in FieldPost.EnemyCampPoints())
        {
            float r = FieldPost.CampZoneRadius;
            Mark(cp, (r + pad) * 2f, true, clear, -29);
            Mark(cp, r * 2f, true, zone, -28);
        }
    }

    void UpdateGhost(Vector2 p, bool ok)
    {
        if (ghost == null) return;
        ghost.transform.position = new Vector3(p.x, p.y, 0f);
        bool tower = placing >= 0 && FieldPost.Specs[placing].IsTower;
        ghost.transform.rotation = Quaternion.Euler(0f, 0f, tower ? 0f : angle);
        Color tint = ok ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.35f, 0.35f);
        for (int i = 0; i < ghostSprites.Count; i++)
        {
            Color c = Color.Lerp(ghostBase[i], tint, 0.55f);
            c.a = 0.65f;
            ghostSprites[i].color = c;
        }
    }

    void PlaceNow(Vector2 p)
    {
        PostKind kind = (PostKind)placing;
        FieldPost.Spec s = FieldPost.Specs[placing];

        // Быстрый заслон: не из запаса - дерево из казны, сразу на место
        if (!s.crafted)
        {
            ResourceManager res = ResourceManager.Instance;
            if (!FieldPost.Allowed(kind) || res == null || !res.Spend(0, s.wood, 0, 0))
            {
                WarManager.Notify(s.name + ": " + NoStockText(placing, placingRaid), false);
                return;
            }
            FieldPost.Create(kind, p, angle, placingRaid, 1f, -1f);
            WarManager.Notify($"{s.name}: −{s.wood} дерева, ставят ~{s.buildSeconds:F0} с. Строители Двора помогут быстрее", true);
            return;
        }

        // Берём вещь из запаса (дома - из Ратуши, на набеге - из того, что взяли с собой)
        FieldGear.Item item = null;
        bool got;
        if (placingRaid)
        {
            RaidManager rm = RaidManager.Instance;
            got = rm != null && rm.TakeKit(kind, out item);
        }
        else got = FieldGear.Take(kind, out item);

        if (!got || item == null)
        {
            WarManager.Notify(s.name + ": " + NoStockText(placing, placingRaid), false);
            return;
        }

        FieldPost.Create(kind, p, angle, placingRaid, item.strength, item.pool);
        if (s.IsTower) WarManager.Notify($"{s.name}: собирают, ~{s.buildSeconds:F0} с (пока рядом нет врагов). Потом стреляет сама", true);
        else WarManager.Notify($"{s.name}: закладывают, ~{s.buildSeconds:F0} с (пока рядом нет врагов). Пока строится — хрупкий: враг вплотную разнесёт стройку", true);
    }

    // Можно ли катить заслон в эту точку ("" - можно). Едет по прямой: препятствие на пути - остановится.
    static string CheckMoveTarget(FieldPost p, Vector2 t)
    {
        if (p == null || !p.Alive) return "Заслона уже нет";
        if (!p.Built) return "Заслон ещё строится";
        float d = Vector2.Distance(p.Position, t);
        if (d < 0.5f) return "Он уже здесь";
        if (d > FieldPost.MoveMaxDistance) return $"Слишком далеко: за раз не больше {FieldPost.MoveMaxDistance:F0} клеток";
        if (!FogOfWar.IsVisible(t)) return "Эту точку не видно (туман войны)";
        if (p.Raid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null || !map.InBounds(map.WorldToCell(t))) return "Край карты";
        }
        else
        {
            GridNavigation nav = GridNavigation.Instance;
            if (nav == null || !nav.InBounds(nav.WorldToCell(t))) return "Край карты";
        }
        return "";
    }

    // ---------- Кадр ----------

    void Update()
    {
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        if (mouse == null || Camera.main == null) return;

        isRaidView = RaidManager.ViewingBattle;
        bool can = CanUse();
        uiButton = can;
        if (!can)
        {
            panelOpen = false;
            StopPlacing();
            movingPost = null;
            hover = null;
            return;
        }
        if (placing >= 0 && placingRaid != isRaidView) StopPlacing();   // переключили камеру на другую карту
        if (movingPost != null && (!movingPost.Alive || movingPost.Raid != isRaidView)) movingPost = null;

        if (kb != null && kb.bKey.wasPressedThisFrame)
        {
            if (placing >= 0 || movingPost != null) { StopPlacing(); movingPost = null; }
            else panelOpen = !panelOpen;
        }

        Vector2 sp = mouse.position.ReadValue();
        Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
        bool overUI = OverUI(gp);
        if (overUI) CameraController.BlockZoom();
        if (placing >= 0 || movingPost != null || overUI) lastBlockFrame = Time.frameCount;   // клик в этом кадре карта не получает

        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (placing >= 0) StopPlacing();
            else if (movingPost != null) movingPost = null;
            else panelOpen = false;
        }

        Vector2 world = MouseWorld();
        hover = null;

        // Выбираем, куда катить заслон на колёсах
        if (movingPost != null)
        {
            if (mouse.rightButton.wasPressedThisFrame) { movingPost = null; return; }
            Vector2 target = Snap(world);
            problem = CheckMoveTarget(movingPost, target);
            if (mouse.leftButton.wasPressedThisFrame && !overUI)
            {
                if (problem == "")
                {
                    movingPost.OrderMove(target);
                    movingPost = null;
                }
                else WarManager.Notify(problem, false);
            }
            return;
        }

        if (placing >= 0)
        {
            if (kb != null && kb.rKey.wasPressedThisFrame) angle = (angle + 45f) % 180f;
            if (mouse.rightButton.wasPressedThisFrame) { StopPlacing(); return; }

            Vector2 p = Snap(world);
            problem = CheckPlace(placing, p, placingRaid);
            UpdateGhost(p, problem == "");

            if (mouse.leftButton.wasPressedThisFrame && !overUI)
            {
                if (problem == "")
                {
                    PlaceNow(p);
                    bool keep = kb != null && kb.shiftKey.isPressed;
                    if (!keep || Stock(placing, placingRaid) <= 0) StopPlacing();
                }
                else WarManager.Notify(problem, false);
            }
            return;
        }

        if (!overUI)
        {
            float bestD = float.MaxValue;
            foreach (var fp in FieldPost.All)
            {
                if (fp == null || !fp.Alive || fp.Raid != isRaidView || !fp.HitBy(world)) continue;
                float d = fp.DistanceTo(world);
                if (d < bestD) { bestD = d; hover = fp; }
            }
        }
    }

    // ---------- Интерфейс ----------

    void OnGUI()
    {
        UiKit.Apply();
        DrawBars();
        if (!uiButton) return;

        // Кнопка
        Rect br = ButtonRect;
        string label;
        UiKit.Kind kind;
        if (placing >= 0 || movingPost != null) { label = "Отмена  [B]"; kind = UiKit.Kind.Danger; }
        else if (panelOpen) { label = "Закрыть  [B]"; kind = UiKit.Kind.Normal; }
        else { label = "Заслоны  [B]"; kind = UiKit.Kind.Primary; }
        GUIStyle big = new GUIStyle(UiKit.ButtonStyle(kind)) { fontSize = 14, fontStyle = FontStyle.Bold };
        if (GUI.Button(br, label, big))
        {
            if (placing >= 0 || movingPost != null) { StopPlacing(); movingPost = null; }
            else panelOpen = !panelOpen;
        }

        if (panelOpen && placing < 0 && movingPost == null) DrawPanel();
        if (placing >= 0) DrawCursorTip();
        else if (movingPost != null) DrawMoveTip();
        else if (hover != null && hover.Alive) DrawHoverTip(hover.InfoText);
    }

    void DrawPanel()
    {
        Rect r = PanelRect();
        string where = isRaidView ? "поле боя" : "оборона";
        Rect c = UiKit.Window(r, "Полевые укрепления  " + UiKit.Col(UiKit.MutedHex, "· " + where), out bool close);
        if (close) { panelOpen = false; return; }

        GUIStyle small = new GUIStyle(UiKit.Small) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        GUIStyle two = new GUIStyle(UiKit.Body) { wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        float x = c.x, w = c.width, cy = c.y;

        var kinds = ShownKinds(isRaidView);
        if (kinds.Count == 0)
        {
            GUI.Label(new Rect(x, cy, w, SpecRowH - 4f), UiKit.Col(UiKit.MutedHex, "Нечего ставить — нужен путь Военного дела"), two);
            cy += SpecRowH;
        }
        foreach (int k in kinds)
        {
            FieldPost.Spec s = FieldPost.Specs[k];
            int stock = Stock(k, isRaidView);
            string stockText;
            if (!s.crafted)
                stockText = UiKit.Col(stock > 0 ? UiKit.GoodHex : UiKit.WarnHex, $"{s.wood} дерева" + (stock > 0 ? $" · хватит на {stock}" : " · не хватает"));
            else stockText = stock > 0
                ? UiKit.Col(UiKit.GoodHex, $"в запасе: {stock}")
                : UiKit.Col(UiKit.WarnHex, "нет в запасе");
            Rect lr = new Rect(x, cy, w - 110f, SpecRowH - 4f);
            GUI.Label(lr, $"<b>{s.name}</b>\n<size=11>{stockText}</size>", two);
            string tip = s.IsTower
                ? $"{s.info}\nУрон {s.towerDamage:F0}+, дальность {s.towerRange:F0} кл., раз в {s.towerCooldown:F1} с, прочность {s.hp:F0}+. " +
                  $"Не ближе {FieldPost.EnemyZoneClear:F0} кл. к зелёной зоне врага; не больше {FieldPost.MaxTowers} на карте"
                : $"{s.info}\nДлина {s.length:F0} кл., стройка {s.buildSeconds:F0} с, прочность {s.hp:F0}+";
            UiKit.Tip(lr, tip);
            string why = stock > 0
                ? "Выбрать место на карте (ЛКМ - поставить, " + (s.IsTower ? "" : "R - повернуть, ") + "Shift - ещё, ПКМ / Esc - отмена)"
                : $"{s.name}: {NoStockText(k, isRaidView)}";
            if (UiKit.Button(new Rect(x + w - 104f, cy + 5f, 104f, SpecRowH - 12f), "Поставить", stock > 0, UiKit.Kind.Primary, why))
                BeginPlacing(k);
            cy += SpecRowH;
        }

        int n = FieldPost.CountOn(isRaidView);
        if (n > 0)
        {
            float secY = cy;
            cy = UiKit.Section(x, cy, w, "Стоят");
            GUI.Label(new Rect(x, secY, w, UiKit.SectionH - 4f), $"{n}/{FieldPost.MaxPerMap}",
                new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.LowerRight, wordWrap = false, fontSize = 11 });
            FieldPost toRemove = null;
            foreach (var p in FieldPost.All)
            {
                if (p == null || !p.Alive || p.Raid != isRaidView) continue;
                string state = p.Built
                    ? $"прочн. {Mathf.CeilToInt(p.Hp)}" + (p.S.healPool > 0f ? $", лечение {Mathf.CeilToInt(p.Pool)}" : "")
                    : $"строится {p.Progress * 100f:F0}%";
                if (p.HasMoveOrder) state += p.Moving ? ", катят" : ", ждёт бойца";
                float labelW = w - (p.S.wheeled ? 204f : 140f);
                GUI.Label(new Rect(x, cy, labelW, PlacedRowH - 2f), $"<b>{p.S.tag}</b> " + UiKit.Col(UiKit.MutedHex, state), small);
                if (p.S.wheeled)
                {
                    if (p.HasMoveOrder)
                    {
                        if (UiKit.Button(new Rect(x + w - 198f, cy, 60f, 22f), "Стоп", UiKit.Kind.Normal, "Остановить: боец отпустит заслон"))
                            p.StopMove(null);
                    }
                    else if (UiKit.Button(new Rect(x + w - 198f, cy, 60f, 22f), "Катить", p.Built, UiKit.Kind.Primary,
                                 p.Built ? $"Выбрать на карте, куда катить (до {FieldPost.MoveMaxDistance:F0} кл., по прямой). Покатит ближайший свободный боец; на ходу заслон изнашивается"
                                         : "Сначала достройте"))
                    {
                        movingPost = p;
                        panelOpen = false;
                    }
                }
                if (UiKit.Button(new Rect(x + w - 134f, cy, 56f, 22f), "Найти", UiKit.Kind.Normal, "Показать на карте") && CameraController.Instance != null)
                    CameraController.Instance.FocusOn(p.Position);
                string removeTip = p.S.crafted ? "Убрать - вещь вернётся в запас" : $"Разобрать - вернётся {p.QuickRefund} дерева";
                if (UiKit.Button(new Rect(x + w - 74f, cy, 74f, 22f), "Убрать", UiKit.Kind.Danger, removeTip)) toRemove = p;
                cy += PlacedRowH;
            }
            if (toRemove != null)
            {
                bool full = !toRemove.Built;
                if (!toRemove.S.crafted)
                {
                    int back = toRemove.QuickRefund;
                    toRemove.Dismantle(true);
                    WarManager.Notify($"{toRemove.S.name} разобрали — вернулось {back} дерева", true);
                }
                else
                {
                    toRemove.Dismantle(true);
                    WarManager.Notify(full ? "Стройку отменили — вещь вернулась в запас" : "Укрепление убрали — вещь вернулась в запас", true);
                }
            }
        }

        cy += 4f;
        Rect hr = new Rect(x, cy, w, UiKit.RowH);
        GUI.Label(hr, UiKit.Col(UiKit.InfoHex, "?") + UiKit.Col(UiKit.MutedHex, " Как работают укрепления"), small);
        UiKit.Tip(hr, "Стенка непроходима и режет урон, только если враг по ту сторону. Недостроенную легко разнести. " +
                      "Нельзя ставить на бойцов, в тумане, рядом с врагами; в набеге - только рядом со своими бойцами. " +
                      "Изготавливает Кузня мастера (Военное дело); в набег берут в окне «В бой!». " +
                      "Полевая башня стреляет сама, но не ближе 6 кл. к зелёной зоне врага. " +
                      "Заслон на колёсах катит боец («Катить»), его сдвигают отбойник, чёрная дыра, пружины и тяжёлые враги. " +
                      "Быстрый заслон (Военный строитель) - сразу за дерево. B - открыть / закрыть.");
    }

    void DrawMoveTip()
    {
        string reason = problem == "" ? UiKit.Col(UiKit.GoodHex, "ЛКМ — катить сюда") : UiKit.Col(UiKit.BadHex, problem);
        DrawHoverTip($"<b>{movingPost.S.name}</b>\n{reason}\n" +
                     UiKit.Col(UiKit.MutedHex, "Едет по прямой и встанет у препятствия. ПКМ / Esc — отмена"));
    }

    void DrawCursorTip()
    {
        FieldPost.Spec s = FieldPost.Specs[placing];
        string reason = problem == ""
            ? UiKit.Col(UiKit.GoodHex, "ЛКМ — поставить") + UiKit.Col(UiKit.MutedHex, "   Shift — ещё")
            : UiKit.Col(UiKit.BadHex, problem);
        DrawHoverTip($"<b>{s.name}</b>  " + UiKit.Col(UiKit.MutedHex, $"в запасе: {Stock(placing, placingRaid)}") + $"\n{reason}\n" +
                     UiKit.Col(UiKit.MutedHex, "R — повернуть, ПКМ / Esc — отмена"));
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

    // Полоски над укреплениями: стройка / прочность (если повреждено) / лечебный запас
    void DrawBars()
    {
        if (Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        float ppu = Screen.height / (2f * cam.orthographicSize);

        foreach (var p in FieldPost.All)
        {
            if (p == null || !p.Alive) continue;
            Vector2 at = p.Position + Vector2.up * 0.95f;
            float w = Mathf.Clamp(ppu * p.S.length * 0.8f, 26f, 110f);
            float dy = 0f;

            if (!p.Built)
            {
                Bar(cam, at, w, p.Progress, new Color(1f, 0.65f, 0.2f), dy);
                continue;
            }
            if (p.Hp < p.MaxHp - 0.5f)
            {
                float f = p.Hp / p.MaxHp;
                Bar(cam, at, w, f, Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.35f, 0.9f, 0.35f), f), dy);
                dy += 8f;
            }
            if (p.S.healPool > 0f && p.MaxPool > 0f)
                Bar(cam, at, w * 0.8f, p.Pool / p.MaxPool, new Color(0.4f, 0.75f, 1f), dy);
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
