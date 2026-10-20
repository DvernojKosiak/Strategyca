using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Пушка (наземная, открывается с Ратуши Ур.3, доступна любому пути). Защитное здание: бьёт ядрами ПРЯМОЙ наводкой.
///  - Стрелять через стены и постройки не может: если между ней и врагом стена или дом - ищет другую цель.
///  - Ядро на попадании разлетается веером осколков вперёд: они ранят тех, кто стоит за целью.
///  - Модуль Инженерного двора «Подъёмник пушки»: раз в несколько секунд поднимает её - тогда пушка бьёт
///    навесом через стены и постройки (поднимается сама, когда цель есть, а прямого выстрела нет).
///  - Магические снаряды Магической оружейной (пачка ставится, как модуль; см. CannonShellSlot): огонь, лёд, чары.
///    Пачку купить можно и на рынке - ставит её любой город.
///  - Бронебойное ядро (Инженерный двор, лежит в Оружейной): «Зарядить» - берёт лучшее со склада. Сильнее, щит и
///    укрытие не спасают, осколки летят сквозь стены. Каждый выстрел изнашивает ядро; кончилось - гнездо пустеет
///    («Авто» - сразу берёт следующее). «Снять» - ядро обратно в Оружейную.
/// Три уровня: урон, дальность, перезарядка, число осколков. Рабочие не нужны. Вне зелёной зоны молчит.
/// Добавляется на здание «Пушка» в BuildingPlacer.
/// </summary>
public class GroundCannonController : MonoBehaviour, IModuleHost
{
    public const string TypeName = "Пушка";
    public const int RequiredHallLevel = 3;

    //                                          Ур.1   Ур.2   Ур.3
    static readonly float[] DamageByLevel =   { 40f,   58f,   80f };
    static readonly float[] RangeByLevel =    { 9f,    10.5f, 12f };
    static readonly float[] CooldownByLevel = { 3.4f,  3.0f,  2.6f };
    static readonly int[] FragsByLevel =      { 5,     6,     8 };
    static readonly float[] FragDmgByLevel =  { 12f,   16f,   21f };
    static readonly float[] FragRangeByLevel = { 2.2f, 2.5f,  2.8f };
    public const float MinRange = 1.6f;      // вплотную ствол не опустить
    const float FragSpread = 32f;

    BuildingInstance bld;
    CannonShellSlot shells;
    float timer, raiseDelay;
    Vector2 aimDir = Vector2.right;
    bool blockedView;                        // враг в радиусе есть, но за стеной (для панели)

    public int ShotsFired { get; private set; }

    // ---------- Бронебойное ядро (Weapon вида Shot из Оружейной) ----------
    Weapon apShot;
    public bool apAuto;                      // кончилось - взять следующее со склада
    float apAutoTimer;
    public Weapon ApShot => apShot;

    int Lvl => bld != null ? Mathf.Clamp(bld.level, 1, 3) - 1 : 0;
    public float Damage => DamageByLevel[Lvl];
    public float Range => RangeByLevel[Lvl] + (Lift.Up ? TowerModules.LiftRangeBonus : 0f);
    public float BaseRange => RangeByLevel[Lvl];
    public float Cooldown => CooldownByLevel[Lvl];
    public CannonShellSlot Shells { get { if (shells == null) shells = GetComponent<CannonShellSlot>(); return shells; } }

    public BuildingInstance Building
    {
        get { if (bld == null) bld = GetComponent<BuildingInstance>(); return bld; }
    }

    // ---------- Подъёмник (модуль Инженерии) ----------
    public readonly CannonLiftState Lift = new CannonLiftState();
    public ModuleKind Module => Lift.Has ? ModuleKind.CannonLift : ModuleKind.None;
    public int ModuleMk => Lift.mk;
    public bool HasModule => Lift.Has;
    public bool ModuleBusy { get; set; }
    public bool ModuleRemoving { get; set; }
    public string ModuleText => HasModule ? TowerModules.KitName(ModuleKind.CannonLift, ModuleMk) : "нет";

    public void SetModule(ModuleKind kind, int mk) { Lift.Set(kind == ModuleKind.CannonLift ? mk : 0); }

    public ModuleKit TakeModule()
    {
        ModuleKit kit = new ModuleKit { kind = Module, mk = ModuleMk };
        Lift.Set(0);
        return kit;
    }

