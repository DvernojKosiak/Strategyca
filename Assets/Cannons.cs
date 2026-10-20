using UnityEngine;
using System.Collections.Generic;

/// <summary>Один выстрел пушки: всё, что нужно ядру и осколкам (наземная пушка, полевая пушка, пушка соседей).</summary>
public struct CannonShot
{
    public float damage;          // прямое попадание
    public float buildingMul;     // во сколько раз сильнее по зданиям
    public float barrierMul;      // ... и ещё во столько - по стенам, воротам, заслонам
    public int fragments;         // сколько осколков разлетается
    public float fragDamage;
    public float fragRange;       // насколько далеко летят осколки (клеток)
    public float fragSpread;      // полуугол разлёта вперёд, градусы
    public bool armorPiercing;    // бронебойное ядро: пробивает заслон / стену, осколки летят сквозь укрепления
    public bool lob;              // выстрел с подъёма: ядро идёт навесом поверх стен
    public ModuleKind shell;      // магический снаряд (None - обычное ядро)
    public int shellMk;
    public float mana;            // сила маны в момент выстрела (для магических снарядов)

    public bool Magic => shell == ModuleKind.FireShells || shell == ModuleKind.FrostShells || shell == ModuleKind.ArcaneShells;
}

/// <summary>
/// Подъёмник пушки (модуль Инженерного двора): раз в несколько секунд поднимает пушку на время -
/// поднятая бьёт навесом через стены и постройки и чуть дальше. Поднимается сам, когда цель есть, а прямого
/// выстрела нет (стена, дом). Работает и на наземной пушке, и на полевой.
/// </summary>
public class CannonLiftState
{
    public int mk;                // 0 - подъёмника нет
    float upUntil = -99f, readyAt;

    public bool Has => mk > 0;
    public bool Up => mk > 0 && Time.time < upUntil;
    public bool Ready => mk > 0 && Time.time >= readyAt;
    public float UpLeft => Mathf.Max(0f, upUntil - Time.time);
    public float ReadyIn => Mathf.Max(0f, readyAt - Time.time);

    /// <summary>Поднять, если готов. true - пушка поднята (или уже была поднята).</summary>
    public bool TryRaise()
    {
        if (Up) return true;
        if (!Ready) return false;
        upUntil = Time.time + TowerModules.LiftSeconds(mk);
        readyAt = upUntil + TowerModules.LiftCooldown(mk);
        return true;
    }

    public void Set(int newMk) { mk = Mathf.Clamp(newMk, 0, 3); upUntil = -99f; readyAt = Time.time + 1f; }

    public string StateText
    {
        get
        {
            if (!Has) return "";
            if (Up) return $"<color=#FFD060>поднята ещё {UpLeft:F0} с</color>";
            return Ready ? "подъём готов" : $"подъём через {ReadyIn:F0} с";
        }
    }
}

/// <summary>
/// Баллистика пушек. Ядро летит ПРЯМО: стена, дом или заслон на пути принимает удар на себя
/// (так полевая пушка и пробивает стены). Поднятая пушка бьёт навесом - поверх всего.
/// В точке попадания ядро разлетается на осколки: они летят вперёд веером и ранят первого, кого заденут;
/// стены и дома осколки останавливают (бронебойные - нет).
/// Магические снаряды: огненный (взрыв и поджог), ледяной (сильное замедление), чародейский
/// (магия: щит и укрытие не спасают, осколки сами ищут врагов).
/// Все числа выстрела - в CannonShot (их собирают пушки), эффекты снарядов - в MagicShells.
/// </summary>
public static class CannonBallistics
{
    const float BallSpeed = 16f;        // клеток в секунду
    const float FragStep = 0.2f;
    const float HitRadius = 0.32f;      // + полразмера тела цели

    // ================= Дом (наша карта) =================

