using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Боец на карте набега.
///  - Наш налётчик (Attacker): "тень" настоящего солдата из деревни (Source). Здоровье, урон, оружие,
///    щит и конь - его собственные: раны и износ остаются с ним после похода, погиб здесь - погиб и дома.
///    Несёт мешок добычи (CarryCap). Приказы: Штурм (сам ищет врагов и здания), Держать точку,
///    Ломать здание, Отступить (уйти через западный край карты домой).
///  - Защитник (бот): свои здоровье и снаряжение. Сам по себе стоит на посту и по тревоге бросается на наших
///    (далеко от поста не уходит). Но обычно им командует RaidDefense, как живой игрок: назначает точку
///    (Command), насколько от неё отходить и кого бить - контратака, оборона у Ратуши, раненых - лечиться.
///    За заслоном или стеной защитник получает меньше урона от тех, кто снаружи. Вражеский маг лечит своих.
///  - Наш боец в плаще из листвы: защитники замечают его вдвое ближе, пока он не бьёт (разведчика - лишь вплотную),
///    см. NoticeRange. Броня и плащ видны на бойце; скрытый плащом боец для игрока полупрозрачный.
///  - pushingPost: колёсная баррикада, которую толкает наш боец (FieldPost.TickRaidPusher).
/// Создаёт и удаляет RaidManager.
/// </summary>
public class RaidUnit : MonoBehaviour
{
    public static readonly List<RaidUnit> All = new List<RaidUnit>();

    public enum Order { Idle, Auto, Hold, Focus, Retreat }

    public bool Attacker { get; private set; }
    public CitizenAgent Source { get; private set; }
    public Squad Squad { get; private set; }
    public UnitType Unit { get; private set; }
    /// <summary>Голем Алтаря жизни: наш налётчик без жителя (Source == null), статы - из Unit, здоровье - своё.</summary>
    public bool IsGolem { get; private set; }
    // Наш боец-житель (статы и раны - у Source)
    bool FromSource => Attacker && !IsGolem;

    public Order Mode { get; private set; } = Order.Idle;
    public Vector2 Post { get; private set; }          // защитник: пост; налётчик: точка удержания
    public RaidBuilding Focus { get; private set; }
    public readonly Loot Carried = new Loot();
    public bool Left { get; private set; }
    public DefRole Role { get; set; } = DefRole.Guard;   // защитник: роль в обороне (см. RaidDefense)
    /// <summary>Наш налётчик толкает колёсную баррикаду (ставит FieldPost / FieldPostManager).</summary>
    public FieldPost pushingPost;

    // Приказ командира обороны: держаться у точки Post, отходить от неё не дальше cmdLeash, замечать врага за cmdLook
    bool commanded;
    float cmdLeash = 14f, cmdLook;

    bool dead;

    // Защитник
    float hp, maxHp, powerMul = 1f;
    Weapon weapon;
    Shield shield;
    bool alerted;

    // Бой
    RaidUnit target;
    RaidBuilding targetBuilding;
    RaidBuilding ignoreBuilding;
    float ignoreBuildingUntil;
    RaidUnit ignoreUnit;
    float ignoreUnitUntil;
    bool breach;
    Vector2 approach;
    float approachTimer;
    RaidUnit healTarget;
    float attackTimer, thinkTimer, healFx;
    float lastCombat = -99f;

    float patrolWait;
    Vector2 patrolPoint;
    bool hasPatrol;
    Vector2 exitPoint;

    readonly RaidMover mover = new RaidMover();
    bool restingAtPost;   // раненый отдыхает на перевалочном пункте
    SpriteRenderer body, sack;

    // ---------- Свойства ----------

    public Vector2 Position => transform.position;
    public bool Alive => !dead && !Left && (!FromSource || (Source != null && !Source.IsDead));
    public float Hp => FromSource ? (Source != null ? Source.Hp : 0f) : hp;
    public float MaxHp => FromSource ? (Source != null ? Source.MaxHp : 1f) : maxHp;
    public float HpFraction => MaxHp > 0f ? Mathf.Clamp01(Hp / MaxHp) : 0f;
    public bool InCombat => Time.time - lastCombat < 3f;
    public bool IsScout => FromSource ? (Source != null && Source.IsScout) : (Unit != null && Unit.unitName == RaidManager.ScoutUnitName);
    public bool IsMounted => FromSource ? (Source != null && Source.HasHorse) : (Unit != null && Unit.requiresHorse);
    /// <summary>Наша полевая пушка: бьёт ядрами (CannonBallistics) - что стоит на прямой, то и ломает.</summary>
    public bool IsCannon => Attacker && Source != null && Source.IsFieldCannon;
    /// <summary>Наша катапульта: камни навесом через стены, по площади (урон падает с дальностью).</summary>
    public bool IsCatapult => Attacker && Source != null && Source.IsCatapult;

    public string Name => IsGolem ? LifeAltar.GolemName + " Алтаря жизни" : Attacker ? (Source != null ? Source.DisplayName + " " + Source.CitizenName : "—") : (Unit != null ? Unit.unitName : "—");

    float Range => FromSource ? (Source != null ? Source.ExpRange : 1f) : Unit.attackRange + (weapon != null ? weapon.RangeBonus : 0f);
    float Cooldown => FromSource ? (Source != null ? Source.ExpCooldown : 1f) : Mathf.Max(0.1f, Unit.attackCooldown);
    // Замедление (лук с зельем замедления) режет скорость
    float Speed => (FromSource ? (Source != null ? Source.moveSpeed : 1.5f) : (IsGolem ? Unit.moveSpeed : Unit.moveSpeed * 0.95f)) * SlowEffect.Mul(this);
    bool Ranged => FromSource ? (Source != null && Source.ExpRanged) : Unit.attackRange > 2f;
    float HealPerSecond => FromSource ? (Source != null ? Source.ExpHealPerSecond : 0f) : Unit.healPerSecond;
    float HealRange => FromSource ? (Source != null ? Source.ExpHealRange : 0f) : Unit.healRange;
    float Aggro => Mathf.Max(6f, Range + 1.5f);

