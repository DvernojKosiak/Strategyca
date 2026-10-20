using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Стратегия соседей-соперников. Каждый сосед живёт своей жизнью, как игрок:
///  - копит войско (каждый по своему вкусу: воинственные - рыцарей, хитрые - конницу и лучников),
///    богатеет и со временем разрастается (хутор → деревня → крепость, расстановка меняется);
///  - ставит заслоны у деревни (осторожные - больше), после нашего набега укрепляется ещё;
///  - помнит обиды (Grudge): набег на него, разграбленная Ратуша - он захочет мести;
///  - узнаёт о нас: шлёт лазутчиков, видит нашу армию в бою. Нападает, когда считает, что сильнее
///    (или когда наша армия ушла в поход - хитрые этим живут). Слабому - не нападёт, пока не наберётся сил;
///  - нападает войском (RivalAssault), а не толпой: дома всегда оставляет гарнизон;
///  - соседи воюют и между собой (по слухам): кто-то ослабнет - самое время по нему ударить.
/// Если мы напали на соседа, пока его войско у нас, - войско бросает всё и спешит домой.
/// Вместо прежних случайных набегов WarManager (те остались для проверки на F8).
/// Создаётся RaidManager (у него список соседей).
/// </summary>
public class RivalManager : MonoBehaviour
{
    public static RivalManager Instance { get; private set; }
    public static bool Active => Instance != null && Instance.isActiveAndEnabled;

    // ---------- Числа (можно менять) ----------
    public float firstMoveAfter = 600f;          // столько секунд с появления Ратуши соседи присматриваются (как раньше первый набег)
    const float WarnSeconds = 20f;               // за сколько предупреждают дозорные
    const float GapMin = 300f, GapMax = 480f;    // между нападениями на нас (любых соседей)
    const float RivalWarMin = 300f, RivalWarMax = 560f;
    const float IntelFresh = 600f;               // сведения о нас считаются свежими столько секунд
    const int SendMax = 16;

    readonly List<RaidTarget> rivals = new List<RaidTarget>();
    float clock, tickTimer, nextGlobalAt, nextRivalWar = 420f;
    int globalAttacks;

    RivalAssault assault, spy;
    RaidTarget pendingRival;
    Dictionary<string, int> pendingForce;
    int pendingSide;
    float pendingAt;
    readonly Dictionary<RaidTarget, float> activityLock = new Dictionary<RaidTarget, float>();
    readonly Dictionary<RaidTarget, float> fortTimer = new Dictionary<RaidTarget, float>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // ---------- Соседи ----------

    public void Init(List<RaidTarget> targets)
    {
        var traits = new List<RivalTrait> { RivalTrait.Warlike, RivalTrait.Cautious, RivalTrait.Cunning, RivalTrait.Calculating };
        for (int i = 0; i < traits.Count; i++) { int j = Random.Range(i, traits.Count); var tmp = traits[i]; traits[i] = traits[j]; traits[j] = tmp; }

        for (int i = 0; i < targets.Count; i++)
        {
            RaidTarget t = targets[i];
            if (rivals.Contains(t)) continue;
            t.Trait = traits[i % traits.Count];
            t.Roster.Clear();
            t.Away.Clear();
            StartRoster(t);
            t.Fortify = t.Trait == RivalTrait.Cautious ? 2 : Mathf.Max(0, t.Level - 1);
            t.Prosperity = 0f;
            t.Stamp++;
            rivals.Add(t);
        }
    }

    static void StartRoster(RaidTarget t)
    {
        void Put(string n, int c) { if (c > 0) t.AddUnit(n, c); }
        switch (Mathf.Clamp(t.Level, 1, 3))
        {
            case 1: Put("Мечник", 3); Put("Лучник", 2); break;
            case 2: Put("Мечник", 4); Put("Лучник", 3); Put("Рыцарь", 1); Put("Полевой маг", 1); break;
            default: Put("Мечник", 5); Put("Лучник", 4); Put("Рыцарь", 2); Put("Полевой маг", 1); Put("Всадник", 1); Put("Конный лучник", 1); break;
        }
        switch (t.Trait)
        {
            case RivalTrait.Warlike: Put(t.Level >= 2 ? "Рыцарь" : "Мечник", 1); break;
            case RivalTrait.Cunning: Put("Лучник", 1); if (t.Level >= 2) Put("Всадник", 1); break;
            case RivalTrait.Cautious: Put("Лучник", 1); Put("Мечник", 1); break;
        }
    }

