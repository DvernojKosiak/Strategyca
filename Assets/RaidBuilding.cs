using UnityEngine;
using System.Collections.Generic;

public enum RaidBuildingKind
{
    Hall, House, Farm, Sawmill, Mine, Warehouse, Workshop, Barracks, Kitchen, Stable, Tower, Cannon, Wall, Gate,
    Barricade,  // заслон из брёвен: защитники ставят на подходах и прячутся за ним
    Gun         // пушка на земле: ядра прямой наводкой, осколки; через стены и дома не бьёт
}

/// <summary>
/// Здание вражеской деревни на карте набега.
///  - У здания есть прочность и склад (Loot). Разрушили - склад уносят наши бойцы, которые стояли рядом.
///  - Башня стреляет стрелами, магическая пушка - ядрами по площади (магию щит не держит). Стреляют только в бою.
///  - Стены и ворота загораживают проход. Разрушенные - проходимый завал. Поле фермы проходимо всегда.
///  - Заслон (Barricade) защитники ставят на подходах заранее или по тревоге: пока его строят (Rising), он
///    не загораживает и хрупкий. За заслоном (и за стеной) защитник получает меньше урона от тех, кто снаружи.
/// Создаёт BotVillage, удаляет RaidManager вместе с картой.
/// </summary>
public class RaidBuilding : MonoBehaviour
{
    public static readonly List<RaidBuilding> All = new List<RaidBuilding>();

    public RaidBuildingKind Kind { get; private set; }
    public string Title { get; private set; }
    public Vector2Int OriginCell { get; private set; }
    public Vector2Int SizeCells { get; private set; }
    public float Hp { get; private set; }
    public float MaxHp { get; private set; }
    public bool IsRuined { get; private set; }
    public readonly Loot Storage = new Loot();

    public bool IsBarrier => Kind == RaidBuildingKind.Wall || Kind == RaidBuildingKind.Gate || Kind == RaidBuildingKind.Barricade;
    public bool IsBarricade => Kind == RaidBuildingKind.Barricade;
    public bool Rising => riseLeft > 0f;   // заслон ещё строят
    public bool IsDefense => Kind == RaidBuildingKind.Tower || Kind == RaidBuildingKind.Cannon || Kind == RaidBuildingKind.Gun;
    public bool IsHall => Kind == RaidBuildingKind.Hall;
    public bool Blocks => !(Kind == RaidBuildingKind.Farm || (IsBarrier && IsRuined) || Rising);
    public bool UnderAttack => Time.time - lastHit < 3f;

    // ---------- Оборона ----------
    const float TowerRange = 6.5f, TowerDamage = 9f, TowerCooldown = 1.3f;
    const float CannonRange = 8f, CannonDamage = 20f, CannonCooldown = 3.5f, CannonBlast = 1.4f;
    const float GunRange = 9f, GunDamage = 35f, GunCooldown = 3.5f;

    float powerMul = 1f;
    float riseLeft, riseTotal;
    const float CoverMul = 0.6f;     // столько урона проходит к защитнику, укрытому заслоном или стеной
    float shootTimer;
    float lastHit = -99f;
    SpriteRenderer look;
    Color baseColor;

    public Vector2 Center => RaidMap.Origin + new Vector2(OriginCell.x + SizeCells.x / 2f, OriginCell.y + SizeCells.y / 2f);

