using UnityEngine;

/// <summary>
/// Поход солдата в набег (продолжение класса CitizenAgent).
///  1. BeginExpedition: боец идёт к краю нашей карты.
///  2. Дошёл - "в походе": на карте деревни его нет, враги его не видят. На карте набега за него
///     воюет RaidUnit, который берёт его здоровье, урон, оружие, щит и коня.
///  3. ReturnFromExpedition: появляется у края карты с добычей и сам несёт её в казарму / Ратушу.
/// Погиб в набеге - погиб и в деревне (как от врагов дома: оружие и конь потеряны).
///
/// Дозор (ветка «Мастер маскировки», окно - CamoScouts): разведчик в маскировочной накидке
/// идёт к точке на нашей карте и сидит там тихо (hiddenLookout): враги его не видят, он рассеивает
/// туман (FogOfWar.LookoutVision) и предупреждает о войсках соседей. Паёк из казны кончился -
/// голодным идёт в казарму. Пока в дозоре - тоже "в походе" (IsOnExpedition): в отряды и набеги не берут.
/// </summary>
public partial class CitizenAgent
{
    enum ExpPhase { None, MarchOut, Away }

    ExpPhase expPhase = ExpPhase.None;
    Vector2 expEdge;
    float expTimer;
    bool expeditionLoot;          // добыча из похода: сдаётся без лишних сообщений (итог показан в отчёте)
    float expDeadline;            // сколько секунд даём дойти до края (зависит от длины пути)

    static float EdgeDist(Vector2 p, GridNavigation nav)
    {
        float h = nav.mapSize / 2f;
        return Mathf.Min(Mathf.Min(p.x + h, h - p.x), Mathf.Min(p.y + h, h - p.y));
    }

    public bool IsOnExpedition => expPhase != ExpPhase.None || lookPhase != LookPhase.None;
    public bool IsAway => expPhase == ExpPhase.Away;

    /// <summary>Разведчик: видит дальше (туман войны), ходит разведывать деревни перед набегом.</summary>
    public bool IsScout => unit != null && unit.unitName == RaidManager.ScoutUnitName;

    // ---------- Боевые числа для карты набега ----------

    public float ExpRange => AttackRange;
    public float ExpCooldown => unit != null ? Mathf.Max(0.1f, unit.attackCooldown * (weapon != null ? weapon.SpeedMul : 1f)) : 1f;
    public float ExpAoeRadius => unit != null ? unit.aoeRadius : 0f;

    // Яд последнего удара в набеге (RaidUnit читает сразу после ExpeditionStrike)
    public float ExpLastPoisonDps { get; private set; }
    public float ExpLastPoisonDuration { get; private set; }
    // Замедление последнего удара (лук с зельем замедления)
    public float ExpLastSlowFactor { get; private set; } = 1f;
    public float ExpLastSlowDuration { get; private set; }
    // Поджигатель: последний удар был ведром горящей смолы (RaidUnit бьёт огнём по площади)
    public bool ExpLastFire { get; private set; }
    public UnitType ExpUnit => unit;
    public bool ExpRanged => Ranged;
    public float ExpHealPerSecond => unit != null ? unit.healPerSecond : 0f;
    public float ExpHealRange => unit != null ? unit.healRange : 0f;
    public float ExpBodyScale => bodyScale;
    public Color ExpTint => currentTint;
    public float ExpDamagePreview => Damage;

    /// <summary>Удар в набеге: урон бойца, оружие изнашивается (может сломаться).</summary>
    public float ExpeditionStrike()
    {
        if (unit == null) return 0f;
        ExpLastFire = false;
        if (BucketFull)
        {
            bucketFull = false;
            ExpLastFire = true;
            ExpLastPoisonDps = 0f;
            ExpLastPoisonDuration = 0f;
            ExpLastSlowFactor = 1f;
            ExpLastSlowDuration = 0f;
            return unit.fireDamage;
        }
        // Маг стихий: иногда огненный шар по площади (на карте набега рек нет - разлива не бывает)
        if (unit.IsElementalist)
        {
            ExpLastPoisonDps = 0f;
            ExpLastPoisonDuration = 0f;
            ExpLastSlowFactor = 1f;
            ExpLastSlowDuration = 0f;
            float mana = ManaManager.Factor;
            if (unit.fireballChance > 0f && Random.value < unit.fireballChance)
            {
                ExpLastFire = true;
                return unit.fireballDamage * mana;
            }
            return Damage * mana;
        }
        float d = Damage;
        TakeShotPoison(out float pDps, out float pDur);
        ExpLastPoisonDps = pDps;
        ExpLastPoisonDuration = pDur;
        TakeShotSlow(out float sF, out float sDur);
        ExpLastSlowFactor = sF;
        ExpLastSlowDuration = sDur;
        if (weapon != null && weapon.Wear())
        {
            WarManager.Notify($"Сломалось оружие в походе: {weapon.Name} ({unit.unitName} {citizenName})", false);
            weapon = null;
        }
        return d;
    }

