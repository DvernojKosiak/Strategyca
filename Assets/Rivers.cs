using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Реки на карте города. Генерируются один раз при старте (Create из BuildingPlacer.Start, до ResourceZones).
///  - 1-2 реки текут от края до края карты, петляют (шум Перлина) и меняют ширину по длине:
///    есть узкие перекаты (1.6-2 клетки) и широкие плёсы (до ~4.5 клеток). У Ратуши рек не бывает.
///  - Вода - это клетки, а не картинка: по ним считает навигация (GridNavigation):
///      * солдаты и враги переходят вброд - медленно (WadeSpeed) и в воде не дерутся;
///      * всадники перескакивают узкие места (ширина до JumpWidth) почти не сбавляя ход;
///      * мирные жители через реку не ходят - только по мосту. Убегая от набега, тоже идут вброд (медленно).
///  - На воде обычные здания не строят - только Мост (BridgeController) и переправы Военного дела (Crossing).
///  - Берега (1-2 клетки от воды) - плодородная почва (ResourceZones): фермы там дают больше.
/// Только своя карта: на карте набега рек нет.
/// </summary>
public class Rivers : MonoBehaviour
{
    public static Rivers Instance { get; private set; }

    // ---------- Настройки (можно менять) ----------
    public const float WadeSpeed = 0.35f;     // во сколько раз медленнее идут вброд
    public const float JumpSpeed = 0.9f;      // всадник на узком месте: прыжок, почти без задержки
    public const float JumpWidth = 2.1f;      // до какой ширины всадник перескакивает реку
    const float MinWidth = 1.6f, MaxWidth = 4.6f;
    const float HallClear = 8.5f;             // ближе к Ратуше (центру карты) реки не подходят
    const int FertileBank = 2;                // сколько клеток от воды - плодородный берег
    const int PxPerCell = 8;                  // детальность картинки реки

    public int mapSize = 50;
    int N => mapSize;
    int Half => mapSize / 2;

    bool[] water;
    float[] width;      // ширина реки в этом месте (для прыжка всадника)
    byte[] bank;        // 0 - не берег, 1..3 - сколько клеток до воды
    byte[] depth;       // 1..3 - сколько клеток до берега (для цвета)
    int[] land;         // номер "берега" (связной суши без мостов)
    readonly List<List<Vector3>> rivers = new List<List<Vector3>>();   // осевые линии: x, y, ширина

    public int Count => rivers.Count;
    public static bool Any => Instance != null && Instance.rivers.Count > 0;

    public static Rivers Create(int size)
    {
        GameObject go = new GameObject("Rivers");
        Rivers r = go.AddComponent<Rivers>();
        r.mapSize = size;
        r.Generate();
        r.BuildLook();
        return r;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    int Idx(Vector2Int c) { return (c.x + Half) * N + (c.y + Half); }
    bool InB(Vector2Int c) { return c.x >= -Half && c.x < Half && c.y >= -Half && c.y < Half; }

    // ---------- Генерация ----------

    void Generate()
    {
        int total = N * N;
        water = new bool[total];
        width = new float[total];
        bank = new byte[total];
        depth = new byte[total];
        land = new int[total];
        float[] nearD = new float[total];
        for (int i = 0; i < total; i++) nearD[i] = float.MaxValue;

        int want = Random.value < 0.5f ? 1 : 2;
        for (int r = 0; r < want; r++)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                List<Vector3> pts = TryRiver();
                if (pts == null) continue;
                rivers.Add(pts);
                Stamp(pts, nearD);
                break;
            }
        }

