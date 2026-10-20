using UnityEngine;
using System.Collections.Generic;
using System.Text;

// ReinforcedPlates - укреплённые плиты: плита + сталь + камень (мастерская Ур.3)
// TarredBeams - смоляной брус: доски + смоляная пропитка (мастерская Ур.3)
// Monolith - монолитное основание: укреплённые плиты + пропитка + смоляной брус (мастерская Ур.3)
// Порядок важен: по номеру хранятся склады Хранилищ. Новые - только в конец.
public enum RefinedKind { Planks, StonePlates, Steel, ReinforcedPlates, TarredBeams, Monolith }

[System.Serializable]
public struct Refined
{
    public RefinedKind kind;
    public int amount;
}

public class UpgradeStep
{
    public QualityTier minTier;   // материалы не хуже этого качества
    public Refined[] items;
}

/// <summary>
/// Правила улучшений и постройки "копий" зданий.
///  1. Улучшение до Ур.2 и Ур.3: обычные ресурсы + доски/плиты из мастерской.
///     На Ур.2 подходит любое качество, на Ур.3 - не хуже "Хорошего".
///  2. Второе и следующие здания одного типа (например, вторая казарма) требуют доски/плиты.
/// Материалы списываются автоматически: сначала самого низкого подходящего качества.
/// Все числа - в таблицах ниже, их можно спокойно менять.
/// </summary>
public static class UpgradeRules
{
    public const int MaxLevel = 3;
    public const int FreeCopies = 1;   // сколько зданий одного типа можно построить без материалов

    // Казарма растёт до Ур.4 (конница), остальные здания - до Ур.3
    public const int BarracksMaxLevel = 4;
    public static int MaxLevelFor(BuildingInstance b)
    {
        if (b == null || b.type == null) return MaxLevel;
        if (b.type.name == BarracksController.TypeName) return BarracksMaxLevel;
        if (b.type.name == BuildingInstance.BridgeName) return 1;
        if (b.type.name == BuildingInstance.SawmillName) return BuildingInstance.LeavesFromLevel;   // Ур.4 - листва   // мост не улучшается (раздвижной - модуль Инженерного двора)
        return MaxLevel;
    }

    static Refined P(int a) { return new Refined { kind = RefinedKind.Planks, amount = a }; }
    static Refined S(int a) { return new Refined { kind = RefinedKind.StonePlates, amount = a }; }
    static Refined St(int a) { return new Refined { kind = RefinedKind.Steel, amount = a }; }
    static Refined RP(int a) { return new Refined { kind = RefinedKind.ReinforcedPlates, amount = a }; }
    static Refined TB(int a) { return new Refined { kind = RefinedKind.TarredBeams, amount = a }; }
    static Refined M(int a) { return new Refined { kind = RefinedKind.Monolith, amount = a }; }
    static UpgradeStep Step(QualityTier min, params Refined[] items) { return new UpgradeStep { minTier = min, items = items }; }

