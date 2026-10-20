using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Школа магии Хаоса (DevelopmentPath.IsChaos).
///  - Магическая оружейная делает «Ядро хаоса» (ModuleKind.ChaosCore, рецепт - TowerModules.ChaosCoreRecipe).
///    Готовое ядро лежит в Ратуше (EngineerYard.HallStock), как пачки снарядов.
///  - Маг стихий сам берёт лучшее ядро в своей казарме (BarracksController.LoadChaosCores).
///    Ядро хранит несколько бросков (по марке). Уволили / ушёл живым - ядро с остатком вернулось в Ратушу,
///    погиб - ядро потеряно (CitizenAgent_Chaos.cs).
///  - Бросок: раз в CooldownSeconds, если у цели рядом ещё хотя бы один враг, маг бросает ЧЁРНУЮ ДЫРУ:
///    урона почти нет, зато пару секунд она тянет всех врагов в радиусе к центру (дома - EnemyAgent.Knockback,
///    в набеге - защитников сдвигает по проходимой земле и вязнет в дыре). Тратит один бросок ядра.
/// Сила (урон, тяга) зависит от маны в воздухе (ManaManager.Factor). Все числа - в таблицах ниже.
/// </summary>
public static class ChaosMagic
{
    //                                           Мк.I   Мк.II  Мк.III
    static readonly int[] CoreCharges =        { 3,     4,     5 };       // бросков на ядро
    static readonly float[] HoleRadius =       { 2.6f,  3.0f,  3.4f };    // клеток
    static readonly float[] HoleSeconds =      { 2.2f,  2.5f,  2.8f };
    static readonly int[] ShelfByLevel =       { 2,     3,     4 };       // сколько ядер может лежать в городе (по уровню оружейной)

    public const float CooldownSeconds = 16f;   // между бросками одного мага
    public const int MinEnemies = 2;            // столько врагов должно быть рядом с целью
    public const float Dps = 4f;                // урон в секунду (× мана), магический
    public const float PullSpeed = 2.4f;        // клеток в секунду у лёгкого врага

    static int I(int mk) { return Mathf.Clamp(mk, 1, 3) - 1; }

    public static int Charges(int mk) { return CoreCharges[I(mk)]; }
    public static float Radius(int mk) { return HoleRadius[I(mk)]; }
    public static float Seconds(int mk) { return HoleSeconds[I(mk)]; }
    public static int Shelf(int armoryLevel) { return ShelfByLevel[Mathf.Clamp(armoryLevel, 1, 3) - 1]; }

    public static string Stats(int mk)
    {
        int i = I(mk);
        return $"бросков: {CoreCharges[i]}; чёрная дыра {HoleSeconds[i]:F1} с, радиус {HoleRadius[i]:F1}, стягивает врагов; " +
               $"урон {Dps:F0}/с, раз в {CooldownSeconds:F0} с";
    }

    // ---------- Дом ----------

    static int EnemiesNear(Vector2 at, float r)
    {
        int n = 0;
        foreach (var e in EnemyAgent.All)
            if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, at) <= r) n++;
        return n;
    }

    /// <summary>Маг дома бросает дыру в точку врага, если может. true - бросил (обычный удар не нужен).</summary>
    public static bool TryCastHome(CitizenAgent caster, Vector2 at)
    {
        if (caster == null || !caster.ChaosReady) return false;
        int mk = caster.ChaosCoreMk;
        if (EnemiesNear(at, Radius(mk)) < MinEnemies) return false;
        if (!caster.UseChaosCharge()) return false;
        Vector2 from = caster.transform.position;
        float mana = ManaManager.Factor;
        CombatFx.Shell(from, at, 0.45f, 0.4f, () => ChaosHole.SpawnHome(at, mk, mana, caster));
        return true;
    }

    // ---------- Набег ----------

    static int DefendersNear(Vector2 at, float r)
    {
        int n = 0;
        foreach (var u in RaidUnit.All)
            if (u != null && !u.Attacker && u.Alive && Vector2.Distance(u.Position, at) <= r) n++;
        return n;
    }

    /// <summary>
    /// Вызывается из RaidUnit.FightUnit вместо обычного удара налётчика.
    /// true - маг бросил чёрную дыру (ядро хаоса, перезарядка готова, у цели есть ещё защитники).
    /// </summary>
    public static bool TryCastRaid(RaidUnit caster, CitizenAgent source, Vector2 targetPos)
    {
        if (caster == null || source == null || source.IsDead || !source.ChaosReady) return false;
        int mk = source.ChaosCoreMk;
        if (DefendersNear(targetPos, Radius(mk)) < MinEnemies) return false;
        if (!source.UseChaosCharge()) return false;
        float mana = ManaManager.Factor;
        RaidUnit by = caster;
        Vector2 at = targetPos;
        CombatFx.Shell(caster.Position, at, 0.45f, 0.4f, () => ChaosHole.SpawnRaid(at, mk, mana, by));
        return true;
    }
}

