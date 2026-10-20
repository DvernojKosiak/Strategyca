using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Поставленная ловушка (см. Traps.cs - числа и рецепты, TrapManager - меню и размещение).
///  - Обычная: ставится дома в мирное время, взводится за пару секунд и ждёт врагов.
///  - Боевая: ставится в бою (дома во время войны или на карте набега). Ставится ДОЛГО (installSeconds),
///    и пока ставится - хрупкая: враг, прошедший вплотную, растаптывает её (вещь потеряна).
///    Пока рядом враги - установка стоит; в набеге её ставят бойцы, поэтому рядом должен быть свой.
///  - «Салют» (Пиротехник): жертву уносит ракетой - см. Pyrotechnics.
///  - Взведённая ловушка срабатывает, когда враг наступает на неё: бьёт всех в радиусе (до maxTargets),
///    держит на месте / замедляет / поджигает / отшвыривает. Срабатывания кончились - ловушка сломана.
///  - Дома боевые ловушки через некоторое время после конца войны снимают обратно в запас (с остатком срабатываний).
///    Обычные стоят, пока не сработают или их не уберут. На карте набега всё пропадает вместе с картой.
/// Враги дома (EnemyAgent) ловушки не видят и не обходят. Свои жители на них не наступают.
/// </summary>
public class Trap : MonoBehaviour
{
    public static readonly List<Trap> All = new List<Trap>();

    public const int MaxOrdinary = 12;      // обычных на карте города
    public const int MaxCombatHome = 8;     // боевых на карте города
    public const int MaxCombatRaid = 8;     // боевых на карте набега
    const float TriggerReach = 0.55f;       // враг ближе этого к центру - наступил
    const float WheeledReach = 1.2f;        // пружина достаёт заслон на колёсах чуть дальше своего радиуса
    const float WheeledPushMul = 0.5f;      // и относит его вдвое слабее лёгкого врага
    const float ThreatPause = 3f;           // враг ближе - установка боевой стоит
    const float TrampleReach = 1.4f;        // враг ближе - топчет недоставленную ловушку
    const float TrampleDps = 30f;           // урон по ловушке от каждого такого врага
    const float BuilderReach = 6f;          // в набеге: ставят свои бойцы не дальше этого
    const float PeaceReturn = 45f;          // дома: через столько секунд мира боевые снимают в запас

    public TrapKit Kit { get; private set; }
    public TrapStats S { get; private set; }
    public bool Raid { get; private set; }
    public bool Combat => Kit != null && Kit.combat;
    public float Progress { get; private set; }     // 0..1 - готовность (установка / взвод)
    public bool Armed => Progress >= 1f;
    public float Hp { get; private set; }
    public float MaxHp => S.installHp;
    public int Charges { get; private set; }
    public bool Alive => !dead;
    public Vector2 Position => transform.position;
    public bool Rearming => Time.time < rearmUntil;
    public string WaitReason { get; private set; } = "";

    bool dead;
    float rearmUntil, peace;
    readonly List<SpriteRenderer> sprites = new List<SpriteRenderer>();

    // ---------- Создание ----------