    public static string TraitName(RivalTrait t)
    {
        switch (t)
        {
            case RivalTrait.Warlike: return "воинственные";
            case RivalTrait.Cautious: return "осторожные";
            case RivalTrait.Cunning: return "хитрые";
            default: return "расчётливые";
        }
    }

    public static string TraitHint(RivalTrait t)
    {
        switch (t)
        {
            case RivalTrait.Warlike: return "нападают часто и упорно, бьют по армии и Ратуше";
            case RivalTrait.Cautious: return "сидят за заслонами, нападают только с большим перевесом";
            case RivalTrait.Cunning: return "бьют, когда ваша армия в походе; грабят окраины и уходят";
            default: return "нападают, когда сила на их стороне";
        }
    }

    public static string SideName(int side)
    {
        switch (side)
        {
            case 0: return "западного";
            case 1: return "восточного";
            case 2: return "южного";
            default: return "северного";
        }
    }

    static string SideFrom(int side)
    {
        switch (side)
        {
            case 0: return "запада";
            case 1: return "востока";
            case 2: return "юга";
            default: return "севера";
        }
    }

    public static void SetActivity(RaidTarget t, string text, float lockSeconds = 60f)
    {
        if (t == null) return;
        t.Activity = text;
        if (Instance != null) Instance.activityLock[t] = Time.time + lockSeconds;
    }

    // ---------- Наша сила (как её видят соседи) ----------

