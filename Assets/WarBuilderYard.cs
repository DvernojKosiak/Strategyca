using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Двор военного строителя (путь Военного дела, ветка «Военный строитель», Ратуша Ур.3, один на город).
/// Рабочие двора - строители (2 / 4 / 6 по уровням, см. PopulationManager). Их поведение - CitizenAgent_WarBuilder.cs.
///
/// ВОЙНА:
///  - строители выбегают из укрытия и чинят здания прямо под ударами врага (встают с той стороны, где врагов нет);
///  - сначала - здания, отмеченные игроком «Чинить в войну» (переключатель под панелью любого здания
///    или список здесь), потом - самые побитые;
///  - помогают строить полевые укрепления (быстрая баррикада - в первую очередь) и чинят побитые
///    (FieldPost.AddBuildWork / Repair / NeedsWork);
///  - враг вплотную (BuilderPanicRadius) - строитель бросает работу и бежит прятаться, как все жители. Могут погибнуть.
/// МИР: в рабочее время подлатывают побитые здания (если включено «Чинить в мирное время»).
/// Работают только в зоне двора (ZoneByLevel клеток вокруг него).
/// Ремонт тратит дерево из казны: 1 дерево на HpPerWood прочности. Нет дерева - чинят втрое медленнее, «на честном слове».
/// Над отмеченными зданиями висит оранжевый молоток (ярче и качается, пока их чинят).
/// Компонент добавляет BuildingPlacer.
/// </summary>
public class WarBuilderYard : MonoBehaviour
{
    public const string TypeName = "Двор военного строителя";
    public const int RequiredHallLevel = 3;

    public static readonly List<WarBuilderYard> All = new List<WarBuilderYard>();

    // ---------- Числа (можно менять) ----------
    //                                              Ур.1   Ур.2   Ур.3
    static readonly float[] RepairByLevel       = { 5f,    6.5f,  8f };     // прочности в секунду на одного строителя
    static readonly float[] BuildMulByLevel     = { 1f,    1.25f, 1.5f };   // секунд стройки укрепления за секунду работы
    static readonly int[]   ZoneByLevel         = { 14,    18,    22 };     // радиус работы (клетки от двора)
    static readonly int[]   MarksByLevel        = { 4,     6,     8 };      // сколько зданий можно отметить

    public const float HpPerWood = 40f;            // 1 дерево из казны на столько прочности
    public const float NoWoodMul = 0.35f;          // без дерева чинят медленнее
    public const float PeaceRepairBelow = 0.9f;    // в мирное время чинят здания ниже этой доли прочности
    public const float WarRepairBelow = 0.98f;     // в войну - почти всё, что поцарапано
    public const int CrewPerBuilding = 3;          // сколько строителей сразу на одном здании
    public const int CrewPerPost = 2;              // ... на одном укреплении
    public const float BuilderPanicRadius = 2.2f;  // враг ближе - строитель бросает работу и бежит
    public const float SpotSafeRadius = 2.8f;      // место для работы: врагов не ближе этого

    // ---------- Настройки игрока ----------
    public bool OnlyMarked = false;    // в войну чинить только отмеченные здания (укрепления - всегда)
    public bool PeaceRepair = true;    // в мирное время подлатывать побитые здания

    // ---------- Статистика ----------
    public float HpRepaired { get; private set; }
    public float PostWork { get; private set; }    // секунд стройки укреплений
    public int WoodSpent { get; private set; }
    public bool NoWood { get; private set; }
    float woodDebt;
    float noWoodUntil;

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public float RepairRate => RepairByLevel[Lvl];
    public float BuildMul => BuildMulByLevel[Lvl];
    public int ZoneCells => ZoneByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined && Building.InGreenZone && DevelopmentPath.IsWarBuilder;

