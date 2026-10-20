using UnityEngine;

/// <summary>
/// Поручения жителя (продолжение класса CitizenAgent).
///  - Курьер: идёт к зданию, забирает его склад и несёт в Ратушу. Вызывают кнопка "Собрать"
///    (PopulationManager.DispatchCourier) и Курьерская (она задаёт груз за рейс и долю, что доезжает).
///  - Перенос груза (HaulJob из Logistics): из здания "from" в здание "to" - материалы мастерской,
///    улучшения, овёс в Конюшню, похлёбка в казарму и т.п. Сорвалось - заказчик получает onAbort.
///  - Целитель (рабочий Дома целителей): берёт еду из казны в сумку, лечит раненых на улице
///    и подкармливает рабочих прямо в зданиях. Остаток еды из сумки возвращает в казну.
///  - Койка в лазарете: тяжело раненый или истощённый ложится и быстро лечится (еда из казны).
/// Во время войны поручения не отменяются: если враг рядом, житель прячется и потом продолжает.
/// </summary>
public partial class CitizenAgent
{
    const float ErrandTimeout = 240f;   // столько секунд на одну половину рейса, потом - сорвано
    const float BedBelow = 0.4f;        // ниже этой доли здоровья ищем койку в лазарете

    float errandTimer;

    // ---------- Курьер ----------
    bool courierActive;
    bool courierLoaded;
    BuildingInstance courierSrc;
    CourierHouse courierOwner;
    int courierCarry;
    float courierShare = 1f;
    readonly int[] courierLoad = new int[10];   // золото, дерево, камень, еда, руда, овёс, ягоды, травы, смола, листва
    static readonly string[] LoadNames = { "золото", "дерево", "камень", "еда", "руда", "овёс", "ягоды", "травы", "смола", "листва" };

    public bool IsCourier => courierActive;
    public CourierHouse CourierOwner => courierActive ? courierOwner : null;

    // ---------- Перенос груза ----------
    HaulJob haul;
    bool haulLoaded;

    public bool IsHauling => haul != null;

    // ---------- Целитель ----------
    enum HealPhase { None, ToUnit, ToBuilding, Feeding, Return }
    HealPhase healPhase = HealPhase.None;
    HealerHouse healHouse;
    CitizenAgent healUnit;
    BuildingInstance healBuilding;
    int healBag;
    float healFoodAcc, healSparkTimer, healCooldown;

    public bool IsHealerBusy => healPhase != HealPhase.None;
    public int HealBag => healBag;

    // ---------- Койка ----------
    HealerHouse bed;             // лазарет, где закреплена койка (идёт к ней или лежит)
    bool bedLying;
    float bedRetry, bedFoodAcc, bedNoFood;

    public bool IsLyingInBed => bed != null && bedLying;

    bool BusyWithErrand => courierActive || haul != null || healPhase != HealPhase.None;

    /// <summary>Бросить все поручения (житель ушёл на обучение, погиб, исчез).</summary>
    void AbortErrands(bool died)
    {
        if (courierActive) EndCourier(died);
        AbortHaul();
        if (healPhase != HealPhase.None) EndHealerRound();
        if (bed != null) LeaveBed();
    }

    // =====================================================================
    // Курьер
    // =====================================================================

    /// <summary>Кнопка "Собрать": житель уносит всё со склада здания в Ратушу.</summary>
    public bool StartCourier(BuildingInstance src)
    {
        return StartCourier(src, null, 100000, 1f);
    }

    /// <summary>Рейс курьера. carry - сколько каждого ресурса берёт, share - какая доля доезжает до Ратуши.</summary>
    public bool StartCourier(BuildingInstance src, CourierHouse owner, int carry, float share)
    {
        if (dead || IsMilitary || IsCartDriver || BusyWithErrand || bed != null) return false;
        if (src == null || src.type == null || src.courierOnTheWay || src.StoredTotal <= 0) return false;

        if (IsGathering) CancelGather();
        courierActive = true;
        courierLoaded = false;
        courierSrc = src;
        courierOwner = owner;
        courierCarry = Mathf.Max(1, carry);
        courierShare = Mathf.Clamp01(share);
        System.Array.Clear(courierLoad, 0, courierLoad.Length);
        src.courierOnTheWay = true;

        BeginErrand();
        return true;
    }

