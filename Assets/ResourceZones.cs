using UnityEngine;
using System.Collections.Generic;

public enum ZoneKind : byte { None, Stone, Gold, Iron, Fertile, Forest }

/// <summary>
/// Зоны ресурсов на карте. Генерируются один раз при старте (Create из BuildingPlacer.Start).
///  - Камень / Золото / Железо: шахта, стоящая в зоне, добывает больше (в центре зоны - больше, к краю - меньше).
///  - Плодородная почва: ферма даёт больше еды и овса.
///  - Лес: лесопилка даёт больше дерева. Кроме того, в лесу:
///      * все ходят медленнее (кроме дорожек), A* старается обходить лес;
///      * обзор резко короче: солдаты и враги замечают друг друга только вблизи (засады);
///      * растут ягодные кусты (минимум несколько в каждом лесу) и они отрастают быстрее.
///  - Берега рек (Rivers, 1-2 клетки от воды) - тоже плодородная почва: у самой воды богаче.
///    На самой воде зон нет.
/// Здание вне своей зоны работает как обычно. Здание на краю зоны получает бонус пропорционально покрытию.
/// Показ: галочка "Показать зоны ресурсов" в меню города (GameMenu), а также автоматически,
/// когда в руке шахта / ферма / лесопилка.
/// </summary>
public class ResourceZones : MonoBehaviour
{
    public static ResourceZones Instance { get; private set; }

    public static bool Show;       // галочка в меню города
    public static bool HintShow;   // в руке (или переносится) здание, которому важна зона

    // ---------- Настройки (можно менять) ----------
    public const float ForestSpeed = 0.6f;     // множитель скорости в лесу
    public const float ForestVision = 0.4f;    // во сколько раз короче обзор, если кто-то из двоих в лесу
    public const float ForestRegrow = 1.6f;    // ягоды в лесу отрастают быстрее
    public const float LegendHeight = 5 * 18f + 4f;

    static readonly string[] Names = { "", "Камень", "Золото", "Железо", "Плодородная почва", "Лес" };
    static readonly string[] Who = { "", "шахта", "шахта", "шахта Ур.3", "ферма", "лесопилка, ягоды" };
    // Максимальный множитель добычи в центре зоны
    static readonly float[] Rich = { 1f, 1.8f, 2.2f, 2.0f, 1.6f, 1.7f };
    static readonly Color32[] Tint =
    {
        new Color32(0, 0, 0, 0),
        new Color32(150, 165, 200, 95),   // камень
        new Color32(255, 205, 60, 115),   // золото
        new Color32(205, 105, 85, 105),   // железо
        new Color32(150, 200, 60, 95),    // почва
        new Color32(30, 150, 60, 95),     // лес
    };

    class Zone { public ZoneKind kind; public Vector2 center; public float radius; public int cells; }
    class Tree { public SpriteRenderer sr; public Vector2Int cell; }

    public int mapSize = 50;
    int N => mapSize;
    int Half => mapSize / 2;

    byte[] kind;
    byte[] zoneId;    // номер зоны + 1
    float[] power;    // множитель добычи в клетке (1 + бонус)
    readonly List<Zone> zones = new List<Zone>();
    readonly List<Tree> trees = new List<Tree>();
    float treeTimer;

