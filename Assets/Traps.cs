using UnityEngine;
using System.Collections.Generic;

/// <summary>Основа ловушки (рецепт Инженерного двора).</summary>
public enum TrapBase { Jaw, Spikes, Net, Spring, Salute }   // новые - только в конец

/// <summary>Добавки к рецепту. Каждая меняет одно-два свойства ловушки и добавляет свою цену.</summary>
[System.Flags]
public enum TrapMod { None = 0, Shock = 1, Resin = 2, Steel = 4, Tar = 8, Slow = 16, Ratchet = 32 }

/// <summary>
/// Готовая ловушка (вещь на складе). Обычную собирает Инженерный двор (путь Инженерии),
/// боевую делает из обычной Кузня мастера (путь Военного дела). Между путями ловушки ходят через рынок.
/// </summary>
public class TrapKit
{
    public TrapBase type;
    public TrapMod mods;
    public int mk = 1;            // марка I-III = уровень Инженерного двора при сборке
    public bool combat;           // боевая: ставится в бою (дома во время войны и в набеге)
    public float power = 1f;      // сила боевой взводки = сила Кузни (у обычных 1)
    public int charges = -1;      // сколько срабатываний осталось (-1 - полная)

    public TrapStats Stats => Traps.StatsOf(this);
    public int ChargesLeft => charges < 0 ? Stats.charges : Mathf.Min(charges, Stats.charges);
    public bool Full => ChargesLeft >= Stats.charges;
    public string Name => Traps.KitName(this);
    public bool Has(TrapMod m) { return (mods & m) != 0; }

    public TrapKit Clone()
    {
        return new TrapKit { type = type, mods = mods, mk = mk, combat = combat, power = power, charges = charges };
    }

    /// <summary>Та же вещь по всем свойствам (для группировки в списках).</summary>
    public bool SameAs(TrapKit o)
    {
        return o != null && type == o.type && mods == o.mods && mk == o.mk && combat == o.combat
            && Mathf.Abs(power - o.power) < 0.01f && ChargesLeft == o.ChargesLeft;
    }
}

/// <summary>Итоговые свойства ловушки (основа × добавки × марка × боевая взводка).</summary>
public struct TrapStats
{
    public float damage;        // урон каждой жертве
    public float radius;        // радиус действия от центра ловушки
    public int maxTargets;      // сколько врагов задевает за раз
    public float root;          // сколько секунд жертва не может двигаться
    public float slow, slowTime;// замедление после (×скорость) и сколько длится
    public float burnDps, burnTime;
    public float push;          // дома: на сколько клеток отбрасывает лёгкого врага
    public float burst;         // «Салют»: урон фейерверка всем рядом с жертвой
    public int charges;         // срабатываний до поломки
    public float rearm;         // секунд на перевзвод между срабатываниями
    public float installSeconds;// боевая: сколько ставится в бою
    public float installHp;     // боевая: прочность, пока ставится (её легко растоптать)
}

/// <summary>Рецепт основы ловушки.</summary>
public class TrapRecipe
{
    public TrapBase type;
    public string name, combatName, desc;
    public int rubber, parts, steel, planks, gold;
    public int resin;           // смола (горючая смесь «Салюта»)
    public float seconds;
    public int minLevel = 1;
    public EngSchool school = EngSchool.None;   // только для этого направления Инженерии (Salute - Пиротехник)
    public Color color;
    public TrapStats stats;     // свойства без добавок (Мк.I)
}

/// <summary>Добавка к рецепту.</summary>
public class TrapModInfo
{
    public TrapMod mod;
    public string name, tag, desc;
    public int rubber, resin, tar, steel, parts, slowPotions;
    public float seconds;       // сколько добавляет к сборке
}

/// <summary>Сколько стоит собрать ловушку.</summary>
public struct TrapCost
{
    public int gold, rubber, parts, steel, planks, resin, tar, slowPotions;
}

