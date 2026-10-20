using UnityEngine;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Добыча, которую кто-то несёт на себе: обычные ресурсы + доски/плиты по качеству.
///  - Враг разрушил производство - забирает всё, что лежало на складе здания.
///  - Враг разрушил Ратушу - забирает треть казны (золото, дерево, камень, еда, железная руда, овёс, ягоды)
///    и 10% досок/плит/стали, выбранных случайно.
///  - Добычу делят между врагами, которые ломали здание (каждый несёт свою часть).
///  - Врага убили - его добыча переходит к убийце (солдату). Убила башня - сразу в казну.
///  - Солдат с добычей сдаёт её, когда заходит в здание (Ратуша, казарма, дом)
///    или доходит до двери Ратуши / казармы.
///  - Солдата с добычей убил враг - враг забирает её обратно.
///  - Враг ушёл за край карты - добыча потеряна.
/// </summary>
public class Loot
{
    // Сколько забирают при разрушении Ратуши
    public const float HallRawShare = 1f / 3f;     // золото, дерево, камень, еда
    public const float HallRefinedShare = 0.1f;    // доски и плиты

    public int gold, wood, stone, food, iron, oats, berries, herbs, potions, resin;  // iron - железная руда, oats - овёс, berries - ягоды, resin - смола
    public readonly int[] planks = new int[3];     // индекс = (int)QualityTier
    public readonly int[] plates = new int[3];
    public readonly int[] steel = new int[3];
    public readonly int[] reinforced = new int[3];   // укреплённые плиты

    int[] Arr(RefinedKind k)
    {
        switch (k)
        {
            case RefinedKind.Planks: return planks;
            case RefinedKind.StonePlates: return plates;
            case RefinedKind.ReinforcedPlates: return reinforced;
            default: return steel;
        }
    }

    public int RefinedTotal
    {
        get
        {
            int n = 0;
            for (int i = 0; i < 3; i++) n += planks[i] + plates[i] + steel[i] + reinforced[i];
            return n;
        }
    }

    public int Total => gold + wood + stone + food + iron + oats + berries + herbs + potions + resin + RefinedTotal;
    public bool IsEmpty => Total <= 0;

    public void Clear()
    {
        gold = wood = stone = food = iron = oats = berries = herbs = potions = resin = 0;
        for (int i = 0; i < 3; i++) { planks[i] = 0; plates[i] = 0; steel[i] = 0; reinforced[i] = 0; }
    }

    public void Add(Loot o)
    {
        if (o == null) return;
        gold += o.gold; wood += o.wood; stone += o.stone; food += o.food; iron += o.iron; oats += o.oats; berries += o.berries; herbs += o.herbs; potions += o.potions; resin += o.resin;
        for (int i = 0; i < 3; i++) { planks[i] += o.planks[i]; plates[i] += o.plates[i]; steel[i] += o.steel[i]; reinforced[i] += o.reinforced[i]; }
    }

    // Переложить всё содержимое в другой мешок (этот становится пустым)
    public void MoveTo(Loot dst)
    {
        if (dst == null || dst == this) return;
        dst.Add(this);
        Clear();
    }

    // Сдать в казну и на склад Ратуши
    public void DepositToTown()
    {
        if (IsEmpty) return;
        var rm = ResourceManager.Instance;
        if (rm != null) rm.Add(gold, wood, stone, food, iron, oats, berries, herbs, potions, resin);

        var st = TownHallStorage.Instance;
        if (st != null)
        {
            for (int i = 0; i < 3; i++)
            {
                if (planks[i] > 0) st.AddPlanks((QualityTier)i, planks[i]);
                if (plates[i] > 0) st.AddStonePlates((QualityTier)i, plates[i]);
                if (steel[i] > 0) st.AddSteel((QualityTier)i, steel[i]);
                if (reinforced[i] > 0) st.Add(RefinedKind.ReinforcedPlates, (QualityTier)i, reinforced[i]);
            }
        }
        Clear();
    }

    // ---------- Разграбление ----------

    // Всё, что лежит на складе здания (шахта, лесопилка, ферма, станция собирателей...)
    public static Loot FromBuildingStorage(BuildingInstance b)
    {
        Loot l = new Loot();
        if (b == null) return l;
        l.gold = b.storedGold;
        l.wood = b.storedWood;
        l.stone = b.storedStone;
        l.food = b.storedFood;
        l.iron = b.storedIron;
        l.oats = b.storedOats;
        l.berries = b.storedBerries;
        l.herbs = b.storedHerbs;
        l.resin = b.storedResin;
        return l;
    }

