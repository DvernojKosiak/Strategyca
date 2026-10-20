using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Туман войны. Две карты - два тумана: Home (наша деревня) и Raid (поле набега).
/// У клетки три состояния:
///  - не разведана: сплошные облака (непрозрачные), ничего не видно - ни зданий, ни людей;
///  - разведана, но сейчас никто не смотрит: затемнена - видно здания и землю, но не людей;
///  - видна сейчас: тумана нет.
/// Что рассеивает туман:
///  - дома: зелёная зона (Territory) и ещё VisionMargin клеток вокруг неё, клетки у построек,
///    башни и пушки (далеко), люди на улице (небольшой радиус, у разведчика - больше), полевые лагеря,
///    разведчики в дозоре (CamoScouts, LookoutVision - дальше всех);
///  - в набеге: полоса по краям карты (с самого начала), наши бойцы, наш лагерь.
/// Враги в тумане не рисуются (Renderer.forceRenderingOff), их полоски здоровья и карточки не показываются.
/// Здания набега под неразведанными облаками не рисуются; в разведанном тумане - видны приглушённо.
/// Туман белый, как облака (с лёгкой неоднородностью). F12 - выключить / включить туман.
/// </summary>
public class FogOfWar : MonoBehaviour
{
    public static bool Enabled = true;

    /// <summary>Школа Природы («Взор леса»): пока true, домашний туман открывает все клетки леса.</summary>
    public static bool RevealForests;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { RevealForests = false; }

    public static FogOfWar Home { get; private set; }
    public static FogOfWar Raid { get; private set; }

    // ---------- Числа ----------
    public const float VisionMargin = 4.5f;     // сколько клеток видно за краем зелёной зоны
    public const float CitizenVision = 3f;
    public const float SoldierVision = 4.5f;
    public const float ScoutVision = 7.5f;
    public const float CampVision = 6f;
    public const float TowerVision = 7.5f;
    public const float LookoutVision = 8f;      // разведчик в дозоре (ветка «Мастер маскировки») сидит тихо и смотрит далеко
    public const int RaidEdgeBand = 3;          // в набеге по краям карты тумана нет

    const float UnexploredAlpha = 1f;          // неразведанное - сплошные облака, сквозь них ничего не видно
    const float ExploredAlpha = 0.45f;
    const float FadeSpeed = 3f;
    static readonly Color FogColor = new Color(0.93f, 0.95f, 0.98f);    // облака
    static readonly Color FogShade = new Color(0.78f, 0.82f, 0.88f);    // тень в облаках

    float[] cloud;          // неоднородность облаков по клеткам (0..1), постоянная
    static int toggleFrame = -1;

    Vector2 origin;     // центр карты в мире
    int size, half;
    bool isRaid;

    bool[] explored, visible, staticVis;
    float[] shown;
    Texture2D tex;
    Color32[] px;
    SpriteRenderer look;
    float scanTimer, staticTimer;

    // кого прятали в прошлый раз (чтобы трогать рендереры только при смене)
    readonly Dictionary<Component, bool> hiddenState = new Dictionary<Component, bool>();

    // ---------- Создание ----------

    public static FogOfWar CreateHome(int mapSize)
    {
        if (Home != null) Destroy(Home.gameObject);
        Home = Make("FogOfWar_Home", Vector2.zero, mapSize, false);
        return Home;
    }

    public static FogOfWar CreateRaid()
    {
        if (Raid != null) Destroy(Raid.gameObject);
        Raid = Make("FogOfWar_Raid", RaidMap.Origin, RaidMap.Size, true);
        return Raid;
    }

    static FogOfWar Make(string n, Vector2 center, int mapSize, bool raid)
    {
        GameObject go = new GameObject(n);
        go.transform.position = new Vector3(center.x, center.y, 0f);
        FogOfWar f = go.AddComponent<FogOfWar>();
        f.Init(center, mapSize, raid);
        return f;
    }

