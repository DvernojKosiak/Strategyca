using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Карта проходимости и поиск пути (A*) по клеткам.
/// Карта преград кэшируется и перестраивается только когда здания поставили/снесли/подвинули.
/// Ворота: в режиме "свои проходят" закрыты только для врагов. Пока думает и ходит враг,
/// EnemyAgent включает HostileQuery - и такие ворота для него становятся стеной.
/// Разрушенные стены и ворота - завал, через который можно пройти.
/// Реки (Rivers): вода - преграда для мирных жителей. Солдаты и враги (WaderQuery) идут вброд медленно,
/// всадники перескакивают узкие места. Целый опущенный мост - обычная земля для всех; разрушенный мост -
/// снова вода; поднятый раздвижной мост - стена для всех. Готовая переправа Военного дела (Crossing) -
/// для солдат и врагов как суша (без замедления), для мирных - всё равно вода.
/// </summary>
public class GridNavigation : MonoBehaviour
{
    public static GridNavigation Instance { get; private set; }

    public int mapSize = 50;

    int N => mapSize;
    int Half => mapSize / 2;

    bool[] blocked;
    bool[] hostileBlocked;   // закрыто только для врагов (ворота)
    bool[] road;
    int[] region;            // номер связной области проходимости (0 - преграда): чтобы выбирать дверь, до которой реально дойти
    bool[] cobble;           // брусчатка (она же дорожка): ускоряет и всадников
    bool[] water;            // река (без целых мостов): мирным - преграда, солдатам - брод
    bool[] crossing;         // готовая переправа Военного дела: солдаты идут по ней как по суше
    bool[] narrow;           // узкое место реки: всадник перескакивает

    // true - сейчас путь ищет враг (ставит EnemyAgent на время своего Update)
    public static bool HostileQuery = false;
    int roadCount;
    int cobbleCount;

    // true - сейчас ходит / ищет путь всадник (ставит CitizenAgent на время своего Update).
    // Для него быстрой считается только брусчатка, обычная дорожка не ускоряет.
    public static bool MountedQuery = false;

    // true - сейчас ходит / ищет путь тот, кто может перейти реку вброд: солдат, враг,
    // житель, убегающий от набега (ставят CitizenAgent / EnemyAgent / MerchantCart на время своего Update)
    public static bool WaderQuery = false;
    bool dirty = true;

    // Во сколько раз быстрее ходят по дорожке (клетка -> соседняя клетка, обе дорожные)
    public const float RoadSpeed = 1.6f;

