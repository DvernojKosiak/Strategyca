using UnityEngine;
using System.Collections.Generic;

public class BuildingInstance : MonoBehaviour
{
    // Реестр всех активных зданий (Ратуша тоже здесь)
    public static readonly List<BuildingInstance> All = new List<BuildingInstance>();

    public const string RoadName = "Дорожка";
    public const string CobbleName = "Брусчатка";   // та же дорожка, но ускоряет и всадников

    public static bool IsRoadName(string n) { return n == RoadName || n == CobbleName; }
    // При дефиците ресурсов здание не встаёт, а работает на этой доле выработки (чтобы город мог выбраться из кризиса)
    public const float StarvedEfficiency = 0.2f;

    public const string WallName = "Стена";
    public const string GateName = "Ворота";
    public const string BridgeName = "Мост";              // ставится только на воду (см. Rivers, BridgeController)
    public const string GuestHouseName = "Гостевой дом";  // жильё только для наёмников (см. PopulationManager)

    // ---------- Прочность ----------
    public const float RepairDelay = 8f;        // сколько секунд без ударов, прежде чем здание начнёт чиниться
    public const float RepairSeconds = 120f;    // за сколько секунд повреждённое здание чинится с нуля до полной
    public const float RebuildSeconds = 180f;   // сколько длится восстановление из руин
    public const int RebuildWood = 10;          // символическая плата за восстановление (за каждые 4 клетки здания)
    public const int RebuildStone = 5;

    [Header("Прочность (задаётся сама по типу и уровню)")]
    public float hp = -1f;                      // -1 = ещё не задано (выставится в первом кадре)

    public bool IsRuined { get; private set; }
    public bool IsStarter;   // поставлено в начале игры (BuildingPlacer)

    /// <summary>Ратуша и стартовые лесопилка, шахта и ферма поднимаются из руин бесплатно.</summary>
    public bool FreeRebuild => type != null && (IsTownHall
        || (IsStarter && (type.name == SawmillName || type.name == MineName || type.name == FarmName)));
    public bool RebuildPaid { get; private set; }       // материалы на восстановление уже взяты
    public float RebuildProgress { get; private set; }  // 0..1

    private float lastHitTime = -999f;
    private float rebuildRetry;
    private SpriteRenderer look;

    public bool IsWall => type != null && type.name == WallName;
    public bool IsGate => type != null && type.name == GateName;
    public bool IsBarrier => IsWall || IsGate;   // преграды: враги ломают их только когда пробивают проход
    public bool IsOutpost => type != null && type.name == OutpostController.TypeName;
    public bool IsBridge => type != null && type.name == BridgeName;
    public bool IsGuestHouse => type != null && type.name == GuestHouseName;
    public bool IsDamageable => type != null && !IsRoad;   // дорожки не ломаются
    public float Hp => hp < 0f ? MaxHp : hp;
    public float MaxHp => BaseHp() * (1f + 0.5f * (level - 1)) * (fortified ? UpgradeRules.FortifyHpMul : 1f);

    // Укрепление смоляным брусом и монолитным основанием (см. UpgradeRules: «Укрепить»)
    public bool fortified = false;
    public bool fortifyInProgress = false;
    public bool UnderAttack => Time.time - lastHitTime < RepairDelay;

    // Здание вот-вот рухнет: его бьют, а прочности меньше трети. Люди из него уходят, новые не заходят.
    public const float EvacuateAt = 0.33f;
    public bool IsFalling => IsDamageable && !IsRuined && UnderAttack && Hp < MaxHp * EvacuateAt;

    float BaseHp()
    {
        if (type == null) return 100f;
        switch (type.name)
        {
            case "Ратуша": return 1500f;
            case WallName: return 400f;
            case GateName: return 500f;
            case "Башня": return 600f;
            case MagicCannonController.TypeName: return 550f;
            case GroundCannonController.TypeName: return 650f;
            case MagicArmory.TypeName: return 450f;
            case LifeAltar.TypeName: return 600f;
            case WarBuilderYard.TypeName: return 600f;
            case EngineerBarracks.TypeName: return 650f;
            case IndustrialPlant.TypeName: return 700f;
            case OutpostController.TypeName: return 650f;
            case BarracksController.TypeName: return 700f;
            case "Шахта": return 500f;
            case "Мастерская": return 450f;
            case KitchenController.TypeName: return 420f;
            case FoundryController.TypeName: return 480f;
            case EngineerYard.TypeName: return 650f;
            case MarketHouse.TypeName: return 550f;
            case "Ферма": return 300f;
            case BerryStation.TypeName: return 300f;
            case BridgeName: return 260f;
            case GuestHouseName: return 380f;
            default: return 400f;
        }
    }