    /// <summary>Свободна ли прямая для ядра от from до to (стены, дома, поднятые мосты, заслоны - мешают). own - сама пушка-здание.</summary>
    public static bool HomeLineClear(Vector2 from, Vector2 to, BuildingInstance own)
    {
        return HomeFirstBlock(from, to, own, out _) < 0f;
    }

    // Расстояние до первой занятой клетки на прямой (-1 - свободно). Вода выстрелу не мешает.
    static float HomeFirstBlock(Vector2 from, Vector2 to, BuildingInstance own, out Vector2 at)
    {
        at = to;
        var nav = GridNavigation.Instance;
        if (nav == null) return -1f;
        bool oldW = GridNavigation.WaderQuery, oldH = GridNavigation.HostileQuery;
        GridNavigation.WaderQuery = true;     // вода - не преграда
        GridNavigation.HostileQuery = false;  // ворота «только для своих» открыты и для ядра
        try
        {
            float len = Vector2.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
            Vector2Int last = nav.WorldToCell(to);
            for (int i = 1; i < steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, i / (float)steps);
                if (own != null && own.DistanceTo(p) <= 0.05f) continue;   // ещё в своей клетке
                Vector2Int c = nav.WorldToCell(p);
                if (c == last) break;                                       // клетка цели
                if (!nav.InBounds(c)) continue;
                if (nav.IsBlocked(c)) { at = p; return Vector2.Distance(from, p); }
            }
        }
        finally
        {
            GridNavigation.WaderQuery = oldW;
            GridNavigation.HostileQuery = oldH;
        }
        return -1f;
    }

    static bool HomeCellBlocked(Vector2 p)
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return false;
        bool oldW = GridNavigation.WaderQuery, oldH = GridNavigation.HostileQuery;
        GridNavigation.WaderQuery = true;
        GridNavigation.HostileQuery = false;
        try
        {
            Vector2Int c = nav.WorldToCell(p);
            return nav.InBounds(c) && nav.IsBlocked(c);
        }
        finally
        {
            GridNavigation.WaderQuery = oldW;
            GridNavigation.HostileQuery = oldH;
        }
    }

    /// <summary>
    /// Выстрел по врагу у нас дома. Наша пушка стреляет только когда прямая свободна или она поднята,
    /// поэтому здесь ядро всегда долетает до цели. byUnit - полевая пушка, byBuilding - наземная.
    /// </summary>
    public static void FireHome(Vector2 from, EnemyAgent target, CannonShot s, CitizenAgent byUnit, BuildingInstance byBuilding)
    {
        if (!EnemyAgent.Alive(target)) return;
        Vector2 aim = target.Position;
        Vector2 dir = (aim - from).sqrMagnitude > 0.0001f ? (aim - from).normalized : Vector2.right;
        float dur = Mathf.Clamp(Vector2.Distance(from, aim) / BallSpeed, 0.18f, 0.9f) * (s.lob ? 1.6f : 1f);
        CannonFx.Muzzle(from, dir);
        CitizenAgent unitRef = byUnit;
        BuildingInstance bldRef = byBuilding;

        CannonFx.Ball(from, aim, dur, s.lob, MagicShells.ShellColor(s.shell), () =>
        {
            CitizenAgent by = unitRef != null ? unitRef : null;           // пушку могли убить, пока летело ядро
            BuildingInstance bb = bldRef != null ? bldRef : null;
            Vector2 hit = EnemyAgent.Alive(target) && Vector2.Distance(target.Position, aim) < 1.0f ? target.Position : aim;
            bool magical = s.shell == ModuleKind.ArcaneShells || s.armorPiercing;   // бронебойное не держат ни щит, ни частокол лагеря

            // Прямое попадание (и кто стоит вплотную)
            foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
            {
                if (!EnemyAgent.Alive(e)) continue;
                float d = Vector2.Distance(e.Position, hit);
                if (d > HitRadius + Body(e) + 0.15f) continue;
                float k = e == target ? 1f : 0.5f;
                e.TakeDamage(s.damage * k, by, bb, magical);
            }
            CannonFx.Impact(hit, s.shell);
            MagicShells.ApplyHome(hit, s, by, bb);

            // Осколки
            foreach (var fd in FragDirs(s, dir, hit, true, false))
            {
                Vector2 fdir = fd;
                Vector2 end = hit + fdir * s.fragRange;
                Vector2 p = hit;
                EnemyAgent victim = null;
                float travelled = 0f;
                while (travelled < s.fragRange)
                {
                    travelled += FragStep;
                    p = hit + fdir * travelled;
                    if (!s.armorPiercing && HomeCellBlocked(p)) { end = p; break; }
                    victim = FirstEnemyNear(p, target, hit);
                    if (victim != null) { end = p; break; }
                }
                CannonFx.Frag(hit, end, s.shell);
                if (victim == null) continue;
                float dmg = s.fragDamage * Mathf.Lerp(1f, 0.6f, travelled / Mathf.Max(0.1f, s.fragRange));
                victim.TakeDamage(dmg, by, bb, magical);
                MagicShells.ApplyFragHome(victim, s, by, bb);
            }
        });
    }

    static EnemyAgent FirstEnemyNear(Vector2 p, EnemyAgent skip, Vector2 hit)
    {
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e)) continue;
            if (e == skip && Vector2.Distance(e.Position, hit) < 1.2f) continue;   // цель уже получила ядро
            if (Vector2.Distance(e.Position, p) <= HitRadius + Body(e)) return e;
        }
        return null;
    }

    static float Body(EnemyAgent e) { return e != null && e.Unit != null ? e.Unit.bodyScale * 0.3f : 0.15f; }

    /// <summary>Направления осколков: веер вперёд. Чародейские сами доворачивают на ближайшего врага.</summary>
    static List<Vector2> FragDirs(CannonShot s, Vector2 dir, Vector2 hit, bool home, bool hitAttackers)
    {
        var list = new List<Vector2>();
        int n = Mathf.Max(0, s.fragments + (s.shell == ModuleKind.ArcaneShells ? 2 : 0));
        float baseAng = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0.5f : i / (float)(n - 1);
            float a = baseAng + Mathf.Lerp(-s.fragSpread, s.fragSpread, t) + Random.Range(-7f, 7f);
            Vector2 d = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
            if (s.shell == ModuleKind.ArcaneShells)
            {
                Vector2? seek = home ? NearestEnemyInCone(hit, d, s.fragRange) : NearestRaidInCone(hit, d, s.fragRange, hitAttackers);
                if (seek.HasValue) d = seek.Value;
            }
            list.Add(d);
        }
        return list;
    }

    static Vector2? NearestEnemyInCone(Vector2 from, Vector2 dir, float range)
    {
        float best = float.MaxValue;
        Vector2? res = null;
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e)) continue;
            Vector2 to = e.Position - from;
            float d = to.magnitude;
            if (d < 0.2f || d > range || Vector2.Angle(dir, to) > 35f) continue;
            if (d < best) { best = d; res = to / d; }
        }
        return res;
    }

    // ================= Карта набега =================

    /// <summary>Первое здание, которое стоит на прямой from→to (кроме ignoreA/ignoreB). null - прямая свободна.</summary>
    public static RaidBuilding RaidFirstBlock(Vector2 from, Vector2 to, RaidBuilding ignoreA, RaidBuilding ignoreB, out Vector2 at)
    {
        at = to;
        RaidMap map = RaidMap.Instance;
        if (map == null) return null;
        float len = Vector2.Distance(from, to);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
        for (int i = 1; i < steps; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, i / (float)steps);
            Vector2Int c = map.WorldToCell(p);
            if (!map.InBounds(c) || !map.IsBlocked(c) || map.IsTree(c)) continue;   // деревья ядру не помеха
            RaidBuilding b = RaidBuildingAt(p);
            if (b == null || b == ignoreA || b == ignoreB) continue;
            at = p;
            return b;
        }
        return null;
    }

    static RaidBuilding RaidBuildingAt(Vector2 p)
    {
        foreach (var b in RaidBuilding.All)
            if (b != null && !b.IsRuined && b.Blocks && b.Contains(p)) return b;
        return null;
    }

    /// <summary>
    /// Выстрел на карте набега. shooterAttacker - стреляют наши (по защитникам и зданиям) или пушка соседей (по нашим).
    /// Ядро бьёт первое, что стоит на прямой (кроме навесного выстрела). Бронебойное прошивает одну стену / заслон.
    /// targetUnit / targetBuilding - во что целились (одно из двух).
    /// </summary>
    public static void FireRaid(Vector2 from, Vector2 aim, RaidUnit targetUnit, RaidBuilding targetBuilding,
                                CannonShot s, RaidUnit byUnit, RaidBuilding byBuilding, bool shooterAttacker)
    {
        Vector2 dir = (aim - from).sqrMagnitude > 0.0001f ? (aim - from).normalized : Vector2.right;
        RaidBuilding pierced = null;
        Vector2 impact = aim;
        RaidBuilding hitBuilding = targetBuilding;

        if (!s.lob)
        {
            RaidBuilding block = RaidFirstBlock(from, aim, byBuilding, targetBuilding, out Vector2 at);
            if (block != null)
            {
                if (s.armorPiercing && block.IsBarrier)
                {
                    pierced = block;   // прошьёт и полетит дальше
                    RaidBuilding second = RaidFirstBlock(at + dir * 0.6f, aim, block, targetBuilding, out Vector2 at2);
                    if (second != null) { impact = at2; hitBuilding = second; }
                }
                else { impact = at; hitBuilding = block; }
            }
        }

        float dur = Mathf.Clamp(Vector2.Distance(from, impact) / BallSpeed, 0.18f, 0.9f) * (s.lob ? 1.6f : 1f);
        CannonFx.Muzzle(from, dir);
        RaidUnit byRef = byUnit;
        RaidBuilding piercedRef = pierced, hitRef = hitBuilding;
        bool aimedAtUnit = hitBuilding == null && targetUnit != null;
        Vector2 imp = impact;

        CannonFx.Ball(from, impact, dur, s.lob, MagicShells.ShellColor(s.shell), () =>
        {
            if (RaidMap.Instance == null) return;   // бой уже кончился
            RaidUnit by = byRef != null && byRef.Alive ? byRef : null;
            bool magical = s.shell == ModuleKind.ArcaneShells;
            Vector2 hit = imp;

            // Пробитое укрепление
            if (piercedRef != null && !piercedRef.IsRuined)
            {
                piercedRef.TakeDamage(s.damage * s.buildingMul * s.barrierMul, by);
                CannonFx.Impact(piercedRef.ClosestPoint(hit), ModuleKind.None);
            }

            if (hitRef != null && !hitRef.IsRuined)
            {
                float mul = s.buildingMul * (hitRef.IsBarrier ? s.barrierMul : 1f);
                hitRef.TakeDamage(s.damage * mul, by);
            }
            else if (aimedAtUnit)
            {
                if (targetUnit != null && targetUnit.Alive && Vector2.Distance(targetUnit.Position, hit) < 1.0f) hit = targetUnit.Position;
                foreach (var u in new List<RaidUnit>(RaidUnit.All))
                {
                    if (u == null || !u.Alive || u.Attacker == shooterAttacker) continue;
                    float d = Vector2.Distance(u.Position, hit);
                    if (d > HitRadius + 0.3f) continue;
                    float k = u == targetUnit ? 1f : 0.5f;
                    u.TakeDamage(s.damage * k, by, magical || s.armorPiercing, from);
                }
            }
            CannonFx.Impact(hit, s.shell);
            MagicShells.ApplyRaid(hit, s, by, shooterAttacker);

            foreach (var fd in FragDirs(s, dir, hit, false, !shooterAttacker))
            {
                Vector2 end = hit + fd * s.fragRange;
                RaidUnit victim = null;
                float travelled = 0f;
                while (travelled < s.fragRange)
                {
                    travelled += FragStep;
                    Vector2 p = hit + fd * travelled;
                    RaidBuilding wall = RaidBuildingAt(p);
                    if (wall != null && wall != hitRef && !(s.armorPiercing && wall.IsBarrier))
                    {
                        wall.TakeDamage(s.fragDamage * 0.3f, by);
                        end = p;
                        break;
                    }
                    victim = FirstRaidNear(p, shooterAttacker, aimedAtUnit ? targetUnit : null, hit);
                    if (victim != null) { end = p; break; }
                }
                CannonFx.Frag(hit, end, s.shell);
                if (victim == null) continue;
                float dmg = s.fragDamage * Mathf.Lerp(1f, 0.6f, travelled / Mathf.Max(0.1f, s.fragRange));
                // Осколки летят от точки взрыва: укрытие считается от неё, бронебойным - не спасает
                victim.TakeDamage(dmg, by, magical || s.armorPiercing, hit - fd * 0.5f);
                MagicShells.ApplyFragRaid(victim, s);
            }
        });
    }

    static RaidUnit FirstRaidNear(Vector2 p, bool shooterAttacker, RaidUnit skip, Vector2 hit)
    {
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Alive || u.Attacker == shooterAttacker) continue;
            if (u == skip && Vector2.Distance(u.Position, hit) < 1.2f) continue;
            if (Vector2.Distance(u.Position, p) <= HitRadius + 0.2f) return u;
        }
        return null;
    }

    static Vector2? NearestRaidInCone(Vector2 from, Vector2 dir, float range, bool attackers)
    {
        float best = float.MaxValue;
        Vector2? res = null;
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Alive || u.Attacker != attackers) continue;
            Vector2 to = u.Position - from;
            float d = to.magnitude;
            if (d < 0.2f || d > range || Vector2.Angle(dir, to) > 35f) continue;
            if (d < best) { best = d; res = to / d; }
        }
        return res;
    }
}