    // Сколько добычи унесёт (конь везёт вдвое больше)
    public int CarryCap => IsGolem ? 0 : (IsMounted ? RaidManager.CarryMounted : RaidManager.CarryFoot);   // голем добычу не носит
    public int CarryFree => Mathf.Max(0, CarryCap - Carried.Total);

    // Урон удара (у налётчика при этом изнашивается оружие)
    float StrikeDamage()
    {
        if (FromSource) return Source != null ? Source.ExpeditionStrike() : 0f;
        return Unit.damage * powerMul * (weapon != null ? weapon.DamageMul : 1f);
    }

    public bool IsRangedUnit => Ranged;
    public bool IsHealerUnit => HealPerSecond > 0f;
    /// <summary>Боевая ценность с учётом ран (для решений командиров).</summary>
    public float Power => (FromSource ? BotVillage.UnitPower(Source != null ? Source.ExpUnit : null) : BotVillage.UnitPower(Unit)) * Mathf.Max(0.15f, HpFraction);

    /// <summary>
    /// С какого расстояния защитники замечают этого бойца: у нашего бойца-жителя - по его плащу (CitizenAgent.NoticeRange),
    /// у остальных - baseRange.
    /// </summary>
    public float NoticeRange(float baseRange)
    {
        return FromSource && Source != null ? Source.NoticeRange(baseRange) : baseRange;
    }

    /// <summary>Скрыт плащом (или сидит в дозоре): замечают ближе обычного.</summary>
    public bool IsConcealed => FromSource && Source != null && Source.IsConcealed;

    /// <summary>Защитник (или башня) у from замечает этого нашего бойца на расстоянии look.</summary>
    public bool NoticedFrom(Vector2 from, float look)
    {
        return Vector2.Distance(from, Position) <= NoticeRange(look);
    }

    /// <summary>
    /// Ближайший живой НАШ боец, которого видно из from (плащ из листвы прячет: NoticeRange).
    /// Для башен и защитников; NearestOf(true, ...) - без учёта скрытности (проверки "свои рядом").
    /// </summary>
    public static RaidUnit NearestNoticed(Vector2 from, float radius, System.Func<RaidUnit, bool> filter = null)
    {
        return NearestOf(true, from, radius, u => u.NoticedFrom(from, radius) && (filter == null || filter(u)));
    }

    // Наш боец ударил - плащ его на время не прячет
    void MarkStrike()
    {
        if (FromSource && Source != null) Source.MarkAttacked();
    }

    public float DamagePreview => FromSource ? (Source != null ? Source.ExpDamagePreview : 0f) : Unit.damage * powerMul * (weapon != null ? weapon.DamageMul : 1f);

    // ---------- Создание ----------

    public static RaidUnit SpawnAttacker(CitizenAgent src, Squad sq, Vector2 pos)
    {
        RaidUnit u = Make("Raider_" + src.CitizenName, pos);
        u.Attacker = true;
        u.Source = src;
        u.Squad = sq;
        u.Post = pos;
        u.exitPoint = pos;
        u.BuildLook(src.ExpBodyScale, src.ExpTint, new Color(0.85f, 0.95f, 1f), src.HasHorse);
        u.BuildGearLook(src.ExpBodyScale);
        if (src.IsFieldCannon) u.BuildCannonLook(src.ExpBodyScale);
        if (src.IsCatapult) u.BuildCatapultLook(src.ExpBodyScale);
        return u;
    }

    /// <summary>Голем Алтаря жизни в набеге: наш налётчик без жителя. hp - его раны из дома.</summary>
    public static RaidUnit SpawnGolem(UnitType unit, float hp, Vector2 pos)
    {
        RaidUnit u = Make("Raider_Golem", pos);
        u.Attacker = true;
        u.IsGolem = true;
        u.Unit = unit;
        u.Post = pos;
        u.exitPoint = pos;
        u.powerMul = 1f;
        u.maxHp = Mathf.Max(1f, unit.maxHp);
        u.hp = Mathf.Clamp(hp, 1f, u.maxHp);
        u.attackTimer = Random.Range(0f, 1f);
        u.BuildLook(unit.bodyScale, unit.color, new Color(0.85f, 0.95f, 1f), false);
        u.Part("Core", Vector2.zero, new Vector2(0.24f, 0.24f), new Color(0.6f, 1f, 0.6f), 14);
        return u;
    }

    public static RaidUnit SpawnDefender(UnitType unit, Vector2 pos, float powerMul, float gearChance, int level)
    {
        RaidUnit u = Make("Defender_" + unit.unitName, pos);
        u.Attacker = false;
        u.Unit = unit;
        u.Post = pos;
        u.powerMul = powerMul;
        u.maxHp = u.hp = Mathf.Max(1f, unit.maxHp * powerMul);
        if (unit.usesWeapon && Random.value < gearChance) u.weapon = new Weapon(unit.weaponKind, RollQuality(level));
        if (unit.usesShield && Random.value < gearChance * 0.7f) u.shield = new Shield(RollQuality(level));
        u.attackTimer = Random.Range(0f, 1f);
        u.BuildLook(unit.bodyScale, unit.color, Color.black, unit.requiresHorse);
        return u;
    }

    static QualityTier RollQuality(int level)
    {
        float r = Random.value;
        if (r < 0.05f * level) return QualityTier.Best;
        if (r < 0.15f + 0.12f * level) return QualityTier.Good;
        return QualityTier.Medium;
    }

    static RaidUnit Make(string n, Vector2 pos)
    {
        GameObject go = new GameObject(n);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        return go.AddComponent<RaidUnit>();
    }

