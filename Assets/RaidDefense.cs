using UnityEngine;
using System.Collections.Generic;

/// <summary>Роль защитника в обороне деревни-соперника.</summary>
public enum DefRole
{
    Garrison,   // гарнизон: стоит у Ратуши
    Guard,      // пехота у проходов, основная сила контратаки
    Sentry,     // часовой снаружи: первым замечает чужую армию
    Archer,     // стрелок: держится за пехотой и за заслонами
    Rider,      // конница: бьёт по стрелкам, лекарям и тем, кто несёт добычу
    Healer      // маг-целитель: идёт за пехотой
}

/// <summary>
/// Командир обороны деревни-соперника на карте набега. Играет за соседа "как человек":
///  - До боя: часовые и посты присматриваются. Если наша армия долго стоит рядом с ними - тревога:
///    защитники занимают позиции против неё, ставят заслоны на подходах. Воинственный сосед,
///    если сильнее, сам бьёт первым - нельзя стоять у его ворот вечно.
///  - В бою: делит нашу армию на группы, находит главную угрозу и сравнивает силы.
///    Хватает сил - контратакует ей навстречу (пехота впереди, стрелки за ней, лекари сзади).
///    Не хватает - держит оборону у проходов и Ратуши, под башнями и за заслонами.
///    Совсем мало - последний рубеж у Ратуши.
///  - Конница не лезет в общую свалку: охотится на наших стрелков, лекарей и тех, кто несёт добычу.
///  - Раненые (меньше 30%) уходят к Ратуше, она их лечит; вылечились - снова в бой.
///  - Наших бойцов в плаще из листвы часовые замечают вдвое ближе (разведчика - лишь вплотную), а командир
///    не знает о тех, кого никто из защитников не видит и кто не в бою (RaidUnit.NoticeRange).
///  - По ходу боя из целых казарм выходят подкрепления (войско, которое не уместилось на поле),
///    а если армия соседа была в походе на нас - она спешит домой и появляется с востока.
/// Создаёт BotVillage.Build, удаляет RaidManager вместе с картой.
/// </summary>
public class RaidDefense : MonoBehaviour
{
    public static RaidDefense Instance { get; private set; }

    // ---------- Числа (можно менять) ----------
    const float HallHealRadius = 4.5f, HallHealPerSecond = 5f;
    const float BarricadeBuildSeconds = 8f;
    const float ReinforceEvery = 30f;            // одна казарма выпускает бойца раз в столько секунд боя
    const float SuspicionToAlarm = 1.5f;
    const float WoundedBelow = 0.3f, HealedAbove = 0.75f;
    const float KnowRange = 10f;                 // командир знает о бойце в плаще, если кто-то из своих видит его с такого расстояния

    public RaidTarget Target { get; private set; }
    BotVillage.Plan plan;
    readonly List<BotVillage.BarricadeSpot> spots = new List<BotVillage.BarricadeSpot>();
    readonly List<string> reserve = new List<string>();
    struct Returning { public string name; public float at; }
    readonly List<Returning> returning = new List<Returning>();
    readonly HashSet<RaidUnit> wounded = new HashSet<RaidUnit>();

    int buildBudget;          // сколько заслонов ещё могут поставить (запас брёвен)
    float reserveTimer;
    bool reinforceSaid, buildSaid;

    public bool Alerted { get; private set; }
    float suspicion, alertedAt, thinkTimer;
    bool struck;
    string status = "живут обычной жизнью";
    public string Status => status;

    RaidUnit builder;
    BotVillage.BarricadeSpot buildSpot;
    float buildWait, builderSince;

    RaidBuilding hall;
    Vector2 hallPos;

