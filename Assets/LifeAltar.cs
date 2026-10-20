using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Алтарь жизни (школа Магии жизни, Ратуша Ур.3, один на город). В алтарь вкладывают ресурсы
/// (еда, камень, травы) - они копятся в запасе подношений, и алтарь понемногу превращает их в рост голема
/// (скорость - от маны в воздухе, ManaManager.Factor). На 100% рождается голем (LifeGolem):
///  - дома стоит у алтаря и бьёт врагов, которые зашли в круг алтаря (LeashRadius); приказы не слушает;
///  - у алтаря медленно лечится; подношения при живом големе тоже лечат его;
///  - можно взять в набег (окно набега, «Взять голема»): раны там остаются с ним, погиб в набеге - погиб и дома;
///  - алтарь разрушен или снесён - голем рассыпается. Погиб - алтарь остывает (DeathCooldown), потом растим заново.
/// Уровень алтаря: голем крепче и сильнее (+12% за уровень). Школа сменилась - алтарь спит, голем скрыт.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class LifeAltar : MonoBehaviour
{
    public const string TypeName = "Алтарь жизни";
    public const int RequiredHallLevel = 3;

    // ---------- Числа (можно менять) ----------
    public const float GrowPoints = 100f;        // сколько очков роста нужно голему
    public const float MaxPending = 36f;         // запас подношений (очков), который алтарь держит
    public const float GrowPerSecond = 0.8f;     // очков роста в секунду (× мана)
    public const float HealPointsPerSecond = 3f; // при живом големе подношения лечат: очков в секунду ...
    public const float HpPerPoint = 6f;          // ... и здоровья за очко
    public const float DeathCooldown = 60f;      // после гибели голема алтарь остывает
    public const float LeashRadius = 6f;         // дома голем не отходит от алтаря дальше этого
    public const float RegenPerSecond = 1.5f;    // у алтаря, вне боя

    // Голем (на Ур.1 алтаря). Для сравнения: Рыцарь 240 здоровья, 24 урона / 1.3 с, скорость 1.3
    public const float BaseHp = 400f, BaseDamage = 16f, AttackCooldown = 1.4f, AttackRange = 1.3f, MoveSpeed = 1.15f;
    public const string GolemName = "Голем";
    public static readonly Color GolemColor = new Color(0.47f, 0.62f, 0.42f);

    public struct Offer
    {
        public string name, hex;
        public int amount;
        public float points;
    }

    // 0 - еда, 1 - камень, 2 - травы
    public static readonly Offer[] Offers =
    {
        new Offer { name = "Еда",    hex = UiKit.FoodHex,  amount = 40, points = 10f },
        new Offer { name = "Камень", hex = UiKit.StoneHex, amount = 40, points = 12f },
        new Offer { name = "Травы",  hex = UiKit.GoodHex,  amount = 5,  points = 12f },
    };

    static readonly List<LifeAltar> all = new List<LifeAltar>();

    /// <summary>Алтарь города (он один).</summary>
    public static LifeAltar Current
    {
        get
        {
            for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].Building != null) return all[i];
            return null;
        }
    }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) : 1;
    public float LevelMul => 1f + 0.12f * (Lvl - 1);
    public float GolemMaxHp => BaseHp * LevelMul;
    public float GolemDamage => BaseDamage * LevelMul;

    // Состояние
    public float Growth { get; private set; }     // 0..GrowPoints
    public float Pending { get; private set; }    // запас подношений (очков)
    public bool GolemAlive { get; private set; }
    public bool GolemAway { get; private set; }   // в набеге
    public float GolemHp { get; private set; }
    public float Cooldown { get; private set; }
    public int Born { get; private set; }
    LifeGolem home;

    public bool Works => Building != null && !Building.IsRuined;
    public LifeGolem HomeGolem => home;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!all.Contains(this)) all.Add(this); }

    void OnDestroy()
    {
        all.Remove(this);
        bool hadGolem = GolemAlive;
        GolemAlive = false;
        GolemAway = false;
        if (home != null) { home.Despawn(); home = null; }
        if (hadGolem && Application.isPlaying && Time.timeSinceLevelLoad > 1f)
            WarManager.Notify("Алтарь жизни снесён — голем рассыпался", false);
    }

    // ---------- Подношения ----------

    public string OfferProblem(int i)
    {
        if (!DevelopmentPath.IsLife) return "Нужна школа Магии жизни";
        if (!Works) return "Алтарь разрушен";
        if (i < 0 || i >= Offers.Length) return "—";
        if (GolemAlive && GolemAway) return "Голем в набеге";
        if (GolemAlive && GolemHp + Pending * HpPerPoint >= GolemMaxHp - 0.5f) return "Голем здоров — подношения не нужны";
        if (Pending + Offers[i].points > MaxPending + 0.01f) return "Запас подношений полон — подождите";
        var rm = ResourceManager.Instance;
        if (rm == null) return "Нет казны";
        Offer o = Offers[i];
        int have = i == 0 ? rm.food : (i == 1 ? rm.stone : rm.herbs);
        if (have < o.amount) return $"Нужно {o.amount} ({o.name.ToLower()}) в Ратуше";
        return "";
    }

    public void MakeOffer(int i)
    {
        if (OfferProblem(i) != "") return;
        var rm = ResourceManager.Instance;
        Offer o = Offers[i];
        bool ok = i == 0 ? rm.Spend(0, 0, 0, o.amount) : (i == 1 ? rm.Spend(0, 0, o.amount, 0) : rm.SpendHerbs(o.amount));
        if (!ok) return;
        Pending = Mathf.Min(MaxPending, Pending + o.points);
        if (Building != null) CombatFx.Ring(Building.Center, 1.2f, new Color(0.5f, 1f, 0.55f, 0.8f), 0.5f);
    }

    // ---------- Жизнь алтаря ----------

    void Update()
    {
        if (Building == null) { Building = GetComponent<BuildingInstance>(); if (Building == null) return; }
        float dt = Time.deltaTime;

        if (Building.IsRuined)
        {
            if (GolemAlive) KillGolem("Алтарь жизни разрушен — голем рассыпался");
            Growth = 0f;
            Pending = 0f;
            return;
        }

        if (!DevelopmentPath.IsLife)
        {
            // Школа не та - алтарь спит, голем скрыт (не погиб)
            if (home != null) { home.Despawn(); home = null; }
            return;
        }

        if (Cooldown > 0f) Cooldown = Mathf.Max(0f, Cooldown - dt);

        if (!GolemAlive)
        {
            if (Cooldown > 0f || Pending <= 0f) return;
            float step = Mathf.Min(Pending, GrowPerSecond * ManaManager.Factor * Building.SpeedMultiplier * dt);
            Pending -= step;
            Growth += step;
            if (Growth >= GrowPoints) Birth();
            return;
        }

        if (GolemAway) return;

        if (GolemHp > GolemMaxHp) GolemHp = GolemMaxHp;
        if (Pending > 0f && GolemHp < GolemMaxHp)
        {
            float step = Mathf.Min(Pending, HealPointsPerSecond * dt);
            Pending -= step;
            GolemHp = Mathf.Min(GolemMaxHp, GolemHp + step * HpPerPoint);
        }
        if (home == null) home = LifeGolem.Spawn(this, HomeSpot(), false);
    }

    void Birth()
    {
        Growth = GrowPoints;
        GolemAlive = true;
        GolemAway = false;
        GolemHp = GolemMaxHp;
        Born++;
        if (home != null) home.Despawn();
        home = LifeGolem.Spawn(this, HomeSpot(), false);
        CombatFx.Ring(Building.Center, 2f, new Color(0.5f, 1f, 0.55f, 0.9f), 0.8f);
        WarManager.Notify("Алтарь жизни: родился голем! Он сторожит алтарь", true);
    }

    /// <summary>Где голем ждёт у алтаря: у двери, иначе - ближайшая свободная клетка.</summary>
    public Vector2 HomeSpot()
    {
        var nav = GridNavigation.Instance;
        if (Building == null) return transform.position;
        Vector2 c = Building.Center;
        if (nav == null) return c;
        if (nav.TryGetDoorCell(Building, nav.WorldToCell(c + Vector2.down * 3f), out Vector2Int door)) return nav.CellCenter(door);
        return nav.CellCenter(nav.NearestFree(nav.WorldToCell(c)));
    }

    // Голем погиб (дома, в набеге или вместе с алтарём)
    void KillGolem(string note)
    {
        if (!GolemAlive) return;
        GolemAlive = false;
        GolemAway = false;
        GolemHp = 0f;
        Growth = 0f;
        Cooldown = DeathCooldown;
        if (home != null)
        {
            CombatFx.Death(home.Position);
            home.Despawn();
            home = null;
        }
        if (!string.IsNullOrEmpty(note)) WarManager.Notify(note, false);
    }

    // ---------- Для LifeGolem ----------

    public void GolemDamaged(float dmg)
    {
        if (!GolemAlive || dmg <= 0f) return;
        GolemHp -= dmg;
        if (GolemHp <= 0f) KillGolem($"Голем пал у алтаря. Алтарь остынет {DeathCooldown:F0} с, потом растите нового");
    }

    public void GolemHealed(float amount)
    {
        if (!GolemAlive || amount <= 0f) return;
        GolemHp = Mathf.Min(GolemMaxHp, GolemHp + amount);
    }

    // ---------- Набег (RaidManager) ----------

    /// <summary>Голема можно взять в набег ("" - можно).</summary>
    public static string RaidProblem()
    {
        LifeAltar a = Current;
        if (!DevelopmentPath.IsLife || a == null) return "Нет Алтаря жизни";
        if (!a.Works) return "Алтарь разрушен";
        if (!a.GolemAlive) return "Голем ещё не вырос";
        if (a.GolemAway) return "Голем уже в походе";
        return "";
    }

    public static bool CanJoinRaid => DevelopmentPath.IsLife && Current != null && Current.GolemAlive;

    /// <summary>Голем уходит с армией (дома его нет до возвращения).</summary>
    public static bool TakeForRaid()
    {
        if (RaidProblem() != "") return false;
        LifeAltar a = Current;
        a.GolemAway = true;
        if (a.home != null) { a.home.Despawn(); a.home = null; }
        return true;
    }

    /// <summary>Голем всё ещё связан с алтарём (в набеге). false - алтарь потерян, голем рассыпается.</summary>
    public static bool RaidBound
    {
        get { LifeAltar a = Current; return a != null && a.Works && a.GolemAlive && a.GolemAway; }
    }

    public static float RaidHp { get { LifeAltar a = Current; return a != null ? a.GolemHp : 0f; } }

    /// <summary>Боец для поля набега: статы голема по уровню алтаря.</summary>
    public static UnitType RaidUnitType()
    {
        LifeAltar a = Current;
        float mul = a != null ? a.LevelMul : 1f;
        return new UnitType
        {
            unitName = GolemName, costGold = 0, costFood = 0, spaceOccupied = 0,
            color = GolemColor, bodyScale = 0.8f,
            maxHp = BaseHp * mul, damage = BaseDamage * mul, attackRange = AttackRange, attackCooldown = AttackCooldown, moveSpeed = MoveSpeed
        };
    }

    /// <summary>Голем вернулся из набега. hp &lt; 0 - не воевал (раны как были). at - где появится (край карты).</summary>
    public static void BackFromRaid(float hp, Vector2 at)
    {
        LifeAltar a = Current;
        if (a == null || !a.GolemAlive || !a.GolemAway) return;
        a.GolemAway = false;
        if (hp >= 0f) a.GolemHp = Mathf.Clamp(hp, 1f, a.GolemMaxHp);
        if (a.home != null) a.home.Despawn();
        a.home = DevelopmentPath.IsLife ? LifeGolem.Spawn(a, at, true) : null;
    }

    /// <summary>Голем погиб в набеге - погиб и дома.</summary>
    public static void DiedInRaid()
    {
        LifeAltar a = Current;
        if (a == null || !a.GolemAlive) return;
        a.KillGolem($"Голем погиб в набеге. Алтарь остынет {DeathCooldown:F0} с, потом растите нового");
    }

    // ---------- Тексты ----------

    public static string StageName(float growth)
    {
        if (growth < 25f) return "Семя";
        if (growth < 50f) return "Глиняный ком";
        if (growth < 75f) return "Каркас";
        return "Пробуждение";
    }

    public string StatsLine => $"{GolemMaxHp:F0} здоровья · урон {GolemDamage:F0} / {AttackCooldown:F1} с";

    // ---------- Панель ----------

    const float OfferH = 30f;

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        const string foldId = "life-altar-info";
        string infoText =
            "Подношения копятся в запасе алтаря, он превращает их в рост голема (быстрее при высокой мане). " +
            $"На 100% рождается голем: стоит у алтаря и бьёт врагов в {LeashRadius:F0} клетках от него, приказов не слушает. " +
            $"У алтаря сам лечится ({RegenPerSecond:F1}/с вне боя), подношения лечат быстрее. " +
            "Его можно взять в набег (окно набега). Алтарь разрушен — голем рассыпается; погиб — алтарь остывает " +
            $"{DeathCooldown:F0} с. Уровень алтаря: +12% к силе голема. Рождено големов: {Born}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        bool life = DevelopmentPath.IsLife;
        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float poolY = barY + 18f;
        float warnY = poolY + 20f;
        float offersY = warnY + ((!life || !Works) ? 22f : 2f);
        float rowsY = offersY + UiKit.SectionH + 2f;
        float foldY = rowsY + Offers.Length * OfferH + 4f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        int mana = ManaManager.Instance != null ? ManaManager.Instance.Percent : 100;

        // --- Голем / рост ---
        string status;
        float frac;
        Color barCol;
        string barText;
        if (GolemAlive)
        {
            frac = GolemMaxHp > 0f ? GolemHp / GolemMaxHp : 0f;
            barCol = Color.Lerp(UiKit.Bad, UiKit.Good, frac);
            barText = $"{Mathf.CeilToInt(GolemHp)}/{Mathf.CeilToInt(GolemMaxHp)}";
            if (GolemAway) status = "<b>Голем</b> " + UiKit.Col(UiKit.WarnHex, "в набеге с армией");
            else if (!life) status = "<b>Голем</b> " + UiKit.Col(UiKit.MutedHex, "спит");
            else if (home != null && home.Fighting) status = "<b>Голем</b> " + UiKit.Col(UiKit.BadHex, "бьётся у алтаря");
            else if (home != null && home.Returning) status = "<b>Голем</b> " + UiKit.Col(UiKit.InfoHex, "возвращается к алтарю");
            else status = "<b>Голем</b> " + UiKit.Col(UiKit.GoodHex, "сторожит алтарь");
        }
        else
        {
            frac = Growth / GrowPoints;
            barCol = new Color(0.45f, 0.85f, 0.5f);
            barText = $"{StageName(Growth)} · {Mathf.FloorToInt(frac * 100f)}%";
            if (Cooldown > 0f) status = UiKit.Col(UiKit.WarnHex, $"Алтарь остывает: {Mathf.CeilToInt(Cooldown)} с");
            else if (Pending > 0f && life && Works) status = UiKit.Col(UiKit.InfoHex, "Голем растёт");
            else status = UiKit.Col(UiKit.MutedHex, "Голем не растёт — сделайте подношение");
        }
        Rect sr = new Rect(x + 10f, y + statusY, w - 20f, 20f);
        GUI.Label(sr, status, st);
        UiKit.Tip(sr, $"Голем (Ур.{b.level} алтаря): {StatsLine}, скорость {MoveSpeed:F2}.\nДля сравнения: Рыцарь — 240 здоровья, урон 24 / 1.3 с.");
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 14f), frac, barCol, barText);

        Rect pr = new Rect(x + 10f, y + poolY, w - 20f, 18f);
        string manaCol = mana >= 50 ? UiKit.InfoHex : UiKit.WarnHex;
        GUI.Label(pr, $"Запас подношений: <b>{Mathf.CeilToInt(Pending)}/{MaxPending:F0}</b> · мана: <color={manaCol}>{mana}%</color> (x{ManaManager.Factor:F2})", small);
        UiKit.Tip(pr, GolemAlive
            ? $"Пока голем ранен, запас лечит его: {HpPerPoint:F0} здоровья за очко."
            : $"Рост: {GrowPerSecond:F1} очка/с × мана. Нужно {GrowPoints:F0} очков.");

        if (!life) GUI.Label(new Rect(x + 10f, y + warnY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, "Нужна школа Магии жизни — алтарь спит"), small);
        else if (!Works) GUI.Label(new Rect(x + 10f, y + warnY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, "Алтарь разрушен"), small);

        // --- Подношения ---
        UiKit.Section(x + 10f, y + offersY, w - 20f, GolemAlive ? "Подношения (лечат голема)" : "Подношения");
        var rm = ResourceManager.Instance;
        float cy = y + rowsY;
        for (int i = 0; i < Offers.Length; i++)
        {
            Offer o = Offers[i];
            int have = rm == null ? 0 : (i == 0 ? rm.food : (i == 1 ? rm.stone : rm.herbs));
            string col = have >= o.amount ? o.hex : UiKit.BadHex;
            Rect nr = new Rect(x + 10f, cy + 4f, w - 130f, 20f);
            GUI.Label(nr, $"<b>{o.name}</b>  <color={col}>{o.amount}</color> {UiKit.Col(UiKit.MutedHex, "→ +" + o.points.ToString("F0") + " очк.")}", st);
            UiKit.Tip(nr, $"{o.name}: {o.amount} из Ратуши (есть {have}) → {o.points:F0} очков роста или лечения.");

            string p = OfferProblem(i);
            bool ok = p == "";
            if (UiKit.Button(new Rect(x + w - 110f, cy, 100f, 26f), "Вложить", ok, ok ? UiKit.Kind.Primary : UiKit.Kind.Normal,
                    ok ? $"Положить на алтарь: {o.amount} ({o.name.ToLower()})" : p))
                MakeOffer(i);
            cy += OfferH;
        }

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}

