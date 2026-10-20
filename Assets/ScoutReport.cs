using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Карта, которую принёс разведчик: набросок плана чужой деревни.
/// Точность (Accuracy) случайная - от 50% до 100%. Чем она ниже, тем сильнее:
///  - сдвинуты и искажены здания, часть подписей - "?";
///  - пропущены здания (Ратушу разведчик не пропускает никогда);
///  - нарисованы несуществующие постройки;
///  - врёт число защитников, оценка складов, а запертые ворота могут оказаться нарисованы проходами.
/// Сам разведчик называет точность примерно (ShownAccuracy) - тоже с ошибкой.
/// Карта годится, пока деревня не отстроилась заново (Valid).
/// </summary>
public class ScoutReport
{
    public class Shape
    {
        public Rect cells;        // локальные клетки карты набега (x, y, ширина, высота)
        public string label;
        public bool defense;      // башня / пушка - рисуется красным
        public float[] wobble;    // дрожание линий (рисунок от руки), постоянное
    }

    public RaidTarget Target;
    public int Seed;
    public float Accuracy;
    public int ShownAccuracy;
    public float MadeAt;

    public readonly List<Shape> Shapes = new List<Shape>();
    public readonly List<Vector2> DefenderMarks = new List<Vector2>();
    public readonly List<Vector2Int> Forest = new List<Vector2Int>();
    public int MapSize;
    public readonly List<Vector2Int> WallCells = new List<Vector2Int>();
    public readonly List<Vector2Int> GateCells = new List<Vector2Int>();
    public int DefendersGuess, TowersGuess, WealthGuess, BarricadesGuess;
    public string StyleGuess = "";
    public bool WallsSeen, GatesSeen;
    public float WallRingGuess;
    public Vector2 VillageCenterGuess;
    float[] wallWobble;

    public bool Valid => Target != null && Target.Seed == Seed;

    int Half => MapSize / 2;

    // ---------- Составление ----------

    public static ScoutReport Make(RaidTarget t)
    {
        var rng = new System.Random(Random.Range(1, int.MaxValue));
        float acc = Mathf.Lerp(0.5f, 1f, (float)rng.NextDouble());
        BotVillage.Plan p = t.GetPlan();
        float err = 1f - acc;   // 0 .. 0.5

        ScoutReport r = new ScoutReport { Target = t, Seed = t.Seed, Accuracy = acc, MadeAt = Time.time, MapSize = RaidMap.Size };
        Vector2Int vc = p.Center;
        float Rnd() => (float)rng.NextDouble();
        float Sym() => Rnd() * 2f - 1f;

        // Здания (стены - отдельно)
        foreach (var b in p.Buildings)
        {
            if (b.kind == RaidBuildingKind.Wall || b.kind == RaidBuildingKind.Gate) continue;
            bool important = b.kind == RaidBuildingKind.Hall;
            float dropChance = b.kind == RaidBuildingKind.House ? err * 0.8f : err * 0.5f;
            if (!important && Rnd() < dropChance) continue;

            float jit = err * 5f;
            float x = b.origin.x + Sym() * jit, y = b.origin.y + Sym() * jit;
            float w = Mathf.Max(1f, b.size.x + Sym() * err * 1.5f), h = Mathf.Max(1f, b.size.y + Sym() * err * 1.5f);
            string label = Rnd() < err * 0.5f && !important ? "?" : Letter(b.kind);
            r.Shapes.Add(new Shape { cells = new Rect(x, y, w, h), label = label, defense = IsDefense(b.kind), wobble = Wobble(rng) });
        }

        // Выдуманные постройки
        int fakes = Mathf.RoundToInt(err * 8f * Rnd());
        for (int i = 0; i < fakes; i++)
        {
            RaidBuildingKind k = Rnd() < 0.35f ? RaidBuildingKind.Tower : (Rnd() < 0.5f ? RaidBuildingKind.House : RaidBuildingKind.Farm);
            int s = k == RaidBuildingKind.Farm ? 3 : 2;
            float x = vc.x + Sym() * 9f, y = vc.y + Sym() * 9f;
            string label = Rnd() < 0.5f ? "?" : Letter(k);
            r.Shapes.Add(new Shape { cells = new Rect(x, y, s, s), label = label, defense = k == RaidBuildingKind.Tower && label != "?", wobble = Wobble(rng) });
        }

        // Стены и ворота
        r.WallsSeen = p.Walls;
        r.GatesSeen = p.Gates && Rnd() >= err * 0.6f;   // запертые ворота можно и не заметить
        r.WallRingGuess = Mathf.Max(p.RingX, p.RingY) + Sym() * err * 2f;
        r.VillageCenterGuess = new Vector2(vc.x + 0.5f + Sym() * err * 2f, vc.y + 0.5f + Sym() * err * 2f);

        // Стены рисуются по-настоящему (с проходами там, где они есть); часть кусков смазана
        foreach (var b in p.Buildings)
        {
            if (b.kind == RaidBuildingKind.Wall) { if (Rnd() >= err * 0.3f) r.WallCells.Add(b.origin); }
            else if (b.kind == RaidBuildingKind.Gate) { if (r.GatesSeen) r.GateCells.Add(b.origin); else if (Rnd() < 0.5f) r.WallCells.Add(b.origin); }
            else if (b.kind == RaidBuildingKind.Barricade) r.BarricadesGuess++;
        }
        if (Rnd() >= err) r.StyleGuess = p.StyleName;

        // Лес разведчик рисует примерно: часть пятен пропущена
        foreach (var c in p.Trees) if (Rnd() >= err * 0.6f) r.Forest.Add(c);
        r.wallWobble = new float[64];
        for (int i = 0; i < r.wallWobble.Length; i++) r.wallWobble[i] = Sym();

        // Защитники: метки возле настоящих постов (с ошибкой), лишние/недостающие
        int count = Mathf.Max(1, Mathf.RoundToInt(p.Defenders.Count * (1f + Sym() * err)));
        for (int i = 0; i < count; i++)
        {
            Vector2 at = i < p.Defenders.Count
                ? p.Defenders[i].post + new Vector2(Sym(), Sym()) * (1f + err * 4f)
                : new Vector2(vc.x + Sym() * 8f, vc.y + Sym() * 8f);
            r.DefenderMarks.Add(at);
        }
        r.DefendersGuess = count;
        r.TowersGuess = 0;
        foreach (var s in r.Shapes) if (s.defense) r.TowersGuess++;
        r.WealthGuess = Mathf.Max(0, Mathf.RoundToInt(p.Wealth * (1f + Sym() * err * 0.8f)));

        int shown = Mathf.RoundToInt((acc + Sym() * 0.08f) * 20f) * 5;
        r.ShownAccuracy = Mathf.Clamp(shown, 40, 100);
        return r;
    }