/// <summary>
/// Магические снаряды для пушек (делает Магическая оружейная, путь Магии). Пачка снарядов ставится на пушку,
/// как модуль: и на наземную пушку, и на полевую (полевые заряжаются сами, вид - в казарме «Снаряж.»).
/// Каждый выстрел тратит один снаряд. Сила зависит от марки пачки и от маны в воздухе.
///  - Огненный: взрыв огня на месте попадания, все рядом горят; осколки поджигают.
///  - Ледяной: всех у места попадания и раненых осколками надолго замедляет.
///  - Чародейский (оружейная Ур.2): магия - щит и укрытие не спасают; осколков больше, и они сами летят во врагов.
/// </summary>
public static class MagicShells
{
    public static readonly ModuleKind[] Kinds = { ModuleKind.FireShells, ModuleKind.FrostShells, ModuleKind.ArcaneShells };

    public static bool IsShell(ModuleKind k) { return k == ModuleKind.FireShells || k == ModuleKind.FrostShells || k == ModuleKind.ArcaneShells; }

    static int I(int mk) { return Mathf.Clamp(mk, 1, 3) - 1; }

    //                                         Мк.I   Мк.II  Мк.III
    static readonly int[] PackCharges =      { 8,     12,    16 };
    static readonly float[] FireRadius =     { 1.3f,  1.5f,  1.7f };
    static readonly float[] FireDamage =     { 16f,   24f,   32f };
    static readonly float[] FireBurnDps =    { 4f,    6f,    8f };
    static readonly float[] FrostRadius =    { 1.5f,  1.8f,  2.1f };
    static readonly float[] FrostSlow =      { 0.5f,  0.42f, 0.35f };   // доля скорости
    static readonly float[] FrostSeconds =   { 3f,    4f,    5f };
    static readonly float[] ArcaneBonus =    { 0.15f, 0.25f, 0.35f };   // прибавка к урону ядра и осколков
    const float FireBurnSeconds = 3f;

