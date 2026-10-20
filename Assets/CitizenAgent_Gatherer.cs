using UnityEngine;

/// <summary>
/// Собиратель ягод - рабочий Станции собирателей (продолжение класса CitizenAgent).
/// В рабочее время собиратель сидит на станции и оттуда выходит в поход:
///  1. Выбирает ближайший спелый свободный куст в радиусе станции (BerryField.FindBush) и занимает его.
///  2. Идёт к кусту, собирает ягоды (время зависит от уровня станции и самочувствия).
///  3. Сумка заполнена меньше чем наполовину и рядом есть ещё куст - идёт к нему, иначе возвращается.
///  4. У станции сдаёт ягоды на её склад, отдыхает пару секунд и снова идёт.
/// Война - поход прерывается, собиратель ведёт себя как обычный житель (прячется). Ягоды остаются в сумке
/// и сдаются, когда он зайдёт на станцию (или в Ратушу). Кончилась смена - возвращается на станцию.
/// </summary>
public partial class CitizenAgent
{
    enum GatherPhase { None, ToBush, Picking, ToStation }

    static readonly Color GathererColor = new Color(0.85f, 0.5f, 0.8f);
    static readonly Color GathererLoadedColor = new Color(0.95f, 0.3f, 0.55f);

    private GatherPhase gatherPhase = GatherPhase.None;
    private BerryBush gatherBush;
    private BerryBush gatherIgnore;
    private float gatherIgnoreUntil;
    private int gatherBag;
    private float gatherTimer;
    private float gatherRestUntil;
    private float gatherDoorTimer;
    private Vector2 gatherDoorPoint;
    private readonly GridMover gatherMover = new GridMover();

    // Поход за травами (путь Магии, станция Ур.2+): идут в лес, а не к кусту
    private bool herbTrip;
    private Vector2 herbSpot;
    private int herbBag;
    static readonly Color HerbalistColor = new Color(0.45f, 0.85f, 0.4f);

    private BuildingInstance bsCachedFor;
    private BerryStation bsCached;

    public bool IsGathering => gatherPhase != GatherPhase.None;
    public int BerryBag => gatherBag;

    BerryStation MyBerryStation()
    {
        if (IsMilitary || !Usable(Workplace)) return null;
        if (!ReferenceEquals(bsCachedFor, Workplace)) { bsCachedFor = Workplace; bsCached = Workplace.GetComponent<BerryStation>(); }
        return bsCached;
    }

    // За ягодами ходят только в рабочее время (ночью и в выходные дежурный просто сидит на станции)
    static bool GatherTime()
    {
        GameClock clock = GameClock.Instance;
        return clock == null || clock.IsWorkTime;
    }

    // true - собиратель занят походом (обычный распорядок на этот кадр пропускаем)
    bool GatherUpdate(GridNavigation nav)
    {
        BerryStation st = MyBerryStation();

        // --- Ещё не в походе: выходим только со станции ---
        if (gatherPhase == GatherPhase.None)
        {
            if (st == null || !inside || insideRef != st.Building) return false;
            if (Time.time < gatherRestUntil || !st.CanSendOut || !GatherTime()) return false;
            if (HpFraction < mealAt + 0.05f || bed != null) return false;   // сначала поесть / подлечиться
            if (insideRef != null && !insideRef.isActiveAndEnabled) return false;

            if (!StartGatherTrip(st)) { gatherRestUntil = Time.time + 4f; return false; }
            ExitBuilding(nav);
            return true;
        }

        // --- Поход прерван: станцию снесли / сменили работу / война ---
        if (st == null || !st.Works || WarManager.IsWar) { StopGather(); return false; }

        // Смена кончилась - несём, что успели собрать
        if (gatherPhase != GatherPhase.ToStation && !GatherTime()) BeginGatherReturn();

        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return true;
            ExitBuilding(nav);
        }
        path = null;
        pathTarget = null;

        if (sr != null)
        {
            Color want = herbTrip ? HerbalistColor : (gatherBag > 0 ? GathererLoadedColor : GathererColor);
            if (sr.color != want) sr.color = want;
        }

