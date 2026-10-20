using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Мост (клетка моста через реку). Строят все с Ратуши Ур.2: ставится только на воду, от берега или
/// от уже построенной клетки моста (тянут как дорожку). По целому мосту ходят все, мирные жители тоже.
/// Мост можно сломать: разрушенная клетка - снова река, мирные по ней не пройдут (солдаты - вброд),
/// пока клетку не отстроят (как любые руины).
///
/// Раздвижной мост (путь Инженерии): на любую клетку пролёта ставят модуль «Раздвижной механизм»
/// из Инженерного двора (как отбойник на башню). Механизм поднимает ВЕСЬ пролёт - все клетки моста,
/// соединённые друг с другом. Поднятый мост - стена для всех: не пройдут ни враги, ни свои.
///  - Мк.I: поднимают вручную (кнопка в панели), подъём 4 с;
///  - Мк.II-III: быстрее, и есть «Авто» - механизм сам поднимает мост, когда к нему подходят враги,
///    и опускает, когда их рядом нет;
///  - поднятый мост крепче: враги снимают с него только часть урона.
/// Проходимость считает GridNavigation (BlocksPassage). Добавляется на здание "Мост" в BuildingPlacer.
/// </summary>
public class BridgeController : MonoBehaviour, IModuleHost
{
    public static readonly List<BridgeController> All = new List<BridgeController>();

    const float AutoRaiseRadius = 5f;    // враг ближе к пролёту - механизм (Мк.II+) поднимает мост
    const float AutoLowerRadius = 7f;    // ...и опускает, когда врагов дальше этого не меньше AutoLowerDelay
    const float AutoLowerDelay = 3f;

    BuildingInstance bld;
    public BuildingInstance Building { get { if (bld == null) bld = GetComponent<BuildingInstance>(); return bld; } }

    // ---------- Модуль (путь Инженерии) ----------
    public ModuleKind Module { get; private set; } = ModuleKind.None;
    public int ModuleMk { get; private set; }
    public bool HasModule => Module != ModuleKind.None;
    public bool ModuleBusy { get; set; }
    public bool ModuleRemoving { get; set; }

    public void SetModule(ModuleKind kind, int mk)
    {
        Module = kind;
        ModuleMk = kind == ModuleKind.None ? 0 : Mathf.Clamp(mk, 1, 3);
        autoRaise = TowerModules.DrawbridgeAuto(ModuleMk);
    }

    public ModuleKit TakeModule()
    {
        ModuleKit kit = new ModuleKit { kind = Module, mk = ModuleMk };
        Module = ModuleKind.None;
        ModuleMk = 0;
        wantRaised = false;
        return kit;
    }

    // ---------- Подъём ----------
    bool wantRaised;          // хочет механизм (хранится у клетки с модулем - "хозяина" пролёта)
    bool autoRaise;
    float calmTimer;
    float raise;              // 0 - опущен, 1 - поднят (плавно)
    bool lastBlocks;

    /// <summary>Клетка сейчас непроходима для всех: мост поднят (и не в руинах).</summary>
    public bool BlocksPassage => Building != null && !Building.IsRuined && raise > 0.5f;
    public bool IsRaised => raise > 0.5f;

