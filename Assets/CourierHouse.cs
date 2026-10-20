using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Курьерская. Рабочие этого здания - курьеры (обычные жители, отдельных агентов нет).
///  - Игрок закрепляет курьеров за конкретными зданиями (лесопилка, шахта, ферма, станция собирателей...).
///  - Курьер сам ходит к закреплённому зданию, когда там накопилось достаточно, забирает
///    добычу и несёт в Ратушу - без кнопки "Собрать".
///  - Это менее выгодно, чем собирать вручную: до Ратуши доезжает только часть груза
///    (Ур.1 - 70%, Ур.3 - 75%), остальное теряется в пути.
///  - В режим войны курьеры из здания не выходят. Ночью и в выходные работают только дежурные.
/// Ур.2 / Ур.3: больше рабочих (2 / 4 / 6), больше груз за рейс и чуть меньше потерь.
/// Курьеров можно закрепить и за аванпостом: тогда они возят туда еду из Ратуши
/// (сколько берут за рейс - как обычно, с упряжкой больше и быстрее; потерь в пути нет).
/// Само здание стоит доски, плиты и сталь (см. UpgradeRules.BaseBuildRules).
/// Сам рейс выполняет CitizenAgent (StartCourier с владельцем-Курьерской).
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class CourierHouse : MonoBehaviour
{
    public const string TypeName = "Курьерская";

    public static readonly List<CourierHouse> All = new List<CourierHouse>();

    // Параметры по уровням (индекс = уровень - 1)
    static readonly int[] CarryByLevel = { 60, 80, 100 };              // сколько каждого ресурса курьер берёт за рейс
    static readonly float[] ShareByLevel = { 0.70f, 0.72f, 0.75f };    // какая доля доезжает до Ратуши (остальное теряется)

    public const int MinPickupFloor = 10;      // меньше этого со склада не забирают - не стоит рейса
    public const float PickupFill = 0.3f;      // ... или когда склад заполнен хотя бы на столько

    // Упряжка (делается в Конюшне Ур.3): курьер с упряжкой ходит быстрее и берёт больше
    public const float TeamSpeed = 1.6f;
    public const float TeamCarryMul = 1.5f;
    public int Teams { get; private set; }

    public BuildingInstance Building { get; private set; }
    int Lvl => Building != null ? Mathf.Clamp(Building.level, 1, 3) - 1 : 0;

    public int Carry => CarryByLevel[Lvl];
    public float Share => ShareByLevel[Lvl];
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined;

    // Статистика для панели
    public int TripsDone { get; private set; }
    public int CarriedTotal { get; private set; }      // сколько курьеры забрали со складов
    public int DeliveredTotal { get; private set; }    // сколько довезли до Ратуши

    // Закрепление: здание -> сколько курьеров на него назначено
    readonly Dictionary<BuildingInstance, int> assigned = new Dictionary<BuildingInstance, int>();

    // Кто сейчас в рейсе от этой Курьерской
    class Run { public CitizenAgent agent; public BuildingInstance target; public bool usesTeam; public float origSpeed; public GameObject wagon; public bool supply; }
    readonly List<Run> runs = new List<Run>();

    float tick;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        foreach (var r in runs) EndRun(r);
        runs.Clear();
        while (Teams > 0)
        {
            Teams--;
            if (!StableController.ReturnTeam()) WarManager.Notify("В Конюшнях нет места: упряжка снесённой Курьерской потеряна", false);
        }
    }

    // ---------- Упряжки ----------

    public bool AddTeam()
    {
        if (Building == null || Teams >= Building.workers) return false;
        if (!StableController.TryTakeTeam()) return false;
        Teams++;
        return true;
    }

    public bool RemoveTeam()
    {
        if (Teams <= 0) return false;
        Teams--;
        if (!StableController.ReturnTeam()) WarManager.Notify("В Конюшнях нет места: упряжка потеряна", false);
        return true;
    }

    static GameObject MakeWagon(CitizenAgent a)
    {
        GameObject go = new GameObject("Wagon");
        go.transform.SetParent(a.transform, false);
        go.transform.localPosition = new Vector3(-0.3f, -0.12f, 0f);
        go.transform.localScale = new Vector3(0.42f, 0.26f, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Square();
        sr.color = new Color(0.55f, 0.38f, 0.2f);
        sr.sortingOrder = 9;
        return go;
    }

    // Рейс закончен: скорость курьера возвращается, телега исчезает
    void EndRun(Run r)
    {
        if (r.wagon != null) Destroy(r.wagon);
        r.wagon = null;
        if (r.usesTeam && r.agent != null && !r.agent.IsMilitary) r.agent.moveSpeed = r.origSpeed;
        r.usesTeam = false;
    }

    // ---------- Закрепление ----------

    // Здание, у которого копится добыча (есть склад и что-то производится / приносится)
    public static bool IsServable(BuildingInstance b)
    {
        if (b == null || b.type == null || b.IsTownHall || b.IsRoad || b.IsBarrier) return false;
        if (b.type.maxStorage <= 0) return false;
        BuildingType t = b.type;
        return t.prodGold > 0 || t.prodWood > 0 || t.prodStone > 0 || t.prodFood > 0
            || b.ProducesIron || b.ProducesOats || b.ProducesBerries || b.ProducesResin;
    }

    // Аванпост, которому курьеры возят еду из Ратуши
    public static bool IsOutpostTarget(BuildingInstance b)
    {
        return b != null && b.type != null && b.IsOutpost && b.GetComponent<OutpostController>() != null;
    }

    /// <summary>За этим зданием закреплён хоть один курьер (в целой Курьерской).</summary>
    public static bool AnyAssigned(BuildingInstance b)
    {
        if (b == null) return false;
        foreach (var h in All)
            if (h != null && h.Works && h.Assigned(b) > 0) return true;
        return false;
    }

    public int Assigned(BuildingInstance b)
    {
        return b != null && assigned.TryGetValue(b, out int n) ? n : 0;
    }

    public void SetAssigned(BuildingInstance b, int n)
    {
        if (b == null) return;
        if (n <= 0) assigned.Remove(b);
        else assigned[b] = n;
    }

    public int AssignedTotal
    {
        get
        {
            int s = 0;
            foreach (var kv in assigned)
                if (kv.Key != null) s += kv.Value;
            return s;
        }
    }

    public void ClearAssignments() { assigned.Clear(); }

    int ActiveRuns(BuildingInstance target)
    {
        int n = 0;
        foreach (var r in runs)
            if (r.target == target) n++;
        return n;
    }

    public int ActiveRunsTotal { get { return runs.Count; } }

    // ---------- Рейсы ----------

    void Update()
    {
        if (Building == null) Building = GetComponent<BuildingInstance>();

        // Телега видна, только пока курьер на улице
        foreach (var r in runs)
            if (r.wagon != null) r.wagon.SetActive(r.agent != null && !r.agent.IsInsideBuilding);

        tick -= Time.deltaTime;
        if (tick > 0f) return;
        tick = 1f;
        Dispatch();
    }

    // Сколько на складе здания должно лежать, чтобы за ним стоило идти
    static int Threshold(BuildingInstance b)
    {
        return Mathf.Max(MinPickupFloor, Mathf.RoundToInt(b.Capacity * PickupFill));
    }

    void Dispatch()
    {
        // Закончившиеся рейсы убираем
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            Run r = runs[i];
            bool done = r.supply
                ? (r.agent == null || r.agent.IsDead || !r.agent.IsHauling)
                : (r.agent == null || r.agent.IsDead || !r.agent.IsCourier || r.agent.CourierOwner != this);
            if (done)
            {
                EndRun(r);
                runs.RemoveAt(i);
            }
        }

        // Снесённые здания из закрепления выбрасываем
        List<BuildingInstance> gone = null;
        foreach (var kv in assigned)
        {
            if (kv.Key == null)
            {
                if (gone == null) gone = new List<BuildingInstance>();
                gone.Add(kv.Key);
            }
        }
        if (gone != null) foreach (var g in gone) assigned.Remove(g);

        if (!Works || WarManager.IsWar) return;

        // Аванпосты: везём еду из Ратуши, когда нужно (за раз - один рейс на аванпост)
        DispatchSupply();

        // Кому пора: закреплённые здания, где накопилось достаточно и за ними ещё никто не идёт
        List<BuildingInstance> due = null;
        foreach (var kv in assigned)
        {
            BuildingInstance b = kv.Key;
            if (b == null || kv.Value <= 0 || b.IsRuined || !b.isActiveAndEnabled) continue;
            if (IsOutpostTarget(b)) continue;
            if (b.courierOnTheWay) continue;
            if (b.StoredTotal < Threshold(b)) continue;
            if (ActiveRuns(b) >= kv.Value) continue;
            if (due == null) due = new List<BuildingInstance>();
            due.Add(b);
        }
        if (due == null) return;

        // Самые полные склады - первыми
        due.Sort((a, c) => c.StoredTotal.CompareTo(a.StoredTotal));

        // Сколько упряжек ещё не занято в рейсах
        int teamsFree = Teams;
        foreach (var r in runs) if (r.usesTeam) teamsFree--;

        foreach (var b in due)
        {
            CitizenAgent a = FreeCourier(b);
            if (a == null) break; // свободных курьеров больше нет

            bool team = teamsFree > 0;
            int carry = team ? Mathf.RoundToInt(Carry * TeamCarryMul) : Carry;
            float orig = a.moveSpeed;
            if (!a.StartCourier(b, this, carry, Share)) continue;

            Run run = new Run { agent = a, target = b, origSpeed = orig, usesTeam = team };
            if (team)
            {
                teamsFree--;
                a.moveSpeed = orig * TeamSpeed;
                run.wagon = MakeWagon(a);
            }
            runs.Add(run);
        }
    }

    void DispatchSupply()
    {
        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;
        if (hall == null) return;

        int teamsFree = Teams;
        foreach (var r in runs) if (r.usesTeam) teamsFree--;

        foreach (var kv in assigned)
        {
            BuildingInstance b = kv.Key;
            if (b == null || kv.Value <= 0 || b.IsRuined || !b.isActiveAndEnabled || !IsOutpostTarget(b)) continue;
            OutpostController oc = b.GetComponent<OutpostController>();
            if (oc == null || !oc.NeedsHallFood || ActiveRuns(b) > 0) continue;

            CitizenAgent a = FreeCourier(hall);
            if (a == null) return;

            bool team = teamsFree > 0;
            int carry = team ? Mathf.RoundToInt(Carry * TeamCarryMul) : Carry;
            float orig = a.moveSpeed;
            HaulJob job = oc.MakeHallJob(carry, null);
            if (!a.StartHaul(job))
            {
                if (job.onAbort != null) job.onAbort(false);   // снять бронь места на аванпосте
                continue;
            }

            Run run = new Run { agent = a, target = b, origSpeed = orig, usesTeam = team, supply = true };
            if (team)
            {
                teamsFree--;
                a.moveSpeed = orig * TeamSpeed;
                run.wagon = MakeWagon(a);
            }
            runs.Add(run);
        }
    }

    // Свободный курьер (рабочий этой Курьерской), ближайший к зданию
    CitizenAgent FreeCourier(BuildingInstance target)
    {
        GameClock clock = GameClock.Instance;
        bool workTime = clock == null || clock.IsWorkTime;
        Vector2 to = target.Center;

        CitizenAgent best = null;
        float bestD = float.MaxValue;
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.IsMilitary) continue;
            if (!ReferenceEquals(a.Workplace, Building)) continue;
            if (a.IsCourier || a.IsHauling || a.IsHealerBusy || a.IsGathering || a.IsLyingInBed) continue;
            if (!workTime && !a.IsSkeleton) continue;     // вне рабочего времени выходят только дежурные
            if (a.HpFraction < 0.4f) continue;            // измотанный сначала поест
            float d = Vector2.Distance(a.transform.position, to);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    // Курьер сдал груз в Ратуше (вызывает CitizenAgent)
    public void OnTripDone(int carried, int delivered)
    {
        TripsDone++;
        CarriedTotal += carried;
        DeliveredTotal += delivered;
    }

    // ---------- Панель здания ----------

    void DrawTeamsRow(float x, float y, float w, GUIStyle small)
    {
        string col = Teams > 0 ? UiKit.GoodHex : UiKit.MutedHex;
        Rect lr = new Rect(x, y + 2f, w - 64f, 22f);
        GUI.Label(lr, $"Упряжки: <color={col}><b>{Teams}</b></color>  " + UiKit.Col(UiKit.MutedHex, $"в конюшнях: {StableController.TeamsAvailable}"), small);
        UiKit.Tip(lr, $"Курьер с упряжкой едет быстрее (×{TeamSpeed:F1}). Упряжки берутся из Конюшни, не больше одной на курьера.");

        bool canAdd = Building != null && Teams < Building.workers && StableController.TeamsAvailable > 0;
        string addWhy = StableController.TeamsAvailable <= 0 ? "В конюшнях нет свободных упряжек" : (canAdd ? "Взять упряжку" : "Упряжек уже по числу курьеров");
        if (UiKit.Button(new Rect(x + w - 56f, y, 26f, 22f), "-", Teams > 0, UiKit.Kind.Normal, "Вернуть упряжку в конюшню")) RemoveTeam();
        if (UiKit.Button(new Rect(x + w - 28f, y, 26f, 22f), "+", canAdd, UiKit.Kind.Normal, addWhy)) AddTeam();
    }

    readonly List<BuildingInstance> rowsBuf = new List<BuildingInstance>();
    Vector2 scroll;

    void CollectServable()
    {
        rowsBuf.Clear();
        foreach (var b in BuildingInstance.All)
            if (IsServable(b) || IsOutpostTarget(b)) rowsBuf.Add(b);

        rowsBuf.Sort((a, c) =>
        {
            bool ao = IsOutpostTarget(a), co = IsOutpostTarget(c);
            if (ao != co) return ao ? -1 : 1;   // аванпосты - сверху
            int byName = string.CompareOrdinal(a.type.name, c.type.name);
            if (byName != 0) return byName;
            int byX = a.OriginCell.x.CompareTo(c.OriginCell.x);
            return byX != 0 ? byX : a.OriginCell.y.CompareTo(c.OriginCell.y);
        });
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        int lvl = b.level;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        CollectServable();
        int rows = rowsBuf.Count;
        const float rowH = 26f;
        float listH = Mathf.Clamp(rows * rowH, rowH * 2f, rowH * 6f);

        int total = AssignedTotal;
        bool over = total > b.workers;
        bool problem = !b.isWorking || over;

        float statusY = UiKit.TitleH + 6f;
        float statsY = statusY + 22f;
        float problemY = statsY + 20f;
        float workersY = problemY + (problem ? 22f : 2f);
        float teamsY = workersY + 32f;
        float titleY = teamsY + 28f;
        float listY = titleY + UiKit.SectionH + 2f;
        float upgradeY = listY + listH + 8f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {lvl}");

        // --- Статус ---
        Rect sr = new Rect(x + 10f, y + statusY, w - 20f, 20f);
        GUI.Label(sr, $"Курьеров: <b>{b.workers}</b>  · в рейсе: <b>{runs.Count}</b>  · закреплено: <b>{total}</b>", st);
        UiKit.Tip(sr, "Закрепите курьеров за зданиями - они носят накопленное в Ратушу. За аванпостами - возят туда еду из Ратуши. " +
                      $"За рейс курьер берёт до {Carry} каждого ресурса, до Ратуши доезжает {Share * 100f:F0}%. В войну курьеры не выходят.");
        GUI.Label(new Rect(x + 10f, y + statsY, w - 20f, 18f),
            $"Рейсов: {TripsDone} · довезено {DeliveredTotal} из {CarriedTotal} · до {Carry} за рейс", UiKit.MutedStyle);
        if (problem)
        {
            string msg = !b.isWorking ? UiKit.Col(UiKit.BadHex, b.StatusText)
                                      : UiKit.Col(UiKit.WarnHex, "Закреплено больше, чем курьеров");
            GUI.Label(new Rect(x + 10f, y + problemY, w - 20f, 20f), msg, small);
        }

        if (PopulationManager.Instance != null)
            PopulationManager.Instance.DrawWorkersRow(x + 10f, y + workersY, b);

        DrawTeamsRow(x + 10f, y + teamsY, w - 20f, small);

        // --- Заголовок списка ---
        UiKit.Section(x + 10f, y + titleY, w - 110f, $"Закрепление {total} из {b.workers}");
        if (UiKit.Button(new Rect(x + w - 100f, y + titleY, 90f, 22f), "Снять всех", total > 0, UiKit.Kind.Ghost, "Открепить всех курьеров"))
            ClearAssignments();

        // --- Список зданий ---
        float innerW = w - 44f;
        scroll = GUI.BeginScrollView(new Rect(x + 10f, y + listY, w - 20f, listH), scroll,
            new Rect(0f, 0f, innerW, Mathf.Max(listH, rows * rowH)));

        if (rows == 0)
            GUI.Label(new Rect(0f, 0f, innerW, 40f),
                "Нет зданий с добычей (лесопилка, шахта, ферма, станция собирателей) и аванпостов", UiKit.MutedStyle);

        GUIStyle cnt = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
        for (int i = 0; i < rows; i++)
        {
            BuildingInstance bld = rowsBuf[i];
            if (bld == null) continue;
            float ry = i * rowH;
            int n = Assigned(bld);

            string stock;
            if (bld.IsRuined) stock = UiKit.Col(UiKit.BadHex, "руины");
            else if (IsOutpostTarget(bld))
            {
                OutpostController oc = bld.GetComponent<OutpostController>();
                string col = !oc.Supplied ? UiKit.BadHex : (oc.NeedsHallFood ? UiKit.WarnHex : UiKit.GoodHex);
                stock = UiKit.Col(col, $"еда {oc.Food}/{oc.Capacity}");
            }
            else stock = UiKit.Col(bld.StoredTotal >= Threshold(bld) ? UiKit.WarnHex : UiKit.MutedHex, $"склад {bld.StoredTotal}");
            string title = n > 0 ? $"<b>{bld.type.name}</b>" : bld.type.name;
            Rect nr = new Rect(0f, ry + 2f, innerW - 134f, rowH - 2f);
            GUI.Label(nr, $"{title}  {stock}", small);
            UiKit.Tip(nr, $"{bld.type.name} [{bld.OriginCell.x},{bld.OriginCell.y}]");

            if (UiKit.Button(new Rect(innerW - 132f, ry, 50f, rowH - 2f), "Найти", UiKit.Kind.Ghost, "Показать на карте") && CameraController.Instance != null)
                CameraController.Instance.FocusOn(bld.Center);

            if (UiKit.Button(new Rect(innerW - 78f, ry, 24f, rowH - 2f), "-", n > 0, UiKit.Kind.Normal, null)) SetAssigned(bld, n - 1);
            bool canAdd = total < b.workers && !bld.IsRuined;
            if (UiKit.Button(new Rect(innerW - 26f, ry, 24f, rowH - 2f), "+", canAdd, UiKit.Kind.Normal,
                    canAdd ? null : (bld.IsRuined ? "Здание в руинах" : "Все курьеры уже закреплены")))
                SetAssigned(bld, n + 1);

            GUI.Label(new Rect(innerW - 52f, ry, 26f, rowH - 2f), n > 0 ? $"<b>{n}</b>" : "0", cnt);
        }
        GUI.EndScrollView();

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
