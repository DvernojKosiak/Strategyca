using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class UnitType
{
    public string unitName = "Воин";
    public int costGold = 20;
    public int costFood = 15;
    public float trainTime = 5f; // Время тренировки в секундах
    public int spaceOccupied = 1; // Сколько мест занимает юнит в казарме

    [Header("Внешний вид солдата на карте")]
    public Color color = new Color(0.9f, 0.2f, 0.2f);
    public float bodyScale = 0.5f; // размер квадратика (у жителя 0.5)

    [Header("Бой")]
    public float maxHp = 100f;
    public float damage = 12f;
    public float attackRange = 1.1f;    // больше 2 - стреляет издалека (стрелы)
    public float attackCooldown = 1f;   // секунд между ударами
    public float moveSpeed = 3f;

    [Header("Доступность и лечение")]
    public int minBarracksLevel = 1;    // с какого уровня казармы можно обучать
    public float healPerSecond = 0f;    // > 0 - лекарь: в бою лечит своих (см. CitizenAgent, "Полевой маг")
    public float healRange = 0f;        // с какого расстояния лечит

    [Header("Снаряжение")]
    public bool usesWeapon = false;                 // бойцу можно выдать оружие из Оружейной
    public WeaponKind weaponKind = WeaponKind.Sword; // какое: меч или лук
    public bool usesShield = false;                 // бойцу можно выдать щит из Оружейной (ближний бой)
    public bool requiresHorse = false;              // конница: при найме нужна лошадь из Конюшни

    [Header("Похлёбка с Кухни")]
    public int pottageCost = 0;             // порций на бойца; 0 - похлёбку этому виду не дают
    public bool requiresPottage = false;    // без похлёбки не обучить вовсе (для будущих видов войск)
    public float pottageHpMul = 1.35f;      // усиленный похлёбкой боец (★): здоровье ...
    public float pottageDamageMul = 1.25f;  // ... урон ...
    public float pottageSpeedMul = 1.1f;    // ... и скорость

    [Header("Магия (путь развития)")]
    public PathKind path = PathKind.None;   // вид войск только для этого пути (None - для всех)
    public float aoeRadius = 0f;            // > 0 - снаряд бьёт по площади
    public float poisonDps = 0f;            // > 0 - удар отравляет (урон ядом в секунду, × сила маны)
    public float poisonDuration = 0f;
    public int potionCost = 0;              // зелий отравления на бойца (из Зельеварни)

    [Header("Горящая смола (путь Военного дела)")]
    public float fireRadius = 0f;           // > 0 - первый удар: ведро горящей смолы по площади (Поджигатель)
    public float fireDamage = 0f;           // урон всем, кто в огне в момент разлива
    public float burnDps = 0f;              // потом ещё горят: урон в секунду ...
    public float burnDuration = 0f;         // ... столько секунд
    public float fistDamage = 0f;           // ведро пустое - бьёт кулаками
    public int resinCost = 0;               // смолы на бойца при найме (из Ратуши)
    public bool IsFireBomber => fireRadius > 0f;

    [Header("Маг стихий (путь Магии)")]
    public float fireballChance = 0f;       // > 0 - иногда вместо снаряда пускает огненный шар (по площади, поджигает: burnDps)
    public float fireballRadius = 0f;
    public float fireballDamage = 0f;
    public float floodChance = 0f;          // > 0 - рядом с рекой иногда выводит её из берегов (волна по врагам)
    public float floodWaterRange = 4f;      // насколько близко к воде должен стоять маг
    public float floodRadius = 0f;
    public float floodDamage = 0f;
    public float floodSlow = 1f;            // замедление после волны (0.35 = почти втрое медленнее)
    public float floodSeconds = 0f;
    public float floodCooldown = 15f;
    public bool IsElementalist => fireballChance > 0f || floodChance > 0f;

    [Header("Полевая пушка (путь Военного дела)")]
    public bool isCannon = false;           // бьёт ядрами прямой наводкой (CannonBallistics), осколки
    public int cannonFragments = 0;
    public float cannonFragDamage = 0f;
    public float cannonFragRange = 2f;
    public float cannonBuildingMul = 1f;    // во сколько раз сильнее по зданиям
    public float cannonBarrierMul = 1f;     // ... и ещё по стенам, воротам, заслонам
    public int steelCost = 0;               // стали на бойца при найме (из Ратуши / Хранилищ)
    public int planksCost = 0;              // досок на бойца при найме
    public bool IsFieldCannon => isCannon;

    [Header("Катапульта (Инженерия: Военный инженер)")]
    public EngSchool engSchool = EngSchool.None;   // вид войск только для этого направления инженеров
    public bool isCatapult = false;         // бьёт камнями навесом по площади (через стены), урон падает с расстоянием
    public float catapultMinRange = 0f;     // ближе не стреляет
    public float catapultFarMul = 1f;       // урон на пределе дальности (× от полного)
    public float catapultRadius = 0f;       // радиус удара камня
    public float catapultBuildingMul = 1f;  // по зданиям (в набеге) сильнее
    public int partsCost = 0;               // деталей на бойца при найме (из Литейной)
    public bool IsCatapult => isCatapult;

    public bool IsHealer => healPerSecond > 0f;
    public bool IsPoisoner => poisonDps > 0f;
    public bool UsesPottage => pottageCost > 0 || requiresPottage;
    public int PottagePortions => Mathf.Max(1, pottageCost);
}

/// <summary>
/// Казарма. Обучение физическое:
///  1. Из свободных жителей выбирается новобранец, идёт к казарме и заходит внутрь.
///  2. Пока он внутри, идёт обучение.
///  3. Обучение закончилось - житель выходит уже солдатом (другой цвет/размер) и принадлежит этой казарме.
/// Приказы солдатам: сидеть внутри / охранять казарму / идти в точку на карте. Можно уволить со службы.
/// Снаряжение: оружие и щиты выдаются из Оружейной (кнопка "Снаряж." в списке солдат).
/// Ур.3 (нужна сталь): мест больше, и можно обучать полевого мага-целителя, который лечит своих в бою.
/// Ур.4: конница (нужны лошади из Конюшни).
/// Похлёбка (галочка "С похлёбкой"): всадники выходят усиленными (★). Порции берутся с Кухни:
///  - Кухня стоит вплотную к этой казарме или к группе казарм, стоящих вплотную друг к другу, -
///    похлёбка поступает сразу, без курьеров;
///  - иначе порцию несёт житель, и обучение начнётся, когда её принесут.
/// Для видов войск с requiresPottage похлёбка обязательна.
/// </summary>
public class BarracksController : MonoBehaviour
{
    public const string TypeName = "Казарма";

    private BuildingInstance buildingInstance;
    BuildingInstance Bld
    {
        get
        {
            if (buildingInstance == null) buildingInstance = GetComponent<BuildingInstance>();
            return buildingInstance;
        }
    }

    [Header("Настройки вместимости")]
    public int maxGarrisonSpace = 10; // Вместимость казармы на 1 уровне (10 мест)

    // Реальная вместимость: 10 мест за каждый уровень казармы
    public int MaxSpace
    {
        get
        {
            if (Bld == null) return maxGarrisonSpace;
            return maxGarrisonSpace * Mathf.Max(1, Bld.level);
        }
    }

    [Header("Охрана")]
    public float guardRadius = 3.5f;  // в каком радиусе от казармы ходят охранники

    [Header("Новобранец")]
    public float recruitTimeout = 25f; // если новобранец не дошёл за это время - заходит принудительно

    [Header("Лагерь и снабжение")]
    public int campWoodCost = 5;       // дерево из казны на баррикаду лагеря
    public int foodPerSoldier = 6;     // сколько еды берёт с собой каждый боец
    public float eatInterval = 60f;    // раз в сколько секунд каждый боец съедает 1 еду
    public int lowFoodMeals = 2;       // когда еды меньше, чем на столько "обедов" отряда - посылаем гонца
    public float starveGrace = 20f;    // сколько секунд отряд терпит голод без помощи, потом уходит в казарму

