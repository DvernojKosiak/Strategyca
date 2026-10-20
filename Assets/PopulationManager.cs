using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Население: жители, жильё, рабочие места и физические агенты на карте.
/// Менеджер решает, СКОЛЬКО людей где работает и где кто живёт.
/// Куда житель идёт в данный момент (работа / дом / гулять) решает сам CitizenAgent.
/// Гостевой дом: жильё только для наёмников (купленных на рынке). Свои жители туда не селятся,
/// поэтому наёмникам всегда есть место, даже когда обычные дома забиты. Наёмник в Гостевом доме
/// не занимает место в обычных домах (прирост своих жителей считается без него).
/// </summary>
public class PopulationManager : MonoBehaviour
{
    public static PopulationManager Instance { get; private set; }

    public const string HouseName = "Жилой дом";

    [Header("Население")]
    public int population = 16;
    public int housePerLevel = 16;
    public int guestPerLevel = 6;   // мест в Гостевом доме на уровень (6 / 12 / 18)
    public float growthInterval = 4f;
    public int growthPerStep = 1;
    public float leaveGrace = 10f;
    public int survivorCore = 6;   // если жителей меньше - новые приходят даже без еды (город восстанавливается после разгрома)

    private readonly Dictionary<string, int> workersByBuilding = new Dictionary<string, int>
    {
        { "Лесопилка", 4 },
        { "Шахта", 6 },
        { "Ферма", 4 },
        { "Мастерская", 3 },
        { "Ратуша", 2 },         // работники Ратуши - курьеры: носят материалы
        { HealerHouse.TypeName, 2 }, // целители (2 / 4 / 6 по уровням)
        { CourierHouse.TypeName, 2 }, // курьеры Курьерской (2 / 4 / 6 по уровням)
        { ArmoryController.TypeName, 3 }, // оружейники (3 / 6 / 9 по уровням)
        { StableController.TypeName, 2 }, // конюхи (2 / 4 / 6 по уровням)
        { BerryStation.TypeName, 3 },     // собиратели ягод (3 / 6 / 9 по уровням)
        { KitchenController.TypeName, 2 }, // повара (2 / 4 / 6 по уровням)
        { WagonYard.TypeName, 2 },         // возчики обоза (2 / 4 / 6 по уровням)
        { PotionHouse.TypeName, 2 },       // зельевары (путь Магии)
        { MagicArmory.TypeName, 2 },       // чародеи Магической оружейной (путь Магии, 2 / 4 / 6)
        { WeaponForge.TypeName, 1 },       // мастер (путь Военного дела)
        { FoundryController.TypeName, 2 }, // литейщики (путь Инженерии, 2 / 4 / 6 по уровням)
        { EngineerYard.TypeName, 2 },      // инженеры (путь Инженерии, 2 / 4 / 6 по уровням)
        { IndustrialPlant.TypeName, 3 },   // рабочие Завода (Индустриализатор, 3 / 6 / 9 по уровням)
        { WarBuilderYard.TypeName, 2 },    // военные строители (Военный строитель, 2 / 4 / 6 по уровням)
        { MarketHouse.TypeName, 2 }        // приказчики Торгового дома: носят товары рынка (2 / 4 / 6)
    };

    private float tickTimer = 0f;
    private float growthTimer = 0f;
    private float overCapTimer = 0f;
    private bool initialized = false;

    private BuildingInstance hall;
    private int housingCapacity = 0;
    private int guestCapacity = 0;
    private int employed = 0;
    private readonly List<BuildingInstance> workplaces = new List<BuildingInstance>();
    private readonly List<BuildingInstance> houses = new List<BuildingInstance>();
    private readonly List<BuildingInstance> guestHouses = new List<BuildingInstance>();

    private readonly List<CitizenAgent> agents = new List<CitizenAgent>();

    public int HousePerLevel => housePerLevel;
    public int Population => population;
    public int HousingCapacity => housingCapacity;
    public int GuestCapacity => guestCapacity;

    /// <summary>Жильё ли это (обычный дом или Гостевой дом).</summary>
    public static bool IsHousing(BuildingInstance b)
    {
        return b != null && b.type != null && (b.type.name == HouseName || b.IsGuestHouse);
    }

    int CapOf(BuildingInstance h) { return (h.IsGuestHouse ? guestPerLevel : housePerLevel) * h.level; }

