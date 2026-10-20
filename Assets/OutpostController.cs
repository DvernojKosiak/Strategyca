using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Аванпост: расширяет зелёную зону застройки (см. Territory). Работает, только если:
///  - подключён к зоне (касается её или дороги, ведущей из зоны) и не разрушен;
///  - снабжается: на аванпосте есть припасы (еда) и связь с Ратушей не прервана.
/// Снабжение:
///  - Аванпост постоянно ест припасы (FoodPerMinute). Когда их меньше RefillBelow, из Ратуши
///    приносят еду: жители (сначала работники Ратуши) или курьеры Курьерской, если их закрепили
///    за этим аванпостом (у курьеров груз больше, с упряжкой - ещё и быстрее).
///  - Связь с Ратушей: аванпост не может долго жить без неё. Если подвоза из Ратуши не было
///    HallLinkTimeout секунд, аванпост заброшен (даже если еда есть). Поэтому из Ратуши иногда
///    везут и "проверочный" груз, даже когда склад полон.
///  - Ур.3: у аванпоста есть смотрители. Если в радиусе зоны аванпоста стоит ферма и на её
///    складе есть еда, смотрители носят еду оттуда (это не заменяет связь с Ратушей).
///  - Еда кончилась: аванпост держится StarveGrace секунд, потом заброшен.
/// Заброшенный аванпост не расширяет зону, пока его снова не снабдят.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class OutpostController : MonoBehaviour
{
    public const string TypeName = "Аванпост";

    public static readonly List<OutpostController> All = new List<OutpostController>();

    // ---------- Числа (можно менять) ----------
    static readonly float[] RadiusByLevel = { 6f, 8f, 10f };
    static readonly int[] CapacityByLevel = { 40, 60, 80 };          // сколько еды помещается
    static readonly float[] FoodPerMinuteByLevel = { 6f, 8f, 10f };  // сколько ест
    public const int HallParcel = 20;            // столько еды несёт житель из Ратуши
    public const int FarmParcel = 15;            // столько смотритель приносит с фермы
    public const float StarveGrace = 120f;       // столько держится без еды
    public const float HallLinkTimeout = 480f;   // столько держится без подвоза из Ратуши
    const float RefillBelow = 0.6f;              // ниже этой доли припасов - везём из Ратуши
    const float LinkRefreshAt = 0.6f;            // прошло столько от HallLinkTimeout - везём, даже если склад полон
    const float FarmRefillBelow = 0.9f;

    public static float RadiusFor(int level) { return RadiusByLevel[Mathf.Clamp(level, 1, RadiusByLevel.Length) - 1]; }

    BuildingInstance bld;
    public BuildingInstance Building
    {
        get
        {
            if (bld == null) bld = GetComponent<BuildingInstance>();
            return bld;
        }
    }

    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) : 1;

    // ---------- Состояние ----------
    public int Food { get; private set; }
    public int Capacity => CapacityByLevel[Lvl - 1];
    public float FoodPerMinute => FoodPerMinuteByLevel[Lvl - 1];

    bool inited;
    bool supplied = true;
    float eatAcc, starve, lastHallSupply, tick;
    int incoming;                 // еда, которую уже несут сюда (место под неё занято)
    bool hallRunPending, farmRunPending;

    public int HallTrips { get; private set; }
    public int FarmTrips { get; private set; }

    public bool Supplied => supplied;
    public float SinceHallSupply => inited ? Time.time - lastHallSupply : 0f;
    public float HallLinkLeft => Mathf.Max(0f, HallLinkTimeout - SinceHallSupply);
    public float StarveLeft => Mathf.Max(0f, StarveGrace - starve);
    public bool LinkDue => SinceHallSupply > HallLinkTimeout * LinkRefreshAt;

    /// <summary>Пора везти еду из Ратуши (мало припасов или давно не было связи).</summary>
    public bool NeedsHallFood => Food + incoming < Capacity * RefillBelow || LinkDue;

    public bool Connected => Territory.Instance != null && Building != null && Territory.Instance.IsActive(Building);

    /// <summary>Для Territory: аванпост снабжается (не аванпост - всегда true).</summary>
    public static bool IsSupplied(BuildingInstance b)
    {
        if (b == null) return false;
        OutpostController oc = b.GetComponent<OutpostController>();
        return oc == null || oc.supplied;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Жизнь ----------

    void Update()
    {
        BuildingInstance b = Building;
        if (b == null || b.type == null) return;

        if (!inited)
        {
            inited = true;
            Food = Capacity / 2;          // новый аванпост строят с запасом на первое время
            lastHallSupply = Time.time;
        }

        float dt = Time.deltaTime;
        if (b.IsRuined) return;           // в руинах никто не ест и никто ничего не везёт

        // Расход припасов
        eatAcc += FoodPerMinute / 60f * dt;
        while (eatAcc >= 1f)
        {
            eatAcc -= 1f;
            if (Food > 0) Food--;
        }
        if (Food > 0) starve = 0f;
        else starve += dt;

        bool now = starve < StarveGrace && SinceHallSupply < HallLinkTimeout;
        if (now != supplied)
        {
            supplied = now;
            Territory.MarkDirty();
            string where = $"[{b.OriginCell.x},{b.OriginCell.y}]";
            if (supplied) WarManager.Notify($"Аванпост {where} снова снабжается — зона восстановлена", true);
            else if (Food <= 0) WarManager.Notify($"Аванпост {where} заброшен: кончилась еда", false);
            else WarManager.Notify($"Аванпост {where} заброшен: нет подвоза из Ратуши", false);
        }

        tick -= dt;
        if (tick > 0f) return;
        tick = 2f;
        RequestSupplies();
    }

    void RequestSupplies()
    {
        // Из Ратуши - жители (если курьеров за аванпостом не закрепили или связь вот-вот прервётся)
        bool couriers = CourierHouse.AnyAssigned(Building);
        if (!hallRunPending && NeedsHallFood && (!couriers || HallLinkLeft < 60f || (Food <= 0 && starve > 30f)))
        {
            hallRunPending = true;
            Logistics.Request(MakeHallJob(HallParcel, () => hallRunPending = false));
        }

        // Ур.3: смотрители носят еду с фермы в радиусе аванпоста
        if (Lvl >= 3 && !farmRunPending && Food + incoming < Capacity * FarmRefillBelow) TryFarmRun();
    }

    // ---------- Подвоз из Ратуши ----------

    /// <summary>
    /// Рейс "Ратуша -> аванпост". parcel - сколько еды берут (не больше свободного места).
    /// Используют жители (Logistics) и курьеры Курьерской. done - вызывается в конце рейса (успех или срыв).
    /// </summary>
    public HaulJob MakeHallJob(int parcel, System.Action done)
    {
        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;
        int want = Mathf.Clamp(Capacity - Food - incoming, 0, parcel);   // 0 - "проверочный" рейс: связь есть, места нет
        incoming += want;
        int carried = 0;

        return new HaulJob
        {
            from = hall,
            to = Building,
            prefer = hall,
            onPickup = () =>
            {
                var rm = ResourceManager.Instance;
                carried = rm != null ? Mathf.Min(want, rm.food) : 0;
                if (carried > 0) rm.Spend(0, 0, 0, carried);
            },
            onDeliver = () =>
            {
                incoming = Mathf.Max(0, incoming - want);
                Food = Mathf.Min(Capacity, Food + carried);
                lastHallSupply = Time.time;
                HallTrips++;
                if (done != null) done();
            },
            onAbort = loaded =>
            {
                incoming = Mathf.Max(0, incoming - want);
                if (loaded && carried > 0 && ResourceManager.Instance != null) ResourceManager.Instance.Add(0, 0, 0, carried);
                if (done != null) done();
            }
        };
    }

    // ---------- Подвоз с фермы (Ур.3) ----------

    /// <summary>Ферма в радиусе аванпоста, на складе которой есть еда (ближайшая).</summary>
    public BuildingInstance FarmInReach(int minFood = 5)
    {
        BuildingInstance b = Building;
        if (b == null) return null;
        float r = RadiusFor(Lvl);
        BuildingInstance best = null;
        float bestD = float.MaxValue;
        foreach (var f in BuildingInstance.All)
        {
            if (f == null || f.type == null || f.type.prodFood <= 0 || f.IsRuined || !f.isActiveAndEnabled) continue;
            if (f.storedFood < minFood) continue;
            float d = f.DistanceTo(b.Center);
            if (d > r || d >= bestD) continue;
            bestD = d;
            best = f;
        }
        return best;
    }

    void TryFarmRun()
    {
        BuildingInstance farm = FarmInReach();
        if (farm == null) return;
        CitizenAgent keeper = FreeKeeper();
        if (keeper == null) return;

        int want = Mathf.Clamp(Capacity - Food - incoming, 0, FarmParcel);
        if (want <= 0) return;
        incoming += want;
        int carried = 0;
        farmRunPending = true;

        var job = new HaulJob
        {
            from = farm,
            to = Building,
            prefer = Building,
            onPickup = () =>
            {
                carried = farm != null ? Mathf.Min(want, farm.storedFood) : 0;
                if (carried > 0) farm.storedFood -= carried;
            },
            onDeliver = () =>
            {
                incoming = Mathf.Max(0, incoming - want);
                Food = Mathf.Min(Capacity, Food + carried);
                FarmTrips++;
                farmRunPending = false;
            },
            onAbort = loaded =>
            {
                incoming = Mathf.Max(0, incoming - want);
                if (loaded && carried > 0)
                {
                    if (farm != null && !farm.IsRuined) farm.storedFood += carried;
                    else if (ResourceManager.Instance != null) ResourceManager.Instance.Add(0, 0, 0, carried);
                }
                farmRunPending = false;
            }
        };

        if (!keeper.StartHaul(job))
        {
            incoming = Mathf.Max(0, incoming - want);
            farmRunPending = false;
        }
    }

    // Свободный смотритель (рабочий этого аванпоста)
    CitizenAgent FreeKeeper()
    {
        GameClock clock = GameClock.Instance;
        bool workTime = clock == null || clock.IsWorkTime;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.IsMilitary || !ReferenceEquals(a.Workplace, Building)) continue;
            if (a.IsCourier || a.IsHauling || a.IsHealerBusy || a.IsGathering || a.IsLyingInBed || a.IsCartDriver || a.IsPanicking) continue;
            if (!workTime && !a.IsSkeleton) continue;
            if (a.HpFraction < 0.4f) continue;
            return a;
        }
        return null;
    }

    // ---------- Панель ----------

    static string Clock(float s)
    {
        int t = Mathf.Max(0, Mathf.CeilToInt(s));
        return $"{t / 60}:{t % 60:00}";
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        int lvl = b.level;
        float cx = x + UiKit.Pad, cw = w - UiKit.Pad * 2f;

        // --- Состояние: коротко + пояснение в подсказке ---
        string state, stateTip;
        if (b.IsRuined) { state = UiKit.Col(UiKit.BadHex, "Разрушен"); stateTip = "Территория не расширяется, пока аванпост не восстановят."; }
        else if (!supplied)
        {
            state = UiKit.Col(UiKit.BadHex, Food <= 0 ? "Заброшен: нет еды" : "Заброшен: нет подвоза из Ратуши");
            stateTip = "Зона не расширяется, пока аванпост не снабжают едой из Ратуши.";
        }
        else if (Connected) { state = UiKit.Col(UiKit.GoodHex, $"Работает · зона +{RadiusFor(lvl):F0} кл."); stateTip = "Аванпост расширяет зелёную зону застройки вокруг себя."; }
        else { state = UiKit.Col(UiKit.BadHex, "Нет дороги от зелёной зоны"); stateTip = "Проведите дорогу от зелёной зоны к аванпосту — иначе территория не расширяется."; }

        // --- Подробности (в свёрнутом блоке) ---
        bool couriers = CourierHouse.AnyAssigned(b);
        string who = couriers ? "Из Ратуши возят курьеры Курьерской" : "Из Ратуши носят жители (сначала работники Ратуши)";
        string farmLine;
        if (lvl < 3) farmLine = UiKit.Col(UiKit.MutedHex, "Ур. 3: смотрители носят еду с фермы в радиусе аванпоста");
        else
        {
            BuildingInstance farm = FarmInReach(0);
            farmLine = farm != null
                ? $"Ферма рядом [{farm.OriginCell.x},{farm.OriginCell.y}]: {farm.storedFood} еды — смотрители носят оттуда"
                : UiKit.Col(UiKit.WarnHex, "В радиусе нет фермы — вся еда только из Ратуши");
        }
        string details = $"{who}.\nРейсов из Ратуши: {HallTrips}" + (lvl >= 3 ? $", с фермы: {FarmTrips}" : "") +
                         $"\nПодвоз был {Clock(SinceHallSupply)} назад.\n{farmLine}\n" +
                         UiKit.Col(UiKit.MutedHex, "Закрепить курьеров: панель Курьерской → строка аванпоста.");
        bool open = UiKit.IsOpen("outpost-more");
        float detailsH = open ? UiKit.TextH(details, cw, UiKit.Small) + 4f : 0f;

        bool keepers = lvl >= 3 && PopulationManager.Instance != null;
        float stateY = UiKit.TitleH + 6f;
        float barY = stateY + UiKit.RowH + 2f;
        float linkY = barY + 24f;
        float foldY = linkY + UiKit.RowH + 2f;
        float workersY = foldY + UiKit.FoldH + detailsH + 4f;
        float upgradeY = workersY + (keepers ? 32f : 0f);
        float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName}  {UiKit.Col(UiKit.MutedHex, "· Ур. " + lvl)}");

        Rect sr = new Rect(cx, y + stateY, cw, UiKit.RowH);
        GUI.Label(sr, state);
        UiKit.Tip(sr, stateTip);

        // --- Припасы ---
        float frac = Capacity > 0 ? Food / (float)Capacity : 0f;
        float minutesLeft = FoodPerMinute > 0f ? Food / FoodPerMinute : 0f;
        string barText = Food > 0
            ? $"Еда {Food}/{Capacity} · ~{minutesLeft:F0} мин"
            : $"Нет еды! Продержится {Clock(StarveLeft)}";
        if (incoming > 0) barText += $" · везут {incoming}";
        Rect bar = new Rect(cx, y + barY, cw, 18f);
        UiKit.Bar(bar, frac, Color.Lerp(UiKit.Bad, UiKit.Good, frac), barText);
        UiKit.Tip(bar, $"Припасы аванпоста: {Food}/{Capacity} еды. Ест {FoodPerMinute:F0} в минуту.");

        // --- Связь с Ратушей ---
        string linkCol = HallLinkLeft < 90f ? UiKit.BadHex : (LinkDue ? UiKit.WarnHex : UiKit.GoodHex);
        Rect lr = new Rect(cx, y + linkY, cw, UiKit.RowH);
        UiKit.Row(lr, "Без подвоза продержится", UiKit.Col(linkCol, Clock(HallLinkLeft)));
        UiKit.Tip(lr, $"Связь с Ратушей: подвоз был {Clock(SinceHallSupply)} назад.");

        if (UiKit.Fold(new Rect(cx, y + foldY, cw, UiKit.FoldH), "outpost-more", "Снабжение"))
            GUI.Label(new Rect(cx, y + foldY + UiKit.FoldH, cw, detailsH), details, UiKit.Small);

        if (keepers) PopulationManager.Instance.DrawWorkersRow(cx, y + workersY, b);

        float uy = UiKit.Section(cx, y + upgradeY, cw, "Улучшение");
        UpgradeRules.DrawUpgradeSection(cx, uy, cw, b);
        return new Rect(x, y, w + 5f, h);
    }
}
