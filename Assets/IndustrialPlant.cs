using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Завод (путь Инженерии, школа «Промышленник»). Вокруг завода - ПЛОЩАДКА: все производства, чей центр
/// попадает в квадрат «здание завода + ZoneByLevel клеток» (6 / 7 / 8 по уровню), работают быстрее,
/// пока завод топят деревом из казны (ResourceManager.wood).
///
/// РЕЖИМ ПЕЧЕЙ (как котлы Литейной):
///  - Тихо    - завод не топят: бонуса нет, печи остывают;
///  - Ровно   - 1 дерево / 3 с, производства +20%, жар понемногу спадает;
///  - Форсаж  - 1 дерево / 1.5 с, +45%, жар растёт;
///  - Предел  - 1 дерево / 1 с, +75%, жар растёт быстро.
/// Бонус умножается на долю рабочих завода (3 из 3 = полный).
/// На 100% жара - ВЗРЫВ: завод и все постройки на площадке теряют BlastDamage от прочности,
/// производства на площадке сломаны ZoneBreakSeconds (стоят), сам завод - PlantBreakSeconds.
/// «Предохранитель» (по умолчанию включён) сам сбрасывает печи на «Ровно» при SafetyHeat.
///
/// Бонус приходит в BuildingInstance.HealthFactor через WorkMul (кэш, пересчёт раз в секунду).
/// Зона рисуется на карте, пока выбран завод (BuildingPlacer.Selected) или игрок ставит постройку (BuildingPlacer.PlacingNow).
/// Компонент добавляет BuildingPlacer.
/// </summary>
public class IndustrialPlant : MonoBehaviour
{
    public const string TypeName = "Завод";

    public static readonly List<IndustrialPlant> All = new List<IndustrialPlant>();

    // ---------- Режимы ----------
    public enum Furnace { Quiet, Even, Forced, Limit }
    static readonly string[] FurnaceNames = { "Тихо", "Ровно", "Форсаж", "Предел" };
    static readonly float[] FurnaceBonus  = { 0f, 0.20f, 0.45f, 0.75f };   // прибавка к скорости производств
    static readonly float[] FuelEvery     = { 0f, 3f, 1.5f, 1f };          // секунд на 1 дерево (0 - не топят)
    static readonly float[] FurnaceHeat   = { -6f, -1.5f, 1.2f, 3f };      // жар в секунду, пока завод топят
    static readonly string[] FurnaceTips  =
    {
        "Тихо: завод не топят. Бонуса нет, печи быстро остывают.",
        "Ровно: 1 дерево / 3 с. Производства на площадке +20%. Жар спадает - так можно хоть вечно.",
        "Форсаж: 1 дерево / 1.5 с. Производства +45%, но жар растёт. Держите глаз на шкале.",
        "Предел: 1 дерево / с. Производства +75%, жар растёт быстро - до взрыва меньше минуты.",
    };

    //                                            Ур.1  Ур.2  Ур.3
    static readonly int[] ZoneByLevel         = { 6,    7,    8 };
    static readonly float[] HeatGainByLevel   = { 1f,   0.8f, 0.65f };   // печи получше греются медленнее

    const float IdleCooling = 5f;
    public const float WarnHeat = 80f;
    public const float SafetyHeat = 90f;
    public const float MaxHeat = 100f;
    public const float BlastDamage = 0.25f;        // доля прочности, которую теряют завод и постройки площадки
    public const float ZoneBreakSeconds = 60f;     // производства на площадке стоят после взрыва
    public const float PlantBreakSeconds = 90f;    // сам завод стоит после взрыва
    const float MembersEvery = 2f;                 // как часто пересчитывать, кто на площадке

    public Furnace Mode = Furnace.Even;
    public bool Safety = true;

