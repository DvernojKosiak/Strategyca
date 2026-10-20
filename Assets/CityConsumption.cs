using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Потребление ресурсов городом.
/// Раз в tickInterval секунд каждое рабочее здание "кормится" из казны.
/// Здания считаются от Ратуши по кольцам: центр кормится первым,
/// при нехватке ресурса закрываются самые дальние здания (с "!" над ними).
/// Игрок ползунками задаёт, какую долю зданий (от центра) снабжать каждым ресурсом.
/// Еду здания больше не едят: её едят сами жители (дома, в Ратуше, у целителей - см. CitizenAgent).
/// </summary>
public class CityConsumption : MonoBehaviour
{
    public static CityConsumption Instance { get; private set; }

    // Порядок совпадает с ResourceManager.Spend(g, w, s, f)
    const int GOLD = 0, WOOD = 1, STONE = 2, FOOD = 3;
    static readonly string[] ResNames = { "Золото", "Дерево", "Камень", "Еда" };
    static readonly int[] UiOrder = { FOOD, WOOD, GOLD, STONE };

    [Header("Цикл потребления")]
    public float tickInterval = 5f;

    const int foodPerBuilding = 0; // еду едят люди, а не здания

    [Header("Расход одного здания за цикл (умножается на уровень)")]
    public int woodPerBuilding = 1;
    public int goldPerBuilding = 0;
    public int stonePerBuilding = 0;

    [Header("Кольца вокруг Ратуши (автоматически масштабируются под карту)")]
    public float ringWidth = 5f;   // ширина одного кольца в клетках
    public int ringsToDraw = 8;    // сколько колец рисовать на карте

    // Рацион 0..1 по каждому ресурсу: какая доля зданий (от центра) получает этот ресурс
    public float[] ration = { 1f, 1f, 1f, 1f };

    private float timer = 0f;
    private BuildingInstance hall;
    private readonly List<BuildingInstance> consumers = new List<BuildingInstance>();

    // Статистика последнего цикла (для меню)
    private readonly int[] demand = new int[4];
    private readonly int[] paid = new int[4];
    private readonly List<int> ringTotal = new List<int>();
    private readonly List<int> ringWorking = new List<int>();

    private bool showRings = false;   // кольца на карте + статистика по кольцам
    private GameObject ringRoot;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // Рационы - вкладка общего меню города (GameMenu, кнопка справа вверху / клавиша F)
    void Start()
    {
        GameMenu.AddPage("Рационы", RationsHeight, DrawRations);
        UpdateRingScaling();
    }

    void UpdateRingScaling()
    {
        BuildingPlacer placer = FindObjectOfType<BuildingPlacer>();
        int mapSize = placer != null ? placer.mapSize : 50;
        // Автоматически адаптируем ширину и количество колец под размер карты
        ringWidth = Mathf.Max(5f, mapSize / 16f);
        ringsToDraw = Mathf.CeilToInt((mapSize / 2f) / ringWidth);
    }

    int Norm(int r)
    {
        switch (r)
        {
            case GOLD: return goldPerBuilding;
            case WOOD: return woodPerBuilding;
            case STONE: return stonePerBuilding;
            default: return foodPerBuilding;
        }
    }

    int Stock(ResourceManager rm, int r)
    {
        switch (r)
        {
            case GOLD: return rm.gold;
            case WOOD: return rm.wood;
            case STONE: return rm.stone;
            default: return rm.food;
        }
    }

    void Update()
    {
        UpdateRingScaling();

        timer += Time.deltaTime;
        if (timer >= tickInterval)
        {
            timer -= tickInterval;
            Tick();
        }

        bool menuOpen = GameMenu.Instance != null && GameMenu.Instance.IsOpen;
        if (showRings && menuOpen && hall != null) // кольца видны, пока открыто меню с рационами
        {
            EnsureRings();
            ringRoot.SetActive(true);
            ringRoot.transform.position = hall.Center;
        }
        else if (ringRoot != null)
        {
            ringRoot.SetActive(false);
        }
    }