    // Материалы для улучшения. Первый элемент - до Ур.2, второй - до Ур.3.
    static readonly Dictionary<string, UpgradeStep[]> Upgrades = new Dictionary<string, UpgradeStep[]>
    {
        // Ратуша: растёт зелёная зона застройки (см. Territory)
        { "Ратуша",     new[] { Step(QualityTier.Medium, P(10), S(10)), Step(QualityTier.Good, P(16), S(16), St(8)) } },
        // Аванпост: растёт радиус зоны
        // Ур.3 - укреплённый аванпост: укреплённые плиты, зато смотрители носят еду с ферм рядом
        { OutpostController.TypeName, new[] { Step(QualityTier.Medium, P(8), S(8)), Step(QualityTier.Good, P(14), S(10), St(5), RP(6)) } },
        { BuildingInstance.WallName, new[] { Step(QualityTier.Medium, S(2)), Step(QualityTier.Good, S(4)) } },
        // Ур.4 лесопилки - листва (накидки Мастера маскировки и др.)
        { "Лесопилка",  new[] { Step(QualityTier.Medium, P(6)),        Step(QualityTier.Good, P(12)), Step(QualityTier.Good, P(16), S(6), St(2)) } },
        { "Шахта",      new[] { Step(QualityTier.Medium, S(6)),        Step(QualityTier.Good, S(12)) } },
        { "Ферма",      new[] { Step(QualityTier.Medium, P(8)),        Step(QualityTier.Good, P(14)) } },
        { "Жилой дом",  new[] { Step(QualityTier.Medium, P(8)),        Step(QualityTier.Good, P(14), S(8)) } },
        { "Мастерская", new[] { Step(QualityTier.Medium, P(6), S(6)),  Step(QualityTier.Good, P(10), S(10)) } },
        // Ур.3 казармы - сталь: больше мест и полевой маг-целитель (см. BarracksController)
        { BarracksController.TypeName, new[] { Step(QualityTier.Medium, P(8), S(8)),  Step(QualityTier.Good, P(14), S(14), St(8)),
                        // Ур.4 - конница (нужны лошади из Конюшни, см. BarracksController)
                        Step(QualityTier.Good, P(20), S(16), St(16), RP(8)) } },
        { "Башня",      new[] { Step(QualityTier.Medium, S(8)),        Step(QualityTier.Good, S(12), RP(4)) } },
        { HealerHouse.TypeName, new[] { Step(QualityTier.Medium, P(10), S(8)), Step(QualityTier.Good, P(14), S(12)) } },
        // Ур.3 пушки - сталь
        { MagicCannonController.TypeName, new[] { Step(QualityTier.Medium, S(10), P(8)), Step(QualityTier.Good, S(16), P(12), St(6)) } },
        // Пушка: ствол из стали - и улучшения со сталью
        { GroundCannonController.TypeName, new[] { Step(QualityTier.Medium, S(10), P(6), St(6)), Step(QualityTier.Good, S(14), P(8), St(12), RP(4)) } },
        // Магическая оружейная (Магия): пачки следующей марки, больше полок, быстрее зачаровка
        { MagicArmory.TypeName, new[] { Step(QualityTier.Medium, P(8), S(8), St(2)), Step(QualityTier.Good, P(12), S(12), St(6)) } },
        // Алтарь жизни (Магия жизни): голем крепче и сильнее (+12% за уровень)
        { LifeAltar.TypeName, new[] { Step(QualityTier.Medium, S(10), P(6)), Step(QualityTier.Good, S(16), P(10), St(4)) } },
        // Курьерская: и улучшения требуют сталь
        { CourierHouse.TypeName, new[] { Step(QualityTier.Medium, P(10), S(8), St(4)), Step(QualityTier.Good, P(16), S(12), St(8)) } },
        // Хранилище: больше места
        { WarehouseController.TypeName, new[] { Step(QualityTier.Medium, P(8), S(4)), Step(QualityTier.Good, P(14), S(10)) } },
        // Оружейная: лучше качество оружия, больше склад, доступны улучшения оружия за золото
        { ArmoryController.TypeName, new[] { Step(QualityTier.Medium, P(10), S(10), St(6)), Step(QualityTier.Good, P(16), S(16), St(12)) } },
        // Конюшня: больше лошадей и быстрее растут
        { StableController.TypeName, new[] { Step(QualityTier.Medium, P(8), S(4)), Step(QualityTier.Good, P(14), S(10)) } },
        // Станция собирателей: больше собирателей, больше ягод за поход, дальше ходят
        { BerryStation.TypeName, new[] { Step(QualityTier.Medium, P(6)), Step(QualityTier.Good, P(10), S(4)) } },
        // Кухня: больше поваров, быстрее варка, больше похлёбки хранится
        { KitchenController.TypeName, new[] { Step(QualityTier.Medium, P(8), S(6)), Step(QualityTier.Good, P(14), S(10), St(4)) } },
        // Обозный двор: больше рабочих и быстрее обоз (60 / 45 / 30 с за кадром)
        { WagonYard.TypeName, new[] { Step(QualityTier.Medium, P(10), S(8)), Step(QualityTier.Good, P(16), S(12), St(4)) } },
        // Зельеварня (Магия): больше полок под зелья и быстрее варка
        { PotionHouse.TypeName, new[] { Step(QualityTier.Medium, P(6), S(6)), Step(QualityTier.Good, P(10), S(8), St(2)) } },
        // Кузня мастера (Война): доводка даёт больше
        { WeaponForge.TypeName, new[] { Step(QualityTier.Medium, P(6), S(4), St(4)), Step(QualityTier.Good, P(10), S(8), St(8), RP(2)) } },
        // Литейная (Инженерия): котлы прочнее - греются медленнее, плавят быстрее, реже горят; Ур.2 - «Большая плавка»
        { FoundryController.TypeName, new[] { Step(QualityTier.Medium, P(6), S(10), St(4)), Step(QualityTier.Good, P(10), S(14), St(8), RP(4)) } },
        // Инженерный двор (Инженерия): модули следующей марки (Мк.II / Мк.III), больше склад, быстрее сборка
        { EngineerYard.TypeName, new[] { Step(QualityTier.Medium, P(12), S(10), St(6)), Step(QualityTier.Good, P(18), S(16), St(10), RP(4)) } },
        // Торговый дом: больше лавок и лотов, ниже комиссия, больше заказов гильдии, купцы быстрее
        { MarketHouse.TypeName, new[] { Step(QualityTier.Medium, P(12), S(10)), Step(QualityTier.Good, P(18), S(14), St(6)) } },
        // Гостевой дом: больше мест для наёмников (6 / 12 / 18)
        { BuildingInstance.GuestHouseName, new[] { Step(QualityTier.Medium, P(8), S(4)), Step(QualityTier.Good, P(12), S(8)) } },
        // Инженерная казарма (Военный инженер): больше мест под катапульты (8 / 16 / 24); ещё и детали (UpgradeParts)
        { EngineerBarracks.TypeName, new[] { Step(QualityTier.Medium, P(10), S(6), St(4)), Step(QualityTier.Good, P(16), S(10), St(8), RP(4)) } },
        // Завод (Индустриализатор): плиты, сталь; ещё и детали (UpgradeParts)
        { IndustrialPlant.TypeName, new[] { Step(QualityTier.Medium, S(10), St(6), P(4)), Step(QualityTier.Good, S(16), St(10), RP(4)) } },
        // Двор военного строителя (Военный строитель): больше строителей, чинят быстрее, шире зона, больше отметок
        { WarBuilderYard.TypeName, new[] { Step(QualityTier.Medium, P(12), S(6), St(2)), Step(QualityTier.Good, P(18), S(10), St(6), TB(4)) } },
    };

