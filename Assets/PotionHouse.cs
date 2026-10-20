using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Зельеварня (только путь Магии). Всё носится физически:
///  - травы берутся из казны Ратуши (их туда принесли со станций): житель несёт их в Зельеварню, потом варка;
///  - готовые зелья несут из Зельеварни в Ратушу или в Хранилище (если оно принимает продукцию);
///  - из Ратуши / Хранилища зелья несут дальше: в Оружейную (отравить лук), в казарму (маг-отравитель).
/// Варка зависит от маны: ниже MinManaPercent не варят, скорость умножается на ManaManager.Factor.
/// Два зелья:
///  - отравления: травы -> зелье (луки с ядом, Маг-отравитель);
///  - замедления: травы + смола (Лесопилка Ур.3) -> зелье (луки, замедляющие цель). Носят в Ратушу.
/// </summary>
public class PotionHouse : MonoBehaviour
{
    public const string TypeName = "Зельеварня";

    public static readonly List<PotionHouse> All = new List<PotionHouse>();

    public const int HerbsPerBrew = 6;
    public const int PotionsPerBrew = 2;
    const float BrewSeconds = 16f;
    public const int MinManaPercent = 20;
    static readonly int[] CapacityByLevel = { 10, 16, 24 };   // сколько готовых зелий может ждать выноса
    static readonly float[] SpeedByLevel = { 1f, 1.3f, 1.6f };

    // Зелье замедления
    public const int SlowHerbs = 4;
    public const int SlowResin = 3;
    public const int SlowPerBrew = 2;
    const float SlowBrewSeconds = 18f;

    enum Stage { Idle, Fetching, Brewing }
    bool brewingSlow;                  // текущая варка - замедление
    public bool AutoSlow;              // что варить постоянно: false - отравление, true - замедление
    Stage stage = Stage.Idle;
    float timer;
    float autoTimer;
    int inTransit;                     // готовые зелья, которые несут из зельеварни

    public bool AutoBrew = true;
    public int BrewedTotal { get; private set; }
    public int InTransit => inTransit;

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public int Capacity => CapacityByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Общий запас зелий: Ратуша + Хранилища ----------

    static BuildingInstance HallBuilding()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    public static int TotalPotions
    {
        get
        {
            var rm = ResourceManager.Instance;
            return (rm != null ? rm.potions : 0) + WarehouseController.TotalPotions;
        }
    }

    /// <summary>
    /// Списать n зелий из одного места: сначала Ратуша, потом Хранилище. source - откуда взяли
    /// (оттуда житель и понесёт). false - нигде нет столько зелий.
    /// </summary>
    public static bool TakePotions(int n, out BuildingInstance source)
    {
        source = null;
        if (n <= 0) return true;
        var rm = ResourceManager.Instance;
        if (rm != null && rm.SpendPotions(n)) { source = HallBuilding(); return true; }
        return WarehouseController.TakePotions(n, out source);
    }

    /// <summary>Вернуть зелья (сорвано обучение / рейс): в то Хранилище, откуда взяли, иначе в Ратушу.</summary>
    public static void ReturnPotions(int n, BuildingInstance source)
    {
        if (n <= 0) return;
        if (source != null)
        {
            WarehouseController w = source.GetComponent<WarehouseController>();
            if (w != null && w.ReturnPotionsHere(n)) return;
        }
        if (ResourceManager.Instance != null) ResourceManager.Instance.AddPotions(n);
    }

    // ---------- Варка ----------

    static bool ManaOk => ManaManager.Instance == null || ManaManager.Instance.Percent >= MinManaPercent;

    public string BrewProblem() { return BrewProblem(false); }

    public string BrewProblem(bool slow)
    {
        if (!DevelopmentPath.IsMagic) return "Нужен путь Магии";
        if (!Works) return "Зельеварня разрушена";
        if (stage != Stage.Idle) return "Уже варят";
        if (!Building.isWorking) return "Нет рабочих";
        if (!ManaOk) return $"Мало маны (нужно от {MinManaPercent}%)";
        if (inTransit + PotionsPerBrew > Capacity) return "Готовые зелья ещё не вынесли";
        var rm = ResourceManager.Instance;
        int herbsNeed = slow ? SlowHerbs : HerbsPerBrew;
        if (rm == null || rm.herbs < herbsNeed) return $"Нет трав в Ратуше (нужно {herbsNeed})";
        if (slow && rm.resin < SlowResin) return $"Нет смолы в Ратуше (нужно {SlowResin})";
        return "";
    }