    void BeginErrand()
    {
        errandTimer = 0f;
        mealTrip = false;
        leaveAt = -1f;
        path = null;
        pathTarget = null;
        pathFails = 0;
        wanderHasPoint = false;
    }

    // Внутри чужого здания - выходим. false - выйти пока нельзя (здание переносят).
    bool LeaveIfInsideOther(GridNavigation nav, BuildingInstance goal)
    {
        if (!inside || IsInside(goal)) return true;
        if (insideRef != null && !insideRef.isActiveAndEnabled) return false;
        ExitBuilding(nav);
        return true;
    }

    void CourierTick(GridNavigation nav, float dt)
    {
        errandTimer += dt;

        if (!courierLoaded)
        {
            if (courierSrc == null || courierSrc.IsRuined || errandTimer > ErrandTimeout || pathFails >= 6)
            {
                EndCourier(false);
                return;
            }
            if (!LeaveIfInsideOther(nav, courierSrc)) return;
            if (!IsInside(courierSrc) && !WalkTo(nav, courierSrc)) return;

            courierSrc.TakeStored(courierCarry,
                out courierLoad[0], out courierLoad[1], out courierLoad[2], out courierLoad[3],
                out courierLoad[4], out courierLoad[5], out courierLoad[6], out courierLoad[7], out courierLoad[8], out courierLoad[9]);
            courierSrc.courierOnTheWay = false;
            courierLoaded = true;
            errandTimer = 0f;
            pathFails = 0;
            path = null;

            if (FarStorage.Sum(courierLoad) <= 0) EndCourier(false);   // склад успели опустошить
            return;
        }

        BuildingInstance hall = Hall();
        if (hall == null) { DeliverCourier(); return; }   // Ратуши нет - сразу в казну

        if (!LeaveIfInsideOther(nav, hall)) return;
        if (!Usable(hall))
        {
            // Ратушу бьют / переносят - ждём рядом, но не вечно
            if (errandTimer > ErrandTimeout) DeliverCourier();
            return;
        }

        if (IsInside(hall) || WalkTo(nav, hall)) DeliverCourier();
        else if (pathFails >= 6 || errandTimer > ErrandTimeout) DeliverCourier();   // дороги нет - груз всё равно сдаётся
    }

    void DeliverCourier()
    {
        int carriedTotal = FarStorage.Sum(courierLoad);
        int[] got = new int[courierLoad.Length];
        for (int i = 0; i < got.Length; i++)
            got[i] = courierShare >= 0.999f ? courierLoad[i] : Mathf.FloorToInt(courierLoad[i] * courierShare);
        int delivered = FarStorage.Sum(got);

        var rm = ResourceManager.Instance;
        if (rm != null) { rm.Add(got[0], got[1], got[2], got[3], got[4], got[5], got[6], got[7], 0, got[8]); rm.AddLeaves(got[9]); }
        if (courierOwner != null) courierOwner.OnTripDone(carriedTotal, delivered);

        System.Array.Clear(courierLoad, 0, courierLoad.Length);
        courierLoaded = false;
        EndCourier(true);
    }

    // Рейс окончен или сорван. Несённое не пропадает (кроме гибели) - уходит в казну.
    void EndCourier(bool died)
    {
        if (!courierActive) return;
        if (!courierLoaded && courierSrc != null) courierSrc.courierOnTheWay = false;

        if (courierLoaded && !died && FarStorage.Sum(courierLoad) > 0)
        {
            var rm = ResourceManager.Instance;
            if (rm != null) { rm.Add(courierLoad[0], courierLoad[1], courierLoad[2], courierLoad[3], courierLoad[4], courierLoad[5], courierLoad[6], courierLoad[7], 0, courierLoad[8]); rm.AddLeaves(courierLoad[9]); }
        }

        System.Array.Clear(courierLoad, 0, courierLoad.Length);
        courierActive = false;
        courierLoaded = false;
        courierSrc = null;
        courierOwner = null;
        path = null;
        pathTarget = null;
        thinkTimer = 0f;
    }