    public BuildingType type;
    public int level = 1;

    // Левая нижняя клетка здания на сетке (нужна для GridNavigation)
    private Vector2Int originCell;
    public Vector2Int OriginCell
    {
        get => originCell;
        set { originCell = value; GridNavigation.MarkDirty(); Territory.MarkDirty(); }
    }

    public int storedGold;
    public int storedWood;
    public int storedStone;
    public int storedFood;
    public int storedIron;     // железная руда (шахта Ур.3)
    public int storedOats;     // овёс (ферма Ур.3)
    public int storedBerries;  // ягоды (приносят собиратели Станции собирателей)
    public int storedHerbs;    // травы (приносят собиратели Станции Ур.2+, путь Магии)
    public int storedResin;    // смола (Лесопилка Ур.3)
    public int storedLeaves;   // листва (Лесопилка Ур.4)

    // Шахта на Ур.3 начинает добывать железную руду - медленнее и камня, и золота
    public const string MineName = "Шахта";
    public const int IronFromLevel = 3;
    public const float IronPerSecond = 0.5f;     // при полной бригаде (камень на Ур.3 - 9/с, золото - 3/с)
    public bool ProducesIron => type != null && type.name == MineName && level >= IronFromLevel;

    // Лесопилка на Ур.3 начинает собирать смолу (кроме дерева) - из неё пропитка, резина, зелье замедления, ведро Поджигателя
    public const string SawmillName = "Лесопилка";
    public const int ResinFromLevel = 3;
    public const float ResinPerSecond = 0.4f;    // при полной бригаде
    public bool ProducesResin => type != null && type.name == SawmillName && level >= ResinFromLevel;
    // Лесопилка на Ур.4 ещё и собирает листву (накидки Мастера маскировки и др.)
    public const int LeavesFromLevel = 4;
    public const float LeavesPerSecond = 0.35f;  // при полной бригаде
    public bool ProducesLeaves => type != null && type.name == SawmillName && level >= LeavesFromLevel;

    // Ферма на Ур.3 начинает давать овёс (кроме еды) - им в Конюшне выращивают лошадей (см. StableController)
    public const string FarmName = "Ферма";
    public const int OatsFromLevel = 3;
    public const float OatsPerSecond = 0.5f;     // при полной бригаде
    public bool ProducesOats => type != null && type.name == FarmName && level >= OatsFromLevel;

    // Станция собирателей: ягоды сами не растут, их приносят собиратели (см. BerryStation)
    public bool ProducesBerries => type != null && type.name == BerryStation.TypeName;

    // За этим зданием уже идёт курьер (задаёт CitizenAgent при курьерском рейсе)
    public bool courierOnTheWay = false;
    // Материалы для улучшения уже несут в это здание (уровень вырастет, когда их доставят)
    public bool upgradeInProgress = false;
    // Всё, что здание производит, лежит на складе до отказа
    public bool IsStorageFull
    {
        get
        {
            if (type == null) return false;
            int cap = Capacity;
            bool produces = false;
            if (type.prodGold > 0)  { produces = true; if (storedGold < cap)  return false; }
            if (type.prodWood > 0)  { produces = true; if (storedWood < cap)  return false; }
            if (type.prodStone > 0) { produces = true; if (storedStone < cap) return false; }
            if (type.prodFood > 0)  { produces = true; if (storedFood < cap)  return false; }
            if (ProducesIron)       { produces = true; if (storedIron < cap)  return false; }
            if (ProducesOats)       { produces = true; if (storedOats < cap)  return false; }
            if (ProducesBerries)    { produces = true; if (storedBerries < cap) return false; }
            if (ProducesResin)      { produces = true; if (storedResin < cap) return false; }
            if (ProducesLeaves)     { produces = true; if (storedLeaves < cap) return false; }
            return produces;
        }
    }

