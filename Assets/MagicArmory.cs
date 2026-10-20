using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Магическая оружейная (только путь Магии, с Ратуши Ур.3). Чародеи делают пачки МАГИЧЕСКИХ СНАРЯДОВ для пушек
/// (см. MagicShells): огненные, ледяные, а с Ур.2 - чародейские. Всё носится физически:
///  1. Заказ: золото, травы (и смола / зелье) списываются из Ратуши, житель несёт их в оружейную.
///  2. Зачаровка. Скорость зависит от маны в воздухе (ниже MinManaPercent - стоит) и от уровня здания.
///  3. Готовую пачку несут в Ратушу. Оттуда её ставят на пушку (панель пушки), полевые пушки берут сами в казарме.
/// Марка пачки (Мк.I-III) = уровень оружейной: больше снарядов в пачке и сильнее эффект.
/// Пачки продаются и покупаются на рынке (как модули) - поставить их на пушку может любой город.
/// Школа Хаоса: ещё и ЯДРО ХАОСА (TowerModules.ChaosCoreRecipe) для Мага стихий - он берёт его сам в казарме (ChaosMagic).
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class MagicArmory : MonoBehaviour
{
    public const string TypeName = "Магическая оружейная";
    public const int RequiredHallLevel = 3;
    public const int MinManaPercent = 20;

    static readonly int[] ShelfByLevel = { 4, 6, 9 };          // сколько готовых пачек может лежать в городе
    static readonly float[] SpeedByLevel = { 1f, 1.3f, 1.6f };

    // Сколько чего на пачку (золото и время - в TowerModules.ShellRecipes)
    public static int Herbs(ModuleKind k) { return k == ModuleKind.ChaosCore ? 6 : (k == ModuleKind.FrostShells ? 5 : (k == ModuleKind.ArcaneShells ? 4 : 3)); }
    public static int Resin(ModuleKind k) { return k == ModuleKind.FireShells ? 2 : 0; }
    public static int Potions(ModuleKind k) { return k == ModuleKind.ArcaneShells || k == ModuleKind.ChaosCore ? 1 : 0; }

    /// <summary>Что можно зачаровать сейчас: снаряды, а в школе Хаоса - ещё ядро хаоса.</summary>
    public static List<ModuleRecipe> ActiveRecipes()
    {
        var list = new List<ModuleRecipe>(TowerModules.ShellRecipes);
        if (DevelopmentPath.IsChaos) list.Add(TowerModules.ChaosCoreRecipe);
        return list;
    }

    /// <summary>Ядра хаоса в городе (Ратуша, дворы) и у магов.</summary>
    public static int ChaosCoresInTown()
    {
        int n = EngineerYard.CountKits(ModuleKind.ChaosCore);
        foreach (var c in CitizenAgent.All) if (c != null && !c.IsDead && c.HasChaosCore) n++;
        return n;
    }

    enum Stage { Idle, Fetching, Enchanting }
    Stage stage = Stage.Idle;
    ModuleRecipe current;
    int currentMk;
    float timer, timerTotal = 1f, autoTimer;
    int inTransit, coresInTransit;
    BuildingInstance potionSrc;

    public bool AutoMake;
    public ModuleKind AutoKind = ModuleKind.FireShells;
    public int MadeTotal { get; private set; }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public int Mk => Lvl + 1;
    public int Shelf => ShelfByLevel[Lvl];
    public int CoreShelf => ChaosMagic.Shelf(Lvl + 1);
    bool IsCore(ModuleKind k) { return k == ModuleKind.ChaosCore; }
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    void Awake() { Building = GetComponent<BuildingInstance>(); }

    static BuildingInstance HallBuilding()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    static bool ManaOk => ManaManager.Instance == null || ManaManager.Instance.Percent >= MinManaPercent;

    // ---------- Заказ ----------

    public string MakeProblem(ModuleRecipe r)
    {
        if (!DevelopmentPath.IsMagic) return "Нужен путь Магии";
        if (!Works) return "Оружейная разрушена";
        if (Building.level < r.minLevel) return $"Нужна оружейная Ур.{r.minLevel}";
        if (stage != Stage.Idle) return "Чародеи заняты";
        if (!Building.isProducing) return "Нет чародеев";
        if (!ManaOk) return $"Мало маны (нужно от {MinManaPercent}%)";
        if (IsCore(r.kind))
        {
            if (!DevelopmentPath.IsChaos) return "Нужна школа магии Хаоса";
            if (ChaosCoresInTown() + coresInTransit >= CoreShelf) return $"Ядер в городе уже {CoreShelf} — дайте их магам стихий";
        }
        else if (MagicShells.TotalPacks() + inTransit >= Shelf) return $"Пачек в городе уже {Shelf} — поставьте их на пушки";
        var rm = ResourceManager.Instance;
        if (rm == null) return "Нет казны";
        if (rm.gold < r.gold) return $"Нужно {r.gold} золота";
        if (rm.herbs < Herbs(r.kind)) return $"Нужно {Herbs(r.kind)} трав в Ратуше";
        if (rm.resin < Resin(r.kind)) return $"Нужно {Resin(r.kind)} смолы";
        if (Potions(r.kind) > 0 && PotionHouse.TotalPotions < Potions(r.kind)) return "Нужно зелье отравления (Зельеварня)";
        return "";
    }

    public void StartMake(ModuleRecipe r)
    {
        if (MakeProblem(r) != "") return;
        var rm = ResourceManager.Instance;
        int herbs = Herbs(r.kind), resin = Resin(r.kind), pots = Potions(r.kind);
        if (!rm.Spend(r.gold, 0, 0, 0)) return;
        if (!rm.SpendHerbs(herbs)) { rm.Add(r.gold, 0, 0, 0); return; }
        if (resin > 0 && !rm.SpendResin(resin)) { rm.Add(r.gold, 0, 0, 0); rm.AddHerbs(herbs); return; }
        potionSrc = null;
        if (pots > 0 && !PotionHouse.TakePotions(pots, out potionSrc))
        {
            rm.Add(r.gold, 0, 0, 0); rm.AddHerbs(herbs); rm.AddResin(resin);
            return;
        }

        current = r;
        currentMk = Mk;
        stage = Stage.Fetching;
        BuildingInstance from = potionSrc != null ? potionSrc : HallBuilding();
        Logistics.Request(new HaulJob
        {
            from = from,
            to = Building,
            prefer = Building,
            onDeliver = () =>
            {
                if (current == null) return;
                if (!Works) { Refund(); return; }
                timer = timerTotal = current.seconds;
                stage = Stage.Enchanting;
            },
            onAbort = loaded => Refund()
        });
    }

    void Refund()
    {
        if (current != null && ResourceManager.Instance != null)
        {
            var rm = ResourceManager.Instance;
            rm.Add(current.gold, 0, 0, 0);
            rm.AddHerbs(Herbs(current.kind));
            rm.AddResin(Resin(current.kind));
            if (Potions(current.kind) > 0) PotionHouse.ReturnPotions(Potions(current.kind), potionSrc);
        }
        current = null;
        potionSrc = null;
        stage = Stage.Idle;
    }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();

        if (stage == Stage.Enchanting)
        {
            if (!Works || !Building.isProducing) return;
            float mana = ManaOk ? ManaManager.Factor : 0f;
            timer -= Time.deltaTime * SpeedByLevel[Lvl] * mana * Building.HealthFactor * Building.OffHoursFactor;
            if (timer <= 0f) Finish();
            return;
        }

        if (stage == Stage.Idle && AutoMake)
        {
            autoTimer -= Time.deltaTime;
            if (autoTimer > 0f) return;
            autoTimer = 3f;
            if (IsCore(AutoKind) && !DevelopmentPath.IsChaos) AutoKind = ModuleKind.FireShells;   // школу сменили
            ModuleRecipe r = TowerModules.RecipeOf(AutoKind);
            if (r != null && MakeProblem(r) == "") StartMake(r);
        }
    }

    void Finish()
    {
        ModuleKit kit = new ModuleKit { kind = current.kind, mk = currentMk };
        current = null;
        potionSrc = null;
        stage = Stage.Idle;
        MadeTotal++;
        bool core = IsCore(kit.kind);
        if (core) coresInTransit++; else inTransit++;

        // Готовую пачку несут в Ратушу
        Logistics.Request(new HaulJob
        {
            from = Building,
            to = HallBuilding(),
            prefer = Building,
            onDeliver = () => Deliver(kit),
            onAbort = loaded => Deliver(kit)   // пачку не теряем
        });
    }

    void Deliver(ModuleKit kit)
    {
        EngineerYard.AddToHall(kit);
        if (IsCore(kit.kind))
        {
            coresInTransit = Mathf.Max(0, coresInTransit - 1);
            WarManager.Notify($"Магическая оружейная: {TowerModules.KitName(kit)} в Ратуше — маг стихий возьмёт его в казарме", true);
            return;
        }
        inTransit = Mathf.Max(0, inTransit - 1);
        WarManager.Notify($"Магическая оружейная: {TowerModules.KitName(kit)} в Ратуше — поставьте на пушку", true);
    }

    // ---------- Панель ----------

    string StatusLine
    {
        get
        {
            switch (stage)
            {
                case Stage.Fetching: return "Несут травы и золото из Ратуши...";
                case Stage.Enchanting:
                    float pct = Mathf.Clamp01(1f - timer / Mathf.Max(0.1f, timerTotal)) * 100f;
                    string n = current != null ? TowerModules.KitName(current.kind, currentMk) : "снаряды";
                    if (!ManaOk) return UiKit.Col(UiKit.BadHex, $"Зачаровка стоит - мало маны ({pct:F0}%)");
                    return Building.isProducing ? $"Зачаровывают: <b>{n}</b> ({pct:F0}%)" : UiKit.Col(UiKit.BadHex, $"Стоит - нет чародеев ({pct:F0}%)");
                default: return "Чародеи свободны";
            }
        }
    }

    static string CostPart(int need, int have, string what)
    {
        if (need <= 0) return "";
        string col = have >= need ? UiKit.GoodHex : UiKit.BadHex;
        return $"<color={col}>{need} {what}</color>";
    }

    float EnchantFraction => stage == Stage.Enchanting ? Mathf.Clamp01(1f - timer / Mathf.Max(0.1f, timerTotal)) : 0f;

    const float RecipeH = 30f;

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        const string foldId = "magic-armory-info";
        string infoText =
            "Заказ: золото, травы (и смола / зелье) списываются из Ратуши, житель несёт их сюда, чародеи зачаровывают. " +
            "Готовую пачку несут в Ратушу. Пачку ставят на пушку из её панели; полевые пушки заряжаются сами в казарме. " +
            $"Марка пачки (Мк.) равна уровню оружейной. Ниже {MinManaPercent}% маны работа стоит. Сделано всего: {MadeTotal}." +
            (DevelopmentPath.IsChaos ? " Ядро хаоса Маг стихий берёт сам в своей казарме; погиб маг — ядро потеряно." : "");
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float barY = statusY + 22f;
        float packsY = barY + 14f;
        float resY = packsY + 20f;
        float problemY = resY + 20f;
        var recipes = ActiveRecipes();
        bool chaos = DevelopmentPath.IsChaos;
        float recipesY = problemY + (b.isWorking ? 2f : 22f);
        float rowsY = recipesY + UiKit.SectionH + 2f;
        float autoY = rowsY + recipes.Count * RecipeH + 4f;
        float workersY = autoY + 28f;
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}  " + UiKit.Col(UiKit.MutedHex, $"Мк.{TowerModules.Roman(Mk)}"));

        var rm = ResourceManager.Instance;
        int mana = ManaManager.Instance != null ? ManaManager.Instance.Percent : 100;
        string manaCol = mana >= MinManaPercent ? UiKit.InfoHex : UiKit.BadHex;

        // --- Что происходит ---
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, st);
        UiKit.Bar(new Rect(x + 10f, y + barY, w - 20f, 10f), EnchantFraction, ManaOk ? UiKit.Info : UiKit.Bad);

        var packs = new List<string>();
        foreach (var k in MagicShells.Kinds)
        {
            int n = MagicShells.Packs(k);
            if (n > 0) packs.Add($"{TowerModules.Name(k).ToLower()} ×{n}");
        }
        int total = MagicShells.TotalPacks();
        string packCol = total + inTransit >= Shelf ? UiKit.WarnHex : "#FFFFFF";
        string carry = inTransit > 0 ? "  " + UiKit.Col(UiKit.WarnHex, $"· несут: {inTransit}") : "";
        Rect pr = new Rect(x + 10f, y + packsY, w - 20f, 18f);
        string coreTxt = "";
        if (chaos)
        {
            int cores = ChaosCoresInTown();
            string coreCol = cores + coresInTransit >= CoreShelf ? UiKit.WarnHex : "#C9A6FF";
            coreTxt = "  · ядра хаоса: " + $"<color={coreCol}><b>{cores}/{CoreShelf}</b></color>" + (coresInTransit > 0 ? UiKit.Col(UiKit.WarnHex, $" (+{coresInTransit})") : "");
        }
        GUI.Label(pr, $"Пачки в городе: <color={packCol}><b>{total}/{Shelf}</b></color>{carry}{coreTxt}", small);
        string packTip = packs.Count > 0 ? string.Join(", ", packs) : "Готовых пачек нет";
        if (chaos) packTip += $"\nЯдра хаоса: в Ратуше {EngineerYard.CountKits(ModuleKind.ChaosCore)}, у магов {ChaosCoresInTown() - EngineerYard.CountKits(ModuleKind.ChaosCore)}";
        UiKit.Tip(pr, packTip);

        GUI.Label(new Rect(x + 10f, y + resY, w - 20f, 18f),
            $"Травы: {(rm != null ? rm.herbs : 0)} · смола: {(rm != null ? rm.resin : 0)} · зелья: {PotionHouse.TotalPotions} · мана: <color={manaCol}>{mana}%</color>", small);

        if (!b.isWorking)
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), UiKit.Col(UiKit.BadHex, b.StatusText), small);

        // --- Рецепты: название · цена · кнопка ---
        UiKit.Section(x + 10f, y + recipesY, w - 20f, "Зачаровка");
        float cy = y + rowsY;
        bool firstOk = true;
        foreach (var r in recipes)
        {
            Rect nr = new Rect(x + 10f, cy + 4f, 130f, 20f);
            GUI.Label(nr, IsCore(r.kind) ? $"<b><color=#C9A6FF>{r.name}</color></b>" : $"<b>{r.name}</b>", st);
            string forWhom = IsCore(r.kind) ? $"\nДля: {r.target} (берёт сам в казарме)" : "";
            UiKit.Tip(nr, $"{r.name} Мк.{TowerModules.Roman(Mk)} - {r.desc}.\n{TowerModules.Stats(r.kind, Mk)}{forWhom}\nВремя: {r.seconds:F0} с");

            var cost = new List<string>();
            string s;
            if ((s = CostPart(r.gold, rm != null ? rm.gold : 0, "зол")) != "") cost.Add(s);
            if ((s = CostPart(Herbs(r.kind), rm != null ? rm.herbs : 0, "трав")) != "") cost.Add(s);
            if ((s = CostPart(Resin(r.kind), rm != null ? rm.resin : 0, "смолы")) != "") cost.Add(s);
            if ((s = CostPart(Potions(r.kind), PotionHouse.TotalPotions, "зелье")) != "") cost.Add(s);
            GUI.Label(new Rect(x + 142f, cy + 5f, w - 152f - 100f, 20f), string.Join(UiKit.Col(UiKit.MutedHex, " · "), cost.ToArray()), small);

            string p = MakeProblem(r);
            bool ok = p == "";
            if (UiKit.Button(new Rect(x + w - 106f, cy, 96f, 26f), "Сделать", ok, ok && firstOk ? UiKit.Kind.Primary : UiKit.Kind.Normal,
                    ok ? $"Зачаровать: {r.name} ({r.seconds:F0} с)" : p))
                StartMake(r);
            if (ok) firstOk = false;
            cy += RecipeH;
        }

        // Делать постоянно: какой вид
        float half = (w - 20f) * 0.45f;
        Rect amr = new Rect(x + 10f, y + autoY, half, 22f);
        AutoMake = GUI.Toggle(amr, AutoMake, " Делать постоянно");
        UiKit.Tip(amr, "Оружейная сама начинает новую пачку выбранного вида, когда может.");
        string autoName = TowerModules.Name(AutoKind).ToLower();
        if (UiKit.Button(new Rect(x + 10f + half, y + autoY - 2f, w - 20f - half, 24f), autoName + " ▸", UiKit.Kind.Ghost, "Сменить, что делать"))
        {
            int i = recipes.FindIndex(rr => rr.kind == AutoKind);
            AutoKind = recipes[(i + 1) % recipes.Count].kind;
        }

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId) && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