/// <summary>
/// Ловушки (новая система). Всё про числа - здесь.
///
/// ИНЖЕНЕРНЫЙ ДВОР (путь Инженерии), вкладка «Ловушки»: рецепт = основа + до двух добавок.
///  Основы (каждой нужна РЕЗИНА):
///   - Капкан: один враг, сильный урон, держит на месте;
///   - Шипованная доска: колет всех вокруг и замедляет, срабатывает несколько раз;
///   - Ловчая сеть: без урона, но надолго опутывает всех вокруг (тяжёлых - меньше);
///   - Пружинная катапульта (двор Ур.2): подбрасывает и отшвыривает врагов, оглушает;
///   - Салют (только Пиротехник): уносит врага ракетой в случайную сторону, в полёте рвётся салют, на земле жертва горит
///     (полёт и вспышки - Pyrotechnics.cs).
///  Добавки (Ур.1 - одна, Ур.2+ - две):
///   - Амортизатор (резина): +1 срабатывание, быстрее перевзводится;
///   - Смоляная пропитка (смола): поджигает жертв;
///   - Стальные зубья (сталь): урон ×1.5 (сеть получает крючья);
///   - Дёготь (пропитка): боевая версия ставится быстрее и крепче держится, пока ставится;
///   - Зелье замедления: после срабатывания жертва долго ползёт;
///   - Храповик (детали): радиус ×1.35, капкан хватает двоих.
///  Марка (Мк.I-III) = уровень двора: урон и длительность эффектов ×1 / ×1.2 / ×1.4.
///
/// ОБЫЧНЫЕ ловушки ставятся только у себя в городе, в мирное время и в своей зелёной зоне (кнопка «Ловушки» [T]).
/// Срабатывают на врагов, когда придёт набег. Поставили - взводится за пару секунд.
///
/// БОЕВЫЕ ловушки (Кузня мастера, путь Военного дела, вкладка «Ловушки») делаются из обычных:
///  ставятся прямо в бою - дома во время войны (где угодно на своей карте) и на карте набега
///  (кроме зелёной зоны врага - кольца стен деревни). Ставятся долго, и пока ставятся - хрупкие:
///  враг, прошедший вплотную, растаптывает их. Готовая боевая ловушка бьёт сильнее (сила Кузни).
/// </summary>
public static class Traps
{
    // ---------- Основы ----------
    public static readonly TrapRecipe[] Recipes =
    {
        new TrapRecipe
        {
            type = TrapBase.Jaw, name = "Капкан", combatName = "Боевой капкан",
            desc = "Хватает одного врага: сильный урон и держит на месте",
            rubber = 2, parts = 2, steel = 1, planks = 0, gold = 15, seconds = 25f, minLevel = 1,
            color = new Color(0.55f, 0.55f, 0.6f),
            stats = new TrapStats { damage = 55f, radius = 0.75f, maxTargets = 1, root = 2.5f, charges = 1, rearm = 2f,
                                    installSeconds = 14f, installHp = 40f }
        },
        new TrapRecipe
        {
            type = TrapBase.Spikes, name = "Шипованная доска", combatName = "Боевая шипованная доска",
            desc = "Колет всех вокруг и замедляет; срабатывает трижды",
            rubber = 2, parts = 1, steel = 0, planks = 3, gold = 10, seconds = 20f, minLevel = 1,
            color = new Color(0.6f, 0.42f, 0.22f),
            stats = new TrapStats { damage = 16f, radius = 1.3f, maxTargets = 6, slow = 0.6f, slowTime = 3f, charges = 3, rearm = 2f,
                                    installSeconds = 12f, installHp = 50f }
        },
        new TrapRecipe
        {
            type = TrapBase.Net, name = "Ловчая сеть", combatName = "Боевая ловчая сеть",
            desc = "Без урона, но надолго опутывает всех вокруг (тяжёлых — меньше)",
            rubber = 3, parts = 0, steel = 0, planks = 1, gold = 15, seconds = 20f, minLevel = 1,
            color = new Color(0.78f, 0.72f, 0.5f),
            stats = new TrapStats { damage = 0f, radius = 1.7f, maxTargets = 8, root = 3f, charges = 1, rearm = 2f,
                                    installSeconds = 16f, installHp = 30f }
        },
        new TrapRecipe
        {
            type = TrapBase.Spring, name = "Пружинная катапульта", combatName = "Боевая пружинная катапульта",
            desc = "Подбрасывает и отшвыривает врагов, оглушает",
            rubber = 4, parts = 3, steel = 1, planks = 1, gold = 25, seconds = 35f, minLevel = 2,
            color = new Color(0.4f, 0.55f, 0.75f),
            stats = new TrapStats { damage = 28f, radius = 1.1f, maxTargets = 4, root = 0.8f, push = 3.2f, charges = 2, rearm = 2.5f,
                                    installSeconds = 18f, installHp = 45f }
        },
        new TrapRecipe
        {
            type = TrapBase.Salute, name = "Салют", combatName = "Боевой салют",
            desc = "Уносит врага ракетой, в полёте — фейерверк, на земле горит",
            rubber = 3, parts = 2, steel = 0, planks = 1, gold = 25, resin = 4, seconds = 30f, minLevel = 1,
            school = EngSchool.Pyro,
            color = new Color(0.85f, 0.25f, 0.35f),
            stats = new TrapStats { damage = 0f, radius = 0.8f, maxTargets = 1, root = 1.2f, push = 4f, burst = 10f,
                                    burnDps = 5f, burnTime = 4f, charges = 1, rearm = 2f,
                                    installSeconds = 14f, installHp = 40f }
        },
    };