    public static ResourceZones Create(int size)
    {
        GameObject go = new GameObject("ResourceZones");
        ResourceZones z = go.AddComponent<ResourceZones>();
        z.mapSize = size;
        z.Generate();
        return z;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    int Idx(Vector2Int c) { return (c.x + Half) * N + (c.y + Half); }
    bool InB(Vector2Int c) { return c.x >= -Half && c.x < Half && c.y >= -Half && c.y < Half; }

    // ---------- Генерация ----------

    void Generate()
    {
        int total = N * N;
        kind = new byte[total];
        zoneId = new byte[total];
        power = new float[total];

        float scale = (float)N / 50f;
        // Количество зон теперь масштабируется в зависимости от размера карты (чтобы карта не была пустой)
        int targetZones = Mathf.RoundToInt(10 * Mathf.Max(1f, scale * 0.8f));

        ZoneKind[] baseTypes = { ZoneKind.Stone, ZoneKind.Gold, ZoneKind.Iron, ZoneKind.Fertile, ZoneKind.Forest };
        List<ZoneKind> plan = new List<ZoneKind>();
        plan.AddRange(baseTypes);
        while (plan.Count < targetZones)
        {
            plan.Add(baseTypes[Random.Range(0, baseTypes.Length)]);
        }

        float seed = Random.value * 100f;
        for (int zi = 0; zi < plan.Count; zi++)
        {
            bool near = zi < 5;
            ZoneKind k = plan[zi];
            Zone placed = null;

            for (int attempt = 0; attempt < 400 && placed == null; attempt++)
            {
                float r = k == ZoneKind.Forest ? Random.Range(4.5f, 7.5f) : Random.Range(3.5f, 5.5f);
                // Зоны распределяются по всей доступной площади карты вплоть до границ (Half - 5)
                float minD = near ? Mathf.Max(8f, r + 2f) : Mathf.Max(12f, r + 4f);
                float maxD = Mathf.Lerp(15f, Half - 5f, (float)zi / plan.Count);
                if (maxD <= minD) maxD = minD + 2f;

                float ang = Random.value * Mathf.PI * 2f;
                Vector2 c = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * Random.Range(minD, maxD);
                if (Mathf.Abs(c.x) + r * 0.8f > Half - 1f || Mathf.Abs(c.y) + r * 0.8f > Half - 1f) continue;

                bool ok = true;
                if (attempt < 300)
                    foreach (var o in zones)
                        if (Vector2.Distance(o.center, c) < r + o.radius + 1.5f) { ok = false; break; }
                if (!ok) continue;

                placed = new Zone { kind = k, center = c, radius = r };
            }
            if (placed != null) zones.Add(placed);
        }

        // Раскладка по клеткам (края зон неровные - шум Перлина)
        for (int zi = 0; zi < zones.Count; zi++)
        {
            Zone z = zones[zi];
            int x0 = Mathf.FloorToInt(z.center.x - z.radius * 1.4f), x1 = Mathf.CeilToInt(z.center.x + z.radius * 1.4f);
            int y0 = Mathf.FloorToInt(z.center.y - z.radius * 1.4f), y1 = Mathf.CeilToInt(z.center.y + z.radius * 1.4f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    Vector2Int c = new Vector2Int(x, y);
                    if (!InB(c)) continue;
                    if (x >= -2 && x <= 3 && y >= -2 && y <= 3) continue;   // у Ратуши чисто
                    if (Rivers.IsWater(c)) continue;                         // на воде зон нет

                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), z.center);
                    float n = Mathf.PerlinNoise(x * 0.35f + seed + zi * 7.3f, y * 0.35f + seed);
                    float edge = z.radius * (0.75f + 0.5f * n);
                    if (d > edge) continue;

                    int i = Idx(c);
                    if (kind[i] != 0) continue;
                    kind[i] = (byte)z.kind;
                    zoneId[i] = (byte)(zi + 1);
                    power[i] = 1f + (Rich[(int)z.kind] - 1f) * (1f - 0.35f * d / edge);
                    z.cells++;
                }
        }

