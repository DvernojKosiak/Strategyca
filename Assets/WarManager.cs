using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Режим войны и тестовые набеги.
///  - Набег вызывается горячей клавишей (в интерфейсе нигде не подписана).
///  - Пока на карте есть враги (и ещё warCooldown секунд после последнего) - режим войны:
///    новые жители не прибывают, мирные прячутся от врагов в здания.
///  - Рисует полоски здоровья над людьми, врагами и повреждёнными зданиями.
/// Создаётся в BuildingPlacer.Start.
/// </summary>
public class WarManager : MonoBehaviour
{
    public static WarManager Instance { get; private set; }

    // Идёт война: на карте враги (или были совсем недавно)
    public static bool IsWar => Instance != null && Instance.war;

    [Header("Режим войны")]
    public float warCooldown = 30f;   // сколько секунд после последнего врага ещё длится война

    [Header("Тестовый набег")]
    public int smallWave = 5;
    public int bigWave = 12;

    [Header("Периодические набеги (сами, без клавиши)")]
    public bool autoRaids = true;
    public float firstRaidDelay = 600f;    // секунд игрового времени до первого набега
    public float raidIntervalMin = 720f;   // дальше - раз в 12-18 минут
    public float raidIntervalMax = 1080f;
    public int autoRaidBase = 3;           // размер первого набега
    public int autoRaidGrowth = 1;         // на сколько врагов больше каждый следующий
    public int autoRaidMax = 14;
    public float raidWarnSeconds = 20f;    // за сколько секунд предупредить, с какой стороны идут

    [Header("Вражеские юниты (те же классы, что у нас, другой цвет)")]
    public List<UnitType> enemyUnits = DefaultEnemyUnits();

