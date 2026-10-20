using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Зелёная зона застройки.
///  - Вокруг Ратуши есть зона (радиус растёт с уровнем Ратуши).
///  - Каждый целый Аванпост, подключённый к зоне, расширяет её своим радиусом (цепочкой).
///  - Аванпост подключён, если касается зоны или стоит вплотную к дороге, ведущей из зоны.
///  - Обычные здания строятся только в зоне. Дорожки и брусчатку можно продлевать за зону,
///    но только от зоны или от уже подключённой дороги.
/// Зона пересчитывается лениво, когда MarkDirty() сообщает об изменениях.
/// </summary>
public class Territory : MonoBehaviour
{
    public static Territory Instance { get; private set; }

    static readonly float[] HallRadius = { 10f, 12f, 14f };
    public static float HallRadiusFor(int level) { return HallRadius[Mathf.Clamp(level, 1, HallRadius.Length) - 1]; }

    public int mapSize = 50;
    int N => mapSize;
    int Half => mapSize / 2;

    bool[] zone, roadMap, linked;
    bool dirty = true;
    int version;
    int zoneCells;
    readonly List<BuildingInstance> activeOutposts = new List<BuildingInstance>();
    readonly List<BuildingInstance> idleOutposts = new List<BuildingInstance>();
    readonly Queue<int> queue = new Queue<int>();

    public static Territory Create(int size)
    {
        GameObject go = new GameObject("Territory");
        Territory t = go.AddComponent<Territory>();
        t.mapSize = size;
        t.dirty = true;
        return t;
    }

    public static void MarkDirty()
    {
        if (Instance != null) Instance.dirty = true;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // ---------- Индексы ----------
    int Idx(Vector2Int c) { return (c.x + Half) * N + (c.y + Half); }
    Vector2Int FromIdx(int i) { return new Vector2Int(i / N - Half, i % N - Half); }
    bool InBounds(Vector2Int c) { return c.x >= -Half && c.x < Half && c.y >= -Half && c.y < Half; }

    void Ensure()
    {
        if (dirty || zone == null) Recompute();
    }

    // ---------- Пересчёт ----------
    void Recompute()
    {
        int total = N * N;
        if (zone == null || zone.Length != total)
        {
            zone = new bool[total];
            roadMap = new bool[total];
            linked = new bool[total];
        }
        else
        {
            System.Array.Clear(zone, 0, total);
            System.Array.Clear(roadMap, 0, total);
            System.Array.Clear(linked, 0, total);
        }
        zoneCells = 0;
        activeOutposts.Clear();
        idleOutposts.Clear();

        BuildingInstance hall = null;
        List<BuildingInstance> pending = new List<BuildingInstance>();
        List<BuildingInstance> unsupplied = new List<BuildingInstance>();   // заброшенные (нет еды / связи с Ратушей)

        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null) continue;
            if (b.IsRoad)
            {
                Vector2Int o = b.OriginCell;
                for (int x = 0; x < b.type.size.x; x++)
                    for (int y = 0; y < b.type.size.y; y++)
                    {
                        Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                        if (InBounds(c)) roadMap[Idx(c)] = true;
                    }
                continue;
            }
            if (b.IsTownHall) hall = b;
            else if (b.IsOutpost && !b.IsRuined)
            {
                if (OutpostController.IsSupplied(b)) pending.Add(b);
                else unsupplied.Add(b);
            }
        }

        if (hall != null) AddArea(hall, HallRadiusFor(hall.level));