    /// <summary>Основа доступна при текущем пути (Салют - только Пиротехнику). Проверять каждый раз: путь может смениться.</summary>
    public static bool Available(TrapRecipe r) { return r != null && DevelopmentPath.EngAllows(r.school); }
    public static bool Available(TrapBase b) { return Available(RecipeOf(b)); }

    // ---------- Добавки ----------
    public static readonly TrapModInfo[] Mods =
    {
        new TrapModInfo { mod = TrapMod.Shock,   name = "Амортизатор",       tag = "аморт.", rubber = 2, seconds = 5f,
                          desc = "+1 срабатывание, перевзвод быстрее" },
        new TrapModInfo { mod = TrapMod.Resin,   name = "Смоляная пропитка", tag = "смола",  resin = 3, seconds = 5f,
                          desc = "поджигает жертв" },
        new TrapModInfo { mod = TrapMod.Steel,   name = "Стальные зубья",    tag = "сталь",  steel = 1, seconds = 5f,
                          desc = "урон ×1.5 (сеть — с крючьями)" },
        new TrapModInfo { mod = TrapMod.Tar,     name = "Дёготь",            tag = "дёготь", tar = 2, seconds = 3f,
                          desc = "боевая ставится на 30% быстрее и вдвое крепче, пока ставится" },
        new TrapModInfo { mod = TrapMod.Slow,    name = "Зелье замедления",  tag = "зелье",  slowPotions = 1, seconds = 3f,
                          desc = "после срабатывания жертва долго ползёт" },
        new TrapModInfo { mod = TrapMod.Ratchet, name = "Храповик",          tag = "храп.",  parts = 2, seconds = 8f,
                          desc = "радиус ×1.35, капкан хватает двоих" },
    };

    static readonly float[] MkMul = { 1f, 1.2f, 1.4f };

    public const int MaxModsLv1 = 1, MaxMods = 2;
    public static int ModSlots(int yardLevel) { return yardLevel >= 2 ? MaxMods : MaxModsLv1; }

    // Боевая переделка в Кузне
    public const int ConvertWood = 10, ConvertPlanks = 2, ConvertSteel = 1;
    public const float ConvertSeconds = 20f;
    public const float CombatDamageMul = 1.25f;   // боевая взводка бьёт сильнее (и ещё × сила Кузни)
    public const float OrdinaryArmSeconds = 3f;   // обычная ставится в мирное время - быстро
    public const float TarInstallMul = 0.7f, TarHpMul = 2f;

    public static TrapRecipe RecipeOf(TrapBase b)
    {
        foreach (var r in Recipes) if (r.type == b) return r;
        return Recipes[0];
    }