    public static int Charges(int mk) { return PackCharges[I(mk)]; }

    /// <summary>Урон ядра с этим снарядом (чародейский - сильнее).</summary>
    public static float DamageMul(ModuleKind k, int mk, float mana)
    {
        return k == ModuleKind.ArcaneShells ? 1f + ArcaneBonus[I(mk)] * mana : 1f;
    }

    public static Color ShellColor(ModuleKind k)
    {
        switch (k)
        {
            case ModuleKind.FireShells: return new Color(1f, 0.45f, 0.12f);
            case ModuleKind.FrostShells: return new Color(0.55f, 0.85f, 1f);
            case ModuleKind.ArcaneShells: return new Color(0.75f, 0.45f, 1f);
            default: return new Color(0.18f, 0.18f, 0.2f);
        }
    }

    public static string Stats(ModuleKind k, int mk)
    {
        int i = I(mk);
        switch (k)
        {
            case ModuleKind.FireShells: return $"{PackCharges[i]} выстр.: огонь {FireDamage[i]:F0} в радиусе {FireRadius[i]:F1}, поджог {FireBurnDps[i]:F0}/с";
            case ModuleKind.FrostShells: return $"{PackCharges[i]} выстр.: замедление до {FrostSlow[i] * 100f:F0}% скорости на {FrostSeconds[i]:F0} с (радиус {FrostRadius[i]:F1})";
            case ModuleKind.ArcaneShells: return $"{PackCharges[i]} выстр.: магия (щит и укрытие не спасают), урон +{ArcaneBonus[i] * 100f:F0}%, осколки сами ищут врагов";
            default: return "";
        }
    }

