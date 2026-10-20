using UnityEngine;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;

/// <summary>
/// Общее меню города: маленькая кнопка в правом верхнем углу (или клавиша F).
/// По нажатию открывается окно: сверху кнопка "Собрать всё" (курьеры забирают добычу со всех зданий),
/// ниже вкладки (сейчас - "Рационы", её добавляет CityConsumption). Новые вкладки:
///     GameMenu.AddPage("Название", ширина => высота, rect => { ...рисуем внутри rect... });
/// При старте меню закрыто.
/// </summary>
public class GameMenu : MonoBehaviour
{
    public static GameMenu Instance { get; private set; }

    class Page
    {
        public string title;
        public Func<float, float> height;   // по ширине содержимого - его высота
        public Action<Rect> draw;
    }

    readonly List<Page> pages = new List<Page>();
    bool open = false;      // при старте закрыто
    int current = 0;

    const float PanelWidth = 300f;
    const float TabsHeight = 26f;
    const float CollectHeight = 28f;

    public bool IsOpen => open;

    public static GameMenu Ensure()
    {
        if (Instance == null) new GameObject("GameMenu").AddComponent<GameMenu>();
        return Instance;
    }

    /// <summary>Добавить вкладку в меню.</summary>
    public static void AddPage(string title, Func<float, float> height, Action<Rect> draw)
    {
        GameMenu m = Ensure();
        m.pages.RemoveAll(p => p.title == title); // повторная регистрация заменяет старую
        m.pages.Add(new Page { title = title, height = height, draw = draw });
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // Тревога (WarManager.RaiseAlarm): свернуть окно, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()
    {
        if (!open) return false;
        open = false;
        return true;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame) open = !open;
    }

    // ---------- Собрать всё ----------

    static bool Collectable(BuildingInstance b)
    {
        return b != null && b.type != null && b.isActiveAndEnabled && !b.IsTownHall && !b.IsRoad
            && !b.IsRuined && !b.courierOnTheWay && b.StoredTotal > 0;
    }

    static void CountCollectable(out int buildings, out int items)
    {
        buildings = 0;
        items = 0;
        foreach (var b in BuildingInstance.All)
        {
            if (!Collectable(b)) continue;
            buildings++;
            items += b.StoredTotal;
        }
    }

    // Курьеры идут ко всем зданиям, где что-то накопилось (как "Собрать со всех", только для всех типов)
    public static void CollectAll()
    {
        var pm = PopulationManager.Instance;
        var list = new List<BuildingInstance>(BuildingInstance.All);
        int sent = 0, noOne = 0;

        foreach (var b in list)
        {
            if (!Collectable(b)) continue;
            if (pm == null || pm.Hall == null) { b.CollectToTreasury(); sent++; }
            else if (pm.DispatchCourier(b)) sent++;
            else noOne++;
        }

        if (sent == 0 && noOne == 0) WarManager.Notify("Собирать пока нечего", false);
        else if (noOne > 0) WarManager.Notify($"Курьеры отправлены: {sent}. Нет свободных жителей для зданий: {noOne}", sent > 0);
        else WarManager.Notify($"Курьеры отправлены к зданиям: {sent}", true);
    }

    // ---------- Размеры ----------

    Rect ButtonRect => new Rect(Screen.width - 110f, 10f, 100f, 26f);

    float ContentWidth => PanelWidth - UiKit.Pad * 2f;

    Rect PanelRect
    {
        get
        {
            float h = UiKit.TitleH + 6f + CollectHeight + 6f;
            h += 24f;   // галочка зон ресурсов
            if (ResourceZones.Show) h += ResourceZones.LegendHeight + 2f;
            if (pages.Count > 1) h += TabsHeight + 6f;
            if (pages.Count > 0)
            {
                current = Mathf.Clamp(current, 0, pages.Count - 1);
                h += pages[current].height(ContentWidth) + 8f;
            }
            else h += 24f;
            h = Mathf.Min(h, Screen.height - 100f);
            return new Rect(Screen.width - PanelWidth - 10f, 40f, PanelWidth, h);
        }
    }

    // guiPoint: координаты мыши в системе OnGUI (Y сверху вниз)
    public bool IsPointerOver(Vector2 guiPoint)
    {
        return ButtonRect.Contains(guiPoint) || (open && PanelRect.Contains(guiPoint));
    }

    // ---------- Отрисовка ----------

    void OnGUI()
    {
        UiKit.Apply();
        if (UiKit.Button(ButtonRect, open ? "Город ▴" : "≡ Город [F]", open ? UiKit.Kind.Primary : UiKit.Kind.Normal,
                "Меню города [F]: собрать всё, зоны ресурсов, рационы")) open = !open;

        if (!open) return;

        Rect p = PanelRect;
        string head = pages.Count == 1 ? pages[0].title : "Город";
        Rect c = UiKit.Window(p, head, out bool close);
        if (close) { open = false; return; }

        float x = c.x, w = c.width, y = c.y;

        // Главное действие: «Собрать всё»
        CountCollectable(out int bCount, out int items);
        string collectLabel = bCount > 0 ? $"Собрать всё  ·  {items} ед." : "Собрать всё";
        string collectTip = bCount > 0
            ? $"Курьеры заберут добычу со всех зданий: {bCount} зд., {items} ед."
            : "Собирать пока нечего: склады зданий пусты";
        if (UiKit.Button(new Rect(x, y, w, CollectHeight), collectLabel, bCount > 0, UiKit.Kind.Primary, collectTip)) CollectAll();
        y += CollectHeight + 6f;

        // Зоны ресурсов
        Rect tr = new Rect(x, y, w, 22f);
        ResourceZones.Show = GUI.Toggle(tr, ResourceZones.Show, " Зоны ресурсов на карте");
        UiKit.Tip(tr, "Где шахта, ферма и лесопилка дают больше. Подсвечиваются и сами, когда здание в руке.");
        y += 24f;
        if (ResourceZones.Show)
        {
            ResourceZones.DrawLegend(new Rect(x, y, ContentWidth, ResourceZones.LegendHeight));
            y += ResourceZones.LegendHeight + 2f;
        }

        if (pages.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 22f), "Пока пусто", UiKit.MutedStyle);
            return;
        }

        // Вкладки (видны, когда страниц больше одной)
        if (pages.Count > 1)
        {
            string[] names = new string[pages.Count];
            for (int i = 0; i < pages.Count; i++) names[i] = pages[i].title;
            current = UiKit.Tabs(new Rect(x, y, w + 2f, TabsHeight), names, Mathf.Clamp(current, 0, pages.Count - 1));
            y += TabsHeight + 6f;
        }

        Page page = pages[Mathf.Clamp(current, 0, pages.Count - 1)];
        page.draw(new Rect(x, y, ContentWidth, p.yMax - y - 4f));
    }
}
