using UnityEngine;
using UnityEngine.InputSystem;

public class ResourceManager : MonoBehaviour
{
    public static ResourceManager Instance { get; private set; }

    [Header("Казна игрока")]
    public int gold = 150;
    public int wood = 100;
    public int stone = 50;
    public int food = 80;

    [Header("Не показывается сверху - смотреть в Ратуше")]
    public int iron = 0;     // железная руда: добывает шахта Ур.3, из неё в мастерской делают сталь
    public int oats = 0;     // овёс: даёт ферма Ур.3, из него в Конюшне выращивают лошадей, на Кухне - похлёбку
    public int berries = 0;  // ягоды: приносят собиратели (Станция собирателей), нужны Кухне
    public int herbs = 0;    // травы (путь Магии): со станций носят в Ратушу, берёт Зельеварня
    public int potions = 0;  // зелья отравления: из Зельеварни носят в Ратушу / Хранилище
    public int parts = 0;    // детали (путь Инженерии): отливает Литейная, тратит Инженерный двор
    public int leaves = 0;   // листва: даёт Лесопилка Ур.4. Маскировочные накидки и др.
    public int resin = 0;    // смола: даёт Лесопилка Ур.3. Пропитка (Мастерская), резина (Литейная), зелье замедления, Поджигатели
    public int tar = 0;      // смоляная пропитка: варит Мастерская Ур.2 из смолы и дерева
    public int rubber = 0;   // резина: варит Литейная (путь Инженерии) из смолы
    public int slowPotions = 0;  // зелья замедления: варит Зельеварня (Магия) из трав и смолы, ими смазывают луки

    [Header("Валюта рынка")]
    public int marks = 0;    // марки Торговой гильдии: только через рынок (продажи, заказы гильдии), см. TradeGuild

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public bool HasEnough(int g, int w, int s, int f)
    {
        return gold >= g && wood >= w && stone >= s && food >= f;
    }

    public bool Spend(int g, int w, int s, int f)
    {
        if (!HasEnough(g, w, s, f)) return false;
        gold -= g;
        wood -= w;
        stone -= s;
        food -= f;
        return true;
    }

