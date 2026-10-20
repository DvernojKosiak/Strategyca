using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Житель - физический человечек на карте. Это же и солдат, и новобранец (см. CitizenAgent_Soldier.cs).
/// Класс разбит на части (partial):
///  - CitizenAgent.cs          - ядро: распорядок дня, голод и еда, вход/выход из зданий, ходьба, укрытие в войну;
///  - CitizenAgent_Soldier.cs  - новобранец и солдат: приказы, бой, снаряжение, конь, добыча, гонец за едой;
///  - CitizenAgent_Errands.cs  - поручения: курьер, перенос груза, обход целителя, койка в лазарете;
///  - CitizenAgent_Gatherer.cs - собиратель ягод;
///  - CitizenAgent_Cart.cs     - возчик обоза (Обозный двор);
///  - CitizenAgent_Departure.cs - боец, проданный на рынке, уходит из города пешком;
///  - CitizenAgent_WarBuilder.cs - строитель Двора военного строителя: чинит здания и укрепления.
///
/// Реки (Rivers): мирный житель через воду не ходит - только по мосту. Вброд (медленно) идут солдаты,
/// житель, убегающий от врагов, и житель, отрезанный рекой и от Ратуши, и от своего дома (например,
/// убежал от набега на тот берег, а моста нет) - иначе он застрял бы навсегда.
///
/// Распорядок мирного жителя:
///  - рабочее время (будни, день) - на работе; ночью и в выходные на работе остаются только дежурные;
///  - ночь - дома (у кого нет дома - ночует в Ратуше);
///  - свободное время днём - то сидит дома, то гуляет рядом;
///  - голод: здоровье понемногу тает. Ниже mealAt житель идёт поесть домой или в Ратушу (еда из казны,
///    1 еда = 1 здоровья). От голода не умирают - здоровье не падает ниже HungerFloor;
///  - раненый / истощённый ложится на койку в лазарете (Дом целителей Ур.2+), если есть свободная.
/// Война: жители идут в укрытие (на работу или домой) и сидят там. Враг рядом - бегут в ближайшее здание.
/// Здание рушится (прочности мало, его бьют) - все выбегают.
/// Создаёт и удаляет жителей PopulationManager.
/// </summary>
public partial class CitizenAgent : MonoBehaviour
{
    public static readonly List<CitizenAgent> All = new List<CitizenAgent>();

    [Header("Движение")]
    public float moveSpeed = CitizenSpeed;   // меняют: Курьерская (упряжка), казарма (вид войск)

    public const float CitizenSpeed = 1.8f;
    public const float CitizenMaxHp = 100f;
    const float CitizenScale = 0.5f;

    // ---------- Голод и еда (числа можно менять) ----------
    const float HungerPerSecond = 0.06f;     // сколько здоровья в секунду съедает голод (100 - примерно за 28 мин)
    const float HungerFloor = 0.15f;         // ниже этой доли здоровья голод не опускает
    const float EatHpPerSecond = 6f;         // как быстро житель наедается дома / в Ратуше
    const float HomeHpPerFood = 1f;          // 1 еда = 1 здоровья (у целителей выгоднее)
    float mealAt = 0.5f;                     // ниже этой доли здоровья идёт поесть

    // ---------- Война ----------
    const float PanicRadius = 6.5f;          // враг ближе - житель бежит в ближайшее здание
    const float SafeRadius = 8f;             // пока враг ближе к зданию - из него не выходят

    static readonly Color CivilianColor = new Color(0.95f, 0.88f, 0.7f);
    static readonly Color RecruitColor = new Color(1f, 0.62f, 0.55f);
    static readonly Color CarryColor = new Color(1f, 0.85f, 0.3f);
    static readonly Color HealerColor = new Color(0.45f, 0.95f, 0.8f);

    // ---------- Состояние ----------
    string citizenName = "";
    bool dead;
    bool cleaned;
    float hp = CitizenMaxHp;
    float maxHp = CitizenMaxHp;
    float bodyScale = CitizenScale;

    SpriteRenderer sr;                       // тело (его включают/выключают и части CitizenAgent_*.cs)
    SpriteRenderer horseSr, starSr, sackSr;  // конь, звёздочка похлёбки, мешок с добычей
    Color currentTint = CivilianColor;       // обычный цвет (к нему возвращаются после похода / обоза)

    BuildingInstance workplace;
    bool inside;
    BuildingInstance insideRef;
    Vector3 insideAnchor;                    // где стояло здание, когда вошли (его могут перенести)