    /// <summary>Сколько наёмников (купленных на рынке) живёт в городе.</summary>
    public int Mercenaries
    {
        get
        {
            int n = 0;
            foreach (var a in agents) if (a != null && !a.IsDead && a.IsMercenary) n++;
            return n;
        }
    }

    // Сколько людей занимает обычное жильё: все, кроме наёмников, живущих в Гостевых домах
    int HousedPopulation => Mathf.Max(0, population - Mathf.Min(Mercenaries, guestCapacity));

    /// <summary>Сколько ещё наёмников поселится: свободные места в Гостевых домах + свободное обычное жильё.</summary>
    public int MercenaryRoom => Mathf.Max(0, guestCapacity - Mercenaries) + Mathf.Max(0, housingCapacity - HousedPopulation);
    public int Employed => employed;
    public BuildingInstance Hall => hall;

    public int Soldiers
    {
        get
        {
            int c = 0;
            foreach (var b in BuildingInstance.All)
            {
                if (b == null) continue;
                BarracksController br = b.GetComponent<BarracksController>();
                if (br != null) c += br.HeadCount;
            }
            return c;
        }
    }

    public int FreePeople => Mathf.Max(0, population - Soldiers - employed);

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public int WorkersFor(BuildingInstance b)
    {
        if (b == null || b.type == null) return 0;
        // Аванпост: смотрители только с Ур.3 (носят еду с ферм рядом, см. OutpostController)
        if (b.type.name == OutpostController.TypeName) return b.level >= 3 ? 2 : 0;
        return workersByBuilding.TryGetValue(b.type.name, out int w) ? w * b.level : 0;
    }

    void Update()
    {
        tickTimer += Time.deltaTime;
        if (tickTimer < 1f) return;
        tickTimer -= 1f;
        Tick();
    }

    void Tick()
    {
        agents.RemoveAll(a => a == null);
        GatherBuildings();

        if (!initialized)
        {
            initialized = true;
            foreach (var b in workplaces) b.autoWorkers = true;
        }

        UpdateGrowth();
        SyncAgentsCount();
        AssignWorkers();
        AssignHomes();
    }

    void GatherBuildings()
    {
        hall = null;
        housingCapacity = 0;
        guestCapacity = 0;
        workplaces.Clear();
        houses.Clear();
        guestHouses.Clear();

        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null) continue;
            if (b.IsRuined && WorkersFor(b) > 0) b.SetWorkers(0, WorkersFor(b)); // в руинах никто не работает