    // ---------- Для тестов ----------
    // F9 - много всего: обычные ресурсы, доски/плиты/сталь всех качеств, полные ягодные кусты и кухни
    [Header("Тест: F9 - выдать ресурсы")]
    public int cheatAmount = 5000;
    public int cheatRefined = 200;

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame) GiveTestResources();
    }

    public void GiveTestResources()
    {
        Add(cheatAmount, cheatAmount, cheatAmount, cheatAmount, cheatAmount, cheatAmount, cheatAmount, cheatAmount);
        parts += cheatRefined;
        resin += cheatRefined;
        leaves += cheatRefined;
        tar += cheatRefined;
        rubber += cheatRefined;
        slowPotions += cheatRefined / 10;
        potions += cheatRefined / 10;
        marks += 500;

        var st = TownHallStorage.Instance;
        if (st != null)
        {
            foreach (QualityTier q in System.Enum.GetValues(typeof(QualityTier)))
            {
                st.AddPlanks(q, cheatRefined);
                st.AddStonePlates(q, cheatRefined);
                st.AddSteel(q, cheatRefined);
                st.Add(RefinedKind.ReinforcedPlates, q, cheatRefined / 4);
                st.Add(RefinedKind.TarredBeams, q, cheatRefined / 4);
                st.Add(RefinedKind.Monolith, q, cheatRefined / 10);
            }
        }
        BerryField.RefillAll();
        KitchenController.CheatFillAll();
        WarManager.Notify($"[Тест] Выдано: по {cheatAmount} золота, дерева, камня, еды, руды, овса, ягод и по {cheatRefined} досок, плит и стали каждого качества, {cheatRefined} деталей, смолы, пропитки и резины, по {cheatRefined / 10} зелий, 500 марок; кухни полны", true);
    }

    // ironOre, oatsAmount, berriesAmount, herbsAmount, potionsAmount - необязательные параметры, чтобы старые вызовы не менять
    public void Add(int g, int w, int s, int f, int ironOre = 0, int oatsAmount = 0, int berriesAmount = 0,
                    int herbsAmount = 0, int potionsAmount = 0, int resinAmount = 0)
    {
        resin += resinAmount;
        gold += g;
        wood += w;
        stone += s;
        food += f;
        iron += ironOre;
        oats += oatsAmount;
        berries += berriesAmount;
        herbs += herbsAmount;
        potions += potionsAmount;
    }

    public void AddHerbs(int n) { herbs += n; }
    public void AddPotions(int n) { potions += n; }

    public void AddParts(int n) { if (n > 0) parts += n; }
    public void AddResin(int n) { if (n > 0) resin += n; }
    public void AddLeaves(int n) { if (n > 0) leaves += n; }
    public void AddTar(int n) { if (n > 0) tar += n; }
    public void AddRubber(int n) { if (n > 0) rubber += n; }
    public void AddSlowPotions(int n) { if (n > 0) slowPotions += n; }

    static bool Take(ref int field, int amount)
    {
        if (amount <= 0) return true;
        if (field < amount) return false;
        field -= amount;
        return true;
    }

    public bool SpendResin(int amount) { return Take(ref resin, amount); }
    public bool SpendLeaves(int amount) { return Take(ref leaves, amount); }
    public bool SpendTar(int amount) { return Take(ref tar, amount); }
    public bool SpendRubber(int amount) { return Take(ref rubber, amount); }
    public bool SpendSlowPotions(int amount) { return Take(ref slowPotions, amount); }

    public void AddMarks(int n) { if (n > 0) marks += n; }

    public bool SpendMarks(int amount)
    {
        if (amount <= 0) return true;
        if (marks < amount) return false;
        marks -= amount;
        return true;
    }

    public bool SpendParts(int amount)
    {
        if (amount <= 0) return true;
        if (parts < amount) return false;
        parts -= amount;
        return true;
    }

    public bool SpendHerbs(int amount)
    {
        if (amount <= 0) return true;
        if (herbs < amount) return false;
        herbs -= amount;
        return true;
    }

    public bool SpendPotions(int amount)
    {
        if (amount <= 0) return true;
        if (potions < amount) return false;
        potions -= amount;
        return true;
    }

    public bool SpendIron(int amount)
    {
        if (amount <= 0) return true;
        if (iron < amount) return false;
        iron -= amount;
        return true;
    }

    public bool SpendOats(int amount)
    {
        if (amount <= 0) return true;
        if (oats < amount) return false;
        oats -= amount;
        return true;
    }

    public bool SpendBerries(int amount)
    {
        if (amount <= 0) return true;
        if (berries < amount) return false;
        berries -= amount;
        return true;
    }

    // ---------- Верхняя полоса (y 0..40: ниже, на y=45, кнопка «Постройки») ----------
    // Строка 1 - казна (рисует ResourceManager), строка 2 - жители (рисует PopulationManager).
    // Тёмная подложка общая для обеих строк, поэтому рисуется «позади» остальных окон (GUI.depth).
    internal const float HudH = 40f;

    static string Item(string name, int value, string hex)
    {
        return UiKit.Col(UiKit.MutedHex, name) + " " + UiKit.B(UiKit.Col(hex, value.ToString()));
    }

    private void OnGUI()
    {
        UiKit.Apply();
        GUI.depth = 10;   // подложка под всеми окнами

        string sep = UiKit.Col(UiKit.MutedHex, "   ·   ");
        string text = Item("Золото", gold, UiKit.GoldHex) + sep + Item("Дерево", wood, UiKit.WoodHex) + sep
                    + Item("Камень", stone, UiKit.StoneHex) + sep + Item("Еда", food, UiKit.FoodHex);
        // Марки гильдии - когда есть Торговый дом (или уже были марки)
        if (TradeGuild.Instance != null || marks > 0) text += sep + Item("◈ Марки", marks, UiKit.AccentHex);

        GUIStyle st = new GUIStyle(UiKit.Body) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
        float w = st.CalcSize(new GUIContent(text)).x;
        bool pop = PopulationManager.Instance != null;
        if (pop) w = Mathf.Max(w, PopulationManager.HudRowWidth);
        w += 24f;

        // Подложка: рамка уходит за край экрана, видны только нижний и правый края
        GUI.Box(new Rect(-6f, -6f, w + 6f, (pop ? HudH : 26f) + 6f), GUIContent.none);

        Rect row = new Rect(12f, 3f, w - 12f, 20f);
        GUI.Label(row, text, st);
        UiKit.Tip(row, ExtrasTip());
    }

    // Подсказка к казне: прочие запасы (они лежат в Ратуше)
    string ExtrasTip()
    {
        var p = new System.Collections.Generic.List<string>();
        if (iron > 0) p.Add($"руда {iron}");
        if (oats > 0) p.Add($"овёс {oats}");
        if (berries > 0) p.Add($"ягоды {berries}");
        if (herbs > 0) p.Add($"травы {herbs}");
        if (potions > 0) p.Add($"зелья отравления {potions}");
        if (slowPotions > 0) p.Add($"зелья замедления {slowPotions}");
        if (parts > 0) p.Add($"детали {parts}");
        if (resin > 0) p.Add($"смола {resin}");
        if (leaves > 0) p.Add($"листва {leaves}");
        if (tar > 0) p.Add($"пропитка {tar}");
        if (rubber > 0) p.Add($"резина {rubber}");
        string extra = p.Count > 0 ? "Ещё в запасе: " + string.Join(", ", p) + "." : "Других запасов пока нет.";
        return "Казна города. " + extra + "\nПодробно — в Ратуше.";
    }
}