    public static TrapModInfo ModOf(TrapMod m)
    {
        foreach (var x in Mods) if (x.mod == m) return x;
        return null;
    }

    public static int ModCount(TrapMod mods)
    {
        int n = 0;
        foreach (var x in Mods) if ((mods & x.mod) != 0) n++;
        return n;
    }

    public static string Roman(int mk) { return TowerModules.Roman(mk); }

    // ---------- Свойства ----------

    public static TrapStats StatsOf(TrapKit k)
    {
        return StatsOf(k.type, k.mods, k.mk, k.combat, k.power);
    }

    public static TrapStats StatsOf(TrapBase b, TrapMod mods, int mk, bool combat, float power)
    {
        TrapStats s = RecipeOf(b).stats;
        float m = MkMul[Mathf.Clamp(mk, 1, 3) - 1];

        if ((mods & TrapMod.Steel) != 0) s.damage = Mathf.Max(s.damage, 8f) * 1.5f;
        if ((mods & TrapMod.Shock) != 0) { s.charges += 1; s.rearm *= 0.5f; }
        if ((mods & TrapMod.Resin) != 0) { s.burnDps = Mathf.Max(s.burnDps, 6f); s.burnTime = Mathf.Max(s.burnTime, 4f) + (b == TrapBase.Salute ? 2f : 0f); }
        if ((mods & TrapMod.Slow) != 0) { s.slow = s.slow > 0f ? Mathf.Min(s.slow, 0.45f) : 0.45f; s.slowTime += 4f; }
        if ((mods & TrapMod.Ratchet) != 0)
        {
            s.radius *= 1.35f;
            if (b == TrapBase.Jaw || b == TrapBase.Salute) s.maxTargets = 2;
        }
        if ((mods & TrapMod.Tar) != 0) { s.installSeconds *= TarInstallMul; s.installHp *= TarHpMul; }

        // Марка: сила и длительность эффектов
        s.damage *= m;
        s.burst *= m;
        s.root *= m;
        s.slowTime *= m;
        s.burnDps *= m;
        s.push *= Mathf.Lerp(1f, m, 0.5f);

        if (combat)
        {
            float p = Mathf.Max(0.5f, power);
            s.damage *= CombatDamageMul * p;
            s.burst *= CombatDamageMul * p;
            s.burnDps *= p;
            s.installHp *= p;
        }
        return s;
    }

    // ---------- Название и описание ----------

    public static string KitName(TrapKit k)
    {
        TrapRecipe r = RecipeOf(k.type);
        string n = (k.combat ? r.combatName : r.name) + " Мк." + Roman(k.mk);
        string tags = ModTags(k.mods);
        if (tags != "") n += " [" + tags + "]";
        return n;
    }

    public static string ModTags(TrapMod mods)
    {
        var parts = new List<string>();
        foreach (var x in Mods) if ((mods & x.mod) != 0) parts.Add(x.tag);
        return string.Join(", ", parts);
    }

    /// <summary>Короткая строка свойств: "урон 55, держит 2.5 с, 1 раз".</summary>
    public static string StatsText(TrapStats s, bool combat, int chargesLeft = -1)
    {
        var p = new List<string>();
        if (s.damage > 0f) p.Add($"урон {s.damage:F0}");
        if (s.maxTargets <= 1) p.Add("1 цель");
        else if (s.maxTargets == 2) p.Add("2 цели");
        else p.Add($"радиус {s.radius:F1}");
        if (s.burst > 0f) p.Add($"салют {s.burst:F0}");
        if (s.push > 0f) p.Add(s.burst > 0f ? $"полёт {s.push:F1} кл." : $"отброс {s.push:F1} кл.");
        if (s.root > 0f) p.Add(s.push > 0f ? $"оглушение {s.root:F1} с" : $"держит {s.root:F1} с");
        if (s.slow > 0f) p.Add($"замедл. ×{s.slow:F2} {s.slowTime:F0} с");
        if (s.burnDps > 0f) p.Add($"огонь {s.burnDps:F0}/с");
        int ch = chargesLeft >= 0 ? chargesLeft : s.charges;
        p.Add(chargesLeft >= 0 && chargesLeft < s.charges ? $"сраб. {ch}/{s.charges}" : $"сраб. {s.charges}");
        if (combat) p.Add($"ставится {s.installSeconds:F0} с");
        return string.Join(", ", p);
    }

