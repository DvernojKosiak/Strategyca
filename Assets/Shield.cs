using UnityEngine;

/// <summary>
/// Щит: вид качества, улучшение золотом и прочность. Делается в Оружейной, выдаётся бойцам из казармы.
/// При каждом физическом ударе врага:
///  1. шанс ПОЛНОСТЬЮ отразить удар (мал, зависит от качества и улучшений, по тяжёлым ударам ещё ниже);
///  2. иначе шанс заблокировать - тогда входящий урон режется на ReduceFraction;
///  3. иначе урон проходит полностью.
/// Каждый поглощённый удар изнашивает щит на 1. Магический урон щит не блокирует.
/// Боец погиб - щит погиб. Уволили - щит возвращается в Оружейную.
/// Все числа - в таблицах, их можно менять.
/// </summary>
public class Shield
{
    public readonly QualityTier quality;
    public int upgrade;            // 0 - обычный, 1 - усиленный, 2 - позолоченный, 3 - золотой
    public float durability;
    public float maxDurability;

    public const int MaxUpgrade = 3;

    // Шанс блока
    static readonly float[] BlockByQuality = { 0.55f, 0.65f, 0.75f };
    static readonly float[] BlockUpgrade = { 0f, 0.05f, 0.08f, 0.12f };
    // Какую долю урона срезает блок
    static readonly float[] ReduceByQuality = { 0.40f, 0.50f, 0.60f };
    static readonly float[] ReduceUpgrade = { 0f, 0.05f, 0.10f, 0.15f };
    // Шанс полного отражения
    static readonly float[] ReflectByQuality = { 0.02f, 0.035f, 0.05f };
    static readonly float[] ReflectUpgrade = { 0f, 0.01f, 0.02f, 0.04f };
    // Прочность (в поглощённых ударах)
    static readonly float[] DurabilityByQuality = { 250f, 380f, 550f };
    static readonly float[] UpgradeDurability = { 1f, 1.2f, 1.4f, 1.7f };
    // Цена улучшения до уровня 1 / 2 / 3, золото
    static readonly int[] UpgradeGold = { 800, 2000, 4000 };

    static readonly string[] TierNames = { "Обычный", "Хороший", "Превосходный" };
    static readonly string[] UpgradeNames = { "", "усиленный", "позолоченный", "золотой" };

    public Shield(QualityTier quality)
    {
        this.quality = quality;
        maxDurability = CalcMaxDurability();
        durability = maxDurability;
    }

    float CalcMaxDurability()
    {
        return DurabilityByQuality[(int)quality] * UpgradeDurability[Mathf.Clamp(upgrade, 0, MaxUpgrade)];
    }

    public static string UpgradeName(int tier) { return UpgradeNames[Mathf.Clamp(tier, 0, MaxUpgrade)]; }
    public static int UpgradeCost(int tier) { return UpgradeGold[Mathf.Clamp(tier, 1, MaxUpgrade) - 1]; }

    public string Name
    {
        get
        {
            string up = upgrade > 0 ? UpgradeNames[upgrade] + " " : "";
            return TierNames[(int)quality] + " " + up + "щит";
        }
    }

    int U { get { return Mathf.Clamp(upgrade, 0, MaxUpgrade); } }

    public float BlockChance { get { return Mathf.Clamp01(BlockByQuality[(int)quality] + BlockUpgrade[U]); } }
    public float ReduceFraction { get { return Mathf.Clamp01(ReduceByQuality[(int)quality] + ReduceUpgrade[U]); } }
    public float ReflectChance { get { return Mathf.Clamp01(ReflectByQuality[(int)quality] + ReflectUpgrade[U]); } }

    // Тяжёлые удары (рыцарь) отразить труднее, лёгкие (стрела) - легче
    public float ReflectChanceVs(float dmg)
    {
        return Mathf.Clamp01(ReflectChance * Mathf.Clamp(12f / Mathf.Max(1f, dmg), 0.5f, 1.25f));
    }

    public float Frac { get { return maxDurability > 0f ? Mathf.Clamp01(durability / maxDurability) : 0f; } }
    public int Percent { get { return Mathf.RoundToInt(Frac * 100f); } }

    // Чем выше - тем лучше щит (для автовыдачи)
    public float Score { get { return (BlockChance * ReduceFraction + ReflectChance) * (0.6f + 0.4f * Frac); } }

    public bool CanUpgrade { get { return upgrade < MaxUpgrade; } }

    public void ApplyUpgrade()
    {
        if (!CanUpgrade) return;
        upgrade++;
        maxDurability = CalcMaxDurability();
        durability = maxDurability;
    }