        FertileBanks();
        BuildForests();
    }

    // Берега рек: плодородная почва (если клетку не заняла другая зона). У самой воды - богаче.
    public const float BankRich = 1.45f;
    void FertileBanks()
    {
        if (!Rivers.Any) return;
        for (int x = -Half; x < Half; x++)
            for (int y = -Half; y < Half; y++)
            {
                Vector2Int c = new Vector2Int(x, y);
                if (!Rivers.IsFertileBank(c)) continue;
                int i = Idx(c);
                if (kind[i] != 0) continue;
                kind[i] = (byte)ZoneKind.Fertile;
                zoneId[i] = 0;
                power[i] = Rivers.BankDist(c) == 1 ? BankRich : 1f + (BankRich - 1f) * 0.6f;
            }
    }

    // Деревья (всегда видны) и ягодные кусты внутри лесов
    void BuildForests()
    {
        GameObject root = new GameObject("Trees");
        root.transform.SetParent(transform, false);

        Color[] greens =
        {
            new Color(0.10f, 0.32f, 0.14f), new Color(0.13f, 0.38f, 0.17f), new Color(0.08f, 0.27f, 0.12f)
        };

        for (int zi = 0; zi < zones.Count; zi++)
        {
            Zone z = zones[zi];
            if (z.kind != ZoneKind.Forest) continue;

            var cells = new List<Vector2Int>();
            for (int x = -Half; x < Half; x++)
                for (int y = -Half; y < Half; y++)
                {
                    Vector2Int c = new Vector2Int(x, y);
                    if (zoneId[Idx(c)] != zi + 1) continue;
                    cells.Add(c);

                    int count = Random.value < 0.35f ? 2 : 1;
                    for (int t = 0; t < count; t++)
                    {
                        if (Random.value < 0.1f) continue;
                        GameObject go = new GameObject("Tree");
                        go.transform.SetParent(root.transform, false);
                        float s = Random.Range(0.55f, 0.95f);
                        go.transform.position = new Vector3(x + 0.5f + Random.Range(-0.3f, 0.3f), y + 0.5f + Random.Range(-0.3f, 0.3f), 0f);
                        go.transform.localScale = new Vector3(s, s, 1f);
                        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                        sr.sprite = CombatFx.Circle();
                        sr.color = greens[Random.Range(0, greens.Length)];
                        sr.sortingOrder = -7;   // выше дорожек (-10), ниже кустов (-6) и зданий
                        trees.Add(new Tree { sr = sr, cell = c });
                    }
                }

            // Ягодные кусты в лесу: минимум 4, дальше пропорционально размеру леса
            int bushes = Mathf.Max(4, cells.Count / 12);
            for (int b = 0; b < bushes && cells.Count > 0; b++)
                for (int tryN = 0; tryN < 30; tryN++)
                {
                    Vector2Int c = cells[Random.Range(0, cells.Count)];
                    Vector2 p = new Vector2(c.x + 0.5f, c.y + 0.5f);
                    bool close = false;
                    foreach (var other in BerryBush.All)
                        if (other != null && Vector2.Distance(other.Position, p) < 1.8f) { close = true; break; }
                    if (close) continue;
                    BerryBush.Create(c, transform);
                    break;
                }
        }
    }

    // ---------- Запросы ----------

    public static ZoneKind KindAt(Vector2Int c)
    {
        var z = Instance;
        if (z == null || !z.InB(c)) return ZoneKind.None;
        return (ZoneKind)z.kind[z.Idx(c)];
    }

    public static bool IsForest(Vector2Int c) { return KindAt(c) == ZoneKind.Forest; }

    public static bool IsForestAt(Vector2 p)
    {
        return IsForest(new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)));
    }

    public static bool Visible(Vector2 a, Vector2 b, float radius)
    {
        float d = Vector2.Distance(a, b);
        if (d > radius) return false;
        if (Instance == null) return true;
        if (IsForestAt(a) || IsForestAt(b)) return d <= radius * ForestVision;
        return true;
    }

    public static float BushRegrowMul(Vector2Int c) { return IsForest(c) ? ForestRegrow : 1f; }

    public static bool IsZoneBuilding(string typeName)
    {
        return typeName == "Шахта" || typeName == "Ферма" || typeName == "Лесопилка";
    }

    public static float BuildingMult(BuildingInstance b, ZoneKind k)
    {
        var z = Instance;
        if (z == null || b == null || b.type == null) return 1f;
        Vector2Int o = b.OriginCell, sz = b.type.size;
        float sum = 0f;
        int n = Mathf.Max(1, sz.x * sz.y);
        for (int x = 0; x < sz.x; x++)
            for (int y = 0; y < sz.y; y++)
            {
                Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                if (!z.InB(c)) continue;
                int i = z.Idx(c);
                if (z.kind[i] == (byte)k) sum += z.power[i] - 1f;
            }
        return 1f + sum / n;
    }

    public static string BuildingZoneTag(BuildingInstance b)
    {
        if (Instance == null || b == null || b.type == null) return "";

        var kinds = new List<ZoneKind>();
        if (b.type.prodStone > 0) kinds.Add(ZoneKind.Stone);
        if (b.type.prodGold > 0) kinds.Add(ZoneKind.Gold);
        if (b.ProducesIron) kinds.Add(ZoneKind.Iron);
        if (b.type.prodFood > 0 || b.ProducesOats) kinds.Add(ZoneKind.Fertile);
        if (b.type.prodWood > 0) kinds.Add(ZoneKind.Forest);
        if (kinds.Count == 0 || !IsZoneBuilding(b.type.name)) return "";

        var parts = new List<string>();
        foreach (var k in kinds)
        {
            float m = BuildingMult(b, k);
            if (m > 1.01f) parts.Add($"{Names[(int)k]} ×{m:F2}");
        }
        return parts.Count > 0
            ? UiKit.Col(UiKit.GoodHex, "зона: " + string.Join(", ", parts))
            : UiKit.Col(UiKit.MutedHex, "вне зоны ресурсов");
    }

    // ---------- Подсветка на карте ----------

    GameObject overlay;

    void Update()
    {
        bool vis = Show || HintShow;
        if (vis)
        {
            if (overlay == null) CreateOverlay();
            if (!overlay.activeSelf) overlay.SetActive(true);
        }
        else if (overlay != null && overlay.activeSelf) overlay.SetActive(false);

        treeTimer -= Time.deltaTime;
        if (treeTimer <= 0f)
        {
            treeTimer = 1.5f;
            var nav = GridNavigation.Instance;
            if (nav != null)
                foreach (var t in trees)
                    if (t.sr != null) t.sr.enabled = !(nav.IsBlocked(t.cell) || nav.IsRoad(t.cell));
        }
    }

    void CreateOverlay()
    {
        Texture2D tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color32[] px = new Color32[N * N];

        for (int x = 0; x < N; x++)
            for (int y = 0; y < N; y++)
            {
                Vector2Int c = new Vector2Int(x - Half, y - Half);
                byte k = kind[Idx(c)];
                if (k == 0) { px[y * N + x] = new Color32(0, 0, 0, 0); continue; }

                Color32 col = Tint[k];
                bool edge = false;
                Vector2Int[] ns = { new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y), new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1) };
                foreach (var n in ns)
                    if (!InB(n) || kind[Idx(n)] != k) { edge = true; break; }
                if (edge) col.a = (byte)Mathf.Min(255, col.a + 80);
                px[y * N + x] = col;
            }

        tex.SetPixels32(px);
        tex.Apply();

        overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
        overlay.name = "ResourceZonesOverlay";
        overlay.transform.SetParent(transform, false);
        overlay.transform.position = new Vector3(0f, 0f, 0.8f);
        overlay.transform.localScale = new Vector3(N, N, 1f);
        Destroy(overlay.GetComponent<Collider>());

        Material mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        Renderer r = overlay.GetComponent<Renderer>();
        r.material = mat;
        r.sortingOrder = -90;
    }

    void OnGUI()
    {
        UiKit.Apply();
        if (!(Show || HintShow) || Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = false };
        foreach (var z in zones)
        {
            Vector3 sp = cam.WorldToScreenPoint(z.center);
            if (sp.z < 0f) continue;
            string text = $"{Names[(int)z.kind]}\n<size=11>×{Rich[(int)z.kind]:F1}  {Who[(int)z.kind]}</size>";
            Rect rc = new Rect(sp.x - 90f, Screen.height - sp.y - 20f, 180f, 40f);
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), text, st);
            st.normal.textColor = Color.white;
            GUI.Label(rc, text, st);
        }
    }

    public static void DrawLegend(Rect r)
    {
        GUIStyle st = new GUIStyle(UiKit.Small) { wordWrap = false, clipping = TextClipping.Clip };

        for (int k = 1; k <= 5; k++)
        {
            float y = r.y + (k - 1) * 18f;
            Color32 c = Tint[k];
            Color old = GUI.color;
            GUI.color = new Color32(c.r, c.g, c.b, 255);
            GUI.DrawTexture(new Rect(r.x + 2f, y + 4f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = old;

            string extra = k == 4 && Rivers.Any ? "ферма, берега рек" : Who[k];
            Rect lr = new Rect(r.x + 18f, y, r.width - 18f, 18f);
            GUI.Label(lr, $"<b>{Names[k]}</b> ×{Rich[k]:F1}  " + UiKit.Col(UiKit.MutedHex, extra), st);
            if (k == 5) UiKit.Tip(lr, $"Лес: лесопилка даёт больше дерева, ягоды отрастают быстрее. " +
                "Но все ходят медленнее (кроме дорожек), а обзор короче — удобно для засад.");
            else UiKit.Tip(lr, $"{Names[k]}: здание в зоне добывает до ×{Rich[k]:F1} (в центре зоны больше, к краю меньше). Подходит: {Who[k]}.");
        }
    }
}