    /// <summary>Голод в походе (кончилась еда): здоровье тает, но не ниже четверти.</summary>
    public void ExpeditionHunger(float amount)
    {
        float floor = maxHp * 0.25f;
        if (hp > floor) hp = Mathf.Max(floor, hp - amount);
    }

    // ---------- Выход и возвращение ----------

    /// <summary>Боец выступает в поход: идёт к краю карты edge. false - не может (не солдат, уже в походе).</summary>
    public bool BeginExpedition(Vector2 edge)
    {
        if (dead || !IsSoldier || IsOnExpedition) return false;
        if (IsSupplying) CancelSupply();
        if (bed != null) LeaveBed();
        target = null;
        healTarget = null;
        recovering = false;
        expEdge = edge;
        float dist = Vector2.Distance(transform.position, edge);
        expDeadline = 25f + dist / Mathf.Max(0.5f, moveSpeed) * 2.5f;
        expPhase = ExpPhase.MarchOut;
        expTimer = 0f;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        return true;
    }

    // true - боец занят походом (остальной распорядок пропускаем)
    bool ExpeditionTick(GridNavigation nav, float dt)
    {
        if (lookPhase != LookPhase.None) return LookoutTick(nav, dt);
        if (expPhase == ExpPhase.None) return false;

        if (expPhase == ExpPhase.Away)
        {
            if (sr != null && sr.enabled) sr.enabled = false;
            return true;
        }

        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return true;
            ExitBuilding(nav);
        }