    public static RaidBuilding Create(RaidBuildingKind kind, string title, Vector2Int origin, Vector2Int size, float maxHp, Color color, float powerMul)
    {
        GameObject go = new GameObject("Raid_" + title);
        RaidBuilding b = go.AddComponent<RaidBuilding>();
        b.Kind = kind;
        b.Title = title;
        b.OriginCell = origin;
        b.SizeCells = size;
        b.MaxHp = b.Hp = Mathf.Max(1f, maxHp);
        b.baseColor = color;
        b.powerMul = powerMul;
        b.shootTimer = Random.Range(0f, 1f);
        go.transform.position = new Vector3(b.Center.x, b.Center.y, 0f);

        // Квадрат здания (чуть меньше клеток, чтобы были видны швы)
        GameObject body = new GameObject("Body");
        body.transform.SetParent(go.transform, false);
        body.transform.localScale = new Vector3(size.x - 0.08f, size.y - 0.08f, 1f);
        b.look = body.AddComponent<SpriteRenderer>();
        b.look.sprite = CombatFx.Square();
        b.look.color = color;
        b.look.sortingOrder = kind == RaidBuildingKind.Farm ? -50 : 0;

        // Башне и пушке - тёмная "крыша", чтобы отличать от домов
        if (b.IsDefense)
        {
            GameObject top = new GameObject("Top");
            top.transform.SetParent(go.transform, false);
            top.transform.localScale = new Vector3(size.x * 0.45f, size.y * 0.45f, 1f);
            SpriteRenderer sr = top.AddComponent<SpriteRenderer>();
            sr.sprite = CombatFx.Square();
            sr.color = new Color(color.r * 0.55f, color.g * 0.55f, color.b * 0.55f);
            sr.sortingOrder = 1;
        }
        return b;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ---------- Геометрия ----------

    public Vector2 ClosestPoint(Vector2 p)
    {
        Vector2 o = RaidMap.Origin + new Vector2(OriginCell.x, OriginCell.y);
        return new Vector2(Mathf.Clamp(p.x, o.x, o.x + SizeCells.x), Mathf.Clamp(p.y, o.y, o.y + SizeCells.y));
    }

    public float DistanceTo(Vector2 p) { return Vector2.Distance(p, ClosestPoint(p)); }

    public bool Contains(Vector2 p) { return DistanceTo(p) <= 0.01f; }

    // ---------- Урон ----------

    public void TakeDamage(float dmg, RaidUnit by)
    {
        if (IsRuined || dmg <= 0f) return;
        Hp -= dmg;
        lastHit = Time.time;
        if (RaidManager.Instance != null) RaidManager.Instance.Alarm(Center, IsHall ? 22f : 14f);
        if (Hp <= 0f) Ruin(by);
    }

    /// <summary>Заслон начинают строить: seconds - сколько строят. Пока строят, он проходим и хрупкий.</summary>
    public void StartRising(float seconds)
    {
        riseTotal = riseLeft = Mathf.Max(0.1f, seconds);
        Hp = MaxHp * 0.15f;
        ApplyRiseLook();
    }

    void ApplyRiseLook()
    {
        if (look == null) return;
        float k = riseTotal > 0f ? 1f - riseLeft / riseTotal : 1f;
        look.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Lerp(0.3f, 1f, k));
    }

    /// <summary>
    /// Сколько урона дойдёт до защитника в точке target от удара / выстрела из from:
    /// если рядом с ним (вплотную) целый заслон или стена, и она между ним и стрелком - урон меньше.
    /// </summary>
    public static float CoverFor(Vector2 target, Vector2 from)
    {
        Vector2 toShooter = from - target;
        if (toShooter.sqrMagnitude < 0.01f) return 1f;
        foreach (var b in All)
        {
            if (b == null || b.IsRuined || b.Rising || !b.IsBarrier) continue;
            float d = b.DistanceTo(target);
            if (d > 1.4f) continue;
            Vector2 toWall = b.ClosestPoint(target) - target;
            if (Vector2.Dot(toWall, toShooter) <= 0f) continue;          // стенка с другой стороны
            if (b.DistanceTo(from) <= d + 0.3f) continue;                // стрелок у самой стенки
            return CoverMul;
        }
        return 1f;
    }

    void Ruin(RaidUnit by)
    {
        Hp = 0f;
        IsRuined = true;
        riseLeft = 0f;
        look.color = new Color(baseColor.r * 0.3f, baseColor.g * 0.3f, baseColor.b * 0.3f, 0.85f);
        if (IsBarrier && RaidMap.Instance != null) RaidMap.Instance.MarkDirty();   // завал - проходим
        CombatFx.Death(Center);
        if (RaidManager.Instance != null) RaidManager.Instance.OnBuildingRuined(this, by);
    }

