using UnityEngine;
using System.Collections.Generic;

/// <summary>Модуль, который Инженерный двор собирает для постройки (путь Инженерии).</summary>
// Новые виды - только в конец. Магические снаряды (Fire/Frost/Arcane) - пачки снарядов для пушек (см. MagicShells),
// ApShot - не модуль, а чертёж бронебойного ядра (готовые ядра уходят в Оружейную как оружие полевой пушки).
// FireworkBow - Фейерверочный лук для башни (только Пиротехник, см. Pyrotechnics).
// ChaosCore - ядро хаоса для Мага стихий (школа Хаоса, см. ChaosMagic): на пушки и постройки не ставится.
public enum ModuleKind { None, SteamRepeller, Drawbridge, CannonLift, FireShells, FrostShells, ArcaneShells, ApShot, ChaosCore, FireworkBow }

/// <summary>
/// Постройка, на которую ставят модуль Инженерного двора: башня (Паровой отбойник) или мост (Раздвижной механизм).
/// EngineerYard носит модули на такие постройки и снимает их.
/// </summary>
public interface IModuleHost
{
    BuildingInstance Building { get; }
    ModuleKind Module { get; }
    int ModuleMk { get; }
    bool HasModule { get; }
    bool ModuleBusy { get; set; }      // инженер сейчас несёт модуль сюда или уносит отсюда
    bool ModuleRemoving { get; set; }  // именно уносят (для подписи в панели)
    void SetModule(ModuleKind kind, int mk);
    ModuleKit TakeModule();
}

/// <summary>Готовый модуль на складе Инженерного двора. mk - марка (I-III): уровень двора, на котором его собрали.
/// charges - только у пачки магических снарядов: сколько снарядов осталось (0 - пачка целая).</summary>
[System.Serializable]
public struct ModuleKit
{
    public ModuleKind kind;
    public int mk;
    public int charges;
}

/// <summary>Чертёж модуля: из чего и сколько собирать в Инженерном дворе, на какое здание ставится.</summary>
public class ModuleRecipe
{
    public ModuleKind kind;
    public string name;
    public string target = "Башня";   // тип здания, на которое ставится модуль
    public string desc;

    public int gold, parts, steel, planks;
    public int resin;                 // смола (горючая смесь пиротехники)
    public EngSchool school = EngSchool.None;   // только для этого направления Инженерии
    public float seconds = 30f;
    public int minLevel = 1;          // с какого уровня двора доступен чертёж
}

/// <summary>
/// Модули построек (путь Инженерии). Всё про модули в одном месте:
///  - чертежи (что собирает Инженерный двор и из чего);
///  - Паровой отбойник для башни: раз в несколько секунд башня бьёт тяжёлым ядром на пару,
///    цель и всех рядом отбрасывает от башни и ненадолго оглушает. Лёгких (лучник, маг) относит далеко,
///    мечников - средне, рыцарей и конницу - чуть-чуть (EnemyAgent.KnockFactor).
///    Влетел в стену / здание - получает удар о камень.
/// Сила модуля зависит от марки (Мк.I-III) = уровня Инженерного двора, на котором его собрали.
/// Все числа - в таблицах ниже.
/// </summary>
public static class TowerModules
{
    public static readonly ModuleRecipe[] Recipes =
    {
        new ModuleRecipe
        {
            kind = ModuleKind.SteamRepeller,
            name = "Паровой отбойник",
            target = "Башня",
            desc = "Башня бьёт ядром на пару: отбрасывает и оглушает врагов",
            gold = 60, parts = 8, steel = 2, planks = 4,
            seconds = 30f, minLevel = 1
        },
        new ModuleRecipe
        {
            kind = ModuleKind.Drawbridge,
            name = "Раздвижной механизм",
            target = BuildingInstance.BridgeName,
            desc = "Мост можно поднять: по нему не пройдёт никто — ни враги, ни свои",
            gold = 50, parts = 6, steel = 2, planks = 6,
            seconds = 35f, minLevel = 1
        },
        new ModuleRecipe
        {
            kind = ModuleKind.CannonLift,
            name = "Подъёмник пушки",
            target = GroundCannonController.TypeName,
            desc = "Раз в несколько секунд поднимает пушку: она бьёт навесом через стены и дома. Ставится и на полевые пушки (берут сами)",
            gold = 70, parts = 10, steel = 4, planks = 4,
            seconds = 40f, minLevel = 1
        },
        new ModuleRecipe
        {
            kind = ModuleKind.ApShot,
            name = "Бронебойное ядро",
            target = "Пушки",
            desc = "Ядро полевой пушки и Пушки (в Оружейную), расходуется: прошивает заслоны и стены, осколки летят сквозь укрепления",
            gold = 40, parts = 6, steel = 4, planks = 0,
            seconds = 30f, minLevel = 1
        },
        new ModuleRecipe
        {
            kind = ModuleKind.FireworkBow,
            name = "Фейерверочный лук",
            target = "Башня",
            desc = "Башня стреляет чаще, но слабее; стрелы-ракеты рвутся, разбрасывая жгучие искры",
            gold = 60, parts = 8, steel = 1, planks = 3, resin = 4,
            seconds = 30f, minLevel = 1, school = EngSchool.Pyro
        },
    };

