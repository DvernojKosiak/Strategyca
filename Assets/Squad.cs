using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отряд. Каждый солдат принадлежит какому-то отряду своей казармы.
/// Приказы отдаются отрядам. Когда отряд посылают в точку:
///  - из казны берутся припасы (еда на каждого бойца) и дерево на баррикаду;
///  - первый дошедший боец ставит лагерь (флаг + баррикада, если хватило дерева);
///  - бойцы едят из общего запаса лагеря;
///  - когда еды остаётся мало, один боец идёт в Ратушу за провиантом и возвращается;
///  - если в Ратуше еды нет (или голод затянулся) - отряд возвращается в казарму.
/// Сам отряд - обычный C#-класс, тикает из BarracksController.
/// </summary>
public class Squad
{
    public readonly int Number;
    public string Name { get { return "Отряд " + Number; } }

    public SoldierOrder Order { get; private set; } = SoldierOrder.Guard;
    public Vector2 Point { get; private set; }
    public bool PatrolAtCamp { get; private set; } = true;   // true - патрулируют вокруг лагеря, false - стоят
    public int Food { get; private set; }
    public bool HasCamp { get { return camp != null; } }
    public Vector2 CampPoint { get { return campPoint; } }
    public IReadOnlyList<CitizenAgent> Members { get { return members; } }
    public CitizenAgent Supplier { get { return supplier; } }
    public bool HasBarricade { get { return barricadePaid; } }
    public CampMarker Camp { get { return camp; } }

    // Отряд ушёл в набег (RaidManager): дома приказы ему не отдаются, лагерь и снабжение не работают
    public bool OnExpedition { get; set; }

    // Лечение в целом лагере: здоровья в секунду и сколько здоровья даёт 1 еда лагеря
    public const float CampHealPerSecond = 3f;
    public const float CampHpPerFood = 10f;
    float healFoodAcc;

    // На сколько минут хватит еды при текущем составе
    public float FoodMinutes
    {
        get { return members.Count <= 0 ? 0f : Food / (float)members.Count * owner.eatInterval / 60f; }
    }

    // Сколько еды стоит принести, чтобы запас снова стал полным
    public int SupplyWanted
    {
        get { return Mathf.Max(1, members.Count * owner.foodPerSoldier - Food); }
    }

    readonly BarracksController owner;
    readonly List<CitizenAgent> members = new List<CitizenAgent>();

    CampMarker camp;
    Vector2 campPoint;
    bool barricadePaid;

    CitizenAgent supplier;
    float eatTimer;
    float hungerTimer;
    float supplyRetry;

    public Squad(BarracksController owner, int number)
    {
        this.owner = owner;
        Number = number;
    }

    // ---------- Состав ----------

    public void Add(CitizenAgent s)
    {
        if (s == null || members.Contains(s)) return;
        members.Add(s);
        s.Squad = this;
        s.PatrolAtPoint = PatrolAtCamp;
    }

    public void Remove(CitizenAgent s)
    {
        if (!members.Remove(s)) return;
        if (s == null) return;
        if (s == supplier) CancelSupplier();
        if (s.Squad == this) s.Squad = null;
    }

    // ---------- Приказы ----------

    /// <summary>Отряд уходит в казарму: "Внутрь" или "Охранять". Лагерь снимается, остатки еды возвращаются в казну.</summary>
    public void GoHome(SoldierOrder order)
    {
        if (OnExpedition) return;
        Order = order;
        DestroyCamp();
        CancelSupplier();

        if (Food > 0 && ResourceManager.Instance != null)
            ResourceManager.Instance.Add(0, 0, 0, Food);
        Food = 0;
        eatTimer = 0f;
        hungerTimer = 0f;

        foreach (var m in members)
            if (m != null) m.SetOrder(order, Vector2.zero);
    }

    public void Retreat()
    {
        GoHome(SoldierOrder.Garrison);
    }

    /// <summary>Отряд идёт в точку и ставит там лагерь.</summary>
    public void Deploy(Vector2 point)
    {
        if (OnExpedition) return;
        members.RemoveAll(m => m == null);
        if (members.Count == 0) return;

        DestroyCamp();
        CancelSupplier();

        Order = SoldierOrder.Move;
        Point = point;
        eatTimer = 0f;
        hungerTimer = 0f;
        supplyRetry = 0f;

        // Припасы из казны: еда на каждого бойца и дерево на баррикаду
        var rm = ResourceManager.Instance;
        if (rm != null)
        {
            int want = Mathf.Max(0, members.Count * owner.foodPerSoldier - Food);
            int take = Mathf.Min(want, rm.food);
            if (take > 0 && rm.Spend(0, 0, 0, take)) Food += take;

            barricadePaid = owner.campWoodCost <= 0 || rm.Spend(0, owner.campWoodCost, 0, 0);
        }
        else barricadePaid = false;

        float spread = members.Count <= 1 ? 0f : Mathf.Min(3f, 0.4f + 0.3f * members.Count);
        foreach (var m in members)
        {
            m.PatrolAtPoint = PatrolAtCamp;
            m.SetOrder(SoldierOrder.Move, point + Random.insideUnitCircle * spread);
        }
    }

