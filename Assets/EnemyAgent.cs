using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Вражеский солдат (тестовый NPC). Выходит с края карты и:
///  - нападает на ближайших жителей и солдат игрока (кто на улице, солдаты - в первую очередь);
///  - если людей рядом нет - ломает ближайшее здание или полевой лагерь отряда;
///  - если до здания не пройти (обнесли стеной) - рубит стену;
///  - отвечает тому, кто его бьёт (солдату или башне);
///  - когда ломать больше нечего - уходит за край карты.
/// Разрушив здание, враги уносят его склад (Ратуша - треть казны и 10% досок/плит, см. Loot).
/// Добыча видна мешком над врагом. Убили врага - добыча у убийцы; ушёл за край карты - потеряна.
/// Удары по людям проходят через щит бойца (ShieldBearer.Mitigate), затем через его броню.
/// Бойцов в плаще из листвы замечает вдвое ближе (разведчика - лишь вплотную, дозорного - никогда): CitizenAgent.NoticeRange.
/// Паровой отбойник башни (путь Инженерии) отбрасывает врага и ненадолго оглушает (см. Knockback).
/// Обычно враг - боец войска соседа-соперника (Army): тогда цели и точки ему даёт командир (RivalAssault),
/// а сам он только дерётся рядом со своей точкой, не гоняясь за кем попало. Без командира (тестовый набег F8) -
/// старое поведение: бьёт ближайшее.
/// Создаётся через WarManager (тест) или RivalAssault.
/// </summary>
public class EnemyAgent : MonoBehaviour
{
    public static readonly List<EnemyAgent> All = new List<EnemyAgent>();

    public UnitType Unit { get; private set; }
    public float Hp { get; private set; }
    public float MaxHp { get; private set; }
    public Vector2 Position => transform.position;

    public string Name { get; private set; } = "";   // имя для карточки
    public int Wave { get; private set; }            // номер набега (с 0): от него зависят снаряжение и добыча

    public float aggroRadius = 6f;   // с какого расстояния замечает людей

    // Награбленное, которое враг несёт на себе
    public readonly Loot Carried = new Loot();
    public bool HasLoot => !Carried.IsEmpty;

    // Сколько всего может унести (штук). Начальная добыча "с собой" входит в счёт.
    // Пеший - BaseCarry, лучник поменьше, конный - больше (вьюки на коне), лекарь - мало.
    public const int BaseCarry = 120;
    public int CarryCapacity { get; set; }
    public int FreeCapacity => Mathf.Max(0, CarryCapacity - Carried.Total);
    public bool IsFull => FreeCapacity <= 5;
    SpriteRenderer sack, sackOutline;

    float baseSpeed = 3f;
    // Замедление (лук с зельем замедления) режет скорость; горящий бежит чуть быстрее - мечется
    float moveSpeed => baseSpeed * SlowEffect.Mul(this);
    bool dead;

    CitizenAgent targetUnit;
    LifeGolem targetGolem;           // голем Алтаря жизни (школа Магии жизни) - бьём, если он рядом или бьёт нас
    LifeGolem ignoreGolem;
    float ignoreGolemUntil;
    BuildingInstance targetBuilding;
    CampMarker targetCamp;           // полевой лагерь, который ломаем
    CampMarker ignoreCamp;           // до этого лагеря не дойти - какое-то время не трогаем
    float ignoreCampUntil;
    FieldPost targetPost;            // перевалочный пункт / заслон, который ломаем
    FieldPost ignorePost;            // до этого пункта не дойти - какое-то время не трогаем
    float ignorePostUntil;
    bool breach;                 // путь к зданиям закрыт - пробиваем стену
    bool leaving;
    Vector2 exitPoint;
    // Уход за край: если застрял (выход недостижим / упёрся), ищем другой выход, а в крайнем случае уходим по таймеру
    float leaveBestEdge, leaveIdle, leaveStall;
    const float LeaveEdgeDist = 1.6f;     // ближе к краю - уже ушёл (клетка у края и следующая за ней)
    const float LeaveGiveUp = 20f;        // столько секунд не приближается к краю - считаем, что ушёл
    Vector2 doorPoint;
    float doorTimer;
    float thinkTimer, attackTimer;

    CitizenAgent ignoreUnit;     // до этого человека не добраться - какое-то время не трогаем
    float ignoreUntil;

    readonly GridMover mover = new GridMover();

    // ---------- Войско соседа: приказы командира (RivalAssault) ----------
    public RivalAssault Army { get; set; }
    public EnemyRole Role { get; set; } = EnemyRole.Vanguard;
    public bool IsSpy { get; set; }                 // лазутчик: не дерётся, только смотрит
    public float LastHitAt { get; private set; } = -99f;
    public bool IsLeaving => leaving;
    public Vector2 OrderPoint => orderPoint;

    bool hasOrder;
    Vector2 orderPoint;
    float orderLeash = 6f, orderStop = 0.6f, orderSpeedCap;
    BuildingInstance orderBuilding;

    /// <summary>Идти к точке и драться только с теми, кто не дальше leash от неё.</summary>
    public void CmdMove(Vector2 p, float leash, float stop = 0.6f, float speedCap = 0f)
    {
        hasOrder = true;
        orderPoint = p;
        orderLeash = leash;
        orderStop = stop;
        orderSpeedCap = speedCap;
        orderBuilding = null;
        leaving = false;
    }

    /// <summary>Ломать здание (людей бить, только если они не дальше leash от него).</summary>
    public void CmdBuilding(BuildingInstance b, float leash)
    {
        if (!BuildingTargetable(b)) return;
        hasOrder = true;
        orderBuilding = b;
        orderPoint = b.Center;
        orderLeash = leash;
        orderStop = 0.6f;
        orderSpeedCap = 0f;
        leaving = false;
    }

    /// <summary>Уходить за край карты (ни на что не отвлекаясь).</summary>
    public void CmdLeave(Vector2 exit)
    {
        if (leaving && Vector2.Distance(exit, exitPoint) < 2f) return;
        hasOrder = false;
        orderBuilding = null;
        targetUnit = null;
        targetGolem = null;
        SetBuildingTarget(null);
        BeginLeave(exit);
    }

    void BeginLeave(Vector2 exit)
    {
        leaving = true;
        exitPoint = exit;
        leaveBestEdge = float.MaxValue;
        leaveIdle = 0f;
        leaveStall = 0f;
        mover.Clear();
        mover.ResetFails();
    }

    /// <summary>Уйти за край прямо сейчас (застрял при отходе): добыча уходит с ним.</summary>
    public void ForceEscape()
    {
        if (dead) return;
        if (Army != null) Army.OnEscaped(this);   // командир сам подведёт итог
        else if (HasLoot) WarManager.Notify($"Враг ушёл с добычей: {Carried.ShortText}", false);
        Despawn();
    }