    public void StartBrew() { StartBrew(false); }

    public void StartBrew(bool slow)
    {
        if (BrewProblem(slow) != "") return;
        int herbsNeed = slow ? SlowHerbs : HerbsPerBrew;
        var rmx = ResourceManager.Instance;
        if (!rmx.SpendHerbs(herbsNeed)) return;
        if (slow && !rmx.SpendResin(SlowResin)) { rmx.AddHerbs(herbsNeed); return; }

        brewingSlow = slow;
        stage = Stage.Fetching;

        // Рабочий идёт в Ратушу за травами и несёт их сюда
        Logistics.Request(new HaulJob
        {
            from = HallBuilding(),
            to = Building,
            prefer = Building,
            onDeliver = () => { timer = slow ? SlowBrewSeconds : BrewSeconds; stage = Stage.Brewing; },
            onAbort = loaded =>
            {
                if (ResourceManager.Instance != null)
                {
                    ResourceManager.Instance.AddHerbs(herbsNeed);
                    if (slow) ResourceManager.Instance.AddResin(SlowResin);
                }
                stage = Stage.Idle;
            }
        });
    }

    void FinishBrew()
    {
        stage = Stage.Idle;
        if (brewingSlow)
        {
            // Зелье замедления - в Ратушу
            int n = SlowPerBrew;
            BrewedTotal += n;
            inTransit += n;
            Logistics.Request(new HaulJob
            {
                from = Building,
                to = HallBuilding(),
                prefer = Building,
                onDeliver = () => DeliverSlow(n),
                onAbort = loaded => DeliverSlow(n)
            });
            return;
        }
        int amount = PotionsPerBrew;
        BrewedTotal += amount;
        inTransit += amount;

        // Хранилище, которое принимает продукцию, иначе Ратуша
        WarehouseController wh = WarehouseController.PickForOutput(Building.Center, amount);
        BuildingInstance dest = wh != null ? wh.Building : HallBuilding();
        if (wh != null) wh.Reserve(amount);

        Logistics.Request(new HaulJob
        {
            from = Building,
            to = dest,
            prefer = Building,
            onDeliver = () => DeliverPotions(amount, wh),
            onAbort = loaded => DeliverPotions(amount, wh)   // зелья не теряем
        });
    }

    void DeliverSlow(int n)
    {
        inTransit = Mathf.Max(0, inTransit - n);
        if (ResourceManager.Instance != null) ResourceManager.Instance.AddSlowPotions(n);
    }

    void DeliverPotions(int amount, WarehouseController wh)
    {
        inTransit = Mathf.Max(0, inTransit - amount);
        if (wh != null)
        {
            wh.Unreserve(amount);
            if (wh.AcceptPotions(amount)) return;
        }
        if (ResourceManager.Instance != null) ResourceManager.Instance.AddPotions(amount);
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();

        if (stage == Stage.Brewing)
        {
            if (!Works || !Building.isProducing) return;
            float mana = ManaOk ? ManaManager.Factor : 0f;
            timer -= Time.deltaTime * SpeedByLevel[Lvl] * mana * Building.HealthFactor * Building.OffHoursFactor;
            if (timer <= 0f) FinishBrew();
            return;
        }

        if (stage == Stage.Idle && AutoBrew)
        {
            autoTimer -= Time.deltaTime;
            if (autoTimer <= 0f)
            {
                autoTimer = 3f;
                if (BrewProblem(AutoSlow) == "") StartBrew(AutoSlow);
            }
        }
    }