    public static string Details(TrapKit k) { return StatsText(k.Stats, k.combat, k.ChargesLeft); }

    /// <summary>Ценность ловушки для рынка (марки).</summary>
    public static float Value(TrapKit k)
    {
        float v;
        switch (k.type)
        {
            case TrapBase.Jaw: v = 30f; break;
            case TrapBase.Spikes: v = 26f; break;
            case TrapBase.Net: v = 28f; break;
            case TrapBase.Salute: v = 40f; break;
            default: v = 45f; break;
        }
        v += ModCount(k.mods) * 9f;
        v *= 1f + 0.25f * (Mathf.Clamp(k.mk, 1, 3) - 1);
        if (k.combat) v *= 1.3f + 0.3f * (k.power - 1f);
        TrapStats s = k.Stats;
        return v * (0.35f + 0.65f * k.ChargesLeft / Mathf.Max(1f, s.charges));
    }

    // ---------- Цена сборки в Инженерном дворе ----------

    public static TrapCost CostOf(TrapBase b, TrapMod mods)
    {
        TrapRecipe r = RecipeOf(b);
        TrapCost c = new TrapCost { gold = r.gold, rubber = r.rubber, parts = r.parts, steel = r.steel, planks = r.planks, resin = r.resin };
        foreach (var x in Mods)
        {
            if ((mods & x.mod) == 0) continue;
            c.rubber += x.rubber; c.resin += x.resin; c.tar += x.tar;
            c.steel += x.steel; c.parts += x.parts; c.slowPotions += x.slowPotions;
        }
        return c;
    }

    public static float SecondsOf(TrapBase b, TrapMod mods)
    {
        float t = RecipeOf(b).seconds;
        foreach (var x in Mods) if ((mods & x.mod) != 0) t += x.seconds;
        return t;
    }