    // Цена полной починки, золото
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

    // Один поглощённый удар. true - щит сломался.
    public bool Wear()
    {
        durability -= 1f;
        return durability <= 0f;
    }

    public string StatsText
    {
        get
        {
            return $"блок {BlockChance * 100f:F0}% (−{ReduceFraction * 100f:F0}%), отраж. {ReflectChance * 100f:F1}%, проч. {Percent}%";
        }
    }
}

/// <summary>
/// Щит бойца. Вешается на жителя-солдата, когда ему выдают щит (CitizenAgent менять не нужно).
/// Враги перед ударом вызывают ShieldBearer.Mitigate (см. EnemyAgent).
/// Боец погиб - щит потерян. Бойца уволили или убрали - щит возвращается в Оружейную.
/// </summary>
public class ShieldBearer : MonoBehaviour
{
    CitizenAgent agent;
    float checkTimer;

    public Shield Current { get; private set; }

    public static Shield ShieldOf(CitizenAgent a)
    {
        ShieldBearer b = a != null ? a.GetComponent<ShieldBearer>() : null;
        return b != null ? b.Current : null;
    }

    public static bool Equip(CitizenAgent a, Shield s)
    {
        if (a == null || s == null || !a.IsSoldier || a.IsDead) return false;
        ShieldBearer b = a.GetComponent<ShieldBearer>();
        if (b == null) b = a.gameObject.AddComponent<ShieldBearer>();
        if (b.Current != null) return false;
        b.Current = s;
        return true;
    }

    public static Shield Unequip(CitizenAgent a)
    {
        ShieldBearer b = a != null ? a.GetComponent<ShieldBearer>() : null;
        if (b == null) return null;
        Shield s = b.Current;
        b.Current = null;
        return s;
    }

    // Уволили: щит на склад Оружейной
    public static void ReturnToArmory(CitizenAgent a)
    {
        Shield s = Unequip(a);
        if (s != null && !ArmoryController.StashShield(s))
            WarManager.Notify("В Оружейных нет места: щит уволенного бойца потерян", false);
    }

    public static string TextOf(CitizenAgent a)
    {
        Shield s = ShieldOf(a);
        return s != null ? $"{s.Name} ({s.Percent}%)" : "";
    }

    /// <summary>
    /// Что останется от урона после щита. magical - магический урон щит не берёт.
    /// by - нападающий (при отражении удар улетает обратно стрелой). Потом удар держит броня бойца (MitigateBody).
    /// </summary>
    public static float Mitigate(CitizenAgent target, float dmg, EnemyAgent by, bool magical = false)
    {
        if (magical || dmg <= 0f || target == null) return dmg;
        ShieldBearer b = target.GetComponent<ShieldBearer>();
        if (b == null || b.Current == null) return target.MitigateBody(dmg, false);   // без щита - только броня / плащ

        Shield s = b.Current;

        // 1. Полное отражение
        if (Random.value < s.ReflectChanceVs(dmg))
        {
            if (by != null) CombatFx.Arrow(target.transform.position, by.Position);
            b.Absorb(s, target);
            return 0f;
        }

        // 2. Обычный блок
        if (Random.value < s.BlockChance)
        {
            dmg *= 1f - s.ReduceFraction;
            b.Absorb(s, target);
        }
        // 3. Что прошло мимо щита - держит броня (CitizenAgent_Gear)
        return target.MitigateBody(dmg, false);
    }

    void Absorb(Shield s, CitizenAgent owner)
    {
        if (!s.Wear()) return;
        Current = null;
        WarManager.Notify($"Сломался щит: {s.Name} ({(owner != null ? owner.UnitName : "")})", false);
    }

    void Awake() { agent = GetComponent<CitizenAgent>(); }

    void Update()
    {
        checkTimer -= Time.deltaTime;
        if (checkTimer > 0f) return;
        checkTimer = 0.5f;

        // Бойца уволили (или казарму снесли) - щит возвращается на склад
        if (Current != null && agent != null && !agent.IsDead && !agent.IsSoldier) ReturnToArmory(agent);
    }

    void OnDestroy()
    {
        if (Current == null) return;
        Shield s = Current;
        Current = null;

        if (agent != null && agent.IsDead)
            WarManager.Notify($"Погиб {agent.UnitName}: вместе с ним потерян щит — {s.Name}", false);
        else if (!ArmoryController.StashShield(s))
            WarManager.Notify("В Оружейных нет места: щит потерян", false);
    }
}