using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Дальний склад - хранилище вдали от города, вне карты. Враги до него не добираются.
///  - Ячейки покупаются за золото (очень дорого, каждая следующая дороже). В ячейку влезает CellCapacity единиц любых ресурсов.
///  - Положить / забрать товары можно только через Обозный двор (WagonYard): обоз с лошадьми, которых ведут
///    рабочие двора, идёт к краю карты, едет за кадром и возвращается.
///  - Товары в складе не входят в казну, поэтому набеги (Loot.RaidTreasury) их не трогают.
/// Склад создаётся сам при первом обращении и живёт, даже если Обозный двор снесли (товары не пропадают).
/// Ресурсы: 0 золото, 1 дерево, 2 камень, 3 еда, 4 железная руда, 5 овёс, 6 ягоды.
/// </summary>
public class FarStorage : MonoBehaviour
{
    public const int Kinds = 7;
    public const int CellCapacity = 200;     // единиц на одну ячейку
    public const int LoadPerHorse = 200;     // сколько единиц везёт одна лошадь

    // Цена 1-й, 2-й, ... ячейки (золото). Сколько цен - столько ячеек максимум.
    public static readonly int[] CellCosts = { 1500, 3000, 5000, 8000, 12000, 17000, 23000, 30000 };
    public static int MaxCells { get { return CellCosts.Length; } }

    public static readonly string[] Names = { "Золото", "Дерево", "Камень", "Еда", "Железная руда", "Овёс", "Ягоды" };
    public static readonly string[] Colors = { "#FFE040", "#D08040", "#60E0F0", "#70F070", "#B0C4DE", "#E8D890", "#F0A0C8" };

    static FarStorage instance;

    public static FarStorage Get()
    {
        if (instance == null)
        {
            GameObject go = new GameObject("FarStorage");
            instance = go.AddComponent<FarStorage>();
        }
        return instance;
    }

    void Awake()
    {
        if (instance == null) instance = this;
        else if (instance != this) Destroy(gameObject);
    }

    public int Cells { get; private set; }
    readonly int[] stock = new int[Kinds];
    int incoming;

    // Чем больше ячеек, тем больше лошадей нужно обозу: 1-2 ячейки - 1, 3-5 - 2, 6+ - 3
    public static int HorsesRequired(int cells) { return Mathf.Clamp(1 + cells / 3, 1, 3); }
    public int HorsesNeeded { get { return HorsesRequired(Cells); } }
    public int CartLoad { get { return HorsesNeeded * LoadPerHorse; } }

    public int Capacity { get { return Cells * CellCapacity; } }
    public int Incoming { get { return incoming; } }
    public int Stock(int i) { return stock[i]; }

    public int Total
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Kinds; i++) n += stock[i];
            return n;
        }
    }

    public int Free { get { return Mathf.Max(0, Capacity - Total - incoming); } }
    public int NextCellCost { get { return Cells < MaxCells ? CellCosts[Cells] : 0; } }

    public bool TryBuyCell()
    {
        if (Cells >= MaxCells) return false;
        var rm = ResourceManager.Instance;
        if (rm == null || !rm.Spend(NextCellCost, 0, 0, 0)) return false;
        Cells++;
        WarManager.Notify("Дальний склад: куплена ячейка №" + Cells, true);
        return true;
    }

    public void Reserve(int n) { incoming += n; }
    public void Unreserve(int n) { incoming = Mathf.Max(0, incoming - n); }

    public void Put(int[] a)
    {
        for (int i = 0; i < Kinds; i++) stock[i] += Mathf.Max(0, a[i]);
    }

    public int[] TakeOut(int[] want)
    {
        int[] got = new int[Kinds];
        for (int i = 0; i < Kinds; i++)
        {
            int n = Mathf.Clamp(want[i], 0, stock[i]);
            stock[i] -= n;
            got[i] = n;
        }
        return got;
    }

    // ---------- Казна ----------

    public static int Treasury(int i)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return 0;
        switch (i)
        {
            case 0: return rm.gold;
            case 1: return rm.wood;
            case 2: return rm.stone;
            case 3: return rm.food;
            case 4: return rm.iron;
            case 5: return rm.oats;
            default: return rm.berries;
        }
    }

    // sign = +1 - положить в казну, -1 - списать из казны
    public static void AddTreasury(int[] a, int sign)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return;
        rm.Add(a[0] * sign, a[1] * sign, a[2] * sign, a[3] * sign, a[4] * sign, a[5] * sign, a[6] * sign);
    }

    public static int Sum(int[] a)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) n += a[i];
        return n;
    }

    public static string Describe(int[] a)
    {
        var parts = new List<string>();
        for (int i = 0; i < Kinds; i++)
            if (a[i] > 0) parts.Add(a[i] + " " + Names[i].ToLower());
        return parts.Count > 0 ? string.Join(", ", parts) : "ничего";
    }
}