    void Tick()
    {
        ResourceManager rm = ResourceManager.Instance;
        if (rm == null) return;

        // 1. Собираем Ратушу и "едоков"
        hall = null;
        consumers.Clear();
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null) continue;
            if (b.IsTownHall) hall = b;
            else if (b.IsConsumer && b.isStaffed && !b.IsRuined) // руины не едят
            {
                // Склад забит под завязку - здание простаивает и ничего не ест, пока его не опустошат
                if (b.IsStorageFull) { b.SetWorking(true, 0); continue; }
                consumers.Add(b); // без рабочих здание не ест
            }
        }

        System.Array.Clear(demand, 0, 4);
        System.Array.Clear(paid, 0, 4);
        ringTotal.Clear();
        ringWorking.Clear();

        // Нет Ратуши - нет и системы, все работают
        if (hall == null)
        {
            foreach (var c in consumers) c.SetWorking(true, 0);
            return;
        }

        // 2. Кольцо и расстояние от центра Ратуши
        Vector2 center = hall.Center;
        foreach (var c in consumers)
        {
            c.distanceToHall = Vector2.Distance(c.Center, center);
            c.ring = Mathf.FloorToInt(c.distanceToHall / Mathf.Max(0.1f, ringWidth));
        }
        consumers.Sort((a, b) => a.distanceToHall.CompareTo(b.distanceToHall));

        // 3. Сколько зданий (от центра) игрок разрешил снабжать каждым ресурсом
        int n = consumers.Count;
        int[] allowed = new int[4];
        for (int r = 0; r < 4; r++)
            allowed[r] = Mathf.Clamp(Mathf.CeilToInt(n * ration[r] - 0.001f), 0, n);

        // 4. Кормим от центра к краю. После первого отказа все дальние здания закрываются.
        bool[] exhausted = new bool[4];
        bool stopped = false;
        int lastMask = 0;

        for (int i = 0; i < n; i++)
        {
            BuildingInstance b = consumers[i];
            int[] need = new int[4];
            need[GOLD] = goldPerBuilding * b.level;
            need[WOOD] = woodPerBuilding * b.level;
            need[STONE] = stonePerBuilding * b.level;
            need[FOOD] = foodPerBuilding * b.level;

            int mask = 0;
            for (int r = 0; r < 4; r++)
            {
                if (need[r] <= 0) continue;
                demand[r] += need[r];

                bool byRation = i >= allowed[r];
                bool byStock = exhausted[r] || Stock(rm, r) < need[r];

                if (byRation || byStock)
                {
                    mask |= 1 << r;
                    if (!byRation) exhausted[r] = true;
                }
            }

            if (mask != 0)
            {
                stopped = true;
                lastMask |= mask;
            }
            else if (stopped)
            {
                mask = lastMask;
            }

            if (mask == 0)
            {
                rm.Spend(need[GOLD], need[WOOD], need[STONE], need[FOOD]);
                for (int r = 0; r < 4; r++) paid[r] += need[r];
                b.SetWorking(true, 0);
            }
            else
            {
                b.SetWorking(false, mask);
            }
        }

        // 5. Статистика по кольцам для меню
        foreach (var c in consumers)
        {
            while (ringTotal.Count <= c.ring) { ringTotal.Add(0); ringWorking.Add(0); }
            ringTotal[c.ring]++;
            if (c.isWorking) ringWorking[c.ring]++;
        }
    }

    // ---------- Кольца на карте ----------
    void EnsureRings()
    {
        if (ringRoot != null)
        {
            // Пересоздаем кольца, если размер карты или количество колец изменились
            if (ringRoot.transform.childCount != ringsToDraw)
            {
                Destroy(ringRoot);
            }
            else return;
        }

        ringRoot = new GameObject("CityRings");
        Material mat = new Material(Shader.Find("Sprites/Default"));
        const int seg = 96;

        for (int k = 1; k <= ringsToDraw; k++)
        {
            GameObject go = new GameObject("Ring" + k);
            go.transform.SetParent(ringRoot.transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.material = mat;
            lr.widthMultiplier = 0.06f;
            lr.startColor = lr.endColor = new Color(1f, 1f, 1f, 0.25f);
            lr.sortingOrder = -50;
            lr.positionCount = seg;

            float radius = k * ringWidth;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }
        }
    }

    // ---------- UI (вкладка "Рационы" в GameMenu) ----------

    const float RowH = 40f;

    int ShownResources()
    {
        int n = 0;
        foreach (int r in UiOrder) if (Norm(r) > 0) n++;
        return n;
    }

    float RationsHeight(float width)
    {
        float h = 20f + ShownResources() * RowH + 22f + 24f;
        if (showRings) h += Mathf.Max(1, ringTotal.Count) * 18f;
        return h;
    }

    static readonly string[] ResHex = { UiKit.GoldHex, UiKit.WoodHex, UiKit.StoneHex, UiKit.FoodHex };

    void DrawRations(Rect area)
    {
        GUIStyle label = new GUIStyle(UiKit.Body) { wordWrap = false };
        GUIStyle small = UiKit.MutedStyle;

        float x = area.x, w = area.width, y = area.y;
        Rect hr = new Rect(x, y, w, 20f);
        GUI.Label(hr, "Сколько зданий снабжать (от центра) <color=" + UiKit.InfoHex + ">?</color>", small);
        UiKit.Tip(hr, "Каждый цикл рабочие здания берут ресурсы из казны — от Ратуши к краю. " +
            "Ползунок задаёт, какую долю зданий (ближних к центру) снабжать. " +
            "Если ресурса не хватает, закрываются самые дальние здания — над ними красный «!».");
        y += 20f;

        foreach (int r in UiOrder)
        {
            if (Norm(r) <= 0) continue;

            int pct = Mathf.RoundToInt(ration[r] * 100f);
            Rect lr = new Rect(x, y, w, 20f);
            GUI.Label(lr, UiKit.B(UiKit.Col(ResHex[r], ResNames[r])) + "  " + pct + "%", label);
            string payCol = paid[r] < demand[r] ? UiKit.WarnHex : UiKit.MutedHex;
            UiKit.Row(lr, "", UiKit.Col(payCol, $"{paid[r]}/{demand[r]} за цикл"));
            float v = GUI.HorizontalSlider(new Rect(x + 2f, y + 22f, w - 4f, 14f), ration[r], 0f, 1f);
            ration[r] = Mathf.Round(v * 20f) / 20f; // шаг 5%
            y += RowH;
        }

        GUI.Label(new Rect(x, y, w, 20f), $"Цикл {tickInterval:F0} с · следующий через {Mathf.Max(0f, tickInterval - timer):F0} с", small);
        y += 22f;

        showRings = GUI.Toggle(new Rect(x, y, w, 22f), showRings, " Кольца вокруг Ратуши");
        y += 24f;
        if (!showRings) return;

        if (ringTotal.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 18f), "нет зданий-потребителей", small);
            return;
        }
        for (int i = 0; i < ringTotal.Count; i++)
        {
            string col = ringWorking[i] < ringTotal[i] ? UiKit.BadHex : UiKit.GoodHex;
            UiKit.Row(new Rect(x, y, w, 18f),
                $"Кольцо {i + 1}  ({i * ringWidth:F0}–{(i + 1) * ringWidth:F0} кл.)",
                UiKit.Col(col, $"{ringWorking[i]}/{ringTotal[i]} работает"));
            y += 18f;
        }
    }

    void OnGUI()
    {
        UiKit.Apply();
        DrawAlerts();
    }

    // Красный "!" над закрытыми зданиями
    void DrawAlerts()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        float ppu = Screen.height / (2f * cam.orthographicSize); // пикселей на клетку
        int fs = Mathf.Clamp(Mathf.RoundToInt(ppu * 1.1f), 18, 64);

        GUIStyle st = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null || b.isWorking || b.IsRuined) continue;

            Vector3 sp = cam.WorldToScreenPoint(b.Center + Vector2.up * (b.type.size.y * 0.5f));
            if (sp.z < 0f) continue;

            Rect rc = new Rect(sp.x - fs, Screen.height - sp.y - fs * 1.4f, fs * 2f, fs * 1.4f);
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(rc.x + 2f, rc.y + 2f, rc.width, rc.height), "!", st);
            st.normal.textColor = UiKit.Bad;
            GUI.Label(rc, "!", st);
        }
    }
}