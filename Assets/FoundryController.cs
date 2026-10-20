using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Литейная (только путь Инженерии). Отливает ДЕТАЛИ (болты, шестерни, заклёпки) из железной руды и стали,
/// а из смолы (Лесопилка Ур.3) варит РЕЗИНУ.
/// Работает как Мастерская - всё носится физически:
///  1. Игрок заказывает плавку: руда и сталь списываются, литейщик несёт их из Ратуши в Литейную.
///  2. Идёт плавка. 3. Готовые детали несут в Ратушу (ResourceManager.parts).
///
/// ЖАР КОТЛОВ. У котлов четыре режима:
///  - Слабый      - плавка медленнее, котлы быстро остывают;
///  - Обычный     - нормальная скорость, жар понемногу спадает;
///  - Сильный     - плавка быстрее, жар растёт;
///  - Предельный  - плавка очень быстрая, жар растёт стремительно.
/// Жар растёт, только пока идёт плавка; без работы котлы остывают.
/// На 100% жара - перегрев: котлы сбрасываются на «Обычный», и дальше одно из двух:
///  - авария: литейная стоит BreakdownSeconds, пока котлы остывают (плавка ждёт, сырьё не пропадает);
///  - пожар (шанс FireChanceByLevel): литейная сгорает - превращается в руины вместе с текущей плавкой
///    (руины потом отстраивают, как после набега).
/// «Предохранитель» (включён по умолчанию) сам сбрасывает котлы на «Обычный» при SafetyHeat -
/// спокойно, но мощный режим держится недолго. Выключите - и можно жечь до упора, на свой риск.
/// С уровнем котлы прочнее: греются медленнее, плавят быстрее и реже горят.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class FoundryController : MonoBehaviour
{
    public const string TypeName = "Литейная";

    public static readonly List<FoundryController> All = new List<FoundryController>();

    public class Recipe
    {
        public string name;
        public int ore;        // железная руда (из казны)
        public int steel;      // сталь (склад Ратуши / Хранилища, любое качество)
        public int resin;      // смола (из казны)
        public bool rubber;    // результат - резина, а не детали
        public int output;     // деталей (или резины) за плавку
        public float seconds;  // время плавки на «Обычном» жару
        public int minLevel = 1;
    }

    public static readonly Recipe[] Recipes =
    {
        new Recipe { name = "Отлить детали",  ore = 8,  steel = 1, output = 3, seconds = 14f, minLevel = 1 },
        new Recipe { name = "Большая плавка", ore = 20, steel = 3, output = 9, seconds = 32f, minLevel = 2 },
        new Recipe { name = "Сварить резину", resin = 8, rubber = true, output = 3, seconds = 20f, minLevel = 1 },
    };

    // ---------- Котлы ----------
    public enum Furnace { Low, Normal, High, Max }
    static readonly string[] FurnaceNames = { "Слабый", "Обычный", "Сильный", "Предельный" };
    static readonly float[] FurnaceSpeed  = { 0.6f, 1f, 1.7f, 2.6f };    // множитель скорости плавки
    static readonly float[] FurnaceHeat   = { -6f, -1.5f, 4f, 9f };      // жар в секунду, пока идёт плавка
    static readonly Color[] FurnaceColors =
    {
        new Color(0.55f, 0.75f, 1f), new Color(0.85f, 0.85f, 0.85f), new Color(1f, 0.7f, 0.35f), new Color(1f, 0.35f, 0.25f)
    };

    //                                               Ур.1   Ур.2   Ур.3
    static readonly float[] HeatGainByLevel   = { 1f,    0.8f,  0.65f };  // котлы получше греются медленнее
    static readonly float[] SpeedByLevel      = { 1f,    1.2f,  1.4f };
    static readonly float[] FireChanceByLevel = { 0.45f, 0.3f,  0.18f };  // шанс пожара при перегреве

    const float IdleCooling = 5f;          // остывание без работы, жара в секунду
    public const float WarnHeat = 70f;     // с этого жара - опасно (предупреждение)
    public const float SafetyHeat = 85f;   // тут срабатывает предохранитель
    public const float MaxHeat = 100f;     // перегрев
    public const float BreakdownSeconds = 45f;

    public Furnace Mode = Furnace.Normal;
    public bool Safety = true;     // предохранитель
    public bool AutoCast;          // плавить постоянно (последний рецепт)

    public float Heat { get; private set; }            // 0..100
    public float BrokenLeft { get; private set; }      // > 0 - авария, котлы остывают
    public bool Broken => BrokenLeft > 0f;
    public int CastTotal { get; private set; }
    public int RubberTotal { get; private set; }
    public int Overheats { get; private set; }
    public int InTransit => inTransit;

    enum Stage { Idle, Fetching, Casting }
    Stage stage = Stage.Idle;
    Recipe current;
    float timer;
    int inTransit;                 // деталей несут в Ратушу
    int autoRecipe;
    float autoTimer, fxTimer;
    bool warned;
    readonly List<UpgradeRules.Spent> spentSteel = new List<UpgradeRules.Spent>();

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;
    public float FireChance => FireChanceByLevel[Lvl];
    public bool IsCasting => stage == Stage.Casting;

    SpriteRenderer look;
    bool tinted;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    static BuildingInstance HallBuilding()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    static int SteelAvailable()
    {
        var st = TownHallStorage.Instance;
        return st != null ? UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) : 0;
    }

    // Жар за секунду плавки в этом режиме (с учётом уровня)
    float HeatRate(Furnace m)
    {
        float r = FurnaceHeat[(int)m];
        return r > 0f ? r * HeatGainByLevel[Lvl] : r;
    }

    // ---------- Заказ плавки ----------

    public bool Unlocked(Recipe r) { return r != null && Building != null && Building.level >= r.minLevel; }

    public string CastProblem(Recipe r)
    {
        if (!DevelopmentPath.IsEngineering) return "Нужен путь Инженерии";
        if (!Works) return "Литейная разрушена";
        if (!Unlocked(r)) return $"Нужна Литейная Ур.{r.minLevel}";
        if (Broken) return $"Авария: котлы остывают ({BrokenLeft:F0} с)";
        if (stage != Stage.Idle) return "Уже идёт плавка";
        if (!Building.isProducing) return "Нет литейщиков";
        var rm = ResourceManager.Instance;
        int ore = rm != null ? rm.iron : 0;
        if (ore < r.ore) return $"Нужно {r.ore} руды (есть {ore})";
        int steel = SteelAvailable();
        if (steel < r.steel) return $"Нужно {r.steel} стали (есть {steel})";
        int resin = rm != null ? rm.resin : 0;
        if (resin < r.resin) return $"Нужно {r.resin} смолы (есть {resin})";
        return "";
    }

    public void StartCast(Recipe r)
    {
        if (CastProblem(r) != "") return;
        var rm = ResourceManager.Instance;
        if (!rm.SpendIron(r.ore)) return;
        if (!rm.SpendResin(r.resin)) { rm.Add(0, 0, 0, 0, r.ore); return; }

        spentSteel.Clear();
        if (r.steel > 0) UpgradeRules.SpendAtLeast(TownHallStorage.Instance, RefinedKind.Steel, QualityTier.Medium, r.steel, spentSteel);

        current = r;
        stage = Stage.Fetching;
        autoRecipe = System.Array.IndexOf(Recipes, r);

        // Литейщик идёт в Ратушу (или Хранилище, где взяли сталь) за рудой и сталью и несёт их сюда
        BuildingInstance pickup = HallBuilding();
        foreach (var sp in spentSteel)
            if (sp.source != null) { pickup = sp.source; break; }

        Logistics.Request(new HaulJob
        {
            from = pickup,
            to = Building,
            prefer = Building,
            onDeliver = OnRawDelivered,
            onAbort = loaded => RefundRaw()
        });
    }

    void OnRawDelivered()
    {
        if (current == null) return;
        if (!Works) { RefundRaw(); return; }   // пока несли, литейную разрушили
        timer = current.seconds;
        stage = Stage.Casting;
    }

    void RefundRaw()
    {
        if (current != null && ResourceManager.Instance != null)
        {
            ResourceManager.Instance.Add(0, 0, 0, 0, current.ore);
            ResourceManager.Instance.AddResin(current.resin);
        }
        UpgradeRules.RefundSpent(spentSteel);
        spentSteel.Clear();
        current = null;
        stage = Stage.Idle;
    }

    void FinishCast()
    {
        Recipe r = current;
        current = null;
        stage = Stage.Idle;
        spentSteel.Clear();
        if (r == null) return;

        int amount = r.output;
        bool rubber = r.rubber;
        if (rubber) RubberTotal += amount; else CastTotal += amount;
        inTransit += amount;

        Logistics.Request(new HaulJob
        {
            from = Building,
            to = HallBuilding(),
            prefer = Building,
            onDeliver = () => DeliverParts(amount, rubber),
            onAbort = loaded => DeliverParts(amount, rubber)   // продукцию не теряем
        });
    }

    void DeliverParts(int amount, bool rubber)
    {
        inTransit = Mathf.Max(0, inTransit - amount);
        if (ResourceManager.Instance == null) return;
        if (rubber) ResourceManager.Instance.AddRubber(amount);
        else ResourceManager.Instance.AddParts(amount);
    }

    // ---------- Работа и жар ----------

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (Building == null) return;
        float dt = Time.deltaTime;

        if (Building.IsRuined)
        {
            Heat = 0f;
            BrokenLeft = 0f;
            tinted = false;   // цвет руин ведёт BuildingInstance
            return;
        }

        bool working = stage == Stage.Casting && !Broken && Building.isProducing;

        if (Broken)
        {
            BrokenLeft = Mathf.Max(0f, BrokenLeft - dt);
            Heat = Mathf.Max(0f, Heat - IdleCooling * dt);
            if (!Broken) WarManager.Notify("Литейная: котлы остыли, можно снова плавить", true);
        }
        else if (working)
        {
            Heat = Mathf.Clamp(Heat + HeatRate(Mode) * dt, 0f, MaxHeat);

            float speed = FurnaceSpeed[(int)Mode] * SpeedByLevel[Lvl]
                        * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
            timer -= dt * speed;

            CheckHeat();
            if (Building.IsRuined) return;   // сгорела
            if (!Broken && timer <= 0f) FinishCast();
        }
        else
        {
            // Без работы котлы остывают (на слабом режиме - не медленнее, чем при работе)
            Heat = Mathf.Max(0f, Heat - Mathf.Max(IdleCooling, -FurnaceHeat[(int)Furnace.Low]) * dt);
        }

        if (warned && Heat < WarnHeat - 15f) warned = false;

        // Плавить постоянно
        if (AutoCast && stage == Stage.Idle && !Broken)
        {
            autoTimer -= dt;
            if (autoTimer <= 0f)
            {
                autoTimer = 3f;
                Recipe r = Recipes[Mathf.Clamp(autoRecipe, 0, Recipes.Length - 1)];
                if (CastProblem(r) == "") StartCast(r);
            }
        }

        UpdateLook(working, dt);
    }

    void CheckHeat()
    {
        // Предохранитель: мощные режимы сбрасываются, пока не поздно
        if (Safety && Mode >= Furnace.High && Heat >= SafetyHeat)
        {
            Mode = Furnace.Normal;
            WarManager.Notify($"Литейная: предохранитель сбросил котлы на «Обычный» (жар {Heat:F0}%)", true);
            return;
        }

        if (!warned && Heat >= WarnHeat && HeatRate(Mode) > 0f)
        {
            warned = true;
            WarManager.Notify($"Литейная раскалилась ({Heat:F0}%)! Сбавьте жар, иначе авария или пожар", false);
        }

        if (Heat >= MaxHeat) Overheat();
    }

    void Overheat()
    {
        Overheats++;
        Mode = Furnace.Normal;
        Vector2 c = Building.Center;
        CombatFx.Ring(c, 2.2f, new Color(1f, 0.55f, 0.15f, 0.8f), 0.6f);

        if (Random.value < FireChance) Burn();
        else
        {
            BrokenLeft = BreakdownSeconds;
            for (int i = 0; i < 8; i++) CombatFx.Spark(c + Random.insideUnitCircle * 1.2f, new Color(1f, 0.8f, 0.3f), 0.2f, 0.4f);
            WarManager.Notify($"Литейная перегрелась — авария! Котлы остывают {BreakdownSeconds:F0} с, плавка ждёт", false);
        }
    }

    // Пожар: литейная сгорает вместе с плавкой (руда и сталь пропали), остаются руины
    void Burn()
    {
        string lost = current != null ? $" Плавка «{current.name}» пропала." : "";
        current = null;
        stage = Stage.Idle;
        spentSteel.Clear();
        AutoCast = false;
        Heat = 0f;
        BrokenLeft = 0f;

        Vector2 c = Building.Center;
        CombatFx.Ring(c, 3f, new Color(1f, 0.3f, 0.1f, 0.85f), 0.9f);
        CombatFx.Ring(c, 1.8f, new Color(1f, 0.75f, 0.2f, 0.85f), 0.6f);
        for (int i = 0; i < 14; i++)
            CombatFx.Spark(c + Random.insideUnitCircle * 1.4f, Random.value < 0.5f ? new Color(1f, 0.4f, 0.1f) : new Color(1f, 0.85f, 0.3f), 0.3f, 0.8f);

        string hurt = HurtThoseInside();
        Building.TakeDamage(Building.MaxHp * 10f);   // без врага - ничего не разграблено, просто руины
        WarManager.Notify("ПОЖАР! Литейная перегрелась и сгорела." + lost + hurt + " Руины отстроят, как после набега", false);
    }

    // Взрыв задевает всех, кто был внутри: каждый с шансом 20% гибнет, остальные теряют 70-99% здоровья.
    // Считаем до того, как здание станет руинами (из руин люди сразу выходят).
    const float BlastDeathChance = 0.2f;
    const float BlastMinDamage = 0.7f;
    const float BlastMaxDamage = 0.99f;

    string HurtThoseInside()
    {
        var inside = new List<CitizenAgent>();
        foreach (var a in CitizenAgent.All)
            if (a != null && !a.IsDead && a.IsInside(Building)) inside.Add(a);
        if (inside.Count == 0) return "";

        int killed = 0, wounded = 0;
        foreach (var a in inside)
        {
            if (Random.value < BlastDeathChance)
            {
                a.TakeDamage(a.MaxHp * 100f);
                killed++;
                continue;
            }
            // Урон считается от полного здоровья, но взрыв сам по себе не добивает: остаётся хотя бы 1 ед.
            float dmg = a.MaxHp * Random.Range(BlastMinDamage, BlastMaxDamage);
            dmg = Mathf.Min(dmg, a.Hp - 1f);
            if (dmg > 0f) a.TakeDamage(dmg);
            wounded++;
        }
        string res = "";
        if (killed > 0) res += $" Погибли при взрыве: {killed}.";
        if (wounded > 0) res += $" Ранены: {wounded}.";
        return res;
    }

    // Литейная краснеет от жара; в аварии - тёмная. Из горна летят искры.
    void UpdateLook(bool working, float dt)
    {
        if (look == null) look = GetComponentInChildren<SpriteRenderer>();
        if (look == null || Building.type == null) return;

        Color baseCol = Building.type.color;
        if (Heat < 1f && !Broken)
        {
            if (tinted) { look.color = baseCol; tinted = false; }
        }
        else
        {
            float k = Heat / MaxHeat;
            if (Heat >= WarnHeat) k = Mathf.Min(1f, k + 0.12f * Mathf.Sin(Time.time * 8f));
            Color hot = Color.Lerp(baseCol, new Color(1f, 0.32f, 0.12f), k * 0.8f);
            if (Broken) hot = Color.Lerp(hot, new Color(0.25f, 0.22f, 0.2f), 0.5f);
            look.color = hot;
            tinted = true;
        }

        if (!working) return;
        fxTimer -= dt;
        if (fxTimer > 0f) return;
        fxTimer = Mode >= Furnace.High ? 0.25f : 0.8f;
        Color sc = Heat >= WarnHeat ? new Color(1f, 0.35f, 0.15f) : new Color(1f, 0.75f, 0.3f);
        CombatFx.Spark(Building.Center + Random.insideUnitCircle * 0.8f, sc, 0.14f, 0.3f);
    }

    // ---------- Панель ----------

    string StatusLine
    {
        get
        {
            if (Broken) return UiKit.Col(UiKit.BadHex, $"Авария: котлы остывают - {BrokenLeft:F0} с");
            switch (stage)
            {
                case Stage.Fetching: return "Несут руду и сталь из Ратуши...";
                case Stage.Casting:
                    float pct = CastFraction * 100f;
                    string name = current != null ? current.name : "плавка";
                    return Building.isProducing ? $"Плавка: <b>{name}</b> ({pct:F0}%)" : UiKit.Col(UiKit.BadHex, $"Плавка стоит - нет литейщиков ({pct:F0}%)");
                default: return "Котлы свободны";
            }
        }
    }

    float CastFraction => stage == Stage.Casting && current != null ? Mathf.Clamp01(1f - timer / current.seconds) : 0f;

    string HeatState()
    {
        if (Broken) return UiKit.Col(UiKit.BadHex, "авария");
        if (Heat >= SafetyHeat) return UiKit.Col(UiKit.BadHex, "<b>на грани!</b>");
        if (Heat >= WarnHeat) return UiKit.Col(UiKit.BadHex, "опасно");
        if (Heat >= 40f) return UiKit.Col(UiKit.WarnHex, "горячо");
        return UiKit.Col(UiKit.GoodHex, "норма");
    }

    const float RecipeRowH = 30f;

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        int shownRecipes = 0;
        foreach (var r in Recipes) if (Unlocked(r)) shownRecipes++;

        const string foldId = "foundry-info";
        string infoText =
            "Жар растёт, только пока идёт плавка; без работы котлы остывают. " +
            $"На 100% жара - перегрев: котлы сбрасываются на «Обычный», литейная стоит {BreakdownSeconds:F0} с (авария), " +
            $"а с шансом {FireChance * 100f:F0}% сгорает вместе с плавкой (пожар). Предохранитель сам сбрасывает режим при {SafetyHeat:F0}%. " +
            $"Улучшение: котлы греются медленнее и реже горят. Отлито всего: {CastTotal}, перегревов: {Overheats}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float stockY = barY + 14f;
        float problemY = stockY + 20f;
        float heatSecY = problemY + (b.isWorking ? 2f : 22f);
        float heatY = heatSecY + UiKit.SectionH;
        float heatBarY = heatY + 20f;
        float modesY = heatBarY + 18f;
        float modeHintY = modesY + 32f;
        float togglesY = modeHintY + 20f;
        float recipesY = togglesY + 28f;
        float rowsY = recipesY + UiKit.SectionH + 2f;
        float workersY = rowsY + shownRecipes * RecipeRowH + 4f;
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        // --- Что происходит ---
        var rm = ResourceManager.Instance;
        int parts = rm != null ? rm.parts : 0;
        int ore = rm != null ? rm.iron : 0;
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, st);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 10f), CastFraction, UiKit.Accent);

        string carry = inTransit > 0 ? " " + UiKit.Col(UiKit.WarnHex, $"(+{inTransit})") : "";
        Rect sr = new Rect(x + 10f, y + stockY, w - 20f, 18f);
        GUI.Label(sr, $"Детали: <b><color={UiKit.AccentHex}>{parts}</color></b>{carry}  " +
            UiKit.Col(UiKit.MutedHex, $"руда {ore} · сталь {SteelAvailable()} · смола {(rm != null ? rm.resin : 0)} · резина {(rm != null ? rm.rubber : 0)}"), small);
        UiKit.Tip(sr, "Детали и резина - в Ратуше. Руда и смола берутся из казны, сталь - со склада Ратуши или Хранилища." +
                      (inTransit > 0 ? $" Несут в Ратушу: {inTransit}." : ""));

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        // --- Жар котлов ---
        UiKit.Section(x + 10f, y + heatSecY, w - 20f, "Котлы");
        GUI.Label(new Rect(x + 10f, y + heatY, w - 20f, 18f), $"Жар: <b>{Heat:F0}%</b> - {HeatState()}", small);
        Rect bar = new Rect(x + 10f, y + heatBarY, w - 20f, 12f);
        float k = Heat / MaxHeat;
        UiKit.Bar(bar, k, k < 0.4f ? UiKit.Good : (k < WarnHeat / MaxHeat ? UiKit.Warn : UiKit.Bad));
        // Метки: где опасно и где предохранитель
        Color old = GUI.color;
        GUI.color = new Color(1f, 0.6f, 0.2f, 0.9f);
        GUI.DrawTexture(new Rect(bar.x + bar.width * WarnHeat / MaxHeat, bar.y - 2f, 2f, bar.height + 4f), Texture2D.whiteTexture);
        if (Safety)
        {
            GUI.color = new Color(0.5f, 0.8f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(bar.x + bar.width * SafetyHeat / MaxHeat, bar.y - 2f, 2f, bar.height + 4f), Texture2D.whiteTexture);
        }
        GUI.color = old;
        UiKit.Tip(new Rect(bar.x, y + heatY, bar.width, 34f),
            $"Оранжевая метка - опасно ({WarnHeat:F0}%), голубая - предохранитель ({SafetyHeat:F0}%). " +
            $"На 100% - авария на {BreakdownSeconds:F0} с или пожар (шанс {FireChance * 100f:F0}%).");

        // --- Режим котлов ---
        bool en = GUI.enabled;
        GUI.enabled = en && !Broken;
        int newMode = UiKit.Tabs(new Rect(x + 10f, y + modesY, w - 18f, 28f), FurnaceNames, (int)Mode);
        GUI.enabled = en;
        if (newMode != (int)Mode && !Broken) Mode = (Furnace)newMode;
        if (Broken) UiKit.Tip(new Rect(x + 10f, y + modesY, w - 20f, 28f), "Авария: режим не сменить, пока котлы остывают");

        float rate = HeatRate(Mode);
        string rateText = rate > 0f ? UiKit.Col(UiKit.WarnHex, $"жар +{rate:F1}/с") : UiKit.Col(UiKit.InfoHex, $"остывает {-rate:F1}/с");
        string risk = rate > 0f ? "  " + UiKit.Col(UiKit.MutedHex, $"· пожар при 100%: {FireChance * 100f:F0}%") : "";
        GUI.Label(new Rect(x + 10f, y + modeHintY, w - 20f, 18f),
            $"Плавка ×{FurnaceSpeed[(int)Mode] * SpeedByLevel[Lvl]:F1}, {rateText}{risk}", small);

        float half = (w - 20f) * 0.48f;
        Rect sfr = new Rect(x + 10f, y + togglesY, half, 22f);
        Safety = GUI.Toggle(sfr, Safety, " Предохранитель");
        UiKit.Tip(sfr, $"При {SafetyHeat:F0}% жара котлы сами переключаются на «Обычный». Выключите - и жгите до упора на свой риск.");
        Rect acr = new Rect(x + 10f + half, y + togglesY, w - 20f - half, 22f);
        AutoCast = GUI.Toggle(acr, AutoCast, " Плавить постоянно");
        UiKit.Tip(acr, "Повторять последний рецепт, пока есть руда и сталь.");

        // --- Рецепты: название · цена · кнопка ---
        UiKit.Section(x + 10f, y + recipesY, w - 20f, "Плавка");
        float by = y + rowsY;
        bool firstOk = true;
        foreach (var r in Recipes)
        {
            if (!Unlocked(r)) continue;
            string p = CastProblem(r);
            bool ok = p == "";
            GUI.Label(new Rect(x + 10f, by + 4f, 130f, 20f), $"<b>{r.name}</b>", st);
            string cost = r.rubber
                ? $"{r.resin} смолы → {r.output} резины"
                : $"{r.ore} руды + {r.steel} стали → {r.output} дет.";
            GUI.Label(new Rect(x + 142f, by + 5f, w - 152f - 96f, 20f), cost, small);
            if (UiKit.Button(new Rect(x + w - 100f, by, 90f, 26f), "Плавить", ok, ok && firstOk ? UiKit.Kind.Primary : UiKit.Kind.Normal,
                    ok ? $"{r.name}: {r.seconds:F0} с на «Обычном» жару" : p))
                StartCast(r);
            if (ok) firstOk = false;
            by += RecipeRowH;
        }

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId, "Как работает жар") && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
