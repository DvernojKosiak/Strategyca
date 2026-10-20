using UnityEngine;

/// <summary>
/// Полевая пушка (продолжение класса CitizenAgent; вид войск - BarracksController.FieldCannonName, путь Военного дела).
///  - Бьёт ядрами прямой наводкой (CannonBallistics): если между ней и врагом стена или дом - подъезжает,
///    пока не увидит цель, или поднимается подъёмником (модуль Инженерии) и бьёт навесом.
///  - В набеге ядро бьёт первое, что стоит на прямой: стену, ворота, заслон - так пушка и пробивает оборону.
///    По зданиям урон втрое больше, по стенам и заслонам - ещё больше.
///  - «Оружие» - бронебойное ядро (Инженерный двор, выдаётся в казарме «Снаряж.»): сильнее, прошивает заслон / стену,
///    осколки летят сквозь укрепления; щит и укрытие от него не спасают. Каждый выстрел изнашивает ядро.
///  - Модули: подъёмник и пачка магических снарядов. Берёт сама со склада Ратуши / двора, где бы ни стояла дома
///    (не в бою и не в походе; без носильщика - упрощение). В казарме «Снаряж.» можно выбрать вид снарядов или снять пачку.
///  - Погибла - модули пропали; уволили / ушла живой - модули вернулись в Ратушу.
/// </summary>
public partial class CitizenAgent
{
    readonly CannonLiftState cannonLift = new CannonLiftState();
    ModuleKind cannonShell = ModuleKind.None;
    int cannonShellMk, cannonShellCharges;
    ModuleKind cannonShellPref = ModuleKind.None;   // какой вид брать самой (None - лучший любой)
    bool cannonShellAuto = true;                    // сама берёт пачку, когда снарядов нет
    float cannonRepositionUntil;               // прямой наводки нет - подъезжаем
    Vector2 cannonAim = Vector2.right;
    SpriteRenderer cannonBarrelSr, cannonWheelSr;

    public bool IsFieldCannon => unit != null && unit.isCannon;
    public CannonLiftState CannonLift => cannonLift;
    public bool CannonHasShells => cannonShell != ModuleKind.None && cannonShellCharges > 0;
    public ModuleKind CannonShellKind => CannonHasShells ? cannonShell : ModuleKind.None;
    public int CannonShellMk => cannonShellMk;
    public int CannonShellCharges => cannonShellCharges;
    public ModuleKind CannonShellPref => cannonShellPref;
    public bool CannonShellAuto { get { return cannonShellAuto; } set { cannonShellAuto = value; } }

    public void MountCannonLift(int mk) { if (IsFieldCannon) cannonLift.Set(mk); }

    public void LoadCannonShells(ModuleKit kit)
    {
        if (!IsFieldCannon || !MagicShells.IsShell(kit.kind)) return;
        cannonShell = kit.kind;
        cannonShellMk = Mathf.Clamp(kit.mk, 1, 3);
        int full = MagicShells.Charges(cannonShellMk);
        cannonShellCharges = kit.charges > 0 ? Mathf.Min(kit.charges, full) : full;
    }

    /// <summary>Пачку снарядов - обратно на склад Ратуши (без зарядки самой, пока не включат «Авто» / не выберут вид).</summary>
    public void UnloadCannonShells()
    {
        if (!CannonHasShells) return;
        int full = MagicShells.Charges(cannonShellMk);
        EngineerYard.AddToHall(new ModuleKit { kind = cannonShell, mk = cannonShellMk, charges = cannonShellCharges < full ? cannonShellCharges : 0 });
        cannonShell = ModuleKind.None;
        cannonShellMk = 0;
        cannonShellCharges = 0;
        cannonShellAuto = false;
    }

    /// <summary>Зарядить пачку этого вида (лучшую марку) со склада; прежняя пачка - на склад. Вид запоминается для «Авто».</summary>
    public bool LoadCannonShellsOf(ModuleKind kind)
    {
        if (!IsFieldCannon || !MagicShells.IsShell(kind)) return false;
        if (!MagicShells.BestPack(kind, out ModuleKit pack)) return false;
        if (CannonHasShells && cannonShell == kind && cannonShellMk >= pack.mk && cannonShellCharges >= MagicShells.Charges(cannonShellMk))
        {
            cannonShellPref = kind;
            cannonShellAuto = true;
            return false;   // такая же полная уже стоит
        }
        if (!EngineerYard.TakeKit(pack, out _)) return false;
        if (CannonHasShells) UnloadCannonShells();
        LoadCannonShells(pack);
        cannonShellPref = kind;
        cannonShellAuto = true;
        return true;
    }

    /// <summary>Ушла со службы живой: подъёмник и снаряды - на склад Ратуши.</summary>
    void CannonReturnGear()
    {
        if (cannonLift.Has) EngineerYard.AddToHall(new ModuleKit { kind = ModuleKind.CannonLift, mk = cannonLift.mk });
        if (CannonHasShells)
        {
            int full = MagicShells.Charges(cannonShellMk);
            EngineerYard.AddToHall(new ModuleKit { kind = cannonShell, mk = cannonShellMk, charges = cannonShellCharges < full ? cannonShellCharges : 0 });
        }
        cannonLift.Set(0);
        cannonShell = ModuleKind.None;
        cannonShellCharges = 0;
    }

    void CannonLoseGear()
    {
        if (!IsFieldCannon) return;
        if (cannonLift.Has || CannonHasShells) WarManager.Notify($"Погибла {unit.unitName}: её модули потеряны", false);
        cannonLift.Set(0);
        cannonShell = ModuleKind.None;
        cannonShellCharges = 0;
    }