        ComputeBanks();
        ComputeDepth();
        LabelLand();
    }

    Vector2 SidePoint(int side, float along, float h)
    {
        switch (side)
        {
            case 0: return new Vector2(-h, along);
            case 1: return new Vector2(h, along);
            case 2: return new Vector2(along, -h);
            default: return new Vector2(along, h);
        }
    }

    static int Opposite(int side) { return side ^ 1; }   // 0<->1, 2<->3

    // Одна река: осевая линия от края до края. null - не вышло (подошла к Ратуше / слишком короткая)
    List<Vector3> TryRiver()
    {
        float h = Half + 1.5f;
        int side = Random.Range(0, 4);
        int endSide = Random.value < 0.7f ? Opposite(side) : (side < 2 ? Random.Range(2, 4) : Random.Range(0, 2));
        Vector2 start = SidePoint(side, Random.Range(-Half * 0.8f, Half * 0.8f), h);
        Vector2 end = SidePoint(endSide, Random.Range(-Half * 0.8f, Half * 0.8f), h);

        float seedA = Random.value * 1000f, seedW = Random.value * 1000f;
        Vector2 hall = new Vector2(0.5f, 0.5f);
        var pts = new List<Vector3>();
        Vector2 p = start;
        float dist = 0f;
        const float step = 0.35f;
        int inside = 0;

        for (int i = 0; i < 3000; i++)
        {
            Vector2 toEnd = end - p;
            if (toEnd.magnitude < 0.8f) break;

            // Петляет: направление на устье + плавный шум. Ближе к концу шум затухает, чтобы точно дойти до края.
            float baseAng = Mathf.Atan2(toEnd.y, toEnd.x);
            float wiggle = (Mathf.PerlinNoise(seedA, dist * 0.055f) - 0.5f) * 2.6f;
            wiggle += (Mathf.PerlinNoise(seedA + 37f, dist * 0.17f) - 0.5f) * 0.7f;   // мелкие изгибы
            float ang = baseAng + wiggle * Mathf.Clamp01(toEnd.magnitude / 12f);

            // Обходим Ратушу стороной
            Vector2 fromHall = p - hall;
            float dh = fromHall.magnitude;
            if (dh < HallClear + 6f)
            {
                float away = Mathf.Atan2(fromHall.y, fromHall.x);
                float push = Mathf.Clamp01((HallClear + 6f - dh) / 6f);
                ang = Mathf.LerpAngle(ang * Mathf.Rad2Deg, away * Mathf.Rad2Deg, push * 0.6f) * Mathf.Deg2Rad;
            }

            p += new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * step;
            dist += step;

            // Ширина плавно меняется: узкие перекаты и широкие плёсы
            float wn = Mathf.PerlinNoise(seedW, dist * 0.045f);
            wn = Mathf.Clamp01((wn - 0.2f) / 0.6f);
            wn = wn * wn * (3f - 2f * wn);
            float w = Mathf.Lerp(MinWidth, MaxWidth, wn);

            if (Vector2.Distance(p, hall) - w * 0.5f < HallClear) return null;
            if (Mathf.Abs(p.x) < Half && Mathf.Abs(p.y) < Half) inside++;
            pts.Add(new Vector3(p.x, p.y, w));
        }

        if (inside * step < Half * 0.8f) return null;   // река только задела угол карты
        return pts;
    }

    void Stamp(List<Vector3> pts, float[] nearD)
    {
        foreach (var s in pts)
        {
            float r = s.z * 0.5f;
            int x0 = Mathf.FloorToInt(s.x - r - 1), x1 = Mathf.CeilToInt(s.x + r + 1);
            int y0 = Mathf.FloorToInt(s.y - r - 1), y1 = Mathf.CeilToInt(s.y + r + 1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    Vector2Int c = new Vector2Int(x, y);
                    if (!InB(c)) continue;
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s.x, s.y));
                    if (d > r) continue;
                    int i = Idx(c);
                    water[i] = true;
                    if (d < nearD[i]) { nearD[i] = d; width[i] = s.z; }
                }
        }
    }

    static readonly Vector2Int[] Dirs4 = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };

    // Берег: расстояние (по 4 соседям) от суши до воды, до 3 клеток
    void ComputeBanks()
    {
        var q = new Queue<Vector2Int>();
        for (int x = -Half; x < Half; x++)
            for (int y = -Half; y < Half; y++)
            {
                Vector2Int c = new Vector2Int(x, y);
                if (water[Idx(c)]) q.Enqueue(c);
            }
        var dist = new int[N * N];
        while (q.Count > 0)
        {
            Vector2Int c = q.Dequeue();
            int dc = water[Idx(c)] ? 0 : dist[Idx(c)];
            if (dc >= 3) continue;
            foreach (var d in Dirs4)
            {
                Vector2Int n = c + d;
                if (!InB(n)) continue;
                int ni = Idx(n);
                if (water[ni] || bank[ni] != 0) continue;
                dist[ni] = dc + 1;
                bank[ni] = (byte)(dc + 1);
                q.Enqueue(n);
            }
        }
    }

    // Глубина: расстояние от воды до берега (для цвета - в середине темнее)
    void ComputeDepth()
    {
        var q = new Queue<Vector2Int>();
        for (int x = -Half; x < Half; x++)
            for (int y = -Half; y < Half; y++)
            {
                Vector2Int c = new Vector2Int(x, y);
                int i = Idx(c);
                if (!water[i]) continue;
                foreach (var d in Dirs4)
                {
                    Vector2Int n = c + d;
                    if (InB(n) && !water[Idx(n)]) { depth[i] = 1; q.Enqueue(c); break; }
                }
            }
        while (q.Count > 0)
        {
            Vector2Int c = q.Dequeue();
            int dc = depth[Idx(c)];
            if (dc >= 3) continue;
            foreach (var d in Dirs4)
            {
                Vector2Int n = c + d;
                if (!InB(n)) continue;
                int ni = Idx(n);
                if (!water[ni] || depth[ni] != 0) continue;
                depth[ni] = (byte)(dc + 1);
                q.Enqueue(n);
            }
        }
        for (int i = 0; i < depth.Length; i++) if (water[i] && depth[i] == 0) depth[i] = 3;
    }

    // Связная суша (без мостов): чтобы стартовые здания встали на одном берегу с Ратушей
    void LabelLand()
    {
        int label = 0;
        var stack = new Stack<Vector2Int>();
        for (int x = -Half; x < Half; x++)
            for (int y = -Half; y < Half; y++)
            {
                Vector2Int s = new Vector2Int(x, y);
                int si = Idx(s);
                if (water[si] || land[si] != 0) continue;
                label++;
                land[si] = label;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    Vector2Int c = stack.Pop();
                    foreach (var d in Dirs4)
                    {
                        Vector2Int n = c + d;
                        if (!InB(n)) continue;
                        int ni = Idx(n);
                        if (water[ni] || land[ni] != 0) continue;
                        land[ni] = label;
                        stack.Push(n);
                    }
                }
            }
    }

    // ---------- Запросы ----------

    /// <summary>Клетка - вода (сама река, без учёта мостов и переправ: их учитывает GridNavigation).</summary>
    public static bool IsWater(Vector2Int c)
    {
        var r = Instance;
        if (r == null || !r.InB(c)) return false;
        return r.water[r.Idx(c)];
    }

    public static bool IsWaterAt(Vector2 p) { return IsWater(new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y))); }

    /// <summary>Ширина реки в этой клетке (0 - не вода).</summary>
    public static float WidthAt(Vector2Int c)
    {
        var r = Instance;
        if (r == null || !r.InB(c)) return 0f;
        int i = r.Idx(c);
        return r.water[i] ? r.width[i] : 0f;
    }

    public static bool IsNarrow(Vector2Int c) { float w = WidthAt(c); return w > 0f && w <= JumpWidth; }

    /// <summary>Сколько клеток до воды (1..3), 0 - далеко от реки или сама вода.</summary>
    public static int BankDist(Vector2Int c)
    {
        var r = Instance;
        if (r == null || !r.InB(c)) return 0;
        return r.bank[r.Idx(c)];
    }

    public static bool IsFertileBank(Vector2Int c) { int d = BankDist(c); return d > 0 && d <= FertileBank; }

    /// <summary>Номер берега: клетки с одним номером соединены сушей без мостов (0 - вода / вне карты).</summary>
    public static int LandRegion(Vector2Int c)
    {
        var r = Instance;
        if (r == null) return 1;
        if (!r.InB(c)) return 0;
        return r.land[r.Idx(c)];
    }

    /// <summary>Есть ли вода в радиусе от точки (Маг стихий выводит реку из берегов).</summary>
    public static bool NearWater(Vector2 p, float radius) { return NearestWater(p, radius, out _); }

    public static bool NearestWater(Vector2 p, float radius, out Vector2 at)
    {
        at = p;
        var r = Instance;
        if (r == null || r.rivers.Count == 0) return false;
        int x0 = Mathf.FloorToInt(p.x - radius), x1 = Mathf.CeilToInt(p.x + radius);
        int y0 = Mathf.FloorToInt(p.y - radius), y1 = Mathf.CeilToInt(p.y + radius);
        float best = radius;
        bool found = false;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                Vector2Int c = new Vector2Int(x, y);
                if (!r.InB(c) || !r.water[r.Idx(c)]) continue;
                Vector2 cc = new Vector2(x + 0.5f, y + 0.5f);
                float d = Vector2.Distance(cc, p);
                if (d <= best) { best = d; at = cc; found = true; }
            }
        return found;
    }

    /// <summary>Ближайшая к точке клетка суши (если точка в воде), иначе сама точка.</summary>
    public static Vector2 DryPoint(Vector2 p)
    {
        var r = Instance;
        Vector2Int c0 = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
        if (r == null || !IsWater(c0)) return p;
        for (int rad = 1; rad <= 8; rad++)
        {
            float best = float.MaxValue;
            Vector2 bestP = p;
            for (int x = -rad; x <= rad; x++)
                for (int y = -rad; y <= rad; y++)
                {
                    if (Mathf.Abs(x) != rad && Mathf.Abs(y) != rad) continue;
                    Vector2Int c = new Vector2Int(c0.x + x, c0.y + y);
                    if (!r.InB(c) || r.water[r.Idx(c)]) continue;
                    Vector2 cc = new Vector2(c.x + 0.5f, c.y + 0.5f);
                    float d = Vector2.Distance(cc, p);
                    if (d < best) { best = d; bestP = cc; }
                }
            if (best < float.MaxValue) return bestP;
        }
        return p;
    }

    // ---------- Картинка ----------

    GameObject look;

    void BuildLook()
    {
        if (rivers.Count == 0) return;
        int res = N * PxPerCell;
        Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        Color32[] px = new Color32[res * res];

        Color shallow = new Color(0.33f, 0.62f, 0.80f);
        Color deep = new Color(0.10f, 0.30f, 0.60f);
        Color sand = new Color(0.78f, 0.70f, 0.46f);
        Color soil = new Color(0.20f, 0.36f, 0.10f);
        float seed = Random.value * 100f;

        for (int py = 0; py < res; py++)
            for (int pxX = 0; pxX < res; pxX++)
            {
                // Мировые координаты пикселя
                float wx = (pxX + 0.5f) / PxPerCell - Half;
                float wy = (py + 0.5f) / PxPerCell - Half;
                float s = Sample(wx - 0.5f, wy - 0.5f, true);          // "вода" 0..1, сглажено между клетками
                float dp = Sample(wx - 0.5f, wy - 0.5f, false);        // глубина 0..3

                Color c;
                if (s > 0.45f)
                {
                    float k = Mathf.Clamp01((dp - 1f) / 2f);
                    c = Color.Lerp(shallow, deep, k);
                    // рябь
                    float ripple = Mathf.PerlinNoise(seed + wx * 1.7f, seed + wy * 0.6f + wx * 0.4f);
                    c = Color.Lerp(c, Color.white, Mathf.Max(0f, ripple - 0.72f) * 0.9f);
                    c.a = Mathf.Lerp(0.75f, 1f, Mathf.Clamp01((s - 0.45f) / 0.2f));
                }
                else if (s > 0.28f)
                {
                    c = sand;                               // песчаная кромка
                    c.a = Mathf.Clamp01((s - 0.28f) / 0.1f) * 0.85f;
                }
                else
                {
                    // Плодородный берег: трава гуще и темнее
                    Vector2Int cell = new Vector2Int(Mathf.FloorToInt(wx), Mathf.FloorToInt(wy));
                    int b = InB(cell) ? bank[Idx(cell)] : 0;
                    if (b > 0 && b <= FertileBank)
                    {
                        c = soil;
                        c.a = b == 1 ? 0.30f : 0.16f;
                    }
                    else c = new Color(0f, 0f, 0f, 0f);
                }
                px[py * res + pxX] = c;
            }

        tex.SetPixels32(px);
        tex.Apply();

        look = GameObject.CreatePrimitive(PrimitiveType.Quad);
        look.name = "RiversLook";
        look.transform.SetParent(transform, false);
        look.transform.position = new Vector3(0f, 0f, 0.9f);
        look.transform.localScale = new Vector3(N, N, 1f);
        Destroy(look.GetComponent<Collider>());
        Material mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        Renderer rr = look.GetComponent<Renderer>();
        rr.material = mat;
        rr.sortingOrder = -95;   // выше травы (-100), ниже подсветки зон (-90), дорожек и мостов
    }

    // Билинейная выборка по центрам клеток: вода (0/1) или глубина
    float Sample(float x, float y, bool isWater)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        float a = Val(x0, y0, isWater), b = Val(x0 + 1, y0, isWater);
        float c = Val(x0, y0 + 1, isWater), d = Val(x0 + 1, y0 + 1, isWater);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    float Val(int x, int y, bool isWater)
    {
        Vector2Int c = new Vector2Int(Mathf.Clamp(x, -Half, Half - 1), Mathf.Clamp(y, -Half, Half - 1));
        int i = Idx(c);
        if (isWater) return water[i] ? 1f : 0f;
        return water[i] ? depth[i] : 0f;
    }
}