    public float Heat { get; private set; }
    public float BrokenLeft { get; private set; }
    public bool Broken => BrokenLeft > 0f;
    public int Explosions { get; private set; }
    public int WoodBurned { get; private set; }
    public bool Fueled { get; private set; } = true;   // последнее дерево удалось взять

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;
    public int ZoneCells => ZoneByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    // Кто на площадке: все постройки (для взрыва) и производства (для бонуса)
    readonly List<BuildingInstance> inZone = new List<BuildingInstance>();
    readonly List<BuildingInstance> producers = new List<BuildingInstance>();
    public int ProducerCount => producers.Count;
    float membersTimer;

    float fuelTimer, fxTimer;
    bool warned, noFuelWarned;
    SpriteRenderer look;
    bool tinted;

    // ---------- Общий кэш для WorkMul ----------
    static readonly Dictionary<BuildingInstance, float> bonusCache = new Dictionary<BuildingInstance, float>();
    static readonly Dictionary<BuildingInstance, float> brokenUntil = new Dictionary<BuildingInstance, float>();
    static float cacheAt = -100f;
    static bool cacheDirty = true;
    const float CacheEvery = 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        All.Clear();
        bonusCache.Clear();
        brokenUntil.Clear();
        cacheAt = -100f;
        cacheDirty = true;
    }

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); cacheDirty = true; membersTimer = 0f; }
    void OnDisable() { All.Remove(this); cacheDirty = true; HideZone(); }
    void OnDestroy() { DestroyZoneFx(); }

    // ---------- Множитель для BuildingInstance.HealthFactor ----------

    /// <summary>1 - обычная скорость; 1+бонус - на площадке работающего завода; 0 - сломано взрывом.</summary>
    public static float WorkMul(BuildingInstance b)
    {
        if (b == null || All.Count == 0 || !DevelopmentPath.IsIndustrial) return 1f;
        float now = Time.time;
        if (brokenUntil.Count > 0)
        {
            float until;
            if (brokenUntil.TryGetValue(b, out until))
            {
                if (now < until) return 0f;
                brokenUntil.Remove(b);
            }
        }
        if (cacheDirty || now - cacheAt >= CacheEvery || now < cacheAt) RebuildCache(now);
        float bonus;
        return bonusCache.TryGetValue(b, out bonus) ? 1f + bonus : 1f;
    }

    /// <summary>Постройка сломана взрывом завода (сколько секунд ещё стоит; 0 - не сломана).</summary>
    public static float BrokenSecondsLeft(BuildingInstance b)
    {
        float until;
        if (b == null || !brokenUntil.TryGetValue(b, out until)) return 0f;
        return Mathf.Max(0f, until - Time.time);
    }

    static void RebuildCache(float now)
    {
        cacheAt = now;
        cacheDirty = false;
        bonusCache.Clear();
        foreach (var p in All)
        {
            if (p == null) continue;
            float bonus = p.CurrentBonus;
            if (bonus <= 0f) continue;
            foreach (var b in p.producers)
            {
                if (b == null) continue;
                float old;
                if (!bonusCache.TryGetValue(b, out old) || old < bonus) bonusCache[b] = bonus;
            }
        }
    }

    // Доля бригады завода - без HealthFactor (он сам зовёт WorkMul)
    float StaffFrac
    {
        get
        {
            if (Building == null) return 0f;
            if (Building.workersNeeded <= 0) return 1f;
            return Mathf.Clamp01((float)Building.workers / Building.workersNeeded) * Building.OffHoursFactor;
        }
    }

    /// <summary>Завод топят и он даёт бонус прямо сейчас.</summary>
    public bool Running => Works && !Broken && Mode != Furnace.Quiet && Fueled && Building.isWorking && producers.Count > 0;

    public float CurrentBonus => Running ? FurnaceBonus[(int)Mode] * StaffFrac : 0f;

    // ---------- Кто на площадке ----------

    static bool IsProduction(BuildingInstance b)
    {
        if (b == null || b.type == null) return false;
        if (b.IsTownHall || b.IsRoad || b.IsBarrier || b.IsBridge || b.IsOutpost || b.IsGuestHouse) return false;
        if (b.workersNeeded <= 0) return false;   // производство - там, где работают люди
        switch (b.type.name)
        {
            case TypeName:
            case "Жилой дом":
            case "Башня":
            case "Инженерная казарма":
            case BarracksController.TypeName:
            case GroundCannonController.TypeName:
            case MagicCannonController.TypeName:
            case CourierHouse.TypeName:
            case WarehouseController.TypeName:
            case HealerHouse.TypeName:
            case MarketHouse.TypeName:
            case LifeAltar.TypeName:
                return false;
        }
        return true;
    }

    // Квадрат площадки в клетках: [min, max)
    void ZoneRect(out Vector2 min, out Vector2 max)
    {
        Vector2Int o = Building.OriginCell;
        Vector2Int s = Building.type != null ? Building.type.size : new Vector2Int(3, 3);
        int r = ZoneCells;
        min = new Vector2(o.x - r, o.y - r);
        max = new Vector2(o.x + s.x + r, o.y + s.y + r);
    }

    static Vector2 FootCenter(BuildingInstance b)
    {
        Vector2Int o = b.OriginCell;
        Vector2Int s = b.type != null ? b.type.size : Vector2Int.one;
        return new Vector2(o.x + s.x * 0.5f, o.y + s.y * 0.5f);
    }

    public bool Covers(BuildingInstance b)
    {
        if (b == null || Building == null || b == Building) return false;
        Vector2 min, max;
        ZoneRect(out min, out max);
        Vector2 c = FootCenter(b);
        return c.x >= min.x && c.x <= max.x && c.y >= min.y && c.y <= max.y;
    }

    void RefreshMembers()
    {
        inZone.Clear();
        producers.Clear();
        if (Building == null || Building.type == null) return;
        Vector2 min, max;
        ZoneRect(out min, out max);
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b == Building || b.type == null || b.IsRoad) continue;
            if (b.GetComponent<IndustrialPlant>() != null) continue;   // заводы друг друга не разгоняют
            Vector2 c = FootCenter(b);
            if (c.x < min.x || c.x > max.x || c.y < min.y || c.y > max.y) continue;
            inZone.Add(b);
            if (IsProduction(b)) producers.Add(b);
        }
        cacheDirty = true;
    }

    // ---------- Работа ----------

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();
        if (Building == null) return;
        float dt = Time.deltaTime;

        membersTimer -= dt;
        if (membersTimer <= 0f) { membersTimer = MembersEvery; RefreshMembers(); }

        UpdateZoneFx();

        if (Building.IsRuined)
        {
            Heat = 0f;
            BrokenLeft = 0f;
            tinted = false;
            return;
        }

        bool wasRunning = Running;

        if (Broken)
        {
            BrokenLeft = Mathf.Max(0f, BrokenLeft - dt);
            Heat = Mathf.Max(0f, Heat - IdleCooling * dt);
            if (!Broken)
            {
                cacheDirty = true;
                WarManager.Notify("Завод: печи починили, можно снова топить", true);
            }
        }
        else
        {
            bool wantsFire = DevelopmentPath.IsIndustrial && Mode != Furnace.Quiet && Building.isWorking && producers.Count > 0;
            if (wantsFire)
            {
                // Топливо: дерево из казны
                fuelTimer -= dt;
                if (fuelTimer <= 0f)
                {
                    var rm = ResourceManager.Instance;
                    bool got = rm != null && rm.Spend(0, 1, 0, 0);
                    if (got)
                    {
                        WoodBurned++;
                        fuelTimer += FuelEvery[(int)Mode];
                        if (fuelTimer < 0f) fuelTimer = 0f;
                        if (!Fueled) cacheDirty = true;
                        Fueled = true;
                        noFuelWarned = false;
                    }
                    else
                    {
                        fuelTimer = 1f;   // попробуем снова через секунду
                        if (Fueled) cacheDirty = true;
                        Fueled = false;
                        if (!noFuelWarned)
                        {
                            noFuelWarned = true;
                            WarManager.Notify("Завод: в казне кончилось дерево - печи гаснут, площадка работает без бонуса", false);
                        }
                    }
                }
            }
            else if (fuelTimer > FuelEvery[(int)Mode]) fuelTimer = FuelEvery[(int)Mode];

            if (wantsFire && Fueled)
            {
                Heat = Mathf.Clamp(Heat + HeatRate(Mode) * dt, 0f, MaxHeat);
                CheckHeat();
            }
            else
            {
                Heat = Mathf.Max(0f, Heat - IdleCooling * dt);
            }
        }

        if (warned && Heat < WarnHeat - 15f) warned = false;
        if (Running != wasRunning) cacheDirty = true;

        UpdateLook(Running, dt);
    }

    float HeatRate(Furnace m)
    {
        float r = FurnaceHeat[(int)m];
        return r > 0f ? r * HeatGainByLevel[Lvl] : r;
    }

    void SetMode(Furnace m)
    {
        if (m == Mode) return;
        Mode = m;
        fuelTimer = Mathf.Min(fuelTimer, FuelEvery[(int)m]);
        cacheDirty = true;
    }

    void CheckHeat()
    {
        if (Safety && Mode >= Furnace.Forced && Heat >= SafetyHeat)
        {
            SetMode(Furnace.Even);
            WarManager.Notify($"Завод: предохранитель сбросил печи на «Ровно» (жар {Heat:F0}%)", true);
            return;
        }

        if (!warned && Heat >= WarnHeat && HeatRate(Mode) > 0f)
        {
            warned = true;
            WarManager.Notify($"Завод раскалился ({Heat:F0}%)! Сбавьте печи - иначе взрыв и поломка всей площадки", false);
        }

        if (Heat >= MaxHeat) Explode();
    }

    // Взрыв: урон заводу и всей площадке, производства стоят ZoneBreakSeconds, завод - PlantBreakSeconds
    void Explode()
    {
        Explosions++;
        RefreshMembers();
        SetMode(Furnace.Even);
        Heat = MaxHeat * 0.6f;
        BrokenLeft = PlantBreakSeconds;
        warned = false;

        Vector2 c = Building.Center;
        float r = ZoneCells + 1.5f;
        CombatFx.Blast(c, r);
        CombatFx.Ring(c, r, new Color(1f, 0.45f, 0.12f, 0.85f), 0.9f);
        CombatFx.Ring(c, r * 0.55f, new Color(1f, 0.8f, 0.25f, 0.85f), 0.6f);
        CombatFx.Ring(c, 2.5f, new Color(1f, 0.3f, 0.1f, 0.9f), 0.4f);
        CombatFx.Flash(c);
        for (int i = 0; i < 18; i++)
            CombatFx.Spark(c + Random.insideUnitCircle * 2f, Random.value < 0.5f ? new Color(1f, 0.4f, 0.1f) : new Color(1f, 0.85f, 0.3f), 0.3f, 0.8f);

        float until = Time.time + ZoneBreakSeconds;
        int hit = 0, broke = 0;
        foreach (var b in inZone)
        {
            if (b == null || b.IsRuined) continue;
            Vector2 bc = b.Center;
            for (int i = 0; i < 3; i++)
                CombatFx.Spark(bc + Random.insideUnitCircle * 0.8f, new Color(1f, 0.55f, 0.2f), 0.22f, 0.6f);
            CombatFx.Ring(bc, 1.2f, new Color(1f, 0.5f, 0.15f, 0.7f), 0.5f);
            if (b.IsDamageable) { b.TakeDamage(b.MaxHp * BlastDamage); hit++; }
            if (IsProduction(b))
            {
                float old;
                if (!brokenUntil.TryGetValue(b, out old) || old < until) brokenUntil[b] = until;
                broke++;
            }
        }
        Building.TakeDamage(Building.MaxHp * BlastDamage);
        cacheDirty = true;

        WarManager.Notify($"ВЗРЫВ НА ЗАВОДЕ! Задето построек: {hit}. Сломано производств: {broke} - стоят {ZoneBreakSeconds:F0} с. " +
                          $"Завод в ремонте {PlantBreakSeconds:F0} с", false);
    }

    // Завод краснеет от жара, в ремонте - тёмный. Из труб летят искры и дым.
    void UpdateLook(bool running, float dt)
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

        if (!running && !Broken) return;
        fxTimer -= dt;
        if (fxTimer > 0f) return;
        Vector2 top = (Vector2)look.bounds.center + new Vector2(0f, look.bounds.extents.y * 0.7f);
        if (Broken)
        {
            fxTimer = 0.5f;
            CombatFx.Spark(top + Random.insideUnitCircle * 0.5f, new Color(0.3f, 0.3f, 0.3f, 0.8f), 0.3f, 0.7f);   // дым
            return;
        }
        fxTimer = Mode >= Furnace.Forced ? 0.2f : 0.7f;
        Color sc = Heat >= WarnHeat ? new Color(1f, 0.35f, 0.15f) : new Color(1f, 0.75f, 0.3f);
        CombatFx.Spark(top + Random.insideUnitCircle * 0.5f, sc, 0.14f, 0.3f);
        if (Random.value < 0.4f) CombatFx.Spark(top + Random.insideUnitCircle * 0.3f, new Color(0.45f, 0.45f, 0.45f, 0.7f), 0.25f, 0.6f);
    }

    // ---------- Зона на карте ----------

    GameObject zoneRoot;
    SpriteRenderer zoneFill;
    readonly SpriteRenderer[] zoneEdges = new SpriteRenderer[4];
    readonly List<GameObject> gears = new List<GameObject>();

    bool ZoneVisible
    {
        get
        {
            if (!Works || !DevelopmentPath.IsIndustrial) return false;
            return BuildingPlacer.PlacingNow || BuildingPlacer.Selected == Building;
        }
    }

    void UpdateZoneFx()
    {
        if (!ZoneVisible) { HideZone(); return; }

        if (zoneRoot == null)
        {
            zoneRoot = new GameObject("PlantZone");
            zoneFill = MakeSprite(zoneRoot.transform, CombatFx.Square(), -45);
            for (int i = 0; i < 4; i++) zoneEdges[i] = MakeSprite(zoneRoot.transform, CombatFx.Square(), -44);
        }
        zoneRoot.SetActive(true);

        Vector2 min, max;
        ZoneRect(out min, out max);
        Vector2 size = max - min;
        Vector2 mid = (min + max) * 0.5f;
        bool hot = Heat >= WarnHeat || Broken;
        Color edge = hot ? new Color(1f, 0.35f, 0.25f, 0.75f) : (Running ? new Color(1f, 0.72f, 0.3f, 0.7f) : new Color(0.75f, 0.75f, 0.75f, 0.55f));
        Color fill = new Color(edge.r, edge.g, edge.b, 0.08f);

        Place(zoneFill, mid, size, fill);
        const float t = 0.1f;
        Place(zoneEdges[0], new Vector2(mid.x, max.y - t * 0.5f), new Vector2(size.x, t), edge);
        Place(zoneEdges[1], new Vector2(mid.x, min.y + t * 0.5f), new Vector2(size.x, t), edge);
        Place(zoneEdges[2], new Vector2(min.x + t * 0.5f, mid.y), new Vector2(t, size.y), edge);
        Place(zoneEdges[3], new Vector2(max.x - t * 0.5f, mid.y), new Vector2(t, size.y), edge);

        // Шестерёнки над производствами площадки (красные - сломаны взрывом)
        int n = 0;
        float spin = Time.time * 90f;
        foreach (var b in producers)
        {
            if (b == null || b.type == null) continue;
            if (n >= gears.Count) gears.Add(MakeGear(zoneRoot.transform));
            GameObject g = gears[n++];
            g.SetActive(true);
            Vector2Int o = b.OriginCell;
            Vector2Int s = b.type.size;
            g.transform.position = new Vector3(o.x + s.x - 0.3f, o.y + s.y - 0.3f, 0f);
            g.transform.rotation = Quaternion.Euler(0f, 0f, Running ? -spin : 0f);
            Color gc = BrokenSecondsLeft(b) > 0f ? new Color(1f, 0.35f, 0.25f, 0.95f)
                     : (Running ? new Color(1f, 0.8f, 0.35f, 0.95f) : new Color(0.75f, 0.75f, 0.75f, 0.85f));
            foreach (var sr in g.GetComponentsInChildren<SpriteRenderer>())
                if (sr.sortingOrder == 18) sr.color = gc;
        }
        for (int i = n; i < gears.Count; i++) if (gears[i] != null) gears[i].SetActive(false);
    }

    static SpriteRenderer MakeSprite(Transform parent, Sprite sprite, int order)
    {
        var go = new GameObject("z");
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

    // Шестерёнка: круг + 4 зубца крестом + тёмная ступица
    static GameObject MakeGear(Transform parent)
    {
        var g = new GameObject("Gear");
        g.transform.SetParent(parent, false);
        var body = MakeSprite(g.transform, CombatFx.Circle(), 18);
        body.transform.localScale = new Vector3(0.42f, 0.42f, 1f);
        for (int i = 0; i < 2; i++)
        {
            var tooth = MakeSprite(g.transform, CombatFx.Square(), 18);
            tooth.transform.localScale = new Vector3(0.56f, 0.12f, 1f);
            tooth.transform.localRotation = Quaternion.Euler(0f, 0f, i * 90f);
        }
        for (int i = 0; i < 2; i++)
        {
            var tooth = MakeSprite(g.transform, CombatFx.Square(), 18);
            tooth.transform.localScale = new Vector3(0.56f, 0.12f, 1f);
            tooth.transform.localRotation = Quaternion.Euler(0f, 0f, 45f + i * 90f);
        }
        var hub = MakeSprite(g.transform, CombatFx.Circle(), 19);
        hub.transform.localScale = new Vector3(0.16f, 0.16f, 1f);
        hub.color = new Color(0.15f, 0.13f, 0.12f, 0.95f);
        return g;
    }

    void HideZone()
    {
        if (zoneRoot != null && zoneRoot.activeSelf) zoneRoot.SetActive(false);
    }

    void DestroyZoneFx()
    {
        if (zoneRoot != null) Destroy(zoneRoot);
        zoneRoot = null;
        gears.Clear();
    }

    // ---------- Панель ----------

    string StatusLine
    {
        get
        {
            if (!DevelopmentPath.IsIndustrial) return UiKit.Col(UiKit.BadHex, "Нужна школа «Промышленник» (путь Инженерии)");
            if (Building.IsRuined) return UiKit.Col(UiKit.BadHex, "Завод разрушен");
            if (Broken) return UiKit.Col(UiKit.BadHex, $"Ремонт после взрыва - {BrokenLeft:F0} с");
            if (Mode == Furnace.Quiet) return UiKit.Col(UiKit.MutedHex, "Печи погашены - бонуса нет");
            if (producers.Count == 0) return UiKit.Col(UiKit.WarnHex, "На площадке нет производств - печи не топят");
            if (!Building.isWorking) return UiKit.Col(UiKit.BadHex, "Стоит: " + Building.StatusText);
            if (!Fueled) return UiKit.Col(UiKit.BadHex, "Нет дерева в казне - печи гаснут");
            return $"Работает: площадка <b>{UiKit.Col(UiKit.GoodHex, $"+{CurrentBonus * 100f:F0}%")}</b>";
        }
    }

    string HeatState()
    {
        if (Broken) return UiKit.Col(UiKit.BadHex, "ремонт");
        if (Heat >= SafetyHeat) return UiKit.Col(UiKit.BadHex, "<b>на грани!</b>");
        if (Heat >= WarnHeat) return UiKit.Col(UiKit.BadHex, "опасно");
        if (Heat >= 45f) return UiKit.Col(UiKit.WarnHex, "горячо");
        return UiKit.Col(UiKit.GoodHex, "норма");
    }

    int BrokenInZone(out float maxLeft)
    {
        int n = 0;
        maxLeft = 0f;
        foreach (var b in producers)
        {
            float left = BrokenSecondsLeft(b);
            if (left <= 0f) continue;
            n++;
            if (left > maxLeft) maxLeft = left;
        }
        return n;
    }

    string ZoneListTip()
    {
        if (producers.Count == 0)
            return $"Ставьте производства (лесопилки, шахты, фермы, мастерские...) в квадрат {ZoneCells} клеток вокруг завода. Зона видна, пока завод выбран или вы строите.";
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var b in producers)
        {
            if (b == null || b.type == null) continue;
            string n = b.type.name + (b.level > 1 ? $" Ур.{b.level}" : "");
            if (BrokenSecondsLeft(b) > 0f) n += " (сломано)";
            int c;
            if (!counts.TryGetValue(n, out c)) order.Add(n);
            counts[n] = c + 1;
        }
        var sb = new System.Text.StringBuilder("На площадке:\n");
        foreach (var n in order) sb.Append("· ").Append(n).Append(counts[n] > 1 ? $" ×{counts[n]}" : "").Append('\n');
        sb.Append($"Остальные постройки в зоне ({inZone.Count - producers.Count}) бонуса не получают, но при взрыве тоже страдают.");
        return sb.ToString();
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        if (b == null) return new Rect(x, y, 0f, 0f);
        GUIStyle small = UiKit.Small;
        GUIStyle muted = UiKit.MutedStyle;

        float brokenMax;
        int brokenN = BrokenInZone(out brokenMax);

        const string foldId = "plant-info";
        string infoText =
            $"Площадка - квадрат {ZoneCells} клеток вокруг завода (Ур.1-3: 6/7/8). Производства в ней работают быстрее, пока завод топят деревом из казны. " +
            "Бонус зависит от режима и рабочих завода. Жар растёт на «Форсаже» и «Пределе», на «Ровно» и «Тихо» спадает. " +
            $"На 100% - взрыв: завод и все постройки площадки теряют {BlastDamage * 100f:F0}% прочности, производства стоят {ZoneBreakSeconds:F0} с, " +
            $"завод - {PlantBreakSeconds:F0} с. Предохранитель сбрасывает печи на «Ровно» при {SafetyHeat:F0}%. " +
            $"Улучшение: площадка шире, печи греются медленнее. Сожжено дерева: {WoodBurned}, взрывов: {Explosions}.";
        bool infoOpen = UiKit.IsOpen(foldId);
        float infoH = infoOpen ? UiKit.TextH(infoText, w - 20f, muted) + 4f : 0f;

        float statusY = UiKit.TitleH + 6f;
        float fuelY = statusY + 22f;
        float heatSecY = fuelY + 22f;
        float heatY = heatSecY + UiKit.SectionH;
        float heatBarY = heatY + 20f;
        float modesY = heatBarY + 18f;
        float modeHintY = modesY + 32f;
        float togglesY = modeHintY + 20f;
        float zoneSecY = togglesY + 28f;
        float zoneY = zoneSecY + UiKit.SectionH;
        float brokenY = zoneY + 20f;
        float workersY = brokenY + (brokenN > 0 ? 22f : 4f);
        float foldY = workersY + 32f;
        float upgradeY = foldY + UiKit.FoldH + infoH + 6f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        // --- Состояние ---
        Rect statusR = new Rect(x + 10f, y + statusY, w - 20f, 20f);
        GUI.Label(statusR, StatusLine, UiKit.Body);
        if (BrokenLeft > 0f)
            UiKit.Bar(new Rect(x + w - 110f, y + statusY + 5f, 100f, 10f), 1f - BrokenLeft / PlantBreakSeconds, UiKit.Warn);

        var rm = ResourceManager.Instance;
        int wood = rm != null ? rm.wood : 0;
        float every = FuelEvery[(int)Mode];
        string use = every > 0f ? $"{60f / every:F0} дерева/мин" : "не топят";
        Rect fr = new Rect(x + 10f, y + fuelY, w - 20f, 18f);
        string woodCol = wood > 0 ? UiKit.WoodHex : UiKit.BadHex;
        GUI.Label(fr, $"Топливо: дерево <b>{UiKit.Col(woodCol, wood.ToString())}</b>  " + UiKit.Col(UiKit.MutedHex, $"· расход {use} · сожжено {WoodBurned}"), small);
        UiKit.Tip(fr, "Завод берёт дерево прямо из казны. Кончится дерево - печи гаснут и площадка теряет бонус. " +
                      "Пока на площадке нет производств или нет рабочих, дерево не тратится.");

        // --- Печи ---
        UiKit.Section(x + 10f, y + heatSecY, w - 20f, "Печи");
        GUI.Label(new Rect(x + 10f, y + heatY, w - 20f, 18f), $"Жар: <b>{Heat:F0}%</b> - {HeatState()}", small);
        Rect bar = new Rect(x + 10f, y + heatBarY, w - 20f, 12f);
        float k = Heat / MaxHeat;
        UiKit.Bar(bar, k, k < 0.45f ? UiKit.Good : (k < WarnHeat / MaxHeat ? UiKit.Warn : UiKit.Bad));
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
            $"На 100% - взрыв: вся площадка ломается на {ZoneBreakSeconds:F0} с.");

        bool en = GUI.enabled;
        GUI.enabled = en && !Broken;
        Rect tabsR = new Rect(x + 10f, y + modesY, w - 18f, 28f);
        int newMode = UiKit.Tabs(tabsR, FurnaceNames, (int)Mode);
        GUI.enabled = en;
        if (newMode != (int)Mode && !Broken) SetMode((Furnace)newMode);
        float tw = tabsR.width / FurnaceNames.Length;
        for (int i = 0; i < FurnaceNames.Length; i++)
            UiKit.Tip(new Rect(tabsR.x + i * tw, tabsR.y, tw - 2f, tabsR.height),
                      Broken ? "Ремонт после взрыва: режим не сменить" : FurnaceTips[i]);

        float rate = HeatRate(Mode);
        string rateText = rate > 0f ? UiKit.Col(UiKit.WarnHex, $"жар +{rate:F1}/с") : UiKit.Col(UiKit.InfoHex, $"остывает {-rate:F1}/с");
        string eta = "";
        if (rate > 0f && Heat < MaxHeat) eta = "  " + UiKit.Col(UiKit.MutedHex, $"· до взрыва ~{(MaxHeat - Heat) / rate:F0} с");
        GUI.Label(new Rect(x + 10f, y + modeHintY, w - 20f, 18f),
            $"Площадка +{FurnaceBonus[(int)Mode] * 100f:F0}%, {rateText}{eta}", small);

        Rect sfr = new Rect(x + 10f, y + togglesY, w - 20f, 22f);
        Safety = GUI.Toggle(sfr, Safety, " Предохранитель");
        UiKit.Tip(sfr, $"При {SafetyHeat:F0}% жара печи сами переходят на «Ровно» и остывают. Выключите - и жгите до упора на свой риск.");

        // --- Площадка ---
        UiKit.Section(x + 10f, y + zoneSecY, w - 20f, "Площадка");
        Rect zr = new Rect(x + 10f, y + zoneY, w - 20f, 18f);
        string bonusTxt = CurrentBonus > 0f ? UiKit.Col(UiKit.GoodHex, $"+{CurrentBonus * 100f:F0}%") : UiKit.Col(UiKit.MutedHex, "+0%");
        GUI.Label(zr, $"На площадке: <b>{producers.Count}</b> построек ({bonusTxt})  " + UiKit.Col(UiKit.MutedHex, $"· радиус {ZoneCells} кл."), small);
        UiKit.Tip(zr, ZoneListTip());
        if (brokenN > 0)
        {
            Rect br = new Rect(x + 10f, y + brokenY, w - 20f, 18f);
            GUI.Label(br, UiKit.Col(UiKit.BadHex, $"Сломано взрывом: {brokenN} - починят через {brokenMax:F0} с"), small);
            UiKit.Tip(br, "Сломанные производства не работают, пока их не починят.");
        }

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        if (UiKit.Fold(new Rect(x + 10f, y + foldY, w - 20f, UiKit.FoldH), foldId, "Как работает завод") && infoOpen)
            GUI.Label(new Rect(x + 10f, y + foldY + UiKit.FoldH, w - 20f, infoH), infoText, muted);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