/// <summary>
/// Обозный двор. Отсюда уходят и сюда возвращаются обозы в Дальний склад.
///  - Рабочие двора (до 2) физически выводят обоз: выходят из здания, идут рядом с повозкой,
///    запряжённой лошадьми из Конюшни. Лошадей нужно от 1 до 3: чем больше ячеек в Дальнем складе, тем больше.
///    Нет лошадей или рабочих на месте - обоз не выйдет.
///  - "Отправить": товары списываются из казны, обоз идёт к краю карты, едет за кадром
///    (товары ложатся в склад), потом возвращается к двору с рабочими и лошадьми.
///  - "Забрать": обоз идёт к краю карты пустым, едет за кадром, возвращается с товарами и сдаёт их в казну.
///    Если идёт война, обоз ждёт за краем карты.
///  - Чем выше уровень двора, тем быстрее обоз (60 / 45 / 30 с за кадром).
///  - Одновременно работает один обоз на двор. Пока обоз в пути, рабочие и лошади заняты.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class WagonYard : MonoBehaviour
{
    public const string TypeName = "Обозный двор";
    public const int MaxDrivers = 2;

    static readonly float[] TripByLevel = { 60f, 45f, 30f };
    public static float TripFor(int level) { return TripByLevel[Mathf.Clamp(level, 1, TripByLevel.Length) - 1]; }

    public BuildingInstance Building { get; private set; }
    public bool Works { get { return Building != null && Building.isActiveAndEnabled && !Building.IsRuined; } }

    CartAgent cart;
    public bool CartBusy { get { return cart != null; } }

    readonly int[] sendPick = new int[FarStorage.Kinds];
    readonly int[] callPick = new int[FarStorage.Kinds];
    int tab;

    void Awake() { Building = GetComponent<BuildingInstance>(); }

    // Рабочие двора, которые сейчас на месте и могут вести обоз
    List<CitizenAgent> FindDrivers()
    {
        var list = new List<CitizenAgent>();
        if (Building == null) return list;
        foreach (var a in CitizenAgent.All)
        {
            if (list.Count >= MaxDrivers) break;
            if (a == null || a.IsDead || a.IsMilitary) continue;
            if (!ReferenceEquals(a.Workplace, Building) || !a.IsInside(Building)) continue;
            if (a.IsCourier || a.IsHauling || a.IsHealerBusy || a.IsGathering || a.IsLyingInBed || a.IsCartDriver) continue;
            if (a.HpFraction < 0.4f) continue;
            list.Add(a);
        }
        return list;
    }

    // Берёт лошадей и рабочих. false - чего-то не хватает (лошади возвращены).
    bool Prepare(out List<CitizenAgent> drivers, out int horses)
    {
        horses = 0;
        drivers = FindDrivers();
        if (!Works || CartBusy) return false;

        if (WarManager.IsWar)
        {
            WarManager.Notify("Обозный двор: идёт война — обоз не выходит", false);
            return false;
        }

        if (drivers.Count == 0)
        {
            GameClock clock = GameClock.Instance;
            bool offHours = clock != null && !clock.IsWorkTime;
            WarManager.Notify(offHours
                ? "Обозный двор: нерабочее время, на месте нет рабочих — обоз выйдет утром"
                : "Обозный двор: нет рабочих на месте — некому вести обоз", false);
            return false;
        }

        int need = FarStorage.Get().HorsesNeeded;
        while (horses < need && StableController.TryTakeHorse()) horses++;
        if (horses < need)
        {
            for (int i = 0; i < horses; i++) StableController.ReturnHorse();
            horses = 0;
            WarManager.Notify($"Обозу нужно лошадей: {need}, в конюшнях: {StableController.HorsesAvailable}", false);
            return false;
        }
        return true;
    }

    void StartSend(int[] want)
    {
        if (!Works || CartBusy) return;
        FarStorage v = FarStorage.Get();

        int[] cargo = new int[FarStorage.Kinds];
        for (int i = 0; i < cargo.Length; i++) cargo[i] = Mathf.Clamp(want[i], 0, FarStorage.Treasury(i));
        int units = FarStorage.Sum(cargo);
        if (units <= 0) return;
        if (units > v.CartLoad || units > v.Free)
        {
            WarManager.Notify("Обозный двор: в Дальнем складе нет места под такой груз", false);
            return;
        }

        if (!Prepare(out List<CitizenAgent> drivers, out int horses)) return;

        FarStorage.AddTreasury(cargo, -1);   // товары уходят из казны сразу
        v.Reserve(units);
        cart = CartAgent.Create(this, cargo, true, drivers, horses);
        WarManager.Notify("Обоз вышел в Дальний склад: " + FarStorage.Describe(cargo), true);
        System.Array.Clear(sendPick, 0, sendPick.Length);
    }

    void StartCall(int[] want)
    {
        if (!Works || CartBusy) return;
        FarStorage v = FarStorage.Get();

        int[] clamp = new int[FarStorage.Kinds];
        for (int i = 0; i < clamp.Length; i++) clamp[i] = Mathf.Clamp(want[i], 0, v.Stock(i));
        if (FarStorage.Sum(clamp) <= 0 || FarStorage.Sum(clamp) > v.CartLoad) return;

        if (!Prepare(out List<CitizenAgent> drivers, out int horses)) return;

        int[] cargo = v.TakeOut(clamp);
        v.Reserve(FarStorage.Sum(cargo));    // место остаётся занятым, пока груз в пути
        cart = CartAgent.Create(this, cargo, false, drivers, horses);
        WarManager.Notify("Обоз вышел за товарами: " + FarStorage.Describe(cargo), true);
        System.Array.Clear(callPick, 0, callPick.Length);
    }

    // ---------- Панель здания ----------

    static int Room(FarStorage v, int sum, bool send)
    {
        int room = v.CartLoad - sum;
        if (send) room = Mathf.Min(room, v.Free - sum);
        return Mathf.Max(0, room);
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        FarStorage v = FarStorage.Get();
        BuildingInstance b = Building;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;
        GUIStyle center = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };

        const float rowH = 26f;
        const string foldId = "wagonyard-info";
        string infoText =
            $"Дальний склад стоит вне карты: товары там недоступны врагам и не входят в казну (набеги их не трогают). " +
            $"Ячейка вмещает {FarStorage.CellCapacity} ед. любых ресурсов, каждая следующая дороже. " +
            $"Обоз берёт до {v.CartLoad} ед. ({FarStorage.LoadPerHorse} на лошадь); чем больше ячеек, тем больше лошадей нужно. " +
            $"За кадром обоз едет {TripFor(b.level):F0} с (выше уровень - быстрее). Во время войны обоз ждёт за краем карты.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        // --- Раскладка (те же числа ниже при рисовании) ---
        float cy0 = UiKit.TitleH + 6f;
        float fillY = cy0 + 22f;
        float barY = fillY + 20f;
        float horsesY = barY + 14f;
        float problemY = horsesY + 20f;
        float workersY = problemY + (b.isWorking ? 2f : 22f);
        float tabsY = workersY + 30f;
        float rowsY = tabsY + 32f;
        float sumY = rowsY + FarStorage.Kinds * rowH;
        float goY = sumY + 24f;
        float cellsY = goY + 38f;
        float foldY = cellsY + 34f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        // --- Статус обоза ---
        string status = !Works ? UiKit.Col(UiKit.BadHex, "Двор разрушен")
            : (CartBusy ? "Обоз: <b>" + cart.StatusText + "</b>" : "Обоз стоит во дворе");
        GUI.Label(new Rect(x + 10f, y + cy0, w - 20f, 20f), status, st);

        // --- Заполнение Дальнего склада ---
        int used = v.Total + v.Incoming;
        float frac = v.Capacity > 0 ? used / (float)v.Capacity : 0f;
        GUI.Label(new Rect(x + 10f, y + fillY, w - 20f, 18f),
            $"Дальний склад: <b>{used}/{v.Capacity}</b>  " + UiKit.Col(UiKit.MutedHex, $"ячеек {v.Cells}/{FarStorage.MaxCells}"), small);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 8f), frac, frac >= 0.9f ? UiKit.Warn : UiKit.Info);

        // --- Лошади и рабочие ---
        int horsesNow = StableController.HorsesAvailable;
        string hcol = horsesNow >= v.HorsesNeeded ? UiKit.GoodHex : UiKit.WarnHex;
        int drivers = FindDrivers().Count;
        string dcol = drivers > 0 ? UiKit.GoodHex : UiKit.WarnHex;
        Rect hr = new Rect(x + 10f, y + horsesY, w - 20f, 18f);
        GUI.Label(hr, $"Лошади: <color={hcol}><b>{horsesNow}</b></color> из {v.HorsesNeeded} нужных  · рабочих на месте: <color={dcol}><b>{drivers}</b></color>", small);
        UiKit.Tip(hr, "Обозу нужны свободные лошади из Конюшни и рабочие двора, которые сейчас на месте.");

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        // --- Вкладки ---
        tab = UiKit.Tabs(new Rect(x + 10f, y + tabsY, w - 18f, 26f), new[] { "Отправить", "Забрать" }, tab);

        // --- Строки ресурсов ---
        bool send = tab == 0;
        int[] pick = send ? sendPick : callPick;
        int step = Event.current != null && Event.current.shift ? 100 : 10;
        float cy = y + rowsY;

        for (int i = 0; i < FarStorage.Kinds; i++)
        {
            int avail = send ? FarStorage.Treasury(i) : v.Stock(i);
            pick[i] = Mathf.Clamp(pick[i], 0, avail);
            int sum = FarStorage.Sum(pick);
            int room = Mathf.Min(Room(v, sum, send), avail - pick[i]);

            GUI.Label(new Rect(x + 10f, cy + 2f, w - 210f, rowH - 2f),
                $"<color={FarStorage.Colors[i]}>{FarStorage.Names[i]}</color>  " + UiKit.Col(UiKit.MutedHex, avail.ToString()), small);

            if (UiKit.Button(new Rect(x + w - 192f, cy, 26f, rowH - 4f), "−", pick[i] > 0, UiKit.Kind.Normal, null))
                pick[i] = Mathf.Max(0, pick[i] - step);

            GUI.Label(new Rect(x + w - 164f, cy, 50f, rowH - 4f), pick[i] > 0 ? "<b>" + pick[i] + "</b>" : "0", center);

            string roomWhy = room > 0 ? null : (avail - pick[i] <= 0 ? "Больше нет" : "Обоз или склад заполнен");
            if (UiKit.Button(new Rect(x + w - 112f, cy, 26f, rowH - 4f), "+", room > 0, UiKit.Kind.Normal, roomWhy)) pick[i] += Mathf.Min(step, room);
            if (UiKit.Button(new Rect(x + w - 82f, cy, 70f, rowH - 4f), "Макс.", room > 0, UiKit.Kind.Normal, roomWhy)) pick[i] += room;
            cy += rowH;
        }

        int total = FarStorage.Sum(pick);
        Rect sumR = new Rect(x + 10f, y + sumY, w - 20f, 20f);
        GUI.Label(sumR, $"В обозе: <b>{total}</b> / {v.CartLoad}  " + UiKit.Col(UiKit.MutedHex, "Shift - шаг 100"), small);

        // --- Главное действие ---
        string why = null;
        if (!Works) why = "Двор разрушен";
        else if (CartBusy) why = "Обоз уже в пути";
        else if (send && v.Cells == 0) why = "Сначала купите ячейку";
        else if (total <= 0) why = "Выберите товары";
        bool canGo = why == null;
        string label = send ? (v.Cells == 0 ? "Нет ячеек" : $"Отправить обоз ({total})") : $"Забрать товары ({total})";
        if (UiKit.Button(new Rect(x + 10f, y + goY, w - 20f, 32f), label, canGo, UiKit.Kind.Primary,
                canGo ? (send ? "Обоз отвезёт выбранное из казны в Дальний склад" : "Обоз привезёт выбранное в казну") : why))
        {
            if (send) StartSend(sendPick); else StartCall(callPick);
        }

        // --- Покупка ячейки ---
        if (v.Cells >= FarStorage.MaxCells)
            GUI.Label(new Rect(x + 10f, y + cellsY + 4f, w - 20f, 22f), UiKit.Col(UiKit.GoodHex, "Все ячейки куплены"), small);
        else
        {
            int cost = v.NextCellCost;
            bool canBuy = FarStorage.Treasury(0) >= cost;
            if (UiKit.Button(new Rect(x + 10f, y + cellsY, w - 20f, 28f), $"Купить ячейку  ·  {cost} зол", canBuy, UiKit.Kind.Normal,
                    canBuy ? $"Ячейка №{v.Cells + 1}: +{FarStorage.CellCapacity} ед. места" : $"Не хватает золота: нужно {cost}"))
                v.TryBuyCell();
        }

        // --- Подробнее ---
        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}