    static int Have(RefinedKind k)
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, k, QualityTier.Medium) : 0;
    }

    public static string Missing(TrapCost c)
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return "нет казны";
        var p = new List<string>();
        if (rm.rubber < c.rubber) p.Add($"резины {c.rubber - rm.rubber}");
        if (rm.gold < c.gold) p.Add($"золота {c.gold - rm.gold}");
        if (rm.parts < c.parts) p.Add($"деталей {c.parts - rm.parts}");
        if (rm.resin < c.resin) p.Add($"смолы {c.resin - rm.resin}");
        if (rm.tar < c.tar) p.Add($"пропитки {c.tar - rm.tar}");
        if (rm.slowPotions < c.slowPotions) p.Add($"зелий замедл. {c.slowPotions - rm.slowPotions}");
        int st = Have(RefinedKind.Steel), pl = Have(RefinedKind.Planks);
        if (st < c.steel) p.Add($"стали {c.steel - st}");
        if (pl < c.planks) p.Add($"досок {c.planks - pl}");
        return string.Join(", ", p);
    }

    public static bool CanPay(TrapCost c) { return ResourceManager.Instance != null && Missing(c) == ""; }

    public static bool Pay(TrapCost c, List<UpgradeRules.Spent> spent)
    {
        if (!CanPay(c)) return false;
        var rm = ResourceManager.Instance;
        rm.Spend(c.gold, 0, 0, 0);
        rm.SpendRubber(c.rubber);
        rm.SpendParts(c.parts);
        rm.SpendResin(c.resin);
        rm.SpendTar(c.tar);
        rm.SpendSlowPotions(c.slowPotions);
        var st = TownHallStorage.Instance;
        if (c.steel > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, c.steel, spent);
        if (c.planks > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, c.planks, spent);
        return true;
    }

    /// <summary>Вернуть сырьё (сборку прервали). Доски и сталь возвращаются через spent.</summary>
    public static void Refund(TrapCost c, List<UpgradeRules.Spent> spent)
    {
        var rm = ResourceManager.Instance;
        if (rm != null)
        {
            rm.Add(c.gold, 0, 0, 0);
            rm.AddRubber(c.rubber);
            rm.AddParts(c.parts);
            rm.AddResin(c.resin);
            rm.AddTar(c.tar);
            rm.AddSlowPotions(c.slowPotions);
        }
        UpgradeRules.RefundSpent(spent);
        spent.Clear();
    }

    /// <summary>Цена с подсветкой: зелёное - есть, красное - не хватает.</summary>
    public static string CostText(TrapCost c)
    {
        var rm = ResourceManager.Instance;
        var p = new List<string>();
        void Part(int need, int have, string what)
        {
            if (need <= 0) return;
            p.Add($"<color={(have >= need ? "#80FF80" : "#FF6060")}>{need} {what}</color>");
        }
        Part(c.rubber, rm != null ? rm.rubber : 0, "резины");
        Part(c.gold, rm != null ? rm.gold : 0, "золота");
        Part(c.parts, rm != null ? rm.parts : 0, "деталей");
        Part(c.steel, Have(RefinedKind.Steel), "стали");
        Part(c.planks, Have(RefinedKind.Planks), "досок");
        Part(c.resin, rm != null ? rm.resin : 0, "смолы");
        Part(c.tar, rm != null ? rm.tar : 0, "пропитки");
        Part(c.slowPotions, rm != null ? rm.slowPotions : 0, "зелья замедл.");
        return string.Join(", ", p);
    }

    // ---------- Боевая переделка (Кузня мастера) ----------

    public static string ConvertMissing()
    {
        var rm = ResourceManager.Instance;
        if (rm == null) return "нет казны";
        var p = new List<string>();
        if (rm.wood < ConvertWood) p.Add($"дерева {ConvertWood - rm.wood}");
        int pl = Have(RefinedKind.Planks), st = Have(RefinedKind.Steel);
        if (pl < ConvertPlanks) p.Add($"досок {ConvertPlanks - pl}");
        if (st < ConvertSteel) p.Add($"стали {ConvertSteel - st}");
        return string.Join(", ", p);
    }

    public static bool PayConvert()
    {
        if (ConvertMissing() != "") return false;
        var rm = ResourceManager.Instance;
        rm.Spend(0, ConvertWood, 0, 0);
        var st = TownHallStorage.Instance;
        UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, ConvertPlanks);
        UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, ConvertSteel);
        return true;
    }

    public static void RefundConvert()
    {
        var rm = ResourceManager.Instance;
        if (rm != null) rm.Add(0, ConvertWood, 0, 0);
        var st = TownHallStorage.Instance;
        if (st == null) return;
        st.AddPlanks(QualityTier.Medium, ConvertPlanks);
        st.AddSteel(QualityTier.Medium, ConvertSteel);
    }

    /// <summary>Случайная обычная ловушка для купца на рынке.</summary>
    public static TrapKit RandomKit()
    {
        float r = Random.value;
        TrapBase b = r < 0.3f ? TrapBase.Jaw : (r < 0.6f ? TrapBase.Spikes : (r < 0.85f ? TrapBase.Net : TrapBase.Spring));
        TrapMod mods = TrapMod.None;
        if (Random.value < 0.5f) mods |= Mods[Random.Range(0, Mods.Length)].mod;
        if (Random.value < 0.15f) mods |= Mods[Random.Range(0, Mods.Length)].mod;
        float m = Random.value;
        int mk = m < 0.6f ? 1 : (m < 0.9f ? 2 : 3);
        if (b == TrapBase.Spring) mk = Mathf.Max(2, mk);
        return new TrapKit { type = b, mods = mods, mk = mk };
    }
}

