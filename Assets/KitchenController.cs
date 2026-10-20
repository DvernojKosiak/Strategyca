using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Кухня. Рабочие здания - повара (2 / 4 / 6 по уровням). Варит похлёбку из ягод, овса и еды.
///  - Ягоды приносит Станция собирателей, овёс - ферма Ур.3, еда - обычная. Всё берётся из казны:
///    повар несёт продукты из Ратуши на Кухню (рейс через Logistics, как в мастерской), потом варит.
///  - Готовая похлёбка (порции) хранится на Кухне (вместимость 8 / 16 / 24).
///  - Похлёбка нужна казарме: с галочкой "С похлёбкой" всадники выходят усиленными (★),
///    а будущим видам войск она обязательна (UnitType.requiresPottage).
///  - Кухня стоит вплотную к казарме или к группе казарм (казармы, стоящие вплотную друг к другу) -
///    похлёбка поступает туда сама, без курьеров. Иначе порцию несёт житель (повар, курьер Ратуши...).
///  - Разрушенная Кухня похлёбку не выдаёт, снесённая - порции пропадают.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class KitchenController : MonoBehaviour
{
    public const string TypeName = "Кухня";

    public static readonly List<KitchenController> All = new List<KitchenController>();

    // Параметры по уровням (индекс = уровень - 1)
    static readonly int[] CapacityByLevel = { 8, 16, 24 };
    static readonly float[] CookTimeByLevel = { 45f, 36f, 26f };
    static readonly int[] PortionsByLevel = { 4, 4, 5 };

    // Продукты на одну варку
    public const int BerriesPerBatch = 12;
    public const int OatsPerBatch = 8;
    public const int FoodPerBatch = 10;
    public const int AutoFoodReserve = 40;   // авто-варка не трогает последние 40 еды - её едят жители

    enum Stage { Idle, Fetching, Cooking }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public int Capacity => CapacityByLevel[Lvl];
    public float CookTime => CookTimeByLevel[Lvl];
    public int BatchPortions => PortionsByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    public int Portions { get; private set; }       // готовая похлёбка на Кухне
    public int CookedTotal { get; private set; }    // сколько порций сварили за всё время
    public int ServedTotal { get; private set; }    // сколько отдали казармам
    public bool AutoCook { get; set; }              // варить, пока есть продукты и место

    Stage stage = Stage.Idle;
    float cookTimer;
    float autoTimer;

    public bool IsBusy => stage != Stage.Idle;
    public float Progress => stage == Stage.Cooking && CookTime > 0f ? 1f - cookTimer / CookTime : 0f;

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return "Несут продукты из Ратуши...";
                case Stage.Cooking: return $"Варится похлёбка ({Progress * 100f:F0}%)";
                default: return Portions + BatchPortions > Capacity ? UiKit.Col(UiKit.WarnHex, "Кухня полна") : "Кухня свободна";
            }
        }
    }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (Portions > 0) WarManager.Notify($"Кухню снесли: пропало порций похлёбки — {Portions}", false);
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (Building == null) return;

        if (stage == Stage.Cooking && Building.isProducing)
        {
            // Голодные повара и дефицит замедляют варку
            cookTimer -= Time.deltaTime * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
            if (cookTimer <= 0f) FinishCooking();
        }

        if (AutoCook)
        {
            autoTimer -= Time.deltaTime;
            if (autoTimer <= 0f)
            {
                autoTimer = 1f;
                if (CanStartWith(true)) StartCooking();
            }
        }
    }

    // ---------- Варка ----------

    static bool HasIngredients(bool keepFoodReserve)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return false;
        int food = FoodPerBatch + (keepFoodReserve ? AutoFoodReserve : 0);
        return rm.berries >= BerriesPerBatch && rm.oats >= OatsPerBatch && rm.food >= food;
    }

    bool CanStartWith(bool auto)
    {
        return Works && Building.isProducing && !IsBusy && Portions + BatchPortions <= Capacity && HasIngredients(auto);
    }

    public bool CanStart => CanStartWith(false);

    public void StartCooking()
    {
        if (!CanStart) return;
        var rm = ResourceManager.Instance;
        if (!rm.SpendBerries(BerriesPerBatch)) return;
        if (!rm.SpendOats(OatsPerBatch)) { rm.Add(0, 0, 0, 0, 0, 0, BerriesPerBatch); return; }
        if (!rm.Spend(0, 0, 0, FoodPerBatch)) { rm.Add(0, 0, 0, 0, 0, OatsPerBatch, BerriesPerBatch); return; }

        stage = Stage.Fetching;

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Повар идёт в Ратушу за продуктами и несёт их сюда
        Logistics.Request(new HaulJob
        {
            from = hall,
            to = Building,
            prefer = Building,
            onDeliver = OnIngredientsDelivered,
            onAbort = loaded => OnIngredientsAborted()
        });
    }

    void OnIngredientsDelivered()
    {
        cookTimer = CookTime;
        stage = Stage.Cooking;
    }

    // Рейс сорван (Кухню снесли, повара забрали в казарму) - продукты возвращаются в казну
    void OnIngredientsAborted()
    {
        if (ResourceManager.Instance != null)
            ResourceManager.Instance.Add(0, 0, 0, FoodPerBatch, 0, OatsPerBatch, BerriesPerBatch);
        stage = Stage.Idle;
    }

    void FinishCooking()
    {
        stage = Stage.Idle;
        int before = Portions;
        Portions = Mathf.Min(Capacity, Portions + BatchPortions);
        CookedTotal += Portions - before;
        WarManager.Notify($"Кухня: сварена похлёбка (+{Portions - before}, всего {PortionsAvailable})", true);
    }

    // ---------- Выдача похлёбки (для казармы) ----------

    /// <summary>Сколько порций можно выдать (во всех целых Кухнях).</summary>
    public static int PortionsAvailable
    {
        get
        {
            int n = 0;
            foreach (var k in All)
                if (k != null && k.Works) n += k.Portions;
            return n;
        }
    }

    /// <summary>Забрать n порций с этой Кухни. false - не хватает.</summary>
    public bool Take(int n)
    {
        if (!Works || n <= 0 || Portions < n) return false;
        Portions -= n;
        ServedTotal += n;
        return true;
    }

    /// <summary>Вернуть порции (обучение сорвано): в ближайшие Кухни со свободным местом. Что не влезло - пропало.</summary>
    public static void ReturnPortions(int n, Vector2 near)
    {
        if (n <= 0) return;
        var list = new List<KitchenController>();
        foreach (var k in All) if (k != null && k.Works) list.Add(k);
        list.Sort((a, b) => a.Building.DistanceTo(near).CompareTo(b.Building.DistanceTo(near)));

        foreach (var k in list)
        {
            int put = Mathf.Min(n, k.Capacity - k.Portions);
            if (put <= 0) continue;
            k.Portions += put;
            k.ServedTotal = Mathf.Max(0, k.ServedTotal - put);
            n -= put;
            if (n <= 0) return;
        }
        if (n > 0) WarManager.Notify($"Похлёбку некуда вернуть: пропало порций — {n}", false);
    }

    // Для теста (F9)
    public static void CheatFillAll()
    {
        foreach (var k in All) if (k != null && k.Works) k.Portions = k.Capacity;
    }

    // ---------- Соседство с казармами ----------

    /// <summary>Здания стоят вплотную: касаются друг друга стороной (не только углом).</summary>
    public static bool Adjacent(BuildingInstance a, BuildingInstance b)
    {
        if (a == null || b == null || a.type == null || b.type == null || a == b) return false;
        Vector2Int ao = a.OriginCell, asz = a.type.size;
        Vector2Int bo = b.OriginCell, bsz = b.type.size;

        bool xOverlap = ao.x < bo.x + bsz.x && bo.x < ao.x + asz.x;
        bool yOverlap = ao.y < bo.y + bsz.y && bo.y < ao.y + asz.y;
        bool xTouch = ao.x + asz.x == bo.x || bo.x + bsz.x == ao.x;
        bool yTouch = ao.y + asz.y == bo.y || bo.y + bsz.y == ao.y;
        return (xTouch && yOverlap) || (yTouch && xOverlap);
    }

    static bool IsBarracks(BuildingInstance b)
    {
        return b != null && b.type != null && b.isActiveAndEnabled && b.type.name == BarracksController.TypeName;
    }

    /// <summary>Группа казарм: эта казарма и все, что стоят вплотную к ней (цепочкой).</summary>
    public static List<BuildingInstance> BarracksGroup(BuildingInstance start)
    {
        var group = new List<BuildingInstance>();
        if (!IsBarracks(start)) return group;

        var queue = new Queue<BuildingInstance>();
        group.Add(start);
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            BuildingInstance cur = queue.Dequeue();
            foreach (var b in BuildingInstance.All)
            {
                if (!IsBarracks(b) || group.Contains(b) || !Adjacent(cur, b)) continue;
                group.Add(b);
                queue.Enqueue(b);
            }
        }
        return group;
    }

    // Кухня стоит вплотную хотя бы к одной казарме группы
    bool FeedsGroup(List<BuildingInstance> group)
    {
        if (!Works) return false;
        foreach (var b in group) if (Adjacent(Building, b)) return true;
        return false;
    }

    /// <summary>Есть ли целая Кухня вплотную к этой казарме или её группе (похлёбка без курьеров).</summary>
    public static bool AnyLinked(BuildingInstance barracks)
    {
        var group = BarracksGroup(barracks);
        foreach (var k in All) if (k != null && k.FeedsGroup(group)) return true;
        return false;
    }

    /// <summary>
    /// Откуда взять n порций для казармы: сначала Кухня вплотную к её группе (linked = true - без курьера),
    /// иначе ближайшая Кухня, где хватает похлёбки. null - похлёбки нет.
    /// </summary>
    public static KitchenController PickFor(BuildingInstance barracks, int n, out bool linked)
    {
        linked = false;
        var group = BarracksGroup(barracks);
        Vector2 at = barracks != null ? barracks.Center : Vector2.zero;

        KitchenController best = null;
        float bestD = float.MaxValue;
        foreach (var k in All)
        {
            if (k == null || !k.Works || k.Portions < n) continue;
            if (k.FeedsGroup(group)) { linked = true; return k; }
            float d = k.Building.DistanceTo(at);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    // Сколько казарм получает похлёбку от этой Кухни без курьеров
    int LinkedBarracksCount()
    {
        if (!Works) return 0;
        var all = new List<BuildingInstance>();
        foreach (var b in BuildingInstance.All)
        {
            if (!IsBarracks(b) || all.Contains(b) || !Adjacent(Building, b)) continue;
            foreach (var g in BarracksGroup(b)) if (!all.Contains(g)) all.Add(g);
        }
        return all.Count;
    }

    // ---------- Панель здания ----------

    static string Have(int have, int need)
    {
        return $"<color={(have >= need ? UiKit.GoodHex : UiKit.BadHex)}>{have}/{need}</color>";
    }

    string CookProblem()
    {
        var rm = ResourceManager.Instance;
        if (!Works || !Building.isProducing) return "Кухня не работает";
        if (IsBusy) return "Уже варится";
        if (Portions + BatchPortions > Capacity) return "Нет места для новых порций";
        if (rm == null || rm.berries < BerriesPerBatch || rm.oats < OatsPerBatch || rm.food < FoodPerBatch) return "Не хватает продуктов";
        return null;
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        int lvl = b.level;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        const string foldId = "kitchen-info";
        string infoText =
            $"Варка идёт {CookTime:F0} с и даёт {BatchPortions} порц. Ягоды - со Станции собирателей, овёс - с фермы Ур.3. " +
            "Похлёбка усиливает всадников (галочка «С похлёбкой» в казарме). Кухня вплотную к казарме (или группе казарм) " +
            $"отдаёт похлёбку туда сама, иначе её носят курьеры. Сварено всего: {CookedTotal}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float portY = barY + 14f;
        float linkY = portY + 20f;
        float problemY = linkY + 20f;
        float btnY = problemY + (b.isWorking ? 4f : 24f);
        float costY = btnY + 34f;
        float autoY = costY + 20f;
        float workersY = autoY + 30f;
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {lvl}");

        var rm = ResourceManager.Instance;
        int berries = rm != null ? rm.berries : 0;
        int oats = rm != null ? rm.oats : 0;
        int food = rm != null ? rm.food : 0;

        // --- Что происходит ---
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, st);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 10f), Progress, UiKit.Warn);

        GUI.Label(new Rect(x + 10f, y + portY, w - 20f, 18f),
            $"Похлёбка: <b>{Portions}/{Capacity}</b> порц.  " + UiKit.Col(UiKit.MutedHex, $"во всех кухнях: {PortionsAvailable}"), small);

        int linked = LinkedBarracksCount();
        Rect lr = new Rect(x + 10f, y + linkY, w - 20f, 18f);
        GUI.Label(lr, linked > 0
            ? UiKit.Col(UiKit.GoodHex, $"Рядом казарм: {linked} - похлёбка идёт туда сама")
            : UiKit.Col(UiKit.WarnHex, "Рядом нет казарм - носят курьеры"), small);
        UiKit.Tip(lr, "Поставьте Кухню вплотную к казарме или группе казарм, чтобы похлёбка поступала без курьеров.");

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        // --- Действия ---
        string why = CookProblem();
        if (UiKit.Button(new Rect(x + 10f, y + btnY, w - 20f, 32f), "Сварить похлёбку", CanStart, UiKit.Kind.Primary,
                why ?? $"Даст {BatchPortions} порц. за {CookTime:F0} с"))
            StartCooking();
        GUI.Label(new Rect(x + 10f, y + costY, w - 20f, 18f),
            $"ягоды {Have(berries, BerriesPerBatch)} · овёс {Have(oats, OatsPerBatch)} · еда {Have(food, FoodPerBatch)}", small);

        Rect ar = new Rect(x + 10f, y + autoY, w - 20f, 24f);
        bool auto = GUI.Toggle(ar, AutoCook, " Варить автоматически");
        UiKit.Tip(ar, $"Кухня варит сама, пока есть продукты и место. Последние {AutoFoodReserve} еды не трогает - их едят жители.");
        if (auto != AutoCook) AutoCook = auto;

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
