using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class CraftRecipe
{
    public string recipeName = "Обработка дерева";
    public RefinedKind kind = RefinedKind.Planks;  // что получается: доски, плиты или сталь
    public int minLevel = 1;                         // с какого уровня мастерской доступен рецепт

    [Header("Стоимость сырья")]
    public int costWood = 15;
    public int costStone = 0;
    public int costIron = 0;     // железная руда

    [Header("Материалы со склада (любое качество)")]
    public int costPlates = 0;   // каменные плиты
    public int costSteel = 0;    // сталь
    public int costPlanks = 0;       // доски
    public int costReinforced = 0;   // укреплённые плиты
    public int costBeams = 0;        // смоляной брус

    [Header("Смола и пропитка (из казны)")]
    public int costResin = 0;
    public int costTar = 0;
    public bool outputsTar = false;  // результат - смоляная пропитка (без качества, в Ратушу)

    // "20д", "20к", "15 руды"
    public string CostText
    {
        get
        {
            var parts = new List<string>();
            if (costWood > 0) parts.Add(costWood + "д");
            if (costStone > 0) parts.Add(costStone + "к");
            if (costIron > 0) parts.Add(costIron + " руды");
            if (costPlates > 0) parts.Add(costPlates + " плит");
            if (costSteel > 0) parts.Add(costSteel + " стали");
            if (costPlanks > 0) parts.Add(costPlanks + " дос.");
            if (costReinforced > 0) parts.Add(costReinforced + " укр.");
            if (costBeams > 0) parts.Add(costBeams + " брус");
            if (costResin > 0) parts.Add(costResin + " смолы");
            if (costTar > 0) parts.Add(costTar + " проп.");
            return parts.Count > 0 ? string.Join(" ", parts) : "даром";
        }
    }

    [Header("Результат")]
    public int outputAmount = 5;
    public float craftTime = 4f;
}

/// <summary>
/// Мастерская. Теперь всё делается руками жителей:
///  1. Игрок заказывает рецепт - сырьё списывается из казны, рабочий мастерской идёт в Ратушу,
///     берёт сырьё и несёт в мастерскую.
///  2. Когда сырьё доставлено, идёт крафт.
///  3. Готовые доски/плиты рабочий несёт обратно в Ратушу, и только там они попадают на склад.
/// Пока рабочего нет (все заняты), заказ ждёт в очереди (см. Logistics).
/// С Ур.2 мастерская делает сталь из железной руды (руду добывает шахта Ур.3).
/// С Ур.3 - укреплённые плиты: каменная плита + сталь + немного камня (для башен Ур.3, аванпостов Ур.3, казармы Ур.4).
/// </summary>
public class WorkshopController : MonoBehaviour
{
    enum Stage { Idle, FetchingRaw, Crafting }

    private BuildingInstance buildingInstance;

    public List<CraftRecipe> availableRecipes = new List<CraftRecipe>
    {
        new CraftRecipe { recipeName = "Сделать доски", kind = RefinedKind.Planks, costWood = 20, outputAmount = 5, craftTime = 4f },
        new CraftRecipe { recipeName = "Сделать плиты", kind = RefinedKind.StonePlates, costStone = 20, outputAmount = 5, craftTime = 4f },
        new CraftRecipe { recipeName = "Выплавить сталь", kind = RefinedKind.Steel, costIron = 15, outputAmount = 3, craftTime = 8f, minLevel = 2 },
        ReinforcedRecipe()
    };

    // Укреплённые плиты: 2 плиты + 1 сталь + 10 камня -> 2 укреплённые плиты (мастерская Ур.3)
    static CraftRecipe ReinforcedRecipe()
    {
        return new CraftRecipe { recipeName = "Укрепить плиты", kind = RefinedKind.ReinforcedPlates, costWood = 0, costStone = 10,
                                 costPlates = 2, costSteel = 1, outputAmount = 2, craftTime = 10f, minLevel = 3 };
    }

