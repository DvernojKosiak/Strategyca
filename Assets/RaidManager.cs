using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Набеги на соседние деревни (пока - боты).
///  1. Кнопка "В бой" справа внизу: выбираем деревню (разведданные: защитники, башни, стены, богатство)
///     и отряды из казарм. Припасы: еда на каждого бойца (запас похода, остаток вернётся домой)
///     и дерево на лагерь (тратится).
///  2. Бойцы идут к краю нашей карты и уходят. Дорога занимает время (зависит от деревни).
///  3. Прибыли: камера переносится на карту набега. Видно чужую деревню - решаем: атаковать или уйти.
///  4. Бой: отряды сами штурмуют (бьют защитников, ломают здания, пробивают стены) или выполняют приказы:
///     ЛКМ по земле - идти и держать точку, ЛКМ по зданию - ломать его. Разрушенное здание отдаёт склад
///     в мешки бойцов рядом (пеший уносит CarryFoot, конный - CarryMounted). Лишнее остаётся на руинах.
///  5. Отступление: бойцы уходят через западный край карты. Кто погиб - погиб и дома, его мешок потерян.
///  6. Дорога назад, бойцы появляются у края нашей карты и сами несут добычу в казарму / Ратушу.
/// Пока армия в походе, деревня остаётся без неё - набеги на нас никто не отменял.
/// Клавиша V - переключить камеру: деревня / поле боя.
/// </summary>
public class RaidManager : MonoBehaviour
{
    public static RaidManager Instance { get; private set; }

    public enum Phase { Idle, Marching, TravelOut, Scouting, Battle, Leaving, TravelHome }

    // ---------- Числа (можно менять) ----------
    public const int FoodPerSoldier = 8;       // еды в запас похода на бойца
    public const int CampWood = 20;            // дерево на лагерь у места высадки
    public const int CarryFoot = 150;          // сколько добычи уносит пеший
    public const int CarryMounted = 300;       // ... и конный
    const float EatInterval = 60f;             // раз в столько секунд каждый боец съедает 1 еду из запаса
    const float HungerPerSecond = 0.4f;        // запас кончился - здоровье тает (не ниже четверти)
    const float CampRadius = 4.5f, CampCover = 1.6f;
    const float CampHealPerSecond = 3f, CampHpPerFood = 10f;
    const float RecoverSeconds = 600f;         // столько деревня восстанавливается после набега
    const float MarchTimeout = 300f;   // только страховка: у бойцов свой таймаут по длине пути

    // ---------- Разведка ----------
    public const string ScoutUnitName = "Разведчик";
    const float ScoutSeconds = 8f;                     // сколько разведчик осматривает деревню на месте
    public static int ScoutCost(RaidTarget t) { return 15 + 10 * t.Level; }   // золото за разведку

    public Phase State { get; private set; } = Phase.Idle;
    // Бой идёт (башни стреляют, защитники дерутся) - только если мы напали. Ушли из разведки - никто не тронет.
    public bool BattleOn => fought && (State == Phase.Battle || State == Phase.Leaving);
    bool fought;
    public static bool ViewingBattle => Instance != null && Instance.viewBattle && RaidMap.Instance != null;

    readonly List<RaidTarget> targets = new List<RaidTarget>();
    RaidTarget current;
    readonly List<Squad> squads = new List<Squad>();
    readonly List<CitizenAgent> members = new List<CitizenAgent>();
    readonly Dictionary<CitizenAgent, Loot> survivors = new Dictionary<CitizenAgent, Loot>();

    int food;
    int woodPaid;
    bool campPaid;
    float eatTimer, campFoodAcc, timer, pickupTimer;
    Vector2 homeEdge, campPos, landing;
    GameObject campGo;

    public bool HasCamp => campPaid && campGo != null;
    public Vector2 CampPosition => campPos;

    // Разведчики в пути и принесённые карты
    class ScoutMission
    {
        public CitizenAgent agent;
        public RaidTarget target;
        public Vector2 edge;
        public bool away;
        public float timer;
    }
    readonly List<ScoutMission> missions = new List<ScoutMission>();
    readonly Dictionary<RaidTarget, ScoutReport> reports = new Dictionary<RaidTarget, ScoutReport>();
    ScoutReport mapShown;   // открытая карта разведчика

    // ---------- Полевое снаряжение, взятое в поход (заслоны / пункты из запаса Ратуши) ----------
    readonly List<FieldGear.Item>[] kit = FieldGear.NewStock();
    readonly int[] kitPick = new int[FieldGear.Kinds];   // сколько берём (окно «В бой!»)

    /// <summary>Сколько таких укреплений взято в поход.</summary>
    public int KitCount(PostKind k) { return kit[(int)k].Count; }

