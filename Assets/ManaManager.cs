using UnityEngine;

/// <summary>
/// Мана в воздухе. С началом каждого нового дня уровень выпадает случайно: 0-100%.
/// От него зависит сила всех целителей: гражданских, военных и лазарета.
/// Эффективность = minEfficiency + мана * (maxEfficiency - minEfficiency).
/// Создаётся в BuildingPlacer.Start.
/// </summary>
public class ManaManager : MonoBehaviour
{
    public static ManaManager Instance { get; private set; }

    [Header("Эффективность магов")]
    public float minEfficiency = 0.25f;   // при 0% маны
    public float maxEfficiency = 1.25f;   // при 100% маны (при 75% ровно x1)

    public int Percent { get; private set; } = 100;
    int lastDay = -1;

    public static float Factor
    {
        get
        {
            if (Instance == null) return 1f;
            return Mathf.Lerp(Instance.minEfficiency, Instance.maxEfficiency, Instance.Percent / 100f);
        }
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Update()
    {
        var clock = GameClock.Instance;
        if (clock == null || clock.DayIndex == lastDay) return;
        lastDay = clock.DayIndex;
        Percent = Random.Range(0, 101);
        WarManager.Notify($"Новый день: мана {Percent}% — маги лечат на {Mathf.RoundToInt(Factor * 100f)}%", Percent >= 50);
    }

    void OnGUI()
    {
        UiKit.Apply();
        var clock = GameClock.Instance;
        if (clock != null && clock.IsHidden) return;
        Rect r = clock != null ? clock.PanelRect : new Rect(Screen.width / 2f - 220f, Screen.height - 42f, 440f, 34f);
        r = new Rect(r.x, r.y - 22f, r.width, 18f);

        // Тонкая полоска над часами: «Мана 64% · маги x0.89»
        float k = Percent / 100f;
        Color fill = Color.Lerp(new Color(0.40f, 0.42f, 0.52f), new Color(0.40f, 0.55f, 0.95f), k);
        UiKit.Bar(r, k, fill, $"Мана {Percent}%  ·  маги x{Factor:F2}");
        UiKit.Tip(r, "Мана в воздухе — новая каждый день (0–100%).\n" +
            $"От неё зависит сила всех целителей и магии: при 0% — x{minEfficiency:F2}, при 100% — x{maxEfficiency:F2}.");
    }
}