    // Детали из Литейной (ResourceManager.parts) для улучшения: первый элемент - до Ур.2, второй - до Ур.3
    static readonly Dictionary<string, int[]> UpgradeParts = new Dictionary<string, int[]>
    {
        { EngineerBarracks.TypeName, new[] { 6, 12 } },
        { IndustrialPlant.TypeName, new[] { 8, 14 } },
    };

    /// <summary>Сколько деталей нужно для улучшения до targetLevel (0 - не нужны).</summary>
    public static int PartsFor(string typeName, int targetLevel)
    {
        if (typeName == null || !UpgradeParts.TryGetValue(typeName, out var arr)) return 0;
        int i = targetLevel - 2;
        return i >= 0 && i < arr.Length ? arr[i] : 0;
    }

    // Здания, которым доски/плиты нужны уже для ПЕРВОЙ постройки.
    // 2-е здание стоит вдвое больше материалов, 3-е - втрое и т.д.
    static readonly Dictionary<string, UpgradeStep> BaseBuildRules = new Dictionary<string, UpgradeStep>
    {
        { HealerHouse.TypeName, Step(QualityTier.Medium, P(8), S(6)) },
        { MagicCannonController.TypeName, Step(QualityTier.Medium, S(10), P(6)) },
        { GroundCannonController.TypeName, Step(QualityTier.Medium, S(8), P(4), St(6)) },
        { MagicArmory.TypeName, Step(QualityTier.Medium, P(6), S(6)) },
        { LifeAltar.TypeName, Step(QualityTier.Medium, S(8), P(4)) },
        // Курьерская: уже первая требует доски, плиты и сталь (2-я - вдвое больше и т.д.)
        { CourierHouse.TypeName, Step(QualityTier.Medium, P(10), S(8), St(5)) },
        // Оружейная: уже первая требует доски и плиты (2-я - вдвое больше и т.д.)
        { ArmoryController.TypeName, Step(QualityTier.Medium, P(8), S(8)) },
        // Аванпост: дорогое здание - уже первый требует доски и плиты
        { OutpostController.TypeName, Step(QualityTier.Medium, P(10), S(10)) },
        // Кухня: печь и котлы - уже первая требует доски и плиты
        { KitchenController.TypeName, Step(QualityTier.Medium, P(6), S(4)) },
        { PotionHouse.TypeName, Step(QualityTier.Medium, P(6)) },
        { WeaponForge.TypeName, Step(QualityTier.Medium, P(4), St(4)) },
        { FoundryController.TypeName, Step(QualityTier.Medium, P(4), S(8)) },
        { EngineerYard.TypeName, Step(QualityTier.Medium, P(10), S(8), St(4)) },
        // Торговый дом: уже первый требует доски, плиты, смоляной брус и монолит (строится только один)
        { MarketHouse.TypeName, Step(QualityTier.Medium, P(10), S(8), TB(4), M(2)) },
        // Обозный двор: тяжёлые склады и навесы для фургонов - смоляной брус и монолитное основание уже для первого
        { WagonYard.TypeName, Step(QualityTier.Medium, P(10), S(8), TB(6), M(2)) },
        // Инженерная казарма: мастерская для сборки катапульт - доски и сталь уже для первой
        { EngineerBarracks.TypeName, Step(QualityTier.Medium, P(8), St(4)) },
        // Завод: тяжёлое основание и сталь уже для первого
        { IndustrialPlant.TypeName, Step(QualityTier.Medium, S(10), St(6)) },
        // Двор военного строителя: леса, козлы и инструмент - доски уже для первого
        { WarBuilderYard.TypeName, Step(QualityTier.Medium, P(10), S(4)) },
    };

    // Материалы, которые нужны КАЖДОЙ постройке одинаково (не растут с числом зданий): брусчатка - плитка на клетку
    static readonly Dictionary<string, UpgradeStep> FlatBuildRules = new Dictionary<string, UpgradeStep>
    {
        { BuildingInstance.CobbleName, Step(QualityTier.Medium, S(1)) },
        // Мост: доска на каждую клетку настила
        { BuildingInstance.BridgeName, Step(QualityTier.Medium, P(1)) },
    };