    public void SetPatrol(bool patrol)
    {
        PatrolAtCamp = patrol;
        foreach (var m in members)
            if (m != null) m.PatrolAtPoint = patrol;
    }

    // ---------- Жизнь отряда ----------

    public void Tick(float dt)
    {
        members.RemoveAll(m => m == null);
        if (OnExpedition) return;   // в походе за отряд всё решает RaidManager
        if (supplier != null && !supplier.IsSupplying) supplier = null; // рейс закончился

        if (Order != SoldierOrder.Move || members.Count == 0) return;

        // Первый дошедший боец ставит лагерь
        if (camp == null && AnyReached()) BuildCamp();

        if (supplyRetry > 0f) supplyRetry -= dt;

        // Все бойцы едят из общего запаса
        eatTimer += dt;
        if (eatTimer >= owner.eatInterval)
        {
            eatTimer -= owner.eatInterval;
            Food = Mathf.Max(0, Food - members.Count);
        }

        // Бойцы у целого лагеря (не в бою) потихоньку лечатся за еду лагеря. В руинах - нет.
        HealAtCamp(dt);

        bool hasSupplier = supplier != null;

        // Еда подходит к концу - посылаем гонца в Ратушу
        if (camp != null && !hasSupplier && supplyRetry <= 0f && Food <= members.Count * owner.lowFoodMeals)
            StartSupplyRun();

        // Голодают, а помощь не идёт - отряд сворачивается
        if (Food <= 0 && !hasSupplier)
        {
            hungerTimer += dt;
            if (hungerTimer >= owner.starveGrace) Retreat();
        }
        else hungerTimer = 0f;
    }

    void HealAtCamp(float dt)
    {
        if (camp == null || camp.IsRuined || Food <= 0) return;
        foreach (var m in members)
        {
            if (m == null || m.IsDead || m.InCombat || m.IsSupplying || m.IsInsideBuilding) continue;
            if (m.Hp >= m.MaxHp || !camp.CanHealAt(m.transform.position)) continue;

            healFoodAcc += m.ReceiveHeal(CampHealPerSecond * dt) / CampHpPerFood;
            while (healFoodAcc >= 1f && Food > 0) { Food--; healFoodAcc -= 1f; }
            if (Food <= 0) { healFoodAcc = 0f; return; }
        }
    }

    // Боец в этой точке укрыт баррикадой лагеря (получает меньше урона)
    public bool CoverAt(Vector2 p)
    {
        return Order == SoldierOrder.Move && camp != null && camp.IsCover(p);
    }

    // Лагерь разрушили: весь запас еды достаётся врагам
    public int TakeAllFood()
    {
        int f = Food;
        Food = 0;
        return f;
    }

    bool AnyReached()
    {
        foreach (var m in members)
            if (m != null && m.OrderReached) return true;
        return false;
    }

    void BuildCamp()
    {
        Vector2 p = Point;
        var nav = GridNavigation.Instance;
        if (nav != null)
        {
            Vector2Int c = nav.WorldToCell(p);
            if (nav.IsBlocked(c)) c = nav.NearestFree(c);
            p = nav.CellCenter(c);
        }
        campPoint = p;
        camp = CampMarker.Create(p, this, barricadePaid);
    }

    void StartSupplyRun()
    {
        CitizenAgent best = null;
        float bestD = float.MaxValue;
        foreach (var m in members)
        {
            if (m == null || m.IsInsideBuilding || m.IsSupplying) continue;
            float d = Vector2.Distance(m.transform.position, campPoint);
            if (m.HasLoot) d -= 100f; // за едой лучше послать того, кто несёт трофеи - сдаст их в Ратуше
            if (d < bestD) { bestD = d; best = m; }
        }

        if (best != null && best.StartSupply(this)) supplier = best;
    }

    // Игрок просит прямо сейчас послать бойца за едой
    public void RequestSupply()
    {
        if (Order == SoldierOrder.Move && camp != null && supplier == null) StartSupplyRun();
    }

    // Гонец вернулся в лагерь. food == 0 - в Ратуше еды не нашлось.
    public void OnSupplyDelivered(int food)
    {
        Food += food;
        supplier = null;
        supplyRetry = 5f;
        if (food <= 0) Retreat();
    }

    // Гонец не смог дойти до Ратуши
    public void OnSupplyFailed()
    {
        supplier = null;
        supplyRetry = 10f;
    }

    void CancelSupplier()
    {
        if (supplier != null) supplier.CancelSupply();
        supplier = null;
    }

    void DestroyCamp()
    {
        if (camp != null) Object.Destroy(camp.gameObject);
        camp = null;
    }

    public void Dispose()
    {
        DestroyCamp();
        CancelSupplier();
    }

    public string StatusText
    {
        get
        {
            if (OnExpedition) return "<color=#FFB060>в походе</color>";
            switch (Order)
            {
                case SoldierOrder.Garrison: return "в казарме";
                case SoldierOrder.Guard: return "охраняет казарму";
                default:
                    if (camp == null) return "идёт в точку";
                    string food = Food <= 0 ? "<color=red>еда 0</color>" : "еда " + Food;
                    string s = "лагерь, " + (PatrolAtCamp ? "патруль" : "стоят") + ", " + food;
                    if (supplier != null) s += " (за едой)";
                    return s;
            }
        }
    }
}