/// <summary>
/// Обоз: повозка с лошадьми и рабочие двора, которые идут рядом (это настоящие жители, см. CitizenAgent_Cart.cs).
///  - Выход: повозка идёт от двери двора к ближайшему краю карты и скрывается (рабочие и лошади тоже).
///  - За кадром TripSeconds секунд. Если обоз вёз товары на склад, они ложатся в Дальний склад.
///    Если ехал за товарами - возвращается с ними. Пока в городе война, обоз ждёт за краем карты.
///  - Возвращение: от края карты к двери двора. Рабочие входят обратно в здание, лошади возвращаются в Конюшню,
///    привезённое попадает в казну.
///  - Пока обоз на карте, враги его не трогают (считаем, что его охраняют).
///  - Двор снесли (или он в руинах дольше RuinWait), пока обоз был в пути: привезённое возвращается в Дальний склад.
/// </summary>
public class CartAgent : MonoBehaviour
{
    public const float Speed = 2.4f;
    const float RuinWait = 240f;   // сколько обоз ждёт у разрушенного двора (восстановление идёт 180 с)

    enum Mode { Outbound, Away, Back }

    Mode mode;
    bool sending;                  // true - везёт товары на склад, false - едет за товарами
    WagonYard yard;
    int[] cargo;
    int horses;
    float tripSeconds = 60f;
    readonly List<CitizenAgent> drivers = new List<CitizenAgent>();
    Vector2 edge;
    float timer, doorTimer, ruinWait, face = 1f;
    Vector2 doorPoint, lastPos;
    GameObject gfx;
    readonly GridMover mover = new GridMover();

