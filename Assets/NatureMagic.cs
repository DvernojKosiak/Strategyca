using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Школа магии Природы (DevelopmentPath.IsNature). Работает, только пока выбрана эта школа
/// (админ может переключить школу в любой момент - всё проверяется каждый тик).
///  - Постройки у рек и лесов (в пределах NearCells клеток) работают на 10% быстрее (WorkMul -> BuildingInstance.HealthFactor).
///  - Свои жители и бойцы у реки (HealRange клеток до воды) понемногу лечатся: HealPerSecond x мана.
///  - «Взор леса»: раз в PulseEvery секунд на PulseLength секунд домашний туман открывает все леса
///    (FogOfWar.RevealForests) - враги и лазутчики в лесу становятся видны.
/// Объект создаётся сам после загрузки сцены.
/// </summary>
public class NatureMagic : MonoBehaviour
{
    public static NatureMagic Instance { get; private set; }

    // ---------- Числа ----------
    public const float WorkBonus = 1.10f;       // постройки у реки / леса
    public const int NearCells = 2;             // насколько близко к воде / лесу (клеток от края постройки)
    const float RecheckEvery = 10f;             // как часто перепроверять постройку (их можно переносить)

    public const float HealPerSecond = 2f;      // у Полевого мага 9/с - здесь заметно слабее
    public const float HealRange = 2.5f;        // клеток до воды
    const float HealTick = 1f;

    public const float PulseEvery = 60f;        // «Взор леса»: раз в минуту...
    public const float PulseLength = 8f;        // ...на 8 секунд

    // ---------- Кэш для WorkMul ----------
    struct Near { public bool near; public float checkedAt; }
    static readonly Dictionary<BuildingInstance, Near> cache = new Dictionary<BuildingInstance, Near>();
    static float lastCleanup;

    float healTimer;
    float pulseTimer = PulseEvery;   // до следующего «Взора»
    float pulseLeft;                 // сколько ещё длится текущий
    static bool notifiedOnce;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        cache.Clear();
        lastCleanup = 0f;
        notifiedOnce = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("NatureMagic");
        DontDestroyOnLoad(go);   // переживает перезагрузку сцены
        go.AddComponent<NatureMagic>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            FogOfWar.RevealForests = false;
        }
    }

    public static bool ForestSightActive => Instance != null && Instance.pulseLeft > 0f;

    // ---------- Ускорение построек ----------

    /// <summary>Множитель скорости работы постройки: x1.10 у реки или леса (только Природа), иначе 1.</summary>
    public static float WorkMul(BuildingInstance b)
    {
        if (!DevelopmentPath.IsNature || b == null || b.type == null) return 1f;
        return IsNearNature(b) ? WorkBonus : 1f;
    }

    /// <summary>Постройка стоит у реки или леса (результат кэшируется на RecheckEvery секунд).</summary>
    public static bool IsNearNature(BuildingInstance b)
    {
        if (b == null || b.type == null) return false;
        float now = Time.time;
        Near n;
        if (cache.TryGetValue(b, out n) && now - n.checkedAt < RecheckEvery && now >= n.checkedAt) return n.near;

        n.near = ComputeNear(b);
        n.checkedAt = now;
        cache[b] = n;

        // Изредка выкидываем уничтоженные постройки
        if (now - lastCleanup > 30f)
        {
            lastCleanup = now;
            List<BuildingInstance> dead = null;
            foreach (var kv in cache)
                if (kv.Key == null) { if (dead == null) dead = new List<BuildingInstance>(); dead.Add(kv.Key); }
            if (dead != null) foreach (var d in dead) cache.Remove(d);
        }
        return n.near;
    }

    static bool ComputeNear(BuildingInstance b)
    {
        if (Rivers.Instance == null && ResourceZones.Instance == null) return false;
        Vector2Int o = b.OriginCell, sz = b.type.size;
        for (int x = o.x - NearCells; x < o.x + sz.x + NearCells; x++)
            for (int y = o.y - NearCells; y < o.y + sz.y + NearCells; y++)
            {
                Vector2Int c = new Vector2Int(x, y);
                if (Rivers.IsWater(c) || ResourceZones.IsForest(c)) return true;
            }
        return false;
    }

    // ---------- Тик ----------

    void Update()
    {
        if (!DevelopmentPath.IsNature)
        {
            // Школу сменили - всё выключаем и начинаем отсчёт заново
            if (FogOfWar.RevealForests) FogOfWar.RevealForests = false;
            pulseLeft = 0f;
            pulseTimer = PulseEvery;
            healTimer = 0f;
            return;
        }

        float dt = Time.deltaTime;

        healTimer -= dt;
        if (healTimer <= 0f)
        {
            healTimer = HealTick;
            HealNearRivers(HealTick);
        }

        if (pulseLeft > 0f)
        {
            pulseLeft -= dt;
            if (pulseLeft <= 0f) { pulseLeft = 0f; pulseTimer = PulseEvery; }
        }
        else
        {
            pulseTimer -= dt;
            if (pulseTimer <= 0f && ResourceZones.Instance != null)
            {
                pulseLeft = PulseLength;
                if (!notifiedOnce)
                {
                    notifiedOnce = true;
                    WarManager.Notify("Взор леса: всё, что в лесах, сейчас видно (раз в минуту)", true);
                }
            }
        }
        FogOfWar.RevealForests = pulseLeft > 0f;
    }

    void HealNearRivers(float dt)
    {
        if (!Rivers.Any) return;
        float amount = HealPerSecond * ManaManager.Factor * dt;
        if (amount <= 0f) return;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.IsInsideBuilding || a.Hp >= a.MaxHp) continue;
            Vector2 p = a.transform.position;
            Vector2 water;
            if (!Rivers.NearestWater(p, HealRange, out water)) continue;
            if (a.ReceiveHeal(amount) > 0f && FogOfWar.IsVisible(p))
                CombatFx.HealSpark(water, p);
        }
    }

    // ---------- Индикатор «Взора леса» над полоской маны ----------

    void OnGUI()
    {
        if (!DevelopmentPath.IsNature || ResourceZones.Instance == null) return;
        if (RaidManager.ViewingBattle) return;
        var clock = GameClock.Instance;
        if (clock == null || clock.IsHidden) return;

        UiKit.Apply();
        Rect p = clock.PanelRect;
        Rect r = new Rect(p.xMax - 170f, p.y - 44f, 170f, 18f);   // над полоской маны (она на p.y - 22)

        if (pulseLeft > 0f)
            UiKit.Bar(r, pulseLeft / PulseLength, UiKit.Good, $"Взор леса: {Mathf.CeilToInt(pulseLeft)} с");
        else
            UiKit.Bar(r, 1f - pulseTimer / PulseEvery, new Color(0.30f, 0.45f, 0.32f), $"Взор леса через {Mathf.CeilToInt(pulseTimer)} с");

        UiKit.Tip(r, "Школа Природы:\n" +
            $"• раз в {PulseEvery:0} с на {PulseLength:0} с туман над лесами расходится — видно врагов и лазутчиков;\n" +
            $"• свои у реки (до {HealRange:0.#} кл.) лечатся на {HealPerSecond:0.#} здоровья/с x мана (сейчас x{ManaManager.Factor:F2});\n" +
            $"• постройки у реки или леса работают на {Mathf.RoundToInt((WorkBonus - 1f) * 100f)}% быстрее.");
    }
}