    /// <summary>Чертёж доступен при текущем пути (Фейерверочный лук - только Пиротехнику). Путь может смениться - проверять всегда.</summary>
    public static bool Available(ModuleRecipe r) { return r != null && DevelopmentPath.EngAllows(r.school); }

    /// <summary>Модули, которые ставятся на башню (на башне стоит только один из них).</summary>
    public static readonly ModuleKind[] TowerKinds = { ModuleKind.SteamRepeller, ModuleKind.FireworkBow };

    /// <summary>Пачки магических снарядов (их делает Магическая оружейная, не Инженерный двор).</summary>
    public static readonly ModuleRecipe[] ShellRecipes =
    {
        new ModuleRecipe { kind = ModuleKind.FireShells, name = "Огненные снаряды", target = GroundCannonController.TypeName,
                           desc = "Взрыв огня и поджог", gold = 30, seconds = 22f, minLevel = 1 },
        new ModuleRecipe { kind = ModuleKind.FrostShells, name = "Ледяные снаряды", target = GroundCannonController.TypeName,
                           desc = "Сильно замедляют врагов", gold = 30, seconds = 22f, minLevel = 1 },
        new ModuleRecipe { kind = ModuleKind.ArcaneShells, name = "Чародейские снаряды", target = GroundCannonController.TypeName,
                           desc = "Магия: щит и укрытие не спасают, осколки ищут врагов", gold = 45, seconds = 28f, minLevel = 2 },
    };

    /// <summary>Ядро хаоса (Магическая оружейная, только школа Хаоса): Маг стихий бросает чёрную дыру. Не снаряд.</summary>
    public static readonly ModuleRecipe ChaosCoreRecipe =
        new ModuleRecipe { kind = ModuleKind.ChaosCore, name = "Ядро хаоса", target = BarracksController.ElementalistName,
                           desc = "Маг стихий иногда бросает чёрную дыру: урона мало, стягивает врагов в одну точку",
                           gold = 60, seconds = 35f, minLevel = 1 };

    public static ModuleRecipe RecipeOf(ModuleKind k)
    {
        foreach (var r in Recipes) if (r.kind == k) return r;
        foreach (var r in ShellRecipes) if (r.kind == k) return r;
        if (k == ModuleKind.ChaosCore) return ChaosCoreRecipe;
        return null;
    }

    public static string Name(ModuleKind k)
    {
        ModuleRecipe r = RecipeOf(k);
        return r != null ? r.name : "нет";
    }

    public static string Roman(int mk)
    {
        switch (Mathf.Clamp(mk, 1, 3)) { case 1: return "I"; case 2: return "II"; default: return "III"; }
    }

    public static string KitName(ModuleKit kit)
    {
        string left = MagicShells.IsShell(kit.kind) && kit.charges > 0 ? $" ({kit.charges} шт.)"
                    : (kit.kind == ModuleKind.ChaosCore && kit.charges > 0 ? $" (бросков: {kit.charges})" : "");
        return $"{Name(kit.kind)} Мк.{Roman(kit.mk)}{left}";
    }
    public static string KitName(ModuleKind k, int mk) { return $"{Name(k)} Мк.{Roman(mk)}"; }

    /// <summary>Строка характеристик модуля этой марки (для панели двора и постройки).</summary>
    public static string Stats(ModuleKind k, int mk)
    {
        switch (k)
        {
            case ModuleKind.SteamRepeller: return RepellerStats(mk);
            case ModuleKind.Drawbridge: return DrawbridgeStats(mk);
            case ModuleKind.CannonLift: return LiftStats(mk);
            case ModuleKind.ApShot: return ApShotStats(mk);
            case ModuleKind.ChaosCore: return ChaosMagic.Stats(mk);
            case ModuleKind.FireworkBow: return FireworkStats(mk);
            default: return MagicShells.IsShell(k) ? MagicShells.Stats(k, mk) : "";
        }
    }