    public BuildingInstance YardBuilding { get { return yard != null ? yard.Building : null; } }
    public bool DriversVisible { get { return mode != Mode.Away; } }

    public Vector2 DriverPosition(int slot)
    {
        Vector2 p = transform.position;
        return p + new Vector2(-0.62f * face, slot == 0 ? -0.14f : 0.22f);
    }

    public static CartAgent Create(WagonYard yard, int[] cargo, bool send, List<CitizenAgent> drv, int horseCount)
    {
        var nav = GridNavigation.Instance;
        BuildingInstance b = yard.Building;
        Vector2 edge = EdgePoint(b.Center, nav);
        Vector2 start = DoorPoint(nav, b, b.Center);

        GameObject go = new GameObject("Cart");
        go.transform.position = new Vector3(start.x, start.y, 0f);
        CartAgent c = go.AddComponent<CartAgent>();
        c.yard = yard;
        c.cargo = cargo;
        c.sending = send;
        c.edge = edge;
        c.horses = horseCount;
        c.tripSeconds = WagonYard.TripFor(b.level);
        c.mode = Mode.Outbound;
        c.lastPos = start;
        c.BuildHorses(horseCount);
        StableController.Lend(horseCount);   // лошади в пути: место в конюшне под них занято

        for (int i = 0; i < drv.Count; i++)
            if (drv[i].StartCartDuty(c, i)) c.drivers.Add(drv[i]);
        return c;
    }