    // Распорядок
    bool mealTrip;                           // идёт поесть
    float leaveAt = -1f;                     // когда выйти из дома (свободное время)
    float thinkTimer, retryTimer;
    float eatAcc;
    BuildingInstance routineDest;
    BuildingInstance unreachable;            // до этого здания не дойти - какое-то время не пытаемся
    float unreachableUntil;

    // Ходьба к зданию (A* по клеткам до двери)
    List<Vector2Int> path;
    int pathIndex;
    BuildingInstance pathTarget;
    Vector2Int pathDoor;
    int pathFails;
    readonly GridMover mover = new GridMover();   // ходьба к точке и погоня

    // Прогулка
    bool leisureHome;
    float wanderUntil, wanderWait;
    Vector2 wanderPoint;
    bool wanderHasPoint;

    // Паника (враг рядом)
    float panicUntil;
    BuildingInstance panicShelter;
    float shelterRetry;

    // ---------- Свойства ----------

    public string CitizenName => citizenName;
    public bool IsDead => dead;
    public float Hp => hp;
    public float MaxHp => maxHp;
    public float HpFraction => maxHp > 0f ? Mathf.Clamp01(hp / maxHp) : 0f;

    public BuildingInstance Workplace => workplace;
    public BuildingInstance Home { get; set; }
    public bool IsSkeleton { get; set; }     // дежурный: остаётся на работе ночью и в выходные (задаёт PopulationManager)
    public bool IsMercenary { get; set; }    // наёмник с рынка: живёт в Гостевом доме (задаёт PopulationManager)

    public bool IsMilitary => IsSoldier || trainingAt != null;
    public bool IsInsideBuilding => inside || IsAway;   // в походе - тоже "не на карте"
    public BuildingInstance InsideBuilding => inside ? insideRef : null;
    public bool IsInside(BuildingInstance b) { return inside && b != null && ReferenceEquals(insideRef, b); }
    public bool IsPanicking => !dead && !inside && Time.time < panicUntil;