/// <summary>
/// Голем Алтаря жизни дома. Не житель и не солдат: приказов нет. Стоит у алтаря, бьёт врагов (EnemyAgent),
/// которые зашли в круг алтаря (LifeAltar.LeashRadius), и возвращается. У алтаря вне боя медленно лечится.
/// Здоровье хранит алтарь (LifeAltar.GolemHp) - объект можно убрать и создать заново (набег, смена школы).
/// Враги тоже бьют голема (см. EnemyAgent: targetGolem).
/// </summary>
public class LifeGolem : MonoBehaviour
{
    public static readonly List<LifeGolem> All = new List<LifeGolem>();

    public LifeAltar Altar { get; private set; }
    public Vector2 Position => transform.position;
    public float Hp => Altar != null ? Altar.GolemHp : 0f;
    public float MaxHp => Altar != null ? Altar.GolemMaxHp : 1f;
    public bool Returning { get; private set; }   // идёт от края карты после набега - ни на что не отвлекается
    public bool Fighting => EnemyAgent.Alive(target);

    bool gone;
    EnemyAgent target, ignore;
    float ignoreUntil, attackTimer, thinkTimer, lastHit = -99f, homeTimer;
    Vector2 homeSpot;
    readonly GridMover mover = new GridMover();
    Transform core, hpFill;
    GameObject hpRoot;
    const float Scale = 0.8f, BarW = 0.9f;