    void Awake()
    {
        gfx = new GameObject("Gfx");
        gfx.transform.SetParent(transform, false);
        AddPart("Body", new Vector2(0f, 0f), new Vector2(0.7f, 0.42f), new Color(0.55f, 0.36f, 0.18f), 11);
        AddPart("Cover", new Vector2(-0.04f, 0.1f), new Vector2(0.45f, 0.3f), new Color(0.92f, 0.85f, 0.65f), 12);
        AddPart("Wheel", new Vector2(0.18f, -0.2f), new Vector2(0.18f, 0.18f), new Color(0.15f, 0.1f, 0.06f), 12);
        AddPart("Wheel2", new Vector2(-0.22f, -0.2f), new Vector2(0.18f, 0.18f), new Color(0.15f, 0.1f, 0.06f), 12);
    }

    // Лошади идут впереди повозки
    void BuildHorses(int n)
    {
        for (int i = 0; i < n; i++)
        {
            AddPart("Horse" + i, new Vector2(0.62f + 0.42f * i, 0f), new Vector2(0.36f, 0.22f), new Color(0.42f, 0.27f, 0.13f), 12);
            AddPart("Head" + i, new Vector2(0.82f + 0.42f * i, 0.1f), new Vector2(0.14f, 0.14f), new Color(0.32f, 0.2f, 0.1f), 13);
        }
    }