    static List<UnitType> DefaultEnemyUnits()
    {
        return new List<UnitType>
        {
            new UnitType { unitName = "Мечник", maxHp = 100f, damage = 12f, attackRange = 1.1f, attackCooldown = 1f,   moveSpeed = 1.6f, color = new Color(0.6f, 0.2f, 0.7f),   bodyScale = 0.55f,
                           usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
            new UnitType { unitName = "Лучник", maxHp = 70f,  damage = 9f,  attackRange = 6f,   attackCooldown = 1.4f, moveSpeed = 1.5f, color = new Color(0.45f, 0.3f, 0.6f),  bodyScale = 0.5f,
                           usesWeapon = true, weaponKind = WeaponKind.Bow },
            new UnitType { unitName = "Рыцарь", maxHp = 240f, damage = 24f, attackRange = 1.2f, attackCooldown = 1.3f, moveSpeed = 1.3f, color = new Color(0.32f, 0.32f, 0.42f), bodyScale = 0.75f,
                           usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
            // Вражеский маг-целитель (с набега №3): лечит раненых своих
            new UnitType { unitName = "Полевой маг", maxHp = 70f, damage = 4f, attackRange = 4f, attackCooldown = 1.5f, moveSpeed = 1.6f, color = new Color(0.7f, 0.35f, 0.85f), bodyScale = 0.5f,
                           healPerSecond = 9f, healRange = 4f },
            // Конница (с набега №4)
            new UnitType { unitName = "Всадник", maxHp = 200f, damage = 20f, attackRange = 1.3f, attackCooldown = 1f, moveSpeed = 2.8f, color = new Color(0.8f, 0.25f, 0.45f), bodyScale = 0.8f,
                           requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Sword, usesShield = true },
            new UnitType { unitName = "Конный лучник", maxHp = 140f, damage = 11f, attackRange = 6.5f, attackCooldown = 1.2f, moveSpeed = 2.6f, color = new Color(0.6f, 0.35f, 0.7f), bodyScale = 0.75f,
                           requiresHorse = true, usesWeapon = true, weaponKind = WeaponKind.Bow },
        };
    }

    // Если список сохранился в сцене старым - достраиваем недостающие классы
    void EnsureUnits()
    {
        if (enemyUnits == null) enemyUnits = new List<UnitType>();
        foreach (var d in DefaultEnemyUnits())
            if (!enemyUnits.Exists(u => u != null && u.unitName == d.unitName)) enemyUnits.Add(d);
    }

    bool war;
    float calm;

    // ---------- Сообщения о добыче ----------
    // Короткие строки под баннером войны: кто что унёс и что отбили.

    struct Note { public string text; public bool good; public float until; }
    static readonly List<Note> notes = new List<Note>();
    const int MaxNotes = 5;
    const float NoteSeconds = 8f;

    public static void Notify(string text, bool good)
    {
        notes.Add(new Note { text = text, good = good, until = Time.time + NoteSeconds });
        if (notes.Count > MaxNotes) notes.RemoveAt(0);
        Debug.Log("[Война] " + text);
    }

    // ---------- Тревога: свернуть окна ----------
    // Окна подписываются (OnEnable/OnDisable); обработчик закрывает своё окно и возвращает true, если что-то закрыл.
    // Срабатывает на начало войны и на предупреждение дозорных. Можно вызвать и извне: WarManager.RaiseAlarm().

    public static event System.Func<bool> OnAlarm;
    public static int CollapseToken { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetAlarmStatics() { OnAlarm = null; CollapseToken = 0; notes.Clear(); }

    public static void RaiseAlarm()
    {
        if (CollapseWindows() > 0) Notify("Тревога! Окна свёрнуты", false);
    }

    /// <summary>Свернуть все окна без сообщения о тревоге (например, после экрана входа). Возвращает, сколько закрыто.</summary>
    public static int CollapseWindows()
    {
        CollapseToken++;
        var h = OnAlarm;
        if (h == null) return 0;
        int closed = 0;
        foreach (System.Delegate d in h.GetInvocationList())
        {
            try { if (((System.Func<bool>)d)()) closed++; }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        return closed;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
        EnsureUnits();
        raidTimer = firstRaidDelay;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.f8Key.wasPressedThisFrame)
        {
            if (kb.ctrlKey.isPressed) ClearEnemies();
            else SpawnWave(kb.shiftKey.isPressed ? bigWave : smallWave);
        }

        TickAutoRaids();

        bool wasWar = war;
        if (EnemyAgent.HostileCount > 0) { war = true; calm = warCooldown; }   // лазутчик войну не объявляет
        else if (war)
        {
            calm -= Time.deltaTime;
            if (calm <= 0f) war = false;
        }
        if (war && !wasWar) RaiseAlarm();
    }

    // Периодические набеги: таймер идёт, только когда в городе есть Ратуша. Враги ещё на карте - набег откладывается.
    float raidTimer;
    int pendingSide = -1;
    bool raidWarned;
    int autoRaidsDone;
    int wavesSpawned;     // сколько волн уже было (любых): от этого растёт сила врагов

    void TickAutoRaids()
    {
        if (!autoRaids) return;
        if (RivalManager.Active) return;   // нападают соседи-соперники сами, по своему расчёту
        var pm = PopulationManager.Instance;
        if (GridNavigation.Instance == null || pm == null || pm.Hall == null) return;

        raidTimer -= Time.deltaTime;
        if (raidTimer > 0f)
        {
            if (raidTimer <= raidWarnSeconds && !raidWarned)
            {
                raidWarned = true;
                pendingSide = Random.Range(0, 4);
                Notify($"Дозорные видят врагов с {SideName(pendingSide)}! Набег через ~{raidWarnSeconds:F0} с", false);
                RaiseAlarm();
            }
            return;
        }

        // Прошлые враги ещё бродят по карте - не наваливаем новых
        if (EnemyAgent.All.Count > 0)
        {
            raidTimer = 30f;
            raidWarned = false;
            pendingSide = -1;
            return;
        }

        int size = Mathf.Min(autoRaidMax, autoRaidBase + autoRaidGrowth * autoRaidsDone);
        SpawnWave(size, pendingSide);
        autoRaidsDone++;
        pendingSide = -1;
        raidWarned = false;
        raidTimer = Random.Range(raidIntervalMin, raidIntervalMax);
    }

    static string SideName(int side)
    {
        switch (side)
        {
            case 0: return "запада";
            case 1: return "востока";
            case 2: return "юга";
            default: return "севера";
        }
    }

    // ---------- Набег ----------

    public void SpawnWave(int count, int side = -1)
    {
        var nav = GridNavigation.Instance;
        if (nav == null || count <= 0) return;

        int level = wavesSpawned++;
        float half = nav.mapSize / 2f;
        if (side < 0) side = Random.Range(0, 4);
        float along = Random.Range(-half * 0.6f, half * 0.6f);
        int spawned = 0;

        for (int i = 0; i < count; i++)
        {
            UnitType unit = PickUnit(level);
            if (unit == null) break;

            float off = Mathf.Clamp(along + Random.Range(-3f, 3f), -half + 1f, half - 1f);
            float edge = half - 0.5f - Random.Range(0f, 1.5f);
            Vector2 p;
            switch (side)
            {
                case 0: p = new Vector2(-edge, off); break;
                case 1: p = new Vector2(edge, off); break;
                case 2: p = new Vector2(off, -edge); break;
                default: p = new Vector2(off, edge); break;
            }

            Vector2Int c = nav.WorldToCell(p);
            if (nav.IsBlocked(c)) c = nav.NearestFree(c);
            EnemyAgent.Spawn(unit, nav.CellCenter(c) + Random.insideUnitCircle * 0.2f, level);
            spawned++;
        }

        if (spawned > 0) Notify($"Набег №{level + 1}: {spawned} врагов идут с {SideName(side)}!", false);
    }

    // Состав растёт с номером набега: сначала мечники и лучники, потом маги-целители и конница
    UnitType PickUnit(int level)
    {
        if (enemyUnits == null || enemyUnits.Count == 0) return null;

        float total = 0f;
        var w = new float[enemyUnits.Count];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = enemyUnits[i] != null ? WeightFor(enemyUnits[i].unitName, level) : 0f;
            total += w[i];
        }
        if (total <= 0f) return enemyUnits[0];

        float r = Random.value * total;
        for (int i = 0; i < w.Length; i++)
        {
            if (r < w[i]) return enemyUnits[i];
            r -= w[i];
        }
        return enemyUnits[enemyUnits.Count - 1];
    }

    static float WeightFor(string unitName, int level)
    {
        switch (unitName)
        {
            case "Мечник": return 60f;
            case "Лучник": return 30f;
            case "Рыцарь": return 10f + level * 1.5f;
            case "Полевой маг": return level >= 2 ? 6f + level : 0f;
            case "Всадник": return level >= 3 ? 5f + level : 0f;
            case "Конный лучник": return level >= 4 ? 4f + level : 0f;
            default: return 10f;
        }
    }

    public void ClearEnemies()
    {
        foreach (var e in new List<EnemyAgent>(EnemyAgent.All))
            if (e != null) e.Despawn();
    }

    // ---------- UI ----------

    void OnGUI()
    {
        UiKit.Apply();
        float notesY = Screen.width < 1500 ? 46f : 8f;   // на узком экране - под верхней полосой казны
        if (war) notesY = DrawBanner() + 6f;
        DrawNotes(notesY);

        if (Event.current.type != EventType.Repaint) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        float ppu = Screen.height / (2f * cam.orthographicSize); // пикселей на клетку

        // Здания: повреждённые и руины
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null || !b.IsDamageable) continue;
            bool ruined = b.IsRuined;
            if (!ruined && b.Hp >= b.MaxHp - 0.5f) continue;

            Vector2 c = b.Center;
            Vector2 at = new Vector2(c.x, c.y - b.type.size.y * 0.5f - 0.25f);
            float w = b.type.size.x * ppu * 0.9f;

            if (ruined)
            {
                Color col = b.RebuildPaid ? new Color(1f, 0.6f, 0.2f) : new Color(0.55f, 0.55f, 0.55f);
                DrawBar(cam, at, w, b.RebuildPaid ? b.RebuildProgress : 0f, col);
                DrawSmallLabel(cam, at + Vector2.up * 0.35f, "руины");
            }
            else
            {
                float frac = b.Hp / b.MaxHp;
                DrawBar(cam, at, w, frac, Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.35f, 0.9f, 0.35f), frac));
            }
        }

        // Полевые лагеря: повреждённые и руины
        foreach (var camp in CampMarker.All)
        {
            if (camp == null) continue;
            Vector2 at = camp.Position + Vector2.up * 1.1f;
            float w = Mathf.Clamp(ppu * 1.6f, 20f, 90f);
            if (camp.IsRuined)
            {
                DrawBar(cam, at, w, camp.Rebuilding ? camp.RebuildProgress : 0f, camp.Rebuilding ? new Color(1f, 0.6f, 0.2f) : new Color(0.55f, 0.55f, 0.55f));
                DrawSmallLabel(cam, at + Vector2.up * 0.35f, "руины лагеря");
            }
            else if (camp.Hp < camp.MaxHp - 0.5f)
            {
                float frac = camp.Hp / camp.MaxHp;
                DrawBar(cam, at, w, frac, Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.35f, 0.9f, 0.35f), frac));
            }
        }

        // Наши люди: в бою, а также раненые во время войны. В мирное время - только совсем ослабшие
        // (голод теперь есть у всех - иначе полоски висели бы над каждым).
        foreach (var a in CitizenAgent.All)
        {
            if (a == null || a.IsDead || a.IsInsideBuilding) continue;
            if (a.Hp >= a.MaxHp - 0.01f && !a.InCombat) continue;
            if (!war && !a.InCombat && a.HpFraction >= 0.5f) continue;
            Color col = a.IsSoldier ? new Color(0.3f, 0.9f, 0.3f) : new Color(1f, 0.85f, 0.3f);
            DrawBar(cam, (Vector2)a.transform.position + Vector2.up * 0.5f, Mathf.Clamp(ppu * 0.7f, 14f, 60f), a.Hp / a.MaxHp, col);
        }

        // Враги - всегда
        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e)) continue;
            if (!FogOfWar.IsVisible(e.Position)) continue;   // в тумане не видно
            DrawBar(cam, e.Position + Vector2.up * 0.55f, Mathf.Clamp(ppu * 0.7f, 14f, 60f), e.Hp / e.MaxHp, new Color(1f, 0.2f, 0.2f));
        }
    }

    // Баннер войны сверху по центру. Возвращает нижний край.
    float DrawBanner()
    {
        int n = EnemyAgent.HostileCount;
        string line2 = n > 0 ? $"Врагов на карте: {n}" : $"Мир через {Mathf.CeilToInt(Mathf.Max(0f, calm))} с";
        string threat = RivalManager.ThreatLine();
        Rect r = new Rect(Screen.width / 2f - 180f, 8f, 360f, threat != "" ? 60f : 42f);

        GUI.Box(r, GUIContent.none);
        Color old = GUI.color;
        GUI.color = new Color(UiKit.Bad.r, UiKit.Bad.g, UiKit.Bad.b, 0.16f);
        GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), Texture2D.whiteTexture);
        GUI.color = UiKit.Bad;
        GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, 2f), Texture2D.whiteTexture);
        GUI.color = old;

        GUIStyle head = new GUIStyle(UiKit.Center) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
        head.normal.textColor = UiKit.Bad;
        GUI.Label(new Rect(r.x, r.y + 4f, r.width, 20f), "РЕЖИМ ВОЙНЫ", head);

        GUIStyle st = new GUIStyle(UiKit.Small) { alignment = TextAnchor.MiddleCenter, wordWrap = false, fontSize = 12 };
        Rect l2 = new Rect(r.x, r.y + 22f, r.width, 18f);
        GUI.Label(l2, line2 + UiKit.Col(UiKit.MutedHex, "  ·  новые жители не прибывают"), st);
        if (threat != "") GUI.Label(new Rect(r.x + 6f, r.y + 39f, r.width - 12f, 18f), threat, st);
        UiKit.Tip(r, "Пока на карте враги (и ещё немного после) — война: новые жители не прибывают, " +
            "мирные прячутся в здания, строить нельзя, купцы не заходят.");
        return r.yMax;
    }

    // Короткие сообщения («тосты») по центру сверху: тёмная плашка с цветной полоской слева
    void DrawNotes(float y)
    {
        notes.RemoveAll(n => Time.time > n.until);
        if (notes.Count == 0) return;

        GUIStyle st = new GUIStyle(UiKit.Body) { fontSize = 12, wordWrap = true, alignment = TextAnchor.MiddleLeft };
        const float maxW = 480f, padX = 10f;
        for (int i = notes.Count - 1; i >= 0; i--)   // свежие сверху
        {
            Note n = notes[i];
            GUIContent gc = new GUIContent(n.text);
            float tw = Mathf.Min(st.CalcSize(gc).x, maxW - padX * 2f - 4f);
            float th = Mathf.Max(18f, st.CalcHeight(gc, tw));
            float w = tw + padX * 2f + 4f, h = th + 6f;
            Rect r = new Rect(Screen.width / 2f - w / 2f, y, w, h);

            // Плавно гаснет в последнюю секунду
            float a = Mathf.Clamp01(n.until - Time.time);
            Color old = GUI.color;
            GUI.color = new Color(0.06f, 0.07f, 0.08f, 0.88f * a);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            Color stripe = n.good ? UiKit.Good : UiKit.Warn;
            GUI.color = new Color(stripe.r, stripe.g, stripe.b, a);
            GUI.DrawTexture(new Rect(r.x, r.y, 3f, r.height), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, a);
            st.normal.textColor = n.good ? UiKit.Text : new Color(1f, 0.88f, 0.75f);
            GUI.Label(new Rect(r.x + 4f + padX, r.y + 3f, tw + 2f, th), n.text, st);
            GUI.color = old;
            y += h + 3f;
        }
    }

    static void DrawBar(Camera cam, Vector2 world, float w, float frac, Color fill)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;

        w = Mathf.Max(w, 14f);
        float h = Mathf.Clamp(w * 0.12f, 3f, 7f);
        Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y - h / 2f, w, h);
        if (r.xMax < 0f || r.x > Screen.width || r.yMax < 0f || r.y > Screen.height) return;

        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    static void DrawSmallLabel(Camera cam, Vector2 world, string text)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;
        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
        st.normal.textColor = UiKit.Warn;
        GUI.Label(new Rect(sp.x - 40f, Screen.height - sp.y - 10f, 80f, 20f), text, st);
    }
}