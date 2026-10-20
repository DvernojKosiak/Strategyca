using UnityEngine;
using System.Collections.Generic;
using UnityEngine.InputSystem;

[System.Serializable]
public class BuildingType
{
    public string name = "Здание";
    public Vector2Int size = new Vector2Int(2, 2);

    [Header("Стоимость постройки")]
    public int costGold = 0;
    public int costWood = 0;
    public int costStone = 0;
    public int costFood = 0;

    [Header("Пассивное производство (в секунду)")]
    public int prodGold = 0;
    public int prodWood = 0;
    public int prodStone = 0;
    public int prodFood = 0;

    [Header("Лимит хранилища (на 1 ур.)")]
    public int maxStorage = 50;

    public Color color = Color.white;
    public GameObject prefab;
}

public class BuildingPlacer : MonoBehaviour
{
    [Header("Префаб-заготовка")]
    public GameObject buildingPrefab;

    [Header("Префаб Ратуши")]
    public GameObject townHallCustomPrefab;

    [Header("Префаб Шахты")]
    public GameObject mineCustomPrefab;

    [Header("Префаб Дороги")]
    public GameObject roadCustomPrefab;

    [Header("Префаб Фермы")]
    public GameObject farmCustomPrefab;

    [Header("Префаб Лесопилки")]
    public GameObject sawmillCustomPrefab;

    [Header("Префаб Жилого дома")]
    public GameObject houseCustomPrefab;

    [Header("Префаб Мастерской")]
    [UnityEngine.Serialization.FormerlySerializedAs("WorkshopPrefab")]
    [UnityEngine.Serialization.FormerlySerializedAs("workshopPrefab")]
    public GameObject workshopCustomPrefab;

    [Header("Префаб Башни")]
    [UnityEngine.Serialization.FormerlySerializedAs("TowerPrefab")]
    [UnityEngine.Serialization.FormerlySerializedAs("towerPrefab")]
    public GameObject towerCustomPrefab;

    [Header("Типы зданий")]
    public BuildingType[] buildingTypes;

    [Header("Карта")]
    public int mapSize = 50;

    [Header("Ягодные кусты (для Станции собирателей)")]
    public int berryBushes = 40;

    [Header("Цвета")]
    public Color backColor = new Color(0.11f, 0.14f, 0.20f);
    public Color lineColor = new Color(0.22f, 0.28f, 0.40f);
    public Color okColor = new Color(0.4f, 1f, 0.4f, 0.6f);
    public Color badColor = new Color(1f, 0.3f, 0.3f, 0.6f);

    [Header("Трава")]
    public Color grassDark = new Color(0.20f, 0.42f, 0.16f);
    public Color grassLight = new Color(0.36f, 0.62f, 0.25f);
    public bool showGridLines = true;      // еле заметная сетка, чтобы было видно клетки
    [Range(0f, 0.3f)] public float gridLineStrength = 0.10f;

    // ---------- Окно построек ----------
    private bool shopOpen = false;
    private Rect shopRect;
    private float shopScroll = 0f;      // прокрутка окна построек (колёсико или полоса справа)
    private float shopMaxScroll = 0f;
    private readonly Rect buildButtonRect = new Rect(10, 45, 240, 40);
    private bool placeSpaceOk = true;   // для подсказки у курсора: есть ли место под зданием
    private int placeDoorProblem = 0;   // 0 - ок, 1 - у самого здания нет свободной стороны для двери, 2 - закроет выход соседу
    private bool placeZoneOk = true;    // ... и входит ли оно в зелёную зону
    private string placeWaterProblem = "";   // ... и не мешает ли река (обычное здание на воде / мост не на воде)
    private bool placeGuard = false;    // клик, которым выбрали постройку, не должен сразу ставить её "сквозь" окно
    private bool wasWar = false;        // для сообщения о начале войны

    // Описание в два слова для карточки
    static readonly Dictionary<string, string> ShortInfo = new Dictionary<string, string>
    {
        { "Стена",      "Задерживает врагов" },
        { "Жилой дом",  "Жильё для горожан" },
        { "Лесопилка",  "Дерево; Ур.3 — смола" },
        { "Шахта",      "Камень, золото; Ур.3 — руда" },
        { "Ферма",      "Еда; Ур.3 — овёс" },
        { "Мастерская", "Доски и плиты" },
        { BarracksController.TypeName, "Обучает солдат" },
        { "Башня",      "Стреляет по врагам" },
        { "Дорожка",    "Ходить быстрее" },
        { BuildingInstance.CobbleName, "Быстро и пешим, и конным" },
        { HealerHouse.TypeName, "Лечат и кормят" },
        { BuildingInstance.GateName, "Проход в стене" },
        { MagicCannonController.TypeName, "Взрывает врагов по области" },
        { CourierHouse.TypeName, "Курьеры сами носят добычу" },
        { WarehouseController.TypeName, "Доски, плиты и сталь" },
        { ArmoryController.TypeName, "Мечи, луки и щиты" },
        { StableController.TypeName, "Лошади из овса" },
        { OutpostController.TypeName, "Расширяет зону; нужен подвоз еды" },
        { BerryStation.TypeName, "Ягоды с кустов за городом" },
        { KitchenController.TypeName, "Похлёбка для войск" },
        { WagonYard.TypeName, "Обозы в Дальний склад, враги не достанут" },
        { PotionHouse.TypeName, "Магия: зелье отравления из трав" },
        { WeaponForge.TypeName, "Война: доводка оружия (мини-игра)" },
        { FoundryController.TypeName, "Инженерия: детали из руды и стали" },
        { EngineerYard.TypeName, "Инженерия: модули для башен и ловушки" },
        { MarketHouse.TypeName, "Рынок: торговля за марки" },
        { BuildingInstance.BridgeName, "Переход через реку (на воду)" },
        { BuildingInstance.GuestHouseName, "Жильё только для наёмников" },
        { GroundCannonController.TypeName, "Ядра прямой наводкой, осколки" },
        { MagicArmory.TypeName, "Магия: снаряды для пушек" },
        { LifeAltar.TypeName, "Магия жизни: растит голема" },
        { EngineerBarracks.TypeName, "Военный инженер: катапульты" },
        { IndustrialPlant.TypeName, "Индустрия: ускоряет производства рядом" },
        { WarBuilderYard.TypeName, "Военный строитель: чинят здания в бою" },
    };

    // С какого уровня Ратуши можно строить (1 - сразу)
    static int HallLevelNeeded(string typeName)
    {
        if (typeName == BuildingInstance.BridgeName) return 2;   // мост - с Ратуши Ур.2
        if (typeName == GroundCannonController.TypeName) return GroundCannonController.RequiredHallLevel;   // пушка - с Ратуши Ур.3
        if (typeName == MagicArmory.TypeName) return MagicArmory.RequiredHallLevel;
        if (typeName == LifeAltar.TypeName) return LifeAltar.RequiredHallLevel;
        if (typeName == EngineerBarracks.TypeName || typeName == IndustrialPlant.TypeName) return 3;   // направления инженеров - с Ратуши Ур.3
        if (typeName == WarBuilderYard.TypeName) return WarBuilderYard.RequiredHallLevel;              // ветки военного дела - с Ратуши Ур.3
        return 1;
    }

    bool HallLevelOk(BuildingType t)
    {
        int need = t != null ? HallLevelNeeded(t.name) : 1;
        if (need <= 1) return true;
        BuildingInstance hall = PopulationManager.Instance != null ? PopulationManager.Instance.Hall : null;
        int lvl = hall != null ? hall.level : 1;
        foreach (var inst in placedInstances)
            if (inst != null && inst.IsTownHall) lvl = Mathf.Max(lvl, inst.level);
        return lvl >= need;
    }

    // Сколько зданий этого типа можно поставить (0 - сколько угодно)
    static int MaxOfType(string typeName)
    {
        if (typeName == MarketHouse.TypeName) return 1;   // Торговый дом - один на город
        if (typeName == LifeAltar.TypeName) return 1;     // Алтарь жизни - один (и голем один)
        if (typeName == EngineerBarracks.TypeName) return 1;   // Инженерная казарма - одна
        if (typeName == IndustrialPlant.TypeName) return 1;    // Завод - один
        if (typeName == WarBuilderYard.TypeName) return 1;     // Двор военного строителя - один
        return 0;
    }

    bool AtLimit(BuildingType t)
    {
        int max = t != null ? MaxOfType(t.name) : 0;
        return max > 0 && CountOfType(t.name) >= max;
    }

    // Здания путей развития: в окне построек видны только выбравшему этот путь
    static PathKind PathOf(string typeName)
    {
        if (typeName == PotionHouse.TypeName || typeName == MagicArmory.TypeName || typeName == LifeAltar.TypeName) return PathKind.Magic;
        if (typeName == WeaponForge.TypeName || typeName == WarBuilderYard.TypeName) return PathKind.War;
        if (typeName == FoundryController.TypeName || typeName == EngineerYard.TypeName
            || typeName == EngineerBarracks.TypeName || typeName == IndustrialPlant.TypeName) return PathKind.Engineering;
        return PathKind.None;
    }

    // Здания школ магии (внутри пути Магии): видны только выбравшему школу
    static MagicSchool SchoolOf(string typeName)
    {
        if (typeName == LifeAltar.TypeName) return MagicSchool.Life;
        return MagicSchool.None;
    }

    // Здания направлений инженеров (внутри пути Инженерии): видны только выбравшему направление
    static EngSchool EngSchoolOf(string typeName)
    {
        if (typeName == EngineerBarracks.TypeName) return EngSchool.Military;
        if (typeName == IndustrialPlant.TypeName) return EngSchool.Industrial;
        return EngSchool.None;
    }

    // Здания веток военного дела (внутри пути Военного дела): видны только выбравшему ветку
    static WarSchool WarSchoolOf(string typeName)
    {
        if (typeName == WarBuilderYard.TypeName) return WarSchool.Builder;
        return WarSchool.None;
    }

    /// <summary>Здание доступно выбранному пути, школе магии, направлению инженеров и ветке военного дела.</summary>
    static bool PathAllows(string typeName)
    {
        return DevelopmentPath.Allows(PathOf(typeName)) && DevelopmentPath.SchoolAllows(SchoolOf(typeName))
            && DevelopmentPath.EngAllows(EngSchoolOf(typeName)) && DevelopmentPath.WarAllows(WarSchoolOf(typeName));
    }

    private BuildingType townHall;
    private int selected = 0;
    private bool isBuildingMode = false;

    private Dictionary<Vector2Int, GameObject> occupied = new Dictionary<Vector2Int, GameObject>();
    // Стартовые постройки (лесопилка, шахта, ферма, жилой дом из начала игры) - их нельзя снести
    private readonly HashSet<GameObject> starterBuildings = new HashSet<GameObject>();
    private List<BuildingInstance> placedInstances = new List<BuildingInstance>();
    private BuildingInstance selectedInstance = null;
    private CampMarker selectedCamp = null;   // выбранный полевой лагерь отряда
    private EnemyAgent selectedEnemy = null;     // выбранный враг (карточка только для просмотра)
    private CitizenAgent selectedCitizen = null; // выбранный житель (карточка только для просмотра)
    private Rect infoPanelRect = new Rect(265, 45, 315, 335); // реальный размер панели выбранного здания

    private Camera cam;
    private GameObject ghost;

    private bool isMoving = false;
    private GameObject movingBuilding = null;
    private BuildingType movingBuildingType = null;

    BuildingType Current => buildingTypes[selected];

    void Start()
    {
        cam = Camera.main;
        cam.orthographic = true;

        EnsureTypes();
        CreateGridBackground();
        CreateGhost();

        // Навигация для жителей, игровые часы и зона застройки (если их нет в сцене - создаём)
        // Реки - до навигации и зон ресурсов: по ним считаются вода и плодородные берега
        if (Rivers.Instance == null) Rivers.Create(mapSize);
        if (GridNavigation.Instance == null) GridNavigation.Create(mapSize);
        if (Territory.Instance == null) Territory.Create(mapSize);
        if (ResourceZones.Instance == null) ResourceZones.Create(mapSize);
        if (GameClock.Instance == null) new GameObject("GameClock").AddComponent<GameClock>();
        if (WarManager.Instance == null) new GameObject("WarManager").AddComponent<WarManager>();
        if (RaidManager.Instance == null) new GameObject("RaidManager").AddComponent<RaidManager>();

        PlaceTownHall();
        PlaceInitialBuildingsRandomly();

        // Ягодные кусты рассаживаются после стартовых зданий (в основном за пределами зелёной зоны)
        if (BerryField.Instance == null) BerryField.Create(berryBushes);

        // Инициализация симуляции потребления города и менеджера населения
        new GameObject("CityConsumption").AddComponent<CityConsumption>();
        new GameObject("PopulationManager").AddComponent<PopulationManager>();
        new GameObject("ManaManager").AddComponent<ManaManager>();

        // Туман войны (после Ратуши - чтобы зелёная зона уже была)
        if (FogOfWar.Home == null) FogOfWar.CreateHome(mapSize);
        if (DevelopmentPath.Instance == null) new GameObject("DevelopmentPath").AddComponent<DevelopmentPath>();
        if (FieldPostManager.Instance == null) new GameObject("FieldPostManager").AddComponent<FieldPostManager>();
        if (TrapManager.Instance == null) new GameObject("TrapManager").AddComponent<TrapManager>();
        if (CrossingManager.Instance == null) new GameObject("CrossingManager").AddComponent<CrossingManager>();
    }