    // ----- Дом -----

    public static void ApplyHome(Vector2 at, CannonShot s, CitizenAgent by, BuildingInstance bb)
    {
        int i = I(s.shellMk);
        switch (s.shell)
        {
            case ModuleKind.FireShells:
            {
                float r = FireRadius[i], dmg = FireDamage[i] * s.mana, dps = FireBurnDps[i] * s.mana;
                CombatFx.FirePool(at, r);
                foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
                {
                    if (!EnemyAgent.Alive(e) || Vector2.Distance(e.Position, at) > r) continue;
                    e.TakeDamage(dmg, by, bb, true);
                    Burn(e, dps, by, bb);
                }
                break;
            }
            case ModuleKind.FrostShells:
            {
                float r = FrostRadius[i];
                CombatFx.Ring(at, r, new Color(0.6f, 0.9f, 1f, 0.8f), 0.6f);
                foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
                    if (EnemyAgent.Alive(e) && Vector2.Distance(e.Position, at) <= r) SlowEffect.Apply(e, FrostSlow[i], FrostSeconds[i]);
                break;
            }
        }
    }

    public static void ApplyFragHome(EnemyAgent e, CannonShot s, CitizenAgent by, BuildingInstance bb)
    {
        if (!EnemyAgent.Alive(e)) return;
        int i = I(s.shellMk);
        if (s.shell == ModuleKind.FireShells) Burn(e, FireBurnDps[i] * 0.5f * s.mana, by, bb);
        else if (s.shell == ModuleKind.FrostShells) SlowEffect.Apply(e, FrostSlow[i], FrostSeconds[i] * 0.7f);
    }