    void BuildLook(float scale, Color color, Color outline, bool horse)
    {
        scale = Mathf.Max(0.3f, scale);
        Part("Outline", Vector2.zero, new Vector2(scale + 0.12f, scale + 0.12f), outline, 12);
        body = Part("Body", Vector2.zero, new Vector2(scale, scale), color, 13);
        if (horse) Part("Horse", new Vector2(0f, -scale * 0.45f), new Vector2(scale * 1.5f, scale * 0.42f), new Color(0.45f, 0.29f, 0.14f), 11);
        sack = Part("Sack", new Vector2(scale * 0.45f, scale * 0.45f), new Vector2(0.22f, 0.22f), new Color(0.9f, 0.7f, 0.2f), 15);
        sack.enabled = false;
    }

    // ---------- Броня и плащ нашего бойца (CitizenAgent_Gear) ----------

    SpriteRenderer gearArmor, gearBelt, gearCloak, gearLeafA, gearLeafB;
    SpriteRenderer[] allParts;
    float partsAlpha = 1f;

    void BuildGearLook(float scale)
    {
        scale = Mathf.Max(0.3f, scale);
        gearArmor = Part("Armor", new Vector2(0f, scale * 0.08f), new Vector2(scale * 1.1f, scale * 0.55f), new Color(0.66f, 0.7f, 0.76f), 14);
        gearBelt = Part("ArmorBelt", new Vector2(0f, -scale * 0.22f), new Vector2(scale * 0.8f, scale * 0.12f), new Color(0.3f, 0.32f, 0.36f), 15);
        gearCloak = Part("Cloak", new Vector2(0f, scale * 0.2f), new Vector2(scale * 1.12f, scale * 0.68f), new Color(0.25f, 0.5f, 0.2f), 14);
        gearLeafA = Part("LeafA", new Vector2(-scale * 0.52f, -scale * 0.05f), new Vector2(scale * 0.26f, scale * 0.26f), new Color(0.5f, 0.8f, 0.3f), 15);
        gearLeafB = Part("LeafB", new Vector2(scale * 0.5f, scale * 0.38f), new Vector2(scale * 0.26f, scale * 0.26f), new Color(0.5f, 0.8f, 0.3f), 15);
        gearLeafA.transform.localRotation = gearLeafB.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
        UpdateGearLook();
    }