    void EnsureTypes()
    {
        // Стоимость Ратуши нужна только для улучшений (цена умножается на уровень)
        townHall = new BuildingType
        {
            name = "Ратуша",
            size = new Vector2Int(3, 3),
            costGold = 150,
            costWood = 100,
            costStone = 100,
            prodGold = 0,
            maxStorage = 100,
            color = Color.white,
            prefab = townHallCustomPrefab != null ? townHallCustomPrefab : buildingPrefab
        };

        if (buildingTypes != null && buildingTypes.Length > 0)
        {
            // Типы заданы в инспекторе - новые здания добавим сами, если их там нет
            EnsureType(BuildingInstance.RoadName, RoadType);
            EnsureType(BuildingInstance.CobbleName, CobbleType);
            EnsureType(HealerHouse.TypeName, HealerType);
            EnsureType(BuildingInstance.GateName, GateType);
            EnsureType(MagicCannonController.TypeName, CannonType);
            EnsureType(CourierHouse.TypeName, CourierType);
            EnsureType(WarehouseController.TypeName, WarehouseType);
            EnsureType(ArmoryController.TypeName, ArmoryType);
            EnsureType(StableController.TypeName, StableType);
            EnsureType(OutpostController.TypeName, OutpostType);
            EnsureType(BerryStation.TypeName, BerryStationType);
            EnsureType(KitchenController.TypeName, KitchenType);
            EnsureType(WagonYard.TypeName, YardType);
            EnsureType(PotionHouse.TypeName, PotionHouseType);
            EnsureType(WeaponForge.TypeName, ForgeType);
            EnsureType(FoundryController.TypeName, FoundryType);
            EnsureType(EngineerYard.TypeName, EngineerYardType);
            EnsureType(MarketHouse.TypeName, MarketType);
            EnsureType(BuildingInstance.BridgeName, BridgeType);
            EnsureType(BuildingInstance.GuestHouseName, GuestHouseType);
            EnsureType(GroundCannonController.TypeName, GroundCannonType);
            EnsureType(MagicArmory.TypeName, MagicArmoryType);
            EnsureType(LifeAltar.TypeName, LifeAltarType);
            EnsureType(EngineerBarracks.TypeName, EngineerBarracksType);
            EnsureType(IndustrialPlant.TypeName, IndustrialPlantType);
            EnsureType(WarBuilderYard.TypeName, WarBuilderYardType);
            
            // Если в инспекторе заданы кастомные префабы, но не привязаны через код
            foreach (var bt in buildingTypes)
            {
                if (bt != null && bt.name == "Шахта" && mineCustomPrefab != null && bt.prefab == null)
                    bt.prefab = mineCustomPrefab;
                if (bt != null && bt.name == BuildingInstance.RoadName && roadCustomPrefab != null && bt.prefab == null)
                    bt.prefab = roadCustomPrefab;
                if (bt != null && bt.name == "Ферма" && farmCustomPrefab != null && bt.prefab == null)
                    bt.prefab = farmCustomPrefab;
                if (bt != null && bt.name == "Лесопилка" && sawmillCustomPrefab != null && bt.prefab == null)
                    bt.prefab = sawmillCustomPrefab;
                if (bt != null && bt.name == "Жилой дом" && houseCustomPrefab != null && bt.prefab == null)
                    bt.prefab = houseCustomPrefab;
                if (bt != null && bt.name == "Мастерская" && workshopCustomPrefab != null && bt.prefab == null)
                    bt.prefab = workshopCustomPrefab;
                if (bt != null && bt.name == "Башня" && towerCustomPrefab != null && bt.prefab == null)
                    bt.prefab = towerCustomPrefab;
                // Мастерская и Башня - без подкраски, префаб виден в своих цветах
                if (bt != null && (bt.name == "Мастерская" || bt.name == "Башня")) bt.color = Color.white;
            }
            return;
        }

        buildingTypes = new BuildingType[]
        {
            new BuildingType { name = "Стена",      size = new Vector2Int(1, 1), costGold = 2,  costWood = 5,  costStone = 5,  color = new Color(0.6f, 0.6f, 0.65f) },
            new BuildingType { name = "Жилой дом",  size = new Vector2Int(2, 2), costGold = 40, costWood = 50, costStone = 10, maxStorage = 0, color = new Color(0.85f, 0.75f, 0.55f), prefab = houseCustomPrefab },
            new BuildingType { name = "Лесопилка",  size = new Vector2Int(2, 2), costGold = 30, costWood = 20, costStone = 10, costFood = 10, prodWood = 4, maxStorage = 60, color = Color.white, prefab = sawmillCustomPrefab },
            new BuildingType { name = "Шахта",      size = new Vector2Int(2, 2), costGold = 50, costWood = 30, costStone = 20, costFood = 15, prodGold = 1, prodStone = 3, maxStorage = 50, color = Color.white, prefab = mineCustomPrefab },
            new BuildingType { name = "Ферма",      size = new Vector2Int(3, 3), costGold = 30, costWood = 40, prodFood = 5, maxStorage = 80, color = Color.white, prefab = farmCustomPrefab },
            new BuildingType { name = "Мастерская", size = new Vector2Int(2, 2), costGold = 70, costWood = 50, costStone = 30, costFood = 10, maxStorage = 0, color = Color.white, prefab = workshopCustomPrefab },
            new BuildingType { name = BarracksController.TypeName, size = new Vector2Int(2, 2), costGold = 100,costWood = 80, costStone = 40, costFood = 30, maxStorage = 0, color = new Color(0.7f, 0.2f, 0.2f) },
            new BuildingType { name = "Башня",      size = new Vector2Int(2, 2), costGold = 80, costWood = 30, costStone = 60, costFood = 20, maxStorage = 0, color = Color.white, prefab = towerCustomPrefab },
            RoadType(),
            HealerType(),
            GateType(),
            CannonType(),
            CourierType(),
            WarehouseType(),
            ArmoryType(),
            StableType(),
            OutpostType(),
            CobbleType(),
            BerryStationType(),
            KitchenType(),
            YardType(),
            PotionHouseType(),
            ForgeType(),
            FoundryType(),
            EngineerYardType(),
            MarketType(),
            BridgeType(),
            GuestHouseType(),
            GroundCannonType(),
            MagicArmoryType(),
            LifeAltarType(),
            EngineerBarracksType(),
            IndustrialPlantType(),
            WarBuilderYardType(),
        };
    }

    // Пушка: ядра прямой наводкой (Ратуша Ур.3, любой путь). Первая - уже со сталью (UpgradeRules)
    static BuildingType GroundCannonType()
    {
        return new BuildingType { name = GroundCannonController.TypeName, size = new Vector2Int(2, 2), costGold = 180, costWood = 60, costStone = 120, costFood = 20, maxStorage = 0, color = new Color(0.42f, 0.4f, 0.36f) };
    }

    // Магическая оружейная: снаряды для пушек (путь Магии, Ратуша Ур.3)
    static BuildingType MagicArmoryType()
    {
        return new BuildingType { name = MagicArmory.TypeName, size = new Vector2Int(2, 2), costGold = 160, costWood = 60, costStone = 80, costFood = 20, maxStorage = 0, color = new Color(0.55f, 0.4f, 0.85f) };
    }

    // Алтарь жизни: растит голема из подношений (школа Магии жизни, Ратуша Ур.3, один на город)
    static BuildingType LifeAltarType()
    {
        return new BuildingType { name = LifeAltar.TypeName, size = new Vector2Int(2, 2), costGold = 200, costWood = 40, costStone = 120, costFood = 30, maxStorage = 0, color = new Color(0.4f, 0.75f, 0.45f) };
    }

    // Инженерная казарма: катапульты (Инженерия, направление «Военный инженер», Ратуша Ур.3, одна на город)
    static BuildingType EngineerBarracksType()
    {
        return new BuildingType { name = EngineerBarracks.TypeName, size = new Vector2Int(3, 3), costGold = 260, costWood = 120, costStone = 80, costFood = 20, maxStorage = 0, color = new Color(0.58f, 0.4f, 0.24f) };
    }

    // Завод: площадка, производства рядом работают быстрее на топливе (Инженерия, «Индустриализатор», Ратуша Ур.3, один)
    static BuildingType IndustrialPlantType()
    {
        return new BuildingType { name = IndustrialPlant.TypeName, size = new Vector2Int(3, 3), costGold = 300, costWood = 100, costStone = 160, costFood = 0, maxStorage = 0, color = new Color(0.45f, 0.52f, 0.6f) };
    }

    // Двор военного строителя: строители чинят здания в бою (Военное дело, «Военный строитель», Ратуша Ур.3, один)
    static BuildingType WarBuilderYardType()
    {
        return new BuildingType { name = WarBuilderYard.TypeName, size = new Vector2Int(3, 2), costGold = 220, costWood = 160, costStone = 80, costFood = 20, maxStorage = 0, color = new Color(0.78f, 0.52f, 0.26f) };
    }

    // Мост: клетка моста через реку (тянется, как дорожка). Ратуша Ур.2, на клетку ещё 1 доска (UpgradeRules)
    BuildingType BridgeType()
    {
        return new BuildingType { name = BuildingInstance.BridgeName, size = new Vector2Int(1, 1), costWood = 8, costStone = 4, maxStorage = 0, color = new Color(0.55f, 0.38f, 0.2f) };
    }

    // Гостевой дом: живут только наёмники (6 / 12 / 18 мест)
    BuildingType GuestHouseType()
    {
        return new BuildingType { name = BuildingInstance.GuestHouseName, size = new Vector2Int(2, 2), costGold = 60, costWood = 60, costStone = 20, maxStorage = 0, color = new Color(0.72f, 0.55f, 0.85f) };
    }

    // Добавить тип в конец списка, если здания с таким именем ещё нет (типы из инспектора не трогаем)
    void EnsureType(string typeName, System.Func<BuildingType> make)
    {
        foreach (var bt in buildingTypes)
            if (bt != null && bt.name == typeName) return;

        var list = new List<BuildingType>(buildingTypes);
        list.Add(make());
        buildingTypes = list.ToArray();
    }

    BuildingType RoadType()
    {
        return new BuildingType { 
            name = BuildingInstance.RoadName, 
            size = new Vector2Int(1, 1), 
            costWood = 2, 
            maxStorage = 0, 
            color = Color.white, 
            prefab = roadCustomPrefab != null ? roadCustomPrefab : buildingPrefab 
        };
    }

    static BuildingType BerryStationType()
    {
        return new BuildingType { name = BerryStation.TypeName, size = new Vector2Int(2, 2), costGold = 40, costWood = 55, costStone = 10, costFood = 15, maxStorage = 40, color = new Color(0.62f, 0.25f, 0.5f) };
    }

    static BuildingType KitchenType()
    {
        return new BuildingType { name = KitchenController.TypeName, size = new Vector2Int(2, 2), costGold = 110, costWood = 80, costStone = 65, costFood = 30, maxStorage = 0, color = new Color(0.88f, 0.48f, 0.32f) };
    }

    static BuildingType PotionHouseType()
    {
        return new BuildingType { name = PotionHouse.TypeName, size = new Vector2Int(2, 2), costGold = 120, costWood = 60, costStone = 60, maxStorage = 0, color = new Color(0.5f, 0.8f, 0.55f) };
    }

    static BuildingType ForgeType()
    {
        return new BuildingType { name = WeaponForge.TypeName, size = new Vector2Int(2, 2), costGold = 150, costWood = 60, costStone = 100, maxStorage = 0, color = new Color(0.55f, 0.35f, 0.3f) };
    }

    static BuildingType FoundryType()
    {
        return new BuildingType { name = FoundryController.TypeName, size = new Vector2Int(3, 2), costGold = 140, costWood = 70, costStone = 120, costFood = 20, maxStorage = 0, color = new Color(0.78f, 0.45f, 0.22f) };
    }

    static BuildingType EngineerYardType()
    {
        return new BuildingType { name = EngineerYard.TypeName, size = new Vector2Int(4, 4), costGold = 220, costWood = 150, costStone = 150, costFood = 30, maxStorage = 0, color = new Color(0.85f, 0.72f, 0.38f) };
    }

    static BuildingType MarketType()
    {
        return new BuildingType { name = MarketHouse.TypeName, size = new Vector2Int(3, 3), costGold = 160, costWood = 120, costStone = 90, costFood = 20, maxStorage = 0, color = new Color(0.95f, 0.78f, 0.3f) };
    }

    static BuildingType YardType()
    {
        return new BuildingType { name = WagonYard.TypeName, size = new Vector2Int(3, 2), costGold = 300, costWood = 200, costStone = 150, costFood = 0, maxStorage = 0, color = new Color(0.25f, 0.5f, 0.72f) };
    }

    static BuildingType OutpostType()
    {
        return new BuildingType { name = OutpostController.TypeName, size = new Vector2Int(2, 2), costGold = 250, costWood = 150, costStone = 150, costFood = 40, maxStorage = 0, color = new Color(0.95f, 0.65f, 0.2f) };
    }

    static BuildingType CourierType()
    {
        return new BuildingType { name = CourierHouse.TypeName, size = new Vector2Int(2, 2), costGold = 90, costWood = 60, costStone = 40, costFood = 20, maxStorage = 0, color = new Color(0.9f, 0.5f, 0.65f) };
    }

    static BuildingType WarehouseType()
    {
        return new BuildingType { name = WarehouseController.TypeName, size = new Vector2Int(3, 2), costGold = 60, costWood = 90, costStone = 50, maxStorage = 0, color = new Color(0.62f, 0.5f, 0.38f) };
    }

    static BuildingType ArmoryType()
    {
        return new BuildingType { name = ArmoryController.TypeName, size = new Vector2Int(3, 2), costGold = 100, costWood = 60, costStone = 60, maxStorage = 0, color = new Color(0.45f, 0.5f, 0.62f) };
    }

    static BuildingType StableType()
    {
        return new BuildingType { name = StableController.TypeName, size = new Vector2Int(3, 2), costGold = 90, costWood = 115, costStone = 40, costFood = 25, maxStorage = 0, color = new Color(0.55f, 0.36f, 0.2f) };
    }