    // Материалы для постройки 2-го, 3-го... здания этого типа.
    // Количество умножается на (номер здания - 1): 2-я казарма = 1x, 3-я = 2x и т.д.
    static readonly Dictionary<string, UpgradeStep> CopyRules = new Dictionary<string, UpgradeStep>
    {
        { BarracksController.TypeName, Step(QualityTier.Medium, P(10), S(5)) },
        { "Башня",      Step(QualityTier.Medium, S(8)) },
        { "Мастерская", Step(QualityTier.Medium, P(6), S(6)) },
        { WarehouseController.TypeName, Step(QualityTier.Medium, P(8), S(4)) },
        { StableController.TypeName, Step(QualityTier.Medium, P(8), S(4)) },
        { BerryStation.TypeName, Step(QualityTier.Medium, P(4)) },
    };

    // ---------- Общие мелочи ----------

    public static string KindLabel(RefinedKind k)
    {
        switch (k)
        {
            case RefinedKind.Planks: return "Доски";
            case RefinedKind.StonePlates: return "Плиты";
            case RefinedKind.ReinforcedPlates: return "Укреплённые плиты";
            case RefinedKind.TarredBeams: return "Смоляной брус";
            case RefinedKind.Monolith: return "Монолитное основание";
            default: return "Сталь";
        }
    }

    static string QualityNote(QualityTier min)
    {
        return min == QualityTier.Medium ? "любое качество" : TownHallStorage.TierName(min).ToLower() + " и лучше";
    }

    public static UpgradeStep StepFor(string typeName, int targetLevel)
    {
        if (typeName != null && Upgrades.TryGetValue(typeName, out var arr))
        {
            int i = targetLevel - 2;
            if (i >= 0 && i < arr.Length) return arr[i];
        }
        return null;
    }

    public static void BaseCost(BuildingInstance b, out int g, out int w, out int s, out int f)
    {
        g = b.type.costGold * b.level;
        w = b.type.costWood * b.level;
        s = b.type.costStone * b.level;
        f = b.type.costFood * b.level;
    }

    static string FormatBase(int g, int w, int s, int f)
    {
        List<string> parts = new List<string>();
        if (g > 0) parts.Add(g + "г");
        if (w > 0) parts.Add(w + "д");
        if (s > 0) parts.Add(s + "к");
        if (f > 0) parts.Add(f + "е");
        return parts.Count > 0 ? string.Join(" ", parts) : "бесплатно";
    }

    // Сколько материала не хуже заданного качества: на складе Ратуши и во всех Хранилищах
    public static int CountAtLeast(TownHallStorage st, RefinedKind kind, QualityTier min)
    {
        int total = 0;
        foreach (QualityTier t in System.Enum.GetValues(typeof(QualityTier)))
            if (t >= min) total += st.Get(kind, t) + WarehouseController.TotalStored(kind, t);
        return total;
    }

    // source - Хранилище, откуда взяли (null - из Ратуши): оттуда житель и понесёт материалы
    public struct Spent { public RefinedKind kind; public QualityTier tier; public int amount; public BuildingInstance source; }

    // Списывает материал: сначала самого низкого подходящего качества, чтобы беречь лучшие.
    // В каждом качестве сначала берётся склад Ратуши, потом Хранилища.
    // log (если задан) запоминает, что именно и какого качества списали - чтобы вернуть при сорванном рейсе.
    public static void SpendAtLeast(TownHallStorage st, RefinedKind kind, QualityTier min, int amount, List<Spent> log = null)
    {
        int left = amount;
        foreach (QualityTier t in System.Enum.GetValues(typeof(QualityTier)))
        {
            if (t < min || left <= 0) continue;

            int take = Mathf.Min(left, st.Get(kind, t));
            if (take > 0 && st.TrySpend(kind, t, take))
            {
                left -= take;
                if (log != null) log.Add(new Spent { kind = kind, tier = t, amount = take });
            }

            if (left > 0)
            {
                int fromWh = WarehouseController.Take(kind, t, left, out BuildingInstance src);
                if (fromWh > 0)
                {
                    left -= fromWh;
                    if (log != null) log.Add(new Spent { kind = kind, tier = t, amount = fromWh, source = src });
                }
            }
        }
    }

    public static void RefundSpent(List<Spent> log)
    {
        TownHallStorage st = TownHallStorage.Instance;
        if (st == null || log == null) return;
        foreach (var sp in log)
        {
            st.Add(sp.kind, sp.tier, sp.amount);
        }
    }

    // ---------- Улучшение ----------