    // ---------- Создание ----------

    public static EnemyAgent Spawn(UnitType unit, Vector2 pos, int wave = 0)
    {
        GameObject go = new GameObject("Enemy_" + unit.unitName);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        EnemyAgent e = go.AddComponent<EnemyAgent>();
        e.Init(unit, wave);
        return e;
    }

    void Init(UnitType unit, int wave)
    {
        Unit = unit;
        Wave = wave;
        Name = CitizenNames.Next();
        GiveGear(wave);
        MaxHp = Hp = Mathf.Max(1f, unit.maxHp);
        baseSpeed = unit.moveSpeed > 0f ? unit.moveSpeed * 0.9f : 2.7f; // чуть медленнее наших
        aggroRadius = Mathf.Max(aggroRadius, unit.attackRange + 1.5f);
        attackTimer = Random.Range(0f, Mathf.Max(0.1f, unit.attackCooldown));
        thinkTimer = Random.Range(0f, 0.3f);

        float s = unit.bodyScale;
        AddSquare("Outline", s + 0.14f, Color.black, 12);  // чёрная обводка - врага видно сразу
        AddSquare("Body", s, unit.color, 13);

        // Мешок с добычей (виден, только когда враг что-то несёт)
        Vector2 sackPos = new Vector2(s * 0.45f, s * 0.45f);
        sackOutline = AddSquare("SackOutline", 0.3f, Color.black, 14, sackPos);
        sack = AddSquare("Sack", 0.22f, new Color(0.9f, 0.7f, 0.2f), 15, sackPos);
        GiveStartLoot(wave);   // у каждого с собой кое-что есть - выпадет, когда его убьют
        float carryMul = unit.requiresHorse ? 1.7f : (unit.IsHealer ? 0.5f : (unit.attackRange > 2f ? 0.85f : 1f));
        CarryCapacity = Carried.Total + Mathf.RoundToInt(BaseCarry * carryMul);
        RefreshSack();
    }