    void Awake()
    {
        bld = GetComponent<BuildingInstance>();
        if (GetComponent<CannonShellSlot>() == null) gameObject.AddComponent<CannonShellSlot>();
        timer = 1f;
    }

    // ---------- Стрельба ----------

    void Update()
    {
        if (bld == null) bld = GetComponent<BuildingInstance>();
        UpdateLook();
        UpdateRangeCircle();
        if (bld == null || bld.IsRuined || !bld.InGreenZone) return;

        if (apAuto && apShot == null)
        {
            apAutoTimer -= Time.deltaTime;
            if (apAutoTimer <= 0f) { apAutoTimer = 2f; LoadApShot(false); }
        }

        timer -= Time.deltaTime;
        if (raiseDelay > 0f) { raiseDelay -= Time.deltaTime; return; }
        if (timer > 0f) return;

        Vector2 c = bld.Center;
        EnemyAgent direct = PickDirect(c, out EnemyAgent hidden);
        blockedView = direct == null && hidden != null;

        if (direct != null)
        {
            Fire(c, direct, false);
            return;
        }
        if (hidden != null && Lift.Has)
        {
            bool wasUp = Lift.Up;
            if (Lift.TryRaise())
            {
                if (!wasUp) { raiseDelay = 0.5f; return; }   // поднимается - через полсекунды выстрел
                Fire(c, hidden, true);
                return;
            }
        }
        timer = 0.3f;
    }