    public static bool CanUpgrade(BuildingInstance b)
    {
        if (b == null || b.type == null || b.level >= MaxLevelFor(b)) return false;
        if (b.upgradeInProgress) return false; // материалы уже несут
        if (b.IsRuined) return false;          // руины не улучшают

        ResourceManager rm = ResourceManager.Instance;
        if (rm == null) return false;

        BaseCost(b, out int g, out int w, out int s, out int f);
        if (!rm.HasEnough(g, w, s, f)) return false;
        if (rm.parts < PartsFor(b.type.name, b.level + 1)) return false;

        UpgradeStep step = StepFor(b.type.name, b.level + 1);
        if (step == null) return true; // для этого здания материалы не нужны

        TownHallStorage st = TownHallStorage.Instance;
        if (st == null) return false;
        foreach (var it in step.items)
            if (CountAtLeast(st, it.kind, step.minTier) < it.amount) return false;

        return true;
    }

    /// <summary>
    /// Заказывает улучшение: деньги и материалы списываются сразу, а уровень вырастет, когда житель
    /// (рабочий этого здания, иначе курьер Ратуши, иначе любой свободный) принесёт материалы из Ратуши.
    /// </summary>
    public static bool TryUpgrade(BuildingInstance b)
    {
        if (!CanUpgrade(b)) return false;

        BaseCost(b, out int g, out int w, out int s, out int f);
        int partsNeed = PartsFor(b.type.name, b.level + 1);
        if (ResourceManager.Instance.parts < partsNeed) return false;
        if (!ResourceManager.Instance.Spend(g, w, s, f)) return false;
        if (partsNeed > 0) ResourceManager.Instance.SpendParts(partsNeed);

        List<Spent> spent = new List<Spent>();
        UpgradeStep step = StepFor(b.type.name, b.level + 1);
        if (step != null)
            foreach (var it in step.items)
                SpendAtLeast(TownHallStorage.Instance, it.kind, step.minTier, it.amount, spent);

        int target = b.level + 1;
        b.upgradeInProgress = true;

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Материалы брали в Хранилище - житель идёт за ними туда, а не в Ратушу
        BuildingInstance pickup = hall;
        foreach (var sp in spent)
            if (sp.source != null) { pickup = sp.source; break; }

        Logistics.Request(new HaulJob
        {
            from = pickup,
            to = b,
            prefer = b,
            onDeliver = () =>
            {
                if (b == null) { Refund(g, w, s, f, spent); RefundParts(partsNeed); return; }
                b.level = Mathf.Max(b.level, target);
                b.upgradeInProgress = false;
                if (!b.IsRuined) b.hp = b.MaxHp; // новый уровень - здание как новое
                Territory.MarkDirty();           // выросла Ратуша / Аванпост - зона застройки больше
            },
            onAbort = loaded =>
            {
                Refund(g, w, s, f, spent);
                RefundParts(partsNeed);
                if (b != null) b.upgradeInProgress = false;
            }
        });
        return true;
    }

    static void RefundParts(int n)
    {
        if (n > 0 && ResourceManager.Instance != null) ResourceManager.Instance.AddParts(n);
    }

    static void Refund(int g, int w, int s, int f, List<Spent> spent)
    {
        if (ResourceManager.Instance != null) ResourceManager.Instance.Add(g, w, s, f);
        RefundSpent(spent);
    }

    // ---------- Постройка 2-го, 3-го... здания ----------

    public static Refined[] BuildExtra(string typeName, int existingCount, out QualityTier minTier)
    {
        minTier = QualityTier.Medium;
        if (typeName == null) return null;

        if (FlatBuildRules.TryGetValue(typeName, out var flat))
        {
            minTier = flat.minTier;
            return (Refined[])flat.items.Clone();
        }

        int copyNumber = existingCount + 1;
        int mult;
        if (BaseBuildRules.TryGetValue(typeName, out var rule)) mult = copyNumber;   // материалы нужны всегда
        else if (CopyRules.TryGetValue(typeName, out rule)) mult = copyNumber - FreeCopies;
        else return null;
        if (mult <= 0) return null;

        minTier = rule.minTier;
        Refined[] res = new Refined[rule.items.Length];
        for (int i = 0; i < res.Length; i++)
            res[i] = new Refined { kind = rule.items[i].kind, amount = rule.items[i].amount * mult };
        return res;
    }

    public static bool CanPayBuild(string typeName, int existingCount)
    {
        Refined[] items = BuildExtra(typeName, existingCount, out QualityTier min);
        if (items == null) return true;

        TownHallStorage st = TownHallStorage.Instance;
        if (st == null) return false;
        foreach (var it in items)
            if (CountAtLeast(st, it.kind, min) < it.amount) return false;
        return true;
    }

    public static void PayBuild(string typeName, int existingCount)
    {
        Refined[] items = BuildExtra(typeName, existingCount, out QualityTier min);
        if (items == null) return;

        TownHallStorage st = TownHallStorage.Instance;
        if (st == null) return;
        foreach (var it in items)
            SpendAtLeast(st, it.kind, min, it.amount);
    }