    /// <summary>Сила нашего города: бойцы дома (с учётом ран), башни и пушки. includeAway - и те, кто в походе.</summary>
    public static float PlayerPower(bool includeAway = false)
    {
        float s = 0f;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || !a.IsSoldier) continue;
            if (!includeAway && a.IsOnExpedition) continue;
            s += BotVillage.UnitPower(a.ExpUnit) * Mathf.Max(0.3f, a.HpFraction);
        }
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null || b.IsRuined) continue;
            if (b.GetComponent<TowerController>() != null) s += 1.2f + 0.4f * b.level;
            else if (b.GetComponent<MagicCannonController>() != null) s += 2.5f;
            else if (b.GetComponent<GroundCannonController>() != null) s += 2.5f + 0.5f * b.level;
        }
        return s;
    }

    static bool PlayerArmyAway => RaidManager.Instance != null && RaidManager.Instance.ArmyAway;

    // ---------- Главный цикл ----------

    void Update()
    {
        var pm = PopulationManager.Instance;
        if (pm == null || pm.Hall == null || GridNavigation.Instance == null) return;
        float dt = Time.deltaTime;
        clock += dt;

        // Предупреждение отыграло - войско выходит
        if (pendingRival != null && Time.time >= pendingAt) LaunchPending();

        tickTimer -= dt;
        if (tickTimer > 0f) return;
        float step = 1f - tickTimer;
        tickTimer = 1f;

        foreach (var t in rivals) Grow(t, step);
        foreach (var t in rivals) UpdateActivity(t);

        if (clock >= nextRivalWar) { nextRivalWar = clock + Random.Range(RivalWarMin, RivalWarMax); RivalWar(); }

        if (clock < firstMoveAfter || pendingRival != null || assault != null) return;
        if (Time.time < nextGlobalAt) return;
        foreach (var t in rivals) if (Decide(t)) break;
    }

    // Рост: войско, богатство, уровень, заслоны, остывание злости
    void Grow(RaidTarget t, float dt)
    {
        int L = Mathf.Clamp(t.Level, 1, 3);
        float trainSec = (90f - 10f * L) * (t.Trait == RivalTrait.Warlike ? 0.85f : (t.Trait == RivalTrait.Cautious ? 0.9f : 1f));
        if (t.ArmySize < Cap(t))
        {
            t.TrainProgress += dt / trainSec;
            if (t.TrainProgress >= 1f)
            {
                t.TrainProgress = 0f;
                t.AddUnit(PickTrainee(t));
            }
        }

        t.Prosperity += dt / 60f * (t.Trait == RivalTrait.Calculating ? 1.2f : (t.Trait == RivalTrait.Cunning ? 1.1f : 1f));
        if (t.Level < 3 && t.Prosperity >= 15f * t.Level && !InBattleWith(t))
        {
            t.Prosperity = 0f;
            t.Level++;
            t.Seed = Random.Range(1, int.MaxValue);
            t.PlanCache = null;
            t.Stamp++;
            WarManager.Notify($"Слухи: {t.Name} разрослась — теперь это {t.LevelName}. Старые карты разведчиков не годятся", false);
        }

        fortTimer.TryGetValue(t, out float ft);
        ft += dt;
        float every = t.Trait == RivalTrait.Cautious ? 150f : 300f;
        int fortCap = t.Trait == RivalTrait.Cautious ? 6 : 3;
        if (ft >= every)
        {
            ft = 0f;
            if (t.Fortify < fortCap && !InBattleWith(t)) { t.Fortify++; t.Stamp++; }
        }
        fortTimer[t] = ft;

        t.Grudge = Mathf.Max(0f, t.Grudge - dt * 0.05f);
    }

    static int Cap(RaidTarget t)
    {
        int L = Mathf.Clamp(t.Level, 1, 3);
        return 5 + 5 * L + (t.Trait == RivalTrait.Cautious ? 3 : (t.Trait == RivalTrait.Warlike ? 2 : 0));
    }

    static string PickTrainee(RaidTarget t)
    {
        int L = Mathf.Clamp(t.Level, 1, 3);
        var names = new List<string>();
        var w = new List<float>();
        void W(string n, float v) { if (v > 0f) { names.Add(n); w.Add(v); } }
        float war = t.Trait == RivalTrait.Warlike ? 1f : 0f, cun = t.Trait == RivalTrait.Cunning ? 1f : 0f, cau = t.Trait == RivalTrait.Cautious ? 1f : 0f;
        W("Мечник", 40f * (1f + 0.2f * war));
        W("Лучник", 30f * (1f + 0.3f * cun + 0.5f * cau));
        if (L >= 2) W("Рыцарь", 10f * (1f + war));
        if (L >= 2) W("Полевой маг", 6f * (1f + 0.5f * cau));
        if (L >= 3 || (L >= 2 && cun > 0f)) W("Всадник", 6f * (1f + 1.5f * cun));
        if (L >= 3) W("Конный лучник", 5f * (1f + 1.5f * cun));

        float sum = 0f;
        foreach (var v in w) sum += v;
        float r = Random.value * sum;
        for (int i = 0; i < w.Count; i++) { if (r < w[i]) return names[i]; r -= w[i]; }
        return "Мечник";
    }

    bool InBattleWith(RaidTarget t)
    {
        var rm = RaidManager.Instance;
        return rm != null && rm.Current == t && rm.ArmyAway;
    }

    void UpdateActivity(RaidTarget t)
    {
        if (activityLock.TryGetValue(t, out float until) && Time.time < until) return;
        if (assault != null && assault.Rival == t) t.Activity = "<color=#FF6060>воюет с вами</color>";
        else if (pendingRival == t) t.Activity = "<color=#FF9060>собирает войско против вас</color>";
        else if (t.AwayCount > 0) t.Activity = "часть войска в походе";
        else if (t.Grudge >= 60f) t.Activity = "<color=#FF9060>жаждет мести и копит силы</color>";
        else if (t.ArmySize >= Cap(t)) t.Activity = t.Trait == RivalTrait.Cautious ? "укрепляется за заслонами" : "войско собрано — ищет, кого ударить";
        else t.Activity = t.Trait == RivalTrait.Cautious ? "строит заслоны и копит войско" : "копит войско";
    }

    // ---------- Решение: нападать ли на нас ----------

    bool Decide(RaidTarget t)
    {
        if (Time.time < t.NextMoveAt || !t.Ready || InBattleWith(t)) return false;

        bool fresh = Time.time - t.IntelAt < IntelFresh && t.IntelPower >= 0f;

        // Ничего о нас не знают - сначала лазутчик (воинственные идут вслепую)
        if (!fresh && t.Trait != RivalTrait.Warlike && spy == null && Random.value < 0.7f)
        {
            int spySide = Random.Range(0, 4);
            spy = RivalAssault.Launch(t, new Dictionary<string, int> { { RivalAssault.SpyType.unitName, 1 } }, spySide, true);
            // Дозорный на той стороне замечает лазутчика ещё на подходе
            if (spy != null && CamoScouts.WarnBonus(spySide, out string spyWatcher) > 0f)
                WarManager.Notify($"Дозор ({spyWatcher}): лазутчик {t.Name} крадётся с {SideFrom(spySide)}", false);
            t.NextMoveAt = Time.time + 120f;
            return true;
        }

        Dictionary<string, int> force = PickForce(t);
        if (force == null) { t.NextMoveAt = Time.time + 60f; return false; }
        float mine = BotVillage.RosterPower(force);

        // Знают ли, что наша армия ушла в поход: хитрые - всегда, остальные - через раз
        bool away = PlayerArmyAway && (t.Trait == RivalTrait.Cunning || Random.value < 0.5f);
        float theirs = fresh ? t.IntelPower : PlayerPower() * Random.Range(0.6f, 1.4f);
        if (away) theirs *= 0.35f;
        float ratio = mine / Mathf.Max(1f, theirs);

        float aggression;
        float minRatio;
        switch (t.Trait)
        {
            case RivalTrait.Warlike: aggression = 0.55f; minRatio = 0.7f; break;
            case RivalTrait.Cautious: aggression = 0.1f; minRatio = 1.4f; break;
            case RivalTrait.Cunning: aggression = 0.3f; minRatio = away ? 0.5f : 0.85f; break;
            default: aggression = 0.35f; minRatio = 1.0f; break;
        }
        float desire = aggression + t.Grudge / 120f + (away ? (t.Trait == RivalTrait.Cunning ? 0.6f : 0.3f) : 0f)
                     + Mathf.Clamp(ratio - 1f, -1f, 1f) * 0.5f + Mathf.Min(0.5f, (Time.time - nextGlobalAt) / 1200f);

        if (desire < 0.9f || ratio < minRatio)
        {
            t.NextMoveAt = Time.time + Random.Range(40f, 80f);
            return false;
        }

        // Нападаем: со стороны, где у нас меньше башен (если знают), иначе наугад
        int side = fresh ? WeakSide() : Random.Range(0, 4);
        pendingRival = t;
        pendingForce = force;
        pendingSide = side;
        // Наш дозор (CamoScouts) на этой стороне видит войско раньше - и точно знает состав
        float early = CamoScouts.WarnBonus(side, out string watcher);
        pendingAt = Time.time + WarnSeconds + early;
        foreach (var kv in force) t.SetAway(kv.Key, kv.Value);
        int n = 0;
        foreach (var kv in force) n += kv.Value;
        if (early > 0f)
            WarManager.Notify($"Дозор ({watcher}): войско {t.Name} ({TraitName(t.Trait)}) идёт с {SideFrom(side)} — {ForceText(force)}. Будут через ~{WarnSeconds + early:F0} с", false);
        else
            WarManager.Notify($"Дозорные: войско {t.Name} ({TraitName(t.Trait)}, ~{n} бойцов) идёт с {SideFrom(side)}! Будут через ~{WarnSeconds:F0} с", false);
        WarManager.RaiseAlarm();   // свернуть открытые окна
        return true;
    }

    // Состав войска одной строкой: «Мечник ×4, Лучник ×2»
    static string ForceText(Dictionary<string, int> force)
    {
        var parts = new List<string>();
        foreach (var kv in force) if (kv.Value > 0) parts.Add($"{kv.Key} ×{kv.Value}");
        return string.Join(", ", parts);
    }

    // Кого послать: дома всегда остаётся гарнизон; размер растёт от нападения к нападению
    Dictionary<string, int> PickForce(RaidTarget t)
    {
        var home = t.HomeRoster();
        var list = new List<string>();
        foreach (var kv in home) for (int i = 0; i < kv.Value; i++) list.Add(kv.Key);
        if (list.Count < 4) return null;

        float frac;
        switch (t.Trait)
        {
            case RivalTrait.Warlike: frac = 0.75f; break;
            case RivalTrait.Cautious: frac = 0.4f; break;
            case RivalTrait.Cunning: frac = 0.5f; break;
            default: frac = 0.6f; break;
        }
        int ramp = Mathf.Min(SendMax, 4 + 2 * globalAttacks);
        int send = Mathf.Clamp(Mathf.RoundToInt(list.Count * frac), 3, ramp);
        send = Mathf.Min(send, list.Count - 1);
        if (send < 3) return null;

        // Порядок: по вкусу соседа (остальное - вперемешку, чтобы был смешанный отряд)
        for (int i = 0; i < list.Count; i++) { int j = Random.Range(i, list.Count); string tmp = list[i]; list[i] = list[j]; list[j] = tmp; }
        list.Sort((a, b) => Pref(t.Trait, a).CompareTo(Pref(t.Trait, b)));

        var force = new Dictionary<string, int>();
        bool healer = false;
        for (int i = 0; i < send; i++)
        {
            force.TryGetValue(list[i], out int c);
            force[list[i]] = c + 1;
            if (list[i] == "Полевой маг") healer = true;
        }
        // В большой отряд - лекаря, если есть
        if (!healer && send >= 6 && list.IndexOf("Полевой маг") >= send)
        {
            force.TryGetValue("Полевой маг", out int c);
            force["Полевой маг"] = c + 1;
        }
        return force;
    }

    static int Pref(RivalTrait trait, string unitName)
    {
        switch (trait)
        {
            case RivalTrait.Warlike: return unitName == "Рыцарь" ? 0 : (unitName == "Всадник" ? 1 : 2);
            case RivalTrait.Cunning: return unitName == "Всадник" || unitName == "Конный лучник" ? 0 : (unitName == "Лучник" ? 1 : 2);
            default: return 1;
        }
    }

    // Сторона, где у нас меньше всего башен
    static int WeakSide()
    {
        int[] cnt = new int[4];
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.IsRuined || b.type == null) continue;
            if (b.GetComponent<TowerController>() == null && b.GetComponent<MagicCannonController>() == null && b.GetComponent<GroundCannonController>() == null) continue;
            Vector2 c = b.Center;
            if (c.x <= 0f) cnt[0]++; else cnt[1]++;
            if (c.y <= 0f) cnt[2]++; else cnt[3]++;
        }
        int best = Random.Range(0, 4);
        for (int s = 0; s < 4; s++) if (cnt[s] < cnt[best]) best = s;
        return best;
    }

    void LaunchPending()
    {
        RaidTarget t = pendingRival;
        var force = pendingForce;
        pendingRival = null;
        pendingForce = null;
        if (t == null || force == null) return;
        assault = RivalAssault.Launch(t, force, pendingSide, false);
        if (assault == null) { foreach (var kv in force) t.SetAway(kv.Key, -kv.Value); return; }
        t.AttacksOnPlayer++;
        globalAttacks++;
    }

    // ---------- События ----------

    /// <summary>Войско или лазутчик соседа ушли с нашей карты (все вернулись или погибли).</summary>
    public static void AssaultEnded(RivalAssault a, List<string> escaped, Loot brought, int lost, int total, bool spyMission, float spyShare)
    {
        var m = Instance;
        RaidTarget t = a.Rival;
        if (m == null || t == null) return;

        if (spyMission)
        {
            if (m.spy == a) m.spy = null;
            if (escaped.Count > 0)
            {
                // Вернулся - знают нашу силу (тем точнее, чем больше успел обойти)
                t.IntelPower = PlayerPower() * Mathf.Lerp(Random.Range(0.6f, 1.4f), 1f, Mathf.Clamp01(spyShare));
                t.IntelAt = Time.time;
                t.NextMoveAt = Time.time + Random.Range(20f, 60f);
            }
            else t.NextMoveAt = Time.time + Random.Range(120f, 200f);
            return;
        }

        if (m.assault == a) m.assault = null;
        t.IntelPower = PlayerPower();
        t.IntelAt = Time.time;
        if (!brought.IsEmpty)
        {
            t.Stash.Add(brought);
            t.Prosperity += brought.Total / 150f;
            t.Stamp++;
        }

        bool crushed = total > 0 && lost >= total * 0.6f;
        if (crushed)
        {
            t.Grudge = Mathf.Min(100f, t.Grudge + 10f);
            t.NextMoveAt = Time.time + Random.Range(840f, 1080f);
            WarManager.Notify($"Войско {t.Name} разбито: домой вернулись {escaped.Count} из {total}" + (brought.IsEmpty ? "" : $", унесли {brought.ShortText}"), true);
            SetActivity(t, "зализывает раны после разгрома", 240f);
        }
        else
        {
            if (!brought.IsEmpty) t.WinsOnPlayer++;
            float k = t.Trait == RivalTrait.Warlike ? 0.7f : 1f;
            t.NextMoveAt = Time.time + Random.Range(600f, 900f) * k;
            WarManager.Notify(brought.IsEmpty
                ? $"Войско {t.Name} ушло ни с чем: вернулись {escaped.Count} из {total}"
                : $"Войско {t.Name} ушло с добычей: {brought.ShortText}. Она лежит в их Ратуше — можно отбить набегом", false);
            if (!brought.IsEmpty) SetActivity(t, "празднует удачный набег на вас", 180f);
        }
        m.nextGlobalAt = Time.time + Random.Range(GapMin, GapMax);
    }

    /// <summary>Боец соседа вернулся из похода. Если у них дома сейчас идёт наш набег - он вступит в бой.</summary>
    public static void UnitReturned(RaidTarget t, string unitName)
    {
        var rd = RaidDefense.Instance;
        var rm = RaidManager.Instance;
        if (rd != null && rd.Target == t && rm != null && rm.Current == t && rm.BattleOn)
            rd.QueueReturning(unitName, Random.Range(12f, 20f));
    }

    /// <summary>Наша армия пришла к соседу: его войско, если оно у нас, спешит домой.</summary>
    public static void PlayerArrived(RaidTarget t)
    {
        var m = Instance;
        if (m == null || t == null) return;
        if (m.assault != null && m.assault.Rival == t && !m.assault.Retreating)
        {
            m.assault.Recall();
            WarManager.Notify($"{t.Name} узнали о вашем набеге — их войско бросает осаду и спешит домой!", true);
        }
        if (m.pendingRival == t)
        {
            foreach (var kv in m.pendingForce) t.SetAway(kv.Key, -kv.Value);
            m.pendingRival = null;
            m.pendingForce = null;
            WarManager.Notify($"{t.Name} отменили нападение — им самим пора обороняться", true);
        }
        SetActivity(t, "<color=#FF6060>отбивается от вашей армии</color>", 120f);
    }

    /// <summary>Защитник соседа погиб в нашем набеге.</summary>
    public static void DefenderLost(RaidTarget t, string unitName)
    {
        if (t != null && !string.IsNullOrEmpty(unitName)) t.LoseUnit(unitName, false);
    }

    /// <summary>Наш набег закончился (fought - был бой).</summary>
    public static void RaidFinished(RaidTarget t, bool fought, bool hallSacked, int ruined, int kills)
    {
        if (t == null || !fought) return;
        t.Grudge = Mathf.Min(100f, t.Grudge + 30f + (hallSacked ? 25f : 0f) + kills * 1.5f);
        int fortCap = (t.Trait == RivalTrait.Cautious ? 6 : 3) + 1;
        t.Fortify = Mathf.Min(fortCap, t.Fortify + 1);
        if (hallSacked) t.Stash.Clear();
        t.Prosperity = Mathf.Max(0f, t.Prosperity - ruined * 0.5f);
        t.IntelPower = Mathf.Max(t.IntelPower, PlayerPower(true));
        t.IntelAt = Time.time;
        // Месть: воинственные и расчётливые не заставят себя ждать
        if (t.Trait == RivalTrait.Warlike || t.Trait == RivalTrait.Calculating)
            t.NextMoveAt = Mathf.Min(t.NextMoveAt, Time.time + Random.Range(120f, 240f));
        t.Stamp++;
        SetActivity(t, t.Grudge >= 60f ? "<color=#FF6060>клянётся отомстить</color>" : "отстраивается после набега", 300f);
    }

    // ---------- Войны соседей между собой (по слухам) ----------

    void RivalWar()
    {
        var cand = new List<RaidTarget>();
        foreach (var t in rivals)
            if (t.HomeCount >= 5 && !InBattleWith(t) && (assault == null || assault.Rival != t) && pendingRival != t) cand.Add(t);
        if (cand.Count < 2) return;

        // Нападает самый воинственный (с долей случая)
        RaidTarget a = null;
        float best = -1f;
        foreach (var t in cand)
        {
            float w = (t.Trait == RivalTrait.Warlike ? 2f : (t.Trait == RivalTrait.Cautious ? 0.4f : 1f)) * Random.Range(0.5f, 1.5f);
            if (w > best) { best = w; a = t; }
        }
        cand.Remove(a);
        RaidTarget b = cand[Random.Range(0, cand.Count)];

        float pa = a.HomePower * 0.6f, pb = b.HomePower * 1.25f + b.Fortify * 0.5f;
        bool win = Random.value < pa / Mathf.Max(0.1f, pa + pb);
        int la = LoseRandom(a, Mathf.RoundToInt(a.HomeCount * 0.6f * (win ? Random.Range(0.1f, 0.2f) : Random.Range(0.25f, 0.4f))));
        int lb = LoseRandom(b, Mathf.RoundToInt(b.HomeCount * (win ? Random.Range(0.25f, 0.4f) : Random.Range(0.1f, 0.2f))));

        if (win)
        {
            a.Prosperity += 3f;
            int gold = 40 * b.Level;
            a.Stash.gold += gold;
            b.Prosperity = Mathf.Max(0f, b.Prosperity - 3f);
            a.Stamp++;
            b.Stamp++;
        }
        string heard = CamoScouts.PostedCount > 0 ? "Дозорные видели" : "Слухи";   // наш дозор видел их войско
        WarManager.Notify($"{heard}: {a.Name} напала на {b.Name} — " + (win ? $"{b.Name} разграблена" : "набег отбит") +
                          $". Потери: {a.Name} −{la}, {b.Name} −{lb}", false);
        SetActivity(a, win ? "празднует победу над " + b.Name : "отбита от " + b.Name + ", зализывает раны", 200f);
        SetActivity(b, win ? "<color=#80FF80>ослаблена набегом " + a.Name + "</color>" : "отбилась от " + a.Name, 200f);
    }

    static int LoseRandom(RaidTarget t, int n)
    {
        int done = 0;
        for (int k = 0; k < n; k++)
        {
            var home = t.HomeRoster();
            if (home.Count == 0) break;
            var keys = new List<string>(home.Keys);
            t.LoseUnit(keys[Random.Range(0, keys.Count)], false);
            done++;
        }
        return done;
    }

    // ---------- Для окна похода ----------

    public static string AttitudeText(RaidTarget t)
    {
        if (t.Grudge >= 60f) return "<color=#FF6060>жаждут мести</color>";
        if (t.Grudge >= 25f) return "<color=#FFB060>враждебны</color>";
        return "<color=#C0C0C0>настороже</color>";
    }

    /// <summary>Что известно о войске соседа: точно (свежая карта разведчика) или по слухам.</summary>
    public static string ArmyText(RaidTarget t, ScoutReport rep)
    {
        string away = t.AwayCount > 0 ? " <color=#80FF80>(часть войска в походе!)</color>" : "";
        if (rep != null && rep.Valid && Time.time - rep.MadeAt < 300f) return $"войско дома ~{t.HomeCount}" + away;
        int n = t.HomeCount;
        string size = n < 6 ? "небольшое войско" : (n < 12 ? "крепкое войско" : "большое войско");
        return "по слухам: " + size + away;
    }

    public static string CardLine(RaidTarget t, ScoutReport rep)
    {
        return $"Нрав: <b>{TraitName(t.Trait)}</b> — {TraitHint(t.Trait)}\nК вам: {AttitudeText(t)} · {ArmyText(t, rep)}\nСейчас: <i>{t.Activity}</i>";
    }

    /// <summary>Строка для панели во время нападения на нас.</summary>
    public static string ThreatLine()
    {
        var m = Instance;
        if (m == null) return "";
        if (m.assault != null) return $"Войско {m.assault.RivalName}: {m.assault.Status}";
        if (m.pendingRival != null) return $"Войско {m.pendingRival.Name} на подходе с {SideFrom(m.pendingSide)}";
        return "";
    }
}
