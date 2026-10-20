using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Кузня мастера (только путь Военного дела). Три раздела в панели (переключаются вкладками):
///
///  1. «Оружие» - доводка готового оружия из Оружейной.
///     - В панели - список мечей и луков со складов Оружейных, которые ещё не доводили.
///     - Доводка стоит золото и сталь. Открывается окно с мини-игрой: по шкале бегает ползунок,
///       нужно ударить (кнопка или Пробел), когда он в центре. Три удара - три свойства:
///         заточка (урон), закалка (прочность), баланс (скорость удара).
///       Чем точнее удар, тем больше прибавка. С каждым ударом ползунок быстрее.
///     - Итог - звёзды (★ - ★★★) в названии оружия. Доводят оружие один раз.
///
///  2. «Снаряжение» - изготовление полевых укреплений (см. FieldPost): Заслон, Перевалочный пункт, Полевая башня;
///     ветка «Тяжёлая стратегия» - ещё Заслон с шипами и Заслон на колёсах.
///     - Цена (дерево, доски, сталь) списывается сразу, потом идёт изготовление (FieldPost.Spec.craftSeconds).
///     - Готовая вещь попадает в запас Ратуши (FieldGear). Оттуда её берут в оборону (кнопка «Заслоны» [B]
///       во время войны) и в набег (окно «В бой!»).
///     - Сила вещи (прочность и лечебный запас) растёт с уровнем кузни.
///     - Изготавливает один мастер за раз. Кузню снесли посреди работы - материалы возвращаются.
///     - Там же - понтонные секции для переправ через реку (PontoonStock, Crossing): их делают подмастерья
///       параллельно с укреплениями, партиями по PontoonStock.PerBatch.
///
///  3. «Ловушки» - боевая переделка обычных ловушек (их собирает Инженерный двор, сюда они приходят с рынка).
///     - Берётся обычная ловушка из запаса Ратуши + дерево, доски, сталь; через Traps.ConvertSeconds
///       она возвращается в запас БОЕВОЙ: такую ставят прямо в бою (дома во время войны и в набеге).
///     - Сила боевой взводки = сила кузни (урон ловушки и её прочность, пока её ставят).
///     - Переделка идёт параллельно с изготовлением заслонов (это другой мастер у горна).
///
/// Сила кузни растёт с уровнем (Ур.2 / Ур.3 - прибавки больше).
/// Для работы в кузне должен быть мастер (рабочий).
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class WeaponForge : MonoBehaviour
{
    public const string TypeName = "Кузня мастера";

    public const int CostGold = 60;
    public const int CostSteel = 1;
    static readonly float[] PowerByLevel = { 1f, 1.25f, 1.5f };
    static readonly float[] SpeedByRound = { 0.9f, 1.25f, 1.6f };   // проходов шкалы в секунду
    static readonly string[] Aspects = { "Заточка", "Закалка", "Баланс" };
    static readonly string[] AspectHints = { "урон", "прочность", "скорость удара" };

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public float Power => PowerByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;
    public int ForgedTotal { get; private set; }

    // ---------- Вкладки панели ----------
    int tab;   // 0 - оружие, 1 - снаряжение, 2 - ловушки

    // ---------- Боевая переделка ловушек ----------
    TrapKit converting;
    float convertTimer;
    public int TrapsConvertedTotal { get; private set; }
    public bool IsConverting => converting != null;
    public float ConvertProgress => converting == null ? 0f : Mathf.Clamp01(1f - convertTimer / Traps.ConvertSeconds);

    // ---------- Изготовление полевого снаряжения ----------
    bool crafting;
    PostKind craftKind;
    float craftTimer;
    float craftStrength = 1f;
    public int GearCraftedTotal { get; private set; }

    public bool IsCraftingGear => crafting;

    // ---------- Понтонные секции (переправы через реку) ----------
    bool pontoonCrafting;
    float pontoonTimer;
    public int PontoonsMadeTotal { get; private set; }
    public float PontoonProgress => pontoonCrafting ? Mathf.Clamp01(1f - pontoonTimer / PontoonStock.CraftSeconds) : 0f;
    public float GearProgress
    {
        get
        {
            if (!crafting) return 0f;
            float total = Mathf.Max(0.1f, FieldPost.Specs[(int)craftKind].craftSeconds);
            return Mathf.Clamp01(1f - craftTimer / total);
        }
    }

    // ---------- Мини-игра ----------
    static WeaponForge session;          // какая кузня сейчас ведёт доводку (окно одно на всех)
    Weapon working;
    int round;
    readonly float[] hits = new float[3];
    float slider;                        // 0..1
    float dir = 1f;
    float pauseUntil;                    // после удара - короткая пауза, чтобы увидеть результат
    string lastHitText = "";
    bool finished;
    Vector2 listScroll;

    static Rect gameRect;
    public static bool SessionOpen => session != null;
    public static bool IsPointerOver(Vector2 gp) { return session != null && gameRect.Contains(gp); }

    void Awake() { Building = GetComponent<BuildingInstance>(); }

    void OnDestroy()
    {
        if (session == this) session = null;

        // Снесли кузню посреди изготовления - материалы возвращаются
        if (crafting && gameObject.scene.isLoaded)
        {
            FieldPost.Refund(FieldPost.Specs[(int)craftKind]);
            crafting = false;
            WarManager.Notify("Кузню снесли: изготовление прервано, материалы возвращены", false);
        }
        // ...и понтоны
        if (pontoonCrafting && gameObject.scene.isLoaded)
        {
            PontoonStock.Refund();
            pontoonCrafting = false;
        }
        // ...и ловушка, которую переделывали, возвращается в запас обычной
        if (converting != null && gameObject.scene.isLoaded)
        {
            TrapStock.Add(converting);
            Traps.RefundConvert();
            converting = null;
            WarManager.Notify("Кузню снесли: переделка ловушки прервана, ловушка и материалы возвращены", false);
        }
    }

    // ---------- Что можно доводить ----------

    static List<ArmoryController.StockEntry> Candidates()
    {
        var list = new List<ArmoryController.StockEntry>();
        foreach (WeaponKind k in new[] { WeaponKind.Sword, WeaponKind.Bow })
            foreach (var e in ArmoryController.Available(k))
                if (e.weapon != null && !e.weapon.forged) list.Add(e);
        list.Sort((a, b) => b.weapon.Score.CompareTo(a.weapon.Score));
        return list;
    }

    string StartProblem()
    {
        if (!DevelopmentPath.IsWar) return "Нужен путь Военного дела";
        if (!Works) return "Кузня разрушена";
        if (!Building.isWorking) return "Нет мастера";
        if (session != null) return "Уже идёт доводка";
        var rm = ResourceManager.Instance;
        if (rm == null || rm.gold < CostGold) return $"Нужно {CostGold} золота";
        var st = TownHallStorage.Instance;
        if (st == null || UpgradeRules.CountAtLeast(st, RefinedKind.Steel, QualityTier.Medium) < CostSteel) return $"Нужна сталь: {CostSteel}";
        return "";
    }

    void Begin(Weapon w)
    {
        if (w == null || w.forged || StartProblem() != "") return;
        var rm = ResourceManager.Instance;
        if (!rm.Spend(CostGold, 0, 0, 0)) return;
        UpgradeRules.SpendAtLeast(TownHallStorage.Instance, RefinedKind.Steel, QualityTier.Medium, CostSteel);

        session = this;
        working = w;
        round = 0;
        finished = false;
        slider = 0f;
        dir = 1f;
        pauseUntil = 0f;
        lastHitText = "";
        for (int i = 0; i < hits.Length; i++) hits[i] = 0f;
    }

    void Strike()
    {
        if (finished || Time.unscaledTime < pauseUntil) return;
        float acc = 1f - Mathf.Abs(slider - 0.5f) * 2f;          // 1 - точно в центр, 0 - край
        acc = Mathf.Clamp01(acc * 1.08f);                          // небольшая поблажка
        hits[round] = acc;
        lastHitText = acc >= 0.95f ? "<color=#FFD040>Идеально!</color>"
                    : acc >= 0.8f ? "<color=#80FF80>Отлично</color>"
                    : acc >= 0.55f ? "<color=#C0E080>Хорошо</color>"
                    : "<color=#FF9060>Мимо центра</color>";
        round++;
        pauseUntil = Time.unscaledTime + 0.6f;

        if (round >= hits.Length)
        {
            finished = true;
            working.ApplyForge(hits[0], hits[1], hits[2], Power);
            ForgedTotal++;
            WarManager.Notify($"Доводка готова: {working.Name}", true);
        }
    }

    void Close()
    {
        // Ушли, не доделав, - оружие доводится тем, что успели (оставшиеся удары - слабые)
        if (!finished && working != null)
        {
            for (int i = round; i < hits.Length; i++) hits[i] = 0.2f;
            working.ApplyForge(hits[0], hits[1], hits[2], Power);
            ForgedTotal++;
        }
        working = null;
        if (session == this) session = null;
    }

    // ---------- Изготовление полевого снаряжения ----------

    string GearProblem(int kind)
    {
        FieldPost.Spec s = FieldPost.Specs[kind];
        if (!DevelopmentPath.IsWar) return "Нужен путь Военного дела";
        if (!s.crafted) return "Не изготавливается - ставится на месте за дерево";
        if (!FieldPost.Allowed((PostKind)kind)) return FieldPost.NeedText((PostKind)kind);
        if (!Works) return "Кузня разрушена";
        if (!Building.isWorking) return "Нет мастера";
        if (crafting) return "Уже идёт изготовление";
        if (!FieldPost.CanPay(s)) return "Не хватает: " + FieldPost.MissingText(s);
        return "";
    }

    void BeginGear(int kind)
    {
        if (GearProblem(kind) != "") return;
        FieldPost.Spec s = FieldPost.Specs[kind];
        FieldPost.Pay(s);

        crafting = true;
        craftKind = (PostKind)kind;
        craftTimer = Mathf.Max(0.5f, s.craftSeconds);
        craftStrength = Power;
        WarManager.Notify($"Кузня: начато изготовление — {s.name}", true);
    }

    void TickGear()
    {
        if (!crafting) return;
        if (!Works || !Building.isProducing) return;   // нет мастера - работа стоит

        // Голодные мастера и дефицит замедляют работу, как в других мастерских
        craftTimer -= Time.deltaTime * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
        if (craftTimer > 0f) return;

        crafting = false;
        FieldPost.Spec s = FieldPost.Specs[(int)craftKind];
        FieldGear.Add(new FieldGear.Item(craftKind, craftStrength));
        GearCraftedTotal++;
        WarManager.Notify($"Кузня: готово — {s.name} (в запасе Ратуши: {FieldGear.Count(craftKind)})", true);
    }

    // ---------- Понтонные секции ----------

    string PontoonProblem()
    {
        if (!DevelopmentPath.IsWar) return "Нужен путь Военного дела";
        if (!Works) return "Кузня разрушена";
        if (!Building.isWorking) return "Нет мастера";
        if (pontoonCrafting) return "Уже делают";
        if (PontoonStock.Count + PontoonStock.PerBatch > PontoonStock.Max) return "Запас секций полон";
        string miss = PontoonStock.Missing();
        if (miss != "") return "Не хватает: " + miss;
        return "";
    }

    void BeginPontoons()
    {
        if (PontoonProblem() != "" || !PontoonStock.Pay()) return;
        pontoonCrafting = true;
        pontoonTimer = PontoonStock.CraftSeconds;
        WarManager.Notify("Кузня: делают понтонные секции для переправы", true);
    }

    void TickPontoons()
    {
        if (!pontoonCrafting) return;
        if (!Works || !Building.isProducing) return;
        pontoonTimer -= Time.deltaTime * Power * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
        if (pontoonTimer > 0f) return;
        pontoonCrafting = false;
        PontoonStock.Add(PontoonStock.PerBatch);
        PontoonsMadeTotal += PontoonStock.PerBatch;
        WarManager.Notify($"Кузня: готовы понтонные секции (в запасе: {PontoonStock.Count}). Ставить — кнопка «Переправы» [P]", true);
    }

    // ---------- Боевая переделка ловушек ----------

    string ConvertProblem()
    {
        if (!DevelopmentPath.IsWar) return "Нужен путь Военного дела";
        if (!Works) return "Кузня разрушена";
        if (!Building.isWorking) return "Нет мастера";
        if (converting != null) return "Уже переделывают";
        string miss = Traps.ConvertMissing();
        if (miss != "") return "Не хватает: " + miss;
        return "";
    }

    void BeginConvert(TrapKit sample)
    {
        if (ConvertProblem() != "") return;
        TrapKit k = TrapStock.TakeLike(TrapStock.Hall, sample);
        if (k == null) return;
        if (!Traps.PayConvert()) { TrapStock.Add(k); return; }
        converting = k;
        convertTimer = Traps.ConvertSeconds;
        WarManager.Notify($"Кузня: боевая переделка — {k.Name}", true);
    }

    void TickConvert()
    {
        if (converting == null) return;
        if (!Works || !Building.isProducing) return;
        convertTimer -= Time.deltaTime * Building.SpeedMultiplier * Building.OffHoursFactor * Building.HealthFactor;
        if (convertTimer > 0f) return;

        TrapKit k = converting;
        converting = null;
        k.combat = true;
        k.power = Power;
        k.charges = -1;   // боевую взводку собирают заново - срабатывания полные
        TrapStock.Add(k);
        TrapsConvertedTotal++;
        WarManager.Notify($"Кузня: готово — {k.Name} (боевых в запасе: {TrapStock.Count(true)})", true);
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();

        TickGear();
        TickConvert();
        TickPontoons();

        if (session != this) return;
        if (!Works) { Close(); return; }

        if (!finished && Time.unscaledTime >= pauseUntil)
        {
            slider += dir * SpeedByRound[Mathf.Min(round, SpeedByRound.Length - 1)] * Time.unscaledDeltaTime;
            if (slider >= 1f) { slider = 1f; dir = -1f; }
            if (slider <= 0f) { slider = 0f; dir = 1f; }
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.spaceKey.wasPressedThisFrame) { if (finished) Close(); else Strike(); }
            if (kb.escapeKey.wasPressedThisFrame) Close();
        }
    }

    // ---------- Панель здания ----------

    static GUIStyle MidLeft(GUIStyle from)
    {
        return new GUIStyle(from) { alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
    }

    // Маленький «?» с подсказкой в правом углу строки
    static void Help(float x, float y, string tip)
    {
        Rect r = new Rect(x - 16f, y, 16f, UiKit.RowH);
        GUI.Label(r, UiKit.Col(UiKit.InfoHex, "?"), UiKit.Body);
        UiKit.Tip(r, tip);
    }

    // Строка «что делается»: текст слева и полоска справа (или приглушённое «свободно»)
    static void ProgressRow(float x, float y, float w, bool busy, string what, float frac, bool stalled, string idle)
    {
        GUIStyle body = MidLeft(UiKit.Body);
        if (busy)
        {
            GUI.Label(new Rect(x, y, w - 150f, UiKit.RowH), what + (stalled ? UiKit.Col(UiKit.WarnHex, " — стоит") : ""), body);
            UiKit.Bar(new Rect(x + w - 140f, y + 4f, 140f, 14f), frac, stalled ? UiKit.Warn : UiKit.Good, $"{frac * 100f:F0}%");
        }
        else GUI.Label(new Rect(x, y, w, UiKit.RowH), UiKit.Col(UiKit.MutedHex, idle), body);
    }

    const float TabsH = 26f;

    float HeaderH(BuildingInstance b) { return UiKit.RowH + (b.isWorking ? 0f : UiKit.RowH) + 4f; }
    float FooterH(BuildingInstance b) { return 4f + 30f + 8f + UpgradeRules.SectionHeight(b); }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        GUIStyle small = MidLeft(UiKit.Small);

        float tabH = tab == 0 ? WeaponTabHeight(b, 0f) : (tab == 1 ? GearTabHeight(b, 0f) : TrapTabHeight(b, 0f));
        float h = UiKit.WindowH(HeaderH(b) + TabsH + 8f + tabH + FooterH(b));
        Rect c = UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");
        float cx = c.x, cw = c.width, cy = c.y;

        // --- Шапка ---
        GUI.Label(new Rect(cx, cy, cw - 20f, UiKit.RowH),
            UiKit.Col(UiKit.MutedHex, "Сила кузни ") + $"<b>×{Power:F2}</b>" + UiKit.Col(UiKit.MutedHex, "  (растёт с уровнем)"), MidLeft(UiKit.Body));
        Help(cx + cw, cy, "Три работы: доводка оружия (мини-игра), полевые укрепления и боевая переделка ловушек. " +
                          "Укрепления и ловушки делают разные мастера - параллельно. Нужен мастер (рабочий).");
        cy += UiKit.RowH;
        if (!b.isWorking)
        {
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.BadHex, b.StatusText), MidLeft(UiKit.Body));
            cy += UiKit.RowH;
        }
        cy += 4f;

        // --- Вкладки ---
        string[] names = { "Оружие", "Снаряжение", "Ловушки" };
        if (crafting) names[1] += $" {GearProgress * 100f:F0}%";
        else if (pontoonCrafting) names[1] += $" {PontoonProgress * 100f:F0}%";
        if (converting != null) names[2] += $" {ConvertProgress * 100f:F0}%";
        int prevTab = tab;
        int newTab = UiKit.Tabs(new Rect(cx, cy, cw, TabsH), names, tab);
        cy += TabsH + 8f;

        if (tab == 0) cy += DrawWeaponTab(cx, cy, cw, b, small);
        else if (tab == 1) cy += DrawGearTab(cx, cy, cw, b, small);
        else cy += DrawTrapTab(cx, cy, cw, b, small);

        // --- Мастера и улучшение (общие для всех вкладок) ---
        cy += 4f;
        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(cx, cy, b);
        cy += 30f + 8f;
        UiKit.Line(cx, cy - 5f, cw);
        UpgradeRules.DrawUpgradeSection(cx, cy, cw, b);

        if (newTab != prevTab) tab = newTab;
        return new Rect(x, y, w + 5f, h);
    }

    // ----- Вкладка «Оружие» -----

    const float WeaponRowH = 30f;

    float WeaponListH(int n) { return Mathf.Clamp(n * WeaponRowH, WeaponRowH * 2f, WeaponRowH * 6f); }

    // top оставлен ради прежней сигнатуры - высота считается только по содержимому вкладки
    float WeaponTabHeight(BuildingInstance b, float top)
    {
        float hh = UiKit.RowH + (StartProblem() != "" ? UiKit.RowH : 0f);
        return top + hh + UiKit.SectionH + WeaponListH(Candidates().Count) + 6f;
    }

    float DrawWeaponTab(float x, float y, float w, BuildingInstance b, GUIStyle small)
    {
        float cy = y;
        var cand = Candidates();
        float listH = WeaponListH(cand.Count);

        GUI.Label(new Rect(x, cy, w - 20f, UiKit.RowH),
            UiKit.Col(UiKit.MutedHex, "Доводка: ") + UiKit.Cost(CostGold, 0, 0, 0) + UiKit.Col(UiKit.MutedHex, " · ") + $"{CostSteel} стали"
            + UiKit.Col(UiKit.MutedHex, $"   доведено: {ForgedTotal}"), MidLeft(UiKit.Body));
        Help(x + w, cy, "Мини-игра: три удара по наковальне - заточка (урон), закалка (прочность), баланс (скорость удара). " +
                        "Бейте, когда ползунок в зелёном центре: чем точнее, тем больше прибавка. Итог - звёзды ★ в названии. Оружие доводят один раз.");
        cy += UiKit.RowH;

        string p = StartProblem();
        if (p != "")
        {
            GUI.Label(new Rect(x, cy, w, UiKit.RowH), UiKit.Col(UiKit.WarnHex, p), MidLeft(UiKit.Body));
            cy += UiKit.RowH;
        }

        cy = UiKit.Section(x, cy, w, "Оружие без доводки");
        float innerW = w - 14f;
        listScroll = GUI.BeginScrollView(new Rect(x, cy, w, listH), listScroll,
            new Rect(0f, 0f, innerW, Mathf.Max(listH, cand.Count * WeaponRowH)));
        if (cand.Count == 0)
        {
            Rect er = new Rect(0f, 0f, innerW, 40f);
            GUI.Label(er, UiKit.Col(UiKit.MutedHex, "Нет оружия без доводки"), UiKit.Small);
            UiKit.Tip(er, "Скуйте мечи и луки в Оружейной.");
        }
        for (int i = 0; i < cand.Count; i++)
        {
            Weapon wpn = cand[i].weapon;
            Rect lr = new Rect(0f, i * WeaponRowH, innerW - 96f, WeaponRowH - 2f);
            GUI.Label(lr, $"<b>{wpn.Name}</b>  " + UiKit.Col(UiKit.MutedHex, $"{wpn.Percent}%"), small);
            UiKit.Tip(lr, wpn.StatsText);
            if (UiKit.Button(new Rect(innerW - 92f, i * WeaponRowH + 2f, 90f, WeaponRowH - 6f), "Доводка", p == "", UiKit.Kind.Primary,
                    p == "" ? $"Открыть мини-игру: {CostGold} золота + {CostSteel} стали" : p))
                Begin(wpn);
        }
        GUI.EndScrollView();
        cy += listH + 6f;
        return cy - y;
    }

    // ----- Вкладка «Снаряжение» -----

    const float GearBlockH = 50f;
    float PontoonBlockH => Rivers.Any ? UiKit.SectionH + GearBlockH : 0f;   // понтоны - только если на карте есть река

    // Что Кузня умеет делать сейчас: общие укрепления + изделия своей ветки (быстрый заслон не изготавливается)
    static List<int> GearKinds()
    {
        var list = new List<int>();
        for (int k = 0; k < FieldPost.Specs.Length; k++)
            if (FieldPost.Specs[k].crafted && FieldPost.Allowed((PostKind)k)) list.Add(k);
        return list;
    }

    float GearTabHeight(BuildingInstance b, float top)
    {
        return top + UiKit.SectionH + GearKinds().Count * GearBlockH + UiKit.RowH + 6f + PontoonBlockH;
    }

    // Блок «изделие»: название и запас, цена и время, кнопка справа
    bool GearBlock(float x, float y, float w, string name, string right, string cost, string tip, string btn, string problem, bool primary)
    {
        GUIStyle body = MidLeft(UiKit.Body);
        Rect nr = new Rect(x, y, w - 130f, UiKit.RowH);
        GUI.Label(nr, $"<b>{name}</b>  " + right, body);
        UiKit.Tip(nr, tip);
        GUI.Label(new Rect(x, y + UiKit.RowH, w - 130f, 20f), cost, MidLeft(UiKit.Small));
        bool click = UiKit.Button(new Rect(x + w - 122f, y + 8f, 122f, 28f), btn, problem == "", primary ? UiKit.Kind.Primary : UiKit.Kind.Normal,
            problem == "" ? tip : problem);
        UiKit.Line(x, y + GearBlockH - 3f, w);
        return click;
    }

    float DrawGearTab(float x, float y, float w, BuildingInstance b, GUIStyle small)
    {
        float cy = y;
        float secY = cy;
        cy = UiKit.Section(x, cy, w, "Полевые укрепления");
        UiKit.Tip(new Rect(x, secY, w, UiKit.SectionH),
            "Для обороны и набегов. Готовое лежит в запасе Ратуши: дома ставят во время войны (кнопка «Заслоны» [B]), в набег берут в окне «В бой!». " +
            $"Сила кузни ×{Power:F2} - прочность и лечебный запас. Изготовлено всего: {GearCraftedTotal}.");

        foreach (int k in GearKinds())
        {
            FieldPost.Spec s = FieldPost.Specs[k];
            bool can = FieldPost.CanPay(s);
            int stock = FieldGear.Count((PostKind)k);
            string right = UiKit.Col(stock > 0 ? UiKit.GoodHex : UiKit.MutedHex, $"в запасе: {stock}");
            string cost = UiKit.Col(can ? UiKit.MutedHex : UiKit.BadHex, FieldPost.CostPlain(s)) + UiKit.Col(UiKit.MutedHex, $" · {s.craftSeconds:F0} с");
            string tip = $"{s.info}\nПрочность {s.hp * Power:F0}" + (s.healPool > 0f ? $", лечение {s.healPool * Power:F0}" : "")
                       + (s.thorns > 0f ? $", шипы: {s.thorns * 100f:F0}% удара обратно, {s.spikeDps * Power:F0}/с прижатым" : "")
                       + (s.wheeled ? ", катится (боец толкает), на ходу изнашивается" : "")
                       + (s.IsTower ? $", стрелы {s.towerDamage * Power:F0} урона, дальность {s.towerRange:F0}. Не ближе {FieldPost.EnemyZoneClear:F0} кл. к зелёной зоне врага" : "")
                       + $". Изготовление {s.craftSeconds:F0} с.";
            string p = GearProblem(k);
            bool thisBusy = crafting && (int)craftKind == k;
            if (GearBlock(x, cy, w, s.name, right, cost, tip, thisBusy ? "Делается..." : "Изготовить", p, true))
                BeginGear(k);
            cy += GearBlockH;
        }

        // --- Ход изготовления ---
        string cname = crafting ? FieldPost.Specs[(int)craftKind].name : "";
        ProgressRow(x, cy, w, crafting, $"Делается: <b>{cname}</b>", GearProgress, !Works || !b.isProducing, "Мастер укреплений свободен");
        cy += UiKit.RowH + 6f;

        // --- Понтонные секции для переправ ---
        if (PontoonBlockH > 0f)
        {
            cy = UiKit.Section(x, cy, w, "Переправы");
            string pp = PontoonProblem();
            string right = UiKit.Col(UiKit.MutedHex, $"в запасе: {PontoonStock.Count}/{PontoonStock.Max}");
            string cost = UiKit.Col(PontoonStock.Missing() == "" ? UiKit.MutedHex : UiKit.BadHex, PontoonStock.CostText);
            string tip = "Переправа через реку для солдат: секций нужно столько, сколько клеток воды поперёк. Ставят кнопкой «Переправы» [P].";
            string pbtn = pontoonCrafting ? $"Делают {PontoonProgress * 100f:F0}%" : $"Сделать {PontoonStock.PerBatch}";
            if (GearBlock(x, cy, w, "Понтонные секции", right, cost, tip, pbtn, pp, false))
                BeginPontoons();
            cy += GearBlockH;
        }
        return cy - y;
    }

    // ----- Вкладка «Ловушки» -----

    const float TrapRowH = 32f;
    Vector2 trapScroll;

    float TrapListH(int groups) { return Mathf.Clamp(groups * TrapRowH, TrapRowH * 2f, TrapRowH * 5f); }

    float TrapTabHeight(BuildingInstance b, float top)
    {
        int g = TrapStock.Groups(TrapStock.Hall, k => !k.combat).Count;
        float hh = UiKit.RowH * 2f + (ConvertProblem() != "" ? UiKit.RowH : 0f);
        return top + hh + UiKit.SectionH + TrapListH(g) + 6f + UiKit.RowH;
    }

    float DrawTrapTab(float x, float y, float w, BuildingInstance b, GUIStyle small)
    {
        float cy = y;
        var groups = TrapStock.Groups(TrapStock.Hall, k => !k.combat);
        float listH = TrapListH(groups.Count);
        GUIStyle body = MidLeft(UiKit.Body);

        string miss = Traps.ConvertMissing();
        GUI.Label(new Rect(x, cy, w - 20f, UiKit.RowH),
            UiKit.Col(UiKit.MutedHex, "Переделка: ") + UiKit.Col(miss == "" ? UiKit.MutedHex : UiKit.BadHex,
                $"{Traps.ConvertWood} дер., {Traps.ConvertPlanks} досок, {Traps.ConvertSteel} стали") + UiKit.Col(UiKit.MutedHex, $" · {Traps.ConvertSeconds:F0} с"), body);
        Help(x + w, cy, "Обычная ловушка из запаса Ратуши становится БОЕВОЙ: её ставят прямо в бою - дома во время войны и в набеге (не в зелёной зоне врага). " +
                        $"Урон ×{Traps.CombatDamageMul * Power:F2}. Ставится долго и, пока ставится, её легко растоптать. " +
                        $"Обычные ловушки - с рынка (их собирают инженеры). Переделано всего: {TrapsConvertedTotal}.");
        cy += UiKit.RowH;
        GUI.Label(new Rect(x, cy, w, UiKit.RowH), UiKit.Col(UiKit.MutedHex, "Боевых в запасе: ") + $"<b>{TrapStock.Count(true)}</b>", body);
        cy += UiKit.RowH;

        string p = ConvertProblem();
        if (p != "")
        {
            GUI.Label(new Rect(x, cy, w, UiKit.RowH), UiKit.Col(UiKit.WarnHex, p), body);
            cy += UiKit.RowH;
        }

        cy = UiKit.Section(x, cy, w, "Обычные ловушки в Ратуше");
        float innerW = w - 14f;
        trapScroll = GUI.BeginScrollView(new Rect(x, cy, w, listH), trapScroll,
            new Rect(0f, 0f, innerW, Mathf.Max(listH, groups.Count * TrapRowH)));
        if (groups.Count == 0)
        {
            Rect er = new Rect(0f, 0f, innerW, 40f);
            GUI.Label(er, UiKit.Col(UiKit.MutedHex, "Обычных ловушек нет"), UiKit.Small);
            UiKit.Tip(er, "Купите их на рынке (раздел «Модули и ловушки»).");
        }
        for (int i = 0; i < groups.Count; i++)
        {
            TrapKit k = groups[i].Key;
            Rect lr = new Rect(0f, i * TrapRowH, innerW - 116f, TrapRowH - 2f);
            GUI.Label(lr, $"<b>{k.Name}</b> ×{groups[i].Value}", small);
            UiKit.Tip(lr, Traps.Details(k));
            if (UiKit.Button(new Rect(innerW - 112f, i * TrapRowH + 3f, 110f, TrapRowH - 6f), "В боевую", p == "", UiKit.Kind.Primary,
                    p == "" ? "Переделать одну ловушку в боевую" : p))
                BeginConvert(k);
        }
        GUI.EndScrollView();
        cy += listH + 6f;

        ProgressRow(x, cy, w, converting != null, $"Переделывается: <b>{(converting != null ? converting.Name : "")}</b>", ConvertProgress,
            !Works || !b.isProducing, "Горн для ловушек свободен");
        cy += UiKit.RowH;
        return cy - y;
    }

    // ---------- Окно мини-игры (рисуется поверх всего, пока идёт доводка) ----------

    void OnGUI()
    {
        UiKit.Apply();
        if (session != this || working == null) return;
        GUI.depth = -10;

        float w = 520f, h = 300f;
        Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        gameRect = r;
        CameraController.BlockZoom();
        UiKit.Window(r, $"Доводка: {working.Name}", out bool close);
        if (close) { Close(); return; }

        GUIStyle st = new GUIStyle(UiKit.Body) { alignment = TextAnchor.MiddleCenter, wordWrap = true };

        // Удары и их результат
        float cw = (w - 40f) / 3f;
        for (int i = 0; i < 3; i++)
        {
            Rect c = new Rect(r.x + 20f + i * cw, r.y + 42f, cw - 8f, 44f);
            string res = i < round ? $"{Mathf.RoundToInt(hits[i] * 100f)}%" : (i == round && !finished ? UiKit.Col(UiKit.WarnHex, "сейчас") : UiKit.Col(UiKit.MutedHex, "—"));
            GUI.Box(c, GUIContent.none);
            GUI.Label(c, $"<b>{Aspects[i]}</b> " + UiKit.Col(UiKit.MutedHex, $"({AspectHints[i]})") + $"\n{res}", st);
        }

        // Шкала
        Rect bar = new Rect(r.x + 30f, r.y + 110f, w - 60f, 34f);
        Fill(bar, new Color(0.15f, 0.12f, 0.1f));
        // Цветные зоны: края - красные, ближе к центру - жёлтые, центр - зелёный
        Fill(new Rect(bar.x + bar.width * 0.225f, bar.y, bar.width * 0.55f, bar.height), new Color(0.55f, 0.45f, 0.15f));
        Fill(new Rect(bar.x + bar.width * 0.4f, bar.y, bar.width * 0.2f, bar.height), new Color(0.35f, 0.6f, 0.2f));
        Fill(new Rect(bar.x + bar.width * 0.48f, bar.y, bar.width * 0.04f, bar.height), new Color(0.6f, 0.95f, 0.35f));
        // Ползунок
        float sx = bar.x + bar.width * slider;
        Fill(new Rect(sx - 3f, bar.y - 6f, 6f, bar.height + 12f), Color.white);

        GUI.Label(new Rect(r.x, r.y + 152f, w, 26f), lastHitText, st);

        GUIStyle big = new GUIStyle(UiKit.ButtonStyle(UiKit.Kind.Primary)) { fontSize = 15, fontStyle = FontStyle.Bold };
        if (!finished)
        {
            if (GUI.Button(new Rect(r.x + w / 2f - 110f, r.y + 186f, 220f, 44f), "Удар!  [Пробел]", big)) Strike();
            GUIStyle hint = new GUIStyle(UiKit.MutedStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            Rect hr = new Rect(r.x, r.y + 238f, w, 22f);
            GUI.Label(hr, "Бейте, когда ползунок в зелёном центре · Esc — бросить", hint);
            UiKit.Tip(hr, "Если бросить, оставшиеся удары засчитаются слабыми.");
        }
        else
        {
            GUI.Label(new Rect(r.x + 20f, r.y + 180f, w - 40f, 50f),
                $"Готово: <b>{working.Name}</b>\n" + UiKit.Col(UiKit.MutedHex, working.StatsText), st);
            if (GUI.Button(new Rect(r.x + w / 2f - 80f, r.y + 240f, 160f, 36f), "Забрать  [Пробел]", big)) Close();
        }
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }
}