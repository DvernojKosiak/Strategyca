using UnityEngine;
using System.Collections.Generic;

/// <summary>Роль бойца в войске соседа, которое пришло к нам.</summary>
public enum EnemyRole { Vanguard, Archer, Healer, Rider, Spy }

/// <summary>
/// Войско соседа-соперника на нашей карте. Командир играет за соседа "как человек", а не "все бегут к ближайшему":
///  1. Сбор: войско выходит с края карты и собирается в точке сбора - подальше от наших башен.
///  2. Лагерь: ставят лагерь с частоколом. Он лечит их раненых и укрывает от стрел. Пока все не подтянутся -
///     ждут. Лагерь можно сжечь: перебить охрану и подвести к нему бойца.
///  3. Цель: командир сам выбирает, что громить - что ценнее и хуже защищено (по нраву: воинственные идут
///     на казармы и Ратушу, хитрые - грабят окраины подальше от башен, осторожные - что поближе).
///  4. Наступление строем: пехота впереди (отряд идёт со скоростью самого медленного), стрелки в паре клеток
///     за ней, лекари сзади. Встретили наших бойцов - останавливаются и дерутся, за одиночками не гоняются.
///     Стена мешает - пробивают её всем отрядом в одном месте.
///  5. Конница не лезет в свалку: ловит жителей, наших стрелков, а если пехоте тяжело - бьёт во фланг.
///  5б. Грабёж: у цели, если наших бойцов рядом нет, войско расходится по ценным зданиям вокруг (Ратуша,
///     Хранилище, рынок, производство с запасами), выносит добро, пока мешки не полны (у каждого бойца свой
///     предел, с одного здания - не больше доли от всех мешков), часть пехоты громит, уходя - могут поджечь.
///     Потом - следующая цель. Нрав: воинственные громят и жгут больше, хитрые не громят и уходят быстро.
///  6. Раненые уходят в лагерь лечиться. Большие потери, полные мешки у большинства носильщиков, время
///     или сделали своё дело (несколько целей) - отход всем войском. Вернувшиеся бойцы и добыча уходят к соседу (добыча ляжет в его Ратушу - её можно отбить).
/// Лазутчик (IsSpyMission): один, не дерётся, обходит город и уходит с вестями. Убили - сосед ничего не узнал.
/// Запускает RivalManager.
/// </summary>
public class RivalAssault : MonoBehaviour
{
    public static readonly List<RivalAssault> All = new List<RivalAssault>();

    // ---------- Числа (можно менять) ----------
    const float RallyInset = 6f;           // точка сбора - столько клеток от края
    const float GatherTimeout = 35f;
    const float CampWaitMin = 10f, CampWaitMax = 25f;
    const float AssaultTimeLimit = 300f;   // дольше этого не воюют - уходят
    const float WoundedBelow = 0.3f, HealedAbove = 0.75f;

    public static readonly UnitType SpyType = new UnitType
    {
        unitName = "Лазутчик", maxHp = 60f, damage = 3f, attackRange = 1.1f, attackCooldown = 1.2f, moveSpeed = 2.5f,
        color = new Color(0.22f, 0.22f, 0.28f), bodyScale = 0.42f
    };

    public RaidTarget Rival { get; private set; }
    public string RivalName => Rival != null ? Rival.Name : "соседа";
    public bool IsSpyMission { get; private set; }
    public bool Retreating => stage == Stage.Retreat;
    public string Status => status;
    public int Side => side;

    enum Stage { Approach, Camp, Assault, Retreat }
    Stage stage;

    readonly List<EnemyAgent> units = new List<EnemyAgent>();
    readonly Dictionary<EnemyAgent, string> unitNames = new Dictionary<EnemyAgent, string>();
    readonly HashSet<EnemyAgent> reported = new HashSet<EnemyAgent>();
    readonly HashSet<EnemyAgent> fallback = new HashSet<EnemyAgent>();
    readonly HashSet<BuildingInstance> skip = new HashSet<BuildingInstance>();
    int total;
    float startHp;

    int side;
    Vector2 entry, rally, anchor, dir = Vector2.right;
    EnemyCamp camp;
    BuildingInstance objective, breachWall;
    int objectivesDone, maxObjectives;
    readonly List<Vector2> route = new List<Vector2>();
    int routeIdx;
    bool atObjective;
    float stageTimer, thinkTimer, assaultTime, retreatAt, bandSpeed = 1.4f;
    int lost;
    readonly List<string> escaped = new List<string>();
    readonly Loot brought = new Loot();
    string status = "собирается";

    // ---------- Грабёж, когда никто не мешает (по нраву соседа, задаётся в Launch) ----------
    // Отряд у цели и наших бойцов рядом нет: кто с пустым мешком - расходится по ценным зданиям рядом
    // (Ратуша, Хранилище, рынок, производство с запасами), выносит добро, пока мешок не полон;
    // часть пехоты громит здание; уходя от цели - могут поджечь. Жителей на улице бьют (они сами прячутся).
    float fullShare = 0.7f;        // отход, когда полны мешки у такой доли носильщиков
    float timeLimit = AssaultTimeLimit;
    float wreckShare = 0.25f;      // доля пехоты, которая громит цель, пока другие грабят
    float wreckTime = 12f;         // сколько секунд громят уже выграбленную цель, прежде чем идти дальше
    float torchChance = 0.25f;     // шанс поджечь цель, уходя от неё
    float pileShare = 0.35f;       // с одного здания уносят не больше этой доли от всех мешков войска
    float spreadRadius = 10f;      // как далеко от отряда расходятся грабить
    bool fullWreck = true;         // с полным мешком - громят (иначе стерегут добычу у отряда)

    const float PlunderReach = 1.7f;       // столько клеток от здания - уже грабит
    const float PlunderPerTick = 4f;       // штук за раз (раз в 0.5 с) на бойца
    const float SpotsRefresh = 3f;         // кэш целей для грабежа обновляется раз в столько секунд
    const float TowersRefresh = 3f;
    const float ObjectiveStuck = 45f;      // столько стоят у цели без толку - идут дальше