    /// <summary>Достать лучшее укрепление этого вида из набора похода.</summary>
    public bool TakeKit(PostKind k, out FieldGear.Item item)
    {
        item = null;
        List<FieldGear.Item> l = kit[(int)k];
        int best = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < l.Count; i++)
            if (l[i].Score > bestScore) { bestScore = l[i].Score; best = i; }
        if (best < 0) return false;
        item = l[best];
        l.RemoveAt(best);
        return true;
    }

    /// <summary>Свернули укрепление на поле боя - вещь снова в наборе похода.</summary>
    public void ReturnKitItem(FieldGear.Item it)
    {
        if (it != null) kit[(int)it.kind].Add(it);
    }

    // Забрать выбранное из запаса Ратуши в поход
    void LoadKit()
    {
        for (int k = 0; k < FieldGear.Kinds; k++)
        {
            int n = Mathf.Min(kitPick[k], FieldGear.Count((PostKind)k));
            for (int i = 0; i < n; i++)
                if (FieldGear.Take((PostKind)k, out FieldGear.Item it)) kit[k].Add(it);
            kitPick[k] = 0;
        }
        TrapStock.LoadRaidKit();   // боевые ловушки
    }

    // Набор похода: toStock - вернуть остаток в запас Ратуши, иначе он потерян
    void UnloadKit(bool toStock)
    {
        for (int k = 0; k < FieldGear.Kinds; k++)
        {
            if (toStock) foreach (var it in kit[k]) FieldGear.Add(it);
            kit[k].Clear();
        }
        TrapStock.UnloadRaidKit(toStock);
    }

    // Какие укрепления показывать в окне «В бой!»: изготовленные в Кузне и доступные ветке (или уже лежащие в запасе).
    // Быстрый заслон не берут с собой - его ставят на месте за дерево.
    static List<int> KitKinds()
    {
        var list = new List<int>();
        for (int k = 0; k < FieldGear.Kinds; k++)
        {
            if (!FieldPost.Specs[k].crafted) continue;
            if (FieldPost.Allowed((PostKind)k) || FieldGear.Count((PostKind)k) > 0) list.Add(k);
        }
        return list;
    }

    // Строки выбора снаряжения в окне «В бой!»
    void DrawKitRows(float x, float y, float w)
    {
        GUIStyle small = Style(12);
        var kinds = KitKinds();
        for (int i = 0; i < kinds.Count; i++)
        {
            int k = kinds[i];
            FieldPost.Spec spec = FieldPost.Specs[k];
            int have = FieldGear.Count((PostKind)k);
            kitPick[k] = Mathf.Clamp(kitPick[k], 0, have);
            float ry = y + i * 24f;
            GUI.Label(new Rect(x, ry, w - 100f, 22f), $"{spec.name}: в запасе <b>{have}</b>, берём <b>{kitPick[k]}</b>", small);

            bool en = GUI.enabled;
            GUI.enabled = en && kitPick[k] > 0;
            if (GUI.Button(new Rect(x + w - 84f, ry, 40f, 22f), "−")) kitPick[k]--;
            GUI.enabled = en && kitPick[k] < have;
            if (GUI.Button(new Rect(x + w - 40f, ry, 40f, 22f), "+")) kitPick[k]++;
            GUI.enabled = en;
        }
        TrapStock.DrawRaidPickRow(x, y + kinds.Count * 24f, w, small);   // последняя строка - боевые ловушки
    }

    // Итоги
    int startCount, lost, kills, ruinedCount, defendersTotal, towersTotal;

    // Голем Алтаря жизни (школа Магии жизни) в походе
    bool takeGolem = true;      // галочка в окне «Набег»
    bool golemOut;              // голем ушёл с армией
    bool golemLost;             // погиб в набеге
    float golemHpBack = -1f;    // ушёл с поля живым: с каким здоровьем
    RaidUnit golemUnit;
    bool hallSacked, resistanceBroken;
    readonly List<Weapon> trophyWeapons = new List<Weapon>();
    readonly List<Shield> trophyShields = new List<Shield>();
    string report;

    // Камера
    bool viewBattle;
    Vector3 homeCamPos;

    // UI
    bool planOpen, statusOpen;
    int pickTarget;
    bool takeCamp = true;
    readonly HashSet<Squad> picked = new HashSet<Squad>();
    readonly HashSet<Squad> selected = new HashSet<Squad>();
    Vector2 planScroll, villageScroll;
    float hudHeight = 200f;
    RaidBuilding hoverBuilding;
    RaidUnit hoverUnit;

    static readonly List<Rect> frameRects = new List<Rect>();
    static Rect[] publishedRects = new Rect[0];

    // ---------- Жизненный цикл ----------

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        var rng = new System.Random(Random.Range(1, int.MaxValue));
        var usedNames = new HashSet<string>();
        for (int lvl = 1; lvl <= 3; lvl++)
        {
            string n;
            do n = BotVillage.RandomName(rng); while (!usedNames.Add(n));
            targets.Add(new RaidTarget { Name = n, Level = lvl, Seed = rng.Next(1, int.MaxValue) });
        }

        // Соседи - живые соперники: копят войско, укрепляются, нападают и мстят
        gameObject.AddComponent<RivalManager>().Init(targets);
    }

    /// <summary>Армия не дома (в пути, у чужой деревни или возвращается).</summary>
    public bool ArmyAway => State != Phase.Idle;
    /// <summary>Деревня, на которую идёт (или с которой возвращается) наш набег.</summary>
    public RaidTarget Current => current;

    /// <summary>К защитникам подошло подкрепление.</summary>
    public void AddDefendersTotal(int n) { defendersTotal += n; }

    /// <summary>Защитники заметили нашу армию и ударили первыми.</summary>
    public void DefendersStrikeFirst()
    {
        if (State != Phase.Scouting) return;
        State = Phase.Battle;
        fought = true;
        foreach (var u in RaidUnit.All) if (u != null && u.Attacker && u.Alive) u.OrderHold(u.Position);
        WarManager.Notify($"{current.Name} ударили первыми! Бойцы держат позиции — отдайте приказы", false);
    }

    // Тревога (WarManager.RaiseAlarm): свернуть окно, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()   // только окно планирования; идущий набег и его статус не трогаем
    {
        if (!planOpen) return false;
        planOpen = false;
        return true;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>Курсор над окнами набега (BuildingPlacer не должен кликать по карте под ними).</summary>
    public static bool IsPointerOverUI(Vector2 guiPoint)
    {
        foreach (var r in publishedRects) if (r.Contains(guiPoint)) return true;
        return false;
    }

    // ---------- Выбор отрядов ----------

    static List<Squad> AvailableSquads()
    {
        var list = new List<Squad>();
        foreach (var b in BuildingInstance.All)
        {
            if (b == null) continue;
            BarracksController br = b.GetComponent<BarracksController>();
            if (br == null) continue;
            foreach (var sq in br.Squads)
                if (sq != null && !sq.OnExpedition && SoldiersOf(sq).Count > 0) list.Add(sq);
        }
        return list;
    }

    static List<CitizenAgent> SoldiersOf(Squad sq)
    {
        var list = new List<CitizenAgent>();
        foreach (var m in sq.Members)
            if (m != null && !m.IsDead && m.IsSoldier && !m.IsOnExpedition) list.Add(m);
        return list;
    }

    static string BarracksLabel(Squad sq)
    {
        foreach (var m in sq.Members)
            if (m != null && m.Barracks != null)
                return $"казарма [{m.Barracks.OriginCell.x},{m.Barracks.OriginCell.y}]";
        return "казарма";
    }

    int PickedSoldiers()
    {
        int n = 0;
        foreach (var sq in picked) if (sq != null && !sq.OnExpedition) n += SoldiersOf(sq).Count;
        return n;
    }

    // Почему нельзя выступить ("" - можно)
    string LaunchProblem()
    {
        if (State != Phase.Idle) return "Армия уже в походе";
        if (WarManager.IsWar) return "Идёт война — армия нужна дома";
        if (pickTarget < 0 || pickTarget >= targets.Count) return "Выберите деревню";
        if (!targets[pickTarget].Ready) return "Деревня ещё не восстановилась";
        int n = PickedSoldiers();
        if (n == 0) return "Отметьте отряды";
        var rm = ResourceManager.Instance;
        int needFood = n * FoodPerSoldier, needWood = takeCamp ? CampWood : 0;
        if (rm == null || rm.food < needFood) return $"Не хватает еды: нужно {needFood}";
        if (rm.wood < needWood) return $"Не хватает дерева на лагерь: нужно {needWood}";
        return "";
    }

    // ---------- Выступление ----------

    void Launch()
    {
        if (LaunchProblem() != "") return;
        RaidTarget t = targets[pickTarget];

        squads.Clear();
        members.Clear();
        foreach (var sq in picked)
        {
            if (sq == null || sq.OnExpedition) continue;
            var list = SoldiersOf(sq);
            if (list.Count == 0) continue;
            sq.GoHome(SoldierOrder.Guard);   // снять лагерь, вернуть его еду
            sq.OnExpedition = true;
            squads.Add(sq);
            members.AddRange(list);
        }
        if (members.Count == 0) return;

        golemOut = takeGolem && LifeAltar.TakeForRaid();
        golemLost = false;
        golemHpBack = -1f;
        golemUnit = null;

        var rm = ResourceManager.Instance;
        food = members.Count * FoodPerSoldier;
        woodPaid = takeCamp ? CampWood : 0;
        rm.Spend(0, woodPaid, 0, food);
        campPaid = takeCamp;
        LoadKit();

        // Уходят через край карты, ближайший к их казармам
        Vector2 c = Vector2.zero;
        foreach (var m in members) c += (Vector2)m.transform.position;
        c /= members.Count;
        homeEdge = HomeEdgePoint(c);
        foreach (var m in members) m.BeginExpedition(JitterHome(homeEdge));

        current = t;
        ResetStats();
        startCount = members.Count;
        State = Phase.Marching;
        timer = 0f;
        planOpen = false;
        picked.Clear();
        WarManager.Notify($"Армия выступила в набег на {t.Name}: бойцов {members.Count}{(golemOut ? " и голем" : "")}, взято еды {food}", true);
    }

    // Голем возвращается домой к алтарю (hp < 0 - раны как были, в бою не был)
    void GolemHome(float hp)
    {
        if (!golemOut) return;
        golemOut = false;
        golemUnit = null;
        LifeAltar.BackFromRaid(hp, JitterHome(homeEdge));
    }

    void ResetStats()
    {
        lost = kills = ruinedCount = defendersTotal = towersTotal = 0;
        hallSacked = resistanceBroken = fought = false;
        survivors.Clear();
        trophyWeapons.Clear();
        trophyShields.Clear();
        selected.Clear();
        eatTimer = campFoodAcc = 0f;
    }

    static Vector2 HomeEdgePoint(Vector2 p)
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return p;
        float h = nav.mapSize / 2f - 0.5f;
        float dl = p.x + h, dr = h - p.x, db = p.y + h, du = h - p.y;
        float m = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, du));
        Vector2 e;
        if (m == dl) e = new Vector2(-h, p.y);
        else if (m == dr) e = new Vector2(h, p.y);
        else if (m == db) e = new Vector2(p.x, -h);
        else e = new Vector2(p.x, h);
        Vector2Int cell = nav.WorldToCell(e);
        if (nav.IsBlocked(cell)) cell = nav.NearestFree(cell);
        return nav.CellCenter(cell);
    }

    static Vector2 JitterHome(Vector2 p)
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return p;
        Vector2Int cell = nav.WorldToCell(p + Random.insideUnitCircle * 1.5f);
        if (!nav.InBounds(cell) || nav.IsBlocked(cell)) cell = nav.NearestFree(nav.WorldToCell(p));
        return nav.CellCenter(cell);
    }

    /// <summary>Отменить поход, пока армия ещё не дошла до деревни: всё возвращается.</summary>
    void Recall()
    {
        if (State != Phase.Marching && State != Phase.TravelOut) return;
        foreach (var m in members) if (m != null && !m.IsDead) m.CancelExpedition();
        var rm = ResourceManager.Instance;
        if (rm != null) rm.Add(0, woodPaid, 0, food);
        UnloadKit(true);
        GolemHome(-1f);
        food = 0;
        woodPaid = 0;
        ReleaseSquads();
        members.Clear();
        current = null;
        State = Phase.Idle;
        statusOpen = false;
        WarManager.Notify("Поход отменён — армия возвращается", false);
    }

    void ReleaseSquads()
    {
        foreach (var sq in squads)
        {
            if (sq == null) continue;
            sq.OnExpedition = false;
            sq.GoHome(SoldierOrder.Guard);
        }
        squads.Clear();
    }

    // ---------- Главный цикл ----------

    void Update()
    {
        members.RemoveAll(m => m == null || m.IsDead);
        HandleKeys();
        ScoutTick(Time.deltaTime);

        switch (State)
        {
            case Phase.Marching:
                timer += Time.deltaTime;
                if (members.Count == 0) { FailEmpty(); break; }
                bool allAway = true;
                foreach (var m in members) if (!m.IsAway) { allAway = false; break; }
                if (!allAway && timer > MarchTimeout)
                {
                    foreach (var m in members) m.ForceAway();
                    allAway = true;
                }
                if (allAway)
                {
                    State = Phase.TravelOut;
                    timer = current.TravelSeconds;
                    WarManager.Notify($"Армия ушла за край карты. До {current.Name} — {timer:F0} с", true);
                }
                break;

            case Phase.TravelOut:
                if (members.Count == 0) { FailEmpty(); break; }
                timer -= Time.deltaTime;
                if (timer <= 0f) Arrive();
                break;

            case Phase.Scouting:
            case Phase.Battle:
            case Phase.Leaving:
                BattleTick(Time.deltaTime);
                if (ViewingBattle) HandleBattleInput();
                break;

            case Phase.TravelHome:
                timer -= Time.deltaTime;
                if (timer <= 0f) ReturnHome();
                break;
        }
    }

    // Все бойцы погибли / исчезли ещё по дороге
    void FailEmpty()
    {
        UnloadKit(true);
        GolemHome(-1f);
        ReleaseSquads();
        current = null;
        State = Phase.Idle;
        WarManager.Notify("Поход сорван: бойцов не осталось", false);
    }

    void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.vKey.wasPressedThisFrame && RaidMap.Instance != null) SetView(!viewBattle);
        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (mapShown != null) mapShown = null;
            else if (planOpen) planOpen = false;
            else if (ViewingBattle) selected.Clear();
        }
    }

    // ---------- Прибытие и бой ----------

    void Arrive()
    {
        BotVillage.Plan plan = current.GetPlan();
        RaidMap map = RaidMap.Create(plan.Trees);
        BotVillage.Build(plan, map, current);
        defendersTotal = plan.Defenders.Count;
        towersTotal = plan.Towers + plan.Cannons + plan.Guns;

        float half = RaidMap.Size / 2f;
        landing = RaidMap.Local(-half + 5.5f, 0.5f);
        campPos = RaidMap.Local(-half + 2.5f, 0.5f);

        int i = 0;
        foreach (var m in members)
        {
            if (m == null || m.IsDead || !m.IsAway) continue;
            int row = i / 5, col = i % 5;
            Vector2 p = landing + new Vector2(row * 0.9f, (col - 2) * 0.9f);
            Vector2Int cell = map.WorldToCell(p);
            if (map.IsBlocked(cell)) p = map.CellCenter(map.NearestFree(cell));
            RaidUnit.SpawnAttacker(m, m.Squad, p);
            i++;
        }

        // Голем идёт последним рядом (если алтарь дома ещё цел)
        if (golemOut)
        {
            if (LifeAltar.RaidBound)
            {
                int row = i / 5, col = i % 5;
                Vector2 p = landing + new Vector2(row * 0.9f, (col - 2) * 0.9f);
                Vector2Int cell = map.WorldToCell(p);
                if (map.IsBlocked(cell)) p = map.CellCenter(map.NearestFree(cell));
                golemUnit = RaidUnit.SpawnGolem(LifeAltar.RaidUnitType(), LifeAltar.RaidHp, p);
            }
            else { golemOut = false; golemLost = true; }
        }

        if (campPaid) BuildCamp();
        FogOfWar.CreateRaid();   // туман: видно края карты и вокруг наших

        State = Phase.Scouting;
        SetView(true);
        WarManager.Notify($"Армия у деревни {current.Name}. Осмотритесь: атаковать или уйти?", true);
        RivalManager.PlayerArrived(current);
    }

    void BuildCamp()
    {
        campGo = new GameObject("RaidCamp");
        campGo.transform.position = new Vector3(campPos.x, campPos.y, 0f);
        CampPart("Tent", new Vector2(0f, -0.1f), new Vector2(1.1f, 0.7f), new Color(0.85f, 0.78f, 0.55f), 3);
        CampPart("Pole", new Vector2(0.65f, 0.35f), new Vector2(0.06f, 1f), new Color(0.35f, 0.25f, 0.15f), 4);
        CampPart("Flag", new Vector2(0.85f, 0.7f), new Vector2(0.38f, 0.26f), new Color(0.25f, 0.55f, 1f), 4);
    }

    void CampPart(string n, Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(campGo.transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.color = c;
        sr.sortingOrder = order;
    }

    public bool InCampCover(Vector2 p) { return campPaid && campGo != null && Vector2.Distance(p, campPos) <= CampCover; }

    void StartAttack()
    {
        if (State != Phase.Scouting) return;
        State = Phase.Battle;
        fought = true;
        foreach (var u in RaidUnit.All) if (u != null && u.Attacker) u.OrderAuto();
        WarManager.Notify($"Штурм {current.Name}!", true);
    }

    Vector2 ExitFor(RaidUnit u)
    {
        float y = Mathf.Clamp(u.Position.y - RaidMap.Origin.y, -8f, 8f);
        return RaidMap.Local(-RaidMap.Size / 2f + 0.5f, y);
    }

    void RetreatUnits(IEnumerable<RaidUnit> list)
    {
        foreach (var u in list) if (u != null && u.Attacker && u.Alive) u.OrderRetreat(ExitFor(u));
    }

    void RetreatAll()
    {
        RetreatUnits(new List<RaidUnit>(RaidUnit.All));
        State = Phase.Leaving;
        WarManager.Notify("Армия отступает домой", false);
    }

    List<RaidUnit> Attackers()
    {
        var list = new List<RaidUnit>();
        foreach (var u in RaidUnit.All) if (u != null && u.Attacker && u.Alive) list.Add(u);
        return list;
    }

    List<RaidUnit> SelectedAttackers()
    {
        var list = Attackers();
        if (selected.Count == 0) return list;
        list.RemoveAll(u => u.Squad == null || !selected.Contains(u.Squad));
        return list.Count > 0 ? list : Attackers();
    }

    void BattleTick(float dt)
    {
        List<RaidUnit> att = Attackers();

        // Еда из запаса похода
        eatTimer += dt;
        if (eatTimer >= EatInterval)
        {
            eatTimer -= EatInterval;
            int had = food;
            int eaters = 0;
            foreach (var u in att) if (!u.IsGolem) eaters++;   // голем не ест
            food = Mathf.Max(0, food - eaters);
            if (had > 0 && food == 0) WarManager.Notify("В походе кончилась еда — бойцы слабеют", false);
        }
        if (food <= 0)
            foreach (var u in att) if (u.Source != null) u.Source.ExpeditionHunger(HungerPerSecond * dt);

        // Лагерь лечит тех, кто рядом и не в бою (за еду из запаса)
        if (campPaid && campGo != null && food > 0)
        {
            foreach (var u in att)
            {
                if (u.Source == null || u.InCombat || u.Hp >= u.MaxHp - 0.01f) continue;   // голема лечит только алтарь
                if (Vector2.Distance(u.Position, campPos) > CampRadius) continue;
                float got = u.Source.ReceiveHeal(CampHealPerSecond * dt);
                campFoodAcc += got / CampHpPerFood;
            }
            while (campFoodAcc >= 1f && food > 0) { food--; campFoodAcc -= 1f; }
        }

        // Добычу с руин подбирают бойцы, которые стоят рядом и у которых есть место
        pickupTimer -= dt;
        if (pickupTimer <= 0f)
        {
            pickupTimer = 0.5f;
            foreach (var b in RaidBuilding.All)
            {
                if (b == null || !b.IsRuined || b.Storage.IsEmpty) continue;
                foreach (var u in att)
                {
                    if (u.CarryFree <= 0 || b.DistanceTo(u.Position) > 1.6f) continue;
                    int got = TakeLoot(b.Storage, u.Carried, u.CarryFree);
                    if (got > 0) CombatFx.LootFly(b.Center, u.Position);
                    if (b.Storage.IsEmpty) break;
                }
            }
        }

        // Победные вехи
        if (State != Phase.Scouting)
        {
            if (!resistanceBroken && defendersTotal > 0 && CountDefenders() == 0)
            {
                resistanceBroken = true;
                WarManager.Notify($"Защитники {current.Name} разбиты!", true);
            }
        }

        // Никого из наших на поле не осталось - поход окончен
        if ((State == Phase.Battle || State == Phase.Leaving) && att.Count == 0) Finish();
    }

    static int CountDefenders()
    {
        int n = 0;
        foreach (var u in RaidUnit.All) if (u != null && !u.Attacker && u.Alive) n++;
        return n;
    }

    // Тревога: защитники рядом бросаются в бой
    public void Alarm(Vector2 at, float radius)
    {
        if (!BattleOn) return;
        foreach (var u in RaidUnit.All)
            if (u != null && !u.Attacker && u.Alive && Vector2.Distance(u.Position, at) <= radius) u.Alert();
    }

    // ---------- События боя ----------

    public void OnBuildingRuined(RaidBuilding b, RaidUnit by)
    {
        ruinedCount++;
        if (b.IsHall && !hallSacked)
        {
            hallSacked = true;
            WarManager.Notify($"Ратуша {current.Name} разрушена — казна деревни ваша!", true);
        }
        if (b.Storage.IsEmpty) return;

        // Склад делят бойцы рядом: у кого больше места - тот берёт первым
        var near = new List<RaidUnit>();
        foreach (var u in Attackers())
            if (u == by || b.DistanceTo(u.Position) <= 3.5f) near.Add(u);
        near.Sort((a, c) => c.CarryFree.CompareTo(a.CarryFree));

        int taken = 0;
        foreach (var u in near)
        {
            if (b.Storage.IsEmpty) break;
            int got = TakeLoot(b.Storage, u.Carried, u.CarryFree);
            if (got > 0) { taken += got; CombatFx.LootFly(b.Center, u.Position); }
        }

        if (!b.Storage.IsEmpty)
            WarManager.Notify($"{b.Title}: мешки полны, на руинах осталось {b.Storage.Total} — подойдите, чтобы забрать", false);
    }

    public void OnDefenderKilled(RaidUnit u, RaidUnit by, Weapon weapon, Shield shield)
    {
        kills++;
        int lvl = current != null ? current.Level : 1;
        if (u != null && u.Unit != null) RivalManager.DefenderLost(current, u.Unit.unitName);

        // Убийце - кошель защитника
        if (by != null && by.Attacker && by.Alive && by.CarryFree > 0)
        {
            int gold = Mathf.Min(by.CarryFree, Random.Range(5, 16) * lvl);
            by.Carried.gold += gold;
            CombatFx.LootFly(u.Position, by.Position);
        }

        // Трофейное снаряжение (поношенное) - домой, если кто-то вернётся
        if (weapon != null && Random.value < 0.35f)
        {
            weapon.durability = weapon.maxDurability * Random.Range(0.4f, 0.8f);
            trophyWeapons.Add(weapon);
        }
        if (shield != null && Random.value < 0.35f)
        {
            shield.durability = shield.maxDurability * Random.Range(0.4f, 0.8f);
            trophyShields.Add(shield);
        }
    }

    public void OnAttackerKilled(RaidUnit u)
    {
        if (u.IsGolem)
        {
            golemLost = true;
            golemUnit = null;
            LifeAltar.DiedInRaid();
            return;
        }
        lost++;
        if (u.Carried.IsEmpty) return;

        // Мешок погибшего забирают защитники - он возвращается на склад Ратуши
        foreach (var b in RaidBuilding.All)
        {
            if (b != null && b.IsHall && !b.IsRuined)
            {
                u.Carried.MoveTo(b.Storage);
                WarManager.Notify("Добыча погибшего бойца вернулась в чужую Ратушу", false);
                return;
            }
        }
        WarManager.Notify($"Погибший боец обронил добычу: {u.Carried.ShortText}", false);
        u.Carried.Clear();
    }

    public void OnAttackerLeft(RaidUnit u)
    {
        if (u.IsGolem) { golemHpBack = Mathf.Max(1f, u.Hp); golemUnit = null; return; }
        if (u.Source == null || u.Source.IsDead) return;
        Loot l = new Loot();
        u.Carried.MoveTo(l);
        survivors[u.Source] = l;
    }

    // Переложить добычу (сначала ценное), не больше room. Возвращает, сколько переложено.
    static int TakeLoot(Loot from, Loot to, int room)
    {
        int moved = 0;
        int Move(ref int a, ref int b)
        {
            int n = Mathf.Min(a, room - moved);
            if (n <= 0) return 0;
            a -= n;
            b += n;
            moved += n;
            return n;
        }

        Move(ref from.gold, ref to.gold);
        for (int t = 2; t >= 0; t--) Move(ref from.steel[t], ref to.steel[t]);
        for (int t = 2; t >= 0; t--) Move(ref from.reinforced[t], ref to.reinforced[t]);
        for (int t = 2; t >= 0; t--) Move(ref from.plates[t], ref to.plates[t]);
        for (int t = 2; t >= 0; t--) Move(ref from.planks[t], ref to.planks[t]);
        Move(ref from.iron, ref to.iron);
        Move(ref from.food, ref to.food);
        Move(ref from.wood, ref to.wood);
        Move(ref from.stone, ref to.stone);
        Move(ref from.oats, ref to.oats);
        Move(ref from.berries, ref to.berries);
        Move(ref from.herbs, ref to.herbs);
        Move(ref from.potions, ref to.potions);
        return moved;
    }

    // ---------- Конец боя ----------

    void Finish()
    {
        // Поле боя убираем целиком
        foreach (var u in new List<RaidUnit>(RaidUnit.All)) if (u != null) Destroy(u.gameObject);
        foreach (var b in new List<RaidBuilding>(RaidBuilding.All)) if (b != null) Destroy(b.gameObject);
        if (campGo != null) Destroy(campGo);
        campGo = null;
        if (RaidMap.Instance != null) Destroy(RaidMap.Instance.gameObject);
        if (FogOfWar.Raid != null) Destroy(FogOfWar.Raid.gameObject);
        if (RaidDefense.Instance != null) Destroy(RaidDefense.Instance.gameObject);
        FieldPost.ClearRaid();   // заслоны и пункты на поле боя пропадают вместе с ним
        Trap.ClearRaid();        // поставленные боевые ловушки - тоже
        mapShown = null;
        SetView(false);

        RivalManager.RaidFinished(current, fought, hallSacked, ruinedCount, kills);

        // Деревня отстраивается заново (другая расстановка), пока - восстанавливается
        if (current != null && (ruinedCount > 0 || kills > 0))
        {
            current.TimesRaided++;
            current.RecoverAt = Time.time + RecoverSeconds;
            current.Seed = Random.Range(1, int.MaxValue);
            current.PlanCache = null;
        }

        State = Phase.TravelHome;
        timer = current != null ? current.TravelSeconds : 20f;
        if (survivors.Count == 0 && golemHpBack <= 0f)
        {
            timer = 0.1f;
            WarManager.Notify("Из набега не вернулся никто", false);
        }
        else WarManager.Notify($"Армия возвращается домой: {survivors.Count} бойцов{(golemHpBack > 0f ? " и голем" : "")}, в пути {timer:F0} с", true);
    }

    void ReturnHome()
    {
        Loot total = new Loot();
        int back = 0;
        foreach (var kv in survivors)
        {
            CitizenAgent m = kv.Key;
            if (m == null || m.IsDead) continue;
            total.Add(kv.Value);
            m.ReturnFromExpedition(JitterHome(homeEdge), kv.Value);
            back++;
        }

        // Голем: ушёл с поля живым - возвращается к алтарю с теми же ранами; не вышел - погиб
        bool golemWent = golemOut || golemLost;
        bool golemBack = golemOut && golemHpBack > 0f && LifeAltar.RaidBound;
        if (golemOut)
        {
            if (golemBack) GolemHome(golemHpBack);
            else { LifeAltar.DiedInRaid(); golemOut = false; golemLost = true; }
        }

        // Кто числился в походе, но на поле не попал (такого быть не должно) - просто возвращается
        foreach (var m in members) if (m != null && !m.IsDead && m.IsOnExpedition) m.CancelExpedition();

        // Остаток еды - в казну, трофеи - в Оружейную
        var rm = ResourceManager.Instance;
        if (rm != null && food > 0 && back > 0) rm.Add(0, 0, 0, food);
        int foodBack = back > 0 ? food : 0;
        food = 0;
        UnloadKit(back > 0);   // уцелевшие укрепления возвращаются в запас Ратуши

        int trophies = 0, trophiesLost = 0;
        if (back > 0)
        {
            foreach (var w in trophyWeapons) { if (ArmoryController.Stash(w)) trophies++; else trophiesLost++; }
            foreach (var s in trophyShields) { if (ArmoryController.StashShield(s)) trophies++; else trophiesLost++; }
        }
        trophyWeapons.Clear();
        trophyShields.Clear();

        ReleaseSquads();

        // Отчёт
        var sb = new StringBuilder();
        sb.Append($"<b>Набег на {current.Name}</b> ({current.LevelName})\n\n");
        if (back == 0) sb.Append("<color=#FF6060>Никто не вернулся.</color>\n");
        else if (hallSacked) sb.Append("<color=#80FF80>Ратуша разграблена!</color>\n");
        else if (resistanceBroken) sb.Append("<color=#80FF80>Защитники разбиты.</color>\n");
        else if (kills == 0 && ruinedCount == 0) sb.Append("Армия ушла без боя.\n");
        else sb.Append("<color=#FFD060>Набег прерван.</color>\n");
        sb.Append($"Бойцов: ушло {startCount}, вернулось <b>{back}</b>, погибло <color=#FF8060>{lost}</color>\n");
        sb.Append($"Убито защитников: {kills}/{defendersTotal}, разрушено зданий: {ruinedCount}\n");
        if (golemWent) sb.Append(golemBack ? UiKit.Col(UiKit.GoodHex, "Голем вернулся к алтарю.") + "\n" : UiKit.Col(UiKit.BadHex, "Голем погиб — растите нового на алтаре.") + "\n");
        sb.Append($"Добыча: <color=#FFD040>{total.ShortText}</color>\n");
        if (trophies > 0) sb.Append($"Трофейное снаряжение на складе Оружейной: {trophies}\n");
        if (trophiesLost > 0) sb.Append($"<color=#FF9060>Трофеев некуда положить (нет места в Оружейных): {trophiesLost}</color>\n");
        if (foodBack > 0) sb.Append($"Остаток припасов вернулся в казну: {foodBack} еды\n");
        sb.Append("\n<color=#A0A0A0>Бойцы сами отнесут добычу в казарму.</color>");
        report = sb.ToString();

        WarManager.Notify($"Армия вернулась из набега: {back} бойцов", back > 0);
        survivors.Clear();
        members.Clear();
        current = null;
        State = Phase.Idle;
    }

    // ---------- Камера ----------

    void SetView(bool battle)
    {
        bool can = battle && RaidMap.Instance != null;
        if (can == viewBattle) return;
        Camera cam = Camera.main;
        viewBattle = can;

        if (viewBattle)
        {
            if (cam != null) homeCamPos = cam.transform.position;
            CameraController.SetViewArea(RaidMap.Origin, RaidMap.Size);
            Vector2 focus = landing + new Vector2(8f, 0f);
            var att = Attackers();
            if (att.Count > 0)
            {
                Vector2 c = Vector2.zero;
                foreach (var u in att) c += u.Position;
                focus = c / att.Count;
            }
            if (CameraController.Instance != null) CameraController.Instance.FocusOn(focus);
        }
        else
        {
            CameraController.ClearViewArea();
            if (CameraController.Instance != null) CameraController.Instance.FocusOn(homeCamPos);
        }
    }

    // ---------- Управление боем (мышь) ----------

    void HandleBattleInput()
    {
        Mouse mouse = Mouse.current;
        Camera cam = Camera.main;
        if (mouse == null || cam == null) return;

        Vector2 sp = mouse.position.ReadValue();
        Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
        Vector3 w3 = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, -cam.transform.position.z));
        Vector2 world = new Vector2(w3.x, w3.y);

        bool overUI = IsPointerOverUI(gp) || FieldPostManager.Blocks(gp) || TrapManager.Blocks(gp) || DevelopmentPath.IsPointerOver(gp) || WeaponForge.IsPointerOver(gp)
            || (GameMenu.Instance != null && GameMenu.Instance.IsPointerOver(gp))
            || (GameClock.Instance != null && GameClock.Instance.IsPointerOver(gp));
        if (overUI) CameraController.BlockZoom();

        hoverBuilding = null;
        hoverUnit = null;
        if (!overUI)
        {
            hoverUnit = PickUnit(world, null);
            if (hoverUnit != null && !hoverUnit.Attacker && !FogOfWar.IsVisible(hoverUnit.Position)) hoverUnit = null;   // в тумане не видно
            if (hoverUnit == null && FogOfWar.IsExplored(world))
                foreach (var b in RaidBuilding.All)
                    if (b != null && b.Contains(world)) { hoverBuilding = b; break; }
        }

        if (overUI || !mouse.leftButton.wasPressedThisFrame) return;

        // Свой боец - выбрать его отряд (Shift - добавить)
        RaidUnit own = PickUnit(world, true);
        if (own != null && own.Squad != null)
        {
            bool add = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (!add) selected.Clear();
            if (!selected.Add(own.Squad) && add) selected.Remove(own.Squad);
            return;
        }

        if (State != Phase.Battle) return;
        RaidMap map = RaidMap.Instance;
        if (map == null || !map.InBounds(map.WorldToCell(world))) return;

        var list = SelectedAttackers();
        if (hoverBuilding != null && !hoverBuilding.IsRuined)
        {
            foreach (var u in list) u.OrderFocus(hoverBuilding);
            WarManager.Notify($"Приказ: ломать {hoverBuilding.Title}", true);
        }
        else
        {
            foreach (var u in list) u.OrderHold(world);
        }
    }

    static RaidUnit PickUnit(Vector2 p, bool? attackers)
    {
        RaidUnit best = null;
        float bestD = 0.55f;
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Alive) continue;
            if (attackers.HasValue && u.Attacker != attackers.Value) continue;
            float d = Vector2.Distance(p, u.Position);
            if (d < bestD) { bestD = d; best = u; }
        }
        return best;
    }

    // ---------- Интерфейс ----------

    void Publish(Rect r) { frameRects.Add(r); }

    void OnGUI()
    {
        UiKit.Apply();
        frameRects.Clear();

        DrawCornerButton();
        if (statusOpen) DrawStatusPanel();
        if (planOpen && State == Phase.Idle) DrawPlanWindow();
        if (ViewingBattle)
        {
            DrawBars();
            DrawMarkers();
            DrawBattleHud();
            DrawTooltip();
        }
        if (mapShown != null) DrawMapWindow();
        if (report != null) DrawReport();

        if (Event.current.type == EventType.Repaint) publishedRects = frameRects.ToArray();
    }

    GUIStyle Style(int size, bool wrap = true)
    {
        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = size, richText = true, wordWrap = wrap };
        st.normal.textColor = UiKit.Text;
        return st;
    }

    // Строка в одну линию (лишнее обрезается, полностью - в подсказке)
    static GUIStyle OneLine(GUIStyle from)
    {
        return new GUIStyle(from) { wordWrap = false, clipping = TextClipping.Clip };
    }

    // Крупная кнопка (угловая, «Выступить», «Атаковать»)
    static GUIStyle BigStyle(UiKit.Kind k)
    {
        return new GUIStyle(UiKit.ButtonStyle(k)) { fontSize = 14, fontStyle = FontStyle.Bold };
    }

    static bool BigButton(Rect r, string text, bool enabled, UiKit.Kind k, string tip)
    {
        bool en = GUI.enabled;
        GUI.enabled = en && enabled;
        bool b = GUI.Button(r, text, BigStyle(k));
        GUI.enabled = en;
        UiKit.Tip(r, tip);
        return b && enabled;
    }

    static string HpHex(int pct) { return pct >= 75 ? UiKit.GoodHex : (pct >= 50 ? UiKit.WarnHex : UiKit.BadHex); }

    void DrawCornerButton()
    {
        Rect r = new Rect(Screen.width - 190f, Screen.height - 50f, 180f, 40f);
        Publish(r);

        switch (State)
        {
            case Phase.Idle:
                if (BigButton(r, planOpen ? "Закрыть" : "В бой!", true, planOpen ? UiKit.Kind.Normal : UiKit.Kind.Primary,
                    planOpen ? null : "Набег на соседнюю деревню: цель, отряды, припасы"))
                { planOpen = !planOpen; if (planOpen) PreparePlan(); }
                break;

            case Phase.Marching:
            case Phase.TravelOut:
            case Phase.TravelHome:
                string label = State == Phase.Marching ? "Армия выступает..."
                             : (State == Phase.TravelOut ? $"В пути: {Mathf.CeilToInt(timer)} с" : $"Домой: {Mathf.CeilToInt(timer)} с");
                if (BigButton(r, label, true, UiKit.Kind.Normal, "Состояние похода")) statusOpen = !statusOpen;
                break;

            default:
                bool war = WarManager.IsWar;
                if (viewBattle)
                {
                    if (BigButton(r, war ? "К деревне! [V]" : "К деревне [V]", true, war ? UiKit.Kind.Danger : UiKit.Kind.Normal,
                        war ? "На вашу деревню напали — идёт война!" : "Показать свою деревню (V)")) SetView(false);
                }
                else if (BigButton(r, "К битве [V]", true, UiKit.Kind.Primary, "Показать поле набега (V)")) SetView(true);
                break;
        }
    }

    void DrawStatusPanel()
    {
        if (State == Phase.Idle) { statusOpen = false; return; }
        Rect r = new Rect(Screen.width - 330f, Screen.height - 154f, 320f, UiKit.WindowH(22f + 6f + 28f));
        Publish(r);

        string where = current != null ? current.Name : "—";
        Rect c = UiKit.Window(r, State == Phase.TravelHome ? $"Возвращение: {where}" : $"Поход: {where}", out bool close);
        if (close) { statusOpen = false; return; }

        string text = State == Phase.TravelHome
            ? $"Бойцов возвращается: <b>{survivors.Count}</b>"
            : $"Бойцов: <b>{members.Count}</b>   еда в запасе: <b>{food}</b>";
        GUI.Label(new Rect(c.x, c.y, c.width, 22f), text, OneLine(UiKit.Body));

        bool can = State == Phase.Marching || State == Phase.TravelOut;
        if (UiKit.Button(new Rect(c.x, c.y + 28f, c.width, 28f), "Отменить поход", can, UiKit.Kind.Danger,
            can ? "Армия вернётся, еда и снаряжение — тоже" : "Армия уже у цели — отменить нельзя")) Recall();
    }

    // ---------- Окно похода ----------

    void PreparePlan()
    {
        picked.Clear();
        if (pickTarget < 0 || pickTarget >= targets.Count || !targets[pickTarget].Ready)
        {
            pickTarget = targets.FindIndex(t => t.Ready);
            if (pickTarget < 0) pickTarget = 0;
        }
    }

    void DrawPlanWindow()
    {
        float w = Mathf.Min(880f, Screen.width - 40f), h = Mathf.Min(620f, Screen.height - 100f);
        Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f - 20f, w, h);
        Publish(r);
        CameraController.BlockZoom();
        Rect c = UiKit.Window(r, "Набег на соседнюю деревню", out bool close);
        if (close) { planOpen = false; return; }

        GUIStyle line = OneLine(UiKit.Body), smallLine = OneLine(UiKit.Small), mutedLine = OneLine(UiKit.MutedStyle);
        GUIStyle mutedRight = new GUIStyle(mutedLine) { alignment = TextAnchor.LowerRight };

        // --- Деревни ---
        float leftW = Mathf.Min(360f, c.width * 0.45f);
        int scoutsHome = FreeScouts().Count;
        float cy = UiKit.Section(c.x, c.y, leftW, "Куда идти");
        Rect sh = new Rect(c.x + leftW - 160f, c.y, 160f, UiKit.SectionH - 4f);
        GUI.Label(sh, $"разведчиков дома: {scoutsHome}", mutedRight);
        UiKit.Tip(sh, "Разведчики обучаются в Казарме ур. 2. Разведка приносит набросок плана деревни.");
        cy += 2f;

        var rmRes = ResourceManager.Instance;
        const float cardH = 112f, cardStep = 118f;
        Rect villView = new Rect(c.x, cy, leftW, c.yMax - cy);
        float cardW = leftW - 14f;
        villageScroll = GUI.BeginScrollView(villView, villageScroll, new Rect(0f, 0f, cardW, targets.Count * cardStep));
        float vy = 0f;
        for (int i = 0; i < targets.Count; i++)
        {
            RaidTarget t = targets[i];
            ScoutReport rep = ReportFor(t);
            ScoutMission mis = MissionFor(t);
            Rect card = new Rect(0f, vy, cardW, cardH);
            GUI.Box(card, GUIContent.none);
            if (i == pickTarget)
            {
                Color oc = GUI.color;
                GUI.color = UiKit.Good;
                GUI.DrawTexture(new Rect(card.x + 1f, card.y + 3f, 3f, card.height - 6f), Texture2D.whiteTexture);
                GUI.color = oc;
            }

            float tx = card.x + 10f, tw = card.width - 20f;
            // Название, уровень, дорога
            GUI.Label(new Rect(tx, card.y + 5f, tw - 80f, 22f), $"<b>{t.Name}</b>  {UiKit.Col(UiKit.MutedHex, t.LevelName + " · Ур. " + t.Level)}", line);
            Rect road = new Rect(tx + tw - 80f, card.y + 5f, 80f, 20f);
            GUI.Label(road, $"{t.TravelSeconds:F0} с", new GUIStyle(mutedLine) { alignment = TextAnchor.UpperRight });
            UiKit.Tip(road, $"Дорога в одну сторону: {t.TravelSeconds:F0} с");

            // Что известно
            string info, infoTip;
            if (!t.Ready)
            {
                info = UiKit.Col(UiKit.BadHex, $"Разорена — отстраивается ещё {FormatTime(t.RecoverAt - Time.time)}");
                infoTip = "После набега деревня отстраивается заново (с другой расстановкой).";
            }
            else if (rep != null)
            {
                info = UiKit.Col(UiKit.GoodHex, $"Карта ~{rep.ShownAccuracy}%") + UiKit.Col(UiKit.MutedHex, " · ") + rep.ShortSummary;
                infoTip = $"Карта разведчика, точность ~{rep.ShownAccuracy}%\n{rep.SummaryText}" +
                          (rep.Valid && rep.StyleGuess != "" ? $"\nДеревня {rep.StyleGuess}" : "");
            }
            else if (mis != null)
            {
                info = UiKit.Col(UiKit.WarnHex, $"Разведчик: {MissionText(mis)}");
                infoTip = "Что внутри — пока неизвестно.";
            }
            else
            {
                info = UiKit.Col(UiKit.MutedHex, "Не разведана — что за частоколом, неизвестно");
                infoTip = "Отправьте разведчика (Казарма ур. 2).";
            }
            Rect ir = new Rect(tx, card.y + 28f, tw, 20f);
            GUI.Label(ir, info, smallLine);
            UiKit.Tip(ir, infoTip);

            // Соседи-соперники: отношение и войско (нрав и занятие - в подсказке)
            if (RivalManager.Active)
            {
                Rect rr = new Rect(tx, card.y + 48f, tw, 20f);
                GUI.Label(rr, $"{RivalManager.TraitName(t.Trait)} · {RivalManager.AttitudeText(t)} · {RivalManager.ArmyText(t, rep)}", smallLine);
                UiKit.Tip(rr, RivalManager.CardLine(t, rep));
            }

            // Кнопки карточки
            float by0 = card.yMax - 34f, bw3 = 70f, bw1 = 84f, bw2 = tw - bw1 - bw3 - 8f;
            string selTip = !t.Ready ? "Деревня ещё не восстановилась" : null;
            if (UiKit.Button(new Rect(tx, by0, bw1, 26f), i == pickTarget ? "✓ Цель" : "Выбрать", t.Ready && i != pickTarget,
                UiKit.Kind.Normal, selTip)) pickTarget = i;

            int cost = ScoutCost(t);
            bool canScout = t.Ready && mis == null && scoutsHome > 0 && rmRes != null && rmRes.gold >= cost;
            string scoutLabel = mis != null ? "Разведчик в пути"
                              : (scoutsHome == 0 ? "Нет разведчика"
                              : (rep != null ? $"Ещё раз · {cost} з" : $"Разведать · {cost} з"));
            string scoutTip = !t.Ready ? "Деревня отстраивается"
                            : mis != null ? "Разведчик уже идёт к этой деревне"
                            : scoutsHome == 0 ? "Нужен свободный разведчик (обучается в Казарме ур. 2)"
                            : (rmRes == null || rmRes.gold < cost) ? $"Разведка стоит {cost} золота — не хватает"
                            : $"Разведка: {cost} золота. Разведчик принесёт набросок плана деревни.";
            if (UiKit.Button(new Rect(tx + bw1 + 4f, by0, bw2, 26f), scoutLabel, canScout, UiKit.Kind.Normal, scoutTip)) StartScout(t);

            if (UiKit.Button(new Rect(tx + tw - bw3, by0, bw3, 26f), "Карта", rep != null, UiKit.Kind.Normal,
                rep != null ? "Открыть карту разведчика" : "Карты нет — сначала разведайте")) mapShown = rep;

            vy += cardStep;
        }
        GUI.EndScrollView();

        // --- Отряды ---
        float rx = c.x + leftW + 16f, rw = c.xMax - rx;
        var avail = AvailableSquads();
        picked.RemoveWhere(sq => sq == null || !avail.Contains(sq));

        float ry = UiKit.Section(rx, c.y, rw, "Кто идёт");
        if (GUI.Button(new Rect(rx + rw - 130f, c.y, 60f, 18f), "Все")) foreach (var sq in avail) picked.Add(sq);
        if (GUI.Button(new Rect(rx + rw - 66f, c.y, 66f, 18f), "Никого")) picked.Clear();
        ry += 2f;

        // Нижний блок (снаряжение, припасы, кнопка) - фиксированной высоты
        float kitH = KitKinds().Count * 24f + 24f;   // строки укреплений + строка ловушек
        bool golemRow = LifeAltar.CanJoinRaid;
        float bottomH = UiKit.SectionH + kitH + 4f + UiKit.SectionH + 24f + 24f + (golemRow ? 24f : 0f) + 22f + 36f;
        float listH = Mathf.Max(40f, c.yMax - bottomH - ry - 6f);
        const float rowH = 26f;
        Rect view = new Rect(rx, ry, rw, listH);
        planScroll = GUI.BeginScrollView(view, planScroll, new Rect(0f, 0f, rw - 14f, Mathf.Max(listH, avail.Count * rowH)));
        if (avail.Count == 0)
            GUI.Label(new Rect(0f, 0f, rw - 14f, 60f), UiKit.Col(UiKit.MutedHex, "Нет отрядов. Обучите бойцов в Казарме — каждый новый боец попадает в отряд."), UiKit.Body);
        GUIStyle tg = new GUIStyle(GUI.skin.toggle) { richText = true, wordWrap = false, clipping = TextClipping.Clip };
        for (int i = 0; i < avail.Count; i++)
        {
            Squad sq = avail[i];
            var soldiers = SoldiersOf(sq);
            var counts = new Dictionary<string, int>();
            float hpSum = 0f;
            int horses = 0;
            foreach (var m in soldiers)
            {
                counts.TryGetValue(m.DisplayName, out int cnt);
                counts[m.DisplayName] = cnt + 1;
                hpSum += m.HpFraction;
                if (m.HasHorse) horses++;
            }
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add($"{kv.Key} {kv.Value}");
            int hpPct = Mathf.RoundToInt(hpSum / Mathf.Max(1, soldiers.Count) * 100f);
            int carry = (soldiers.Count - horses) * CarryFoot + horses * CarryMounted;

            bool on = picked.Contains(sq);
            Rect tr = new Rect(0f, i * rowH, rw - 14f, rowH - 4f);
            string label = $" <b>{sq.Name}</b>  {soldiers.Count} бойц. · <color={HpHex(hpPct)}>{hpPct}%</color>  {UiKit.Col(UiKit.MutedHex, string.Join(", ", parts))}";
            bool now = GUI.Toggle(tr, on, label, tg);
            if (now != on) { if (now) picked.Add(sq); else picked.Remove(sq); }
            UiKit.Tip(tr, $"{BarracksLabel(sq)}\nСостав: {string.Join(", ", parts)}\nЗдоровье: {hpPct}%\nУнесут добычи: до {carry}");
        }
        GUI.EndScrollView();

        // --- Снаряжение ---
        float by = c.yMax - bottomH;
        by = UiKit.Section(rx, by, rw, "Снаряжение в поход");
        DrawKitRows(rx, by, rw);
        by += kitH + 4f;

        // --- Припасы и выступление ---
        by = UiKit.Section(rx, by, rw, "Припасы");
        int n = PickedSoldiers();
        var rm = ResourceManager.Instance;
        int haveFood = rm != null ? rm.food : 0, haveWood = rm != null ? rm.wood : 0;
        int needFood = n * FoodPerSoldier;
        string fcol = haveFood >= needFood ? UiKit.GoodHex : UiKit.BadHex;
        Rect fr = new Rect(rx, by, rw, 22f);
        GUI.Label(fr, $"Бойцов: <b>{n}</b>    Еда: <color={fcol}><b>{needFood}</b></color> {UiKit.Col(UiKit.MutedHex, "(в казне " + haveFood + ")")}", line);
        UiKit.Tip(fr, $"Еда — запас похода: {FoodPerSoldier} на бойца. Каждый боец ест раз в минуту, остаток вернётся домой. Без еды бойцы слабеют.\n" +
                      $"Добычу несут в мешках: пеший — {CarryFoot}, конный — {CarryMounted}.");
        by += 24f;

        string wcol = haveWood >= CampWood ? UiKit.GoodHex : UiKit.BadHex;
        Rect cr = new Rect(rx, by, rw, 22f);
        takeCamp = GUI.Toggle(cr, takeCamp, $" Лагерь у высадки: <color={wcol}>{CampWood} дер</color>", new GUIStyle(GUI.skin.toggle) { richText = true });
        UiKit.Tip(cr, "Лагерь лечит бойцов рядом и даёт укрытие. Дерево тратится.");
        by += 24f;

        // Голем Алтаря жизни (школа Магии жизни)
        if (golemRow)
        {
            string gp = LifeAltar.RaidProblem();
            LifeAltar alt = LifeAltar.Current;
            Rect gr = new Rect(rx, by, rw, 22f);
            if (gp == "")
            {
                int ghp = alt != null && alt.GolemMaxHp > 0f ? Mathf.RoundToInt(alt.GolemHp / alt.GolemMaxHp * 100f) : 0;
                takeGolem = GUI.Toggle(gr, takeGolem, $" Взять голема  <color={HpHex(ghp)}>{ghp}%</color>", new GUIStyle(GUI.skin.toggle) { richText = true });
                UiKit.Tip(gr, $"Голем Алтаря жизни идёт с армией: {(alt != null ? alt.StatsLine : "")}. Не ест, добычу не носит.\n" +
                              "Раны остаются с ним. Погибнет в набеге — погибнет и дома. Пока он в походе, алтарь без охраны.");
            }
            else
            {
                GUI.Label(gr, UiKit.Col(UiKit.MutedHex, $" Голем: {gp.ToLower()}"), line);
                UiKit.Tip(gr, "Голема растят на Алтаре жизни.");
            }
            by += 24f;
        }

        string problem = LaunchProblem();
        GUI.Label(new Rect(rx, by, rw, 20f), problem == "" ? "" : UiKit.Col(UiKit.BadHex, problem), smallLine);
        by += 22f;
        if (BigButton(new Rect(rx, by, rw, 34f), problem == "" ? $"Выступить на {targets[pickTarget].Name}" : "Выступить", problem == "", UiKit.Kind.Primary,
            problem == "" ? "Армия уйдёт с края карты; дома её не будет до возвращения" : problem)) Launch();
    }

    static string FormatTime(float s)
    {
        int t = Mathf.Max(0, Mathf.CeilToInt(s));
        return $"{t / 60}:{t % 60:00}";
    }

    // ---------- Панель боя ----------

    void DrawBattleHud()
    {
        float x = 10f, y = 45f, w = 300f;
        Rect box = new Rect(x, y, w, hudHeight);
        Publish(box);

        var att = Attackers();
        int towersRuined = 0;
        Loot inBags = new Loot();
        foreach (var b in RaidBuilding.All) if (b != null && b.IsDefense && b.IsRuined) towersRuined++;
        foreach (var u in att) inBags.Add(u.Carried);
        foreach (var kv in survivors) inBags.Add(kv.Value);

        string phase = State == Phase.Scouting ? UiKit.Col(UiKit.WarnHex, "разведка")
                     : (State == Phase.Battle ? UiKit.Col(UiKit.BadHex, "бой") : UiKit.Col(UiKit.WarnHex, "отступление"));
        Rect c = UiKit.Window(box, $"{current.Name} · {phase}");
        float cx = c.x, cw = c.width, cy = c.y;
        GUIStyle line = OneLine(UiKit.Body), small = UiKit.Small;

        // Главные числа
        int eatersN = 0;
        foreach (var u in att) if (!u.IsGolem) eatersN++;
        float minutes = eatersN > 0 ? food / (float)eatersN * EatInterval / 60f : 0f;
        string foodText = food > 0 ? $"еда ~{minutes:F0} мин" : UiKit.Col(UiKit.BadHex, "еды нет!");
        Rect nr = new Rect(cx, cy, cw, 20f);
        int soldiersOn = 0;
        RaidUnit golemOn = null;
        foreach (var u in att) { if (u.IsGolem) golemOn = u; else soldiersOn++; }
        string golemText = golemOn != null ? $"  {UiKit.Col(UiKit.GoodHex, "+голем")} <color={HpHex(Mathf.RoundToInt(golemOn.HpFraction * 100f))}>{Mathf.RoundToInt(golemOn.HpFraction * 100f)}%</color>" : "";
        GUI.Label(nr, $"Бойцы <b>{soldiersOn}</b>/{startCount}{golemText}   убито <b>{kills}</b>   {foodText}", line);
        UiKit.Tip(nr, $"Бойцов на поле: {soldiersOn} из {startCount}" + (golemOn != null ? $"\nГолем: {Mathf.CeilToInt(golemOn.Hp)}/{Mathf.CeilToInt(golemOn.MaxHp)}" : "") + $"\nПогибло: {lost}, ушли с поля: {survivors.Count}\n" +
                      $"Убито защитников: {kills}, башен разрушено: {towersRuined}\nЕда в запасе: {food}" + (food > 0 ? "" : " — бойцы слабеют"));
        cy += 22f;

        string loot = $"Добыча: {UiKit.Col(UiKit.GoldHex, inBags.ShortText)}";
        float lh = UiKit.TextH(loot, cw, small);
        GUI.Label(new Rect(cx, cy, cw, lh), loot, small);
        cy += lh + 2f;

        RaidDefense def = RaidDefense.Instance;
        if (def != null && !string.IsNullOrEmpty(def.Status))
        {
            string en = $"{UiKit.Col(UiKit.MutedHex, "Противник:")} {def.Status}";
            float eh = UiKit.TextH(en, cw, small);
            GUI.Label(new Rect(cx, cy, cw, eh), en, small);
            cy += eh + 2f;
        }

        ScoutReport myMap = ReportFor(current);
        if (myMap != null)
        {
            if (UiKit.Button(new Rect(cx, cy + 2f, cw, 24f), mapShown == null ? "Карта разведчика" : "Скрыть карту", UiKit.Kind.Ghost,
                $"Набросок плана, точность ~{myMap.ShownAccuracy}%")) mapShown = mapShown == null ? myMap : null;
            cy += 28f;
        }
        cy += 4f;

        if (State == Phase.Scouting)
        {
            string st = def != null ? def.ScoutingText : UiKit.Col(UiKit.MutedHex, "Защитники пока вас не заметили. Смотрите карту и решайте.");
            float sh = UiKit.TextH(st, cw, small);
            GUI.Label(new Rect(cx, cy, cw, sh), st, small);
            cy += sh + 4f;
            float bw = (cw - 4f) / 2f;
            if (BigButton(new Rect(cx, cy, bw, 32f), "Атаковать!", true, UiKit.Kind.Primary, "Начать штурм деревни")) StartAttack();
            if (BigButton(new Rect(cx + bw + 4f, cy, bw, 32f), "Уйти домой", true, UiKit.Kind.Normal, "Уйти без боя через западный край")) RetreatAll();
            cy += 36f;
        }
        else
        {
            if (State == Phase.Battle)
            {
                bool won = hallSacked || resistanceBroken;
                if (BigButton(new Rect(cx, cy, cw, 32f), won ? "Уходить с добычей!" : "Отступить всем", true, won ? UiKit.Kind.Primary : UiKit.Kind.Danger,
                    "Все бойцы уходят через западный край карты; погибшие теряют свой мешок")) RetreatAll();
                cy += 36f;

                // Приказ выбранным отрядам (ничего не выбрано - всем)
                float bw = (cw - 4f) / 2f;
                if (UiKit.Button(new Rect(cx, cy, bw, 26f), "Штурм", UiKit.Kind.Normal, "Выбранные (или все) штурмуют сами: бьют защитников, ломают здания"))
                    foreach (var u in SelectedAttackers()) u.OrderAuto();
                if (UiKit.Button(new Rect(cx + bw + 4f, cy, bw, 26f), "Отступить выбр.", UiKit.Kind.Normal, "Отступить только выбранным отрядам"))
                    RetreatUnits(SelectedAttackers());
                cy += 30f;
            }
            else
            {
                GUI.Label(new Rect(cx, cy, cw, 20f), UiKit.Col(UiKit.WarnHex, $"Отступают — на поле ещё {att.Count}"), line);
                cy += 22f;
            }

            // Отряды - свёрнуто: выбор, кому отдавать приказы
            string selText = selected.Count == 0 ? "все" : selected.Count.ToString();
            if (UiKit.Fold(new Rect(cx, cy, cw, UiKit.FoldH), "raid.squads", $"Отряды (выбрано: {selText})"))
            {
                cy += UiKit.FoldH;
                GUIStyle tg = new GUIStyle(GUI.skin.toggle) { fontSize = 12, wordWrap = false };
                foreach (var sq in squads)
                {
                    int alive = 0;
                    float hpSum = 0f;
                    foreach (var u in att) if (u.Squad == sq) { alive++; hpSum += u.HpFraction; }
                    if (alive == 0) continue;
                    bool on = selected.Contains(sq);
                    int hp = Mathf.RoundToInt(hpSum / alive * 100f);
                    string lbl = $" {sq.Name}: {alive} бойц., <color={HpHex(hp)}>{hp}%</color>";
                    bool now = GUI.Toggle(new Rect(cx + 8f, cy, cw - 8f, 20f), on, lbl, tg);
                    if (now != on) { if (now) selected.Add(sq); else selected.Remove(sq); }
                    cy += 21f;
                }
                if (UiKit.Button(new Rect(cx + 8f, cy + 2f, 90f, 22f), "Все", UiKit.Kind.Ghost, "Сбросить выбор — приказы получат все отряды")) selected.Clear();
                cy += 28f;
            }
            else cy += UiKit.FoldH + 2f;

            if (State == Phase.Battle)
            {
                Rect hr = new Rect(cx, cy, cw, 18f);
                GUI.Label(hr, UiKit.Col(UiKit.MutedHex, "? Управление мышью"), OneLine(UiKit.Small));
                UiKit.Tip(hr,
                    "ЛКМ по земле — идти и держать точку\nЛКМ по зданию — ломать его\n" +
                    "ЛКМ по своему бойцу — выбрать отряд (Shift — добавить)\nV — деревня / битва, Esc — сброс выбора");
                cy += 20f;
            }
        }

        hudHeight = cy - y + UiKit.Pad * 0.6f + 2f;
    }

    void DrawTooltip()
    {
        string text = null;
        if (hoverUnit != null && hoverUnit.Alive)
        {
            string side = hoverUnit.Attacker ? UiKit.Col(UiKit.InfoHex, "наш") : UiKit.Col(UiKit.BadHex, "защитник");
            string carry = hoverUnit.Attacker && !hoverUnit.Carried.IsEmpty ? $"\nнесёт: {UiKit.Col(UiKit.GoldHex, hoverUnit.Carried.ShortText)} ({hoverUnit.Carried.Total}/{hoverUnit.CarryCap})" : "";
            text = $"<b>{hoverUnit.Name}</b> ({side})\nздоровье {Mathf.CeilToInt(hoverUnit.Hp)}/{Mathf.CeilToInt(hoverUnit.MaxHp)}, урон {hoverUnit.DamagePreview:F0}" +
                   (hoverUnit.Attacker ? $"\n{hoverUnit.StatusText}" : "") + carry;
        }
        else if (hoverBuilding != null) text = hoverBuilding.TooltipText;
        if (text == null || Mouse.current == null) return;

        Vector2 m = Mouse.current.position.ReadValue();
        GUIStyle st = Style(12);
        float w = 280f;
        float h = st.CalcHeight(new GUIContent(text), w - 12f) + 10f;
        Rect r = new Rect(Mathf.Min(m.x + 18f, Screen.width - w - 4f), Mathf.Min(Screen.height - m.y + 18f, Screen.height - h - 4f), w, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 6f, r.y + 5f, w - 12f, h - 10f), text, st);
    }

    void DrawBars()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        float ppu = Screen.height / (2f * cam.orthographicSize);

        foreach (var b in RaidBuilding.All)
        {
            if (b == null || b.IsRuined || b.Hp >= b.MaxHp - 0.5f) continue;
            if (!FogOfWar.IsVisible(b.Center)) continue;
            float f = b.Hp / b.MaxHp;
            Bar(cam, b.Center + Vector2.up * (b.SizeCells.y * 0.5f + 0.2f), Mathf.Clamp(ppu * b.SizeCells.x * 0.8f, 16f, 90f), f,
                Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.35f, 0.9f, 0.35f), f));
        }
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Alive) continue;
            if (u.Hp >= u.MaxHp - 0.01f && !u.InCombat) continue;
            if (!u.Attacker && !FogOfWar.IsVisible(u.Position)) continue;
            Color col = u.Attacker ? new Color(0.3f, 0.9f, 0.3f) : new Color(1f, 0.2f, 0.2f);
            Bar(cam, u.Position + Vector2.up * 0.55f, Mathf.Clamp(ppu * 0.7f, 14f, 60f), u.HpFraction, col);
        }
    }

    static void Bar(Camera cam, Vector2 world, float w, float frac, Color fill)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;
        float h = Mathf.Clamp(w * 0.12f, 3f, 7f);
        Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y - h / 2f, w, h);
        if (r.xMax < 0f || r.x > Screen.width || r.yMax < 0f || r.y > Screen.height) return;
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    // Жёлтые стрелки над выбранными бойцами
    void DrawMarkers()
    {
        if (selected.Count == 0) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Attacker || !u.Alive || u.Squad == null || !selected.Contains(u.Squad)) continue;
            Vector3 sp = cam.WorldToScreenPoint(u.Position + Vector2.up * 0.75f);
            if (sp.z < 0f) continue;
            Rect rc = new Rect(sp.x - 12f, Screen.height - sp.y - 12f, 24f, 24f);
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), "▼", st);
            st.normal.textColor = Color.yellow;
            GUI.Label(rc, "▼", st);
        }
    }

    void DrawReport()
    {
        float w = 440f;
        float th = UiKit.TextH(report, w - UiKit.Pad * 2f);
        float h = UiKit.WindowH(th + 8f + 30f);
        Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        Publish(r);
        Rect c = UiKit.Window(r, "Итоги набега", out bool close);
        if (close) { report = null; return; }
        GUI.Label(new Rect(c.x, c.y, c.width, th), report, UiKit.Body);
        if (UiKit.Button(new Rect(r.x + w / 2f - 60f, c.y + th + 8f, 120f, 30f), "Хорошо", UiKit.Kind.Primary)) report = null;
    }

    // =====================================================================
    // Разведка
    // =====================================================================

    static List<CitizenAgent> FreeScouts()
    {
        var list = new List<CitizenAgent>();
        foreach (var a in CitizenAgent.All)
            if (a != null && !a.IsDead && a.IsScout && !a.IsOnExpedition) list.Add(a);
        return list;
    }

    ScoutMission MissionFor(RaidTarget t)
    {
        foreach (var m in missions) if (m.target == t) return m;
        return null;
    }

    /// <summary>Карта разведчика по деревне (если она ещё верна - деревня не отстроилась заново).</summary>
    public ScoutReport ReportFor(RaidTarget t)
    {
        if (t == null || !reports.TryGetValue(t, out ScoutReport r)) return null;
        if (r.Valid) return r;
        reports.Remove(t);
        return null;
    }

    static string MissionText(ScoutMission m)
    {
        return m.away ? $"вернётся через {Mathf.CeilToInt(m.timer)} с" : "идёт к краю карты";
    }

    void StartScout(RaidTarget t)
    {
        if (t == null || !t.Ready || MissionFor(t) != null) return;
        var scouts = FreeScouts();
        if (scouts.Count == 0) { WarManager.Notify("Нет свободного разведчика (обучается в Казарме ур. 2)", false); return; }

        var rm = ResourceManager.Instance;
        int cost = ScoutCost(t);
        if (rm == null || !rm.Spend(cost, 0, 0, 0)) { WarManager.Notify($"Разведка стоит {cost} золота — не хватает", false); return; }

        // Ближайший к краю карты
        CitizenAgent best = scouts[0];
        Vector2 edge = HomeEdgePoint(best.transform.position);
        float bestD = Vector2.Distance(best.transform.position, edge);
        foreach (var s in scouts)
        {
            Vector2 e = HomeEdgePoint(s.transform.position);
            float d = Vector2.Distance(s.transform.position, e);
            if (d < bestD) { bestD = d; best = s; edge = e; }
        }

        if (!best.BeginExpedition(JitterHome(edge)))
        {
            rm.Add(cost, 0, 0, 0);
            return;
        }
        missions.Add(new ScoutMission { agent = best, target = t, edge = edge });
        WarManager.Notify($"Разведчик {best.CitizenName} отправился к {t.Name}", true);
    }

    void ScoutTick(float dt)
    {
        for (int i = missions.Count - 1; i >= 0; i--)
        {
            ScoutMission m = missions[i];
            if (m.agent == null || m.agent.IsDead)
            {
                WarManager.Notify($"Разведка {m.target.Name} сорвана: разведчика больше нет", false);
                missions.RemoveAt(i);
                continue;
            }

            if (!m.away)
            {
                m.timer += dt;
                if (m.agent.IsAway)
                {
                    m.away = true;
                    m.timer = m.target.TravelSeconds + ScoutSeconds;   // туда (полдороги), осмотр, обратно (полдороги)
                }
                else if (m.timer > MarchTimeout) m.agent.ForceAway();
                continue;
            }

            m.timer -= dt;
            if (m.timer > 0f) continue;

            m.agent.ReturnFromExpedition(JitterHome(m.edge), null);
            ScoutReport rep = ScoutReport.Make(m.target);
            reports[m.target] = rep;
            missions.RemoveAt(i);
            WarManager.Notify($"Разведчик вернулся с картой {m.target.Name} (точность ~{rep.ShownAccuracy}%) — смотрите в окне «В бой!»", true);
        }
    }

    void DrawMapWindow()
    {
        ScoutReport rep = mapShown;
        if (rep == null || !rep.Valid) { mapShown = null; return; }

        float side = Mathf.Min(Screen.height - 150f, 560f, Screen.width - 60f);
        side = Mathf.Max(220f, side);
        float w = side + UiKit.Pad * 2f, h = UiKit.WindowH(24f + side + 26f);
        Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        Publish(r);
        CameraController.BlockZoom();
        Rect c = UiKit.Window(r, $"Карта разведчика: {rep.Target.Name}", out bool close);
        if (close) { mapShown = null; return; }

        GUIStyle line = OneLine(UiKit.Small);
        Rect sr = new Rect(c.x, c.y, side, 20f);
        GUI.Label(sr, UiKit.Col(UiKit.AccentHex, $"Точность ~{rep.ShownAccuracy}%") + UiKit.Col(UiKit.MutedHex, " · ") + rep.ShortSummary, line);
        UiKit.Tip(sr, $"Набросок от руки: что-то сдвинуто, пропущено или выдумано. Точность называет сам разведчик (~{rep.ShownAccuracy}%).\n\n{rep.SummaryText}");

        Rect view = new Rect(c.x, c.y + 24f, side, side);
        DrawZoomableMap(rep, view);

        Rect lr = new Rect(c.x, view.yMax + 4f, 115f, 20f);
        GUI.Label(lr, UiKit.Col(UiKit.InfoHex, "? Обозначения"), line);
        UiKit.Tip(lr, ScoutReport.Legend);
        GUI.Label(new Rect(c.x + 120f, view.yMax + 4f, side - 120f, 20f), "колесо — приблизить, тянуть — двигать",
            new GUIStyle(OneLine(UiKit.MutedStyle)) { alignment = TextAnchor.UpperRight });
    }

    // ---------- Приближение карты разведчика ----------

    float mapZoom = 1f;
    Vector2 mapPan = new Vector2(0.5f, 0.5f);   // центр окна в долях карты (0..1, y - сверху вниз)
    ScoutReport mapZoomFor;

    void FocusMapOnVillage(ScoutReport rep, float zoom)
    {
        float half = rep.MapSize / 2f;
        mapZoom = zoom;
        mapPan = new Vector2((rep.VillageCenterGuess.x + half) / rep.MapSize, (half - rep.VillageCenterGuess.y) / rep.MapSize);
    }

    void DrawZoomableMap(ScoutReport rep, Rect view)
    {
        // Новая карта - сразу приближаем к деревне (на большой карте иначе ничего не разглядеть)
        if (mapZoomFor != rep)
        {
            mapZoomFor = rep;
            FocusMapOnVillage(rep, Mathf.Clamp(rep.MapSize / 34f, 1f, 5f));
        }

        Event e = Event.current;
        if (view.Contains(e.mousePosition))
        {
            if (e.type == EventType.ScrollWheel)
            {
                // Приближаем к точке под курсором
                float vs0 = view.width * mapZoom;
                Vector2 mouseFrac = mapPan + (e.mousePosition - view.center) / vs0;
                float newZoom = Mathf.Clamp(mapZoom * (e.delta.y > 0f ? 0.85f : 1.18f), 1f, 8f);
                float vs1 = view.width * newZoom;
                mapPan = mouseFrac - (e.mousePosition - view.center) / vs1;
                mapZoom = newZoom;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag)
            {
                mapPan -= e.delta / (view.width * mapZoom);
                e.Use();
            }
        }

        // Карта не уезжает за край окна
        float halfView = 0.5f / mapZoom;
        mapPan.x = Mathf.Clamp(mapPan.x, halfView, 1f - halfView);
        mapPan.y = Mathf.Clamp(mapPan.y, halfView, 1f - halfView);

        float vs = view.width * mapZoom;
        float vx = view.width / 2f - mapPan.x * vs;
        float vy = view.height / 2f - mapPan.y * vs;
        GUI.BeginGroup(view);
        rep.Draw(new Rect(vx, vy, vs, vs));
        GUI.EndGroup();

        // Кнопки масштаба поверх карты
        float bx = view.xMax - 30f, by = view.y + 6f;
        if (GUI.Button(new Rect(bx, by, 24f, 24f), "+")) mapZoom = Mathf.Min(8f, mapZoom * 1.4f);
        if (GUI.Button(new Rect(bx, by + 28f, 24f, 24f), "−")) mapZoom = Mathf.Max(1f, mapZoom / 1.4f);
        if (GUI.Button(new Rect(view.x + 6f, view.yMax - 28f, 90f, 22f), "К деревне")) FocusMapOnVillage(rep, Mathf.Max(mapZoom, 2f));
        if (GUI.Button(new Rect(view.x + 100f, view.yMax - 28f, 90f, 22f), "Вся карта")) { mapZoom = 1f; mapPan = new Vector2(0.5f, 0.5f); }

        GUIStyle hint = Style(11);
        hint.normal.textColor = new Color(0.25f, 0.18f, 0.1f);
        GUI.Label(new Rect(view.x + 196f, view.yMax - 26f, 60f, 20f), $"×{mapZoom:F1}", hint);
    }
}