    /// <summary>Какую долю урона получает клетка моста (поднятый раздвижной мост крепче).</summary>
    public static float DamageTakenMul(BuildingInstance b)
    {
        BridgeController bc = b != null ? b.GetComponent<BridgeController>() : null;
        if (bc == null || !bc.IsRaised) return 1f;
        BridgeController m = bc.Master;
        return m != null ? TowerModules.DrawbridgeArmor(m.ModuleMk) : 1f;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); cacheFrame = -1; }
    void OnDisable() { All.Remove(this); cacheFrame = -1; if (gap != null) gap.enabled = false; }

    void OnDestroy()
    {
        if (gapRoot != null) Destroy(gapRoot);
        if (lastBlocks) GridNavigation.MarkDirty();
    }

    // ---------- Пролёт: клетки моста, соединённые друг с другом ----------

    static readonly Dictionary<Vector2Int, BridgeController> byCell = new Dictionary<Vector2Int, BridgeController>();
    static readonly Dictionary<BridgeController, int> spanOf = new Dictionary<BridgeController, int>();
    static readonly List<BridgeController> masters = new List<BridgeController>();   // по номеру пролёта
    static readonly List<int> spanSize = new List<int>();
    static int cacheFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { All.Clear(); byCell.Clear(); spanOf.Clear(); masters.Clear(); spanSize.Clear(); cacheFrame = -1; }

    static readonly Vector2Int[] Dirs4 = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };

    static void EnsureCache()
    {
        if (cacheFrame == Time.frameCount) return;
        cacheFrame = Time.frameCount;
        byCell.Clear();
        spanOf.Clear();
        masters.Clear();
        spanSize.Clear();
        foreach (var b in All)
            if (b != null && b.Building != null) byCell[b.Building.OriginCell] = b;

        var stack = new Stack<BridgeController>();
        foreach (var b in All)
        {
            if (b == null || b.Building == null || spanOf.ContainsKey(b)) continue;
            int id = masters.Count;
            BridgeController master = null;
            int size = 0;
            spanOf[b] = id;
            stack.Push(b);
            while (stack.Count > 0)
            {
                BridgeController c = stack.Pop();
                size++;
                if (c.HasModule && (master == null || c.ModuleMk > master.ModuleMk)) master = c;
                Vector2Int cell = c.Building.OriginCell;
                foreach (var d in Dirs4)
                {
                    if (!byCell.TryGetValue(cell + d, out BridgeController n) || n == null || spanOf.ContainsKey(n)) continue;
                    spanOf[n] = id;
                    stack.Push(n);
                }
            }
            masters.Add(master);
            spanSize.Add(size);
        }
    }

    /// <summary>Клетка пролёта, на которой стоит механизм (null - механизма нет).</summary>
    public BridgeController Master
    {
        get
        {
            EnsureCache();
            return spanOf.TryGetValue(this, out int id) ? masters[id] : null;
        }
    }

    public int SpanLength
    {
        get
        {
            EnsureCache();
            return spanOf.TryGetValue(this, out int id) ? spanSize[id] : 1;
        }
    }

    List<BridgeController> SpanTiles()
    {
        EnsureCache();
        var list = new List<BridgeController>();
        if (!spanOf.TryGetValue(this, out int id)) { list.Add(this); return list; }
        foreach (var kv in spanOf) if (kv.Value == id && kv.Key != null) list.Add(kv.Key);
        return list;
    }

    bool EnemyNearSpan(float radius)
    {
        foreach (var t in SpanTiles())
            if (t != null && t.Building != null && EnemyAgent.AnyWithin(t.Building.Center, radius)) return true;
        return false;
    }

    // ---------- Кадр ----------

    void Update()
    {
        if (Building == null) return;
        BridgeController m = Master;

        // Хозяин пролёта решает, поднят ли мост (вручную или сам - Мк.II+)
        if (m == this && autoRaise && TowerModules.DrawbridgeAuto(ModuleMk) && !ModuleBusy)
        {
            if (EnemyNearSpan(AutoRaiseRadius)) { calmTimer = 0f; if (!wantRaised) { wantRaised = true; WarManager.Notify("Враги у моста — механизм поднял мост", true); } }
            else if (wantRaised)
            {
                if (EnemyNearSpan(AutoLowerRadius)) calmTimer = 0f;
                else
                {
                    calmTimer += Time.deltaTime;
                    if (calmTimer >= AutoLowerDelay) wantRaised = false;
                }
            }
        }

        bool want = m != null && m.wantRaised && !Building.IsRuined;
        float secs = m != null ? TowerModules.DrawbridgeSeconds(m.ModuleMk) : 2f;
        raise = Mathf.MoveTowards(raise, want ? 1f : 0f, Time.deltaTime / Mathf.Max(0.2f, secs));

        bool blocks = BlocksPassage;
        if (blocks != lastBlocks) { lastBlocks = blocks; GridNavigation.MarkDirty(); }
        UpdateLook(m);
    }

    // ---------- Вид: поднятый мост открывает воду ----------

    GameObject gapRoot;
    SpriteRenderer gap, gear;

    void UpdateLook(BridgeController m)
    {
        if (gapRoot == null)
        {
            gapRoot = new GameObject("BridgeGap");
            gap = MakePart("Gap", new Color(0.16f, 0.4f, 0.7f, 0f), -8);
            gear = MakePart("Gear", new Color(1f, 0.8f, 0.3f), -7);
            gear.sprite = CombatFx.Circle();
        }
        Vector2 c = Building.Center;
        gapRoot.transform.position = new Vector3(c.x, c.y, 0f);
        gap.enabled = raise > 0.01f;
        gap.transform.localScale = new Vector3(0.96f, 0.96f * raise, 1f);
        gap.color = new Color(0.16f, 0.4f, 0.7f, 0.9f * raise);
        gear.enabled = HasModule && !Building.IsRuined;
        gear.transform.localScale = new Vector3(0.28f, 0.28f, 1f);
        gear.transform.localRotation = Quaternion.Euler(0f, 0f, raise * 180f);
    }

    SpriteRenderer MakePart(string n, Color col, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(gapRoot.transform, false);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = CombatFx.Square();
        r.color = col;
        r.sortingOrder = order;
        return r;
    }

    // ---------- Панель ----------

    public string StateText
    {
        get
        {
            if (Building.IsRuined) return UiKit.Col(UiKit.BadHex, "Разрушен - здесь снова река");
            if (raise > 0.5f) return raise < 0.99f ? UiKit.Col(UiKit.WarnHex, "Поднимается...") : UiKit.Col(UiKit.BadHex, "Поднят - не проходит никто");
            if (raise > 0.01f) return UiKit.Col(UiKit.WarnHex, "Опускается...");
            return UiKit.Col(UiKit.GoodHex, "Опущен - проходят все");
        }
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        BridgeController m = Master;
        IModuleHost host = m != null ? (IModuleHost)m : this;

        GUIStyle st = UiKit.Body;

        float modH = EngineerYard.HostSectionHeight(host, ModuleKind.Drawbridge);
        float ctrlH = m != null ? 34f + (TowerModules.DrawbridgeAuto(m.ModuleMk) ? 26f : 0f) : 0f;
        float stateY = UiKit.TitleH + 6f;
        float modY = stateY + 26f;
        float h = modY + (modH > 0f ? modH : 0f) + ctrlH + 8f;
        UiKit.Window(new Rect(x, y, w, h), "Мост  " + UiKit.Col(UiKit.MutedHex, $"пролёт {SpanLength} кл."));

        float cy = y + stateY;
        Rect sr = new Rect(x + 10f, cy, w - 20f, 22f);
        GUI.Label(sr, StateText, st);
        UiKit.Tip(sr, m != null
            ? "Поднятый мост не пропускает никого - ни врагов, ни своих. Поднимается весь пролёт."
            : (b.IsRuined ? "Пока мост не отстроят, мирные не пройдут, солдаты - только вброд."
                          : "По мосту ходят все. Сломанный мост - снова река, пока его не отстроят."));
        cy = y + modY;

        if (modH > 0f) EngineerYard.DrawHostSection(x + 10f, cy, w - 20f, host, ModuleKind.Drawbridge);
        cy += modH;

        if (m != null)
        {
            bool ok = !b.IsRuined;
            string why = ok ? null : "Мост разрушен";
            float bw = (w - 26f) / 2f;
            if (UiKit.Button(new Rect(x + 10f, cy, bw, 28f), m.wantRaised ? "● Поднять" : "Поднять", ok,
                    UiKit.Kind.Normal, why ?? "Закрыть проход для всех"))
            { m.wantRaised = true; m.calmTimer = 0f; }
            if (UiKit.Button(new Rect(x + 16f + bw, cy, bw, 28f), !m.wantRaised ? "● Опустить" : "Опустить", ok,
                    UiKit.Kind.Normal, why ?? "Открыть проход"))
                m.wantRaised = false;
            cy += 34f;

            if (TowerModules.DrawbridgeAuto(m.ModuleMk))
            {
                Rect ar = new Rect(x + 10f, cy, w - 20f, 22f);
                bool auto = GUI.Toggle(ar, m.autoRaise, " Поднимать сам при врагах");
                UiKit.Tip(ar, "Мост поднимается, когда враги подходят, и опускается, когда рядом спокойно.");
                if (auto != m.autoRaise) m.autoRaise = auto;
            }
        }
        return new Rect(x, y, w + 5f, h);
    }
}