    sealed class LootSpot { public BuildingInstance b; public float est; }
    readonly List<LootSpot> spots = new List<LootSpot>();
    float spotsTimer, towersTimer = -1f, objectiveTime;
    List<TowerSpot> towerCache;
    readonly Dictionary<BuildingInstance, Loot> piles = new Dictionary<BuildingInstance, Loot>();
    readonly Dictionary<BuildingInstance, Vector2> doors = new Dictionary<BuildingInstance, Vector2>();
    readonly HashSet<BuildingInstance> emptied = new HashSet<BuildingInstance>();
    readonly HashSet<BuildingInstance> noPlunder = new HashSet<BuildingInstance>();
    Dictionary<EnemyAgent, BuildingInstance> plunderAt = new Dictionary<EnemyAgent, BuildingInstance>();
    Dictionary<EnemyAgent, BuildingInstance> plunderPrev = new Dictionary<EnemyAgent, BuildingInstance>();
    readonly Dictionary<BuildingInstance, int> plunderCount = new Dictionary<BuildingInstance, int>();
    int capacityTotal;

    // лазутчик
    readonly List<Vector2> spyPoints = new List<Vector2>();
    int spyIdx;
    bool spySeen;

    // ---------- Запуск ----------

    public static RivalAssault Launch(RaidTarget t, Dictionary<string, int> force, int side, bool spy)
    {
        var nav = GridNavigation.Instance;
        if (nav == null || t == null) return null;

        GameObject go = new GameObject((spy ? "RivalSpy_" : "RivalAssault_") + t.Name);
        RivalAssault a = go.AddComponent<RivalAssault>();
        a.Rival = t;
        a.IsSpyMission = spy;
        a.side = side;

        float half = nav.mapSize / 2f;
        float along = Random.Range(-half * 0.5f, half * 0.5f);
        a.entry = FreePoint(nav, EdgeAt(side, along, nav, 0.5f));
        a.rally = spy ? a.entry : a.PickRally(nav, along);
        a.anchor = a.rally;

        switch (t.Trait)
        {
            case RivalTrait.Warlike:
                a.retreatAt = 0.3f; a.maxObjectives = 6; a.fullShare = 0.8f; a.timeLimit = 300f;
                a.wreckShare = 0.5f; a.wreckTime = 25f; a.torchChance = 0.6f; a.pileShare = 0.35f; a.spreadRadius = 10f; a.fullWreck = true;
                break;
            case RivalTrait.Cunning:   // окраины, быстро и без лишнего шума: не громят, но уходя - поджигают
                a.retreatAt = 0.55f; a.maxObjectives = 3; a.fullShare = 0.5f; a.timeLimit = 160f;
                a.wreckShare = 0f; a.wreckTime = 0f; a.torchChance = 0.35f; a.pileShare = 0.45f; a.spreadRadius = 8f; a.fullWreck = false;
                break;
            case RivalTrait.Cautious:
                a.retreatAt = 0.5f; a.maxObjectives = 3; a.fullShare = 0.6f; a.timeLimit = 220f;
                a.wreckShare = 0.15f; a.wreckTime = 6f; a.torchChance = 0.1f; a.pileShare = 0.4f; a.spreadRadius = 7f; a.fullWreck = false;
                break;
            default:
                a.retreatAt = 0.45f; a.maxObjectives = 5; a.fullShare = 0.7f; a.timeLimit = 260f;
                a.wreckShare = 0.25f; a.wreckTime = 12f; a.torchChance = 0.25f; a.pileShare = 0.35f; a.spreadRadius = 10f; a.fullWreck = true;
                break;
        }

        int wave = t.Level * 2 + t.AttacksOnPlayer;
        int i = 0;
        foreach (var kv in force)
        {
            UnitType u = spy ? SpyType : BotVillage.FindUnit(kv.Key);
            if (u == null) continue;
            for (int n = 0; n < kv.Value; n++)
            {
                Vector2 off = Along(side) * ((i % 7) - 3) * 0.8f - Inward(side) * (i / 7) * 0.8f;
                Vector2 pos = FreePoint(nav, a.entry + off);
                EnemyAgent e = EnemyAgent.Spawn(u, pos, wave);
                e.Army = a;
                e.IsSpy = spy;
                e.Role = spy ? EnemyRole.Spy : RoleFor(u);
                a.units.Add(e);
                a.unitNames[e] = kv.Key;
                a.startHp += e.MaxHp;
                i++;
            }
        }
        a.total = a.units.Count;
        foreach (var e in a.units) if (e.Role != EnemyRole.Healer && e.Role != EnemyRole.Spy) a.capacityTotal += e.FreeCapacity;

        float slow = 99f;
        foreach (var e in a.units) if (e.Role == EnemyRole.Vanguard || e.Role == EnemyRole.Archer) slow = Mathf.Min(slow, e.Unit.moveSpeed * 0.9f);
        a.bandSpeed = slow < 90f ? slow * 0.95f : 1.4f;

        if (spy)
        {
            a.MakeSpyRoute(nav);
            a.stage = Stage.Assault;
            a.status = "высматривает";
        }
        else a.stage = Stage.Approach;
        return a;
    }

    static EnemyRole RoleFor(UnitType u)
    {
        if (u.IsHealer) return EnemyRole.Healer;
        if (u.requiresHorse) return EnemyRole.Rider;
        if (u.attackRange > 2f) return EnemyRole.Archer;
        return EnemyRole.Vanguard;
    }

    public string RoleName(EnemyRole r)
    {
        switch (r)
        {
            case EnemyRole.Archer: return "стрелки";
            case EnemyRole.Healer: return "лекарь";
            case EnemyRole.Rider: return "конница";
            case EnemyRole.Spy: return "лазутчик";
            default: return "пехота";
        }
    }

