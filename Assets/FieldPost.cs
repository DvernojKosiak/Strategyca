using UnityEngine;
using System.Collections.Generic;

// Новые виды - только в конец (номер вида = индекс в Specs и в запасах FieldGear / набора похода)
public enum PostKind { Barricade, Waypoint, SpikedBarricade, WheeledBarricade, QuickBarricade, FieldTower }

/// <summary>
/// Полевое укрепление: ставится прямо в бою - и дома (оборона), и на карте набега (нападение).
/// Виды (см. Specs):
///  - Заслон: стенка из брёвен. НЕПРОХОДИМА (для своих и для врагов - как стена), режет урон бойцам,
///    которые стоят за ней (враг должен быть по ту сторону стенки - важно, как её повернуть).
///    Дома враги ломают её, поэтому у неё есть прочность.
///  - Перевалочный пункт: то же + палатка. Раненые бойцы сами идут сюда и отдыхают до HealCap (70%) здоровья.
///    Лечебный запас ограничен (Pool), потом пункт работает просто как укрытие.
///  - Заслон с шипами (ветка «Тяжёлая стратегия»): кто рубит его вплотную, получает часть удара обратно (thorns),
///    а враги, прижатые к кольям, ранятся каждую секунду (spikeDps). В набеге защитники заслоны не рубят -
///    там работают только колья (защитник, подошедший вплотную, ранится).
///  - Заслон на колёсах (Тяжёлая стратегия): игрок приказывает катить его (OrderMove), ближайший свободный боец
///    берётся за него (CitizenAgent.pushingPost / RaidUnit.pushingPost) и толкает (TickPusher / TickRaidPusher).
///    На ходу изнашивается и получает больше урона. Его можно сдвинуть и чужой силой (Shove): Паровой отбойник
///    башни, чёрная дыра Мага стихий (тянет), пружинная ловушка, удары тяжёлых врагов (рыцари, конница).
///  - Быстрый заслон (Военный строитель): не изготавливается - ставится прямо из меню за дерево.
///    Вдвое слабее, короче, строится вдвое быстрее. Строители Двора военного строителя помогают строить
///    и чинят (AddBuildWork / Repair / NeedsWork).
///  - Полевая башня (всё Военное дело): упрощённая городская башня - сама стреляет стрелами (см. FieldTower.cs).
///    Ставится, только если ближе 6 клеток нет зелёной зоны врага.
/// Кроме быстрого заслона, укрепления изготавливает Кузня мастера (путь Военного дела), они лежат в запасе
/// Ратуши (см. FieldGear), а в бой берутся оттуда. В набег - через окно «В бой!».
/// Пока укрепление строится, оно очень хрупкое: прочность фундамента - 12%, удары по стройке вдвое сильнее,
/// а враг, подошедший вплотную, топчет и растаскивает её (в набеге тоже - защитники обычно укрепления не бьют).
/// Разнесли стройку - вещь потеряна.
/// Можно ставить на руины зданий (если руины ещё не начали отстраивать). Дома пункты сворачиваются сами
/// через некоторое время после конца войны (вещь возвращается в запас), а руины не начнут восстанавливаться,
/// пока рядом стоит укрепление. На карте набега укрепления остаются на поле и пропадают с ним.
/// Размещением и меню занимается FieldPostManager.
/// </summary>
public partial class FieldPost : MonoBehaviour
{
    public static readonly List<FieldPost> All = new List<FieldPost>();

    public class Spec
    {
        public string name, tag, info;
        public int wood, planks, steel;       // цена изготовления в Кузне: дерево, доски (среднего качества или лучше), сталь
        public float hp, buildSeconds, craftSeconds, length;
        public float coverMul;                // во сколько раз режется урон за стенкой
        public float healPool, healCap, healRate, healRadius;   // healPool = 0 - не лечит
        public Color wallColor;
        public WarSchool school = WarSchool.None;   // ветка Военного дела (None - любая)
        public bool crafted = true;                 // false - не изготавливается: ставится из меню прямо за дерево (wood)
        public float thorns;                        // шипы: доля удара ближнего боя, которая возвращается бьющему
        public float spikeDps;                      // шипы: урон в секунду врагам вплотную к стенке
        public bool wheeled;                        // на колёсах: его можно катить и сдвигать
        public float towerRange, towerDamage, towerCooldown;   // towerDamage > 0 - стреляет (Полевая башня)

        public bool IsTower { get { return towerDamage > 0f; } }
        public bool IsWall { get { return !IsTower; } }
    }

    // ---------- Числа (можно менять) ----------
    public static readonly Spec[] Specs =
    {
        new Spec { name = "Заслон", tag = "Заслон", info = "Непроходимая стенка: режет урон от врагов с той стороны",
                   wood = 12, planks = 2, steel = 0, hp = 260f, buildSeconds = 12f, craftSeconds = 20f, length = 3.2f, coverMul = 0.5f,
                   wallColor = new Color(0.50f, 0.34f, 0.17f) },
        new Spec { name = "Перевалочный пункт", tag = "Пункт", info = "Стенка + палатка: раненые отдыхают до 70% здоровья",
                   wood = 25, planks = 5, steel = 1, hp = 380f, buildSeconds = 18f, craftSeconds = 40f, length = 3.2f, coverMul = 0.55f,
                   healPool = 500f, healCap = 0.7f, healRate = 7f, healRadius = 3.2f,
                   wallColor = new Color(0.45f, 0.30f, 0.15f) },
        new Spec { name = "Заслон с шипами", tag = "Шипы", info = "Стенка с кольями: кто рубит её вплотную, получает удар обратно",
                   wood = 14, planks = 3, steel = 2, hp = 300f, buildSeconds = 14f, craftSeconds = 30f, length = 3.2f, coverMul = 0.5f,
                   thorns = 0.4f, spikeDps = 6f, school = WarSchool.Heavy,
                   wallColor = new Color(0.42f, 0.29f, 0.16f) },
        new Spec { name = "Заслон на колёсах", tag = "Колёса", info = "Боец катит его, куда прикажете. На ходу изнашивается",
                   wood = 16, planks = 4, steel = 1, hp = 240f, buildSeconds = 6f, craftSeconds = 35f, length = 2.4f, coverMul = 0.55f,
                   wheeled = true, school = WarSchool.Heavy,
                   wallColor = new Color(0.56f, 0.38f, 0.19f) },
        new Spec { name = "Быстрый заслон", tag = "Быстрый", info = "Без Кузни, сразу за дерево: вдвое слабее и короче, строится вдвое быстрее",
                   wood = 10, planks = 0, steel = 0, hp = 130f, buildSeconds = 6f, craftSeconds = 0f, length = 2.2f, coverMul = 0.55f,
                   crafted = false, school = WarSchool.Builder,
                   wallColor = new Color(0.64f, 0.49f, 0.28f) },
        new Spec { name = "Полевая башня", tag = "Башня", info = "Вышка с лучником: сама стреляет по врагам рядом",
                   wood = 20, planks = 6, steel = 2, hp = 300f, buildSeconds = 20f, craftSeconds = 60f, length = 0.6f, coverMul = 1f,
                   towerRange = 6f, towerDamage = 6f, towerCooldown = 1.3f,
                   wallColor = new Color(0.47f, 0.32f, 0.17f) },
    };

