using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Пиротехника (путь Инженерии, направление «Пиротехник», DevelopmentPath.IsPyro).
///  - Фейерверочный лук (модуль башни, ModuleKind.FireworkBow, числа - TowerModules): башня стреляет чаще,
///    но слабее; каждая стрела-ракета на попадании рвётся: взрыв бьёт врагов рядом с целью,
///    во все стороны летят искры и жгут тех, в кого попадут.
///  - Ловушка «Салют» (TrapBase.Salute, числа - Traps): наступившего подбрасывает и уносит в случайную
///    сторону, в полёте над ним рвётся салют (урон всем рядом), приземлившийся горит.
/// Здесь же - разноцветные вспышки и искры (свой маленький эффект PyroFx).
/// </summary>
public static class Pyrotechnics
{
    static readonly Color[] Palette =
    {
        new Color(1f, 0.32f, 0.32f),   // красный
        new Color(1f, 0.85f, 0.25f),   // золотой
        new Color(0.4f, 1f, 0.45f),    // зелёный
        new Color(0.4f, 0.75f, 1f),    // голубой
        new Color(0.85f, 0.45f, 1f),   // сиреневый
        new Color(1f, 0.55f, 0.85f),   // розовый
        new Color(1f, 0.6f, 0.15f),    // оранжевый
    };

    public static Color RandomColor() { return Palette[Random.Range(0, Palette.Length)]; }

    /// <summary>Разноцветная вспышка салюта (только картинка): кольца и разлетающиеся огоньки.</summary>
    public static void Burst(Vector2 at, float radius, int particles = 14)
    {
        Color main = RandomColor();
        CombatFx.Ring(at, radius, new Color(main.r, main.g, main.b, 0.75f), 0.45f);
        CombatFx.Ring(at, radius * 0.55f, new Color(1f, 1f, 0.85f, 0.85f), 0.3f);
        CombatFx.Spark(at, Color.white, 0.35f, 0.15f);
        for (int i = 0; i < particles; i++)
        {
            float a = (i + Random.value * 0.5f) / particles * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Color c = Random.value < 0.6f ? main : RandomColor();
            PyroFx.Particle(at, dir, radius * Random.Range(0.7f, 1.15f), c, Random.Range(0.4f, 0.6f));
        }
    }

    // ---------- Фейерверочный лук ----------

    /// <summary>
    /// Выстрел башни с Фейерверочным луком: ракета летит в цель, бьёт её (dmg), рвётся -
    /// взрыв задевает врагов рядом (не саму цель), искры разлетаются и жгут попавшихся.
    /// </summary>
    public static void FireworkArrow(BuildingInstance tower, EnemyAgent target, int mk, float dmg)
    {
        if (tower == null || !EnemyAgent.Alive(target)) return;
        Vector2 from = tower.Center;
        Vector2 aim = target.Position;
        Color col = RandomColor();
        float dur = Mathf.Clamp(Vector2.Distance(from, aim) / 16f, 0.1f, 0.4f);

        PyroFx.Rocket(from, aim, dur, col, () =>
        {
            BuildingInstance by = tower != null ? tower : null;   // башню могли снести, пока летела ракета
            Vector2 at = aim;
            if (EnemyAgent.Alive(target))
            {
                at = target.Position;
                target.TakeDamage(dmg, null, by);
            }
            TowerBurst(at, target, mk, dmg, by);
        });
    }

