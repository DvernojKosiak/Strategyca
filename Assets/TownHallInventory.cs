using UnityEngine;
using System.Collections.Generic;
using System.Text;

public enum QualityTier
{
    Medium, // Среднее
    Good,   // Хорошее
    Best    // Наилучшее
}

public class TownHallStorage : MonoBehaviour
{
    public static TownHallStorage Instance { get; private set; }

    // Словари хранения материалов с атрибутом NonSerialized для Unity 6
    [System.NonSerialized]
    public Dictionary<QualityTier, int> planks = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    [System.NonSerialized]
    public Dictionary<QualityTier, int> stonePlates = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    // Сталь: делают в мастерской Ур.2 из железной руды
    [System.NonSerialized]
    public Dictionary<QualityTier, int> steel = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    // Укреплённые плиты: мастерская Ур.3 из плиты, стали и камня
    [System.NonSerialized]
    public Dictionary<QualityTier, int> reinforced = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    // Смоляной брус и монолитное основание: мастерская Ур.3 (из смоляной пропитки)
    [System.NonSerialized]
    public Dictionary<QualityTier, int> beams = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    [System.NonSerialized]
    public Dictionary<QualityTier, int> monolith = new Dictionary<QualityTier, int>()
    {
        { QualityTier.Medium, 0 },
        { QualityTier.Good, 0 },
        { QualityTier.Best, 0 }
    };

    Dictionary<QualityTier, int> Dict(RefinedKind kind)
    {
        switch (kind)
        {
            case RefinedKind.Planks: return planks;
            case RefinedKind.StonePlates: return stonePlates;
            case RefinedKind.ReinforcedPlates: return reinforced;
            case RefinedKind.TarredBeams: return beams;
            case RefinedKind.Monolith: return monolith;
            default: return steel;
        }
    }

    public void AddSteel(QualityTier quality, int amount)
    {
        steel[quality] += amount;
        Debug.Log($"[Склад Ратуши] Добавлено стали ({quality}): +{amount}. Всего: {steel[quality]}");
    }