    SpriteRenderer AddSquare(string partName, float size, Color c, int order, Vector2 offset = default)
    {
        GameObject go = new GameObject(partName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
        go.transform.localScale = new Vector3(size, size, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.color = c;
        sr.sortingOrder = order;
        return sr;
    }

    // ---------- Снаряжение, добыча "с собой", описание для карточки ----------

    Weapon weapon;
    Shield shield;
    public Weapon HeldWeapon { get { return weapon; } }
    public Shield HeldShield { get { return shield; } }

    public bool IsMounted { get { return Unit != null && Unit.requiresHorse; } }
    public float HpFraction { get { return MaxHp > 0f ? Hp / MaxHp : 0f; } }
    float AttackDamage { get { return Unit.damage * (weapon != null ? weapon.DamageMul : 1f); } }

    // Качество вражеского снаряжения растёт с номером набега
    static QualityTier RollQuality(int wave)
    {
        float best = Mathf.Clamp(0.02f * wave, 0f, 0.2f);
        float good = Mathf.Clamp(0.15f + 0.05f * wave, 0.15f, 0.5f);
        float r = Random.value;
        if (r < best) return QualityTier.Best;
        if (r < best + good) return QualityTier.Good;
        return QualityTier.Medium;
    }

    // Оружие и щит: не у всех, чем позже набег - тем чаще (там же настраивается шанс)
    void GiveGear(int wave)
    {
        float chance = Mathf.Clamp(0.25f + 0.08f * wave, 0.25f, 0.8f);
        if (Unit.usesWeapon && Random.value < chance) weapon = new Weapon(Unit.weaponKind, RollQuality(wave));
        if (Unit.usesShield && Random.value < chance * 0.7f) shield = new Shield(RollQuality(wave));
    }

    // Что враг несёт с собой с самого начала. Убили - достанется убийце (солдату) или сразу в казну (башня / пушка).
    // Все числа здесь - потом можно подставить свои уникальные ресурсы.
    void GiveStartLoot(int wave)
    {
        int k = 1 + wave / 3;   // с каждым третьим набегом добычи больше
        Loot l = Carried;
        switch (Unit.unitName)
        {
            case "Мечник":
                l.gold = Random.Range(8, 21) * k;
                l.stone = Random.Range(0, 9) * k;
                l.iron = Random.Range(0, 6) * k;
                break;
            case "Лучник":
                l.wood = Random.Range(6, 16) * k;
                l.food = Random.Range(5, 13) * k;
                l.gold = Random.Range(0, 11) * k;
                break;
            case "Рыцарь":
                l.gold = Random.Range(20, 46) * k;
                l.iron = Random.Range(5, 13) * k;
                if (Random.value < 0.35f) l.steel[(int)QualityTier.Medium] += 1;
                if (Random.value < 0.30f) l.plates[(int)QualityTier.Medium] += Random.Range(1, 4);
                break;
            case "Полевой маг":
                l.berries = Random.Range(6, 15) * k;
                l.oats = Random.Range(0, 9) * k;
                l.gold = Random.Range(10, 26) * k;
                break;
            case "Всадник":
                l.gold = Random.Range(25, 56) * k;
                l.oats = Random.Range(8, 19) * k;
                l.food = Random.Range(6, 15) * k;
                if (Random.value < 0.25f) l.steel[(int)QualityTier.Medium] += 1;
                break;
            case "Конный лучник":
                l.gold = Random.Range(20, 46) * k;
                l.oats = Random.Range(6, 15) * k;
                l.wood = Random.Range(6, 16) * k;
                if (Random.value < 0.25f) l.planks[(int)QualityTier.Medium] += Random.Range(1, 4);
                break;
            default:
                l.gold = Random.Range(5, 16) * k;
                break;
        }
    }

    // Щит врага режет / отражает физический удар (магию пушки не берёт)
    float MitigateByShield(float dmg, CitizenAgent byUnit, BuildingInstance byBuilding)
    {
        if (shield == null) return dmg;
        if (byBuilding != null && byBuilding.type != null && byBuilding.type.name == MagicCannonController.TypeName) return dmg;

        if (Random.value < shield.ReflectChanceVs(dmg))
        {
            Vector2 from = byUnit != null ? (Vector2)byUnit.transform.position : (byBuilding != null ? byBuilding.Center : Position);
            CombatFx.Arrow(Position, from);
            AbsorbShield();
            return 0f;
        }
        if (Random.value < shield.BlockChance)
        {
            dmg *= 1f - shield.ReduceFraction;
            AbsorbShield();
        }
        return dmg;
    }

    void AbsorbShield()
    {
        if (shield != null && shield.Wear()) shield = null;
    }

    // Убили свои - оружие и щит иногда достаются нам (на склад Оружейной, уже поношенные)
    void DropGear(CitizenAgent byUnit, BuildingInstance byBuilding)
    {
        if (byUnit == null && byBuilding == null) return;

        if (weapon != null && Random.value < 0.35f)
        {
            weapon.durability = weapon.maxDurability * Random.Range(0.4f, 0.8f);
            if (ArmoryController.Stash(weapon)) WarManager.Notify($"Трофей с врага: {weapon.Name} — на складе Оружейной", true);
            else WarManager.Notify($"Трофей ({weapon.Name}) некуда положить — в Оружейных нет места", false);
        }
        if (shield != null && Random.value < 0.35f)
        {
            shield.durability = shield.maxDurability * Random.Range(0.4f, 0.8f);
            if (ArmoryController.StashShield(shield)) WarManager.Notify($"Трофей с врага: {shield.Name} — на складе Оружейной", true);
            else WarManager.Notify($"Трофей ({shield.Name}) некуда положить — в Оружейных нет места", false);
        }
    }

    // ---------- Вражеский маг-целитель ----------
    // Лечит раненых врагов рядом (подходит на дистанцию лечения), сам себя - вдвое медленнее. Некого лечить - дерётся.

    EnemyAgent healTarget, healIgnore;
    float healScanTimer, healFxTimer, healIgnoreUntil;

    public void Heal(float amount)
    {
        if (!dead && amount > 0f) Hp = Mathf.Min(MaxHp, Hp + amount);
    }

    static bool NeedsHeal(EnemyAgent e, EnemyAgent self)
    {
        return Alive(e) && e != self && e.Hp < e.MaxHp * 0.98f;
    }

    EnemyAgent FindWounded()
    {
        EnemyAgent best = null;
        float bestScore = float.MaxValue;
        foreach (var e in All)
        {
            if (!Alive(e) || e == this) continue;
            if (e == healIgnore && Time.time < healIgnoreUntil) continue;
            if (e.Hp >= e.MaxHp * 0.9f) continue;
            float d = Vector2.Distance(Position, e.Position);
            if (d > aggroRadius + 3f) continue;
            float score = d + e.HpFraction * 12f;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    // true - маг занят лечением (боевой распорядок на этот кадр пропускаем)
    bool HealAllies(GridNavigation nav)
    {
        if (Hp < MaxHp) Heal(Unit.healPerSecond * 0.5f * Time.deltaTime);

        healScanTimer -= Time.deltaTime;
        if (healScanTimer <= 0f || !NeedsHeal(healTarget, this))
        {
            healScanTimer = 0.4f;
            healTarget = FindWounded();
        }
        if (healTarget == null) return false;

        Vector2 tp = healTarget.Position;
        if (Vector2.Distance(Position, tp) > Unit.healRange)
        {
            mover.Step(transform, nav, tp, moveSpeed, Unit.healRange * 0.75f);
            if (mover.Fails >= 4)
            {
                healIgnore = healTarget;
                healIgnoreUntil = Time.time + 6f;
                healTarget = null;
                mover.Clear();
                mover.ResetFails();
            }
            return true;
        }

        mover.Clear();
        healTarget.Heal(Unit.healPerSecond * Time.deltaTime);
        healFxTimer -= Time.deltaTime;
        if (healFxTimer <= 0f)
        {
            healFxTimer = 0.25f;
            CombatFx.HealSpark(Position, tp);
        }
        return true;
    }

    // Враг под курсором (для карточки)
    public static EnemyAgent PickAt(Vector2 p, float radius = 0.5f)
    {
        EnemyAgent best = null;
        float bestD = float.MaxValue;
        foreach (var e in All)
        {
            if (!Alive(e)) continue;
            float d = Vector2.Distance(p, e.Position);
            float r = radius + e.Unit.bodyScale * 0.3f;
            if (d <= r && d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    // ---------- Тексты для карточки (см. EnemyInfoPanel) ----------

    public string RoleText
    {
        get
        {
            if (Army != null) return $"Враг: <b>{Unit.unitName}</b>{(IsMounted ? " (верхом)" : "")}, войско {Army.RivalName} ({Army.RoleName(Role)})";
            return $"Враг: <b>{Unit.unitName}</b>{(IsMounted ? " (верхом)" : "")}, набег №{Wave + 1}";
        }
    }

    public string ActivityText
    {
        get
        {
            if (IsStunned) return "<color=#C8D0FF>оглушён ударом отбойника</color>";
            if (BurnEffect.IsBurning(this)) return "<color=#FF9040>горит!</color>";
            if (SlowEffect.IsSlowed(this)) return "<color=#80C0FF>замедлен</color> — " + (leaving ? "уходит" : "наступает");
            if (leaving) return HasLoot ? "<color=#FFB060>уходит с добычей за край карты</color>" : "уходит за край карты";
            if (IsSpy) return "<color=#FFD060>лазутчик: высматривает ваш город</color>";
            if (Unit.IsHealer && NeedsHeal(healTarget, this)) return "<color=#60FFD0>лечит союзника</color>";
            if (CitizenAgent.IsTargetable(targetUnit))
            {
                string who = targetUnit.IsSoldier ? targetUnit.DisplayName : "житель";
                return $"<color=#FF8060>атакует: {targetUnit.CitizenName} ({who})</color>";
            }
            if (LifeGolem.Targetable(targetGolem)) return "<color=#FF8060>атакует голема</color>";
            if (BuildingTargetable(targetBuilding))
            {
                string bn = targetBuilding.type != null ? targetBuilding.type.name : "здание";
                return targetBuilding.IsBarrier ? "пробивает проход: " + bn : "громит: " + bn;
            }
            if (CampMarker.Targetable(targetCamp)) return "ломает полевой лагерь";
            if (FieldPost.Targetable(targetPost)) return "ломает укрепление: " + targetPost.S.tag;
            if (Army != null) return Army.OrderText(this);
            return "ищет цель";
        }
    }

    public string CarryText
    {
        get { return HasLoot ? "<color=#FFD040>" + Carried.ShortText + "</color>" : "<color=#A0A0A0>ничего</color>"; }
    }

    public string EquipmentText
    {
        get
        {
            var parts = new List<string>();
            if (weapon != null) parts.Add($"{weapon.Name} (×{weapon.DamageMul:F2})");
            if (shield != null) parts.Add($"{shield.Name} ({shield.Percent}%)");
            if (IsMounted) parts.Add("конь");
            return parts.Count > 0 ? string.Join(", ", parts) : "<color=#A0A0A0>без снаряжения</color>";
        }
    }

    public string CombatStatsText
    {
        get
        {
            string t = $"Урон: {AttackDamage:F0}, дальность: {Unit.attackRange:F1}";
            if (Unit.IsHealer) t += $", лечение: {Unit.healPerSecond:F0}/с";
            return t;
        }
    }

    // ---------- Добыча ----------

    // Мешок тем больше, чем больше несёт
    void RefreshSack()
    {
        bool show = HasLoot;
        if (sack != null)
        {
            sack.enabled = show;
            float k = Mathf.Lerp(0.18f, 0.34f, Mathf.Clamp01(Carried.Total / 150f));
            sack.transform.localScale = new Vector3(k, k, 1f);
            if (sackOutline != null)
            {
                sackOutline.enabled = show;
                sackOutline.transform.localScale = new Vector3(k + 0.08f, k + 0.08f, 1f);
            }
        }
    }

    // Враг получил добычу (из здания или отобрал у нашего солдата)
    public void ReceiveLoot(Loot l, Vector2 from)
    {
        if (l == null || l.IsEmpty || dead) return;
        l.MoveTo(Carried);
        CombatFx.LootFly(from, Position);
        RefreshSack();
    }

    /// <summary>
    /// Здание разрушено: добычу делят враги, которые его ломали (стоят рядом), - каждый несёт свою часть.
    /// Вызывает BuildingInstance при разрушении.
    /// </summary>
    public static void ShareLoot(Loot loot, BuildingInstance b, EnemyAgent by)
    {
        if (b == null) return;
        string bname = b.type != null ? b.type.name : "здание";
        ShareLootAt(loot, b.Center, bname, by, p => b.DistanceTo(p));
    }

    /// <summary>То же для чего угодно на карте: distTo - расстояние от точки до разграбленного места.</summary>
    public static void ShareLootAt(Loot loot, Vector2 from, string placeName, EnemyAgent by, System.Func<Vector2, float> distTo)
    {
        if (loot == null || loot.IsEmpty) return;

        var raiders = new List<EnemyAgent>();
        foreach (var e in All)
        {
            if (!Alive(e)) continue;
            if (e == by || distTo(e.Position) <= e.Unit.attackRange + 1f) raiders.Add(e);
        }
        if (raiders.Count == 0) { if (Alive(by)) raiders.Add(by); else return; }

        // Каждый берёт свою долю, но не больше, чем влезет в мешок; лишнее - тем, у кого есть место
        List<Loot> shares = loot.Split(raiders.Count);
        Loot rest = new Loot();
        Loot taken = new Loot();
        for (int i = 0; i < raiders.Count; i++)
        {
            Loot part = TakeUpTo(shares[i], raiders[i].FreeCapacity);
            rest.Add(shares[i]);
            taken.Add(part);
            raiders[i].ReceiveLoot(part, from);
        }
        for (int i = 0; i < raiders.Count && !rest.IsEmpty; i++)
        {
            Loot part = TakeUpTo(rest, raiders[i].FreeCapacity);
            taken.Add(part);
            raiders[i].ReceiveLoot(part, from);
        }
        loot.Clear();

        if (!taken.IsEmpty) WarManager.Notify($"Враги разграбили «{placeName}» и уносят: {taken.ShortText}", false);
        if (!rest.IsEmpty)
        {
            // Унести всё не смогли - брошенное жители подберут (возвращается в казну)
            string left = rest.ShortText;
            rest.DepositToTown();
            WarManager.Notify($"Враги не унесли всё из «{placeName}» — брошенное вернулось в казну: {left}", true);
        }
    }

    /// <summary>
    /// Переложить из src в новый мешок не больше amount штук - сначала самое ценное
    /// (золото, сталь, плиты, доски, зелья, руда...). src уменьшается на взятое.
    /// </summary>
    public static Loot TakeUpTo(Loot src, int amount)
    {
        Loot l = new Loot();
        if (src == null || amount <= 0 || src.IsEmpty) return l;
        int left = amount;
        TakeField(ref src.gold, ref l.gold, ref left);
        for (int q = 2; q >= 0; q--) TakeField(ref src.steel[q], ref l.steel[q], ref left);
        for (int q = 2; q >= 0; q--) TakeField(ref src.reinforced[q], ref l.reinforced[q], ref left);
        for (int q = 2; q >= 0; q--) TakeField(ref src.plates[q], ref l.plates[q], ref left);
        for (int q = 2; q >= 0; q--) TakeField(ref src.planks[q], ref l.planks[q], ref left);
        TakeField(ref src.potions, ref l.potions, ref left);
        TakeField(ref src.iron, ref l.iron, ref left);
        TakeField(ref src.resin, ref l.resin, ref left);
        TakeField(ref src.herbs, ref l.herbs, ref left);
        TakeField(ref src.food, ref l.food, ref left);
        TakeField(ref src.oats, ref l.oats, ref left);
        TakeField(ref src.berries, ref l.berries, ref left);
        TakeField(ref src.stone, ref l.stone, ref left);
        TakeField(ref src.wood, ref l.wood, ref left);
        return l;
    }

    static void TakeField(ref int from, ref int to, ref int left)
    {
        int n = Mathf.Min(from, left);
        if (n <= 0) return;
        from -= n;
        to += n;
        left -= n;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Поиск ----------

    public static bool Alive(EnemyAgent e) { return e != null && !e.dead; }

    public static EnemyAgent FindNearest(Vector2 from, float radius, System.Func<EnemyAgent, bool> filter = null)
    {
        EnemyAgent best = null;
        float bestD = radius;
        foreach (var e in All)
        {
            if (!Alive(e)) continue;
            float d = Vector2.Distance(from, e.transform.position);
            if (d > bestD) continue;
            if (filter != null && !filter(e)) continue;
            bestD = d;
            best = e;
        }
        return best;
    }

    public static bool AnyWithin(Vector2 p, float radius) { return FindNearest(p, radius) != null; }

    /// <summary>Сколько на карте настоящих врагов (лазутчики не в счёт - из-за них война не начинается).</summary>
    public static int HostileCount
    {
        get
        {
            int n = 0;
            foreach (var e in All) if (Alive(e) && !e.IsSpy) n++;
            return n;
        }
    }

    static bool BuildingTargetable(BuildingInstance b)
    {
        return b != null && b.isActiveAndEnabled && b.IsDamageable && !b.IsRuined;
    }

    // ---------- Урон ----------

    public void TakeDamage(float dmg, CitizenAgent byUnit = null, BuildingInstance byBuilding = null, bool magical = false, LifeGolem byGolem = null)
    {
        if (dead || dmg <= 0f) return;
        if (!magical && Army != null) dmg *= Army.CoverAt(Position);    // за частоколом своего лагеря
        if (!magical) dmg = MitigateByShield(dmg, byUnit, byBuilding);   // магию (яд) щит не берёт
        if (dmg <= 0f) return;
        Hp -= dmg;
        LastHitAt = Time.time;
        if (Hp <= 0f) { Die(byUnit, byBuilding, byGolem); return; }

        // Бьёт голем Алтаря жизни - отвечаем ему (если не дерёмся с людьми)
        if (byGolem != null && !(Army != null && (leaving || IsSpy)) && !CitizenAgent.IsTargetable(targetUnit) && LifeGolem.Targetable(byGolem))
        {
            if (targetGolem != byGolem) { targetGolem = byGolem; mover.Clear(); mover.ResetFails(); }
            leaving = false;
            return;
        }

        // Ответка: кто бьёт - тот и цель (если сейчас не дерёмся с людьми)
        if (byUnit != null && !(Army != null && (leaving || IsSpy)) && !CitizenAgent.IsTargetable(targetUnit) && CitizenAgent.IsTargetable(byUnit))
        {
            targetUnit = byUnit;
            mover.Clear();
            mover.ResetFails();
            leaving = false;
        }
        else if (byBuilding != null && Army == null && !CitizenAgent.IsTargetable(targetUnit) && BuildingTargetable(byBuilding) && targetBuilding != byBuilding)
        {
            SetBuildingTarget(byBuilding);
            leaving = false;
        }
    }

    void Die(CitizenAgent byUnit, BuildingInstance byBuilding, LifeGolem byGolem = null)
    {
        if (dead) return;
        dead = true;
        if (Army != null) Army.OnDied(this);
        DropLoot(byUnit, byBuilding, byGolem);
        DropGear(byUnit, byGolem != null && byBuilding == null && byGolem.Altar != null ? byGolem.Altar.Building : byBuilding);
        CombatFx.Death(transform.position);
        Destroy(gameObject);
    }

    // Добыча убитого врага: солдату-убийце в руки, башне - сразу в казну (она сама здание)
    void DropLoot(CitizenAgent byUnit, BuildingInstance byBuilding, LifeGolem byGolem = null)
    {
        if (!HasLoot) return;
        string what = Carried.ShortText;

        if (byGolem != null && byUnit == null && byBuilding == null)
        {
            CombatFx.LootFly(Position, byGolem.Position);
            Carried.DepositToTown();
            WarManager.Notify($"Голем отбил добычу, возвращено в казну: {what}", true);
            RefreshSack();
            return;
        }

        if (byUnit != null && !byUnit.IsDead)
        {
            byUnit.ReceiveLoot(Carried, Position);
            WarManager.Notify($"Солдат отбил добычу: {what}. Вернёт, когда дойдёт до здания", true);
        }
        else if (byBuilding != null)
        {
            CombatFx.LootFly(Position, byBuilding.Center);
            Carried.DepositToTown();
            WarManager.Notify($"Башня отбила добычу, возвращено в казну: {what}", true);
        }
        else Carried.Clear();
        RefreshSack();
    }

    // Убрать без следа (тестовая очистка карты). Добыча пропадает вместе с ним.
    public void Despawn()
    {
        dead = true;
        Destroy(gameObject);
    }

    // ---------- Мозги ----------

    void Update()
    {
        if (dead) return;
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        // Пока думает и ходит враг, закрытые для врагов ворота для навигации - стена
        // Всадники ускоряются только на брусчатке (как наши)
        // Реку враги переходят вброд (медленно, и в воде не бьют); всадники перескакивают узкие места
        GridNavigation.HostileQuery = true;
        GridNavigation.MountedQuery = IsMounted;
        GridNavigation.WaderQuery = true;
        try { Tick(nav); }
        finally { GridNavigation.HostileQuery = false; GridNavigation.MountedQuery = false; GridNavigation.WaderQuery = false; }
    }

    void Tick(GridNavigation nav)
    {
        if (TickKnockback(nav)) return;   // летит от удара отбойника или оглушён - ни ходит, ни бьёт

        attackTimer -= Time.deltaTime;
        thinkTimer -= Time.deltaTime;
        wading = nav.IsWading(transform.position);
        if (thinkTimer <= 0f)
        {
            thinkTimer = 0.4f;
            Think(nav);
        }

        if (leaving)
        {
            TickLeave(nav);
            return;
        }

        if (Unit.IsHealer && HealAllies(nav)) return;   // маг сначала лечит своих
        if (CitizenAgent.IsTargetable(targetUnit)) FightUnit(nav);
        else if (LifeGolem.Targetable(targetGolem)) FightGolem(nav);
        else if (BuildingTargetable(targetBuilding)) FightBuilding(nav);
        else if (CampMarker.Targetable(targetCamp)) FightCamp(nav);
        else if (FieldPost.Targetable(targetPost)) FightPost(nav);
        else if (Army != null && hasOrder) MoveToOrder(nav);
    }

    // Уход за край карты. Выход войска стоит в 1.5 клетки от края, а свободная клетка у края
    // может оказаться глубже (NearestFree) - поэтому уходим и у края, и дойдя до точки выхода.
    void TickLeave(GridNavigation nav)
    {
        bool arrived = mover.Step(transform, nav, exitPoint, moveSpeed, 0.2f);
        Vector2 pos = transform.position;
        float ed = EdgeDistance(pos, nav);
        if (ed < LeaveEdgeDist || (arrived && ed < 4f)) { ForceEscape(); return; }

        // Прогресс: стали ближе к краю хоть на полклетки
        if (ed < leaveBestEdge - 0.5f) { leaveBestEdge = ed; leaveIdle = 0f; leaveStall = 0f; }
        else { leaveIdle += Time.deltaTime; leaveStall += Time.deltaTime; }

        // Пришли не туда / путь не находится / топчемся - ищем выход, до которого реально дойти
        if (arrived || mover.Fails >= 3 || leaveStall > 4f)
        {
            leaveStall = 0f;
            mover.Clear();
            mover.ResetFails();
            Vector2 alt;
            if (ReachableEdgeExit(nav, pos, out alt)) exitPoint = alt;
        }

        if (leaveIdle > LeaveGiveUp) ForceEscape();   // последний шанс: заперт или застрял - всё равно уходит
    }

    // Ближайшая (по шагам) свободная клетка у края карты, до которой можно дойти (BFS, флаги врага уже стоят)
    static bool ReachableEdgeExit(GridNavigation nav, Vector2 from, out Vector2 exit)
    {
        exit = from;
        int n = nav.mapSize, half = n / 2;
        Vector2Int start = nav.WorldToCell(from);
        if (!nav.InBounds(start)) return false;
        if (nav.IsBlocked(start)) start = nav.NearestFree(start);
        if (!nav.InBounds(start) || nav.IsBlocked(start)) return false;
        bool[] seen = new bool[n * n];
        Queue<Vector2Int> q = new Queue<Vector2Int>();
        q.Enqueue(start);
        seen[(start.x + half) * n + (start.y + half)] = true;
        while (q.Count > 0)
        {
            Vector2Int c = q.Dequeue();
            if (c.x == -half || c.x == half - 1 || c.y == -half || c.y == half - 1)
            {
                exit = nav.CellCenter(c);
                return true;
            }
            for (int d = 0; d < 4; d++)
            {
                Vector2Int nc = new Vector2Int(c.x + (d == 0 ? 1 : d == 1 ? -1 : 0), c.y + (d == 2 ? 1 : d == 3 ? -1 : 0));
                if (!nav.InBounds(nc)) continue;
                int i = (nc.x + half) * n + (nc.y + half);
                if (seen[i]) continue;
                seen[i] = true;
                if (nav.IsBlocked(nc)) continue;
                q.Enqueue(nc);
            }
        }
        return false;
    }

    // Войско: идти к точке отряда (со скоростью отряда, если задана)
    void MoveToOrder(GridNavigation nav)
    {
        Vector2 me = transform.position;
        if (Vector2.Distance(me, orderPoint) <= orderStop) { mover.Clear(); return; }
        float sp = orderSpeedCap > 0f ? Mathf.Min(moveSpeed, orderSpeedCap) : moveSpeed;
        mover.Step(transform, nav, orderPoint, sp, orderStop);
        if (mover.Fails >= 4)
        {
            mover.Clear();
            mover.ResetFails();
            if (Army != null) Army.OnBlockedMove(this);
        }
    }

    // Войско: думает командир, боец только выбирает, кого бить рядом со своей точкой
    void ArmyThink()
    {
        if (leaving) return;
        Vector2 me = transform.position;
        Vector2 anchor = hasOrder ? orderPoint : me;

        if (targetUnit != null && (!CitizenAgent.IsTargetable(targetUnit) ||
            Vector2.Distance(anchor, targetUnit.transform.position) > orderLeash + 2f || LostSight(me, targetUnit)))
            targetUnit = null;

        if (!IsSpy && orderLeash > 0.5f && !CitizenAgent.IsTargetable(targetUnit))
        {
            CitizenAgent v = FindVictimNear(me, anchor);
            if (v != null) { targetUnit = v; mover.Clear(); mover.ResetFails(); }
        }
        if (IsSpy || CitizenAgent.IsTargetable(targetUnit)) return;

        // Голем Алтаря жизни рядом с точкой отряда
        if (targetGolem != null && (!LifeGolem.Targetable(targetGolem) || Vector2.Distance(anchor, targetGolem.Position) > orderLeash + 2f))
            targetGolem = null;
        if (targetGolem == null && orderLeash > 0.5f) targetGolem = FindGolem(me, anchor, orderLeash);
        if (targetGolem != null) return;

        // Приказано ломать здание
        if (orderBuilding != null && !BuildingTargetable(orderBuilding)) orderBuilding = null;
        if (orderBuilding != null)
        {
            if (targetBuilding != orderBuilding) SetBuildingTarget(orderBuilding);
            return;
        }
        targetBuilding = null;

        // По пути - наши полевые лагеря и заслоны, если они рядом с точкой отряда
        if (!CampMarker.Targetable(targetCamp))
        {
            targetCamp = null;
            CampMarker c = PickCamp(me);
            if (c != null && Vector2.Distance(anchor, c.Position) <= orderLeash) targetCamp = c;
        }
        if (targetCamp == null && !FieldPost.Targetable(targetPost))
        {
            targetPost = null;
            FieldPost fp = FieldPost.Nearest(me, false, ignorePost, ignorePostUntil);
            if (fp != null && fp.DistanceTo(anchor) <= orderLeash * 0.7f) targetPost = fp;
        }
    }

    CitizenAgent FindVictimNear(Vector2 me, Vector2 anchor)
    {
        CitizenAgent best = null;
        float bestScore = float.MaxValue;
        foreach (var c in CitizenAgent.All)
        {
            if (!CitizenAgent.IsTargetable(c)) continue;
            if (c == ignoreUnit && Time.time < ignoreUntil) continue;
            Vector2 cp = c.transform.position;
            if (Vector2.Distance(anchor, cp) > orderLeash) continue;
            float d = Vector2.Distance(me, cp);
            float notice = c.NoticeRange(aggroRadius + 2f);   // плащ - вдвое ближе, дозорный - никогда
            if (d > notice) continue;
            if (!ResourceZones.Visible(me, cp, notice)) continue;
            float score = d;
            switch (Role)
            {
                case EnemyRole.Rider: score += c.IsSoldier ? (c.ExpRanged ? -3f : 2f) : -2.5f; break;   // конница: стрелки и жители
                case EnemyRole.Archer: score -= c.IsSoldier ? 1.5f : 0f; break;
                default: score -= c.IsSoldier ? 2f : 0f; break;
            }
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    // Стоит в реке вброд: не бьёт, пока не выберется на берег
    bool wading;

    // Цель в зоне удара, а сами в воде - выходим на берег ближе к цели. true - этим и заняты.
    bool WadeOut(GridNavigation nav, Vector2 tp)
    {
        if (!wading) return false;
        mover.Step(transform, nav, Rivers.DryPoint(tp), moveSpeed, 0.3f);
        return true;
    }

    void Think(GridNavigation nav)
    {
        if (Army != null) { ArmyThink(); return; }
        Vector2 me = transform.position;

        // 1. Люди рядом - главная цель
        if (targetUnit != null && (!CitizenAgent.IsTargetable(targetUnit) ||
            Vector2.Distance(me, targetUnit.transform.position) > aggroRadius * 1.6f || LostSight(me, targetUnit)))
            targetUnit = null;

        if (!CitizenAgent.IsTargetable(targetUnit))
        {
            CitizenAgent v = FindVictim(me);
            if (v != null) { targetUnit = v; mover.Clear(); mover.ResetFails(); }
        }
        if (CitizenAgent.IsTargetable(targetUnit)) { leaving = false; return; }

        // 1б. Голем Алтаря жизни поблизости
        if (targetGolem != null && (!LifeGolem.Targetable(targetGolem) || Vector2.Distance(me, targetGolem.Position) > aggroRadius * 1.6f))
            targetGolem = null;
        if (targetGolem == null) targetGolem = FindGolem(me, me, aggroRadius);
        if (targetGolem != null) { leaving = false; return; }

        // 2. Здания или полевые лагеря (при проломе - ближайшая стена / ворота)
        if (targetPost != null && !FieldPost.Targetable(targetPost)) targetPost = null;
        if (!BuildingTargetable(targetBuilding) && !CampMarker.Targetable(targetCamp) && targetPost == null)
        {
            bool wasBreach = breach;
            BuildingInstance b = PickBuilding(me, breach);
            breach = false; // пролом делаем один раз, потом снова идём к настоящим целям

            CampMarker c = wasBreach ? null : PickCamp(me);
            if (c != null && (b == null || Vector2.Distance(me, c.Position) < b.DistanceTo(me)))
            {
                SetBuildingTarget(null);
                targetCamp = c;
            }
            else SetBuildingTarget(b);

            // Заслон или пункт ближе всего - ломаем его
            FieldPost fp = wasBreach ? null : FieldPost.Nearest(me, false, ignorePost, ignorePostUntil);
            if (fp != null)
            {
                float dPost = fp.DistanceTo(me);
                float dOther = float.MaxValue;
                if (targetBuilding != null) dOther = targetBuilding.DistanceTo(me);
                else if (CampMarker.Targetable(targetCamp)) dOther = Vector2.Distance(me, targetCamp.Position);
                if (dPost < dOther) { SetBuildingTarget(null); targetPost = fp; }
            }
        }
        if (targetBuilding != null || CampMarker.Targetable(targetCamp) || targetPost != null) { leaving = false; return; }

        // 3. Ломать больше нечего - уходим за край карты
        if (!leaving) BeginLeave(EdgePoint(me, nav));
    }

    // Цель в плаще отошла дальше, чем её можно разглядеть (дозорного не видно вовсе) - теряем её.
    // Кто бьёт сам, плащом не скрыт (CitizenAgent.NoticeRange).
    bool LostSight(Vector2 me, CitizenAgent c)
    {
        if (c == null || !c.IsConcealed) return false;
        float keep = c.NoticeRange(aggroRadius) * 1.6f;
        return Vector2.Distance(me, c.transform.position) > keep;
    }

    CitizenAgent FindVictim(Vector2 me)
    {
        CitizenAgent best = null;
        float bestScore = float.MaxValue;
        foreach (var c in CitizenAgent.All)
        {
            if (!CitizenAgent.IsTargetable(c)) continue;
            if (c == ignoreUnit && Time.time < ignoreUntil) continue;
            float d = Vector2.Distance(me, c.transform.position);
            // в лесу обзор короче; боец в плаще заметен вдвое ближе, разведчик в плаще - лишь вплотную
            if (!ResourceZones.Visible(me, c.transform.position, c.NoticeRange(aggroRadius))) continue;
            float score = d - (c.IsSoldier ? 1.5f : 0f); // солдаты - главная угроза
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    // Голем Алтаря жизни не дальше radius от anchor и в поле зрения
    LifeGolem FindGolem(Vector2 me, Vector2 anchor, float radius)
    {
        if (IsSpy) return null;
        foreach (var g in LifeGolem.All)
        {
            if (!LifeGolem.Targetable(g)) continue;
            if (g == ignoreGolem && Time.time < ignoreGolemUntil) continue;
            if (Vector2.Distance(anchor, g.Position) > radius) continue;
            if (Vector2.Distance(me, g.Position) > aggroRadius + 2f) continue;
            if (!ResourceZones.Visible(me, g.Position, aggroRadius + 2f)) continue;
            return g;
        }
        return null;
    }

    void FightGolem(GridNavigation nav)
    {
        Vector2 tp = targetGolem.Position;
        float d = Vector2.Distance(transform.position, tp);
        float reach = Unit.attackRange + 0.3f;   // голем крупный

        if (d <= reach)
        {
            if (WadeOut(nav, tp)) return;
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = Unit.attackCooldown;
                Strike(tp);
                targetGolem.TakeDamage(AttackDamage, this);
            }
            return;
        }

        mover.Step(transform, nav, tp, moveSpeed, reach * 0.8f);
        if (mover.Fails >= 3)
        {
            ignoreGolem = targetGolem;
            ignoreGolemUntil = Time.time + 6f;
            targetGolem = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    static BuildingInstance PickBuilding(Vector2 from, bool wallsOnly)
    {
        BuildingInstance best = null;
        float bestD = float.MaxValue;
        foreach (var b in BuildingInstance.All)
        {
            if (!BuildingTargetable(b)) continue;
            if (wallsOnly != b.IsBarrier) continue; // стены и ворота ломают только при проломе
            float d = b.DistanceTo(from) + (b.IsBridge ? 6f : 0f);   // мост ломают, только если рядом нет ничего лучше
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best == null && wallsOnly) return PickBuilding(from, false);
        return best;
    }

    CampMarker PickCamp(Vector2 from)
    {
        CampMarker best = null;
        float bestD = float.MaxValue;
        foreach (var c in CampMarker.All)
        {
            if (!CampMarker.Targetable(c)) continue;
            if (c == ignoreCamp && Time.time < ignoreCampUntil) continue;
            float d = Vector2.Distance(from, c.Position);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    void SetBuildingTarget(BuildingInstance b)
    {
        targetBuilding = b;
        targetCamp = null;
        targetPost = null;
        doorTimer = 0f;
        mover.Clear();
        mover.ResetFails();
    }

    void Strike(Vector2 at)
    {
        if (Unit.attackRange > 2f) CombatFx.Arrow(transform.position, at);
        else CombatFx.Flash(at);
    }

    void FightUnit(GridNavigation nav)
    {
        Vector2 tp = targetUnit.transform.position;
        float d = Vector2.Distance(transform.position, tp);

        if (d <= Unit.attackRange)
        {
            if (WadeOut(nav, tp)) return;
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = Unit.attackCooldown;
                Strike(tp);

                // Щит бойца может срезать или полностью отразить физический удар
                float dmg = ShieldBearer.Mitigate(targetUnit, AttackDamage, this);
                if (dmg > 0f) targetUnit.TakeDamage(dmg, this);
            }
            return;
        }

        mover.Step(transform, nav, tp, moveSpeed, Unit.attackRange * 0.8f);

        // До человека не добраться (за стеной) - бросаем его на время
        if (mover.Fails >= 3)
        {
            ignoreUnit = targetUnit;
            ignoreUntil = Time.time + 6f;
            targetUnit = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    void FightBuilding(GridNavigation nav)
    {
        BuildingInstance b = targetBuilding;
        Vector2 me = transform.position;

        if (b.DistanceTo(me) <= Unit.attackRange + 0.15f)
        {
            if (!b.IsBridge && WadeOut(nav, b.ClosestPoint(me))) return;   // сваи моста рубят и из воды
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = Unit.attackCooldown;
                Strike(b.ClosestPoint(me));
                float mul = Unit.attackRange > 2f ? 0.4f : 1f; // стрелами здания ломать долго
                if (b.IsBridge) mul *= BridgeController.DamageTakenMul(b);   // поднятый раздвижной мост крепче
                b.TakeDamage(AttackDamage * mul, this);
            }
            return;
        }

        doorTimer -= Time.deltaTime;
        if (doorTimer <= 0f)
        {
            doorTimer = 2f;
            if (nav.TryGetDoorCell(b, nav.WorldToCell(me), out Vector2Int door)) doorPoint = nav.CellCenter(door);
            else
            {
                breach = true; targetBuilding = null;   // подойти некуда
                if (Army != null) { orderBuilding = null; Army.OnBlocked(this, b); }
                return;
            }
        }

        mover.Step(transform, nav, doorPoint, moveSpeed);

        // Дороги к зданию нет - значит, мешает стена: идём ломать её (войску пролом выбирает командир)
        if (mover.Fails >= 3)
        {
            breach = true;
            targetBuilding = null;
            mover.Clear();
            mover.ResetFails();
            if (Army != null) { orderBuilding = null; Army.OnBlocked(this, b); }
        }
    }

    void FightCamp(GridNavigation nav)
    {
        CampMarker c = targetCamp;
        Vector2 cp = c.Position;
        float d = Vector2.Distance(transform.position, cp);

        if (d <= Unit.attackRange + 0.6f)
        {
            if (WadeOut(nav, cp)) return;
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = Unit.attackCooldown;
                Strike(cp + Random.insideUnitCircle * 0.3f);
                float mul = Unit.attackRange > 2f ? 0.5f : 1f;
                c.TakeDamage(AttackDamage * mul, this);
            }
            return;
        }

        mover.Step(transform, nav, cp, moveSpeed, Mathf.Max(0.5f, Unit.attackRange * 0.8f));

        // До лагеря не дойти (обнесён стеной) - пробиваем стену, лагерь пока не трогаем
        if (mover.Fails >= 3)
        {
            ignoreCamp = c;
            ignoreCampUntil = Time.time + 10f;
            targetCamp = null;
            breach = true;
            mover.Clear();
            mover.ResetFails();
        }
    }

    void FightPost(GridNavigation nav)
    {
        FieldPost p = targetPost;
        Vector2 me = transform.position;

        if (p.DistanceTo(me) <= Unit.attackRange + 0.6f)
        {
            if (WadeOut(nav, p.ClosestPoint(me))) return;
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = Unit.attackCooldown;
                Strike(p.ClosestPoint(me));
                float mul = Unit.attackRange > 2f ? 0.5f : 1f;   // стрелами стенку ломать долго
                p.TakeDamage(AttackDamage * mul, this);
            }
            return;
        }

        mover.Step(transform, nav, p.ClosestPoint(me), moveSpeed, Mathf.Max(0.5f, Unit.attackRange * 0.8f));

        // Не добраться (обнесён стеной) - бросаем на время
        if (mover.Fails >= 3)
        {
            ignorePost = p;
            ignorePostUntil = Time.time + 10f;
            targetPost = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    // ---------- Отбрасывание (Паровой отбойник башни, путь Инженерии) ----------
    // Врага за KnockSeconds относит на distance клеток. Влетел в стену / здание / край карты - останавливается
    // и получает урон от удара (wallDamage). После полёта - короткое оглушение.

    const float KnockSeconds = 0.25f;
    Vector2 knockVel;
    float knockLeft;
    float stunUntil;
    float knockWallDamage;
    BuildingInstance knockBy;

    public bool IsStunned => !dead && (knockLeft > 0f || Time.time < stunUntil);

    /// <summary>
    /// Насколько этого врага относит ударом (1 - лёгкий, как лучник; меньше - тяжелее).
    /// Лучники и маги летят далеко, мечники - средне, рыцари и конница - еле сдвигаются.
    /// Щит помогает упереться.
    /// </summary>
    public float KnockFactor
    {
        get
        {
            if (Unit == null) return 1f;
            float f;
            switch (Unit.unitName)
            {
                case "Лучник": f = 1f; break;
                case "Полевой маг": f = 1.1f; break;
                case "Мечник": f = 0.7f; break;
                case "Рыцарь": f = 0.35f; break;
                case "Всадник": f = 0.2f; break;
                case "Конный лучник": f = 0.3f; break;
                default:
                    // Незнакомый вид: чем крупнее, тем тяжелее; конь - ещё тяжелее
                    f = Mathf.Clamp(0.5f / Mathf.Max(0.3f, Unit.bodyScale), 0.3f, 1.1f);
                    if (IsMounted) f *= 0.4f;
                    break;
            }
            if (shield != null) f *= 0.85f;
            return f;
        }
    }

    /// <summary>Отбросить по направлению dir на distance клеток, потом оглушить на stun секунд.</summary>
    public void Knockback(Vector2 dir, float distance, float stun, float wallDamage = 0f, BuildingInstance by = null)
    {
        if (dead) return;
        if (dir.sqrMagnitude < 0.0001f) dir = Random.insideUnitCircle;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;

        if (distance > 0.05f)
        {
            knockVel = dir.normalized * (distance / KnockSeconds);
            knockLeft = KnockSeconds;
            knockWallDamage = wallDamage;
            knockBy = by;
        }
        stunUntil = Mathf.Max(stunUntil, Time.time + (distance > 0.05f ? KnockSeconds : 0f) + Mathf.Max(0f, stun));
        mover.Clear();
        mover.ResetFails();
    }

    // true - враг летит или оглушён (весь остальной распорядок на этот кадр пропускаем)
    bool TickKnockback(GridNavigation nav)
    {
        if (knockLeft > 0f)
        {
            float dt = Mathf.Min(Time.deltaTime, knockLeft);
            knockLeft -= dt;

            Vector2 pos = transform.position;
            Vector2 next = pos + knockVel * dt;
            if (nav.IsBlocked(nav.WorldToCell(next)))
            {
                // Влетел в стену, здание или край карты
                knockLeft = 0f;
                float dmg = knockWallDamage;
                knockWallDamage = 0f;
                if (dmg > 0f)
                {
                    CombatFx.Flash(next);
                    TakeDamage(dmg, null, knockBy, true);   // удар о камень - щит не спасает
                    if (dead) return true;
                }
            }
            else transform.position = new Vector3(next.x, next.y, 0f);

            if (knockLeft <= 0f) mover.Clear();
            return true;
        }
        return Time.time < stunUntil;
    }

    // ---------- Край карты ----------

    static Vector2 EdgePoint(Vector2 p, GridNavigation nav)
    {
        float h = nav.mapSize / 2f - 0.5f;
        float dl = p.x + h, dr = h - p.x, db = p.y + h, du = h - p.y;
        float m = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, du));
        if (m == dl) return new Vector2(-h, p.y);
        if (m == dr) return new Vector2(h, p.y);
        if (m == db) return new Vector2(p.x, -h);
        return new Vector2(p.x, h);
    }

    static float EdgeDistance(Vector2 p, GridNavigation nav)
    {
        float h = nav.mapSize / 2f;
        return Mathf.Min(Mathf.Min(p.x + h, h - p.x), Mathf.Min(p.y + h, h - p.y));
    }
}