    [Header("Доступные для найма юниты")]
    public List<UnitType> availableUnits = new List<UnitType>
    {
        new UnitType { unitName = "Мечник", costGold = 20, costFood = 15, trainTime = 3f, spaceOccupied = 1, color = new Color(0.9f, 0.15f, 0.2f),  bodyScale = 0.55f,
                       maxHp = 100f, damage = 12f, attackRange = 1.1f, attackCooldown = 1f,   moveSpeed = 1.6f,
                       usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
        new UnitType { unitName = "Лучник", costGold = 35, costFood = 20, trainTime = 5f, spaceOccupied = 1, color = new Color(0.25f, 0.85f, 0.3f), bodyScale = 0.5f,
                       maxHp = 70f,  damage = 9f,  attackRange = 6f,   attackCooldown = 1.4f, moveSpeed = 1.5f,
                       usesWeapon = true, weaponKind = WeaponKind.Bow },
        new UnitType { unitName = "Рыцарь", costGold = 80, costFood = 40, trainTime = 8f, spaceOccupied = 3, color = new Color(0.92f, 0.92f, 1f),   bodyScale = 0.75f,
                       maxHp = 240f, damage = 24f, attackRange = 1.2f, attackCooldown = 1.3f, moveSpeed = 1.3f,
                       usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
        // Полевой маг-целитель: слабо бьёт издалека, зато лечит раненых бойцов рядом прямо в бою
        new UnitType { unitName = "Полевой маг", costGold = 90, costFood = 40, trainTime = 10f, spaceOccupied = 2, color = new Color(0.3f, 0.9f, 0.95f), bodyScale = 0.5f,
                       maxHp = 70f, damage = 4f, attackRange = 4f, attackCooldown = 1.5f, moveSpeed = 1.6f,
                       minBarracksLevel = 3, healPerSecond = 9f, healRange = 4f },
        // Конница (казарма Ур.4): каждому нужна лошадь из Конюшни. Лошадь гибнет вместе со всадником.
        // С похлёбкой (2 порции) - усиленный всадник ★
        new UnitType { unitName = "Всадник", costGold = 120, costFood = 50, trainTime = 10f, spaceOccupied = 4, color = new Color(1f, 0.6f, 0.15f), bodyScale = 0.8f,
                       maxHp = 200f, damage = 20f, attackRange = 1.3f, attackCooldown = 1f, moveSpeed = 2.8f,
                       minBarracksLevel = 4, requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true,
                       pottageCost = 2 },
        new UnitType { unitName = "Конный лучник", costGold = 130, costFood = 50, trainTime = 11f, spaceOccupied = 4, color = new Color(0.75f, 0.9f, 0.2f), bodyScale = 0.75f,
                       maxHp = 140f, damage = 11f, attackRange = 6.5f, attackCooldown = 1.2f, moveSpeed = 2.6f,
                       minBarracksLevel = 4, requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Bow,
                       pottageCost = 2 }
    };

    // Разведчик (казарма Ур.2): быстрый, слабый в бою. Нужен, чтобы разведать деревню перед набегом
    // (см. RaidManager), а в набеге и дома видит дальше остальных (туман войны).
    static UnitType ScoutType()
    {
        return new UnitType { unitName = RaidManager.ScoutUnitName, costGold = 30, costFood = 15, trainTime = 4f, spaceOccupied = 1,
                              color = new Color(0.95f, 0.85f, 0.4f), bodyScale = 0.45f,
                              maxHp = 60f, damage = 5f, attackRange = 1.1f, attackCooldown = 1f, moveSpeed = 2.4f,
                              minBarracksLevel = 2 };
    }

    // Маг-отравитель (путь Магии, казарма Ур.2): бьёт издалека ядовитыми снарядами по площади.
    // Сила яда зависит от маны в воздухе (ManaManager.Factor).
    public const string PoisonerName = "Маг-отравитель";
    static UnitType PoisonerType()
    {
        return new UnitType { unitName = PoisonerName, costGold = 70, costFood = 25, trainTime = 7f, spaceOccupied = 1,
                              color = new Color(0.45f, 0.8f, 0.35f), bodyScale = 0.5f,
                              maxHp = 70f, damage = 6f, attackRange = 5.5f, attackCooldown = 2.2f, moveSpeed = 1.5f,
                              minBarracksLevel = 2, path = PathKind.Magic, potionCost = 1,
                              aoeRadius = 1.6f, poisonDps = 6f, poisonDuration = 5f };
    }

    // Маг стихий (путь Магии, казарма Ур.3): сильный, но дорогой и занимает много места.
    // Бьёт водяными снарядами; иногда - огненный шар по площади; у реки изредка выводит её из берегов
    // на врагов (волна + долгое замедление). Сила - от маны в воздухе (ManaManager.Factor).
    public const string ElementalistName = "Маг стихий";
    static UnitType ElementalistType()
    {
        return new UnitType { unitName = ElementalistName, costGold = 180, costFood = 60, trainTime = 16f, spaceOccupied = 4,
                              color = new Color(0.35f, 0.55f, 1f), bodyScale = 0.62f,
                              maxHp = 160f, damage = 15f, attackRange = 5.5f, attackCooldown = 1.6f, moveSpeed = 1.4f,
                              minBarracksLevel = 3, path = PathKind.Magic, potionCost = 3,
                              fireballChance = 0.2f, fireballRadius = 1.6f, fireballDamage = 40f, burnDps = 6f, burnDuration = 3f,
                              floodChance = 0.12f, floodWaterRange = 4f, floodRadius = 2.4f, floodDamage = 25f,
                              floodSlow = 0.35f, floodSeconds = 6f, floodCooldown = 15f };
    }

    // Поджигатель (путь Военного дела, казарма Ур.3): подбегает и выплёскивает ведро горящей смолы -
    // огонь бьёт всех на площади и поджигает (они ещё немного горят). Потом дерётся кулаками, слабо.
    // Ведро снова наполняют в казарме (RefillResin смолы из Ратуши).
    public const string FireBomberName = "Поджигатель";
    public const int RefillResin = 2;
    static UnitType FireBomberType()
    {
        return new UnitType { unitName = FireBomberName, costGold = 45, costFood = 20, trainTime = 6f, spaceOccupied = 1,
                              color = new Color(1f, 0.42f, 0.12f), bodyScale = 0.52f,
                              maxHp = 95f, damage = 4f, attackRange = 3.2f, attackCooldown = 1.1f, moveSpeed = 1.9f,
                              minBarracksLevel = 3, path = PathKind.War, resinCost = 3,
                              fireRadius = 1.9f, fireDamage = 32f, burnDps = 5f, burnDuration = 4f, fistDamage = 4f };
    }

    // Полевая пушка (путь Военного дела, казарма Ур.3): медленная, занимает много места и дорогая (сталь, доски).
    // Бьёт ядрами прямой наводкой - через стены не стреляет, зато ядро, попав в стену, ломает её: лучшая против
    // стен, ворот, заслонов и зданий. Осколки ядра ранят тех, кто стоит за целью.
    // «Оружие» - бронебойное ядро Инженерного двора; модули - подъёмник (Инженерия) и магические снаряды (Магия),
    // их пушка берёт сама, когда дома и не в бою.
    public const string FieldCannonName = "Полевая пушка";
    static UnitType FieldCannonType()
    {
        return new UnitType { unitName = FieldCannonName, costGold = 220, costFood = 40, trainTime = 18f, spaceOccupied = 5,
                              color = new Color(0.36f, 0.33f, 0.3f), bodyScale = 0.8f,
                              maxHp = 260f, damage = 34f, attackRange = 7.5f, attackCooldown = 4.2f, moveSpeed = 0.95f,
                              minBarracksLevel = 3, path = PathKind.War, usesWeapon = true, weaponKind = WeaponKind.Shot,
                              isCannon = true, cannonFragments = 4, cannonFragDamage = 11f, cannonFragRange = 2.3f,
                              cannonBuildingMul = 3f, cannonBarrierMul = 1.5f, steelCost = 6, planksCost = 8 };
    }

    // Катапульта (Инженерия, направление «Военный инженер», Инженерная казарма): расчёт - один житель,
    // плюс доски и детали. Камень летит навесом через стены и бьёт по площади; чем дальше цель, тем слабее удар.
    public const string CatapultName = "Катапульта";
    public static UnitType CatapultType()
    {
        return new UnitType { unitName = CatapultName, costGold = 180, costFood = 30, trainTime = 20f, spaceOccupied = 4,
                              color = new Color(0.55f, 0.4f, 0.24f), bodyScale = 0.8f,
                              maxHp = 220f, damage = 45f, attackRange = 13f, attackCooldown = 5f, moveSpeed = 0.8f,
                              minBarracksLevel = 1, path = PathKind.Engineering, engSchool = EngSchool.Military,
                              isCatapult = true, catapultMinRange = 3f, catapultFarMul = 0.35f, catapultRadius = 1.6f,
                              catapultBuildingMul = 1.5f, planksCost = 10, partsCost = 8 };
    }

    public static UnitType FindType(string unitName)
    {
        foreach (var b in BuildingInstance.All)
        {
            if (b == null) continue;
            BarracksController br = b.GetComponent<BarracksController>();
            if (br == null || br.availableUnits == null) continue;
            UnitType u = br.availableUnits.Find(x => x != null && x.unitName == unitName);
            if (u != null) return u;
        }
        if (unitName == CatapultName) return CatapultType();   // Инженерной казармы может и не быть
        return null;
    }

    // ---------- Инженерная казарма ----------
    // Тот же контроллер, но в режиме инженеров: в списке только инженерные войска (катапульта),
    // свои места. Все приказы, отряды, лагеря и набеги работают как у обычной казармы.
    bool modeChecked;
    bool engineerMode;
    public bool IsEngineer
    {
        get
        {
            if (!modeChecked) ApplyMode();
            return engineerMode;
        }
    }

    void ApplyMode()
    {
        bool eng = GetComponent<EngineerBarracks>() != null
                   || (Bld != null && Bld.type != null && Bld.type.name == EngineerBarracks.TypeName);
        if (Bld != null && Bld.type != null) modeChecked = true;   // тип здания уже известен - решение окончательное
        else if (eng) modeChecked = true;
        if (!eng || engineerMode) return;
        engineerMode = true;
        availableUnits = new List<UnitType> { CatapultType() };
        maxGarrisonSpace = EngineerSpacePerLevel;
    }

    public const int EngineerSpacePerLevel = 8;   // 2 катапульты на уровень

    /// <summary>Название казармы для текстов.</summary>
    public string Title => IsEngineer ? EngineerBarracks.TypeName : TypeName;

    void Start() { ApplyMode(); }

    // Список мог сохраниться в сцене старым - разведчика добавляем сами (после лучника)
    void Awake()
    {
        ApplyMode();
        if (engineerMode) return;   // у инженеров - свой список (только катапульта)

        if (availableUnits != null && !availableUnits.Exists(u => u != null && u.unitName == FireBomberName))
            availableUnits.Add(FireBomberType());
        if (availableUnits != null && !availableUnits.Exists(u => u != null && u.unitName == PoisonerName))
            availableUnits.Add(PoisonerType());
        if (availableUnits != null && !availableUnits.Exists(u => u != null && u.unitName == ElementalistName))
            availableUnits.Add(ElementalistType());
        if (availableUnits != null && !availableUnits.Exists(u => u != null && u.unitName == FieldCannonName))
            availableUnits.Add(FieldCannonType());
        if (availableUnits == null) availableUnits = new List<UnitType>();
        var poisoner = availableUnits.Find(u => u != null && u.unitName == PoisonerName);
        if (poisoner != null && poisoner.potionCost <= 0) poisoner.potionCost = 1;   // старый список из сцены
        var fireBomber = availableUnits.Find(u => u != null && u.unitName == FireBomberName);
        if (fireBomber != null && fireBomber.minBarracksLevel < 3) fireBomber.minBarracksLevel = 3;   // старый список из сцены (было Ур.2)
        if (!availableUnits.Exists(u => u != null && u.unitName == RaidManager.ScoutUnitName))
        {
            int at = availableUnits.FindIndex(u => u != null && u.unitName == "Лучник");
            availableUnits.Insert(at >= 0 ? at + 1 : availableUnits.Count, ScoutType());
        }
    }

    // Юнит доступен на текущем уровне казармы
    public bool Unlocked(UnitType u)
    {
        int lvl = Bld != null ? Bld.level : 1;
        return u != null && lvl >= u.minBarracksLevel && DevelopmentPath.Allows(u.path) && DevelopmentPath.EngAllows(u.engSchool);
    }

    // Слот «на тело» (броня / плащ): показываем, если бойцу можно и это вообще есть у города
    static bool ShowBody(CitizenAgent s)
    {
        if (s == null || !s.CanWearBody) return false;
        return s.EquippedBody != null || ArmoryController.TotalBodies > 0 || DevelopmentPath.IsHeavy || DevelopmentPath.IsCamo;
    }

    // Положен ли бойцу щит (по виду войск)
    bool UnitUsesShield(CitizenAgent s)
    {
        if (s == null) return false;
        UnitType u = availableUnits.Find(x => x.unitName == s.UnitName);
        return u != null && u.usesShield;
    }

    // Солдаты этой казармы (физические жители на карте)
    private readonly List<CitizenAgent> soldiers = new List<CitizenAgent>();
    private readonly HashSet<CitizenAgent> selection = new HashSet<CitizenAgent>();
    public IReadOnlyList<CitizenAgent> Soldiers => soldiers;

    // Обучение
    private UnitType currentTrainingUnit = null;
    private CitizenAgent recruit = null;
    private float trainingTimer = 0f;
    private float recruitWait = 0f;
    private bool trainingHorse = false;   // под текущее обучение уже забрана лошадь из Конюшни
    private BuildingInstance potionSource;   // откуда взяли зелье (Ратуша / Хранилище)
    private bool potionsPending = false;     // зелье ещё несут (обучение ждёт)
    private int potionToken = 0;
    private int trainingPotions = 0;  // зелья, забранные под текущее обучение
    private readonly List<UpgradeRules.Spent> trainingMaterials = new List<UpgradeRules.Spent>();   // сталь / доски полевой пушки
    private int trainingParts = 0;   // детали, забранные под текущее обучение (катапульта)

    // Похлёбка для текущего обучения
    public bool UsePottage { get; set; }  // галочка "С похлёбкой" (усиленные всадники)
    private int pottageHeld = 0;          // сколько порций забрано с Кухни под это обучение
    private bool pottagePending = false;  // порцию ещё несут (обучение ждёт)
    private int pottageToken = 0;         // номер заказа: опоздавший рейс от отменённого обучения не засчитывается

    // Режим "кликните на карту, куда послать" (обрабатывает BuildingPlacer)
    public bool PickingTarget { get; set; }

    // UI
    private Vector2 scroll;
    private CitizenAgent focused;
    private float focusUntil;

    public bool IsTraining => currentTrainingUnit != null;
    public string CurrentTrainingName => currentTrainingUnit != null ? currentTrainingUnit.unitName + (pottageHeld > 0 ? " ★" : "") : "";
    public bool RecruitInside => recruit != null && recruit.IsInside(Bld);
    public bool WaitingPottage => IsTraining && pottagePending;
    public float TrainingProgress => currentTrainingUnit != null && currentTrainingUnit.trainTime > 0 ? (1f - (trainingTimer / currentTrainingUnit.trainTime)) : 0f;

    // Общее количество людей (готовые солдаты + новобранец, который обучается прямо сейчас)
    public int HeadCount
    {
        get
        {
            int c = 0;
            foreach (var s in soldiers) if (s != null) c++;
            if (IsTraining) c++;
            return c;
        }
    }

    // Занятое место в казарме с учётом веса каждого юнита
    public int CurrentOccupiedSpace
    {
        get
        {
            int total = 0;
            foreach (var s in soldiers)
            {
                if (s == null) continue;
                UnitType u = availableUnits.Find(x => x.unitName == s.UnitName);
                total += u != null ? u.spaceOccupied : 1;
            }
            return total;
        }
    }

    void Update()
    {
        soldiers.RemoveAll(s => s == null);
        TickSquads(Time.deltaTime);
        RefillBuckets();
        LoadCannons();
        LoadChaosCores();

        if (currentTrainingUnit == null) return;

        // Новобранец пропал (например, уменьшилось население) - отменяем и возвращаем ресурсы
        if (recruit == null || recruit.TrainingAt != Bld)
        {
            CancelTraining(true);
            return;
        }

        if (Bld != null && !Bld.isWorking) return;

        // Пока новобранец не зашёл в казарму - обучение не идёт
        if (!recruit.IsInside(Bld))
        {
            recruitWait += Time.deltaTime;
            if (recruitWait > recruitTimeout) recruit.ForceEnter(Bld); // застрял по дороге
            return;
        }

        // Похлёбку ещё несут с Кухни - ждём
        if (pottagePending) return;

        // Зелье ещё несут - ждём
        if (potionsPending) return;

        trainingTimer -= Time.deltaTime;
        if (trainingTimer <= 0f)
        {
            bool hearty = pottageHeld > 0;
            UnitType unit = currentTrainingUnit;
            recruit.BecomeSoldier(Bld, unit, hearty);
            trainingPotions = 0;   // зелье израсходовано
            trainingMaterials.Clear();   // сталь и доски ушли на пушку
            trainingParts = 0;           // детали ушли на катапульту
            soldiers.Add(recruit);
            AssignNewSoldier(recruit);
            trainingHorse = false;   // лошадь теперь у бойца
            pottageHeld = 0;         // похлёбку съели
            if (hearty) WarManager.Notify($"{Title}: обучен {unit.unitName} ★ (на похлёбке)", true);

            recruit = null;
            currentTrainingUnit = null;
        }
    }

    // Поджигатели с пустым ведром, зашедшие в казарму, заправляются смолой из Ратуши
    float refillTimer;
    void RefillBuckets()
    {
        refillTimer -= Time.deltaTime;
        if (refillTimer > 0f) return;
        refillTimer = 1f;
        var rm = ResourceManager.Instance;
        if (rm == null || Bld == null) return;
        foreach (var s in soldiers)
        {
            if (s == null || !s.NeedsBucket || !s.IsInside(Bld)) continue;
            if (!rm.SpendResin(RefillResin)) return;
            s.RefillBucket();
        }
    }

    // Полевые пушки сами берут модули со склада Ратуши / двора: подъёмник (Инженерия) и пачку магических
    // снарядов (Магия). Покупные с рынка - тоже. Где бы ни стояли дома (на посту, в лагере), лишь бы не в бою
    // и не в походе: носильщика нет - упрощение. Снаряды - выбранного в «Снаряж.» вида, если он есть, иначе лучшие.
    float cannonTimer;
    void LoadCannons()
    {
        cannonTimer -= Time.deltaTime;
        if (cannonTimer > 0f) return;
        cannonTimer = 2f;
        if (Bld == null || Bld.IsRuined) return;
        foreach (var s in soldiers)
        {
            if (s == null || !s.IsFieldCannon || s.IsDead || s.IsOnExpedition || s.InCombat) continue;
            if (!s.CannonLift.Has && EngineerYard.CountKits(ModuleKind.CannonLift) > 0)
            {
                int mk = EngineerYard.BestMk(ModuleKind.CannonLift);
                if (EngineerYard.TakeKit(new ModuleKit { kind = ModuleKind.CannonLift, mk = mk }, out _))
                {
                    s.MountCannonLift(mk);
                    WarManager.Notify($"{s.UnitName} {s.CitizenName}: поставлен {TowerModules.KitName(ModuleKind.CannonLift, mk)}", true);
                }
            }
            if (s.CannonHasShells || !s.CannonShellAuto) continue;
            ModuleKit pack = new ModuleKit();
            bool got = s.CannonShellPref != ModuleKind.None && MagicShells.BestPack(s.CannonShellPref, out pack);
            if (!got) got = MagicShells.BestPack(out pack);
            if (got && EngineerYard.TakeKit(pack, out _))
            {
                s.LoadCannonShells(pack);
                WarManager.Notify($"{s.UnitName} {s.CitizenName}: заряжена — {TowerModules.KitName(pack)}", true);
            }
        }
    }

    // Маги стихий в казарме (или у её двери) сами берут лучшее ядро хаоса из Ратуши (школа Хаоса, ChaosMagic).
    // Покупное ядро с рынка тоже годится, даже если школу сменили.
    float chaosTimer;
    void LoadChaosCores()
    {
        chaosTimer -= Time.deltaTime;
        if (chaosTimer > 0f) return;
        chaosTimer = 2f;
        if (Bld == null || Bld.IsRuined) return;
        foreach (var s in soldiers)
        {
            if (s == null || !s.IsElementalist || s.HasChaosCore || s.IsDead || s.IsOnExpedition || s.InCombat) continue;
            if (!s.IsInside(Bld) && Bld.DistanceTo(s.transform.position) > 1.5f) continue;
            if (!BestChaosCore(out ModuleKit core) || !EngineerYard.TakeKit(core, out _)) return;
            s.LoadChaosCore(core);
            WarManager.Notify($"{s.UnitName} {s.CitizenName}: взял {TowerModules.KitName(core)}", true);
        }
    }

    // Лучшее ядро в Ратуше / дворах: выше марка, при равной - начатое (с остатком) раньше целого
    static bool BestChaosCore(out ModuleKit best)
    {
        best = default(ModuleKit);
        bool found = false;
        foreach (var k in EngineerYard.AllKits())
        {
            if (k.kind != ModuleKind.ChaosCore) continue;
            if (!found || k.mk > best.mk || (k.mk == best.mk && k.charges > 0 && best.charges == 0)) { best = k; found = true; }
        }
        return found;
    }

    // ---------- Наёмники (рынок) ----------

    /// <summary>Свободное место под этот вид войск.</summary>
    public int FreeSpace => MaxSpace - CurrentOccupiedSpace - (IsTraining && currentTrainingUnit != null ? currentTrainingUnit.spaceOccupied : 0);

    /// <summary>Казарма, где поместится боец этого вида (больше всего места).</summary>
    public static BarracksController WithRoomFor(int space)
    {
        BarracksController best = null;
        int bestFree = -1;
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.IsRuined) continue;
            BarracksController br = b.GetComponent<BarracksController>();
            if (br == null || br.IsEngineer) continue;   // наёмников в Инженерную казарму не селим
            int free = br.FreeSpace;
            if (free >= space && free > bestFree) { bestFree = free; best = br; }
        }
        return best;
    }