    static float[] Wobble(System.Random rng)
    {
        float[] w = new float[8];
        for (int i = 0; i < w.Length; i++) w[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return w;
    }

    static bool IsDefense(RaidBuildingKind k) { return k == RaidBuildingKind.Tower || k == RaidBuildingKind.Cannon || k == RaidBuildingKind.Gun; }

    static string Letter(RaidBuildingKind k)
    {
        switch (k)
        {
            case RaidBuildingKind.Hall: return "Р";
            case RaidBuildingKind.House: return "Д";
            case RaidBuildingKind.Farm: return "Ф";
            case RaidBuildingKind.Sawmill: return "Л";
            case RaidBuildingKind.Mine: return "Ш";
            case RaidBuildingKind.Warehouse: return "Х";
            case RaidBuildingKind.Workshop: return "М";
            case RaidBuildingKind.Barracks: return "К";
            case RaidBuildingKind.Kitchen: return "Кх";
            case RaidBuildingKind.Stable: return "Кн";
            case RaidBuildingKind.Tower: return "Б";
            case RaidBuildingKind.Cannon: return "П";
            case RaidBuildingKind.Gun: return "Пш";
            case RaidBuildingKind.Barricade: return "з";
            default: return "?";
        }
    }

    public string SummaryText
    {
        get
        {
            string walls = !WallsSeen ? "стен нет" : (GatesSeen ? "стены, ворота заперты" : "стены с проходами");
            string bar = BarricadesGuess > 0 ? $", заслонов: {BarricadesGuess}" : "";
            return $"Защитников ~{DefendersGuess}, башен ~{TowersGuess}, {walls}{bar}\nСклады: {BotVillage.WealthText(WealthGuess)} (~{WealthGuess / 50 * 50})";
        }
    }

    /// <summary>Коротко, в одну строку: защитники, башни, стены, богатство.</summary>
    public string ShortSummary
    {
        get
        {
            string walls = !WallsSeen ? "без стен" : (GatesSeen ? "стены, ворота" : "стены");
            return $"защитников ~{DefendersGuess} · башен ~{TowersGuess} · {walls} · {BotVillage.WealthText(WealthGuess)}";
        }
    }

    // ---------- Рисунок ----------

    static readonly Color Paper = new Color(0.93f, 0.86f, 0.68f);
    static readonly Color PaperDark = new Color(0.78f, 0.68f, 0.48f);
    static readonly Color Ink = new Color(0.25f, 0.18f, 0.1f);
    static readonly Color RedInk = new Color(0.65f, 0.12f, 0.08f);

    /// <summary>Нарисовать набросок в прямоугольнике r (квадратная область карты).</summary>
    public void Draw(Rect r)
    {
        Color old = GUI.color;

        // Бумага с потемневшим краем
        Fill(new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 8f), PaperDark);
        Fill(r, Paper);

        float k = r.width / MapSize;   // пикселей на клетку
        Vector2 ToScreen(float lx, float ly) => new Vector2(r.x + (lx + Half) * k, r.y + (Half - ly) * k);

        // Лес - зелёные точки-кроны
        Color leaf = new Color(0.32f, 0.5f, 0.22f, 0.55f);
        float dot = Mathf.Max(2f, k * 0.8f);
        foreach (var c in Forest)
        {
            Vector2 s = ToScreen(c.x + 0.5f, c.y + 0.5f);
            Fill(new Rect(s.x - dot / 2f, s.y - dot / 2f, dot, dot), leaf);
        }

        // Стены - куски частокола там, где разведчик их видел; ворота - красным
        if (WallsSeen)
        {
            float th = Mathf.Max(2f, k * 0.6f);
            int wi = 0;
            foreach (var c in WallCells)
            {
                Vector2 sp = ToScreen(c.x + 0.5f, c.y + 0.5f);
                float jit = wallWobble[wi++ % wallWobble.Length] * 1.2f;
                Fill(new Rect(sp.x - th / 2f + jit, sp.y - th / 2f - jit, th + k * 0.3f, th + k * 0.3f), Ink);
            }
            GUIStyle g = Label(Mathf.RoundToInt(Mathf.Clamp(k * 1.1f, 9f, 14f)), RedInk);
            foreach (var c in GateCells)
            {
                Vector2 sp = ToScreen(c.x + 0.5f, c.y + 0.5f);
                Fill(new Rect(sp.x - th / 2f, sp.y - th / 2f, th + k * 0.3f, th + k * 0.3f), RedInk);
            }
            if (GateCells.Count > 0)
            {
                Vector2 sp = ToScreen(GateCells[0].x + 0.5f, GateCells[0].y + 1.5f);
                GUI.Label(new Rect(sp.x - 20f, sp.y - 9f, 40f, 18f), "ворота", g);
            }
        }

        // Здания: контур "от руки" и буква
        foreach (var s in Shapes)
        {
            Vector2 a = ToScreen(s.cells.x, s.cells.y + s.cells.height);
            Rect rc = new Rect(a.x, a.y, s.cells.width * k, s.cells.height * k);
            Color ink = s.defense ? RedInk : Ink;
            Fill(rc, new Color(ink.r, ink.g, ink.b, 0.12f));
            HandRect(rc, ink, s.wobble);
            GUIStyle st = Label(Mathf.RoundToInt(Mathf.Clamp(k * 1.2f, 9f, 16f)), ink);
            GUI.Label(rc, s.label, st);
        }

        // Защитники - крестики
        GUIStyle cross = Label(Mathf.RoundToInt(Mathf.Clamp(k * 1.1f, 9f, 14f)), RedInk);
        foreach (var d in DefenderMarks)
        {
            Vector2 s = ToScreen(d.x, d.y);
            GUI.Label(new Rect(s.x - 8f, s.y - 9f, 16f, 18f), "×", cross);
        }

        // Место высадки
        GUIStyle land = Label(12, Ink);
        land.alignment = TextAnchor.MiddleLeft;
        Vector2 l = ToScreen(-Half + 0.5f, 0.5f);
        GUI.Label(new Rect(l.x + 2f, l.y - 10f, 120f, 20f), "► высадка", land);

        // Роза ветров (для красоты) и рамка
        GUIStyle n = Label(12, Ink);
        GUI.Label(new Rect(r.xMax - 30f, r.y + 4f, 24f, 18f), "С", n);
        HandRect(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), Ink, new float[] { 0.5f, -0.4f, 0.3f, -0.6f, 0.2f, 0.7f, -0.3f, 0.1f });