    static void Burn(EnemyAgent e, float dps, CitizenAgent by, BuildingInstance bb)
    {
        if (!EnemyAgent.Alive(e) || dps <= 0f) return;
        EnemyAgent victim = e;
        BurnEffect.Apply(e, dps, FireBurnSeconds, d =>
        {
            if (!EnemyAgent.Alive(victim)) return;
            CitizenAgent k = by != null ? by : null;
            BuildingInstance kb = bb != null ? bb : null;
            victim.TakeDamage(d, k, kb, true);
        });
    }

    // ----- Набег -----

    public static void ApplyRaid(Vector2 at, CannonShot s, RaidUnit by, bool shooterAttacker)
    {
        int i = I(s.shellMk);
        switch (s.shell)
        {
            case ModuleKind.FireShells:
            {
                float r = FireRadius[i], dmg = FireDamage[i] * s.mana, dps = FireBurnDps[i] * s.mana;
                CombatFx.FirePool(at, r);
                foreach (var u in new List<RaidUnit>(RaidUnit.All))
                {
                    if (u == null || !u.Alive || u.Attacker == shooterAttacker || Vector2.Distance(u.Position, at) > r) continue;
                    u.TakeDamage(dmg, by, true);
                    BurnRaid(u, dps);
                }
                break;
            }
            case ModuleKind.FrostShells:
            {
                float r = FrostRadius[i];
                CombatFx.Ring(at, r, new Color(0.6f, 0.9f, 1f, 0.8f), 0.6f);
                foreach (var u in new List<RaidUnit>(RaidUnit.All))
                    if (u != null && u.Alive && u.Attacker != shooterAttacker && Vector2.Distance(u.Position, at) <= r)
                        SlowEffect.Apply(u, FrostSlow[i], FrostSeconds[i]);
                break;
            }
        }
    }

