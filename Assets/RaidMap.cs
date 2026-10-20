using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Карта набега: отдельное поле боя далеко в стороне от нашей деревни (центр - Origin).
/// Своя сетка проходимости и поиск пути (A*), чтобы не мешать навигации города (GridNavigation).
/// Клетки - локальные: от -Size/2 до Size/2-1, мировая точка = Origin + (x + 0.5, y + 0.5).
/// Размер - как у нашей карты (BuildingPlacer.mapSize), но не меньше MinSize и не больше MaxSize.
/// На карте растут леса (непроходимые, план - в BotVillage.Plan.Trees).
/// Создаётся RaidManager при прибытии отряда и удаляется после похода.
/// </summary>
public class RaidMap : MonoBehaviour
{
    public static RaidMap Instance { get; private set; }

    public const int MinSize = 44, MaxSize = 160;

    /// <summary>Размер карты набега: как у нашей карты (чётный, в пределах MinSize..MaxSize).</summary>
    public static int Size
    {
        get
        {
            int home = GridNavigation.Instance != null ? GridNavigation.Instance.mapSize : 50;
            int s = Mathf.Clamp(home, MinSize, MaxSize);
            return s - (s % 2);
        }
    }

    // Центр поля боя в мире: далеко от нашей карты и от ночного затемнения (оно до 200 клеток от центра)
    public static Vector2 Origin => new Vector2(300f + MaxSize / 2f, 0f);

    readonly HashSet<Vector2Int> trees = new HashSet<Vector2Int>();
    public bool IsTree(Vector2Int c) { return trees.Contains(c); }

    int Half => size / 2;

    bool[] blocked;
    bool dirty = true;
    GameObject background;

