using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Игровые часы: день (20 мин) + ночь (10 мин), неделя из 7 дней (5 рабочих + 2 выходных).
/// Показывает время внизу экрана, затемняет карту ночью и управляет скоростью игры.
/// Клавиши: F1 - пауза, F2 - x1, F3 - x2, F4 - x4, F5 - x10, F10 - спрятать / показать панель.
/// </summary>
public class GameClock : MonoBehaviour
{
    public static GameClock Instance { get; private set; }

    [Header("Длительность (реальные минуты при скорости x1)")]
    public float dayMinutes = 20f;
    public float nightMinutes = 10f;

    [Header("Неделя")]
    public int daysPerWeek = 7;
    public int workDaysPerWeek = 5;

    [Header("Ночная смена и выходные")]
    public int skeletonCrew = 1;       // сколько человек остаётся на производстве ночью и в выходные
    public int skeletonCrewBig = 2;    // сколько остаётся на большом здании (от bigBuildingLevel)
    public int bigBuildingLevel = 3;

    [Header("Затемнение ночью")]
    public float nightDarkness = 0.45f;
    public float twilightSeconds = 60f;

    static readonly string[] DayNames = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
    static readonly float[] Speeds = { 0f, 1f, 2f, 4f, 10f };
    static readonly string[] SpeedLabels = { "II", "x1", "x2", "x4", "x10" };

    private float timeInDay = 0f;   // секунд с начала суток (сутки начинаются с утра, 06:00)
    private int dayIndex = 0;       // сколько суток прошло (0 = первый понедельник)
    private int speedIndex = 1;
    private int lastSpeedIndex = 1;
    private SpriteRenderer overlay;
    private bool hidden = false;   // F10 - спрятать нижнюю панель (часы, скорость, мана)

    public bool IsHidden => hidden;

    float DayLen => dayMinutes * 60f;
    float NightLen => nightMinutes * 60f;
    float TotalLen => DayLen + NightLen;

    public bool IsNight => timeInDay >= DayLen;
    public int DayIndex => dayIndex;
    public int DayOfWeekIndex => dayIndex % Mathf.Max(1, daysPerWeek);
    public bool IsWeekend => DayOfWeekIndex >= workDaysPerWeek;

    // Сколько человек дежурит в здании данного уровня, когда не рабочее время (остальные идут домой)
    public int NightCrewFor(int level)
    {
        return level >= bigBuildingLevel ? skeletonCrewBig : skeletonCrew;
    }

    // Рабочее время: будний день, не ночь
    public bool IsWorkTime => !IsNight && !IsWeekend;