    public static RaidDefense Create(RaidTarget t, BotVillage.Plan p)
    {
        if (Instance != null) Destroy(Instance.gameObject);
        GameObject go = new GameObject("RaidDefense");
        RaidDefense d = go.AddComponent<RaidDefense>();
        Instance = d;
        d.Target = t;
        d.plan = p;
        d.spots.AddRange(p.Spots);
        d.reserve.AddRange(p.Reserve);
        int lvl = Mathf.Clamp(p.Level, 1, 3);
        d.buildBudget = 1 + lvl + (t != null && t.Trait == RivalTrait.Cautious ? 2 : 0) + (t != null && t.Grudge > 50f ? 1 : 0);
        // Недавно на них нападали - часовые настороже
        d.suspicion = t != null ? Mathf.Clamp01(t.Grudge / 100f) * 0.8f : 0f;
        return d;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>Боец соседа вернулся из похода на нас, пока у них идёт бой: появится с востока через delay.</summary>
    public void QueueReturning(string unitName, float delay)
    {
        returning.Add(new Returning { name = unitName, at = Time.time + delay });
    }

    // ---------- Тексты для интерфейса набега ----------

    public string ScoutingText
    {
        get
        {
            if (Alerted) return "<color=#FF8060><b>Вас заметили!</b></color> Защитники занимают позиции и ставят заслоны. Чем дольше ждёте — тем крепче оборона.";
            if (suspicion > 0.5f) return "<color=#FFD060>Часовые что-то заподозрили...</color> Решайте быстрее: атаковать или уйти.";
            return "<color=#A0A0A0>Защитники пока вас не заметили. Не подходите к часовым. Смотрите карту разведчика (если есть) и решайте.</color>";
        }
    }

    // ---------- Главный цикл ----------

    void Update()
    {
        RaidManager rm = RaidManager.Instance;
        RaidMap map = RaidMap.Instance;
        if (rm == null || map == null) return;
        float dt = Time.deltaTime;

        hall = FindHall();
        if (hall != null) hallPos = hall.Center;

        HealAtHall(dt);
        TickBuilder(map, dt);
        TickReinforcements(rm, map, dt);

        thinkTimer -= dt;
        if (thinkTimer > 0f) return;
        thinkTimer = 0.5f;

        if (rm.BattleOn) { Command(); return; }
        if (rm.State == RaidManager.Phase.Scouting) Watch(rm, 0.5f);
    }

    static RaidBuilding FindHall()
    {
        foreach (var b in RaidBuilding.All) if (b != null && b.IsHall) return b;
        return null;
    }

    static List<RaidUnit> Side(bool attackers)
    {
        var list = new List<RaidUnit>();
        foreach (var u in RaidUnit.All) if (u != null && u.Attacker == attackers && u.Alive) list.Add(u);
        return list;
    }

    static float PowerOf(List<RaidUnit> list)
    {
        float s = 0f;
        foreach (var u in list) s += u.Power;
        return s;
    }

    void HealAtHall(float dt)
    {
        if (hall == null || hall.IsRuined) return;
        foreach (var u in RaidUnit.All)
        {
            if (u == null || u.Attacker || !u.Alive || u.InCombat || u.Hp >= u.MaxHp) continue;
            if (hall.DistanceTo(u.Position) <= HallHealRadius) u.Heal(HallHealPerSecond * dt);
        }
    }

    // ---------- До боя: часовые ----------

    void Watch(RaidManager rm, float step)
    {
        var att = Side(true);
        var defs = Side(false);
        if (att.Count == 0 || defs.Count == 0) return;

        float rate = 0f;
        Vector2 seen = Vector2.zero;
        int seenN = 0;
        foreach (var a in att)
        {
            float best = 0f;
            foreach (var d in defs)
            {
                // Боец в плаще заметен вдвое ближе, разведчик в плаще - лишь вплотную
                float sight = a.NoticeRange(d.Role == DefRole.Sentry ? 9f : 6.5f);
                float dist = Vector2.Distance(a.Position, d.Position);
                if (sight <= 0f || dist >= sight) continue;
                float k = (1f - dist / sight * 0.6f) * (d.Role == DefRole.Sentry ? 1.4f : 1f);
                if (k > best) best = k;
            }
            if (best <= 0f) continue;
            rate += best * (a.IsScout ? 0.25f : 1f);
            seen += a.Position;
            seenN++;
        }

        if (seenN > 0) suspicion += Mathf.Min(rate, 2.5f) * 0.35f * step;
        else if (!Alerted) suspicion = Mathf.Max(0f, suspicion - 0.05f * step);

        Vector2 threat = seenN > 0 ? seen / seenN : Centroid(att);
        if (!Alerted && suspicion >= SuspicionToAlarm)
        {
            Alerted = true;
            alertedAt = Time.time;
            WarManager.Notify($"Вас заметили! {Target.Name} поднимает тревогу и готовит оборону", false);
            RivalManager.SetActivity(Target, "отбивается от вашей армии");
        }
        if (!Alerted) { status = suspicion > 0.5f ? "часовые насторожились" : "живут обычной жизнью"; return; }

        Prepare(threat, defs);
        status = "заняли позиции и ставят заслоны";

        // Нельзя стоять у ворот вечно: сильный и злой сосед бьёт первым
        if (!struck && Time.time - alertedAt > 30f && Target != null)
        {
            float mine = PowerOf(defs), theirs = PowerOf(att);
            float need = Target.Trait == RivalTrait.Warlike ? 0.9f : (Target.Trait == RivalTrait.Calculating ? 1.25f : 99f);
            if (mine >= theirs * need)
            {
                struck = true;
                rm.DefendersStrikeFirst();
            }
        }
    }

    // Наши бойцы, о которых знает командир: не скрыты плащом, в бою или кто-то из защитников их видит
    static List<RaidUnit> Known(List<RaidUnit> att, List<RaidUnit> defs)
    {
        var res = new List<RaidUnit>();
        foreach (var a in att)
        {
            if (!a.IsConcealed || a.InCombat) { res.Add(a); continue; }
            foreach (var d in defs)
                if (a.NoticedFrom(d.Position, KnowRange)) { res.Add(a); break; }
        }
        return res;
    }

    static Vector2 Centroid(List<RaidUnit> list)
    {
        if (list.Count == 0) return Vector2.zero;
        Vector2 c = Vector2.zero;
        foreach (var u in list) c += u.Position;
        return c / list.Count;
    }

    // Проход в стене, ближайший к угрозе (в мировых координатах); без стен - точка на окраине со стороны угрозы
    Vector2 FrontToward(Vector2 threat)
    {
        Vector2 dir = (threat - hallPos);
        float dist = dir.magnitude;
        dir = dist > 0.01f ? dir / dist : Vector2.left;
        if (plan != null && plan.Gaps.Count > 0)
        {
            Vector2 best = hallPos;
            float bestD = float.MaxValue;
            foreach (var g in plan.Gaps)
            {
                Vector2 w = RaidMap.Origin + g;
                float d = Vector2.Distance(w, threat);
                if (d < bestD) { bestD = d; best = w; }
            }
            return best + (hallPos - best).normalized * 1.8f;
        }
        return hallPos + dir * Mathf.Clamp(dist - 2f, 2.5f, 6f);
    }

    // Расставить всех против угрозы (до боя и в обороне)
    void Prepare(Vector2 threat, List<RaidUnit> defs)
    {
        Vector2 front = FrontToward(threat);
        Vector2 dir = (threat - front).normalized;
        if (dir.sqrMagnitude < 0.01f) dir = Vector2.left;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        int gi = 0, ai = 0, ri = 0, hi = 0, ci = 0;
        foreach (var d in defs)
        {
            switch (d.Role)
            {
                case DefRole.Guard:
                case DefRole.Sentry:
                    d.Command(front + Slot(perp, -dir, gi++), 5f, 7f);
                    break;
                case DefRole.Archer:
                    d.Command(ArcherSpot(front, dir) + Slot(perp, -dir, ai++), 6f, 8f);
                    break;
                case DefRole.Rider:
                    d.Command(hallPos + perp * (3.5f + ri++ * 0.8f), 7f, 8f);
                    break;
                case DefRole.Healer:
                    d.Command(front - dir * 2.5f + perp * (hi++ - 0.5f), 4f, 5f);
                    break;
                default:
                    d.Command(hallPos + Ring(ci++, 2.6f), 6f, 7f);
                    break;
            }
        }
        if (builder == null && buildBudget > 0) AssignBuilder(threat, front, defs);
    }

    static Vector2 Slot(Vector2 perp, Vector2 back, int i)
    {
        int col = i % 5, row = i / 5;
        return perp * ((col - 2) * 0.9f) + back * (row * 0.9f);
    }

    static Vector2 Ring(int i, float r)
    {
        float a = i * 1.1f;
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
    }

    // Стрелкам - за целым заслоном рядом с фронтом (со своей стороны), иначе за пехотой
    Vector2 ArcherSpot(Vector2 front, Vector2 dir)
    {
        RaidBuilding best = null;
        float bestD = 7f;
        foreach (var b in RaidBuilding.All)
        {
            if (b == null || !b.IsBarricade || b.IsRuined) continue;
            float d = Vector2.Distance(b.Center, front);
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best != null) return best.Center - dir * 1.3f;
        return front - dir * 3f;
    }

    // ---------- Заслоны ----------

    void AssignBuilder(Vector2 threat, Vector2 front, List<RaidUnit> defs)
    {
        RaidMap map = RaidMap.Instance;
        if (map == null) return;

        BotVillage.BarricadeSpot pick = null;
        float bestScore = float.MaxValue;
        foreach (var s in spots)
        {
            Vector2 c = RaidMap.Origin + s.Center;
            if (RaidUnit.NearestOf(true, c, 5f) != null) continue;   // под носом у врага не строят
            if (!CellsFree(map, s)) continue;
            float score = Vector2.Distance(c, front) + Vector2.Distance(c, threat) * 0.3f;
            if (score < bestScore) { bestScore = score; pick = s; }
        }
        if (pick == null || bestScore > 22f) return;

        RaidUnit who = null;
        float wd = float.MaxValue;
        Vector2 pc = RaidMap.Origin + pick.Center;
        foreach (var d in defs)
        {
            if (d.Role != DefRole.Garrison && d.Role != DefRole.Guard) continue;
            if (d.InCombat || wounded.Contains(d)) continue;
            float dd = Vector2.Distance(d.Position, pc);
            if (dd < wd) { wd = dd; who = d; }
        }
        if (who == null) return;
        builder = who;
        buildSpot = pick;
        buildWait = 0f;
        builderSince = Time.time;
    }

    static bool CellsFree(RaidMap map, BotVillage.BarricadeSpot s)
    {
        for (int x = 0; x < s.size.x; x++)
            for (int y = 0; y < s.size.y; y++)
                if (map.IsBlocked(new Vector2Int(s.origin.x + x, s.origin.y + y))) return false;
        return true;
    }

    void TickBuilder(RaidMap map, float dt)
    {
        if (builder == null) return;
        if (!builder.Alive || buildSpot == null || Time.time - builderSince > 30f) { builder = null; buildSpot = null; return; }

        Vector2 c = RaidMap.Origin + buildSpot.Center;
        builder.Command(c, 1f, 2.5f);
        if (Vector2.Distance(builder.Position, c) > 1.8f) { buildWait = 0f; return; }
        if (RaidUnit.NearestOf(true, c, 3.5f) != null) { buildWait = 0f; return; }

        buildWait += dt;
        if (buildWait < 1f) return;

        spots.Remove(buildSpot);
        if (CellsFree(map, buildSpot))
        {
            int lvl = plan != null ? plan.Level : 1;
            RaidBuilding b = RaidBuilding.Create(RaidBuildingKind.Barricade, "Заслон", buildSpot.origin, buildSpot.size,
                BotVillage.BarricadeHp(lvl), BotVillage.BarricadeColor, BotVillage.PowerMul(lvl));
            b.StartRising(BarricadeBuildSeconds);
            buildBudget--;
            if (!buildSaid)
            {
                buildSaid = true;
                WarManager.Notify($"{Target.Name} ставит заслоны на подходах — пока строят, их легко разнести", false);
            }
        }
        builder = null;
        buildSpot = null;
    }

    // ---------- Подкрепления ----------

    void TickReinforcements(RaidManager rm, RaidMap map, float dt)
    {
        if (!rm.BattleOn) return;

        // Армия соседа вернулась из похода на нас - входит с востока
        for (int i = returning.Count - 1; i >= 0; i--)
        {
            if (Time.time < returning[i].at) continue;
            float half = RaidMap.Size / 2f;
            Vector2 at = RaidMap.Local(half - 1.5f, Mathf.Clamp(hallPos.y - RaidMap.Origin.y + Random.Range(-3f, 3f), -half + 2f, half - 2f));
            Spawn(returning[i].name, at, map, rm);
            returning.RemoveAt(i);
            if (!reinforceSaid || i == 0) WarManager.Notify($"Войско {Target.Name} вернулось из похода и вступает в бой с востока!", false);
            reinforceSaid = true;
        }

        if (reserve.Count == 0) return;
        var barracks = new List<RaidBuilding>();
        foreach (var b in RaidBuilding.All) if (b != null && b.Kind == RaidBuildingKind.Barracks && !b.IsRuined) barracks.Add(b);
        if (barracks.Count == 0) return;   // казармы разрушены - подкрепления не выйдут

        reserveTimer += dt * barracks.Count;
        if (reserveTimer < ReinforceEvery) return;
        reserveTimer = 0f;

        RaidBuilding from = barracks[Random.Range(0, barracks.Count)];
        Vector2 p = map.ApproachPoint(from, hallPos);
        string n = reserve[0];
        reserve.RemoveAt(0);
        Spawn(n, p, map, rm);
        if (!reinforceSaid)
        {
            reinforceSaid = true;
            WarManager.Notify($"Из казарм {Target.Name} выходят подкрепления — разрушьте казармы, чтобы их остановить", false);
        }
    }

    void Spawn(string unitName, Vector2 pos, RaidMap map, RaidManager rm)
    {
        UnitType u = BotVillage.FindUnit(unitName);
        if (u == null) return;
        int lvl = plan != null ? plan.Level : 1;
        Vector2Int cell = map.WorldToCell(pos);
        if (map.IsBlocked(cell)) pos = map.CellCenter(map.NearestFree(cell));
        RaidUnit ru = RaidUnit.SpawnDefender(u, pos, BotVillage.PowerMul(lvl), BotVillage.GearChance(lvl), lvl);
        ru.Role = u.IsHealer ? DefRole.Healer : (u.requiresHorse ? DefRole.Rider : (u.attackRange > 2f ? DefRole.Archer : DefRole.Guard));
        ru.Alert();
        rm.AddDefendersTotal(1);
    }

    // ---------- Бой: командир ----------

    class Cluster
    {
        public readonly List<RaidUnit> units = new List<RaidUnit>();
        public Vector2 center;
        public float power;
    }

    static List<Cluster> Clusters(List<RaidUnit> att)
    {
        var left = new List<RaidUnit>(att);
        var res = new List<Cluster>();
        while (left.Count > 0)
        {
            var c = new Cluster();
            Vector2 seed = left[0].Position;
            for (int i = left.Count - 1; i >= 0; i--)
                if (Vector2.Distance(left[i].Position, seed) <= 6f) { c.units.Add(left[i]); left.RemoveAt(i); }
            c.center = Centroid(c.units);
            c.power = PowerOf(c.units);
            res.Add(c);
        }
        return res;
    }

    void Command()
    {
        var all = Side(true);
        var defs = Side(false);
        if (defs.Count == 0) { status = "разбиты"; return; }
        if (all.Count == 0) { status = "ждут"; return; }
        var att = Known(all, defs);   // скрытых плащом командир не видит
        if (att.Count == 0) { status = "<color=#90D070>потеряли ваших бойцов из виду</color>"; return; }

        // Раненые - к Ратуше (пока она цела)
        bool canHeal = hall != null && !hall.IsRuined;
        wounded.RemoveWhere(u => u == null || !u.Alive || u.HpFraction >= HealedAbove || !canHeal);
        if (canHeal)
            foreach (var d in defs)
                if (d.HpFraction < WoundedBelow && d.Role != DefRole.Healer) wounded.Add(d);

        var clusters = Clusters(att);
        Cluster main = null;
        float bestThreat = -1f;
        foreach (var c in clusters)
        {
            float th = c.power / (1f + Vector2.Distance(c.center, hallPos) / 12f);
            if (th > bestThreat) { bestThreat = th; main = c; }
        }

        float mine = 0f;
        int fit = 0;
        foreach (var d in defs) if (!wounded.Contains(d)) { mine += d.Power; fit++; }

        bool lastStand = fit <= 3 || mine < main.power * 0.35f;
        bool intercept = !lastStand && mine >= main.power * 0.8f;
        Vector2 threat = main.center;
        Vector2 dir = threat - hallPos;
        float tDist = dir.magnitude;
        dir = tDist > 0.01f ? dir / tDist : Vector2.left;
        Vector2 perp = new Vector2(-dir.y, dir.x);

        // Фронт: навстречу угрозе (контратака) или у прохода / окраины (оборона), или у Ратуши
        Vector2 front;
        if (lastStand) front = hallPos + dir * 2.5f;
        else if (intercept) front = threat;
        else front = FrontToward(threat);

        // Мелкая группа отдельно ломает что-то в стороне - пошлём туда пару бойцов
        Cluster side = null;
        if (intercept)
            foreach (var c in clusters)
                if (c != main && c.power < mine * 0.25f && (side == null || c.power > side.power)) side = c;
        int sideSent = 0;

        int gi = 0, ai = 0, hi = 0, ci = 0, ri = 0;
        foreach (var d in defs)
        {
            if (d == builder) continue;
            if (wounded.Contains(d)) { d.Command(hallPos + Ring(ci++, 1.6f), 1.5f, 2.5f); continue; }

            switch (d.Role)
            {
                case DefRole.Guard:
                case DefRole.Sentry:
                    bool toSide = side != null && sideSent < 2 && (d.Role == DefRole.Sentry || gi > 4);
                    if (toSide)
                    {
                        d.Command(side.center, 6f, 8f);
                        sideSent++;
                        break;
                    }
                    d.Command(front + Slot(perp, -dir, gi++), intercept ? 8f : 5f, intercept ? 10f : 7f);
                    break;

                case DefRole.Archer:
                    d.Command((intercept ? front - dir * 3.5f : ArcherSpot(front, dir)) + Slot(perp, -dir, ai++), 6.5f, 8f);
                    break;

                case DefRole.Healer:
                    d.Command(front - dir * 2f + perp * ((hi++ % 3) - 1), 4f, 5f);
                    break;

                case DefRole.Rider:
                    RaidUnit prey = lastStand ? null : SoftTarget(att, main, d.Position);
                    if (prey != null)
                    {
                        d.Command(prey.Position, 6f, 7f);
                        d.CommandTarget(prey);
                    }
                    else d.Command(front + perp * (3.5f + (ri++ % 2) * -7f), 7f, 9f);
                    break;

                default:   // гарнизон: у Ратуши, пока враг не подошёл к ней
                    if (lastStand || tDist < 9f) d.Command(Vector2.Lerp(hallPos, threat, 0.45f) + Ring(ci++, 1.2f), 7f, 9f);
                    else d.Command(hallPos + Ring(ci++, 2.6f), 6f, 7f);
                    break;
            }
        }

        // Пока есть брёвна и время - ставят заслон между угрозой и Ратушей
        if (builder == null && buildBudget > 0 && !lastStand && tDist > 8f) AssignBuilder(threat, FrontToward(threat), defs);

        if (lastStand) status = "<color=#FF8060>последний рубеж у Ратуши</color>";
        else if (intercept) status = $"контратакуют вашу главную группу ({main.units.Count} бойц.)";
        else status = "держат оборону у проходов и под башнями";
        if (wounded.Count > 0) status += $", раненых у Ратуши: {wounded.Count}";
    }

    // Конница выбирает лёгкую добычу: стрелков, лекарей, гружёных добычей - кто отбился от своих
    static RaidUnit SoftTarget(List<RaidUnit> att, Cluster main, Vector2 from)
    {
        RaidUnit best = null;
        float bestScore = float.MaxValue;
        foreach (var a in att)
        {
            bool soft = a.IsRangedUnit || a.IsHealerUnit || !a.Carried.IsEmpty;
            if (!soft) continue;
            float score = Vector2.Distance(from, a.Position);
            if (main.units.Contains(a)) score += 6f;                    // в гуще своих - опасно
            if (!a.Carried.IsEmpty) score -= 3f;                        // отбить добычу
            if (score < bestScore) { bestScore = score; best = a; }
        }
        return bestScore < 22f ? best : null;
    }
}