    // Смоляная пропитка (Ур.2): смола + дерево. Из неё - смоляной брус и монолитное основание (Ур.3),
    // которыми укрепляют здания (+50% прочности, см. UpgradeRules - «Укрепить»)
    static CraftRecipe TarRecipe()
    {
        return new CraftRecipe { recipeName = "Сварить пропитку", kind = RefinedKind.Planks, outputsTar = true,
                                 costWood = 10, costResin = 6, outputAmount = 4, craftTime = 8f, minLevel = 2 };
    }

    static CraftRecipe BeamsRecipe()
    {
        return new CraftRecipe { recipeName = "Смоляной брус", kind = RefinedKind.TarredBeams, costWood = 0,
                                 costPlanks = 4, costTar = 2, outputAmount = 3, craftTime = 9f, minLevel = 3 };
    }

    static CraftRecipe MonolithRecipe()
    {
        return new CraftRecipe { recipeName = "Монолит", kind = RefinedKind.Monolith, costWood = 0, costStone = 10,
                                 costReinforced = 2, costBeams = 1, costTar = 3, outputAmount = 1, craftTime = 14f, minLevel = 3 };
    }

    // Список мог сохраниться в сцене старым - новые рецепты добавляем сами
    void Awake()
    {
        if (availableRecipes == null) availableRecipes = new List<CraftRecipe>();
        if (!availableRecipes.Exists(r => r != null && r.kind == RefinedKind.ReinforcedPlates))
            availableRecipes.Add(ReinforcedRecipe());
        if (!availableRecipes.Exists(r => r != null && r.outputsTar)) availableRecipes.Add(TarRecipe());
        if (!availableRecipes.Exists(r => r != null && !r.outputsTar && r.kind == RefinedKind.TarredBeams)) availableRecipes.Add(BeamsRecipe());
        if (!availableRecipes.Exists(r => r != null && !r.outputsTar && r.kind == RefinedKind.Monolith)) availableRecipes.Add(MonolithRecipe());
    }

    private Stage stage = Stage.Idle;
    private CraftRecipe currentRecipe = null;
    private float craftTimer = 0f;
    private int outputsInTransit = 0;   // сколько партий готового сейчас несут в Ратушу

    public bool IsCrafting => stage == Stage.Crafting;
    public bool IsBusy => stage != Stage.Idle;
    public string CurrentRecipeName => currentRecipe != null ? currentRecipe.recipeName : "";
    public float CraftProgress => stage == Stage.Crafting && currentRecipe != null && currentRecipe.craftTime > 0
        ? (1f - (craftTimer / currentRecipe.craftTime)) : 0f;

