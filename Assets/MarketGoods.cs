using UnityEngine;
using System.Collections.Generic;

/// <summary>Вид товара на рынке. Доски, плиты, сталь и укреплённые плиты продаются с указанием качества.</summary>
// Новые виды - только в конец (по номеру хранятся цены)
public enum GoodKind { Gold, Wood, Stone, Food, Iron, Oats, Berries, Herbs, Potions, Parts, Planks, Plates, Steel, Reinforced,
                       Resin, Tar, Rubber, SlowPotions, Beams, Monolith }

/// <summary>Конкретный товар: вид + качество (качество важно только для досок, плит и стали).</summary>
[System.Serializable]
public struct GoodId : System.IEquatable<GoodId>
{
    public GoodKind kind;
    public QualityTier tier;

    public GoodId(GoodKind k, QualityTier t = QualityTier.Medium)
    {
        kind = k;
        tier = MarketGoods.IsRefined(k) ? t : QualityTier.Medium;
    }

    public bool Equals(GoodId o) { return kind == o.kind && tier == o.tier; }
    public override bool Equals(object o) { return o is GoodId g && Equals(g); }
    public override int GetHashCode() { return (int)kind * 8 + (int)tier; }
}

/// <summary>
/// Каталог товаров рынка: название, размер лота, базовая цена в марках, спрос, к какому пути относится.
/// И всё, что нужно рынку от казны: сколько есть, списать, положить обратно.
///  - Сырьё (золото, дерево, камень, еда, руда, овёс, ягоды, травы) продаётся лотами по 10 штук,
///    доски и плиты - по 5, сталь, укреплённые плиты, зелья и детали - поштучно.
///  - Доски / плиты / сталь хорошего и наилучшего качества стоят дороже (TierMul).
///  - Травы видны только на пути Магии, детали - только на пути Инженерии (это сырьё для их зданий).
///    Готовые вещи (зелья, резина, пропитка, брус...) видны всем: купленным может пользоваться любой город.
///  - Вкладки (Category): 0 - сырьё, 1 - материалы, 2 - зелья. Оружие, модули и бойцы - штучные (MarketItem).
/// Все числа - в таблице Infos.
/// </summary>
public static class MarketGoods
{
    public class Info
    {
        public string name;       // "Дерево"
        public string unit;       // как называть штуки в тексте: "дерева"
        public int pack;          // штук в лоте
        public float price;       // базовая цена лота, марки
        public float demand;      // как охотно покупают боты (1 - обычно, больше - быстрее)
        public string color;
        public PathKind path = PathKind.None;
        public int category;      // 0 - сырьё, 1 - материалы, 2 - зелья
    }

    public static readonly string[] CategoryNames = { "Сырьё", "Материалы", "Зелья", "Оружие и щиты", "Модули и ловушки", "Войска" };

    // Порядок совпадает с GoodKind
    static readonly Info[] Infos =
    {
        new Info { name = "Золото",            unit = "золота",   pack = 10, price = 3f,  demand = 1.2f, color = "#FFE040" },
        new Info { name = "Дерево",            unit = "дерева",   pack = 10, price = 2f,  demand = 1.4f, color = "#D08040" },
        new Info { name = "Камень",            unit = "камня",    pack = 10, price = 2f,  demand = 1.3f, color = "#60E0F0" },
        new Info { name = "Еда",               unit = "еды",      pack = 10, price = 2f,  demand = 1.5f, color = "#70F070" },
        new Info { name = "Железная руда",     unit = "руды",     pack = 10, price = 5f,  demand = 1.0f, color = "#B0C4DE" },
        new Info { name = "Овёс",              unit = "овса",     pack = 10, price = 4f,  demand = 0.9f, color = "#E8D890" },
        new Info { name = "Ягоды",             unit = "ягод",     pack = 10, price = 3f,  demand = 1.0f, color = "#F0A0C8" },
        new Info { name = "Травы",             unit = "трав",     pack = 10, price = 8f,  demand = 0.8f, color = "#90E060", path = PathKind.Magic },
        new Info { name = "Зелье отравления",  unit = "зелий",    pack = 1,  price = 6f,  demand = 0.7f, color = "#B0E080", category = 2 },
        new Info { name = "Детали",            unit = "деталей",  pack = 1,  price = 4f,  demand = 0.8f, color = "#FFC870", path = PathKind.Engineering, category = 1 },
        new Info { name = "Доски",             unit = "досок",    pack = 5,  price = 5f,  demand = 1.1f, color = "#E0B080", category = 1 },
        new Info { name = "Плиты",             unit = "плит",     pack = 5,  price = 5f,  demand = 1.0f, color = "#C0D0E0", category = 1 },
        new Info { name = "Сталь",             unit = "стали",    pack = 1,  price = 4f,  demand = 0.9f, color = "#D0D8F0", category = 1 },
        new Info { name = "Укреплённые плиты", unit = "укр. плит", pack = 1, price = 9f,  demand = 0.6f, color = "#A8B8D8", category = 1 },
        new Info { name = "Смола",             unit = "смолы",    pack = 10, price = 6f,  demand = 1.0f, color = "#E0A040" },
        new Info { name = "Смоляная пропитка", unit = "пропитки", pack = 5,  price = 6f,  demand = 0.8f, color = "#C08040", category = 1 },
        new Info { name = "Резина",            unit = "резины",   pack = 1,  price = 3f,  demand = 0.7f, color = "#A0A0B0", category = 1 },
        new Info { name = "Зелье замедления",  unit = "зелий замедл.", pack = 1, price = 7f, demand = 0.7f, color = "#80C0FF", category = 2 },
        new Info { name = "Смоляной брус",     unit = "бруса",    pack = 1,  price = 4f,  demand = 0.8f, color = "#D8A060", category = 1 },
        new Info { name = "Монолитное основание", unit = "монолита", pack = 1, price = 14f, demand = 0.5f, color = "#C0B0A0", category = 1 },
    };