    void Init(Vector2 center, int mapSize, bool raid)
    {
        origin = center;
        size = Mathf.Max(4, mapSize);
        half = size / 2;
        isRaid = raid;

        int n = size * size;
        explored = new bool[n];
        visible = new bool[n];
        staticVis = new bool[n];
        shown = new float[n];
        px = new Color32[n];
        for (int i = 0; i < n; i++) shown[i] = UnexploredAlpha;

        // Облака: два слоя шума - крупные клубы и мелкая рябь
        cloud = new float[n];
        float ox = Random.Range(0f, 1000f), oy = Random.Range(0f, 1000f);
        for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            {
                float big = Mathf.PerlinNoise(ox + x * 0.09f, oy + y * 0.09f);
                float fine = Mathf.PerlinNoise(ox + 500f + x * 0.31f, oy + 500f + y * 0.31f);
                cloud[x * size + y] = Mathf.Clamp01(big * 0.7f + fine * 0.3f);
            }

        tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        WritePixels(true);

        look = gameObject.AddComponent<SpriteRenderer>();
        look.sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 1f);
        look.sortingOrder = 60;   // выше всего на карте (и ночи), ниже интерфейса

        RebuildStatic();
        Scan();
        for (int i = 0; i < n; i++) shown[i] = Target(i);   // сразу, без проявления
        WritePixels(true);
    }

    void OnDestroy()
    {
        if (Home == this) Home = null;
        if (Raid == this) Raid = null;
        // Всё, что прятали, снова показываем (если ещё живо)
        foreach (var kv in hiddenState) if (kv.Key != null) SetHidden(kv.Key, false);
        if (tex != null) Destroy(tex);
    }

    // ---------- Клетки ----------

    int Idx(int x, int y) { return (x + half) * size + (y + half); }
    bool InBounds(int x, int y) { return x >= -half && x < half && y >= -half && y < half; }

    bool Contains(Vector2 p)
    {
        Vector2 l = p - origin;
        return l.x >= -half && l.x < half && l.y >= -half && l.y < half;
    }

    int CellIndex(Vector2 p)
    {
        Vector2 l = p - origin;
        int x = Mathf.FloorToInt(l.x), y = Mathf.FloorToInt(l.y);
        return InBounds(x, y) ? Idx(x, y) : -1;
    }

    void Stamp(bool[] into, Vector2 world, float radius)
    {
        Vector2 l = world - origin;
        int r = Mathf.CeilToInt(radius);
        int cx = Mathf.FloorToInt(l.x), cy = Mathf.FloorToInt(l.y);
        float r2 = radius * radius;
        for (int x = cx - r; x <= cx + r; x++)
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (!InBounds(x, y)) continue;
                float dx = x + 0.5f - l.x, dy = y + 0.5f - l.y;
                if (dx * dx + dy * dy <= r2) into[Idx(x, y)] = true;
            }
    }

    // ---------- Запросы ----------

    /// <summary>Точка сейчас видна (вне карт с туманом - всегда видна).</summary>
    public static bool IsVisible(Vector2 p)
    {
        if (!Enabled) return true;
        FogOfWar f = For(p);
        if (f == null) return true;
        int i = f.CellIndex(p);
        return i < 0 || f.visible[i];
    }

    /// <summary>Точку уже видели (разведана).</summary>
    public static bool IsExplored(Vector2 p)
    {
        if (!Enabled) return true;
        FogOfWar f = For(p);
        if (f == null) return true;
        int i = f.CellIndex(p);
        return i < 0 || f.explored[i];
    }

    static FogOfWar For(Vector2 p)
    {
        if (Raid != null && Raid.Contains(p)) return Raid;
        if (Home != null && Home.Contains(p)) return Home;
        return null;
    }

    // ---------- Источники обзора ----------

    // То, что меняется редко: зона, постройки, края карты набега
    void RebuildStatic()
    {
        System.Array.Clear(staticVis, 0, staticVis.Length);

        if (isRaid)
        {
            for (int x = -half; x < half; x++)
                for (int y = -half; y < half; y++)
                {
                    int d = Mathf.Min(Mathf.Min(x + half, half - 1 - x), Mathf.Min(y + half, half - 1 - y));
                    if (d < RaidEdgeBand) staticVis[Idx(x, y)] = true;
                }
            return;
        }

        // Зелёная зона + VisionMargin вокруг (растягиваем только от краевых клеток зоны)
        Territory t = Territory.Instance;
        if (t != null)
        {
            for (int x = -half; x < half; x++)
                for (int y = -half; y < half; y++)
                {
                    Vector2Int c = new Vector2Int(x, y);
                    if (!t.InZone(c)) continue;
                    staticVis[Idx(x, y)] = true;
                    bool edge = !t.InZone(new Vector2Int(x + 1, y)) || !t.InZone(new Vector2Int(x - 1, y))
                             || !t.InZone(new Vector2Int(x, y + 1)) || !t.InZone(new Vector2Int(x, y - 1));
                    if (edge) Stamp(staticVis, origin + new Vector2(x + 0.5f, y + 0.5f), VisionMargin);
                }
        }

        // Постройки: видно вокруг каждой, с башен и пушек - далеко
        foreach (var b in BuildingInstance.All)
        {
            if (b == null || b.type == null || !b.isActiveAndEnabled || b.IsRoad) continue;
            float r = Mathf.Max(b.type.size.x, b.type.size.y) * 0.5f + 2f;
            if (!b.IsRuined && (b.GetComponent<TowerController>() != null || b.GetComponent<MagicCannonController>() != null || b.GetComponent<GroundCannonController>() != null)) r = TowerVision;
            Stamp(staticVis, b.Center, r);
        }
    }

    // То, что движется: люди и лагеря
    void Scan()
    {
        System.Array.Copy(staticVis, visible, visible.Length);

        if (isRaid)
        {
            foreach (var u in RaidUnit.All)
            {
                if (u == null || !u.Attacker || !u.Alive) continue;
                float r = u.IsScout ? ScoutVision : (u.IsMounted ? SoldierVision + 0.5f : SoldierVision);
                Stamp(visible, u.Position, r);
            }
            RaidManager rm = RaidManager.Instance;
            if (rm != null && rm.HasCamp) Stamp(visible, rm.CampPosition, CampVision);
        }
        else
        {
            foreach (var a in CitizenAgent.All)
            {
                if (a == null || a.IsDead || a.IsInsideBuilding) continue;
                float r = a.LookoutPosted ? LookoutVision : (a.IsScout ? ScoutVision : (a.IsSoldier ? SoldierVision : CitizenVision));
                Stamp(visible, a.transform.position, r);
            }
            foreach (var c in CampMarker.All)
            {
                if (c == null || c.IsRuined) continue;
                Stamp(visible, c.Position, CampVision);
            }

            // Школа Природы: временами видно всё, что творится в лесах
            if (RevealForests && ResourceZones.Instance != null)
                for (int x = -half; x < half; x++)
                    for (int y = -half; y < half; y++)
                    {
                        Vector2 w = origin + new Vector2(x + 0.5f, y + 0.5f);
                        if (ResourceZones.IsForestAt(w)) visible[Idx(x, y)] = true;
                    }
        }

        // Заслоны и перевалочные пункты тоже рассеивают туман
        foreach (var fp in FieldPost.All)
            if (fp != null && fp.Alive && fp.Raid == isRaid && fp.Built) Stamp(visible, fp.Position, fp.VisionRadius);

        for (int i = 0; i < visible.Length; i++) if (visible[i]) explored[i] = true;
    }

    float Target(int i)
    {
        if (visible[i]) return 0f;
        return explored[i] ? ExploredAlpha : UnexploredAlpha;
    }

    // ---------- Обновление ----------

    void Update()
    {
        // F12 - туман вкл/выкл (обрабатывает один туман за кадр, их бывает два)
        if (Keyboard.current != null && Keyboard.current.f12Key.wasPressedThisFrame && toggleFrame != Time.frameCount)
        {
            toggleFrame = Time.frameCount;
            Enabled = !Enabled;
            WarManager.Notify(Enabled ? "Туман войны включён (F12)" : "Туман войны выключен (F12)", true);
        }

        look.enabled = Enabled;

        staticTimer -= Time.deltaTime;
        if (staticTimer <= 0f)
        {
            staticTimer = 1f;
            RebuildStatic();
        }

        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = 0.2f;
            Scan();
        }

        // Плавное проявление / затягивание
        float step = FadeSpeed * Time.deltaTime;
        bool changed = false;
        for (int i = 0; i < shown.Length; i++)
        {
            float t = Target(i);
            float s = shown[i];
            if (Mathf.Abs(s - t) < 0.001f) continue;
            shown[i] = Mathf.MoveTowards(s, t, step);
            changed = true;
        }
        if (changed) WritePixels(false);
    }

    void WritePixels(bool force)
    {
        for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            {
                int i = x * size + y;                       // наш индекс: (x, y) клетки со сдвигом half
                float c = cloud != null ? cloud[i] : 0.5f;
                Color col = Color.Lerp(FogShade, FogColor, c);
                // Облака чуть "дышат": прозрачность немного зависит от шума, но неразведанное - всегда сплошное
                float a = Mathf.Clamp01(shown[i]);
                if (a < UnexploredAlpha - 0.001f) a *= Mathf.Lerp(0.9f, 1f, c);
                px[y * size + x] = new Color32((byte)(col.r * 255f), (byte)(col.g * 255f), (byte)(col.b * 255f), (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false);
    }

    // Враги в тумане не рисуются
    void LateUpdate()
    {
        if (isRaid)
        {
            foreach (var u in RaidUnit.All)
                if (u != null && !u.Attacker) ApplyHidden(u, Enabled && !IsVisible(u.Position));
            // Здания: пока ни одной их клетки не разведали - не рисуются вовсе (даже на размытом краю облаков);
            // разведанные, но не видимые сейчас - видны приглушённо сквозь тонкий туман (память)
            foreach (var b in RaidBuilding.All)
                if (b != null) ApplyHidden(b, Enabled && !AnyExplored(b.OriginCell, b.SizeCells));
        }
        else
        {
            foreach (var e in EnemyAgent.All)
                if (e != null) ApplyHidden(e, Enabled && !IsVisible(e.Position));
        }

        // Убираем из памяти тех, кого уже нет
        if (hiddenState.Count > 0 && Time.frameCount % 120 == 0)
        {
            var dead = new List<Component>();
            foreach (var kv in hiddenState) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var k in dead) hiddenState.Remove(k);
        }
    }

    /// <summary>Разведана ли хоть одна клетка прямоугольника (локальные клетки этой карты).</summary>
    bool AnyExplored(Vector2Int originCell, Vector2Int sizeCells)
    {
        int w = Mathf.Max(1, sizeCells.x), h = Mathf.Max(1, sizeCells.y);
        for (int x = originCell.x; x < originCell.x + w; x++)
            for (int y = originCell.y; y < originCell.y + h; y++)
                if (!InBounds(x, y) || explored[Idx(x, y)]) return true;
        return false;
    }

    void ApplyHidden(Component c, bool hidden)
    {
        // Спрятанных время от времени прячем заново: у них могли появиться новые части (рендереры)
        bool refresh = hidden && Time.frameCount % 30 == 0;
        if (hiddenState.TryGetValue(c, out bool was) && was == hidden && !refresh) return;
        hiddenState[c] = hidden;
        SetHidden(c, hidden);
    }

    static void SetHidden(Component c, bool hidden)
    {
        foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = hidden;
    }
}
