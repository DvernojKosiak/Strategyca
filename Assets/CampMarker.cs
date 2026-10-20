using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Временный полевой лагерь отряда: флаг, палатка и (если хватило дерева) баррикада из брёвен вокруг.
/// Не блокирует проход. По клику на лагерь открывается панель (BuildingPlacer вызывает DrawPanel):
/// запас еды, "Свернуть лагерь", поведение отряда в лагере.
/// Что даёт лагерь:
///  - бойцы рядом с лагерем (не в бою) понемногу лечатся, тратя еду лагеря;
///  - за баррикадой бойцы получают меньше урона (укрытие).
/// У лагеря есть прочность: враги могут его разрушить. Тогда остаются руины - панель работает,
/// но лечиться и укрываться там уже нельзя, а запас еды враги уносят с собой.
/// Руины можно отстроить за дерево (пока рядом нет врагов). Целый лагерь сам чинится, если его не бьют.
/// Создаётся и удаляется отрядом (Squad).
/// </summary>
public class CampMarker : MonoBehaviour
{
    public static readonly List<CampMarker> All = new List<CampMarker>();

    Squad squad;
    static Sprite squareSprite;

    public Squad Squad { get { return squad; } }

    // ---------- Прочность ----------
    public const float BaseHp = 250f;          // палатка и припасы
    public const float BarricadeHp = 350f;     // баррикада добавляет
    public const float CoverRadius = 1.6f;     // в этом радиусе от флага бойцы в укрытии
    public const float CoverDamage = 0.6f;     // в укрытии проходит 60% урона
    public const float HealRadius = 4.5f;      // в этом радиусе бойцы лечатся
    public const int RebuildWood = 20;         // сколько дерева стоит отстроить руины
    public const float RebuildSeconds = 20f;
    const float RepairDelay = 8f, RepairSeconds = 90f;

    bool barricade;
    float lastHitTime = -999f;

    public float Hp { get; private set; }
    public float MaxHp => BaseHp + (barricade ? BarricadeHp : 0f);
    public bool HasBarricade => barricade;
    public bool IsRuined { get; private set; }
    public bool Rebuilding { get; private set; }
    public float RebuildProgress { get; private set; }
    public bool UnderAttack => Time.time - lastHitTime < RepairDelay;
    public Vector2 Position => transform.position;

    // Враг может его бить
    public static bool Targetable(CampMarker c) { return c != null && c.isActiveAndEnabled && !c.IsRuined; }

    // Боец в этой точке укрыт баррикадой
    public bool IsCover(Vector2 p)
    {
        return !IsRuined && barricade && Vector2.Distance(p, transform.position) <= CoverRadius;
    }

    // Здесь можно лечиться
    public bool CanHealAt(Vector2 p)
    {
        return !IsRuined && Vector2.Distance(p, transform.position) <= HealRadius;
    }

    public void TakeDamage(float dmg, EnemyAgent by = null)
    {
        if (IsRuined || dmg <= 0f) return;
        Hp -= dmg;
        lastHitTime = Time.time;
        if (Hp <= 0f) Ruin(by);
    }

    void Ruin(EnemyAgent by)
    {
        Hp = 0f;
        IsRuined = true;
        Rebuilding = false;
        RebuildProgress = 0f;

        // Запас еды лагеря враги уносят с собой
        int food = squad != null ? squad.TakeAllFood() : 0;
        if (food > 0 && EnemyAgent.Alive(by))
        {
            Loot loot = new Loot { food = food };
            Vector2 at = transform.position;
            EnemyAgent.ShareLootAt(loot, at, "лагерь " + (squad != null ? squad.Name : ""), by, p => Vector2.Distance(p, at));
        }
        else WarManager.Notify("Враги разрушили лагерь " + (squad != null ? squad.Name : ""), false);

        ApplyLook();
    }

