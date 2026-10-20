using UnityEngine;
using System.Collections.Generic;

/// <summary>Нрав соседа-соперника: от него зависит, как он воюет, копит войско и строит оборону.</summary>
public enum RivalTrait
{
    Warlike,    // Воинственный: нападает часто и упорно, отступает поздно, бьёт по армии и Ратуше
    Cautious,   // Осторожный: нападает редко и только с перевесом, держит дома большой гарнизон, строит заслоны
    Cunning,    // Хитрый: бьёт, когда ваша армия в походе, грабит окраины и уходит, разведывает всегда
    Calculating // Расчётливый: всего понемногу, нападает, когда сила на его стороне
}

/// <summary>
/// Деревня-соперник (пока - бот, который играет "как человек"). Хранит своё войско, богатство,
/// злость на игрока и что он знает о нас. Стратегию ведёт RivalManager, оборону на карте набега - RaidDefense,
/// нападение на наш город - RivalAssault.
/// Расстановка деревни (здания, стены, лес) получается из Seed - поэтому разведданные до похода
/// совпадают с тем, что игрок увидит на месте. Защитники - это настоящее войско деревни, которое сейчас дома.
/// После набега деревня какое-то время восстанавливается, потом на её месте появляется новая расстановка.
/// </summary>
public class RaidTarget
{
    public string Name;
    public int Level;             // 1 - хутор, 2 - деревня, 3 - крепость
    public int Seed;
    public float RecoverAt;       // до этого времени деревня восстанавливается после набега
    public int TimesRaided;

    // ---------- Соперник ----------
    public RivalTrait Trait;
    public readonly Dictionary<string, int> Roster = new Dictionary<string, int>();   // всё войско (дома + в походе)
    public readonly Dictionary<string, int> Away = new Dictionary<string, int>();     // сейчас в походе
    public int Fortify;           // сколько заслонов стоит у деревни заранее
    public float Prosperity;      // растёт со временем и с добычей; хватило - деревня растёт в уровне
    public float Grudge;          // злость на игрока, 0..100
    public float NextMoveAt;      // раньше этого времени против игрока ничего не затевает
    public float IntelPower = -1f;   // какой силы, по их сведениям, наш город (-1 - не знают)
    public float IntelAt = -9999f;   // когда узнали
    public float TrainProgress;
    public string Activity = "живёт своей жизнью";
    public readonly Loot Stash = new Loot();   // награбленное: лежит в их Ратуше, можно отбить
    public int Stamp;             // меняется вместе с войском / заслонами / складом - план деревни пересобирается
    public int AttacksOnPlayer, WinsOnPlayer;

    public BotVillage.Plan PlanCache;

    public bool Ready => Time.time >= RecoverAt;
    public float TravelSeconds => 15f + 10f * Level;

    public BotVillage.Plan GetPlan()
    {
        if (PlanCache == null || PlanCache.Seed != Seed || PlanCache.Stamp != Stamp || PlanCache.Level != Mathf.Clamp(Level, 1, 3))
            PlanCache = BotVillage.MakePlan(this);
        return PlanCache;
    }

    public string LevelName => Level <= 1 ? "хутор" : (Level == 2 ? "деревня" : "крепость");

    // ---------- Войско ----------

    public int Count(Dictionary<string, int> d) { int n = 0; foreach (var kv in d) n += Mathf.Max(0, kv.Value); return n; }
    public int ArmySize => Count(Roster);
    public int AwayCount => Count(Away);
    public int HomeCount => Mathf.Max(0, ArmySize - AwayCount);

    /// <summary>Кто сейчас дома (всё войско минус ушедшие в поход).</summary>
    public Dictionary<string, int> HomeRoster()
    {
        var d = new Dictionary<string, int>();
        foreach (var kv in Roster)
        {
            Away.TryGetValue(kv.Key, out int a);
            int n = kv.Value - a;
            if (n > 0) d[kv.Key] = n;
        }
        return d;
    }

    public void AddUnit(string name, int n = 1)
    {
        Roster.TryGetValue(name, out int c);
        Roster[name] = Mathf.Max(0, c + n);
        if (Roster[name] == 0) Roster.Remove(name);
        Stamp++;
    }

    /// <summary>Боец погиб: away - он был в походе.</summary>
    public void LoseUnit(string name, bool away)
    {
        AddUnit(name, -1);
        if (away) SetAway(name, -1);
    }

    public void SetAway(string name, int delta)
    {
        Away.TryGetValue(name, out int c);
        int v = Mathf.Max(0, c + delta);
        if (v == 0) Away.Remove(name); else Away[name] = v;
        Stamp++;
    }

    public float HomePower => BotVillage.RosterPower(HomeRoster());
}

public static class BotVillage
{
    static readonly string[] Names =
    {
        "Ashford", "Millbrook", "Stonebridge", "Oakvale", "Redwater", "Greywick", "Elmstead", "Thornbury",
        "Highmoor", "Fenwick", "Coldbrook", "Westmere", "Larkhill", "Ravensby", "Briarton", "Dunmore"
    };

    public static string RandomName(System.Random rng) { return Names[rng.Next(Names.Length)]; }