    // ---------- Раздвижной механизм моста ----------
    //                                           Мк.I   Мк.II  Мк.III
    static readonly float[] BridgeRaise =      { 4.0f,  2.5f,  1.5f };   // за сколько секунд мост поднимается / опускается
    static readonly float[] BridgeRaisedArmor = { 0.6f, 0.45f, 0.3f };  // поднятый мост получает только эту долю урона

    public static float DrawbridgeSeconds(int mk) { return BridgeRaise[I(mk)]; }
    public static float DrawbridgeArmor(int mk) { return BridgeRaisedArmor[I(mk)]; }
    /// <summary>С Мк.II механизм сам поднимает мост, когда к нему подходят враги.</summary>
    public static bool DrawbridgeAuto(int mk) { return mk >= 2; }

    public static string DrawbridgeStats(int mk)
    {
        int i = I(mk);
        string auto = DrawbridgeAuto(mk) ? ", сам поднимается при врагах" : ", поднимают вручную";
        return $"подъём за {BridgeRaise[i]:F1} с{auto}, поднятый получает {BridgeRaisedArmor[i] * 100f:F0}% урона";
    }

    // ---------- Подъёмник пушки ----------
    //                                           Мк.I   Мк.II  Мк.III
    static readonly float[] LiftUp =           { 4f,    5f,    6f };     // сколько секунд пушка поднята
    static readonly float[] LiftRest =         { 11f,   9f,    7f };     // потом столько опускается и ждёт
    public const float LiftRangeBonus = 1f;                              // поднятая бьёт чуть дальше

    public static float LiftSeconds(int mk) { return LiftUp[I(mk)]; }
    public static float LiftCooldown(int mk) { return LiftRest[I(mk)]; }

    public static string LiftStats(int mk)
    {
        int i = I(mk);
        return $"поднимается на {LiftUp[i]:F0} с раз в {LiftUp[i] + LiftRest[i]:F0} с: бьёт навесом через стены, дальность +{LiftRangeBonus:F0}";
    }

    // ---------- Бронебойное ядро (чертёж: готовое ядро - оружие полевой пушки, лежит в Оружейной) ----------

    /// <summary>Качество ядра по марке двора: Мк.I - обычное, Мк.II - хорошее, Мк.III - превосходное.</summary>
    public static QualityTier ApShotQuality(int mk) { return mk >= 3 ? QualityTier.Best : (mk == 2 ? QualityTier.Good : QualityTier.Medium); }

    public static string ApShotStats(int mk)
    {
        Weapon w = new Weapon(WeaponKind.Shot, ApShotQuality(mk));
        return $"{w.Name}: урон ×{w.DamageMul:F2}, {w.maxDurability:F0} выстрелов, прошивает укрепления";
    }

    // ---------- Фейерверочный лук (выстрел - Pyrotechnics.FireworkArrow) ----------
    //                                           Мк.I   Мк.II  Мк.III
    static readonly float[] FwCooldown =       { 0.6f,  0.55f, 0.5f };   // перезарядка башни ×
    static readonly float[] FwDamage =         { 0.6f,  0.62f, 0.65f };  // урон стрелы ×
    static readonly float[] FwRadius =         { 1.0f,  1.1f,  1.25f };  // радиус взрыва вокруг цели
    static readonly float[] FwSplash =         { 0.35f, 0.4f,  0.45f };  // урон взрыва (соседям) от урона стрелы
    static readonly int[] FwSparks =           { 3,     4,     5 };      // искр от взрыва
    static readonly float[] FwSparkDmg =       { 0.15f, 0.18f, 0.2f };   // урон искры от урона стрелы
    static readonly float[] FwSparkRange =     { 1.1f,  1.2f,  1.35f };  // на сколько клеток летят искры

    public static float FireworkCooldownMul(int mk) { return FwCooldown[I(mk)]; }
    public static float FireworkDamageMul(int mk) { return FwDamage[I(mk)]; }
    public static float FireworkBurstRadius(int mk) { return FwRadius[I(mk)]; }
    public static float FireworkSplashMul(int mk) { return FwSplash[I(mk)]; }
    public static int FireworkSparks(int mk) { return FwSparks[I(mk)]; }
    public static float FireworkSparkMul(int mk) { return FwSparkDmg[I(mk)]; }
    public static float FireworkSparkRange(int mk) { return FwSparkRange[I(mk)]; }