            if (b.IsTownHall)
            {
                hall = b;
                if (WorkersFor(b) > 0 && !b.IsRuined) workplaces.Add(b);
            }
            else if (b.IsGuestHouse)
            {
                // Гостевой дом: только для наёмников
                if (!b.InGreenZone) continue;
                guestCapacity += guestPerLevel * b.level;
                if (!b.IsRuined) guestHouses.Add(b);
            }
            else if (b.type.name == HouseName)
            {
                // Разрушенный дом жильцов не держит, но место за ним остаётся (дом восстановят) -
                // поэтому люди из города не разбегаются, а пока ночуют в Ратуше
                // Дом вне зелёной зоны жильцов не держит (аванпост забросили)
                if (!b.InGreenZone) continue;
                housingCapacity += housePerLevel * b.level;
                if (!b.IsRuined) houses.Add(b);
            }
            else if (WorkersFor(b) > 0 && !b.IsRuined)
            {
                // Вне зелёной зоны работы нет - рабочих снимаем
                if (b.InGreenZone) workplaces.Add(b);
                else b.SetWorkers(0, WorkersFor(b));
            }
        }
    }

    void RefreshNow()
    {
        GatherBuildings();
        AssignWorkers();
    }

    void UpdateGrowth()
    {
        // Режим войны: новые жители не прибывают (и никто не уходит)
        if (WarManager.IsWar)
        {
            growthTimer = 0f;
            overCapTimer = 0f;
            return;
        }

        int housed = HousedPopulation;   // наёмники в Гостевых домах свои дома не занимают
        if (housed < housingCapacity)
        {
            overCapTimer = 0f;
            growthTimer += 1f;
            // Без еды новые не приходят. Но если город почти вымер, небольшое ядро жителей
            // возвращается и без еды - иначе на ферме некому работать и еды не будет никогда.
            bool hasFood = ResourceManager.Instance != null && ResourceManager.Instance.food > 0;
            if (population < survivorCore) hasFood = true;
            if (growthTimer >= growthInterval && hasFood)
            {
                growthTimer = 0f;
                population += Mathf.Min(growthPerStep, housingCapacity - housed);
            }
        }
        else if (housed > housingCapacity)
        {
            overCapTimer += 1f;
            if (overCapTimer >= leaveGrace) population--;
        }
        else
        {
            overCapTimer = 0f;
        }
    }

    void SyncAgentsCount()
    {
        Vector2 spawnPos = hall != null ? hall.Center : Vector2.zero;
        var nav = GridNavigation.Instance;
        if (hall != null && nav != null &&
            nav.TryGetDoorCell(hall, nav.WorldToCell(hall.Center), out Vector2Int door))
        {
            spawnPos = nav.CellCenter(door);
        }

        while (agents.Count < population)
        {
            GameObject go = new GameObject("Citizen_" + agents.Count);
            go.transform.position = spawnPos + Random.insideUnitCircle * 0.3f;
            agents.Add(go.AddComponent<CitizenAgent>());
        }

        while (agents.Count > population)
        {
            // Сначала уходят обычные жители, солдат и новобранцев трогаем в последнюю очередь
            int idx = agents.Count - 1;
            for (int i = agents.Count - 1; i >= 0; i--)
            {
                if (agents[i] != null && !agents[i].IsMilitary) { idx = i; break; }
            }
            CitizenAgent agent = agents[idx];
            agents.RemoveAt(idx);
            if (agent != null) Destroy(agent.gameObject);
        }
    }

    // ---------- Работа ----------

    void AssignWorkers()
    {
        Vector2 center = hall != null ? hall.Center : Vector2.zero;
        // Ратуша в очереди на людей последняя: сначала производство
        System.Func<BuildingInstance, float> key = w => w.IsTownHall ? 1000000f : Vector2.Distance(w.Center, center);
        workplaces.Sort((a, b) => key(a).CompareTo(key(b)));

        // 1. Считаем, сколько людей должно работать в каждом здании
        employed = 0;
        int military = 0;
        foreach (var a in agents) if (a != null && a.IsMilitary) military++;
        int pool = Mathf.Max(0, agents.Count - military); // солдаты и новобранцы не работают
        var target = new Dictionary<BuildingInstance, int>();

        foreach (var b in workplaces)
        {
            if (b.autoWorkers) continue;
            int need = WorkersFor(b);
            int give = Mathf.Min(Mathf.Min(b.desiredWorkers, need), Mathf.Max(0, pool - employed));
            employed += give;
            target[b] = give;
            b.SetWorkers(give, need);
        }

        foreach (var b in workplaces)
        {
            if (!b.autoWorkers) continue;
            int need = WorkersFor(b);
            int give = Mathf.Min(need, Mathf.Max(0, pool - employed));
            employed += give;
            target[b] = give;
            b.SetWorkers(give, need);
        }

        // 2. Смотрим, кто сейчас где числится
        var crews = new Dictionary<BuildingInstance, List<CitizenAgent>>();
        var free = new List<CitizenAgent>();

        foreach (var a in agents)
        {
            if (a == null) continue;
            if (a.IsMilitary) { a.AssignWork(null); continue; }

            BuildingInstance w = a.Workplace;
            if (w != null && target.ContainsKey(w))
            {
                if (!crews.TryGetValue(w, out var list)) { list = new List<CitizenAgent>(); crews[w] = list; }
                list.Add(a);
            }
            else
            {
                a.AssignWork(null); // здание снесено / больше не рабочее место
                free.Add(a);
            }
        }

        // Свободных перемешиваем: на работу пойдут случайные жители
        for (int i = free.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            var tmp = free[i]; free[i] = free[j]; free[j] = tmp;
        }

        // 3. Подгоняем состав каждой бригады под нужное число
        foreach (var b in workplaces)
        {
            int want = target[b];
            if (!crews.TryGetValue(b, out var list)) { list = new List<CitizenAgent>(); crews[b] = list; }

            // Лишних увольняем (случайных) - они выйдут из здания и пойдут по своим делам
            while (list.Count > want)
            {
                int i = Random.Range(0, list.Count);
                list[i].AssignWork(null);
                list.RemoveAt(i);
            }

            // Не хватает - зовём случайных свободных жителей
            while (list.Count < want && free.Count > 0)
            {
                CitizenAgent a = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                a.AssignWork(b);
                list.Add(a);
            }

            // Дежурные на ночь / выходные: 1 человек (2 на большом здании), остальные идут домой спать.
            // Кто уже дежурит - остаётся дежурным, чтобы люди не бегали туда-сюда.
            int slots = GameClock.Instance != null ? GameClock.Instance.NightCrewFor(b.level) : 0;
            bool[] keep = new bool[list.Count];
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsSkeleton && slots > 0) { keep[i] = true; slots--; }
            for (int i = 0; i < list.Count; i++)
                if (!keep[i] && slots > 0) { keep[i] = true; slots--; }
            for (int i = 0; i < list.Count; i++) list[i].IsSkeleton = keep[i];
        }
    }

    // ---------- Жильё ----------

    void AssignHomes()
    {
        var residents = new Dictionary<BuildingInstance, int>();
        foreach (var h in houses) residents[h] = 0;
        foreach (var h in guestHouses) residents[h] = 0;

        var homeless = new List<CitizenAgent>();
        foreach (var a in agents)
        {
            BuildingInstance h = a.Home;
            // Свои жители в Гостевом доме не живут
            bool allowed = h != null && (!h.IsGuestHouse || a.IsMercenary);
            if (allowed && residents.TryGetValue(h, out int c) && c < CapOf(h))
            {
                residents[h] = c + 1;
            }
            else
            {
                a.Home = null;
                homeless.Add(a);
            }
        }

        // Сначала селим наёмников - в Гостевые дома, потом остальных - в обычные
        homeless.Sort((x, y) => (y.IsMercenary ? 1 : 0).CompareTo(x.IsMercenary ? 1 : 0));
        foreach (var a in homeless)
        {
            BuildingInstance best = a.IsMercenary ? LeastFull(guestHouses, residents) : null;
            if (best == null) best = LeastFull(houses, residents);

            if (best != null)
            {
                a.Home = best;
                residents[best]++;
            }
            // нет места - Home остаётся null, ночевать будет в Ратуше
        }
    }

    BuildingInstance LeastFull(List<BuildingInstance> list, Dictionary<BuildingInstance, int> residents)
    {
        BuildingInstance best = null;
        float bestRatio = float.MaxValue;
        foreach (var h in list)
        {
            int cap = CapOf(h);
            int cnt = residents[h];
            if (cnt >= cap) continue;
            float ratio = (float)cnt / cap;
            if (ratio < bestRatio) { bestRatio = ratio; best = h; }
        }
        return best;
    }

    /// <summary>Житель (или солдат) погиб: убираем его из населения. Новый на его место не появится, пока идёт война.</summary>
    public void OnCitizenDied(CitizenAgent a)
    {
        if (a == null) return;
        if (agents.Remove(a)) population = Mathf.Max(0, population - 1);
        Destroy(a.gameObject);
    }

    /// <summary>Сколько жителей закреплено за домом (живёт).</summary>
    public int ResidentsOf(BuildingInstance house)
    {
        int n = 0;
        foreach (var a in agents)
            if (a != null && a.Home == house) n++;
        return n;
    }

    /// <summary>Сколько жителей сейчас находится внутри дома.</summary>
    public int AtHome(BuildingInstance house)
    {
        int n = 0;
        foreach (var a in agents)
            if (a != null && a.IsInside(house)) n++;
        return n;
    }

    /// <summary>
    /// Казарма просит жителя на обучение: берём ближайшего свободного (сначала безработных).
    /// Он сам дойдёт до казармы и зайдёт внутрь.
    /// </summary>
    // ---------- Наёмники (рынок) ----------

    /// <summary>Есть свободное жильё для нового жителя (наёмника).</summary>
    public bool HasRoomForNewcomer => MercenaryRoom > 0;

    /// <summary>Житель ушёл из города (боец продан в лагерь наёмников): население -1.</summary>
    public void RemoveAgent(CitizenAgent a)
    {
        if (a == null) return;
        agents.Remove(a);
        population = Mathf.Max(0, population - 1);
        Destroy(a.gameObject);
    }

    /// <summary>Новый житель пришёл в город (наёмник): население +1, появляется в точке pos.
    /// mercenary - наёмник с рынка: живёт в Гостевом доме.</summary>
    public CitizenAgent SpawnAgent(Vector2 pos, bool mercenary = false)
    {
        population++;
        GameObject go = new GameObject("Citizen_" + agents.Count);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        CitizenAgent a = go.AddComponent<CitizenAgent>();
        a.IsMercenary = mercenary;
        agents.Add(a);
        agents.RemoveAll(x => x == null);
        AssignHomes();   // сразу ищем ему жильё (Гостевой дом)
        return a;
    }

    public CitizenAgent TakeRecruit(BuildingInstance barracks)
    {
        Vector2 from = barracks != null ? barracks.Center : Vector2.zero;
        CitizenAgent best = null;
        float bestScore = float.MaxValue;

        foreach (var a in agents)
        {
            if (a == null || a.IsMilitary || a.IsDead) continue;
            float score = Vector2.Distance(a.transform.position, from);
            if (a.Workplace != null) score += 1000f; // занятых работой берём в последнюю очередь
            if (score < bestScore) { bestScore = score; best = a; }
        }

        if (best == null) return null;

        best.StartTraining(barracks);
        RefreshNow(); // пересчитать рабочих без новобранца
        return best;
    }

    // Житель может бросить свои дела и пойти с поручением (курьер, перенос груза)
    static bool CanRunErrand(CitizenAgent a)
    {
        return a != null && !a.IsDead && !a.IsMilitary && !a.IsCourier && !a.IsHauling
            && !a.IsHealerBusy && !a.IsGathering && !a.IsLyingInBed && !a.IsPanicking && !a.IsCartDriver
            && !a.IsWarBuilding;   // строитель на ремонте поручения не берёт
    }

    /// <summary>
    /// Отправить за ресурсами здания курьера - обычного жителя: сначала рабочего этого здания,
    /// иначе ближайшего безработного, иначе ближайшего чужого рабочего. Солдаты не привлекаются.
    /// </summary>
    public bool DispatchCourier(BuildingInstance src)
    {
        if (src == null || src.type == null || src.courierOnTheWay || src.StoredTotal <= 0) return false;

        Vector2 from = src.Center;
        CitizenAgent best = null;
        float bestScore = float.MaxValue;

        foreach (var a in agents)
        {
            if (!CanRunErrand(a)) continue;
            float score = Vector2.Distance(a.transform.position, from);
            if (a.Workplace == src) score -= 1000f;          // свои рабочие - в первую очередь
            else if (a.Workplace != null) score += 1000f;    // чужих рабочих отвлекаем в последнюю очередь
            if (score < bestScore) { bestScore = score; best = a; }
        }

        return best != null && best.StartCourier(src);
    }

    /// <summary>
    /// Отправить жителя с грузом: сначала рабочий здания job.prefer (мастерская / улучшаемое здание),
    /// потом работник Ратуши (курьер), потом безработный, в последнюю очередь - чужой рабочий.
    /// Солдаты не привлекаются. false - свободных жителей сейчас нет (заказ подождёт в очереди).
    /// </summary>
    public bool DispatchHaul(HaulJob job)
    {
        if (job == null || job.from == null) return false;

        Vector2 from = job.from.Center;
        CitizenAgent best = null;
        float bestScore = float.MaxValue;

        foreach (var a in agents)
        {
            if (!CanRunErrand(a)) continue;

            float score = Vector2.Distance(a.transform.position, from);
            BuildingInstance w = a.Workplace;
            if (w != null && job.prefer != null && w == job.prefer) score -= 1000f;   // свои рабочие
            else if (w != null && w.IsTownHall) score -= 500f;                        // курьеры Ратуши
            else if (w != null) score += 1000f;                                       // чужих - в последнюю очередь
            if (score < bestScore) { bestScore = score; best = a; }
        }

        return best != null && best.StartHaul(job);
    }

    public void ChangeWorkers(BuildingInstance b, int delta)
    {
        int need = WorkersFor(b);
        if (need <= 0) return;

        if (b.autoWorkers)
        {
            b.autoWorkers = false;
            b.desiredWorkers = b.workers;
        }

        if (delta > 0) delta = Mathf.Min(delta, FreePeople);
        b.desiredWorkers = Mathf.Clamp(b.desiredWorkers + delta, 0, need);
        RefreshNow();
    }

    public void SetAuto(BuildingInstance b)
    {
        b.autoWorkers = true;
        RefreshNow();
    }

    // Строка «рабочие» в панелях зданий: [-] Рабочие 2/3 [+] [Авто]. Ширина 266, высота 26.
    public void DrawWorkersRow(float x, float y, BuildingInstance b)
    {
        int need = WorkersFor(b);
        if (b == null || need <= 0) return;

        GUIStyle label = new GUIStyle(UiKit.Body) { alignment = TextAnchor.MiddleCenter, wordWrap = false };

        if (GUI.Button(new Rect(x, y, 28, 26), "-")) ChangeWorkers(b, -1);

        string color = b.workers < need ? UiKit.WarnHex : UiKit.GoodHex;
        Rect lr = new Rect(x + 32, y, 130, 26);
        GUI.Label(lr, UiKit.Col(UiKit.MutedHex, "Рабочие ") + UiKit.B(UiKit.Col(color, $"{b.workers}/{need}")), label);
        UiKit.Tip(lr, $"Работают {b.workers} из {need}. Свободных жителей в городе: {FreePeople}.\nНочью и в выходные остаётся только дежурная смена.");

        if (GUI.Button(new Rect(x + 166, y, 28, 26), "+")) ChangeWorkers(b, +1);

        Rect ar = new Rect(x + 200, y, 66, 26);
        if (GUI.Button(ar, b.autoWorkers ? "Авто ✓" : "Авто", b.autoWorkers ? UiKit.ButtonStyle(UiKit.Kind.Primary) : UiKit.ButtonStyle(UiKit.Kind.Normal))) SetAuto(b);
        UiKit.Tip(ar, "Авто: город сам ставит сюда людей, сколько нужно. Кнопки - / + переключают на ручной выбор.");
    }

    // Ширина второй строки верхней полосы (по ней ResourceManager растягивает подложку)
    internal static float HudRowWidth;

    void OnGUI()
    {
        UiKit.Apply();

        // Среднее самочувствие мирных жителей (голод + раны)
        float sum = 0f;
        int n = 0, weak = 0;
        foreach (var a in agents)
        {
            if (a == null || a.IsDead || a.IsMilitary) continue;
            sum += a.HpFraction;
            n++;
            if (a.HpFraction < 0.5f) weak++;
        }
        int avg = n > 0 ? Mathf.RoundToInt(sum / n * 100f) : 100;
        string col = avg >= 75 ? UiKit.GoodHex : (avg >= 50 ? UiKit.WarnHex : UiKit.BadHex);

        string sep = UiKit.Col(UiKit.MutedHex, "  ·  ");
        string text = UiKit.Col(UiKit.MutedHex, "Жители ") + UiKit.B($"{HousedPopulation}/{housingCapacity}")
            + sep + UiKit.Col(UiKit.MutedHex, "свободно ") + FreePeople
            + sep + UiKit.Col(UiKit.MutedHex, "на работе ") + employed
            + sep + UiKit.Col(UiKit.MutedHex, "солдат ") + Soldiers;
        if (guestCapacity > 0) text += sep + UiKit.Col(UiKit.MutedHex, "наёмники ") + $"{Mercenaries}/{guestCapacity}";
        text += sep + UiKit.Col(UiKit.MutedHex, "здоровье ") + UiKit.Col(col, avg + "%");
        if (weak > 0) text += " " + UiKit.Col(UiKit.WarnHex, $"(слабых {weak})");
        if (WarManager.IsWar) text += sep + UiKit.B(UiKit.Col(UiKit.BadHex, "Война: прирост остановлен"));

        GUIStyle style = new GUIStyle(UiKit.Small) { wordWrap = false, alignment = TextAnchor.MiddleLeft, fontSize = 12 };
        HudRowWidth = style.CalcSize(new GUIContent(text)).x;
        Rect r = new Rect(12f, 22f, HudRowWidth + 4f, 16f);
        GUI.Label(r, text, style);
        UiKit.Tip(r, $"Жители: в домах {HousedPopulation} из {housingCapacity} мест.\n" +
            "Свободные — без работы, их можно поставить в здания.\n" +
            "Здоровье — среднее самочувствие мирных (голод и раны); слабые — ниже 50%." +
            (guestCapacity > 0 ? "\nНаёмники живут в гостевых домах." : ""));
    }
}