    static void TowerBurst(Vector2 at, EnemyAgent main, int mk, float arrowDmg, BuildingInstance by)
    {
        float radius = TowerModules.FireworkBurstRadius(mk);
        int sparks = TowerModules.FireworkSparks(mk);
        Burst(at, radius, 8);

        // Взрыв: все враги рядом, кроме самой цели (ей досталась стрела)
        float splash = arrowDmg * TowerModules.FireworkSplashMul(mk);
        var hit = new List<EnemyAgent>();
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e) || e == main) continue;
            float body = e.Unit != null ? e.Unit.bodyScale * 0.3f : 0f;
            if (Vector2.Distance(e.Position, at) <= radius + body) hit.Add(e);
        }
        foreach (var e in hit) e.TakeDamage(splash, null, by);

        // Искры: разлетаются веером, каждая жжёт первого врага на пути (не цель)
        float sparkDmg = arrowDmg * TowerModules.FireworkSparkMul(mk);
        float range = TowerModules.FireworkSparkRange(mk);
        float a0 = Random.value * Mathf.PI * 2f;
        for (int i = 0; i < sparks; i++)
        {
            float a = a0 + (i + Random.Range(-0.25f, 0.25f)) / sparks * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            PyroFx.HurtSpark(at, dir, range, RandomColor(), sparkDmg, main, by);
        }
    }

    // ---------- Ловушка «Салют» ----------

    /// <summary>Враг в городе наступил на «Салют»: подбросить и унести в случайную сторону (см. PyroFx.Flight).</summary>
    public static void LaunchSalute(EnemyAgent e, TrapStats s)
    {
        if (!EnemyAgent.Alive(e)) return;
        float a = Random.value * Mathf.PI * 2f;
        Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        // Тяжёлых уносит ближе, но всё равно уносит (ракеты!)
        float dist = s.push * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(e.KnockFactor));
        PyroFx.Flight(e, dir, dist, s.root, s.burst, SaluteBurstRadius, s.burnDps, s.burnTime);
    }

    public const float SaluteBurstRadius = 1.4f;
    public const float SaluteFlightSeconds = 1f;
    public const float SaluteArc = 1.6f;          // высота дуги полёта, клеток

    /// <summary>Салют в набеге: защитника не унести (держит на месте Trap), над ним взлетает ракета и рвётся.</summary>
    public static void SaluteRaid(RaidUnit u, TrapStats s)
    {
        if (u == null || !u.Alive) return;
        Vector2 p = u.Position;
        float dmg = s.burst;
        PyroFx.Rocket(p, p + Vector2.up * SaluteArc, 0.45f, RandomColor(), () =>
        {
            Burst(p + Vector2.up * SaluteArc, SaluteBurstRadius, 16);
            if (dmg <= 0f) return;
            var hit = new List<RaidUnit>();
            foreach (var x in RaidUnit.All)
                if (x != null && x.Alive && !x.Attacker && Vector2.Distance(x.Position, p) <= SaluteBurstRadius) hit.Add(x);
            foreach (var x in hit) x.TakeDamage(dmg, null, false);
        });
    }

    /// <summary>Урон салюта врагам рядом с точкой (в городе). Убитый бросает добычу - её забирает город.</summary>
    public static void SaluteDamage(Vector2 at, float dmg)
    {
        if (dmg <= 0f) return;
        var hit = new List<EnemyAgent>();
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, at) <= SaluteBurstRadius) hit.Add(e);
        foreach (var e in hit)
        {
            if (e.HasLoot && e.Hp <= dmg) { e.Carried.DepositToTown(); CombatFx.LootFly(e.Position, at); }
            e.TakeDamage(dmg, null, null);
        }
    }
}

/// <summary>
/// Маленькие эффекты пиротехники: ракета (летит и оставляет искристый след), огонёк салюта,
/// жгучая искра (бьёт первого врага на пути), полёт врага на «Салюте». Сами себя удаляют.
/// </summary>
public class PyroFx : MonoBehaviour
{
    enum Kind { Rocket, Particle, HurtSpark, Flight }

    Kind kind;
    Vector2 from, to;
    float t, duration;
    Color color;
    SpriteRenderer sr;
    System.Action onEnd;
    float trailTimer;

    // Жгучая искра
    float damage;
    EnemyAgent skip;
    BuildingInstance by;

    // Полёт врага
    EnemyAgent victim;
    Vector2 dir;
    float dist, stun, burstDmg, burstRadius, burnDps, burnTime;
    int segDone;
    Vector2 segStart;
    bool burst, ended;
    float lifted;                        // на сколько сейчас подняты части врага
    readonly List<Transform> parts = new List<Transform>();
    Transform shadow;
    const int Segments = 4;              // полёт - несколько коротких толчков EnemyAgent.Knockback (по 0.25 с)

    static readonly List<EnemyAgent> flying = new List<EnemyAgent>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { flying.Clear(); }

    /// <summary>Враг сейчас летит на «Салюте» (ловушки под ним не срабатывают).</summary>
    public static bool Airborne(EnemyAgent e) { return e != null && flying.Contains(e); }

