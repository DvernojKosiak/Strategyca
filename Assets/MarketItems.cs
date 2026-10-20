using UnityEngine;
using System.Collections.Generic;

/// <summary>Боец, ушедший в лагерь наёмников гильдии (продаётся или куплен): вид войск и всё, что при нём.</summary>
public class SoldierLot
{
    public UnitType unit;
    public bool hearty;          // обучен на похлёбке (★)
    public Weapon weapon;
    public Shield shield;
    public string name;          // имя бойца (для своих) или прозвище наёмника
}

public enum ItemKind { Weapon, Shield, Module, Soldier, Trap }   // новые виды - только в конец

/// <summary>
/// Штучный товар рынка: оружие, щит, модуль для башни, ловушка или боец. В отличие от сырья, каждый - единственный в своём роде
/// (качество, улучшения, доводка, заряды яда/замедления, марка модуля, снаряжение бойца).
///  - Продажа: вещь берут из Оружейной / двора / Ратуши и несут в Торговый дом.
///    Боец уходит в лагерь наёмников гильдии сразу (вместе с оружием, щитом и конём).
///  - Ловушки (обычные и боевые) лежат в запасе Ратуши (TrapStock): так обычные ловушки инженеров
///    попадают к игроку Военного дела, который переделывает их в боевые.
///  - Покупка: купец привозит вещь в Торговый дом; оружие и щиты несут в Оружейную, модули и ловушки - в Ратушу
///    (поставить их на башню может любой город), боец сходит с повозки и идёт в казарму.
/// Цена - по ценности вещи (Value) и рыночному спросу.
/// </summary>
public class MarketItem
{
    public ItemKind kind;
    public Weapon weapon;
    public Shield shield;
    public ModuleKit kit;
    public TrapKit trap;
    public SoldierLot soldier;

    // Пока вещь ещё в городе (кандидат на продажу) - откуда её брать
    public ArmoryController armory;
    public CitizenAgent agent;
    public BarracksController barracks;

    /// <summary>Вкладка рынка: 3 - оружие и щиты, 4 - модули и ловушки, 5 - войска.</summary>
    public int Category => kind == ItemKind.Module || kind == ItemKind.Trap ? 4 : (kind == ItemKind.Soldier ? 5 : 3);

    static readonly float[] QMul = { 1f, 1.7f, 2.8f };
    static readonly int[] ModuleValue = { 45, 75, 110 };

    // ---------- Название и описание ----------

    public string Name
    {
        get
        {
            switch (kind)
            {
                case ItemKind.Weapon: return Cap(weapon.Name);
                case ItemKind.Shield: return Cap(shield.Name);
                case ItemKind.Module: return TowerModules.KitName(kit);
                case ItemKind.Trap: return trap != null ? trap.Name : "Ловушка";
                default:
                    UnitType u = SoldierUnit;
                    string star = SoldierHearty ? " ★" : "";
                    return (u != null ? u.unitName : "Боец") + star + (SoldierName != "" ? " " + SoldierName : "");
            }
        }
    }

    public string Details
    {
        get
        {
            switch (kind)
            {
                case ItemKind.Weapon: return weapon.StatsText;
                case ItemKind.Shield: return shield.StatsText;
                case ItemKind.Module: return TowerModules.Stats(kit.kind, kit.mk);
                case ItemKind.Trap: return trap != null ? Traps.Details(trap) + (trap.combat ? " (ставится в бою)" : " (ставится в мирное время)") : "";
                default:
                    UnitType u = SoldierUnit;
                    if (u == null) return "";
                    var parts = new List<string> { $"здоровье {u.maxHp:F0}, урон {u.damage:F0}, мест {u.spaceOccupied}" };
                    Weapon w = SoldierWeapon;
                    Shield sh = SoldierShield;
                    if (w != null) parts.Add(w.Name);
                    if (sh != null) parts.Add(sh.Name);
                    if (u.requiresHorse) parts.Add("с конём");
                    return string.Join(", ", parts);
            }
        }
    }