    // Голодные и раненые работают хуже (среднее по бригаде - BuildingInstance.HealthFactor)
    public float WorkFactor
    {
        get
        {
            if (IsSoldier) return 1f;
            return Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(HungerFloor, 0.6f, HpFraction));
        }
    }

    // ---------- Общие проверки ----------

    /// <summary>В здание можно зайти: стоит, не руины и не рушится прямо сейчас.</summary>
    public static bool Usable(BuildingInstance b)
    {
        return b != null && b.type != null && b.isActiveAndEnabled && !b.IsRuined && !b.IsFalling;
    }

    /// <summary>Враг может бить этого человека: жив и стоит на улице. Возчиков обоза охраняют - их не трогают.</summary>
    public static bool IsTargetable(CitizenAgent c)
    {
        return c != null && !c.dead && !c.inside && c.isActiveAndEnabled && !c.IsCartDriver && !c.IsAway;
    }

    /// <summary>Житель под курсором (для карточки).</summary>
    public static CitizenAgent PickAt(Vector2 p, float radius = 0.35f)
    {
        CitizenAgent best = null;
        float bestD = float.MaxValue;
        foreach (var a in All)
        {
            if (a == null || a.dead || a.inside || a.IsAway) continue;
            float d = Vector2.Distance(p, a.transform.position);
            float r = radius + a.bodyScale * 0.3f;
            if (d <= r && d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    // Жильё: обычный дом или Гостевой дом (для наёмников)
    static bool IsHouse(BuildingInstance b)
    {
        return PopulationManager.IsHousing(b);
    }

    // Укрытие: обычное здание с крышей (не дорожка, не стена, не поле фермы)
    static bool IsShelter(BuildingInstance b)
    {
        return Usable(b) && !b.IsRoad && !b.IsBarrier && !b.IsWalkable;
    }

    // Где едят сами: дома и в Ратуше
    static bool CanEatIn(BuildingInstance b)
    {
        return b != null && (b.IsTownHall || IsHouse(b));
    }

    static BuildingInstance Hall()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    bool Reachable(BuildingInstance b)
    {
        return Usable(b) && !(ReferenceEquals(b, unreachable) && Time.time < unreachableUntil);
    }

    // ---------- Жизненный цикл ----------

    void Awake()
    {
        citizenName = CitizenNames.Next();

        sr = MakePart("Body", Vector2.zero, Vector2.one, CivilianColor, 10);
        horseSr = MakePart("Horse", Vector2.zero, Vector2.one, new Color(0.45f, 0.29f, 0.14f), 9);
        starSr = MakePart("Star", Vector2.zero, Vector2.one, new Color(1f, 0.85f, 0.2f), 12);
        sackSr = MakePart("Sack", Vector2.zero, Vector2.one, new Color(0.9f, 0.7f, 0.2f), 12);
        horseSr.enabled = starSr.enabled = sackSr.enabled = false;
        SetBodyScale(CitizenScale);

        thinkTimer = Random.Range(0f, 0.5f);
        wanderUntil = Time.time + Random.Range(5f, 25f);
    }

    SpriteRenderer MakePart(string n, Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = CombatFx.Square();
        r.color = c;
        r.sortingOrder = order;
        return r;
    }

    void SetBodyScale(float s)
    {
        bodyScale = Mathf.Max(0.2f, s);
        if (sr != null) sr.transform.localScale = new Vector3(bodyScale, bodyScale, 1f);
        if (horseSr != null)
        {
            horseSr.transform.localPosition = new Vector3(0f, -bodyScale * 0.42f, 0f);
            horseSr.transform.localScale = new Vector3(bodyScale * 1.5f, bodyScale * 0.45f, 1f);
        }
        if (starSr != null)
        {
            starSr.transform.localPosition = new Vector3(-bodyScale * 0.45f, bodyScale * 0.45f, 0f);
            starSr.transform.localScale = new Vector3(0.14f, 0.14f, 1f);
        }
        if (sackSr != null)
        {
            sackSr.transform.localPosition = new Vector3(bodyScale * 0.45f, bodyScale * 0.45f, 0f);
            sackSr.transform.localScale = new Vector3(0.2f, 0.2f, 1f);
        }
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (!gameObject.scene.isLoaded) return;   // игра закрывается - ничего не возвращаем
        Cleanup(false);
    }

    /// <summary>Житель исчезает (погиб или ушёл из города): отпускаем всё, что он держал.</summary>
    void Cleanup(bool died)
    {
        if (cleaned) return;
        cleaned = true;

        StopGather();
        StopWarBuild();
        CancelCartDuty();
        if (died) supplyFood = 0;            // еда гонца пропадает вместе с ним
        AbortErrands(died);
        if (IsSupplying)
        {
            Squad sq = supplySquad;
            CancelSupply();
            if (sq != null) sq.OnSupplyFailed();
        }
        if (Squad != null)
        {
            Squad sq = Squad;
            sq.Remove(this);
            Squad = null;
        }
        HealerHouse.ReleaseUnit(this);

        // Солдат ушёл из города живым (уменьшилось население) - оружие и конь возвращаются
        if (!died && IsSoldier)
        {
            if (weapon != null && !ArmoryController.Stash(weapon))
                WarManager.Notify("В Оружейных нет места: оружие ушедшего бойца потеряно", false);
            weapon = null;
            if (hasHorse && !StableController.ReturnHorse())
                WarManager.Notify("В Конюшнях нет места: лошадь ушедшего бойца потеряна", false);
            hasHorse = false;
            CannonReturnGear();
            ChaosReturnCore();
            if (HasLoot) carried.DepositToTown();
        }
    }

    void Die(EnemyAgent by)
    {
        if (dead) return;
        dead = true;
        hp = 0f;
        Vector2 at = transform.position;

        // Добычу солдата забирает убийца
        if (HasLoot)
        {
            if (EnemyAgent.Alive(by)) by.ReceiveLoot(carried, at);
            else carried.Clear();
        }

        string who;
        if (IsSoldier)
        {
            who = DisplayName;
            if (weapon != null) WarManager.Notify($"Погиб {unit.unitName}: вместе с ним потеряно оружие — {weapon.Name}", false);
            if (hasHorse) WarManager.Notify($"Погиб {unit.unitName}: конь погиб вместе со всадником", false);
            CannonLoseGear();
            ChaosLoseCore();
            weapon = null;
            hasHorse = false;
        }
        else who = trainingAt != null ? "новобранец" : "житель";
        WarManager.Notify($"Погиб {who}: {citizenName}", false);

        if (!IsAway) CombatFx.Death(at);   // погиб в набеге - следа дома нет
        Cleanup(true);

        var pm = PopulationManager.Instance;
        if (pm != null) pm.OnCitizenDied(this);
        else Destroy(gameObject);
    }

    // ---------- Здоровье ----------

    /// <summary>Подлечить. Возвращает, сколько здоровья реально прибавилось.</summary>
    public float ReceiveHeal(float amount)
    {
        if (dead || amount <= 0f) return 0f;
        float before = hp;
        hp = Mathf.Min(maxHp, hp + amount);
        return hp - before;
    }

    /// <summary>Удар врага (щит уже учтён в ShieldBearer.Mitigate). Баррикада лагеря режет урон.</summary>
    public void TakeDamage(float dmg, EnemyAgent by = null)
    {
        if (dead || dmg <= 0f) return;
        if (Squad != null && Squad.CoverAt(transform.position)) dmg *= CampMarker.CoverDamage;
        if (by != null) dmg *= FieldPost.CoverFactor(transform.position, by.Position);   // за заслоном / пунктом урон меньше

        hp -= dmg;
        lastCombatTime = Time.time;
        if (hp <= 0f) { Die(by); return; }

        if (IsSoldier)
        {
            // Ответка: кто бьёт - тот и цель (гонец с едой не отвлекается)
            if (!IsSupplying && !EnemyAgent.Alive(target) && EnemyAgent.Alive(by)) target = by;
        }
        else panicUntil = Time.time + 5f;
    }

    void HungerTick(float dt)
    {
        if (IsLyingInBed) return;
        float floor = maxHp * HungerFloor;
        if (hp > floor) hp = Mathf.Max(floor, hp - HungerPerSecond * dt);
    }

    // Дома и в Ратуше житель ест из казны, пока не наестся
    void EatInside(float dt)
    {
        if (!inside || !CanEatIn(insideRef) || hp >= maxHp - 0.01f) return;
        var rm = ResourceManager.Instance;
        if (rm == null || rm.food <= 0) return;

        float got = ReceiveHeal(EatHpPerSecond * dt);
        eatAcc += got / HomeHpPerFood;
        int n = Mathf.FloorToInt(eatAcc);
        if (n > 0)
        {
            rm.Spend(0, 0, 0, Mathf.Min(n, rm.food));
            eatAcc -= n;
        }
    }

    // ---------- Главный цикл ----------

    void Update()
    {
        if (dead) return;
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        // Всадника ускоряет только брусчатка - навигация должна это знать, пока он ищет путь.
        // Реку вброд переходят солдаты, убегающие от врагов, отрезанные от дома, и тот, кто уже стоит в воде.
        bool mounted = HasHorse;
        bool wader = CanWade(nav);
        if (mounted) GridNavigation.MountedQuery = true;
        if (wader) GridNavigation.WaderQuery = true;
        try { Tick(nav, Time.deltaTime); }
        finally
        {
            if (mounted) GridNavigation.MountedQuery = false;
            if (wader) GridNavigation.WaderQuery = false;
        }

        UpdateLook();
    }

    // ---------- Реки: кто может идти вброд ----------

    float strandedUntil, strandedCheckAt;

    bool CanWade(GridNavigation nav)
    {
        if (!Rivers.Any) return false;
        if (IsSoldier || IsDeparting || IsPanicking) return true;
        if (!inside && nav.IsWaterCell(nav.WorldToCell(transform.position))) return true;   // уже в воде - выходим
        if (Time.time < strandedUntil) return true;

        // Отрезан рекой: не может дойти ни до Ратуши, ни до своего дома - выбирается вброд
        if (inside || pathFails < 3 || Time.time < strandedCheckAt) return false;
        strandedCheckAt = Time.time + 2f;
        Vector2Int me = nav.WorldToCell(transform.position);
        int myRegion = nav.RegionAt(me);
        if (myRegion == 0) return false;
        BuildingInstance hall = Hall();
        if (hall == null || !nav.TryGetDoorCell(hall, me, out Vector2Int hd) || nav.RegionAt(hd) == myRegion) return false;
        if (Home != null && nav.TryGetDoorCell(Home, me, out Vector2Int homeDoor) && nav.RegionAt(homeDoor) == myRegion) return false;
        strandedUntil = Time.time + 25f;
        return true;
    }

    void Tick(GridNavigation nav, float dt)
    {
        if (retryTimer > 0f) retryTimer -= dt;

        // Проданный на рынке боец уходит из города - остальное его не касается
        if (IsDeparting) { DepartTick(nav, dt); return; }

        // Возчик идёт рядом с обозом - остальное ждёт
        if (IsCartDriver && CartDutyUpdate(nav)) return;

        // Солдат в походе (набег) - идёт к краю карты или уже далеко
        if (ExpeditionTick(nav, dt)) return;

        KeepInside(nav);
        if (dead) return;

        if (IsSoldier) { SoldierTick(nav, dt); return; }
        if (trainingAt != null) { RecruitTick(nav); return; }
        CivilianTick(nav, dt);
    }

    // Пока житель внутри, держим его в здании; здание рушится или исчезло - выходим
    void KeepInside(GridNavigation nav)
    {
        if (!inside) return;

        if (insideRef == null)          // здание снесли
        {
            ExitBuilding(nav);
            return;
        }
        if (!insideRef.isActiveAndEnabled) return;   // здание переносят - ждём внутри

        if (insideRef.IsRuined || insideRef.IsFalling)
        {
            ExitBuilding(nav);
            if (!IsSoldier) panicUntil = Time.time + 4f;
            return;
        }

        // Здание перенесли - человек переезжает вместе с ним
        if (insideRef.transform.position != insideAnchor)
        {
            insideAnchor = insideRef.transform.position;
            transform.position = insideRef.Center;
        }
    }

    void CivilianTick(GridNavigation nav, float dt)
    {
        HungerTick(dt);
        EatInside(dt);

        if (IsLyingInBed) { BedTick(nav, dt); return; }   // лежачего бой снаружи не тревожит
        if (WarManager.IsWar && IsGathering) StopGather();
        if (WarBuildTick(nav, dt)) return;   // строитель двора: чинит под ударами, сам решает, когда бежать
        if (PanicTick(nav)) return;

        if (bed != null) { BedTick(nav, dt); return; }
        if (IsHealerBusy) { HealerTick(nav, dt); return; }
        if (IsCourier) { CourierTick(nav, dt); return; }
        if (IsHauling) { HaulTick(nav, dt); return; }
        if (GatherUpdate(nav)) return;

        if (TryTakeBed()) { BedTick(nav, dt); return; }
        if (TryStartHealerRound()) { HealerTick(nav, dt); return; }

        RoutineTick(nav, dt);
    }

    // ---------- Война: укрытие ----------

    // true - житель прячется (остальной распорядок на этот кадр пропускаем)
    bool PanicTick(GridNavigation nav)
    {
        if (!WarManager.IsWar)
        {
            panicUntil = 0f;
            panicShelter = null;
            return false;
        }

        if (inside)
        {
            // Пока враги у здания - сидим внутри (поручения подождут)
            if (insideRef != null && insideRef.isActiveAndEnabled && EnemyAgent.AnyWithin(insideRef.Center, SafeRadius))
                return true;
            return false;
        }

        Vector2 me = transform.position;
        if (EnemyAgent.AnyWithin(me, PanicRadius)) panicUntil = Time.time + 5f;
        if (Time.time >= panicUntil) return false;

        shelterRetry -= Time.deltaTime;
        if (!IsShelter(panicShelter) || shelterRetry <= 0f)
        {
            shelterRetry = 2f;
            BuildingInstance s = NearestShelter(me);
            if (!ReferenceEquals(s, panicShelter)) { panicShelter = s; path = null; }
        }
        if (panicShelter == null) return true;   // спрятаться негде - стоим

        if (WalkTo(nav, panicShelter))
        {
            EnterBuilding(panicShelter);
            panicShelter = null;
        }
        else if (pathFails >= 3)
        {
            unreachable = panicShelter;
            unreachableUntil = Time.time + 10f;
            panicShelter = null;
            pathFails = 0;
        }
        return true;
    }

    BuildingInstance NearestShelter(Vector2 from)
    {
        BuildingInstance best = null;
        float bestD = float.MaxValue;
        foreach (var b in BuildingInstance.All)
        {
            if (!IsShelter(b) || !Reachable(b)) continue;
            if (EnemyAgent.AnyWithin(b.Center, 2.5f)) continue;   // у этого здания уже враги
            float d = b.DistanceTo(from);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    // ---------- Распорядок дня ----------

    void RoutineTick(GridNavigation nav, float dt)
    {
        thinkTimer -= dt;
        if (thinkTimer <= 0f || (routineDest != null && !Usable(routineDest)))
        {
            thinkTimer = 0.5f;
            routineDest = PickDestination();
        }

        BuildingInstance dest = routineDest;
        if (dest == null) { Wander(nav, dt); return; }

        if (inside)
        {
            if (ReferenceEquals(insideRef, dest)) return;   // уже на месте
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;
            ExitBuilding(nav);
        }

        if (WalkTo(nav, dest))
        {
            EnterBuilding(dest);
            thinkTimer = 0f;
        }
        else if (pathFails >= 4)
        {
            // Не дойти (обнесли стеной) - какое-то время не пытаемся
            unreachable = dest;
            unreachableUntil = Time.time + 15f;
            pathFails = 0;
            thinkTimer = 0f;
        }
    }

    BuildingInstance PickDestination()
    {
        GameClock clock = GameClock.Instance;
        bool workTime = clock == null || clock.IsWorkTime;
        bool night = clock != null && clock.IsNight;

        BuildingInstance hall = Hall();
        BuildingInstance home = Reachable(Home) ? Home : (Reachable(hall) ? hall : null);
        var rm = ResourceManager.Instance;
        bool food = rm != null && rm.food > 0;

        // Война: сидим в укрытии (на работе в рабочее время, иначе дома)
        if (WarManager.IsWar)
        {
            if (inside && IsShelter(insideRef)) return insideRef;
            if (workTime && Reachable(workplace) && IsShelter(workplace)) return workplace;
            return home ?? NearestShelter(transform.position);
        }

        // Поесть
        if (mealTrip && (!food || HpFraction >= 0.97f)) mealTrip = false;
        if (!mealTrip && food && HpFraction < mealAt) mealTrip = true;
        if (mealTrip)
        {
            if (inside && CanEatIn(insideRef) && Usable(insideRef)) return insideRef;
            BuildingInstance place = Nearest(Reachable(Home) ? Home : null, Reachable(hall) ? hall : null);
            if (place != null) return place;
            mealTrip = false;
        }

        // Работа (ночью и в выходные - только дежурные)
        if (Reachable(workplace) && (workTime || IsSkeleton)) return workplace;

        // Ночь - спать
        if (night) return home;

        // Свободное время днём: то посидеть дома, то погулять
        if (leisureHome)
        {
            if (home == null) { leisureHome = false; return null; }
            if (inside && ReferenceEquals(insideRef, home))
            {
                if (leaveAt < 0f) leaveAt = Time.time + Random.Range(25f, 70f);
                if (Time.time < leaveAt) return home;
                leisureHome = false;
                leaveAt = -1f;
                wanderUntil = Time.time + Random.Range(20f, 50f);
                return null;
            }
            return home;
        }
        if (Time.time > wanderUntil && home != null) leisureHome = true;
        return leisureHome ? home : null;
    }

    BuildingInstance Nearest(BuildingInstance a, BuildingInstance b)
    {
        if (a == null) return b;
        if (b == null) return a;
        Vector2 me = transform.position;
        return a.DistanceTo(me) <= b.DistanceTo(me) ? a : b;
    }

    void Wander(GridNavigation nav, float dt)
    {
        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;
            ExitBuilding(nav);
        }

        if (wanderWait > 0f) { wanderWait -= dt; return; }

        if (!wanderHasPoint)
        {
            BuildingInstance anchor = Usable(Home) ? Home : Hall();
            Vector2 c = anchor != null ? anchor.Center : (Vector2)transform.position;
            if (nav.TryRandomWalkable(c, 5.5f, out Vector2Int cell))
            {
                wanderPoint = nav.CellCenter(cell) + Random.insideUnitCircle * 0.3f;
                wanderHasPoint = true;
                mover.Clear();
                mover.ResetFails();
            }
            else { wanderWait = 2f; return; }
        }

        if (mover.Step(transform, nav, wanderPoint, moveSpeed * 0.7f, 0.1f) || mover.Fails >= 3)
        {
            wanderHasPoint = false;
            wanderWait = Random.Range(2f, 6f);
        }
    }

    // ---------- Вход и выход ----------

    void EnterBuilding(BuildingInstance b)
    {
        if (b == null) return;
        inside = true;
        insideRef = b;
        insideAnchor = b.transform.position;
        path = null;
        pathTarget = null;
        mover.Clear();
        wanderHasPoint = false;
        if (sr != null) sr.enabled = false;
        transform.position = b.Center;

        GatherDepositOnEnter(b);

        // Солдат с добычей сдаёт её в Ратуше, казарме или доме
        if (HasLoot && (b.IsTownHall || IsHouse(b) || b.GetComponent<BarracksController>() != null)) DepositLoot();
    }

    void ExitBuilding(GridNavigation nav)
    {
        if (!inside) return;
        BuildingInstance b = insideRef;
        inside = false;
        insideRef = null;
        leaveAt = -1f;
        path = null;
        pathTarget = null;
        mover.Clear();
        mover.ResetFails();
        if (sr != null) sr.enabled = true;
        if (nav == null) return;

        Vector2Int cell = nav.WorldToCell(transform.position);
        if (b != null && nav.TryGetDoorCell(b, cell, out Vector2Int door)) cell = door;
        else if (nav.IsBlocked(cell)) cell = nav.NearestFree(cell);
        transform.position = nav.CellCenter(cell) + Random.insideUnitCircle * 0.15f;
    }

    // ---------- Ходьба к зданию ----------

    /// <summary>Шаг к двери здания по A*. true - пришли (стоим у двери).</summary>
    bool WalkTo(GridNavigation nav, BuildingInstance b)
    {
        if (b == null || nav == null) return false;

        Vector2 pos = transform.position;
        Vector2Int cur = nav.WorldToCell(pos);
        if (nav.IsBlocked(cur))
        {
            // Рядом поставили здание - выталкиваемся наружу
            cur = nav.NearestFree(cur);
            transform.position = nav.CellCenter(cur);
            pos = transform.position;
            path = null;
        }

        if (path == null || !ReferenceEquals(pathTarget, b))
        {
            if (retryTimer > 0f) return false;
            if (!GridNavigation.TryUsePathBudget()) return false;

            if (!nav.TryGetDoorCell(b, cur, out Vector2Int door))
            {
                pathFails++;
                retryTimer = 2f;
                path = null;
                return false;
            }

            List<Vector2Int> p = door == cur ? new List<Vector2Int>() : nav.FindPath(cur, door);
            if (p == null)
            {
                pathFails++;
                retryTimer = 1.5f;
                path = null;
                pathTarget = null;
                return false;
            }

            path = p;
            pathIndex = 0;
            pathTarget = b;
            pathDoor = door;
            pathFails = 0;
        }

        Vector2Int toCell;
        if (pathIndex < path.Count)
        {
            toCell = path[pathIndex];
            if (nav.IsBlocked(toCell)) { path = null; return false; }   // путь перекрыли - ищем новый
        }
        else toCell = pathDoor;

        Vector2 target = nav.CellCenter(toCell);
        float sp = moveSpeed * nav.SpeedFactor(pos, toCell);
        Vector2 np = Vector2.MoveTowards(pos, target, sp * Time.deltaTime);
        transform.position = np;

        if (Vector2.Distance(np, target) > 0.05f) return false;
        if (pathIndex < path.Count) { pathIndex++; return false; }

        path = null;
        // Здание могли перенести, пока шли - тогда ищем дверь заново
        if (b.DistanceTo(np) > 1.6f) { pathTarget = null; return false; }
        return true;
    }

    // ---------- Внешний вид ----------

    void UpdateLook()
    {
        if (sr == null) return;

        if (!IsGathering && !IsCartDriver)
        {
            Color want;
            if (IsSoldier) want = currentTint;
            else if (trainingAt != null) want = RecruitColor;
            else if (IsHealerBusy) want = HealerColor;
            else if (IsWarBuilding) want = WarBuilderColor;
            else if ((courierActive && courierLoaded) || (haul != null && haulLoaded)) want = CarryColor;
            else want = currentTint;
            if (sr.color != want) sr.color = want;
        }

        bool shown = sr.enabled;
        if (horseSr != null) horseSr.enabled = shown && (HasHorse || (IsDeparting && departMounted));
        if (starSr != null) starSr.enabled = shown && IsSoldier && hearty;
        if (sackSr != null) sackSr.enabled = shown && HasLoot;
        CannonLook(shown);
        CatapultLook(shown);
        WarBuildLook(shown);
    }

    // ---------- Тексты для карточки (CitizenInfoPanel) и казармы ----------

    public string DisplayName
    {
        get
        {
            if (IsSoldier) return unit.unitName + (hearty ? " ★" : "");
            if (trainingAt != null) return "Новобранец";
            return "Житель";
        }
    }

    public string RoleText
    {
        get
        {
            if (IsSoldier)
            {
                string sq = Squad != null ? ", " + Squad.Name : "";
                string merc = IsMercenary ? " <color=#C8A8FF>(наёмник)</color>" : "";
                return $"Солдат: <b>{DisplayName}</b>{(HasHorse ? " (верхом)" : "")}{merc}{sq}";
            }
            if (trainingAt != null) return "Новобранец: идёт учиться в казарму";
            if (workplace != null)
            {
                string n = workplace.type != null ? workplace.type.name : "здание";
                return $"Работает: <b>{n}</b>{(IsSkeleton ? " <color=#A0A0A0>(дежурный)</color>" : "")}";
            }
            return "Безработный";
        }
    }

    public string HomeText
    {
        get
        {
            if (Home != null)
            {
                string ruin = Home.IsRuined ? " <color=#FF8060>(руины)</color>" : "";
                string hn = Home.type != null ? Home.type.name : PopulationManager.HouseName;
                return $"{hn} [{Home.OriginCell.x},{Home.OriginCell.y}]{ruin}";
            }
            if (IsMercenary) return Hall() != null ? "<color=#FFD060>ночует в Ратуше (нет места в Гостевом доме)</color>" : "<color=#FF6060>негде жить</color>";
            return Hall() != null ? "<color=#FFD060>ночует в Ратуше (в домах нет места)</color>" : "<color=#FF6060>негде жить</color>";
        }
    }

    public string ConditionText
    {
        get
        {
            float f = HpFraction;
            if (IsSoldier)
            {
                if (f >= 0.75f) return "<color=#80FF80>в строю</color>";
                if (f >= 0.4f) return "<color=#FFD060>ранен</color>";
                return "<color=#FF6060>тяжело ранен</color>";
            }
            if (f >= 0.75f) return "<color=#80FF80>сыт и здоров</color>";
            if (f >= 0.5f) return "<color=#D0FF80>проголодался</color>";
            if (f >= 0.3f) return "<color=#FFD060>голоден или ранен</color>";
            return "<color=#FF6060>еле держится</color>";
        }
    }

    public string ActivityText
    {
        get
        {
            if (dead) return "погиб";
            if (IsDeparting) return DepartText;
            if (IsCartDriver) return "<color=#C0E890>ведёт обоз</color>";
            if (IsPanicking) return "<color=#FF8060>убегает от врагов</color>";
            if (Time.time < strandedUntil && !inside) return "<color=#80C0FF>отрезан рекой — пробирается вброд</color>";
            if (IsLyingInBed) return "<color=#80FF80>лечится в лазарете</color>";
            if (bed != null) return "идёт в лазарет";
            if (IsSoldier) return StatusText;
            if (trainingAt != null) return inside ? "проходит обучение" : "идёт в казарму на обучение";

            string g = GatherActivityText;
            if (g != "") return g;
            if (IsHealerBusy) return HealerActivityText;
            string wb = WarBuildActivityText;
            if (wb != "") return wb;
            if (IsCourier) return courierLoaded ? "несёт добычу в Ратушу" : "идёт за добычей: " + NameOf(courierSrc);
            if (IsHauling) return haulLoaded ? "несёт груз: " + NameOf(haul.to) : "идёт за грузом: " + NameOf(haul.from);

            if (inside && insideRef != null)
            {
                string n = NameOf(insideRef);
                if (WarManager.IsWar) return "<color=#FFB080>прячется: " + n + "</color>";
                var rm = ResourceManager.Instance;
                if (CanEatIn(insideRef) && hp < maxHp - 0.5f && rm != null && rm.food > 0) return "ест: " + n;
                if (ReferenceEquals(insideRef, workplace))
                {
                    GameClock c = GameClock.Instance;
                    return c == null || c.IsWorkTime ? "работает" : "дежурит";
                }
                GameClock clock = GameClock.Instance;
                if (clock != null && clock.IsNight) return "спит: " + n;
                return "отдыхает: " + n;
            }

            if (mealTrip) return "идёт поесть";
            if (pathTarget != null) return "идёт: " + NameOf(pathTarget);
            return "гуляет";
        }
    }

    static string NameOf(BuildingInstance b)
    {
        return b != null && b.type != null ? b.type.name : "—";
    }

    public string CarryText
    {
        get
        {
            var parts = new List<string>();
            if (HasLoot) parts.Add("добыча: " + carried.ShortText);
            if (courierActive && courierLoaded) parts.Add(CourierLoadText());
            if (haul != null && haulLoaded) parts.Add("груз для: " + NameOf(haul.to));
            if (healBag > 0) parts.Add($"еда для лечения: {healBag}");
            if (BerryBag > 0) parts.Add($"ягоды: {BerryBag}");
            if (herbBag > 0) parts.Add($"травы: {herbBag}");
            if (IsSupplying && supplyFood > 0) parts.Add($"еда для лагеря: {supplyFood}");
            return parts.Count > 0 ? string.Join("\n", parts) : "<color=#A0A0A0>ничего</color>";
        }
    }
}