    /// <summary>Наёмник прибыл (купили на рынке или вернулся из лагеря гильдии): становится бойцом этой казармы.</summary>
    public void AdoptSoldier(CitizenAgent a, UnitType u, bool isHearty, Weapon w, Shield sh)
    {
        if (a == null || u == null) return;
        a.BecomeSoldier(Bld, u, isHearty);
        if (w != null && !a.EquipWeapon(w) && !ArmoryController.Stash(w))
            WarManager.Notify("Оружие наёмника некуда деть — потеряно", false);
        if (sh != null && !ShieldBearer.Equip(a, sh) && !ArmoryController.StashShield(sh))
            WarManager.Notify("Щит наёмника некуда деть — потерян", false);
        soldiers.Add(a);
        AssignNewSoldier(a);
    }

    /// <summary>Отпустить бойца в лагерь наёмников гильдии (продажа на рынке). Его снаряжение и конь уходят с ним.</summary>
    public bool ReleaseForSale(CitizenAgent a, out SoldierLot lot)
    {
        lot = null;
        if (a == null || a.IsDead || !soldiers.Contains(a) || a.IsOnExpedition) return false;
        bool mounted = a.HasHorse;
        lot = a.PackForMercenaryCamp();
        if (lot == null) return false;
        lot.shield = ShieldBearer.Unequip(a);
        soldiers.Remove(a);
        selection.Remove(a);
        // Боец не исчезает на месте: идёт к Торговому дому и уходит за край карты (CitizenAgent_Departure)
        a.DepartForSale(TradeGuild.Open && TradeGuild.House != null ? TradeGuild.House.Building : null, mounted);
        return true;
    }

    void CancelTraining(bool refund)
    {
        // Зелье: уже здесь - вернуть; ещё несут - вернёт сам рейс (заказ устарел)
        if (trainingPotions > 0 && !potionsPending) PotionHouse.ReturnPotions(trainingPotions, potionSource);
        trainingPotions = 0;
        potionToken++;
        potionsPending = false;
        potionSource = null;
        if (refund && currentTrainingUnit != null && ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(currentTrainingUnit.costGold, 0, 0, currentTrainingUnit.costFood);
            ResourceManager.Instance.AddResin(currentTrainingUnit.resinCost);
            UpgradeRules.RefundSpent(trainingMaterials);
            ResourceManager.Instance.AddParts(trainingParts);
        }
        trainingMaterials.Clear();
        trainingParts = 0;
        if (recruit != null) recruit.CancelTraining();
        if (trainingHorse && !StableController.ReturnHorse())   // обучение сорвано - лошадь обратно в Конюшню
            WarManager.Notify("В Конюшнях нет места: лошадь сорванного обучения потеряна", false);
        trainingHorse = false;

        // Похлёбка: уже здесь - обратно на Кухню; ещё несут - вернёт сам рейс (заказ устарел)
        if (pottageHeld > 0 && !pottagePending) KitchenController.ReturnPortions(pottageHeld, BarracksPos());
        pottageToken++;
        pottageHeld = 0;
        pottagePending = false;

        recruit = null;
        currentTrainingUnit = null;
    }

    Vector2 BarracksPos() { return Bld != null ? Bld.Center : (Vector2)transform.position; }

    // Сколько порций похлёбки нужно этому виду войск сейчас (0 - без похлёбки)
    public int PottageNeed(UnitType unit)
    {
        if (unit == null || !unit.UsesPottage) return 0;
        if (unit.requiresPottage || UsePottage) return unit.PottagePortions;
        return 0;
    }

    static void Fail(string msg) { WarManager.Notify(msg, false); }