    // Броня / плащ могут порваться в бою; скрытый плащом боец для игрока полупрозрачный
    void UpdateGearLook()
    {
        if (gearArmor == null || Source == null) return;
        bool armor = Source.HasArmor, cloak = Source.HasCloak;
        gearArmor.enabled = gearBelt.enabled = armor;
        gearCloak.enabled = gearLeafA.enabled = gearLeafB.enabled = cloak;

        float alpha = Source.IsConcealed ? 0.45f : 1f;
        if (Mathf.Abs(alpha - partsAlpha) < 0.001f) return;
        partsAlpha = alpha;
        if (allParts == null) allParts = GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var r in allParts)
        {
            if (r == null) continue;
            Color c = r.color;
            c.a = r == gearCloak || r == gearLeafA || r == gearLeafB ? Mathf.Min(1f, alpha + 0.15f) : alpha;
            r.color = c;
        }
    }

    // Полевая пушка: колёса и ствол (ствол смотрит на цель, с подъёмника - длиннее)
    SpriteRenderer cannonBarrel, cannonWheels;
    Vector2 cannonAim = Vector2.right;

    void BuildCannonLook(float scale)
    {
        cannonWheels = Part("Wheels", new Vector2(0f, -scale * 0.42f), new Vector2(scale * 1.2f, scale * 0.3f), new Color(0.35f, 0.24f, 0.12f), 11);
        cannonBarrel = Part("Barrel", Vector2.zero, Vector2.one, new Color(0.12f, 0.12f, 0.14f), 14);
        UpdateCannonLook();
    }

    void UpdateCannonLook()
    {
        if (cannonBarrel == null || Source == null) return;
        float scale = Mathf.Max(0.3f, Source.ExpBodyScale);
        bool up = Source.CannonLift.Up;
        float len = scale * (up ? 1.35f : 1.05f), th = scale * (up ? 0.34f : 0.28f);
        cannonBarrel.transform.localPosition = new Vector3(cannonAim.x * len * 0.5f, cannonAim.y * len * 0.5f + (up ? scale * 0.2f : 0f), 0f);
        cannonBarrel.transform.localScale = new Vector3(len, th, 1f);
        cannonBarrel.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(cannonAim.y, cannonAim.x) * Mathf.Rad2Deg);
        if (cannonWheels != null) cannonWheels.color = up ? new Color(0.75f, 0.55f, 0.25f) : new Color(0.35f, 0.24f, 0.12f);
    }

    /// <summary>
    /// Выстрел полевой пушки по точке aim (по бойцу tu или по зданию tb). Ядро бьёт первое, что стоит на прямой:
    /// стену или заслон на пути - так пушка и пробивает дорогу. Подъёмник поднимает пушку, если прямая занята.
    /// </summary>
    void CannonFire(Vector2 aim, RaidUnit tu, RaidBuilding tb)
    {
        attackTimer = Cooldown;
        lastCombat = Time.time;
        MarkStrike();
        bool lob = Source.CannonLift.Up;
        if (!lob && Source.CannonLift.Has)
        {
            RaidBuilding block = CannonBallistics.RaidFirstBlock(Position, aim, null, tb, out _);
            if (block != null && Source.CannonLift.TryRaise())
            {
                attackTimer = 0.5f;   // поднимается - выстрел через полсекунды
                return;
            }
        }
        cannonAim = (aim - Position).sqrMagnitude > 0.001f ? (aim - Position).normalized : cannonAim;
        Source.ExpCannonAim(cannonAim);
        CannonShot s = Source.ExpCannonShot(lob);
        CannonBallistics.FireRaid(Position + cannonAim * 0.4f, aim, tu, tb, s, this, null, true);
    }

    // ---------- Катапульта ----------

    SpriteRenderer catArm, catStone;
    Vector2 catAim = Vector2.right;
    float catShotAt = -99f;
    static readonly Color CatStoneColor = new Color(0.5f, 0.48f, 0.44f);
    static readonly Color CatDustColor = new Color(0.62f, 0.55f, 0.45f, 0.6f);

    void BuildCatapultLook(float scale)
    {
        scale = Mathf.Max(0.3f, scale);
        Part("CatWheels", new Vector2(0f, -scale * 0.45f), new Vector2(scale * 1.3f, scale * 0.22f), new Color(0.22f, 0.15f, 0.08f), 11);
        Part("CatFrame", new Vector2(0f, -scale * 0.25f), new Vector2(scale * 1.2f, scale * 0.25f), new Color(0.45f, 0.3f, 0.15f), 14);
        catArm = Part("CatArm", Vector2.zero, Vector2.one, new Color(0.66f, 0.5f, 0.28f), 15);
        catStone = Part("CatStone", Vector2.zero, new Vector2(scale * 0.22f, scale * 0.22f), CatStoneColor, 16);
        UpdateCatapultLook();
    }

    void UpdateCatapultLook()
    {
        if (catArm == null || Source == null) return;
        float scale = Mathf.Max(0.3f, Source.ExpBodyScale);
        float angle = CitizenAgent.CatapultArmAngle(Time.time - catShotAt, Cooldown);
        CitizenAgent.CatapultArmPose(scale, angle, catAim.x >= 0f, out Vector3 pos, out Quaternion rot, out Vector3 size, out Vector3 tip);
        catArm.transform.localPosition = pos;
        catArm.transform.localRotation = rot;
        catArm.transform.localScale = size;
        if (catStone != null)
        {
            catStone.enabled = angle > 150f;
            catStone.transform.localPosition = tip;
        }
    }

    /// <summary>
    /// Бросок камня по точке aim (по бойцу tu или зданию tb): летит навесом через стены, бьёт по площади.
    /// Защитники в радиусе - урон (у края 60%), цель-здание - ×catapultBuildingMul, здания рядом с местом падения - половину.
    /// </summary>
    void CatapultFire(Vector2 aim, RaidUnit tu, RaidBuilding tb)
    {
        attackTimer = Cooldown;
        lastCombat = Time.time;
        MarkStrike();
        Vector2 me = Position;
        float d = Vector2.Distance(me, aim);
        if ((aim - me).sqrMagnitude > 0.001f) catAim = (aim - me).normalized;
        catShotAt = Time.time;
        Source.ExpCatapultShot(d, out float dmg, out float radius, out float bMul);
        radius = Mathf.Max(0.3f, radius);

        RaidUnit byRef = this;
        RaidBuilding tbRef = tb;
        CannonFx.Ball(me + Vector2.up * 0.25f, aim, CitizenAgent.ExpCatapultFlight(d), true, CatStoneColor, () =>
        {
            if (RaidMap.Instance == null) return;   // бой уже кончился
            RaidUnit by = byRef != null && byRef.Alive ? byRef : null;
            CannonFx.Impact(aim, ModuleKind.None);
            CombatFx.Ring(aim, radius, CatDustColor, 0.45f);
            foreach (var u in new List<RaidUnit>(All))
            {
                if (u == null || !u.Alive || u.Attacker) continue;
                float r = Vector2.Distance(u.Position, aim);
                if (r > radius + 0.3f) continue;
                // Камень падает сверху: укрытие считается от места падения
                u.TakeDamage(dmg * Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(r / radius)), by, false, aim);
            }
            foreach (var b in new List<RaidBuilding>(RaidBuilding.All))
            {
                if (b == null || b.IsRuined) continue;
                bool main = b == tbRef;
                if (!main && b.DistanceTo(aim) > radius * 0.5f) continue;
                b.TakeDamage(dmg * bMul * (main ? 1f : 0.5f), by);
            }
        });
    }

    void CatapultFightUnit(RaidMap map)
    {
        Vector2 me = Position;
        Vector2 tp = target.Position;
        float d = Vector2.Distance(me, tp);
        float range = Range;
        float minR = Source != null ? Source.CatapultMinRange : 0f;
        bool hold = Mode == Order.Hold;

        // Слишком близко: цель подальше, иначе откатываемся (держит точку - ждёт)
        if (d < minR)
        {
            lastCombat = Time.time;
            RaidUnit alt = NearestOf(false, me, range, u => Vector2.Distance(u.Position, me) >= minR);
            if (alt != null) { target = alt; return; }
            if (!hold)
            {
                Vector2 away = (me - tp).sqrMagnitude > 0.001f ? (me - tp).normalized : Vector2.left;
                mover.Step(transform, map, me + away * (minR + 0.6f - d), Speed, 0.2f);
            }
            return;
        }

        if (d <= range)
        {
            mover.Clear();
            lastCombat = Time.time;
            if (attackTimer <= 0f) CatapultFire(tp, target, null);
            return;
        }

        if (hold)
        {
            // Держит точку: за целью не едет
            ignoreUnit = target;
            ignoreUnitUntil = Time.time + 2f;
            target = null;
            return;
        }

        mover.Step(transform, map, tp, Speed, range * 0.8f);
        if (mover.Fails >= 3)
        {
            ignoreUnit = target;
            ignoreUnitUntil = Time.time + 5f;
            target = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    SpriteRenderer Part(string n, Vector2 pos, Vector2 size, Color c, int order)
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

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Приказы (налётчики) ----------

    public void OrderAuto()
    {
        if (!Attacker || Mode == Order.Retreat) return;
        Mode = Order.Auto;
        Focus = null;
        targetBuilding = null;
        breach = false;
        mover.Clear();
    }

    public void OrderHold(Vector2 point)
    {
        if (!Attacker || Mode == Order.Retreat) return;
        Mode = Order.Hold;
        Post = point + Random.insideUnitCircle * 0.8f;
        Focus = null;
        target = null;
        targetBuilding = null;
        breach = false;
        mover.Clear();
        mover.ResetFails();
    }

    public void OrderFocus(RaidBuilding b)
    {
        if (!Attacker || Mode == Order.Retreat || b == null || b.IsRuined) return;
        Mode = Order.Focus;
        Focus = b;
        target = null;
        targetBuilding = b;
        breach = false;
        mover.Clear();
        mover.ResetFails();
    }

    public void OrderRetreat(Vector2 exit)
    {
        if (!Attacker) return;
        Mode = Order.Retreat;
        pushingPost = null;   // бросает баррикаду
        exitPoint = exit;
        target = null;
        targetBuilding = null;
        mover.Clear();
        mover.ResetFails();
    }

    // Защитники поднимаются по тревоге
    public void Alert() { if (!Attacker) alerted = true; }

    /// <summary>Командир обороны: стоять у anchor, гоняться за врагом не дальше leash от неё, замечать за look.</summary>
    public void Command(Vector2 anchor, float leash, float look)
    {
        if (Attacker) return;
        if (!commanded || Vector2.Distance(anchor, Post) > 0.6f) hasPatrol = false;
        commanded = true;
        Post = anchor;
        cmdLeash = Mathf.Max(0.5f, leash);
        cmdLook = look;
    }

    /// <summary>Командир указал, кого бить.</summary>
    public void CommandTarget(RaidUnit u)
    {
        if (Attacker || !Alive2(u)) return;
        target = u;
        mover.Clear();
    }

    // ---------- Урон ----------

    public void TakeDamage(float dmg, RaidUnit by, bool magical, Vector2? shotFrom = null)
    {
        if (!Alive || dmg <= 0f) return;
        lastCombat = Time.time;

        if (IsGolem)
        {
            float d = dmg;
            if (!magical && RaidManager.Instance != null && RaidManager.Instance.InCampCover(Position)) d *= CampMarker.CoverDamage;
            if (!magical)
            {
                Vector2? src = shotFrom.HasValue ? shotFrom : (by != null ? (Vector2?)by.Position : null);
                if (src.HasValue) d *= FieldPost.CoverFactor(Position, src.Value);
            }
            if (d <= 0f) return;
            hp -= d;
            if (hp <= 0f) { hp = 0f; Die(by); return; }
            if (Mode != Order.Retreat && !Alive2(target) && Alive2(by)) target = by;
            return;
        }

        if (Attacker)
        {
            float d = ShieldBearer.Mitigate(Source, dmg, null, magical);
            if (!magical && RaidManager.Instance != null && RaidManager.Instance.InCampCover(Position)) d *= CampMarker.CoverDamage;
            if (!magical)
            {
                // Заслон / пункт режет урон, если стрелок или бьющий - по ту сторону стенки
                Vector2? src = shotFrom.HasValue ? shotFrom : (by != null ? (Vector2?)by.Position : null);
                if (src.HasValue) d *= FieldPost.CoverFactor(Position, src.Value);
            }
            if (d > 0f) Source.TakeDamage(d, null);
            if (Source == null || Source.IsDead) Die(by);
            else if (Mode != Order.Retreat && !Alive2(target) && Alive2(by)) target = by;
            return;
        }

        if (!magical)
        {
            // За заслоном или стеной - укрытие от тех, кто снаружи
            Vector2? from = shotFrom.HasValue ? shotFrom : (by != null ? (Vector2?)by.Position : null);
            if (from.HasValue) dmg *= RaidBuilding.CoverFor(Position, from.Value);
            dmg = MitigateByShield(dmg, by);
        }
        if (dmg <= 0f) return;
        hp -= dmg;
        alerted = true;
        if (RaidManager.Instance != null) RaidManager.Instance.Alarm(Position, 12f);
        if (hp <= 0f) { Die(by); return; }
        if (!Alive2(target) && Alive2(by)) target = by;
    }

    float MitigateByShield(float dmg, RaidUnit by)
    {
        if (shield == null) return dmg;
        if (Random.value < shield.ReflectChanceVs(dmg))
        {
            if (by != null) CombatFx.Arrow(Position, by.Position);
            if (shield.Wear()) shield = null;
            return 0f;
        }
        if (Random.value < shield.BlockChance)
        {
            dmg *= 1f - shield.ReduceFraction;
            if (shield.Wear()) shield = null;
        }
        return dmg;
    }

    public void Heal(float amount)
    {
        if (!Alive || amount <= 0f) return;
        if (FromSource) Source.ReceiveHeal(amount);
        else hp = Mathf.Min(maxHp, hp + amount);
    }

    void Die(RaidUnit by)
    {
        if (dead) return;
        dead = true;
        CombatFx.Death(Position);
        RaidManager rm = RaidManager.Instance;
        if (rm != null)
        {
            if (Attacker) rm.OnAttackerKilled(this);
            else rm.OnDefenderKilled(this, by, weapon, shield);
        }
        Destroy(gameObject);
    }

    // Налётчик ушёл с карты (домой)
    void Leave()
    {
        if (Left || dead) return;
        Left = true;
        if (RaidManager.Instance != null) RaidManager.Instance.OnAttackerLeft(this);
        Destroy(gameObject);
    }

    static bool Alive2(RaidUnit u) { return u != null && u.Alive; }

    static void Poison(RaidUnit u, float dps, float dur)
    {
        if (dps <= 0f || !Alive2(u)) return;
        PoisonEffect.Apply(u, dps, dur, d => { if (Alive2(u)) u.TakeDamage(d, null, true); });
    }

    static void Slow(RaidUnit u, float factor, float dur)
    {
        if (dur <= 0f || factor >= 1f || !Alive2(u)) return;
        SlowEffect.Apply(u, factor, dur);
    }

    // Поджигатель в набеге: ведро горящей смолы по защитникам
    static void FireSplash(RaidUnit by, Vector2 at, UnitType ut, float dmg)
    {
        if (ut == null) return;
        // Поджигатель - ведро смолы; Маг стихий - огненный шар (у него своё поле радиуса)
        bool fireball = ut.fireRadius <= 0f && ut.fireballRadius > 0f;
        float radius = fireball ? ut.fireballRadius : ut.fireRadius, dps = ut.burnDps * (fireball ? ManaManager.Factor : 1f), dur = ut.burnDuration;
        System.Action land = () =>
        {
            foreach (var u in new List<RaidUnit>(All))
            {
                if (u == null || u.Attacker || !u.Alive || Vector2.Distance(u.Position, at) > radius) continue;
                u.TakeDamage(dmg, by, true);
                if (!Alive2(u)) continue;
                RaidUnit victim = u;
                BurnEffect.Apply(u, dps, dur, d => { if (Alive2(victim)) victim.TakeDamage(d, null, true); });
            }
        };
        if (fireball) CombatFx.Fireball(by != null ? by.Position : at, at, radius, land);
        else CombatFx.Bucket(by != null ? by.Position : at, at, radius, land);
    }

    // ---------- Поиск ----------

    /// <summary>Ближайший живой боец стороны attackers в радиусе (для башен и ИИ).</summary>
    public static RaidUnit NearestOf(bool attackers, Vector2 from, float radius, System.Func<RaidUnit, bool> filter = null)
    {
        RaidUnit best = null;
        float bestD = radius;
        foreach (var u in All)
        {
            if (u == null || u.Attacker != attackers || !u.Alive) continue;
            float d = Vector2.Distance(from, u.Position);
            if (d > bestD) continue;
            if (filter != null && !filter(u)) continue;
            bestD = d;
            best = u;
        }
        return best;
    }

    // ---------- Мозги ----------

    void Update()
    {
        if (dead || Left) return;
        RaidMap map = RaidMap.Instance;
        RaidManager rm = RaidManager.Instance;
        if (map == null || rm == null) return;

        if (FromSource && (Source == null || Source.IsDead)) { Die(null); return; }
        if (IsGolem && !LifeAltar.RaidBound) { Die(null); return; }   // алтарь дома потерян - голем рассыпается

        float dt = Time.deltaTime;
        attackTimer -= dt;

        if (Attacker) AttackerTick(map, rm, dt);
        else DefenderTick(map, rm, dt);

        if (sack != null) sack.enabled = !Carried.IsEmpty;
        if (cannonBarrel != null) UpdateCannonLook();
        if (catArm != null) UpdateCatapultLook();
        if (gearArmor != null) UpdateGearLook();
    }

    void AttackerTick(RaidMap map, RaidManager rm, float dt)
    {
        if (Mode == Order.Retreat)
        {
            // Отступают, не ввязываясь в бой (кто не успел - того догонят)
            bool arrived = mover.Step(transform, map, exitPoint, Speed, 0.5f);
            Vector2 local = Position - RaidMap.Origin;
            if (arrived || local.x < -RaidMap.Size / 2f + 1.2f || mover.Fails >= 6) Leave();
            return;
        }

        // Толкает колёсную баррикаду (FieldPost): пока она не доехала, другим не занят
        if (pushingPost != null)
        {
            if (pushingPost.TickRaidPusher(this)) return;
            pushingPost = null;
        }

        if (!rm.BattleOn) return;   // разведка: стоят у лагеря и ждут приказа
        if (Mode == Order.Idle) Mode = Order.Auto;

        thinkTimer -= dt;
        if (thinkTimer <= 0f)
        {
            thinkTimer = 0.3f;
            AttackerThink();
        }

        if (HealPerSecond > 0f && HealAllies(map, dt)) return;
        if (Alive2(target)) { FightUnit(map); return; }
        if (Mode != Order.Auto) restingAtPost = false;
        else if (PostRestTick(map)) return;
        if (targetBuilding != null && !targetBuilding.IsRuined) { FightBuilding(map); return; }

        if (Mode == Order.Hold) mover.Step(transform, map, Post, Speed, 0.3f);
    }

    // Раненый боец на штурме (меньше SeekBelow здоровья) сам идёт отдыхать на ближайший пункт с припасами,
    // сидит за стенкой до его предела лечения, потом возвращается в бой. true - занят отдыхом.
    bool PostRestTick(RaidMap map)
    {
        if (InCombat) { restingAtPost = false; return false; }

        FieldPost post = FieldPost.FindHealPost(Position, true, 18f);
        if (post == null) { restingAtPost = false; return false; }

        float f = HpFraction;
        if (!restingAtPost)
        {
            if (f >= FieldPost.SeekBelow) return false;
            if (NearestOf(false, Position, 5f) != null) return false;
            restingAtPost = true;
        }
        if (f >= post.HealCap - 0.02f) { restingAtPost = false; return false; }

        RaidUnit threat = NearestOf(false, post.Position, 30f);
        float lateral = ((GetHashCode() & 3) - 1.5f) * 0.5f;
        Vector2 spot = post.RestSpot(Position, threat != null ? (Vector2?)threat.Position : null, lateral);
        if (Vector2.Distance(Position, spot) > 0.7f) mover.Step(transform, map, spot, Speed, 0.5f);
        else mover.Clear();
        return true;
    }

    void AttackerThink()
    {
        Vector2 me = Position;
        float aggro = Aggro;
        bool hold = Mode == Order.Hold;
        Vector2 post = Post;

        if (Alive2(target) && Vector2.Distance(target.Position, me) <= aggro * 1.6f
            && (!hold || (IsCatapult ? Vector2.Distance(target.Position, me) <= Range : Vector2.Distance(target.Position, post) <= 9f))) return;

        bool cat = IsCatapult;
        float minR = cat ? Source.CatapultMinRange : 0f;
        float range = Range;
        target = NearestOf(false, me, aggro, u =>
            (u != ignoreUnit || Time.time > ignoreUnitUntil)
            && (!cat || Vector2.Distance(u.Position, me) >= minR)
            && (!hold || (cat ? Vector2.Distance(u.Position, me) <= range : Vector2.Distance(u.Position, post) <= 9f)));
        if (target != null) return;

        switch (Mode)
        {
            case Order.Focus:
                if (Focus == null || Focus.IsRuined) { Mode = Order.Auto; Focus = null; goto case Order.Auto; }
                if (breach && (targetBuilding == null || targetBuilding.IsRuined || !targetBuilding.IsBarrier)) breach = false;
                if (!breach) targetBuilding = Focus;
                break;

            case Order.Auto:
                if (targetBuilding != null && !targetBuilding.IsRuined) break;
                if (breach)
                {
                    targetBuilding = NearestBarrier(me);
                    if (targetBuilding == null) breach = false;
                    break;
                }
                targetBuilding = PickBuilding(me);
                break;

            default:
                targetBuilding = null;
                break;
        }
    }

    RaidBuilding PickBuilding(Vector2 me)
    {
        RaidBuilding best = null;
        float bestScore = float.MaxValue;
        foreach (var b in RaidBuilding.All)
        {
            if (b == null || b.IsRuined || b.IsBarrier) continue;
            if (b == ignoreBuilding && Time.time < ignoreBuildingUntil) continue;
            float d = b.DistanceTo(me);
            float score = d;
            if (b.IsDefense && d < 10f) score -= 5f;          // башни поблизости - в первую очередь
            if ((IsCannon || IsCatapult) && b.IsDefense && d < 14f) score -= 4f;   // полевая пушка охотится за башнями и пушками
            if (!b.Storage.IsEmpty) score -= 1.5f;            // где есть чем поживиться
            if (score < bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    static RaidBuilding NearestBarrier(Vector2 me)
    {
        RaidBuilding best = null;
        float bestD = float.MaxValue;
        foreach (var b in RaidBuilding.All)
        {
            if (b == null || b.IsRuined || !b.IsBarrier) continue;
            float d = b.DistanceTo(me);
            if (b.Kind == RaidBuildingKind.Gate) d += 1.5f;   // ворота крепче стены
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    void DefenderTick(RaidMap map, RaidManager rm, float dt)
    {
        if (!rm.BattleOn)
        {
            // До боя: по приказу (тревога) - на позицию, иначе - бродят у поста
            if (commanded && Vector2.Distance(Position, Post) > 1.5f) { mover.Step(transform, map, Post, Speed, 0.3f); return; }
            Patrol(map, dt, commanded ? 1f : 1.5f);
            return;
        }

        thinkTimer -= dt;
        if (thinkTimer <= 0f)
        {
            thinkTimer = 0.35f;
            DefenderThink();
        }

        if (HealPerSecond > 0f && HealAllies(map, dt)) return;
        if (Alive2(target)) { FightUnit(map); return; }

        // Врагов рядом нет - на пост (по приказу командира - точно на точку)
        if (Vector2.Distance(Position, Post) > (commanded ? 1f : 2.5f)) mover.Step(transform, map, Post, Speed, 0.3f);
        else Patrol(map, dt, commanded ? 0.8f : 1.5f);
    }

    void DefenderThink()
    {
        Vector2 me = Position;
        float leash = commanded ? cmdLeash : 14f;
        float look = commanded && cmdLook > 0f ? cmdLook : (alerted ? 12f : 6f);

        // Цель в плаще отошла дальше, чем её можно разглядеть, - потеряли
        if (Alive2(target) && target.IsConcealed && Vector2.Distance(target.Position, me) > target.NoticeRange(look) * 1.6f) target = null;
        if (Alive2(target) && Vector2.Distance(target.Position, Post) <= leash + 2f) return;
        target = NearestNoticed(me, look, u =>
            Vector2.Distance(u.Position, Post) <= leash && (u != ignoreUnit || Time.time > ignoreUnitUntil));
        if (target != null && !alerted)
        {
            alerted = true;
            if (RaidManager.Instance != null) RaidManager.Instance.Alarm(me, 10f);
        }
    }

    void Patrol(RaidMap map, float dt, float radius)
    {
        if (patrolWait > 0f) { patrolWait -= dt; return; }
        if (!hasPatrol)
        {
            if (!map.TryRandomWalkable(Post, radius, out patrolPoint)) { patrolWait = 2f; return; }
            hasPatrol = true;
            mover.Clear();
            mover.ResetFails();
        }
        if (mover.Step(transform, map, patrolPoint, Speed * 0.5f, 0.1f) || mover.Fails >= 3)
        {
            hasPatrol = false;
            patrolWait = Random.Range(2f, 5f);
        }
    }

    // ---------- Бой ----------

    void Strike(Vector2 at)
    {
        if (Ranged) CombatFx.Arrow(Position, at);
        else CombatFx.Flash(at);
    }

    void FightUnit(RaidMap map)
    {
        if (IsCatapult) { CatapultFightUnit(map); return; }   // навесом по площади

        Vector2 me = Position;
        Vector2 tp = target.Position;
        float range = Range;

        if (Vector2.Distance(me, tp) <= range)
        {
            mover.Clear();
            lastCombat = Time.time;
            if (attackTimer <= 0f && IsCannon) { CannonFire(tp, target, null); return; }
            // Маг стихий с ядром хаоса: иногда вместо удара - чёрная дыра (ChaosMagic)
            if (attackTimer <= 0f && Attacker && Source != null && ChaosMagic.TryCastRaid(this, Source, tp)) { attackTimer = Cooldown; MarkStrike(); return; }
            if (attackTimer <= 0f)
            {
                attackTimer = Cooldown;
                MarkStrike();
                float dmg = StrikeDamage();
                float aoe = Attacker && Source != null ? Source.ExpAoeRadius : 0f;
                float pDps = Attacker && Source != null ? Source.ExpLastPoisonDps : 0f;
                float pDur = Attacker && Source != null ? Source.ExpLastPoisonDuration : 0f;
                float sF = Attacker && Source != null ? Source.ExpLastSlowFactor : 1f;
                float sDur = Attacker && Source != null ? Source.ExpLastSlowDuration : 0f;

                if (Attacker && Source != null && Source.ExpLastFire)
                {
                    FireSplash(this, tp, Source.ExpUnit, dmg);
                }
                else if (aoe > 0f)
                {
                    // Ядовитый снаряд по площади (Маг-отравитель)
                    RaidUnit by = this;
                    CombatFx.Shell(Position, tp, 0.55f, aoe, () =>
                    {
                        foreach (var u in new List<RaidUnit>(All))
                        {
                            if (u == null || u.Attacker || !u.Alive || Vector2.Distance(u.Position, tp) > aoe) continue;
                            u.TakeDamage(dmg, by, true);
                            Poison(u, pDps, pDur);
                            Slow(u, sF, sDur);
                        }
                    });
                }
                else
                {
                    Strike(tp);
                    RaidUnit t = target;
                    t.TakeDamage(dmg, this, false);
                    Poison(t, pDps, pDur);
                    Slow(t, sF, sDur);
                }
            }
            return;
        }

        mover.Step(transform, map, tp, Speed, range * 0.8f);
        if (mover.Fails >= 3)
        {
            ignoreUnit = target;
            ignoreUnitUntil = Time.time + 5f;
            target = null;
            mover.Clear();
            mover.ResetFails();
            if (Attacker && Mode == Order.Auto) breach = true;   // за стеной - надо пробивать
        }
    }

    void FightBuilding(RaidMap map)
    {
        RaidBuilding b = targetBuilding;
        Vector2 me = Position;

        if (b.DistanceTo(me) <= Range + 0.15f)
        {
            mover.Clear();
            if (attackTimer <= 0f && IsCannon) { CannonFire(b.Center, null, b); return; }
            if (attackTimer <= 0f && IsCatapult) { CatapultFire(b.Center, null, b); return; }
            if (attackTimer <= 0f)
            {
                attackTimer = Cooldown;
                MarkStrike();
                Strike(b.ClosestPoint(me));
                float mul = Ranged ? 0.4f : 1f;   // стрелами здания ломать долго
                b.TakeDamage(StrikeDamage() * mul, this);
            }
            return;
        }

        approachTimer -= Time.deltaTime;
        if (approachTimer <= 0f)
        {
            approachTimer = 1.5f;
            approach = map.ApproachPoint(b, me);
        }
        mover.Step(transform, map, approach, Speed, 0.2f);

        if (mover.Fails >= 3)
        {
            // Не подойти: за стеной - пробиваем ближайшую стену, иначе бросаем это здание на время
            mover.Clear();
            mover.ResetFails();
            if (!b.IsBarrier) breach = true;
            else { ignoreBuilding = b; ignoreBuildingUntil = Time.time + 10f; }
            if (b.IsBarrier && Mode == Order.Focus && Focus == b) { Mode = Order.Auto; Focus = null; }
            targetBuilding = breach ? NearestBarrier(me) : null;
        }
    }

    // Лечение своих (наш полевой маг / маг защитников). true - занят лечением.
    bool HealAllies(RaidMap map, float dt)
    {
        if (Hp < MaxHp) Heal(HealPerSecond * 0.5f * dt);

        if (healTarget == null || !healTarget.Alive || healTarget.Hp >= healTarget.MaxHp * 0.98f)
        {
            healTarget = null;
            float best = float.MaxValue;
            foreach (var u in All)
            {
                if (u == null || u == this || u.Attacker != Attacker || !u.Alive) continue;
                if (u.Hp >= u.MaxHp * 0.9f) continue;
                float d = Vector2.Distance(Position, u.Position);
                if (d > Aggro + 3f) continue;
                float score = d + u.HpFraction * 12f;
                if (score < best) { best = score; healTarget = u; }
            }
        }
        if (healTarget == null) return false;

        Vector2 tp = healTarget.Position;
        if (Vector2.Distance(Position, tp) > HealRange)
        {
            mover.Step(transform, map, tp, Speed, HealRange * 0.75f);
            if (mover.Fails >= 4) { healTarget = null; mover.Clear(); mover.ResetFails(); }
            return true;
        }

        mover.Clear();
        healTarget.Heal(HealPerSecond * dt);
        healFx -= dt;
        if (healFx <= 0f)
        {
            healFx = 0.25f;
            CombatFx.HealSpark(Position, tp);
        }
        return true;
    }

    // ---------- Тексты ----------

    public string StatusText
    {
        get
        {
            if (!Alive) return "выбыл";
            if (Mode == Order.Retreat) return "отступает";
            if (restingAtPost) return "отдыхает на пункте";
            if (InCombat) return "в бою";
            if (targetBuilding != null && !targetBuilding.IsRuined) return breach ? "пробивает стену" : "ломает: " + targetBuilding.Title;
            switch (Mode)
            {
                case Order.Hold: return "держит точку";
                case Order.Focus: return "идёт ломать: " + (Focus != null ? Focus.Title : "—");
                case Order.Auto: return "штурм";
                default: return "ждёт приказа";
            }
        }
    }
}
