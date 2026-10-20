using UnityEngine;

public enum WeaponKind
{
    Sword,   // меч - для мечников, рыцарей, всадников
    Bow,     // лук - для лучников и конных лучников
    Shot     // бронебойное ядро - «оружие» полевой пушки (собирает Инженерный двор). Прочность = число выстрелов
}

/// <summary>
/// Одна единица оружия: вид, качество, улучшение золотом и прочность.
///  - Качество (Обычный / Хороший / Превосходный) выпадает случайно при ковке в Оружейной.
///  - Улучшение за золото: Усиленный (1000) -> Позолоченный (2500) -> Золотой (5000).
///    Каждое улучшение даёт больше урона, больше прочности и полностью чинит оружие.
///  - Каждый удар/выстрел изнашивает оружие на 1. Сломалось - исчезло.
///  - Боец погиб - оружие погибает вместе с ним. Уволили - оружие возвращается в Оружейную.
/// Это обычный C#-класс (не MonoBehaviour): лежит в списке Оружейной или в руках у бойца (CitizenAgent).
/// Все числа - в таблицах ниже, их можно спокойно менять.
/// </summary>
public class Weapon
{
    public readonly WeaponKind kind;
    public readonly QualityTier quality;
    public int upgrade;            // 0 - обычное, 1 - усиленное, 2 - позолоченное, 3 - золотое
    public float durability;
    public float maxDurability;

    public const int MaxUpgrade = 3;

    // ---------- Яд (путь Магии): лук смазывают зельем отравления в Оружейной ----------
    public int poisonCharges;                    // сколько ещё отравленных выстрелов
    public const int PoisonChargesPerPotion = 30;
    public const float PoisonDps = 4f;           // урон ядом в секунду (умножается на силу маны)
    public const float PoisonDuration = 4f;
    public bool IsPoisoned => poisonCharges > 0;

    // ---------- Замедление: лук смазывают зельем замедления в Оружейной ----------
    // Зелье варит Зельеварня (путь Магии), но купленным на рынке может пользоваться любой город.
    public int slowCharges;                      // сколько ещё замедляющих выстрелов
    public const int SlowChargesPerPotion = 25;
    public const float SlowFactor = 0.5f;        // цель ходит вдвое медленнее
    public const float SlowDuration = 2.5f;
    public bool IsSlowing => slowCharges > 0;

    // ---------- Доводка в Кузне мастера (путь Военного дела) ----------
    public bool forged;                          // доводку делают один раз
    public float forgeDamage;                    // прибавка к множителю урона
    public float forgeDurability;                // доля прибавки к прочности
    public float forgeSpeed;                     // насколько быстрее удар (доля)
    public int forgeStars;                       // 1..3 - как удалась доводка

    /// <summary>Множитель перезарядки удара (меньше - быстрее).</summary>
    public float SpeedMul => 1f - Mathf.Clamp(forgeSpeed, 0f, 0.5f);

    /// <summary>Доводка: аспекты 0..1 (точность попаданий в мини-игре), power - сила кузни.</summary>
    public void ApplyForge(float sharp, float temper, float balance, float power)
    {
        float frac = Frac;
        forged = true;
        forgeDamage = 0.3f * Mathf.Clamp01(sharp) * power;
        forgeDurability = 0.6f * Mathf.Clamp01(temper) * power;
        forgeSpeed = 0.2f * Mathf.Clamp01(balance) * power;
        float avg = (sharp + temper + balance) / 3f;
        forgeStars = avg >= 0.85f ? 3 : (avg >= 0.6f ? 2 : 1);
        maxDurability = CalcMaxDurability();
        durability = Mathf.Max(1f, frac * maxDurability);
    }

    public static string Stars(int n) { return new string('★', Mathf.Clamp(n, 0, 3)); }

    // Множитель урона бойца по качеству оружия (1.0 = боец без оружия)
    static readonly float[] QualityDamage = { 1.25f, 1.5f, 1.8f };
    // Прибавка к множителю за улучшение (индекс = уровень улучшения)
    static readonly float[] UpgradeDamage = { 0f, 0.2f, 0.45f, 0.8f };
    // Во сколько раз больше прочность после улучшения
    static readonly float[] UpgradeDurability = { 1f, 1.2f, 1.4f, 1.7f };
    // Цена улучшения до уровня 1 / 2 / 3, золото
    static readonly int[] UpgradeGold = { 1000, 2500, 5000 };

    // Прочность новой вещи (в ударах) по качеству
    static readonly float[] SwordDurability = { 500f, 700f, 1000f };
    static readonly float[] BowDurability = { 450f, 650f, 900f };
    static readonly float[] ShotDurability = { 40f, 60f, 90f };   // бронебойное ядро: выстрелов