        expTimer += dt;
        bool arrived = mover.Step(transform, nav, expEdge, moveSpeed, 0.5f);
        bool atEdge = EdgeDist(transform.position, nav) < 1.0f;
        if (arrived || atEdge || mover.Fails >= 8 || expTimer > expDeadline) GoAway();
        return true;
    }

    /// <summary>Отстающий (застрял по дороге к краю) уходит сразу.</summary>
    public void ForceAway()
    {
        if (expPhase == ExpPhase.MarchOut) GoAway();
    }

    void GoAway()
    {
        expPhase = ExpPhase.Away;
        mover.Clear();
        inside = false;
        insideRef = null;
        transform.position = expEdge;
        if (sr != null) sr.enabled = false;
    }

    /// <summary>Поход окончен: боец появляется в точке at (у края карты) и несёт добычу домой.</summary>
    public void ReturnFromExpedition(Vector2 at, Loot loot)
    {
        if (dead) return;
        expPhase = ExpPhase.None;
        inside = false;
        insideRef = null;
        transform.position = at;
        if (sr != null) sr.enabled = true;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        target = null;
        patrolHasPoint = false;
        if (loot != null && !loot.IsEmpty)
        {
            loot.MoveTo(carried);
            expeditionLoot = true;
        }
        restAfterTrip = HpFraction < RestUntil;   // вернулся раненым - идёт отдыхать в казарму
    }

    /// <summary>Поход отменён, пока бойцы шли к краю / были в пути: возвращаются на месте.</summary>
    public void CancelExpedition()
    {
        if (expPhase == ExpPhase.None) return;
        bool wasAway = expPhase == ExpPhase.Away;
        expPhase = ExpPhase.None;
        if (wasAway && sr != null) sr.enabled = true;
        mover.Clear();
        mover.ResetFails();
    }

    // ---------- Дозор (CamoScouts) ----------

    enum LookPhase { None, Going, Posted, Returning }

    const float LookoutHungryHp = 0.4f;      // паёк кончился: здоровье падает до этой доли
    const float LookoutFleeBelow = 0.5f;     // ранили по дороге ниже этой доли - возвращается
    const float LookoutReturnLimit = 240f;   // дольше этого не ищет дорогу домой - дальше как обычный боец

    LookPhase lookPhase = LookPhase.None;
    Vector2 lookPoint;
    float lookRation, lookRationMax;         // паёк в секундах: осталось / всего
    int lookFood;                            // сколько еды взял из казны
    float lookTimer, lookDeadline;
    bool lookHungry;

    public bool IsLookout => lookPhase != LookPhase.None;
    public bool LookoutGoing => lookPhase == LookPhase.Going;
    public bool LookoutPosted => lookPhase == LookPhase.Posted;
    public bool LookoutReturning => lookPhase == LookPhase.Returning;
    public Vector2 LookoutPoint => lookPoint;
    public float LookoutRationLeft => Mathf.Max(0f, lookRation);
    public float LookoutRationFrac => lookRationMax > 0f ? Mathf.Clamp01(lookRation / lookRationMax) : 0f;
    public bool LookoutHungry => lookHungry;

    /// <summary>Что делает дозорный (для окна дозоров и карточки бойца).</summary>
    public string LookoutStatus
    {
        get
        {
            switch (lookPhase)
            {
                case LookPhase.Going: return "идёт в дозор";
                case LookPhase.Posted: return UiKit.Col(UiKit.GoodHex, "в дозоре");
                case LookPhase.Returning: return lookHungry ? UiKit.Col(UiKit.WarnHex, "голоден, идёт домой") : "возвращается из дозора";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Разведчик уходит в дозор к точке point. Паёк (food еды, уже взятой из казны) хватает на rationSeconds.
    /// false - не может (не разведчик, в походе, уже в дозоре).
    /// </summary>
    public bool BeginLookout(Vector2 point, float rationSeconds, int food)
    {
        if (dead || !IsScout || IsOnExpedition || IsDeparting) return false;
        if (IsSupplying) CancelSupply();
        if (bed != null) LeaveBed();
        if (Squad != null)
        {
            Squad sq = Squad;
            sq.Remove(this);
            Squad = null;
        }
        SetOrder(SoldierOrder.Guard, transform.position);   // вернётся - охраняет свою казарму
        target = null;
        healTarget = null;
        recovering = false;
        restAfterTrip = false;

        lookPoint = point;
        lookRationMax = Mathf.Max(1f, rationSeconds);
        lookRation = lookRationMax;
        lookFood = Mathf.Max(0, food);
        lookHungry = false;
        lookTimer = 0f;
        float dist = Vector2.Distance(transform.position, point);
        lookDeadline = 30f + dist / Mathf.Max(0.5f, moveSpeed) * 3f;
        lookPhase = LookPhase.Going;
        hiddenLookout = false;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        return true;
    }

    /// <summary>Снять с дозора: идёт в казарму, остаток пайка сдаст в казну.</summary>
    public void RecallLookout()
    {
        if (lookPhase == LookPhase.Going || lookPhase == LookPhase.Posted) BeginLookoutReturn(null, true);
    }

    void BeginLookoutReturn(string note, bool good)
    {
        lookPhase = LookPhase.Returning;
        hiddenLookout = false;
        lookTimer = 0f;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
        if (!string.IsNullOrEmpty(note)) WarManager.Notify($"{UnitName} {citizenName}: {note}", good);
    }

    // Дозор окончен: дальше - обычный распорядок бойца. home - дошёл до казармы (сдаёт остаток пайка).
    void FinishLookout(bool home)
    {
        if (home && !lookHungry && lookRationMax > 0f)
        {
            int back = Mathf.FloorToInt(lookFood * LookoutRationFrac);
            var rm = ResourceManager.Instance;
            if (back > 0 && rm != null) rm.Add(0, 0, 0, back);
        }
        lookPhase = LookPhase.None;
        hiddenLookout = false;
        lookFood = 0;
        lookRation = lookRationMax = 0f;
        restAfterTrip = HpFraction < RestUntil;
        if (home && restAfterTrip) recovering = true;   // голодный - сразу ест и отдыхает в казарме
        lookHungry = false;
        mover.Clear();
        mover.ResetFails();
        path = null;
        pathTarget = null;
    }

    // true - боец занят дозором (остальной распорядок пропускаем)
    bool LookoutTick(GridNavigation nav, float dt)
    {
        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return true;
            ExitBuilding(nav);
        }
        if (sr != null && !sr.enabled) sr.enabled = true;

        switch (lookPhase)
        {
            case LookPhase.Going:
            {
                hiddenLookout = false;
                lookTimer += dt;
                if (!HasCloak) { BeginLookoutReturn("без накидки в дозор не идёт", false); return true; }
                if (InCombat && HpFraction < LookoutFleeBelow) { BeginLookoutReturn("ранен по дороге в дозор — возвращается", false); return true; }
                if (mover.Step(transform, nav, lookPoint, moveSpeed, 0.25f))
                {
                    lookPhase = LookPhase.Posted;
                    hiddenLookout = true;
                    mover.Clear();
                    CombatFx.Ring(lookPoint, 1.4f, new Color(0.45f, 0.85f, 0.35f, 0.7f), 0.6f);
                    WarManager.Notify($"{UnitName} {citizenName} затаился в дозоре", true);
                }
                else if (mover.Fails >= 8 || lookTimer > lookDeadline)
                    BeginLookoutReturn("не нашёл дороги к месту дозора", false);
                return true;
            }

            case LookPhase.Posted:
            {
                hiddenLookout = true;
                lookRation -= dt;
                if (!HasCloak) { BeginLookoutReturn("накидка порвана — уходит из дозора", false); return true; }
                if (InCombat)   // задело (по площади, ловушкой) - дозор раскрыт
                {
                    BeginLookoutReturn("дозор раскрыт — уходит домой", false);
                    return true;
                }
                if (lookRation <= 0f)
                {
                    lookRation = 0f;
                    lookHungry = true;
                    hp = Mathf.Min(hp, maxHp * LookoutHungryHp);
                    BeginLookoutReturn("паёк кончился — голодным идёт в казарму", false);
                }
                return true;
            }

            case LookPhase.Returning:
            {
                hiddenLookout = false;
                lookTimer += dt;
                // Казармы нет, напали по дороге, заблудился - дальше как обычный боец
                if (!Usable(barracks) || InCombat || lookTimer > LookoutReturnLimit) { FinishLookout(false); return false; }
                if (WalkTo(nav, barracks))
                {
                    EnterBuilding(barracks);
                    FinishLookout(true);
                }
                return true;
            }
        }
        return false;
    }
}