    public static Trap Create(TrapKit kit, Vector2 pos, bool raid)
    {
        GameObject go = new GameObject("Trap_" + kit.type);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        Trap t = go.AddComponent<Trap>();
        t.Kit = kit;
        t.S = kit.Stats;
        t.Raid = raid;
        t.Charges = kit.ChargesLeft;
        t.Hp = t.S.installHp;
        MakeLook(go.transform, kit, t.sprites);
        t.ApplyLook();
        return t;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    /// <summary>Рисунок ловушки (им же рисуется призрак при размещении).</summary>
    public static void MakeLook(Transform root, TrapKit kit, List<SpriteRenderer> list)
    {
        TrapRecipe r = Traps.RecipeOf(kit.type);
        SpriteRenderer Add(string n, Vector2 pos, Vector2 size, Color c, int order, bool circle = false, float rot = 0f)
        {
            GameObject go = new GameObject(n);
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = circle ? CombatFx.Circle() : CombatFx.Square();
            sr.color = c;
            sr.sortingOrder = order;
            list.Add(sr);
            return sr;
        }

        Color dark = new Color(r.color.r * 0.55f, r.color.g * 0.55f, r.color.b * 0.55f, 1f);
        switch (kit.type)
        {
            case TrapBase.Jaw:
                Add("Base", Vector2.zero, new Vector2(0.62f, 0.62f), dark, 3, true);
                Add("JawA", new Vector2(0f, 0.17f), new Vector2(0.56f, 0.09f), r.color, 4);
                Add("JawB", new Vector2(0f, -0.17f), new Vector2(0.56f, 0.09f), r.color, 4);
                for (int i = -2; i <= 2; i++)
                {
                    Add("Tooth", new Vector2(i * 0.11f, 0.1f), new Vector2(0.04f, 0.08f), Color.white * 0.85f, 5);
                    Add("Tooth", new Vector2(i * 0.11f, -0.1f), new Vector2(0.04f, 0.08f), Color.white * 0.85f, 5);
                }
                break;
            case TrapBase.Spikes:
                Add("Board", Vector2.zero, new Vector2(0.9f, 0.5f), r.color, 3);
                for (int x = -2; x <= 2; x++)
                    for (int y = -1; y <= 1; y++)
                        Add("Spike", new Vector2(x * 0.17f, y * 0.14f), new Vector2(0.06f, 0.06f), new Color(0.85f, 0.85f, 0.9f), 4, false, 45f);
                break;
            case TrapBase.Net:
                Add("Net", Vector2.zero, new Vector2(0.95f, 0.95f), new Color(r.color.r, r.color.g, r.color.b, 0.55f), 3, true);
                for (int i = -2; i <= 2; i++)
                {
                    Add("RopeH", new Vector2(0f, i * 0.16f), new Vector2(0.8f, 0.03f), dark, 4);
                    Add("RopeV", new Vector2(i * 0.16f, 0f), new Vector2(0.03f, 0.8f), dark, 4);
                }
                break;
            case TrapBase.Salute:
                // Ящик с пучком разноцветных ракет
                Add("Crate", Vector2.zero, new Vector2(0.7f, 0.56f), dark, 3);
                Add("Lid", new Vector2(0f, 0.2f), new Vector2(0.7f, 0.08f), r.color, 4);
                {
                    Color[] tubes = { new Color(1f, 0.3f, 0.3f), new Color(1f, 0.85f, 0.25f), new Color(0.4f, 0.75f, 1f), new Color(0.4f, 1f, 0.45f) };
                    for (int i = 0; i < 4; i++)
                    {
                        Add("Rocket", new Vector2(-0.21f + i * 0.14f, 0f), new Vector2(0.09f, 0.34f), tubes[i], 5);
                        Add("Tip", new Vector2(-0.21f + i * 0.14f, 0.19f), new Vector2(0.09f, 0.09f), Color.white * 0.9f, 6, true);
                    }
                }
                break;
            default:
                Add("Plate", Vector2.zero, new Vector2(0.72f, 0.72f), dark, 3);
                Add("Coil", Vector2.zero, new Vector2(0.48f, 0.48f), r.color, 4, true);
                Add("Coil2", Vector2.zero, new Vector2(0.28f, 0.28f), dark, 5, true);
                Add("Cap", Vector2.zero, new Vector2(0.14f, 0.14f), r.color, 6, true);
                break;
        }

        // Добавки видны цветными метками по углам
        float corner = 0.34f;
        int n = 0;
        foreach (var m in Traps.Mods)
        {
            if (!kit.Has(m.mod)) continue;
            Color mc;
            switch (m.mod)
            {
                case TrapMod.Resin: mc = new Color(1f, 0.5f, 0.1f); break;
                case TrapMod.Steel: mc = new Color(0.85f, 0.9f, 1f); break;
                case TrapMod.Slow: mc = new Color(0.5f, 0.8f, 1f); break;
                case TrapMod.Tar: mc = new Color(0.25f, 0.18f, 0.1f); break;
                case TrapMod.Shock: mc = new Color(0.35f, 0.35f, 0.4f); break;
                default: mc = new Color(1f, 0.8f, 0.4f); break;
            }
            Vector2 at = n == 0 ? new Vector2(corner, corner) : new Vector2(-corner, corner);
            Add("Mod", at, new Vector2(0.12f, 0.12f), mc, 7, true);
            n++;
        }
        if (kit.combat) Add("Combat", new Vector2(0f, -corner), new Vector2(0.2f, 0.07f), new Color(0.9f, 0.2f, 0.15f), 7);
    }

    void ApplyLook()
    {
        // Ставится - полупрозрачная; взведённая обычная - чуть прозрачнее (замаскирована); перевзвод - тусклее
        float a = !Armed ? 0.45f : (Rearming ? 0.55f : (Combat ? 0.95f : 0.8f));
        foreach (var sr in sprites)
        {
            if (sr == null) continue;
            Color c = sr.color;
            c.a = Mathf.Min(a, sr.name == "Net" ? 0.55f : 1f);
            sr.color = c;
        }
    }

    // ---------- Запросы ----------

    public static int CountOn(bool raid, bool combat)
    {
        int n = 0;
        foreach (var t in All) if (t != null && !t.dead && t.Raid == raid && t.Combat == combat) n++;
        return n;
    }

    public static int CountOn(bool raid)
    {
        int n = 0;
        foreach (var t in All) if (t != null && !t.dead && t.Raid == raid) n++;
        return n;
    }

    public static Trap At(Vector2 p, bool raid, float radius = 0.5f)
    {
        Trap best = null;
        float bestD = radius;
        foreach (var t in All)
        {
            if (t == null || t.dead || t.Raid != raid) continue;
            float d = Vector2.Distance(t.Position, p);
            if (d <= bestD) { bestD = d; best = t; }
        }
        return best;
    }

    public string InfoText
    {
        get
        {
            string t = $"<b>{Kit.Name}</b>\n";
            if (!Armed)
            {
                t += Combat ? $"ставится: {Progress * 100f:F0}%, прочность {Mathf.CeilToInt(Hp)}/{Mathf.CeilToInt(MaxHp)}"
                            : $"взводится: {Progress * 100f:F0}%";
                if (WaitReason != "") t += $"\n<color=#FFA060>{WaitReason}</color>";
                if (Combat) t += "\n<color=#FF8060>пока ставится — враг вплотную растопчет</color>";
            }
            else t += Rearming ? "<color=#FFD060>перевзводится...</color>" : "<color=#80FF80>взведена</color>";
            t += "\n" + Traps.StatsText(S, false, Charges);
            return t;
        }
    }

    // ---------- Жизнь ----------

    void Update()
    {
        if (dead || Kit == null) return;
        float dt = Time.deltaTime;

        if (!Armed) TickInstall(dt);
        else if (!Rearming) TryTrigger();

        if (Armed) ApplyLook();

        // Дома: боевую после войны снимают обратно в запас
        if (!Raid && Combat)
        {
            if (WarManager.IsWar) peace = 0f;
            else
            {
                peace += dt;
                if (peace >= PeaceReturn)
                {
                    WarManager.Notify($"{Kit.Name}: сняли — вернулась в запас Ратуши", true);
                    Dismantle(true);
                }
            }
        }
    }

    void TickInstall(float dt)
    {
        if (!Combat)
        {
            // Обычная - ставится в мирное время, просто взводится
            WaitReason = "";
            Progress = Mathf.Min(1f, Progress + dt / Traps.OrdinaryArmSeconds);
            if (Armed) ApplyLook();
            return;
        }

        // Враги вплотную топчут ловушку, пока её ставят
        int tramplers = CountFoesNear(TrampleReach);
        if (tramplers > 0)
        {
            Hp -= TrampleDps * tramplers * dt;
            if (Hp <= 0f) { Break(); return; }
        }

        if (AnyFoeNear(ThreatPause)) { WaitReason = "рядом враги — установка стоит"; return; }
        if (Raid && RaidUnit.NearestOf(true, Position, BuilderReach) == null) { WaitReason = "рядом нет своих бойцов — некому ставить"; return; }
        WaitReason = "";

        Progress = Mathf.Min(1f, Progress + dt / Mathf.Max(1f, S.installSeconds));
        if (Armed)
        {
            ApplyLook();
            CombatFx.Ring(Position, 0.6f, new Color(0.9f, 0.3f, 0.2f, 0.8f), 0.4f);
            WarManager.Notify($"{Kit.Name} взведена", true);
        }
    }

    bool AnyFoeNear(float r)
    {
        if (Raid) return RaidUnit.NearestOf(false, Position, r) != null;
        return EnemyAgent.AnyWithin(Position, r);
    }

    int CountFoesNear(float r)
    {
        int n = 0;
        Vector2 p = Position;
        if (Raid)
        {
            foreach (var u in RaidUnit.All)
                if (u != null && u.Alive && !u.Attacker && Vector2.Distance(u.Position, p) <= r) n++;
        }
        else
        {
            foreach (var e in EnemyAgent.All)
                if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, p) <= r) n++;
        }
        return n;
    }

    // ---------- Срабатывание ----------

    void TryTrigger()
    {
        Vector2 p = Position;
        bool stepped = Raid ? RaidUnit.NearestOf(false, p, TriggerReach) != null
                            : EnemyAgent.FindNearest(p, TriggerReach, e => !PyroFx.Airborne(e)) != null;   // летящего на «Салюте» не ловит
        if (!stepped) return;

        int hit = 0;
        if (Raid)
        {
            var victims = new List<RaidUnit>();
            foreach (var u in RaidUnit.All)
                if (u != null && u.Alive && !u.Attacker && Vector2.Distance(u.Position, p) <= S.radius) victims.Add(u);
            victims.Sort((a, b) => Vector2.Distance(a.Position, p).CompareTo(Vector2.Distance(b.Position, p)));
            for (int i = 0; i < victims.Count && hit < S.maxTargets; i++, hit++) HitRaid(victims[i]);
        }
        else
        {
            var victims = new List<EnemyAgent>();
            foreach (var e in EnemyAgent.All)
                if (EnemyAgent.Alive(e) && !PyroFx.Airborne(e) && Vector2.Distance(e.Position, p) <= S.radius) victims.Add(e);
            victims.Sort((a, b) => Vector2.Distance(a.Position, p).CompareTo(Vector2.Distance(b.Position, p)));
            for (int i = 0; i < victims.Count && hit < S.maxTargets; i++, hit++) HitHome(victims[i]);
        }
        if (hit == 0) return;

        // Пружина бьёт во все стороны: заслон на колёсах рядом отбрасывает от ловушки
        if (S.push > 0f) FieldPost.ShoveFrom(p, p, S.radius + WheeledReach, S.push * WheeledPushMul, Raid);

        PlayFx();
        Charges--;
        if (Charges <= 0) { Spent(); return; }
        rearmUntil = Time.time + S.rearm;
        ApplyLook();
    }

    // Враг в нашем городе
    void HitHome(EnemyAgent e)
    {
        float dmg = S.damage;
        if (dmg > 0f)
        {
            // Убитый ловушкой бросает награбленное прямо тут - город его забирает
            if (e.HasLoot && e.Hp <= dmg) { e.Carried.DepositToTown(); CombatFx.LootFly(e.Position, Position); }
            e.TakeDamage(dmg, null, null, true);   // щит от капкана под ногами не спасает
            if (!EnemyAgent.Alive(e)) return;
        }

        if (Kit.type == TrapBase.Salute)
        {
            // Салют: полёт, фейерверк и поджог на приземлении - всё в Pyrotechnics (огонь - после полёта)
            if (S.slow > 0f && S.slowTime > 0f) SlowEffect.Apply(e, S.slow, S.slowTime + S.root + Pyrotechnics.SaluteFlightSeconds);
            Pyrotechnics.LaunchSalute(e, S);
            return;
        }

        if (S.burnDps > 0f)
        {
            EnemyAgent victim = e;
            BurnEffect.Apply(e, S.burnDps, S.burnTime, d => { if (EnemyAgent.Alive(victim)) victim.TakeDamage(d, null, null, true); });
        }
        if (S.slow > 0f && S.slowTime > 0f) SlowEffect.Apply(e, S.slow, S.slowTime + S.root);

        if (S.push > 0f)
        {
            Vector2 dir = e.Position - Position;
            e.Knockback(dir, S.push * e.KnockFactor, S.root, 10f);
        }
        else if (S.root > 0f)
        {
            // Сеть тяжёлых держит меньше; капкан держит всех одинаково
            float root = Kit.type == TrapBase.Net ? S.root * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(e.KnockFactor)) : S.root;
            e.Knockback(Vector2.zero, 0f, root);
        }
    }

    // Защитник на карте набега (отбросить его нельзя - вместо этого оглушение)
    void HitRaid(RaidUnit u)
    {
        if (S.damage > 0f)
        {
            u.TakeDamage(S.damage, null, true);
            if (u == null || !u.Alive) return;
        }
        if (Kit.type == TrapBase.Salute) Pyrotechnics.SaluteRaid(u, S);   // унести защитника нельзя - ракета рвётся над ним
        if (S.burnDps > 0f)
        {
            RaidUnit victim = u;
            BurnEffect.Apply(u, S.burnDps, S.burnTime, d => { if (victim != null && victim.Alive) victim.TakeDamage(d, null, true); });
        }

        float root = S.push > 0f ? S.root + 0.7f : S.root;
        if (root > 0f)
        {
            // Держит на месте почти полной остановкой; замедление - когда отпустит
            SlowEffect.Apply(u, 0.05f, root);
            if (S.slow > 0f && S.slowTime > 0f && TrapManager.Instance != null)
            {
                RaidUnit victim = u;
                float f = S.slow, t = S.slowTime;
                TrapManager.Instance.StartCoroutine(After(root, () => { if (victim != null && victim.Alive) SlowEffect.Apply(victim, f, t); }));
            }
        }
        else if (S.slow > 0f && S.slowTime > 0f) SlowEffect.Apply(u, S.slow, S.slowTime);
    }

    static IEnumerator After(float delay, System.Action a)
    {
        yield return new WaitForSeconds(delay);
        if (a != null) a();
    }

    void PlayFx()
    {
        Vector2 c = Position;
        Color col = Traps.RecipeOf(Kit.type).color;
        CombatFx.Ring(c, Mathf.Max(0.6f, S.radius), new Color(col.r, col.g, col.b, 0.85f), 0.45f);
        if (S.burnDps > 0f)
            for (int i = 0; i < 6; i++) CombatFx.Spark(c + Random.insideUnitCircle * S.radius * 0.7f, new Color(1f, 0.55f, 0.15f), 0.2f, 0.5f);
        if (S.push > 0f) CombatFx.Flash(c);
        if (Kit.type == TrapBase.Salute)
            for (int i = 0; i < 8; i++) CombatFx.Spark(c + Random.insideUnitCircle * 0.5f, Pyrotechnics.RandomColor(), 0.16f, 0.45f);
        if (Kit.type == TrapBase.Spikes || Kit.type == TrapBase.Jaw)
            for (int i = 0; i < 4; i++) CombatFx.Spark(c + Random.insideUnitCircle * 0.4f, new Color(0.9f, 0.9f, 1f), 0.15f, 0.3f);
    }

    // ---------- Конец ----------

    // Срабатывания кончились
    void Spent()
    {
        if (dead) return;
        dead = true;
        CombatFx.Flash(Position);
        Destroy(gameObject);
    }

    // Растоптали, пока ставили
    void Break()
    {
        if (dead) return;
        dead = true;
        CombatFx.Death(Position);
        WarManager.Notify($"Враги растоптали ловушку, пока её ставили: {Kit.Name}", false);
        Destroy(gameObject);
    }

    /// <summary>Убрать ловушку. toStock - вернуть вещь в запас (в набеге - в набор похода) с остатком срабатываний.</summary>
    public void Dismantle(bool toStock)
    {
        if (dead) return;
        dead = true;
        if (toStock)
        {
            TrapKit k = Kit.Clone();
            k.charges = Charges;
            if (Raid) TrapStock.ReturnToRaidKit(k);
            else TrapStock.Add(k);
        }
        Destroy(gameObject);
    }

    /// <summary>Конец набега: ловушки на поле боя пропадают вместе с ним.</summary>
    public static void ClearRaid()
    {
        foreach (var t in new List<Trap>(All))
        {
            if (t == null || !t.Raid) continue;
            t.dead = true;
            Destroy(t.gameObject);
        }
    }
}