    // Лучшая цель прямой наводкой; hidden - лучшая из тех, кого закрывает стена / дом
    EnemyAgent PickDirect(Vector2 c, out EnemyAgent hidden)
    {
        hidden = null;
        EnemyAgent best = null;
        float bestScore = float.MinValue, bestHidden = float.MinValue;
        float range = Range;
        int checks = 0;

        // Ближние - первыми (проверка прямой дороже, чем расстояние)
        var cand = new List<EnemyAgent>();
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e) || e.IsSpy && !FogOfWar.IsVisible(e.Position)) continue;
            float d = Vector2.Distance(c, e.Position);
            if (d > range || d < MinRange) continue;
            cand.Add(e);
        }
        cand.Sort((a, b) => Vector2.Distance(c, a.Position).CompareTo(Vector2.Distance(c, b.Position)));

        foreach (var e in cand)
        {
            if (checks++ > 14) break;
            float score = Crowd(e) * 6f - Vector2.Distance(c, e.Position) * 0.5f;
            if (CannonBallistics.HomeLineClear(c, e.Position, bld))
            {
                if (score > bestScore) { bestScore = score; best = e; }
            }
            else if (score > bestHidden) { bestHidden = score; hidden = e; }
        }
        return best;
    }

    // Сколько врагов стоит за целью (в веере осколков) - туда выгоднее бить
    int Crowd(EnemyAgent e)
    {
        Vector2 c = bld.Center;
        Vector2 dir = (e.Position - c).normalized;
        float fr = FragRangeByLevel[Lvl] + 0.5f;
        int n = 0;
        foreach (var o in EnemyAgent.All)
        {
            if (o == e || !EnemyAgent.Alive(o)) continue;
            Vector2 to = o.Position - e.Position;
            float d = to.magnitude;
            if (d > fr) continue;
            if (d < 0.6f || Vector2.Angle(dir, to) <= FragSpread + 10f) n++;
        }
        return n;
    }

    void Fire(Vector2 c, EnemyAgent target, bool lob)
    {
        timer = Cooldown;
        ShotsFired++;
        aimDir = (target.Position - c).sqrMagnitude > 0.001f ? (target.Position - c).normalized : aimDir;

        CannonShot s = new CannonShot
        {
            damage = Damage,
            buildingMul = 1f,
            barrierMul = 1f,
            fragments = FragsByLevel[Lvl],
            fragDamage = FragDmgByLevel[Lvl],
            fragRange = FragRangeByLevel[Lvl],
            fragSpread = FragSpread,
            lob = lob,
            mana = ManaManager.Factor
        };
        if (apShot != null)
        {
            // Как у полевой пушки: сильнее, +1 осколок, пробивает заслоны; щит и укрытие не помогают (FireHome)
            s.armorPiercing = true;
            s.barrierMul *= 1.6f;
            s.fragments += 1;
            s.damage *= apShot.DamageMul;
            s.fragDamage *= apShot.DamageMul;
            if (apShot.Wear())
            {
                WarManager.Notify($"Пушка: израсходовано {apShot.Name.ToLower()}", false);
                apShot = null;
                apAutoTimer = 0f;
            }
        }
        CannonShellSlot sl = Shells;
        if (sl != null && sl.TakeShell(out ModuleKind kind, out int mk))
        {
            s.shell = kind;
            s.shellMk = mk;
            s.damage *= MagicShells.DamageMul(kind, mk, s.mana);
            s.fragDamage *= MagicShells.DamageMul(kind, mk, s.mana);
        }
        CannonBallistics.FireHome(c + aimDir * 0.6f, target, s, null, bld);
    }

    // ---------- Внешний вид: ствол смотрит на цель, поднятая пушка - выше и длиннее ----------

    Transform barrelPivot;
    SpriteRenderer barrel, platform;

    void UpdateLook()
    {
        if (bld == null) return;
        if (barrelPivot == null)
        {
            GameObject pivot = new GameObject("BarrelPivot");
            pivot.transform.SetParent(transform, false);
            barrelPivot = pivot.transform;

            GameObject pl = new GameObject("LiftPlatform");
            pl.transform.SetParent(transform, false);
            platform = pl.AddComponent<SpriteRenderer>();
            platform.sprite = CombatFx.Square();
            platform.color = new Color(0.55f, 0.42f, 0.25f);
            platform.sortingOrder = 2;
            pl.transform.localScale = new Vector3(0.9f, 0.9f, 1f);

            GameObject br = new GameObject("Barrel");
            br.transform.SetParent(barrelPivot, false);
            barrel = br.AddComponent<SpriteRenderer>();
            barrel.sprite = CombatFx.Square();
            barrel.color = new Color(0.16f, 0.16f, 0.18f);
            barrel.sortingOrder = 3;
        }

        // Ставим в центр здания (transform здания может быть не в центре)
        Vector3 local = transform.InverseTransformPoint(new Vector3(bld.Center.x, bld.Center.y, 0f));
        barrelPivot.localPosition = local;
        platform.transform.localPosition = local;

        bool up = Lift.Up && !bld.IsRuined;
        platform.enabled = Lift.Has && !bld.IsRuined;
        platform.color = up ? new Color(0.85f, 0.65f, 0.3f) : new Color(0.5f, 0.38f, 0.22f);
        barrel.enabled = !bld.IsRuined;

        Vector3 ls = transform.lossyScale;
        float sx = Mathf.Abs(ls.x) > 0.001f ? 1f / ls.x : 1f, sy = Mathf.Abs(ls.y) > 0.001f ? 1f / ls.y : 1f;
        float len = up ? 1.5f : 1.15f, thick = up ? 0.38f : 0.3f;
        barrelPivot.localScale = new Vector3(sx, sy, 1f);   // не тянется вместе со зданием
        barrel.transform.localScale = new Vector3(len, thick, 1f);
        barrel.transform.localPosition = new Vector3(len * 0.5f, 0f, 0f);
        float ang = Mathf.Atan2(aimDir.y, aimDir.x) * Mathf.Rad2Deg;
        barrelPivot.localRotation = Quaternion.Euler(0f, 0f, ang);
        platform.transform.localScale = new Vector3(sx * 1.3f, sy * 1.3f, 1f);
    }

    // ---------- Круг дальности (виден, пока здание выбрано) ----------

    LineRenderer circle;
    int shownFrame = -10;

    public void ShowRadiusThisFrame() { shownFrame = Time.frameCount; }

    void UpdateRangeCircle()
    {
        bool show = Time.frameCount - shownFrame <= 2 && bld != null && !bld.IsRuined;
        if (!show) { if (circle != null) circle.enabled = false; return; }

        if (circle == null)
        {
            GameObject go = new GameObject("GroundCannonRange");
            circle = go.AddComponent<LineRenderer>();
            circle.useWorldSpace = true;
            circle.loop = true;
            circle.material = new Material(Shader.Find("Sprites/Default"));
            circle.widthMultiplier = 0.08f;
            circle.startColor = circle.endColor = new Color(1f, 0.75f, 0.35f, 0.5f);
            circle.sortingOrder = -40;
            circle.positionCount = 72;
        }
        circle.enabled = true;
        Vector2 c = bld.Center;
        float r = Range;
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72;
            circle.SetPosition(i, new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f));
        }
    }

    void OnDestroy()
    {
        if (circle != null) Destroy(circle.gameObject);
        // Пушку снесли - ядро не пропадает, если в Оружейной есть место
        if (apShot != null && ArmoryController.Stash(apShot)) apShot = null;
    }

    // ---------- Бронебойное ядро: зарядить / снять ----------

    /// <summary>Взять лучшее ядро из Оружейной (если уже заряжено - старое уходит на склад). false - не вышло.</summary>
    public bool LoadApShot(bool notify)
    {
        var opts = ArmoryController.Available(WeaponKind.Shot);
        if (opts.Count == 0)
        {
            if (notify) WarManager.Notify("Пушка: в Оружейной нет бронебойных ядер", false);
            return false;
        }
        var best = opts[0];
        if (apShot != null && best.weapon.Score <= apShot.Score)
        {
            if (notify) WarManager.Notify("Пушка: на складе нет ядра лучше заряженного", false);
            return false;
        }
        if (!best.armory.RemoveFromStock(best.weapon)) return false;
        if (apShot != null)
        {
            best.armory.AddToStock(apShot);   // место освободилось - старое кладём туда же
        }
        apShot = best.weapon;
        if (notify || !apAuto) WarManager.Notify($"Пушка: заряжено {apShot.Name.ToLower()}", true);
        else WarManager.Notify($"Пушка: взято новое ядро ({apShot.Name.ToLower()})", true);
        return true;
    }

    /// <summary>Снять ядро обратно в Оружейную. false - места нет.</summary>
    public bool UnloadApShot()
    {
        if (apShot == null) return true;
        if (!ArmoryController.Stash(apShot))
        {
            WarManager.Notify("Пушка: в Оружейных нет места для ядра", false);
            return false;
        }
        apShot = null;
        apAuto = false;   // сняли руками - само не заряжается
        return true;
    }

    const float ApSectionH = 22f + 18f + 4f + 28f;

    void DrawApSection(float x, float y, float w)
    {
        GUIStyle st = UiKit.Body;
        int avail = ArmoryController.Available(WeaponKind.Shot).Count;
        string head = apShot != null
            ? $"<b>{apShot.Name}</b>"
            : UiKit.Col(UiKit.MutedHex, "нет") + (apAuto ? "  " + UiKit.Col(UiKit.WarnHex, "ждёт со склада") : "");
        Rect hr = new Rect(x, y, w, 22f);
        GUI.Label(hr, "Бронебойное ядро: " + head, st);
        UiKit.Tip(hr, "Бронебойное ядро (Инженерный двор, хранится в Оружейной). Урон сильнее, +1 осколок, " +
                      "щит и укрытие не спасают, осколки летят сквозь стены. Каждый выстрел изнашивает ядро.");
        float cy = y + 22f;

        float frac = apShot != null ? apShot.Frac : 0f;
        string bt = apShot != null ? $"выстрелов: {Mathf.CeilToInt(apShot.durability)} · урон ×{apShot.DamageMul:F2}" : "обычные ядра";
        UiKit.Bar(new Rect(x, cy, w, 16f), frac, UiKit.Accent, bt);
        cy += 18f + 4f;

        float bw = (w - 8f) / 3f;
        string lt = avail > 0 ? $"Взять лучшее из Оружейной (там: {avail})" : "В Оружейной нет ядер - их делает Инженерный двор";
        if (UiKit.Button(new Rect(x, cy, bw, 28f), $"Зарядить ({avail})", avail > 0, UiKit.Kind.Primary, lt))
            LoadApShot(true);
        bool newAuto = GUI.Toggle(new Rect(x + bw + 4f, cy, bw, 28f), apAuto, apAuto ? "Авто ✓" : "Авто", GUI.skin.button);
        if (newAuto != apAuto) { apAuto = newAuto; apAutoTimer = 0f; }
        UiKit.Tip(new Rect(x + bw + 4f, cy, bw, 28f), "Ядро кончилось - сразу взять следующее из Оружейной");
        bool slots = ArmoryController.FreeSlotsTotal > 0;
        if (UiKit.Button(new Rect(x + 2f * (bw + 4f), cy, bw, 28f), "Снять", apShot != null && slots, UiKit.Kind.Danger,
                apShot == null ? "Ядро не заряжено" : (slots ? "Ядро - обратно в Оружейную" : "В Оружейных нет места")))
            UnloadApShot();
    }

    // ---------- Панель здания ----------

    public Rect DrawPanel(float x, float y, float w)
    {
        ShowRadiusThisFrame();
        if (bld == null) bld = GetComponent<BuildingInstance>();
        BuildingInstance b = bld;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        float shellH = CannonShellSlot.SectionHeight(Shells);
        float liftH = EngineerYard.HostSectionHeight(this, ModuleKind.CannonLift);
        float stateY = UiKit.TitleH + 6f;
        float statsY = stateY + 22f;
        float apY = statsY + 24f;
        float shellY = apY + ApSectionH + 6f;
        float liftY = shellY + shellH + (shellH > 0f ? 6f : 0f);
        float upgradeY = liftY + liftH + (liftH > 0f ? 4f : 0f);
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        string state = b.IsRuined ? UiKit.Col(UiKit.BadHex, "Разрушена")
                     : (!b.InGreenZone ? UiKit.Col(UiKit.BadHex, "Вне зелёной зоны - не стреляет")
                     : (blockedView && !Lift.Has ? UiKit.Col(UiKit.WarnHex, "Враг за стеной - не достать") : UiKit.Col(UiKit.GoodHex, "Готова к бою")));
        if (Lift.Has && !b.IsRuined) state += "  " + UiKit.Col(UiKit.MutedHex, "·") + " " + Lift.StateText;
        GUI.Label(new Rect(x + 10f, y + stateY, w - 20f, 20f), state, st);

        int lvl = Lvl;
        Rect sr = new Rect(x + 10f, y + statsY, w - 20f, 20f);
        GUI.Label(sr, $"Ядро <b>{Damage:F0}</b> · осколки {FragsByLevel[lvl]}×{FragDmgByLevel[lvl]:F0} · дальность <b>{BaseRange:F1}</b> · {Cooldown:F1} с", small);
        UiKit.Tip(sr,
            $"Ядро: {Damage:F0} урона. Осколков: {FragsByLevel[lvl]} по {FragDmgByLevel[lvl]:F0}, летят на {FragRangeByLevel[lvl]:F1} кл. и ранят тех, кто стоит за целью.\n" +
            $"Дальность: {BaseRange:F1} кл. (вплотную - не ближе {MinRange:F1}). Перезарядка: {Cooldown:F1} с. Выстрелов: {ShotsFired}.\n" +
            "Бьёт прямой наводкой: через стены и дома не стреляет. Подъёмник (модуль) позволяет бить навесом.");

        UiKit.Line(x + 10f, y + apY - 3f, w - 20f);
        DrawApSection(x + 10f, y + apY, w - 20f);
        if (shellH > 0f || liftH > 0f) UiKit.Line(x + 10f, y + shellY - 3f, w - 20f);
        if (shellH > 0f) CannonShellSlot.DrawSection(x + 10f, y + shellY, w - 20f, Shells);
        if (liftH > 0f) EngineerYard.DrawHostSection(x + 10f, y + liftY, w - 20f, this, ModuleKind.CannonLift);

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}