    // Короткая строка состояния для панели
    public string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.FetchingRaw:
                    return "Несут сырьё из Ратуши...";
                case Stage.Crafting:
                    return $"Крафт: <b>{CurrentRecipeName}</b> ({CraftProgress * 100f:F0}%)";
                default:
                    return outputsInTransit > 0 ? $"Свободна. Готовое несут: {outputsInTransit}" : "Мастерская свободна";
            }
        }
    }

    void Start()
    {
        buildingInstance = GetComponent<BuildingInstance>();
    }

    void Update()
    {
        if (stage != Stage.Crafting) return;
        if (buildingInstance != null && !buildingInstance.isProducing) return;

        // При дефиците мастерская работает медленнее, но не встаёт
        // Голодные и раненые мастера работают медленнее (HealthFactor)
        craftTimer -= Time.deltaTime * (buildingInstance != null ? buildingInstance.SpeedMultiplier * buildingInstance.OffHoursFactor * buildingInstance.HealthFactor : 1f);
        if (craftTimer > 0f) return;

        FinishCraft();
    }

    // ---------- Заказ ----------

    // Рецепт доступен на этом уровне мастерской
    public bool Unlocked(CraftRecipe recipe)
    {
        if (buildingInstance == null) buildingInstance = GetComponent<BuildingInstance>();
        int lvl = buildingInstance != null ? buildingInstance.level : 1;
        return recipe != null && lvl >= recipe.minLevel;
    }

    public bool CanAfford(CraftRecipe recipe)
    {
        var rm = ResourceManager.Instance;
        if (rm == null || !rm.HasEnough(0, recipe.costWood, recipe.costStone, 0) || rm.iron < recipe.costIron) return false;
        if (rm.resin < recipe.costResin || rm.tar < recipe.costTar) return false;
        if (recipe.costPlates <= 0 && recipe.costSteel <= 0 && recipe.costPlanks <= 0 && recipe.costReinforced <= 0 && recipe.costBeams <= 0) return true;
        var st = TownHallStorage.Instance;
        if (st == null) return false;
        return UpgradeRules.CountAtLeast(st, RefinedKind.StonePlates, QualityTier.Medium) >= recipe.costPlates
            && UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) >= recipe.costSteel
            && UpgradeRules.CountAtLeast(st, RefinedKind.Planks, QualityTier.Medium) >= recipe.costPlanks
            && UpgradeRules.CountAtLeast(st, RefinedKind.ReinforcedPlates, QualityTier.Medium) >= recipe.costReinforced
            && UpgradeRules.CountAtLeast(st, RefinedKind.TarredBeams, QualityTier.Medium) >= recipe.costBeams;
    }

    // Что списали со склада под текущий заказ (вернём, если рейс за сырьём сорвётся)
    readonly List<UpgradeRules.Spent> spentRefined = new List<UpgradeRules.Spent>();

    public void StartCraft(CraftRecipe recipe)
    {
        if (buildingInstance == null) buildingInstance = GetComponent<BuildingInstance>();
        if (buildingInstance != null && !buildingInstance.isProducing) return;
        if (IsBusy || !Unlocked(recipe)) return;

        var rm = ResourceManager.Instance;
        if (rm == null || !CanAfford(recipe) || !rm.Spend(0, recipe.costWood, recipe.costStone, 0))
        {
            Debug.Log("Недостаточно сырья для мастерской!");
            return;
        }
        rm.SpendIron(recipe.costIron);
        rm.SpendResin(recipe.costResin);
        rm.SpendTar(recipe.costTar);

        // Материалы со склада: сначала самые простые по качеству
        spentRefined.Clear();
        var st = TownHallStorage.Instance;
        if (st != null)
        {
            if (recipe.costPlates > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.StonePlates, QualityTier.Medium, recipe.costPlates, spentRefined);
            if (recipe.costSteel > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, recipe.costSteel, spentRefined);
            if (recipe.costPlanks > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, recipe.costPlanks, spentRefined);
            if (recipe.costReinforced > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.ReinforcedPlates, QualityTier.Medium, recipe.costReinforced, spentRefined);
            if (recipe.costBeams > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.TarredBeams, QualityTier.Medium, recipe.costBeams, spentRefined);
        }

        currentRecipe = recipe;
        stage = Stage.FetchingRaw;

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Рабочий мастерской идёт в Ратушу за сырьём и несёт его сюда
        Logistics.Request(new HaulJob
        {
            from = hall,
            to = buildingInstance,
            prefer = buildingInstance,
            onDeliver = OnRawDelivered,
            onAbort = loaded => OnRawAborted(recipe)
        });
    }

    void OnRawDelivered()
    {
        if (currentRecipe == null) return;
        craftTimer = currentRecipe.craftTime;
        stage = Stage.Crafting;
    }

    // Рейс сорван (мастерскую снесли, жителя забрали в казарму и т.п.) - сырьё возвращаем в казну
    void OnRawAborted(CraftRecipe recipe)
    {
        if (ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(0, recipe.costWood, recipe.costStone, 0, recipe.costIron);
            ResourceManager.Instance.AddResin(recipe.costResin);
            ResourceManager.Instance.AddTar(recipe.costTar);
        }
        UpgradeRules.RefundSpent(spentRefined);
        spentRefined.Clear();
        currentRecipe = null;
        stage = Stage.Idle;
    }

    // ---------- Готовая продукция ----------

    void FinishCraft()
    {
        CraftRecipe recipe = currentRecipe;
        QualityTier quality = RollQuality();
        RefinedKind kind = recipe.kind;
        int amount = recipe.outputAmount;

        // Пропитка - жидкость без качества: несут в Ратушу
        if (recipe.outputsTar)
        {
            currentRecipe = null;
            stage = Stage.Idle;
            outputsInTransit++;
            var pmT = PopulationManager.Instance;
            Logistics.Request(new HaulJob
            {
                from = buildingInstance,
                to = pmT != null ? pmT.Hall : null,
                prefer = buildingInstance,
                onDeliver = () => DeliverTar(amount),
                onAbort = loaded => DeliverTar(amount)
            });
            return;
        }

        // Мастерская сразу свободна для нового заказа, а готовое несут в Ратушу
        currentRecipe = null;
        stage = Stage.Idle;
        outputsInTransit++;

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Есть Хранилище, которое принимает продукцию мастерских, - несём туда, иначе в Ратушу
        Vector2 here = buildingInstance != null ? buildingInstance.Center : Vector2.zero;
        WarehouseController wh = WarehouseController.PickForOutput(here, amount);
        BuildingInstance dest = wh != null ? wh.Building : hall;
        if (wh != null) wh.Reserve(amount);

        Logistics.Request(new HaulJob
        {
            from = buildingInstance,
            to = dest,
            prefer = buildingInstance,
            onDeliver = () => DeliverOutput(kind, quality, amount, wh),
            onAbort = loaded => DeliverOutput(kind, quality, amount, wh) // продукцию не теряем
        });
    }

    void DeliverTar(int amount)
    {
        outputsInTransit = Mathf.Max(0, outputsInTransit - 1);
        if (ResourceManager.Instance != null) ResourceManager.Instance.AddTar(amount);
    }

    void DeliverOutput(RefinedKind kind, QualityTier quality, int amount, WarehouseController wh = null)
    {
        outputsInTransit = Mathf.Max(0, outputsInTransit - 1);

        // Несли в Хранилище. Если его успели разрушить - продукция уходит в Ратушу.
        if (wh != null)
        {
            wh.Unreserve(amount);
            if (wh.Accept(kind, quality, amount))
            {
                Debug.Log($"[Мастерская] Доставлено в Хранилище! Качество: {TownHallStorage.TierName(quality)}");
                return;
            }
        }

        var storage = TownHallStorage.Instance;
        if (storage == null) return;

        storage.Add(kind, quality, amount);

        Debug.Log($"[Мастерская] Доставлено в Ратушу! Качество: {storage.GetQualityName(quality)}");
    }

    // Система шансов в зависимости от уровня здания (1, 2 или 3)
    QualityTier RollQuality()
    {
        int lvl = buildingInstance != null ? buildingInstance.level : 1;
        float roll = Random.Range(0f, 100f);

        if (lvl == 1)
        {
            // Ур. 1: 85% Среднее, 15% Хорошее, 0% Наилучшее
            if (roll < 85f) return QualityTier.Medium;
            else return QualityTier.Good;
        }
        else if (lvl == 2)
        {
            // Ур. 2: 50% Среднее, 40% Хорошее, 10% Наилучшее
            if (roll < 50f) return QualityTier.Medium;
            else if (roll < 90f) return QualityTier.Good;
            else return QualityTier.Best;
        }
        else
        {
            // Ур. 3: 20% Среднее, 50% Хорошее, 30% Наилучшее
            if (roll < 20f) return QualityTier.Medium;
            else if (roll < 70f) return QualityTier.Good;
            else return QualityTier.Best;
        }
    }
}