        GUI.color = old;
    }

    // Прямоугольник неровными линиями
    static void HandRect(Rect r, Color ink, float[] w)
    {
        const float th = 2f;
        float j = 1.6f;
        Fill(new Rect(r.x + w[0] * j, r.y + w[1] * j, r.width + w[2] * j, th), ink);              // верх
        Fill(new Rect(r.x + w[3] * j, r.yMax - th + w[4] * j, r.width + w[5] * j, th), ink);       // низ
        Fill(new Rect(r.x + w[6] * j, r.y + w[1] * j, th, r.height + w[7] * j), ink);              // лево
        Fill(new Rect(r.xMax - th + w[2] * j, r.y + w[0] * j, th, r.height + w[5] * j), ink);      // право
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;   // иначе подписи окрасятся в цвет заливки
    }

    static GUIStyle Label(int size, Color c)
    {
        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
        st.normal.textColor = c;
        return st;
    }

    /// <summary>Условные обозначения (для подсказки под картой; фон подсказки тёмный).</summary>
    public const string Legend =
        "<b>Условные обозначения</b>\n" +
        "Р — Ратуша, К — казарма, Д — дом, Ф — ферма\n" +
        "Л — лесопилка, Ш — шахта, Х — хранилище\n" +
        "М — мастерская, Кх — кухня, Кн — конюшня, з — заслон\n" +
        "<color=" + UiKit.BadHex + ">Б — башня, П — магическая пушка, Пш — пушка\n× — защитник, ворота — красным</color>\n" +
        "? — не разобрать, <color=" + UiKit.GoodHex + ">●</color> — лес, ► — место высадки";
}