    public int StoredTotal => storedGold + storedWood + storedStone + storedFood + storedIron + storedOats + storedBerries + storedHerbs + storedResin + storedLeaves;

    // Со смолой и листвой (курьеры-жители)
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f, out int iron, out int oats, out int berries, out int herbs, out int resin, out int leaves)
    {
        TakeStored(maxPerResource, out g, out w, out s, out f, out iron, out oats, out berries, out herbs, out resin);
        leaves = Mathf.Min(storedLeaves, maxPerResource); storedLeaves -= leaves;
    }

    // Со смолой (курьеры-жители)
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f, out int iron, out int oats, out int berries, out int herbs, out int resin)
    {
        TakeStored(maxPerResource, out g, out w, out s, out f, out iron, out oats, out berries, out herbs);
        resin = Mathf.Min(storedResin, maxPerResource); storedResin -= resin;
    }

    // Курьер забирает со склада не больше maxPerResource каждого ресурса за ходку (вместе с рудой, овсом и ягодами)
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f, out int iron, out int oats, out int berries)
    {
        TakeStored(maxPerResource, out g, out w, out s, out f, out iron, out oats);
        berries = Mathf.Min(storedBerries, maxPerResource); storedBerries -= berries;
    }

    // С травами
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f, out int iron, out int oats, out int berries, out int herbs)
    {
        TakeStored(maxPerResource, out g, out w, out s, out f, out iron, out oats, out berries);
        herbs = Mathf.Min(storedHerbs, maxPerResource); storedHerbs -= herbs;
    }

    // Вариант без ягод
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f, out int iron, out int oats)
    {
        TakeStored(maxPerResource, out g, out w, out s, out f);
        iron = Mathf.Min(storedIron, maxPerResource); storedIron -= iron;
        oats = Mathf.Min(storedOats, maxPerResource); storedOats -= oats;
    }

    // Старый вариант (только 4 обычных ресурса) - для CourierAgent
    public void TakeStored(int maxPerResource, out int g, out int w, out int s, out int f)
    {
        g = Mathf.Min(storedGold, maxPerResource);  storedGold -= g;
        w = Mathf.Min(storedWood, maxPerResource);  storedWood -= w;
        s = Mathf.Min(storedStone, maxPerResource); storedStone -= s;
        f = Mathf.Min(storedFood, maxPerResource);  storedFood -= f;
    }

    [Header("Рабочие (задаёт PopulationManager)")]
    public int workersNeeded = 0;     // сколько нужно для полной отдачи (0 = здание без рабочих)
    public int workers = 0;           // сколько работает сейчас
    public bool autoWorkers = true;   // true = менеджер сам подбирает людей (новые здания - тоже; раньше получали 0 рабочих)
    public int desiredWorkers = 0;    // сколько хочет игрок (в ручном режиме)

    [Header("Состояние (задаёт CityConsumption)")]
    public bool isFed = true;         // хватает ли ресурсов
    public int missingMask = 0;       // биты: 0 золото, 1 дерево, 2 камень, 3 еда
    public int ring = 0;              // номер кольца от Ратуши (0 = ближайшее)
    public float distanceToHall = 0f;

    // Есть хотя бы один рабочий (или рабочие не нужны)
    public bool isStaffed => IsTownHall || workersNeeded <= 0 || workers > 0; // Ратуша работает и без курьеров

    // Здание работает в полную силу: и кормят, и есть кому работать (от этого зависит "!" над зданием)
    public bool isWorking => isFed && isStaffed && !IsRuined && InGreenZone;

    // Здание стоит в зелёной зоне (Territory). Вне её ничего не работает: аванпост забросили -
    // постройки в его зоне встают. Ратуша, дороги, стены и ворота работают всегда.
    float zoneCheckAt = -1f;
    bool zoneCached = true;
    public bool InGreenZone
    {
        get
        {
            if (type == null || IsTownHall || IsRoad || IsBarrier || IsBridge) return true;
            if (Time.time < zoneCheckAt) return zoneCached;
            zoneCheckAt = Time.time + 0.5f;
            Territory t = Territory.Instance;
            if (t == null) { zoneCached = true; return true; }
            Vector2 c = Center;
            zoneCached = t.InZone(new Vector2Int(Mathf.FloorToInt(c.x), Mathf.FloorToInt(c.y)));
            return zoneCached;
        }
    }

    // Здание хоть что-то производит: достаточно рабочих. Без еды/дерева - на StarvedEfficiency от нормы. Руины не работают.
    public bool isProducing => isStaffed && !IsRuined && InGreenZone;

    // Множитель скорости работы с учётом дефицита (1 = нормально)
    public float SpeedMultiplier => isFed ? 1f : StarvedEfficiency;

    // Доля от полной выработки: 3 рабочих из 6 = 50%
    // Ночью и в выходные на работе остаётся только дежурная смена - здание работает на малых оборотах
    public float OffHoursFactor
    {
        get
        {
            GameClock clock = GameClock.Instance;
            if (clock == null || clock.IsWorkTime || workers <= 0) return 1f;
            return Mathf.Min(workers, clock.NightCrewFor(level)) / (float)workers;
        }
    }

    public float Efficiency => workersNeeded > 0 ? Mathf.Clamp01((float)workers / workersNeeded) * OffHoursFactor * HealthFactor : 1f;

    // Самочувствие рабочих: голодные и раненые работают хуже (среднее CitizenAgent.WorkFactor по бригаде)
    // Самочувствие бригады; магия природы (школа Магии) у рек и лесов добавляет к темпу работы
    public float HealthFactor => healthFactor * NatureMagic.WorkMul(this) * IndustrialPlant.WorkMul(this);
    float healthFactor = 1f;
    public float WorkersHealth { get; private set; } = 1f;   // среднее здоровье бригады 0..1 (для панели)
    private float healthTimer;

    void UpdateHealthFactor()
    {
        float sumF = 0f, sumH = 0f;
        int n = 0;
        foreach (var c in CitizenAgent.All)
        {
            if (c == null || c.IsDead || !ReferenceEquals(c.Workplace, this)) continue;
            sumF += c.WorkFactor;
            sumH += c.HpFraction;
            n++;
        }
        healthFactor = n > 0 ? sumF / n : 1f;
        WorkersHealth = n > 0 ? sumH / n : 1f;
    }

    // Проходимо ли здание для пеших юнитов (нужно для GridNavigation)
    public bool IsWalkable => type != null && (type.name == FarmName || IsRoadName(type.name) || type.name == BridgeName);

    // Дорожка: по ней ходят быстрее, а маршруты стараются идти вдоль неё
    public bool IsRoad => type != null && IsRoadName(type.name);
    public bool IsCobble => type != null && type.name == CobbleName;

    private float timer = 0f;
    private float accGold, accWood, accStone, accFood, accIron, accOats, accResin, accLeaves; // дробные остатки выработки

    public int Capacity => (type != null ? type.maxStorage : 100) * level;

    public bool IsTownHall => type != null && type.name == "Ратуша";
    // Аванпост ничего не потребляет (пока что)
    public bool IsConsumer => type != null && !IsTownHall && !IsRoad && !IsBarrier && type.name != "Жилой дом" && type.name != OutpostController.TypeName
                              && type.name != BridgeName && type.name != GuestHouseName;

    // Центр здания в мире (с учётом того, что BuildingPlacer двигает объект через FitToCells)
    public Vector2 Center
    {
        get
        {
            SpriteRenderer sr = GetComponentInChildren<SpriteRenderer>();
            return sr != null ? (Vector2)sr.bounds.center : (Vector2)transform.position;
        }
    }

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
        GridNavigation.MarkDirty();
        Territory.MarkDirty();
    }

    void OnDisable()
    {
        All.Remove(this);
        GridNavigation.MarkDirty();
        Territory.MarkDirty();
    }

    public void SetWorking(bool fed, int mask)
    {
        isFed = fed;
        missingMask = fed ? 0 : mask;
    }

    public void SetWorkers(int assigned, int needed)
    {
        workers = assigned;
        workersNeeded = needed;
    }

    public string StatusText
    {
        get
        {
            if (IsRuined)
            {
                return "! Руины: " + RebuildStatus;
            }
            if (isWorking) return "";
            if (!InGreenZone) return "! Вне зелёной зоны — не работает (нужна зона Ратуши или снабжённого аванпоста)";
            if (!isStaffed) return $"! Нет рабочих (нужно до {workersNeeded})";
            string pct = Mathf.RoundToInt(StarvedEfficiency * 100f) + "%";

            List<string> lack = new List<string>();
            if ((missingMask & 1) != 0) lack.Add("золота");
            if ((missingMask & 2) != 0) lack.Add("дерева");
            if ((missingMask & 4) != 0) lack.Add("камня");
            if ((missingMask & 8) != 0) lack.Add("еды");
            return "! Дефицит: не хватает " + string.Join(", ", lack) + "\nРаботает вполсилы - на " + pct;
        }
    }

    private void Update()
    {
        TickHealth(Time.deltaTime);

        healthTimer -= Time.deltaTime;
        if (healthTimer <= 0f) { healthTimer = 1f; UpdateHealthFactor(); }

        if (type == null || IsRoad || !isProducing) return; // без рабочих здание ничего не производит (при дефиците - на малых оборотах)

        timer += Time.deltaTime;
        if (timer >= 1f)
        {
            timer = 0f;
            Produce();
        }
    }

    private void Produce()
    {
        int cap = Capacity;
        if (cap <= 0) return;

        float eff = Efficiency * SpeedMultiplier;
        accGold += type.prodGold * level * eff * ResourceZones.BuildingMult(this, ZoneKind.Gold);
        accWood += type.prodWood * level * eff * ResourceZones.BuildingMult(this, ZoneKind.Forest);
        accStone += type.prodStone * level * eff * ResourceZones.BuildingMult(this, ZoneKind.Stone);
        accFood += type.prodFood * level * eff * ResourceZones.BuildingMult(this, ZoneKind.Fertile);

        int g = Mathf.FloorToInt(accGold); accGold -= g;
        int w = Mathf.FloorToInt(accWood); accWood -= w;
        int s = Mathf.FloorToInt(accStone); accStone -= s;
        int f = Mathf.FloorToInt(accFood); accFood -= f;

        if (g > 0) storedGold = Mathf.Min(cap, storedGold + g);
        if (w > 0) storedWood = Mathf.Min(cap, storedWood + w);
        if (s > 0) storedStone = Mathf.Min(cap, storedStone + s);
        if (f > 0) storedFood = Mathf.Min(cap, storedFood + f);

        if (ProducesIron)
        {
            accIron += IronPerSecond * eff * ResourceZones.BuildingMult(this, ZoneKind.Iron);
            int i = Mathf.FloorToInt(accIron); accIron -= i;
            if (i > 0) storedIron = Mathf.Min(cap, storedIron + i);
        }

        if (ProducesResin)
        {
            accResin += ResinPerSecond * eff * ResourceZones.BuildingMult(this, ZoneKind.Forest);
            int r = Mathf.FloorToInt(accResin); accResin -= r;
            if (r > 0) storedResin = Mathf.Min(cap, storedResin + r);
        }

        if (ProducesLeaves)
        {
            accLeaves += LeavesPerSecond * eff * ResourceZones.BuildingMult(this, ZoneKind.Forest);
            int lv = Mathf.FloorToInt(accLeaves); accLeaves -= lv;
            if (lv > 0) storedLeaves = Mathf.Min(cap, storedLeaves + lv);
        }

        if (ProducesOats)
        {
            accOats += OatsPerSecond * eff * ResourceZones.BuildingMult(this, ZoneKind.Fertile);
            int o = Mathf.FloorToInt(accOats); accOats -= o;
            if (o > 0) storedOats = Mathf.Min(cap, storedOats + o);
        }
    }

    // ---------- Урон, руины, восстановление ----------

    // Ближайшая к точке p точка прямоугольника здания (по клеткам)
    public Vector2 ClosestPoint(Vector2 p)
    {
        Vector2Int sz = type != null ? type.size : Vector2Int.one;
        return new Vector2(
            Mathf.Clamp(p.x, originCell.x, originCell.x + sz.x),
            Mathf.Clamp(p.y, originCell.y, originCell.y + sz.y));
    }

    public float DistanceTo(Vector2 p)
    {
        return Vector2.Distance(p, ClosestPoint(p));
    }

    // Сколько простых материалов нужно, чтобы поднять здание из руин
    public void RebuildCost(out int wood, out int stone)
    {
        if (FreeRebuild) { wood = stone = 0; return; }
        int mul = type != null ? Mathf.Max(1, (type.size.x * type.size.y) / 4) : 1;
        wood = RebuildWood * mul;
        stone = RebuildStone * mul;
    }

    // ---------- Восстановление после разгрома ----------
    // Если город разрушен целиком, дерева и камня может быть просто негде взять
    // (лесопилка и шахта сами в руинах или на них некому работать). Тогда:
    //  - ресурс, которого нет в казне и который негде добыть, прощается (берём сколько есть);
    //  - сначала поднимаются Ратуша, дома и производство, а стены, башни и т.п. ждут их.

    // Без этих зданий город не может встать на ноги
    public bool IsEssential
    {
        get
        {
            if (type == null) return false;
            if (IsTownHall || type.name == "Жилой дом") return true;
            return type.prodGold > 0 || type.prodWood > 0 || type.prodStone > 0 || type.prodFood > 0;
        }
    }

    // Есть ли в городе живое производство ресурса: не руины и есть кому работать
    static bool HasProducer(System.Func<BuildingType, int> prod)
    {
        foreach (var b in All)
        {
            if (b == null || b.type == null || b.IsRuined) continue;
            if (prod(b.type) <= 0) continue;
            if (b.workersNeeded > 0 && b.workers <= 0) continue;
            return true;
        }
        return false;
    }

    // Какие-то важные руины ещё ждут материалов
    static bool AnyEssentialWaiting()
    {
        foreach (var b in All)
            if (b != null && b.IsRuined && !b.RebuildPaid && b.IsEssential) return true;
        return false;
    }

    /// <summary>
    /// Сколько реально заплатить за восстановление. false - ждём: ресурса не хватает,
    /// но в городе его добывают (скоро появится). Если добывать негде - недостающее прощается.
    /// </summary>
    public bool RebuildPlan(out int payWood, out int payStone)
    {
        RebuildCost(out int w, out int s);
        payWood = w;
        payStone = s;

        var rm = ResourceManager.Instance;
        if (rm == null) { payWood = payStone = 0; return true; }

        bool ok = true;
        if (rm.wood < w)
        {
            if (HasProducer(t => t.prodWood)) ok = false;
            else payWood = rm.wood;
        }
        if (rm.stone < s)
        {
            if (HasProducer(t => t.prodStone)) ok = false;
            else payStone = rm.stone;
        }
        return ok;
    }

    // Что сейчас с руинами - для панели здания
    public string RebuildStatus
    {
        get
        {
            if (!IsRuined) return "";
            if (RebuildPaid) return $"восстановление {RebuildProgress * 100f:F0}%";
            if (EnemyAgent.AnyWithin(Center, 6f)) return "ждут, пока уйдут враги";
            if (FreeRebuild) return "скоро восстановят (бесплатно)";
            if (!IsEssential && AnyEssentialWaiting()) return "сначала восстановят дома и производство";
            if (!RebuildPlan(out int w, out int s))
            {
                RebuildCost(out int nw, out int ns);
                return $"ждут {nw}д и {ns}к на восстановление";
            }
            return w == 0 && s == 0 ? "скоро восстановят (даром — ресурсов нет)" : "скоро начнут восстанавливать";
        }
    }

    // by - враг, который нанёс удар (при разрушении он и его товарищи рядом уносят добычу)
    public void TakeDamage(float dmg, EnemyAgent by = null)
    {
        if (!IsDamageable || IsRuined || dmg <= 0f) return;
        if (hp < 0f) hp = MaxHp;
        hp -= dmg;
        lastHitTime = Time.time;
        if (hp <= 0f) Ruin(by);
    }

    void Ruin(EnemyAgent by)
    {
        hp = 0f;
        IsRuined = true;
        RebuildPaid = false;
        RebuildProgress = 0f;
        rebuildRetry = 0f;

        // Склад здания разграблен: враги уносят его с собой (см. Loot).
        // Ратуша - ещё и треть казны и 10% досок/плит.
        if (EnemyAgent.Alive(by))
        {
            Loot loot = Loot.FromBuildingStorage(this);
            if (IsTownHall) loot.Add(Loot.RaidTreasury());
            WarehouseController warehouse = GetComponent<WarehouseController>();
            if (warehouse != null) loot.Add(warehouse.Raid());   // Хранилище: враги уносят часть содержимого
            EnemyAgent.ShareLoot(loot, this, by);
        }
        storedGold = storedWood = storedStone = storedFood = storedIron = storedOats = storedBerries = storedHerbs = storedResin = storedLeaves = 0;
        accGold = accWood = accStone = accFood = accIron = accOats = accResin = accLeaves = 0f;
        SetWorking(true, 0);
        ApplyLook();
        if (IsBarrier || IsBridge) GridNavigation.MarkDirty(); // разрушенная стена / ворота - теперь проход; разрушенный мост - снова река
        if (IsOutpost) Territory.MarkDirty();      // разрушенный аванпост не расширяет зону
    }

    void TickHealth(float dt)
    {
        if (!IsDamageable) return;
        if (hp < 0f) hp = MaxHp;

        if (IsRuined)
        {
            // Пока рядом враги - не восстанавливаемся
            if (!RebuildPaid)
            {
                rebuildRetry -= dt;
                if (rebuildRetry > 0f) return;
                rebuildRetry = 2f;
                if (EnemyAgent.AnyWithin(Center, 6f)) return;

                // Ратуша и стартовые постройки - бесплатно и без очереди
                if (FreeRebuild) { RebuildPaid = true; return; }

                // Стены, башни и т.п. ждут, пока не начнут восстанавливать дома и производство
                if (!IsEssential && AnyEssentialWaiting()) return;

                // Нет материалов - ждём, пока появятся. Если взять их негде - восстанавливаем даром.
                if (!RebuildPlan(out int w, out int s)) return;
                var rm = ResourceManager.Instance;
                if (rm == null || rm.Spend(0, w, s, 0)) RebuildPaid = true;
                return;
            }

            if (EnemyAgent.AnyWithin(Center, 6f)) return;
            RebuildProgress = Mathf.Min(1f, RebuildProgress + dt / RebuildSeconds);
            hp = MaxHp * RebuildProgress;
            if (RebuildProgress >= 1f)
            {
                IsRuined = false;
                RebuildPaid = false;
                RebuildProgress = 0f;
                hp = MaxHp;
                if (IsBarrier || IsBridge) GridNavigation.MarkDirty(); // стену / ворота отстроили - прохода снова нет; мост - снова переправа
                if (IsOutpost) Territory.MarkDirty();      // аванпост снова расширяет зону
            }
            ApplyLook();
            return;
        }

        // Полуразрушенное здание потихоньку чинится само, если его давно не били
        if (hp < MaxHp && !UnderAttack) hp = Mathf.Min(MaxHp, hp + MaxHp / RepairSeconds * dt);
        if (hp > MaxHp) hp = MaxHp;
    }

    // Руины - тёмные и полупрозрачные, при восстановлении постепенно светлеют
    void ApplyLook()
    {
        if (type == null) return;
        if (look == null) look = GetComponentInChildren<SpriteRenderer>();
        if (look == null) return;

        if (!IsRuined) { look.color = type.color; return; }

        Color ruin = new Color(type.color.r * 0.3f, type.color.g * 0.3f, type.color.b * 0.3f, 0.85f);
        look.color = Color.Lerp(ruin, type.color, RebuildProgress * 0.7f);
    }

    public void CollectToTreasury()
    {
        if (ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(storedGold, storedWood, storedStone, storedFood, storedIron, storedOats, storedBerries, storedHerbs, 0, storedResin);
            storedHerbs = 0;
            storedResin = 0;
            if (storedLeaves > 0) ResourceManager.Instance.AddLeaves(storedLeaves);
            storedLeaves = 0;
            storedIron = 0;
            storedOats = 0;
            storedBerries = 0;
            storedGold = 0;
            storedWood = 0;
            storedStone = 0;
            storedFood = 0;
        }
    }

    // Улучшение: обычные ресурсы + доски/плиты (см. UpgradeRules)
    public bool TryUpgrade()
    {
        return UpgradeRules.TryUpgrade(this);
    }
}
