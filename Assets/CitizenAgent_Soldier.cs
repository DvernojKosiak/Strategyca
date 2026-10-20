using UnityEngine;

/// <summary>Приказ отряду / бойцу.</summary>
public enum SoldierOrder
{
    Garrison,   // сидеть внутри казармы
    Guard,      // охранять казарму (ходят вокруг неё)
    Move        // идти в точку на карте и стоять там лагерем
}

/// <summary>
/// Новобранец и солдат (продолжение класса CitizenAgent).
///  - Новобранец идёт в казарму и сидит там, пока идёт обучение (BarracksController).
///  - Солдат: вид войск (UnitType), казарма, отряд (Squad), приказ.
///    Охрана - ходит вокруг казармы и бьёт врагов поблизости. Раненый (меньше половины здоровья)
///    уходит отдыхать в казарму или на койку лазарета. Внутри казармы бойцы едят из казны и лечатся.
///    Поход - идёт в точку, там лагерь отряда: патрулирует вокруг или стоит.
///  - Полевой маг лечит раненых рядом (и себя - вдвое медленнее), если лечить некого - дерётся.
///  - Поджигатель: первый удар - ведро горящей смолы по площади (урон всем в огне + поджог), дальше кулаки.
///    Ведро снова наполняют в казарме (смола из Ратуши).
///  - Лук с зельем замедления замедляет цель (заряды тратятся по выстрелу).
///  - Оружие (Оружейная) усиливает урон и изнашивается с каждым ударом. Лук ещё и бьёт дальше.
///  - Похлёбка (★): больше здоровья, урона и скорости.
///  - Броня / плащ из листвы (ветки Военного дела, CitizenAgent_Gear): броня режет урон и замедляет,
///    плащ прячет бойца от врагов, пока он не бьёт. Толкает колёсную баррикаду - pushingPost (FieldPost).
///  - Конь (конница): всадник быстрее только на брусчатке. Боец погиб - конь погиб, уволили - конь в Конюшню.
///  - Добыча, отбитая у врага, - в мешке над бойцом. Сдаётся в Ратуше / казарме / доме или у их двери.
///  - Гонец: когда в лагере кончается еда, отряд посылает бойца в Ратушу (StartSupply).
///  - Казарму снесли - её бойцы становятся обычными жителями.
///  - Реки: боец переходит вброд медленно и, пока стоит в воде, не бьёт - сначала выбирается на берег.
///    Точка приказа в воде сдвигается на ближайший берег.
///  - Маг стихий (путь Магии): бьёт водяными снарядами, иногда - огненным шаром по площади (поджигает).
///    С ядром хаоса (школа Хаоса) иногда бросает чёрную дыру - стягивает врагов (ChaosMagic).
///    Рядом с рекой изредка выводит её из берегов: волна бьёт и надолго замедляет врагов (сила - от маны).
/// </summary>
public partial class CitizenAgent
{
    const float BarracksHealPerSecond = 3f;   // лечение внутри казармы
    const float BarracksHpPerFood = 3f;       // ... 1 еда из казны = 3 здоровья
    const float RestBelow = 0.5f;             // охранник с меньшим здоровьем уходит отдыхать
    const float RestUntil = 0.95f;

    // ---------- Солдат ----------
    UnitType unit;
    BuildingInstance barracks;
    BarracksController barracksCtrl;
    bool hearty;                              // обучен на похлёбке (★)
    bool hasHorse;
    bool bucketFull;                          // Поджигатель: ведро горящей смолы ещё не вылито
    Weapon weapon;
    SoldierOrder order = SoldierOrder.Guard;
    Vector2 orderPoint;
    bool recovering;                          // ранен - отдыхает в казарме
    bool restAfterTrip;                       // вернулся из похода раненым: отдыхает до RestUntil

    bool postRest;                            // раненый отдыхает на перевалочном пункте
    EnemyAgent target;
    EnemyAgent ignoreEnemy;
    float ignoreEnemyUntil;
    float attackTimer, scanTimer;
    float lastCombatTime = -99f;
    float floodReadyAt;                       // Маг стихий: когда снова можно вызвать разлив реки

    float patrolWait;
    Vector2 patrolPoint;
    bool patrolHasPoint;

    CitizenAgent healTarget;                  // полевой маг: кого лечит
    float healScan, healFx;
    float restFoodAcc;

    readonly Loot carried = new Loot();       // отбитая у врага добыча

    // ---------- Новобранец ----------
    BuildingInstance trainingAt;

    // ---------- Гонец за едой ----------
    Squad supplySquad;
    bool supplyLoaded;
    int supplyFood;
    float supplyTimer;

    // ---------- Свойства ----------

    public bool IsSoldier => unit != null;
    public string UnitName => unit != null ? unit.unitName : (trainingAt != null ? "Новобранец" : "Житель");
    public BuildingInstance TrainingAt => trainingAt;
    public BuildingInstance Barracks => barracks;

    public Squad Squad { get; set; }
    public bool PatrolAtPoint { get; set; } = true;   // в лагере: true - патрулируют, false - стоят
    public bool OrderReached { get; private set; }    // дошёл до точки приказа
    public SoldierOrder Order => order;