    public static void ApplyFragRaid(RaidUnit u, CannonShot s)
    {
        if (u == null || !u.Alive) return;
        int i = I(s.shellMk);
        if (s.shell == ModuleKind.FireShells) BurnRaid(u, FireBurnDps[i] * 0.5f * s.mana);
        else if (s.shell == ModuleKind.FrostShells) SlowEffect.Apply(u, FrostSlow[i], FrostSeconds[i] * 0.7f);
    }

    static void BurnRaid(RaidUnit u, float dps)
    {
        if (u == null || !u.Alive || dps <= 0f) return;
        RaidUnit victim = u;
        BurnEffect.Apply(u, dps, FireBurnSeconds, d => { if (victim != null && victim.Alive) victim.TakeDamage(d, null, true); });
    }

    // ----- Запас готовых пачек (лежат в Ратуше, как модули) -----

    public static int Packs(ModuleKind k) { return EngineerYard.CountKits(k); }

    public static int TotalPacks()
    {
        int n = 0;
        foreach (var k in Kinds) n += EngineerYard.CountKits(k);
        return n;
    }

    /// <summary>Лучшая готовая пачка любого вида (для полевых пушек в казарме). false - нет ни одной.</summary>
    public static bool BestPack(out ModuleKit kit) { return BestPack(ModuleKind.None, out kit); }

    /// <summary>Лучшая готовая пачка этого вида (None - любого). false - нет ни одной.</summary>
    public static bool BestPack(ModuleKind kind, out ModuleKit kit)
    {
        kit = new ModuleKit();
        bool found = false;
        foreach (var k in EngineerYard.AllKits())
        {
            if (!IsShell(k.kind) || (kind != ModuleKind.None && k.kind != kind)) continue;
            if (!found || k.mk > kit.mk) { kit = k; found = true; }
        }
        return found;
    }
}

/// <summary>Эффекты пушек: ядро (прямо или навесом), вспышка выстрела, удар, осколки.</summary>
public class CannonFx : MonoBehaviour
{
    enum Kind { Ball, Flash, Frag, Smoke }

    Kind kind;
    Vector3 from, to;
    float t, duration;
    bool lob;
    System.Action onLand;
    SpriteRenderer sr;
    Color baseColor;
    Vector3 baseScale;

