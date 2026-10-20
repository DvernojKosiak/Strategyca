using UnityEngine;

/// <summary>
/// Ядро хаоса у Мага стихий (продолжение класса CitizenAgent; школа Хаоса, см. ChaosMagic).
///  - Ядро маг берёт сам в своей казарме (BarracksController.LoadChaosCores), хранит броски и перезарядку.
///  - Бросок чёрной дыры дома - ChaosMagic.TryCastHome (из ElementalStrike), в набеге - ChaosMagic.TryCastRaid.
///  - Уволили / ушёл живым - ядро с оставшимися бросками в Ратушу; погиб - ядро потеряно.
/// </summary>
public partial class CitizenAgent
{
    int chaosMk, chaosCharges;
    float chaosReadyAt;

    public bool IsElementalist => unit != null && unit.IsElementalist;
    public bool HasChaosCore => chaosCharges > 0 && unit != null && unit.IsElementalist;
    public int ChaosCoreMk => Mathf.Clamp(chaosMk, 1, 3);
    public int ChaosCharges => chaosCharges;
    public float ChaosCooldownLeft => Mathf.Max(0f, chaosReadyAt - Time.time);
    public bool ChaosReady => HasChaosCore && !dead && Time.time >= chaosReadyAt;

    /// <summary>Вставить ядро хаоса (только Маг стихий). charges = 0 - ядро целое.</summary>
    public void LoadChaosCore(ModuleKit kit)
    {
        if (unit == null || !unit.IsElementalist || kit.kind != ModuleKind.ChaosCore) return;
        chaosMk = Mathf.Clamp(kit.mk, 1, 3);
        int full = ChaosMagic.Charges(chaosMk);
        chaosCharges = kit.charges > 0 ? Mathf.Min(kit.charges, full) : full;
    }

    /// <summary>Потратить бросок: перезарядка, ядро кончилось - сообщение.</summary>
    public bool UseChaosCharge()
    {
        if (!ChaosReady) return false;
        chaosCharges--;
        chaosReadyAt = Time.time + ChaosMagic.CooldownSeconds;
        if (chaosCharges <= 0)
        {
            chaosCharges = 0;
            WarManager.Notify($"{unit.unitName} {citizenName}: ядро хаоса иссякло", false);
        }
        return true;
    }

    /// <summary>Ушёл со службы живым: ядро с остатком бросков - в Ратушу.</summary>
    void ChaosReturnCore()
    {
        if (chaosCharges > 0)
        {
            int full = ChaosMagic.Charges(ChaosCoreMk);
            EngineerYard.AddToHall(new ModuleKit { kind = ModuleKind.ChaosCore, mk = ChaosCoreMk, charges = chaosCharges < full ? chaosCharges : 0 });
        }
        chaosCharges = 0;
    }

    void ChaosLoseCore()
    {
        if (chaosCharges > 0 && unit != null) WarManager.Notify($"Погиб {unit.unitName}: ядро хаоса потеряно", false);
        chaosCharges = 0;
    }

    /// <summary>Дома: бросить чёрную дыру вместо обычного удара (если готово и врагов у цели двое и больше).</summary>
    bool TryChaosHome(EnemyAgent e)
    {
        return HasChaosCore && EnemyAgent.Alive(e) && ChaosMagic.TryCastHome(this, e.Position);
    }

    /// <summary>Строка для снаряжения: «ядро хаоса Мк.II: 3 (через 7 с)».</summary>
    public string ChaosCoreText
    {
        get
        {
            if (!HasChaosCore) return "";
            float cd = ChaosCooldownLeft;
            string wait = cd > 0.5f ? $" (через {cd:F0} с)" : "";
            return $"ядро хаоса Мк.{TowerModules.Roman(ChaosCoreMk)}: {chaosCharges}{wait}";
        }
    }
}