    // Буферы A*
    float[] g, f;
    int[] parent;
    bool[] closed;

    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
    };

    // Бюджет поисков пути на кадр (на всех бойцов поля боя)
    static int budgetFrame = -1, budgetUsed;
    public static bool TryUsePathBudget(int perFrame = 6)
    {
        if (Time.frameCount != budgetFrame) { budgetFrame = Time.frameCount; budgetUsed = 0; }
        if (budgetUsed >= perFrame) return false;
        budgetUsed++;
        return true;
    }

    int size;   // размер этой карты (запоминаем при создании)

    public static RaidMap Create(List<Vector2Int> forest)
    {
        if (Instance != null) Destroy(Instance.gameObject);
        GameObject go = new GameObject("RaidMap");
        go.transform.position = new Vector3(Origin.x, Origin.y, 0f);
        RaidMap m = go.AddComponent<RaidMap>();
        Instance = m;
        m.size = Size;
        if (forest != null) foreach (var c in forest) m.trees.Add(c);
        m.CreateBackground();
        return m;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (background != null) Destroy(background);
    }

    void CreateBackground()
    {
        int Size = size;
        int Half = Size / 2;
        int cellPx = Size > 100 ? 6 : 8;
        int res = Size * cellPx;
        Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color back = new Color(0.27f, 0.36f, 0.22f);
        Color line = new Color(0.31f, 0.41f, 0.26f);
        Color[] px = new Color[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                px[y * res + x] = (x % cellPx == 0 || y % cellPx == 0) ? line : back;

        // Полоса высадки у западного края - чуть светлее
        for (int y = 0; y < res; y++)
            for (int x = 0; x < 4 * cellPx; x++)
                px[y * res + x] = Color.Lerp(px[y * res + x], new Color(0.55f, 0.5f, 0.35f), 0.35f);

        // Леса: тёмные кроны (по круглому пятну на клетку, оттенок чуть гуляет)
        var rng = new System.Random(trees.Count * 7919 + Size);
        foreach (var t in trees)
        {
            int bx = (t.x + Half) * cellPx, by = (t.y + Half) * cellPx;
            float shade = 0.8f + (float)rng.NextDouble() * 0.3f;
            Color crown = new Color(0.12f * shade, 0.32f * shade, 0.13f * shade);
            Color edge = new Color(0.08f, 0.2f, 0.09f);
            float rad = cellPx * (0.55f + (float)rng.NextDouble() * 0.15f);
            float cx = bx + cellPx / 2f + (float)(rng.NextDouble() - 0.5) * 2f;
            float cy = by + cellPx / 2f + (float)(rng.NextDouble() - 0.5) * 2f;
            for (int x = bx - 2; x < bx + cellPx + 2; x++)
                for (int y = by - 2; y < by + cellPx + 2; y++)
                {
                    if (x < 0 || y < 0 || x >= res || y >= res) continue;
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    if (d > rad) continue;
                    px[y * res + x] = d > rad - 1.2f ? edge : crown;
                }
        }

        tex.SetPixels(px);
        tex.Apply();

        background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "RaidMap_Background";
        background.transform.position = new Vector3(Origin.x, Origin.y, 1f);
        background.transform.localScale = new Vector3(Size, Size, 1f);
        Destroy(background.GetComponent<Collider>());
        Material mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        Renderer r = background.GetComponent<Renderer>();
        r.material = mat;
        r.sortingOrder = -100;
    }

    // ---------- Клетки ----------

    public void MarkDirty() { dirty = true; }

    public bool InBounds(Vector2Int c) { return c.x >= -Half && c.x < Half && c.y >= -Half && c.y < Half; }
    int Idx(Vector2Int c) { return (c.x + Half) * size + (c.y + Half); }
    Vector2Int FromIdx(int i) { return new Vector2Int(i / size - Half, i % size - Half); }

    public Vector2Int WorldToCell(Vector2 w)
    {
        Vector2 l = w - Origin;
        return new Vector2Int(Mathf.FloorToInt(l.x), Mathf.FloorToInt(l.y));
    }

    public Vector2 CellCenter(Vector2Int c) { return Origin + new Vector2(c.x + 0.5f, c.y + 0.5f); }

    /// <summary>Точка мира по локальной координате карты (не клетке).</summary>
    public static Vector2 Local(float x, float y) { return Origin + new Vector2(x, y); }

    void Rebuild()
    {
        int total = size * size;
        if (blocked == null || blocked.Length != total) blocked = new bool[total];
        else System.Array.Clear(blocked, 0, total);

        foreach (var b in RaidBuilding.All)
        {
            if (b == null || !b.Blocks) continue;
            for (int x = 0; x < b.SizeCells.x; x++)
                for (int y = 0; y < b.SizeCells.y; y++)
                {
                    Vector2Int c = new Vector2Int(b.OriginCell.x + x, b.OriginCell.y + y);
                    if (InBounds(c)) blocked[Idx(c)] = true;
                }
        }
        foreach (var t in trees) if (InBounds(t)) blocked[Idx(t)] = true;

        // Полевые укрепления на поле боя - сплошная стена для наших и для защитников
        foreach (var fp in FieldPost.All)
        {
            if (fp == null || !fp.Alive || !fp.Raid || fp.Cells == null) continue;
            foreach (var fc in fp.Cells)
                if (InBounds(fc)) blocked[Idx(fc)] = true;
        }
        dirty = false;
    }

    public bool IsBlocked(Vector2Int c)
    {
        if (!InBounds(c)) return true;
        if (dirty || blocked == null) Rebuild();
        return blocked[Idx(c)];
    }

    public bool LineWalkable(Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
        for (int i = 1; i <= steps; i++)
        {
            if (IsBlocked(WorldToCell(Vector2.Lerp(a, b, i / (float)steps)))) return false;
        }
        return true;
    }

    public Vector2Int NearestFree(Vector2Int cell)
    {
        if (!IsBlocked(cell)) return cell;
        for (int r = 1; r <= 12; r++)
            for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                {
                    if (Mathf.Abs(x) != r && Mathf.Abs(y) != r) continue;
                    Vector2Int c = new Vector2Int(cell.x + x, cell.y + y);
                    if (!IsBlocked(c)) return c;
                }
        return cell;
    }

    public bool TryRandomWalkable(Vector2 center, float radius, out Vector2 point)
    {
        for (int i = 0; i < 20; i++)
        {
            Vector2 p = center + Random.insideUnitCircle * radius;
            Vector2Int c = WorldToCell(p);
            if (!IsBlocked(c)) { point = CellCenter(c) + Random.insideUnitCircle * 0.3f; return true; }
        }
        point = center;
        return false;
    }

    /// <summary>Свободная клетка у стены здания, ближайшая к from (куда подходить, чтобы его бить).</summary>
    public Vector2 ApproachPoint(RaidBuilding b, Vector2 from)
    {
        Vector2Int f0 = WorldToCell(from);
        Vector2Int o = b.OriginCell, sz = b.SizeCells;
        int best = int.MaxValue;
        Vector2Int pick = o;
        bool found = false;

        if (!b.Blocks)
        {
            // Проходимое (поле, руины стены) - можно встать прямо на него
            for (int x = 0; x < sz.x; x++)
                for (int y = 0; y < sz.y; y++)
                {
                    Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                    int d = Mathf.Abs(c.x - f0.x) + Mathf.Abs(c.y - f0.y);
                    if (d < best) { best = d; pick = c; found = true; }
                }
            return found ? CellCenter(pick) : b.Center;
        }

        for (int x = -1; x <= sz.x; x++)
            for (int y = -1; y <= sz.y; y++)
            {
                bool ex = x == -1 || x == sz.x, ey = y == -1 || y == sz.y;
                if (ex == ey) continue;   // только боковые клетки периметра
                Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                if (IsBlocked(c)) continue;
                int d = Mathf.Abs(c.x - f0.x) + Mathf.Abs(c.y - f0.y);
                if (d < best) { best = d; pick = c; found = true; }
            }
        return found ? CellCenter(pick) : b.Center;
    }

    static float Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x), dy = Mathf.Abs(a.y - b.y);
        return (dx + dy) + (1.414f - 2f) * Mathf.Min(dx, dy);
    }

    public List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal)
    {
        if (!InBounds(start) || !InBounds(goal) || IsBlocked(goal)) return null;
        if (start == goal) return new List<Vector2Int>();

        int total = size * size;
        if (g == null || g.Length != total)
        {
            g = new float[total]; f = new float[total]; parent = new int[total]; closed = new bool[total];
            heapIdx = new int[total * 4]; heapF = new float[total * 4];   // с запасом: узлы могут попадать в кучу повторно
        }
        for (int i = 0; i < total; i++) { g[i] = float.MaxValue; parent[i] = -1; closed[i] = false; }

        int s = Idx(start), t = Idx(goal);
        g[s] = 0f;
        f[s] = Heuristic(start, goal);
        heapCount = 0;
        HeapPush(s, f[s]);
        int iterations = 0;

        while (heapCount > 0 && iterations++ < total * 2)
        {
            int cur = HeapPop();
            if (closed[cur]) continue;
            closed[cur] = true;

            if (cur == t)
            {
                var result = new List<Vector2Int>();
                int c = t;
                while (c != s)
                {
                    result.Add(FromIdx(c));
                    c = parent[c];
                    if (c < 0) return null;
                }
                result.Reverse();
                return result;
            }

            Vector2Int cc = FromIdx(cur);
            for (int d = 0; d < Dirs.Length; d++)
            {
                Vector2Int dir = Dirs[d];
                Vector2Int nc = new Vector2Int(cc.x + dir.x, cc.y + dir.y);
                if (IsBlocked(nc)) continue;
                int ni = Idx(nc);
                if (closed[ni]) continue;
                bool diag = dir.x != 0 && dir.y != 0;
                if (diag && (IsBlocked(new Vector2Int(cc.x + dir.x, cc.y)) || IsBlocked(new Vector2Int(cc.x, cc.y + dir.y)))) continue;
                float ng = g[cur] + (diag ? 1.414f : 1f);
                if (ng < g[ni])
                {
                    g[ni] = ng;
                    f[ni] = ng + Heuristic(nc, goal);
                    parent[ni] = cur;
                    if (heapCount < heapIdx.Length) HeapPush(ni, f[ni]);
                }
            }
        }
        return null;
    }

    // ---------- Двоичная куча для A* (карта большая - перебирать список целиком медленно) ----------

    int[] heapIdx;
    float[] heapF;
    int heapCount;

    void HeapPush(int idx, float pri)
    {
        int i = heapCount++;
        heapIdx[i] = idx;
        heapF[i] = pri;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (heapF[p] <= heapF[i]) break;
            Swap(i, p);
            i = p;
        }
    }

    int HeapPop()
    {
        int top = heapIdx[0];
        heapCount--;
        if (heapCount > 0)
        {
            heapIdx[0] = heapIdx[heapCount];
            heapF[0] = heapF[heapCount];
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < heapCount && heapF[l] < heapF[m]) m = l;
                if (r < heapCount && heapF[r] < heapF[m]) m = r;
                if (m == i) break;
                Swap(i, m);
                i = m;
            }
        }
        return top;
    }

    void Swap(int a, int b)
    {
        int ti = heapIdx[a]; heapIdx[a] = heapIdx[b]; heapIdx[b] = ti;
        float tf = heapF[a]; heapF[a] = heapF[b]; heapF[b] = tf;
    }
}