    static PyroFx Make(string n, Vector2 pos, float size, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size, size, 1f);
        PyroFx fx = go.AddComponent<PyroFx>();
        fx.color = c;
        fx.sr = go.AddComponent<SpriteRenderer>();
        fx.sr.sprite = CombatFx.Circle();
        fx.sr.color = c;
        fx.sr.sortingOrder = order;
        return fx;
    }

    /// <summary>Ракета: летит от a к b за dur, искрит следом, в конце - onEnd.</summary>
    public static void Rocket(Vector2 a, Vector2 b, float dur, Color c, System.Action onEnd)
    {
        PyroFx fx = Make("PyroRocket", a, 0.17f, c, 23);
        fx.kind = Kind.Rocket;
        fx.from = a;
        fx.to = b;
        fx.duration = Mathf.Max(0.05f, dur);
        fx.onEnd = onEnd;
    }

    /// <summary>Огонёк салюта: разлетается от точки и гаснет (без урона).</summary>
    public static void Particle(Vector2 at, Vector2 dir, float range, Color c, float dur)
    {
        PyroFx fx = Make("PyroParticle", at, Random.Range(0.1f, 0.16f), c, 24);
        fx.kind = Kind.Particle;
        fx.from = at;
        fx.to = at + dir.normalized * range;
        fx.duration = dur;
    }

    /// <summary>Жгучая искра: летит на range клеток, бьёт первого встречного врага (кроме skip) и гаснет.</summary>
    public static void HurtSpark(Vector2 at, Vector2 dir, float range, Color c, float dmg, EnemyAgent skip, BuildingInstance by)
    {
        PyroFx fx = Make("PyroSpark", at, 0.15f, c, 24);
        fx.kind = Kind.HurtSpark;
        fx.from = at;
        fx.to = at + dir.normalized * range;
        fx.duration = 0.35f;
        fx.damage = dmg;
        fx.skip = skip;
        fx.by = by;
    }

    /// <summary>Полёт врага на «Салюте»: уносит на dist клеток по dir, на вершине - салют, на земле - горит.</summary>
    public static void Flight(EnemyAgent e, Vector2 dir, float dist, float stun, float burstDmg, float burstRadius, float burnDps, float burnTime)
    {
        PyroFx fx = Make("SaluteFlight", e.Position, 0.01f, new Color(0, 0, 0, 0), 1);
        fx.kind = Kind.Flight;
        fx.victim = e;
        fx.dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
        fx.dist = dist;
        fx.stun = stun;
        fx.burstDmg = burstDmg;
        fx.burstRadius = burstRadius;
        fx.burnDps = burnDps;
        fx.burnTime = burnTime;
        fx.duration = Pyrotechnics.SaluteFlightSeconds;
        flying.Add(e);

        // Части врага (тело, обводка, мешок) поднимаем над землёй; на земле - тень
        foreach (Transform ch in e.transform) fx.parts.Add(ch);
        GameObject sh = new GameObject("SaluteShadow");
        sh.transform.position = e.transform.position;
        sh.transform.localScale = new Vector3(0.6f, 0.25f, 1f);
        SpriteRenderer ssr = sh.AddComponent<SpriteRenderer>();
        ssr.sprite = CombatFx.Circle();
        ssr.color = new Color(0f, 0f, 0f, 0.45f);
        ssr.sortingOrder = 4;
        fx.shadow = sh.transform;

        CombatFx.Ring(e.Position, 0.7f, new Color(1f, 0.8f, 0.3f, 0.85f), 0.35f);   // хлопок пускового заряда
        fx.StartSegment();
    }

    void StartSegment()
    {
        if (!EnemyAgent.Alive(victim)) return;
        segStart = victim.Position;
        bool last = segDone == Segments - 1;
        // Промежуточные толчки держат оглушение, последний - полное оглушение после приземления
        victim.Knockback(dir, dist / Segments, last ? stun : 0.3f, last ? 6f : 0f);
    }

    void SetLift(float h)
    {
        float d = h - lifted;
        if (Mathf.Abs(d) < 0.0001f) return;
        foreach (var p in parts)
            if (p != null) p.localPosition += new Vector3(0f, d, 0f);
        lifted = h;
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;

        switch (kind)
        {
            case Kind.Rocket: TickRocket(k); break;
            case Kind.Particle: TickParticle(k); break;
            case Kind.HurtSpark: TickHurtSpark(k); break;
            case Kind.Flight: TickFlight(k); break;
        }
    }

    void TickRocket(float k)
    {
        Vector2 p = Vector2.Lerp(from, to, k);
        transform.position = new Vector3(p.x, p.y, 0f);
        trailTimer -= Time.deltaTime;
        if (trailTimer <= 0f)
        {
            trailTimer = 0.035f;
            CombatFx.Spark(p + Random.insideUnitCircle * 0.05f, Random.value < 0.5f ? color : new Color(1f, 0.9f, 0.6f), 0.09f, 0.25f);
        }
        if (k >= 1f) Finish();
    }

    void TickParticle(float k)
    {
        float e = 1f - (1f - k) * (1f - k);
        Vector2 p = Vector2.Lerp(from, to, e);
        p.y -= 0.25f * k * k;                       // огоньки чуть оседают
        transform.position = new Vector3(p.x, p.y, 0f);
        Color c = color;
        c.a = 1f - k * k;
        sr.color = c;
        if (k >= 1f) Destroy(gameObject);
    }

    void TickHurtSpark(float k)
    {
        Vector2 p = Vector2.Lerp(from, to, 1f - (1f - k) * (1f - k));
        transform.position = new Vector3(p.x, p.y, 0f);
        Color c = color;
        c.a = 1f - 0.6f * k;
        sr.color = c;

        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e) || e == skip) continue;
            float body = e.Unit != null ? e.Unit.bodyScale * 0.35f : 0.2f;
            if (Vector2.Distance(e.Position, p) > body + 0.12f) continue;
            CombatFx.Spark(p, color, 0.22f, 0.2f);
            e.TakeDamage(damage, null, by != null ? by : null);
            Destroy(gameObject);
            return;
        }
        if (k >= 1f) Destroy(gameObject);
    }

    void TickFlight(float k)
    {
        if (victim == null) { EndFlight(false); return; }          // врага убрали со сцены
        if (!EnemyAgent.Alive(victim)) { EndFlight(false); return; }

        // Каждые 0.25 с - следующий толчок. Врезался в стену (почти не сдвинулся) - падает сразу
        float segLen = duration / Segments;
        int want = Mathf.Min(Segments, Mathf.FloorToInt(t / segLen) + 1);
        while (segDone + 1 < want)
        {
            float moved = Vector2.Distance(victim.Position, segStart);
            if (moved < dist / Segments * 0.4f) { EndFlight(true); return; }
            segDone++;
            StartSegment();
            if (!EnemyAgent.Alive(victim)) { EndFlight(false); return; }
        }

        float h = 4f * k * (1f - k) * Pyrotechnics.SaluteArc;
        SetLift(h);
        Vector2 ground = victim.Position;
        if (shadow != null)
        {
            shadow.position = new Vector3(ground.x, ground.y - 0.3f, 0f);
            float s = Mathf.Lerp(0.6f, 0.35f, h / Mathf.Max(0.01f, Pyrotechnics.SaluteArc));
            shadow.localScale = new Vector3(s, s * 0.4f, 1f);
        }
        Vector2 top = ground + Vector2.up * h;
        trailTimer -= Time.deltaTime;
        if (trailTimer <= 0f)
        {
            trailTimer = 0.04f;
            CombatFx.Spark(top + Random.insideUnitCircle * 0.2f, Pyrotechnics.RandomColor(), 0.12f, 0.35f);
        }

        // На вершине - салют: картинка над врагом, урон всем рядом с ним
        if (!burst && k >= 0.5f)
        {
            burst = true;
            Pyrotechnics.Burst(top + Vector2.up * 0.4f, burstRadius, 18);
            Pyrotechnics.SaluteDamage(ground, burstDmg);
            if (!EnemyAgent.Alive(victim)) { EndFlight(false); return; }
        }

        if (k >= 1f) EndFlight(true);
    }

    void EndFlight(bool landed)
    {
        if (ended) return;
        ended = true;
        flying.Remove(victim);
        SetLift(0f);
        if (shadow != null) Destroy(shadow.gameObject);

        if (landed && EnemyAgent.Alive(victim))
        {
            Vector2 p = victim.Position;
            if (!burst)
            {
                // Врезался раньше вершины - салют рвётся прямо на нём
                Pyrotechnics.Burst(p, burstRadius, 14);
                Pyrotechnics.SaluteDamage(p, burstDmg);
            }
            // Врезался раньше времени - оглушение всё равно полное
            if (EnemyAgent.Alive(victim) && t < duration) victim.Knockback(Vector2.zero, 0f, stun);
            if (EnemyAgent.Alive(victim))
            {
                // Приземление: пыль и огонь
                CombatFx.Ring(p, 0.8f, new Color(0.6f, 0.5f, 0.4f, 0.7f), 0.35f);
                CombatFx.FirePool(p, 0.5f);
                EnemyAgent burning = victim;
                BurnEffect.Apply(victim, burnDps, burnTime, d => { if (EnemyAgent.Alive(burning)) burning.TakeDamage(d, null, null, true); });
            }
        }
        Destroy(gameObject);
    }

    void Finish()
    {
        System.Action cb = onEnd;
        onEnd = null;
        Destroy(gameObject);
        if (cb != null) cb();
    }

    void OnDestroy()
    {
        // Подстраховка: части врага не должны остаться висеть в воздухе
        if (kind == Kind.Flight)
        {
            flying.Remove(victim);
            SetLift(0f);
            if (shadow != null) Destroy(shadow.gameObject);
        }
    }
}