    // ---------- Выстрел ----------

    /// <summary>Собрать выстрел: урон бойца (похлёбка, ядро), осколки, бронебойность, магический снаряд (тратится).</summary>
    CannonShot MakeCannonShot(bool lob)
    {
        bool ap = weapon != null && weapon.kind == WeaponKind.Shot;
        CannonShot s = new CannonShot
        {
            damage = Damage,
            buildingMul = unit.cannonBuildingMul,
            barrierMul = unit.cannonBarrierMul * (ap ? 1.6f : 1f),
            fragments = unit.cannonFragments + (ap ? 1 : 0),
            fragDamage = unit.cannonFragDamage * (hearty ? unit.pottageDamageMul : 1f) * (weapon != null ? weapon.DamageMul : 1f),
            fragRange = unit.cannonFragRange,
            fragSpread = 30f,
            armorPiercing = ap,
            lob = lob,
            mana = ManaManager.Factor
        };
        if (CannonHasShells)
        {
            s.shell = cannonShell;
            s.shellMk = cannonShellMk;
            float k = MagicShells.DamageMul(cannonShell, cannonShellMk, s.mana);
            s.damage *= k;
            s.fragDamage *= k;
            cannonShellCharges--;
            if (cannonShellCharges <= 0)
            {
                WarManager.Notify($"{unit.unitName} {citizenName}: кончились {TowerModules.Name(cannonShell).ToLower()}", false);
                cannonShell = ModuleKind.None;
                cannonShellCharges = 0;
            }
        }
        if (weapon != null && weapon.Wear())
        {
            WarManager.Notify($"Израсходовано: {weapon.Name} ({unit.unitName} {citizenName})", false);
            weapon = null;
        }
        return s;
    }

    /// <summary>Выстрел по врагу дома. lob - с подъёма (навесом).</summary>
    void CannonStrikeHome(EnemyAgent e, bool lob)
    {
        Vector2 me = transform.position;
        cannonAim = (e.Position - me).sqrMagnitude > 0.001f ? (e.Position - me).normalized : cannonAim;
        CannonShot s = MakeCannonShot(lob);
        CannonBallistics.FireHome(me + cannonAim * 0.5f, e, s, this, null);
    }

    /// <summary>
    /// Пушка в радиусе выстрела: можно ли бить сейчас. Прямой наводки нет - поднимается (если есть подъёмник),
    /// иначе ненадолго подъезжает к цели. lob - бить навесом.
    /// </summary>
    bool CannonCanFire(Vector2 tp, out bool lob)
    {
        lob = false;
        if (cannonLift.Up) { lob = true; return true; }
        if (CannonBallistics.HomeLineClear(transform.position, tp, null)) return true;
        if (cannonLift.Has && cannonLift.TryRaise()) { lob = true; attackTimer = Mathf.Max(attackTimer, 0.5f); return false; }
        cannonRepositionUntil = Time.time + 1.5f;
        return false;
    }

    bool CannonRepositioning => IsFieldCannon && Time.time < cannonRepositionUntil;

    // ---------- Набег ----------

    /// <summary>Выстрел в набеге (RaidUnit): ядро изнашивается, магический снаряд тратится.</summary>
    public CannonShot ExpCannonShot(bool lob) { return MakeCannonShot(lob); }

    public void ExpCannonAim(Vector2 dir) { if (dir.sqrMagnitude > 0.001f) cannonAim = dir.normalized; }

    public string CannonGearText
    {
        get
        {
            if (!IsFieldCannon) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (cannonLift.Has) parts.Add(TowerModules.KitName(ModuleKind.CannonLift, cannonLift.mk));
            if (CannonHasShells) parts.Add($"{TowerModules.Name(cannonShell).ToLower()} Мк.{TowerModules.Roman(cannonShellMk)}: {cannonShellCharges}");
            return string.Join(", ", parts);
        }
    }

    // ---------- Внешний вид: колёса и ствол, поднятая - ствол длиннее ----------

    void CannonLook(bool shown)
    {
        bool cannon = IsFieldCannon && shown;
        if (!cannon)
        {
            if (cannonBarrelSr != null) cannonBarrelSr.enabled = false;
            if (cannonWheelSr != null) cannonWheelSr.enabled = false;
            return;
        }
        if (cannonBarrelSr == null)
        {
            cannonWheelSr = MakePart("CannonWheels", Vector2.zero, Vector2.one, new Color(0.35f, 0.24f, 0.12f), 9);
            cannonBarrelSr = MakePart("CannonBarrel", Vector2.zero, Vector2.one, new Color(0.12f, 0.12f, 0.14f), 11);
        }
        cannonBarrelSr.enabled = cannonWheelSr.enabled = true;
        bool up = cannonLift.Up;
        float len = bodyScale * (up ? 1.35f : 1.05f), th = bodyScale * (up ? 0.34f : 0.28f);
        cannonWheelSr.transform.localPosition = new Vector3(0f, -bodyScale * 0.42f, 0f);
        cannonWheelSr.transform.localScale = new Vector3(bodyScale * 1.2f, bodyScale * 0.3f, 1f);
        cannonWheelSr.color = up ? new Color(0.75f, 0.55f, 0.25f) : new Color(0.35f, 0.24f, 0.12f);
        cannonBarrelSr.transform.localPosition = new Vector3(cannonAim.x * len * 0.5f, cannonAim.y * len * 0.5f + (up ? bodyScale * 0.2f : 0f), 0f);
        cannonBarrelSr.transform.localScale = new Vector3(len, th, 1f);
        cannonBarrelSr.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(cannonAim.y, cannonAim.x) * Mathf.Rad2Deg);
    }
}