    // ---------- План деревни ----------

    public enum Layout { Ring, Long, Keep, Sprawl }

    public class BuildingSpec
    {
        public RaidBuildingKind kind;
        public string title;
        public Vector2Int origin;     // локальная клетка карты набега
        public Vector2Int size;
        public float hp;
        public Color color;
        public Loot loot = new Loot();
    }

    public class DefenderSpec
    {
        public string unitName;
        public Vector2 post;          // локальная точка карты набега
        public DefRole role;
    }

    /// <summary>Место под заслон (3 клетки поперёк подхода). Строят заранее или по тревоге.</summary>
    public class BarricadeSpot
    {
        public Vector2Int origin, size;
        public Vector2 facing;        // откуда ждут врага (наружу)
        public Vector2 Center => new Vector2(origin.x + size.x / 2f, origin.y + size.y / 2f);
    }

    public class Plan
    {
        public int Seed, Stamp;
        public int Level;
        public Layout Style;
        public Vector2Int Center;
        public int RingX, RingY;      // полуразмеры кольца стен (или границы застройки, если стен нет)
        public readonly List<BuildingSpec> Buildings = new List<BuildingSpec>();
        public readonly List<DefenderSpec> Defenders = new List<DefenderSpec>();
        public readonly List<string> Reserve = new List<string>();          // не вышли сразу - подкрепление из казарм
        public readonly List<Vector2Int> Trees = new List<Vector2Int>();   // лес (непроходимые клетки)
        public readonly List<Vector2> Gaps = new List<Vector2>();          // проходы в стене (середина прохода)
        public readonly List<BarricadeSpot> Spots = new List<BarricadeSpot>();   // свободные места под заслоны (тревога)
        public int Towers, Cannons, Guns, Barricades;
        public bool Walls, Gates;
        public int Wealth;            // сколько всего лежит на складах
        public Loot Total = new Loot();

        public string StyleName
        {
            get
            {
                switch (Style)
                {
                    case Layout.Long: return "вытянутая, за стенами";
                    case Layout.Keep: return "крепость-ядро, дома снаружи";
                    case Layout.Sprawl: return "раскидана по полю";
                    default: return Walls ? "за кольцом стен" : "кучно";
                }
            }
        }
    }

    // Расстановка, которую сейчас видно на карте набега (её центр и кольцо стен - для ловушек и т.п.)
    static Plan active;
    public static Vector2Int Center => active != null ? active.Center : new Vector2Int(Mathf.RoundToInt(RaidMap.Size * 0.12f), 0);
    public static int WallRing => active != null ? Mathf.Max(active.RingX, active.RingY) : 12;

    public const int MaxDefendersOnField = 22;   // больше - выходят подкреплением из казарм по ходу боя