    static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1); }

    UnitType SoldierUnit => soldier != null ? soldier.unit : (agent != null ? agent.ExpUnit : null);
    bool SoldierHearty => soldier != null && soldier.hearty;
    string SoldierName => soldier != null ? (soldier.name ?? "") : (agent != null ? agent.CitizenName : "");
    Weapon SoldierWeapon => soldier != null ? soldier.weapon : (agent != null ? agent.EquippedWeapon : null);
    Shield SoldierShield => soldier != null ? soldier.shield : (agent != null ? ShieldBearer.ShieldOf(agent) : null);

    /// <summary>Сколько места займёт боец в казарме.</summary>
    public int Space => SoldierUnit != null ? SoldierUnit.spaceOccupied : 1;

    // ---------- Ценность (базовая цена в марках) ----------

    static float WeaponValue(Weapon w)
    {
        if (w == null) return 0f;
        float v = 10f * QMul[(int)w.quality] + w.upgrade * 12f + (w.forged ? w.forgeStars * 6f : 0f)
                + w.poisonCharges / 6f + w.slowCharges / 5f;
        return v * (0.4f + 0.6f * w.Frac);
    }

    static float ShieldValue(Shield s)
    {
        if (s == null) return 0f;
        return (9f * QMul[(int)s.quality] + s.upgrade * 10f) * (0.4f + 0.6f * s.Frac);
    }

    public float Value
    {
        get
        {
            switch (kind)
            {
                case ItemKind.Weapon: return WeaponValue(weapon);
                case ItemKind.Shield: return ShieldValue(shield);
                case ItemKind.Module: return ModuleValue[Mathf.Clamp(kit.mk, 1, 3) - 1];
                case ItemKind.Trap: return trap != null ? Traps.Value(trap) : 10f;
                default:
                    UnitType u = SoldierUnit;
                    if (u == null) return 10f;
                    float v = u.costGold * 0.22f + u.costFood * 0.1f + u.maxHp * 0.05f
                            + (u.requiresHorse ? 20f : 0f) + (SoldierHearty ? 8f : 0f)
                            + u.resinCost * 2f + u.potionCost * 6f;
                    return v + WeaponValue(SoldierWeapon) + ShieldValue(SoldierShield);
            }
        }
    }

    // ---------- Что в городе можно продать ----------

    public static List<MarketItem> SellableNow(int category)
    {
        var list = new List<MarketItem>();
        if (category == 3)
        {
            foreach (var a in ArmoryController.All)
            {
                if (a == null || !a.Works) continue;
                foreach (var w in a.Stock) list.Add(new MarketItem { kind = ItemKind.Weapon, weapon = w, armory = a });
                foreach (var s in a.Shields) list.Add(new MarketItem { kind = ItemKind.Shield, shield = s, armory = a });
            }
        }
        else if (category == 4)
        {
            foreach (var k in EngineerYard.AllKits()) list.Add(new MarketItem { kind = ItemKind.Module, kit = k });
            foreach (var t in TrapStock.Hall) list.Add(new MarketItem { kind = ItemKind.Trap, trap = t });
        }
        else if (category == 5)
        {
            foreach (var b in BuildingInstance.All)
            {
                if (b == null) continue;
                BarracksController br = b.GetComponent<BarracksController>();
                if (br == null) continue;
                foreach (var s in br.Soldiers)
                {
                    if (s == null || s.IsDead || s.IsOnExpedition || s.InCombat || s.ExpUnit == null) continue;
                    list.Add(new MarketItem { kind = ItemKind.Soldier, agent = s, barracks = br });
                }
            }
        }
        return list;
    }

    /// <summary>Вещь ещё на месте и её можно забрать на продажу.</summary>
    public string SellProblem()
    {
        switch (kind)
        {
            case ItemKind.Weapon:
            case ItemKind.Shield:
                if (armory == null || !armory.Works) return "Оружейная недоступна";
                return "";
            case ItemKind.Soldier:
                if (agent == null || agent.IsDead || agent.IsOnExpedition) return "Боец недоступен";
                if (agent.InCombat) return "Боец в бою";
                return "";
            case ItemKind.Trap:
                return trap != null && TrapStock.Hall.Contains(trap) ? "" : "Ловушки уже нет в запасе";
            default: return "";
        }
    }

    /// <summary>Забрать вещь из города под продажу. source - откуда её несут (null - нести не нужно: боец ушёл сам).</summary>
    public bool TakeFromTown(out BuildingInstance source)
    {
        source = null;
        switch (kind)
        {
            case ItemKind.Weapon:
                if (armory == null || !armory.RemoveFromStock(weapon)) return false;
                source = armory.Building;
                armory = null;
                return true;
            case ItemKind.Shield:
                if (armory == null || !armory.RemoveShield(shield)) return false;
                source = armory.Building;
                armory = null;
                return true;
            case ItemKind.Module:
                return EngineerYard.TakeKit(kit, out source);
            case ItemKind.Trap:
            {
                if (!TrapStock.Remove(trap)) return false;
                var pm = PopulationManager.Instance;
                source = pm != null ? pm.Hall : null;
                return true;
            }
            default:
                if (barracks == null || !barracks.ReleaseForSale(agent, out SoldierLot lot)) return false;
                soldier = lot;
                agent = null;
                barracks = null;
                return true;
        }
    }

    // ---------- Получение вещи городом ----------

    /// <summary>Можно ли принять эту вещь (купить / вернуть): pendingGear, pendingSpace - что уже везут.</summary>
    public static string ReceiveProblem(ItemKind kind, int space, int pendingGear, int pendingSpace, int pendingPeople)
    {
        switch (kind)
        {
            case ItemKind.Weapon:
            case ItemKind.Shield:
                if (ArmoryController.All.Count == 0) return "Нужна Оружейная";
                if (ArmoryController.FreeSlotsTotal - pendingGear <= 0) return "В Оружейных нет места";
                return "";
            case ItemKind.Soldier:
                var pm = PopulationManager.Instance;
                if (BarracksController.WithRoomFor(space + pendingSpace) == null) return "Нет места в казармах";
                // Наёмники живут в Гостевом доме; нет в нём мест - в обычных домах, если там есть место
                if (pm != null && pm.MercenaryRoom - pendingPeople <= 0)
                    return pm.GuestCapacity > 0 ? "Нет мест: Гостевые дома полны" : "Нет жилья для наёмника — постройте Гостевой дом";
                return "";
            default: return "";
        }
    }

    /// <summary>
    /// Вещь доставлена к зданию at (Торговый дом или Ратуша). Оружие и щиты несут в Оружейную, модуль - в Ратушу,
    /// боец сходит с повозки и становится бойцом казармы. onDone - когда вещь окончательно на месте.
    /// </summary>
    public void Receive(BuildingInstance at, System.Action onDone)
    {
        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        if (kind == ItemKind.Soldier)
        {
            ArriveSoldier(at);
            if (onDone != null) onDone();
            return;
        }

        if (kind == ItemKind.Module || kind == ItemKind.Trap)
        {
            System.Action store = () =>
            {
                if (kind == ItemKind.Module) EngineerYard.AddToHall(kit);
                else TrapStock.Add(trap);
            };
            if (at == null || hall == null || at == hall) { store(); if (onDone != null) onDone(); return; }
            Logistics.Request(new HaulJob
            {
                from = at, to = hall, prefer = at,
                onDeliver = () => { store(); if (onDone != null) onDone(); },
                onAbort = loaded => { store(); if (onDone != null) onDone(); }
            });
            return;
        }

        // Оружие и щиты - в Оружейную, где есть место
        ArmoryController dest = null;
        foreach (var a in ArmoryController.All)
            if (a != null && a.Works && a.ItemCount < a.Capacity) { dest = a; break; }

        if (dest == null || at == null)
        {
            StashOrHold();
            if (onDone != null) onDone();
            return;
        }
        Logistics.Request(new HaulJob
        {
            from = at, to = dest.Building, prefer = at,
            onDeliver = () => { StashOrHold(); if (onDone != null) onDone(); },
            onAbort = loaded => { StashOrHold(); if (onDone != null) onDone(); }
        });
    }

    // Положить в Оружейную; нет места - вещь ждёт в Торговом доме (гильдия попробует позже)
    void StashOrHold()
    {
        bool ok = kind == ItemKind.Weapon ? ArmoryController.Stash(weapon) : ArmoryController.StashShield(shield);
        if (ok) WarManager.Notify($"В Оружейную принесли: {Name}", true);
        else if (TradeGuild.Instance != null) TradeGuild.Instance.HoldGear(this);
    }

    /// <summary>Повторная попытка положить вещь в Оружейную (место освободилось).</summary>
    public bool TryStash()
    {
        return kind == ItemKind.Weapon ? ArmoryController.Stash(weapon) : ArmoryController.StashShield(shield);
    }

    // Наёмник сходит с повозки у здания at и идёт в казарму
    void ArriveSoldier(BuildingInstance at)
    {
        var pm = PopulationManager.Instance;
        if (pm == null || soldier == null || soldier.unit == null) return;
        Vector2 pos = at != null ? DoorOf(at) : (pm.Hall != null ? pm.Hall.Center : Vector2.zero);

        CitizenAgent a = pm.SpawnAgent(pos, true);   // наёмник: живёт в Гостевом доме
        BarracksController br = BarracksController.WithRoomFor(soldier.unit.spaceOccupied);
        if (br == null)
        {
            // Казарм нет / все полны: наёмник остаётся простым жителем, снаряжение - на склад
            if (soldier.weapon != null && !ArmoryController.Stash(soldier.weapon)) WarManager.Notify("Оружие наёмника некуда деть — потеряно", false);
            if (soldier.shield != null && !ArmoryController.StashShield(soldier.shield)) WarManager.Notify("Щит наёмника некуда деть — потерян", false);
            WarManager.Notify($"{soldier.unit.unitName}: в казармах нет места — наёмник остался в городе простым жителем", false);
            return;
        }
        br.AdoptSoldier(a, soldier.unit, soldier.hearty, soldier.weapon, soldier.shield);
        WarManager.Notify($"Наёмник прибыл: {soldier.unit.unitName}{(soldier.hearty ? " ★" : "")} — идёт в казарму", true);
    }

    static Vector2 DoorOf(BuildingInstance b)
    {
        var nav = GridNavigation.Instance;
        if (nav != null && nav.TryGetDoorCell(b, nav.WorldToCell(b.Center), out Vector2Int d)) return nav.CellCenter(d);
        return b.Center;
    }

    // ---------- Товар купцов ----------

    static QualityTier RollTier(float good, float best)
    {
        float r = Random.value;
        return r < best ? QualityTier.Best : (r < best + good ? QualityTier.Good : QualityTier.Medium);
    }

    static int RollUpgrade()
    {
        float r = Random.value;
        return r < 0.05f ? 2 : (r < 0.2f ? 1 : 0);
    }

    static Weapon RandomWeapon(WeaponKind k)
    {
        Weapon w = new Weapon(k, RollTier(0.3f, 0.1f));
        for (int i = RollUpgrade(); i > 0; i--) w.ApplyUpgrade();
        if (Random.value < 0.15f) w.ApplyForge(Random.Range(0.4f, 1f), Random.Range(0.4f, 1f), Random.Range(0.4f, 1f), 1f);
        if (k == WeaponKind.Bow && Random.value < 0.12f) w.poisonCharges = Weapon.PoisonChargesPerPotion;
        if (k == WeaponKind.Bow && Random.value < 0.12f) w.slowCharges = Weapon.SlowChargesPerPotion;
        w.durability = w.maxDurability * Random.Range(0.6f, 1f);
        return w;
    }

    static Shield RandomShield()
    {
        Shield s = new Shield(RollTier(0.3f, 0.1f));
        for (int i = RollUpgrade(); i > 0; i--) s.ApplyUpgrade();
        s.durability = s.maxDurability * Random.Range(0.6f, 1f);
        return s;
    }

    static readonly string[] Nicknames = { "Хромой", "Рыжий", "Молчун", "Ворон", "Седой", "Бывалый", "Кривой", "Удалой", "Тихий", "Лютый" };

    // Наёмников можно купить любых, в том числе чужого пути
    static readonly string[] MercPool = { "Мечник", "Мечник", "Лучник", "Лучник", "Рыцарь", "Всадник", "Конный лучник", "Полевой маг",
                                          BarracksController.PoisonerName, BarracksController.FireBomberName };

    /// <summary>Случайный штучный товар для купца: category 3 - оружие/щит, 4 - модуль или ловушка, 5 - наёмник. null - сейчас нечего.</summary>
    public static MarketItem RandomBotItem(int category)
    {
        if (category == 3)
        {
            float r = Random.value;
            if (r < 0.4f) return new MarketItem { kind = ItemKind.Weapon, weapon = RandomWeapon(WeaponKind.Sword) };
            if (r < 0.72f) return new MarketItem { kind = ItemKind.Weapon, weapon = RandomWeapon(WeaponKind.Bow) };
            if (r < 0.8f) return new MarketItem { kind = ItemKind.Weapon, weapon = RandomWeapon(WeaponKind.Shot) };   // бронебойное ядро для полевой пушки
            return new MarketItem { kind = ItemKind.Shield, shield = RandomShield() };
        }
        if (category == 4)
        {
            // Купцы возят и обычные ловушки инженеров (резина у них своя) - так ловушки достаются и другим путям
            if (Random.value < 0.45f) return new MarketItem { kind = ItemKind.Trap, trap = Traps.RandomKit() };
            float r = Random.value;
            int mk = r < 0.6f ? 1 : (r < 0.9f ? 2 : 3);
            float pick = Random.value;
            ModuleKind mkind = Rivers.Any && pick < 0.25f ? ModuleKind.Drawbridge : ModuleKind.SteamRepeller;
            if (pick >= 0.5f && pick < 0.65f) mkind = ModuleKind.CannonLift;
            else if (pick >= 0.65f && pick < 0.8f) mkind = MagicShells.Kinds[Random.Range(0, MagicShells.Kinds.Length)];   // пачка снарядов (полная)
            else if (pick >= 0.8f && pick < 0.87f) mkind = ModuleKind.FireworkBow;   // редкий товар пиротехников
            return new MarketItem { kind = ItemKind.Module, kit = new ModuleKit { kind = mkind, mk = mk } };
        }

        // Наёмник: вид войск берём из казармы игрока (нет казарм - наёмники не предлагаются)
        UnitType u = BarracksController.FindType(MercPool[Random.Range(0, MercPool.Length)]);
        if (u == null) return null;
        var lot = new SoldierLot
        {
            unit = u,
            hearty = u.UsesPottage && Random.value < 0.25f,
            name = "«" + Nicknames[Random.Range(0, Nicknames.Length)] + "»"
        };
        if (u.usesWeapon && Random.value < 0.6f) lot.weapon = RandomWeapon(u.weaponKind);
        if (u.usesShield && Random.value < 0.4f) lot.shield = RandomShield();
        return new MarketItem { kind = ItemKind.Soldier, soldier = lot };
    }
}