    string CourierLoadText()
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < courierLoad.Length; i++)
            if (courierLoad[i] > 0) parts.Add(courierLoad[i] + " " + LoadNames[i]);
        return parts.Count > 0 ? "в Ратушу: " + string.Join(", ", parts) : "ничего";
    }

    // =====================================================================
    // Перенос груза (Logistics)
    // =====================================================================

    public bool StartHaul(HaulJob job)
    {
        if (dead || IsMilitary || IsCartDriver || BusyWithErrand || bed != null) return false;
        if (job == null || job.from == null || job.to == null) return false;

        if (IsGathering) CancelGather();
        haul = job;
        haulLoaded = false;
        BeginErrand();
        return true;
    }

    void HaulTick(GridNavigation nav, float dt)
    {
        errandTimer += dt;
        HaulJob j = haul;
        BuildingInstance goal = haulLoaded ? j.to : j.from;

        if (goal == null || goal.IsRuined || errandTimer > ErrandTimeout || pathFails >= 6)
        {
            AbortHaul();
            return;
        }

        if (!LeaveIfInsideOther(nav, goal)) return;
        if (!Usable(goal)) return;   // здание бьют или переносят - ждём

        if (!IsInside(goal) && !WalkTo(nav, goal)) return;

        if (!haulLoaded)
        {
            haulLoaded = true;
            errandTimer = 0f;
            pathFails = 0;
            path = null;
            if (j.onPickup != null) j.onPickup();
            return;
        }

        haul = null;
        haulLoaded = false;
        thinkTimer = 0f;
        if (j.onDeliver != null) j.onDeliver();
    }

    void AbortHaul()
    {
        if (haul == null) return;
        HaulJob j = haul;
        bool loaded = haulLoaded;
        haul = null;
        haulLoaded = false;
        path = null;
        pathTarget = null;
        thinkTimer = 0f;
        if (j.onAbort != null) j.onAbort(loaded);
    }

    // =====================================================================
    // Целитель
    // =====================================================================

    HealerHouse MyHealerHouse()
    {
        if (!Usable(workplace)) return null;
        return workplace.GetComponent<HealerHouse>();
    }

    bool TryStartHealerRound()
    {
        if (Time.time < healCooldown) return false;
        healCooldown = Time.time + 1.5f;

        HealerHouse h = MyHealerHouse();
        if (h == null || !h.Works || !IsInside(h.Building)) return false;   // выходят только со своей работы

        GameClock clock = GameClock.Instance;
        bool workTime = clock == null || clock.IsWorkTime;
        if (!workTime && !IsSkeleton) return false;
        if (HpFraction < mealAt + 0.1f) return false;   // сначала поесть самому

        var rm = ResourceManager.Instance;
        if (rm == null || rm.food <= 0) return false;
        if (!h.FindPatient(this, out CitizenAgent u, out BuildingInstance b)) return false;

        healHouse = h;
        if (!SetPatient(u, b)) { healHouse = null; healPhase = HealPhase.None; return false; }

        // Еда в сумку
        int take = Mathf.Min(h.BagCapacity, rm.food);
        rm.Spend(0, 0, 0, take);
        healBag = take;
        healFoodAcc = 0f;
        BeginErrand();
        return true;
    }

    bool SetPatient(CitizenAgent u, BuildingInstance b)
    {
        ReleasePatient();
        if (u != null && HealerHouse.ClaimUnit(u))
        {
            healUnit = u;
            healPhase = HealPhase.ToUnit;
            mover.Clear();
            mover.ResetFails();
            return true;
        }
        if (b != null && HealerHouse.ClaimBuilding(b))
        {
            healBuilding = b;
            healPhase = HealPhase.ToBuilding;
            path = null;
            pathFails = 0;
            return true;
        }
        return false;
    }

    void ReleasePatient()
    {
        if (healUnit != null) HealerHouse.ReleaseUnit(healUnit);
        if (healBuilding != null) HealerHouse.ReleaseBuilding(healBuilding);
        healUnit = null;
        healBuilding = null;
    }

    bool ValidPatient(CitizenAgent a, HealerHouse h)
    {
        if (a == null || a.dead || a.inside || a.IsLyingInBed) return false;
        if (a.HpFraction >= h.healUpTo) return false;
        return Vector2.Distance(a.transform.position, h.Building.Center) <= h.ServiceRadius + 4f;
    }

    void HealerTick(GridNavigation nav, float dt)
    {
        HealerHouse h = healHouse;
        errandTimer += dt;

        if (healPhase != HealPhase.Return && (h == null || !h.Works || healBag <= 0 || errandTimer > 300f))
        {
            ReleasePatient();
            healPhase = HealPhase.Return;
            path = null;
        }

        switch (healPhase)
        {
            case HealPhase.ToUnit:
            {
                if (!ValidPatient(healUnit, h)) { NextPatient(h); break; }
                if (!LeaveIfInsideOther(nav, null)) break;

                Vector2 tp = healUnit.transform.position;
                if (Vector2.Distance(transform.position, tp) > HealerHouse.HealRange)
                {
                    mover.Step(transform, nav, tp, moveSpeed, HealerHouse.HealRange * 0.7f);
                    if (mover.Fails >= 4) NextPatient(h);
                    break;
                }
                mover.Clear();
                HealOne(h, healUnit, dt, true);
                if (healUnit.HpFraction >= h.healUpTo)
                {
                    h.CountHealed();
                    NextPatient(h);
                }
                break;
            }

            case HealPhase.ToBuilding:
            {
                if (!Usable(healBuilding)) { NextPatient(h); break; }
                if (!LeaveIfInsideOther(nav, healBuilding)) break;
                if (IsInside(healBuilding) || WalkTo(nav, healBuilding))
                {
                    if (!inside) EnterBuilding(healBuilding);
                    healPhase = HealPhase.Feeding;
                }
                else if (pathFails >= 4) NextPatient(h);
                break;
            }

            case HealPhase.Feeding:
            {
                if (!IsInside(healBuilding)) { NextPatient(h); break; }
                bool any = false;
                foreach (var a in All)
                {
                    if (a == null || a == this || a.dead || !a.IsInside(healBuilding)) continue;
                    if (a.HpFraction >= h.healUpTo) continue;
                    HealOne(h, a, dt, false);
                    any = true;
                    if (healBag <= 0) break;
                }
                if (!any) NextPatient(h);
                break;
            }

            case HealPhase.Return:
            {
                ReleasePatient();
                if (h == null || !Usable(h.Building)) { EndHealerRound(); break; }
                if (!LeaveIfInsideOther(nav, h.Building)) break;
                if (IsInside(h.Building) || WalkTo(nav, h.Building))
                {
                    if (!inside) EnterBuilding(h.Building);
                    EndHealerRound();
                }
                else if (pathFails >= 5) EndHealerRound();
                break;
            }
        }
    }

    void HealOne(HealerHouse h, CitizenAgent a, float dt, bool showFx)
    {
        float got = a.ReceiveHeal(h.HealPerSecond * dt);
        healFoodAcc += got / Mathf.Max(0.1f, h.HpPerFood);
        while (healFoodAcc >= 1f && healBag > 0)
        {
            healBag--;
            healFoodAcc -= 1f;
            h.CountFood(1);
        }

        if (!showFx) return;
        healSparkTimer -= dt;
        if (healSparkTimer <= 0f)
        {
            healSparkTimer = 0.3f;
            CombatFx.HealSpark(transform.position, a.transform.position);
        }
    }

    void NextPatient(HealerHouse h)
    {
        ReleasePatient();
        GameClock clock = GameClock.Instance;
        bool workTime = clock == null || clock.IsWorkTime || IsSkeleton;
        if (healBag > 0 && h != null && h.Works && workTime
            && h.FindPatient(this, out CitizenAgent u, out BuildingInstance b) && SetPatient(u, b))
        {
            errandTimer = 0f;
            return;
        }
        healPhase = HealPhase.Return;
        path = null;
        pathFails = 0;
    }

    // Обход окончен: остаток еды из сумки - обратно в казну
    void EndHealerRound()
    {
        ReleasePatient();
        if (healBag > 0 && ResourceManager.Instance != null) ResourceManager.Instance.Add(0, 0, 0, healBag);
        healBag = 0;
        healFoodAcc = 0f;
        healPhase = HealPhase.None;
        healHouse = null;
        healCooldown = Time.time + 3f;
        thinkTimer = 0f;
    }

    string HealerActivityText
    {
        get
        {
            switch (healPhase)
            {
                case HealPhase.ToUnit:
                    if (healUnit == null) return "ищет, кого лечить";
                    bool near = Vector2.Distance(transform.position, healUnit.transform.position) <= HealerHouse.HealRange;
                    return near ? $"<color=#60FFD0>лечит: {healUnit.CitizenName}</color>" : $"идёт лечить: {healUnit.CitizenName}";
                case HealPhase.ToBuilding: return "идёт подкормить рабочих: " + NameOf(healBuilding);
                case HealPhase.Feeding: return "<color=#60FFD0>кормит рабочих: " + NameOf(healBuilding) + "</color>";
                case HealPhase.Return: return "возвращается в Дом целителей";
                default: return "";
            }
        }
    }

    // =====================================================================
    // Койка в лазарете
    // =====================================================================

    // Ищем свободную койку, если здоровья мало. true - койка закреплена.
    bool TryTakeBed()
    {
        if (bed != null) return true;
        if (Time.time < bedRetry || HpFraction >= BedBelow || HealerHouse.All.Count == 0) return false;
        bedRetry = Time.time + 5f;

        var rm = ResourceManager.Instance;
        if (rm == null || rm.food <= 0) return false;
        if (WarManager.IsWar && EnemyAgent.AnyWithin(transform.position, SafeRadius)) return false;

        HealerHouse h = HealerHouse.ReserveBed(this);
        if (h == null) return false;

        bed = h;
        bedLying = false;
        bedFoodAcc = 0f;
        bedNoFood = 0f;
        BeginErrand();
        return true;
    }

    void BedTick(GridNavigation nav, float dt)
    {
        HealerHouse h = bed;
        if (h == null || !h.Works || !h.Holds(this)) { LeaveBed(); return; }

        if (!bedLying)
        {
            errandTimer += dt;
            if (errandTimer > 45f || pathFails >= 5) { LeaveBed(); return; }
            if (!LeaveIfInsideOther(nav, h.Building)) return;
            if (IsInside(h.Building) || WalkTo(nav, h.Building))
            {
                if (!inside) EnterBuilding(h.Building);
                bedLying = true;
                h.MarkArrived(this);
            }
            return;
        }

        if (!IsInside(h.Building)) { LeaveBed(); return; }

        var rm = ResourceManager.Instance;
        if (rm == null || rm.food <= 0)
        {
            bedNoFood += dt;
            if (bedNoFood > 15f) LeaveBed();   // кормить нечем - койку освобождаем
            return;
        }
        bedNoFood = 0f;

        float got = ReceiveHeal(h.BedHpPerSecond * dt);
        bedFoodAcc += got / HealerHouse.BedHpPerFood;
        int n = Mathf.FloorToInt(bedFoodAcc);
        if (n > 0)
        {
            int pay = Mathf.Min(n, rm.food);
            rm.Spend(0, 0, 0, pay);
            h.CountFood(pay);
            bedFoodAcc -= n;
        }

        if (HpFraction >= h.healUpTo) LeaveBed();
    }

    void LeaveBed()
    {
        if (bed != null) bed.Release(this);
        bed = null;
        bedLying = false;
        bedFoodAcc = 0f;
        bedRetry = Time.time + 5f;
        thinkTimer = 0f;
    }

    /// <summary>Лазарет сам снимает бронь (пациент не дошёл, здание снесли).</summary>
    public void DropBed(HealerHouse h)
    {
        if (bed == null || bed != h) return;
        bed = null;
        bedLying = false;
        bedFoodAcc = 0f;
        bedRetry = Time.time + 5f;
    }

    // =====================================================================
    // Работа
    // =====================================================================

    /// <summary>Назначить место работы (PopulationManager). null - уволен.</summary>
    public void AssignWork(BuildingInstance b)
    {
        if (ReferenceEquals(workplace, b)) return;
        if (IsGathering) CancelGather();
        if (healPhase != HealPhase.None) EndHealerRound();
        workplace = b;
        if (b == null) IsSkeleton = false;
        thinkTimer = 0f;
    }
}