    public static Plan MakePlan(RaidTarget t)
    {
        var rng = new System.Random(t.Seed);
        int L = Mathf.Clamp(t.Level, 1, 3);
        Plan p = new Plan { Seed = t.Seed, Stamp = t.Stamp, Level = L };
        int size = RaidMap.Size, h = size / 2;
        bool[,] used = new bool[size, size];
        float hpMul = 1f + 0.25f * (L - 1);

        // ---------- Расстановка: вид, размер, центр ----------
        p.Style = PickLayout(rng, L, t.Trait);
        int maxR = Mathf.Max(8, (size - 18) / 2);
        int rx, ry;
        switch (p.Style)
        {
            case Layout.Long:
                if (rng.NextDouble() < 0.5) { rx = R(rng, 8, 10); ry = R(rng, 13, 15); }
                else { rx = R(rng, 13, 15); ry = R(rng, 8, 10); }
                break;
            case Layout.Keep: rx = ry = R(rng, 6, 7); break;
            case Layout.Sprawl: rx = ry = R(rng, 12, 14); break;
            default: rx = ry = R(rng, 10, 13); break;
        }
        int outer = p.Style == Layout.Keep ? Mathf.Min(13, maxR) : 0;   // у крепости-ядра дома стоят снаружи стен
        rx = Mathf.Min(rx, maxR);
        ry = Mathf.Min(ry, h - 5);
        p.RingX = rx; p.RingY = ry;

        int spanX = Mathf.Max(rx, outer), spanY = Mathf.Max(ry, outer);
        int minX = -h + 15 + spanX, maxX = h - 3 - spanX;
        int cx = minX <= maxX ? R(rng, minX, maxX) : Mathf.RoundToInt(size * 0.12f);
        int cyLim = Mathf.Max(0, Mathf.Min(h - 4 - spanY, 7));
        int cy = R(rng, -cyLim, cyLim);
        p.Center = new Vector2Int(cx, cy);

        // ---------- Здания ----------
        // Ратуша - в центре
        Add(p, used, RaidBuildingKind.Hall, "Ратуша", new Vector2Int(cx - 1, cy - 1), new Vector2Int(3, 3), 1500f * hpMul, Color.white);

        int barracks = L == 1 ? 1 : 2;
        int houses = 3 + 2 * L;
        int farms = 1 + L / 2;
        int mines = L >= 2 ? 2 : 1;

        // Где ставить: внутри стен (с отступом) или, у крепости-ядра, снаружи
        bool walled = L >= 2 && p.Style != Layout.Sprawl;
        int inset = p.Style == Layout.Keep ? 2 : 3;
        int inX = walled ? rx - inset : rx, inY = walled ? ry - inset : ry;
        int coreMin = 3;
        int outMin = p.Style == Layout.Keep ? rx + 2 : coreMin;
        int outX = p.Style == Layout.Keep ? outer : inX, outY = p.Style == Layout.Keep ? outer : inY;
        int gapMargin = p.Style == Layout.Sprawl ? 2 : 1;   // раскиданная деревня - между домами шире

        Place(p, used, rng, RaidBuildingKind.Barracks, "Казарма", 2, 2, barracks, coreMin, Mathf.Min(inX, 7), Mathf.Min(inY, 7), 700f * hpMul, new Color(0.7f, 0.2f, 0.2f), 1);
        if (L >= 2) Place(p, used, rng, RaidBuildingKind.Warehouse, "Хранилище", 3, 2, 1, coreMin, Mathf.Min(inX, 7), Mathf.Min(inY, 7), 450f * hpMul, new Color(0.62f, 0.5f, 0.38f), 1);
        Place(p, used, rng, RaidBuildingKind.Farm, "Ферма", 3, 3, farms, outMin + 1, outX, outY, 300f * hpMul, new Color(0.4f, 0.75f, 0.3f), gapMargin);
        Place(p, used, rng, RaidBuildingKind.Sawmill, "Лесопилка", 2, 2, 1, outMin + 1, outX, outY, 400f * hpMul, new Color(0.85f, 0.5f, 0.2f), gapMargin);
        Place(p, used, rng, RaidBuildingKind.Mine, "Шахта", 2, 2, mines, outMin + 1, outX, outY, 500f * hpMul, new Color(0.5f, 0.5f, 0.55f), gapMargin);
        if (L >= 2) Place(p, used, rng, RaidBuildingKind.Workshop, "Мастерская", 2, 2, 1, outMin, outX, outY, 450f * hpMul, new Color(0.7f, 0.5f, 0.2f), gapMargin);
        if (L >= 3)
        {
            Place(p, used, rng, RaidBuildingKind.Kitchen, "Кухня", 2, 2, 1, outMin, outX, outY, 420f * hpMul, new Color(0.88f, 0.48f, 0.32f), gapMargin);
            Place(p, used, rng, RaidBuildingKind.Stable, "Конюшня", 3, 2, 1, outMin + 1, outX, outY, 400f * hpMul, new Color(0.55f, 0.36f, 0.2f), gapMargin);
        }
        Place(p, used, rng, RaidBuildingKind.House, "Жилой дом", 2, 2, houses, outMin, outX, outY, 400f * hpMul, new Color(0.85f, 0.75f, 0.55f), gapMargin);

        // ---------- Стены: прямоугольник с проходами в случайных местах ----------
        if (walled) BuildWalls(p, used, rng, L, hpMul);

        // ---------- Оборона: башни у проходов и на углах, пушка у Ратуши ----------
        int towers = L + (t.Trait == RivalTrait.Cautious ? 1 : 0);
        p.Towers = PlaceTowers(p, used, rng, towers, hpMul);
        if (L >= 3) p.Cannons = Place(p, used, rng, RaidBuildingKind.Cannon, "Магическая пушка", 2, 2, 1, coreMin, 6, 6, 550f * hpMul, new Color(0.6f, 0.35f, 0.9f), 1);
        if (L >= 3) p.Guns = Place(p, used, rng, RaidBuildingKind.Gun, "Пушка", 2, 2, 1, coreMin, 8, 8, 650f * hpMul, new Color(0.42f, 0.4f, 0.36f), 1);

        // ---------- Места под заслоны (до леса, чтобы лес от числа заслонов не менялся) ----------
        MakeSpots(p, used);

        GrowForest(p, used, rng);

        // Заранее поставленные заслоны (чем осторожнее и злее сосед, тем больше)
        int fort = Mathf.Min(t.Fortify, p.Spots.Count);
        for (int i = 0; i < fort; i++)
        {
            BarricadeSpot s = p.Spots[0];
            p.Spots.RemoveAt(0);
            var bs = new BuildingSpec { kind = RaidBuildingKind.Barricade, title = "Заслон", origin = s.origin, size = s.size, hp = BarricadeHp(L), color = BarricadeColor };
            p.Buildings.Add(bs);
            p.Barricades++;
        }

        FillStorages(p, rng, L);
        // Награбленное у вас и у соседей лежит в их Ратуше
        foreach (var b in p.Buildings) if (b.kind == RaidBuildingKind.Hall) { b.loot.Add(t.Stash); break; }

        // Защитники - войско деревни, которое сейчас дома (своё зерно: расстановка от войска не зависит)
        MakeDefenders(p, t, new System.Random(t.Seed ^ (t.Stamp * 7919 + 17)));

        foreach (var b in p.Buildings) p.Total.Add(b.loot);
        p.Wealth = p.Total.Total;
        return p;
    }