    static readonly string[] TierNames = { "Обычный", "Хороший", "Превосходный" };
    static readonly string[] UpgradeNames = { "", "усиленный", "позолоченный", "золотой" };
    // Ядро - «оно»
    static readonly string[] TierNamesN = { "Обычное", "Хорошее", "Превосходное" };
    static readonly string[] UpgradeNamesN = { "", "усиленное", "позолоченное", "золотое" };

    public Weapon(WeaponKind kind, QualityTier quality)
    {
        this.kind = kind;
        this.quality = quality;
        maxDurability = CalcMaxDurability();
        durability = maxDurability;
    }

    float CalcMaxDurability()
    {
        float[] table = kind == WeaponKind.Sword ? SwordDurability : (kind == WeaponKind.Shot ? ShotDurability : BowDurability);
        return table[(int)quality] * UpgradeDurability[Mathf.Clamp(upgrade, 0, MaxUpgrade)] * (1f + forgeDurability);
    }

    public static string KindName(WeaponKind k) { return k == WeaponKind.Sword ? "меч" : (k == WeaponKind.Shot ? "бронебойное ядро" : "лук"); }
    public static string UpgradeName(int tier) { return UpgradeNames[Mathf.Clamp(tier, 0, MaxUpgrade)]; }
    public static int UpgradeCost(int tier) { return UpgradeGold[Mathf.Clamp(tier, 1, MaxUpgrade) - 1]; }

    // "Хороший золотой меч"
    public string Name
    {
        get
        {
            bool neuter = kind == WeaponKind.Shot;
            string up = upgrade > 0 ? (neuter ? UpgradeNamesN : UpgradeNames)[upgrade] + " " : "";
            string n = (neuter ? TierNamesN : TierNames)[(int)quality] + " " + up + KindName(kind);
            if (forged) n += (neuter ? " кованое " : " кованый ") + Stars(forgeStars);
            if (IsPoisoned) n += $" (яд: {poisonCharges})";
            if (IsSlowing) n += $" (замедл.: {slowCharges})";
            return n;
        }
    }

    // Во сколько раз оружие усиливает урон бойца
    public float DamageMul { get { return QualityDamage[(int)quality] + UpgradeDamage[Mathf.Clamp(upgrade, 0, MaxUpgrade)] + forgeDamage; } }

    // Лук бьёт дальше (меч - нет)
    public float RangeBonus { get { return kind == WeaponKind.Bow ? 0.5f * ((int)quality + 1) : 0f; } }

    public float Frac { get { return maxDurability > 0f ? Mathf.Clamp01(durability / maxDurability) : 0f; } }
    public int Percent { get { return Mathf.RoundToInt(Frac * 100f); } }

    // Чем выше - тем лучше оружие (для автовыдачи)
    public float Score { get { return DamageMul * (0.6f + 0.4f * Frac); } }

    public bool CanUpgrade { get { return upgrade < MaxUpgrade; } }

    // Улучшить: больше урон и прочность, оружие чинится полностью. Золото списывает вызывающий.
    public void ApplyUpgrade()
    {
        if (!CanUpgrade) return;
        upgrade++;
        maxDurability = CalcMaxDurability();
        durability = maxDurability;
    }

    // Цена полной починки, золото (чем лучше оружие, тем дороже)
    public int RepairCost
    {
        get
        {
            float missing = 1f - Frac;
            if (missing <= 0.001f) return 0;
            return Mathf.Max(1, Mathf.CeilToInt(missing * 100f * ((int)quality + 1) * (1 + upgrade)));
        }
    }

    public void Repair() { durability = maxDurability; }

    // Один удар / выстрел. true - оружие сломалось.
    public bool Wear()
    {
        durability -= 1f;
        return durability <= 0f;
    }

    public string StatsText
    {
        get
        {
            string range = kind == WeaponKind.Bow ? $", дальность +{RangeBonus:F1}" : "";
            if (kind == WeaponKind.Shot) return $"урон пушки ×{DamageMul:F2}, бронебойное, выстрелов осталось {Mathf.CeilToInt(durability)}";
            string speed = forgeSpeed > 0.001f ? $", удар быстрее на {forgeSpeed * 100f:F0}%" : "";
            string poison = IsPoisoned ? $", <color=#90E060>яд ещё на {poisonCharges} выстр.</color>" : "";
            string slow = IsSlowing ? $", <color=#80C0FF>замедление ещё на {slowCharges} выстр.</color>" : "";
            return $"урон ×{DamageMul:F2}{range}{speed}, прочность {Percent}%{poison}{slow}";
        }
    }
}