    // ---------- Отметки «Чинить в войну» (общие на город) ----------
    static readonly List<BuildingInstance> marked = new List<BuildingInstance>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        All.Clear();
        marked.Clear();
    }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); HideFx(); }
    void OnDestroy() { DestroyFx(); }

    /// <summary>Есть работающий двор (ветка выбрана, двор стоит).</summary>
    public static bool Any
    {
        get
        {
            if (!DevelopmentPath.IsWarBuilder) return false;
            foreach (var y in All) if (y != null && y.Works) return true;
            return false;
        }
    }

    /// <summary>Лучший двор (выше уровень) - по нему лимит отметок.</summary>
    public static WarBuilderYard Main
    {
        get
        {
            WarBuilderYard best = null;
            foreach (var y in All)
                if (y != null && y.Building != null && (best == null || y.Building.level > best.Building.level)) best = y;
            return best;
        }
    }

    public static int MaxMarks
    {
        get
        {
            WarBuilderYard m = Main;
            return m != null ? MarksByLevel[m.Lvl] : MarksByLevel[0];
        }
    }

    static void CleanMarks() { marked.RemoveAll(b => b == null || b.type == null); }

    public static int MarkedCount { get { CleanMarks(); return marked.Count; } }
    public static bool IsMarked(BuildingInstance b) { return b != null && marked.Contains(b); }

    /// <summary>Можно ли такое здание отметить (дорожки не ломаются - их не чинят).</summary>
    public static bool CanMark(BuildingInstance b) { return b != null && b.type != null && b.IsDamageable; }

    /// <summary>Отметить / снять отметку. false - лимит отметок (сообщение уже показано).</summary>
    public static bool SetMarked(BuildingInstance b, bool on)
    {
        if (!CanMark(b)) return false;
        CleanMarks();
        if (!on) { marked.Remove(b); return true; }
        if (marked.Contains(b)) return true;
        if (marked.Count >= MaxMarks)
        {
            WarManager.Notify($"Отмечено уже {marked.Count} из {MaxMarks} — улучшите Двор или снимите отметку", false);
            return false;
        }
        marked.Add(b);
        return true;
    }

    // ---------- Расходы ----------

    /// <summary>Строитель хочет починить hp прочности: списываем дерево. Возвращает, сколько реально починили.</summary>
    public float PayRepair(float hp)
    {
        if (hp <= 0f) return 0f;
        if (Time.time < noWoodUntil) return hp * NoWoodMul;
        woodDebt += hp / HpPerWood;
        if (woodDebt < 1f) { NoWood = false; return hp; }
        var rm = ResourceManager.Instance;
        if (rm != null && rm.Spend(0, 1, 0, 0))
        {
            woodDebt -= 1f;
            WoodSpent++;
            NoWood = false;
            return hp;
        }
        // Дерева нет: чиним «на честном слове», снова проверим казну через пару секунд
        woodDebt = 0f;
        noWoodUntil = Time.time + 2f;
        if (!NoWood) WarManager.Notify("Двор военного строителя: в казне нет дерева — чинят медленнее", false);
        NoWood = true;
        return hp * NoWoodMul;
    }

    public void CountRepair(float hp) { HpRepaired += hp; }
    public void CountPostWork(float s) { PostWork += s; }

    // ---------- Поиск работы ----------

    public bool InZone(Vector2 p)
    {
        if (Building == null) return false;
        return Building.DistanceTo(p) <= ZoneCells;
    }

    /// <summary>Здание нужно чинить (в войну / в мирное время).</summary>
    public static bool NeedsRepair(BuildingInstance b, bool war)
    {
        if (b == null || b.type == null || !b.IsDamageable || b.IsRuined || !b.isActiveAndEnabled) return false;
        float frac = b.Hp / Mathf.Max(1f, b.MaxHp);
        return frac < (war ? WarRepairBelow : PeaceRepairBelow);
    }

    /// <summary>
    /// Найти работу для строителя в точке from. skip - цель, которую строитель сейчас обходит (недостижима / опасна).
    /// Укрепления: недостроенная быстрая баррикада - важнее всего, потом другие стройки, потом починка.
    /// Здания: отмеченные - в первую очередь, потом самые побитые; ближе - лучше.
    /// </summary>
    public bool FindJob(Vector2 from, CitizenAgent who, object skip, bool war, out BuildingInstance bestB, out FieldPost bestP)
    {
        bestB = null;
        bestP = null;
        if (!Works) return false;
        float bestScore = float.MaxValue;

        foreach (var p in FieldPost.All)
        {
            if (p == null || !p.Alive || p.Raid || !p.NeedsWork) continue;
            if (ReferenceEquals(p, skip) || !InZone(p.Position)) continue;
            if (CrewOn(p, who) >= CrewPerPost) continue;
            float score = Vector2.Distance(from, p.Position);
            if (!p.Built) score -= p.Kind == PostKind.QuickBarricade ? 40f : 28f;
            else score -= 18f * (1f - Mathf.Clamp01(p.Hp / Mathf.Max(1f, p.MaxHp)));
            if (score < bestScore) { bestScore = score; bestP = p; }
        }

        foreach (var b in BuildingInstance.All)
        {
            if (!NeedsRepair(b, war) || ReferenceEquals(b, skip)) continue;
            bool mark = IsMarked(b);
            if (war && OnlyMarked && !mark) continue;
            if (b.DistanceTo(Building.Center) > ZoneCells) continue;
            if (CrewOn(b, who) >= CrewPerBuilding) continue;
            float frac = Mathf.Clamp01(b.Hp / Mathf.Max(1f, b.MaxHp));
            float score = Vector2.Distance(from, b.Center) - 30f * (1f - frac);
            if (mark) score -= war ? 45f : 10f;
            if (b.UnderAttack) score -= 6f;          // бьют прямо сейчас - спешим
            if (score < bestScore) { bestScore = score; bestP = null; bestB = b; }
        }
        return bestB != null || bestP != null;
    }

    /// <summary>Сколько строителей (кроме who) уже заняты этой целью.</summary>
    public static int CrewOn(object target, CitizenAgent who)
    {
        if (target == null) return 0;
        int n = 0;
        foreach (var a in CitizenAgent.All)
            if (a != null && !ReferenceEquals(a, who) && ReferenceEquals(a.WarBuildTarget, target)) n++;
        return n;
    }

    // Сколько строителей двора сейчас на работе снаружи
    int BusyBuilders(out int total)
    {
        int busy = 0;
        total = 0;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.Workplace != Building) continue;
            total++;
            if (a.WarBuildTarget != null) busy++;
        }
        return busy;
    }

    // ---------- Вид на карте ----------

    GameObject fxRoot;
    SpriteRenderer zoneFill;
    readonly SpriteRenderer[] zoneEdges = new SpriteRenderer[4];
    readonly List<GameObject> hammers = new List<GameObject>();
    SpriteRenderer flag;

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (Building == null) return;
        UpdateFx();
    }

    bool ZoneVisible => Works && (BuildingPlacer.PlacingNow || BuildingPlacer.Selected == Building);

    void UpdateFx()
    {
        bool leader = All.Count > 0 && All[0] == this;   // молотки над отметками рисует один двор
        bool show = DevelopmentPath.IsWarBuilder && Building != null && !Building.IsRuined;
        if (!show) { HideFx(); return; }

        if (fxRoot == null)
        {
            fxRoot = new GameObject("WarBuilderFx");
            zoneFill = MakeSprite(fxRoot.transform, CombatFx.Square(), -45);
            for (int i = 0; i < 4; i++) zoneEdges[i] = MakeSprite(fxRoot.transform, CombatFx.Square(), -44);
            flag = MakeSprite(fxRoot.transform, CombatFx.Square(), 16);
        }
        fxRoot.SetActive(true);

        // Флажок над двором: оранжевый, в войну полощется
        Vector2Int o = Building.OriginCell;
        Vector2Int s = Building.type != null ? Building.type.size : new Vector2Int(2, 2);
        float wave = WarManager.IsWar ? Mathf.Sin(Time.time * 9f) * 0.06f : 0f;
        Place(flag, new Vector2(o.x + s.x - 0.35f + wave, o.y + s.y - 0.25f), new Vector2(0.38f, 0.22f), new Color(1f, 0.6f, 0.15f, 0.95f));

        // Зона работы - пока двор выбран или ставят постройку
        bool zone = ZoneVisible;
        zoneFill.enabled = zone;
        for (int i = 0; i < 4; i++) zoneEdges[i].enabled = zone;
        if (zone)
        {
            int r = ZoneCells;
            Vector2 min = new Vector2(o.x - r, o.y - r), max = new Vector2(o.x + s.x + r, o.y + s.y + r);
            Vector2 size = max - min, mid = (min + max) * 0.5f;
            Color edge = new Color(1f, 0.62f, 0.2f, 0.7f);
            Place(zoneFill, mid, size, new Color(edge.r, edge.g, edge.b, 0.06f));
            const float t = 0.1f;
            Place(zoneEdges[0], new Vector2(mid.x, max.y - t * 0.5f), new Vector2(size.x, t), edge);
            Place(zoneEdges[1], new Vector2(mid.x, min.y + t * 0.5f), new Vector2(size.x, t), edge);
            Place(zoneEdges[2], new Vector2(min.x + t * 0.5f, mid.y), new Vector2(t, size.y), edge);
            Place(zoneEdges[3], new Vector2(max.x - t * 0.5f, mid.y), new Vector2(t, size.y), edge);
        }

        // Молотки над отмеченными зданиями
        int n = 0;
        if (leader)
        {
            CleanMarks();
            foreach (var b in marked)
            {
                if (b == null || b.type == null || !b.isActiveAndEnabled) continue;
                if (n >= hammers.Count) hammers.Add(MakeHammer(fxRoot.transform));
                GameObject h = hammers[n++];
                h.SetActive(true);
                Vector2Int bo = b.OriginCell, bs = b.type.size;
                bool busy = CrewOn(b, null) > 0;
                float swing = busy ? Mathf.Sin(Time.time * 10f) * 35f : 0f;
                h.transform.position = new Vector3(bo.x + 0.3f, bo.y + bs.y - 0.3f, 0f);
                h.transform.rotation = Quaternion.Euler(0f, 0f, 30f + swing);
                float a = busy ? 1f : 0.75f;
                Color head = b.IsRuined ? new Color(0.6f, 0.6f, 0.6f, a) : new Color(1f, 0.62f, 0.2f, a);
                foreach (var sr in h.GetComponentsInChildren<SpriteRenderer>())
                    sr.color = sr.sortingOrder == 31 ? head : new Color(0.55f, 0.38f, 0.2f, a);
            }
        }
        for (int i = n; i < hammers.Count; i++) if (hammers[i] != null) hammers[i].SetActive(false);
    }

    static SpriteRenderer MakeSprite(Transform parent, Sprite sprite, int order)
    {
        var go = new GameObject("w");
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        return sr;
    }

    static void Place(SpriteRenderer sr, Vector2 pos, Vector2 size, Color c)
    {
        sr.transform.position = new Vector3(pos.x, pos.y, 0f);
        sr.transform.localScale = new Vector3(size.x, size.y, 1f);
        sr.color = c;
    }

    // Молоток: рукоять + боёк
    static GameObject MakeHammer(Transform parent)
    {
        var g = new GameObject("Hammer");
        g.transform.SetParent(parent, false);
        var handle = MakeSprite(g.transform, CombatFx.Square(), 30);
        handle.transform.localScale = new Vector3(0.08f, 0.42f, 1f);
        handle.transform.localPosition = new Vector3(0f, -0.06f, 0f);
        var head = MakeSprite(g.transform, CombatFx.Square(), 31);
        head.transform.localScale = new Vector3(0.3f, 0.13f, 1f);
        head.transform.localPosition = new Vector3(0f, 0.16f, 0f);
        return g;
    }

    void HideFx() { if (fxRoot != null && fxRoot.activeSelf) fxRoot.SetActive(false); }

    void DestroyFx()
    {
        if (fxRoot != null) Destroy(fxRoot);
        fxRoot = null;
        hammers.Clear();
    }

    // ---------- Панель ----------

    string StatusLine
    {
        get
        {
            if (Building == null) return "";
            if (Building.IsRuined) return UiKit.Col(UiKit.BadHex, "Двор разрушен — строители не работают");
            if (!Building.InGreenZone) return UiKit.Col(UiKit.BadHex, "Вне зелёной зоны — работы нет");
            if (Building.workers <= 0) return UiKit.Col(UiKit.WarnHex, "Нет строителей — дайте рабочих");
            int total;
            int busy = BusyBuilders(out total);
            if (WarManager.IsWar)
                return busy > 0 ? UiKit.Col(UiKit.GoodHex, $"Война: чинят {busy} из {total}") : UiKit.Col(UiKit.WarnHex, "Война: ждут работы или прячутся");
            return busy > 0 ? UiKit.Col(UiKit.InfoHex, $"Подлатывают город: {busy} из {total}") : UiKit.Col(UiKit.MutedHex, "Мир: ждут работы");
        }
    }

    Vector2 listScroll;

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        if (b == null) return new Rect(x, y, 0f, 0f);
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;
        CleanMarks();

        const string foldId = "wbyard-info";
        string infoText =
            $"Строители (рабочие двора) работают в квадрате {ZoneCells} клеток вокруг двора (Ур.1-3: 14/18/22).\n" +
            $"В войну выходят из укрытия и чинят здания прямо под ударами: {RepairRate:F1} прочности/с на строителя (Ур.1-3: 5/6.5/8), " +
            $"не больше {CrewPerBuilding} на здание. Встают с той стороны, где нет врагов; враг вплотную — бегут прятаться.\n" +
            $"Укрепления: строят быстрее (×{BuildMul:F2} за строителя, быстрая баррикада — первой) и чинят.\n" +
            $"Ремонт: 1 дерево из казны на {HpPerWood:F0} прочности. Без дерева — втрое медленнее.\n" +
            "Порядок: отмеченные здания, потом самые побитые и ближние.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        int rows = Mathf.Max(1, marked.Count);
        float listH = Mathf.Min(rows, 5) * 26f;

        float statusY = UiKit.TitleH + 6f;
        float statsY = statusY + 22f;
        float secY = statsY + 22f;
        float marksY = secY + UiKit.SectionH;
        float listY = marksY + 22f;
        float quickY = listY + listH + 4f;
        float togglesY = quickY + 32f;
        float workersY = togglesY + 48f;
        float foldY = workersY + 32f;
        float upSecY = foldY + UiKit.FoldH + infoH + 6f;
        float upY = upSecY + UiKit.SectionH;
        float h = upY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        // --- Состояние ---
        GUI.Label(new Rect(x + 10f, y + statusY, w - 20f, 20f), StatusLine, UiKit.Body);
        Rect sr = new Rect(x + 10f, y + statsY, w - 20f, 18f);
        string woodTxt = NoWood ? UiKit.Col(UiKit.BadHex, "нет дерева!") : UiKit.Col(UiKit.WoodHex, $"дерева {WoodSpent}");
        GUI.Label(sr, UiKit.Col(UiKit.MutedHex, "Починено ") + UiKit.B(Mathf.FloorToInt(HpRepaired).ToString())
            + UiKit.Col(UiKit.MutedHex, " · стройка укреплений ") + $"{PostWork:F0} с" + UiKit.Col(UiKit.MutedHex, " · ") + woodTxt, small);
        UiKit.Tip(sr, $"Всего прочности починено: {HpRepaired:F0}. Секунд стройки укреплений: {PostWork:F0}. Дерева потрачено: {WoodSpent}.\n" +
            $"Сейчас: {RepairRate:F1} прочности/с на строителя, радиус {ZoneCells} кл.");

        // --- Отмеченные здания ---
        UiKit.Section(x + 10f, y + secY, w - 20f, $"Чинить в войну · {marked.Count}/{MaxMarks}");
        GUI.Label(new Rect(x + 10f, y + marksY, w - 20f, 20f),
            UiKit.Col(UiKit.MutedHex, "Отметка — переключатель под панелью любого здания"), small);

        Rect view = new Rect(x + 10f, y + listY, w - 20f, listH);
        if (marked.Count == 0)
        {
            GUI.Label(new Rect(view.x, view.y + 3f, view.width, 20f), UiKit.Col(UiKit.MutedHex, "Пока ничего не отмечено — чинят самые побитые"), small);
        }
        else
        {
            bool scroll = marked.Count > 5;
            Rect content = new Rect(0f, 0f, view.width - (scroll ? 14f : 0f), marked.Count * 26f);
            listScroll = GUI.BeginScrollView(view, listScroll, content, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            BuildingInstance remove = null;
            for (int i = 0; i < marked.Count; i++)
            {
                BuildingInstance m = marked[i];
                if (m == null) continue;
                float ry = i * 26f;
                float frac = m.IsRuined ? 0f : Mathf.Clamp01(m.Hp / Mathf.Max(1f, m.MaxHp));
                string nm = m.type != null ? m.type.name : "здание";
                Rect nr = new Rect(0f, ry, content.width - 150f, 22f);
                GUI.Label(nr, nm, new GUIStyle(small) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft });
                UiKit.Tip(nr, $"{nm} [{m.OriginCell.x},{m.OriginCell.y}]" + (InZoneOfAny(m) ? "" : "\nВне зоны двора — строители не дойдут"));
                Color col = Color.Lerp(UiKit.Bad, UiKit.Good, frac);
                string barTxt = m.IsRuined ? "руины" : (CrewOn(m, null) > 0 ? $"{frac * 100f:F0}% · чинят" : $"{frac * 100f:F0}%");
                UiKit.Bar(new Rect(content.width - 146f, ry + 4f, 90f, 14f), frac, InZoneOfAny(m) ? col : UiKit.Muted, barTxt);
                if (UiKit.Button(new Rect(content.width - 52f, ry, 52f, 22f), "Снять", UiKit.Kind.Ghost, "Снять отметку"))
                    remove = m;
            }
            GUI.EndScrollView();
            if (remove != null) SetMarked(remove, false);
        }

        // Быстрая отметка обороны
        float half = (w - 26f) * 0.5f;
        if (UiKit.Button(new Rect(x + 10f, y + quickY, half, UiKit.BtnH), "Отметить оборону", UiKit.Kind.Primary,
                "Отметить Ратушу, башни, казармы и пушки в зоне двора (пока есть место)"))
            MarkDefense();
        if (UiKit.Button(new Rect(x + 16f + half, y + quickY, half, UiKit.BtnH), "Снять все", marked.Count > 0, UiKit.Kind.Ghost, "Снять все отметки"))
            marked.Clear();

        // --- Настройки ---
        Rect t1 = new Rect(x + 10f, y + togglesY, w - 20f, 22f);
        OnlyMarked = GUI.Toggle(t1, OnlyMarked, " В войну — только отмеченные здания");
        UiKit.Tip(t1, "Включено: в войну строители чинят только отмеченные здания (и укрепления). Выключено: сначала отмеченные, потом самые побитые.");
        Rect t2 = new Rect(x + 10f, y + togglesY + 24f, w - 20f, 22f);
        PeaceRepair = GUI.Toggle(t2, PeaceRepair, " Чинить в мирное время");
        UiKit.Tip(t2, $"В рабочее время строители подлатывают здания ниже {PeaceRepairBelow * 100f:F0}% прочности. Тратит дерево.");

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId, "Как работают строители") && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UiKit.Section(x + 10f, y + upSecY, w - 20f, "Улучшение");
        UpgradeRules.DrawUpgradeSection(x + 10f, y + upY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }

    static bool InZoneOfAny(BuildingInstance b)
    {
        foreach (var y in All)
            if (y != null && y.Building != null && b.DistanceTo(y.Building.Center) <= y.ZoneCells) return true;
        return false;
    }

    // Ратуша, башни, казармы, пушки в зоне - в список (сначала самые ценные)
    void MarkDefense()
    {
        var list = new List<BuildingInstance>();
        foreach (var b in BuildingInstance.All)
        {
            if (!CanMark(b) || IsMarked(b) || !InZoneOfAny(b)) continue;
            string n = b.type.name;
            if (b.IsTownHall || n == "Башня" || n == BarracksController.TypeName || n == EngineerBarracks.TypeName
                || n == GroundCannonController.TypeName || n == MagicCannonController.TypeName)
                list.Add(b);
        }
        list.Sort((a, c) => Rank(a).CompareTo(Rank(c)));
        int added = 0;
        foreach (var b in list)
        {
            if (MarkedCount >= MaxMarks) break;
            if (SetMarked(b, true)) added++;
        }
        WarManager.Notify(added > 0 ? $"Отмечено для ремонта в войну: {added}" : "Нечего отмечать (или лимит отметок)", added > 0);
    }

    static int Rank(BuildingInstance b)
    {
        if (b.IsTownHall) return 0;
        if (b.type.name == "Башня") return 1;
        if (b.type.name == GroundCannonController.TypeName || b.type.name == MagicCannonController.TypeName) return 2;
        return 3;
    }
}