    public void TrainUnit(UnitType unit)
    {
        if (unit == null) return;
        if (Bld != null && !Bld.isWorking) { Fail($"{Title} не работает: {Bld.StatusText}"); return; }
        if (!DevelopmentPath.EngAllows(unit.engSchool)) { Fail($"{unit.unitName}: нужно направление «{DevelopmentPath.EngName(unit.engSchool)}»"); return; }
        if (!Unlocked(unit)) { Fail($"{unit.unitName}: нужна казарма Ур.{unit.minBarracksLevel}"); return; }
        if (IsTraining) { Fail($"{Title} уже занята обучением"); return; }

        var pm = PopulationManager.Instance;
        if (pm != null && pm.FreePeople < 1) { Fail("Нет свободных жителей для найма"); return; }

        if (CurrentOccupiedSpace + unit.spaceOccupied > MaxSpace)
        {
            Fail($"Мало места в казарме: нужно {unit.spaceOccupied}, свободно {MaxSpace - CurrentOccupiedSpace}");
            return;
        }

        var rm = ResourceManager.Instance;
        if (rm == null || !rm.HasEnough(unit.costGold, 0, 0, unit.costFood))
        {
            Fail($"{unit.unitName}: нужно {unit.costGold} золота и {unit.costFood} еды");
            return;
        }

        if (unit.resinCost > 0 && rm.resin < unit.resinCost)
        {
            Fail($"{unit.unitName}: нужно {unit.resinCost} смолы в Ратуше (её даёт Лесопилка Ур.3 или рынок)");
            return;
        }

        if (unit.steelCost > 0 || unit.planksCost > 0)
        {
            var st0 = TownHallStorage.Instance;
            int steel = st0 != null ? UpgradeRules.CountAtLeast(st0, RefinedKind.Steel, QualityTier.Medium) : 0;
            int planks = st0 != null ? UpgradeRules.CountAtLeast(st0, RefinedKind.Planks, QualityTier.Medium) : 0;
            if (steel < unit.steelCost || planks < unit.planksCost)
            {
                Fail($"{unit.unitName}: нужно {unit.steelCost} стали и {unit.planksCost} досок (есть {steel} и {planks})");
                return;
            }
        }

        if (unit.partsCost > 0 && rm.parts < unit.partsCost)
        {
            Fail($"{unit.unitName}: нужно {unit.partsCost} деталей (есть {rm.parts}) — их льёт Литейная");
            return;
        }

        if (unit.potionCost > 0 && PotionHouse.TotalPotions < unit.potionCost)
        {
            Fail($"{unit.unitName}: нужно зелье отравления ({unit.potionCost}) — сварите в Зельеварне, оно должно лежать в Ратуше или Хранилище");
            return;
        }

        // Похлёбка: ищем Кухню, где хватает порций (лучше - стоящую вплотную)
        int portions = PottageNeed(unit);
        KitchenController kitchen = null;
        bool linked = false;
        if (portions > 0)
        {
            kitchen = KitchenController.PickFor(Bld, portions, out linked);
            if (kitchen == null)
            {
                Fail($"{unit.unitName}: нужна похлёбка ({portions} порц.) — сварите её на Кухне" +
                     (unit.requiresPottage ? "" : " или снимите галочку «С похлёбкой»"));
                return;
            }
        }

        // Конница: сначала берём лошадь из Конюшни
        bool horse = unit.requiresHorse;
        if (horse && !StableController.TryTakeHorse())
        {
            Fail($"{unit.unitName}: нет свободных лошадей (выращивайте их в Конюшне)");
            return;
        }

        // Берём реального жителя с карты
        CitizenAgent r = pm != null ? pm.TakeRecruit(Bld) : null;
        if (r == null)
        {
            if (horse) StableController.ReturnHorse();
            Fail("Некого отправить на обучение");
            return;
        }

        // Зелье берём из Ратуши / Хранилища - его принесут физически
        BuildingInstance potSrc = null;
        if (unit.potionCost > 0 && !PotionHouse.TakePotions(unit.potionCost, out potSrc))
        {
            r.CancelTraining();
            if (horse) StableController.ReturnHorse();
            Fail($"{unit.unitName}: зелья закончились — попробуйте ещё раз");
            return;
        }

        // Забираем похлёбку с Кухни
        if (kitchen != null && !kitchen.Take(portions))
        {
            r.CancelTraining();
            if (horse) StableController.ReturnHorse();
            if (unit.potionCost > 0) PotionHouse.ReturnPotions(unit.potionCost, potSrc);
            Fail("Похлёбка закончилась — попробуйте ещё раз");
            return;
        }

        trainingPotions = unit.potionCost;
        potionSource = potSrc;

        trainingHorse = horse;
        rm.Spend(unit.costGold, 0, 0, unit.costFood);
        rm.SpendResin(unit.resinCost);
        trainingMaterials.Clear();
        var stx = TownHallStorage.Instance;
        if (stx != null && unit.steelCost > 0) UpgradeRules.SpendAtLeast(stx, RefinedKind.Steel, QualityTier.Medium, unit.steelCost, trainingMaterials);
        if (stx != null && unit.planksCost > 0) UpgradeRules.SpendAtLeast(stx, RefinedKind.Planks, QualityTier.Medium, unit.planksCost, trainingMaterials);
        trainingParts = unit.partsCost > 0 && rm.SpendParts(unit.partsCost) ? unit.partsCost : 0;
        recruit = r;
        currentTrainingUnit = unit;
        trainingTimer = unit.trainTime;
        recruitWait = 0f;

        pottageHeld = kitchen != null ? portions : 0;
        pottagePending = kitchen != null && !linked;
        int token = ++pottageToken;

        // Кухня не вплотную - порцию несёт житель (повар, курьер Ратуши...). Обучение начнётся, когда принесут.
        if (pottagePending)
        {
            int n = portions;
            Vector2 at = BarracksPos();
            Logistics.Request(new HaulJob
            {
                from = kitchen.Building,
                to = Bld,
                prefer = kitchen.Building,
                onDeliver = () => OnPottageArrived(token, n, at),
                onAbort = loaded => OnPottageFailed(token, n, at)
            });
        }

        // Зелье несёт житель из Ратуши / Хранилища; обучение начнётся, когда принесут
        potionsPending = false;
        int potTok = ++potionToken;
        if (trainingPotions > 0)
        {
            int pn = trainingPotions;
            BuildingInstance pfrom = potSrc != null ? potSrc : (pm != null ? pm.Hall : null);
            potionsPending = true;
            Logistics.Request(new HaulJob
            {
                from = pfrom,
                to = Bld,
                prefer = Bld,
                onDeliver = () => OnPotionsArrived(potTok, pn, potSrc),
                onAbort = loaded => OnPotionsFailed(potTok, pn, potSrc)
            });
        }
    }

    void OnPotionsArrived(int token, int n, BuildingInstance src)
    {
        // Обучение отменили / казарму снесли, пока несли - зелья назад
        if (this == null || token != potionToken || !IsTraining)
        {
            PotionHouse.ReturnPotions(n, src);
            return;
        }
        potionsPending = false;
    }

    void OnPotionsFailed(int token, int n, BuildingInstance src)
    {
        PotionHouse.ReturnPotions(n, src);
        if (this == null || token != potionToken || !IsTraining) return;
        trainingPotions = 0;      // уже вернули выше
        potionsPending = false;
        Fail("Зелье не донесли — обучение отменено");
        CancelTraining(true);
    }

    void OnPottageArrived(int token, int n, Vector2 at)
    {
        // Обучение отменили / казарму снесли, пока несли - порции на Кухню
        if (this == null || token != pottageToken || !IsTraining)
        {
            KitchenController.ReturnPortions(n, at);
            return;
        }
        pottagePending = false;
    }

    void OnPottageFailed(int token, int n, Vector2 at)
    {
        KitchenController.ReturnPortions(n, at);
        if (this == null || token != pottageToken || !IsTraining) return;

        pottageHeld = 0;
        pottagePending = false;
        if (currentTrainingUnit != null && currentTrainingUnit.requiresPottage)
        {
            Fail($"{currentTrainingUnit.unitName}: похлёбку не донесли — обучение отменено");
            CancelTraining(true);
        }
        else Fail("Похлёбку не донесли — боец выйдет без усиления");
    }

    // ---------- Отряды ----------

    private readonly List<Squad> squads = new List<Squad>();
    private int squadCounter = 0;
    public IReadOnlyList<Squad> Squads => squads;

    Squad NewSquad()
    {
        Squad sq = new Squad(this, ++squadCounter);
        squads.Add(sq);
        return sq;
    }

    // Новый солдат попадает в отряд, который сейчас дома; если таких нет - создаётся новый
    void AssignNewSoldier(CitizenAgent s)
    {
        Squad home = null;
        foreach (var sq in squads)
            if (sq.Order != SoldierOrder.Move && !sq.OnExpedition) { home = sq; break; }
        if (home == null) home = NewSquad();

        home.Add(s);
        if (home.Order == SoldierOrder.Garrison) s.SetOrder(SoldierOrder.Garrison, Vector2.zero);
    }

    void TickSquads(float dt)
    {
        for (int i = squads.Count - 1; i >= 0; i--)
        {
            Squad sq = squads[i];
            sq.Tick(dt);
            if (sq.Members.Count == 0)
            {
                sq.Dispose();
                squads.RemoveAt(i);
            }
        }
    }

    void OnDestroy()
    {
        foreach (var sq in squads) sq.Dispose(); // казарму снесли - лагеря на карте не остаются

        // Снесли во время обучения: золото и еду возвращаем, лошадь - в Конюшню, похлёбку - на Кухню
        // (раньше всё это пропадало)
        if (IsTraining && gameObject.scene.isLoaded) CancelTraining(true);
    }

    // Отряды, к которым относятся отмеченные бойцы (если никто не отмечен - все отряды)
    List<Squad> TargetSquads()
    {
        var result = new List<Squad>();
        foreach (var s in Targets())
            if (s.Squad != null && !result.Contains(s.Squad)) result.Add(s.Squad);
        return result;
    }

    public void FormSquadFromSelected()
    {
        var list = new List<CitizenAgent>();
        foreach (var s in soldiers) if (s != null && selection.Contains(s) && !s.IsOnExpedition) list.Add(s);
        if (list.Count == 0) return;

        Squad sq = NewSquad();
        foreach (var s in list)
        {
            if (s.Squad != null) s.Squad.Remove(s);
            sq.Add(s);
            s.SetOrder(SoldierOrder.Guard, Vector2.zero); // новый отряд сначала охраняет казарму
        }
    }

    float SquadSectionHeight()
    {
        return Mathf.Max(1, squads.Count) * 20f + 4f;
    }

    // ---------- Приказы ----------

    // Кому отдаём приказ: отмеченным галочками, а если никого не отметили - всем
    List<CitizenAgent> Targets()
    {
        var list = new List<CitizenAgent>();
        foreach (var s in soldiers)
            if (s != null && selection.Contains(s)) list.Add(s);
        if (list.Count == 0)
            foreach (var s in soldiers) if (s != null) list.Add(s);
        return list;
    }

    // Кому пойдёт приказ: целый отряд - если отмечены все его бойцы, иначе новый отряд из отмеченных
    List<Squad> SquadsForOrder()
    {
        var result = new List<Squad>();
        var bySquad = new Dictionary<Squad, List<CitizenAgent>>();
        foreach (var s in Targets())
        {
            if (s == null || s.Squad == null || s.IsOnExpedition) continue;
            if (!bySquad.TryGetValue(s.Squad, out var l)) { l = new List<CitizenAgent>(); bySquad[s.Squad] = l; }
            l.Add(s);
        }

        foreach (var kv in bySquad)
        {
            Squad sq = kv.Key;
            bool whole = true;
            foreach (var m in sq.Members)
                if (m != null && !kv.Value.Contains(m)) { whole = false; break; }

            if (whole) { result.Add(sq); continue; }

            Squad ns = NewSquad();
            foreach (var s in kv.Value) { sq.Remove(s); ns.Add(s); }
            result.Add(ns);
        }
        return result;
    }

    public void OrderGarrison()
    {
        foreach (var sq in SquadsForOrder()) sq.GoHome(SoldierOrder.Garrison);
    }

    public void OrderGuard()
    {
        foreach (var sq in SquadsForOrder()) sq.GoHome(SoldierOrder.Guard);
    }

    public void SendSelectedTo(Vector2 point)
    {
        var nav = GridNavigation.Instance;
        if (nav != null && !nav.InBounds(nav.WorldToCell(point))) return; // кликнули за картой - ждём новый клик

        // Приказ получает весь отряд: он идёт в точку, ставит лагерь и живёт там на своих припасах
        foreach (var sq in SquadsForOrder()) sq.Deploy(point);

        PickingTarget = false;
    }

    public void Dismiss(CitizenAgent a)
    {
        if (a == null) return;
        if (a.IsOnExpedition)
        {
            WarManager.Notify($"{a.CitizenName} в походе — уволить можно после возвращения", false);
            return;
        }
        ShieldBearer.ReturnToArmory(a);   // щит - на склад Оружейной (оружие вернёт сам Discharge)
        a.Discharge();
        soldiers.Remove(a);
        selection.Remove(a);
    }

    public void DismissSelected()
    {
        var list = new List<CitizenAgent>();
        foreach (var s in soldiers) if (s != null && selection.Contains(s)) list.Add(s);
        foreach (var s in list) Dismiss(s);
    }

    // ---------- Снаряжение ----------

    private CitizenAgent equipFor;      // чьё снаряжение открыто в боковой панели
    private Vector2 equipScroll;
    private Vector2 shieldScroll;
    private Vector2 bodyScroll;

    // Выдать бойцу оружие со склада Оружейной (старое оружие бойца уходит на склад)
    bool EquipFrom(CitizenAgent s, ArmoryController from, Weapon w)
    {
        if (s == null || from == null || w == null || !s.CanUseWeapon || w.kind != s.WeaponKindAllowed) return false;
        if (!from.RemoveFromStock(w)) return false;

        Weapon old = s.UnequipWeapon();
        if (old != null && !ArmoryController.Stash(old))
        {
            // Старое оружие некуда положить - ничего не меняем
            s.EquipWeapon(old);
            from.AddToStock(w);
            WarManager.Notify("Оружейная: нет места для старого оружия", false);
            return false;
        }
        return s.EquipWeapon(w);
    }

    // Выдать бойцу щит со склада Оружейной (старый щит уходит на склад)
    bool EquipShieldFrom(CitizenAgent s, ArmoryController from, Shield sh)
    {
        if (s == null || from == null || sh == null || !UnitUsesShield(s)) return false;
        if (!from.RemoveShield(sh)) return false;

        Shield old = ShieldBearer.Unequip(s);
        if (old != null && !ArmoryController.StashShield(old))
        {
            ShieldBearer.Equip(s, old);
            from.AddShield(sh);
            WarManager.Notify("Оружейная: нет места для старого щита", false);
            return false;
        }

        if (!ShieldBearer.Equip(s, sh))
        {
            from.AddShield(sh);
            return false;
        }
        return true;
    }