/// <summary>
/// Запас ловушек города (лежат в Ратуше) и то, что взяли с собой в набег.
/// Обычные и боевые - в одном списке, различаются TrapKit.combat.
/// </summary>
public static class TrapStock
{
    public static readonly List<TrapKit> Hall = new List<TrapKit>();
    public static readonly List<TrapKit> RaidKit = new List<TrapKit>();
    static int raidPick;

    public const int MaxStock = 30;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Hall.Clear(); RaidKit.Clear(); raidPick = 0; }

    public static int Count(bool combat)
    {
        int n = 0;
        foreach (var k in Hall) if (k.combat == combat) n++;
        return n;
    }

    public static bool Full => Hall.Count >= MaxStock;

    public static void Add(TrapKit k) { if (k != null) Hall.Add(k); }

    public static bool Remove(TrapKit k) { return k != null && Hall.Remove(k); }

    /// <summary>Группы одинаковых ловушек (для списков): первая вещь группы и сколько их.</summary>
    public static List<KeyValuePair<TrapKit, int>> Groups(List<TrapKit> src, System.Func<TrapKit, bool> filter)
    {
        var res = new List<KeyValuePair<TrapKit, int>>();
        foreach (var k in src)
        {
            if (k == null || (filter != null && !filter(k))) continue;
            int i = res.FindIndex(g => g.Key.SameAs(k));
            if (i >= 0) res[i] = new KeyValuePair<TrapKit, int>(res[i].Key, res[i].Value + 1);
            else res.Add(new KeyValuePair<TrapKit, int>(k, 1));
        }
        res.Sort((a, b) => Traps.Value(b.Key).CompareTo(Traps.Value(a.Key)));
        return res;
    }

    /// <summary>Взять из списка вещь, такую же, как sample.</summary>
    public static TrapKit TakeLike(List<TrapKit> src, TrapKit sample)
    {
        int i = src.FindIndex(k => k.SameAs(sample));
        if (i < 0) return null;
        TrapKit k = src[i];
        src.RemoveAt(i);
        return k;
    }

    // ---------- Набег ----------

    public static int RaidCount => RaidKit.Count;

    /// <summary>Выступили в набег: выбранные боевые ловушки (лучшие) уходят с армией.</summary>
    public static void LoadRaidKit()
    {
        int n = Mathf.Min(raidPick, Count(true));
        for (int i = 0; i < n; i++)
        {
            TrapKit best = null;
            foreach (var k in Hall)
                if (k.combat && (best == null || Traps.Value(k) > Traps.Value(best))) best = k;
            if (best == null) break;
            Hall.Remove(best);
            RaidKit.Add(best);
        }
        raidPick = 0;
    }

    /// <summary>Набег кончился: неизрасходованное - домой (toStock) или потеряно.</summary>
    public static void UnloadRaidKit(bool toStock)
    {
        if (toStock) Hall.AddRange(RaidKit);
        RaidKit.Clear();
    }

    public static void ReturnToRaidKit(TrapKit k) { if (k != null) RaidKit.Add(k); }

    /// <summary>Строка выбора в окне «В бой!» (под заслонами и пунктами).</summary>
    public static void DrawRaidPickRow(float x, float y, float w, GUIStyle small)
    {
        int have = Count(true);
        raidPick = Mathf.Clamp(raidPick, 0, have);
        Rect lr = new Rect(x, y, w - 100f, 22f);
        GUI.Label(lr, $"Боевые ловушки: берём <b>{raidPick}</b>" + UiKit.Col(UiKit.MutedHex, $" из {have}"), small);
        UiKit.Tip(lr, "Боевые ловушки (их делает Кузня мастера) ставят в набеге рядом со своими бойцами, но не в зелёной зоне врага. Неизрасходованные вернутся домой.");
        if (UiKit.Button(new Rect(x + w - 84f, y, 40f, 22f), "−", raidPick > 0, UiKit.Kind.Normal, null)) raidPick--;
        if (UiKit.Button(new Rect(x + w - 40f, y, 40f, 22f), "+", raidPick < have, UiKit.Kind.Normal, have > 0 ? null : "Боевых ловушек в запасе нет")) raidPick++;
    }
}