    static readonly float[] TierMuls = { 1f, 1.6f, 2.6f };
    static readonly string[] TierShort = { "ср.", "хор.", "наил." };

    public static int KindCount => Infos.Length;
    public static Info Of(GoodKind k) { return Infos[(int)k]; }
    public static int Category(GoodKind k) { return Infos[(int)k].category; }
    public static float TierMul(QualityTier t) { return TierMuls[(int)t]; }

    public static bool IsRefined(GoodKind k)
    {
        return k == GoodKind.Planks || k == GoodKind.Plates || k == GoodKind.Steel || k == GoodKind.Reinforced
            || k == GoodKind.Beams || k == GoodKind.Monolith;
    }

    static RefinedKind Refined(GoodKind k)
    {
        switch (k)
        {
            case GoodKind.Planks: return RefinedKind.Planks;
            case GoodKind.Plates: return RefinedKind.StonePlates;
            case GoodKind.Reinforced: return RefinedKind.ReinforcedPlates;
            case GoodKind.Beams: return RefinedKind.TarredBeams;
            case GoodKind.Monolith: return RefinedKind.Monolith;
            default: return RefinedKind.Steel;
        }
    }

    /// <summary>Товар виден на рынке (травы, зелья, детали - только своему пути).</summary>
    public static bool Visible(GoodKind k) { return DevelopmentPath.Allows(Of(k).path); }

    /// <summary>Все товары, которые сейчас видны на рынке (доски/плиты/сталь - в трёх качествах). category -1 - все.</summary>
    public static List<GoodId> AllVisible(int category = -1)
    {
        var list = new List<GoodId>();
        for (int i = 0; i < Infos.Length; i++)
        {
            GoodKind k = (GoodKind)i;
            if (!Visible(k)) continue;
            if (category >= 0 && Infos[i].category != category) continue;
            if (IsRefined(k))
                foreach (QualityTier t in System.Enum.GetValues(typeof(QualityTier))) list.Add(new GoodId(k, t));
            else list.Add(new GoodId(k));
        }
        return list;
    }

    public static string Name(GoodId g)
    {
        string n = Of(g.kind).name;
        return IsRefined(g.kind) ? $"{n} ({TierShort[(int)g.tier]})" : n;
    }

    /// <summary>"Дерево ×10" - как называется один лот.</summary>
    public static string PackName(GoodId g)
    {
        int p = Of(g.kind).pack;
        return p > 1 ? $"{Name(g)} ×{p}" : Name(g);
    }

    public static string Colored(GoodId g, string text = null)
    {
        return $"<color={Of(g.kind).color}>{text ?? Name(g)}</color>";
    }

    public static int Pack(GoodId g) { return Of(g.kind).pack; }

    /// <summary>"30 дерева", "4 стали (хор.)"</summary>
    public static string UnitsText(GoodId g, int units)
    {
        string t = IsRefined(g.kind) ? $" ({TierShort[(int)g.tier]})" : "";
        return $"{units} {Of(g.kind).unit}{t}";
    }

    // ---------- Казна ----------