/// <summary>
/// Чёрная дыра: тёмно-фиолетовый вращающийся диск на земле, внутрь летят искры.
/// Каждые 0.2 с тянет врагов в радиусе к центру и наносит немного магического урона.
/// Дома тянет EnemyAgent.Knockback (короткие толчки к центру - враг пока летит, не дерётся);
/// в набеге сдвигает защитников (RaidUnit) по проходимой земле и сильно их замедляет.
/// </summary>
public class ChaosHole : MonoBehaviour
{
    float radius, seconds, t, tickTimer, dps, pull;
    bool raid;
    CitizenAgent homeBy;
    RaidUnit raidBy;
    Vector2 center;

    SpriteRenderer glow, disk, core;
    readonly List<Transform> arms = new List<Transform>();
    readonly List<SpriteRenderer> motes = new List<SpriteRenderer>();
    readonly List<Vector2> moteFrom = new List<Vector2>();
    readonly List<float> moteT = new List<float>();
    float moteTimer;

    static readonly Color GlowCol = new Color(0.55f, 0.18f, 0.85f, 0.35f);
    static readonly Color DiskCol = new Color(0.10f, 0.02f, 0.16f, 0.85f);
    static readonly Color ArmCol = new Color(0.62f, 0.30f, 0.95f, 0.75f);

    static ChaosHole Make(Vector2 at, int mk, float mana)
    {
        GameObject go = new GameObject("ChaosHole");
        go.transform.position = new Vector3(at.x, at.y, 0f);
        ChaosHole h = go.AddComponent<ChaosHole>();
        h.center = at;
        h.radius = ChaosMagic.Radius(mk);
        h.seconds = ChaosMagic.Seconds(mk);
        h.dps = ChaosMagic.Dps * mana;
        h.pull = ChaosMagic.PullSpeed * Mathf.Clamp(mana, 0.6f, 1.25f);
        h.BuildLook();
        CombatFx.Ring(at, h.radius, new Color(0.6f, 0.25f, 0.95f, 0.7f), 0.45f);
        return h;
    }

    public static void SpawnHome(Vector2 at, int mk, float mana, CitizenAgent by)
    {
        ChaosHole h = Make(at, mk, mana);
        h.homeBy = by;
        WarManager.Notify("Маг стихий: чёрная дыра стягивает врагов!", true);
    }

    public static void SpawnRaid(Vector2 at, int mk, float mana, RaidUnit by)
    {
        ChaosHole h = Make(at, mk, mana);
        h.raid = true;
        h.raidBy = by;
    }

