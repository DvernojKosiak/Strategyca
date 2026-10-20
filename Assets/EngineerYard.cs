using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Инженерный двор (только путь Инженерии, здание 4×4). Инженеры собирают МОДУЛИ для построек по чертежам
/// (см. TowerModules.Recipes): Паровой отбойник для башни и Раздвижной механизм для моста
/// (поднятый мост не пропускает никого - см. BridgeController).
/// Всё носится физически:
///  1. Заказ: золото, детали (из Литейной), сталь и доски списываются, инженер несёт их из Ратуши во двор.
///  2. Сборка. Готовый модуль лежит на складе двора (место растёт с уровнем).
///  3. Установка (из панели башни / моста или двора): инженер несёт модуль со двора к постройке и ставит.
///     На постройке уже стоит модуль - старый возвращается на склад двора. «Снять» - инженер уносит модуль во двор.
/// Марка модуля (Мк.I-III) = уровень двора в момент сборки: чем выше марка, тем модуль сильнее.
/// Модули продаются и покупаются на рынке; купленные лежат в Ратуше, и поставить их может любой город.
///
/// Вторая вкладка - «Ловушки» (см. Traps.cs): рецепт = основа (капкан, шипы, сеть, пружина) + добавки.
/// Каждой основе нужна РЕЗИНА (её варит Литейная). Готовые ловушки кладут в запас Ратуши; ставятся они
/// кнопкой «Ловушки» [T]. Инженеры одни: пока собирают ловушку, модуль ждёт, и наоборот.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class EngineerYard : MonoBehaviour
{
    public const string TypeName = "Инженерный двор";

    public static readonly List<EngineerYard> All = new List<EngineerYard>();

    static readonly int[] CapacityByLevel = { 2, 4, 6 };        // сколько готовых модулей помещается на складе
    static readonly float[] SpeedByLevel = { 1f, 1.25f, 1.5f };

    enum Stage { Idle, Fetching, Building }
    Stage stage = Stage.Idle;
    ModuleRecipe current;
    int currentMk;
    float timer;
    float timerTotal = 1f;

    // Сборка ловушки (вкладка «Ловушки»): что собираем и что за это заплатили
    TrapKit currentTrap;
    TrapCost currentTrapCost;
    int tab;                                      // 0 - модули, 1 - ловушки
    TrapBase pickBase = TrapBase.Jaw;
    TrapMod pickMods = TrapMod.None;
    public int TrapsBuiltTotal { get; private set; }
    readonly List<ModuleKit> stock = new List<ModuleKit>();
    readonly List<UpgradeRules.Spent> spent = new List<UpgradeRules.Spent>();

    public int BuiltTotal { get; private set; }
    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public int Mk => Lvl + 1;
    public int Capacity => CapacityByLevel[Lvl];
    public int StockCount => stock.Count;
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    static BuildingInstance HallBuilding()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    static int Have(RefinedKind k)
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, k, QualityTier.Medium) : 0;
    }

    // ---------- Сборка модуля ----------

    public string BuildProblem(ModuleRecipe r)
    {
        if (!DevelopmentPath.IsEngineering) return "Нужен путь Инженерии";
        if (!TowerModules.Available(r)) return $"Только для направления «{DevelopmentPath.EngName(r.school)}»";
        if (!Works) return "Двор разрушен";
        if (Building.level < r.minLevel) return $"Нужен двор Ур.{r.minLevel}";
        if (stage != Stage.Idle) return "Инженеры заняты";
        if (!Building.isProducing) return "Нет инженеров";
        if (r.kind == ModuleKind.ApShot)
        {
            if (ArmoryController.FreeSlotsTotal <= 0) return "Нет места в Оружейной для ядра";
        }
        else if (stock.Count >= Capacity) return "Склад модулей полон — поставьте модули на башни, мосты и пушки";
        var rm = ResourceManager.Instance;
        if (rm == null) return "Нет казны";
        if (rm.gold < r.gold) return $"Нужно {r.gold} золота";
        if (rm.parts < r.parts) return $"Нужно {r.parts} деталей (есть {rm.parts}) — их льёт Литейная";
        if (Have(RefinedKind.Steel) < r.steel) return $"Нужно {r.steel} стали";
        if (Have(RefinedKind.Planks) < r.planks) return $"Нужно {r.planks} досок";
        if (rm.resin < r.resin) return $"Нужно {r.resin} смолы (есть {rm.resin})";
        return "";
    }

    public void StartBuild(ModuleRecipe r)
    {
        if (BuildProblem(r) != "") return;
        var rm = ResourceManager.Instance;
        if (!rm.Spend(r.gold, 0, 0, 0)) return;
        if (!rm.SpendParts(r.parts)) { rm.Add(r.gold, 0, 0, 0); return; }
        if (r.resin > 0 && !rm.SpendResin(r.resin)) { rm.Add(r.gold, 0, 0, 0); rm.AddParts(r.parts); return; }

        spent.Clear();
        var st = TownHallStorage.Instance;
        if (r.steel > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, r.steel, spent);
        if (r.planks > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, r.planks, spent);

        current = r;
        currentMk = Mk;
        stage = Stage.Fetching;

        Logistics.Request(new HaulJob
        {
            from = HallBuilding(),
            to = Building,
            prefer = Building,
            onDeliver = () =>
            {
                if (current == null) return;
                if (!Works) { Refund(); return; }
                timer = timerTotal = current.seconds;
                stage = Stage.Building;
            },
            onAbort = loaded => Refund()
        });
    }

    void Refund()
    {
        if (currentTrap != null)
        {
            Traps.Refund(currentTrapCost, spent);
            currentTrap = null;
            stage = Stage.Idle;
            return;
        }
        if (current != null && ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(current.gold, 0, 0, 0);
            ResourceManager.Instance.AddParts(current.parts);
            ResourceManager.Instance.AddResin(current.resin);
        }
        UpgradeRules.RefundSpent(spent);
        spent.Clear();
        current = null;
        stage = Stage.Idle;
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (stage != Stage.Building || !Works || !Building.isProducing) return;

        timer -= Time.deltaTime * SpeedByLevel[Lvl] * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
        if (timer > 0f) return;

        if (currentTrap != null)
        {
            TrapKit done = currentTrap;
            TrapStock.Add(done);
            TrapsBuiltTotal++;
            currentTrap = null;
            stage = Stage.Idle;
            spent.Clear();
            WarManager.Notify($"Инженерный двор: собрана ловушка — {done.Name}. Лежит в Ратуше, ставить — кнопка «Ловушки» [T]", true);
            return;
        }

        if (current.kind == ModuleKind.ApShot)
        {
            // Бронебойное ядро - не модуль, а «оружие» полевой пушки: несут в Оружейную
            Weapon core = new Weapon(WeaponKind.Shot, TowerModules.ApShotQuality(currentMk));
            BuiltTotal++;
            current = null;
            stage = Stage.Idle;
            spent.Clear();
            if (ArmoryController.Stash(core))
                WarManager.Notify($"Инженерный двор: готово {core.Name.ToLower()} — лежит в Оружейной, выдайте полевой пушке («Снаряж.» в казарме) или зарядите Пушку", true);
            else
                WarManager.Notify("Инженерный двор: ядро готово, но в Оружейных нет места — оно потеряно", false);
            return;
        }

        ModuleKit kit = new ModuleKit { kind = current.kind, mk = currentMk };
        stock.Add(kit);
        BuiltTotal++;
        current = null;
        stage = Stage.Idle;
        spent.Clear();
        ModuleRecipe rr = TowerModules.RecipeOf(kit.kind);
        string where = rr != null && rr.target == BuildingInstance.BridgeName ? "мост (панель моста)"
                     : (kit.kind == ModuleKind.CannonLift ? "пушку (панель пушки); полевые пушки берут его сами" : "башню");
        WarManager.Notify($"Инженерный двор: собран {TowerModules.KitName(kit)}. Поставьте его на {where}", true);
    }

    // ---------- Склад модулей: дворы + Ратуша ----------
    // Модули, купленные на рынке (или вернувшиеся, когда двора нет), лежат в Ратуше.
    // Ставить модули может любой город - путь Инженерии нужен только, чтобы их собирать.

    public static readonly List<ModuleKit> HallStock = new List<ModuleKit>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { HallStock.Clear(); }

    public static int HallKitCount => HallStock.Count;

    static BuildingInstance HallB()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    public static int CountKits(ModuleKind k, int betterThan = 0)
    {
        int n = 0;
        foreach (var y in All)
        {
            if (y == null || !y.Works) continue;
            foreach (var kit in y.stock) if (kit.kind == k && kit.mk > betterThan) n++;
        }
        foreach (var kit in HallStock) if (kit.kind == k && kit.mk > betterThan) n++;
        return n;
    }

    public static int BestMk(ModuleKind k)
    {
        int best = 0;
        foreach (var y in All)
        {
            if (y == null || !y.Works) continue;
            foreach (var kit in y.stock) if (kit.kind == k && kit.mk > best) best = kit.mk;
        }
        foreach (var kit in HallStock) if (kit.kind == k && kit.mk > best) best = kit.mk;
        return best;
    }

    /// <summary>Все готовые модули (во дворах и в Ратуше) - для рынка.</summary>
    public static List<ModuleKit> AllKits()
    {
        var list = new List<ModuleKit>();
        foreach (var y in All)
            if (y != null && y.Works) list.AddRange(y.stock);
        list.AddRange(HallStock);
        return list;
    }

    /// <summary>Взять модуль такого вида и марки (для продажи). source - откуда его понесут.</summary>
    /// Сначала ищется точно такой же (с тем же остатком зарядов), иначе - любой этого вида и марки.
    public static bool TakeKit(ModuleKit want, out BuildingInstance source)
    {
        return TakeKit(want, true, out source) || TakeKit(want, false, out source);
    }

    static bool TakeKit(ModuleKit want, bool exact, out BuildingInstance source)
    {
        source = null;
        System.Predicate<ModuleKit> match = k => k.kind == want.kind && k.mk == want.mk && (!exact || k.charges == want.charges);
        foreach (var y in All)
        {
            if (y == null || !y.Works) continue;
            int i = y.stock.FindIndex(match);
            if (i < 0) continue;
            y.stock.RemoveAt(i);
            source = y.Building;
            return true;
        }
        int h = HallStock.FindIndex(match);
        if (h < 0) return false;
        HallStock.RemoveAt(h);
        source = HallB();
        return true;
    }

    /// <summary>Модуль привезли (рынок) - кладём на склад Ратуши.</summary>
    public static void AddToHall(ModuleKit kit) { if (kit.kind != ModuleKind.None) HallStock.Add(kit); }

    // Где лежит лучший модуль этого вида (при равной марке - ближе к точке). yard == null, index >= 0 - в Ратуше.
    static bool FindKit(ModuleKind k, Vector2 near, int betterThan, out EngineerYard yard, out int index)
    {
        yard = null;
        index = -1;
        int bestMk = betterThan;
        float bestD = float.MaxValue;
        bool found = false;
        foreach (var y in All)
        {
            if (y == null || !y.Works) continue;
            for (int i = 0; i < y.stock.Count; i++)
            {
                ModuleKit kit = y.stock[i];
                if (kit.kind != k || kit.mk <= betterThan) continue;
                float d = Vector2.Distance(near, y.Building.Center);
                if (kit.mk > bestMk || (kit.mk == bestMk && d < bestD))
                {
                    bestMk = kit.mk; bestD = d; yard = y; index = i; found = true;
                }
            }
        }
        BuildingInstance hall = HallB();
        float hd = hall != null ? Vector2.Distance(near, hall.Center) : 0f;
        for (int i = 0; i < HallStock.Count; i++)
        {
            ModuleKit kit = HallStock[i];
            if (kit.kind != k || kit.mk <= betterThan) continue;
            if (kit.mk > bestMk || (kit.mk == bestMk && hd < bestD))
            {
                bestMk = kit.mk; bestD = hd; yard = null; index = i; found = true;
            }
        }
        return found;
    }

    static EngineerYard NearestYard(Vector2 p)
    {
        EngineerYard best = null;
        float bestD = float.MaxValue;
        foreach (var y in All)
        {
            if (y == null || !y.Works) continue;
            float d = Vector2.Distance(p, y.Building.Center);
            if (d < bestD) { bestD = d; best = y; }
        }
        return best;
    }

    // Вернуть модуль на склад: в этот двор, иначе в ближайший, иначе в Ратушу. Возврат не упирается в лимит места.
    static void ReturnKit(ModuleKit kit, EngineerYard prefer, Vector2 near)
    {
        if (kit.kind == ModuleKind.None) return;
        EngineerYard y = prefer != null && prefer.Works ? prefer : NearestYard(near);
        if (y != null) y.stock.Add(kit);
        else HallStock.Add(kit);
    }

    // ---------- Установка на постройку (башню, мост) ----------

    static string HostName(IModuleHost h)
    {
        return h != null && h.Building != null && h.Building.type != null ? h.Building.type.name : "постройка";
    }

    /// <summary>Почему нельзя поставить (или заменить) модуль на эту постройку ("" - можно).</summary>
    public static string InstallProblem(IModuleHost tower, ModuleKind k)
    {
        if (tower == null || tower.Building == null) return "Нет постройки";
        if (tower.ModuleBusy) return tower.ModuleRemoving ? "Модуль уносят..." : "Модуль несут...";
        if (tower.Building.IsRuined) return HostName(tower) + ": в руинах";
        int betterThan = BetterThan(tower, k);
        if (CountKits(k, betterThan) <= 0)
        {
            if (betterThan > 0 && CountKits(k) > 0) return "Стоит лучший из готовых";
            if (MagicShells.IsShell(k)) return DevelopmentPath.IsMagic ? "Нет пачек (Магическая оружейная)" : "Нет снарядов (рынок)";
            return DevelopmentPath.IsEngineering ? "Нет готовых (Инженерный двор)" : "Нет модулей (рынок)";
        }
        return "";
    }

    // Какую марку надо превзойти: та же марка годится, если стоит начатая пачка снарядов (дозарядка)
    static int BetterThan(IModuleHost tower, ModuleKind k)
    {
        if (tower.Module != k) return 0;
        CannonShellSlot slot = tower as CannonShellSlot;
        if (slot != null && slot.Charges < MagicShells.Charges(tower.ModuleMk)) return tower.ModuleMk - 1;
        return tower.ModuleMk;
    }

    public static bool Install(IModuleHost tower, ModuleKind k)
    {
        if (InstallProblem(tower, k) != "") return false;
        BuildingInstance tb = tower.Building;
        int betterThan = BetterThan(tower, k);
        if (!FindKit(k, tb.Center, betterThan, out EngineerYard yard, out int idx)) return false;

        ModuleKit kit;
        BuildingInstance from;
        if (yard != null) { kit = yard.stock[idx]; yard.stock.RemoveAt(idx); from = yard.Building; }
        else { kit = HallStock[idx]; HallStock.RemoveAt(idx); from = HallB(); }

        tower.ModuleBusy = true;
        tower.ModuleRemoving = false;
        Vector2 towerPos = tb.Center;

        // Инженер двора (или курьер Ратуши) несёт модуль к постройке
        string hostName = HostName(tower);
        MonoBehaviour hostObj = tower as MonoBehaviour;
        Logistics.Request(new HaulJob
        {
            from = from,
            to = tb,
            prefer = from,
            onDeliver = () =>
            {
                if (hostObj == null) { ReturnKit(kit, yard, towerPos); return; }   // постройку снесли
                tower.ModuleBusy = false;
                if (tower.HasModule) ReturnKit(tower.TakeModule(), yard, towerPos);  // старый - обратно на склад
                tower.SetModule(kit.kind, kit.mk);
                CannonShellSlot slot = tower as CannonShellSlot;
                if (slot != null) slot.SetCharges(kit.charges);
                WarManager.Notify($"{hostName}: поставлен {TowerModules.KitName(kit)}", true);
            },
            onAbort = loaded =>
            {
                if (hostObj != null) tower.ModuleBusy = false;
                ReturnKit(kit, yard, towerPos);
            }
        });
        return true;
    }

    public static string RemoveProblem(IModuleHost tower)
    {
        if (tower == null || !tower.HasModule) return "Модуля нет";
        if (tower.ModuleBusy) return "Инженеры уже заняты";
        return "";
    }

    /// <summary>Снять модуль: постройка сразу перестаёт им пользоваться, его уносят во двор (нет двора - в Ратушу).</summary>
    public static bool Remove(IModuleHost tower)
    {
        if (RemoveProblem(tower) != "") return false;
        MonoBehaviour hostObj = tower as MonoBehaviour;
        BuildingInstance tb = tower.Building;
        Vector2 towerPos = tb.Center;
        EngineerYard yard = NearestYard(towerPos);
        BuildingInstance to = yard != null ? yard.Building : HallB();
        ModuleKit kit = tower.TakeModule();
        tower.ModuleBusy = true;
        tower.ModuleRemoving = true;

        Logistics.Request(new HaulJob
        {
            from = tb,
            to = to,
            prefer = to,
            onDeliver = () => { if (hostObj != null) { tower.ModuleBusy = false; tower.ModuleRemoving = false; } ReturnKit(kit, yard, towerPos); },
            onAbort = loaded => { if (hostObj != null) { tower.ModuleBusy = false; tower.ModuleRemoving = false; } ReturnKit(kit, yard, towerPos); }
        });
        return true;
    }

    // Ближайшая к двору целая башня без модуля
    TowerController NearestBareTower()
    {
        TowerController best = null;
        float bestD = float.MaxValue;
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.IsRuined) continue;
            TowerController t = b.GetComponent<TowerController>();
            if (t == null || t.HasModule || t.ModuleBusy) continue;
            float d = Vector2.Distance(b.Center, Building.Center);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    // ---------- Раздел «Модуль» в панели башни ----------

    // На башню ставится один модуль из TowerModules.TowerKinds (отбойник или фейерверочный лук).
    // Главный (стоящий, иначе первый, какой есть на складе) - обычный раздел; остальные - кнопки «Сменить на ...».
    const float TowerAltRowH = 34f;

    static bool TowerKindRelevant(ModuleKind k)
    {
        if (CountKits(k) > 0) return true;
        ModuleRecipe r = TowerModules.RecipeOf(k);
        return DevelopmentPath.IsEngineering && TowerModules.Available(r);
    }

    static ModuleKind TowerPrimary(TowerController t)
    {
        if (t.HasModule) return t.Module;
        foreach (var k in TowerModules.TowerKinds) if (CountKits(k) > 0) return k;
        foreach (var k in TowerModules.TowerKinds) if (TowerKindRelevant(k)) return k;
        return ModuleKind.SteamRepeller;
    }

    static List<ModuleKind> TowerAlternatives(TowerController t, ModuleKind primary)
    {
        var list = new List<ModuleKind>();
        foreach (var k in TowerModules.TowerKinds)
            if (k != primary && TowerKindRelevant(k)) list.Add(k);
        return list;
    }

    public static bool ShowTowerSection(TowerController t) { return TowerSectionHeight(t) > 0f; }

    public static float TowerSectionHeight(TowerController t)
    {
        if (t == null) return 0f;
        ModuleKind p = TowerPrimary(t);
        float h = HostSectionHeight(t, p);
        if (h <= 0f) return 0f;
        return h + TowerAlternatives(t, p).Count * TowerAltRowH;
    }

    public static float DrawTowerSection(float x, float y, float w, TowerController t)
    {
        if (t == null) return 0f;
        ModuleKind p = TowerPrimary(t);
        float h = DrawHostSection(x, y, w, t, p);
        if (h <= 0f) return 0f;
        float cy = y + h;
        foreach (var k in TowerAlternatives(t, p))
        {
            string ip = InstallProblem(t, k);
            int mk = BestMk(k);
            string name = TowerModules.Name(k);
            string label = ip == "" ? (t.HasModule ? $"Сменить на «{name}» Мк.{TowerModules.Roman(mk)}" : $"Поставить «{name}» Мк.{TowerModules.Roman(mk)}")
                                    : (t.HasModule ? $"Сменить на «{name}»" : $"Поставить «{name}»");
            string tip = ip == "" ? $"{TowerModules.KitName(k, mk)}: {TowerModules.Stats(k, mk)}." +
                                    (t.HasModule ? " На башне один модуль: старый вернётся на склад." : "")
                                  : ip;
            if (UiKit.Button(new Rect(x, cy, w, 30f), label, ip == "", UiKit.Kind.Normal, tip)) Install(t, k);
            cy += TowerAltRowH;
        }
        return cy - y;
    }

    // ---------- Раздел «Модуль» в панели постройки (башня, мост) ----------

    public static bool ShowHostSection(IModuleHost t, ModuleKind k)
    {
        return t != null && (DevelopmentPath.IsEngineering || t.HasModule || t.ModuleBusy || CountKits(k) > 0);
    }

    public static float HostSectionHeight(IModuleHost t, ModuleKind k)
    {
        if (!ShowHostSection(t, k)) return 0f;
        return 22f + (t.HasModule ? 50f : 0f) + 36f + 6f;
    }

    public static float DrawHostSection(float x, float y, float w, IModuleHost t, ModuleKind k)
    {
        float total = HostSectionHeight(t, k);
        if (total <= 0f) return 0f;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        float cy = y;
        string head = t.HasModule ? UiKit.Col(UiKit.AccentHex, "<b>" + TowerModules.KitName(t.Module, t.ModuleMk) + "</b>") : UiKit.Col(UiKit.MutedHex, "нет");
        if (t.ModuleBusy) head += "  " + UiKit.Col(UiKit.WarnHex, t.ModuleRemoving ? "уносят" : "инженер несёт новый");
        GUI.Label(new Rect(x, cy, w, 22f), "Модуль: " + head, st);
        cy += 22f;

        if (t.HasModule)
        {
            Rect sr = new Rect(x, cy, w, 50f);
            GUI.Label(sr, TowerModules.Stats(t.Module, t.ModuleMk) + ".", small);
            if (t.Module == ModuleKind.SteamRepeller) UiKit.Tip(sr, TowerModules.RepellerWeightHint);
            else if (t.Module == ModuleKind.FireworkBow) UiKit.Tip(sr, TowerModules.FireworkHint);
            cy += 50f;
        }

        string ip = InstallProblem(t, k);
        float bw = t.HasModule ? (w - 6f) * 0.68f : w;

        int bestMk = BestMk(k);
        string label;
        string tip;
        if (t.HasModule)
        {
            label = ip == "" ? $"Заменить на Мк.{TowerModules.Roman(bestMk)}" : "Заменить";
            tip = ip == "" ? $"Инженер принесёт {TowerModules.KitName(k, bestMk)}, старый модуль вернётся на склад двора" : ip;
        }
        else
        {
            label = ip == "" ? $"Поставить Мк.{TowerModules.Roman(bestMk)}" : "Поставить модуль";
            tip = ip == "" ? $"{TowerModules.KitName(k, bestMk)} (готово: {CountKits(k)}). Инженер принесёт его со двора." : ip;
        }
        if (UiKit.Button(new Rect(x, cy, bw, 30f), label, ip == "", t.HasModule ? UiKit.Kind.Normal : UiKit.Kind.Primary, tip)) Install(t, k);

        if (t.HasModule)
        {
            string rp = RemoveProblem(t);
            if (UiKit.Button(new Rect(x + bw + 6f, cy, w - bw - 6f, 30f), "Снять", rp == "", UiKit.Kind.Danger,
                    rp == "" ? "Инженер унесёт модуль во двор" : rp))
                Remove(t);
        }
        return total;
    }

    // ---------- Панель двора ----------

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return currentTrap != null ? "Несут резину и материалы для ловушки из Ратуши..." : "Несут детали и материалы из Ратуши...";
                case Stage.Building:
                    float pct = Mathf.Clamp01(1f - timer / Mathf.Max(0.1f, timerTotal)) * 100f;
                    string name = currentTrap != null ? currentTrap.Name : (current != null ? TowerModules.KitName(current.kind, currentMk) : "модуль");
                    return Building.isProducing ? $"Сборка: <b>{name}</b> ({pct:F0}%)" : UiKit.Col(UiKit.BadHex, $"Сборка стоит - нет инженеров ({pct:F0}%)");
                default: return "Инженеры свободны";
            }
        }
    }

    string StockText()
    {
        if (stock.Count == 0) return "пусто";
        var counts = new SortedDictionary<string, int>();
        foreach (var kit in stock)
        {
            string n = TowerModules.KitName(kit);
            counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
        }
        var parts = new List<string>();
        foreach (var kv in counts) parts.Add($"{kv.Key} ×{kv.Value}");
        return string.Join(", ", parts);
    }

    static string CostPart(int need, int have, string what)
    {
        if (need <= 0) return "";
        string col = have >= need ? UiKit.GoodHex : UiKit.BadHex;
        return $"<color={col}>{need} {what}</color>";
    }

    // ---------- Сборка ловушек ----------

    const string SaluteTip = "Пиротехник. Наступившего уносит ракетой на 3–5 клеток в случайную сторону (тяжёлых — ближе), " +
                             "на вершине полёта рвётся салют — урон всем рядом, после приземления жертва оглушена и горит. " +
                             "Нужна смола (горючая смесь).";

    public string TrapProblem(TrapBase b, TrapMod mods)
    {
        if (!DevelopmentPath.IsEngineering) return "Нужен путь Инженерии";
        if (!Works) return "Двор разрушен";
        TrapRecipe r = Traps.RecipeOf(b);
        if (!Traps.Available(r)) return $"{r.name}: только для направления «{DevelopmentPath.EngName(r.school)}»";
        if (Building.level < r.minLevel) return $"{r.name}: нужен двор Ур.{r.minLevel}";
        if (Traps.ModCount(mods) > Traps.ModSlots(Building.level)) return $"Добавок не больше {Traps.ModSlots(Building.level)} (двор Ур.{Building.level})";
        if (stage != Stage.Idle) return "Инженеры заняты";
        if (!Building.isProducing) return "Нет инженеров";
        if (TrapStock.Full) return $"Запас ловушек полон ({TrapStock.MaxStock}) — поставьте или продайте";
        TrapCost c = Traps.CostOf(b, mods);
        string miss = Traps.Missing(c);
        if (miss != "") return "Не хватает: " + miss;
        return "";
    }

    public void StartTrap(TrapBase b, TrapMod mods)
    {
        if (TrapProblem(b, mods) != "") return;
        TrapCost c = Traps.CostOf(b, mods);
        spent.Clear();
        if (!Traps.Pay(c, spent)) return;

        currentTrap = new TrapKit { type = b, mods = mods, mk = Mk };
        currentTrapCost = c;
        float seconds = Traps.SecondsOf(b, mods);
        stage = Stage.Fetching;

        Logistics.Request(new HaulJob
        {
            from = HallBuilding(),
            to = Building,
            prefer = Building,
            onDeliver = () =>
            {
                if (currentTrap == null) return;
                if (!Works) { Refund(); return; }
                timer = timerTotal = seconds;
                stage = Stage.Building;
            },
            onAbort = loaded => Refund()
        });
    }

    // ---------- Панель двора: вкладки «Модули» / «Ловушки» ----------

    // Общая шапка: статус, полоска, (проблема), вкладки. Возвращает, с какой высоты начинается вкладка.
    static float TabsTop(BuildingInstance b)
    {
        float tabsY = UiKit.TitleH + 6f + 22f + 14f + (b.isWorking ? 0f : 22f);
        return tabsY + 32f;
    }

    float BuildFraction => stage == Stage.Building ? Mathf.Clamp01(1f - timer / Mathf.Max(0.1f, timerTotal)) : 0f;

    static float FoldBlockH(string id, string text, float w)
    {
        return UiKit.FoldH + (UiKit.IsOpen(id) ? UiKit.TextH(text, w - 20f, UiKit.MutedStyle) + 4f : 0f) + 6f;
    }

    static void DrawFoldBlock(float x, float y, float w, string id, string text)
    {
        bool open = UiKit.IsOpen(id);
        float th = open ? UiKit.TextH(text, w - 20f, UiKit.MutedStyle) + 4f : 0f;
        if (UiKit.Fold(new Rect(x + 10f, y, w - 20f, UiKit.FoldH), id) && open)
            GUI.Label(new Rect(x + 10f, y + UiKit.FoldH, w - 20f, th), text, UiKit.MutedStyle);
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        float h = tab == 0 ? ModulesTabHeight(b, w) : TrapsTabHeight(b, w);
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}  " + UiKit.Col(UiKit.MutedHex, $"Мк.{TowerModules.Roman(Mk)}"));

        // --- Что происходит (общее для обеих вкладок) ---
        float cy = y + UiKit.TitleH + 6f;
        GUI.Label(new Rect(x + 10f, cy, w - 20f, 20f), StatusLine, UiKit.Body);
        cy += 22f;
        UiKit.Bar(new Rect(x + 10f, cy, w - 20f, 10f), BuildFraction, UiKit.Accent);
        cy += 14f;
        if (!b.isWorking)
        {
            GUI.Label(new Rect(x + 10f, cy, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), UiKit.Small);
            cy += 22f;
        }

        string[] names = { "Модули", $"Ловушки ({TrapStock.Hall.Count})" };
        tab = UiKit.Tabs(new Rect(x + 10f, cy, w - 18f, 26f), names, tab);

        if (tab == 0) DrawModulesTab(x, y, w, b);
        else DrawTrapsTab(x, y, w, b);
        return new Rect(x, y, w + 5f, h);
    }

    // ----- Вкладка «Модули» -----

    const float ModRecipeH = 42f;   // название, цена (слева) + кнопка (справа)
    const string ModFoldId = "engineer-modules-info";

    string ModulesInfoText()
    {
        return "Заказ: золото, детали, сталь и доски списываются, инженер несёт их из Ратуши во двор. Готовый модуль лежит на складе двора. " +
               "Ставит модуль инженер: несёт его со двора к постройке. Механизм моста и подъёмник пушки ставятся из их панелей, там же «Заменить» и «Снять». " +
               $"Марка модуля равна уровню двора. Детали льёт Литейная. Собрано всего: {BuiltTotal}.";
    }

    float ModulesTabHeight(BuildingInstance b, float w)
    {
        float cy = TabsTop(b);
        cy += 22f;                                              // склад и детали
        cy += UiKit.SectionH + 2f;                              // «Сборка»
        cy += VisibleRecipeCount() * ModRecipeH;
        cy += 36f;                                              // быстрая установка
        cy += 32f;                                              // рабочие
        cy += FoldBlockH(ModFoldId, ModulesInfoText(), w);
        return cy + UpgradeRules.SectionHeight(b) + 8f;
    }

    // Чертежи, видные при текущем пути (Фейерверочный лук - только Пиротехнику)
    static int VisibleRecipeCount()
    {
        int n = 0;
        foreach (var r in TowerModules.Recipes) if (TowerModules.Available(r)) n++;
        return n;
    }

    void DrawModulesTab(float x, float y, float w, BuildingInstance b)
    {
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        var rm = ResourceManager.Instance;
        int parts = rm != null ? rm.parts : 0;
        float cy = y + TabsTop(b);

        string stCol = stock.Count >= Capacity ? UiKit.WarnHex : "#FFFFFF";
        Rect sr = new Rect(x + 10f, cy, w - 20f, 20f);
        GUI.Label(sr, $"Склад модулей: <color={stCol}><b>{stock.Count}/{Capacity}</b></color>  · детали: <b>{UiKit.Col(UiKit.AccentHex, parts.ToString())}</b>", small);
        UiKit.Tip(sr, "На складе: " + StockText() + ". Детали льёт Литейная.");
        cy += 22f;

        cy = UiKit.Section(x + 10f, cy, w - 20f, "Сборка") + 2f;
        bool firstOk = true;
        foreach (var r in TowerModules.Recipes)
        {
            if (!TowerModules.Available(r)) continue;
            Rect nr = new Rect(x + 10f, cy, w - 120f, 20f);
            GUI.Label(nr, $"<b>{r.name}</b>  " + UiKit.Col(UiKit.MutedHex, "→ " + r.target), st);
            UiKit.Tip(nr, $"{r.name} Мк.{TowerModules.Roman(Mk)} → на «{r.target}». {r.desc}.\n{TowerModules.Stats(r.kind, Mk)}\nВремя: {r.seconds:F0} с");

            var costParts = new List<string>();
            string s;
            if ((s = CostPart(r.gold, rm != null ? rm.gold : 0, "зол")) != "") costParts.Add(s);
            if ((s = CostPart(r.parts, parts, "дет")) != "") costParts.Add(s);
            if ((s = CostPart(r.steel, Have(RefinedKind.Steel), "стали")) != "") costParts.Add(s);
            if ((s = CostPart(r.planks, Have(RefinedKind.Planks), "досок")) != "") costParts.Add(s);
            if ((s = CostPart(r.resin, rm != null ? rm.resin : 0, "смолы")) != "") costParts.Add(s);
            GUI.Label(new Rect(x + 10f, cy + 20f, w - 120f, 18f), string.Join(UiKit.Col(UiKit.MutedHex, " · "), costParts.ToArray()), small);

            string p = BuildProblem(r);
            bool ok = p == "";
            if (UiKit.Button(new Rect(x + w - 104f, cy + 4f, 94f, 30f), "Собрать", ok, ok && firstOk ? UiKit.Kind.Primary : UiKit.Kind.Normal,
                    ok ? $"Собрать {r.name} Мк.{TowerModules.Roman(Mk)} ({r.seconds:F0} с)" : p))
                StartBuild(r);
            if (ok) firstOk = false;
            cy += ModRecipeH;
        }

        // Быстрая установка: на ближайшую башню без модуля
        TowerController bare = NearestBareTower();
        bool canQuick = bare != null && InstallProblem(bare, ModuleKind.SteamRepeller) == "";
        string quickWhy = canQuick ? "Поставить лучший паровой отбойник на ближайшую башню без модуля"
                        : (bare == null ? "Все башни уже с модулями (или башен нет)" : "Нет готовых отбойников");
        if (UiKit.Button(new Rect(x + 10f, cy, w - 20f, 30f), "Отбойник на башню", canQuick, UiKit.Kind.Normal, quickWhy))
            Install(bare, ModuleKind.SteamRepeller);
        cy += 36f;

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, cy, b);
        cy += 32f;

        DrawFoldBlock(x, cy, w, ModFoldId, ModulesInfoText());
        cy += FoldBlockH(ModFoldId, ModulesInfoText(), w);

        UpgradeRules.DrawUpgradeSection(x + 10f, cy, w - 20f, b);
    }

    // ----- Вкладка «Ловушки» -----

    const float TrapBaseRowH = 30f;               // по 2 основы в ряд
    const float TrapBaseDescH = 20f;
    const float TrapModRowH = 30f;                // по 3 добавки в ряд
    const float TrapResultH = 58f, TrapButtonH = 36f;
    const string TrapFoldId = "engineer-traps-info";

    static int TrapBaseRows => (VisibleTrapBases().Count + 1) / 2;

    // Основы, видные при текущем пути («Салют» - только Пиротехнику)
    static List<TrapRecipe> VisibleTrapBases()
    {
        var list = new List<TrapRecipe>();
        foreach (var r in Traps.Recipes) if (Traps.Available(r)) list.Add(r);
        return list;
    }
    static int TrapModRows => (Traps.Mods.Length + 2) / 3;

    string TrapsInfoText()
    {
        return "Рецепт = основа + добавки. Каждой основе нужна резина (её варит Литейная). Готовые ловушки кладут в запас Ратуши. " +
               "Ставятся дома в мирное время, в зелёной зоне (кнопка «Ловушки» [T]). Военное дело переделывает их в боевые в Кузне мастера - продайте на рынке. " +
               $"Инженеры одни: пока собирают ловушку, модуль ждёт, и наоборот. Собрано ловушек: {TrapsBuiltTotal}.";
    }

    float TrapsTabHeight(BuildingInstance b, float w)
    {
        float cy = TabsTop(b);
        cy += 22f;                                              // запас и резина
        cy += UiKit.SectionH + 2f + TrapBaseRows * TrapBaseRowH + TrapBaseDescH;
        cy += UiKit.SectionH + 2f + TrapModRows * TrapModRowH + 4f;
        cy += TrapResultH + TrapButtonH;
        cy += 32f;                                              // рабочие
        cy += FoldBlockH(TrapFoldId, TrapsInfoText(), w);
        return cy + UpgradeRules.SectionHeight(b) + 8f;
    }

    void DrawTrapsTab(float x, float y, float w, BuildingInstance b)
    {
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        var rm = ResourceManager.Instance;
        int rubber = rm != null ? rm.rubber : 0;
        int slots = Traps.ModSlots(b.level);

        // Добавки сверх лимита (двор понизили / выбрали на Ур.2) - лишние снимаем
        while (Traps.ModCount(pickMods) > slots)
            foreach (var m in Traps.Mods) if ((pickMods & m.mod) != 0) { pickMods &= ~m.mod; break; }

        float cy = y + TabsTop(b);
        string stCol = TrapStock.Full ? UiKit.WarnHex : "#FFFFFF";
        Rect ir = new Rect(x + 10f, cy, w - 20f, 20f);
        GUI.Label(ir, $"Ловушек в Ратуше: <color={stCol}><b>{TrapStock.Hall.Count}/{TrapStock.MaxStock}</b></color>  · резина: <b>{rubber}</b>", small);
        UiKit.Tip(ir, $"Обычных: {TrapStock.Count(false)}, боевых: {TrapStock.Count(true)}. Резину варит Литейная.");
        cy += 22f;

        // --- Основа ---
        cy = UiKit.Section(x + 10f, cy, w - 20f, "1. Основа") + 2f;
        float bw = (w - 24f) / 2f;
        if (!Traps.Available(pickBase)) pickBase = TrapBase.Jaw;   // путь сменили - «Салют» пропал
        var bases = VisibleTrapBases();
        for (int i = 0; i < bases.Count; i++)
        {
            TrapRecipe r = bases[i];
            bool locked = b.level < r.minLevel;
            bool sel = pickBase == r.type;
            string label = locked ? $"{r.name} " + UiKit.Col(UiKit.MutedHex, $"Ур.{r.minLevel}") : (sel ? "● " + r.name : r.name);
            Rect br = new Rect(x + 10f + (i % 2) * (bw + 4f), cy + (i / 2) * TrapBaseRowH, bw, TrapBaseRowH - 4f);
            if (UiKit.Button(br, label, sel ? UiKit.Kind.Normal : UiKit.Kind.Ghost,
                    r.desc + (locked ? $" Нужен двор Ур.{r.minLevel}." : "") + (r.type == TrapBase.Salute ? "\n" + SaluteTip : "")))
                pickBase = r.type;
        }
        cy += TrapBaseRows * TrapBaseRowH;
        GUI.Label(new Rect(x + 10f, cy, w - 20f, TrapBaseDescH), Traps.RecipeOf(pickBase).desc, muted);
        cy += TrapBaseDescH;

        // --- Добавки ---
        cy = UiKit.Section(x + 10f, cy, w - 20f, $"2. Добавки {Traps.ModCount(pickMods)}/{slots}") + 2f;
        if (slots < Traps.MaxMods) UiKit.Tip(new Rect(x + 10f, cy - UiKit.SectionH, w - 20f, UiKit.SectionH), $"С двором Ур.2 - до {Traps.MaxMods} добавок");
        float mw = (w - 28f) / 3f;
        for (int i = 0; i < Traps.Mods.Length; i++)
        {
            TrapModInfo m = Traps.Mods[i];
            bool on = (pickMods & m.mod) != 0;
            bool full = !on && Traps.ModCount(pickMods) >= slots;
            Rect mr = new Rect(x + 10f + (i % 3) * (mw + 4f), cy + (i / 3) * TrapModRowH, mw, TrapModRowH - 4f);
            GUIStyle mbs = new GUIStyle(UiKit.ButtonStyle(on ? UiKit.Kind.Normal : UiKit.Kind.Ghost)) { fontSize = 11, padding = new RectOffset(3, 3, 2, 2) };
            bool clicked = GUI.Button(mr, on ? "✓ " + m.name : m.name, mbs);
            UiKit.Tip(mr, m.desc + (full ? $" (добавок не больше {slots})" : ""));
            if (clicked)
            {
                if (on) pickMods &= ~m.mod;
                else if (!full) pickMods |= m.mod;
                else WarManager.Notify($"Добавок не больше {slots}" + (slots < Traps.MaxMods ? " - улучшите двор до Ур.2" : ""), false);
            }
        }
        cy += TrapModRows * TrapModRowH + 4f;

        // --- Итог ---
        TrapKit preview = new TrapKit { type = pickBase, mods = pickMods, mk = Mk };
        TrapCost cost = Traps.CostOf(pickBase, pickMods);
        float secs = Traps.SecondsOf(pickBase, pickMods);
        GUI.Label(new Rect(x + 10f, cy, w - 20f, 20f), $"<b>{preview.Name}</b>", st);
        GUI.Label(new Rect(x + 10f, cy + 20f, w - 20f, 18f), Traps.StatsText(preview.Stats, false), muted);
        GUI.Label(new Rect(x + 10f, cy + 38f, w - 20f, 18f), $"{Traps.CostText(cost)}  " + UiKit.Col(UiKit.MutedHex, $"· {secs:F0} с"), small);
        cy += TrapResultH;

        string p = TrapProblem(pickBase, pickMods);
        if (UiKit.Button(new Rect(x + 10f, cy, w - 20f, TrapButtonH - 4f), "Собрать ловушку", p == "", UiKit.Kind.Primary,
                p == "" ? $"Собрать: {preview.Name}. Готовая ляжет в запас Ратуши." : p))
            StartTrap(pickBase, pickMods);
        cy += TrapButtonH;

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, cy, b);
        cy += 32f;

        DrawFoldBlock(x, cy, w, TrapFoldId, TrapsInfoText());
        cy += FoldBlockH(TrapFoldId, TrapsInfoText(), w);

        UpgradeRules.DrawUpgradeSection(x + 10f, cy, w - 20f, b);
    }
}