    // Игровые часы 0..24 (сутки начинаются в 06:00, день идёт до 22:00, потом ночь)
    public float Hours => (6f + timeInDay / TotalLen * 24f) % 24f;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        CreateOverlay();
    }

    void OnDestroy()
    {
        if (Instance == this) Time.timeScale = 1f;
    }

    void CreateOverlay()
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

        GameObject go = new GameObject("NightOverlay");
        go.transform.SetParent(transform, false);
        go.transform.localScale = new Vector3(400f, 400f, 1f);

        overlay = go.AddComponent<SpriteRenderer>();
        overlay.sprite = sp;
        overlay.sortingOrder = 50;
        overlay.color = new Color(0.04f, 0.06f, 0.18f, 0f);
    }

    void SetSpeed(int index)
    {
        speedIndex = Mathf.Clamp(index, 0, Speeds.Length - 1);
        if (speedIndex > 0) lastSpeedIndex = speedIndex;
        Time.timeScale = Speeds[speedIndex];
    }

    void Update()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.f1Key.wasPressedThisFrame) SetSpeed(speedIndex == 0 ? lastSpeedIndex : 0);
            if (Keyboard.current.f2Key.wasPressedThisFrame) SetSpeed(1);
            if (Keyboard.current.f3Key.wasPressedThisFrame) SetSpeed(2);
            if (Keyboard.current.f4Key.wasPressedThisFrame) SetSpeed(3);
            if (Keyboard.current.f5Key.wasPressedThisFrame) SetSpeed(4);
            if (Keyboard.current.f10Key.wasPressedThisFrame) hidden = !hidden;
        }

        timeInDay += Time.deltaTime;
        while (timeInDay >= TotalLen)
        {
            timeInDay -= TotalLen;
            dayIndex++;
        }

        if (overlay != null)
        {
            Color c = overlay.color;
            c.a = Darkness() * nightDarkness;
            overlay.color = c;
        }
    }

    // 0 = светло, 1 = полная ночь. Плавный рассвет и закат.
    float Darkness()
    {
        float tw = Mathf.Min(twilightSeconds, DayLen * 0.5f);
        if (tw <= 0.01f) return IsNight ? 1f : 0f;

        if (IsNight) return 1f;
        if (timeInDay < tw) return 1f - timeInDay / tw;          // рассвет
        if (timeInDay > DayLen - tw) return (timeInDay - (DayLen - tw)) / tw; // закат
        return 0f;
    }

    // ---------- UI ----------
    const float PanelW = 440f, PanelH = 34f;
    const float SpeedBtnW = 40f;

    public Rect PanelRect => new Rect(Screen.width / 2f - PanelW / 2f, Screen.height - PanelH - 8f, PanelW, PanelH);

    public bool IsPointerOver(Vector2 guiPoint)
    {
        return !hidden && PanelRect.Contains(guiPoint);
    }

    void OnGUI()
    {
        UiKit.Apply();
        if (hidden) return;
        Rect p = PanelRect;
        GUI.Box(p, GUIContent.none);

        // Слева: день недели и время, ниже мелко - фаза суток
        int h = Mathf.FloorToInt(Hours);
        int m = Mathf.FloorToInt((Hours - h) * 60f);
        string day = DayNames[DayOfWeekIndex % DayNames.Length];
        string phase = IsNight ? UiKit.Col(UiKit.InfoHex, "ночь") : UiKit.Col(UiKit.AccentHex, "день");
        if (IsWeekend) phase += UiKit.Col(UiKit.MutedHex, " · выходной");

        GUIStyle timeSt = new GUIStyle(UiKit.Title) { alignment = TextAnchor.MiddleLeft };
        timeSt.normal.textColor = IsNight ? UiKit.Info : UiKit.Text;
        Rect timeR = new Rect(p.x + 12f, p.y + 2f, 96f, p.height - 4f);
        GUI.Label(timeR, $"{day}  {h:00}:{m:00}", timeSt);

        GUIStyle phaseSt = new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
        Rect phaseR = new Rect(timeR.xMax + 2f, p.y + 2f, 110f, p.height - 4f);
        GUI.Label(phaseR, phase, phaseSt);
        UiKit.Tip(new Rect(timeR.x, timeR.y, phaseR.xMax - timeR.x, timeR.height),
            $"День {dayIndex + 1}. Сутки: день {dayMinutes:F0} мин + ночь {nightMinutes:F0} мин (при x1).\n" +
            $"Неделя: {workDaysPerWeek} рабочих дней и {daysPerWeek - workDaysPerWeek} выходных. Ночью и в выходные на производстве остаётся дежурная смена.\n" +
            "F10 — спрятать панель");

        // Справа: скорость - компактная группа вкладок
        float gw = SpeedBtnW * Speeds.Length;
        Rect seg = new Rect(p.xMax - gw - 8f, p.y + 4f, gw, p.height - 8f);
        int pick = UiKit.Tabs(seg, SpeedLabels, speedIndex);
        if (pick != speedIndex) SetSpeed(pick);
        UiKit.Tip(seg, "Скорость игры\nF1 — пауза / продолжить, F2 — x1, F3 — x2, F4 — x4, F5 — x10");
    }
}
