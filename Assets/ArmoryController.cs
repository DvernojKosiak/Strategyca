using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class WeaponRecipe
{
    public string recipeName = "Сковать меч";
    public WeaponKind kind = WeaponKind.Sword;
    public bool isShield = false;  // true - получается щит (kind игнорируется)
    public bool isBody = false;    // true - броня или плащ (bodyKind), см. CitizenAgent_Gear
    public BodyKind bodyKind = BodyKind.Armor;
    public WarSchool school = WarSchool.None;   // рецепт только для этой ветки Военного дела
    public int steel = 4;          // сталь (любого качества)
    public int planks = 0;         // доски (любого качества)
    public int leaves = 0;         // листва из казны (Лесопилка Ур.4)
    public int gold = 0;           // золото из казны
    public float craftTime = 18f;

    public string CostText
    {
        get
        {
            var parts = new List<string>();
            if (steel > 0) parts.Add(steel + " стали");
            if (planks > 0) parts.Add(planks + " досок");
            if (leaves > 0) parts.Add(leaves + " листвы");
            if (gold > 0) parts.Add(gold + " зол");
            return parts.Count > 0 ? string.Join(" + ", parts) : "даром";
        }
    }
}

/// <summary>
/// Оружейная. Рабочие здания - оружейники.
///  1. Игрок заказывает рецепт: сталь (и доски) списываются со склада Ратуши / Хранилищ,
///     рабочий несёт их в Оружейную (как в мастерской, см. Logistics).
///  2. Идёт ковка. Качество выпадает случайно, чем выше уровень Оружейной - тем лучше.
///  3. Готовые мечи, луки и щиты лежат на общем складе Оружейной (вместимость 12 / 24 / 40).
///  4. Предметы можно улучшать за золото (Усиленный, Позолоченный, Золотой;
///     для 2-го и 3-го улучшения нужна Оружейная Ур.2 / Ур.3) и чинить за золото.
///  5. Из казармы предметы выдают бойцам. Боец погиб - предметы погибли.
///     Бойца уволили - предметы возвращаются сюда.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class ArmoryController : MonoBehaviour
{
    public const string TypeName = "Оружейная";

    public static readonly List<ArmoryController> All = new List<ArmoryController>();

    static readonly int[] CapacityByLevel = { 12, 24, 40 };

    public List<WeaponRecipe> recipes = new List<WeaponRecipe>
    {
        new WeaponRecipe { recipeName = "Сковать меч", kind = WeaponKind.Sword, steel = 4, planks = 0, craftTime = 18f },
        new WeaponRecipe { recipeName = "Сделать лук", kind = WeaponKind.Bow, steel = 2, planks = 4, craftTime = 14f },
        new WeaponRecipe { recipeName = "Сделать щит", isShield = true, steel = 3, planks = 6, craftTime = 16f },
    };

    // Рецепты веток Военного дела (не сериализуются: ветку могут сменить на ходу - проверяем каждый кадр)
    static readonly WeaponRecipe[] BranchRecipes =
    {
        new WeaponRecipe { recipeName = "Сделать броню", isBody = true, bodyKind = BodyKind.Armor, school = WarSchool.Heavy, steel = 6, planks = 3, craftTime = 24f },
        new WeaponRecipe { recipeName = "Сшить плащ", isBody = true, bodyKind = BodyKind.Cloak, school = WarSchool.Camo, steel = 0, planks = 0, leaves = 12, gold = 15, craftTime = 14f },
    };

    /// <summary>Что можно заказать сейчас: оружие и щиты + броня (Тяжёлая стратегия) или плащ (Мастер маскировки).</summary>
    public List<WeaponRecipe> VisibleRecipes()
    {
        var res = new List<WeaponRecipe>(recipes);
        foreach (var r in BranchRecipes)
            if (r.school != WarSchool.None && DevelopmentPath.WarAllows(r.school)) res.Add(r);
        return res;
    }

    enum Stage { Idle, Fetching, Crafting }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public int Capacity => CapacityByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    readonly List<Weapon> stock = new List<Weapon>();
    readonly List<Shield> shields = new List<Shield>();
    readonly List<BodyGear> bodies = new List<BodyGear>();   // броня и плащи
    public IReadOnlyList<Weapon> Stock => stock;
    public IReadOnlyList<Shield> Shields => shields;
    public IReadOnlyList<BodyGear> Bodies => bodies;
    public int Count => stock.Count;
    public int ItemCount => stock.Count + shields.Count + bodies.Count;
    public int CraftedTotal { get; private set; }

    Stage stage = Stage.Idle;
    WeaponRecipe current;
    float craftTimer;
    List<UpgradeRules.Spent> spentLog;
    int spentLeaves, spentGold;   // листва и золото заказа (вернуть, если рейс сорван)

    public bool IsBusy => stage != Stage.Idle;
    public float CraftProgress => stage == Stage.Crafting && current != null && current.craftTime > 0f
        ? 1f - craftTimer / current.craftTime : 0f;

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return "Несут материалы из Ратуши...";
                case Stage.Crafting: return $"Ковка: <b>{(current != null ? current.recipeName : "")}</b> ({CraftProgress * 100f:F0}%)";
                default: return ItemCount >= Capacity ? "<color=#FFB060>Склад полон</color>" : "Оружейная свободна";
            }
        }
    }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (stage != Stage.Crafting) return;
        if (Building != null && !Building.isProducing) return;

        // Голодные мастера и дефицит замедляют ковку
        craftTimer -= Time.deltaTime * (Building != null ? Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor : 1f);
        if (craftTimer > 0f) return;
        FinishCraft();
    }

    // Здание снесли - предметы переезжают в другие Оружейные, что не влезло - пропадает
    void OnDestroy()
    {
        int lost = 0;
        var items = new List<Weapon>(stock);
        var shs = new List<Shield>(shields);
        var bds = new List<BodyGear>(bodies);
        stock.Clear();
        shields.Clear();
        bodies.Clear();
        All.Remove(this);
        foreach (var w in items)
            if (!Stash(w)) lost++;
        foreach (var s in shs)
            if (!StashShield(s)) lost++;
        foreach (var g in bds)
            if (!StashBody(g)) lost++;
        if (lost > 0) WarManager.Notify($"Оружейную снесли: потеряно предметов — {lost}", false);
    }

    // ---------- Ковка ----------

    public bool CanAfford(WeaponRecipe r)
    {
        var st = TownHallStorage.Instance;
        if (st == null || r == null) return false;
        if (UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) < r.steel) return false;
        if (r.planks > 0 && UpgradeRules.CountAtLeast(st, RefinedKind.Planks, QualityTier.Medium) < r.planks) return false;
        var rm = ResourceManager.Instance;
        if ((r.leaves > 0 || r.gold > 0) && rm == null) return false;
        if (r.leaves > 0 && rm.leaves < r.leaves) return false;
        if (r.gold > 0 && rm.gold < r.gold) return false;
        return true;
    }

    public bool CanStart(WeaponRecipe r)
    {
        return Works && Building.isProducing && !IsBusy && ItemCount < Capacity && CanAfford(r);
    }

    public void StartCraft(WeaponRecipe r)
    {
        if (!CanStart(r)) return;
        var st = TownHallStorage.Instance;

        // Материалы списываются сразу: сначала самого низкого качества, склад Ратуши, потом Хранилища
        var spent = new List<UpgradeRules.Spent>();
        if (r.steel > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, r.steel, spent);
        if (r.planks > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, r.planks, spent);
        var rm = ResourceManager.Instance;
        spentLeaves = r.leaves > 0 && rm != null && rm.SpendLeaves(r.leaves) ? r.leaves : 0;
        spentGold = r.gold > 0 && rm != null && rm.Spend(r.gold, 0, 0, 0) ? r.gold : 0;

        current = r;
        spentLog = spent;
        stage = Stage.Fetching;

        var pm = PopulationManager.Instance;
        BuildingInstance pickup = pm != null ? pm.Hall : null;
        foreach (var sp in spent)
            if (sp.source != null) { pickup = sp.source; break; }   // брали в Хранилище - житель идёт туда

        Logistics.Request(new HaulJob
        {
            from = pickup,
            to = Building,
            prefer = Building,
            onDeliver = OnMaterialsDelivered,
            onAbort = loaded => OnMaterialsAborted()
        });
    }

    void OnMaterialsDelivered()
    {
        if (current == null) return;
        craftTimer = current.craftTime;
        stage = Stage.Crafting;
    }

    // Рейс сорван - материалы возвращаются на склад
    void OnMaterialsAborted()
    {
        UpgradeRules.RefundSpent(spentLog);
        spentLog = null;
        var rm = ResourceManager.Instance;
        if (rm != null)
        {
            rm.AddLeaves(spentLeaves);
            if (spentGold > 0) rm.Add(spentGold, 0, 0, 0);
        }
        spentLeaves = spentGold = 0;
        current = null;
        stage = Stage.Idle;
    }

    void FinishCraft()
    {
        WeaponRecipe r = current;
        current = null;
        spentLog = null;
        spentLeaves = spentGold = 0;
        stage = Stage.Idle;
        if (r == null) return;

        if (r.isBody)
        {
            BodyGear g = new BodyGear(r.bodyKind, RollQuality());
            bodies.Add(g);
            CraftedTotal++;
            WarManager.Notify($"Оружейная: готово — {g.Name}", true);
            return;
        }

        if (r.isShield)
        {
            Shield sh = new Shield(RollQuality());
            shields.Add(sh);
            CraftedTotal++;
            WarManager.Notify($"Оружейная: готово — {sh.Name}", true);
            return;
        }

        Weapon w = new Weapon(r.kind, RollQuality());
        stock.Add(w);
        CraftedTotal++;
        WarManager.Notify($"Оружейная: готово — {w.Name}", true);
    }

    // Шансы качества по уровню здания (как в мастерской)
    QualityTier RollQuality()
    {
        int lvl = Building != null ? Building.level : 1;
        float roll = Random.Range(0f, 100f);
        if (lvl <= 1) return roll < 85f ? QualityTier.Medium : QualityTier.Good;
        if (lvl == 2) return roll < 50f ? QualityTier.Medium : (roll < 90f ? QualityTier.Good : QualityTier.Best);
        return roll < 20f ? QualityTier.Medium : (roll < 70f ? QualityTier.Good : QualityTier.Best);
    }

    // ---------- Улучшение и починка за золото: оружие ----------

    public bool CanUpgradeWeapon(Weapon w)
    {
        if (!Works || w == null || !w.CanUpgrade) return false;
        if (Building.level < w.upgrade + 1) return false;   // 2-е улучшение - Ур.2, 3-е - Ур.3
        var rm = ResourceManager.Instance;
        return rm != null && rm.gold >= Weapon.UpgradeCost(w.upgrade + 1);
    }

    public bool TryUpgradeWeapon(Weapon w)
    {
        if (!CanUpgradeWeapon(w)) return false;
        int cost = Weapon.UpgradeCost(w.upgrade + 1);
        if (!ResourceManager.Instance.Spend(cost, 0, 0, 0)) return false;
        w.ApplyUpgrade();
        WarManager.Notify($"Оружейная: оружие улучшено — {w.Name}", true);
        return true;
    }

    public bool CanRepairWeapon(Weapon w)
    {
        if (!Works || w == null) return false;
        int cost = w.RepairCost;
        var rm = ResourceManager.Instance;
        return cost > 0 && rm != null && rm.gold >= cost;
    }

    public bool TryRepairWeapon(Weapon w)
    {
        if (!CanRepairWeapon(w)) return false;
        if (!ResourceManager.Instance.Spend(w.RepairCost, 0, 0, 0)) return false;
        w.Repair();
        return true;
    }

    // ---------- Улучшение и починка за золото: щиты ----------

    public bool CanUpgradeShield(Shield s)
    {
        if (!Works || s == null || !s.CanUpgrade) return false;
        if (Building.level < s.upgrade + 1) return false;
        var rm = ResourceManager.Instance;
        return rm != null && rm.gold >= Shield.UpgradeCost(s.upgrade + 1);
    }

    public bool TryUpgradeShield(Shield s)
    {
        if (!CanUpgradeShield(s)) return false;
        int cost = Shield.UpgradeCost(s.upgrade + 1);
        if (!ResourceManager.Instance.Spend(cost, 0, 0, 0)) return false;
        s.ApplyUpgrade();
        WarManager.Notify($"Оружейная: щит улучшен — {s.Name}", true);
        return true;
    }

    public bool CanRepairShield(Shield s)
    {
        if (!Works || s == null) return false;
        int cost = s.RepairCost;
        var rm = ResourceManager.Instance;
        return cost > 0 && rm != null && rm.gold >= cost;
    }

    public bool TryRepairShield(Shield s)
    {
        if (!CanRepairShield(s)) return false;
        if (!ResourceManager.Instance.Spend(s.RepairCost, 0, 0, 0)) return false;
        s.Repair();
        return true;
    }

    // ---------- Починка за золото: броня и плащи ----------

    public bool CanRepairBody(BodyGear g)
    {
        if (!Works || g == null) return false;
        int cost = g.RepairCost;
        var rm = ResourceManager.Instance;
        return cost > 0 && rm != null && rm.gold >= cost;
    }

    public bool TryRepairBody(BodyGear g)
    {
        if (!CanRepairBody(g)) return false;
        if (!ResourceManager.Instance.Spend(g.RepairCost, 0, 0, 0)) return false;
        g.Repair();
        return true;
    }

    // ---------- Склад (общие функции для казармы и бойцов) ----------

    public struct StockEntry { public ArmoryController armory; public Weapon weapon; }
    public struct ShieldEntry { public ArmoryController armory; public Shield shield; }
    public struct BodyEntry { public ArmoryController armory; public BodyGear gear; }

    /// <summary>Броня и плащи во всех целых Оружейных (броня первой, внутри - лучшее первым).</summary>
    public static List<BodyEntry> AvailableBodies()
    {
        var res = new List<BodyEntry>();
        foreach (var a in All)
        {
            if (a == null || !a.Works) continue;
            foreach (var g in a.bodies)
                res.Add(new BodyEntry { armory = a, gear = g });
        }
        res.Sort((p, q) => p.gear.kind != q.gear.kind ? p.gear.kind.CompareTo(q.gear.kind) : q.gear.Score.CompareTo(p.gear.Score));
        return res;
    }

    public static int TotalBodies
    {
        get
        {
            int n = 0;
            foreach (var a in All)
                if (a != null && a.Works) n += a.bodies.Count;
            return n;
        }
    }

    /// <summary>Положить броню / плащ в любую целую Оружейную со свободным местом. false - места нет.</summary>
    public static bool StashBody(BodyGear g)
    {
        if (g == null) return true;
        foreach (var a in All)
        {
            if (a == null || !a.Works || a.ItemCount >= a.Capacity) continue;
            a.bodies.Add(g);
            return true;
        }
        return false;
    }

    /// <summary>Всё подходящее оружие во всех целых Оружейных, лучшее - первым.</summary>
    public static List<StockEntry> Available(WeaponKind kind)
    {
        var res = new List<StockEntry>();
        foreach (var a in All)
        {
            if (a == null || !a.Works) continue;
            foreach (var w in a.stock)
                if (w.kind == kind) res.Add(new StockEntry { armory = a, weapon = w });
        }
        res.Sort((p, q) => q.weapon.Score.CompareTo(p.weapon.Score));
        return res;
    }

    /// <summary>Все щиты во всех целых Оружейных, лучший - первым.</summary>
    public static List<ShieldEntry> AvailableShields()
    {
        var res = new List<ShieldEntry>();
        foreach (var a in All)
        {
            if (a == null || !a.Works) continue;
            foreach (var s in a.shields)
                res.Add(new ShieldEntry { armory = a, shield = s });
        }
        res.Sort((p, q) => q.shield.Score.CompareTo(p.shield.Score));
        return res;
    }

    /// <summary>Сколько ещё предметов влезет во все целые Оружейные.</summary>
    public static int FreeSlotsTotal
    {
        get
        {
            int n = 0;
            foreach (var a in All)
                if (a != null && a.Works) n += Mathf.Max(0, a.Capacity - a.ItemCount);
            return n;
        }
    }

    public static int TotalWeapons
    {
        get
        {
            int n = 0;
            foreach (var a in All)
                if (a != null && a.Works) n += a.stock.Count;
            return n;
        }
    }

    public static int TotalShields
    {
        get
        {
            int n = 0;
            foreach (var a in All)
                if (a != null && a.Works) n += a.shields.Count;
            return n;
        }
    }

    /// <summary>Положить оружие в любую целую Оружейную со свободным местом. false - места нет.</summary>
    public static bool Stash(Weapon w)
    {
        if (w == null) return true;
        foreach (var a in All)
        {
            if (a == null || !a.Works || a.ItemCount >= a.Capacity) continue;
            a.stock.Add(w);
            return true;
        }
        return false;
    }

    /// <summary>Положить щит в любую целую Оружейную со свободным местом. false - места нет.</summary>
    public static bool StashShield(Shield s)
    {
        if (s == null) return true;
        foreach (var a in All)
        {
            if (a == null || !a.Works || a.ItemCount >= a.Capacity) continue;
            a.shields.Add(s);
            return true;
        }
        return false;
    }

    // ---------- Отравленные луки (путь Магии) ----------

    /// <summary>Лучший лук на складе этой Оружейной, ещё не отравленный (или с почти кончившимся ядом).</summary>
    Weapon BowToPoison()
    {
        Weapon best = null;
        foreach (var w in stock)
        {
            if (w == null || w.kind != WeaponKind.Bow || w.poisonCharges > Weapon.PoisonChargesPerPotion / 3 || poisonPending.Contains(w)) continue;
            if (best == null || w.Score > best.Score) best = w;
        }
        return best;
    }

    readonly HashSet<Weapon> poisonPending = new HashSet<Weapon>();   // луки, к которым уже несут зелье

    void StartPoison(Weapon bow)
    {
        if (!Works || bow == null || poisonPending.Contains(bow)) return;
        if (!PotionHouse.TakePotions(1, out BuildingInstance src)) return;
        poisonPending.Add(bow);

        var pm = PopulationManager.Instance;
        BuildingInstance pickup = src != null ? src : (pm != null ? pm.Hall : null);

        Logistics.Request(new HaulJob
        {
            from = pickup,
            to = Building,
            prefer = Building,
            onDeliver = () =>
            {
                poisonPending.Remove(bow);
                bow.poisonCharges += Weapon.PoisonChargesPerPotion;
                WarManager.Notify($"Отравлен: {bow.Name}", true);
            },
            onAbort = loaded =>
            {
                poisonPending.Remove(bow);
                PotionHouse.ReturnPotions(1, src);
            }
        });
    }

    // ---------- Замедляющие луки (зелье замедления: Зельеварня или рынок) ----------

    readonly HashSet<Weapon> slowPending = new HashSet<Weapon>();

    Weapon BowToSlow()
    {
        Weapon best = null;
        foreach (var w in stock)
        {
            if (w == null || w.kind != WeaponKind.Bow || w.slowCharges > Weapon.SlowChargesPerPotion / 3 || slowPending.Contains(w)) continue;
            if (best == null || w.Score > best.Score) best = w;
        }
        return best;
    }

    void StartSlow(Weapon bow)
    {
        var rm = ResourceManager.Instance;
        if (!Works || bow == null || slowPending.Contains(bow) || rm == null || !rm.SpendSlowPotions(1)) return;
        slowPending.Add(bow);
        var pm = PopulationManager.Instance;

        Logistics.Request(new HaulJob
        {
            from = pm != null ? pm.Hall : null,
            to = Building,
            prefer = Building,
            onDeliver = () =>
            {
                slowPending.Remove(bow);
                bow.slowCharges += Weapon.SlowChargesPerPotion;
                WarManager.Notify($"Лук смазан зельем замедления: {bow.Name}", true);
            },
            onAbort = loaded =>
            {
                slowPending.Remove(bow);
                if (ResourceManager.Instance != null) ResourceManager.Instance.AddSlowPotions(1);
            }
        });
    }

    void DrawSlowRow(float x, float y, float w, GUIStyle small)
    {
        Weapon bow = BowToSlow();
        int n = ResourceManager.Instance != null ? ResourceManager.Instance.slowPotions : 0;
        Rect lr = new Rect(x, y, w - 150f, 28f);
        GUI.Label(lr, UiKit.Col(UiKit.InfoHex, "Замедление") + $": зелий {n}" + (slowPending.Count > 0 ? UiKit.Col(UiKit.MutedHex, $" · несут {slowPending.Count}") : ""), small);
        UiKit.Tip(lr, $"Зелье замедления из Ратуши (Зельеварня или рынок). 1 зелье = {Weapon.SlowChargesPerPotion} выстрелов: стрелы замедляют врагов. Смазывается лучший лук на складе.");
        string why = !Works ? "Оружейная разрушена" : (bow == null ? "На складе нет луков без зелья" : (n <= 0 ? "Нет зелий замедления в Ратуше" : "Смазать лучший лук зельем замедления"));
        if (UiKit.Button(new Rect(x + w - 144f, y, 144f, 26f), "Смазать лук", Works && bow != null && n > 0, UiKit.Kind.Normal, why) && bow != null) StartSlow(bow);
    }

    // Ряды зелий видны пути Магии и всем, у кого есть зелья (например, купленные на рынке)
    static bool ShowPoisonRow => DevelopmentPath.IsMagic || PotionHouse.TotalPotions > 0;
    static bool ShowSlowRow => DevelopmentPath.IsMagic || (ResourceManager.Instance != null && ResourceManager.Instance.slowPotions > 0);

    void DrawPoisonRow(float x, float y, float w, GUIStyle small)
    {
        Weapon bow = BowToPoison();
        int potions = PotionHouse.TotalPotions;
        Rect lr = new Rect(x, y, w - 150f, 28f);
        GUI.Label(lr, UiKit.Col(UiKit.GoodHex, "Яд") + $": зелий {potions}" + (poisonPending.Count > 0 ? UiKit.Col(UiKit.MutedHex, $" · несут {poisonPending.Count}") : ""), small);
        UiKit.Tip(lr, $"Зелья отравления в Ратуше и Хранилищах. 1 зелье = {Weapon.PoisonChargesPerPotion} выстрелов ядом. Смазывается лучший лук на складе.");
        string why = !Works ? "Оружейная разрушена" : (bow == null ? "На складе нет луков без яда" : (potions <= 0 ? "Нет зелий отравления" : "Отравить лучший лук"));
        if (UiKit.Button(new Rect(x + w - 144f, y, 144f, 26f), "Отравить лук", Works && bow != null && potions > 0, UiKit.Kind.Normal, why) && bow != null) StartPoison(bow);
    }

    public bool RemoveFromStock(Weapon w) { return Works && stock.Remove(w); }
    public void AddToStock(Weapon w) { if (w != null) stock.Add(w); }
    public bool RemoveShield(Shield s) { return Works && shields.Remove(s); }
    public void AddShield(Shield s) { if (s != null) shields.Add(s); }
    public bool RemoveBody(BodyGear g) { return Works && bodies.Remove(g); }
    public void AddBody(BodyGear g) { if (g != null) bodies.Add(g); }

    // ---------- Панель здания ----------

    Vector2 scroll;
    int tab = 0;   // 0 - Ковка, 1 - Склад

    const float ItemRowH = 50f;
    const float RecipeRowH = 32f;

    // Одна строка склада: название, полоска прочности, кнопки "Починить" и "Улучшить"
    void ItemRow(float ry, float innerW, GUIStyle small, string title, string stats, float frac, int repairCost, bool canRepair,
                 bool maxed, int bldLevel, int nextUpgrade, string nextName, int nextCost, bool canUpgrade,
                 out bool repair, out bool upgrade, bool upgradable = true)
    {
        Rect tr = new Rect(0f, ry, innerW, 18f);
        GUI.Label(tr, title, small);
        UiKit.Tip(tr, stats);
        UiKit.Bar(new Rect(0f, ry + 19f, innerW, 5f), frac,
            Color.Lerp(UiKit.Bad, UiKit.Good, frac));

        float bw = upgradable ? (innerW - 4f) / 2f : innerW;

        string repairLabel = repairCost > 0 ? $"Починить ({repairCost} зол)" : "Целое";
        string repairTip = repairCost <= 0 ? "Предмет целый" : (canRepair ? $"Починить за {repairCost} золота" : "Не хватает золота");
        repair = UiKit.Button(new Rect(0f, ry + 26f, bw, 22f), repairLabel, canRepair, UiKit.Kind.Normal, repairTip);

        upgrade = false;
        if (!upgradable) return;   // броню и плащи не улучшают - только чинят
        string upLabel, upTip;
        if (maxed) { upLabel = "Макс."; upTip = "Улучшено до предела"; }
        else if (bldLevel < nextUpgrade) { upLabel = $"→ {nextName}"; upTip = $"Нужна Оружейная Ур. {nextUpgrade}"; }
        else { upLabel = $"→ {nextName} ({nextCost} зол)"; upTip = canUpgrade ? $"Улучшить до «{nextName}» за {nextCost} золота" : "Не хватает золота"; }
        upgrade = UiKit.Button(new Rect(bw + 4f, ry + 26f, bw, 22f), upLabel, canUpgrade, UiKit.Kind.Normal, upTip);
    }

    // Почему нельзя начать ковку ("" - можно)
    string CraftProblem(WeaponRecipe r)
    {
        if (!Works) return "Оружейная разрушена";
        if (!Building.isProducing) return "Оружейная не работает" + (Building.StatusText != "" ? ": " + Building.StatusText : "");
        if (IsBusy) return "Уже идёт ковка";
        if (ItemCount >= Capacity) return "Склад полон";
        if (!CanAfford(r)) return r.leaves > 0 || r.gold > 0 ? $"Не хватает в казне: {r.CostText}" : $"Не хватает материалов в Ратуше: {r.CostText} (среднего качества и выше)";
        return "";
    }

    float HeaderH(BuildingInstance b)
    {
        return UiKit.RowH * 2f + (b.isWorking ? 0f : UiKit.RowH) + 4f;
    }

    float ForgeTabH(BuildingInstance b)
    {
        return UiKit.SectionH + VisibleRecipes().Count * RecipeRowH + 4f + 30f + 8f + UpgradeRules.SectionHeight(b);
    }

    float StockTabH()
    {
        float listH = Mathf.Clamp(ItemCount * ItemRowH, ItemRowH * 2f, ItemRowH * 5f);
        float h = listH + 6f;
        if (ShowPoisonRow || ShowSlowRow) h += UiKit.SectionH;
        if (ShowPoisonRow) h += 30f;
        if (ShowSlowRow) h += 30f;
        return h;
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        int lvl = b.level;
        const float tabsH = 26f;

        float contentH = HeaderH(b) + tabsH + 8f + (tab == 1 ? StockTabH() : ForgeTabH(b));
        float h = UiKit.WindowH(contentH);
        Rect c = UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {lvl}");
        float cx = c.x, cw = c.width, cy = c.y;

        GUIStyle body = new GUIStyle(UiKit.Body) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
        GUIStyle small = new GUIStyle(UiKit.Small) { wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };

        // --- Что делает ---
        if (stage == Stage.Crafting)
        {
            GUI.Label(new Rect(cx, cy, cw - 150f, UiKit.RowH), $"Ковка: <b>{(current != null ? current.recipeName : "")}</b>", body);
            UiKit.Bar(new Rect(cx + cw - 140f, cy + 4f, 140f, 14f), CraftProgress, UiKit.Good, $"{CraftProgress * 100f:F0}%");
        }
        else if (stage == Stage.Fetching)
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.WarnHex, "Несут материалы из Ратуши..."), body);
        else
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), ItemCount >= Capacity ? UiKit.Col(UiKit.WarnHex, "Склад полон") : UiKit.Col(UiKit.MutedHex, "Свободна — закажите ковку"), body);
        cy += UiKit.RowH;

        Rect sr = new Rect(cx, cy, cw - 20f, UiKit.RowH);
        GUI.Label(sr, UiKit.Col(UiKit.MutedHex, "Склад: ") + UiKit.Col(ItemCount >= Capacity ? UiKit.WarnHex : UiKit.GoodHex, $"{ItemCount}/{Capacity}")
            + UiKit.Col(UiKit.MutedHex, $"   оружие {stock.Count} · щиты {shields.Count}" + (bodies.Count > 0 ? $" · броня и плащи {bodies.Count}" : "")), body);
        Rect qr = new Rect(cx + cw - 16f, cy, 16f, UiKit.RowH);
        GUI.Label(qr, UiKit.Col(UiKit.InfoHex, "?"), body);
        UiKit.Tip(qr, "Куёт мечи, луки и щиты из стали и досок. Качество случайное: чем выше уровень, тем лучше. " +
                      (DevelopmentPath.IsHeavy ? "Ветка «Тяжёлая стратегия»: броня (−25…36% урона, −10% скорости). " : "") +
                      (DevelopmentPath.IsCamo ? "Ветка «Мастер маскировки»: плащи из листвы (враги замечают вдвое ближе). " : "") +
                      "Золото чинит и усиливает предметы, выдаёт их казарма (вкладка «Снаряжение»). " +
                      $"Вместимость склада растёт с уровнем: 12 / 24 / 40. Выковано всего: {CraftedTotal}.");
        cy += UiKit.RowH;

        if (!b.isWorking)
        {
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.BadHex, b.StatusText), body);
            cy += UiKit.RowH;
        }
        cy += 4f;

        int prevTab = tab;
        int newTab = UiKit.Tabs(new Rect(cx, cy, cw, tabsH), new[] { "Ковка", $"Склад ({ItemCount})" }, tab);
        cy += tabsH + 8f;

        if (tab == 0)
        {
            // --- Рецепты ---
            cy = UiKit.Section(cx, cy, cw, "Заказать");
            GUIStyle cost = new GUIStyle(small) { wordWrap = false };
            foreach (var r in VisibleRecipes())
            {
                GUI.Label(new Rect(cx, cy, 130f, RecipeRowH - 4f), $"<b>{r.recipeName}</b>", body);
                GUI.Label(new Rect(cx + 134f, cy, cw - 134f - 100f, RecipeRowH - 4f),
                    r.CostText + UiKit.Col(UiKit.MutedHex, $" · {r.craftTime:0.#} с"), cost);
                string problem = CraftProblem(r);
                string what = r.isBody ? (r.bodyKind == BodyKind.Armor
                        ? "\nБроня: урон по бойцу −25…36% (по качеству), скорость −10%. Выдаётся в казарме."
                        : "\nПлащ: враги замечают бойца вдвое ближе, пока он не бьёт; разведчика — лишь вплотную. Выдаётся в казарме.") : "";
                if (UiKit.Button(new Rect(cx + cw - 92f, cy, 92f, RecipeRowH - 4f), "Заказать", problem == "", UiKit.Kind.Primary,
                        (problem == "" ? $"{r.recipeName}: {r.CostText}, {r.craftTime:0.#} с" : problem) + what))
                    StartCraft(r);
                cy += RecipeRowH;
            }
            cy += 4f;

            if (PopulationManager.Instance != null)
                PopulationManager.Instance.DrawWorkersRow(cx, cy, b);
            cy += 30f + 8f;

            UiKit.Line(cx, cy - 5f, cw);
            UpgradeRules.DrawUpgradeSection(cx, cy, cw, b);
        }
        else
        {
            // --- Склад ---
            int rows = ItemCount;
            float listH = Mathf.Clamp(rows * ItemRowH, ItemRowH * 2f, ItemRowH * 5f);
            float innerW = cw - 14f;
            scroll = GUI.BeginScrollView(new Rect(cx, cy, cw, listH), scroll,
                new Rect(0f, 0f, innerW, Mathf.Max(listH, rows * ItemRowH)));

            if (rows == 0)
                GUI.Label(new Rect(0f, 0f, innerW, 40f), UiKit.Col(UiKit.MutedHex, "Склад пуст — закажите ковку"), UiKit.Small);

            Weapon repairW = null, upgradeW = null;
            Shield repairS = null, upgradeS = null;
            BodyGear repairB = null;

            for (int i = 0; i < stock.Count; i++)
            {
                Weapon wp = stock[i];
                ItemRow(i * ItemRowH, innerW, small,
                    $"<b>{wp.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"{wp.Percent}%"), wp.StatsText, wp.Frac, wp.RepairCost, CanRepairWeapon(wp),
                    !wp.CanUpgrade, lvl, wp.upgrade + 1, Weapon.UpgradeName(wp.upgrade + 1), Weapon.UpgradeCost(wp.upgrade + 1), CanUpgradeWeapon(wp),
                    out bool rep, out bool upg);
                if (rep) repairW = wp;
                if (upg) upgradeW = wp;
            }

            for (int i = 0; i < shields.Count; i++)
            {
                Shield sh = shields[i];
                ItemRow((stock.Count + i) * ItemRowH, innerW, small,
                    $"<b>{sh.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"{sh.Percent}%"), sh.StatsText, sh.Frac, sh.RepairCost, CanRepairShield(sh),
                    !sh.CanUpgrade, lvl, sh.upgrade + 1, Shield.UpgradeName(sh.upgrade + 1), Shield.UpgradeCost(sh.upgrade + 1), CanUpgradeShield(sh),
                    out bool rep, out bool upg);
                if (rep) repairS = sh;
                if (upg) upgradeS = sh;
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                BodyGear bg = bodies[i];
                string col = bg.IsArmor ? "#B8C4D0" : "#90D070";
                ItemRow((stock.Count + shields.Count + i) * ItemRowH, innerW, small,
                    UiKit.Col(col, $"<b>{bg.Name}</b>") + "  " + UiKit.Col(UiKit.MutedHex, $"{bg.Percent}%"), bg.StatsText, bg.Frac, bg.RepairCost, CanRepairBody(bg),
                    true, lvl, 0, "", 0, false, out bool rep, out bool upg, false);
                if (rep) repairB = bg;
            }
            GUI.EndScrollView();
            cy += listH + 6f;

            if (repairW != null) TryRepairWeapon(repairW);
            if (upgradeW != null) TryUpgradeWeapon(upgradeW);
            if (repairS != null) TryRepairShield(repairS);
            if (upgradeS != null) TryUpgradeShield(upgradeS);
            if (repairB != null) TryRepairBody(repairB);

            // --- Зелья для луков ---
            if (ShowPoisonRow || ShowSlowRow) cy = UiKit.Section(cx, cy, cw, "Зелья для луков");
            if (ShowPoisonRow) { DrawPoisonRow(cx, cy, cw, small); cy += 30f; }
            if (ShowSlowRow) { DrawSlowRow(cx, cy, cw, small); cy += 30f; }
        }

        if (newTab != prevTab) tab = newTab;
        return new Rect(x, y, w + 5f, h);
    }
}
