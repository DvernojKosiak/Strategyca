using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Полевая башня (PostKind.FieldTower) - новый модуль для всего пути Военного дела.
/// Упрощённая городская башня (TowerController): вышка с лучником, сама стреляет стрелами по ближайшему врагу.
/// Слабее городской: прочность 300 против 600, урон 6 против 9, дальность 6 против 7, стреляет реже.
/// Урон и прочность растут с силой Кузни мастера, где её сделали.
/// Изготавливает Кузня мастера, ставится из меню укреплений [B] - дома во время войны и на карте набега.
/// Правило места: ближе EnemyZoneClear (6) клеток не должно быть зелёной зоны врага:
///  - на карте набега - деревня внутри кольца стен и полоса у стен (TrapManager.EnemyZoneDistance);
///  - дома - полевые лагеря армий соседей (EnemyCamp, зона - их радиус лечения).
/// Дома враги ломают её, как заслоны. В набеге защитники укрепления не штурмуют, но башню, к которой
/// подошли вплотную, рубят.
/// </summary>
public partial class FieldPost
{
    public const int MaxTowers = 2;                 // полевых башен на одной карте
    public const float EnemyZoneClear = 6f;         // клеток до зелёной зоны врага
    const float TowerMeleeReach = 1.6f;             // защитник ближе - рубит башню (набег)
    const float TowerMeleeEvery = 1.2f;
    const float TowerMeleeMul = 0.6f;

    float towerTimer, towerMeleeTimer;

    public float TowerDamage { get { return S.towerDamage * Strength; } }
    public float TowerRange { get { return S.towerRange; } }

    /// <summary>Насколько укрепление рассеивает туман: башня видит дальше, чем стреляет.</summary>
    public float VisionRadius { get { return S.IsTower ? Mathf.Max(Vision, S.towerRange + 1.5f) : Vision; } }

    void TowerTick(float dt)
    {
        if (!Built) return;
        if (Raid) TowerMeleeTick(dt);
        if (dead) return;

        towerTimer -= dt;
        if (towerTimer > 0f) return;

        Vector2 from = Position + Vector2.up * 0.35f;   // стреляет лучник с площадки
        if (Raid)
        {
            RaidUnit u = RaidUnit.NearestOf(false, Position, TowerRange);
            if (u == null) { towerTimer = 0.25f; return; }
            towerTimer = S.towerCooldown;
            CombatFx.Arrow(from, u.Position);
            u.TakeDamage(TowerDamage, null, false, Position);
        }
        else
        {
            EnemyAgent e = EnemyAgent.FindNearest(Position, TowerRange);
            if (e == null) { towerTimer = 0.25f; return; }
            towerTimer = S.towerCooldown;
            CombatFx.Arrow(from, e.Position);
            float dmg = TowerDamage;
            // Убитый со стрелой башни бросает награбленное - город его забирает (как у ловушек)
            if (e.HasLoot && e.Hp <= dmg) { e.Carried.DepositToTown(); CombatFx.LootFly(e.Position, Position); }
            e.TakeDamage(dmg, null, null);
        }
    }

    // В набеге защитники вплотную рубят башню
    void TowerMeleeTick(float dt)
    {
        towerMeleeTimer -= dt;
        if (towerMeleeTimer > 0f) return;
        towerMeleeTimer = TowerMeleeEvery;
        float dmg = 0f;
        foreach (var u in RaidUnit.All)
        {
            if (u == null || !u.Alive || u.Attacker || DistanceTo(u.Position) > TowerMeleeReach) continue;
            dmg += u.DamagePreview * TowerMeleeMul;
        }
        if (dmg <= 0f) return;
        CombatFx.Spark(Position + Random.insideUnitCircle * 0.3f, new Color(1f, 0.8f, 0.4f), 0.18f, 0.25f);
        Hp -= dmg;
        lastHit = Time.time;
        if (Hp <= 0f) Break($"Защитники срубили: {S.name}");
    }

    // ---------- Правило места: далеко от зелёной зоны врага ----------

    static readonly List<EnemyCamp> camps = new List<EnemyCamp>();
    static float campsAt = -99f;

    static List<EnemyCamp> Camps()
    {
        if (Time.time - campsAt > 0.5f)
        {
            campsAt = Time.time;
            camps.Clear();
            foreach (var c in Object.FindObjectsByType<EnemyCamp>(FindObjectsSortMode.None))
                if (c != null && c.Alive) camps.Add(c);
        }
        return camps;
    }

    /// <summary>Лагеря соседей у нас дома (для подсветки запретной зоны при размещении).</summary>
    public static List<Vector2> EnemyCampPoints()
    {
        var list = new List<Vector2>();
        foreach (var c in Camps()) if (c != null && c.Alive) list.Add(c.Position);
        return list;
    }

    /// <summary>Радиус зелёной зоны вражеского лагеря дома (клеток).</summary>
    public static float CampZoneRadius { get { return EnemyCamp.HealRadius; } }

    /// <summary>Почему здесь нельзя ставить полевую башню из-за зелёной зоны врага ("" - можно).</summary>
    public static string EnemyZoneProblem(Vector2 p, bool raid)
    {
        if (raid)
        {
            RaidMap map = RaidMap.Instance;
            if (map == null) return "";
            int d = TrapManager.EnemyZoneDistance(map.WorldToCell(p));
            if (d <= 0) return "Это зелёная зона врага";
            if (d <= EnemyZoneClear) return $"До зелёной зоны врага {d} кл. — нужно больше {EnemyZoneClear:F0}";
            return "";
        }
        foreach (var c in Camps())
        {
            if (c == null || !c.Alive) continue;
            float d = Vector2.Distance(p, c.Position) - CampZoneRadius;
            if (d <= 0f) return "Это зона вражеского лагеря";
            if (d <= EnemyZoneClear) return $"До зоны вражеского лагеря {Mathf.CeilToInt(d)} кл. — нужно больше {EnemyZoneClear:F0}";
        }
        return "";
    }
}