    static CannonFx Make(string n, Kind k, Vector2 pos, float size, Color c, float dur, int order, bool circle)
    {
        GameObject go = new GameObject(n);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size, size, 1f);
        CannonFx fx = go.AddComponent<CannonFx>();
        fx.kind = k;
        fx.duration = Mathf.Max(0.02f, dur);
        fx.sr = go.AddComponent<SpriteRenderer>();
        fx.sr.sprite = circle ? CombatFx.Circle() : CombatFx.Square();
        fx.sr.color = c;
        fx.sr.sortingOrder = order;
        fx.baseColor = c;
        fx.baseScale = go.transform.localScale;
        return fx;
    }

    public static void Ball(Vector2 a, Vector2 b, float dur, bool lob, Color c, System.Action onLand)
    {
        CannonFx fx = Make("CannonBall", Kind.Ball, a, 0.3f, c, dur, 40, true);
        fx.from = a;
        fx.to = b;
        fx.lob = lob;
        fx.onLand = onLand;
    }

    public static void Muzzle(Vector2 at, Vector2 dir)
    {
        Make("CannonFlash", Kind.Flash, at + dir * 0.6f, 0.55f, new Color(1f, 0.85f, 0.4f, 0.9f), 0.12f, 41, true);
        CannonFx smoke = Make("CannonSmoke", Kind.Smoke, at + dir * 0.7f, 0.5f, new Color(0.75f, 0.75f, 0.75f, 0.6f), 0.7f, 39, true);
        smoke.from = smoke.transform.position;
        smoke.to = smoke.from + (Vector3)(dir * 0.4f + Vector2.up * 0.3f);
    }

    public static void Impact(Vector2 at, ModuleKind shell)
    {
        Color c = shell == ModuleKind.None ? new Color(0.95f, 0.8f, 0.45f, 0.9f) : MagicShells.ShellColor(shell);
        Make("CannonImpact", Kind.Flash, at, 0.9f, c, 0.18f, 38, true);
        CannonFx dust = Make("CannonDust", Kind.Smoke, at, 0.8f, new Color(0.55f, 0.5f, 0.45f, 0.55f), 0.6f, 37, true);
        dust.from = dust.transform.position;
        dust.to = dust.from + Vector3.up * 0.25f;
    }

    public static void Frag(Vector2 a, Vector2 b, ModuleKind shell)
    {
        Color c = shell == ModuleKind.None ? new Color(1f, 0.9f, 0.6f, 0.95f) : MagicShells.ShellColor(shell);
        CannonFx fx = Make("CannonFrag", Kind.Frag, a, 0.08f, c, Mathf.Clamp(Vector2.Distance(a, b) / 22f, 0.06f, 0.2f), 40, false);
        fx.from = a;
        fx.to = b;
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = Mathf.Clamp01(t / duration);
        switch (kind)
        {
            case Kind.Ball:
            {
                Vector3 p = Vector3.Lerp(from, to, k);
                if (lob) p.y += Mathf.Sin(k * Mathf.PI) * Mathf.Clamp(Vector3.Distance(from, to) * 0.3f, 0.8f, 2.5f);
                transform.position = p;
                float s = lob ? Mathf.Lerp(0.3f, 0.45f, Mathf.Sin(k * Mathf.PI)) : 0.3f;
                transform.localScale = new Vector3(s, s, 1f);
                break;
            }
            case Kind.Frag:
            {
                // Чёрточка, летящая от взрыва
                Vector3 head = Vector3.Lerp(from, to, k);
                Vector3 tail = Vector3.Lerp(from, to, Mathf.Max(0f, k - 0.35f));
                Vector3 d = head - tail;
                transform.position = (head + tail) * 0.5f;
                transform.localScale = new Vector3(Mathf.Max(0.06f, d.magnitude), 0.06f, 1f);
                if (d.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                break;
            }
            case Kind.Smoke:
                transform.position = Vector3.Lerp(from, to, k);
                transform.localScale = baseScale * Mathf.Lerp(1f, 1.8f, k);
                sr.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * (1f - k));
                break;
            default:
                transform.localScale = baseScale * Mathf.Lerp(0.6f, 1.2f, k);
                sr.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * (1f - k));
                break;
        }

        if (t >= duration)
        {
            System.Action a = onLand;
            onLand = null;
            Destroy(gameObject);
            a?.Invoke();
        }
    }
}
