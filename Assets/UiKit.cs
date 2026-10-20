using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Общий стиль интерфейса (IMGUI): тёмные окна с рамкой, плоские кнопки, золотые заголовки,
/// вкладки, полоски, сворачиваемые блоки «Подробнее» и всплывающие подсказки.
///
/// Как пользоваться:
///  - В начале каждого OnGUI: UiKit.Apply();  - все GUI.Box / Button / Label берут этот стиль.
///  - Окно: Rect c = UiKit.Window(rect, "Заголовок", out bool close); if (close) ...
///  - Подзаголовок раздела: y = UiKit.Section(x, y, w, "Склад");
///  - Кнопки: UiKit.Button(r, "Текст", UiKit.Kind.Primary / Danger / Normal, "подсказка");
///  - Вкладки: tab = UiKit.Tabs(r, new[] {"Обзор","Склад"}, tab);
///  - Полоска: UiKit.Bar(r, 0.4f, UiKit.Good, "40%");
///  - Сворачиваемый блок: if (UiKit.Fold(r, "id", "Подробнее")) { ... } (высота блока - FoldH)
///  - Подсказка при наведении: UiKit.Tip(r, "длинное пояснение");
///  - Цвета в тексте: UiKit.Col(UiKit.GoodHex, "ок"), UiKit.MutedHex и т.п.
/// Текстуры кнопок и окон рисуются программно; свои картинки можно подставить позже в BuildSkin.
/// </summary>
public static class UiKit
{
    // ---------- Палитра ----------
    public static readonly Color Panel = new Color(0.09f, 0.10f, 0.12f, 0.95f);
    public static readonly Color PanelEdge = new Color(0.32f, 0.30f, 0.26f, 1f);
    public static readonly Color Text = new Color(0.92f, 0.91f, 0.88f);
    public static readonly Color Muted = new Color(0.62f, 0.63f, 0.66f);
    public static readonly Color Accent = new Color(0.96f, 0.80f, 0.42f);
    public static readonly Color Good = new Color(0.50f, 0.88f, 0.50f);
    public static readonly Color Bad = new Color(1f, 0.45f, 0.40f);
    public static readonly Color Warn = new Color(1f, 0.74f, 0.36f);
    public static readonly Color Info = new Color(0.55f, 0.80f, 1f);

    public const string AccentHex = "#F5CC6B";
    public const string GoodHex = "#80E080";
    public const string BadHex = "#FF7366";
    public const string WarnHex = "#FFBD5C";
    public const string InfoHex = "#8CCCFF";
    public const string MutedHex = "#9EA1A8";

    // Цвета ресурсов (для строк цены)
    public const string GoldHex = "#FFD54A";
    public const string WoodHex = "#D08A4E";
    public const string StoneHex = "#A9C6D6";
    public const string FoodHex = "#8FE07A";

    public static string Col(string hex, string s) { return $"<color={hex}>{s}</color>"; }
    public static string B(string s) { return $"<b>{s}</b>"; }

    /// <summary>Цена в одну строку: «120 зол · 60 дер · 40 кам». Нули пропускаются.</summary>
    public static string Cost(int gold, int wood, int stone, int food)
    {
        var parts = new List<string>();
        if (gold > 0) parts.Add(Col(GoldHex, gold + " зол"));
        if (wood > 0) parts.Add(Col(WoodHex, wood + " дер"));
        if (stone > 0) parts.Add(Col(StoneHex, stone + " кам"));
        if (food > 0) parts.Add(Col(FoodHex, food + " еды"));
        return parts.Count == 0 ? Col(MutedHex, "бесплатно") : string.Join(Col(MutedHex, " · "), parts);
    }

    // ---------- Размеры ----------
    public const float TitleH = 30f;     // шапка окна
    public const float Pad = 10f;        // отступ содержимого от края окна
    public const float RowH = 22f;       // строка текста
    public const float BtnH = 28f;       // обычная кнопка
    public const float SectionH = 24f;   // подзаголовок раздела
    public const float FoldH = 22f;      // строка «▸ Подробнее»

    // ---------- Стиль ----------
    static GUISkin skin;
    static GUIStyle title, section, body, small, muted, center, btnPrimary, btnDanger, btnGhost, tab, tabOn, fold, tipStyle, close;
    static Texture2D white;

    public enum Kind { Normal, Primary, Danger, Ghost }

    /// <summary>Включить общий стиль. Вызывать в начале каждого OnGUI.</summary>
    public static void Apply()
    {
        if (skin == null) BuildSkin();
        GUI.skin = skin;
    }