    /// <summary>Отстроить руины: дерево из казны сразу, потом RebuildSeconds без врагов рядом.</summary>
    public bool StartRebuild()
    {
        if (!IsRuined || Rebuilding) return false;
        var rm = ResourceManager.Instance;
        if (rm != null && !rm.Spend(0, RebuildWood, 0, 0)) return false;
        Rebuilding = true;
        RebuildProgress = 0f;
        return true;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (IsRuined)
        {
            if (!Rebuilding || EnemyAgent.AnyWithin(transform.position, 6f)) return;
            RebuildProgress = Mathf.Min(1f, RebuildProgress + dt / RebuildSeconds);
            if (RebuildProgress >= 1f)
            {
                IsRuined = false;
                Rebuilding = false;
                RebuildProgress = 0f;
                Hp = MaxHp;
                ApplyLook();
            }
            return;
        }

        // Повреждённый лагерь понемногу чинится, если его давно не били
        if (Hp < MaxHp && !UnderAttack) Hp = Mathf.Min(MaxHp, Hp + MaxHp / RepairSeconds * dt);
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public static CampMarker Create(Vector2 pos, Squad squad, bool barricade)
    {
        GameObject go = new GameObject("Camp_" + squad.Name);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        CampMarker m = go.AddComponent<CampMarker>();
        m.squad = squad;
        m.barricade = barricade;
        m.Hp = m.MaxHp;
        m.Build(barricade);
        return m;
    }

    // Попал ли клик (в мировых координатах) в лагерь
    public bool Hit(Vector2 worldPoint)
    {
        return Vector2.Distance(worldPoint, transform.position) <= 1.4f;
    }

    static Sprite GetSquare()
    {
        if (squareSprite == null)
        {
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            squareSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }
        return squareSprite;
    }

    // Части лагеря - чтобы показать руины
    SpriteRenderer tent, pole, flag;
    readonly List<SpriteRenderer> bars = new List<SpriteRenderer>();
    static readonly Color WoodColor = new Color(0.45f, 0.3f, 0.15f);
    static readonly Color TentColor = new Color(0.85f, 0.75f, 0.5f);
    static readonly Color FlagColor = new Color(0.9f, 0.15f, 0.15f);

    void Build(bool barricade)
    {
        tent = AddPart("Tent", new Vector2(0f, -0.15f), new Vector2(0.7f, 0.5f), TentColor, 4);
        pole = AddPart("Pole", new Vector2(0f, 0.35f), new Vector2(0.07f, 1.0f), WoodColor, 5);
        flag = AddPart("Flag", new Vector2(0.22f, 0.65f), new Vector2(0.4f, 0.26f), FlagColor, 6);

        if (barricade)
        {
            const float h = 1.25f;   // половина стороны квадрата баррикады
            const float t = 0.16f;   // толщина
            bars.Add(AddPart("BarN", new Vector2(0f, h), new Vector2(2f * h + t, t), WoodColor, 3));
            bars.Add(AddPart("BarS", new Vector2(0f, -h), new Vector2(2f * h + t, t), WoodColor, 3));
            bars.Add(AddPart("BarW", new Vector2(-h, 0f), new Vector2(t, 2f * h + t), WoodColor, 3));
            bars.Add(AddPart("BarE", new Vector2(h, 0f), new Vector2(t, 2f * h + t), WoodColor, 3));
        }
    }

    // Руины: палатка завалилась, флаг сорван, баррикада потемнела и разбита
    void ApplyLook()
    {
        if (tent == null) return;
        bool r = IsRuined;
        Color dark = new Color(0.3f, 0.3f, 0.3f, 1f);

        tent.color = r ? TentColor * dark : TentColor;
        tent.transform.localScale = r ? new Vector3(0.8f, 0.18f, 1f) : new Vector3(0.7f, 0.5f, 1f);
        tent.transform.localPosition = r ? new Vector3(0f, -0.3f, 0f) : new Vector3(0f, -0.15f, 0f);

        pole.color = r ? WoodColor * dark : WoodColor;
        pole.transform.localRotation = Quaternion.Euler(0f, 0f, r ? 70f : 0f);
        pole.transform.localPosition = r ? new Vector3(0.25f, -0.05f, 0f) : new Vector3(0f, 0.35f, 0f);
        flag.enabled = !r;

        for (int i = 0; i < bars.Count; i++)
        {
            bars[i].color = r ? WoodColor * dark : WoodColor;
            bars[i].enabled = !r || i % 2 == 0; // половина брёвен раскидана
        }
    }

    SpriteRenderer AddPart(string partName, Vector2 localPos, Vector2 size, Color color, int order)
    {
        GameObject go = new GameObject(partName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetSquare();
        sr.color = color;
        sr.sortingOrder = order;
        return sr;
    }

    /// <summary>Панель лагеря. Вызывается из BuildingPlacer.OnGUI, возвращает занятый прямоугольник.</summary>
    public Rect DrawPanel(float x, float y, float w)
    {
        // Высота содержимого: состояние (40, у руин +22) + 4 строки по 22 + кнопки 34 + раздел 24 + переключатели 28
        float contentH = 40f + (IsRuined ? UiKit.RowH : 0f) + UiKit.RowH * 4f + 34f + UiKit.SectionH + UiKit.BtnH;
        Rect panel = new Rect(x, y, w, UiKit.WindowH(contentH));
        if (squad == null) return panel;

        Rect c = UiKit.Window(panel, (IsRuined ? "Руины лагеря · " : "Лагерь · ") + squad.Name);
        float cx = c.x, cw = c.width, cy = c.y;
        GUIStyle body = new GUIStyle(UiKit.Body) { wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };

        // --- Прочность / руины ---
        if (!IsRuined)
        {
            float frac = Hp / MaxHp;
            string st = UnderAttack ? UiKit.Col(UiKit.BadHex, "под ударом") : (frac < 0.999f ? UiKit.Col(UiKit.WarnHex, "чинится") : UiKit.Col(UiKit.GoodHex, "цел"));
            GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.MutedHex, "Прочность: ") + st, body);
            UiKit.Bar(new Rect(cx, cy + UiKit.RowH, cw, 14f), frac, Color.Lerp(UiKit.Bad, UiKit.Good, frac),
                $"{Mathf.CeilToInt(Hp)}/{Mathf.CeilToInt(MaxHp)}");
            cy += 40f;
        }
        else
        {
            Rect rr = new Rect(cx, cy, cw, UiKit.RowH);
            GUI.Label(rr, UiKit.B(UiKit.Col(UiKit.BadHex, "Лагерь разрушен")), body);
            UiKit.Tip(rr, "Здесь нельзя лечиться и укрываться, запас еды разграблен.");
            cy += UiKit.RowH;

            if (Rebuilding)
            {
                bool enemies = EnemyAgent.AnyWithin(transform.position, 6f);
                GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), UiKit.Col(UiKit.MutedHex, "Отстраивают") +
                    (enemies ? UiKit.Col(UiKit.WarnHex, " — ждут, пока уйдут враги") : ""), body);
                UiKit.Bar(new Rect(cx, cy + UiKit.RowH, cw, 14f), RebuildProgress, UiKit.Warn, $"{RebuildProgress * 100f:F0}%");
            }
            else
            {
                var rm = ResourceManager.Instance;
                bool can = rm == null || rm.wood >= RebuildWood;
                if (UiKit.Button(new Rect(cx, cy + 6f, cw, UiKit.BtnH), "Отстроить лагерь", can, UiKit.Kind.Primary,
                        can ? $"Стоит {RebuildWood} дерева" : $"Не хватает дерева: нужно {RebuildWood}"))
                    StartRebuild();
            }
            cy += 40f;
        }

        // --- Отряд и припасы ---
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Бойцов", $"<b>{squad.Members.Count}</b>");
        cy += UiKit.RowH;

        string food = UiKit.Col(squad.Food <= 0 ? UiKit.BadHex : UiKit.GoodHex, $"<b>{squad.Food}</b>") + UiKit.Col(UiKit.MutedHex, $"  ~{squad.FoodMinutes:F1} мин");
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Еда", food);
        cy += UiKit.RowH;

        string barricadeText = IsRuined ? UiKit.Col(UiKit.MutedHex, "—") : (barricade ? UiKit.Col(UiKit.GoodHex, "есть") : UiKit.Col(UiKit.WarnHex, "нет"));
        Rect brR = new Rect(cx, cy, cw, UiKit.RowH);
        UiKit.Row(brR, "Баррикада", barricadeText);
        UiKit.Tip(brR, IsRuined ? "Лечения и укрытия нет" : (barricade ? "Бойцы у флага получают меньше урона" : "Не хватило дерева на баррикаду"));
        cy += UiKit.RowH;

        GUI.Label(new Rect(cx, cy, cw, UiKit.RowH), squad.Supplier != null
            ? UiKit.Col(UiKit.WarnHex, "Боец идёт в Ратушу за едой")
            : UiKit.Col(UiKit.MutedHex, "Припасы под контролем"), body);
        cy += UiKit.RowH;

        // --- Кнопки ---
        float hw = (cw - 4f) / 2f;
        if (UiKit.Button(new Rect(cx, cy, hw, UiKit.BtnH), "За едой", squad.Supplier == null, UiKit.Kind.Normal,
                squad.Supplier == null ? "Послать бойца в Ратушу за едой сейчас" : "Боец уже идёт за едой"))
            squad.RequestSupply();
        if (UiKit.Button(new Rect(cx + 4f + hw, cy, hw, UiKit.BtnH), "Свернуть", UiKit.Kind.Danger, "Свернуть лагерь: отряд вернётся в казарму"))
        {
            squad.Retreat();
            return panel;
        }
        cy += 34f;

        // --- Поведение в лагере ---
        cy = UiKit.Section(cx, cy, cw, "Поведение в лагере");
        bool patrol = squad.PatrolAtCamp;
        Rect holdR = new Rect(cx, cy, hw, UiKit.BtnH);
        if (GUI.Toggle(holdR, !patrol, "Стоять", GUI.skin.button) && patrol) squad.SetPatrol(false);
        UiKit.Tip(holdR, "Бойцы стоят на месте у лагеря");
        Rect patrolR = new Rect(cx + 4f + hw, cy, hw, UiKit.BtnH);
        if (GUI.Toggle(patrolR, patrol, "Патрулировать", GUI.skin.button) && !patrol) squad.SetPatrol(true);
        UiKit.Tip(patrolR, "Бойцы обходят окрестности лагеря");
        return panel;
    }

}