    static BuildingType GateType()
    {
        return new BuildingType { name = BuildingInstance.GateName, size = new Vector2Int(1, 1), costGold = 10, costWood = 20, costStone = 15, maxStorage = 0, color = new Color(0.5f, 0.5f, 0.56f) };
    }

    static BuildingType CannonType()
    {
        return new BuildingType { name = MagicCannonController.TypeName, size = new Vector2Int(2, 2), costGold = 120, costWood = 40, costStone = 80, costFood = 20, maxStorage = 0, color = new Color(0.6f, 0.35f, 0.9f) };
    }

    static BuildingType HealerType()
    {
        return new BuildingType { name = HealerHouse.TypeName, size = new Vector2Int(2, 2), costGold = 60, costWood = 40, costStone = 20, costFood = 20, maxStorage = 0, color = new Color(0.45f, 0.85f, 0.75f) };
    }

    static BuildingType CobbleType()
    {
        return new BuildingType { name = BuildingInstance.CobbleName, size = new Vector2Int(1, 1), maxStorage = 0, color = new Color(0.72f, 0.7f, 0.66f) };
    }

    void CreateGridBackground()
    {
        int cellPx = 16;
        int res = mapSize * cellPx;
        Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        var rng = new System.Random(12345);
        float offX = rng.Next(0, 1000), offY = rng.Next(0, 1000);

        Color[] pixels = new Color[res * res];
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float big = Mathf.PerlinNoise(offX + x * 0.02f, offY + y * 0.02f);
                float mid = Mathf.PerlinNoise(offX + x * 0.12f, offY + y * 0.12f);
                float t = Mathf.Clamp01(big * 0.65f + mid * 0.35f);
                Color c = Color.Lerp(grassDark, grassLight, t);

                double r = rng.NextDouble();
                if (r < 0.06) c = Color.Lerp(c, grassLight * 1.25f, 0.6f);
                else if (r < 0.12) c = Color.Lerp(c, grassDark * 0.7f, 0.6f);

                if (r > 0.9985) c = new Color(0.95f, 0.9f, 0.4f);

                if (showGridLines && (x % cellPx == 0 || y % cellPx == 0))
                    c = Color.Lerp(c, Color.black, gridLineStrength);

                c.a = 1f;
                pixels[y * res + x] = c;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();

        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "GameMap_Background";
        bg.transform.position = new Vector3(0f, 0f, 1f);
        bg.transform.localScale = new Vector3(mapSize, mapSize, 1f);
        Destroy(bg.GetComponent<Collider>());

        Material mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        bg.GetComponent<Renderer>().material = mat;
        bg.GetComponent<Renderer>().sortingOrder = -100;
    }

    void CreateGhost()
    {
        if (buildingPrefab == null) return;
        ghost = Instantiate(buildingPrefab);
        ghost.name = "BuildingGhost";
        foreach (var col in ghost.GetComponentsInChildren<Collider2D>()) Destroy(col);
        foreach (var col in ghost.GetComponentsInChildren<Collider>()) Destroy(col);
        ghost.SetActive(false);
    }

    void PlaceTownHall()
    {
        Vector2Int origin = new Vector2Int(-1, -1);
        PlaceBuilding(townHall, origin, GetFootprint(origin, townHall.size), false);
    }

    void PlaceInitialBuildingsRandomly()
    {
        BuildingType sawmillType = System.Array.Find(buildingTypes, b => b.name == "Лесопилка");
        BuildingType mineType = System.Array.Find(buildingTypes, b => b.name == "Шахта");
        BuildingType farmType = System.Array.Find(buildingTypes, b => b.name == "Ферма");
        BuildingType houseType = System.Array.Find(buildingTypes, b => b.name == "Жилой дом");

        BuildingType[] initialToSpawn = new BuildingType[] { sawmillType, mineType, farmType, houseType };

        foreach (var bType in initialToSpawn)
        {
            if (bType == null) continue;

            bool placed = false;
            int attempts = 0;

            while (!placed && attempts < 200)
            {
                attempts++;
                int rx = Random.Range(-10, 10 - bType.size.x + 1);
                int ry = Random.Range(-10, 10 - bType.size.y + 1);
                Vector2Int origin = new Vector2Int(rx, ry);
                List<Vector2Int> footprint = GetFootprint(origin, bType.size);

                if (CellsFree(footprint) && ZoneOk(bType, footprint) && WaterProblem(bType, footprint) == "" && SameBankAsHall(footprint)
                    && DoorProblem(bType, origin, footprint, null) == 0)
                {
                    PlaceBuilding(bType, origin, footprint, false);
                    if (occupied.TryGetValue(origin, out GameObject starter) && starter != null)
                    {
                        starterBuildings.Add(starter);
                        BuildingInstance si = starter.GetComponent<BuildingInstance>();
                        if (si != null) si.IsStarter = true;   // стартовые лесопилка, шахта и ферма восстанавливаются даром
                    }
                    placed = true;
                }
            }
        }
    }