    void AddPart(string n, Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(gfx.transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.color = c;
        sr.sortingOrder = order;
    }

    void SetVisible(bool v) { if (gfx != null) gfx.SetActive(v); }

    public void DriverLeft(CitizenAgent d) { drivers.Remove(d); }

    public string StatusText
    {
        get
        {
            switch (mode)
            {
                case Mode.Outbound: return "идёт к краю карты";
                case Mode.Away:
                    int s = Mathf.CeilToInt(Mathf.Max(0f, timer));
                    if (timer <= 0f && WarManager.IsWar) return "ждёт конца войны за краем карты";
                    return "вдали от города, ещё " + s + " с";
                default: return sending ? "возвращается во двор" : "везёт товары во двор";
            }
        }
    }

    // ---------- Движение ----------

    void Update()
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        drivers.RemoveAll(d => d == null || d.IsDead);

        // Куда смотрит повозка (лошади всегда впереди)
        Vector2 pos = transform.position;
        if (Mathf.Abs(pos.x - lastPos.x) > 0.002f)
        {
            face = pos.x > lastPos.x ? 1f : -1f;
            if (gfx != null) gfx.transform.localScale = new Vector3(face, 1f, 1f);
        }
        lastPos = pos;

        switch (mode)
        {
            case Mode.Outbound:
            {
                bool arrived = mover.Step(transform, nav, edge, Speed, 0.3f);
                if (arrived || mover.Fails >= 5 || EdgeDistance(transform.position, nav) < 1.2f)
                {
                    mode = Mode.Away;
                    timer = tripSeconds;
                    mover.Clear();
                    SetVisible(false);
                }
                break;
            }

            case Mode.Away:
            {
                timer -= Time.deltaTime;
                if (timer > 0f) break;

                // Товары доехали до склада сразу, как кончилась дорога (даже если в городе война)
                if (sending && FarStorage.Sum(cargo) > 0) DepositGoods();

                // В войну обоз (рабочие, лошади, груз) ждёт за краем карты
                if (WarManager.IsWar) break;

                transform.position = new Vector3(edge.x, edge.y, 0f);
                lastPos = edge;
                SetVisible(true);
                mover.Clear();
                mover.ResetFails();
                doorTimer = 0f;
                mode = Mode.Back;
                break;
            }

            case Mode.Back:
            {
                if (yard == null || yard.Building == null) { Abandon(); break; }   // двора больше нет
                if (!yard.Works)
                {
                    // Двор в руинах - ждём восстановления, но не дольше RuinWait: потом расходимся
                    ruinWait += Time.deltaTime;
                    if (ruinWait > RuinWait) Abandon();
                    break;
                }
                ruinWait = 0f;

                doorTimer -= Time.deltaTime;
                if (doorTimer <= 0f)
                {
                    doorTimer = 2f;
                    doorPoint = DoorPoint(nav, yard.Building, transform.position);
                }

                bool arrived = mover.Step(transform, nav, doorPoint, Speed, 0.3f);
                if (arrived || mover.Fails >= 5) Arrive();
                break;
            }
        }
    }