    public string OrderText(EnemyAgent e)
    {
        if (fallback.Contains(e)) return "<color=#80FFD0>отходит в лагерь лечиться</color>";
        switch (stage)
        {
            case Stage.Approach: return "идёт к месту сбора";
            case Stage.Camp: return "ждёт в лагере";
            case Stage.Retreat: return "отступает";
            default:
                BuildingInstance pb;
                if (plunderAt.TryGetValue(e, out pb) && pb != null && pb.type != null) return "<color=#FFB060>грабит: " + pb.type.name + "</color>";
                if (e.Role == EnemyRole.Rider) return "конница рыщет по окраинам";
                if (breachWall != null) return "пробивает стену отрядом";
                if (objective != null) return (atObjective ? "штурмует: " : "наступает на: ") + objective.type.name;
                return "наступает";
        }
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Геометрия ----------

    static Vector2 EdgeAt(int side, float along, GridNavigation nav, float inset)
    {
        float h = nav.mapSize / 2f - 0.5f - inset;
        switch (side)
        {
            case 0: return new Vector2(-h, along);
            case 1: return new Vector2(h, along);
            case 2: return new Vector2(along, -h);
            default: return new Vector2(along, h);
        }
    }

    static Vector2 Inward(int side)
    {
        switch (side)
        {
            case 0: return Vector2.right;
            case 1: return Vector2.left;
            case 2: return Vector2.up;
            default: return Vector2.down;
        }
    }

    static Vector2 Along(int side) { return side < 2 ? Vector2.up : Vector2.right; }

    static Vector2 FreePoint(GridNavigation nav, Vector2 p)
    {
        Vector2Int c = nav.WorldToCell(p);
        int h = nav.mapSize / 2;
        c.x = Mathf.Clamp(c.x, -h, h - 1);
        c.y = Mathf.Clamp(c.y, -h, h - 1);
        if (nav.IsBlocked(c)) c = nav.NearestFree(c);
        return nav.CellCenter(c);
    }

    struct TowerSpot { public Vector2 pos; public float range; }

    static List<TowerSpot> Towers()
    {
        var list = new List<TowerSpot>();
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.IsRuined || b.type == null) continue;
            TowerController tc = b.GetComponent<TowerController>();
            if (tc != null) { list.Add(new TowerSpot { pos = b.Center, range = tc.Range }); continue; }
            if (b.GetComponent<MagicCannonController>() != null) list.Add(new TowerSpot { pos = b.Center, range = 8f });
            GroundCannonController gc = b.GetComponent<GroundCannonController>();
            if (gc != null) list.Add(new TowerSpot { pos = b.Center, range = gc.Range });
        }
        return list;
    }

    static int TowersCovering(List<TowerSpot> towers, Vector2 p, float extra)
    {
        int n = 0;
        foreach (var t in towers) if (Vector2.Distance(t.pos, p) <= t.range + extra) n++;
        return n;
    }

    // Точка сбора: у своего края, вне досягаемости башен
    Vector2 PickRally(GridNavigation nav, float along)
    {
        var towers = Towers();
        Vector2 best = FreePoint(nav, entry + Inward(side) * RallyInset);
        float bestScore = float.MaxValue;
        for (int k = -3; k <= 3; k++)
        {
            Vector2 p = EdgeAt(side, along + k * 4f, nav, RallyInset);
            Vector2 f = FreePoint(nav, p);
            if (Vector2.Distance(f, p) > 3f) continue;
            float score = TowersCovering(towers, f, 2f) * 10f + Mathf.Abs(k) * 0.6f;
            if (score < bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    // ---------- Главный цикл ----------

    void Update()
    {
        stageTimer += Time.deltaTime;
        if (stage == Stage.Assault) assaultTime += Time.deltaTime;
        thinkTimer -= Time.deltaTime;
        if (thinkTimer > 0f) return;
        thinkTimer = 0.5f;

        // Пропавшие без вести (тестовая очистка карты) - считаем, что ушли домой
        for (int i = units.Count - 1; i >= 0; i--)
        {
            EnemyAgent e = units[i];
            if (EnemyAgent.Alive(e)) continue;
            if (!reported.Contains(e) && unitNames.TryGetValue(e, out string n) && Rival != null) Rival.SetAway(n, -1);
            units.RemoveAt(i);
        }
        if (units.Count == 0) { End(); return; }

        if (IsSpyMission) { SpyTick(); return; }

        switch (stage)
        {
            case Stage.Approach: ApproachTick(); break;
            case Stage.Camp: CampTick(); break;
            case Stage.Assault: AssaultTick(); break;
            case Stage.Retreat: RetreatTick(); break;
        }
    }

    static Vector2 Slot(Vector2 perp, Vector2 back, int i)
    {
        int col = i % 5, row = i / 5;
        return perp * ((col - 2) * 0.9f) + back * (row * 0.9f);
    }

    void ApproachTick()
    {
        Vector2 back = -Inward(side), perp = Along(side);
        int near = 0, i = 0;
        foreach (var e in units)
        {
            e.CmdMove(rally + Slot(perp, back, i++), 4f, 0.6f);
            if (Vector2.Distance(e.Position, rally) < 3.5f) near++;
        }
        status = "собирается у края карты";
        if (near >= units.Count * 0.8f || stageTimer > GatherTimeout)
        {
            stage = Stage.Camp;
            stageTimer = 0f;
            camp = EnemyCamp.Create(rally, this);
            WarManager.Notify($"Войско {RivalName} ставит лагерь у {RivalManager.SideName(side)} края — там они лечатся и прячутся от стрел", false);
        }
    }

    void CampTick()
    {
        int near = 0, i = 0;
        foreach (var e in units)
        {
            float a = i++ * 0.9f;
            e.CmdMove(rally + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 1.8f, 5f, 0.5f);
            if (Vector2.Distance(e.Position, rally) < 3.5f) near++;
        }
        status = "ждёт в лагере";
        bool campOk = camp != null && camp.Alive;
        bool ready = (!campOk || camp.Built) && stageTimer >= CampWaitMin && (near >= units.Count || stageTimer > CampWaitMax);
        if (!campOk && stageTimer > 3f) ready = true;
        if (!ready) return;

        stage = Stage.Assault;
        stageTimer = 0f;
        if (!NextObjective(rally)) { Retreat("не нашли, что громить"); return; }
        WarManager.Notify($"Войско {RivalName} выступило из лагеря — идут на «{objective.type.name}»", false);
    }

    // ---------- Наступление ----------

    bool NextObjective(Vector2 from)
    {
        breachWall = null;
        atObjective = false;
        objectiveTime = 0f;
        spotsTimer = 0f;   // у новой цели - свежий список соседних зданий
        objective = ChooseObjective(from);
        if (objective == null) return false;
        BuildRoute(from, objective);
        return true;
    }

    float Risk
    {
        get
        {
            switch (Rival != null ? Rival.Trait : RivalTrait.Calculating)
            {
                case RivalTrait.Warlike: return 0.5f;
                case RivalTrait.Cunning: return 1.6f;
                case RivalTrait.Cautious: return 1.3f;
                default: return 1f;
            }
        }
    }

    float BaseValue(BuildingInstance b)
    {
        string n = b.type.name;
        RivalTrait tr = Rival != null ? Rival.Trait : RivalTrait.Calculating;
        float v;
        if (n == "Ратуша") v = 9f;
        else if (n == WarehouseController.TypeName) v = 6f;
        else if (n == MarketHouse.TypeName) v = 5f;
        else if (n == BarracksController.TypeName) v = tr == RivalTrait.Warlike ? 8f : 3f;
        else if (n == "Ферма" || n == "Шахта") v = 3.5f;
        else if (n == "Лесопилка" || n == WagonYard.TypeName) v = 3f;
        else if (n == KitchenController.TypeName) v = 2.5f;
        else if (n == "Жилой дом") v = 1.5f;
        else v = 2f;
        if (b.GetComponent<TowerController>() != null || b.GetComponent<MagicCannonController>() != null || b.GetComponent<GroundCannonController>() != null)
            v = tr == RivalTrait.Warlike ? 4f : 1f;
        return v;
    }

    BuildingInstance ChooseObjective(Vector2 from)
    {
        var towers = CachedTowers();
        var pm = PopulationManager.Instance;
        Vector2 hallPos = pm != null && pm.Hall != null ? pm.Hall.Center : Vector2.zero;
        bool cunning = Rival != null && Rival.Trait == RivalTrait.Cunning;

        BuildingInstance best = null;
        float bestScore = float.MinValue;
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null || !b.isActiveAndEnabled || !b.IsDamageable || b.IsRuined) continue;
            if (b.IsBarrier || b.IsBridge || skip.Contains(b) || emptied.Contains(b)) continue;
            float value = BaseValue(b) + EstLoot(b) / 60f;   // что можно унести (у Ратуши - казна)
            float danger = TowersCovering(towers, b.Center, 1f) * 2.5f + SoldierPower(b.Center, 7f) * 0.8f;
            float dist = Vector2.Distance(from, b.Center);
            float score = value - danger * Risk - dist * 0.12f;
            if (cunning) score += Vector2.Distance(b.Center, hallPos) * 0.12f;   // хитрые грабят окраины
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    static float SoldierPower(Vector2 p, float r)
    {
        float s = 0f;
        foreach (var c in CitizenAgent.All)
        {
            if (!CitizenAgent.IsTargetable(c) || !c.IsSoldier) continue;
            if (Vector2.Distance(p, c.transform.position) > r) continue;
            s += BotVillage.UnitPower(c.ExpUnit) * Mathf.Max(0.3f, c.HpFraction);
        }
        return s;
    }

    static int SoldiersNear(Vector2 p, float r)
    {
        int n = 0;
        foreach (var c in CitizenAgent.All)
            if (CitizenAgent.IsTargetable(c) && c.IsSoldier && Vector2.Distance(p, c.transform.position) <= r) n++;
        return n;
    }

    // Путь отряда: по сетке, как ходят враги (закрытые для них ворота - стена, реку можно вброд)
    void BuildRoute(Vector2 from, BuildingInstance b)
    {
        route.Clear();
        routeIdx = 0;
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        bool h = GridNavigation.HostileQuery, w = GridNavigation.WaderQuery;
        GridNavigation.HostileQuery = true;
        GridNavigation.WaderQuery = true;
        try
        {
            Vector2Int s = nav.WorldToCell(from);
            if (nav.IsBlocked(s)) s = nav.NearestFree(s);
            if (nav.TryGetDoorCell(b, s, out Vector2Int door))
            {
                List<Vector2Int> path = nav.FindPath(s, door);
                if (path != null) foreach (var c in path) route.Add(nav.CellCenter(c));
            }
        }
        finally
        {
            GridNavigation.HostileQuery = h;
            GridNavigation.WaderQuery = w;
        }

        if (route.Count == 0)
        {
            // Не пройти - пробиваем стену в одном месте: там, где короче всего до цели
            breachWall = ChooseBreach(from, b.Center);
            if (breachWall == null) { skip.Add(b); return; }
            route.Add(breachWall.Center);
        }
    }

    static BuildingInstance ChooseBreach(Vector2 from, Vector2 target)
    {
        BuildingInstance best = null;
        float bestScore = float.MaxValue;
        foreach (var w in BuildingInstance.All)
        {
            if (w == null || !w.IsBarrier || w.IsRuined || !w.isActiveAndEnabled) continue;
            float score = Vector2.Distance(from, w.Center) + Vector2.Distance(w.Center, target) * 0.8f;
            if (w.IsGate) score += 1.5f;   // ворота крепче
            if (score < bestScore) { bestScore = score; best = w; }
        }
        return best;
    }

    /// <summary>Боец не может подойти к цели (стена): командир назначает пролом для всего отряда.</summary>
    public void OnBlocked(EnemyAgent e, BuildingInstance b)
    {
        if (stage != Stage.Assault || IsSpyMission) return;
        if (breachWall == null || breachWall.IsRuined)
        {
            breachWall = ChooseBreach(e.Position, b != null ? b.Center : anchor);
            if (breachWall == null) { if (b != null) skip.Add(b); objective = null; return; }
            WarManager.Notify($"Войско {RivalName} пробивает стену, чтобы пройти дальше", false);
        }
        e.CmdBuilding(breachWall, 6f);
    }

    public void OnBlockedMove(EnemyAgent e)
    {
        if (stage != Stage.Assault || IsSpyMission) return;
        // Шёл грабить соседнее здание и не дошёл - это здание бросаем, стену ради него не ломаем
        BuildingInstance pb;
        if (plunderAt.TryGetValue(e, out pb) && pb != null)
        {
            noPlunder.Add(pb);
            plunderAt.Remove(e);
            return;
        }
        OnBlocked(e, objective);
    }

    void AssaultTick()
    {
        // Потери, мешки, время
        float hp = 0f;
        int carriers = 0, full = 0;
        foreach (var e in units)
        {
            hp += e.Hp;
            if (e.Role == EnemyRole.Healer || e.Role == EnemyRole.Spy) continue;
            carriers++;
            if (e.IsFull) full++;
        }
        float th = retreatAt + (camp == null || !camp.Alive ? 0.1f : 0f);
        if (hp < startHp * th) { Retreat("несёт большие потери"); return; }
        // Уходят, когда полны мешки у большинства носильщиков (а не когда всё войско вместе набрало "сколько-то")
        if (carriers > 0 && full >= Mathf.Max(1, Mathf.CeilToInt(carriers * fullShare))) { Retreat("набили мешки"); return; }
        if (assaultTime > timeLimit) { Retreat("устали воевать"); return; }

        // Цель
        if (breachWall != null && (breachWall.IsRuined || !breachWall.isActiveAndEnabled))
        {
            breachWall = null;
            if (objective != null) BuildRoute(anchor, objective);
        }
        bool finished = objective != null && !objective.IsRuined && ObjectiveFinished();
        if (objective == null || objective.IsRuined || !objective.isActiveAndEnabled || finished)
        {
            if (objective != null && (objective.IsRuined || finished))
            {
                objectivesDone++;
                skip.Add(objective);
                if (finished) MaybeTorch(objective);
            }
            if (objectivesDone >= maxObjectives) { Retreat("сделали своё дело"); return; }
            if (!NextObjective(anchor)) { Retreat("громить больше нечего"); return; }
        }

        // Раненые - в лагерь лечиться
        bool campOk = camp != null && camp.Alive;
        fallback.RemoveWhere(u => !EnemyAgent.Alive(u) || u.HpFraction >= HealedAbove || !campOk);
        if (campOk)
            foreach (var e in units)
                if (e.HpFraction < WoundedBelow && e.Role != EnemyRole.Healer) fallback.Add(e);

        // Где отряд и куда идёт
        BuildingInstance target = breachWall != null ? breachWall : objective;
        int foes = SoldiersNear(anchor, 6f);
        bool engaged = foes > 0;
        Vector2 vc = Vector2.zero;
        int vn = 0;
        foreach (var e in units) if (e.Role == EnemyRole.Vanguard && !fallback.Contains(e)) { vc += e.Position; vn++; }
        bool behind = vn > 0 && Vector2.Distance(vc / vn, anchor) > 4f;   // пехота отстала - ждём

        if (!engaged && !behind && routeIdx < route.Count)
        {
            float step = bandSpeed * 0.85f * 0.5f;
            while (step > 0f && routeIdx < route.Count)
            {
                Vector2 next = route[routeIdx];
                float d = Vector2.Distance(anchor, next);
                if (d <= step) { anchor = next; step -= d; routeIdx++; }
                else { anchor = Vector2.MoveTowards(anchor, next, step); step = 0f; }
            }
        }
        Vector2 aim = routeIdx < route.Count ? route[routeIdx] : target.Center;
        if ((aim - anchor).sqrMagnitude > 0.01f) dir = (aim - anchor).normalized;
        atObjective = target != null && (routeIdx >= route.Count || target.DistanceTo(anchor) < 3.5f);

        Vector2 perp = new Vector2(-dir.y, dir.x), back = -dir;
        int vi = 0, ai = 0, hi = 0, ri = 0;
        bool heavy = foes >= 3;

        // Никто не мешает: грабим (и громим - по нраву)
        bool raid = atObjective && !engaged && breachWall == null && objective != null;
        var swap = plunderPrev; plunderPrev = plunderAt; plunderAt = swap;
        plunderAt.Clear();
        plunderCount.Clear();
        int wreckers = 0;
        if (raid)
        {
            objectiveTime += 0.5f;
            spotsTimer -= 0.5f;
            if (spotsTimer <= 0f) { spotsTimer = SpotsRefresh; RefreshSpots(); }
            int vanguards = 0;
            foreach (var e in units) if (e.Role == EnemyRole.Vanguard && !fallback.Contains(e)) vanguards++;
            wreckers = Mathf.RoundToInt(vanguards * wreckShare);
        }
        int plundering = 0;

        foreach (var e in units)
        {
            if (fallback.Contains(e)) { e.CmdMove(camp.Position + Random.insideUnitCircle * 0.8f, 1.5f, 0.8f); continue; }
            if (raid && e.Role != EnemyRole.Healer)
            {
                bool wrecker = e.Role == EnemyRole.Vanguard && vi < wreckers;
                if (e.Role == EnemyRole.Vanguard) vi++;
                if (e.Role == EnemyRole.Rider && RiderHunt(e)) { ri++; continue; }   // конница сперва ловит жителей
                if (!wrecker && !e.IsFull && TryPlunder(e)) { plundering++; continue; }
                if (wrecker || (e.IsFull && fullWreck) || (!e.IsFull && wreckShare > 0f))
                    e.CmdBuilding(target, e.Role == EnemyRole.Archer ? 6.5f : 6f);
                else
                    e.CmdMove(anchor + back * 1.5f + Slot(perp, back, ai++), 6f, 0.6f);   // стерегут добычу у отряда
                continue;
            }
            switch (e.Role)
            {
                case EnemyRole.Vanguard:
                    if (atObjective) e.CmdBuilding(target, 6f);
                    else e.CmdMove(anchor + Slot(perp, back, vi), 6f, 0.6f, bandSpeed);
                    vi++;
                    break;
                case EnemyRole.Archer:
                    if (atObjective && !engaged) e.CmdBuilding(target, 6.5f);
                    else e.CmdMove(anchor + back * 3.2f + Slot(perp, back, ai), 6.5f, 0.6f, bandSpeed);
                    ai++;
                    break;
                case EnemyRole.Healer:
                    e.CmdMove(anchor + back * 2f + perp * ((hi++ % 3) - 1), 3f, 0.6f, bandSpeed);
                    break;
                case EnemyRole.Rider:
                    RiderOrder(e, heavy, perp, ri++);
                    break;
            }
        }

        string tn = target != null && target.type != null ? target.type.name : "?";
        if (raid && plundering > 0) status = "грабит «" + tn + "» и дома вокруг";
        else status = breachWall != null ? "пробивает стену" : (engaged ? "бьётся с вашими бойцами" : (atObjective ? "громит «" + tn + "»" : "наступает на «" + tn + "»"));
    }

    // ---------- Грабёж ----------

    List<TowerSpot> CachedTowers()
    {
        if (towerCache == null || Time.time >= towersTimer)
        {
            towerCache = Towers();
            towersTimer = Time.time + TowersRefresh;
        }
        return towerCache;
    }

    int PileCap()
    {
        return Mathf.Max(80, Mathf.RoundToInt(capacityTotal * pileShare));
    }

    // Сколько примерно можно унести из здания (без выделения памяти)
    float EstLoot(BuildingInstance b)
    {
        if (b == null) return 0f;
        Loot pile;
        if (piles.TryGetValue(b, out pile)) return pile.Total;   // уже вскрыли - осталось столько
        float est = b.StoredTotal;
        if (b.IsTownHall)
        {
            var rm = ResourceManager.Instance;
            if (rm != null)
                est += (Mathf.Max(0, rm.gold) + Mathf.Max(0, rm.wood) + Mathf.Max(0, rm.stone) + Mathf.Max(0, rm.food) + Mathf.Max(0, rm.iron)) * Loot.HallRawShare;
        }
        WarehouseController wh = b.GetComponent<WarehouseController>();
        if (wh != null) est += wh.RefinedTotal * WarehouseController.RaidShare;
        return Mathf.Min(est, PileCap());
    }

    // Вскрыть здание: что из него вынесут за этот набег (не больше PileCap). Остальное остаётся на месте.
    Loot OpenPile(BuildingInstance b)
    {
        Loot pile;
        if (piles.TryGetValue(b, out pile)) return pile;
        int cap = PileCap();

        Loot store = Loot.FromBuildingStorage(b);
        pile = EnemyAgent.TakeUpTo(store, cap);
        b.storedGold = store.gold; b.storedWood = store.wood; b.storedStone = store.stone; b.storedFood = store.food;
        b.storedIron = store.iron; b.storedOats = store.oats; b.storedBerries = store.berries; b.storedHerbs = store.herbs; b.storedResin = store.resin;

        if (b.IsTownHall && pile.Total < cap)
        {
            Loot t = Loot.RaidTreasury();
            pile.Add(EnemyAgent.TakeUpTo(t, cap - pile.Total));
            t.DepositToTown();   // что не берут - остаётся в казне
        }
        WarehouseController wh = b.GetComponent<WarehouseController>();
        if (wh != null && pile.Total < cap)
        {
            Loot w = wh.Raid();
            pile.Add(EnemyAgent.TakeUpTo(w, cap - pile.Total));
            w.DepositToTown();
        }
        piles[b] = pile;
        if (pile.IsEmpty) emptied.Add(b);
        else if (b.type != null) WarManager.Notify($"Войско {RivalName} грабит «{b.type.name}»!", false);
        return pile;
    }

    bool Plunderable(BuildingInstance b)
    {
        if (b == null || b.type == null || !b.isActiveAndEnabled || b.IsRuined) return false;
        if (emptied.Contains(b) || noPlunder.Contains(b)) return false;
        return EstLoot(b) > 0f;
    }

    bool DoorFor(BuildingInstance b, out Vector2 door)
    {
        if (doors.TryGetValue(b, out door)) return true;
        var nav = GridNavigation.Instance;
        if (nav == null) return false;
        bool h = GridNavigation.HostileQuery, w = GridNavigation.WaderQuery;
        GridNavigation.HostileQuery = true;
        GridNavigation.WaderQuery = true;
        try
        {
            Vector2Int s = nav.WorldToCell(anchor);
            if (nav.IsBlocked(s)) s = nav.NearestFree(s);
            if (!nav.TryGetDoorCell(b, s, out Vector2Int c)) { noPlunder.Add(b); return false; }
            door = nav.CellCenter(c);
        }
        finally
        {
            GridNavigation.HostileQuery = h;
            GridNavigation.WaderQuery = w;
        }
        doors[b] = door;
        return true;
    }

    // Цели для грабежа рядом с отрядом (кэш, обновляется раз в SpotsRefresh секунд)
    void RefreshSpots()
    {
        spots.Clear();
        var towers = CachedTowers();
        bool bold = Rival != null && Rival.Trait == RivalTrait.Warlike;
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b == objective || b.type == null || !b.isActiveAndEnabled || b.IsRuined) continue;
            if (b.IsRoad || b.IsBarrier || b.IsBridge || skip.Contains(b) || emptied.Contains(b) || noPlunder.Contains(b)) continue;
            if (b.DistanceTo(anchor) > spreadRadius) continue;
            float est = EstLoot(b);
            if (est <= 0f) continue;
            if (!bold && TowersCovering(towers, b.Center, 0f) > 0) continue;   // под башни грабить не лезут
            spots.Add(new LootSpot { b = b, est = est });
        }
        spots.Sort((x, y) => y.est.CompareTo(x.est));
        if (spots.Count > 8) spots.RemoveRange(8, spots.Count - 8);
    }

    // Боец с пустым мешком идёт грабить: цель отряда или здание рядом (не больше 3-4 бойцов на здание)
    bool TryPlunder(EnemyAgent e)
    {
        BuildingInstance best = null;
        float bestScore = float.MaxValue;
        BuildingInstance prev;
        plunderPrev.TryGetValue(e, out prev);
        for (int k = -1; k < spots.Count; k++)
        {
            BuildingInstance b = k < 0 ? objective : spots[k].b;
            if (!Plunderable(b)) continue;
            int n;
            plunderCount.TryGetValue(b, out n);
            if (n >= (b.IsTownHall || b == objective ? 4 : 3)) continue;
            float score = b.DistanceTo(e.Position) - EstLoot(b) / 50f;
            if (b == objective) score -= 2f;
            if (b == prev) score -= 3f;   // не мечется между зданиями
            if (score < bestScore) { bestScore = score; best = b; }
        }
        if (best == null) return false;

        Vector2 door;
        if (!DoorFor(best, out door)) return false;
        int c;
        plunderCount.TryGetValue(best, out c);
        plunderCount[best] = c + 1;
        plunderAt[e] = best;

        if (best.DistanceTo(e.Position) <= PlunderReach)
        {
            Loot pile = OpenPile(best);
            int take = Mathf.Min(e.FreeCapacity, Mathf.RoundToInt(PlunderPerTick * (Rival != null && Rival.Trait == RivalTrait.Cunning ? 1.5f : 1f)));
            if (take > 0 && !pile.IsEmpty) e.ReceiveLoot(EnemyAgent.TakeUpTo(pile, take), best.Center);
            if (pile.IsEmpty) emptied.Add(best);
        }
        e.CmdMove(door, 5f, 0.4f);   // жителей рядом всё равно бьют (ArmyThink)
        return true;
    }

    // Цель отряда выграблена (и, по нраву, разгромлена) - идём дальше
    bool ObjectiveFinished()
    {
        if (!atObjective) return false;
        if (objectiveTime >= ObjectiveStuck) return true;
        if (Plunderable(objective)) return false;
        if (objectiveTime < wreckTime) return false;
        // Пока грабят соседние здания - не уходим, если там ещё есть что взять
        foreach (var s in spots) if (Plunderable(s.b)) return objectiveTime >= wreckTime + 20f;
        return true;
    }

    void MaybeTorch(BuildingInstance b)
    {
        if (b == null || b.IsRuined || !b.isActiveAndEnabled || BurnEffect.IsBurning(b)) return;
        if (Random.value >= torchChance) return;
        const float dur = 20f;
        float frac = Rival != null && Rival.Trait == RivalTrait.Warlike ? 0.5f : 0.3f;   // какую долю прочности выжжет
        BuildingInstance target = b;
        BurnEffect.Apply(b, b.MaxHp * frac / dur, dur, d => { if (target != null) target.TakeDamage(d); });
        CombatFx.FirePool(b.Center, 1.2f);
        if (b.type != null) WarManager.Notify($"Войско {RivalName} подожгло «{b.type.name}»! Пожар потухнет сам, но здание пострадает", false);
    }

    // Добыча, которую вскрыли, но не унесли, остаётся городу
    void ReturnPiles()
    {
        foreach (var kv in piles) if (kv.Value != null && !kv.Value.IsEmpty) kv.Value.DepositToTown();
        piles.Clear();
    }

    // Конница при грабеже: сначала жители на улице (не под башнями). true - есть кого ловить.
    bool RiderHunt(EnemyAgent e)
    {
        CitizenAgent prey = FindPrey(e);
        if (prey == null) return false;
        e.CmdMove(prey.transform.position, 5f, 0.8f);
        return true;
    }

    CitizenAgent FindPrey(EnemyAgent e)
    {
        var towers = CachedTowers();
        CitizenAgent prey = null;
        float best = float.MaxValue;
        foreach (var c in CitizenAgent.All)
        {
            if (!CitizenAgent.IsTargetable(c)) continue;
            bool soft = !c.IsSoldier || c.ExpRanged;
            if (!soft) continue;
            Vector2 cp = c.transform.position;
            if (c.IsConcealed && Vector2.Distance(cp, e.Position) > c.NoticeRange(14f)) continue;
            float d = Vector2.Distance(cp, anchor);
            if (d > 14f) continue;
            if (TowersCovering(towers, cp, 0f) > 0) continue;   // под башню не лезут
            float score = Vector2.Distance(cp, e.Position);
            if (score < best) { best = score; prey = c; }
        }
        return prey;
    }

    void RiderOrder(EnemyAgent e, bool heavy, Vector2 perp, int i)
    {
        float s = i % 2 == 0 ? 1f : -1f;
        if (heavy) { e.CmdMove(anchor + perp * 3f * s - dir * 0.5f, 7f, 0.6f); return; }   // помочь пехоте - во фланг

        CitizenAgent prey = FindPrey(e);
        if (prey != null) e.CmdMove(prey.transform.position, 5f, 0.8f);
        else e.CmdMove(anchor + perp * 4f * s, 7f, 0.6f);
    }

    // ---------- Отход ----------

    Vector2 ExitFor(Vector2 p)
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return entry;
        float along = side < 2 ? p.y : p.x;
        return FreePoint(nav, EdgeAt(side, along, nav, 0.5f));
    }

    void Retreat(string why)
    {
        if (stage == Stage.Retreat) return;
        stage = Stage.Retreat;
        stageTimer = 0f;
        fallback.Clear();
        plunderAt.Clear();
        ReturnPiles();
        foreach (var e in units) e.CmdLeave(ExitFor(e.Position));
        status = "отступает";
        if (!IsSpyMission) WarManager.Notify($"Войско {RivalName} {why} и отступает!", true);
    }

    /// <summary>Сосед узнал, что мы напали на его деревню - войско спешит домой.</summary>
    public void Recall()
    {
        if (stage == Stage.Retreat) return;
        Retreat("спешит домой защищать деревню");
    }

    void RetreatTick()
    {
        foreach (var e in units) if (!e.IsLeaving) e.CmdLeave(ExitFor(e.Position));
        status = "отступает";
        // Страховка: отход не может длиться вечно (кто-то застрял) - оставшиеся уходят, войско не ждёт их бесконечно
        if (stageTimer > RetreatTimeLimit)
        {
            var left = new List<EnemyAgent>(units);
            foreach (var e in left) if (EnemyAgent.Alive(e)) e.ForceEscape();
        }
    }

    const float RetreatTimeLimit = 150f;

    // ---------- Лазутчик ----------

    void MakeSpyRoute(GridNavigation nav)
    {
        var pm = PopulationManager.Instance;
        var picks = new List<Vector2>();
        if (pm != null && pm.Hall != null) picks.Add(pm.Hall.Center);
        var others = new List<BuildingInstance>();
        foreach (var b in BuildingInstance.All)
            if (b != null && b.type != null && !b.IsRuined && !b.IsRoad && !b.IsBarrier && b.isActiveAndEnabled) others.Add(b);
        foreach (var b in others) if (b.GetComponent<BarracksController>() != null) { picks.Add(b.Center); break; }
        if (others.Count > 0) picks.Add(others[Random.Range(0, others.Count)].Center);

        // Смотрит издали: точка в 4-5 клетках от здания, со стороны прихода
        foreach (var p in picks)
        {
            Vector2 toEntry = (entry - p).normalized;
            spyPoints.Add(FreePoint(nav, p + toEntry * Random.Range(4f, 5.5f)));
        }
        if (spyPoints.Count == 0) spyPoints.Add(FreePoint(nav, Vector2.zero));
    }

    void SpyTick()
    {
        if (units.Count == 0) return;
        EnemyAgent spy = units[0];
        if (!spySeen && FogOfWar.IsVisible(spy.Position))
        {
            spySeen = true;
            WarManager.Notify($"Замечен чужой лазутчик ({RivalName})! Убейте его, пока он не ушёл с вестями", false);
        }
        if (stage == Stage.Retreat) { if (!spy.IsLeaving) spy.CmdLeave(ExitFor(spy.Position)); return; }

        // Наши бойцы рядом или под башней - бежит
        bool hit = Time.time - spy.LastHitAt < 1f;
        bool danger = SoldiersNear(spy.Position, 4f) > 0 || TowersCovering(CachedTowers(), spy.Position, 0f) > 0;
        if (hit || (danger && spyIdx > 0)) { Retreat("лазутчик бежит"); return; }

        if (spyIdx >= spyPoints.Count) { Retreat("лазутчик всё высмотрел"); return; }
        Vector2 p = spyPoints[spyIdx];
        spy.CmdMove(p, 0f, 1.2f);
        if (Vector2.Distance(spy.Position, p) < 1.6f || stageTimer > 40f) { spyIdx++; stageTimer = 0f; }
        status = "высматривает";
    }

    // ---------- События бойцов ----------

    public void OnDied(EnemyAgent e)
    {
        if (!units.Contains(e)) return;
        reported.Add(e);
        units.Remove(e);
        fallback.Remove(e);
        lost++;
        if (unitNames.TryGetValue(e, out string n) && Rival != null) Rival.LoseUnit(n, true);
        if (IsSpyMission)
            WarManager.Notify(spySeen ? $"Лазутчик {RivalName} убит — сосед ничего о вас не узнал" : $"Убит неизвестный лазутчик ({RivalName})", true);
    }

    public void OnEscaped(EnemyAgent e)
    {
        if (!units.Contains(e)) return;
        reported.Add(e);
        units.Remove(e);
        fallback.Remove(e);
        brought.Add(e.Carried);
        if (unitNames.TryGetValue(e, out string n))
        {
            escaped.Add(n);
            if (Rival != null)
            {
                Rival.SetAway(n, -1);
                RivalManager.UnitReturned(Rival, n);
            }
        }
    }

    /// <summary>Частокол лагеря: свои внутри получают меньше урона от стрел и ударов.</summary>
    public float CoverAt(Vector2 p)
    {
        return camp != null && camp.Alive && camp.Built && Vector2.Distance(p, camp.Position) <= EnemyCamp.CoverRadius ? EnemyCamp.CoverMul : 1f;
    }

    public bool Owns(EnemyAgent e) { return units.Contains(e); }

    public void OnCampLost()
    {
        WarManager.Notify($"Лагерь войска {RivalName} сожжён — им негде лечиться!", true);
        fallback.Clear();
    }

    void End()
    {
        ReturnPiles();
        if (camp != null) Destroy(camp.gameObject);
        camp = null;
        RivalManager.AssaultEnded(this, escaped, brought, lost, total, IsSpyMission, IsSpyMission ? spyIdx / (float)Mathf.Max(1, spyPoints.Count) : 1f);
        Destroy(gameObject);
    }
}