        // Аванпосты подключаются по цепочке: новая зона открывает дороги к следующим
        bool changed = true;
        while (changed && pending.Count > 0)
        {
            changed = false;
            FloodRoads();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (!Touches(pending[i])) continue;
                AddArea(pending[i], OutpostController.RadiusFor(pending[i].level));
                activeOutposts.Add(pending[i]);
                pending.RemoveAt(i);
                changed = true;
            }
        }
        FloodRoads();
        idleOutposts.AddRange(pending);
        idleOutposts.AddRange(unsupplied);

        zoneCells = 0;
        for (int i = 0; i < total; i++) if (zone[i]) zoneCells++;

        dirty = false;
        version++;
    }

    void AddArea(BuildingInstance b, float radius)
    {
        int total = N * N;
        for (int i = 0; i < total; i++)
        {
            if (zone[i]) continue;
            Vector2Int c = FromIdx(i);
            if (b.DistanceTo(new Vector2(c.x + 0.5f, c.y + 0.5f)) <= radius) zone[i] = true;
        }
    }

    bool NearZone(Vector2Int c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int n = new Vector2Int(c.x + dx, c.y + dy);
                if (InBounds(n) && zone[Idx(n)]) return true;
            }
        return false;
    }

    bool NearZoneOrLinked(Vector2Int c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int n = new Vector2Int(c.x + dx, c.y + dy);
                if (!InBounds(n)) continue;
                int i = Idx(n);
                if (zone[i] || linked[i]) return true;
            }
        return false;
    }

    // Дорожные клетки, соединённые с зоной (идём от зоны по дорогам, 8 соседей)
    void FloodRoads()
    {
        System.Array.Clear(linked, 0, linked.Length);
        queue.Clear();
        int total = N * N;
        for (int i = 0; i < total; i++)
        {
            if (!roadMap[i]) continue;
            if (zone[i] || NearZone(FromIdx(i)))
            {
                linked[i] = true;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            Vector2Int c = FromIdx(queue.Dequeue());
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    Vector2Int n = new Vector2Int(c.x + dx, c.y + dy);
                    if (!InBounds(n)) continue;
                    int j = Idx(n);
                    if (roadMap[j] && !linked[j])
                    {
                        linked[j] = true;
                        queue.Enqueue(j);
                    }
                }
        }
    }

    // Аванпост касается зоны или подключённой дороги
    bool Touches(BuildingInstance b)
    {
        Vector2Int o = b.OriginCell;
        for (int x = 0; x < b.type.size.x; x++)
            for (int y = 0; y < b.type.size.y; y++)
            {
                Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                if (!InBounds(c)) continue;
                if (zone[Idx(c)] || NearZoneOrLinked(c)) return true;
            }
        return false;
    }

    // ---------- Запросы ----------
    public bool InZone(Vector2Int c)
    {
        if (!InBounds(c)) return false;
        Ensure();
        return zone[Idx(c)];
    }

    /// <summary>Можно ли поставить здание этого типа на эти клетки (по правилам зоны).</summary>
    public bool CanPlace(string typeName, List<Vector2Int> footprint)
    {
        Ensure();

        if (BuildingInstance.IsRoadName(typeName))
        {
            foreach (var c in footprint)
            {
                if (!InBounds(c)) return false;
                if (!zone[Idx(c)] && !NearZoneOrLinked(c)) return false;
            }
            return true;
        }

        if (typeName == OutpostController.TypeName)
        {
            foreach (var c in footprint)
            {
                if (!InBounds(c)) continue;
                if (zone[Idx(c)] || NearZoneOrLinked(c)) return true;
            }
            return false;
        }

        foreach (var c in footprint)
        {
            if (!InBounds(c) || !zone[Idx(c)]) return false;
        }
        return true;
    }

    public bool IsActive(BuildingInstance b)
    {
        Ensure();
        return activeOutposts.Contains(b);
    }

    public int ActiveOutposts { get { Ensure(); return activeOutposts.Count; } }
    public int IdleOutposts { get { Ensure(); return idleOutposts.Count; } }
    public int ZoneCells { get { Ensure(); return zoneCells; } }

    // ---------- Подсветка на карте ----------
    GameObject overlay;
    Texture2D tex;
    Color32[] px;
    int shownVersion = -1;
    bool shownFrontier;
    bool visible, wantFrontier;

    public void SetOverlay(bool show, bool frontier)
    {
        visible = show;
        wantFrontier = frontier;
    }

    void Update()
    {
        if (!visible)
        {
            if (overlay != null && overlay.activeSelf) overlay.SetActive(false);
            return;
        }

        Ensure();
        if (overlay == null) CreateOverlay();
        if (!overlay.activeSelf) overlay.SetActive(true);
        if (shownVersion != version || shownFrontier != wantFrontier) Paint();
    }

    void CreateOverlay()
    {
        tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        px = new Color32[N * N];

        overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
        overlay.name = "TerritoryOverlay";
        overlay.transform.SetParent(transform, false);
        overlay.transform.position = new Vector3(0f, 0f, 0.9f);
        overlay.transform.localScale = new Vector3(N, N, 1f);
        Destroy(overlay.GetComponent<Collider>());

        Material mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        Renderer r = overlay.GetComponent<Renderer>();
        r.material = mat;
        r.sortingOrder = -95;
    }

    void Paint()
    {
        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 inner = new Color32(80, 230, 110, 40);
        Color32 border = new Color32(110, 255, 140, 130);
        Color32 front = new Color32(255, 215, 80, 70);

        for (int x = 0; x < N; x++)
        {
            for (int y = 0; y < N; y++)
            {
                Vector2Int c = new Vector2Int(x - Half, y - Half);
                int i = Idx(c);
                Color32 col = clear;

                if (zone[i])
                {
                    bool edge = false;
                    Vector2Int[] ns = { new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y), new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1) };
                    foreach (var n in ns)
                        if (InBounds(n) && !zone[Idx(n)]) { edge = true; break; }
                    col = edge ? border : inner;
                }
                else if (wantFrontier && NearZoneOrLinked(c))
                {
                    col = front;
                }

                px[y * N + x] = col;
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        shownVersion = version;
        shownFrontier = wantFrontier;
    }
}