/// <summary>
/// Гнездо магических снарядов на пушке. Пачку (модуль из Магической оружейной / с рынка) приносит житель,
/// как модуль Инженерного двора (EngineerYard.Install). Каждый выстрел тратит снаряд; пачка кончилась - гнездо пустеет.
/// «Снять» - неизрасходованные снаряды уносят обратно на склад.
/// </summary>
public class CannonShellSlot : MonoBehaviour, IModuleHost
{
    ModuleKind kind = ModuleKind.None;
    int mk, charges;

    public BuildingInstance Building => GetComponent<BuildingInstance>();
    public ModuleKind Module => kind;
    public int ModuleMk => mk;
    public bool HasModule => kind != ModuleKind.None && charges > 0;
    public bool ModuleBusy { get; set; }
    public bool ModuleRemoving { get; set; }
    public int Charges => charges;

    public void SetModule(ModuleKind k, int m)
    {
        kind = MagicShells.IsShell(k) ? k : ModuleKind.None;
        mk = kind == ModuleKind.None ? 0 : Mathf.Clamp(m, 1, 3);
        charges = kind == ModuleKind.None ? 0 : MagicShells.Charges(mk);
    }

    /// <summary>Пачку уже начинали: столько снарядов в ней осталось (EngineerYard.Install вызывает после SetModule).</summary>
    public void SetCharges(int left) { if (kind != ModuleKind.None && left > 0) charges = Mathf.Min(left, MagicShells.Charges(mk)); }