    // Разграбление Ратуши: треть казны + 10% досок/плит в случайном порядке. Списывается сразу.
    public static Loot RaidTreasury()
    {
        Loot l = new Loot();

        var rm = ResourceManager.Instance;
        if (rm != null)
        {
            l.gold = Mathf.FloorToInt(Mathf.Max(0, rm.gold) * HallRawShare);
            l.wood = Mathf.FloorToInt(Mathf.Max(0, rm.wood) * HallRawShare);
            l.stone = Mathf.FloorToInt(Mathf.Max(0, rm.stone) * HallRawShare);
            l.food = Mathf.FloorToInt(Mathf.Max(0, rm.food) * HallRawShare);
            l.iron = Mathf.FloorToInt(Mathf.Max(0, rm.iron) * HallRawShare);
            l.oats = Mathf.FloorToInt(Mathf.Max(0, rm.oats) * HallRawShare);
            l.berries = Mathf.FloorToInt(Mathf.Max(0, rm.berries) * HallRawShare);
            l.herbs = Mathf.FloorToInt(Mathf.Max(0, rm.herbs) * HallRawShare);
            l.potions = Mathf.FloorToInt(Mathf.Max(0, rm.potions) * HallRawShare);
            l.resin = Mathf.FloorToInt(Mathf.Max(0, rm.resin) * HallRawShare);
            rm.Add(-l.gold, -l.wood, -l.stone, -l.food, -l.iron, -l.oats, -l.berries, -l.herbs, -l.potions, -l.resin);
        }

        var st = TownHallStorage.Instance;
        if (st == null) return l;

        RefinedKind[] kinds = { RefinedKind.Planks, RefinedKind.StonePlates, RefinedKind.Steel, RefinedKind.ReinforcedPlates };

        // Сколько штук на складе всего (доски + плиты + сталь всех качеств)
        int total = 0;
        foreach (RefinedKind kind in kinds)
            foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
                total += st.Get(kind, q);
        if (total <= 0) return l;

        // 10%: целая часть + шанс на ещё одну штуку (чтобы и с маленького склада иногда что-то уносили)
        float exact = total * HallRefinedShare;
        int take = Mathf.FloorToInt(exact);
        if (Random.value < exact - take) take++;

        // Тянем по одной штуке случайно: чего больше на складе - то чаще и уносят
        for (int n = 0; n < take && total > 0; n++)
        {
            int r = Random.Range(0, total);
            bool picked = false;
            foreach (RefinedKind kind in kinds)
            {
                foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
                {
                    int have = st.Get(kind, q);
                    if (r >= have) { r -= have; continue; }
                    if (st.TrySpend(kind, q, 1)) l.Arr(kind)[(int)q]++;
                    picked = true;
                    break;
                }
                if (picked) break;
            }
            total--;
        }
        return l;
    }

    // ---------- Дележ между несколькими врагами ----------

    // Разделить на parts частей (по возможности поровну, остатки - случайным)
    public List<Loot> Split(int parts)
    {
        var res = new List<Loot>();
        parts = Mathf.Max(1, parts);
        for (int i = 0; i < parts; i++) res.Add(new Loot());

        int start = Random.Range(0, parts);
        int Share(int v, int i) => v / parts + (((i - start + parts) % parts) < v % parts ? 1 : 0);

        for (int i = 0; i < parts; i++)
        {
            Loot p = res[i];
            p.gold = Share(gold, i);
            p.wood = Share(wood, i);
            p.stone = Share(stone, i);
            p.food = Share(food, i);
            p.iron = Share(iron, i);
            p.oats = Share(oats, i);
            p.berries = Share(berries, i);
            p.herbs = Share(herbs, i);
            p.potions = Share(potions, i);
            p.resin = Share(resin, i);
        }

        // Доски и плиты раздаём поштучно случайным врагам
        for (int t = 0; t < 3; t++)
        {
            for (int k = 0; k < planks[t]; k++) res[Random.Range(0, parts)].planks[t]++;
            for (int k = 0; k < plates[t]; k++) res[Random.Range(0, parts)].plates[t]++;
            for (int k = 0; k < steel[t]; k++) res[Random.Range(0, parts)].steel[t]++;
            for (int k = 0; k < reinforced[t]; k++) res[Random.Range(0, parts)].reinforced[t]++;
        }
        return res;
    }

    // ---------- Текст ----------

    public string ShortText
    {
        get
        {
            if (IsEmpty) return "ничего";
            var parts = new List<string>();
            if (gold > 0) parts.Add($"{gold} зол.");
            if (wood > 0) parts.Add($"{wood} дер.");
            if (stone > 0) parts.Add($"{stone} камня");
            if (food > 0) parts.Add($"{food} еды");
            if (iron > 0) parts.Add($"{iron} руды");
            if (oats > 0) parts.Add($"{oats} овса");
            if (berries > 0) parts.Add($"{berries} ягод");
            if (herbs > 0) parts.Add($"{herbs} трав");
            if (potions > 0) parts.Add($"{potions} зелий");
            if (resin > 0) parts.Add($"{resin} смолы");
            int pl = planks[0] + planks[1] + planks[2];
            int st = plates[0] + plates[1] + plates[2];
            int sl = steel[0] + steel[1] + steel[2];
            if (pl > 0) parts.Add($"{pl} досок");
            if (st > 0) parts.Add($"{st} плит");
            if (sl > 0) parts.Add($"{sl} стали");
            int rp = reinforced[0] + reinforced[1] + reinforced[2];
            if (rp > 0) parts.Add($"{rp} укр. плит");
            return string.Join(", ", parts);
        }
    }
}