/// <summary>
/// Лагерь войска соседа на нашей карте: палатка и частокол. Строится несколько секунд, потом
/// лечит их раненых (кто не в бою) и укрывает тех, кто внутри. Сжечь: перебить охрану (рядом нет их бойцов)
/// и подвести к нему нашего бойца - через несколько секунд лагерь сгорит.
/// В тумане войны не виден.
/// </summary>
public class EnemyCamp : MonoBehaviour
{
    public const float BuildSeconds = 8f;
    public const float HealRadius = 3.5f, HealPerSecond = 5f;
    public const float CoverRadius = 2.2f, CoverMul = 0.65f;
    const float BurnSeconds = 4f, BurnReach = 2.2f, GuardReach = 3.5f;

    RivalAssault army;
    float build, burn;
    bool burnt;
    readonly List<SpriteRenderer> parts = new List<SpriteRenderer>();
    readonly List<Color> colors = new List<Color>();
    SpriteRenderer fire;

    public bool Built => build >= 1f;
    public bool Alive => !burnt;
    public Vector2 Position => transform.position;

    public static EnemyCamp Create(Vector2 pos, RivalAssault army)
    {
        GameObject go = new GameObject("EnemyCamp_" + army.RivalName);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        EnemyCamp c = go.AddComponent<EnemyCamp>();
        c.army = army;
        c.MakeLook();
        return c;
    }