    public const int MaxPerMap = 6;            // сколько укреплений может стоять на одной карте
    public const float Vision = 5f;            // пункт рассеивает туман вокруг себя
    public const float SeekBelow = 0.5f;       // раненый с меньшим здоровьем идёт на пункт
    public const float CoverReach = 1.5f;      // насколько близко к стенке надо стоять, чтобы быть в укрытии
    const float PeaceDismantle = 45f;          // дома: через столько секунд мира пункт сворачивают
    const float RepairDelay = 8f, RepairSeconds = 90f;
    const float FoundationHp = 0.12f;          // недостроенный пункт очень хрупкий (было 0.35)
    public const float FoundationDamageMul = 2f;   // по стройке удары проходят вдвое сильнее
    const float TrampleReach = 1.3f;           // враг вплотную к стройке топчет и растаскивает её
    const float TramplePerSecond = 0.08f;      // доля полной прочности в секунду от каждого такого врага
    const float SpikeReach = 0.75f;            // враг ближе к стенке - на кольях
    const float MeleeReach = 2f;               // дальность удара, до которой бьющий считается «вплотную» (шипы, толчок)

    /// <summary>Этот вид доступен сейчас (путь и ветка проверяются вживую - их может переключить админ).</summary>
    public static bool Allowed(PostKind k)
    {
        Spec s = Specs[(int)k];
        return DevelopmentPath.IsWar && DevelopmentPath.WarAllows(s.school);
    }

    public static string NeedText(PostKind k)
    {
        Spec s = Specs[(int)k];
        if (s.school == WarSchool.None) return "Нужен путь Военного дела";
        return "Только для ветки «" + DevelopmentPath.WarName(s.school) + "»";
    }

    // ---------- Состояние ----------
    public PostKind Kind { get; private set; }
    public Spec S { get { return Specs[(int)Kind]; } }
    public bool Raid { get; private set; }        // true - стоит на карте набега
    public float Angle { get; private set; }
    public float Strength { get; private set; } = 1f;   // сила вещи (уровень Кузни, где её сделали)
    public float Progress { get; private set; }   // 0..1 - готовность
    public float Hp { get; private set; }
    public float Pool { get; private set; }       // сколько здоровья пункт ещё может раздать
    public List<Vector2Int> Cells { get; private set; }   // клетки, которые стенка перекрывает (локальные для своей карты)
    public bool Built { get { return Progress >= 1f; } }
    public bool Alive { get { return !dead; } }
    public float MaxHp { get { return S.hp * Strength; } }
    public float CurMaxHp { get { return Built ? MaxHp : MaxHp * FoundationHp; } }
    public float MaxPool { get { return S.healPool * Strength; } }
    public float HealCap { get { return S.healCap; } }
    public bool CanHeal { get { return !dead && Built && S.healPool > 0f && Pool > 1f; } }
    public bool UnderAttack { get { return Time.time - lastHit < RepairDelay; } }
    public Vector2 Position { get { return transform.position; } }

    Vector2 Dir { get { float a = Angle * Mathf.Deg2Rad; return new Vector2(Mathf.Cos(a), Mathf.Sin(a)); } }
    public Vector2 EndA { get { return Position - Dir * (S.length * 0.5f); } }
    public Vector2 EndB { get { return Position + Dir * (S.length * 0.5f); } }

    bool dead, poolWarned;
    float lastHit = -99f, peace, sparkTimer, spikeTimer;
    readonly List<SpriteRenderer> sprites = new List<SpriteRenderer>();

    static readonly Color SpikeSparkColor = new Color(0.85f, 0.85f, 0.9f);
    static readonly Color DustColor = new Color(0.62f, 0.52f, 0.38f, 0.8f);

    // ---------- Создание ----------