    // Буферы A* (переиспользуем, чтобы не мусорить памятью)
    float[] g, f;
    int[] parent;
    bool[] closed;

    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
    };

    // ---------- Бюджет поиска путей: не больше N поисков за кадр на всех жителей ----------
    static int budgetFrame = -1;
    static int budgetUsed = 0;

    public static bool TryUsePathBudget(int perFrame = 3)
    {
        if (Time.frameCount != budgetFrame) { budgetFrame = Time.frameCount; budgetUsed = 0; }
        if (budgetUsed >= perFrame) return false;
        budgetUsed++;
        return true;
    }

    public static GridNavigation Create(int mapSize)
    {
        GameObject go = new GameObject("GridNavigation");
        GridNavigation nav = go.AddComponent<GridNavigation>();
        nav.mapSize = mapSize;
        nav.dirty = true;
        return nav;
    }

    // Вызывать, когда здание появилось / исчезло / переехало
    public static void MarkDirty()
    {
        if (Instance != null) Instance.dirty = true;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Rebuild()
    {
        int total = N * N;
        if (blocked == null || blocked.Length != total) blocked = new bool[total];
        else System.Array.Clear(blocked, 0, total);
        if (road == null || road.Length != total) road = new bool[total];
        else System.Array.Clear(road, 0, total);
        if (cobble == null || cobble.Length != total) cobble = new bool[total];
        else System.Array.Clear(cobble, 0, total);
        if (hostileBlocked == null || hostileBlocked.Length != total) hostileBlocked = new bool[total];
        else System.Array.Clear(hostileBlocked, 0, total);
        if (water == null || water.Length != total) { water = new bool[total]; crossing = new bool[total]; narrow = new bool[total]; }
        else { System.Array.Clear(crossing, 0, total); }
        for (int i = 0; i < total; i++)
        {
            Vector2Int rc = FromIdx(i);
            water[i] = Rivers.IsWater(rc);
            narrow[i] = water[i] && Rivers.IsNarrow(rc);
        }
        roadCount = 0;
        cobbleCount = 0;

        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null) continue;
            Vector2Int o = b.OriginCell;
            Vector2Int sz = b.type.size;

            if (b.IsRoad)
            {
                for (int x = 0; x < sz.x; x++)
                    for (int y = 0; y < sz.y; y++)
                    {
                        Vector2Int rc = new Vector2Int(o.x + x, o.y + y);
                        if (InBounds(rc) && !road[Idx(rc)]) { road[Idx(rc)] = true; roadCount++; }
                        if (b.IsCobble && InBounds(rc) && !cobble[Idx(rc)]) { cobble[Idx(rc)] = true; cobbleCount++; }
                    }
                continue;
            }

            // Мост: целый и опущенный - суша для всех; поднятый - стена для всех; разрушенный - снова река
            if (b.IsBridge)
            {
                BridgeController bc = b.GetComponent<BridgeController>();
                bool raised = bc != null && bc.BlocksPassage;
                for (int x = 0; x < sz.x; x++)
                    for (int y = 0; y < sz.y; y++)
                    {
                        Vector2Int bc2 = new Vector2Int(o.x + x, o.y + y);
                        if (!InBounds(bc2)) continue;
                        int bi = Idx(bc2);
                        if (raised) blocked[bi] = true;
                        else if (!b.IsRuined) water[bi] = false;
                    }
                continue;
            }

            if (b.IsWalkable) continue; // фермы проходимы
            if (b.IsBarrier && b.IsRuined) continue; // от стены / ворот остался завал - проходимо

            // Ворота: заперты - стена для всех; "свои проходят" - стена только для врагов; открыты - проход
            bool forAll = true, forEnemies = false;
            if (b.IsGate)
            {
                GateController gate = b.GetComponent<GateController>();
                forAll = gate != null && gate.BlocksEveryone;
                forEnemies = gate == null || gate.BlocksEnemies;
            }

            for (int x = 0; x < sz.x; x++)
                for (int y = 0; y < sz.y; y++)
                {
                    Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                    if (!InBounds(c)) continue;
                    if (forAll) blocked[Idx(c)] = true;
                    else if (forEnemies) hostileBlocked[Idx(c)] = true;
                }
        }
        // Полевые укрепления (заслоны, пункты, полевые башни) - сплошная стена для всех: и для своих, и для врагов.
        // Заслон на колёсах при каждом переезде в новые клетки обновляет Cells и зовёт MarkDirty
        foreach (var fp in FieldPost.All)
        {
            if (fp == null || !fp.Alive || fp.Raid || fp.Cells == null) continue;
            foreach (var fc in fp.Cells)
                if (InBounds(fc)) blocked[Idx(fc)] = true;
        }

        // Переправы Военного дела: готовые - для солдат и врагов как суша
        foreach (var cr in Crossing.All)
        {
            if (cr == null || !cr.Open) continue;
            foreach (var cc in cr.Cells)
                if (InBounds(cc)) crossing[Idx(cc)] = true;
        }

        // Связные области (по 4 соседям: диагональ у нас всё равно требует свободных боков).
        // Считаются для мирных жителей: вода - преграда.
        if (region == null || region.Length != total) region = new int[total];
        else System.Array.Clear(region, 0, total);
        int label = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < total; i++)
        {
            if (blocked[i] || water[i] || region[i] != 0) continue;
            label++;
            region[i] = label;
            stack.Push(i);
            while (stack.Count > 0)
            {
                Vector2Int c = FromIdx(stack.Pop());
                for (int d = 0; d < 4; d++)
                {
                    Vector2Int n = new Vector2Int(c.x + Dirs[d].x, c.y + Dirs[d].y);
                    if (!InBounds(n)) continue;
                    int ni = Idx(n);
                    if (blocked[ni] || water[ni] || region[ni] != 0) continue;
                    region[ni] = label;
                    stack.Push(ni);
                }
            }
        }

        dirty = false;
    }

    public bool InBounds(Vector2Int c)
    {
        return c.x >= -Half && c.x < Half && c.y >= -Half && c.y < Half;
    }

    public bool IsBlocked(Vector2Int c)
    {
        if (!InBounds(c)) return true;
        if (dirty || blocked == null) Rebuild();
        int i = Idx(c);
        return blocked[i] || (HostileQuery && hostileBlocked[i]) || (water[i] && !WaderQuery);
    }

    /// <summary>В клетке вода, по которой идут вброд (не мост и не готовая переправа).</summary>
    public bool IsWadeCell(Vector2Int c)
    {
        if (!InBounds(c)) return false;
        if (dirty || blocked == null) Rebuild();
        int i = Idx(c);
        return water[i] && !crossing[i];
    }

    /// <summary>Стоит в воде вброд: в такой момент не дерутся (и солдаты, и враги).</summary>
    public bool IsWading(Vector2 p) { return IsWadeCell(WorldToCell(p)); }

    /// <summary>В клетке река (без целого моста), переправа тоже считается водой - для мирных.</summary>
    public bool IsWaterCell(Vector2Int c)
    {
        if (!InBounds(c)) return false;
        if (dirty || blocked == null) Rebuild();
        return water[Idx(c)];
    }

    // Номер связной области клетки (0 - клетка заблокирована / вне карты)
    public int RegionAt(Vector2Int c)
    {
        if (!InBounds(c)) return 0;
        if (dirty || blocked == null) Rebuild();
        int i = Idx(c);
        return blocked[i] || water[i] ? 0 : region[i];
    }

    // Дорожка ли в этой клетке
    public bool IsRoad(Vector2Int c)
    {
        if (!InBounds(c)) return false;
        if (dirty || blocked == null) Rebuild();
        return road[Idx(c)];
    }

    // Брусчатка ли в этой клетке
    public bool IsCobble(Vector2Int c)
    {
        if (!InBounds(c)) return false;
        if (dirty || blocked == null) Rebuild();
        return cobble[Idx(c)];
    }

    // Множитель скорости на шаге из текущей позиции в клетку toCell: по дорожке (обе клетки дорожные) быстрее
    public float SpeedFactor(Vector2 from, Vector2Int toCell)
    {
        Vector2Int fromCell = WorldToCell(from);
        // Вброд - медленно; всадник узкое место перескакивает
        if (InBounds(toCell))
        {
            if (dirty || blocked == null) Rebuild();
            int ti = Idx(toCell);
            if (water[ti] && !crossing[ti]) return MountedQuery && narrow[ti] ? Rivers.JumpSpeed : Rivers.WadeSpeed;
        }
        // Всадника ускоряет только брусчатка (обе клетки), пешего - любая дорожка, брусчатка тоже
        float f;
        if (MountedQuery) f = IsCobble(toCell) && IsCobble(fromCell) ? RoadSpeed : 1f;
        else f = IsRoad(toCell) && IsRoad(fromCell) ? RoadSpeed : 1f;
        // Лес замедляет всех (дорожка в лесу - нет)
        if (f <= 1f && !IsRoad(toCell) && ResourceZones.IsForest(toCell)) f *= ResourceZones.ForestSpeed;
        return f;
    }

    // Можно ли пройти по прямой из a в b, не задевая препятствий (проверка с шагом 0.25 клетки).
    // Нужно для боя: если путь прямой - идём напрямую, без A*.
    public bool LineWalkable(Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
        for (int i = 1; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
            if (IsBlocked(WorldToCell(p))) return false;
        }
        return true;
    }

    public Vector2 CellCenter(Vector2Int c)
    {
        return new Vector2(c.x + 0.5f, c.y + 0.5f);
    }

    public Vector2Int WorldToCell(Vector2 p)
    {
        return new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
    }

    int Idx(Vector2Int c) { return (c.x + Half) * N + (c.y + Half); }
    Vector2Int FromIdx(int i) { return new Vector2Int(i / N - Half, i % N - Half); }

    static float Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dy = Mathf.Abs(a.y - b.y);
        return (dx + dy) + (1.414f - 2f) * Mathf.Min(dx, dy);
    }

    public List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal)
    {
        if (!InBounds(start) || !InBounds(goal)) return null;
        if (IsBlocked(goal)) return null;
        if (start == goal) return new List<Vector2Int>();

        int total = N * N;
        if (g == null || g.Length != total)
        {
            g = new float[total];
            f = new float[total];
            parent = new int[total];
            closed = new bool[total];
        }
        for (int i = 0; i < total; i++) { g[i] = float.MaxValue; parent[i] = -1; closed[i] = false; }

        int s = Idx(start);
        int t = Idx(goal);
        g[s] = 0f;
        // Если на карте есть дорожки, цена шага по ним меньше - эвристику уменьшаем, чтобы A* не ошибался
        if (dirty || blocked == null) Rebuild();
        // Для всадника "быстрые" клетки - только брусчатка
        bool[] fastTile = MountedQuery ? cobble : road;
        int fastCount = MountedQuery ? cobbleCount : roadCount;
        float hScale = fastCount > 0 ? 1f / RoadSpeed : 1f;
        f[s] = Heuristic(start, goal) * hScale;

        List<int> open = new List<int> { s };
        int iterations = 0;

        while (open.Count > 0 && iterations++ < 8000)
        {
            int bestPos = 0;
            float bestF = float.MaxValue;
            for (int i = 0; i < open.Count; i++)
            {
                float fi = f[open[i]];
                if (fi < bestF) { bestF = fi; bestPos = i; }
            }

            int cur = open[bestPos];
            open[bestPos] = open[open.Count - 1];
            open.RemoveAt(open.Count - 1);

            if (closed[cur]) continue;
            closed[cur] = true;

            if (cur == t)
            {
                List<Vector2Int> result = new List<Vector2Int>();
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

                bool diagonal = dir.x != 0 && dir.y != 0;
                if (diagonal)
                {
                    if (IsBlocked(new Vector2Int(cc.x + dir.x, cc.y))) continue;
                    if (IsBlocked(new Vector2Int(cc.x, cc.y + dir.y))) continue;
                }

                float step = diagonal ? 1.414f : 1f;
                if (water[ni] && !crossing[ni]) step /= MountedQuery && narrow[ni] ? Rivers.JumpSpeed : Rivers.WadeSpeed; // вброд - "дороже"
                else if (fastCount > 0 && fastTile[cur] && fastTile[ni]) step /= RoadSpeed; // по дорожке путь "дешевле"
                else if (!road[ni] && ResourceZones.IsForest(nc)) step /= ResourceZones.ForestSpeed; // а по лесу - дороже
                float ng = g[cur] + step;
                if (ng < g[ni])
                {
                    g[ni] = ng;
                    f[ni] = ng + Heuristic(nc, goal) * hScale;
                    parent[ni] = cur;
                    open.Add(ni);
                }
            }
        }
        return null;
    }

    /// <summary>Ближайшая к "from" клетка у двери здания (по периметру) или внутри, если здание проходимое.</summary>
    public bool TryGetDoorCell(BuildingInstance b, Vector2Int from, out Vector2Int door)
    {
        door = from;
        if (b == null || b.type == null) return false;

        Vector2Int o = b.OriginCell;
        Vector2Int sz = b.type.size;
        int myRegion = RegionAt(from);

        // Здания можно ставить вплотную, между ними остаются узкие проходы и тупики.
        // Первый проход: только двери из той же связной области, что и житель (туда можно дойти).
        // Второй: любые (если жителя выкинуло в другую область или зданию больше не подойти).
        for (int pass = 0; pass < 2; pass++)
        {
            bool found = false;
            int best = int.MaxValue;

            if (b.IsWalkable)
            {
                for (int x = 0; x < sz.x; x++)
                {
                    for (int y = 0; y < sz.y; y++)
                    {
                        Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                        if (IsBlocked(c)) continue;
                        if (pass == 0 && myRegion != 0 && RegionAt(c) != myRegion) continue;
                        int d = Mathf.Abs(c.x - from.x) + Mathf.Abs(c.y - from.y);
                        if (d < best) { best = d; door = c; found = true; }
                    }
                }
                if (found) return true;
            }

            for (int x = -1; x <= sz.x; x++)
            {
                for (int y = -1; y <= sz.y; y++)
                {
                    bool edgeX = (x == -1 || x == sz.x);
                    bool edgeY = (y == -1 || y == sz.y);
                    if (edgeX == edgeY) continue; // только боковые клетки периметра, без углов и без внутренних

                    Vector2Int c = new Vector2Int(o.x + x, o.y + y);
                    if (IsBlocked(c)) continue;
                    if (pass == 0 && myRegion != 0 && RegionAt(c) != myRegion) continue;
                    int d = Mathf.Abs(c.x - from.x) + Mathf.Abs(c.y - from.y);
                    if (d < best) { best = d; door = c; found = true; }
                }
            }
            if (found) return true;
        }
        return false;
    }

    public Vector2Int NearestFree(Vector2Int cell)
    {
        for (int r = 1; r <= 15; r++)
        {
            for (int x = -r; x <= r; x++)
            {
                for (int y = -r; y <= r; y++)
                {
                    if (Mathf.Abs(x) != r && Mathf.Abs(y) != r) continue;
                    Vector2Int c = new Vector2Int(cell.x + x, cell.y + y);
                    if (!IsBlocked(c)) return c;
                }
            }
        }
        return cell;
    }

    public bool TryRandomWalkable(Vector2 center, float radius, out Vector2Int cell)
    {
        for (int i = 0; i < 25; i++)
        {
            Vector2 p = center + Random.insideUnitCircle * radius;
            Vector2Int c = WorldToCell(p);
            if (!IsBlocked(c) && !IsWadeCell(c))   // гулять по реке никто не ходит
            {
                cell = c;
                return true;
            }
        }
        cell = Vector2Int.zero;
        return false;
    }
}