        switch (gatherPhase)
        {
            case GatherPhase.ToBush:
                if (herbTrip)
                {
                    if (gatherMover.Step(transform, nav, herbSpot, moveSpeed, 0.3f))
                    {
                        gatherMover.Clear();
                        gatherPhase = GatherPhase.Picking;
                        gatherTimer = st.PickSeconds * 1.4f / Mathf.Max(0.4f, WorkFactor);
                    }
                    else if (gatherMover.Fails >= 4) BeginGatherReturn();
                    break;
                }
                if (!GatherBushOk(st)) { NextBushOrReturn(st); break; }
                if (gatherMover.Step(transform, nav, gatherBush.Position, moveSpeed, 0.45f))
                {
                    gatherMover.Clear();
                    gatherPhase = GatherPhase.Picking;
                    gatherTimer = st.PickSeconds / Mathf.Max(0.4f, WorkFactor);   // измотанный собирает дольше
                }
                else if (gatherMover.Fails >= 4)
                {
                    // До куста не дойти (обнесён стеной) - какое-то время к нему не ходим
                    gatherIgnore = gatherBush;
                    gatherIgnoreUntil = Time.time + 30f;
                    NextBushOrReturn(st);
                }
                break;

            case GatherPhase.Picking:
                if (herbTrip)
                {
                    gatherTimer -= Time.deltaTime;
                    if (gatherTimer > 0f) break;
                    herbBag += st.HerbCarry;
                    CombatFx.LootFly(herbSpot, transform.position);
                    BeginGatherReturn();
                    break;
                }
                if (!GatherBushOk(st)) { NextBushOrReturn(st); break; }
                gatherTimer -= Time.deltaTime;
                if (gatherTimer > 0f) break;

                int got = gatherBush.Pick(st.Carry - gatherBag);
                if (got > 0) CombatFx.LootFly(gatherBush.Position, transform.position);
                gatherBag += got;
                NextBushOrReturn(st);
                break;

            case GatherPhase.ToStation:
                if (StepToGatherDoor(nav, st.Building))
                {
                    st.Deposit(gatherBag);
                    gatherBag = 0;
                    st.DepositHerbs(herbBag);
                    herbBag = 0;
                    StopGather();
                    EnterBuilding(st.Building);
                    gatherRestUntil = Time.time + Random.Range(2f, 5f);
                }
                else if (gatherMover.Fails >= 4)
                {
                    StopGather();   // дороги нет - пусть идёт обычным распорядком (ягоды сдаст, когда зайдёт)
                }
                break;
        }
        return true;
    }

    bool StartGatherTrip(BerryStation st)
    {
        // Путь Магии: преимущественно в лес за травами (а если ягодный склад полон - только за травами)
        herbTrip = false;
        if (st.HerbsWanted && (st.IsFull || Random.value < BerryStation.HerbTripShare)
            && st.TryFindForestSpot(GridNavigation.Instance, out herbSpot))
        {
            herbTrip = true;
            gatherPhase = GatherPhase.ToBush;
            gatherMover.Clear();
            gatherMover.ResetFails();
            mealTrip = false;
            return true;
        }
        if (st.IsFull) return false;

        BerryBush skip = Time.time < gatherIgnoreUntil ? gatherIgnore : null;
        BerryBush bush = BerryField.FindBush(st.Building.Center, st.Radius, transform.position, st.Carry, skip);
        if (bush == null || !bush.Claim(this)) return false;

        gatherBush = bush;
        gatherPhase = GatherPhase.ToBush;
        gatherMover.Clear();
        gatherMover.ResetFails();
        mealTrip = false;
        return true;
    }

    // Куст всё ещё наш и в пределах станции (его могли пересадить или отдать другому)
    bool GatherBushOk(BerryStation st)
    {
        return gatherBush != null && gatherBush.ClaimedBy == this
            && Vector2.Distance(gatherBush.Position, st.Building.Center) <= st.Radius + 3f;
    }

    // Сумка заполнена меньше чем на 60% - пробуем соседний куст, иначе домой
    void NextBushOrReturn(BerryStation st)
    {
        ReleaseGatherBush();
        gatherMover.Clear();
        gatherMover.ResetFails();

        if (gatherBag < st.Carry * 0.6f && GatherTime())
        {
            BerryBush skip = Time.time < gatherIgnoreUntil ? gatherIgnore : null;
            BerryBush next = BerryField.FindBush(st.Building.Center, st.Radius, transform.position, st.Carry - gatherBag, skip);
            if (next != null && Vector2.Distance(next.Position, transform.position) <= 8f && next.Claim(this))
            {
                gatherBush = next;
                gatherPhase = GatherPhase.ToBush;
                return;
            }
        }
        BeginGatherReturn();
    }

    void BeginGatherReturn()
    {
        ReleaseGatherBush();
        gatherPhase = GatherPhase.ToStation;
        gatherDoorTimer = 0f;
        gatherMover.Clear();
        gatherMover.ResetFails();
    }

    // Идём к двери станции. true - пришли.
    bool StepToGatherDoor(GridNavigation nav, BuildingInstance b)
    {
        gatherDoorTimer -= Time.deltaTime;
        if (gatherDoorTimer <= 0f)
        {
            gatherDoorTimer = 2f;
            if (nav.TryGetDoorCell(b, nav.WorldToCell(transform.position), out Vector2Int door))
                gatherDoorPoint = nav.CellCenter(door);
            else { StopGather(); return false; }
        }
        return gatherMover.Step(transform, nav, gatherDoorPoint, moveSpeed, 0.2f);
    }

    void ReleaseGatherBush()
    {
        if (gatherBush != null) gatherBush.Release(this);
        gatherBush = null;
    }

    // Поход закончен или прерван. Ягоды остаются в сумке - их сдадут при входе на станцию / в Ратушу.
    void StopGather()
    {
        bool was = gatherPhase != GatherPhase.None;
        ReleaseGatherBush();
        gatherPhase = GatherPhase.None;
        herbTrip = false;
        gatherMover.Clear();
        gatherMover.ResetFails();
        if (was)
        {
            thinkTimer = 0f;
            retryTimer = 0f;
            if (sr != null) sr.color = currentTint;
        }
    }

    /// <summary>Жителя забрали (в казарму и т.п.): поход отменяется, собранное засчитывается станции.</summary>
    public void CancelGather()
    {
        BerryStation st = MyBerryStation();
        if (gatherBag > 0)
        {
            if (st != null) st.Deposit(gatherBag);
            gatherBag = 0;
        }
        if (herbBag > 0)
        {
            if (st != null) st.DepositHerbs(herbBag);
            herbBag = 0;
        }
        StopGather();
    }

    // Зашёл в здание с ягодами в сумке: на станции - на её склад, в Ратуше - сразу в казну
    void GatherDepositOnEnter(BuildingInstance b)
    {
        if (b == null) return;
        if (herbBag > 0)
        {
            BerryStation hs = b.GetComponent<BerryStation>();
            if (hs != null && hs.Works) { hs.DepositHerbs(herbBag); herbBag = 0; }
        }
        if (gatherBag <= 0) return;
        BerryStation st = b.GetComponent<BerryStation>();
        if (st != null && st.Works)
        {
            st.Deposit(gatherBag);
            gatherBag = 0;
        }
        else if (b.IsTownHall && ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(0, 0, 0, 0, 0, 0, gatherBag);
            gatherBag = 0;
        }
    }

    // Для карточки жителя
    string GatherActivityText
    {
        get
        {
            switch (gatherPhase)
            {
                case GatherPhase.ToBush: return herbTrip ? "<color=#90E060>идёт в лес за травами</color>" : "<color=#F0A0D0>идёт к ягодному кусту</color>";
                case GatherPhase.Picking: return herbTrip ? "<color=#90E060>собирает травы</color>" : "<color=#F0A0D0>собирает ягоды</color>";
                case GatherPhase.ToStation:
                    if (herbBag > 0) return "<color=#90E060>несёт травы на станцию</color>";
                    return gatherBag > 0 ? "<color=#F0A0D0>несёт ягоды на станцию</color>" : "возвращается на станцию";
                default: return "";
            }
        }
    }
}