    // ---------- Панель ----------

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return "Несут травы из Ратуши...";
                case Stage.Brewing:
                    float pct = Mathf.Clamp01(1f - timer / (brewingSlow ? SlowBrewSeconds : BrewSeconds)) * 100f;
                    string what = brewingSlow ? "замедления" : "отравления";
                    return ManaOk ? $"Варится зелье {what}: {pct:F0}%" : UiKit.Col(UiKit.BadHex, $"Варка стоит - мало маны ({pct:F0}%)");
                default: return "Котёл свободен";
            }
        }
    }

    float BrewFraction
    {
        get
        {
            if (stage != Stage.Brewing) return 0f;
            return Mathf.Clamp01(1f - timer / (brewingSlow ? SlowBrewSeconds : BrewSeconds));
        }
    }

    // Строка рецепта: название · цена · короткая кнопка. true - нажали.
    static bool RecipeRow(float x, float y, float w, string name, string cost, string problem, string tip, bool primary)
    {
        GUI.Label(new Rect(x, y + 4f, 110f, 20f), "<b>" + name + "</b>", UiKit.Body);
        GUI.Label(new Rect(x + 112f, y + 5f, w - 112f - 92f, 20f), cost, UiKit.Small);
        bool ok = problem == "";
        return UiKit.Button(new Rect(x + w - 88f, y, 88f, 26f), "Варить", ok,
            primary ? UiKit.Kind.Primary : UiKit.Kind.Normal, ok ? tip : problem);
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        const string foldId = "potion-info";
        string infoText =
            "Травы со станций уносят в Ратушу («Собрать», курьеры). Повар несёт их из Ратуши в Зельеварню и варит. " +
            "Готовые зелья несут в Ратушу или Хранилище, оттуда - в Оружейную (отравленные луки) и в казарму (маг-отравитель). " +
            $"Смолу даёт Лесопилка Ур.3. Ниже {MinManaPercent}% маны варка стоит. Сварено всего: {BrewedTotal}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float stockY = barY + 14f;
        float herbsY = stockY + 20f;
        float problemY = herbsY + 20f;
        float recipesY = problemY + (b.isWorking ? 2f : 22f);
        float row1Y = recipesY + UiKit.SectionH + 2f;
        float row2Y = row1Y + 30f;
        float autoY = row2Y + 32f;
        float workersY = autoY + 26f;
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        var rm = ResourceManager.Instance;
        int herbs = rm != null ? rm.herbs : 0;
        int resin = rm != null ? rm.resin : 0;
        int mana = ManaManager.Instance != null ? ManaManager.Instance.Percent : 100;

        // --- Что происходит ---
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, st);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 10f), BrewFraction, ManaOk ? UiKit.Info : UiKit.Bad);

        string carry = inTransit > 0 ? "  " + UiKit.Col(UiKit.WarnHex, $"· несут: {inTransit}") : "";
        GUI.Label(new Rect(x + 10f, y + stockY, w - 20f, 18f),
            $"Зелья: отравления <b>{TotalPotions}</b> · замедления <b>{(rm != null ? rm.slowPotions : 0)}</b>{carry}", small);

        string herbCol = herbs >= SlowHerbs ? UiKit.GoodHex : UiKit.WarnHex;
        string manaCol = mana >= MinManaPercent ? UiKit.InfoHex : UiKit.BadHex;
        Rect hr = new Rect(x + 10f, y + herbsY, w - 20f, 18f);
        GUI.Label(hr, $"Травы: <color={herbCol}><b>{herbs}</b></color> · смола: {resin} · мана: <color={manaCol}>{mana}%</color>  " +
                      UiKit.Col(UiKit.MutedHex, $"скорость ×{(mana >= MinManaPercent ? ManaManager.Factor : 0f) * SpeedByLevel[Lvl]:F2}"), small);
        UiKit.Tip(hr, $"Травы и смола - в казне Ратуши. На станциях ещё ждут трав: {BerryStation.TotalHerbs}.");

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        // --- Рецепты ---
        UiKit.Section(x + 10f, y + recipesY, w - 20f, "Варка");
        if (RecipeRow(x + 10f, y + row1Y, w - 20f, "Отравление",
                $"{HerbsPerBrew} трав → {PotionsPerBrew}", BrewProblem(false),
                "Зелье для отравленных луков и мага-отравителя", true))
            StartBrew(false);
        if (RecipeRow(x + 10f, y + row2Y, w - 20f, "Замедление",
                $"{SlowHerbs} трав + {SlowResin} смолы → {SlowPerBrew}", BrewProblem(true),
                "Зелье для луков, замедляющих цель", false))
            StartBrew(true);

        float half = (w - 20f) * 0.55f;
        Rect abr = new Rect(x + 10f, y + autoY, half, 22f);
        AutoBrew = GUI.Toggle(abr, AutoBrew, " Варить постоянно");
        UiKit.Tip(abr, "Зельеварня сама начинает новую варку, когда может.");
        Rect asr = new Rect(x + 10f + half, y + autoY, w - 20f - half, 22f);
        AutoSlow = GUI.Toggle(asr, AutoSlow, AutoSlow ? " замедление" : " отравление");
        UiKit.Tip(asr, "Какое зелье варить постоянно (галочка - замедление).");

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