    // Выдать бойцу броню или плащ со склада Оружейной (прежняя вещь уходит на склад)
    bool EquipBodyFrom(CitizenAgent s, ArmoryController from, BodyGear g)
    {
        if (s == null || from == null || g == null || !s.CanWearBody) return false;
        if (!from.RemoveBody(g)) return false;

        BodyGear old = s.UnequipBody();
        if (old != null && !ArmoryController.StashBody(old))
        {
            s.EquipBody(old);
            from.AddBody(g);
            WarManager.Notify("Оружейная: нет места для прежней брони / плаща", false);
            return false;
        }
        if (!s.EquipBody(g))
        {
            from.AddBody(g);
            return false;
        }
        return true;
    }

    // Что лучше надеть бойцу: разведчику и стрелкам - плащ, бойцам ближнего боя - броню
    static BodyKind PreferredBody(CitizenAgent s)
    {
        return s.IsScout || s.ExpRanged ? BodyKind.Cloak : BodyKind.Armor;
    }

    // Лучшая броня / плащ для бойца (сначала - предпочтительного вида). found = false - на складах пусто.
    static ArmoryController.BodyEntry BestBodyFor(CitizenAgent s, out bool found)
    {
        var opts = ArmoryController.AvailableBodies();
        found = opts.Count > 0;
        if (!found) return new ArmoryController.BodyEntry();
        BodyKind want = PreferredBody(s);
        foreach (var o in opts) if (o.gear.kind == want) return o;
        return opts[0];
    }

    // Каждому бойцу без оружия / щита - лучшее из доступного
    void AutoEquipAll()
    {
        int given = 0;
        foreach (var s in soldiers)
        {
            if (s == null) continue;

            if (s.CanUseWeapon && s.EquippedWeapon == null)
            {
                var opts = ArmoryController.Available(s.WeaponKindAllowed);
                if (opts.Count > 0 && EquipFrom(s, opts[0].armory, opts[0].weapon)) given++;
            }

            if (UnitUsesShield(s) && ShieldBearer.ShieldOf(s) == null)
            {
                var sopts = ArmoryController.AvailableShields();
                if (sopts.Count > 0 && EquipShieldFrom(s, sopts[0].armory, sopts[0].shield)) given++;
            }

            if (s.CanWearBody && s.EquippedBody == null)
            {
                var bo = BestBodyFor(s, out bool found);
                if (found && EquipBodyFrom(s, bo.armory, bo.gear)) given++;
            }
        }
        WarManager.Notify(given > 0 ? $"Казарма: выдано снаряжения — {given}" : "Казарма: нет бойцов без снаряжения или нет подходящих предметов", given > 0);
    }

    // ---------- Интерфейс ----------

    // Вкладки панели: 0 - Обучение, 1 - Войска, 2 - Снаряжение
    private int tab = 0;
    private Vector2 gearListScroll;

    const float UnitRowH = 32f;      // строка вида войск
    const float SoldierRowH = 26f;   // строка бойца в списке
    const float ListH = 150f;        // высота прокручиваемых списков
    const float EquipRowH = 28f;     // строка предмета в Оружейной

    static GUIStyle Mid(GUIStyle from, bool wrap)
    {
        return new GUIStyle(from) { alignment = TextAnchor.MiddleLeft, wordWrap = wrap, clipping = TextClipping.Clip };
    }