    // ---------- Стрельба ----------

    void Update()
    {
        if (riseLeft > 0f && !IsRuined)
        {
            riseLeft -= Time.deltaTime;
            Hp = Mathf.Min(MaxHp, Hp + MaxHp * 0.85f * Time.deltaTime / riseTotal);
            ApplyRiseLook();
            if (riseLeft <= 0f)
            {
                riseLeft = 0f;
                if (RaidMap.Instance != null) RaidMap.Instance.MarkDirty();   // достроили - загораживает
            }
        }
        if (!IsDefense || IsRuined) return;
        RaidManager rm = RaidManager.Instance;
        if (rm == null || !rm.BattleOn) return;

        shootTimer -= Time.deltaTime;
        if (shootTimer > 0f) return;

        if (Kind == RaidBuildingKind.Gun) { GunShoot(); return; }

        float range = Kind == RaidBuildingKind.Tower ? TowerRange : CannonRange;
        RaidUnit t = RaidUnit.NearestNoticed(Center, range + SizeCells.x * 0.5f);
        if (t == null) { shootTimer = 0.3f; return; }

        if (Kind == RaidBuildingKind.Tower)
        {
            shootTimer = TowerCooldown;
            CombatFx.Arrow(Center, t.Position);
            t.TakeDamage(TowerDamage * powerMul, null, false, Center);
        }
        else
        {
            shootTimer = CannonCooldown;
            Vector2 at = t.Position;
            float dmg = CannonDamage * powerMul;
            CombatFx.Shell(Center, at, 0.9f, CannonBlast, () =>
            {
                // Пока ядро летело, поле боя могло исчезнуть
                foreach (var u in new List<RaidUnit>(RaidUnit.All))
                    if (u != null && u.Attacker && u.Alive && Vector2.Distance(u.Position, at) <= CannonBlast)
                        u.TakeDamage(dmg, null, true);
            });
        }
    }

    /// <summary>Пушка соседей: бьёт только того, кого видит прямо (стены и дома заслоняют), ядро разлетается осколками.</summary>
    void GunShoot()
    {
        RaidBuilding self = this;
        RaidUnit t = RaidUnit.NearestNoticed(Center, GunRange + SizeCells.x * 0.5f,
            u => Vector2.Distance(u.Position, Center) > 1.8f && CannonBallistics.RaidFirstBlock(Center, u.Position, self, null, out _) == null);
        if (t == null) { shootTimer = 0.4f; return; }
        shootTimer = GunCooldown;
        CannonShot shot = new CannonShot
        {
            damage = GunDamage * powerMul,
            buildingMul = 1f,
            barrierMul = 1f,
            fragments = 5,
            fragDamage = 10f * powerMul,
            fragRange = 2.2f,
            fragSpread = 32f,
            mana = 1f
        };
        CannonBallistics.FireRaid(Center, t.Position, t, null, shot, null, this, false);
    }

    // ---------- Подсказка ----------

    public string TooltipText
    {
        get
        {
            string hp = IsRuined ? "<color=#FF8060>руины</color>" : $"прочность {Mathf.CeilToInt(Hp)}/{Mathf.CeilToInt(MaxHp)}";
            string loot = Storage.IsEmpty ? "" : "\nсклад: <color=#FFD040>" + Storage.ShortText + "</color>";
            if (IsBarricade && !IsRuined)
                return $"<b>{Title}</b>  {hp}\n" + (Rising ? "<color=#FFD060>строят — пока не загораживает</color>" : "<color=#FF9060>защитники за ним получают меньше урона</color>");
            string def = IsDefense && !IsRuined ? (Kind == RaidBuildingKind.Tower ? "\n<color=#FF9060>стреляет по нашим</color>"
                : Kind == RaidBuildingKind.Gun ? "\n<color=#FF9060>бьёт ядрами с осколками; из-за стен и домов не достанет</color>"
                : "\n<color=#FF9060>бьёт магией по площади</color>") : "";
            return $"<b>{Title}</b>  {hp}{def}{loot}";
        }
    }
}