    public bool HasHorse => hasHorse && unit != null;
    public bool HasLoot => !carried.IsEmpty;
    public bool InCombat => Time.time - lastCombatTime < 3f;
    public bool IsSupplying => supplySquad != null;

    public bool CanUseWeapon => unit != null && unit.usesWeapon;

    // ---------- Поджигатель ----------
    public bool IsFireBomber => unit != null && unit.IsFireBomber;
    public bool BucketFull => IsFireBomber && bucketFull;
    public bool NeedsBucket => IsFireBomber && !bucketFull;
    public void RefillBucket() { if (IsFireBomber) bucketFull = true; }
    public WeaponKind WeaponKindAllowed => unit != null ? unit.weaponKind : WeaponKind.Sword;
    public Weapon EquippedWeapon => weapon;

    float Damage
    {
        get
        {
            if (unit == null) return 0f;
            float baseDmg = IsFireBomber && !bucketFull && unit.fistDamage > 0f ? unit.fistDamage : unit.damage;
            return baseDmg * (hearty ? unit.pottageDamageMul : 1f) * (weapon != null ? weapon.DamageMul : 1f);
        }
    }

    // Поджигатель с пустым ведром дерётся вплотную
    float AttackRange => unit == null ? 1f : (IsFireBomber && !bucketFull ? 1.1f : unit.attackRange) + (weapon != null ? weapon.RangeBonus : 0f)
                         + (IsFieldCannon && cannonLift.Up ? TowerModules.LiftRangeBonus : 0f);
    bool Ranged => unit != null && unit.attackRange > 2f && !IsFireBomber;
    float AggroRadius => Mathf.Max(6f, AttackRange + 1.5f);

    float GuardRadius
    {
        get
        {
            if (barracksCtrl == null && barracks != null) barracksCtrl = barracks.GetComponent<BarracksController>();
            return barracksCtrl != null ? barracksCtrl.guardRadius : 3.5f;
        }
    }

    // ---------- Обучение ----------

    /// <summary>Житель идёт в казарму учиться (PopulationManager.TakeRecruit). Все прежние дела бросает.</summary>
    public void StartTraining(BuildingInstance b)
    {
        if (dead || b == null) return;
        CancelCartDuty();
        CancelGather();
        AbortErrands(false);
        mealTrip = false;
        leaveAt = -1f;
        workplace = null;
        IsSkeleton = false;
        trainingAt = b;
        path = null;
        pathTarget = null;
        pathFails = 0;
    }

    /// <summary>Обучение сорвано - снова обычный житель.</summary>
    public void CancelTraining()
    {
        if (trainingAt == null) return;
        trainingAt = null;
        thinkTimer = 0f;
        path = null;
        pathTarget = null;
    }

    /// <summary>Новобранец застрял по дороге - заходит в казарму сразу.</summary>
    public void ForceEnter(BuildingInstance b)
    {
        if (dead || b == null || IsInside(b)) return;
        inside = false;
        insideRef = null;
        EnterBuilding(b);
    }