    SpriteRenderer Part(string n, Sprite s, Color c, int order)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(transform, false);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = s;
        r.color = c;
        r.sortingOrder = order;
        return r;
    }

    void BuildLook()
    {
        glow = Part("Glow", CombatFx.Circle(), GlowCol, -6);
        disk = Part("Disk", CombatFx.Circle(), DiskCol, -5);
        for (int i = 0; i < 3; i++)
        {
            SpriteRenderer a = Part("Arm", CombatFx.Square(), ArmCol, -4);
            arms.Add(a.transform);
        }
        core = Part("Core", CombatFx.Circle(), new Color(0f, 0f, 0f, 0.95f), -3);
        ApplyScale(0f);
    }

    // Появляется быстро, в конце схлопывается
    void ApplyScale(float k)
    {
        float grow = Mathf.Clamp01(k / 0.12f);
        float shrink = Mathf.Clamp01((1f - k) / 0.15f);
        float s = Mathf.Min(grow, shrink);
        float pulse = 1f + 0.06f * Mathf.Sin(t * 9f);
        float d = radius * 2f * s;
        glow.transform.localScale = new Vector3(d * pulse, d * pulse, 1f);
        disk.transform.localScale = new Vector3(d * 0.62f, d * 0.62f, 1f);
        core.transform.localScale = new Vector3(d * 0.2f * pulse, d * 0.2f * pulse, 1f);
        for (int i = 0; i < arms.Count; i++)
        {
            float ang = t * 260f + i * 120f;
            Transform a = arms[i];
            a.localRotation = Quaternion.Euler(0f, 0f, ang);
            Vector2 dir = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad));
            float len = radius * 0.75f * s;
            a.localPosition = new Vector3(dir.x * len * 0.5f, dir.y * len * 0.5f, 0f);
            a.localScale = new Vector3(len, 0.07f * s + 0.01f, 1f);
        }
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = seconds > 0f ? Mathf.Clamp01(t / seconds) : 1f;
        ApplyScale(k);
        UpdateMotes();

        tickTimer -= Time.deltaTime;
        if (tickTimer <= 0f && k < 1f)
        {
            tickTimer = 0.2f;
            if (raid) TickRaid(0.2f);
            else TickHome(0.2f);
        }
        if (raid) DragRaid();

        if (t >= seconds)
        {
            CombatFx.Ring(center, radius * 0.5f, new Color(0.75f, 0.45f, 1f, 0.8f), 0.3f);
            Destroy(gameObject);
        }
    }

    // ----- Искры, летящие внутрь -----
    void UpdateMotes()
    {
        moteTimer -= Time.deltaTime;
        if (moteTimer <= 0f && t < seconds - 0.2f)
        {
            moteTimer = 0.05f;
            SpriteRenderer m = Part("Mote", CombatFx.Circle(), Random.value < 0.5f ? new Color(0.8f, 0.55f, 1f, 0.9f) : new Color(0.4f, 0.15f, 0.7f, 0.9f), 22);
            float sz = Random.Range(0.08f, 0.16f);
            m.transform.localScale = new Vector3(sz, sz, 1f);
            Vector2 from = Random.insideUnitCircle.normalized * radius * Random.Range(0.8f, 1.1f);
            m.transform.localPosition = from;
            motes.Add(m);
            moteFrom.Add(from);
            moteT.Add(0f);
        }
        for (int i = motes.Count - 1; i >= 0; i--)
        {
            float mt = moteT[i] + Time.deltaTime / 0.5f;
            moteT[i] = mt;
            SpriteRenderer m = motes[i];
            if (mt >= 1f || m == null)
            {
                if (m != null) Destroy(m.gameObject);
                motes.RemoveAt(i); moteFrom.RemoveAt(i); moteT.RemoveAt(i);
                continue;
            }
            // по спирали к центру
            float ang = mt * 140f * Mathf.Deg2Rad;
            Vector2 f = moteFrom[i] * (1f - mt * mt);
            Vector2 p = new Vector2(f.x * Mathf.Cos(ang) - f.y * Mathf.Sin(ang), f.x * Mathf.Sin(ang) + f.y * Mathf.Cos(ang));
            m.transform.localPosition = p;
        }
    }

    // ----- Дом -----
    void TickHome(float dt)
    {
        CitizenAgent by = homeBy != null ? homeBy : null;
        foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
        {
            if (!EnemyAgent.Alive(e)) continue;
            Vector2 d = center - e.Position;
            float dist = d.magnitude;
            if (dist > radius + 0.3f) continue;
            e.TakeDamage(dps * dt, by, null, true);
            if (!EnemyAgent.Alive(e)) continue;
            if (dist < 0.3f) { e.Knockback(d, 0f, 0.25f); continue; }   // в центре - держит на месте
            float w = Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(e.KnockFactor));
            float step = Mathf.Min(dist - 0.25f, pull * dt * w);
            if (step > 0.05f) e.Knockback(d, step, 0.05f);
        }
        // Заслоны на колёсах тоже катятся в дыру (тяжёлые - медленнее лёгкого врага)
        FieldPost.PullTo(center, radius, pull * dt * WheeledPullMul, false);
    }

    const float WheeledPullMul = 0.4f;   // заслон на колёсах тянет так, как очень тяжёлого врага

    // ----- Набег -----
    readonly List<RaidUnit> caught = new List<RaidUnit>();

    void TickRaid(float dt)
    {
        RaidUnit by = raidBy != null ? raidBy : null;
        caught.Clear();
        foreach (var u in new List<RaidUnit>(RaidUnit.All))
        {
            if (u == null || u.Attacker || !u.Alive) continue;
            if (Vector2.Distance(u.Position, center) > radius + 0.3f) continue;
            u.TakeDamage(dps * dt, by, true);
            if (u == null || !u.Alive) continue;
            SlowEffect.Apply(u, 0.25f, 0.3f);   // вязнет в дыре и не уходит из неё своим ходом
            caught.Add(u);
        }
        FieldPost.PullTo(center, radius, pull * dt * WheeledPullMul, true);   // и наши заслоны на колёсах
    }

    // Сдвиг защитников к центру каждый кадр - только по проходимой земле (сквозь стены не тянет)
    void DragRaid()
    {
        RaidMap map = RaidMap.Instance;
        if (map == null) return;
        for (int i = caught.Count - 1; i >= 0; i--)
        {
            RaidUnit u = caught[i];
            if (u == null || !u.Alive) { caught.RemoveAt(i); continue; }
            Vector2 pos = u.Position;
            Vector2 d = center - pos;
            float dist = d.magnitude;
            if (dist < 0.3f) continue;
            float w = u.IsMounted ? 0.35f : 1f;
            Vector2 next = pos + d / dist * Mathf.Min(dist - 0.25f, pull * w * Time.deltaTime);
            if (!map.LineWalkable(pos, next)) continue;
            u.transform.position = new Vector3(next.x, next.y, u.transform.position.z);
        }
    }

    void OnDestroy()
    {
        foreach (var m in motes) if (m != null) Destroy(m.gameObject);
    }
}