    /// <summary>
    /// Короткая строка для карточки в окне построек: "Здание №2: Плиты 3/8".
    /// Пусто, если доски/плиты для этого здания не нужны. ok = хватает ли их на складе.
    /// </summary>
    public static string ShortBuildNeeds(string typeName, int existingCount, out bool ok)
    {
        ok = true;
        Refined[] items = BuildExtra(typeName, existingCount, out QualityTier min);
        if (items == null) return "";

        TownHallStorage st = TownHallStorage.Instance;
        List<string> parts = new List<string>();
        foreach (var it in items)
        {
            int have = st != null ? CountAtLeast(st, it.kind, min) : 0;
            if (have < it.amount) ok = false;
            string col = have >= it.amount ? UiKit.GoodHex : UiKit.BadHex;
            parts.Add($"{KindLabel(it.kind)} <color={col}>{have}/{it.amount}</color>");
        }

        string quality = min == QualityTier.Medium ? "" : $" ({QualityNote(min)})";
        string prefix = FlatBuildRules.ContainsKey(typeName) ? "На клетку: " : $"Здание №{existingCount + 1}: ";
        return prefix + string.Join(", ", parts) + quality;
    }

    public static string DescribeBuild(string typeName, int existingCount)
    {
        Refined[] items = BuildExtra(typeName, existingCount, out QualityTier min);
        if (items == null) return "";

        TownHallStorage st = TownHallStorage.Instance;
        StringBuilder sb = new StringBuilder();
        sb.Append($"<b>Здание №{existingCount + 1}: нужны материалы</b>");
        foreach (var it in items)
        {
            int have = st != null ? CountAtLeast(st, it.kind, min) : 0;
            string col = have >= it.amount ? UiKit.GoodHex : UiKit.BadHex;
            sb.Append($"\n {KindLabel(it.kind)} ({QualityNote(min)}): <color={col}>{have}/{it.amount}</color>");
        }
        return sb.ToString();
    }

    // ---------- Панель улучшения (рисуется внутри панелей зданий) ----------

    // ---------- Укрепление: смоляной брус + монолитное основание ----------
    // Здание один раз укрепляют: +50% прочности навсегда (и после восстановления из руин).
    // Цена растёт с размером здания. Материалы несут из Ратуши, как при улучшении.

    public const float FortifyHpMul = 1.5f;

    public static bool CanBeFortified(BuildingInstance b)
    {
        return b != null && b.type != null && b.IsDamageable && !b.IsRoad;
    }

    public static void FortifyCost(BuildingInstance b, out int beams, out int monolith, out int gold)
    {
        int cells = b != null && b.type != null ? Mathf.Max(1, b.type.size.x * b.type.size.y) : 1;
        beams = Mathf.Max(1, cells / 2);
        monolith = cells >= 4 ? Mathf.Max(1, cells / 4) : 0;
        gold = 10 * cells;
    }

    static bool ShowFortify(BuildingInstance b)
    {
        if (!CanBeFortified(b)) return false;
        if (b.fortified || b.fortifyInProgress) return true;
        var st = TownHallStorage.Instance;
        return st != null && CountAtLeast(st, RefinedKind.TarredBeams, QualityTier.Medium) > 0;
    }

    public static string FortifyProblem(BuildingInstance b)
    {
        if (!CanBeFortified(b)) return "Это не укрепить";
        if (b.fortified) return "Уже укреплено";
        if (b.fortifyInProgress) return "Материалы несут...";
        if (b.IsRuined) return "Сначала восстановите";
        FortifyCost(b, out int beams, out int mono, out int gold);
        var rm = ResourceManager.Instance;
        var st = TownHallStorage.Instance;
        if (rm == null || st == null) return "Нет склада";
        if (rm.gold < gold) return $"Нужно {gold} золота";
        if (CountAtLeast(st, RefinedKind.TarredBeams, QualityTier.Medium) < beams) return $"Нужно смоляного бруса: {beams}";
        if (CountAtLeast(st, RefinedKind.Monolith, QualityTier.Medium) < mono) return $"Нужно монолита: {mono}";
        return "";
    }

    public static bool TryFortify(BuildingInstance b)
    {
        if (FortifyProblem(b) != "") return false;
        FortifyCost(b, out int beams, out int mono, out int gold);
        if (!ResourceManager.Instance.Spend(gold, 0, 0, 0)) return false;
        List<Spent> spent = new List<Spent>();
        SpendAtLeast(TownHallStorage.Instance, RefinedKind.TarredBeams, QualityTier.Medium, beams, spent);
        if (mono > 0) SpendAtLeast(TownHallStorage.Instance, RefinedKind.Monolith, QualityTier.Medium, mono, spent);
        b.fortifyInProgress = true;

        var pm = PopulationManager.Instance;
        BuildingInstance pickup = pm != null ? pm.Hall : null;
        foreach (var sp in spent)
            if (sp.source != null) { pickup = sp.source; break; }

        Logistics.Request(new HaulJob
        {
            from = pickup,
            to = b,
            prefer = b,
            onDeliver = () =>
            {
                if (b == null) { Refund(gold, 0, 0, 0, spent); return; }
                float frac = b.MaxHp > 0f ? b.Hp / b.MaxHp : 1f;
                b.fortified = true;
                b.fortifyInProgress = false;
                if (!b.IsRuined) b.hp = b.MaxHp * Mathf.Max(frac, 0.5f);
                WarManager.Notify($"{b.type.name} укреплено: прочность +{(FortifyHpMul - 1f) * 100f:F0}%", true);
            },
            onAbort = loaded =>
            {
                Refund(gold, 0, 0, 0, spent);
                if (b != null) b.fortifyInProgress = false;
            }
        });
        return true;
    }