    void RecruitTick(GridNavigation nav)
    {
        if (IsInside(trainingAt)) return;   // учится
        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;
            ExitBuilding(nav);
        }
        if (!Usable(trainingAt)) return;    // казарму бьют / переносят - ждём рядом
        if (WalkTo(nav, trainingAt)) EnterBuilding(trainingAt);
    }

    /// <summary>Обучение закончено: житель становится бойцом этой казармы.</summary>
    public void BecomeSoldier(BuildingInstance b, UnitType u, bool isHearty)
    {
        if (dead || u == null) return;
        trainingAt = null;
        barracks = b;
        barracksCtrl = b != null ? b.GetComponent<BarracksController>() : null;
        unit = u;
        hearty = isHearty;
        hasHorse = u.requiresHorse;
        bucketFull = u.IsFireBomber;   // новый Поджигатель выходит с полным ведром

        maxHp = Mathf.Max(1f, u.maxHp * (hearty ? u.pottageHpMul : 1f));
        hp = maxHp;
        moveSpeed = (u.moveSpeed > 0f ? u.moveSpeed : CitizenSpeed) * (hearty ? u.pottageSpeedMul : 1f);
        if (HasArmor) moveSpeed *= BodyGear.ArmorSpeedMul;   // броня тяжёлая

        order = SoldierOrder.Guard;   // новый боец сначала охраняет казарму (отряд может сразу сменить приказ)
        OrderReached = false;
        recovering = false;
        restAfterTrip = false;
        target = null;
        attackTimer = 0f;
        patrolHasPoint = false;

        currentTint = u.color;
        SetBodyScale(u.bodyScale);
        if (sr != null) sr.color = currentTint;
    }

    /// <summary>Боец уволен: оружие - в Оружейную, конь - в Конюшню, добыча - в казну. Щит снимает казарма.</summary>
    public void Discharge()
    {
        if (unit == null) return;

        if (IsSupplying) CancelSupply();
        if (Squad != null)
        {
            Squad sq = Squad;
            sq.Remove(this);
            Squad = null;
        }
        if (bed != null) LeaveBed();

        if (weapon != null && !ArmoryController.Stash(weapon))
            WarManager.Notify("В Оружейных нет места: оружие уволенного бойца потеряно", false);
        weapon = null;
        ReturnBodyToArmory(); // броня / плащ - в Оружейную
        pushingPost = null;
        CannonReturnGear();   // полевая пушка: подъёмник и снаряды - в Ратушу
        ChaosReturnCore();    // Маг стихий: ядро хаоса - в Ратушу

        if (hasHorse && !StableController.ReturnHorse())
            WarManager.Notify("В Конюшнях нет места: лошадь уволенного бойца потеряна", false);
        hasHorse = false;

        if (HasLoot) DepositLoot();

        float frac = HpFraction;
        unit = null;
        barracks = null;
        barracksCtrl = null;
        hearty = false;
        recovering = false;
        target = null;
        healTarget = null;
        order = SoldierOrder.Guard;
        OrderReached = false;

        maxHp = CitizenMaxHp;
        hp = Mathf.Max(1f, frac * maxHp);
        moveSpeed = CitizenSpeed;
        currentTint = CivilianColor;
        SetBodyScale(CitizenScale);
        mover.Clear();
        path = null;
        pathTarget = null;
        thinkTimer = 0f;
    }

    // ---------- Снаряжение ----------

    public bool EquipWeapon(Weapon w)
    {
        if (w == null || dead || !CanUseWeapon || w.kind != WeaponKindAllowed || weapon != null) return false;
        weapon = w;
        return true;
    }

    public Weapon UnequipWeapon()
    {
        Weapon w = weapon;
        weapon = null;
        return w;
    }

    // ---------- Приказы ----------

    public void SetOrder(SoldierOrder o, Vector2 point)
    {
        order = o;
        orderPoint = o == SoldierOrder.Move ? Rivers.DryPoint(point) : point;   // в реке лагерем не стоят
        OrderReached = false;
        recovering = false;
        target = null;
        healTarget = null;
        patrolHasPoint = false;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        pathFails = 0;
        if (o == SoldierOrder.Move && bed != null) LeaveBed();
    }

    // ---------- Добыча ----------

    public void ReceiveLoot(Loot l, Vector2 from)
    {
        if (l == null || l.IsEmpty || dead) return;
        l.MoveTo(carried);
        CombatFx.LootFly(from, transform.position);
    }

    void DepositLoot()
    {
        if (!HasLoot) return;
        if (expeditionLoot)
        {
            // Добыча похода: итог уже показан в отчёте - сдаём молча
            expeditionLoot = false;
            carried.DepositToTown();
            return;
        }
        string what = carried.ShortText;
        carried.DepositToTown();
        WarManager.Notify($"{DisplayName} {citizenName} сдал добычу: {what}", true);
    }

    // Добыча сдаётся и у двери Ратуши / казармы
    void TryDepositLootAtDoor()
    {
        Vector2 me = transform.position;
        BuildingInstance hall = Hall();
        if ((Usable(hall) && hall.DistanceTo(me) < 1.3f) || (Usable(barracks) && barracks.DistanceTo(me) < 1.3f))
            DepositLoot();
    }

    // ---------- Главный цикл солдата ----------

    void SoldierTick(GridNavigation nav, float dt)
    {
        // Казарму снесли - служить негде, боец становится жителем
        if (barracks == null)
        {
            WarManager.Notify($"Казармы больше нет: {unit.unitName} {citizenName} снова житель", false);
            ShieldBearer.ReturnToArmory(this);
            Discharge();
            return;
        }

        attackTimer -= dt;
        bool home = Usable(barracks);

        if (HasLoot && !inside) TryDepositLootAtDoor();

        if (IsSupplying) { SupplyTick(nav, dt); return; }
        if (bed != null) { BedTick(nav, dt); return; }

        // Толкает колёсную баррикаду (FieldPost): пока она не доехала, другим не занят
        if (pushingPost != null)
        {
            if (inside)
            {
                if (insideRef != null && !insideRef.isActiveAndEnabled) return;
                ExitBuilding(nav);
            }
            if (pushingPost.TickPusher(this)) return;
            pushingPost = null;
        }

        // Внутри казармы: гарнизон и раненые сидят и лечатся
        if (inside)
        {
            bool stay = home && ReferenceEquals(insideRef, barracks) && (order == SoldierOrder.Garrison || recovering);
            if (stay)
            {
                RestInBarracks(dt);
                if (recovering)
                {
                    bool threat = HpFraction >= 0.6f && EnemyAgent.AnyWithin(barracks.Center, GuardRadius + 4f);
                    if (HpFraction >= RestUntil) restAfterTrip = false;
                    if (HpFraction >= RestUntil || threat) recovering = false;
                }
                return;
            }
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;
            ExitBuilding(nav);
        }

        scanTimer -= dt;
        if (scanTimer <= 0f)
        {
            scanTimer = 0.3f;
            AcquireTarget();
        }

        if (unit.IsHealer && FieldHealTick(nav, dt)) return;
        if (EnemyAgent.Alive(target)) { FightTick(nav); return; }
        if (PostRestTick(nav)) return;

        switch (order)
        {
            case SoldierOrder.Garrison:
                if (home) { if (WalkTo(nav, barracks)) EnterBuilding(barracks); }
                else GuardTick(nav, dt, false);
                break;
            case SoldierOrder.Guard:
                GuardTick(nav, dt, home);
                break;
            default:
                MoveTick(nav, dt);
                break;
        }
    }

    void RestInBarracks(float dt)
    {
        if (hp >= maxHp - 0.01f) return;
        var rm = ResourceManager.Instance;
        if (rm == null || rm.food <= 0)
        {
            ReceiveHeal(BarracksHealPerSecond * 0.3f * dt);   // еды нет - просто отдых, медленнее
            return;
        }
        float got = ReceiveHeal(BarracksHealPerSecond * dt);
        restFoodAcc += got / BarracksHpPerFood;
        int n = Mathf.FloorToInt(restFoodAcc);
        if (n > 0)
        {
            rm.Spend(0, 0, 0, Mathf.Min(n, rm.food));
            restFoodAcc -= n;
        }
    }

    // ---------- Поиск врага ----------

    // Куда боец привязан: казарма (охрана) или лагерь (поход). Дальше leash за врагом не бежит.
    void Anchor(out Vector2 anchor, out float leash)
    {
        if (order == SoldierOrder.Move)
        {
            if (!OrderReached)
            {
                anchor = transform.position;   // по дороге бьёт только тех, кто рядом
                leash = AggroRadius + 2f;
                return;
            }
            anchor = Squad != null && Squad.HasCamp ? Squad.CampPoint : orderPoint;
            leash = IsCatapult ? Mathf.Max(10f, AttackRange + 1f) : 10f;   // катапульта бьёт дальше лагеря
            return;
        }
        anchor = barracks != null ? barracks.Center : (Vector2)transform.position;
        leash = GuardRadius + 7f;
    }

    void AcquireTarget()
    {
        Vector2 me = transform.position;
        float aggro = AggroRadius;
        Anchor(out Vector2 anchor, out float leash);

        if (EnemyAgent.Alive(target))
        {
            Vector2 tp = target.Position;
            if (Vector2.Distance(tp, anchor) <= leash + 3f && Vector2.Distance(tp, me) <= aggro * 2f) return;
            target = null;   // убежал далеко - не гонимся
            mover.Clear();
        }

        target = EnemyAgent.FindNearest(me, aggro, e =>
            (e != ignoreEnemy || Time.time > ignoreEnemyUntil)
            && Vector2.Distance(e.Position, anchor) <= leash
            && (!IsCatapult || CatapultWants(e))
            && ResourceZones.Visible(me, e.Position, aggro));

        // Охрана: враги у самой казармы - идём туда, даже если боец их ещё не видит
        if (target == null && order != SoldierOrder.Move && barracks != null)
            target = EnemyAgent.FindNearest(barracks.Center, GuardRadius + 2f);

        if (target != null) { recovering = false; mover.ResetFails(); }
    }

    void FightTick(GridNavigation nav)
    {
        if (IsCatapult) { CatapultFightTick(nav); return; }   // навесом по площади (CitizenAgent_Catapult)

        Vector2 me = transform.position;
        Vector2 tp = target.Position;
        float d = Vector2.Distance(me, tp);
        float range = AttackRange;
        bool wading = nav.IsWading(me);   // вброд не дерутся

        // Полевая пушка: прямой наводки нет (стена, дом) и подъёмника нет - подъезжает к цели
        if (CannonRepositioning && !wading)
        {
            mover.Step(transform, nav, tp, moveSpeed, 0.8f);
            return;
        }

        if (d <= range && !wading)
        {
            mover.Clear();
            lastCombatTime = Time.time;
            if (attackTimer <= 0f)
            {
                if (IsFieldCannon)
                {
                    if (!CannonCanFire(tp, out bool lob)) return;
                    attackTimer = Mathf.Max(0.1f, unit.attackCooldown);
                    MarkAttacked();
                    CannonStrikeHome(target, lob);
                    return;
                }
                attackTimer = Mathf.Max(0.1f, unit.attackCooldown * (weapon != null ? weapon.SpeedMul : 1f));
                MarkAttacked();   // ударил - плащ на время его не прячет
                Strike(target);
            }
            return;
        }
        if (wading && d <= range)
        {
            // Враг рядом, а мы в воде - сначала на берег (ближе к нему)
            mover.Step(transform, nav, Rivers.DryPoint(tp), moveSpeed, 0.3f);
            return;
        }

        mover.Step(transform, nav, tp, moveSpeed, range * 0.8f);
        if (mover.Fails >= 3)
        {
            // До врага не добраться (за стеной) - бросаем его на время
            ignoreEnemy = target;
            ignoreEnemyUntil = Time.time + 5f;
            target = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    void Strike(EnemyAgent e)
    {
        Vector2 me = transform.position;

        // Поджигатель: ведро горящей смолы на площадь (один раз, потом - кулаки)
        if (BucketFull)
        {
            bucketFull = false;
            ThrowFire(me, e.Position, unit, this);
            return;
        }

        // Маг стихий: свои заряды (водяной снаряд, огненный шар, разлив реки)
        if (unit.IsElementalist) { ElementalStrike(e); return; }

        float dmg = Damage;
        TakeShotPoison(out float pDps, out float pDur);
        TakeShotSlow(out float slowF, out float slowDur);
        if (weapon != null && weapon.Wear())
        {
            WarManager.Notify($"Сломалось оружие: {weapon.Name} ({unit.unitName} {citizenName})", false);
            weapon = null;
        }

        // Маг-отравитель: ядовитый снаряд по площади
        if (unit.aoeRadius > 0f)
        {
            Vector2 at = e.Position;
            float radius = unit.aoeRadius;
            CitizenAgent by = this;
            CombatFx.Shell(me, at, 0.55f, radius, () =>
            {
                foreach (var en in new System.Collections.Generic.List<EnemyAgent>(EnemyAgent.All))
                {
                    if (!EnemyAgent.Alive(en) || Vector2.Distance(en.Position, at) > radius) continue;
                    en.TakeDamage(dmg, by != null ? by : null);
                    PoisonEnemy(en, pDps, pDur, by);
                    if (EnemyAgent.Alive(en)) SlowEffect.Apply(en, slowF, slowDur);
                }
            });
            return;
        }

        if (Ranged) CombatFx.Arrow(me, e.Position);
        else CombatFx.Flash(e.Position);
        e.TakeDamage(dmg, this);
        PoisonEnemy(e, pDps, pDur, this);
        if (EnemyAgent.Alive(e)) SlowEffect.Apply(e, slowF, slowDur);
    }

    // ---------- Маг стихий ----------

    void ElementalStrike(EnemyAgent e)
    {
        Vector2 me = transform.position;
        Vector2 at = e.Position;
        float mana = ManaManager.Factor;
        CitizenAgent by = this;

        // 0. Ядро хаоса: у цели толпа и перезарядка готова - чёрная дыра (ChaosMagic)
        if (TryChaosHome(e)) return;

        // 1. Рядом река - изредка выводит её из берегов на врагов
        if (unit.floodChance > 0f && Time.time >= floodReadyAt && Random.value < unit.floodChance
            && Rivers.NearestWater(me, unit.floodWaterRange, out Vector2 src))
        {
            floodReadyAt = Time.time + unit.floodCooldown;
            float radius = unit.floodRadius, dmgF = unit.floodDamage * mana, slow = unit.floodSlow, secs = unit.floodSeconds;
            CombatFx.Wave(src, at, radius, () =>
            {
                foreach (var en in new System.Collections.Generic.List<EnemyAgent>(EnemyAgent.All))
                {
                    if (!EnemyAgent.Alive(en) || Vector2.Distance(en.Position, at) > radius + en.Unit.bodyScale * 0.3f) continue;
                    en.TakeDamage(dmgF, by != null ? by : null, null, true);
                    if (EnemyAgent.Alive(en)) SlowEffect.Apply(en, slow, secs);
                }
                FloodPool.Create(at, radius, slow, secs);
            });
            WarManager.Notify($"{unit.unitName}: река вышла из берегов — врагов накрыло волной!", true);
            return;
        }

        // 2. Иногда - огненный шар по площади (поджигает)
        if (unit.fireballChance > 0f && Random.value < unit.fireballChance)
        {
            float radius = unit.fireballRadius, dmgF = unit.fireballDamage * mana, dps = unit.burnDps * mana, dur = unit.burnDuration;
            CombatFx.Fireball(me, at, radius, () =>
            {
                foreach (var en in new System.Collections.Generic.List<EnemyAgent>(EnemyAgent.All))
                {
                    if (!EnemyAgent.Alive(en) || Vector2.Distance(en.Position, at) > radius + en.Unit.bodyScale * 0.3f) continue;
                    CitizenAgent killer = by != null ? by : null;
                    en.TakeDamage(dmgF, killer, null, true);
                    if (!EnemyAgent.Alive(en) || dps <= 0f) continue;
                    EnemyAgent victim = en;
                    BurnEffect.Apply(en, dps, dur, d => { if (EnemyAgent.Alive(victim)) victim.TakeDamage(d, killer, null, true); });
                }
            });
            return;
        }

        // 3. Обычный водяной снаряд (магический урон - щит не закрывает)
        CombatFx.Bolt(me, at);
        e.TakeDamage(Damage * mana, this, null, true);
    }

    /// <summary>Разлив горящей смолы: урон всем врагам в огне в этот момент, и они ещё горят. Потом огонь гаснет.</summary>
    static void ThrowFire(Vector2 from, Vector2 at, UnitType u, CitizenAgent by)
    {
        float radius = u.fireRadius, dmg = u.fireDamage, dps = u.burnDps, dur = u.burnDuration;
        CombatFx.Bucket(from, at, radius, () =>
        {
            foreach (var en in new System.Collections.Generic.List<EnemyAgent>(EnemyAgent.All))
            {
                if (!EnemyAgent.Alive(en) || Vector2.Distance(en.Position, at) > radius + en.Unit.bodyScale * 0.3f) continue;
                CitizenAgent killer = by != null ? by : null;
                en.TakeDamage(dmg, killer, null, true);   // огонь щитом не закрыть
                if (!EnemyAgent.Alive(en)) continue;
                EnemyAgent victim = en;
                BurnEffect.Apply(en, dps, dur, d => { if (EnemyAgent.Alive(victim)) victim.TakeDamage(d, killer, null, true); });
            }
        });
    }

    /// <summary>Замедление этого выстрела: лук с зельем замедления (заряд тратится).</summary>
    void TakeShotSlow(out float factor, out float dur)
    {
        factor = 1f;
        dur = 0f;
        if (weapon != null && weapon.IsSlowing)
        {
            weapon.slowCharges--;
            factor = Weapon.SlowFactor;
            dur = Weapon.SlowDuration;
        }
    }

    /// <summary>
    /// Продажа на рынке: боец уходит в лагерь наёмников гильдии вместе со своим оружием и конём (щит снимает казарма).
    /// Добыча сдаётся в казну. Возвращает данные бойца для лота; сам житель потом убирается из города.
    /// </summary>
    public SoldierLot PackForMercenaryCamp()
    {
        if (unit == null || dead || IsOnExpedition) return null;
        if (IsSupplying) CancelSupply();
        if (Squad != null) { Squad sq = Squad; sq.Remove(this); Squad = null; }
        if (bed != null) LeaveBed();
        if (HasLoot) carried.DepositToTown();
        CannonReturnGear();   // модули пушки остаются городу
        ChaosReturnCore();    // и ядро хаоса тоже
        ReturnBodyToArmory(); // броня и плащ - тоже (их делают только ветки Военного дела)
        pushingPost = null;

        SoldierLot lot = new SoldierLot
        {
            unit = unit,
            hearty = hearty,
            weapon = weapon,
            name = citizenName
        };
        weapon = null;      // уходит вместе с бойцом
        hasHorse = false;   // конь тоже (в Конюшню не возвращается)
        return lot;
    }

    /// <summary>
    /// Яд этого выстрела (сила - по мане в воздухе): у Мага-отравителя - всегда,
    /// у отравленного лука - пока не кончились заряды (заряд тратится).
    /// </summary>
    void TakeShotPoison(out float dps, out float dur)
    {
        dps = 0f;
        dur = 0f;
        float mana = ManaManager.Factor;
        if (unit != null && unit.IsPoisoner)
        {
            dps = unit.poisonDps * mana;
            dur = unit.poisonDuration;
        }
        if (weapon != null && weapon.IsPoisoned)
        {
            weapon.poisonCharges--;
            dps = Mathf.Max(dps, Weapon.PoisonDps * mana);
            dur = Mathf.Max(dur, Weapon.PoisonDuration);
        }
    }

    static void PoisonEnemy(EnemyAgent e, float dps, float dur, CitizenAgent by)
    {
        if (dps <= 0f || !EnemyAgent.Alive(e)) return;
        // Яд - магический урон тиками: щит его не берёт, убийство засчитывается отравителю
        PoisonEffect.Apply(e, dps, dur, d => { if (EnemyAgent.Alive(e)) e.TakeDamage(d, by, null, true); });
    }

    // ---------- Полевой маг ----------

    // true - маг занят лечением (остальное на этот кадр пропускаем)
    bool FieldHealTick(GridNavigation nav, float dt)
    {
        if (hp < maxHp) ReceiveHeal(unit.healPerSecond * 0.5f * dt);

        healScan -= dt;
        if (healScan <= 0f || !NeedsFieldHeal(healTarget))
        {
            healScan = 0.4f;
            healTarget = FindWoundedAlly();
        }
        if (healTarget == null) return false;

        Vector2 me = transform.position;
        Vector2 tp = healTarget.transform.position;
        if (Vector2.Distance(me, tp) > unit.healRange)
        {
            mover.Step(transform, nav, tp, moveSpeed, unit.healRange * 0.75f);
            if (mover.Fails >= 4)
            {
                healTarget = null;
                mover.Clear();
                mover.ResetFails();
            }
            return true;
        }

        mover.Clear();
        healTarget.ReceiveHeal(unit.healPerSecond * dt);
        healFx -= dt;
        if (healFx <= 0f)
        {
            healFx = 0.25f;
            CombatFx.HealSpark(me, tp);
        }
        return true;
    }

    bool NeedsFieldHeal(CitizenAgent a)
    {
        return a != null && a != this && !a.dead && !a.inside && a.hp < a.maxHp * 0.98f;
    }

    CitizenAgent FindWoundedAlly()
    {
        Vector2 me = transform.position;
        Anchor(out Vector2 anchor, out float leash);
        float radius = AggroRadius + 3f;

        CitizenAgent best = null;
        float bestScore = float.MaxValue;
        foreach (var a in All)
        {
            if (a == null || a == this || a.dead || a.inside || a.IsCartDriver) continue;
            if (a.hp >= a.maxHp * 0.9f) continue;
            float d = Vector2.Distance(me, a.transform.position);
            if (d > radius || Vector2.Distance(a.transform.position, anchor) > leash) continue;
            float score = d + a.HpFraction * 12f - (a.IsSoldier ? 3f : 0f);
            if (score < bestScore) { bestScore = score; best = a; }
        }
        return best;
    }

    // ---------- Перевалочный пункт ----------

    // Раненый боец (меньше SeekBelow здоровья), которому никто не мешает, идёт отдыхать на ближайший пункт
    // с лечебными припасами и сидит за стенкой, пока не поправится до его предела. true - занят отдыхом.
    bool PostRestTick(GridNavigation nav)
    {
        if (InCombat) { postRest = false; return false; }

        Vector2 me = transform.position;
        FieldPost post = FieldPost.FindHealPost(me, false, 16f);
        if (post == null) { postRest = false; return false; }

        float f = HpFraction;
        if (!postRest)
        {
            if (f >= FieldPost.SeekBelow || EnemyAgent.AnyWithin(me, 5f)) return false;
            postRest = true;
        }
        if (f >= post.HealCap - 0.02f) { postRest = false; return false; }

        EnemyAgent threat = EnemyAgent.FindNearest(post.Position, 30f);
        float lateral = ((GetHashCode() & 3) - 1.5f) * 0.5f;
        Vector2 spot = post.RestSpot(me, threat != null ? (Vector2?)threat.Position : null, lateral);
        if (Vector2.Distance(me, spot) > 0.7f) mover.Step(transform, nav, spot, moveSpeed, 0.5f);
        else mover.Clear();
        return true;
    }

    // ---------- Охрана ----------

    void GuardTick(GridNavigation nav, float dt, bool home)
    {
        Vector2 me = transform.position;

        // Добычу - в казарму
        if (HasLoot && home)
        {
            if (WalkTo(nav, barracks)) EnterBuilding(barracks);
            return;
        }

        // Ранен, а врагов рядом нет - лечиться: лазарет, иначе казарма
        float restThreshold = restAfterTrip ? RestUntil : RestBelow;
        if (!recovering && HpFraction < restThreshold && !InCombat && !EnemyAgent.AnyWithin(me, 8f))
        {
            if (TryTakeBed()) return;
            if (home) recovering = true;
        }
        if (recovering && home)
        {
            if (WalkTo(nav, barracks)) EnterBuilding(barracks);
            return;
        }
        recovering = false;

        Vector2 c = barracks != null ? barracks.Center : me;
        PatrolAround(nav, dt, c, GuardRadius);
    }

    void PatrolAround(GridNavigation nav, float dt, Vector2 center, float radius)
    {
        if (patrolWait > 0f) { patrolWait -= dt; return; }

        if (!patrolHasPoint)
        {
            if (nav.TryRandomWalkable(center, radius, out Vector2Int cell))
            {
                patrolPoint = nav.CellCenter(cell) + Random.insideUnitCircle * 0.3f;
                patrolHasPoint = true;
                mover.Clear();
                mover.ResetFails();
            }
            else { patrolWait = 1f; return; }
        }

        if (mover.Step(transform, nav, patrolPoint, moveSpeed * 0.6f, 0.1f) || mover.Fails >= 3)
        {
            patrolHasPoint = false;
            patrolWait = Random.Range(1.5f, 4f);
        }
    }

    // ---------- Поход ----------

    void MoveTick(GridNavigation nav, float dt)
    {
        if (!OrderReached)
        {
            if (mover.Step(transform, nav, orderPoint, moveSpeed, 0.3f)) OrderReached = true;
            else if (mover.Fails >= 6)
            {
                // Дальше не пройти (стена) - встаём там, где смогли
                OrderReached = true;
                orderPoint = transform.position;
                mover.ResetFails();
            }
            return;
        }

        if (PatrolAtPoint && !IsCatapult)   // катапульта в лагере всегда стоит на позиции
        {
            Vector2 camp = Squad != null && Squad.HasCamp ? Squad.CampPoint : orderPoint;
            PatrolAround(nav, dt, camp, 2.8f);
        }
        else
        {
            // Стоят на своём месте (после боя возвращаются)
            if (!mover.Step(transform, nav, orderPoint, moveSpeed, 0.15f) && mover.Fails >= 4) orderPoint = transform.position;
        }
    }

    // ---------- Гонец за едой для лагеря ----------

    /// <summary>Отряд посылает бойца в Ратушу за едой. false - боец не может пойти.</summary>
    public bool StartSupply(Squad sq)
    {
        if (!IsSoldier || dead || sq == null || IsSupplying || inside || bed != null || IsOnExpedition) return false;
        if (!Usable(Hall())) return false;

        supplySquad = sq;
        supplyLoaded = false;
        supplyFood = 0;
        supplyTimer = 0f;
        target = null;
        path = null;
        pathTarget = null;
        pathFails = 0;
        mover.Clear();
        mover.ResetFails();
        return true;
    }

    /// <summary>Рейс отменён отрядом: несённая еда - обратно в казну.</summary>
    public void CancelSupply()
    {
        if (supplyFood > 0 && ResourceManager.Instance != null) ResourceManager.Instance.Add(0, 0, 0, supplyFood);
        supplyFood = 0;
        supplyLoaded = false;
        supplySquad = null;
    }

    void FailSupply()
    {
        Squad sq = supplySquad;
        CancelSupply();
        if (sq != null) sq.OnSupplyFailed();
    }

    void SupplyTick(GridNavigation nav, float dt)
    {
        supplyTimer += dt;
        Squad sq = supplySquad;

        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;
            ExitBuilding(nav);
        }

        if (!supplyLoaded)
        {
            BuildingInstance hall = Hall();
            if (!Usable(hall) || supplyTimer > 150f || pathFails >= 5) { FailSupply(); return; }
            if (!WalkTo(nav, hall)) return;

            if (HasLoot) DepositLoot();   // заодно сдаёт трофеи

            var rm = ResourceManager.Instance;
            int take = rm != null ? Mathf.Min(sq.SupplyWanted, rm.food) : 0;
            if (take <= 0 || !rm.Spend(0, 0, 0, take))
            {
                // В Ратуше еды нет - отряд об этом узнает сразу (и уйдёт домой)
                supplySquad = null;
                sq.OnSupplyDelivered(0);
                return;
            }

            supplyFood = take;
            supplyLoaded = true;
            supplyTimer = 0f;
            mover.Clear();
            mover.ResetFails();
            return;
        }

        Vector2 camp = sq.HasCamp ? sq.CampPoint : sq.Point;
        if (mover.Step(transform, nav, camp, moveSpeed, 1.2f))
        {
            int food = supplyFood;
            supplyFood = 0;
            supplyLoaded = false;
            supplySquad = null;
            sq.OnSupplyDelivered(food);
        }
        else if (mover.Fails >= 6 || supplyTimer > 150f) FailSupply();   // до лагеря не дойти - еда в казну
    }

    // ---------- Тексты ----------

    public string StatusText
    {
        get
        {
            if (dead) return "погиб";
            if (!IsSoldier) return trainingAt != null ? "идёт на обучение" : "житель";
            if (IsLookout) return LookoutStatus;
            if (IsOnExpedition) return IsAway ? "<color=#FFB060>в походе</color>" : "выступает в поход";
            if (IsLyingInBed) return "в лазарете";
            if (bed != null) return "идёт в лазарет";
            if (IsSupplying) return supplyLoaded ? "несёт еду в лагерь" : "идёт за едой в Ратушу";
            if (InCombat) return "<color=#FF8060>в бою</color>";
            if (postRest) return "отдыхает на пункте";
            if (inside && ReferenceEquals(insideRef, barracks)) return recovering ? "отдыхает в казарме" : "в казарме";
            switch (order)
            {
                case SoldierOrder.Garrison: return "идёт в казарму";
                case SoldierOrder.Guard: return recovering ? "идёт отдыхать" : (HasLoot ? "несёт добычу" : "охраняет");
                default:
                    if (!OrderReached) return "идёт в точку";
                    if (IsCatapult) return "на позиции";
                    return PatrolAtPoint ? "патруль у лагеря" : "стоит в лагере";
            }
        }
    }

    public string CombatStatsText
    {
        get
        {
            if (!IsSoldier) return "";
            string t = $"урон {Damage:F0}, дальность {AttackRange:F1}, скорость {moveSpeed:F1}";
            if (unit.IsHealer) t += $", лечение {unit.healPerSecond:F0}/с";
            if (IsCatapult) t += $", мин. дальность {unit.catapultMinRange:0}, по площади {unit.catapultRadius:0.#}, на пределе урон ×{unit.catapultFarMul:0.##}";
            if (IsFieldCannon) t += $", по зданиям ×{unit.cannonBuildingMul:F0}, осколков {unit.cannonFragments}" + (cannonLift.Has ? ", " + cannonLift.StateText : "");
            return t;
        }
    }

    public string EquipmentText
    {
        get
        {
            if (!IsSoldier) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (weapon != null) parts.Add($"{weapon.Name} ({weapon.Percent}%)");
            string sh = ShieldBearer.TextOf(this);
            if (sh != "") parts.Add(sh);
            string bd = BodyText;
            if (bd != "") parts.Add(bd);
            if (HasHorse) parts.Add("конь");
            string cg = CannonGearText;
            if (cg != "") parts.Add(cg);
            string cc = ChaosCoreText;
            if (cc != "") parts.Add(cc);
            if (hearty) parts.Add("похлёбка ★");
            return parts.Count > 0 ? string.Join(", ", parts) : "<color=#A0A0A0>без снаряжения</color>";
        }
    }
}