    public static bool Targetable(LifeGolem g)
    {
        return g != null && !g.gone && g.isActiveAndEnabled && !g.Returning && g.Altar != null && g.Altar.GolemAlive && !g.Altar.GolemAway;
    }

    public static LifeGolem Spawn(LifeAltar altar, Vector2 pos, bool returning)
    {
        GameObject go = new GameObject("LifeGolem");
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        LifeGolem g = go.AddComponent<LifeGolem>();
        g.Altar = altar;
        g.Returning = returning;
        g.homeSpot = altar != null ? altar.HomeSpot() : pos;
        g.BuildLook();
        return g;
    }

    public void Despawn()
    {
        gone = true;
        if (this != null) Destroy(gameObject);
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    SpriteRenderer Part(string n, Sprite sp, Vector2 pos, Vector2 size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = sp;
        r.color = c;
        r.sortingOrder = order;
        return r;
    }

    void BuildLook()
    {
        // Тяжёлое каменное тело, моховые плечи и светящееся зелёное ядро
        Part("Outline", CombatFx.Square(), Vector2.zero, new Vector2(Scale + 0.14f, Scale + 0.14f), new Color(0.12f, 0.16f, 0.1f), 12);
        Part("Body", CombatFx.Square(), Vector2.zero, new Vector2(Scale, Scale), LifeAltar.GolemColor, 13);
        Part("Moss", CombatFx.Square(), new Vector2(0f, Scale * 0.32f), new Vector2(Scale, Scale * 0.2f), new Color(0.3f, 0.55f, 0.25f), 14);
        core = Part("Core", CombatFx.Circle(), Vector2.zero, new Vector2(0.3f, 0.3f), new Color(0.6f, 1f, 0.6f), 15).transform;

        hpRoot = new GameObject("Hp");
        hpRoot.transform.SetParent(transform, false);
        hpRoot.transform.localPosition = new Vector3(0f, Scale * 0.5f + 0.22f, 0f);
        GameObject bg = new GameObject("Bg");
        bg.transform.SetParent(hpRoot.transform, false);
        bg.transform.localScale = new Vector3(BarW + 0.06f, 0.14f, 1f);
        var bgr = bg.AddComponent<SpriteRenderer>();
        bgr.sprite = CombatFx.Square();
        bgr.color = new Color(0f, 0f, 0f, 0.8f);
        bgr.sortingOrder = 16;
        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(hpRoot.transform, false);
        var fr = fill.AddComponent<SpriteRenderer>();
        fr.sprite = CombatFx.Square();
        fr.color = new Color(0.35f, 0.9f, 0.4f);
        fr.sortingOrder = 17;
        hpFill = fill.transform;
        UpdateLook();
    }

    void UpdateLook()
    {
        float f = MaxHp > 0f ? Mathf.Clamp01(Hp / MaxHp) : 0f;
        if (hpRoot != null) hpRoot.SetActive(f < 0.999f);
        if (hpFill != null)
        {
            hpFill.localScale = new Vector3(BarW * f, 0.1f, 1f);
            hpFill.localPosition = new Vector3(-BarW * (1f - f) * 0.5f, 0f, 0f);
        }
        if (core != null)
        {
            float k = 0.26f + 0.05f * Mathf.Sin(Time.time * (Fighting ? 8f : 2.5f));
            core.localScale = new Vector3(k, k, 1f);
        }
    }

    // ---------- Урон ----------

    public void TakeDamage(float dmg, EnemyAgent by)
    {
        if (gone || Altar == null || dmg <= 0f) return;
        lastHit = Time.time;
        Altar.GolemDamaged(dmg);   // погиб - алтарь сам уберёт объект
        if (gone) return;
        if (!EnemyAgent.Alive(target) && EnemyAgent.Alive(by) && !Returning && InLeash(by.Position, 2f)) { target = by; mover.Clear(); mover.ResetFails(); }
    }

    // ---------- Мозги ----------

    bool InLeash(Vector2 p, float extra)
    {
        return Altar != null && Altar.Building != null && Vector2.Distance(Altar.Building.Center, p) <= LifeAltar.LeashRadius + extra;
    }

    void Update()
    {
        if (gone) return;
        if (Altar == null || !Altar.GolemAlive || Altar.GolemAway) { Despawn(); return; }
        UpdateLook();
        if (!Altar.isActiveAndEnabled) return;   // алтарь переносят - стоим
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        float dt = Time.deltaTime;
        attackTimer -= dt;
        homeTimer -= dt;
        if (homeTimer <= 0f)
        {
            homeTimer = 2f;
            homeSpot = Altar.HomeSpot();
        }

        // Идёт домой после набега
        if (Returning)
        {
            bool there = mover.Step(transform, nav, homeSpot, LifeAltar.MoveSpeed * 1.2f, 0.4f);
            if (there || InLeash(Position, -1f)) { Returning = false; mover.Clear(); }
            else if (mover.Fails >= 6)
            {
                transform.position = new Vector3(homeSpot.x, homeSpot.y, 0f);
                Returning = false;
                mover.Clear();
                mover.ResetFails();
            }
            return;
        }

        thinkTimer -= dt;
        if (thinkTimer <= 0f)
        {
            thinkTimer = 0.3f;
            Think();
        }

        if (EnemyAgent.Alive(target)) { Fight(nav); return; }

        // Нет врагов - к алтарю, там лечится
        bool atHome = mover.Step(transform, nav, homeSpot, LifeAltar.MoveSpeed, 0.4f);
        if ((atHome || Vector2.Distance(Position, homeSpot) < 2.5f) && Time.time - lastHit > 5f)
            Altar.GolemHealed(LifeAltar.RegenPerSecond * dt);
    }

    void Think()
    {
        if (EnemyAgent.Alive(target) && InLeash(target.Position, 1f)) return;
        target = null;
        float best = float.MaxValue;
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e)) continue;
            if (e == ignore && Time.time < ignoreUntil) continue;
            if (!InLeash(e.Position, 0f)) continue;
            float d = Vector2.Distance(Position, e.Position);
            if (d < best) { best = d; target = e; }
        }
        if (target != null) { mover.Clear(); mover.ResetFails(); }
    }

    void Fight(GridNavigation nav)
    {
        Vector2 tp = target.Position;
        float d = Vector2.Distance(Position, tp);
        if (d <= LifeAltar.AttackRange)
        {
            mover.Clear();
            if (attackTimer <= 0f)
            {
                attackTimer = LifeAltar.AttackCooldown;
                CombatFx.Flash(tp);
                target.TakeDamage(Altar.GolemDamage, null, null, false, this);
            }
            return;
        }

        mover.Step(transform, nav, tp, LifeAltar.MoveSpeed, LifeAltar.AttackRange * 0.8f);
        if (mover.Fails >= 3)
        {
            ignore = target;
            ignoreUntil = Time.time + 5f;
            target = null;
            mover.Clear();
            mover.ResetFails();
        }
    }
}
