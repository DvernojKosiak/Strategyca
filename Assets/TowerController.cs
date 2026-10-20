using UnityEngine;

/// <summary>
/// Башня стреляет по врагам в радиусе. Урон и дальность растут с уровнем.
/// Разрушенная башня не стреляет. Рабочие не нужны.
/// Путь Инженерии: на башню можно поставить модуль из Инженерного двора (см. EngineerYard, TowerModules).
///  - Паровой отбойник: кроме обычных стрел, раз в несколько секунд бьёт ядром на пару - ближайшего врага
///    и всех рядом с ним отбрасывает от башни и ненадолго оглушает.
///  - Фейерверочный лук (Пиротехник): стрелы чаще, но слабее; каждая рвётся - взрыв и искры бьют врагов
///    рядом с целью (Pyrotechnics.FireworkArrow, числа - TowerModules).
/// На башне стоит только один модуль: новый заменяет старый (старый - обратно на склад).
/// Модуль остаётся на башне и в руинах (заработает, когда её отстроят). Снесли башню - модуль пропал.
/// Добавляется на здание "Башня" в BuildingPlacer.
/// </summary>
public class TowerController : MonoBehaviour, IModuleHost
{
    public float baseRange = 7f;
    public float rangePerLevel = 1.5f;
    public float baseDamage = 9f;
    public float cooldown = 1.1f;

    BuildingInstance bld;
    float timer;

    int Level => bld != null ? bld.level : 1;
    public float Range => baseRange + rangePerLevel * (Level - 1);
    public float Damage => baseDamage * Level;
    bool Firework => Module == ModuleKind.FireworkBow;
    /// <summary>Урон одной стрелы с учётом модуля (Фейерверочный лук - слабее, но чаще).</summary>
    public float ArrowDamage => Firework ? Damage * TowerModules.FireworkDamageMul(ModuleMk) : Damage;
    public float ArrowCooldown => Firework ? cooldown * TowerModules.FireworkCooldownMul(ModuleMk) : cooldown;

    public BuildingInstance Building
    {
        get { if (bld == null) bld = GetComponent<BuildingInstance>(); return bld; }
    }

    // ---------- Модуль (путь Инженерии) ----------
    public ModuleKind Module { get; private set; } = ModuleKind.None;
    public int ModuleMk { get; private set; }
    public bool HasModule => Module != ModuleKind.None;
    /// <summary>Инженер сейчас несёт модуль на башню или уносит с неё (ставит EngineerYard).</summary>
    public bool ModuleBusy { get; set; }
    public bool ModuleRemoving { get; set; }   // именно уносят (для подписи в панели)

    float moduleTimer;

    public void SetModule(ModuleKind kind, int mk)
    {
        Module = kind;
        ModuleMk = kind == ModuleKind.None ? 0 : Mathf.Clamp(mk, 1, 3);
        moduleTimer = 1f;   // первый выстрел - почти сразу после установки
    }

    public ModuleKit TakeModule()
    {
        ModuleKit kit = new ModuleKit { kind = Module, mk = ModuleMk };
        Module = ModuleKind.None;
        ModuleMk = 0;
        return kit;
    }

    public string ModuleText => HasModule ? TowerModules.KitName(Module, ModuleMk) : "нет";

    void Update()
    {
        if (bld == null) bld = GetComponent<BuildingInstance>();
        if (bld == null || bld.IsRuined || !bld.InGreenZone) return;   // вне зелёной зоны не стреляет

        if (Module == ModuleKind.SteamRepeller) TickRepeller();

        timer -= Time.deltaTime;
        if (timer > 0f) return;

        Vector2 c = bld.Center;
        EnemyAgent target = EnemyAgent.FindNearest(c, Range);
        if (target == null) { timer = 0.25f; return; }

        timer = ArrowCooldown;
        if (Firework)
        {
            Pyrotechnics.FireworkArrow(bld, target, ModuleMk, ArrowDamage);
            return;
        }
        CombatFx.Arrow(c, target.Position);
        target.TakeDamage(Damage, null, bld);
    }

    // Паровой отбойник: бьёт ближайшего к башне врага, который ещё не оглушён (оглушённых и так отбросило)
    void TickRepeller()
    {
        moduleTimer -= Time.deltaTime;
        if (moduleTimer > 0f) return;

        Vector2 c = bld.Center;
        EnemyAgent target = EnemyAgent.FindNearest(c, Range, e => !e.IsStunned);
        if (target == null) { moduleTimer = 0.3f; return; }

        moduleTimer = TowerModules.RepellerCooldown(ModuleMk);
        TowerModules.FireRepeller(bld, target, ModuleMk, Damage);
    }
}
