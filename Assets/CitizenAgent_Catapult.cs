using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Катапульта (продолжение класса CitizenAgent; вид войск - BarracksController.CatapultName,
/// путь Инженерии, направление «Военный инженер», собирается в Инженерной казарме).
///  - Расчёт - один житель. Бьёт камнями навесом: камень летит через стены и дома и бьёт всех врагов в радиусе.
///  - Дальность от catapultMinRange до attackRange: ближе не стреляет (ищет цель подальше или откатывается).
///  - Урон падает с расстоянием: вблизи полный, на пределе - catapultFarMul от него.
///    В радиусе удара: в центре 100%, у края 60%.
///  - Приказы - как у солдат. В лагере («Послать...») катапульта стоит на месте и бьёт всё, что в радиусе,
///    за врагами не ездит. В охране казармы - подъезжает к врагу, как другие бойцы.
///  - В набеге (RaidUnit.IsCatapult): так же навесом по защитникам и зданиям (по зданиям ×catapultBuildingMul).
/// </summary>
public partial class CitizenAgent
{
    float catapultShotAt = -99f;
    Vector2 catapultAim = Vector2.right;
    SpriteRenderer catFrameSr, catWheelSr, catArmSr, catStoneSr;

    static readonly Color CatapultStoneColor = new Color(0.5f, 0.48f, 0.44f);
    static readonly Color CatapultDustColor = new Color(0.62f, 0.55f, 0.45f, 0.6f);

    public bool IsCatapult => unit != null && unit.isCatapult;
    public float CatapultMinRange => IsCatapult ? unit.catapultMinRange : 0f;
    public float CatapultRadius => IsCatapult ? unit.catapultRadius : 0f;

    // В лагере катапульта держит позицию: бьёт всё в радиусе и за врагами не ездит
    bool CatapultHolds => IsCatapult && order == SoldierOrder.Move && OrderReached;

    /// <summary>Урон камня по цели на расстоянии d: вблизи полный, на пределе дальности - меньше.</summary>
    public float CatapultDamageAt(float d)
    {
        if (!IsCatapult) return 0f;
        float minR = unit.catapultMinRange, maxR = Mathf.Max(minR + 0.1f, AttackRange);
        float t = Mathf.Clamp01((d - minR) / (maxR - minR));
        return Damage * Mathf.Lerp(1f, unit.catapultFarMul, t);
    }

    static float CatapultFlight(float d) { return Mathf.Clamp(d / 10f, 0.45f, 1.3f); }

    // Подходит ли враг катапульте: не ближе мин. дальности; держит позицию - только в радиусе выстрела
    bool CatapultWants(EnemyAgent e)
    {
        if (e == null) return false;
        float d = Vector2.Distance(transform.position, e.Position);
        if (d < unit.catapultMinRange) return false;
        return !CatapultHolds || d <= AttackRange;
    }

    // ---------- Бой дома ----------

    void CatapultFightTick(GridNavigation nav)
    {
        Vector2 me = transform.position;
        Vector2 tp = target.Position;
        float d = Vector2.Distance(me, tp);
        float range = AttackRange;
        float minR = unit.catapultMinRange;
        bool wading = nav.IsWading(me);
        bool hold = CatapultHolds;

        // Враг слишком близко: цель подальше, иначе откатываемся (держит позицию - просто ждёт)
        if (d < minR)
        {
            lastCombatTime = Time.time;
            EnemyAgent alt = EnemyAgent.FindNearest(me, range, CatapultWants);
            if (alt != null) { target = alt; return; }
            if (!hold && !wading)
            {
                Vector2 away = (me - tp).sqrMagnitude > 0.001f ? (me - tp).normalized : Vector2.down;
                mover.Step(transform, nav, me + away * (minR + 0.6f - d), moveSpeed, 0.2f);
            }
            return;
        }

        if (d <= range && !wading)
        {
            mover.Clear();
            lastCombatTime = Time.time;
            if (attackTimer <= 0f)
            {
                attackTimer = Mathf.Max(0.5f, unit.attackCooldown);
                CatapultStrikeHome(target);
            }
            return;
        }

        if (hold)
        {
            // Цель ушла из радиуса - не гонимся, ждём следующую
            target = null;
            return;
        }

        if (wading && d <= range)
        {
            mover.Step(transform, nav, Rivers.DryPoint(tp), moveSpeed, 0.3f);
            return;
        }

        mover.Step(transform, nav, tp, moveSpeed, range * 0.8f);
        if (mover.Fails >= 3)
        {
            ignoreEnemy = target;
            ignoreEnemyUntil = Time.time + 5f;
            target = null;
            mover.Clear();
            mover.ResetFails();
        }
    }

    /// <summary>Бросок камня по врагу дома: летит навесом (через стены), бьёт по площади.</summary>
    void CatapultStrikeHome(EnemyAgent e)
    {
        Vector2 me = transform.position;
        Vector2 at = e.Position;
        float d = Vector2.Distance(me, at);
        if ((at - me).sqrMagnitude > 0.001f) catapultAim = (at - me).normalized;
        catapultShotAt = Time.time;

        float dmg = CatapultDamageAt(d);
        float radius = unit.catapultRadius;
        CitizenAgent by = this;
        CannonFx.Ball(me + Vector2.up * bodyScale * 0.3f, at, CatapultFlight(d), true, CatapultStoneColor, () =>
        {
            CannonFx.Impact(at, ModuleKind.None);
            CombatFx.Ring(at, radius, CatapultDustColor, 0.45f);
            CitizenAgent killer = by != null ? by : null;
            foreach (var en in new List<EnemyAgent>(EnemyAgent.All))
            {
                if (!EnemyAgent.Alive(en)) continue;
                float r = Vector2.Distance(en.Position, at);
                float reach = radius + (en.Unit != null ? en.Unit.bodyScale * 0.3f : 0f);
                if (r > reach) continue;
                en.TakeDamage(dmg * Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(r / Mathf.Max(0.1f, radius))), killer);
            }
        });
    }

    // ---------- Набег (RaidUnit) ----------

    /// <summary>Выстрел в набеге по точке на расстоянии d: урон с учётом дальности, радиус, множитель по зданиям.</summary>
    public void ExpCatapultShot(float d, out float damage, out float radius, out float buildingMul)
    {
        damage = CatapultDamageAt(d);
        radius = IsCatapult ? unit.catapultRadius : 0f;
        buildingMul = IsCatapult ? unit.catapultBuildingMul : 1f;
        catapultShotAt = Time.time;
    }

    public static float ExpCatapultFlight(float d) { return CatapultFlight(d); }

    // ---------- Внешний вид: рама на колёсах и рычаг, который бросает и медленно взводится ----------

    /// <summary>Угол рычага (0° - вправо, вперёд по цели): взведён назад, после броска - вперёд и медленно обратно.</summary>
    public static float CatapultArmAngle(float since, float cooldown)
    {
        const float cocked = 160f, thrown = 60f, swing = 0.12f;
        if (since < swing) return Mathf.Lerp(cocked, thrown, since / swing);
        return Mathf.Lerp(thrown, cocked, Mathf.Clamp01((since - swing) / Mathf.Max(0.3f, cooldown * 0.8f)));
    }

    /// <summary>Положение и поворот рычага для стороны facingRight (общая формула для дома и набега).</summary>
    public static void CatapultArmPose(float scale, float angleDeg, bool facingRight, out Vector3 pos, out Quaternion rot, out Vector3 size, out Vector3 tip)
    {
        float a = facingRight ? angleDeg : 180f - angleDeg;
        float len = scale * 1.0f;
        Vector2 pivot = new Vector2(0f, -scale * 0.05f);
        Vector2 dir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
        pos = pivot + dir * len * 0.5f;
        rot = Quaternion.Euler(0f, 0f, a);
        size = new Vector3(len, scale * 0.12f, 1f);
        tip = pivot + dir * len;
    }

    void CatapultLook(bool shown)
    {
        bool on = IsCatapult && shown;
        if (!on)
        {
            if (catFrameSr != null) catFrameSr.enabled = catWheelSr.enabled = catArmSr.enabled = catStoneSr.enabled = false;
            return;
        }
        if (catFrameSr == null)
        {
            catWheelSr = MakePart("CatapultWheels", Vector2.zero, Vector2.one, new Color(0.22f, 0.15f, 0.08f), 9);
            catFrameSr = MakePart("CatapultFrame", Vector2.zero, Vector2.one, new Color(0.45f, 0.3f, 0.15f), 11);
            catArmSr = MakePart("CatapultArm", Vector2.zero, Vector2.one, new Color(0.66f, 0.5f, 0.28f), 12);
            catStoneSr = MakePart("CatapultStone", Vector2.zero, Vector2.one, CatapultStoneColor, 13);
        }
        catFrameSr.enabled = catWheelSr.enabled = catArmSr.enabled = true;

        float s = bodyScale;
        catWheelSr.transform.localPosition = new Vector3(0f, -s * 0.45f, 0f);
        catWheelSr.transform.localScale = new Vector3(s * 1.3f, s * 0.22f, 1f);
        catFrameSr.transform.localPosition = new Vector3(0f, -s * 0.25f, 0f);
        catFrameSr.transform.localScale = new Vector3(s * 1.2f, s * 0.25f, 1f);

        float since = Time.time - catapultShotAt;
        float angle = CatapultArmAngle(since, unit.attackCooldown);
        CatapultArmPose(s, angle, catapultAim.x >= 0f, out Vector3 pos, out Quaternion rot, out Vector3 size, out Vector3 tip);
        catArmSr.transform.localPosition = pos;
        catArmSr.transform.localRotation = rot;
        catArmSr.transform.localScale = size;

        // Камень в ложке - когда рычаг снова взведён
        bool loaded = angle > 150f;
        catStoneSr.enabled = loaded;
        if (loaded)
        {
            catStoneSr.transform.localPosition = tip;
            catStoneSr.transform.localScale = new Vector3(s * 0.22f, s * 0.22f, 1f);
        }
    }
}
