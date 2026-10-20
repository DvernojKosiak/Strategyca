using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Дом целителей. Рабочие этого здания - целители (маги-лекари).
///  - В рабочее время целитель берёт еду из казны в сумку и ходит по городу:
///    лечит раненых и голодных жителей на улице, а заодно заходит в мастерские, шахты и т.п.
///    и подкармливает рабочих прямо на месте (им не надо бросать работу и идти обедать).
///    Магия экономнее обычной еды: одна единица еды даёт целителю больше здоровья, чем дома.
///  - Ур.2: лазарет. Тяжело раненый (или очень голодный) житель или солдат на охране ложится на койку
///    и быстро восстанавливается. После каждого пациента койке нужна перезарядка.
///  - Ур.3: больше целителей, коек, сумка и радиус больше, лечат и перезаряжаются быстрее.
/// Добавляется на здание в BuildingPlacer. Логика движения целителя - в CitizenAgent (раздел "Целитель").
/// </summary>
public class HealerHouse : MonoBehaviour
{
    public const string TypeName = "Дом целителей";

    public static readonly List<HealerHouse> All = new List<HealerHouse>();

    [Header("Кого лечат")]
    public float healBelow = 0.75f;          // целитель берётся за тех, у кого здоровья меньше этой доли
    public float healUpTo = 0.98f;           // и лечит почти до полного
    public float bedBelow = 0.4f;            // в лазарет ложатся, если здоровья меньше этой доли

    // ---------- Параметры по уровням (индекс = уровень - 1) ----------
    static readonly float[] ServiceRadiusByLevel = { 12f, 16f, 20f };   // как далеко от здания ходят целители
    static readonly float[] HealPerSecondByLevel = { 6f, 8f, 11f };     // скорость лечения
    static readonly float[] HpPerFoodByLevel = { 2f, 2f, 3f };          // сколько здоровья даёт 1 еда у целителя (дома - 1)
    static readonly int[] BagByLevel = { 25, 35, 50 };                  // сколько еды целитель уносит в сумке
    static readonly int[] BedsByLevel = { 0, 2, 4 };                    // коек в лазарете
    static readonly float[] BedCooldownByLevel = { 0f, 45f, 30f };      // перезарядка койки после пациента, с
    static readonly float[] BedHpPerSecondByLevel = { 0f, 15f, 22f };   // лечение на койке
    public const float BedHpPerFood = 3f;                               // на койке 1 еда = 3 здоровья
    public const float HealRange = 2.5f;                                // с какого расстояния целитель лечит
    const float ReservationTimeout = 40f;                               // столько ждём пациента, потом койка снова свободна

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public float ServiceRadius => ServiceRadiusByLevel[Lvl];
    public float HealPerSecond => HealPerSecondByLevel[Lvl];
    public float HpPerFood => HpPerFoodByLevel[Lvl];
    public int BagCapacity => BagByLevel[Lvl];
    public int Beds => BedsByLevel[Lvl];
    public float BedCooldown => BedCooldownByLevel[Lvl];
    public float BedHpPerSecond => BedHpPerSecondByLevel[Lvl];

    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;
    public int HealedTotal { get; private set; }     // скольких вылечили (для панели)
    public int FoodUsed { get; private set; }        // сколько еды потратили целители и лазарет

    // ---------- Кого уже лечат (чтобы два целителя не бежали к одному) ----------
    static readonly HashSet<CitizenAgent> claimedUnits = new HashSet<CitizenAgent>();
    static readonly HashSet<BuildingInstance> claimedBuildings = new HashSet<BuildingInstance>();

    public static bool ClaimUnit(CitizenAgent c) { return c != null && claimedUnits.Add(c); }
    public static bool ClaimBuilding(BuildingInstance b) { return b != null && claimedBuildings.Add(b); }
    public static void ReleaseUnit(CitizenAgent c) { if (c != null) claimedUnits.Remove(c); }
    public static void ReleaseBuilding(BuildingInstance b) { if (b != null) claimedBuildings.Remove(b); }

    // ---------- Койки ----------
    class Bed
    {
        public CitizenAgent who;      // кто занял (идёт или лежит)
        public float reservedAt;
        public bool arrived;          // уже лёг
        public float readyAt;         // до какого времени койка на перезарядке
    }
    readonly List<Bed> beds = new List<Bed>();

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();

        // Число коек следует за уровнем
        while (beds.Count < Beds) beds.Add(new Bed());
        while (beds.Count > Beds)
        {
            Bed last = beds[beds.Count - 1];
            if (last.who != null) break; // занятую не убираем, пока не освободится
            beds.RemoveAt(beds.Count - 1);
        }

        // Пациент так и не дошёл (передумал, погиб) - освобождаем койку без перезарядки
        foreach (var b in beds)
        {
            if (b.who == null) continue;
            if (b.who.IsDead || (!b.arrived && Time.time - b.reservedAt > ReservationTimeout))
            {
                b.who.DropBed(this);
                b.who = null;
                b.arrived = false;
            }
        }