    public static GUISkin Skin { get { if (skin == null) BuildSkin(); return skin; } }
    public static GUIStyle Title { get { Apply(); return title; } }
    public static GUIStyle SectionStyle { get { Apply(); return section; } }
    public static GUIStyle Body { get { Apply(); return body; } }
    public static GUIStyle Small { get { Apply(); return small; } }
    public static GUIStyle MutedStyle { get { Apply(); return muted; } }
    public static GUIStyle Center { get { Apply(); return center; } }

    static Texture2D Tex(Color fill, Color edge, int radius = 3, int size = 16)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int dx = Mathf.Max(0, Mathf.Max(radius - x, x - (size - 1 - radius)));
                int dy = Mathf.Max(0, Mathf.Max(radius - y, y - (size - 1 - radius)));
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                Color c;
                if (d > radius + 0.5f) c = new Color(0, 0, 0, 0);
                else if (d > radius - 0.5f || x == 0 || y == 0 || x == size - 1 || y == size - 1) c = edge;
                else c = fill;
                t.SetPixel(x, y, c);
            }
        t.Apply();
        return t;
    }

    static GUIStyle Btn(GUIStyle from, Color fill, Color edge, Color txt)
    {
        var s = new GUIStyle(from);
        s.normal.background = Tex(fill, edge);
        s.hover.background = Tex(fill * 1.25f + new Color(0.03f, 0.03f, 0.03f, 0f), edge * 1.2f);
        s.active.background = Tex(fill * 0.75f, edge);
        s.focused.background = s.normal.background;
        s.onNormal.background = Tex(fill * 1.35f, Accent);
        s.onHover.background = s.onNormal.background;
        s.onActive.background = s.active.background;
        s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = txt;
        s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = txt;
        s.border = new RectOffset(4, 4, 4, 4);
        s.padding = new RectOffset(8, 8, 4, 4);
        s.margin = new RectOffset(2, 2, 2, 2);
        s.fontSize = 13;
        s.richText = true;
        s.wordWrap = false;
        s.alignment = TextAnchor.MiddleCenter;
        return s;
    }

    static void BuildSkin()
    {
        skin = Object.Instantiate(GUI.skin);
        skin.hideFlags = HideFlags.HideAndDontSave;
        white = Texture2D.whiteTexture;

        // Окна
        skin.box = new GUIStyle(skin.box);
        skin.box.normal.background = Tex(Panel, PanelEdge, 4);
        skin.box.normal.textColor = Accent;
        skin.box.border = new RectOffset(5, 5, 5, 5);
        skin.box.padding = new RectOffset(8, 8, 7, 6);
        skin.box.fontSize = 14;
        skin.box.fontStyle = FontStyle.Bold;
        skin.box.alignment = TextAnchor.UpperCenter;
        skin.box.richText = true;
        skin.window = new GUIStyle(skin.box);

        // Кнопки
        skin.button = Btn(skin.button, new Color(0.30f, 0.31f, 0.34f), new Color(0.46f, 0.46f, 0.48f), Text);
        btnPrimary = Btn(skin.button, new Color(0.62f, 0.48f, 0.18f), new Color(0.90f, 0.74f, 0.38f), Color.white);
        btnDanger = Btn(skin.button, new Color(0.55f, 0.20f, 0.18f), new Color(0.85f, 0.38f, 0.33f), Color.white);
        btnGhost = Btn(skin.button, new Color(0.14f, 0.15f, 0.17f, 0.9f), new Color(0.26f, 0.27f, 0.30f), Muted);
        tab = Btn(skin.button, new Color(0.17f, 0.18f, 0.20f), new Color(0.28f, 0.29f, 0.32f), Muted);
        tabOn = Btn(skin.button, new Color(0.34f, 0.29f, 0.18f), Accent, Accent);
        tabOn.fontStyle = FontStyle.Bold;
        close = Btn(skin.button, new Color(0.20f, 0.20f, 0.22f), new Color(0.35f, 0.35f, 0.38f), Muted);
        close.padding = new RectOffset(0, 0, 0, 2);
        close.fontSize = 15;

        // Текст
        skin.label = new GUIStyle(skin.label) { fontSize = 13, richText = true, wordWrap = true };
        skin.label.normal.textColor = Text;
        skin.label.padding = new RectOffset(0, 0, 1, 1);
        body = skin.label;
        small = new GUIStyle(skin.label) { fontSize = 11 };
        small.normal.textColor = new Color(0.80f, 0.80f, 0.80f);
        muted = new GUIStyle(skin.label) { fontSize = 12 };
        muted.normal.textColor = Muted;
        center = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter };
        title = new GUIStyle(skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        title.normal.textColor = Accent;
        section = new GUIStyle(skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.LowerLeft };
        section.normal.textColor = new Color(0.85f, 0.75f, 0.52f);
        fold = new GUIStyle(skin.label) { fontSize = 12, wordWrap = false, alignment = TextAnchor.MiddleLeft };
        fold.normal.textColor = Info;
        fold.hover.textColor = Color.white;

        // Переключатели и поля
        skin.toggle = new GUIStyle(skin.toggle) { fontSize = 13, richText = true };
        skin.toggle.normal.textColor = skin.toggle.hover.textColor = skin.toggle.onNormal.textColor = skin.toggle.onHover.textColor = Text;
        skin.textField = new GUIStyle(skin.textField) { fontSize = 13 };
        skin.textField.normal.background = Tex(new Color(0.06f, 0.06f, 0.07f), new Color(0.35f, 0.35f, 0.38f), 2);
        skin.textField.focused.background = Tex(new Color(0.06f, 0.06f, 0.07f), Accent, 2);
        skin.textField.normal.textColor = skin.textField.focused.textColor = Text;
        skin.textField.border = new RectOffset(3, 3, 3, 3);

        // Полосы прокрутки - тонкие
        skin.verticalScrollbar = new GUIStyle(skin.verticalScrollbar) { fixedWidth = 10f };
        skin.verticalScrollbar.normal.background = Tex(new Color(0.08f, 0.08f, 0.09f, 0.8f), new Color(0.08f, 0.08f, 0.09f, 0.8f), 3);
        skin.verticalScrollbar.border = new RectOffset(3, 3, 3, 3);
        skin.verticalScrollbarThumb = new GUIStyle(skin.verticalScrollbarThumb) { fixedWidth = 10f };
        skin.verticalScrollbarThumb.normal.background = Tex(new Color(0.42f, 0.40f, 0.36f), new Color(0.42f, 0.40f, 0.36f), 3);
        skin.verticalScrollbarThumb.border = new RectOffset(3, 3, 3, 3);

        tipStyle = new GUIStyle(skin.label) { fontSize = 12, wordWrap = true };
        tipStyle.normal.background = Tex(new Color(0.05f, 0.05f, 0.06f, 0.97f), Accent * 0.8f, 3);
        tipStyle.border = new RectOffset(4, 4, 4, 4);
        tipStyle.padding = new RectOffset(8, 8, 6, 6);
        tipStyle.normal.textColor = Text;
    }

    // ---------- Окно ----------

    /// <summary>
    /// Окно с шапкой. Возвращает прямоугольник содержимого (с отступами). close - нажали «×».
    /// showClose=false - без крестика.
    /// </summary>
    public static Rect Window(Rect r, string caption, out bool close, bool showClose = true)
    {
        Apply();
        GUI.Box(r, GUIContent.none);
        // Шапка: чуть светлее, золотая линия снизу
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.05f);
        GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, TitleH - 2f), white);
        GUI.color = new Color(Accent.r, Accent.g, Accent.b, 0.35f);
        GUI.DrawTexture(new Rect(r.x + 6f, r.y + TitleH, r.width - 12f, 1f), white);
        GUI.color = old;
        GUI.Label(new Rect(r.x + Pad, r.y + 2f, r.width - Pad * 2f - (showClose ? 26f : 0f), TitleH - 2f), caption, title);
        close = showClose && GUI.Button(new Rect(r.xMax - 28f, r.y + 5f, 22f, 20f), "×", UiKit.close);
        return new Rect(r.x + Pad, r.y + TitleH + 6f, r.width - Pad * 2f, r.height - TitleH - 6f - Pad * 0.6f);
    }

    public static Rect Window(Rect r, string caption) { return Window(r, caption, out _, false); }

    /// <summary>Высота окна по высоте содержимого.</summary>
    public static float WindowH(float contentH) { return TitleH + 6f + contentH + Pad * 0.6f; }

    // ---------- Разделы ----------

    /// <summary>Подзаголовок раздела с тонкой линией. Возвращает y под ним.</summary>
    public static float Section(float x, float y, float w, string caption)
    {
        Apply();
        GUI.Label(new Rect(x, y, w, SectionH - 4f), caption.ToUpper(), section);
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(new Rect(x, y + SectionH - 3f, w, 1f), white);
        GUI.color = old;
        return y + SectionH;
    }

    public static void Line(float x, float y, float w)
    {
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.10f);
        GUI.DrawTexture(new Rect(x, y, w, 1f), white);
        GUI.color = old;
    }

    /// <summary>Строка «название ........ значение».</summary>
    public static void Row(Rect r, string name, string value)
    {
        Apply();
        GUI.Label(r, name, muted);
        var right = new GUIStyle(body) { alignment = TextAnchor.UpperRight, wordWrap = false };
        GUI.Label(r, value, right);
    }

    // ---------- Кнопки ----------

    public static GUIStyle ButtonStyle(Kind k)
    {
        Apply();
        switch (k)
        {
            case Kind.Primary: return btnPrimary;
            case Kind.Danger: return btnDanger;
            case Kind.Ghost: return btnGhost;
            default: return skin.button;
        }
    }

    public static bool Button(Rect r, string text, Kind k = Kind.Normal, string tip = null)
    {
        bool b = GUI.Button(r, text, ButtonStyle(k));
        if (!string.IsNullOrEmpty(tip)) Tip(r, tip);
        return b;
    }

    /// <summary>Кнопка, которая может быть недоступна (серая) с причиной в подсказке.</summary>
    public static bool Button(Rect r, string text, bool enabled, Kind k, string tip)
    {
        bool en = GUI.enabled;
        GUI.enabled = en && enabled;
        bool b = GUI.Button(r, text, ButtonStyle(k));
        GUI.enabled = en;
        if (!string.IsNullOrEmpty(tip)) Tip(r, tip);
        return b && enabled;
    }

    // ---------- Вкладки ----------

    public static int Tabs(Rect r, string[] labels, int current)
    {
        Apply();
        if (labels == null || labels.Length == 0) return current;
        float w = r.width / labels.Length;
        for (int i = 0; i < labels.Length; i++)
        {
            Rect b = new Rect(r.x + i * w, r.y, w - 2f, r.height);
            if (GUI.Button(b, labels[i], i == current ? tabOn : tab)) current = i;
        }
        return current;
    }

    // ---------- Полоска ----------

    public static void Bar(Rect r, float frac, Color fill, string text = null)
    {
        Apply();
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(r, white);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x + 1f, r.y + 1f, (r.width - 2f) * Mathf.Clamp01(frac), r.height - 2f), white);
        GUI.color = old;
        if (!string.IsNullOrEmpty(text))
        {
            var st = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            st.normal.textColor = Color.white;
            GUI.Label(r, text, st);
        }
    }

    // ---------- Сворачиваемый блок ----------

    static readonly HashSet<string> openFolds = new HashSet<string>();

    public static bool IsOpen(string id) { return openFolds.Contains(id); }
    public static void SetOpen(string id, bool on) { if (on) openFolds.Add(id); else openFolds.Remove(id); }

    /// <summary>Строка «▸ Подробнее» / «▾ Подробнее». Возвращает, раскрыт ли блок. Состояние помнится по id.</summary>
    public static bool Fold(Rect r, string id, string caption = "Подробнее")
    {
        Apply();
        bool on = openFolds.Contains(id);
        if (GUI.Button(r, (on ? "▾ " : "▸ ") + caption, fold)) { on = !on; SetOpen(id, on); }
        return on;
    }

    // ---------- Подсказки ----------

    static string tipText;
    static Vector2 tipScreen;
    static int tipFrame = -1;

    /// <summary>Показать подсказку, если мышь над r (r - в текущих координатах GUI, можно внутри групп и прокрутки).</summary>
    public static void Tip(Rect r, string text)
    {
        if (string.IsNullOrEmpty(text) || Event.current == null) return;
        if (Event.current.type != EventType.Repaint) return;
        if (!r.Contains(Event.current.mousePosition)) return;
        tipText = text;
        tipScreen = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
        tipFrame = Time.frameCount;
        EnsureDrawer();
    }

    static void EnsureDrawer()
    {
        if (UiTooltipDrawer.Instance == null)
        {
            var go = new GameObject("UiTooltips");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<UiTooltipDrawer>();
        }
    }

    internal static void DrawTip()
    {
        if (tipFrame != Time.frameCount || string.IsNullOrEmpty(tipText)) return;
        Apply();
        Vector2 m = GUIUtility.ScreenToGUIPoint(tipScreen);
        float w = 280f;
        float h = tipStyle.CalcHeight(new GUIContent(tipText), w);
        float x = Mathf.Min(m.x + 16f, Screen.width - w - 4f);
        float y = m.y + 20f;
        if (y + h > Screen.height - 4f) y = m.y - h - 8f;
        GUI.Label(new Rect(x, y, w, h), tipText, tipStyle);
    }

    /// <summary>Высота текста в стиле body при ширине w (для расчёта высоты окон).</summary>
    public static float TextH(string text, float w, GUIStyle st = null)
    {
        Apply();
        return (st ?? body).CalcHeight(new GUIContent(text), w);
    }
}

/// <summary>Рисует подсказки UiKit поверх всех окон.</summary>
public class UiTooltipDrawer : MonoBehaviour
{
    public static UiTooltipDrawer Instance { get; private set; }

    void Awake() { if (Instance == null) Instance = this; else Destroy(gameObject); }

    void OnGUI()
    {
        GUI.depth = -1000;
        if (Event.current.type != EventType.Repaint) return;
        UiKit.DrawTip();
    }
}