    // Стартовое здание - на том же берегу, что и Ратуша (иначе без моста до него не дойти)
    static bool SameBankAsHall(List<Vector2Int> footprint)
    {
        if (!Rivers.Any) return true;
        int hallBank = Rivers.LandRegion(new Vector2Int(0, -3));
        if (hallBank == 0) hallBank = Rivers.LandRegion(new Vector2Int(-2, 0));
        foreach (var c in footprint)
        {
            if (Rivers.LandRegion(c) != hallBank) return false;
            // и дверь (клетки вокруг) тоже не за рекой
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int r = Rivers.LandRegion(new Vector2Int(c.x + dx, c.y + dy));
                    if (r != 0 && r != hallBank) return false;
                }
        }
        return true;
    }

    bool BuildBlocked => WarManager.IsWar;

    void UpdateTerritoryOverlay()
    {
        string hintType = isMoving ? (movingBuildingType != null ? movingBuildingType.name : null)
                        : (isBuildingMode && Current != null ? Current.name : null);
        ResourceZones.HintShow = hintType != null && ResourceZones.IsZoneBuilding(hintType);

        if (Territory.Instance == null) return;

        bool show = shopOpen || isBuildingMode || isMoving;
        bool frontier = false;

        if (isBuildingMode && !isMoving && Current != null) frontier = IsFrontierType(Current.name);
        if (isMoving && movingBuildingType != null) frontier = IsFrontierType(movingBuildingType.name);

        if (!show && selectedInstance != null && selectedInstance.type != null &&
            (selectedInstance.IsTownHall || selectedInstance.IsOutpost)) show = true;

        Territory.Instance.SetOverlay(show, frontier);
    }

    static bool IsFrontierType(string typeName)
    {
        return BuildingInstance.IsRoadName(typeName) || typeName == OutpostController.TypeName;
    }

    // Тревога (WarManager.RaiseAlarm): свернуть окно, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()   // только список построек; постройка «в руке» остаётся (при войне её снимет Update)
    {
        if (!shopOpen) return false;
        SetShopOpen(false);
        return true;
    }

    /// <summary>Здание «в руках»: ставят новое или переносят (обновляется каждый кадр).</summary>
    public static bool PlacingNow { get; private set; }
    /// <summary>Выбранное сейчас здание (или null). Обновляется каждый кадр.</summary>
    public static BuildingInstance Selected { get; private set; }

    void OnDestroy() { PlacingNow = false; Selected = null; }

    void LateUpdate()
    {
        PlacingNow = isBuildingMode || isMoving;
        Selected = selectedInstance != null && selectedInstance.gameObject != null ? selectedInstance : null;
    }

    void Update()
    {
        PlacingNow = isBuildingMode || isMoving;
        Selected = selectedInstance != null && selectedInstance.gameObject != null ? selectedInstance : null;
        if (Mouse.current == null || cam == null) return;

        if (RaidManager.ViewingBattle)
        {
            if (ghost != null) ghost.SetActive(false);
            if (shopOpen) SetShopOpen(false);
            if (isBuildingMode) StopBuilding();
            if (isMoving) CancelMove();
            return;
        }

        Vector2 mousePos = Mouse.current.position.ReadValue();
        float guiY = Screen.height - mousePos.y;

        bool war = BuildBlocked;
        if (war && !wasWar && (isBuildingMode || isMoving))   // открытый список закрывает тревога со своим сообщением
            WarManager.Notify("Режим войны — строительство остановлено", false);
        wasWar = war;
        if (war)
        {
            if (shopOpen) SetShopOpen(false);
            if (isBuildingMode) StopBuilding();
            if (isMoving) CancelMove();
            if (DemolishDialog.IsOpen)
            {
                DemolishDialog.Close();
                WarManager.Notify("Идёт война — снос отменён", false);
            }
        }

        // Пока открыт вопрос «Снести?» или окно входа - карта не реагирует на мышь и клавиши
        if (DemolishDialog.IsOpen || AccountManager.Blocking)
        {
            if (ghost != null) ghost.SetActive(false);
            return;
        }

        UpdateTerritoryOverlay();

        if (shopOpen)
        {
            if (ghost != null) ghost.SetActive(false);
            CameraController.BlockZoom();
            float wheel = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f) shopScroll = Mathf.Clamp(shopScroll - Mathf.Sign(wheel) * 70f, 0f, shopMaxScroll);
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) SetShopOpen(false);

            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                Vector2 gp = new Vector2(mousePos.x, guiY);
                bool onWindow = shopRect.Contains(gp);
                bool onButtons = buildButtonRect.Contains(gp) || RaidManager.IsPointerOverUI(gp) || DevelopmentPath.IsPointerOver(gp) || WeaponForge.IsPointerOver(gp) || MarketWindow.IsPointerOver(gp) || CamoScouts.Blocks(gp)
                    || (GameMenu.Instance != null && GameMenu.Instance.IsPointerOver(gp))
                    || (GameClock.Instance != null && GameClock.Instance.IsPointerOver(gp));
                if (!onWindow && !onButtons) SetShopOpen(false);
            }
            return;
        }

        bool mouseOverMenu = buildButtonRect.Contains(new Vector2(mousePos.x, guiY));
        bool mouseOverInfo = ((selectedInstance != null || selectedCamp != null || selectedCitizen != null || selectedEnemy != null) && infoPanelRect.Contains(new Vector2(mousePos.x, guiY)))
            || (selectedInstance != null && durabilityRect.Contains(new Vector2(mousePos.x, guiY)));   // плашка прочности под панелью

        if (selectedCitizen != null && selectedCitizen.IsDead) selectedCitizen = null;
        if (selectedEnemy != null && (!EnemyAgent.Alive(selectedEnemy) || !FogOfWar.IsVisible(selectedEnemy.Position))) selectedEnemy = null;
        if (selectedEnemy != null && EnemyInfoPanel.Follow && CameraController.Instance != null)
            CameraController.Instance.FocusOn(selectedEnemy.Position);
        if (selectedCitizen != null && CitizenInfoPanel.Follow && CameraController.Instance != null)
            CameraController.Instance.FocusOn(selectedCitizen.transform.position);
        bool mouseOverCity = GameMenu.Instance != null && GameMenu.Instance.IsPointerOver(new Vector2(mousePos.x, guiY));
        bool mouseOverClock = GameClock.Instance != null && GameClock.Instance.IsPointerOver(new Vector2(mousePos.x, guiY));
        Vector2 guiMouse = new Vector2(mousePos.x, guiY);
        bool mouseOverRaid = RaidManager.IsPointerOverUI(guiMouse) || DevelopmentPath.IsPointerOver(guiMouse) || WeaponForge.IsPointerOver(guiMouse) || MarketWindow.IsPointerOver(guiMouse) || FieldPostManager.Blocks(guiMouse) || TrapManager.Blocks(guiMouse) || CrossingManager.Blocks(guiMouse) || CamoScouts.Blocks(guiMouse);
        bool mouseOverUI = mouseOverMenu || mouseOverInfo || mouseOverCity || mouseOverClock || mouseOverRaid;
        if (mouseOverInfo || mouseOverCity) CameraController.BlockZoom();

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (isMoving) { CancelMove(); return; }
            if (isBuildingMode) { StopBuilding(); return; }
        }

        if (Mouse.current.rightButton.wasPressedThisFrame && isBuildingMode)
        {
            StopBuilding();
            return;
        }

        BarracksController pickBarracks = (selectedInstance != null && !isMoving && !isBuildingMode)
            ? selectedInstance.GetComponent<BarracksController>() : null;
        if (pickBarracks != null && pickBarracks.PickingTarget)
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                pickBarracks.PickingTarget = false;
            }
            else if (Mouse.current.leftButton.wasPressedThisFrame && !mouseOverUI)
            {
                Vector3 pw = MouseWorld();
                pickBarracks.SendSelectedTo(new Vector2(pw.x, pw.y));
            }
            return;
        }

        if (!isMoving && !isBuildingMode) HandleHotkeys();

        if (isMoving)
        {
            if (ghost != null) ghost.SetActive(true);
            Vector2Int origin = GetOriginUnderMouse(movingBuildingType.size);
            List<Vector2Int> footprint = GetFootprint(origin, movingBuildingType.size);

            placeWaterProblem = WaterProblem(movingBuildingType, footprint);
            bool spaceAvailable = CanBuildWhileMoving(footprint, movingBuilding) && ZoneOk(movingBuildingType, footprint) && placeWaterProblem == "";
            placeDoorProblem = spaceAvailable ? DoorProblem(movingBuildingType, origin, footprint, movingBuilding) : 0;
            spaceAvailable = spaceAvailable && placeDoorProblem == 0;
            UpdateGhost(movingBuildingType, origin, spaceAvailable);

            if (Mouse.current.leftButton.wasPressedThisFrame && spaceAvailable && !mouseOverUI)
            {
                ConfirmMove(origin, footprint);
            }
            return;
        }

        if (isBuildingMode)
        {
            BuildingType activeType = Current;
            Vector2Int origin = GetOriginUnderMouse(activeType.size);
            List<Vector2Int> footprint = GetFootprint(origin, activeType.size);

            bool freeCells = CellsFree(footprint);
            placeWaterProblem = WaterProblem(activeType, footprint);
            bool zoneOk = placeWaterProblem == "" && ZoneOk(activeType, footprint);
            placeDoorProblem = (freeCells && zoneOk) ? DoorProblem(activeType, origin, footprint, null) : 0;
            bool affordable = CanAfford(activeType);
            bool canBuild = freeCells && zoneOk && placeDoorProblem == 0 && affordable && !mouseOverUI && !BuildBlocked;
            placeSpaceOk = freeCells;
            placeZoneOk = zoneOk;

            UpdateGhost(activeType, origin, canBuild);

            bool paint = IsPaintType(activeType);

            if (placeGuard && !Mouse.current.leftButton.isPressed) placeGuard = false;
            bool placeClick = !placeGuard && (paint ? Mouse.current.leftButton.isPressed : Mouse.current.leftButton.wasPressedThisFrame);

            if (placeClick && canBuild)
            {
                PlaceBuilding(activeType, origin, footprint, true);

                bool keepInHand = paint || (Keyboard.current != null && Keyboard.current.shiftKey.isPressed);
                if (!keepInHand) StopBuilding();
            }
        }
        else
        {
            if (ghost != null) ghost.SetActive(false);

            if (Mouse.current.leftButton.wasPressedThisFrame && !mouseOverUI)
            {
                TrySelectBuildingUnderMouse();
            }

            if (!mouseOverUI && Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
            {
                StartMoveBuildingUnderMouse();
            }

            if (!mouseOverUI && Keyboard.current != null && Keyboard.current.xKey.wasPressedThisFrame)
            {
                AskDemolishUnderMouse();
            }
        }
    }

    void TrySelectBuildingUnderMouse()
    {
        Vector3 w = MouseWorld();

        EnemyAgent foe = EnemyAgent.PickAt(new Vector2(w.x, w.y));
        if (foe != null && !FogOfWar.IsVisible(foe.Position)) foe = null;
        selectedEnemy = foe;
        if (foe != null)
        {
            selectedCitizen = null;
            selectedInstance = null;
            selectedCamp = null;
            EnemyInfoPanel.Follow = false;
            return;
        }

        CitizenAgent who = CitizenAgent.PickAt(new Vector2(w.x, w.y));
        selectedCitizen = who;
        if (who != null)
        {
            selectedInstance = null;
            selectedCamp = null;
            CitizenInfoPanel.Follow = false;
            return;
        }

        selectedCamp = null;
        foreach (var camp in CampMarker.All)
        {
            if (camp != null && camp.Hit(new Vector2(w.x, w.y))) { selectedCamp = camp; break; }
        }
        if (selectedCamp != null)
        {
            selectedInstance = null;
            return;
        }
        Vector2Int cell = new Vector2Int(Mathf.FloorToInt(w.x), Mathf.FloorToInt(w.y));

        if (occupied.TryGetValue(cell, out GameObject clickedObj))
        {
            BuildingInstance inst = clickedObj.GetComponent<BuildingInstance>();
            if (inst != null && inst.type != null && inst.IsRoad)
            {
                selectedInstance = null;
                return;
            }
            selectedInstance = inst;
            if (inst != null && inst.GetComponent<MarketHouse>() != null) MarketWindow.Open();
        }
        else
        {
            selectedInstance = null;
        }
    }

    int CountOfType(string typeName)
    {
        int n = 0;
        foreach (var inst in placedInstances)
            if (inst != null && inst.type != null && inst.type.name == typeName) n++;
        return n;
    }

    void CollectFrom(BuildingInstance inst)
    {
        if (inst == null) return;
        var pm = PopulationManager.Instance;
        if (pm == null || pm.Hall == null) inst.CollectToTreasury();
        else pm.DispatchCourier(inst);
    }

    void CollectAllOfType(BuildingType targetType)
    {
        foreach (var inst in placedInstances)
        {
            if (inst == null) continue;
            if (inst.type == targetType || (inst.type != null && targetType != null && inst.type.name == targetType.name))
            {
                CollectFrom(inst);
            }
        }
    }

    void HandleHotkeys()
    {
        if (Keyboard.current == null) return;
        int count = Mathf.Min(9, buildingTypes.Length);
        for (int i = 0; i < count; i++)
        {
            if (Keyboard.current[Key.Digit1 + i].wasPressedThisFrame) TakeInHand(i);
        }
    }

    void SetShopOpen(bool open)
    {
        if (open && BuildBlocked)
        {
            WarManager.Notify("Идёт война — строить нельзя", false);
            return;
        }

        shopOpen = open;
        if (!open) return;
        shopScroll = 0f;

        StopBuilding();
        selectedInstance = null;
        selectedCamp = null;
        selectedCitizen = null;
        selectedEnemy = null;
    }

    void TakeInHand(int index)
    {
        if (index < 0 || index >= buildingTypes.Length) return;
        if (BuildBlocked)
        {
            WarManager.Notify("Идёт война — строить нельзя", false);
            return;
        }

        selected = index;
        isBuildingMode = true;
        shopOpen = false;
        placeGuard = true;
        selectedInstance = null;
        selectedCamp = null;
        selectedCitizen = null;
        selectedEnemy = null;
    }

    void StopBuilding()
    {
        isBuildingMode = false;
        if (ghost != null) ghost.SetActive(false);
    }

    static bool IsPaintType(BuildingType t)
    {
        return t != null && (BuildingInstance.IsRoadName(t.name) || t.name == BuildingInstance.WallName || t.name == BuildingInstance.BridgeName);
    }

    bool HasResources(BuildingType t)
    {
        var rm = ResourceManager.Instance;
        return rm != null && rm.HasEnough(t.costGold, t.costWood, t.costStone, t.costFood);
    }

    bool CanAfford(BuildingType t)
    {
        return HasResources(t) && UpgradeRules.CanPayBuild(t.name, CountOfType(t.name)) && !AtLimit(t) && HallLevelOk(t) && PathAllows(t.name);
    }

    static string CostLine(BuildingType t)
    {
        var rm = ResourceManager.Instance;
        List<string> parts = new List<string>();
        AddCost(parts, t.costGold, rm != null ? rm.gold : 0, "золота");
        AddCost(parts, t.costWood, rm != null ? rm.wood : 0, "дерева");
        AddCost(parts, t.costStone, rm != null ? rm.stone : 0, "камня");
        AddCost(parts, t.costFood, rm != null ? rm.food : 0, "еды");
        return parts.Count > 0 ? string.Join(UiKit.Col(UiKit.MutedHex, " · "), parts) : UiKit.Col(UiKit.MutedHex, t.name == BuildingInstance.CobbleName ? "только плитка" : "бесплатно");
    }

    static void AddCost(List<string> parts, int need, int have, string what)
    {
        if (need <= 0) return;
        string hex = what == "золота" ? UiKit.GoldHex : (what == "дерева" ? UiKit.WoodHex : (what == "камня" ? UiKit.StoneHex : UiKit.FoodHex));
        string shortName = what == "золота" ? "зол" : (what == "дерева" ? "дер" : (what == "камня" ? "кам" : "еды"));
        parts.Add(UiKit.Col(have >= need ? hex : UiKit.BadHex, need + " " + shortName));
    }

    static string MissingLine(BuildingType t)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return "";
        List<string> parts = new List<string>();
        if (rm.gold < t.costGold) parts.Add($"{t.costGold - rm.gold} золота");
        if (rm.wood < t.costWood) parts.Add($"{t.costWood - rm.wood} дерева");
        if (rm.stone < t.costStone) parts.Add($"{t.costStone - rm.stone} камня");
        if (rm.food < t.costFood) parts.Add($"{t.costFood - rm.food} еды");
        return string.Join(", ", parts);
    }

    string Describe(BuildingType t)
    {
        if (t.name == PopulationManager.HouseName && PopulationManager.Instance != null)
            return $"Жильё на {PopulationManager.Instance.HousePerLevel} чел.";
        if (t.name == BuildingInstance.GuestHouseName && PopulationManager.Instance != null)
            return $"Наёмникам: {PopulationManager.Instance.guestPerLevel} мест на уровень";
        if (ShortInfo.TryGetValue(t.name, out string s)) return s;

        if (t.prodWood > 0) return "Добывает дерево";
        if (t.prodStone > 0) return "Добывает камень";
        if (t.prodFood > 0) return "Даёт еду";
        if (t.prodGold > 0) return "Приносит золото";
        return "Постройка";
    }

    Vector3 MouseWorld()
    {
        Vector2 screen = Mouse.current.position.ReadValue();
        return cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, Mathf.Abs(cam.transform.position.z)));
    }

    Vector2Int GetOriginUnderMouse(Vector2Int size)
    {
        Vector3 w = MouseWorld();
        return new Vector2Int(Mathf.RoundToInt(w.x - size.x / 2f), Mathf.RoundToInt(w.y - size.y / 2f));
    }

    List<Vector2Int> GetFootprint(Vector2Int origin, Vector2Int size)
    {
        List<Vector2Int> cells = new List<Vector2Int>();
        for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++)
                cells.Add(new Vector2Int(origin.x + x, origin.y + y));
        return cells;
    }

    bool CellsFree(List<Vector2Int> footprint)
    {
        int half = mapSize / 2;
        foreach (var c in footprint)
        {
            if (c.x < -half || c.x >= half || c.y < -half || c.y >= half) return false;
            if (occupied.ContainsKey(c)) return false;
        }
        return true;
    }

    bool ZoneOk(BuildingType t, List<Vector2Int> footprint)
    {
        if (t == null || Territory.Instance == null) return true;
        if (t.name == BuildingInstance.BridgeName) return true;   // мосту - свои правила (WaterProblem)
        return Territory.Instance.CanPlace(t.name, footprint);
    }

    // Река: обычные здания на воду не ставят; мост - только на воду, от берега или от другой клетки моста.
    // "" - можно.
    string WaterProblem(BuildingType t, List<Vector2Int> footprint)
    {
        if (t == null) return "";
        bool bridge = t.name == BuildingInstance.BridgeName;
        if (!bridge)
        {
            foreach (var c in footprint)
                if (Rivers.IsWater(c)) return "На реке строить нельзя — только мост";
            return "";
        }

        Vector2Int[] dirs = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        Territory tr = Territory.Instance;
        foreach (var c in footprint)
        {
            if (!Rivers.IsWater(c)) return "Мост ставят только на воду";
            if (Crossing.Occupies(c)) return "Здесь стоит переправа";
            bool fromBank = false, fromBridge = false;
            foreach (var d in dirs)
            {
                Vector2Int n = c + d;
                if (occupied.TryGetValue(n, out GameObject o) && o != null)
                {
                    BuildingInstance bi = o.GetComponent<BuildingInstance>();
                    if (bi != null && bi.IsBridge) { fromBridge = true; continue; }
                }
                if (!Rivers.IsWater(n) && (tr == null || tr.InZone(n))) fromBank = true;
            }
            if (!fromBank && !fromBridge) return "Мост тянут от берега в зелёной зоне или от другой клетки моста";
        }
        return "";
    }

    const int DoorOpenArea = 12;

    static bool NeedsDoor(BuildingType t)
    {
        if (t == null) return false;
        string n = t.name;
        if (BuildingInstance.IsRoadName(n) || n == BuildingInstance.WallName || n == BuildingInstance.GateName) return false;
        if (n == BuildingInstance.FarmName || n == BuildingInstance.BridgeName) return false;
        if (n == "Башня" || n == MagicCannonController.TypeName || n == GroundCannonController.TypeName || n == OutpostController.TypeName) return false;
        return true;
    }

    bool CellPassable(Vector2Int c, HashSet<Vector2Int> extra, GameObject ignore)
    {
        int half = mapSize / 2;
        if (c.x < -half || c.x >= half || c.y < -half || c.y >= half) return false;
        if (extra != null && extra.Contains(c)) return false;
        if (!occupied.TryGetValue(c, out GameObject o) || o == null || o == ignore) return !Rivers.IsWater(c);   // через воду к двери не пройти
        BuildingInstance bi = o.GetComponent<BuildingInstance>();
        return bi != null && bi.type != null && (bi.IsRoad || bi.IsWalkable || (bi.IsBarrier && bi.IsRuined));
    }

    bool OpenArea(Vector2Int start, HashSet<Vector2Int> extra, GameObject ignore)
    {
        var seen = new HashSet<Vector2Int> { start };
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(start);
        Vector2Int[] dirs = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };

        while (queue.Count > 0)
        {
            if (seen.Count >= DoorOpenArea) return true;
            Vector2Int c = queue.Dequeue();
            foreach (var d in dirs)
            {
                Vector2Int n = c + d;
                if (seen.Contains(n) || !CellPassable(n, extra, ignore)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return seen.Count >= DoorOpenArea;
    }

    bool HasDoor(Vector2Int o, Vector2Int sz, HashSet<Vector2Int> extra, GameObject ignore)
    {
        for (int x = -1; x <= sz.x; x++)
            for (int y = -1; y <= sz.y; y++)
            {
                bool edgeX = (x == -1 || x == sz.x);
                bool edgeY = (y == -1 || y == sz.y);
                if (edgeX == edgeY) continue;
                Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                if (CellPassable(c, extra, ignore) && OpenArea(c, extra, ignore)) return true;
            }
        return false;
    }

    static bool TouchesSide(BuildingInstance b, List<Vector2Int> cells)
    {
        Vector2Int o = b.OriginCell, sz = b.type.size;
        foreach (var c in cells)
        {
            bool inX = c.x >= o.x && c.x < o.x + sz.x;
            bool inY = c.y >= o.y && c.y < o.y + sz.y;
            if (inX && (c.y == o.y - 1 || c.y == o.y + sz.y)) return true;
            if (inY && (c.x == o.x - 1 || c.x == o.x + sz.x)) return true;
        }
        return false;
    }

    int DoorProblem(BuildingType t, Vector2Int origin, List<Vector2Int> footprint, GameObject ignore)
    {
        if (t == null || BuildingInstance.IsRoadName(t.name) || t.name == BuildingInstance.FarmName || t.name == BuildingInstance.BridgeName) return 0;
        var extra = new HashSet<Vector2Int>(footprint);

        if (NeedsDoor(t) && !HasDoor(origin, t.size, extra, ignore)) return 1;

        foreach (var inst in placedInstances)
        {
            if (inst == null || inst.type == null || !NeedsDoor(inst.type)) continue;
            if (ignore != null && inst.gameObject == ignore) continue;
            if (!TouchesSide(inst, footprint)) continue;
            if (HasDoor(inst.OriginCell, inst.type.size, null, ignore) && !HasDoor(inst.OriginCell, inst.type.size, extra, ignore)) return 2;
        }
        return 0;
    }

    bool CanBuildWhileMoving(List<Vector2Int> footprint, GameObject buildingToMove)
    {
        int half = mapSize / 2;
        foreach (var c in footprint)
        {
            if (c.x < -half || c.x >= half || c.y < -half || c.y >= half) return false;
            if (occupied.ContainsKey(c) && occupied[c] != buildingToMove) return false;
        }
        return true;
    }

    Vector3 CenterOf(Vector2Int origin, Vector2Int size)
    {
        return new Vector3(origin.x + size.x / 2f, origin.y + size.y / 2f, 0f);
    }

    void UpdateGhost(BuildingType type, Vector2Int origin, bool canBuild)
    {
        if (ghost == null) return;
        ghost.SetActive(true);
        FitToCells(ghost, CenterOf(origin, type.size), type.size);
        SpriteRenderer sr = ghost.GetComponentInChildren<SpriteRenderer>();
        if (sr != null)
        {
            Color alphaMod = canBuild ? okColor : badColor;
            sr.color = new Color(type.color.r * alphaMod.r, type.color.g * alphaMod.g, type.color.b * alphaMod.b, alphaMod.a);
        }
    }

    void PlaceBuilding(BuildingType type, Vector2Int origin, List<Vector2Int> footprint, bool deductCost)
    {
        GameObject prefab = type.prefab != null ? type.prefab : buildingPrefab;
        if (prefab == null) return;

        if (deductCost && ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Spend(type.costGold, type.costWood, type.costStone, type.costFood);
        }

        if (deductCost) UpgradeRules.PayBuild(type.name, CountOfType(type.name));

        GameObject b = Instantiate(prefab);
        b.name = type.name;
        FitToCells(b, CenterOf(origin, type.size), type.size);

        SpriteRenderer sr = b.GetComponentInChildren<SpriteRenderer>();
        if (sr != null && type.color != Color.white)
        {
            sr.color = type.color;
        }
        if (sr != null && BuildingInstance.IsRoadName(type.name)) sr.sortingOrder = -10;
        if (sr != null && type.name == BuildingInstance.BridgeName) sr.sortingOrder = -9;   // над рекой, под людьми

        BuildingInstance inst = b.AddComponent<BuildingInstance>();
        inst.type = type;
        inst.OriginCell = origin;

        switch (type.name)
        {
            case "Ратуша": b.AddComponent<TownHallStorage>(); break;
            case BarracksController.TypeName: b.AddComponent<BarracksController>(); break;
            case "Мастерская": b.AddComponent<WorkshopController>(); break;
            case "Башня": b.AddComponent<TowerController>(); break;
            case MagicCannonController.TypeName: b.AddComponent<MagicCannonController>(); break;
            case HealerHouse.TypeName: b.AddComponent<HealerHouse>(); break;
            case BuildingInstance.GateName: b.AddComponent<GateController>(); break;
            case CourierHouse.TypeName: b.AddComponent<CourierHouse>(); break;
            case WarehouseController.TypeName: b.AddComponent<WarehouseController>(); break;
            case ArmoryController.TypeName: b.AddComponent<ArmoryController>(); break;
            case StableController.TypeName: b.AddComponent<StableController>(); break;
            case OutpostController.TypeName: b.AddComponent<OutpostController>(); break;
            case BerryStation.TypeName: b.AddComponent<BerryStation>(); break;
            case KitchenController.TypeName: b.AddComponent<KitchenController>(); break;
            case WagonYard.TypeName: b.AddComponent<WagonYard>(); break;
            case PotionHouse.TypeName: b.AddComponent<PotionHouse>(); break;
            case WeaponForge.TypeName: b.AddComponent<WeaponForge>(); break;
            case FoundryController.TypeName: b.AddComponent<FoundryController>(); break;
            case EngineerYard.TypeName: b.AddComponent<EngineerYard>(); break;
            case MarketHouse.TypeName: b.AddComponent<MarketHouse>(); break;
            case BuildingInstance.BridgeName: b.AddComponent<BridgeController>(); break;
            case GroundCannonController.TypeName: b.AddComponent<GroundCannonController>(); break;
            case MagicArmory.TypeName: b.AddComponent<MagicArmory>(); break;
            case LifeAltar.TypeName: b.AddComponent<LifeAltar>(); break;
            case EngineerBarracks.TypeName: b.AddComponent<EngineerBarracks>(); break;   // сама добавит казарму в режиме инженеров
            case IndustrialPlant.TypeName: b.AddComponent<IndustrialPlant>(); break;
            case WarBuilderYard.TypeName: b.AddComponent<WarBuilderYard>(); break;
        }

        foreach (var c in footprint) occupied[c] = b;
        placedInstances.Add(inst);
        Territory.MarkDirty();
    }

    void StartMoveBuildingUnderMouse()
    {
        if (BuildBlocked)
        {
            WarManager.Notify("Идёт война — переносить здания нельзя", false);
            return;
        }

        Vector3 w = MouseWorld();
        Vector2Int cell = new Vector2Int(Mathf.FloorToInt(w.x), Mathf.FloorToInt(w.y));
        if (!occupied.TryGetValue(cell, out GameObject b)) return;

        BuildingInstance inst = b.GetComponent<BuildingInstance>();
        if (inst == null) return;

        if (inst.IsTownHall)
        {
            WarManager.Notify("Ратушу переносить нельзя", false);
            return;
        }
        if (inst.IsBridge)
        {
            WarManager.Notify("Мост не переносят — снесите клетку и постройте заново", false);
            return;
        }

        isMoving = true;
        movingBuilding = b;
        movingBuildingType = inst.type;
        movingBuilding.SetActive(false);
        selectedInstance = null;
    }

    void ConfirmMove(Vector2Int newOrigin, List<Vector2Int> newFootprint)
    {
        List<Vector2Int> keysToRemove = new List<Vector2Int>();
        foreach (var kv in occupied)
        {
            if (kv.Value == movingBuilding) keysToRemove.Add(kv.Key);
        }
        foreach (var c in keysToRemove) occupied.Remove(c);

        movingBuilding.SetActive(true);
        FitToCells(movingBuilding, CenterOf(newOrigin, movingBuildingType.size), movingBuildingType.size);

        BuildingInstance movedInst = movingBuilding.GetComponent<BuildingInstance>();
        if (movedInst != null) movedInst.OriginCell = newOrigin;

        foreach (var c in newFootprint) occupied[c] = movingBuilding;

        isMoving = false;
        movingBuilding = null;
        movingBuildingType = null;
    }

    void CancelMove()
    {
        if (movingBuilding != null)
        {
            movingBuilding.SetActive(true);
        }
        isMoving = false;
        movingBuilding = null;
        movingBuildingType = null;
    }

    // ---------- Снос ----------
    public const float DemolishRefund = 0.5f;        // какая доля цены постройки возвращается в Ратушу
    public const float DemolishRuinRefund = 0.25f;   // если сносят руины

    // X: спросить, точно ли сносить здание под курсором
    void AskDemolishUnderMouse()
    {
        Vector3 w = MouseWorld();
        Vector2Int cell = new Vector2Int(Mathf.FloorToInt(w.x), Mathf.FloorToInt(w.y));

        if (!occupied.TryGetValue(cell, out GameObject b) || b == null) return;
        if (BuildBlocked)
        {
            WarManager.Notify("Идёт война — сносить здания нельзя", false);
            return;
        }
        if (b.name == townHall.name)
        {
            WarManager.Notify("Ратушу снести нельзя", false);
            return;
        }
        if (starterBuildings.Contains(b))
        {
            WarManager.Notify("Стартовые постройки снести нельзя (их можно двигать и улучшать)", false);
            return;
        }

        BuildingInstance inst = b.GetComponent<BuildingInstance>();
        string name = b.name;
        int lvl = inst != null ? inst.level : 1;
        string caption = lvl > 1 ? $"Снести «{name}» · Ур. {lvl}?" : $"Снести «{name}»?";
        DemolishDialog.Ask(b, caption, DescribeRefund(inst), () => Demolish(b));
    }

    // Что вернётся в Ратушу при сносе: доля цены постройки (и досок/плит, если они были нужны)
    void RefundFor(BuildingInstance inst, out int g, out int w, out int s, out int f, out Refined[] extra, out QualityTier tier)
    {
        g = w = s = f = 0;
        extra = null;
        tier = QualityTier.Medium;
        if (inst == null || inst.type == null) return;

        BuildingType t = inst.type;
        float k = inst.IsRuined ? DemolishRuinRefund : DemolishRefund;
        g = Mathf.FloorToInt(t.costGold * k);
        w = Mathf.FloorToInt(t.costWood * k);
        s = Mathf.FloorToInt(t.costStone * k);
        f = Mathf.FloorToInt(t.costFood * k);

        // Доски/плиты за постройку зависят от номера здания; сносим последнее по счёту
        Refined[] paid = UpgradeRules.BuildExtra(t.name, Mathf.Max(0, CountOfType(t.name) - 1), out tier);
        if (paid == null) return;
        List<Refined> back = new List<Refined>();
        foreach (var it in paid)
        {
            int n = Mathf.FloorToInt(it.amount * k);
            if (n > 0) back.Add(new Refined { kind = it.kind, amount = n });
        }
        if (back.Count > 0) extra = back.ToArray();
    }

    string DescribeRefund(BuildingInstance inst)
    {
        RefundFor(inst, out int g, out int w, out int s, out int f, out Refined[] extra, out _);
        string pct = inst != null && inst.IsRuined ? "четверть цены (это руины)" : "половина цены постройки";
        string text = "Вернётся в Ратушу " + pct + ":\n" + UiKit.Cost(g, w, s, f);
        if (extra != null)
        {
            List<string> parts = new List<string>();
            foreach (var it in extra) parts.Add(UpgradeRules.KindLabel(it.kind) + " " + it.amount);
            text += UiKit.Col(UiKit.MutedHex, " · ") + string.Join(", ", parts);
        }
        if (inst != null && !inst.IsRuined && inst.StoredTotal > 0)
            text += "\nИ всё, что лежит в здании: " + inst.StoredTotal + " ед.";
        text += "\n" + UiKit.Col(UiKit.MutedHex, "Отменить снос нельзя.");
        return text;
    }

    // Снос после подтверждения
    void Demolish(GameObject b)
    {
        if (b == null) return;
        if (BuildBlocked)
        {
            WarManager.Notify("Идёт война — сносить здания нельзя", false);
            return;
        }

        BuildingInstance inst = b.GetComponent<BuildingInstance>();

        // Возврат в Ратушу: часть цены и то, что лежало на складе здания
        if (inst != null)
        {
            RefundFor(inst, out int g, out int w, out int s, out int f, out Refined[] extra, out QualityTier tier);
            if (ResourceManager.Instance != null) ResourceManager.Instance.Add(g, w, s, f);
            if (extra != null && TownHallStorage.Instance != null)
                foreach (var it in extra) TownHallStorage.Instance.Add(it.kind, tier, it.amount);
            if (!inst.IsRuined) inst.CollectToTreasury();
            List<string> got = new List<string>();
            if (g > 0) got.Add(g + " зол");
            if (w > 0) got.Add(w + " дер");
            if (s > 0) got.Add(s + " кам");
            if (f > 0) got.Add(f + " еды");
            if (extra != null) foreach (var it in extra) got.Add(UpgradeRules.KindLabel(it.kind) + " " + it.amount);
            WarManager.Notify(got.Count > 0 ? $"«{b.name}» снесено. В Ратушу вернулось: {string.Join(", ", got)}" : $"«{b.name}» снесено", true);
        }

        List<Vector2Int> toFree = new List<Vector2Int>();
        foreach (var kv in occupied)
            if (kv.Value == b) toFree.Add(kv.Key);
        foreach (var c in toFree) occupied.Remove(c);

        if (inst != null)
        {
            if (selectedInstance == inst) selectedInstance = null;
            placedInstances.Remove(inst);
        }

        Destroy(b);
        Territory.MarkDirty();
    }

    void FitToCells(GameObject obj, Vector3 center, Vector2Int size)
    {
        SpriteRenderer sr = obj.GetComponentInChildren<SpriteRenderer>();
        if (sr == null || sr.sprite == null) { obj.transform.position = center; return; }

        obj.transform.rotation = Quaternion.identity;
        obj.transform.localScale = Vector3.one;
        obj.transform.position = Vector3.zero;

        Vector3 s = sr.bounds.size;
        if (s.x <= 0.0001f || s.y <= 0.0001f) return;

        obj.transform.localScale = new Vector3(size.x / s.x, size.y / s.y, 1f);
        Vector3 offset = center - sr.bounds.center;
        obj.transform.position = new Vector3(offset.x, offset.y, 0f);
    }

    // Закреплённая подсказка по управлению (кнопка «?» рядом с «Постройки»)
    private bool showControls = false;

    const string ControlsTip =
        "<b>Управление</b>\n" +
        "ЛКМ — выбрать здание, жителя или врага\n" +
        "M — перенести здание под курсором\n" +
        "X — снести здание под курсором (с вопросом; в войну нельзя)\n" +
        "1–9 — взять постройку в руки\n" +
        "ПКМ — камера, в режиме стройки — отмена\n" +
        "Esc — отмена";

    void OnGUI()
    {
        UiKit.Apply();
        if (buildingTypes == null || buildingTypes.Length == 0) return;
        if (RaidManager.ViewingBattle) return;

        // Кнопка «Постройки» и «?» рядом (обе внутри buildButtonRect - мышь над ними не кликает по карте)
        bool war = BuildBlocked;
        Rect mainBtn = new Rect(buildButtonRect.x, buildButtonRect.y, buildButtonRect.width - 44f, buildButtonRect.height);
        Rect helpBtn = new Rect(mainBtn.xMax + 4f, buildButtonRect.y, 40f, buildButtonRect.height);

        UiKit.Kind kind = war ? UiKit.Kind.Danger : ((shopOpen || isBuildingMode) ? UiKit.Kind.Primary : UiKit.Kind.Normal);
        GUIStyle bigBtn = new GUIStyle(UiKit.ButtonStyle(kind)) { fontSize = 15, fontStyle = FontStyle.Bold };
        string btnText = war ? "Строить нельзя" : (shopOpen ? "Закрыть" : "Постройки");
        if (GUI.Button(mainBtn, btnText, bigBtn) && !isMoving)
            SetShopOpen(!shopOpen);
        UiKit.Tip(mainBtn, war ? "Идёт война — строить и переносить здания нельзя" : "Окно построек (Esc — закрыть)");

        if (UiKit.Button(helpBtn, "?", showControls ? UiKit.Kind.Primary : UiKit.Kind.Ghost, showControls ? null : ControlsTip))
            showControls = !showControls;

        float underY = buildButtonRect.yMax + 6f;
        if (isMoving) DrawMoveHint(underY);
        else if (showControls && !isBuildingMode) DrawControlsBox(underY);

        if (shopOpen)
        {
            DrawShop();
            return;
        }
        if (isBuildingMode && !isMoving) DrawCursorTip();

        if (selectedEnemy != null && EnemyAgent.Alive(selectedEnemy) && !isBuildingMode && !isMoving)
        {
            EnemyInfoPanel.DrawMarker(selectedEnemy);
            infoPanelRect = DrawScrolled(selectedEnemy, 265f, 45f, () => EnemyInfoPanel.Draw(selectedEnemy, 0f, 0f, 330f));
        }

        if (selectedCitizen != null && !isBuildingMode && !isMoving)
        {
            CitizenInfoPanel.DrawMarker(selectedCitizen);
            infoPanelRect = DrawScrolled(selectedCitizen, 265f, 45f, () => CitizenInfoPanel.Draw(selectedCitizen, 0f, 0f, 330f));
        }

        if (selectedCamp != null && !isBuildingMode && !isMoving)
        {
            infoPanelRect = DrawScrolled(selectedCamp, 265f, 45f, () => selectedCamp.DrawPanel(0f, 0f, 310f));
        }

        if (selectedInstance != null && selectedInstance.gameObject != null)
        {
            infoPanelRect = DrawScrolled(selectedInstance, 265f, 45f, DrawBuildingPanel);

            BarracksController bm = selectedInstance.GetComponent<BarracksController>();
            if (bm != null) bm.DrawMarkers();

            DrawDurability(selectedInstance);
        }
    }

    // Режим переноса: подсказка видна всё время, пока здание «в руках»
    void DrawMoveHint(float y)
    {
        string door = DoorHint();
        float h = door != "" ? 70f : 52f;
        Rect r = new Rect(buildButtonRect.x, y, buildButtonRect.width, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 10f, r.y + 5f, r.width - 20f, 20f), UiKit.Col(UiKit.AccentHex, UiKit.B("Перенос здания")));
        GUI.Label(new Rect(r.x + 10f, r.y + 26f, r.width - 20f, 20f), "ЛКМ — поставить   Esc — отмена", UiKit.MutedStyle);
        if (door != "") GUI.Label(new Rect(r.x + 10f, r.y + 46f, r.width - 20f, 20f), door.TrimStart('\n'), UiKit.Small);
    }

    void DrawControlsBox(float y)
    {
        float h = UiKit.TextH(ControlsTip, buildButtonRect.width - 20f, UiKit.Small) + 12f;
        Rect r = new Rect(buildButtonRect.x, y, buildButtonRect.width, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, h - 10f), ControlsTip, UiKit.Small);
    }

    Vector2 panelScroll;
    object panelKey;
    float panelContentW = 330f, panelContentH = 300f;
    const float PanelBottomMargin = 130f;
    const float PanelBarW = 16f;

    Rect DrawScrolled(object key, float x, float y, System.Func<Rect> draw)
    {
        if (!ReferenceEquals(panelKey, key)) { panelKey = key; panelScroll = Vector2.zero; }

        float maxH = Mathf.Max(120f, Screen.height - y - PanelBottomMargin);
        bool need = panelContentH > maxH;
        float viewH = Mathf.Min(panelContentH, maxH);
        float viewW = panelContentW + (need ? PanelBarW : 0f);
        Rect view = new Rect(x, y, viewW, viewH);

        panelScroll = GUI.BeginScrollView(view, panelScroll, new Rect(0f, 0f, panelContentW, panelContentH),
            GUIStyle.none, GUI.skin.verticalScrollbar);
        Rect r = draw();
        GUI.EndScrollView();

        panelContentW = Mathf.Max(100f, r.xMax);
        panelContentH = Mathf.Max(40f, r.yMax);
        return view;
    }

    // ---------- Панели зданий ----------

    const float PanelTop = UiKit.TitleH + 6f;   // где начинается содержимое под шапкой окна
    const float WorkersRowH = 30f;               // строка «- Рабочие + Авто» (PopulationManager.DrawWorkersRow)

    // Окно панели с шапкой «Название · Ур. N»
    static void PanelWindow(float x, float y, float w, float h, string name, int lvl)
    {
        UiKit.Window(new Rect(x, y, w, h), lvl > 0 ? $"{name}  {UiKit.Col(UiKit.MutedHex, "· Ур. " + lvl)}" : name);
    }

    // Высота строки состояния «что не так» (пусто - 0)
    static float StatusH(string text, float w)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        return UiKit.TextH(text, w, UiKit.Small) + 4f;
    }

    static void DrawStatus(float x, float y, float w, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        GUI.Label(new Rect(x, y, w, UiKit.TextH(text, w, UiKit.Small)), UiKit.Col(UiKit.BadHex, text), UiKit.Small);
    }

    static string Stored(string what, int n, string hex)
    {
        return n > 0 ? UiKit.Col(hex, $"{what} {n}") : null;
    }

    Rect DrawBuildingPanel()
    {
        const float infoX = 0f, infoY = 0f, infoWidth = 310f;
        Rect empty = new Rect(0f, 0f, infoWidth + 5f, 40f);
        const float cx = infoX + UiKit.Pad, cw = infoWidth - UiKit.Pad * 2f;

        string bName = selectedInstance.type != null ? selectedInstance.type.name : "Ратуша";
        int lvl = selectedInstance.level;

        if (bName == "Ратуша")
        {
            string storageText = "";
            int lines = 0;
            if (TownHallStorage.Instance != null)
            {
                storageText = TownHallStorage.Instance.BuildListText(out lines).TrimEnd('\n');
                if (lines == 0) storageText = UiKit.Col(UiKit.MutedHex, "Пусто — доски, плиты и сталь делают в Мастерской");
            }
            else storageText = UiKit.Col(UiKit.MutedHex, "Склад недоступен");

            int outposts = Territory.Instance != null ? Territory.Instance.ActiveOutposts : 0;
            int idle = Territory.Instance != null ? Territory.Instance.IdleOutposts : 0;

            float storeH = UiKit.TextH(storageText, cw, UiKit.Small) + 4f;
            float zoneY = PanelTop;
            float workersY = zoneY + UiKit.RowH * 2f + (idle > 0 ? UiKit.RowH : 0f) + 4f;
            float storeY = workersY + WorkersRowH;
            float upY = storeY + UiKit.SectionH + storeH + 4f;
            float h = upY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;

            PanelWindow(infoX, infoY, infoWidth, h, "Ратуша", lvl);

            // Главное: зона застройки и аванпосты
            string next = lvl < UpgradeRules.MaxLevelFor(selectedInstance)
                ? UiKit.Col(UiKit.MutedHex, $"  → {Territory.HallRadiusFor(lvl + 1):F0} на Ур. {lvl + 1}") : "";
            Rect zr = new Rect(cx, infoY + zoneY, cw, UiKit.RowH);
            UiKit.Row(zr, "Зона застройки", $"{Territory.HallRadiusFor(lvl):F0} кл.{next}");
            UiKit.Tip(zr, "Радиус зелёной зоны вокруг Ратуши: строить можно только в ней. Растёт с уровнем Ратуши и аванпостами.");
            UiKit.Row(new Rect(cx, infoY + zoneY + UiKit.RowH, cw, UiKit.RowH), "Аванпостов", outposts.ToString());
            if (idle > 0)
            {
                Rect ir = new Rect(cx, infoY + zoneY + UiKit.RowH * 2f, cw, UiKit.RowH);
                GUI.Label(ir, UiKit.Col(UiKit.WarnHex, $"Не работают: {idle} (нет дороги или снабжения)"), UiKit.Small);
            }

            if (PopulationManager.Instance != null)
                PopulationManager.Instance.DrawWorkersRow(cx, infoY + workersY, selectedInstance);

            float sy = UiKit.Section(cx, infoY + storeY, cw, "Склад материалов");
            GUI.Label(new Rect(cx, sy, cw, storeH), storageText, UiKit.Small);

            float uy = UiKit.Section(cx, infoY + upY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }
        if (bName == OutpostController.TypeName)
        {
            OutpostController outpost = selectedInstance.GetComponent<OutpostController>();
            return outpost != null ? outpost.DrawPanel(infoX, infoY, 340f) : empty;
        }
        if (bName == "Жилой дом" || bName == BuildingInstance.GuestHouseName)
        {
            bool guest = bName == BuildingInstance.GuestHouseName;
            var pm = PopulationManager.Instance;
            int cap = pm != null ? (guest ? pm.guestPerLevel : pm.HousePerLevel) * lvl : 0;
            int living = pm != null ? pm.ResidentsOf(selectedInstance) : 0;
            int athome = pm != null ? pm.AtHome(selectedInstance) : 0;

            float upY = PanelTop + 20f + 6f + UiKit.RowH + 4f;
            float h = upY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;
            PanelWindow(infoX, infoY, infoWidth, h, bName, lvl);

            float cy = infoY + PanelTop;
            Rect bar = new Rect(cx, cy, cw, 20f);
            float frac = cap > 0 ? living / (float)cap : 0f;
            UiKit.Bar(bar, frac, living >= cap && cap > 0 ? UiKit.Accent : UiKit.Good, (guest ? "Наёмников" : "Живёт") + $": {living}/{cap}");
            UiKit.Tip(bar, guest
                ? $"Сюда селятся только купленные на рынке бойцы — свои жители это место не займут.\n{(pm != null ? pm.guestPerLevel : 0)} мест на уровень."
                : $"Мест для горожан: {(pm != null ? pm.HousePerLevel : 0)} на уровень дома.");
            cy += 26f;
            UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), guest ? "Сейчас внутри" : "Сейчас дома", athome.ToString());

            float uy = UiKit.Section(cx, infoY + upY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }
        if (bName == "Мастерская")
        {
            WorkshopController workshop = selectedInstance.GetComponent<WorkshopController>();
            if (workshop == null) return empty;

            int recipeCount = 0;
            if (workshop.availableRecipes != null)
                foreach (var rc in workshop.availableRecipes) if (workshop.Unlocked(rc)) recipeCount++;

            string status = selectedInstance.isWorking ? "" : selectedInstance.StatusText;
            float statusH = StatusH(status, cw);
            float barY = PanelTop;
            float recipesY = barY + 24f + statusH;
            float workersY = recipesY + UiKit.SectionH + recipeCount * 34f + 2f;
            float upgradeY = workersY + WorkersRowH;
            float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;

            PanelWindow(infoX, infoY, infoWidth, h, "Мастерская", lvl);

            // Что делает сейчас
            Rect br = new Rect(cx, infoY + barY, cw, 20f);
            if (workshop.IsBusy) UiKit.Bar(br, workshop.CraftProgress, UiKit.Info, StripTags(workshop.StatusLine));
            else GUI.Label(br, workshop.StatusLine, UiKit.MutedStyle);
            DrawStatus(cx, infoY + barY + 24f, cw, status);

            float btnY = UiKit.Section(cx, infoY + recipesY, cw, "Заказать");
            if (workshop.availableRecipes != null)
            {
                foreach (var recipe in workshop.availableRecipes)
                {
                    if (!workshop.Unlocked(recipe)) continue;
                    bool ok = !workshop.IsBusy && workshop.CanAfford(recipe);
                    string label = $"{recipe.recipeName}  {UiKit.Col(UiKit.MutedHex, recipe.CostText + " → " + recipe.outputAmount + " шт.")}";
                    string tip = workshop.IsBusy ? "Мастерская занята — дождитесь конца работы"
                        : (ok ? $"Сырьё: {recipe.CostText}. Готово: {recipe.outputAmount} шт." : $"Не хватает сырья: нужно {recipe.CostText}");
                    if (UiKit.Button(new Rect(cx, btnY, cw, 30f), label, ok, UiKit.Kind.Normal, tip))
                        workshop.StartCraft(recipe);
                    btnY += 34f;
                }
            }

            if (PopulationManager.Instance != null)
                PopulationManager.Instance.DrawWorkersRow(cx, infoY + workersY, selectedInstance);

            float uy = UiKit.Section(cx, infoY + upgradeY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }
        if (bName == BarracksController.TypeName)
        {
            BarracksController barracks = selectedInstance.GetComponent<BarracksController>();
            if (barracks == null) return empty;
            if (barracks.PickingTarget) return barracks.DrawPickBanner(infoX, infoY, 420f);
            return barracks.DrawPanel(infoX, infoY, 460f);
        }
        if (bName == BuildingInstance.GateName)
        { var c = selectedInstance.GetComponent<GateController>(); return c != null ? c.DrawPanel(infoX, infoY, 330f) : empty; }
        if (bName == HealerHouse.TypeName)
        { var c = selectedInstance.GetComponent<HealerHouse>(); return c != null ? c.DrawPanel(infoX, infoY, 340f) : empty; }
        if (bName == MagicCannonController.TypeName)
        { var c = selectedInstance.GetComponent<MagicCannonController>(); return c != null ? c.DrawPanel(infoX, infoY, 340f) : empty; }
        if (bName == GroundCannonController.TypeName)
        { var c = selectedInstance.GetComponent<GroundCannonController>(); return c != null ? c.DrawPanel(infoX, infoY, 380f) : empty; }
        if (bName == MagicArmory.TypeName)
        { var c = selectedInstance.GetComponent<MagicArmory>(); return c != null ? c.DrawPanel(infoX, infoY, 420f) : empty; }
        if (bName == LifeAltar.TypeName)
        { var c = selectedInstance.GetComponent<LifeAltar>(); return c != null ? c.DrawPanel(infoX, infoY, 380f) : empty; }
        if (bName == EngineerBarracks.TypeName)
        { var c = selectedInstance.GetComponent<EngineerBarracks>(); return c != null ? c.DrawPanel(infoX, infoY, 460f) : empty; }
        if (bName == IndustrialPlant.TypeName)
        { var c = selectedInstance.GetComponent<IndustrialPlant>(); return c != null ? c.DrawPanel(infoX, infoY, 420f) : empty; }
        if (bName == WarBuilderYard.TypeName)
        { var c = selectedInstance.GetComponent<WarBuilderYard>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == CourierHouse.TypeName)
        { var c = selectedInstance.GetComponent<CourierHouse>(); return c != null ? c.DrawPanel(infoX, infoY, 370f) : empty; }
        if (bName == WarehouseController.TypeName)
        { var c = selectedInstance.GetComponent<WarehouseController>(); return c != null ? c.DrawPanel(infoX, infoY, 360f) : empty; }
        if (bName == ArmoryController.TypeName)
        { var c = selectedInstance.GetComponent<ArmoryController>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == StableController.TypeName)
        { var c = selectedInstance.GetComponent<StableController>(); return c != null ? c.DrawPanel(infoX, infoY, 380f) : empty; }
        if (bName == BerryStation.TypeName)
        { var c = selectedInstance.GetComponent<BerryStation>(); return c != null ? c.DrawPanel(infoX, infoY, 380f) : empty; }
        if (bName == KitchenController.TypeName)
        { var c = selectedInstance.GetComponent<KitchenController>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == PotionHouse.TypeName)
        { var c = selectedInstance.GetComponent<PotionHouse>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == WeaponForge.TypeName)
        { var c = selectedInstance.GetComponent<WeaponForge>(); return c != null ? c.DrawPanel(infoX, infoY, 440f) : empty; }
        if (bName == WagonYard.TypeName)
        { var c = selectedInstance.GetComponent<WagonYard>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == FoundryController.TypeName)
        { var c = selectedInstance.GetComponent<FoundryController>(); return c != null ? c.DrawPanel(infoX, infoY, 430f) : empty; }
        if (bName == EngineerYard.TypeName)
        { var c = selectedInstance.GetComponent<EngineerYard>(); return c != null ? c.DrawPanel(infoX, infoY, 430f) : empty; }
        if (bName == MarketHouse.TypeName)
        { var c = selectedInstance.GetComponent<MarketHouse>(); return c != null ? c.DrawPanel(infoX, infoY, 400f) : empty; }
        if (bName == BuildingInstance.BridgeName)
        {
            var c = selectedInstance.GetComponent<BridgeController>();
            if (c == null) return empty;
            Rect pr = c.DrawPanel(infoX, infoY, 360f);
            return pr;
        }

        if (bName == "Башня")
        {
            TowerController tower = selectedInstance.GetComponent<TowerController>();
            float modH = EngineerYard.TowerSectionHeight(tower);
            float modY = PanelTop + UiKit.RowH * 3f + 4f;
            float upY = modY + modH;
            float h = upY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;
            PanelWindow(infoX, infoY, infoWidth, h, bName, lvl);

            float cy = infoY + PanelTop;
            string state = selectedInstance.IsRuined ? UiKit.Col(UiKit.BadHex, "Разрушена") : UiKit.Col(UiKit.GoodHex, "Готова к бою");
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), state);
            cy += UiKit.RowH;
            if (tower != null)
            {
                UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Урон", $"{tower.ArrowDamage:F0}");
                UiKit.Row(new Rect(cx, cy + UiKit.RowH, cw, UiKit.RowH), "Радиус", $"{tower.Range:F1} кл.");
            }
            else GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), "Обеспечивает оборону базы.", UiKit.MutedStyle);

            if (modH > 0f) EngineerYard.DrawTowerSection(cx, infoY + modY, cw, tower);
            float uy = UiKit.Section(cx, infoY + upY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }
        if (bName == BuildingInstance.WallName)
        {
            float upY = PanelTop + UiKit.RowH + 4f;
            float h = upY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;
            PanelWindow(infoX, infoY, infoWidth, h, bName, lvl);
            UiKit.Row(new Rect(cx, infoY + PanelTop, cw, UiKit.RowH), "Макс. прочность", $"{selectedInstance.MaxHp:F0}");
            float uy = UiKit.Section(cx, infoY + upY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }

        {
            // Обычное здание: добыча и склад
            var parts = new List<string>();
            System.Action<string> add = s => { if (s != null) parts.Add(s); };
            add(Stored("Золото", selectedInstance.storedGold, UiKit.GoldHex));
            add(Stored("Дерево", selectedInstance.storedWood, UiKit.WoodHex));
            add(Stored("Камень", selectedInstance.storedStone, UiKit.StoneHex));
            add(Stored("Еда", selectedInstance.storedFood, UiKit.FoodHex));
            add(Stored("Руда", selectedInstance.storedIron, "#B0C4DE"));
            add(Stored("Овёс", selectedInstance.storedOats, "#E8D890"));
            add(Stored("Ягоды", selectedInstance.storedBerries, "#F0A0C8"));
            add(Stored("Травы", selectedInstance.storedHerbs, "#90E060"));
            add(Stored("Смола", selectedInstance.storedResin, "#E0A040"));
            add(Stored("Листва", selectedInstance.storedLeaves, "#7FD060"));
            bool hasAny = parts.Count > 0;
            string resText = hasAny ? string.Join(UiKit.Col(UiKit.MutedHex, " · "), parts) : UiKit.Col(UiKit.MutedHex, "Склад пуст");

            string zoneTag = ResourceZones.BuildingZoneTag(selectedInstance);
            string status = selectedInstance.isWorking ? "" : selectedInstance.StatusText;
            bool showHealth = selectedInstance.workers > 0;

            float statusH = StatusH(status, cw);
            float zoneH = string.IsNullOrEmpty(zoneTag) ? 0f : UiKit.RowH;
            float storeY = PanelTop + statusH + zoneH;
            float resH = UiKit.TextH(resText, cw, UiKit.Small) + 2f;
            float btnY = storeY + UiKit.SectionH + resH + 2f;
            float workersY = btnY + (hasAny ? 34f : 0f);
            float healthY = workersY + WorkersRowH;
            float upgradeY = healthY + (showHealth ? UiKit.RowH : 0f) + 4f;
            float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(selectedInstance) + 8f;

            PanelWindow(infoX, infoY, infoWidth, h, bName, lvl);

            // Что не так - сразу под шапкой
            DrawStatus(cx, infoY + PanelTop, cw, status);
            if (zoneH > 0f)
            {
                Rect zr = new Rect(cx, infoY + PanelTop + statusH, cw, UiKit.RowH);
                GUI.Label(zr, zoneTag, UiKit.Small);
                UiKit.Tip(zr, "Зона ресурса под зданием: в богатой зоне добыча быстрее.");
            }

            float sy = UiKit.Section(cx, infoY + storeY, cw, $"Склад · лимит {selectedInstance.Capacity}");
            GUI.Label(new Rect(cx, sy, cw, resH), resText, UiKit.Small);

            if (hasAny)
            {
                float halfW = (cw - 6f) / 2f;
                string hereLabel = selectedInstance.courierOnTheWay ? "Курьер идёт..." : "Собрать";
                if (UiKit.Button(new Rect(cx, infoY + btnY, halfW, UiKit.BtnH), hereLabel, !selectedInstance.courierOnTheWay, UiKit.Kind.Primary,
                        selectedInstance.courierOnTheWay ? "Курьер уже идёт за добычей" : "Курьер отнесёт добычу отсюда в Ратушу"))
                    CollectFrom(selectedInstance);

                if (UiKit.Button(new Rect(cx + halfW + 6f, infoY + btnY, halfW, UiKit.BtnH), "Со всех", UiKit.Kind.Normal,
                        $"Собрать добычу со всех зданий «{bName}»"))
                    CollectAllOfType(selectedInstance.type);
            }

            if (PopulationManager.Instance != null)
                PopulationManager.Instance.DrawWorkersRow(cx, infoY + workersY, selectedInstance);

            if (showHealth)
            {
                int hpPct = Mathf.RoundToInt(selectedInstance.WorkersHealth * 100f);
                int effPct = Mathf.RoundToInt(selectedInstance.HealthFactor * 100f);
                string hcol = effPct >= 95 ? UiKit.GoodHex : (effPct >= 75 ? UiKit.WarnHex : UiKit.BadHex);
                Rect hr = new Rect(cx, infoY + healthY, cw, UiKit.RowH);
                UiKit.Row(hr, "Самочувствие рабочих", UiKit.Col(hcol, $"{hpPct}%  → {effPct}%"));
                UiKit.Tip(hr, $"Голодные и раненые рабочие работают медленнее.\nСамочувствие {hpPct}% → выработка {effPct}%.");
            }

            float uy = UiKit.Section(cx, infoY + upgradeY, cw, "Улучшение");
            UpgradeRules.DrawUpgradeSection(cx, uy, cw, selectedInstance);
            return new Rect(infoX, infoY, infoWidth + 5, h);
        }
    }

    // Убрать теги <b>, <color> и т.п. (для текста поверх полоски)
    static string StripTags(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        bool tag = false;
        foreach (char ch in s)
        {
            if (ch == '<') { tag = true; continue; }
            if (ch == '>') { tag = false; continue; }
            if (!tag) sb.Append(ch);
        }
        return sb.ToString();
    }

    // ---------- Окно построек ----------

    const float CardW = 220f, CardH = 112f, CardGap = 10f;
    const int ShopCols = 3;

    // Вкладки окна построек (группы выводятся из названий зданий; механика не меняется)
    static readonly string[] ShopTabs = { "Все", "Хозяйство", "Ремёсла", "Оборона", "Дороги" };
    private int shopTab = 0;

    static int CategoryOf(string n)
    {
        if (BuildingInstance.IsRoadName(n) || n == BuildingInstance.BridgeName) return 4;
        if (n == BuildingInstance.WallName || n == BuildingInstance.GateName || n == "Башня" || n == BarracksController.TypeName
            || n == MagicCannonController.TypeName || n == GroundCannonController.TypeName || n == EngineerBarracks.TypeName
            || n == WarBuilderYard.TypeName) return 3;
        if (n == "Мастерская" || n == ArmoryController.TypeName || n == StableController.TypeName || n == KitchenController.TypeName
            || n == PotionHouse.TypeName || n == WeaponForge.TypeName || n == FoundryController.TypeName || n == EngineerYard.TypeName
            || n == MagicArmory.TypeName || n == LifeAltar.TypeName || n == IndustrialPlant.TypeName) return 2;
        return 1;   // жильё, добыча, склады, торговля, аванпост
    }

    void DrawShop()
    {
        var shown = new List<int>();
        for (int k = 0; k < buildingTypes.Length; k++)
        {
            if (buildingTypes[k] == null || !PathAllows(buildingTypes[k].name)) continue;
            if (shopTab > 0 && CategoryOf(buildingTypes[k].name) != shopTab) continue;
            shown.Add(k);
        }
        int n = shown.Count;
        int rows = Mathf.Max(1, Mathf.CeilToInt(n / (float)ShopCols));
        float contentH = rows * (CardH + CardGap);

        const float tabsH = 28f, footH = 28f, top = 56f, bottomGap = 50f;
        const float headH = UiKit.TitleH + 6f + tabsH + 8f;
        float maxViewH = Mathf.Max(CardH + CardGap, Screen.height - top - bottomGap - headH - footH);
        float viewH = Mathf.Min(contentH, maxViewH);
        bool scrolling = contentH > viewH + 0.5f;
        shopMaxScroll = Mathf.Max(0f, contentH - viewH);
        shopScroll = Mathf.Clamp(shopScroll, 0f, shopMaxScroll);

        const float barW = 16f;
        float w = ShopCols * CardW + (ShopCols + 1) * CardGap + (scrolling ? barW : 0f);
        float h = headH + viewH + footH;
        float x = (Screen.width - w) / 2f;
        float y = Mathf.Max(top, (Screen.height - h) / 2f);
        shopRect = new Rect(x, y, w, h);

        UiKit.Window(shopRect, "Постройки", out bool close);
        if (close) { SetShopOpen(false); return; }

        int tab = UiKit.Tabs(new Rect(x + CardGap, y + UiKit.TitleH + 6f, w - CardGap * 2f, tabsH - 2f), ShopTabs, shopTab);
        if (tab != shopTab) { shopTab = tab; shopScroll = 0f; }

        Rect view = new Rect(x, y + headH, w - (scrolling ? barW : 0f), viewH);
        bool mouseInView = view.Contains(Event.current.mousePosition);

        GUI.BeginGroup(view);
        for (int i = 0; i < n; i++)
        {
            int col = i % ShopCols, row = i / ShopCols;
            Rect r = new Rect(CardGap + col * (CardW + CardGap), row * (CardH + CardGap) - shopScroll, CardW, CardH);
            if (r.yMax < 0f || r.y > viewH) continue;
            DrawCard(shown[i], r, mouseInView);
        }
        if (n == 0) GUI.Label(new Rect(CardGap, 10f, w - CardGap * 2f, 22f), "Здесь пока ничего нет", UiKit.MutedStyle);
        GUI.EndGroup();

        if (scrolling)
        {
            shopScroll = GUI.VerticalScrollbar(new Rect(view.xMax, view.y, barW - 2f, viewH), shopScroll, viewH, 0f, contentH);
        }

        GUIStyle foot = new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
        Rect fr = new Rect(x, shopRect.yMax - footH, w, footH - 4f);
        GUI.Label(fr, "Только в зелёной зоне  ·  Shift — несколько  ·  ПКМ / Esc — отмена", foot);
        UiKit.Tip(fr, "Строить можно только в зелёной зоне (вокруг Ратуши и аванпостов).\nЛКМ — поставить, Shift — поставить несколько подряд, ПКМ / Esc — убрать из рук." +
            (scrolling ? "\nКолёсико — прокрутка списка." : "") + "\nЦифры 1–9 — быстро взять постройку в руки.");
    }

    void DrawCard(int i, Rect r, bool clickable = true)
    {
        BuildingType bt = buildingTypes[i];
        int count = CountOfType(bt.name);
        bool resOk = HasResources(bt);
        string extra = UpgradeRules.ShortBuildNeeds(bt.name, count, out bool extraOk);
        bool limit = AtLimit(bt);
        bool hallOk = HallLevelOk(bt);
        bool can = resOk && extraOk && !limit && hallOk;

        bool clicked = GUI.Button(r, GUIContent.none, UiKit.ButtonStyle(can ? UiKit.Kind.Normal : UiKit.Kind.Ghost));
        if (clicked && clickable)
        {
            if (can) { TakeInHand(i); return; }
            if (limit) { WarManager.Notify($"{bt.name}: можно построить только один", false); return; }
            if (!hallOk) { WarManager.Notify($"{bt.name}: нужна Ратуша Ур.{HallLevelNeeded(bt.name)}", false); return; }

            string miss = MissingLine(bt);
            if (!extraOk) miss += (miss != "" ? " и " : "") + "материалов из мастерской";
            WarManager.Notify($"{bt.name}: не хватает {miss}", false);
            return;
        }

        // Цветной значок здания
        Color oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.8f);
        GUI.DrawTexture(new Rect(r.x + 9f, r.y + 10f, 16f, 16f), Texture2D.whiteTexture);
        GUI.color = can ? bt.color : bt.color * 0.6f;
        GUI.DrawTexture(new Rect(r.x + 10f, r.y + 11f, 14f, 14f), Texture2D.whiteTexture);
        GUI.color = oldColor;

        GUIStyle head = new GUIStyle(UiKit.Body) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
        head.normal.textColor = can ? UiKit.Text : UiKit.Muted;
        GUIStyle line = new GUIStyle(UiKit.Small) { wordWrap = false, clipping = TextClipping.Clip };
        GUIStyle desc = new GUIStyle(UiKit.MutedStyle) { wordWrap = false, clipping = TextClipping.Clip };
        GUIStyle key = new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.UpperRight, wordWrap = false };

        GUI.Label(new Rect(r.x + 30f, r.y + 8f, r.width - 64f, 22f), bt.name, head);
        if (i < 9) GUI.Label(new Rect(r.xMax - 34f, r.y + 9f, 26f, 20f), $"[{i + 1}]", key);

        GUI.Label(new Rect(r.x + 10f, r.y + 32f, r.width - 20f, 18f), Describe(bt), desc);
        GUI.Label(new Rect(r.x + 10f, r.y + 52f, r.width - 20f, 18f), CostLine(bt), line);
        if (extra != "") GUI.Label(new Rect(r.x + 10f, r.y + 70f, r.width - 20f, 18f), extra, line);

        // Статус-чип
        string chip; Color chipCol;
        if (can) { chip = "Можно строить"; chipCol = UiKit.Good; }
        else if (limit) { chip = "Уже построен"; chipCol = UiKit.Warn; }
        else if (!hallOk) { chip = $"Ратуша Ур.{HallLevelNeeded(bt.name)}"; chipCol = UiKit.Warn; }
        else { chip = "Не хватает"; chipCol = UiKit.Bad; }
        DrawChip(new Rect(r.x + 10f, r.yMax - 24f, 0f, 18f), chip, chipCol);

        if (count > 0)
            GUI.Label(new Rect(r.xMax - 110f, r.yMax - 24f, 100f, 18f), $"построено: {count}", new GUIStyle(desc) { alignment = TextAnchor.MiddleRight });

        if (clickable) UiKit.Tip(r, CardTip(bt, count, can, limit, hallOk, extraOk));
    }

    // Подробности карточки - в подсказке
    string CardTip(BuildingType bt, int count, bool can, bool limit, bool hallOk, bool extraOk)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<b>{bt.name}</b>  {UiKit.Col(UiKit.MutedHex, bt.size.x + "×" + bt.size.y + " кл.")}\n");
        sb.Append(Describe(bt));
        sb.Append("\nЦена: " + CostLine(bt));
        string mats = UpgradeRules.DescribeBuild(bt.name, count);
        if (mats != "") sb.Append("\n" + mats);
        if (!hallOk) sb.Append("\n" + UiKit.Col(UiKit.WarnHex, $"Нужна Ратуша Ур.{HallLevelNeeded(bt.name)}"));
        if (limit) sb.Append("\n" + UiKit.Col(UiKit.WarnHex, "Можно построить только один"));
        string miss = MissingLine(bt);
        if (miss != "") sb.Append("\n" + UiKit.Col(UiKit.BadHex, "Не хватает: " + miss));
        if (can) sb.Append("\n" + UiKit.Col(UiKit.MutedHex, IsPaintType(bt) ? "Клик — взять в руки, ЛКМ можно зажать" : "Клик — взять в руки"));
        return sb.ToString();
    }

    static void DrawChip(Rect r, string text, Color col)
    {
        GUIStyle st = new GUIStyle(UiKit.Small) { alignment = TextAnchor.MiddleCenter, wordWrap = false, fontStyle = FontStyle.Bold };
        st.normal.textColor = col;
        float w = st.CalcSize(new GUIContent(text)).x + 14f;
        Rect cr = new Rect(r.x, r.y, w, r.height);
        Color old = GUI.color;
        GUI.color = new Color(col.r, col.g, col.b, 0.16f);
        GUI.DrawTexture(cr, Texture2D.whiteTexture);
        GUI.color = old;
        GUI.Label(cr, text, st);
    }

    string DoorHint()
    {
        if (placeDoorProblem == 1) return "\n" + UiKit.Col(UiKit.WarnHex, "Нужна свободная сторона для двери");
        if (placeDoorProblem == 2) return "\n" + UiKit.Col(UiKit.WarnHex, "Закроет выход соседу");
        return "";
    }

    static string ZoneHint(BuildingType t)
    {
        if (BuildingInstance.IsRoadName(t.name)) return "Дорогу ведут от зелёной зоны или от подключённой дороги";
        if (t.name == OutpostController.TypeName) return "Аванпост — у границы зелёной зоны или у дороги из неё";
        return "Вне зелёной зоны строить нельзя";
    }

    void DrawCursorTip()
    {
        BuildingType t = Current;
        if (t == null) return;

        string reason;
        if (BuildBlocked) reason = UiKit.Col(UiKit.BadHex, "Идёт война — строить нельзя");
        else if (!HallLevelOk(t)) reason = UiKit.Col(UiKit.BadHex, $"Нужна Ратуша Ур.{HallLevelNeeded(t.name)}");
        else if (placeWaterProblem != "") reason = UiKit.Col(UiKit.WarnHex, placeWaterProblem);
        else if (!placeZoneOk) reason = UiKit.Col(UiKit.WarnHex, ZoneHint(t));
        else if (!placeSpaceOk) reason = UiKit.Col(UiKit.BadHex, "Здесь занято");
        else if (placeDoorProblem == 1) reason = UiKit.Col(UiKit.BadHex, "Нужна свободная сторона для двери");
        else if (placeDoorProblem == 2) reason = UiKit.Col(UiKit.BadHex, "Закроет выход соседнему зданию");
        else if (AtLimit(t)) reason = UiKit.Col(UiKit.BadHex, "Можно построить только один");
        else if (!HasResources(t)) reason = UiKit.Col(UiKit.BadHex, "Не хватает: " + MissingLine(t));
        else if (!UpgradeRules.CanPayBuild(t.name, CountOfType(t.name)))
            reason = UpgradeRules.ShortBuildNeeds(t.name, CountOfType(t.name), out _);
        else reason = UiKit.Col(UiKit.GoodHex, IsPaintType(t) ? "ЛКМ — ставить (можно зажать)" : "ЛКМ — поставить   Shift — ещё");

        if (t.name == KitchenController.TypeName && placeZoneOk && placeSpaceOk)
            reason += "\n" + UiKit.Col(UiKit.InfoHex, "Вплотную к казарме — похлёбка без курьеров");

        string text = $"<b>{t.name}</b>\n{reason}\n{UiKit.Col(UiKit.MutedHex, "ПКМ / Esc — убрать из рук")}";

        Vector2 m = Mouse.current.position.ReadValue();
        float w = 290f;
        float h = UiKit.TextH(text, w - 16f) + 10f;
        float x = Mathf.Min(m.x + 20f, Screen.width - w - 4f);
        float y = Mathf.Min(Screen.height - m.y + 20f, Screen.height - h - 4f);
        Rect r = new Rect(x, y, w, h);

        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 8f, r.y + 5f, r.width - 16f, r.height - 8f), text);
    }

    Rect durabilityRect;   // плашка прочности под панелью здания (клики по ней не уходят на карту)

    void DrawDurability(BuildingInstance b)
    {
        if (b == null || !b.IsDamageable) { durabilityRect = new Rect(0f, 0f, 0f, 0f); return; }

        // Военный строитель: переключатель «Чинить в войну» прямо под прочностью
        bool markRow = WarBuilderYard.Any && WarBuilderYard.CanMark(b);
        Rect p = infoPanelRect;
        Rect r = new Rect(p.x, p.yMax + 4f, p.width - 5f, markRow ? 74f : 46f);
        durabilityRect = r;
        GUI.Box(r, GUIContent.none);

        if (markRow)
        {
            bool on = WarBuilderYard.IsMarked(b);
            Rect tr = new Rect(r.x + 10f, r.y + 46f, r.width - 20f, 22f);
            string cap = $" Чинить в войну  ({WarBuilderYard.MarkedCount}/{WarBuilderYard.MaxMarks})";
            bool want = GUI.Toggle(tr, on, cap);
            if (want != on) WarBuilderYard.SetMarked(b, want);
            UiKit.Tip(tr, "Строители Двора военного строителя в войну бегут чинить отмеченные здания в первую очередь — прямо под ударами.\n" +
                          "Над отмеченным зданием висит оранжевый молоток.");
        }

        string text;
        float frac;
        Color col;
        if (b.IsRuined)
        {
            frac = b.RebuildPaid ? b.RebuildProgress : 0f;
            col = UiKit.Warn;
            text = UiKit.Col(UiKit.WarnHex, UiKit.B("Руины")) + " — " + b.RebuildStatus;
        }
        else
        {
            frac = b.Hp / b.MaxHp;
            col = Color.Lerp(UiKit.Bad, UiKit.Good, frac);
            string state = b.UnderAttack
                ? "  " + UiKit.Col(UiKit.BadHex, "под ударом")
                : (frac < 0.999f ? "  " + UiKit.Col(UiKit.MutedHex, "чинится") : "");
            text = $"Прочность{state}";
        }

        GUIStyle st = new GUIStyle(UiKit.Body) { wordWrap = false, clipping = TextClipping.Clip };
        GUI.Label(new Rect(r.x + 10f, r.y + 4f, r.width - 20f, 20f), text, st);
        string barText = b.IsRuined ? (b.RebuildPaid ? $"{frac * 100f:F0}%" : null) : $"{Mathf.CeilToInt(b.Hp)}/{Mathf.CeilToInt(b.MaxHp)}";
        UiKit.Bar(new Rect(r.x + 10f, r.y + 26f, r.width - 20f, 14f), frac, col, barText);
    }
}