    void MakeLook()
    {
        Color wood = new Color(0.4f, 0.26f, 0.12f), tent = new Color(0.55f, 0.18f, 0.18f);
        Part(new Vector2(0f, -0.1f), new Vector2(0.9f, 0.6f), tent, 4);
        Part(new Vector2(0.4f, 0.4f), new Vector2(0.06f, 0.9f), wood, 5);
        Part(new Vector2(0.58f, 0.72f), new Vector2(0.34f, 0.22f), new Color(0.1f, 0.1f, 0.1f), 6);   // чёрный флаг
        const float h = 1.6f, t = 0.14f;
        // частокол с проходом с одной стороны
        Part(new Vector2(0f, h), new Vector2(2f * h + t, t), wood, 3);
        Part(new Vector2(0f, -h), new Vector2(2f * h + t, t), wood, 3);
        Part(new Vector2(-h, 0.55f), new Vector2(t, h + t), wood, 3);
        Part(new Vector2(-h, -0.55f - 0.5f), new Vector2(t, 0.9f), wood, 3);
        Part(new Vector2(h, 0f), new Vector2(t, 2f * h + t), wood, 3);
        fire = Part(Vector2.zero, new Vector2(1.2f, 1.2f), new Color(1f, 0.45f, 0.1f, 0.0f), 7);
    }