/// <summary>
/// Разлив реки (Маг стихий): на несколько секунд на земле остаётся лужа - враги в ней вязнут (замедление).
/// Сам урон наносит маг в момент удара волны; лужа только держит замедление на тех, кто в неё зашёл.
/// </summary>
public class FloodPool : MonoBehaviour
{
    float radius, slow, until, tick, life;
    SpriteRenderer sr;

    public static void Create(Vector2 at, float radius, float slowFactor, float seconds)
    {
        GameObject go = new GameObject("FloodPool");
        go.transform.position = new Vector3(at.x, at.y, 0f);
        go.transform.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
        FloodPool p = go.AddComponent<FloodPool>();
        p.radius = radius;
        p.slow = slowFactor;
        p.life = Mathf.Max(0.5f, seconds);
        p.until = Time.time + p.life;
        p.sr = go.AddComponent<SpriteRenderer>();
        p.sr.sprite = CombatFx.Circle();
        p.sr.color = new Color(0.3f, 0.55f, 0.95f, 0.45f);
        p.sr.sortingOrder = -8;   // на земле: выше дорожек, ниже людей
    }

    void Update()
    {
        float left = until - Time.time;
        if (left <= 0f) { Destroy(gameObject); return; }
        Color c = sr.color;
        c.a = 0.45f * Mathf.Clamp01(left / Mathf.Min(1.5f, life));   // в конце впитывается
        sr.color = c;

        tick -= Time.deltaTime;
        if (tick > 0f) return;
        tick = 0.4f;
        Vector2 at = transform.position;
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, at) <= radius)
                SlowEffect.Apply(e, slow, 0.8f);
    }
}