    static float FortifyHeight(BuildingInstance b) { return ShowFortify(b) ? 30f : 0f; }

    // Короткие названия материалов для строки цены
    static string ShortKind(RefinedKind k)
    {
        switch (k)
        {
            case RefinedKind.Planks: return "Доски";
            case RefinedKind.StonePlates: return "Плиты";
            case RefinedKind.ReinforcedPlates: return "Укр. плиты";
            case RefinedKind.TarredBeams: return "Брус";
            case RefinedKind.Monolith: return "Монолит";
            default: return "Сталь";
        }
    }

    // Цена обычными ресурсами: цвета ресурсов, нехватка - красным
    static string BaseCostLine(int g, int w, int s, int f)
    {
        var rm = ResourceManager.Instance;
        List<string> parts = new List<string>();
        AddRes(parts, g, rm != null ? rm.gold : 0, "зол", UiKit.GoldHex);
        AddRes(parts, w, rm != null ? rm.wood : 0, "дер", UiKit.WoodHex);
        AddRes(parts, s, rm != null ? rm.stone : 0, "кам", UiKit.StoneHex);
        AddRes(parts, f, rm != null ? rm.food : 0, "еды", UiKit.FoodHex);
        return parts.Count > 0 ? string.Join(UiKit.Col(UiKit.MutedHex, " · "), parts) : UiKit.Col(UiKit.MutedHex, "бесплатно");
    }

    static void AddRes(List<string> parts, int need, int have, string what, string hex)
    {
        if (need <= 0) return;
        parts.Add(UiKit.Col(have >= need ? hex : UiKit.BadHex, need + " " + what));
    }

    // Почему нельзя улучшить ("" - можно)
    static string UpgradeProblem(BuildingInstance b)
    {
        if (b.IsRuined) return "Сначала восстановите здание";
        ResourceManager rm = ResourceManager.Instance;
        if (rm == null) return "Нет казны";
        BaseCost(b, out int g, out int w, out int s, out int f);
        List<string> lack = new List<string>();
        if (rm.gold < g) lack.Add($"{g - rm.gold} золота");
        if (rm.wood < w) lack.Add($"{w - rm.wood} дерева");
        if (rm.stone < s) lack.Add($"{s - rm.stone} камня");
        if (rm.food < f) lack.Add($"{f - rm.food} еды");
        int pn = PartsFor(b.type.name, b.level + 1);
        if (rm.parts < pn) lack.Add($"{pn - rm.parts} деталей");

        UpgradeStep step = StepFor(b.type.name, b.level + 1);
        TownHallStorage st = TownHallStorage.Instance;
        if (step != null)
        {
            foreach (var it in step.items)
            {
                int have = st != null ? CountAtLeast(st, it.kind, step.minTier) : 0;
                if (have < it.amount) lack.Add($"{KindLabel(it.kind).ToLower()} ({QualityNote(step.minTier)}): {it.amount - have}");
            }
        }
        return lack.Count > 0 ? "Не хватает: " + string.Join(", ", lack) : "";
    }

    static void DrawFortifyRow(float x, float y, float w, BuildingInstance b)
    {
        if (b.fortified)
        {
            Rect fr = new Rect(x, y + 4f, w, 22f);
            GUI.Label(fr, UiKit.Col("#D8A060", "■ Укреплено") + UiKit.Col(UiKit.MutedHex, $"  прочность ×{FortifyHpMul:F1}"), UiKit.MutedStyle);
            UiKit.Tip(fr, "Здание укреплено смоляным брусом: прочность выше навсегда (и после восстановления из руин).");
            return;
        }
        FortifyCost(b, out int beams, out int mono, out int gold);
        string p = FortifyProblem(b);
        string cost = $"{gold} зол, смоляной брус: {beams}" + (mono > 0 ? $", монолит: {mono}" : "");
        string tip = $"Прочность +{(FortifyHpMul - 1f) * 100f:F0}% навсегда.\nЦена: {cost}." + (p != "" ? "\n" + UiKit.Col(UiKit.BadHex, p) : "");
        string label = b.fortifyInProgress ? "Укрепление: несут..." : $"Укрепить (+{(FortifyHpMul - 1f) * 100f:F0}%)";
        if (UiKit.Button(new Rect(x, y, w, 26f), label, p == "", UiKit.Kind.Normal, tip)) TryFortify(b);
    }

    public static float SectionHeight(BuildingInstance b)
    {
        return CoreSectionHeight(b) + FortifyHeight(b);
    }

