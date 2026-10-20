using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Конюшня. Рабочие здания - конюхи. Выращивает лошадей из овса.
///  - Овёс даёт ферма Ур.3 (лежит в казне, виден в панели Ратуши).
///  - Игрок заказывает "Вырастить лошадь" (или включает авторазведение): овёс списывается из казны,
///    конюх несёт его из Ратуши в Конюшню (рейс через Logistics, как в мастерской), потом лошадь растёт.
///  - Лошади - не юниты на карте: они просто числятся в Конюшне (вместимость 4 / 8 / 14).
///  - Конному бойцу (казарма Ур.4) при найме отдаётся одна лошадь. Боец погиб - лошадь погибла.
///    Бойца уволили - лошадь возвращается в Конюшню (если есть место).
///  - Лошади обоза (Обозный двор) считаются "в пути": место под них в конюшне остаётся занятым.
///  - Разрушенная Конюшня лошадей не выдаёт, снесённая - лошади пропадают.
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class StableController : MonoBehaviour
{
    public const string TypeName = "Конюшня";

    public static readonly List<StableController> All = new List<StableController>();

    static readonly int[] CapacityByLevel = { 4, 8, 14 };
    static readonly float[] GrowTimeByLevel = { 90f, 70f, 50f };   // секунд на одну лошадь
    public const int OatsPerHorse = 30;

    enum Stage { Idle, Fetching, Growing }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public int Capacity => CapacityByLevel[Lvl];
    public float GrowTime => GrowTimeByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    public int Horses { get; private set; }        // сколько лошадей стоит в этой Конюшне
    public int BredTotal { get; private set; }     // сколько вырастили за всё время
    public bool AutoBreed { get; set; }            // растить лошадей, пока есть овёс и место

    Stage stage = Stage.Idle;
    float growTimer;
    float autoTimer;

    public bool IsBusy => stage != Stage.Idle;
    public float Progress => stage == Stage.Growing && GrowTime > 0f ? 1f - growTimer / GrowTime : 0f;

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return "Несут овёс из Ратуши...";
                case Stage.Growing: return $"Растёт жеребёнок ({Progress * 100f:F0}%)";
                default: return Horses >= Capacity ? UiKit.Col(UiKit.WarnHex, "Конюшня полна") : "Конюшня свободна";
            }
        }
    }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (Horses > 0) WarManager.Notify($"Конюшню снесли: лошадей потеряно — {Horses}", false);
        if (Teams > 0) WarManager.Notify($"Конюшню снесли: упряжек потеряно — {Teams}", false);
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (Building == null) return;

        if (stage == Stage.Growing && Building.isProducing)
        {
            // Голодные конюхи и дефицит замедляют рост
            growTimer -= Time.deltaTime * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
            if (growTimer <= 0f) FinishGrowing();
        }

        TickTeam();

        if (AutoBreed)
        {
            autoTimer -= Time.deltaTime;
            if (autoTimer <= 0f)
            {
                autoTimer = 1f;
                if (CanStart) StartBreeding();
            }
        }
    }

    // ---------- Лошади в обозе ----------
    // Пока обоз в пути, лошади не стоят в конюшне, но место под них занято:
    // иначе конюшня успевала вырастить новых, и вернувшиеся лошади пропадали.

    public static int HorsesOut { get; private set; }
    public static void Lend(int n) { HorsesOut += Mathf.Max(0, n); }
    public static void Unlend(int n) { HorsesOut = Mathf.Max(0, HorsesOut - Mathf.Max(0, n)); }

    static int TotalCapacity
    {
        get
        {
            int n = 0;
            foreach (var s in All)
                if (s != null && s.Works) n += s.Capacity;
            return n;
        }
    }

    // ---------- Разведение ----------

    public bool CanStart
    {
        get
        {
            var rm = ResourceManager.Instance;
            return Works && Building.isProducing && !IsBusy && Horses < Capacity
                && HorsesAvailable + HorsesOut < TotalCapacity
                && rm != null && rm.oats >= OatsPerHorse;
        }
    }

    public void StartBreeding()
    {
        if (!CanStart) return;
        if (!ResourceManager.Instance.SpendOats(OatsPerHorse)) return;

        stage = Stage.Fetching;

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Конюх идёт в Ратушу за овсом и несёт его сюда
        Logistics.Request(new HaulJob
        {
            from = hall,
            to = Building,
            prefer = Building,
            onDeliver = OnOatsDelivered,
            onAbort = loaded => OnOatsAborted()
        });
    }

    void OnOatsDelivered()
    {
        growTimer = GrowTime;
        stage = Stage.Growing;
    }

    // Рейс сорван (Конюшню снесли, конюха забрали в казарму) - овёс возвращается в казну
    void OnOatsAborted()
    {
        if (ResourceManager.Instance != null) ResourceManager.Instance.Add(0, 0, 0, 0, 0, OatsPerHorse);
        stage = Stage.Idle;
    }

    void FinishGrowing()
    {
        stage = Stage.Idle;
        Horses = Mathf.Min(Capacity, Horses + 1);
        BredTotal++;
        WarManager.Notify($"Конюшня: выращена лошадь (всего {HorsesAvailable})", true);
    }

    // ---------- Упряжки (Конюшня Ур.3) ----------
    // Упряжку (лошадь + доски + сталь) закрепляют за Курьерской: курьеры с упряжкой ходят быстрее и берут больше.
    // Упряжка не расходуется: пока Курьерская стоит, она остаётся у неё. Снесли Курьерскую - упряжки вернулись в Конюшни.

    public const int MinTeamLevel = 3;
    public const int TeamHorses = 1;
    public const int TeamPlanks = 5;
    public const int TeamSteel = 2;
    public const float TeamBuildTime = 40f;
    static readonly int[] TeamCapByLevel = { 0, 0, 6 };

    public int Teams { get; private set; }             // готовые упряжки в этой Конюшне
    public int TeamsBuiltTotal { get; private set; }

    bool teamBuilding;
    float teamTimer;

    public int TeamCapacity => TeamCapByLevel[Lvl];
    public bool TeamsUnlocked => Building != null && Building.level >= MinTeamLevel;
    public float TeamProgress => teamBuilding ? 1f - teamTimer / TeamBuildTime : 0f;

    static bool TeamMaterialsOk()
    {
        var st = TownHallStorage.Instance;
        return st != null
            && UpgradeRules.CountAtLeast(st, RefinedKind.Planks, QualityTier.Medium) >= TeamPlanks
            && UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) >= TeamSteel;
    }

    public bool CanStartTeam
    {
        get
        {
            return Works && Building.isProducing && TeamsUnlocked && !teamBuilding
                && Teams < TeamCapacity && Horses >= TeamHorses && TeamMaterialsOk();
        }
    }

    public void StartTeam()
    {
        if (!CanStartTeam) return;
        var st = TownHallStorage.Instance;
        UpgradeRules.SpendAtLeast(st, RefinedKind.Planks, QualityTier.Medium, TeamPlanks);
        UpgradeRules.SpendAtLeast(st, RefinedKind.Steel, QualityTier.Medium, TeamSteel);
        Horses -= TeamHorses;
        teamBuilding = true;
        teamTimer = TeamBuildTime;
    }

    void TickTeam()
    {
        if (!teamBuilding) return;
        if (!Works || !Building.isProducing) return;

        teamTimer -= Time.deltaTime * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
        if (teamTimer > 0f) return;

        teamBuilding = false;
        Teams = Mathf.Min(TeamCapacity, Teams + 1);
        TeamsBuiltTotal++;
        WarManager.Notify($"Конюшня: готова упряжка (всего свободных {TeamsAvailable})", true);
    }

    /// <summary>Сколько свободных упряжек во всех целых Конюшнях.</summary>
    public static int TeamsAvailable
    {
        get
        {
            int n = 0;
            foreach (var s in All)
                if (s != null && s.Works) n += s.Teams;
            return n;
        }
    }

    /// <summary>Забрать упряжку для Курьерской. false - свободных нет.</summary>
    public static bool TryTakeTeam()
    {
        foreach (var s in All)
        {
            if (s == null || !s.Works || s.Teams <= 0) continue;
            s.Teams--;
            return true;
        }
        return false;
    }

    /// <summary>Вернуть упряжку в Конюшню. false - места нет.</summary>
    public static bool ReturnTeam()
    {
        foreach (var s in All)
        {
            if (s == null || !s.Works || s.Teams >= s.TeamCapacity) continue;
            s.Teams++;
            return true;
        }
        return false;
    }

    const float TeamSectionH = UiKit.SectionH + 20f + 32f;

    void DrawTeamSection(float x, float y, float w, GUIStyle small)
    {
        if (!TeamsUnlocked) return;

        float cy = UiKit.Section(x, y, w, "Упряжки");
        string state = teamBuilding ? "  " + UiKit.Col(UiKit.WarnHex, $"делается {TeamProgress * 100f:F0}%") : "";
        GUI.Label(new Rect(x, cy, w, 18f),
            $"Готово: <b>{Teams}/{TeamCapacity}</b>  " + UiKit.Col(UiKit.MutedHex, $"свободных везде: {TeamsAvailable}") + state, small);
        cy += 20f;

        string cost = $"{TeamHorses} лошадь, {TeamPlanks} досок, {TeamSteel} стали";
        string why = null;
        if (teamBuilding) why = "Упряжка уже делается";
        else if (Teams >= TeamCapacity) why = "Место для упряжек занято";
        else if (Horses < TeamHorses) why = "Нет свободной лошади";
        else if (!TeamMaterialsOk()) why = $"Не хватает материалов: {TeamPlanks} досок и {TeamSteel} стали";
        else if (!CanStartTeam) why = "Конюшня не работает";
        if (UiKit.Button(new Rect(x, cy, w, 28f), "Сделать упряжку", CanStartTeam, UiKit.Kind.Normal,
                (why == null ? "" : why + ". ") + $"Цена: {cost}. Упряжка ускоряет курьеров."))
            StartTeam();
    }

    // ---------- Общий запас лошадей (для казармы и обозов) ----------

    /// <summary>Сколько лошадей можно выдать (во всех целых Конюшнях).</summary>
    public static int HorsesAvailable
    {
        get
        {
            int n = 0;
            foreach (var s in All)
                if (s != null && s.Works) n += s.Horses;
            return n;
        }
    }

    /// <summary>Забрать одну лошадь для всадника или обоза. false - лошадей нет.</summary>
    public static bool TryTakeHorse()
    {
        foreach (var s in All)
        {
            if (s == null || !s.Works || s.Horses <= 0) continue;
            s.Horses--;
            return true;
        }
        return false;
    }

    /// <summary>Вернуть лошадь (всадника уволили / обучение отменили / обоз вернулся). false - свободного места нет.</summary>
    public static bool ReturnHorse()
    {
        foreach (var s in All)
        {
            if (s == null || !s.Works || s.Horses >= s.Capacity) continue;
            s.Horses++;
            return true;
        }
        return false;
    }

    // ---------- Панель здания ----------

    string BreedProblem()
    {
        var rm = ResourceManager.Instance;
        if (!Works || !Building.isProducing) return "Конюшня не работает";
        if (IsBusy) return "Уже идёт выращивание";
        if (Horses >= Capacity || HorsesAvailable + HorsesOut >= TotalCapacity) return "Нет места для лошади";
        if (rm == null || rm.oats < OatsPerHorse) return $"Не хватает овса: нужно {OatsPerHorse}";
        return null;
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        int lvl = b.level;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        const string foldId = "stable-info";
        string infoText =
            $"Лошадь растёт {GrowTime:F0} с. Овёс даёт ферма Ур.3. Лошади нужны конным войскам (казарма Ур.4) и обозам; " +
            $"гибнут вместе со всадником, уволенный всадник возвращает лошадь. Вырастили всего: {BredTotal}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float horsesY = barY + 14f;
        float oatsY = horsesY + 20f;
        float problemY = oatsY + 20f;
        float btnY = problemY + (b.isWorking ? 4f : 24f);
        float autoY = btnY + 36f;
        float teamY = autoY + 28f;
        float teamH = TeamsUnlocked ? TeamSectionH + 6f : 0f;
        float workersY = teamY + teamH;
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {lvl}");

        // --- Что происходит ---
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, st);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 10f), Progress, UiKit.Accent);

        var rm = ResourceManager.Instance;
        int oats = rm != null ? rm.oats : 0;
        string oatsCol = oats >= OatsPerHorse ? UiKit.GoodHex : UiKit.WarnHex;
        string outNote = HorsesOut > 0 ? $"  · в обозах: {HorsesOut}" : "";
        GUI.Label(new Rect(x + 10f, y + horsesY, w - 20f, 18f),
            $"Лошадей: <b>{Horses}/{Capacity}</b>  " + UiKit.Col(UiKit.MutedHex, $"во всех конюшнях: {HorsesAvailable}{outNote}"), small);
        GUI.Label(new Rect(x + 10f, y + oatsY, w - 20f, 18f),
            $"Овёс: <color={oatsCol}><b>{oats}</b></color>  " + UiKit.Col(UiKit.MutedHex, $"на лошадь {OatsPerHorse}"), small);

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        // --- Действия ---
        string why = BreedProblem();
        if (UiKit.Button(new Rect(x + 10f, y + btnY, w - 20f, 32f), "Вырастить лошадь", CanStart, UiKit.Kind.Primary,
                why ?? $"Спишет {OatsPerHorse} овса из казны"))
            StartBreeding();

        Rect ar = new Rect(x + 10f, y + autoY, w - 20f, 24f);
        bool auto = GUI.Toggle(ar, AutoBreed, " Разводить автоматически");
        UiKit.Tip(ar, "Конюшня сама заказывает новую лошадь, пока есть овёс и место.");
        if (auto != AutoBreed) AutoBreed = auto;

        DrawTeamSection(x + 10f, y + teamY, w - 20f, small);

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}