    public static string FireworkStats(int mk)
    {
        int i = I(mk);
        return $"выстрелы ×{1f / FwCooldown[i]:F1} чаще, урон стрелы ×{FwDamage[i]:F2}; взрыв r{FwRadius[i]:F1} — {FwSplash[i] * 100f:F0}% соседям, {FwSparks[i]} искр по {FwSparkDmg[i] * 100f:F0}%";
    }

    public const string FireworkHint = "Цель получает стрелу, враги вокруг неё — взрыв, искры летят во все стороны и жгут первого встречного. Хорошо против толпы, хуже против одного рыцаря.";

    // ---------- Паровой отбойник ----------
    //                                           Мк.I   Мк.II  Мк.III
    static readonly float[] RepCooldown =      { 5.0f,  4.2f,  3.5f };   // секунд между выстрелами
    static readonly float[] RepPush =          { 2.4f,  3.0f,  3.6f };   // на сколько клеток относит лёгкого врага
    static readonly float[] RepStun =          { 0.6f,  0.8f,  1.0f };   // оглушение после полёта (лёгкого), с
    static readonly float[] RepSplash =        { 0.9f,  1.1f,  1.3f };   // радиус облака пара - задевает соседей
    static readonly float[] RepDamageMul =     { 0.6f,  0.75f, 0.9f };   // урон ядра от урона башни
    static readonly float[] RepWallDamage =    { 10f,   15f,   20f };    // удар о стену / здание
    const float SplashPushMul = 0.6f;    // соседей относит слабее, чем цель
    const float WheeledPushMul = 0.5f;   // заслон на колёсах (FieldPost.Shove) относит вдвое слабее лёгкого врага
    const float SplashDamageMul = 0.5f;

    static int I(int mk) { return Mathf.Clamp(mk, 1, 3) - 1; }

    public static float RepellerCooldown(int mk) { return RepCooldown[I(mk)]; }
    public static float RepellerPush(int mk) { return RepPush[I(mk)]; }

    /// <summary>Короткая строка характеристик для панели башни / двора.</summary>
    public static string RepellerStats(int mk)
    {
        int i = I(mk);
        return $"отброс до {RepPush[i]:F1} кл., раз в {RepCooldown[i]:F1} с, оглушение {RepStun[i]:F1} с";
    }

    public const string RepellerWeightHint = "Лучников и магов относит далеко, мечников — средне, рыцарей и конницу — чуть-чуть";

    /// <summary>Выстрел отбойника: ядро летит из башни в цель, на попадании - отбрасывание и урон.</summary>
    public static void FireRepeller(BuildingInstance tower, EnemyAgent target, int mk, float towerDamage)
    {
        if (tower == null || !EnemyAgent.Alive(target)) return;
        int i = I(mk);
        Vector2 from = tower.Center;
        Vector2 aim = target.Position;
        float splash = RepSplash[i];

        CombatFx.Slug(from, aim, splash, () =>
        {
            // Цель за время полёта могла отойти - бьём туда, где она сейчас, если недалеко
            Vector2 hitAt = EnemyAgent.Alive(target) && Vector2.Distance(target.Position, aim) < 1.2f ? target.Position : aim;

            var hit = new List<EnemyAgent>();
            foreach (var e in EnemyAgent.All)
            {
                if (!EnemyAgent.Alive(e) || e.Unit == null) continue;
                if (Vector2.Distance(e.Position, hitAt) <= splash + e.Unit.bodyScale * 0.3f) hit.Add(e);
            }

            BuildingInstance by = tower != null ? tower : null;   // башню могли снести, пока летело ядро
            foreach (var e in hit)
            {
                bool main = e == target;
                float dmg = towerDamage * RepDamageMul[i] * (main ? 1f : SplashDamageMul);
                e.TakeDamage(dmg, null, by);
                if (!EnemyAgent.Alive(e)) continue;

                float k = e.KnockFactor * (main ? 1f : SplashPushMul);
                Vector2 dir = e.Position - from;            // всегда от башни
                float stun = RepStun[i] * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(k));
                e.Knockback(dir, RepPush[i] * k, stun, RepWallDamage[i], by);
            }

            // Заслон на колёсах рядом с местом удара тоже отбрасывает (от башни) - он лёгкий, катится
            FieldPost.ShoveFrom(from, hitAt, splash + 1f, RepPush[i] * WheeledPushMul, false);
        });
    }
}