    public static float DrawUpgradeSection(float x, float y, float w, BuildingInstance b)
    {
        float core = DrawCoreSection(x, y, w, b);
        float f = FortifyHeight(b);
        if (f > 0f) DrawFortifyRow(x, y + core, w, b);
        return core + f;
    }

    // Компактный блок: строка цены, строка материалов (если нужны), кнопка «Улучшить»
    const float CostRowH = 22f, MatRowH = 18f, UpBtnH = 28f, UpGap = 6f;

    static float CoreSectionHeight(BuildingInstance b)
    {
        if (b == null || b.type == null) return 0f;
        if (b.level >= MaxLevelFor(b)) return 24f;
        if (b.upgradeInProgress) return 24f;

        UpgradeStep step = StepFor(b.type.name, b.level + 1);
        bool mats = (step != null && step.items != null && step.items.Length > 0) || PartsFor(b.type.name, b.level + 1) > 0;
        return CostRowH + (mats ? MatRowH : 0f) + UpBtnH + UpGap;
    }

    static float DrawCoreSection(float x, float y, float w, BuildingInstance b)
    {
        float total = CoreSectionHeight(b);
        if (total <= 0f) return 0f;

        if (b.level >= MaxLevelFor(b))
        {
            GUI.Label(new Rect(x, y, w, 22f), UiKit.Col(UiKit.GoodHex, "★") + UiKit.Col(UiKit.MutedHex, $" Максимальный уровень (Ур. {b.level})"), UiKit.MutedStyle);
            return total;
        }

        if (b.upgradeInProgress)
        {
            Rect pr = new Rect(x, y, w, 22f);
            GUI.Label(pr, UiKit.Col(UiKit.InfoHex, $"Улучшение до Ур. {b.level + 1}: несут материалы..."), UiKit.MutedStyle);
            UiKit.Tip(pr, "Уровень вырастет, когда житель донесёт материалы до здания.");
            return total;
        }

        int target = b.level + 1;
        UpgradeStep step = StepFor(b.type.name, target);
        TownHallStorage storage = TownHallStorage.Instance;
        float cy = y;

        // Обычные ресурсы - одной строкой
        BaseCost(b, out int g, out int wd, out int s, out int f);
        GUIStyle line = new GUIStyle(UiKit.Body) { wordWrap = false, clipping = TextClipping.Clip };
        GUI.Label(new Rect(x, cy, w, CostRowH), UiKit.Col(UiKit.MutedHex, $"До Ур. {target}:  ") + BaseCostLine(g, wd, s, f), line);
        cy += CostRowH;

        // Доски / плиты / сталь (и детали) - короткой строкой, качество - в подсказке
        int partsNeed = PartsFor(b.type.name, target);
        if ((step != null && step.items != null && step.items.Length > 0) || partsNeed > 0)
        {
            List<string> parts = new List<string>();
            StringBuilder tip = new StringBuilder();
            QualityTier minT = step != null ? step.minTier : QualityTier.Medium;
            tip.Append($"Материалы со склада Ратуши и Хранилищ ({QualityNote(minT)}):");
            if (partsNeed > 0)
            {
                int haveP = ResourceManager.Instance != null ? ResourceManager.Instance.parts : 0;
                parts.Add($"Детали {UiKit.Col(haveP >= partsNeed ? UiKit.GoodHex : UiKit.BadHex, haveP + "/" + partsNeed)}");
                tip.Append($"\n Детали (Литейная): {haveP}/{partsNeed}");
            }
            if (step != null && step.items != null) foreach (var it in step.items)
            {
                int have = storage != null ? CountAtLeast(storage, it.kind, step.minTier) : 0;
                string col = have >= it.amount ? UiKit.GoodHex : UiKit.BadHex;
                parts.Add($"{ShortKind(it.kind)} {UiKit.Col(col, have + "/" + it.amount)}");
                tip.Append($"\n {KindLabel(it.kind)}: {have}/{it.amount}");
            }
            string q = minT == QualityTier.Medium ? "" : UiKit.Col(UiKit.MutedHex, $"  ({TownHallStorage.TierName(minT).ToLower()}+)");
            GUIStyle sm = new GUIStyle(UiKit.Small) { wordWrap = false, clipping = TextClipping.Clip };
            Rect mr = new Rect(x, cy, w, MatRowH);
            GUI.Label(mr, string.Join(UiKit.Col(UiKit.MutedHex, " · "), parts) + q, sm);
            UiKit.Tip(mr, tip.ToString());
            cy += MatRowH;
        }

        string problem = UpgradeProblem(b);
        string btnTip = problem == "" ? "Цена списывается сразу; уровень вырастет, когда житель донесёт материалы." : problem;
        if (UiKit.Button(new Rect(x, cy, w, UpBtnH), $"Улучшить до Ур. {target}", CanUpgrade(b), UiKit.Kind.Primary, btnTip))
            TryUpgrade(b);

        return total;
    }
}