    /// <summary>strength - сила вещи из запаса; pool - остаток лечебного запаса (меньше 0 - полный).</summary>
    public static FieldPost Create(PostKind kind, Vector2 pos, float angle, bool raid, float strength = 1f, float pool = -1f)
    {
        Spec spec = Specs[(int)kind];
        if (spec.IsTower) angle = 0f;   // башня не поворачивается
        GameObject go = new GameObject("FieldPost_" + spec.tag);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        FieldPost p = go.AddComponent<FieldPost>();
        p.Kind = kind;
        p.Raid = raid;
        p.Angle = angle;
        p.Strength = Mathf.Max(0.1f, strength);
        p.Hp = p.MaxHp * FoundationHp;
        p.Pool = pool < 0f ? p.MaxPool : Mathf.Min(pool, p.MaxPool);
        p.poolWarned = p.S.healPool > 0f && p.Pool <= 1f;
        p.Cells = CellsFor(p.S, pos, angle, raid);
        MakeLook(p.transform, p.S, p.sprites);
        p.FindMovingParts();
        p.ApplyLook();
        NavDirty();
        return p;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); NavDirty(); }
    void OnDisable() { All.Remove(this); NavDirty(); }

    void OnDestroy()
    {
        ReleasePusher();
        HideMoveMark();
    }

    // Стенка меняет проходимость: навигацию дома и карты набега надо перестроить
    static void NavDirty()
    {
        GridNavigation.MarkDirty();
        if (RaidMap.Instance != null) RaidMap.Instance.MarkDirty();
    }

    /// <summary>
    /// Клетки, которые перекрывает стенка (в координатах своей карты). Диагональная стенка получается
    /// сплошной (по сторонам), чтобы между клетками нельзя было проскочить по диагонали.
    /// </summary>
    public static List<Vector2Int> CellsFor(Spec s, Vector2 pos, float angle, bool raid)
    {
        float a = angle * Mathf.Deg2Rad;
        Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        Vector2 o = raid ? RaidMap.Origin : Vector2.zero;
        float half = Mathf.Max(0.3f, s.length * 0.5f - 0.2f);
        int steps = Mathf.CeilToInt(half * 2f / 0.2f);

        var list = new List<Vector2Int>();
        Vector2Int prev = Vector2Int.zero;
        bool has = false;
        for (int i = 0; i <= steps; i++)
        {
            float t = -half + 2f * half * i / steps;
            Vector2 q = pos + d * t - o;
            Vector2Int c = new Vector2Int(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y));
            if (has && c == prev) continue;
            if (has && c.x != prev.x && c.y != prev.y)
            {
                Vector2Int link = new Vector2Int(c.x, prev.y);
                if (!list.Contains(link)) list.Add(link);
            }
            if (!list.Contains(c)) list.Add(c);
            prev = c;
            has = true;
        }
        return list;
    }

    /// <summary>Рисунок пункта (им же рисуется призрак при размещении). Локальная ось X - вдоль стенки.</summary>
    public static void MakeLook(Transform root, Spec s, List<SpriteRenderer> list)
    {
        SpriteRenderer Add(string n, Vector2 pos, Vector2 size, Color c, int order, float rot, bool round)
        {
            GameObject go = new GameObject(n);
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = round ? CombatFx.Circle() : CombatFx.Square();
            sr.color = c;
            sr.sortingOrder = order;
            list.Add(sr);
            return sr;
        }

        Color dark = new Color(s.wallColor.r * 0.6f, s.wallColor.g * 0.6f, s.wallColor.b * 0.6f, 1f);
        Color light = Color.Lerp(s.wallColor, Color.white, 0.25f);

        if (s.IsTower)
        {
            // Полевая башня: четыре ноги, площадка с бортиком, лучник и флажок
            Color steel = new Color(0.55f, 0.57f, 0.6f);
            Add("Shadow", new Vector2(0.06f, -0.08f), new Vector2(1.0f, 1.0f), new Color(0f, 0f, 0f, 0.25f), 3, 0f, true);
            Add("LegA", new Vector2(-0.34f, -0.3f), new Vector2(0.1f, 0.5f), dark, 4, 8f, false);
            Add("LegB", new Vector2(0.34f, -0.3f), new Vector2(0.1f, 0.5f), dark, 4, -8f, false);
            Add("Brace", new Vector2(0f, -0.32f), new Vector2(0.62f, 0.06f), dark, 4, 0f, false);
            Add("Deck", new Vector2(0f, 0.05f), new Vector2(0.86f, 0.42f), s.wallColor, 5, 0f, false);
            Add("Rail", new Vector2(0f, 0.22f), new Vector2(0.9f, 0.08f), light, 6, 0f, false);
            Add("Archer", new Vector2(0f, 0.36f), new Vector2(0.26f, 0.26f), new Color(0.32f, 0.5f, 0.85f), 7, 0f, true);
            Add("Bow", new Vector2(0.15f, 0.38f), new Vector2(0.05f, 0.26f), steel, 8, 0f, false);
            Add("Pole", new Vector2(-0.38f, 0.42f), new Vector2(0.04f, 0.5f), dark, 6, 0f, false);
            Add("Flag", new Vector2(-0.26f, 0.6f), new Vector2(0.22f, 0.14f), new Color(0.95f, 0.75f, 0.25f), 7, 0f, false);
            return;
        }

        float thick = s.crafted ? 0.3f : 0.22f;   // быстрый заслон - тоньше
        Add("Wall", Vector2.zero, new Vector2(s.length, thick), s.wallColor, 5, 0f, false);
        Add("Top", new Vector2(0f, thick * 0.2f), new Vector2(s.length * 0.92f, 0.08f), light, 6, 0f, false);
        Add("EndA", new Vector2(-s.length * 0.5f, 0f), new Vector2(0.24f, 0.42f), dark, 7, 0f, false);
        Add("EndB", new Vector2(s.length * 0.5f, 0f), new Vector2(0.24f, 0.42f), dark, 7, 0f, false);

        if (!s.crafted)
        {
            // Быстрый заслон: вбитые колышки и верёвка
            int n = Mathf.Max(2, Mathf.RoundToInt(s.length / 0.55f));
            for (int i = 1; i < n; i++)
            {
                float x = -s.length * 0.5f + s.length * i / n;
                Add("Stake", new Vector2(x, 0f), new Vector2(0.09f, 0.34f), dark, 7, 0f, false);
            }
            Add("Rope", new Vector2(0f, 0.1f), new Vector2(s.length * 0.95f, 0.03f), new Color(0.85f, 0.78f, 0.55f), 8, 0f, false);
        }

        if (s.thorns > 0f || s.spikeDps > 0f)
        {
            // Колья с обеих сторон стенки
            Color spike = new Color(0.72f, 0.74f, 0.78f);
            int n = Mathf.Max(3, Mathf.RoundToInt(s.length / 0.36f));
            for (int i = 0; i < n; i++)
            {
                float x = -s.length * 0.5f + s.length * (i + 0.5f) / n;
                Add("SpikeU", new Vector2(x, 0.2f), new Vector2(0.13f, 0.13f), spike, 4, 45f, false);
                Add("SpikeD", new Vector2(x, -0.2f), new Vector2(0.13f, 0.13f), spike, 4, 45f, false);
            }
        }

        if (s.wheeled)
        {
            // Два колеса со спицами (крутятся на ходу) и ручка сзади
            Color tire = new Color(0.18f, 0.13f, 0.08f);
            Color hub = new Color(0.7f, 0.66f, 0.55f);
            float wx = s.length * 0.5f - 0.38f;
            foreach (float sx in new[] { -1f, 1f })
            {
                Add("Wheel", new Vector2(sx * wx, -0.24f), new Vector2(0.38f, 0.38f), tire, 8, 0f, true);
                Add("Spoke", new Vector2(sx * wx, -0.24f), new Vector2(0.32f, 0.05f), hub, 9, 0f, false);
                Add("Spoke", new Vector2(sx * wx, -0.24f), new Vector2(0.32f, 0.05f), hub, 9, 90f, false);
                Add("Hub", new Vector2(sx * wx, -0.24f), new Vector2(0.1f, 0.1f), hub, 10, 0f, true);
            }
            Add("Handle", new Vector2(0f, -0.3f), new Vector2(0.5f, 0.06f), dark, 6, 0f, false);
        }

        if (s.healPool > 0f)
        {
            Color red = new Color(0.85f, 0.15f, 0.15f);
            Add("Tent", new Vector2(0f, -0.62f), new Vector2(1.1f, 0.6f), new Color(0.88f, 0.82f, 0.62f), 4, 0f, false);
            Add("CrossH", new Vector2(0f, -0.62f), new Vector2(0.38f, 0.1f), red, 6, 0f, false);
            Add("CrossV", new Vector2(0f, -0.62f), new Vector2(0.1f, 0.38f), red, 6, 0f, false);
            Add("Pole", new Vector2(0.68f, -0.35f), new Vector2(0.05f, 0.7f), dark, 6, 0f, false);
            Add("Flag", new Vector2(0.86f, -0.14f), new Vector2(0.32f, 0.2f), new Color(0.3f, 0.7f, 1f), 7, 0f, false);
        }
    }

    void ApplyLook()
    {
        float a = Built ? 1f : 0.5f;   // стройка - полупрозрачная
        foreach (var sr in sprites)
        {
            if (sr == null) continue;
            Color c = sr.color;
            c.a = sr.name == "Shadow" ? a * 0.25f : a;
            sr.color = c;
        }
    }

    // ---------- Цена изготовления (платит Кузня мастера) ----------

    static int PlanksHave()
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, RefinedKind.Planks, QualityTier.Medium) : 0;
    }

    static int SteelHave()
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) : 0;
    }

    public static bool CanPay(Spec s)
    {
        var rm = ResourceManager.Instance;
        return rm != null && rm.wood >= s.wood && PlanksHave() >= s.planks && SteelHave() >= s.steel;
    }

    public static void Pay(Spec s)
    {
        var rm = ResourceManager.Instance;
        if (rm != null) rm.Spend(0, s.wood, 0, 0);
        var st = TownHallStorage.Instance;
        if (st == null) return;
        if (s.planks > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, s.planks);
        if (s.steel > 0) UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, s.steel);
    }

    /// <summary>Вернуть материалы (Кузню снесли посреди изготовления).</summary>
    public static void Refund(Spec s)
    {
        var rm = ResourceManager.Instance;
        if (rm != null && s.wood > 0) rm.Add(0, s.wood, 0, 0);
        var st = TownHallStorage.Instance;
        if (st == null) return;
        if (s.planks > 0) st.AddPlanks(QualityTier.Medium, s.planks);
        if (s.steel > 0) st.AddSteel(QualityTier.Medium, s.steel);
    }

    public static string CostPlain(Spec s)
    {
        var parts = new List<string>();
        if (s.wood > 0) parts.Add(s.wood + " дер.");
        if (s.planks > 0) parts.Add(s.planks + " досок");
        if (s.steel > 0) parts.Add(s.steel + " стали");
        return string.Join(", ", parts);
    }

    public static string MissingText(Spec s)
    {
        var rm = ResourceManager.Instance;
        var parts = new List<string>();
        int w = rm != null ? rm.wood : 0;
        if (w < s.wood) parts.Add($"{s.wood - w} дерева");
        int pl = PlanksHave();
        if (pl < s.planks) parts.Add($"{s.planks - pl} досок");
        int st = SteelHave();
        if (st < s.steel) parts.Add($"{s.steel - st} стали");
        return string.Join(", ", parts);
    }

    /// <summary>Сколько быстрых заслонов хватает дерева в казне (они не изготавливаются - платят на месте).</summary>
    public static int AffordableQuick()
    {
        var rm = ResourceManager.Instance;
        Spec s = Specs[(int)PostKind.QuickBarricade];
        return rm != null && s.wood > 0 ? rm.wood / s.wood : 0;
    }

    // ---------- Геометрия ----------

    public Vector2 ClosestPoint(Vector2 p)
    {
        Vector2 a = EndA, ab = EndB - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
        return a + ab * t;
    }

    public float DistanceTo(Vector2 p) { return Vector2.Distance(p, ClosestPoint(p)); }

    public bool HitBy(Vector2 world)
    {
        return DistanceTo(world) <= 0.9f || Vector2.Distance(Position, world) <= 1.2f;
    }

    static float Cr(Vector2 a, Vector2 b, Vector2 c) { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }

    static bool SegCross(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        float d1 = Cr(q1, q2, p1), d2 = Cr(q1, q2, p2), d3 = Cr(p1, p2, q1), d4 = Cr(p1, p2, q2);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    // Боец target укрыт от выстрела / удара из точки from: стоит у стенки, а линия от врага пересекает её
    bool Shields(Vector2 target, Vector2 from)
    {
        if (DistanceTo(target) > CoverReach) return false;
        Vector2 ext = Dir * 0.35f;
        return SegCross(from, target, EndA - ext, EndB + ext);
    }

    /// <summary>Множитель урона для бойца в точке target, по которому бьют из точки from (1 - укрытия нет).</summary>
    public static float CoverFactor(Vector2 target, Vector2 from)
    {
        float best = 1f;
        foreach (var p in All)
        {
            if (p == null || p.dead || !p.Built) continue;
            if (p.S.coverMul < best && p.Shields(target, from)) best = p.S.coverMul;
        }
        return best;
    }

    /// <summary>Куда встать отдыхать: за стенкой, подальше от врага (threat). lateral разводит бойцов вдоль стенки.</summary>
    public Vector2 RestSpot(Vector2 me, Vector2? threat, float lateral)
    {
        Vector2 d = Dir;
        Vector2 n = new Vector2(-d.y, d.x);
        float side;
        if (threat.HasValue) side = Vector2.Dot(threat.Value - Position, n) > 0f ? -1f : 1f;
        else side = Vector2.Dot(me - Position, n) >= 0f ? 1f : -1f;
        return Position + n * (side * 0.85f) + d * lateral;
    }

    // ---------- Запросы ----------

    public static int CountOn(bool raid)
    {
        int n = 0;
        foreach (var p in All) if (p != null && !p.dead && p.Raid == raid) n++;
        return n;
    }

    public static int CountOn(bool raid, PostKind kind)
    {
        int n = 0;
        foreach (var p in All) if (p != null && !p.dead && p.Raid == raid && p.Kind == kind) n++;
        return n;
    }

    /// <summary>Укрепление стоит на клетках этого здания (дома). Пока оно стоит, руины не отстраивают.</summary>
    public static bool AnyOn(BuildingInstance b)
    {
        if (b == null || b.type == null) return false;
        Vector2Int o = b.OriginCell, sz = b.type.size;
        foreach (var p in All)
        {
            if (p == null || p.dead || p.Raid || p.Cells == null) continue;
            foreach (var c in p.Cells)
                if (c.x >= o.x && c.x < o.x + sz.x && c.y >= o.y && c.y < o.y + sz.y) return true;
        }
        return false;
    }

    /// <summary>Враги дома могут бить этот пункт.</summary>
    public static bool Targetable(FieldPost p)
    {
        return p != null && p.isActiveAndEnabled && !p.dead && !p.Raid;
    }

    public static FieldPost Nearest(Vector2 from, bool raid, FieldPost ignore, float ignoreUntil)
    {
        FieldPost best = null;
        float bestD = float.MaxValue;
        foreach (var p in All)
        {
            if (p == null || p.dead || p.Raid != raid) continue;
            if (p == ignore && Time.time < ignoreUntil) continue;
            float d = p.DistanceTo(from);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>Ближайший готовый пункт с лечебными припасами в радиусе maxDist.</summary>
    public static FieldPost FindHealPost(Vector2 from, bool raid, float maxDist)
    {
        FieldPost best = null;
        float bestD = maxDist;
        foreach (var p in All)
        {
            if (p == null || p.Raid != raid || !p.CanHeal) continue;
            float d = p.DistanceTo(from);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>Конец набега: укрепления на поле боя пропадают вместе с ним.</summary>
    public static void ClearRaid()
    {
        foreach (var p in new List<FieldPost>(All))
        {
            if (p == null || !p.Raid) continue;
            p.dead = true;
            Destroy(p.gameObject);
        }
        NavDirty();
    }

    public string InfoText
    {
        get
        {
            string t = $"<b>{S.name}</b>\n";
            t += Built ? $"прочность {Mathf.CeilToInt(Hp)}/{Mathf.CeilToInt(MaxHp)}" : $"строится: {Progress * 100f:F0}%";
            if (S.IsTower)
                t += $"\nстрелы: урон {TowerDamage:F0}, дальность {S.towerRange:F0} кл., раз в {S.towerCooldown:F1} с";
            else
                t += $"\nнепроходим; укрытие: урон ×{S.coverMul:F2} (если враг по ту сторону)";
            if (S.thorns > 0f) t += $"\nшипы: {S.thorns * 100f:F0}% удара вплотную - обратно, {S.spikeDps * Strength:F0}/с прижатым";
            if (S.wheeled)
            {
                if (HasMoveOrder) t += "\n" + (Moving ? "<color=#FFD070>катят</color>" : "<color=#FFD070>ждёт, кто покатит</color>");
                else t += "\nможно катить (меню укреплений, «Катить»)";
            }
            if (S.healPool > 0f)
                t += Pool > 1f ? $"\nлечебный запас: {Mathf.CeilToInt(Pool)}/{Mathf.CeilToInt(MaxPool)} (отдых до {S.healCap * 100f:F0}%)"
                               : "\n<color=#FF8060>лечебный запас кончился</color>";
            return t;
        }
    }

    // ---------- Работа строителей (Двор военного строителя) ----------

    /// <summary>Есть работа: не достроен или повреждён (и ещё цел).</summary>
    public bool NeedsWork { get { return !dead && (!Built || Hp < CurMaxHp - 0.5f); } }

    /// <summary>Строители помогают: продвинуть стройку так, будто строили ещё seconds секунд.</summary>
    public void AddBuildWork(float seconds)
    {
        if (dead || Built || seconds <= 0f) return;
        Progress = Mathf.Min(1f, Progress + seconds / Mathf.Max(0.5f, S.buildSeconds));
        if (Built) FinishBuild();
    }

    /// <summary>Починить на amount прочности (не выше полной; недостроенный - не выше фундамента).</summary>
    public void Repair(float amount)
    {
        if (dead || amount <= 0f) return;
        Hp = Mathf.Min(CurMaxHp, Hp + amount);
    }

    void FinishBuild()
    {
        Hp = MaxHp;
        ApplyLook();
        WarManager.Notify($"{S.name} готов", true);
    }

    // ---------- Урон и снос ----------

    public void TakeDamage(float dmg, EnemyAgent by = null)
    {
        if (dead || dmg <= 0f) return;
        float raw = dmg;
        if (!Built) dmg *= FoundationDamageMul;   // недостроенное ломается легко
        if (Moving) dmg *= MovingDamageMul;       // на ходу колёса и рама уязвимы
        Hp -= dmg;
        lastHit = Time.time;
        if (Hp <= 0f) { Break(null); return; }

        // Удар вплотную: шипы возвращают часть удара, тяжёлый враг сдвигает заслон на колёсах
        if (!Built || !EnemyAgent.Alive(by) || by.Unit == null || by.Unit.attackRange > MeleeReach) return;
        if (S.thorns > 0f)
        {
            float back = raw * S.thorns;
            // Убитый шипами бросает награбленное прямо тут - город его забирает
            if (by.HasLoot && by.Hp <= back) { by.Carried.DepositToTown(); CombatFx.LootFly(by.Position, Position); }
            by.TakeDamage(back, null, null, true);   // кол в руку - щит не поможет
            CombatFx.Spark(Vector2.Lerp(ClosestPoint(by.Position), by.Position, 0.5f), SpikeSparkColor, 0.2f, 0.25f);
            if (!EnemyAgent.Alive(by)) return;
        }
        if (S.wheeled && by.KnockFactor < HeavyKnock)
        {
            Vector2 away = Position - by.Position;
            if (away.sqrMagnitude > 0.0001f) Shove(away.normalized * HeavyShove * (1f - by.KnockFactor));
        }
    }

    void Break(string text)
    {
        if (dead) return;
        bool wasBuilt = Built;
        dead = true;
        NavDirty();
        CombatFx.Death(Position);
        if (text == null) text = wasBuilt ? $"Враги разрушили: {S.name}" : $"Враги разнесли стройку: {S.name} (вещь потеряна)";
        WarManager.Notify(text, false);
        Destroy(gameObject);
    }

    /// <summary>
    /// Убрать укрепление. toStock - вернуть вещь в запас (в набеге - в то, что взяли с собой):
    /// с тем же остатком лечебного запаса, прочность восстановится. Быстрый заслон в запас не идёт -
    /// разбирают на брёвна: вернётся половина дерева (недостроенный - всё).
    /// </summary>
    public void Dismantle(bool toStock)
    {
        if (dead) return;
        dead = true;
        if (toStock) GiveBack();
        NavDirty();
        Destroy(gameObject);
    }

    /// <summary>Сколько дерева вернёт разборка быстрого заслона.</summary>
    public int QuickRefund { get { return S.crafted ? 0 : (Built ? Mathf.CeilToInt(S.wood * 0.5f) : S.wood); } }

    void GiveBack()
    {
        if (!S.crafted)
        {
            var res = ResourceManager.Instance;
            if (res != null && QuickRefund > 0) res.Add(0, QuickRefund, 0, 0);
            return;
        }
        FieldGear.Item it = new FieldGear.Item(Kind, Strength);
        it.pool = Pool;
        if (Raid)
        {
            RaidManager rm = RaidManager.Instance;
            if (rm != null) { rm.ReturnKitItem(it); return; }
        }
        FieldGear.Add(it);
    }

    // ---------- Жизнь ----------

    void Update()
    {
        if (dead) return;
        float dt = Time.deltaTime;

        if (!Built)
        {
            // Враги вплотную топчут стройку: в набеге защитники не бьют укрепления, но недостроенное растаскивают
            int tramplers = Raid ? CountDefendersNear(Position, TrampleReach) : CountEnemiesNear(Position, TrampleReach);
            if (tramplers > 0)
            {
                TakeDamage(MaxHp * TramplePerSecond * tramplers * dt / FoundationDamageMul);
                if (dead) return;
            }

            // Пока рядом враги - не строится
            bool threat = Raid ? RaidUnit.NearestOf(false, Position, 4.5f) != null : EnemyAgent.AnyWithin(Position, 4.5f);
            if (!threat)
            {
                Progress = Mathf.Min(1f, Progress + dt / Mathf.Max(0.5f, S.buildSeconds));
                if (Built) FinishBuild();
            }
        }

        // Повреждённый пункт понемногу чинится, если его не бьют
        if (Hp < CurMaxHp && !UnderAttack && !Moving) Hp = Mathf.Min(CurMaxHp, Hp + MaxHp / RepairSeconds * dt);

        HealTick(dt);
        SpikeTick(dt);
        if (S.IsTower) TowerTick(dt);
        if (dead) return;
        if (S.wheeled) WheelTick(dt);
        if (dead) return;

        // Дома: мир наступил - через некоторое время пункт сворачивают
        if (!Raid)
        {
            if (WarManager.IsWar) peace = 0f;
            else
            {
                peace += dt;
                if (peace >= PeaceDismantle)
                {
                    WarManager.Notify(S.crafted ? $"{S.name} свернули — вернулся в запас Ратуши"
                                                : $"{S.name} разобрали — вернулось {QuickRefund} дерева", true);
                    Dismantle(true);
                }
            }
        }
    }

    static int CountDefendersNear(Vector2 p, float r)
    {
        int n = 0;
        foreach (var u in RaidUnit.All)
            if (u != null && u.Alive && !u.Attacker && Vector2.Distance(u.Position, p) <= r) n++;
        return n;
    }

    static int CountEnemiesNear(Vector2 p, float r)
    {
        int n = 0;
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, p) <= r) n++;
        return n;
    }

    // Колья: враги, прижатые к готовому заслону с шипами, ранятся (в набеге - защитники; они заслоны не рубят)
    void SpikeTick(float dt)
    {
        if (S.spikeDps <= 0f || !Built) return;
        spikeTimer -= dt;
        if (spikeTimer > 0f) return;
        spikeTimer = 0.5f;
        float dmg = S.spikeDps * Strength * 0.5f;

        if (Raid)
        {
            foreach (var u in new List<RaidUnit>(RaidUnit.All))
            {
                if (u == null || !u.Alive || u.Attacker || DistanceTo(u.Position) > SpikeReach) continue;
                u.TakeDamage(dmg, null, true);
                CombatFx.Spark(u.Position, SpikeSparkColor, 0.14f, 0.2f);
            }
        }
        else
        {
            foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
            {
                if (!EnemyAgent.Alive(e) || DistanceTo(e.Position) > SpikeReach) continue;
                if (e.HasLoot && e.Hp <= dmg) { e.Carried.DepositToTown(); CombatFx.LootFly(e.Position, Position); }
                e.TakeDamage(dmg, null, null, true);
                CombatFx.Spark(e.Position, SpikeSparkColor, 0.14f, 0.2f);
            }
        }
    }

    // Раненые бойцы рядом (не в бою) лечатся до HealCap за счёт лечебного запаса пункта
    void HealTick(float dt)
    {
        if (S.healPool <= 0f || !Built) return;
        sparkTimer -= dt;
        bool spark = false;
        Vector2 pos = Position;

        if (Pool > 0f)
        {
            if (Raid)
            {
                foreach (var u in RaidUnit.All)
                {
                    if (Pool <= 0f) break;
                    if (u == null || !u.Attacker || !u.Alive || u.InCombat) continue;
                    if (Vector2.Distance(u.Position, pos) > S.healRadius) continue;
                    float room = S.healCap * u.MaxHp - u.Hp;
                    if (room <= 0f) continue;
                    float amt = Mathf.Min(Mathf.Min(S.healRate * dt, room), Pool);
                    u.Heal(amt);
                    Pool -= amt;
                    if (sparkTimer <= 0f) { CombatFx.HealSpark(pos, u.Position); spark = true; }
                }
            }
            else
            {
                foreach (var a in CitizenAgent.All)
                {
                    if (Pool <= 0f) break;
                    if (a == null || a.IsDead || !a.IsSoldier || a.IsInsideBuilding || a.InCombat) continue;
                    if (Vector2.Distance(a.transform.position, pos) > S.healRadius) continue;
                    float room = S.healCap * a.MaxHp - a.Hp;
                    if (room <= 0f) continue;
                    float got = a.ReceiveHeal(Mathf.Min(Mathf.Min(S.healRate * dt, room), Pool));
                    Pool -= got;
                    if (sparkTimer <= 0f) { CombatFx.HealSpark(pos, a.transform.position); spark = true; }
                }
            }
        }

        if (spark) sparkTimer = 0.35f;
        if (Pool <= 1f && !poolWarned)
        {
            poolWarned = true;
            WarManager.Notify($"{S.name}: лечебный запас кончился — осталось только укрытие", false);
        }
    }

    // =====================================================================
    // ---------- Заслон на колёсах: катить, толкать, тянуть ----------
    // =====================================================================

    public const float PushSpeedMul = 0.45f;    // с заслоном боец идёт медленнее
    public const float MovingDamageMul = 1.3f;  // на ходу удары по нему сильнее
    const float WearPerCell = 0.012f;           // износ: доля полной прочности за клетку пути
    const float CrashWear = 0.05f;              // отбросило в препятствие - доля полной прочности
    const float HeavyKnock = 0.5f;              // враг с KnockFactor меньше - «тяжёлый» (рыцарь, конница)
    const float HeavyShove = 0.45f;             // на сколько клеток удар тяжёлого сдвигает заслон (× тяжесть)
    public const float MoveMaxDistance = 22f;   // дальше за раз катить нельзя
    const float PusherSearch = 24f;             // где искать бойца, который покатит
    const float PusherFightReach = 3f;          // враг ближе - боец бросает заслон и дерётся
    const float ReachTimeout = 25f;             // не дошёл до заслона за это время - отмена
    const float NoPusherTimeout = 30f;          // никто так и не взялся - отмена

    bool hasMoveOrder, attached;
    Vector2 moveTarget;
    CitizenAgent homePusher;
    RaidUnit raidPusher;
    GridMover homeMover;
    RaidMover raidMover;
    float pusherSince, lastPusherAt, nextAssign, blockedTime, dustTimer;
    GameObject moveMark;
    readonly List<Transform> spokes = new List<Transform>();
    float spin;

    public bool HasMoveOrder { get { return hasMoveOrder && !dead; } }
    public bool Moving { get { return HasMoveOrder && attached && HasPusher; } }
    public Vector2 MoveTarget { get { return moveTarget; } }
    bool HasPusher { get { return homePusher != null || raidPusher != null; } }

    public string PusherName
    {
        get
        {
            if (homePusher != null) return homePusher.UnitName + " " + homePusher.CitizenName;
            if (raidPusher != null) return raidPusher.Name;
            return "";
        }
    }

    void FindMovingParts()
    {
        spokes.Clear();
        foreach (var sr in sprites)
            if (sr != null && sr.name == "Spoke") spokes.Add(sr.transform);
    }

    /// <summary>Приказ: докатить заслон до точки target. Возвращает false, если нельзя (причина - в уведомлении).</summary>
    public bool OrderMove(Vector2 target)
    {
        if (dead || !S.wheeled) return false;
        if (!Built) { WarManager.Notify($"{S.name} ещё строится — катить нельзя", false); return false; }
        hasMoveOrder = true;
        moveTarget = target;
        attached = false;
        blockedTime = 0f;
        lastPusherAt = Time.time;
        ReleasePusher();
        if (!TryAssignPusher(true))
            WarManager.Notify($"{S.name}: нет свободного бойца рядом — покатят, когда кто-то освободится", false);
        ShowMoveMark();
        return true;
    }

    /// <summary>Отменить перекатывание (why - уведомление, null - молча).</summary>
    public void StopMove(string why)
    {
        bool had = hasMoveOrder;
        hasMoveOrder = false;
        attached = false;
        ReleasePusher();
        HideMoveMark();
        if (had && !string.IsNullOrEmpty(why)) WarManager.Notify(why, false);
    }

    // Отпустить бойца (у него сбрасывается pushingPost - он вернётся к своему приказу)
    void ReleasePusher()
    {
        if (homePusher != null && homePusher.pushingPost == this) homePusher.pushingPost = null;
        if (raidPusher != null && raidPusher.pushingPost == this) raidPusher.pushingPost = null;
        homePusher = null;
        raidPusher = null;
        homeMover = null;
        raidMover = null;
        attached = false;
    }

    // Боец бросил заслон (бой, ранен) - заслон ждёт следующего
    void DropPusher()
    {
        homePusher = null;
        raidPusher = null;
        attached = false;
        nextAssign = Time.time + 3f;
        lastPusherAt = Time.time;
    }

    static bool CanPushHome(CitizenAgent c)
    {
        if (c == null || c.IsDead || !c.IsSoldier || c.IsInsideBuilding) return false;
        if (c.IsOnExpedition || c.IsSupplying || c.IsLyingInBed || c.IsDeparting) return false;
        if (c.IsFieldCannon || c.IsCatapult || c.InCombat || c.HpFraction < 0.4f) return false;
        if (c.ExpUnit != null && c.ExpUnit.IsHealer) return false;   // лекарь нужен в бою
        if (EnemyAgent.AnyWithin(c.transform.position, PusherFightReach + 1f)) return false;   // сейчас не до того
        return c.pushingPost == null;
    }

    static bool CanPushRaid(RaidUnit u)
    {
        if (u == null || !u.Attacker || !u.Alive || u.IsGolem || u.Source == null) return false;
        if (u.IsCannon || u.IsCatapult || u.IsHealerUnit || u.InCombat || u.HpFraction < 0.4f) return false;
        if (u.Mode == RaidUnit.Order.Retreat) return false;
        if (RaidUnit.NearestOf(false, u.Position, PusherFightReach + 1f) != null) return false;
        return u.pushingPost == null;
    }

    bool TryAssignPusher(bool loud)
    {
        nextAssign = Time.time + 2f;
        Vector2 me = Position;
        if (Raid)
        {
            RaidUnit best = null;
            float bestD = PusherSearch;
            foreach (var u in RaidUnit.All)
            {
                if (!CanPushRaid(u)) continue;
                float d = Vector2.Distance(u.Position, me);
                if (d < bestD) { bestD = d; best = u; }
            }
            if (best == null) return false;
            raidPusher = best;
            best.pushingPost = this;
            raidMover = new RaidMover();
        }
        else
        {
            CitizenAgent best = null;
            float bestD = PusherSearch;
            foreach (var c in CitizenAgent.All)
            {
                if (!CanPushHome(c)) continue;
                float d = Vector2.Distance(c.transform.position, me);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return false;
            homePusher = best;
            best.pushingPost = this;
            homeMover = new GridMover();
        }
        attached = false;
        blockedTime = 0f;
        pusherSince = Time.time;
        lastPusherAt = Time.time;
        if (loud) WarManager.Notify($"{S.name}: катит {PusherName}", true);
        return true;
    }

    /// <summary>
    /// Шаг бойца, который катит заслон (зовётся из главного цикла солдата дома).
    /// true - боец занят заслоном в этом кадре; false - больше не катит (pushingPost надо сбросить).
    /// </summary>
    public bool TickPusher(CitizenAgent c)
    {
        if (c == null || dead || !hasMoveOrder || c != homePusher)
        {
            if (c != null && c == homePusher) { homePusher = null; attached = false; }
            return false;
        }
        if (c.IsDead || c.IsInsideBuilding) { DropPusher(); return false; }
        if (EnemyAgent.AnyWithin(c.transform.position, PusherFightReach) || c.HpFraction < 0.3f)
        {
            DropPusher();   // бой: бросает заслон, дерётся; потом заслон подхватят снова
            return false;
        }
        return PushStep(c.transform, c.moveSpeed);
    }

    /// <summary>То же на карте набега (зовётся из ИИ нашего налётчика).</summary>
    public bool TickRaidPusher(RaidUnit u)
    {
        if (u == null || dead || !hasMoveOrder || u != raidPusher)
        {
            if (u != null && u == raidPusher) { raidPusher = null; attached = false; }
            return false;
        }
        if (!u.Alive || u.Source == null || u.Mode == RaidUnit.Order.Retreat) { DropPusher(); return false; }
        if (RaidUnit.NearestOf(false, u.Position, PusherFightReach) != null || u.HpFraction < 0.3f)
        {
            DropPusher();
            return false;
        }
        return PushStep(u.transform, u.Source.moveSpeed);
    }

    // На сколько отойти от центра заслона назад, чтобы взяться за него (зависит от направления)
    float GripOffset(Vector2 dir)
    {
        return Mathf.Abs(Vector2.Dot(Dir, dir)) * S.length * 0.5f + 0.55f;
    }

    bool PushStep(Transform tr, float speed)
    {
        Vector2 to = moveTarget - Position;
        float dist = to.magnitude;
        if (dist <= 0.12f)
        {
            WarManager.Notify($"{S.name} на месте", true);
            StopMove(null);
            return false;
        }
        Vector2 dir = to / dist;
        float off = GripOffset(dir);
        Vector2 grip = Position - dir * off;
        Vector2 me = tr.position;

        if (!attached)
        {
            float gd = Vector2.Distance(me, grip);
            // Место за заслоном занято (стена, дерево) - берётся сбоку, как дотянется
            if (gd <= 0.45f || (gd <= 1.1f && BlockedAt(grip))) { attached = true; blockedTime = 0f; }
            else
            {
                // Идёт к заслону
                int fails = 0;
                if (Raid)
                {
                    RaidMap map = RaidMap.Instance;
                    if (map == null) return false;
                    if (raidMover == null) raidMover = new RaidMover();
                    raidMover.Step(tr, map, grip, speed, 0.3f);
                    fails = raidMover.Fails;
                }
                else
                {
                    GridNavigation nav = GridNavigation.Instance;
                    if (nav == null) return false;
                    if (homeMover == null) homeMover = new GridMover();
                    homeMover.Step(tr, nav, grip, speed, 0.3f);
                    fails = homeMover.Fails;
                }
                if (fails >= 4 || Time.time - pusherSince > ReachTimeout)
                {
                    StopMove($"{S.name}: боец не может подойти — перекатывание отменено");
                    return false;
                }
                return true;
            }
        }

        // Отбросило от заслона - снова подойти
        if (Vector2.Distance(me, grip) > 1.2f) { attached = false; pusherSince = Time.time; return true; }

        float step = Mathf.Min(dist, speed * PushSpeedMul * Time.deltaTime);
        float moved = MoveBy(dir, step, true, tr);
        if (moved < step * 0.5f)
        {
            blockedTime += Time.deltaTime;
            if (blockedTime > 2.5f)
            {
                StopMove($"{S.name}: дальше не проехать — упёрлись");
                return false;
            }
        }
        else
        {
            blockedTime = 0f;
            Wear(moved, $"{S.name} развалился на ходу");
            if (dead) return false;
        }
        Vector2 g = Position - dir * off;
        tr.position = new Vector3(g.x, g.y, tr.position.z);
        return true;
    }

    bool BlockedAt(Vector2 w)
    {
        if (Raid)
        {
            RaidMap map = RaidMap.Instance;
            return map == null || map.IsBlocked(map.WorldToCell(w));
        }
        GridNavigation nav = GridNavigation.Instance;
        return nav == null || nav.IsBlocked(nav.WorldToCell(w));
    }

    // Износ на ходу: доля прочности за пройденные клетки
    void Wear(float cells, string breakText)
    {
        if (cells <= 0f || dead) return;
        Hp -= MaxHp * WearPerCell * cells;
        if (Hp <= 0f) Break(breakText);
    }

    /// <summary>
    /// Сдвинуть заслон на колёсах чужой силой (отбойник, чёрная дыра, пружина, удар тяжёлого врага).
    /// Едет, пока не упрётся (стена, здание, вода, другое укрепление); упёрся - удар, лишний износ.
    /// Другие виды укреплений не сдвигаются.
    /// </summary>
    public void Shove(Vector2 delta)
    {
        if (dead || !S.wheeled || !Built) return;
        float len = delta.magnitude;
        if (len < 0.02f) return;
        Vector2 dir = delta / len;
        float moved = MoveBy(dir, len, false, null);
        Wear(moved, $"{S.name} разбило");
        if (dead) return;
        if (moved < len - 0.05f)
        {
            CombatFx.Flash(Position + dir * 0.4f);
            Hp -= MaxHp * CrashWear;
            if (Hp <= 0f) { Break($"{S.name} разбило о препятствие"); return; }
        }
        if (moved > 0.05f)
        {
            spin += moved * 360f;
            for (int i = 0; i < 3; i++) CombatFx.Spark(Position - dir * 0.3f + Random.insideUnitCircle * 0.4f, DustColor, 0.16f, 0.35f);
        }
        if (attached) attached = false;   // вырвало из рук - боец подойдёт снова
    }

    /// <summary>Оттолкнуть все заслоны на колёсах рядом с точкой at (в радиусе radius) в сторону от from.</summary>
    public static void ShoveFrom(Vector2 from, Vector2 at, float radius, float distance, bool raid)
    {
        if (distance <= 0f) return;
        foreach (var p in new List<FieldPost>(All))
        {
            if (p == null || p.dead || !p.S.wheeled || p.Raid != raid || p.DistanceTo(at) > radius) continue;
            Vector2 d = p.Position - from;
            if (d.sqrMagnitude < 0.0001f) d = p.Position - at;
            if (d.sqrMagnitude < 0.0001f) d = Random.insideUnitCircle;
            float k = 1f - Mathf.Clamp01(p.DistanceTo(at) / Mathf.Max(0.1f, radius)) * 0.5f;   // у края - слабее
            p.Shove(d.normalized * distance * k);
        }
    }

    /// <summary>Подтянуть заслоны на колёсах в радиусе radius к точке center на step клеток (чёрная дыра).</summary>
    public static void PullTo(Vector2 center, float radius, float step, bool raid)
    {
        if (step <= 0f) return;
        foreach (var p in new List<FieldPost>(All))
        {
            if (p == null || p.dead || !p.S.wheeled || p.Raid != raid) continue;
            Vector2 d = center - p.Position;
            float dist = d.magnitude;
            if (dist > radius + p.S.length * 0.5f || dist < 0.5f) continue;
            p.Shove(d / dist * Mathf.Min(step, dist - 0.4f));
        }
    }

    // Сдвинуть по dir на dist (шагами): клетки, куда въезжает стенка, должны быть свободны.
    // checkUnits - не наезжать на людей (когда катит боец); ignore - сам толкающий.
    float MoveBy(Vector2 dir, float dist, bool checkUnits, Transform ignore)
    {
        float done = 0f;
        while (done < dist - 0.001f)
        {
            float st = Mathf.Min(0.2f, dist - done);
            Vector2 np = Position + dir * st;
            List<Vector2Int> nc = CellsFor(S, np, Angle, Raid);
            if (!CellsFree(nc, checkUnits, ignore)) break;
            transform.position = new Vector3(np.x, np.y, 0f);
            if (!SameCells(nc, Cells)) { Cells = nc; NavDirty(); }
            done += st;
        }
        if (done > 0f) spin += done * 220f;
        return done;
    }

    static bool SameCells(List<Vector2Int> a, List<Vector2Int> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        foreach (var c in a) if (!b.Contains(c)) return false;
        return true;
    }

    bool CellsFree(List<Vector2Int> cells, bool checkUnits, Transform ignore)
    {
        var fresh = new List<Vector2Int>();
        foreach (var c in cells) if (Cells == null || !Cells.Contains(c)) fresh.Add(c);
        if (fresh.Count == 0) return true;

        if (Raid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null) return false;
            foreach (var c in fresh) if (!map.InBounds(c) || map.IsBlocked(c)) return false;
            if (!checkUnits) return true;
            foreach (var u in RaidUnit.All)
            {
                if (u == null || !u.Alive || u.transform == ignore) continue;
                if (fresh.Contains(map.WorldToCell(u.Position))) return false;
            }
            return true;
        }

        GridNavigation nav = GridNavigation.Instance;
        if (nav == null) return false;
        foreach (var c in fresh) if (!nav.InBounds(c) || nav.IsBlocked(c)) return false;
        if (!checkUnits) return true;
        foreach (var en in EnemyAgent.All)
            if (EnemyAgent.Alive(en) && fresh.Contains(nav.WorldToCell(en.Position))) return false;
        foreach (var ca in CitizenAgent.All)
        {
            if (ca == null || ca.IsDead || ca.IsInsideBuilding || ca.transform == ignore) continue;
            if (fresh.Contains(nav.WorldToCell(ca.transform.position))) return false;
        }
        return true;
    }

    // Колёса: приказ, поиск бойца, вид на ходу
    void WheelTick(float dt)
    {
        if (hasMoveOrder)
        {
            // Боец пропал (погиб, ушёл, взялся за другое) - ждём следующего
            if (homePusher != null && (homePusher.IsDead || homePusher.pushingPost != this)) DropPusher();
            if (raidPusher != null && (!raidPusher.Alive || raidPusher.pushingPost != this)) DropPusher();

            if (HasPusher) lastPusherAt = Time.time;
            else
            {
                if (Time.time >= nextAssign) TryAssignPusher(false);
                if (!HasPusher && Time.time - lastPusherAt > NoPusherTimeout)
                {
                    StopMove($"{S.name}: так никто и не покатил — приказ снят");
                    return;
                }
            }

            // Пыль из-под колёс
            if (Moving)
            {
                dustTimer -= dt;
                if (dustTimer <= 0f)
                {
                    dustTimer = 0.25f;
                    Vector2 back = (Position - moveTarget).normalized;
                    CombatFx.Spark(Position + back * 0.5f + Random.insideUnitCircle * 0.3f, DustColor, 0.14f, 0.4f);
                }
            }
            UpdateMoveMark();
        }

        // Спицы крутятся, пока колёса катятся
        if (spokes.Count > 0 && spin > 0f)
        {
            float step = Mathf.Min(spin, 720f * dt);
            spin -= step;
            foreach (var s in spokes) if (s != null) s.localRotation *= Quaternion.Euler(0f, 0f, -step);
        }
    }

    void ShowMoveMark()
    {
        HideMoveMark();
        moveMark = new GameObject("FieldPostMoveMark");
        moveMark.transform.position = new Vector3(moveTarget.x, moveTarget.y, 0f);
        SpriteRenderer ring = moveMark.AddComponent<SpriteRenderer>();
        ring.sprite = CombatFx.Circle();
        ring.color = new Color(1f, 0.82f, 0.35f, 0.35f);
        ring.sortingOrder = -20;
        moveMark.transform.localScale = new Vector3(0.9f, 0.9f, 1f);

        GameObject dot = new GameObject("Dot");
        dot.transform.SetParent(moveMark.transform, false);
        dot.transform.localScale = new Vector3(0.3f, 0.3f, 1f);
        SpriteRenderer d = dot.AddComponent<SpriteRenderer>();
        d.sprite = CombatFx.Circle();
        d.color = new Color(1f, 0.85f, 0.4f, 0.9f);
        d.sortingOrder = -19;
    }

    void UpdateMoveMark()
    {
        if (moveMark == null) return;
        float k = 0.8f + 0.15f * Mathf.Sin(Time.time * 5f);
        moveMark.transform.localScale = new Vector3(k, k, 1f);
    }

    void HideMoveMark()
    {
        if (moveMark != null) Destroy(moveMark);
        moveMark = null;
    }
}