    public void Add(RefinedKind kind, QualityTier quality, int amount)
    {
        if (amount <= 0) return;
        Dict(kind)[quality] += amount;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public void AddPlanks(QualityTier quality, int amount)
    {
        planks[quality] += amount;
        Debug.Log($"[Склад Ратуши] Добавлено досок ({quality}): +{amount}. Всего: {planks[quality]}");
    }

    public void AddStonePlates(QualityTier quality, int amount)
    {
        stonePlates[quality] += amount;
        Debug.Log($"[Склад Ратуши] Добавлено плит ({quality}): +{amount}. Всего: {stonePlates[quality]}");
    }

    // Сколько материала этого вида и качества лежит на складе
    public int Get(RefinedKind kind, QualityTier quality)
    {
        var d = Dict(kind);
        return d.TryGetValue(quality, out int v) ? v : 0;
    }

    // Списать материал со склада. false - не хватает, ничего не списано.
    public bool TrySpend(RefinedKind kind, QualityTier quality, int amount)
    {
        if (amount <= 0) return true;
        var d = Dict(kind);
        if (!d.TryGetValue(quality, out int v) || v < amount) return false;
        d[quality] = v - amount;
        return true;
    }

    // Список для панели Ратуши: только то, что реально есть на складе (нули не показываем)
    public string BuildListText(out int lines)
    {
        StringBuilder sb = new StringBuilder();
        lines = 0;

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = planks[q];
            if (n <= 0) continue;
            sb.Append($" • Доски ({TierName(q).ToLower()}): {n} шт.\n");
            lines++;
        }

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = stonePlates[q];
            if (n <= 0) continue;
            sb.Append($" • Плиты ({TierName(q).ToLower()}): {n} шт.\n");
            lines++;
        }

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = steel[q];
            if (n <= 0) continue;
            sb.Append($" • Сталь ({TierName(q).ToLower()}): {n} шт.\n");
            lines++;
        }

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = reinforced[q];
            if (n <= 0) continue;
            sb.Append($" • Укреплённые плиты ({TierName(q).ToLower()}): {n} шт.\n");
            lines++;
        }

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = beams[q];
            if (n <= 0) continue;
            sb.Append($" • <color=#D8A060>Смоляной брус ({TierName(q).ToLower()}): {n} шт.</color>\n");
            lines++;
        }

        foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
        {
            int n = monolith[q];
            if (n <= 0) continue;
            sb.Append($" • <color=#C0B0A0>Монолитное основание ({TierName(q).ToLower()}): {n} шт.</color>\n");
            lines++;
        }

        var rm = ResourceManager.Instance;

        // Смола (Лесопилка Ур.3) и что из неё делают
        if (rm != null && rm.resin > 0) { sb.Append($" • <color=#E0A040>Смола: {rm.resin}</color>\n"); lines++; }
        if (rm != null && rm.tar > 0) { sb.Append($" • <color=#C08040>Смоляная пропитка: {rm.tar}</color>\n"); lines++; }
        if (rm != null && rm.rubber > 0) { sb.Append($" • <color=#A0A0B0>Резина: {rm.rubber}</color>\n"); lines++; }
        if (rm != null && rm.slowPotions > 0) { sb.Append($" • <color=#80C0FF>Зелья замедления: {rm.slowPotions}</color>\n"); lines++; }
        int kits = EngineerYard.HallKitCount;
        if (kits > 0) { sb.Append($" • <color=#FFC870>Модули для построек: {kits}</color>\n"); lines++; }

        // Железная руда лежит в казне (ResourceManager), но сверху не показывается - только здесь
        int ore = rm != null ? rm.iron : 0;
        if (ore > 0)
        {
            sb.Append($" • <color=#B0C4DE>Железная руда: {ore}</color>\n");
            lines++;
        }

        // Овёс (ферма Ур.3) - тоже только здесь
        int oats = rm != null ? rm.oats : 0;
        if (oats > 0)
        {
            sb.Append($" • <color=#E8D890>Овёс: {oats}</color>\n");
            lines++;
        }

        // Ягоды (Станция собирателей) - тоже только здесь
        int berries = rm != null ? rm.berries : 0;
        if (berries > 0)
        {
            sb.Append($" • <color=#F0A0C8>Ягоды: {berries}</color>\n");
            lines++;
        }

        // Травы и зелья (путь Магии) - только если они есть
        int herbs = rm != null ? rm.herbs : 0;
        if (herbs > 0)
        {
            sb.Append($" • <color=#90E060>Травы: {herbs}</color>\n");
            lines++;
        }
        int potionsAll = PotionHouse.TotalPotions;   // Ратуша + Хранилища
        if (potionsAll > 0)
        {
            sb.Append($" • <color=#B0E080>Зелья отравления: {potionsAll}</color>\n");
            lines++;
        }

        // Детали (путь Инженерии): отливает Литейная, тратит Инженерный двор
        int parts = rm != null ? rm.parts : 0;
        if (parts > 0)
        {
            sb.Append($" • <color=#FFC870>Детали: {parts}</color>\n");
            lines++;
        }

        // Похлёбка хранится на Кухнях - показываем общий запас
        int pottage = KitchenController.PortionsAvailable;
        if (pottage > 0)
        {
            sb.Append($" • <color=#FFB080>Похлёбка (на кухнях): {pottage} порц.</color>\n");
            lines++;
        }

        if (lines == 0)
        {
            sb.Append("<i>Склад пуст. Доски, плиты и сталь делают в мастерской.</i>");
            lines = 2;
        }
        return sb.ToString();
    }

    public static string TierName(QualityTier quality)
    {
        switch (quality)
        {
            case QualityTier.Medium: return "Среднее";
            case QualityTier.Good: return "Хорошее";
            case QualityTier.Best: return "Наилучшее";
            default: return "";
        }
    }

    public string GetQualityName(QualityTier quality)
    {
        return TierName(quality);
    }
}