    // Товары доехали до склада
    void DepositGoods()
    {
        FarStorage v = FarStorage.Get();
        v.Unreserve(FarStorage.Sum(cargo));
        v.Put(cargo);
        WarManager.Notify("Обоз довёз до Дальнего склада: " + FarStorage.Describe(cargo), true);
        cargo = new int[FarStorage.Kinds];
    }

    // Обоз вернулся к двору
    void Arrive()
    {
        if (!sending && FarStorage.Sum(cargo) > 0)
        {
            FarStorage.Get().Unreserve(FarStorage.Sum(cargo));
            FarStorage.AddTreasury(cargo, 1);
            WarManager.Notify("Обоз вернулся: в казну " + FarStorage.Describe(cargo), true);
            cargo = new int[FarStorage.Kinds];
        }
        else WarManager.Notify("Обоз вернулся во двор", true);
        EndTrip(yard != null ? yard.Building : null);
    }

    // Двор снесли
    void Abandon()
    {
        if (FarStorage.Sum(cargo) > 0)
        {
            FarStorage v = FarStorage.Get();
            v.Unreserve(FarStorage.Sum(cargo));
            v.Put(cargo);
            cargo = new int[FarStorage.Kinds];
        }
        WarManager.Notify("Обозный двор не принимает обоз — товары остались в Дальнем складе, рабочие разошлись", false);
        EndTrip(null);
    }

    void EndTrip(BuildingInstance enterAt)
    {
        foreach (var d in drivers)
            if (d != null) d.EndCartDuty(enterAt);
        drivers.Clear();

        StableController.Unlend(horses);
        for (int i = 0; i < horses; i++)
            if (!StableController.ReturnHorse())
                WarManager.Notify("В Конюшнях нет места: лошадь обоза потеряна", false);
        horses = 0;
        Destroy(gameObject);
    }

    // ---------- Геометрия ----------

    static Vector2 DoorPoint(GridNavigation nav, BuildingInstance b, Vector2 from)
    {
        if (nav != null && nav.TryGetDoorCell(b, nav.WorldToCell(from), out Vector2Int d)) return nav.CellCenter(d);
        return b.Center;
    }

    static Vector2 EdgePoint(Vector2 p, GridNavigation nav)
    {
        if (nav == null) return p;
        float h = nav.mapSize / 2f - 0.5f;
        float dl = p.x + h, dr = h - p.x, db = p.y + h, du = h - p.y;
        float m = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, du));
        Vector2 e;
        if (m == dl) e = new Vector2(-h, p.y);
        else if (m == dr) e = new Vector2(h, p.y);
        else if (m == db) e = new Vector2(p.x, -h);
        else e = new Vector2(p.x, h);

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