    SpriteRenderer Part(Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject("Part");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.color = c;
        sr.sortingOrder = order;
        parts.Add(sr);
        colors.Add(c);
        return sr;
    }

    void Update()
    {
        if (burnt) return;
        float dt = Time.deltaTime;
        Vector2 p = Position;

        bool guarded = false;
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && army != null && army.Owns(e) && Vector2.Distance(e.Position, p) <= GuardReach) { guarded = true; break; }

        bool ours = false;
        foreach (var c in CitizenAgent.All)
            if (CitizenAgent.IsTargetable(c) && c.IsSoldier && Vector2.Distance(c.transform.position, p) <= BurnReach) { ours = true; break; }

        // Строят, пока рядом нет наших
        if (!Built && !ours) build = Mathf.Min(1f, build + dt / BuildSeconds);

        // Лечит своих
        if (Built && army != null)
            foreach (var e in EnemyAgent.All)
            {
                if (!EnemyAgent.Alive(e) || !army.Owns(e) || e.Hp >= e.MaxHp) continue;
                if (Time.time - e.LastHitAt < 3f || Vector2.Distance(e.Position, p) > HealRadius) continue;
                e.Heal(HealPerSecond * dt);
            }

        // Жгут наши, если охраны нет
        if (ours && !guarded) burn += dt / BurnSeconds;
        else burn = Mathf.Max(0f, burn - dt / BurnSeconds);
        if (burn >= 1f) Burn();

        ApplyLook();
    }

    void Burn()
    {
        burnt = true;
        CombatFx.Death(Position);
        if (army != null) army.OnCampLost();
        for (int i = 0; i < parts.Count; i++)
            if (parts[i] != null && parts[i] != fire) parts[i].color = new Color(0.15f, 0.12f, 0.1f, 0.8f);
        if (fire != null) fire.color = new Color(0f, 0f, 0f, 0f);
        Destroy(gameObject, 6f);
    }

    void ApplyLook()
    {
        float a = Mathf.Lerp(0.35f, 1f, build);
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == null || parts[i] == fire) continue;
            Color c = colors[i];
            parts[i].color = new Color(c.r, c.g, c.b, a);
        }
        if (fire != null) fire.color = new Color(1f, 0.45f, 0.1f, Mathf.Clamp01(burn) * 0.75f);

        bool hide = FogOfWar.Enabled && !FogOfWar.IsVisible(Position);
        foreach (var r in parts) if (r != null) r.forceRenderingOff = hide;
    }
}