    /// <summary>Сколько штук этого товара есть в городе (казна Ратуши + Хранилища).</summary>
    public static int Have(GoodId g)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return 0;
        switch (g.kind)
        {
            case GoodKind.Gold: return rm.gold;
            case GoodKind.Wood: return rm.wood;
            case GoodKind.Stone: return rm.stone;
            case GoodKind.Food: return rm.food;
            case GoodKind.Iron: return rm.iron;
            case GoodKind.Oats: return rm.oats;
            case GoodKind.Berries: return rm.berries;
            case GoodKind.Herbs: return rm.herbs;
            case GoodKind.Potions: return PotionHouse.TotalPotions;
            case GoodKind.Parts: return rm.parts;
            case GoodKind.Resin: return rm.resin;
            case GoodKind.Tar: return rm.tar;
            case GoodKind.Rubber: return rm.rubber;
            case GoodKind.SlowPotions: return rm.slowPotions;
            default:
                var st = TownHallStorage.Instance;
                RefinedKind rk = Refined(g.kind);
                return (st != null ? st.Get(rk, g.tier) : 0) + WarehouseController.TotalStored(rk, g.tier);
        }
    }

    public static int HavePacks(GoodId g) { return Have(g) / Mathf.Max(1, Pack(g)); }

    /// <summary>
    /// Списать units штук из города. source - откуда их понесёт житель (Ратуша или Хранилище).
    /// false - не хватает, ничего не списано.
    /// </summary>
    public static bool Take(GoodId g, int units, out BuildingInstance source)
    {
        var pm = PopulationManager.Instance;
        source = pm != null ? pm.Hall : null;
        if (units <= 0) return true;
        if (Have(g) < units) return false;

        var rm = ResourceManager.Instance;
        switch (g.kind)
        {
            case GoodKind.Gold: return rm.Spend(units, 0, 0, 0);
            case GoodKind.Wood: return rm.Spend(0, units, 0, 0);
            case GoodKind.Stone: return rm.Spend(0, 0, units, 0);
            case GoodKind.Food: return rm.Spend(0, 0, 0, units);
            case GoodKind.Iron: return rm.SpendIron(units);
            case GoodKind.Oats: return rm.SpendOats(units);
            case GoodKind.Berries: return rm.SpendBerries(units);
            case GoodKind.Herbs: return rm.SpendHerbs(units);
            case GoodKind.Parts: return rm.SpendParts(units);
            case GoodKind.Resin: return rm.SpendResin(units);
            case GoodKind.Tar: return rm.SpendTar(units);
            case GoodKind.Rubber: return rm.SpendRubber(units);
            case GoodKind.SlowPotions: return rm.SpendSlowPotions(units);
            case GoodKind.Potions:
            {
                if (PotionHouse.TakePotions(units, out BuildingInstance src)) { if (src != null) source = src; return true; }
                return false;
            }
            default:
            {
                var st = TownHallStorage.Instance;
                RefinedKind rk = Refined(g.kind);
                int left = units;
                bool fromHall = false;
                if (st != null)
                {
                    int h = Mathf.Min(left, st.Get(rk, g.tier));
                    if (h > 0 && st.TrySpend(rk, g.tier, h)) { left -= h; fromHall = true; }
                }
                BuildingInstance whSource = null;
                while (left > 0)
                {
                    int got = WarehouseController.Take(rk, g.tier, left, out BuildingInstance src);
                    if (got <= 0) break;
                    left -= got;
                    if (whSource == null) whSource = src;
                }
                if (left > 0)
                {
                    // Не хватило (склад изменился) - всё взятое возвращаем
                    if (st != null) st.Add(rk, g.tier, units - left);
                    return false;
                }
                if (!fromHall && whSource != null) source = whSource;
                return true;
            }
        }
    }

    /// <summary>Положить units штук в город (казна / склад Ратуши).</summary>
    public static void Give(GoodId g, int units)
    {
        if (units <= 0) return;
        var rm = ResourceManager.Instance;
        if (rm == null) return;
        switch (g.kind)
        {
            case GoodKind.Gold: rm.Add(units, 0, 0, 0); break;
            case GoodKind.Wood: rm.Add(0, units, 0, 0); break;
            case GoodKind.Stone: rm.Add(0, 0, units, 0); break;
            case GoodKind.Food: rm.Add(0, 0, 0, units); break;
            case GoodKind.Iron: rm.Add(0, 0, 0, 0, units); break;
            case GoodKind.Oats: rm.Add(0, 0, 0, 0, 0, units); break;
            case GoodKind.Berries: rm.Add(0, 0, 0, 0, 0, 0, units); break;
            case GoodKind.Herbs: rm.AddHerbs(units); break;
            case GoodKind.Potions: rm.AddPotions(units); break;
            case GoodKind.Parts: rm.AddParts(units); break;
            case GoodKind.Resin: rm.AddResin(units); break;
            case GoodKind.Tar: rm.AddTar(units); break;
            case GoodKind.Rubber: rm.AddRubber(units); break;
            case GoodKind.SlowPotions: rm.AddSlowPotions(units); break;
            default:
                var st = TownHallStorage.Instance;
                if (st != null) st.Add(Refined(g.kind), g.tier, units);
                break;
        }
    }
}
