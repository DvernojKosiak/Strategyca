using UnityEngine;

/// <summary>
/// Возчик обоза - рабочий Обозного двора (продолжение класса CitizenAgent).
/// Пока обоз (CartAgent) в пути, житель идёт рядом с повозкой: на карте он виден и держится у повозки,
/// а когда обоз уезжает за кадр - "заходит" в здание двора (на улице его нельзя тронуть).
/// Обоз вернулся - возчик входит во двор и продолжает работать как обычно.
/// Управляет возчиком CartAgent, сам житель только следует за ним.
/// </summary>
public partial class CitizenAgent
{
    private CartAgent cartRef;
    private int cartSlot;

    public bool IsCartDriver => cartRef != null;

    public bool StartCartDuty(CartAgent cart, int slot)
    {
        if (cart == null || IsCartDriver || IsMilitary || dead) return false;
        mealTrip = false;
        cartRef = cart;
        cartSlot = slot;
        path = null;
        pathTarget = null;
        leaveAt = -1f;

        var nav = GridNavigation.Instance;
        if (inside && nav != null) ExitBuilding(nav);
        transform.position = cart.DriverPosition(slot);
        if (sr != null) { sr.enabled = true; sr.color = new Color(0.75f, 0.9f, 0.55f); }
        return true;
    }

    // true - занят обозом (обычный распорядок на этот кадр пропускаем)
    bool CartDutyUpdate(GridNavigation nav)
    {
        if (cartRef == null) { EndCartDuty(null); return false; }   // обоз исчез

        if (cartRef.DriversVisible)
        {
            if (inside) ExitBuilding(nav);
            if (sr != null) sr.enabled = true;
            transform.position = cartRef.DriverPosition(cartSlot);
        }
        else
        {
            // Обоз за кадром: возчик "внутри" двора, чтобы враги его не доставали
            if (!inside)
            {
                BuildingInstance yb = cartRef.YardBuilding;
                if (yb != null && Usable(yb)) EnterBuilding(yb);
                else
                {
                    // Двор снесён или в руинах - возчик остаётся на улице обычным жителем
                    CancelCartDuty();
                    return false;
                }
            }
            else if (insideRef == null || insideRef.IsRuined)
            {
                // Двор рухнул, пока возчик внутри: обычная логика обрушения сама его выпустит
                CancelCartDuty();
                return false;
            }
        }
        return true;
    }

    /// <summary>Обоз вернулся (или двор снесли): возчик заходит во двор или остаётся на улице.</summary>
    public void EndCartDuty(BuildingInstance enterAt)
    {
        if (cartRef == null) return;
        cartRef = null;
        thinkTimer = 0f;
        retryTimer = 0f;
        path = null;
        pathTarget = null;

        if (enterAt != null && Usable(enterAt)) EnterBuilding(enterAt);
        else
        {
            var nav = GridNavigation.Instance;
            if (inside && nav != null) ExitBuilding(nav);
            else if (sr != null) sr.enabled = true;
        }
        if (sr != null) sr.color = currentTint;
    }

    // Жителя забрали (в казарму) или он исчез: обоз остаётся без него
    void CancelCartDuty()
    {
        if (cartRef == null) return;
        CartAgent c = cartRef;
        EndCartDuty(null);
        c.DriverLeft(this);
    }
}