    public static float BarricadeHp(int level) { return 240f * (1f + 0.25f * (Mathf.Clamp(level, 1, 3) - 1)); }
    public static readonly Color BarricadeColor = new Color(0.5f, 0.34f, 0.17f);

    static Layout PickLayout(System.Random rng, int L, RivalTrait trait)
    {
        double r = rng.NextDouble();
        if (L <= 1) return r < 0.5 ? Layout.Sprawl : Layout.Ring;
        if (trait == RivalTrait.Cautious && r < 0.4) return Layout.Keep;
        if (L == 2) return r < 0.4 ? Layout.Ring : (r < 0.7 ? Layout.Long : (r < 0.85 ? Layout.Keep : Layout.Sprawl));
        return r < 0.35 ? Layout.Keep : (r < 0.7 ? Layout.Ring : Layout.Long);
    }

    static bool Free(bool[,] used, Vector2Int o, Vector2Int size, int margin)
    {
        int h = RaidMap.Size / 2;
        for (int x = -margin; x < size.x + margin; x++)
            for (int y = -margin; y < size.y + margin; y++)
            {
                int gx = o.x + x + h, gy = o.y + y + h;
                if (gx < 0 || gy < 0 || gx >= RaidMap.Size || gy >= RaidMap.Size) return false;
                if (used[gx, gy]) return false;
            }
        return true;
    }