    public ModuleKit TakeModule()
    {
        ModuleKit kit = new ModuleKit { kind = kind, mk = mk, charges = charges < MagicShells.Charges(mk) ? charges : 0 };
        kind = ModuleKind.None;
        mk = charges = 0;
        return kit;
    }

    /// <summary>Выстрел магическим снарядом: true - снаряд есть (тратится).</summary>
    public bool TakeShell(out ModuleKind k, out int m)
    {
        k = kind;
        m = mk;
        if (!HasModule) return false;
        charges--;
        if (charges <= 0)
        {
            WarManager.Notify($"Пушка: кончились {TowerModules.Name(kind).ToLower()}", false);
            kind = ModuleKind.None;
            mk = 0;
            charges = 0;
        }
        return true;
    }

    // ---------- Раздел панели пушки ----------

    public static bool ShowSection(CannonShellSlot s)
    {
        return s != null && (DevelopmentPath.IsMagic || s.HasModule || s.ModuleBusy || MagicShells.TotalPacks() > 0);
    }

    public static float SectionHeight(CannonShellSlot s)
    {
        if (!ShowSection(s)) return 0f;
        return 22f + (s.HasModule ? 34f : 0f) + 32f;
    }

    public static void DrawSection(float x, float y, float w, CannonShellSlot s)
    {
        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        float cy = y;
        string head = s.HasModule
            ? $"<color=#D0A0FF><b>{TowerModules.Name(s.kind)} Мк.{TowerModules.Roman(s.mk)}</b></color> " + UiKit.Col(UiKit.MutedHex, "·") + $" осталось <b>{s.charges}</b>"
            : UiKit.Col(UiKit.MutedHex, "обычные ядра");
        if (s.ModuleBusy) head += "  " + UiKit.Col(UiKit.WarnHex, s.ModuleRemoving ? "уносят" : "несут пачку");
        GUI.Label(new Rect(x, cy, w, 22f), "Снаряды: " + head, st);
        cy += 22f;
        if (s.HasModule)
        {
            GUI.Label(new Rect(x, cy, w, 34f), MagicShells.Stats(s.kind, s.mk), small);
            cy += 34f;
        }

        float removeW = s.HasModule ? 64f : 0f;
        float bw = (w - removeW - (s.HasModule ? 4f : 0f) - 8f) / 3f;
        float bx = x;
        foreach (var k in MagicShells.Kinds)
        {
            int n = MagicShells.Packs(k);
            string p = EngineerYard.InstallProblem(s, k);
            bool ok = p == "" && n > 0;
            string shortName = k == ModuleKind.FireShells ? "Огонь" : (k == ModuleKind.FrostShells ? "Лёд" : "Чары");
            string tip = ok ? $"Поставить: {TowerModules.Name(k)} (пачек в городе: {n})"
                            : (n <= 0 ? $"{TowerModules.Name(k)}: пачек нет - их делает Магическая оружейная или продаёт рынок" : p);
            if (UiKit.Button(new Rect(bx, cy, bw, 28f), $"{shortName} ({n})", ok, UiKit.Kind.Normal, tip)) EngineerYard.Install(s, k);
            bx += bw + 4f;
        }
        if (s.HasModule)
        {
            string rp = EngineerYard.RemoveProblem(s);
            if (UiKit.Button(new Rect(x + w - removeW, cy, removeW, 28f), "Снять", rp == "", UiKit.Kind.Danger,
                    rp == "" ? "Неизрасходованные снаряды унесут обратно" : rp))
                EngineerYard.Remove(s);
        }
    }
}