    string TypeSummary()
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in soldiers)
        {
            if (s == null) continue;
            counts.TryGetValue(s.DisplayName, out int c);
            counts[s.DisplayName] = c + 1;
        }
        if (counts.Count == 0) return "Бойцов нет";

        var parts = new List<string>();
        foreach (var kv in counts) parts.Add($"{kv.Key}: {kv.Value}");
        return string.Join("\n", parts);
    }

    public void DrawMarkers()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        GUIStyle st = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        foreach (var s in soldiers)
        {
            if (s == null || s.IsInsideBuilding) continue;
            bool mark = selection.Contains(s) || (s == focused && Time.unscaledTime < focusUntil);
            if (!mark) continue;

            Vector3 sp = cam.WorldToScreenPoint(s.transform.position + Vector3.up * 0.7f);
            if (sp.z < 0f) continue;

            Rect rc = new Rect(sp.x - 15f, Screen.height - sp.y - 15f, 30f, 30f);
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), "▼", st);
            st.normal.textColor = UiKit.Accent;
            GUI.Label(rc, "▼", st);
        }
    }

    /// <summary>
    /// Пока игрок выбирает точку на карте ("Послать..."), большая панель казармы прячется,
    /// остаётся маленькая плашка с подсказкой и кнопкой отмены. Метки выбранных бойцов остаются.
    /// </summary>
    public Rect DrawPickBanner(float x, float y, float w)
    {
        Rect r = new Rect(x, y, w, 66f);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(x + 10f, y + 6f, w - 20f, 22f),
            UiKit.B(UiKit.Col(UiKit.AccentHex, "Куда послать отряд?")) + "  " + UiKit.Col(UiKit.MutedHex, "кликните по карте"), UiKit.Body);
        if (UiKit.Button(new Rect(x + 10f, y + 32f, w - 20f, 26f), "Отмена (Esc)", UiKit.Kind.Danger)) PickingTarget = false;
        return r;
    }

    // Строка про похлёбку под галочкой
    string PottageInfoLine()
    {
        int avail = KitchenController.PortionsAvailable;
        if (KitchenController.All.Count == 0) return UiKit.Col(UiKit.MutedHex, "Кухни нет");
        string link = KitchenController.AnyLinked(Bld)
            ? UiKit.Col(UiKit.GoodHex, "кухня вплотную")
            : UiKit.Col(UiKit.WarnHex, "кухня не рядом — несут курьеры");
        return $"Похлёбки: <b>{avail}</b> порц. · {link}";
    }

    // ---------- Вкладка «Обучение» ----------

    List<UnitType> UnlockedUnits()
    {
        return availableUnits != null ? availableUnits.FindAll(u => u != null && Unlocked(u)) : new List<UnitType>();
    }

    bool AnyPottageUnit(List<UnitType> units)
    {
        foreach (var u in units) if (u.UsesPottage) return true;
        return false;
    }

    // Ближайший уровень казармы, на котором откроются новые бойцы (0 - больше нечего открывать)
    int NextUnlockLevel(out string names)
    {
        names = "";
        int lvl = Bld != null ? Bld.level : 1;
        int next = 0;
        if (availableUnits == null) return 0;
        foreach (var u in availableUnits)
            if (u != null && !Unlocked(u) && DevelopmentPath.Allows(u.path) && DevelopmentPath.EngAllows(u.engSchool) && u.minBarracksLevel > lvl && (next == 0 || u.minBarracksLevel < next))
                next = u.minBarracksLevel;
        if (next == 0) return 0;
        var list = new List<string>();
        foreach (var u in availableUnits)
            if (u != null && u.minBarracksLevel == next && DevelopmentPath.Allows(u.path) && DevelopmentPath.EngAllows(u.engSchool)) list.Add(u.unitName);
        names = string.Join(", ", list);
        return next;
    }

    float TrainTabHeight()
    {
        var units = UnlockedUnits();
        float h = Mathf.Max(1, units.Count) * UnitRowH + 4f;
        if (NextUnlockLevel(out _) > 0) h += 20f;
        if (AnyPottageUnit(units)) h += 6f + 22f + 20f;
        h += 10f + UpgradeRules.SectionHeight(Bld);
        return h;
    }

    // Подсказка к виду войск: характеристики и особенности
    string UnitTip(UnitType u)
    {
        string t = $"<b>{u.unitName}</b>\n";
        t += $"Здоровье {u.maxHp:F0} · урон {u.damage:F0} · удар раз в {u.attackCooldown:0.#} с\n";
        t += $"Скорость {u.moveSpeed:0.#} · {(u.attackRange > 2f ? "бьёт издалека" : "ближний бой")}";
        if (u.IsHealer) t += $"\nЛечит своих рядом в бою ({u.healPerSecond:0.#} в сек.)";
        if (u.unitName == RaidManager.ScoutUnitName) t += "\nБыстрый, видит дальше. Нужен для разведки перед набегом.";
        if (u.IsPoisoner) t += "\nЯдовитые снаряды по площади. Сила яда — от маны в воздухе.";
        if (u.IsElementalist) t += "\nИногда огненный шар по площади; у реки — волна, долго замедляет врагов. Сила — от маны." +
                                   (DevelopmentPath.IsChaos ? $"\n<color=#C9A6FF>Ядро хаоса</color> (берёт сам в казарме): раз в {ChaosMagic.CooldownSeconds:F0} с чёрная дыра — стягивает врагов." : "");
        if (u.IsFireBomber) t += $"\nВыплёскивает ведро горящей смолы по площади, потом бьётся кулаками. Ведро заправляется в казарме ({RefillResin} смолы).";
        if (u.IsCatapult) t += $"\nКамни навесом через стены, удар по площади (радиус {u.catapultRadius:0.#}). Дальность {u.catapultMinRange:0}–{u.attackRange:0}: " +
                                $"вблизи урон {u.damage:F0}, на пределе — {u.damage * u.catapultFarMul:F0}. По зданиям в набеге ×{u.catapultBuildingMul:0.#}.\n" +
                                "Расчёт — один житель. В лагере («Послать...») стоит на месте и бьёт всё в радиусе.";
        if (u.IsFieldCannon) t += "\nЯдра прямой наводкой: лучшая против стен, ворот и зданий; осколки ранят тех, кто за целью. Модули и снаряды берёт сама; вид снарядов - в «Снаряж.».";
        if (u.requiresHorse) t += $"\nНужна лошадь из Конюшни (свободно: {StableController.HorsesAvailable}). Гибнет вместе со всадником.";
        if (u.UsesPottage) t += $"\nС похлёбкой ({u.PottagePortions} порц.) выходит усиленным ★.";
        if (u.usesWeapon || u.usesShield)
        {
            string kind = u.weaponKind == WeaponKind.Bow ? "лук" : (u.weaponKind == WeaponKind.Shot ? "бронебойное ядро" : "меч");
            t += "\nСнаряжение из Оружейной: " + (u.usesWeapon ? kind : "") + (u.usesWeapon && u.usesShield ? ", " : "") + (u.usesShield ? "щит" : "");
        }
        if (!u.isCannon && !u.isCatapult && (DevelopmentPath.IsHeavy || DevelopmentPath.IsCamo))
            t += "\nНа тело: " + (DevelopmentPath.IsHeavy ? "броня (−25…36% урона, −10% скорости)" : "плащ из листвы (замечают вдвое ближе)");
        t += $"\nОбучение {u.trainTime:0.#} с · мест в казарме: {u.spaceOccupied}";
        return t;
    }

    // Дополнительные затраты (кроме золота и еды); missing - чего-то не хватает
    string UnitExtras(UnitType u, int portions, out bool missing)
    {
        missing = false;
        var parts = new List<string>();
        if (u.requiresHorse)
        {
            parts.Add($"конь ({StableController.HorsesAvailable})");
            if (StableController.HorsesAvailable <= 0) missing = true;
        }
        if (portions > 0)
        {
            parts.Add($"{portions} пох.");
            if (KitchenController.PortionsAvailable < portions) missing = true;
        }
        if (u.potionCost > 0)
        {
            parts.Add($"{u.potionCost} зел.");
            if (PotionHouse.TotalPotions < u.potionCost) missing = true;
        }
        if (u.resinCost > 0)
        {
            parts.Add($"{u.resinCost} смолы");
            if (ResourceManager.Instance == null || ResourceManager.Instance.resin < u.resinCost) missing = true;
        }
        if (u.steelCost > 0) parts.Add($"{u.steelCost} стали");
        if (u.planksCost > 0) parts.Add($"{u.planksCost} досок");
        if (u.partsCost > 0)
        {
            parts.Add($"{u.partsCost} дет.");
            if (ResourceManager.Instance == null || ResourceManager.Instance.parts < u.partsCost) missing = true;
        }
        return string.Join(", ", parts);
    }

    // Почему нельзя обучить прямо сейчас ("" - можно)
    string TrainProblem(UnitType u, int portions)
    {
        if (Bld != null && !Bld.isWorking) return Title + " не работает: " + Bld.StatusText;
        if (IsTraining) return Title + " уже обучает: " + CurrentTrainingName;
        if (u.partsCost > 0 && (ResourceManager.Instance == null || ResourceManager.Instance.parts < u.partsCost)) return $"Мало деталей: нужно {u.partsCost} (Литейная)";
        if (u.requiresHorse && StableController.HorsesAvailable <= 0) return "Нет свободных лошадей в Конюшне";
        if (portions > 0 && KitchenController.PortionsAvailable < portions) return $"Мало похлёбки: нужно {portions} порц.";
        if (u.potionCost > 0 && PotionHouse.TotalPotions < u.potionCost) return $"Мало зелий: нужно {u.potionCost} (Зельеварня)";
        if (u.resinCost > 0 && (ResourceManager.Instance == null || ResourceManager.Instance.resin < u.resinCost)) return $"Мало смолы: нужно {u.resinCost}";
        return "";
    }

    float DrawTrainTab(float x, float y, float w)
    {
        float cy = y;
        var units = UnlockedUnits();
        GUIStyle nameSt = Mid(UiKit.Body, false);
        GUIStyle costSt = Mid(UiKit.Small, true);
        GUIStyle infoSt = Mid(UiKit.MutedStyle, true);
        infoSt.fontSize = 11;

        const float btnW = 82f, infoW = 46f, nameW = 128f;
        float costW = w - nameW - infoW - btnW - 12f;

        if (units.Count == 0)
        {
            string none = IsEngineer && !DevelopmentPath.IsMilitaryEng ? "Нужно направление инженеров «Военный инженер»" : "Нет доступных бойцов";
            GUI.Label(new Rect(x, cy, w, UnitRowH), UiKit.Col(UiKit.MutedHex, none), nameSt);
            cy += UnitRowH;
        }

        foreach (var unit in units)
        {
            int portions = PottageNeed(unit);
            string star = unit.UsesPottage ? UiKit.Col(UiKit.AccentHex, " ★") : "";
            Rect nameR = new Rect(x, cy, nameW, UnitRowH - 2f);
            GUI.Label(nameR, $"<b>{unit.unitName}</b>{star}", nameSt);
            UiKit.Tip(nameR, UnitTip(unit));

            string extras = UnitExtras(unit, portions, out bool missing);
            string cost = UiKit.Cost(unit.costGold, 0, 0, unit.costFood);
            if (extras != "") cost += "\n" + UiKit.Col(missing ? UiKit.WarnHex : UiKit.MutedHex, "+ " + extras);
            Rect costR = new Rect(x + nameW + 4f, cy, costW, UnitRowH - 2f);
            GUI.Label(costR, cost, costSt);
            if (extras != "") UiKit.Tip(costR, UnitTip(unit));

            Rect infoR = new Rect(x + nameW + costW + 8f, cy, infoW, UnitRowH - 2f);
            GUI.Label(infoR, $"{unit.trainTime:0.#} с\n{unit.spaceOccupied} м.", infoSt);
            UiKit.Tip(infoR, $"Обучение {unit.trainTime:0.#} с · мест в казарме: {unit.spaceOccupied}");

            string problem = TrainProblem(unit, portions);
            if (UiKit.Button(new Rect(x + w - btnW, cy + 2f, btnW, UnitRowH - 6f), "Обучить", problem == "", UiKit.Kind.Primary,
                    problem == "" ? $"Обучить: {unit.unitName}" : problem))
                TrainUnit(unit);

            UiKit.Line(x, cy + UnitRowH - 1f, w);
            cy += UnitRowH;
        }
        cy += 4f;

        // Какие бойцы откроются дальше
        int next = NextUnlockLevel(out string nextNames);
        if (next > 0)
        {
            Rect nr = new Rect(x, cy, w, 20f);
            GUI.Label(nr, UiKit.Col(UiKit.MutedHex, $"Ещё бойцы — с Ур. {next} казармы"), UiKit.Small);
            UiKit.Tip(nr, "Откроются: " + nextNames);
            cy += 20f;
        }

        // --- Похлёбка (только если есть бойцы, которым её дают) ---
        if (AnyPottageUnit(units))
        {
            cy += 6f;
            Rect tr = new Rect(x, cy, w, 22f);
            bool usePot = GUI.Toggle(tr, UsePottage, " <b>С похлёбкой</b> " + UiKit.Col(UiKit.AccentHex, "★"));
            UiKit.Tip(tr, "Бойцы ★ выходят усиленными: +35% здоровья, +25% урона, +10% скорости.\n" +
                          "Порции берутся с Кухни. Кухня вплотную к казарме (или к группе казарм) - подаётся сразу, иначе порцию несёт житель, и обучение ждёт.");
            if (usePot != UsePottage) UsePottage = usePot;
            cy += 22f;
            GUI.Label(new Rect(x + 18f, cy, w - 18f, 20f), PottageInfoLine(), UiKit.Small);
            cy += 20f;
        }

        // --- Улучшение казармы ---
        cy += 10f;
        UiKit.Line(x, cy - 5f, w);
        cy += UpgradeRules.DrawUpgradeSection(x, cy, w, Bld);
        return cy - y;
    }

    // ---------- Вкладка «Войска» ----------

    float ArmyTabHeight()
    {
        return UiKit.SectionH + SquadSectionHeight() + 24f + ListH + 6f + UiKit.SectionH + 32f + 32f + 30f;
    }

    void FindSoldier(CitizenAgent s)
    {
        focused = s;
        focusUntil = Time.unscaledTime + 5f;
        Vector2 p = s.IsInsideBuilding && Bld != null ? Bld.Center : (Vector2)s.transform.position;
        if (CameraController.Instance != null) CameraController.Instance.FocusOn(p);
    }

    float DrawArmyTab(float x, float y, float w)
    {
        float cy = y;
        GUIStyle small = UiKit.Small;
        GUIStyle toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 12, richText = true, wordWrap = false, clipping = TextClipping.Clip };

        // --- Отряды ---
        cy = UiKit.Section(x, cy, w, "Отряды");
        UiKit.Tip(new Rect(x, cy - UiKit.SectionH, w, UiKit.SectionH),
            "Новые бойцы встают в отряд, который дома. Приказ получает весь отряд; если отмечена только часть, из неё делается новый отряд.");
        if (squads.Count == 0)
            GUI.Label(new Rect(x, cy, w, 20f), UiKit.Col(UiKit.MutedHex, "Отрядов пока нет"), small);
        for (int i = 0; i < squads.Count; i++)
        {
            Squad sq = squads[i];
            GUI.Label(new Rect(x, cy + i * 20f, w, 20f), $"<b>{sq.Name}</b> ({sq.Members.Count}) " + UiKit.Col(UiKit.MutedHex, "— " + sq.StatusText), Mid(small, false));
        }
        cy += SquadSectionHeight();

        // --- Список бойцов ---
        GUI.Label(new Rect(x, cy, w - 130f, 22f), $"<b>Бойцы: {soldiers.Count}</b>  " + UiKit.Col(UiKit.MutedHex, $"отмечено: {selection.Count}"), Mid(UiKit.Body, false));
        if (UiKit.Button(new Rect(x + w - 122f, cy, 56f, 22f), "Все", UiKit.Kind.Ghost, "Отметить всех"))
            foreach (var s in soldiers) selection.Add(s);
        if (UiKit.Button(new Rect(x + w - 62f, cy, 62f, 22f), "Сброс", UiKit.Kind.Ghost, "Снять отметки"))
            selection.Clear();
        cy += 24f;

        float innerW = w - 14f;
        scroll = GUI.BeginScrollView(new Rect(x, cy, w, ListH), scroll,
            new Rect(0f, 0f, innerW, Mathf.Max(ListH, soldiers.Count * SoldierRowH)));

        if (soldiers.Count == 0)
            GUI.Label(new Rect(0f, 0f, innerW, 22f), UiKit.Col(UiKit.MutedHex, "Бойцов нет — обучите их на вкладке «Обучение»"), small);

        CitizenAgent toDismiss = null;
        for (int i = 0; i < soldiers.Count; i++)
        {
            CitizenAgent s = soldiers[i];
            float ry = i * SoldierRowH;

            bool sel = selection.Contains(s);
            float hpFrac = s.MaxHp > 0f ? s.Hp / s.MaxHp : 1f;
            string hp = UiKit.Col(hpFrac < 0.35f ? UiKit.BadHex : (hpFrac < 0.7f ? UiKit.WarnHex : UiKit.MutedHex), $"{Mathf.CeilToInt(s.Hp)}/{Mathf.CeilToInt(s.MaxHp)}");
            Rect tr = new Rect(0f, ry, innerW - 150f, 24f);
            bool nsel = GUI.Toggle(tr, sel, $" [{(s.Squad != null ? s.Squad.Number : 0)}] {s.DisplayName} {hp} " + UiKit.Col(UiKit.MutedHex, s.StatusText), toggle);
            if (nsel != sel) { if (nsel) selection.Add(s); else selection.Remove(s); }
            string chaosLine = s.HasChaosCore ? "\n" + UiKit.Col("#C9A6FF", s.ChaosCoreText) : "";
            string cannonLine = s.IsFieldCannon ? "\n" + CannonGearLine(s) : "";
            UiKit.Tip(tr, $"<b>{s.DisplayName}</b> {s.CitizenName}\nЗдоровье {Mathf.CeilToInt(s.Hp)}/{Mathf.CeilToInt(s.MaxHp)}\n{s.StatusText}{chaosLine}{cannonLine}");

            bool canGear = s.CanUseWeapon || UnitUsesShield(s) || ShowBody(s);
            bool geared = s.EquippedWeapon != null || ShieldBearer.ShieldOf(s) != null || s.EquippedBody != null;
            if (UiKit.Button(new Rect(innerW - 146f, ry, 66f, 24f), geared ? "Снаряж.✓" : "Снаряж.", canGear, UiKit.Kind.Normal,
                    canGear ? "Оружие, щит, броня или плащ бойца" : "Этому бойцу снаряжение не положено"))
            {
                equipFor = s;
                tab = 2;
            }

            if (UiKit.Button(new Rect(innerW - 78f, ry, 50f, 24f), "Найти", UiKit.Kind.Normal, "Показать на карте"))
                FindSoldier(s);
            if (UiKit.Button(new Rect(innerW - 26f, ry, 26f, 24f), "×", UiKit.Kind.Danger, "Уволить со службы")) toDismiss = s;
        }
        GUI.EndScrollView();
        cy += ListH + 6f;

        if (toDismiss != null) Dismiss(toDismiss);

        // --- Приказы ---
        float secY = cy;
        cy = UiKit.Section(x, cy, w, "Приказ");
        var rightMuted = new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.LowerRight, wordWrap = false, fontSize = 11 };
        GUI.Label(new Rect(x, secY, w, UiKit.SectionH - 4f), selection.Count > 0 ? $"отмеченным ({selection.Count})" : "всем (никто не отмечен)", rightMuted);

        float bw = (w - 8f) / 3f;
        if (UiKit.Button(new Rect(x, cy, bw, UiKit.BtnH), "Внутрь", UiKit.Kind.Normal, "Зайти в казарму и сидеть внутри")) { PickingTarget = false; OrderGarrison(); }
        if (UiKit.Button(new Rect(x + 4f + bw, cy, bw, UiKit.BtnH), "Охранять", UiKit.Kind.Normal, "Охранять казарму вокруг неё")) { PickingTarget = false; OrderGuard(); }
        if (UiKit.Button(new Rect(x + 8f + 2f * bw, cy, bw, UiKit.BtnH), PickingTarget ? "Отмена" : "Послать...", soldiers.Count > 0 || PickingTarget,
                PickingTarget ? UiKit.Kind.Danger : UiKit.Kind.Primary,
                soldiers.Count > 0 ? "Кликнуть по карте: отряд пойдёт туда, разобьёт лагерь и будет жить на своих припасах (Esc - отмена)" +
                    (IsEngineer ? "\nКатапульты там встанут и будут бить всё в радиусе, не сходя с места." : "") : "Нет бойцов"))
            PickingTarget = !PickingTarget && soldiers.Count > 0;
        cy += 32f;

        // --- Поведение отряда в лагере ---
        List<Squad> tsq = TargetSquads();
        bool allPatrol = tsq.Count > 0, allHold = tsq.Count > 0;
        foreach (var q in tsq)
        {
            if (q.PatrolAtCamp) allHold = false; else allPatrol = false;
        }

        float hw = (w - 4f) / 2f;
        Rect holdR = new Rect(x, cy, hw, UiKit.BtnH);
        if (GUI.Toggle(holdR, allHold, "Лагерь: стоять", GUI.skin.button) != allHold)
            foreach (var q in tsq) q.SetPatrol(false);
        UiKit.Tip(holdR, "В лагере бойцы стоят на месте");
        Rect patrolR = new Rect(x + 4f + hw, cy, hw, UiKit.BtnH);
        if (GUI.Toggle(patrolR, allPatrol, "Лагерь: патруль", GUI.skin.button) != allPatrol)
            foreach (var q in tsq) q.SetPatrol(true);
        UiKit.Tip(patrolR, "В лагере бойцы обходят окрестности");
        cy += 32f;

        bool any = selection.Count > 0;
        if (UiKit.Button(new Rect(x, cy, hw, UiKit.BtnH), "Новый отряд", any, UiKit.Kind.Normal,
                any ? "Сформировать новый отряд из отмеченных" : "Сначала отметьте бойцов галочками"))
            FormSquadFromSelected();
        if (UiKit.Button(new Rect(x + 4f + hw, cy, hw, UiKit.BtnH), "Уволить отмеченных", any, UiKit.Kind.Danger,
                any ? "Уволить отмеченных со службы" : "Сначала отметьте бойцов галочками"))
            DismissSelected();
        cy += 30f;

        return cy - y;
    }

    // ---------- Вкладка «Снаряжение» ----------

    List<CitizenAgent> GearableSoldiers()
    {
        var list = new List<CitizenAgent>();
        foreach (var s in soldiers) if (s != null && (s.CanUseWeapon || UnitUsesShield(s) || ShowBody(s))) list.Add(s);
        return list;
    }

    float GearTabHeight()
    {
        if (equipFor != null) return EquipBodyHeight(equipFor);
        return 34f + UiKit.SectionH + ListH;
    }

    float DrawGearTab(float x, float y, float w)
    {
        if (equipFor != null) return DrawEquipBody(x, y, w);

        float cy = y;
        if (UiKit.Button(new Rect(x, cy, w, UiKit.BtnH), "Выдать лучшее всем", UiKit.Kind.Primary,
                "Каждому бойцу без оружия, щита, брони или плаща - лучшее, что есть в Оружейных (разведчикам и стрелкам - плащи, остальным - броня)"))
            AutoEquipAll();
        cy += 34f;

        cy = UiKit.Section(x, cy, w, "Снаряжение бойцов");
        var list = GearableSoldiers();
        float innerW = w - 14f;
        gearListScroll = GUI.BeginScrollView(new Rect(x, cy, w, ListH), gearListScroll,
            new Rect(0f, 0f, innerW, Mathf.Max(ListH, list.Count * SoldierRowH)));
        if (list.Count == 0)
            GUI.Label(new Rect(0f, 0f, innerW, 40f), UiKit.Col(UiKit.MutedHex, "Нет бойцов, которым положено снаряжение"), UiKit.Small);

        GUIStyle row = Mid(UiKit.Small, false);
        for (int i = 0; i < list.Count; i++)
        {
            CitizenAgent s = list[i];
            float ry = i * SoldierRowH;
            string wpn = s.CanUseWeapon ? (s.EquippedWeapon != null ? s.EquippedWeapon.Name : UiKit.Col(UiKit.WarnHex, "без оружия")) : "";
            string shd = "";
            if (UnitUsesShield(s))
            {
                Shield sh = ShieldBearer.ShieldOf(s);
                shd = sh != null ? sh.Name : UiKit.Col(UiKit.WarnHex, "без щита");
            }
            string gear = wpn + (wpn != "" && shd != "" ? UiKit.Col(UiKit.MutedHex, " · ") : "") + shd;
            BodyGear bg = s.EquippedBody;
            if (bg != null) gear += (gear != "" ? UiKit.Col(UiKit.MutedHex, " · ") : "") + UiKit.Col(bg.IsArmor ? "#B8C4D0" : "#90D070", bg.Name);
            if (s.IsFieldCannon && s.CannonHasShells)
                gear += UiKit.Col(UiKit.MutedHex, " · ") + UiKit.Col("#D0A0FF", $"{ShellShort(s.CannonShellKind)} {s.CannonShellCharges}");
            Rect lr = new Rect(0f, ry, innerW - 80f, SoldierRowH - 2f);
            GUI.Label(lr, $"<b>{s.DisplayName}</b> " + UiKit.Col(UiKit.MutedHex, s.CitizenName) + "  " + gear, row);
            if (UiKit.Button(new Rect(innerW - 74f, ry, 74f, SoldierRowH - 2f), "Выбрать", UiKit.Kind.Normal, "Сменить оружие, щит, броню или плащ"))
                equipFor = s;
        }
        GUI.EndScrollView();
        cy += ListH;
        return cy - y;
    }

    // Высота карточки снаряжения выбранного бойца (должна совпадать с DrawEquipBody)
    float EquipBodyHeight(CitizenAgent s)
    {
        bool canWeapon = s.CanUseWeapon;
        bool canShield = UnitUsesShield(s);
        bool canBody = ShowBody(s);
        int wn = canWeapon ? ArmoryController.Available(s.WeaponKindAllowed).Count : 0;
        int sn = canShield ? ArmoryController.AvailableShields().Count : 0;
        int bn = canBody ? ArmoryController.AvailableBodies().Count : 0;
        float h = 30f;
        if (canWeapon) h += 26f;
        if (canShield) h += 26f;
        if (canBody) h += 26f;
        h += 4f;
        if (canWeapon) h += UiKit.SectionH + Mathf.Clamp(wn * EquipRowH, EquipRowH * 2f, EquipRowH * 4f) + 6f;
        if (canShield) h += UiKit.SectionH + Mathf.Clamp(sn * EquipRowH, EquipRowH * 2f, EquipRowH * 4f) + 6f;
        if (canBody) h += UiKit.SectionH + Mathf.Clamp(bn * EquipRowH, EquipRowH * 2f, EquipRowH * 4f) + 6f;
        if (s.IsFieldCannon) h += CannonShellsH;
        return h;
    }

    // ---------- Полевая пушка: снаряды и подъёмник ----------

    const float CannonShellsH = UiKit.SectionH + 22f + 30f + 6f;   // раздел + строка + кнопки + отступ

    static string ShellShort(ModuleKind k)
    {
        return k == ModuleKind.FireShells ? "Огонь" : (k == ModuleKind.FrostShells ? "Лёд" : (k == ModuleKind.ArcaneShells ? "Чары" : "-"));
    }

    // Строка «снаряды · подъёмник» для подсказок
    static string CannonGearLine(CitizenAgent s)
    {
        string sh = s.CannonHasShells
            ? UiKit.Col("#D0A0FF", $"{TowerModules.Name(s.CannonShellKind)} Мк.{TowerModules.Roman(s.CannonShellMk)}: {s.CannonShellCharges}")
            : UiKit.Col(UiKit.MutedHex, "снарядов нет") + (s.CannonShellAuto ? "" : UiKit.Col(UiKit.MutedHex, " (авто выкл.)"));
        string lift = s.CannonLift.Has ? TowerModules.KitName(ModuleKind.CannonLift, s.CannonLift.mk) : UiKit.Col(UiKit.MutedHex, "без подъёмника");
        return "Снаряды: " + sh + UiKit.Col(UiKit.MutedHex, " · ") + lift;
    }

    float DrawCannonShells(float x, float y, float w, CitizenAgent s)
    {
        float cy = UiKit.Section(x, y, w, "Магические снаряды");
        Rect lr = new Rect(x, cy, w, 22f);
        GUI.Label(lr, CannonGearLine(s), Mid(UiKit.Small, false));
        UiKit.Tip(lr, s.CannonHasShells ? MagicShells.Stats(s.CannonShellKind, s.CannonShellMk)
            : "Пачки делает Магическая оружейная или продаёт рынок. С «Авто» пушка берёт пачку сама, когда она дома и не в бою.");
        cy += 22f;

        float bw = (w - 16f) / 5f;
        float bx = x;
        foreach (var k in MagicShells.Kinds)
        {
            int n = MagicShells.Packs(k);
            bool cur = s.CannonHasShells && s.CannonShellKind == k;
            string label = (s.CannonShellPref == k && s.CannonShellAuto ? "• " : "") + $"{ShellShort(k)} ({n})";
            string tip = n > 0 ? $"Зарядить: {TowerModules.Name(k)} (пачек: {n}). Прежняя пачка - в Ратушу. Этот вид пушка будет брать сама."
                               : $"{TowerModules.Name(k)}: пачек нет - их делает Магическая оружейная или продаёт рынок";
            if (UiKit.Button(new Rect(bx, cy, bw, 28f), label, n > 0, cur ? UiKit.Kind.Primary : UiKit.Kind.Normal, tip))
            {
                if (s.LoadCannonShellsOf(k))
                    WarManager.Notify($"{s.UnitName} {s.CitizenName}: заряжена — {TowerModules.KitName(k, s.CannonShellMk)}", true);
            }
            bx += bw + 4f;
        }
        Rect ar = new Rect(bx, cy, bw, 28f);
        bool auto = GUI.Toggle(ar, s.CannonShellAuto, s.CannonShellAuto ? "Авто ✓" : "Авто", GUI.skin.button);
        if (auto != s.CannonShellAuto) s.CannonShellAuto = auto;
        UiKit.Tip(ar, "Снарядов нет - пушка сама берёт пачку со склада (где бы ни стояла дома, не в бою)");
        bx += bw + 4f;
        if (UiKit.Button(new Rect(bx, cy, bw, 28f), "Снять", s.CannonHasShells, UiKit.Kind.Danger,
                s.CannonHasShells ? "Пачку - обратно в Ратушу (Авто выключится)" : "Снарядов нет"))
            s.UnloadCannonShells();
        cy += 30f + 6f;
        return cy;
    }

    float DrawEquipBody(float x, float y, float w)
    {
        CitizenAgent s = equipFor;
        float cy = y;

        bool canWeapon = s.CanUseWeapon;
        bool canShield = UnitUsesShield(s);
        var wOpts = canWeapon ? ArmoryController.Available(s.WeaponKindAllowed) : new List<ArmoryController.StockEntry>();
        var sOpts = canShield ? ArmoryController.AvailableShields() : new List<ArmoryController.ShieldEntry>();
        float wListH = Mathf.Clamp(wOpts.Count * EquipRowH, EquipRowH * 2f, EquipRowH * 4f);
        float sListH = Mathf.Clamp(sOpts.Count * EquipRowH, EquipRowH * 2f, EquipRowH * 4f);
        Shield curShield = ShieldBearer.ShieldOf(s);
        bool canBody = ShowBody(s);
        var bOpts = canBody ? ArmoryController.AvailableBodies() : new List<ArmoryController.BodyEntry>();
        float bListH = Mathf.Clamp(bOpts.Count * EquipRowH, EquipRowH * 2f, EquipRowH * 4f);
        BodyGear curBody = s.EquippedBody;
        bool slots = ArmoryController.FreeSlotsTotal > 0;
        string noSlots = "На складах Оружейных нет места";

        // Шапка: назад к списку + кто
        bool back = UiKit.Button(new Rect(x, cy, 84f, 24f), "← Бойцы", UiKit.Kind.Ghost, "К списку бойцов");
        GUI.Label(new Rect(x + 92f, cy, w - 92f, 24f),
            $"<b>{s.DisplayName}</b> {s.CitizenName}" + (s.HasHorse ? UiKit.Col(UiKit.MutedHex, " (верхом)") : ""), Mid(UiKit.Body, false));
        cy += 30f;

        GUIStyle line = Mid(UiKit.Body, false);

        // --- Что надето ---
        if (canWeapon)
        {
            Weapon cw = s.EquippedWeapon;
            Rect lr = new Rect(x, cy, w - 90f, 24f);
            GUI.Label(lr, UiKit.Col(UiKit.MutedHex, "Оружие: ") + (cw != null ? $"<b>{cw.Name}</b>" : UiKit.Col(UiKit.WarnHex, "нет")), line);
            if (cw != null)
            {
                UiKit.Tip(lr, cw.StatsText);
                if (UiKit.Button(new Rect(x + w - 84f, cy, 84f, 24f), "Снять", slots, UiKit.Kind.Normal, slots ? "Оружие - на склад Оружейной" : noSlots))
                {
                    Weapon old = s.UnequipWeapon();
                    if (old != null && !ArmoryController.Stash(old)) s.EquipWeapon(old);
                }
            }
            cy += 26f;
        }
        if (canShield)
        {
            Rect lr = new Rect(x, cy, w - 90f, 24f);
            GUI.Label(lr, UiKit.Col(UiKit.MutedHex, "Щит: ") + (curShield != null ? $"<b>{curShield.Name}</b>" : UiKit.Col(UiKit.WarnHex, "нет")), line);
            if (curShield != null)
            {
                UiKit.Tip(lr, curShield.StatsText);
                if (UiKit.Button(new Rect(x + w - 84f, cy, 84f, 24f), "Снять", slots, UiKit.Kind.Normal, slots ? "Щит - на склад Оружейной" : noSlots))
                {
                    Shield old = ShieldBearer.Unequip(s);
                    if (old != null && !ArmoryController.StashShield(old)) ShieldBearer.Equip(s, old);
                }
            }
            cy += 26f;
        }
        if (canBody)
        {
            Rect lr = new Rect(x, cy, w - 90f, 24f);
            string bodyName = curBody != null ? UiKit.Col(curBody.IsArmor ? "#B8C4D0" : "#90D070", $"<b>{curBody.Name}</b>") + UiKit.Col(UiKit.MutedHex, $" {curBody.Percent}%") : UiKit.Col(UiKit.WarnHex, "нет");
            GUI.Label(lr, UiKit.Col(UiKit.MutedHex, "На теле: ") + bodyName, line);
            UiKit.Tip(lr, curBody != null ? curBody.StatsText : "Броня (Тяжёлая стратегия) или плащ из листвы (Мастер маскировки) из Оружейной. Только одно из двух.");
            if (curBody != null && UiKit.Button(new Rect(x + w - 84f, cy, 84f, 24f), "Снять", slots, UiKit.Kind.Normal, slots ? "На склад Оружейной" : noSlots))
            {
                BodyGear old = s.UnequipBody();
                if (old != null && !ArmoryController.StashBody(old)) s.EquipBody(old);
            }
            cy += 26f;
        }
        cy += 4f;

        GUIStyle itemSt = Mid(UiKit.Small, false);
        float innerW = w - 14f;

        // --- Оружие на складах ---
        if (canWeapon)
        {
            string kindName = s.WeaponKindAllowed == WeaponKind.Bow ? "луки" : (s.WeaponKindAllowed == WeaponKind.Shot ? "ядра" : "мечи");
            cy = UiKit.Section(x, cy, w, $"В оружейных: {kindName}");
            equipScroll = GUI.BeginScrollView(new Rect(x, cy, w, wListH), equipScroll,
                new Rect(0f, 0f, innerW, Mathf.Max(wListH, wOpts.Count * EquipRowH)));

            if (wOpts.Count == 0)
            {
                Rect er = new Rect(0f, 0f, innerW, 40f);
                GUI.Label(er, UiKit.Col(UiKit.MutedHex, "Нет подходящего оружия"), UiKit.Small);
                UiKit.Tip(er, "Постройте Оружейную и закажите ковку.");
            }

            for (int i = 0; i < wOpts.Count; i++)
            {
                var o = wOpts[i];
                float ry = i * EquipRowH;
                GUI.Label(new Rect(0f, ry, innerW - 70f, EquipRowH - 2f),
                    $"<b>{o.weapon.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"×{o.weapon.DamageMul:F2} · {o.weapon.Percent}%"), itemSt);
                if (UiKit.Button(new Rect(innerW - 64f, ry, 64f, EquipRowH - 4f), "Взять", UiKit.Kind.Primary, o.weapon.StatsText))
                {
                    EquipFrom(s, o.armory, o.weapon);
                    break;   // список изменился - перерисуем в следующем кадре
                }
            }
            GUI.EndScrollView();
            cy += wListH + 6f;
        }

        // --- Щиты на складах ---
        if (canShield)
        {
            cy = UiKit.Section(x, cy, w, "В оружейных: щиты");
            shieldScroll = GUI.BeginScrollView(new Rect(x, cy, w, sListH), shieldScroll,
                new Rect(0f, 0f, innerW, Mathf.Max(sListH, sOpts.Count * EquipRowH)));

            if (sOpts.Count == 0)
            {
                Rect er = new Rect(0f, 0f, innerW, 40f);
                GUI.Label(er, UiKit.Col(UiKit.MutedHex, "Нет щитов"), UiKit.Small);
                UiKit.Tip(er, "Закажите щиты в Оружейной.");
            }

            for (int i = 0; i < sOpts.Count; i++)
            {
                var o = sOpts[i];
                float ry = i * EquipRowH;
                GUI.Label(new Rect(0f, ry, innerW - 70f, EquipRowH - 2f),
                    $"<b>{o.shield.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"блок {o.shield.BlockChance * 100f:F0}% · {o.shield.Percent}%"), itemSt);
                string tip = $"Блок {o.shield.BlockChance * 100f:F0}%, урон −{o.shield.ReduceFraction * 100f:F0}%, отражение {o.shield.ReflectChance * 100f:F1}%, состояние {o.shield.Percent}%";
                if (UiKit.Button(new Rect(innerW - 64f, ry, 64f, EquipRowH - 4f), "Взять", UiKit.Kind.Primary, tip))
                {
                    EquipShieldFrom(s, o.armory, o.shield);
                    break;
                }
            }
            GUI.EndScrollView();
            cy += sListH + 6f;
        }

        // --- Броня и плащи на складах ---
        if (canBody)
        {
            cy = UiKit.Section(x, cy, w, "В оружейных: броня и плащи");
            bodyScroll = GUI.BeginScrollView(new Rect(x, cy, w, bListH), bodyScroll,
                new Rect(0f, 0f, innerW, Mathf.Max(bListH, bOpts.Count * EquipRowH)));

            if (bOpts.Count == 0)
            {
                Rect er = new Rect(0f, 0f, innerW, 40f);
                GUI.Label(er, UiKit.Col(UiKit.MutedHex, "Нет брони и плащей"), UiKit.Small);
                UiKit.Tip(er, DevelopmentPath.IsHeavy ? "Закажите броню в Оружейной: сталь и доски."
                    : (DevelopmentPath.IsCamo ? "Закажите плащ в Оружейной: листва (Лесопилка Ур.4) и немного золота." : "Броню и плащи делают ветки Военного дела."));
            }

            for (int i = 0; i < bOpts.Count; i++)
            {
                var o = bOpts[i];
                float ry = i * EquipRowH;
                string col = o.gear.IsArmor ? "#B8C4D0" : "#90D070";
                string brief = o.gear.IsArmor ? $"−{o.gear.Reduce * 100f:F0}% урона" : "скрытность";
                GUI.Label(new Rect(0f, ry, innerW - 70f, EquipRowH - 2f),
                    UiKit.Col(col, $"<b>{o.gear.Name}</b>") + "  " + UiKit.Col(UiKit.MutedHex, $"{brief} · {o.gear.Percent}%"), itemSt);
                if (UiKit.Button(new Rect(innerW - 64f, ry, 64f, EquipRowH - 4f), "Надеть", UiKit.Kind.Primary, o.gear.StatsText))
                {
                    EquipBodyFrom(s, o.armory, o.gear);
                    break;
                }
            }
            GUI.EndScrollView();
            cy += bListH + 6f;
        }

        if (s.IsFieldCannon) cy = DrawCannonShells(x, cy, w, s);

        if (back) equipFor = null;
        return cy - y;
    }

    // ---------- Панель ----------

    // Шапка: строка обучения + строка мест (+ строка «не работает»)
    float HeaderHeight()
    {
        float h = UiKit.RowH + UiKit.RowH;
        if (Bld != null && !Bld.isWorking) h += UiKit.RowH;
        return h + 4f;
    }

    float TabHeight(int t)
    {
        if (t == 1) return ArmyTabHeight();
        if (t == 2) return GearTabHeight();
        return TrainTabHeight();
    }

    /// <summary>Рисует панель казармы. Вызывается из BuildingPlacer.OnGUI. Возвращает занятый прямоугольник.</summary>
    public Rect DrawPanel(float x, float y, float w)
    {
        soldiers.RemoveAll(s => s == null);
        selection.RemoveWhere(s => s == null);
        if (equipFor != null && !soldiers.Contains(equipFor)) equipFor = null;

        const float tabsH = 26f;
        float contentH = HeaderHeight() + tabsH + 8f + TabHeight(tab);
        Rect panel = new Rect(x, y, w, UiKit.WindowH(contentH));

        int lvl = Bld != null ? Bld.level : 1;
        Rect c = UiKit.Window(panel, $"{Title} · Ур. {lvl}");
        float cx = c.x, cw = c.width, cy = c.y;

        // --- Что делает ---
        GUIStyle body = Mid(UiKit.Body, false);
        bool showBar = false;
        string training;
        if (!IsTraining) training = UiKit.Col(UiKit.MutedHex, "Никого не обучает");
        else if (!RecruitInside) training = $"<b>{CurrentTrainingName}</b> " + UiKit.Col(UiKit.MutedHex, "— новобранец идёт");
        else if (pottagePending) training = $"<b>{CurrentTrainingName}</b> " + UiKit.Col(UiKit.WarnHex, "— ждёт похлёбку");
        else if (potionsPending) training = $"<b>{CurrentTrainingName}</b> " + UiKit.Col(UiKit.WarnHex, "— ждёт зелье");
        else { training = $"Обучение: <b>{CurrentTrainingName}</b>"; showBar = true; }
        GUI.Label(new Rect(cx, cy, showBar ? cw - 150f : cw, UiKit.RowH), training, body);
        if (showBar)
            UiKit.Bar(new Rect(cx + cw - 140f, cy + 4f, 140f, 14f), TrainingProgress, UiKit.Good, $"{TrainingProgress * 100f:F0}%");
        cy += UiKit.RowH;

        // --- Места ---
        Rect placesR = new Rect(cx, cy, cw, UiKit.RowH);
        float freeFrac = MaxSpace > 0 ? (float)CurrentOccupiedSpace / MaxSpace : 0f;
        string places = UiKit.Col(freeFrac >= 1f ? UiKit.WarnHex : UiKit.GoodHex, $"{CurrentOccupiedSpace}/{MaxSpace}");
        GUI.Label(placesR, UiKit.Col(UiKit.MutedHex, "Места: ") + places
            + UiKit.Col(UiKit.MutedHex, "   Бойцов: ") + soldiers.Count
            + UiKit.Col(UiKit.MutedHex, "   Отрядов: ") + squads.Count, body);
        UiKit.Tip(placesR, "Состав:\n" + TypeSummary() + $"\n\nМест: {maxGarrisonSpace} за каждый уровень казармы.");
        cy += UiKit.RowH;

        if (Bld != null && !Bld.isWorking)
        {
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.BadHex, Bld.StatusText), body);
            cy += UiKit.RowH;
        }
        cy += 4f;

        // --- Вкладки ---
        int prevTab = tab;
        int newTab = UiKit.Tabs(new Rect(cx, cy, cw, tabsH), new[] { "Обучение", $"Войска ({soldiers.Count})", "Снаряжение" }, tab);
        cy += tabsH + 8f;

        if (tab == 1) DrawArmyTab(cx, cy, cw);
        else if (tab == 2) DrawGearTab(cx, cy, cw);
        else DrawTrainTab(cx, cy, cw);

        // Вкладку меняем после отрисовки - чтобы высота панели в этом кадре совпала с содержимым
        if (newTab != prevTab)
        {
            if (newTab == 2) equipFor = null;   // по вкладке - сначала список бойцов
            tab = newTab;
        }

        return panel;
    }
}