/// <summary>
/// Движение по карте набега (как GridMover для города): по прямой, если свободно, иначе по A*.
/// Fails - сколько раз подряд путь не нашёлся.
/// </summary>
public class RaidMover
{
    List<Vector2Int> path;
    int index;
    float repathTimer;
    Vector2Int pathGoal;

    public int Fails { get; private set; }

    public void Clear() { path = null; repathTimer = 0f; }
    public void ResetFails() { Fails = 0; }

    public bool Step(Transform tr, RaidMap map, Vector2 target, float speed, float stopDist = 0.05f)
    {
        Vector2 pos = tr.position;
        if (Vector2.Distance(pos, target) <= stopDist) { path = null; return true; }

        Vector2Int cur = map.WorldToCell(pos);
        if (map.IsBlocked(cur))
        {
            cur = map.NearestFree(cur);
            tr.position = map.CellCenter(cur);
            pos = tr.position;
            path = null;
        }

        if (map.LineWalkable(pos, target))
        {
            path = null;
            Fails = 0;
            tr.position = Vector2.MoveTowards(pos, target, speed * Time.deltaTime);
            return Vector2.Distance(tr.position, target) <= stopDist;
        }

        Vector2Int goal = map.WorldToCell(target);
        if (map.IsBlocked(goal)) goal = map.NearestFree(goal);

        repathTimer -= Time.deltaTime;
        bool valid = path != null && index < path.Count;
        bool wantNew = !valid || goal != pathGoal;

        if (wantNew && repathTimer <= 0f && goal != cur && RaidMap.TryUsePathBudget())
        {
            repathTimer = 0.6f;
            List<Vector2Int> p = map.FindPath(cur, goal);
            if (p != null && p.Count > 0)
            {
                path = p;
                index = 0;
                pathGoal = goal;
                Fails = 0;
                valid = true;
            }
            else
            {
                path = null;
                valid = false;
                Fails++;
            }
        }

        if (!valid) return false;

        Vector2Int next = path[index];
        if (map.IsBlocked(next)) { path = null; repathTimer = 0f; return false; }
        Vector2 t = map.CellCenter(next);
        tr.position = Vector2.MoveTowards(pos, t, speed * Time.deltaTime);
        if (Vector2.Distance(tr.position, t) < 0.05f) index++;
        return false;
    }
}