    static BuildingSpec Add(Plan p, bool[,] used, RaidBuildingKind kind, string title, Vector2Int o, Vector2Int size, float hp, Color color)
    {
        int h = RaidMap.Size / 2;
        for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++)
                used[o.x + x + h, o.y + y + h] = true;
        var s = new BuildingSpec { kind = kind, title = title, origin = o, size = size, hp = hp, color = color };
        p.Buildings.Add(s);
        return s;
    }

    // Ставит count зданий в прямоугольнике maxX × maxY от центра, не ближе minR (с проходом margin клеток вокруг).
    // Возвращает, сколько встало.
    static int Place(Plan p, bool[,] used, System.Random rng, RaidBuildingKind kind, string title, int w, int hgt, int count,
                     int minR, int maxX, int maxY, float hp, Color color, int margin)
    {
        int placed = 0;
        Vector2Int c0 = p.Center;
        for (int n = 0; n < count; n++)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                int ox = c0.x + rng.Next(-maxX, maxX - w + 2);
                int oy = c0.y + rng.Next(-maxY, maxY - hgt + 2);
                float dx = Mathf.Abs(ox + w / 2f - c0.x - 0.5f), dy = Mathf.Abs(oy + hgt / 2f - c0.y - 0.5f);
                if (dx > maxX || dy > maxY || Mathf.Max(dx, dy) < minR) continue;
                Vector2Int o = new Vector2Int(ox, oy);
                if (!Free(used, o, new Vector2Int(w, hgt), attempt < 120 ? margin : 1)) continue;
                Add(p, used, kind, title, o, new Vector2Int(w, hgt), hp, color);
                placed++;
                break;
            }
        }
        return placed;
    }

    static void BuildWalls(Plan p, bool[,] used, System.Random rng, int L, float hpMul)
    {
        p.Walls = true;
        p.Gates = L >= 3 || (p.Style == Layout.Keep && L >= 2);
        Vector2Int c = p.Center;
        int x0 = c.x - p.RingX, x1 = c.x + p.RingX;
        int y0 = c.y - p.RingY, y1 = c.y + p.RingY;
        Color wallC = new Color(0.6f, 0.6f, 0.65f), gateC = new Color(0.5f, 0.33f, 0.16f);

        // Проходы: 2-4 стороны, на каждой - в случайном месте (не обязательно посередине)
        var gapCells = new HashSet<Vector2Int>();
        int sides = R(rng, 2, 4);
        var order = new List<int> { 0, 1, 2, 3 };
        for (int i = 0; i < order.Count; i++) { int j = rng.Next(i, order.Count); int tmp = order[i]; order[i] = order[j]; order[j] = tmp; }
        for (int k = 0; k < sides; k++)
        {
            int side = order[k];   // 0 - запад, 1 - восток, 2 - юг, 3 - север
            bool vertical = side < 2;
            int half = vertical ? p.RingY : p.RingX;
            int off = R(rng, -(half - 4), half - 5);
            for (int d = 0; d < 2; d++)
            {
                Vector2Int g = vertical ? new Vector2Int(side == 0 ? x0 : x1, c.y + off + d) : new Vector2Int(c.x + off + d, side == 2 ? y0 : y1);
                gapCells.Add(g);
            }
            Vector2 mid = vertical ? new Vector2((side == 0 ? x0 : x1) + 0.5f, c.y + off + 1f) : new Vector2(c.x + off + 1f, (side == 2 ? y0 : y1) + 0.5f);
            p.Gaps.Add(mid);
        }

        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                bool edge = x == x0 || x == x1 || y == y0 || y == y1;
                if (!edge) continue;
                Vector2Int cell = new Vector2Int(x, y);
                if (!Free(used, cell, Vector2Int.one, 0)) continue;
                if (gapCells.Contains(cell))
                {
                    if (p.Gates) Add(p, used, RaidBuildingKind.Gate, "Ворота", cell, Vector2Int.one, 500f * hpMul, gateC);
                    continue;
                }
                Add(p, used, RaidBuildingKind.Wall, "Стена", cell, Vector2Int.one, 400f * hpMul, wallC);
            }
    }

    // Башни - у проходов (изнутри, сбоку) и на углах; без стен - по краю деревни со стороны подходов
    static int PlaceTowers(Plan p, bool[,] used, System.Random rng, int count, float hpMul)
    {
        Color col = new Color(0.3f, 0.55f, 0.9f);
        var wanted = new List<Vector2Int>();
        Vector2Int c = p.Center;
        foreach (var g in p.Gaps)
        {
            Vector2 inward = ((Vector2)c + new Vector2(0.5f, 0.5f) - g).normalized;
            Vector2 side = new Vector2(-inward.y, inward.x);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector2 at = g + inward * 2.5f + side * (2.5f * s);
                wanted.Add(new Vector2Int(Mathf.RoundToInt(at.x - 1f), Mathf.RoundToInt(at.y - 1f)));
            }
        }
        // углы и стороны (запад - откуда обычно приходят - первым)
        int rx = p.RingX - (p.Walls ? 2 : 0), ry = p.RingY - (p.Walls ? 2 : 0);
        wanted.Add(new Vector2Int(c.x - rx, c.y - 1));
        wanted.Add(new Vector2Int(c.x - rx, c.y - ry));
        wanted.Add(new Vector2Int(c.x - rx, c.y + ry - 1));
        wanted.Add(new Vector2Int(c.x + rx - 1, c.y - ry));
        wanted.Add(new Vector2Int(c.x + rx - 1, c.y + ry - 1));
        wanted.Add(new Vector2Int(c.x - 1, c.y + ry - 1));
        wanted.Add(new Vector2Int(c.x - 1, c.y - ry));

        int placed = 0;
        foreach (var w in wanted)
        {
            if (placed >= count) break;
            for (int t = 0; t < 9 && placed < count; t++)
            {
                Vector2Int o = new Vector2Int(w.x + (t % 3) - 1, w.y + (t / 3) - 1);
                if (!Free(used, o, new Vector2Int(2, 2), 1)) continue;
                Add(p, used, RaidBuildingKind.Tower, "Башня", o, new Vector2Int(2, 2), 600f * hpMul, col);
                placed++;
                break;
            }
            // по разу на каждую желаемую точку; если не встала - берём следующую
        }
        if (placed < count)
        {
            int fx = p.Style == Layout.Keep ? 12 : p.RingX, fy = p.Style == Layout.Keep ? 12 : p.RingY;
            placed += Place(p, used, rng, RaidBuildingKind.Tower, "Башня", 2, 2, count - placed, 4, fx, fy, 600f * hpMul, col, 1);
        }
        return placed;
    }

    // Места под заслоны: перед каждым проходом снаружи, и по дуге со стороны подхода (запад), если стен нет
    static void MakeSpots(Plan p, bool[,] used)
    {
        Vector2Int c = p.Center;
        var cand = new List<BarricadeSpot>();
        var centers = new List<Vector2>();

        // Перед проходами: запад первым (оттуда приходят чаще всего)
        var gaps = new List<Vector2>(p.Gaps);
        gaps.Sort((a, b) => a.x.CompareTo(b.x));
        foreach (var g in gaps)
        {
            Vector2 outward = (g - ((Vector2)c + new Vector2(0.5f, 0.5f))).normalized;
            bool vertical = Mathf.Abs(outward.x) > Mathf.Abs(outward.y);
            Vector2 at = g + (vertical ? new Vector2(Mathf.Sign(outward.x), 0f) : new Vector2(0f, Mathf.Sign(outward.y))) * 3f;
            cand.Add(SpotAt(at, vertical, vertical ? new Vector2(Mathf.Sign(outward.x), 0f) : new Vector2(0f, Mathf.Sign(outward.y))));
        }

        // Подходы с запада, юга и севера (без стен - основная оборона)
        int rx = p.RingX + 3, ry = p.RingY + 3;
        cand.Add(SpotAt(new Vector2(c.x - rx + 0.5f, c.y + 0.5f), true, Vector2.left));
        cand.Add(SpotAt(new Vector2(c.x - rx + 1.5f, c.y + ry * 0.5f), true, Vector2.left));
        cand.Add(SpotAt(new Vector2(c.x - rx + 1.5f, c.y - ry * 0.5f), true, Vector2.left));
        cand.Add(SpotAt(new Vector2(c.x + 0.5f, c.y + ry - 0.5f), false, Vector2.up));
        cand.Add(SpotAt(new Vector2(c.x + 0.5f, c.y - ry + 0.5f), false, Vector2.down));
        // Без стен - ещё кольцо у Ратуши
        if (!p.Walls)
        {
            cand.Add(SpotAt(new Vector2(c.x - 4.5f, c.y + 0.5f), true, Vector2.left));
            cand.Add(SpotAt(new Vector2(c.x + 0.5f, c.y + 4.5f), false, Vector2.up));
            cand.Add(SpotAt(new Vector2(c.x + 0.5f, c.y - 3.5f), false, Vector2.down));
        }

        int h = RaidMap.Size / 2;
        foreach (var s in cand)
        {
            if (s.origin.x < -h + 12) continue;   // не у самого места высадки
            if (!Free(used, s.origin, s.size, 0)) continue;
            bool near = false;
            foreach (var o in centers) if (Vector2.Distance(o, s.Center) < 3f) near = true;
            if (near) continue;
            for (int x = 0; x < s.size.x; x++)
                for (int y = 0; y < s.size.y; y++) used[s.origin.x + x + h, s.origin.y + y + h] = true;
            centers.Add(s.Center);
            p.Spots.Add(s);
        }
    }

    static BarricadeSpot SpotAt(Vector2 at, bool vertical, Vector2 facing)
    {
        Vector2Int size = vertical ? new Vector2Int(1, 3) : new Vector2Int(3, 1);
        Vector2Int o = new Vector2Int(Mathf.FloorToInt(at.x - size.x / 2f + 0.5f), Mathf.FloorToInt(at.y - size.y / 2f + 0.5f));
        return new BarricadeSpot { origin = o, size = size, facing = facing };
    }

    static int R(System.Random rng, int min, int max) { return max < min ? min : rng.Next(min, max + 1); }

    // Леса: пятна деревьев по всей карте, кроме места высадки, самой деревни и краёв.
    // От места высадки к деревне всегда остаётся просека.
    static void GrowForest(Plan p, bool[,] used, System.Random rng)
    {
        int size = RaidMap.Size, h = size / 2;
        Vector2Int c0 = p.Center;
        int landingX = -h + 9;
        int clumps = Mathf.RoundToInt(size * size / 160f);
        int keepX = Mathf.Max(p.RingX, p.Style == Layout.Keep ? 13 : 0) + 3;
        int keepY = Mathf.Max(p.RingY, p.Style == Layout.Keep ? 13 : 0) + 3;
        var set = new HashSet<Vector2Int>();

        for (int n = 0; n < clumps; n++)
        {
            int cx = rng.Next(-h + 2, h - 2), cy = rng.Next(-h + 2, h - 2);
            float rad = 1.5f + (float)rng.NextDouble() * 2.8f;
            int ri = Mathf.CeilToInt(rad);
            for (int x = cx - ri; x <= cx + ri; x++)
                for (int y = cy - ri; y <= cy + ri; y++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d > rad) continue;
                    if (rng.NextDouble() < d / rad * 0.55) continue;            // к краю пятна - реже
                    if (x <= -h + 1 || x >= h - 2 || y <= -h + 1 || y >= h - 2) continue;   // края карты чистые
                    if (x < landingX) continue;                                  // место высадки
                    if (Mathf.Abs(x - c0.x) <= keepX && Mathf.Abs(y - c0.y) <= keepY) continue;   // деревня
                    if (used[x + h, y + h]) continue;
                    set.Add(new Vector2Int(x, y));
                }
        }

        // Просека от места высадки к деревне
        for (int x = landingX - 9; x <= c0.x - keepX; x++)
            for (int y = -1; y <= 1; y++)
            {
                set.Remove(new Vector2Int(x, y));
                set.Remove(new Vector2Int(x, c0.y + y));
            }
        // ... и вдоль, до высоты деревни
        int ylo = Mathf.Min(0, c0.y) - 1, yhi = Mathf.Max(0, c0.y) + 1;
        for (int x = c0.x - keepX - 2; x <= c0.x - keepX; x++)
            for (int y = ylo; y <= yhi; y++) set.Remove(new Vector2Int(x, y));

        foreach (var c in set)
        {
            used[c.x + h, c.y + h] = true;
            p.Trees.Add(c);
        }
    }

    static void FillStorages(Plan p, System.Random rng, int L)
    {
        foreach (var b in p.Buildings)
        {
            Loot l = b.loot;
            switch (b.kind)
            {
                case RaidBuildingKind.Hall:
                    l.gold = R(rng, 150, 300) * L;
                    l.wood = R(rng, 80, 200) * L;
                    l.stone = R(rng, 60, 150) * L;
                    l.food = R(rng, 80, 200) * L;
                    if (L >= 2) { l.iron = R(rng, 10, 40) * L; l.oats = R(rng, 10, 40) * L; }
                    l.berries = R(rng, 0, 30) * L;
                    l.planks[(int)QualityTier.Medium] = R(rng, 2, 6) * L;
                    l.plates[(int)QualityTier.Medium] = R(rng, 1, 4) * L;
                    if (L >= 2) l.steel[(int)QualityTier.Medium] = R(rng, 1, 3) * (L - 1);
                    if (L >= 3) l.planks[(int)QualityTier.Good] = R(rng, 1, 4);
                    if (L >= 3) l.reinforced[(int)QualityTier.Medium] = R(rng, 1, 3);
                    break;
                case RaidBuildingKind.Farm:
                    l.food = R(rng, 40, 90) * L;
                    if (L >= 2) l.oats = R(rng, 10, 30) * L;
                    break;
                case RaidBuildingKind.Sawmill:
                    l.wood = R(rng, 40, 100) * L;
                    break;
                case RaidBuildingKind.Mine:
                    l.stone = R(rng, 30, 80) * L;
                    l.gold = R(rng, 10, 30) * L;
                    if (L >= 2) l.iron = R(rng, 5, 20) * L;
                    break;
                case RaidBuildingKind.Warehouse:
                    l.wood = R(rng, 60, 150) * L;
                    l.stone = R(rng, 60, 150) * L;
                    l.food = R(rng, 40, 120) * L;
                    l.gold = R(rng, 20, 60) * L;
                    break;
                case RaidBuildingKind.Workshop:
                    l.planks[(int)QualityTier.Medium] = R(rng, 2, 6) * L;
                    l.plates[(int)QualityTier.Medium] = R(rng, 1, 4) * L;
                    if (L >= 3) l.steel[(int)QualityTier.Good] = R(rng, 0, 2);
                    break;
                case RaidBuildingKind.Kitchen:
                    l.berries = R(rng, 15, 40);
                    l.oats = R(rng, 10, 30);
                    l.food = R(rng, 20, 50);
                    break;
                case RaidBuildingKind.Stable:
                    l.oats = R(rng, 30, 80);
                    break;
                case RaidBuildingKind.House:
                    l.food = R(rng, 5, 20);
                    l.gold = R(rng, 5, 15) * L;
                    break;
                case RaidBuildingKind.Barracks:
                    l.gold = R(rng, 10, 40) * L;
                    l.food = R(rng, 20, 50);
                    break;
            }
        }
    }

    // ---------- Защитники ----------

    static DefRole RoleOf(UnitType u)
    {
        if (u == null) return DefRole.Guard;
        if (u.IsHealer) return DefRole.Healer;
        if (u.requiresHorse) return DefRole.Rider;
        if (u.attackRange > 2f) return DefRole.Archer;
        return DefRole.Guard;
    }

    static void MakeDefenders(Plan p, RaidTarget t, System.Random rng)
    {
        // Всё войско, что сейчас дома; сильные - первыми в поле, остальные - в казармах (подкрепление)
        var names = new List<string>();
        foreach (var kv in t.HomeRoster()) for (int i = 0; i < kv.Value; i++) names.Add(kv.Key);
        for (int i = 0; i < names.Count; i++) { int j = rng.Next(i, names.Count); string tmp = names[i]; names[i] = names[j]; names[j] = tmp; }
        int onField = Mathf.Min(names.Count, MaxDefendersOnField);
        for (int i = onField; i < names.Count; i++) p.Reserve.Add(names[i]);
        names.RemoveRange(onField, names.Count - onField);

        Vector2 c = new Vector2(p.Center.x + 0.5f, p.Center.y + 0.5f);
        Vector2 hall = c;
        var towers = new List<Vector2>();
        var barracks = new List<Vector2>();
        foreach (var b in p.Buildings)
        {
            Vector2 bc = new Vector2(b.origin.x + b.size.x / 2f, b.origin.y + b.size.y / 2f);
            if (b.kind == RaidBuildingKind.Tower) towers.Add(bc);
            if (b.kind == RaidBuildingKind.Barracks || b.kind == RaidBuildingKind.Stable) barracks.Add(bc);
        }

        // Часовые - один-два снаружи, со стороны места высадки (запад)
        int sentries = Mathf.Min(names.Count / 4, p.Level >= 2 ? 2 : 1);
        int guardIdx = 0;
        foreach (var n in names)
        {
            UnitType u = FindUnit(n);
            DefRole role = RoleOf(u);
            Vector2 post;
            if (sentries > 0 && (role == DefRole.Guard || role == DefRole.Archer))
            {
                sentries--;
                role = DefRole.Sentry;
                float sx = c.x - Mathf.Max(p.RingX, p.Style == Layout.Keep ? 13 : 0) - 4f - (float)rng.NextDouble() * 4f;
                post = new Vector2(sx, c.y + ((float)rng.NextDouble() * 2f - 1f) * p.RingY * 0.7f);
            }
            else switch (role)
            {
                case DefRole.Guard:
                    // Часть мечников - гарнизон у Ратуши, часть - у проходов
                    if (p.Gaps.Count > 0 && guardIdx % 3 != 2)
                    {
                        Vector2 g = p.Gaps[guardIdx % p.Gaps.Count];
                        post = g + (hall - g).normalized * 1.8f;
                    }
                    else { role = DefRole.Garrison; post = hall + Ring(rng, 2.6f, 3.4f); }
                    guardIdx++;
                    break;
                case DefRole.Archer:
                    post = towers.Count > 0 ? towers[rng.Next(towers.Count)] + Ring(rng, 1.8f, 2.4f) : hall + Ring(rng, 3f, 4f);
                    break;
                case DefRole.Rider:
                    post = barracks.Count > 0 ? barracks[rng.Next(barracks.Count)] + Ring(rng, 2f, 2.8f) : hall + Ring(rng, 4f, 5f);
                    break;
                default:
                    post = hall + Ring(rng, 2.2f, 3f);
                    break;
            }
            p.Defenders.Add(new DefenderSpec { unitName = n, post = post, role = role });
        }
    }

    static Vector2 Ring(System.Random rng, float r0, float r1)
    {
        float a = (float)(rng.NextDouble() * Mathf.PI * 2.0);
        float r = r0 + (float)rng.NextDouble() * (r1 - r0);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
    }

    // ---------- Постройка на карте ----------

    public static float PowerMul(int level) { return 0.9f + 0.1f * level; }
    public static float GearChance(int level) { return 0.2f + 0.15f * level; }

    public static void Build(Plan p, RaidMap map, RaidTarget t)
    {
        active = p;
        float power = PowerMul(p.Level);
        foreach (var s in p.Buildings)
        {
            RaidBuilding b = RaidBuilding.Create(s.kind, s.title, s.origin, s.size, s.hp, s.color, power);
            b.Storage.Add(s.loot);   // копия: план не портится
        }
        map.MarkDirty();

        foreach (var d in p.Defenders)
        {
            UnitType u = FindUnit(d.unitName);
            if (u == null) continue;
            Vector2 pos = RaidMap.Origin + d.post;
            Vector2Int cell = map.WorldToCell(pos);
            if (map.IsBlocked(cell)) pos = map.CellCenter(map.NearestFree(cell));
            RaidUnit ru = RaidUnit.SpawnDefender(u, pos, power, GearChance(p.Level), p.Level);
            ru.Role = d.role;
        }

        RaidDefense.Create(t, p);
    }

    /// <summary>Вид войск соседей по имени - те же, что у набегов на нас (WarManager), со своими цветами.</summary>
    public static UnitType FindUnit(string name)
    {
        var wm = WarManager.Instance;
        if (wm != null && wm.enemyUnits != null)
        {
            UnitType u = wm.enemyUnits.Find(x => x != null && x.unitName == name);
            if (u != null) return u;
        }
        return Fallback.Find(x => x.unitName == name);
    }

    static readonly List<UnitType> Fallback = new List<UnitType>
    {
        new UnitType { unitName = "Мечник", maxHp = 100f, damage = 12f, attackRange = 1.1f, attackCooldown = 1f, moveSpeed = 1.6f, color = new Color(0.6f, 0.2f, 0.7f), bodyScale = 0.55f, usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
        new UnitType { unitName = "Лучник", maxHp = 70f, damage = 9f, attackRange = 6f, attackCooldown = 1.4f, moveSpeed = 1.5f, color = new Color(0.45f, 0.3f, 0.6f), bodyScale = 0.5f, usesWeapon = true, weaponKind = WeaponKind.Bow },
        new UnitType { unitName = "Рыцарь", maxHp = 240f, damage = 24f, attackRange = 1.2f, attackCooldown = 1.3f, moveSpeed = 1.3f, color = new Color(0.32f, 0.32f, 0.42f), bodyScale = 0.75f, usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
        new UnitType { unitName = "Полевой маг", maxHp = 70f, damage = 4f, attackRange = 4f, attackCooldown = 1.5f, moveSpeed = 1.6f, color = new Color(0.7f, 0.35f, 0.85f), bodyScale = 0.5f, healPerSecond = 9f, healRange = 4f },
        new UnitType { unitName = "Всадник", maxHp = 200f, damage = 20f, attackRange = 1.3f, attackCooldown = 1f, moveSpeed = 2.8f, color = new Color(0.8f, 0.25f, 0.45f), bodyScale = 0.8f, requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
        new UnitType { unitName = "Конный лучник", maxHp = 140f, damage = 11f, attackRange = 6.5f, attackCooldown = 1.2f, moveSpeed = 2.6f, color = new Color(0.6f, 0.35f, 0.7f), bodyScale = 0.75f, requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Bow },
    };

    // ---------- Сила войска (для решений соперников) ----------

    /// <summary>Боевая ценность бойца: здоровье + урон в секунду (стрелкам - надбавка за дальность) + лечение.</summary>
    public static float UnitPower(UnitType u)
    {
        if (u == null) return 1f;
        float dps = u.damage / Mathf.Max(0.3f, u.attackCooldown);
        float p = u.maxHp / 100f * 0.5f + dps / 10f * 0.5f * (u.attackRange > 2f ? 1.4f : 1f) + u.healPerSecond / 10f;
        return Mathf.Max(0.2f, p);
    }

    public static float RosterPower(Dictionary<string, int> roster)
    {
        float s = 0f;
        foreach (var kv in roster) s += UnitPower(FindUnit(kv.Key)) * Mathf.Max(0, kv.Value);
        return s;
    }

    // ---------- Разведданные для меню ----------

    public static string WealthText(int w)
    {
        if (w < 600) return "<color=#B0B0B0>бедная</color>";
        if (w < 1500) return "<color=#FFD060>зажиточная</color>";
        return "<color=#FFB040>богатая</color>";
    }
}
