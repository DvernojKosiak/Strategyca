using UnityEngine;

/// <summary>
/// Боец, проданный на рынке (продолжение класса CitizenAgent).
/// Раньше он исчезал прямо там, где стоял. Теперь уходит из города пешком:
///  1. Выходит из здания и идёт к двери Торгового дома (там его "оформляют" в лагерь наёмников).
///     Торгового дома нет / закрыт / не дойти - сразу к краю карты.
///  2. Идёт к ближайшему краю карты (реку переходит вброд, как любой солдат) и там пропадает:
///     только тогда население города уменьшается (PopulationManager.RemoveAgent).
/// Он уже не боец казармы: приказов не слушает и в бой не вступает. Конь и оружие ушли вместе с лотом,
/// но всадник уезжает верхом (это только вид).
/// </summary>
public partial class CitizenAgent
{
    enum DepartStage { None, ToMarket, ToEdge }

    DepartStage departStage = DepartStage.None;
    BuildingInstance departVia;
    Vector2 departEdge;
    bool departMounted;
    float departTimer;

    const float DepartMarketTimeout = 60f;    // не дошёл до Торгового дома за это время - идёт прямо к краю
    const float DepartTotalTimeout = 160f;    // совсем застрял - уходит "за кадром"

    public bool IsDeparting => departStage != DepartStage.None;

    string DepartText => departStage == DepartStage.ToMarket
        ? "<color=#C8A8FF>продан: идёт к Торговому дому</color>"
        : "<color=#C8A8FF>продан: уходит из города в лагерь наёмников</color>";

    /// <summary>Боец продан (BarracksController.ReleaseForSale): идёт через Торговый дом к краю карты.</summary>
    public void DepartForSale(BuildingInstance via, bool mounted)
    {
        if (dead) return;
        departMounted = mounted;
        departVia = via;
        departStage = via != null ? DepartStage.ToMarket : DepartStage.ToEdge;
        departTimer = 0f;
        departEdge = Vector2.zero;

        barracks = null;
        barracksCtrl = null;
        target = null;
        healTarget = null;
        recovering = false;
        restAfterTrip = false;
        postRest = false;
        Home = null;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        pathFails = 0;
    }

    void DepartTick(GridNavigation nav, float dt)
    {
        departTimer += dt;

        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return;   // здание переносят - ждём
            ExitBuilding(nav);
        }

        if (departStage == DepartStage.ToMarket)
        {
            bool give = departVia == null || !Usable(departVia) || departTimer > DepartMarketTimeout || pathFails >= 4;
            if (!give && !WalkTo(nav, departVia)) return;
            if (!give) CombatFx.Spark(transform.position, new Color(0.8f, 0.65f, 1f), 0.3f, 0.5f);   // "оформили"
            departStage = DepartStage.ToEdge;
            departEdge = Vector2.zero;
            mover.Clear();
            mover.ResetFails();
        }

        if (departEdge == Vector2.zero) departEdge = NearestEdge(nav, transform.position);
        bool arrived = mover.Step(transform, nav, departEdge, moveSpeed, 0.4f);
        if (arrived || EdgeDist(transform.position, nav) < 0.9f || mover.Fails >= 8 || departTimer > DepartTotalTimeout)
            FinishDeparture();
    }

    static Vector2 NearestEdge(GridNavigation nav, Vector2 p)
    {
        float h = nav.mapSize / 2f - 0.5f;
        float dl = p.x + h, dr = h - p.x, db = p.y + h, dt = h - p.y;
        float m = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, dt));
        Vector2 e;
        if (m == dl) e = new Vector2(-h, p.y);
        else if (m == dr) e = new Vector2(h, p.y);
        else if (m == db) e = new Vector2(p.x, -h);
        else e = new Vector2(p.x, h);
        Vector2Int c = nav.WorldToCell(e);
        if (nav.IsBlocked(c)) c = nav.NearestFree(c);
        return nav.CellCenter(c);
    }

    void FinishDeparture()
    {
        departStage = DepartStage.None;
        var pm = PopulationManager.Instance;
        if (pm != null) pm.RemoveAgent(this);
        else Destroy(gameObject);
    }
}