        UpdateRadiusCircle();
    }

    // ---------- Лазарет ----------

    bool BedFree(Bed b) { return b.who == null && Time.time >= b.readyAt; }

    public int FreeBeds
    {
        get
        {
            if (!Works) return 0;
            int n = 0;
            for (int i = 0; i < beds.Count && i < Beds; i++) if (BedFree(beds[i])) n++;
            return n;
        }
    }

    bool TryReserve(CitizenAgent c)
    {
        if (!Works) return false;
        for (int i = 0; i < beds.Count && i < Beds; i++)
        {
            Bed b = beds[i];
            if (!BedFree(b)) continue;
            b.who = c;
            b.reservedAt = Time.time;
            b.arrived = false;
            return true;
        }
        return false;
    }

    public bool Holds(CitizenAgent c)
    {
        foreach (var b in beds) if (b.who == c) return true;
        return false;
    }

    // Пациент лёг на койку
    public void MarkArrived(CitizenAgent c)
    {
        foreach (var b in beds) if (b.who == c) b.arrived = true;
    }

    // Пациент ушёл. Если он успел полежать - койке нужна перезарядка.
    public void Release(CitizenAgent c)
    {
        foreach (var b in beds)
        {
            if (b.who != c) continue;
            if (b.arrived) { b.readyAt = Time.time + BedCooldown; HealedTotal++; }
            b.who = null;
            b.arrived = false;
        }
    }

    /// <summary>Ближайший лазарет со свободной койкой (койка сразу закрепляется за жителем).</summary>
    public static HealerHouse ReserveBed(CitizenAgent c, float maxDistance = 30f)
    {
        if (c == null) return null;
        Vector2 from = c.transform.position;
        HealerHouse best = null;
        float bestD = maxDistance;
        foreach (var h in All)
        {
            if (h == null || h.FreeBeds <= 0) continue;
            float d = h.Building.DistanceTo(from);
            if (d < bestD) { bestD = d; best = h; }
        }
        return best != null && best.TryReserve(c) ? best : null;
    }

    public void CountFood(int n) { FoodUsed += n; }
    public void CountHealed() { HealedTotal++; }

    // ---------- Поиск пациента для целителя ----------

    readonly Dictionary<BuildingInstance, int> hungryInside = new Dictionary<BuildingInstance, int>();
    readonly Dictionary<BuildingInstance, float> fracInside = new Dictionary<BuildingInstance, float>();

    // В какие здания целитель заходит подкормить рабочих: производство, мастерская и т.п.
    // Дома и Ратуша не нужны (там едят сами), казарма - тоже (солдаты лечатся там сами).
    static bool FeedableWorkplace(BuildingInstance b)
    {
        if (b == null || b.type == null || b.IsRuined || !b.isActiveAndEnabled) return false;
        if (b.IsTownHall || b.type.name == PopulationManager.HouseName) return false;
        if (b.GetComponent<HealerHouse>() != null || b.GetComponent<BarracksController>() != null) return false;
        return true;
    }

    /// <summary>
    /// Кого лечить: раненого/голодного на улице или здание с голодными рабочими внутри.
    /// Чем меньше здоровья и чем ближе - тем раньше. Уже занятых другими целителями пропускаем.
    /// </summary>
    public bool FindPatient(CitizenAgent healer, out CitizenAgent unit, out BuildingInstance bld)
    {
        unit = null;
        bld = null;
        if (!Works || healer == null) return false;

        Vector2 me = healer.transform.position;
        Vector2 home = Building.Center;
        float R = ServiceRadius;
        float best = float.MaxValue;

        hungryInside.Clear();
        fracInside.Clear();

        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a == healer) continue;
            float frac = a.Hp / a.MaxHp;
            if (frac >= healBelow) continue;

            if (a.IsInsideBuilding)
            {
                // Рабочие сидят в своём здании - запоминаем, где голодные
                BuildingInstance b = a.InsideBuilding;
                if (b != null && b == a.Workplace && FeedableWorkplace(b))
                {
                    hungryInside.TryGetValue(b, out int n);
                    hungryInside[b] = n + 1;
                    fracInside.TryGetValue(b, out float f);
                    fracInside[b] = f + frac;
                }
                continue;
            }

            if (a.IsLyingInBed) continue;
            if (Vector2.Distance(a.transform.position, home) > R) continue;
            if (claimedUnits.Contains(a)) continue;
            if (WarManager.IsWar && EnemyAgent.AnyWithin(a.transform.position, 4f)) continue; // под ударом не лечат

            float score = Vector2.Distance(me, a.transform.position) + frac * 15f - (a.IsSoldier ? 3f : 0f);
            if (score < best) { best = score; unit = a; }
        }

        foreach (var kv in hungryInside)
        {
            BuildingInstance b = kv.Key;
            if (claimedBuildings.Contains(b)) continue;
            if (b.DistanceTo(home) > R) continue;
            if (WarManager.IsWar && EnemyAgent.AnyWithin(b.Center, 5f)) continue;

            float avg = fracInside[b] / kv.Value;
            float score = b.DistanceTo(me) + avg * 15f - kv.Value * 1.5f; // где голодных больше - туда раньше
            if (score < best) { best = score; unit = null; bld = b; }
        }

        return unit != null || bld != null;
    }

    // ---------- Круг обслуживания на карте (виден, пока выбрано здание) ----------

    LineRenderer circle;
    int shownFrame = -10;

    public void ShowRadiusThisFrame() { shownFrame = Time.frameCount; }

    void UpdateRadiusCircle()
    {
        bool show = Time.frameCount - shownFrame <= 2 && Works;
        if (!show) { if (circle != null) circle.enabled = false; return; }

        if (circle == null)
        {
            GameObject go = new GameObject("HealRadius");
            circle = go.AddComponent<LineRenderer>();
            circle.useWorldSpace = true;
            circle.loop = true;
            circle.material = new Material(Shader.Find("Sprites/Default"));
            circle.widthMultiplier = 0.08f;
            circle.startColor = circle.endColor = new Color(0.45f, 1f, 0.7f, 0.5f);
            circle.sortingOrder = -40;
            circle.positionCount = 72;
        }

        circle.enabled = true;
        Vector2 c = Building.Center;
        float r = ServiceRadius;
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72;
            circle.SetPosition(i, new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f));
        }
    }

    void OnDestroy()
    {
        if (circle != null) Destroy(circle.gameObject);
        foreach (var b in beds) if (b.who != null) b.who.DropBed(this);
    }

    // ---------- Панель здания ----------

    int HealersOut(out int foodInBags)
    {
        int n = 0;
        foodInBags = 0;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.Workplace != Building) continue;
            foodInBags += a.HealBag;
            if (a.IsHealerBusy) n++;
        }
        return n;
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        ShowRadiusThisFrame();
        BuildingInstance b = Building;
        int lvl = b.level;
        float cx = x + UiKit.Pad, cw = w - UiKit.Pad * 2f;

        string status = b.isWorking ? "" : b.StatusText;
        float statusH = status != "" ? UiKit.TextH(status, cw, UiKit.Small) + 4f : 0f;
        float rowsY = UiKit.TitleH + 6f + statusH;
        float bedsY = rowsY + UiKit.RowH * 3f + 4f;
        float bedsH = Beds > 0 ? UiKit.SectionH + Beds * 18f + 4f : 0f;
        float workersY = bedsY + bedsH;
        float upgradeY = workersY + 30f;
        float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName}  {UiKit.Col(UiKit.MutedHex, "· Ур. " + lvl)}");

        // Что не так - сразу под шапкой
        if (status != "")
            GUI.Label(new Rect(cx, y + UiKit.TitleH + 6f, cw, statusH), UiKit.Col(UiKit.BadHex, status), UiKit.Small);

        int out_ = HealersOut(out int bags);
        float cy = y + rowsY;
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "В обходе", $"{out_}  {UiKit.Col(UiKit.MutedHex, "· еды в сумках " + bags)}");
        cy += UiKit.RowH;
        Rect rr = new Rect(cx, cy, cw, UiKit.RowH);
        UiKit.Row(rr, "Радиус · лечение", $"{ServiceRadius:F0} кл. · {HealPerSecond:F0}/с");
        UiKit.Tip(rr, $"Лечат тех, у кого меньше {healBelow * 100f:F0}% здоровья, и подкармливают рабочих прямо в зданиях.\n1 еда = {HpPerFood:F0} здоровья.");
        cy += UiKit.RowH;
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Вылечено", $"{HealedTotal}  {UiKit.Col(UiKit.MutedHex, "· еды " + FoodUsed)}");

        if (Beds > 0)
        {
            float by = UiKit.Section(cx, y + bedsY, cw, $"Лазарет · коек {Beds}");
            UiKit.Tip(new Rect(cx, y + bedsY, cw, UiKit.SectionH), $"Койка после пациента перезаряжается {BedCooldown:F0} с.");
            for (int i = 0; i < Beds && i < beds.Count; i++)
            {
                Bed bed = beds[i];
                string bs;
                if (bed.who != null && bed.arrived)
                    bs = UiKit.Col(UiKit.GoodHex, $"пациент: {Mathf.RoundToInt(bed.who.Hp / bed.who.MaxHp * 100f)}%");
                else if (bed.who != null) bs = UiKit.Col(UiKit.WarnHex, "пациент идёт");
                else if (Time.time < bed.readyAt) bs = UiKit.Col(UiKit.WarnHex, $"перезарядка {Mathf.CeilToInt(bed.readyAt - Time.time)} с");
                else bs = UiKit.Col(UiKit.MutedHex, "свободна");
                GUI.Label(new Rect(cx, by, cw, 18f), $"Койка {i + 1}: {bs}", UiKit.Small);
                by += 18f;
            }
        }

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(cx, y + workersY, b);

        float uy = UiKit.Section(cx, y + upgradeY, cw, "Улучшение");
        UpgradeRules.DrawUpgradeSection(cx, uy, cw, b);
        return new Rect(x, y, w + 5f, h);
    }
}
