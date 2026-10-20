using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Окно рынка Торговой гильдии. Открывается кликом по Торговому дому (или кнопкой в его панели), Esc / × - закрыть.
/// Вкладки:
///  - «Купить»          - предложения купцов по разделам: Сырьё / Материалы / Зелья / Оружие и щиты / Модули и ловушки / Войска;
///  - «Мои лавки»       - свои лавки: выставить товар (раздел -> что -> сколько -> цена), менять цену, снять; купить лавку;
///  - «Заказы гильдии»  - партии товара к сроку за марки выше рынка, растят репутацию;
///  - «Цены»            - рыночная цена каждого товара, куда идёт, что у вас есть;
///  - «Обозы и новости» - кто сейчас едет в город и зачем, доставки, лента рынка.
/// Живёт на объекте TradeGuild.
/// </summary>
public class MarketWindow : MonoBehaviour
{
    static bool open;
    static Rect winRect;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { open = false; winRect = default; }

    public static bool IsOpen => open;
    public static void Open() { TradeGuild.Ensure(); open = true; }
    public static void Close() { open = false; }
    public static bool IsPointerOver(Vector2 gp) { return open && winRect.Contains(gp); }

    int tab;
    int buyCat;                        // раздел во вкладке «Купить»
    Vector2 scroll;
    float contentH = 400f;
    readonly Dictionary<int, int> buyPick = new Dictionary<int, int>();

    // Редактор новой лавки
    bool editing;
    int editCat;
    bool hasGood;
    GoodId editGood;
    MarketItem editItem;
    int editPacks = 1;
    int editPrice = 1;


    GUIStyle line, lineMuted, para, center, pick, pickOn;
    const string Sym = TradeGuild.Sym;

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
        if (!open) return;
        if (TradeGuild.House == null) { open = false; return; }
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) open = false;
    }

    void Styles()
    {
        if (line != null) return;
        // Строка таблицы - в одну строку, лишнее обрезается (полный текст - в подсказке)
        line = new GUIStyle(UiKit.Body) { wordWrap = false, clipping = TextClipping.Clip };
        lineMuted = new GUIStyle(UiKit.MutedStyle) { wordWrap = false, clipping = TextClipping.Clip };
        para = new GUIStyle(UiKit.MutedStyle) { wordWrap = true };
        center = new GUIStyle(UiKit.Body) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
        // Кнопки выбора товара в редакторе лавки: выбранная - светлая, остальные - приглушённые
        pick = new GUIStyle(UiKit.ButtonStyle(UiKit.Kind.Ghost)) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        pickOn = new GUIStyle(UiKit.ButtonStyle(UiKit.Kind.Normal)) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, fontStyle = FontStyle.Bold };
    }

    void OnGUI()
    {
        UiKit.Apply();
        if (!open || RaidManager.ViewingBattle) return;
        TradeGuild g = TradeGuild.Instance;
        if (g == null) return;
        GUI.depth = -10;
        Styles();

        float w = Mathf.Min(900f, Screen.width - 40f), h = Mathf.Min(620f, Screen.height - 70f);
        Rect r = new Rect((Screen.width - w) / 2f, Mathf.Max(50f, (Screen.height - h) / 2f), w, h);
        winRect = r;
        CameraController.BlockZoom();

        Rect c = UiKit.Window(r, "Торговая гильдия — рынок", out bool close);
        if (close) { open = false; return; }

        // --- Счёт и главное ---
        string next = g.Rank >= 5 ? "ранг максимальный" : $"до ранга {g.Rank + 1}: {g.ContractsToNextRank} заказ(а)";
        Rect sr = new Rect(c.x, c.y, c.width, 20f);
        GUI.Label(sr,
            $"{UiKit.B(UiKit.Col(UiKit.GoldHex, Sym + " " + TradeGuild.Marks))}     " +
            $"Лавки {UiKit.B(g.Listings.Count + "/" + g.SlotsOwned)}     " +
            $"Комиссия {UiKit.B((g.FeeRate * 100f).ToString("F0") + "%")}     " +
            $"Ранг {UiKit.B(g.Rank.ToString())}", line);
        UiKit.Tip(sr, $"Марки гильдии ({Sym}) — деньги рынка.\nЛавок: {g.Listings.Count} занято из {g.SlotsOwned} (можно до {g.MaxSlots}).\n" +
                      $"Комиссия берётся с выручки.\nРепутация: ранг {g.Rank}, {next}.\nЗаработано {g.TotalEarned} / потрачено {g.TotalSpent}");

        // --- Что происходит на рынке (одна строка) ---
        string ev, evTip = null;
        if (!TradeGuild.Open) { ev = UiKit.Col(UiKit.BadHex, "<b>Торговый дом закрыт</b> — купцы ждут за краем карты"); evTip = "Дом разрушен или стоит вне зелёной зоны."; }
        else if (WarManager.IsWar) { ev = UiKit.Col(UiKit.BadHex, "<b>Война</b> — купцы не заходят и ничего не покупают"); }
        else if (g.ActiveEvent != null)
        {
            int mins = Mathf.CeilToInt(g.EventLeft / 60f);
            ev = UiKit.Col(UiKit.WarnHex, "<b>" + g.ActiveEvent.title + "</b>") + UiKit.Col(UiKit.MutedHex, $" · ещё {mins} мин") + ": " + g.ActiveEvent.text;
            evTip = $"<b>{g.ActiveEvent.title}</b>\n{g.ActiveEvent.text}\nЕщё {mins} мин.";
        }
        else ev = UiKit.Col(UiKit.MutedHex, "На рынке спокойно");
        Rect er = new Rect(c.x, c.y + 20f, c.width, 20f);
        GUI.Label(er, ev, line);
        UiKit.Tip(er, evTip);

        // --- Вкладки ---
        int openContracts = 0;
        foreach (var ct in g.Contracts) if (ct.state == 0) openContracts++;
        string[] tabs = { "Купить", "Мои лавки", $"Заказы ({openContracts})", "Цены", $"Обозы ({MerchantCart.All.Count})" };
        int nt = UiKit.Tabs(new Rect(c.x, c.y + 44f, c.width, 28f), tabs, tab);
        if (nt != tab) { tab = nt; scroll = Vector2.zero; }

        // --- Содержимое с прокруткой ---
        Rect view = new Rect(c.x, c.y + 78f, c.width, c.yMax - c.y - 78f);
        float cw = view.width - 14f;
        scroll = GUI.BeginScrollView(view, scroll, new Rect(0f, 0f, cw, Mathf.Max(contentH, view.height - 4f)));
        float used;
        switch (tab)
        {
            case 0: used = DrawBuy(g, cw); break;
            case 1: used = DrawShops(g, cw); break;
            case 2: used = DrawContracts(g, cw); break;
            case 3: used = DrawPrices(g, cw); break;
            default: used = DrawCarts(g, cw); break;
        }
        GUI.EndScrollView();
        contentH = used + 6f;
    }

    // ---------- Мелочи ----------

    // Полоса-подложка строки таблицы (через одну)
    static void RowBg(float y, float w, float h, bool odd)
    {
        if (!odd) return;
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.05f);
        GUI.DrawTexture(new Rect(0f, y, w, h), Texture2D.whiteTexture);
        GUI.color = old;
    }

    // Кнопка действия: если есть помеха - серая, на ней коротко причина, полностью - в подсказке
    static bool ActButton(Rect r, string label, string problem, UiKit.Kind k, string tip = null)
    {
        if (string.IsNullOrEmpty(problem)) return UiKit.Button(r, label, true, k, tip);
        UiKit.Button(r, problem, false, k, problem + (string.IsNullOrEmpty(tip) ? "" : "\n" + tip));
        return false;
    }

    // Цвет цены относительно рынка
    static string PriceHex(int price, int market)
    {
        float ratio = price / (float)Mathf.Max(1, market);
        return ratio <= 0.95f ? UiKit.GoodHex : (ratio <= 1.15f ? "#EBE8E0" : UiKit.WarnHex);
    }

    // Сворачиваемая справка внизу вкладки. Возвращает y под ней.
    float HelpFold(float y, float w, string id, string text)
    {
        if (UiKit.Fold(new Rect(4f, y, 200f, UiKit.FoldH), id, "Как это работает"))
        {
            y += UiKit.FoldH;
            float th = UiKit.TextH(text, w - 12f, para);
            GUI.Label(new Rect(12f, y, w - 12f, th), text, para);
            return y + th + 4f;
        }
        return y + UiKit.FoldH + 2f;
    }

    // Ряд разделов (Сырьё / Материалы / ...). counts - сколько всего в каждом разделе (для подписи).
    int CategoryRow(float y, float w, int current, int[] counts)
    {
        string[] names = MarketGoods.CategoryNames;
        string[] labels = new string[names.Length];
        for (int i = 0; i < names.Length; i++) labels[i] = counts != null ? $"{names[i]} ({counts[i]})" : names[i];
        int res = UiKit.Tabs(new Rect(0f, y, w, 24f), labels, current);
        if (res != current) scroll = Vector2.zero;
        return res;
    }

    // ---------- Купить ----------

    // Колонки строки предложения
    const float BtnW = 150f, StepW = 82f, StockW = 64f, PriceW = 130f;

    float DrawBuy(TradeGuild g, float w)
    {
        float y = 0f;
        int[] counts = new int[6];
        foreach (var o in g.Offers) if (o.packs > 0) counts[Mathf.Clamp(o.Category, 0, 5)]++;
        buyCat = CategoryRow(y, w, buyCat, counts);
        y += 32f;

        // Шапка таблицы
        float px = w - BtnW - StepW - StockW - PriceW - 12f;
        GUI.Label(new Rect(8f, y, px - 8f, 18f), "Товар", lineMuted);
        GUI.Label(new Rect(px, y, PriceW, 18f), "Цена (рынок)", lineMuted);
        GUI.Label(new Rect(px + PriceW, y, StockW, 18f), "Есть", lineMuted);
        y += 20f;

        bool any = false, odd = false;
        bool en = GUI.enabled;
        foreach (var o in g.Offers.ToArray())
        {
            if (o.packs <= 0 || o.Category != buyCat) continue;
            any = true;
            RowBg(y, w, 30f, odd);
            odd = !odd;
            y = o.IsItem ? ItemOfferRow(g, o, w, y, en) : GoodOfferRow(g, o, w, y, en);
        }

        if (!any)
        {
            string empty = buyCat == 5 && BarracksController.FindType("Мечник") == null
                ? "Наёмников предлагают только городам с казармой."
                : "В этом разделе купцов пока нет — скоро подъедут.";
            GUI.Label(new Rect(8f, y + 4f, w, 20f), UiKit.Col(UiKit.MutedHex, empty), line);
            y += 26f;
        }

        y += 6f;
        return HelpFold(y, w, "market.buy.help",
            "Купили — марки списываются сразу, купец везёт товар в Торговый дом. Сырьё несут в Ратушу, оружие — в Оружейную, " +
            "модули и ловушки — в Ратушу (их может поставить любой город), наёмник идёт в казарму.\n" +
            UiKit.Col(UiKit.GoodHex, "★") + " — дешевле рынка. Shift + −/+ — шаг 10. Одни купцы уезжают, другие приезжают.");
    }

    float GoodOfferRow(TradeGuild g, TradeGuild.Offer o, float w, float y, bool en)
    {
        int market = g.Price(o.good);
        string bargain = o.bargain ? " " + UiKit.Col(UiKit.GoodHex, "★") : "";
        float px = w - BtnW - StepW - StockW - PriceW - 12f;

        Rect nr = new Rect(8f, y + 5f, px - 12f, 20f);
        GUI.Label(nr, MarketGoods.Colored(o.good, MarketGoods.PackName(o.good)) + bargain, line);
        UiKit.Tip(nr, $"Продаёт: {o.seller.Name}" + (o.bargain ? "\n★ дешевле рынка" : ""));
        GUI.Label(new Rect(px, y + 5f, PriceW, 20f),
            $"<color={PriceHex(o.price, market)}><b>{o.price} {Sym}</b></color> {UiKit.Col(UiKit.MutedHex, "(" + market + ")")}", line);
        GUI.Label(new Rect(px + PriceW, y + 5f, StockW, 20f), $"{o.packs} лот.", line);

        if (!buyPick.TryGetValue(o.id, out int pick)) pick = 1;
        pick = Mathf.Clamp(pick, 1, o.packs);
        float bx = w - BtnW - StepW - 6f;
        if (GUI.Button(new Rect(bx, y + 3f, 24f, 24f), "−")) pick = Mathf.Max(1, pick - (Event.current.shift ? 10 : 1));
        GUI.Label(new Rect(bx + 24f, y + 3f, 34f, 24f), $"<b>{pick}</b>", center);
        if (GUI.Button(new Rect(bx + 58f, y + 3f, 24f, 24f), "+")) pick = Mathf.Min(o.packs, pick + (Event.current.shift ? 10 : 1));
        buyPick[o.id] = pick;

        string p = g.BuyProblem(o, pick);
        if (ActButton(new Rect(w - BtnW, y + 3f, BtnW - 2f, 24f), $"Купить · {pick * o.price} {Sym}", p, UiKit.Kind.Primary)) g.Buy(o, pick);
        return y + 30f;
    }

    float ItemOfferRow(TradeGuild g, TradeGuild.Offer o, float w, float y, bool en)
    {
        int market = g.ItemPrice(o.item);
        string bargain = o.bargain ? " " + UiKit.Col(UiKit.GoodHex, "★") : "";
        float px = w - BtnW - StepW - StockW - PriceW - 12f;

        // Название и коротко свойства (полностью - в подсказке)
        Rect nr = new Rect(8f, y + 5f, px - 12f + StockW, 20f);
        GUI.Label(nr, $"<b>{o.item.Name}</b>{bargain}  {UiKit.Col(UiKit.MutedHex, o.item.Details)}", line);
        UiKit.Tip(nr, $"<b>{o.item.Name}</b>\n{o.item.Details}\nПродаёт: {o.seller.Name}");
        GUI.Label(new Rect(px + StockW, y + 5f, PriceW, 20f),
            $"<color={PriceHex(o.price, market)}><b>{o.price} {Sym}</b></color> {UiKit.Col(UiKit.MutedHex, "(" + market + ")")}", line);

        string p = g.BuyProblem(o, 1);
        if (ActButton(new Rect(w - BtnW, y + 3f, BtnW - 2f, 24f), "Купить", p, UiKit.Kind.Primary)) g.Buy(o, 1);
        return y + 30f;
    }

    // ---------- Мои лавки ----------

    float DrawShops(TradeGuild g, float w)
    {
        float y = 0f;
        bool en = GUI.enabled;

        if (g.HeldGearCount > 0)
        {
            GUI.Label(new Rect(4f, y, w, 20f), UiKit.Col(UiKit.WarnHex, $"Ждут места в Оружейной: {g.HeldGearCount}"), line);
            y += 22f;
        }

        if (editing) y = DrawEditor(g, w, y);

        bool odd = false;
        foreach (var l in g.Listings.ToArray())
        {
            RowBg(y, w, 46f, odd);
            odd = !odd;
            int market = g.MarketPriceOf(l);
            float chance = TradeGuild.BuyChance(l.price / (float)market);
            string name = l.IsItem ? "<b>" + l.item.Name + "</b>" : MarketGoods.Colored(l.good, "<b>" + MarketGoods.PackName(l.good) + "</b>");
            string state = l.cancelled ? UiKit.Col(UiKit.WarnHex, "снято") : TradeGuild.SpeedText(chance);
            GUI.Label(new Rect(8f, y + 3f, w - 240f, 20f),
                $"{name}   <b>{l.price} {Sym}</b> {UiKit.Col(UiKit.MutedHex, "(рынок " + market + ")")}   {state}", line);

            var parts = new List<string>();
            bool merc = l.IsItem && l.item.kind == ItemKind.Soldier;
            if (l.incoming > 0) parts.Add(UiKit.Col(UiKit.WarnHex, $"несут: {l.incoming}"));
            if (l.shelf > 0) parts.Add(merc ? "ждёт нанимателя в лагере" : $"на витрине: {l.shelf}");
            if (l.awaiting > 0) parts.Add(UiKit.Col(UiKit.InfoHex, $"продано, ждут купца: {l.awaiting}"));
            if (l.returning > 0) parts.Add(UiKit.Col(UiKit.WarnHex, merc ? "возвращается в город" : "несут обратно: " + l.returning));
            if (l.sold > 0) parts.Add($"продано {l.sold} ({UiKit.Col(UiKit.GoodHex, "+" + l.earned + " " + Sym)})");
            GUI.Label(new Rect(8f, y + 23f, w - 240f, 20f), string.Join(UiKit.Col(UiKit.MutedHex, " · "), parts), lineMuted);

            float bx = w - 228f;
            GUI.enabled = en && !l.cancelled;
            if (UiKit.Button(new Rect(bx, y + 9f, 28f, 26f), "−", UiKit.Kind.Normal, "Цена −1 (Shift: −5)")) g.ChangePrice(l, Event.current.shift ? -5 : -1);
            if (UiKit.Button(new Rect(bx + 30f, y + 9f, 28f, 26f), "+", UiKit.Kind.Normal, "Цена +1 (Shift: +5)")) g.ChangePrice(l, Event.current.shift ? 5 : 1);
            if (UiKit.Button(new Rect(bx + 60f, y + 9f, 72f, 26f), "Рынок", UiKit.Kind.Normal, $"Поставить рыночную цену: {market} {Sym}")) l.price = market;
            if (UiKit.Button(new Rect(bx + 136f, y + 9f, 88f, 26f), "Снять", UiKit.Kind.Danger, "Снять с продажи — непроданное унесут обратно в город")) g.Cancel(l);
            GUI.enabled = en;
            y += 48f;
        }

        // Свободные лавки
        for (int i = 0; i < g.FreeSlots; i++)
        {
            RowBg(y, w, 32f, odd);
            odd = !odd;
            GUI.Label(new Rect(8f, y + 6f, w - 240f, 20f), UiKit.Col(UiKit.MutedHex, "Пустая лавка"), line);
            string why = !TradeGuild.Open ? "Торговый дом закрыт" : (editing ? "Уже оформляется" : "");
            if (ActButton(new Rect(w - 228f, y + 3f, 224f, 26f), "Выставить товар", why, UiKit.Kind.Primary)) StartEditor(g);
            y += 34f;
        }

        // Покупка лавки
        y += 6f;
        string sp = g.BuySlotProblem();
        if (ActButton(new Rect(0f, y, Mathf.Min(w, 360f), 28f), $"Купить лавку №{g.SlotsOwned + 1} · {g.NextSlotCost} {Sym}", sp == "" ? "" : "Новая лавка: " + sp, UiKit.Kind.Normal))
            g.BuySlot();
        y += 36f;

        string feeNote = g.NoFeeEvent ? "сейчас ярмарка — комиссии нет" : $"комиссия гильдии — {g.FeeRate * 100f:F0}% с выручки";
        return HelpFold(y, w, "market.shops.help",
            "Выставьте товар в лавку: сырьё несут из Ратуши, оружие — из Оружейной, модули — со склада, а боец сам уходит в лагерь наёмников. " +
            $"Чем ниже цена относительно рынка, тем быстрее купят. Марки приходят, когда купец заберёт товар ({feeNote}). " +
            "Во время войны купцы не покупают. Shift + −/+ — шаг цены 5.");
    }

    void StartEditor(TradeGuild g)
    {
        editing = true;
        editCat = 0;
        hasGood = false;
        editItem = null;
        editPacks = 1;
        // Сразу выбираем первый товар, который есть
        foreach (var gid in MarketGoods.AllVisible(0))
            if (MarketGoods.HavePacks(gid) > 0) { SelectGood(g, gid); break; }
    }

    void SelectGood(TradeGuild g, GoodId gid)
    {
        hasGood = true;
        editItem = null;
        editGood = gid;
        editPacks = Mathf.Clamp(editPacks, 1, Mathf.Max(1, Mathf.Min(MarketGoods.HavePacks(gid), g.MaxPacks)));
        editPrice = g.Price(gid);
    }

    void SelectItem(TradeGuild g, MarketItem it)
    {
        hasGood = false;
        editItem = it;
        editPrice = g.ItemPrice(it);
    }

    // То же самое: та же вещь (лист кандидатов строится заново каждый кадр)
    static bool SameItem(MarketItem a, MarketItem b)
    {
        if (a == null || b == null || a.kind != b.kind) return false;
        switch (a.kind)
        {
            case ItemKind.Weapon: return a.weapon == b.weapon;
            case ItemKind.Shield: return a.shield == b.shield;
            case ItemKind.Module: return a.kit.kind == b.kit.kind && a.kit.mk == b.kit.mk;
            case ItemKind.Trap: return a.trap == b.trap;
            default: return a.agent == b.agent;
        }
    }

    float DrawEditor(TradeGuild g, float w, float y)
    {
        bool itemCat = editCat >= 3;

        // Что есть в разделе
        List<GoodId> goods = null;
        List<MarketItem> items = null;
        if (itemCat) items = MarketItem.SellableNow(editCat);
        else
        {
            goods = MarketGoods.AllVisible(editCat);
            goods.RemoveAll(x => MarketGoods.HavePacks(x) <= 0);
        }

        const int cols = 4;
        float bw = (w - 16f - (cols - 1) * 4f) / cols;
        int count = itemCat ? items.Count : goods.Count;
        int rows = itemCat ? Mathf.Min(count, 6) : Mathf.CeilToInt(count / (float)cols);
        float listH = Mathf.Max(1, rows) * (itemCat ? 30f : 28f);
        float detailH = itemCat ? 30f + 24f : 30f + 30f + 24f;
        float boxH = 4f + 24f + 30f + listH + 8f + detailH + 40f;
        GUI.Box(new Rect(0f, y, w, boxH), GUIContent.none);
        float cy = y + 4f;

        GUI.Label(new Rect(8f, cy, w - 16f, 22f), "<b>Новая лавка</b>  " + UiKit.Col(UiKit.MutedHex, "раздел → товар → количество и цена"), line);
        cy += 24f;

        int newCat = CategoryRowAt(8f, cy, w - 16f, editCat);
        if (newCat != editCat) { editCat = newCat; hasGood = false; editItem = null; editPacks = 1; }
        cy += 30f;

        if (count == 0)
        {
            string why = editCat == 3 ? "В Оружейных нет оружия и щитов"
                       : editCat == 4 ? "Нет готовых модулей и ловушек"
                       : editCat == 5 ? "Нет бойцов дома (в походе и в бою продавать нельзя)"
                       : "Нечего продать — нет ни одного полного лота";
            GUI.Label(new Rect(8f, cy + 2f, w - 16f, 22f), UiKit.Col(UiKit.BadHex, why), line);
        }
        else if (itemCat)
        {
            // Штучные вещи - списком (первые 6, остальное - после продажи)
            for (int i = 0; i < Mathf.Min(items.Count, 6); i++)
            {
                MarketItem it = items[i];
                bool sel = SameItem(editItem, it);
                Rect br = new Rect(8f, cy + i * 30f, w - 16f, 28f);
                string txt = $"{(sel ? "▸ " : "")}<b>{it.Name}</b>  {UiKit.Col(UiKit.MutedHex, it.Details)}";
                if (GUI.Button(br, txt, sel ? pickOn : pick)) SelectItem(g, it);
                UiKit.Tip(br, $"<b>{it.Name}</b>\n{it.Details}\nРыночная цена: {g.ItemPrice(it)} {Sym}");
            }
            // Выбранная вещь могла исчезнуть (продана, боец ушёл в поход) - снимаем выбор
            if (editItem != null)
            {
                bool still = false;
                foreach (var it in items) if (SameItem(editItem, it)) { editItem = it; still = true; break; }
                if (!still) editItem = null;
            }
        }
        else
        {
            for (int i = 0; i < goods.Count; i++)
            {
                GoodId gid = goods[i];
                int col = i % cols, row = i / cols;
                bool sel = hasGood && editGood.Equals(gid);
                Rect br = new Rect(8f + col * (bw + 4f), cy + row * 28f, bw, 26f);
                if (GUI.Button(br, $"{(sel ? "▸ " : "")}{MarketGoods.PackName(gid)} {UiKit.Col(UiKit.MutedHex, "(" + MarketGoods.HavePacks(gid) + ")")}", sel ? pickOn : pick))
                    SelectGood(g, gid);
                UiKit.Tip(br, $"В городе: {MarketGoods.HavePacks(gid)} лот(а). Рыночная цена: {g.Price(gid)} {Sym} за лот");
            }
        }
        cy += listH + 8f;

        if (hasGood && MarketGoods.HavePacks(editGood) <= 0) hasGood = false;
        float bx = 120f;
        int market = hasGood ? g.Price(editGood) : (editItem != null ? g.ItemPrice(editItem) : 1);

        if (hasGood)
        {
            int maxPacks = Mathf.Max(1, Mathf.Min(MarketGoods.HavePacks(editGood), g.MaxPacks));
            editPacks = Mathf.Clamp(editPacks, 1, maxPacks);

            GUI.Label(new Rect(8f, cy + 4f, 110f, 22f), "Лотов:", line);
            if (GUI.Button(new Rect(bx, cy, 36f, 26f), "−10")) editPacks = Mathf.Max(1, editPacks - 10);
            if (GUI.Button(new Rect(bx + 38f, cy, 28f, 26f), "−")) editPacks = Mathf.Max(1, editPacks - 1);
            GUI.Label(new Rect(bx + 68f, cy, 44f, 26f), $"<b>{editPacks}</b>", center);
            if (GUI.Button(new Rect(bx + 114f, cy, 28f, 26f), "+")) editPacks = Mathf.Min(maxPacks, editPacks + 1);
            if (GUI.Button(new Rect(bx + 144f, cy, 36f, 26f), "+10")) editPacks = Mathf.Min(maxPacks, editPacks + 10);
            if (GUI.Button(new Rect(bx + 182f, cy, 56f, 26f), "Все")) editPacks = maxPacks;
            Rect ur = new Rect(bx + 246f, cy + 4f, w - bx - 254f, 22f);
            GUI.Label(ur, "= " + MarketGoods.UnitsText(editGood, editPacks * MarketGoods.Pack(editGood)), line);
            UiKit.Tip(ur, $"В одну лавку влезает до {g.MaxPacks} лотов");
            cy += 30f;
        }

        if (hasGood || editItem != null)
        {
            GUI.Label(new Rect(8f, cy + 4f, 110f, 22f), hasGood ? "Цена за лот:" : "Цена:", line);
            if (GUI.Button(new Rect(bx, cy, 36f, 26f), "−5")) editPrice = Mathf.Max(1, editPrice - 5);
            if (GUI.Button(new Rect(bx + 38f, cy, 28f, 26f), "−")) editPrice = Mathf.Max(1, editPrice - 1);
            GUI.Label(new Rect(bx + 68f, cy, 44f, 26f), $"<b>{editPrice}</b> {Sym}", center);
            if (GUI.Button(new Rect(bx + 114f, cy, 28f, 26f), "+")) editPrice++;
            if (GUI.Button(new Rect(bx + 144f, cy, 36f, 26f), "+5")) editPrice += 5;
            if (UiKit.Button(new Rect(bx + 182f, cy, 56f, 26f), "Рынок", UiKit.Kind.Normal, $"Рыночная цена: {market} {Sym}")) editPrice = market;
            float chance = TradeGuild.BuyChance(editPrice / (float)Mathf.Max(1, market));
            GUI.Label(new Rect(bx + 246f, cy + 4f, w - bx - 254f, 22f),
                $"{UiKit.Col(UiKit.MutedHex, "рынок " + market + " " + Sym + ", продастся:")} {TradeGuild.SpeedText(chance)}", line);
            cy += 30f;

            int gross = (hasGood ? editPacks : 1) * editPrice;
            int fee = g.Fee(gross);
            bool soldier = editItem != null && editItem.kind == ItemKind.Soldier;
            string warn = soldier ? "   " + UiKit.Col(UiKit.WarnHex, "боец сразу уйдёт в лагерь наёмников") : "";
            Rect gr = new Rect(8f, cy, w - 16f, 22f);
            GUI.Label(gr, $"Выручка: {gross} − комиссия {fee} = {UiKit.B(UiKit.Col(UiKit.GoodHex, (gross - fee) + " " + Sym))}{warn}", line);
            if (soldier) UiKit.Tip(gr, "Боец уйдёт в лагерь наёмников вместе с оружием, щитом и конём.");
            cy += 24f;
        }
        else cy += detailH;

        string p = hasGood ? g.ListProblem(editGood, editPacks)
                 : (editItem != null ? g.ListItemProblem(editItem) : "Выберите товар");
        if (ActButton(new Rect(8f, cy + 4f, w * 0.6f, 30f), "Выставить в лавку", p, UiKit.Kind.Primary))
        {
            bool ok = hasGood ? g.CreateListing(editGood, editPacks, editPrice) : g.CreateItemListing(editItem, editPrice);
            if (ok) { editing = false; editItem = null; }
        }
        if (GUI.Button(new Rect(w * 0.6f + 14f, cy + 4f, w * 0.4f - 22f, 30f), "Отмена")) { editing = false; editItem = null; }

        return y + boxH + 8f;
    }

    // Ряд разделов внутри редактора (со смещением x)
    int CategoryRowAt(float x, float y, float w, int current)
    {
        return UiKit.Tabs(new Rect(x, y, w, 24f), MarketGoods.CategoryNames, current);
    }

    // ---------- Заказы гильдии ----------

    float DrawContracts(TradeGuild g, float w)
    {
        float y = 0f;

        if (g.Contracts.Count == 0)
        {
            GUI.Label(new Rect(4f, y, w, 22f), TradeGuild.Open
                ? UiKit.Col(UiKit.MutedHex, "Заказов пока нет — гильдия присылает их время от времени.")
                : UiKit.Col(UiKit.BadHex, "Торговый дом закрыт — заказы не приходят."), line);
            y += 26f;
        }

        bool odd = false;
        foreach (var c in g.Contracts.ToArray())
        {
            RowBg(y, w, 46f, odd);
            odd = !odd;
            int units = c.packs * MarketGoods.Pack(c.good);
            int marketValue = c.packs * g.Price(c.good);
            float mul = marketValue > 0 ? c.reward / (float)marketValue : 1f;
            int have = MarketGoods.HavePacks(c.good);

            Rect tr = new Rect(8f, y + 3f, w - 240f, 20f);
            GUI.Label(tr,
                $"<b>{c.client}</b>: {MarketGoods.Colored(c.good, MarketGoods.UnitsText(c.good, units))}  " +
                UiKit.Col(have >= c.packs ? UiKit.GoodHex : UiKit.MutedHex, $"(есть {have}/{c.packs} лот.)"), line);
            UiKit.Tip(tr, $"Нужно {c.packs} × {MarketGoods.PackName(c.good)}, у вас {have}.");

            string state;
            if (c.state == 1) state = UiKit.Col(UiKit.WarnHex, "несут в Торговый дом");
            else if (c.state == 2) state = UiKit.Col(UiKit.InfoHex, "ждёт обоз гильдии");
            else
            {
                int left = Mathf.Max(0, Mathf.CeilToInt(c.deadline - Time.time));
                state = $"срок {UiKit.B(left / 60 + ":" + (left % 60).ToString("00"))}";
            }
            Rect rr = new Rect(8f, y + 23f, w - 240f, 20f);
            GUI.Label(rr, $"Награда {UiKit.B(UiKit.Col(UiKit.GoodHex, c.reward + " " + Sym))} {UiKit.Col(UiKit.MutedHex, $"(×{mul:F1})")}   {state}", line);
            UiKit.Tip(rr, $"Награда в {mul:F1} раза выше рыночной цены партии.");

            if (c.state == 0)
            {
                string p = g.FulfillProblem(c);
                if (ActButton(new Rect(w - 228f, y + 8f, 224f, 30f), "Выполнить", p, UiKit.Kind.Primary,
                    "Товар понесут в Торговый дом, обоз гильдии заберёт и заплатит")) g.Fulfill(c);
            }
            y += 48f;
        }

        y += 4f;
        Rect fr = new Rect(4f, y, w, 20f);
        GUI.Label(fr, UiKit.Col(UiKit.MutedHex, $"Выполнено: {g.ContractsDone} · ранг {g.Rank}/5 · заказов одновременно до {g.MaxContracts}"), line);
        UiKit.Tip(fr, "Число одновременных заказов растёт с уровнем Торгового дома.");
        y += 24f;
        return HelpFold(y, w, "market.contracts.help",
            "Гильдии нужны партии товара к сроку — платит заметно выше рынка. Взялись — товар несут в Торговый дом, " +
            "обоз гильдии его забирает и платит. Каждые 3 заказа — ранг репутации: −1% комиссии и +10% к наградам.");
    }

    // ---------- Цены ----------

    float DrawPrices(TradeGuild g, float w)
    {
        float y = 0f;
        float c1 = 8f, c2 = w * 0.40f, c3 = w * 0.58f, c4 = w * 0.74f;
        GUI.Label(new Rect(c1, y, c2 - c1, 18f), "Товар (лот)", lineMuted);
        GUI.Label(new Rect(c2, y, c3 - c2, 18f), "Цена", lineMuted);
        GUI.Label(new Rect(c3, y, c4 - c3, 18f), "За штуку", lineMuted);
        GUI.Label(new Rect(c4, y, w - c4, 18f), "У вас", lineMuted);
        Rect hr = new Rect(c2, y, c3 - c2, 18f);
        UiKit.Tip(hr, "▲▼ — куда цена шла последние полминуты.\n" + UiKit.Col(UiKit.WarnHex, "●") + " — товар задет событием.");
        y += 20f;

        for (int cat = 0; cat < 3; cat++)
        {
            y = UiKit.Section(0f, y + 2f, w, MarketGoods.CategoryNames[cat]);
            bool odd = false;
            foreach (var gid in MarketGoods.AllVisible(cat))
            {
                RowBg(y, w, 22f, odd);
                odd = !odd;

                int price = g.Price(gid);
                int tr = g.Trend(gid.kind);
                string arrow = tr > 0 ? UiKit.Col(UiKit.GoodHex, "▲") : (tr < 0 ? UiKit.Col(UiKit.BadHex, "▼") : UiKit.Col(UiKit.MutedHex, "•"));
                string evMark = g.EventTouches(gid.kind) ? " " + UiKit.Col(UiKit.WarnHex, "●") : "";
                int pack = MarketGoods.Pack(gid);

                GUI.Label(new Rect(c1 + 4f, y + 1f, c2 - c1 - 4f, 20f), MarketGoods.Colored(gid, MarketGoods.PackName(gid)) + evMark, line);
                GUI.Label(new Rect(c2, y + 1f, c3 - c2, 20f), $"{arrow} <b>{price} {Sym}</b>", line);
                GUI.Label(new Rect(c3, y + 1f, c4 - c3, 20f), UiKit.Col(UiKit.MutedHex, $"{price / (float)pack:0.##} {Sym}"), line);
                int have = MarketGoods.Have(gid);
                GUI.Label(new Rect(c4, y + 1f, w - c4, 20f), have > 0 ? $"{have} шт. ({have / pack} лот.)" : UiKit.Col(UiKit.MutedHex, "—"), line);
                y += 22f;
            }
        }

        y += 8f;
        string ev3 = g.EventTouchesCat(3) ? " " + UiKit.Col(UiKit.WarnHex, "●") : "";
        string ev5 = g.EventTouchesCat(5) ? " " + UiKit.Col(UiKit.WarnHex, "●") : "";
        Rect nr = new Rect(4f, y, w, 20f);
        GUI.Label(nr, UiKit.Col(UiKit.MutedHex, $"Оружие{ev3}, модули, наёмники{ev5} — штучный товар, цены во вкладке «Купить»"), line);
        UiKit.Tip(nr, "Цена штучного товара зависит от самой вещи: качество, улучшения, доводка, заряды, марка, снаряжение бойца.");
        return y + 24f;
    }

    // ---------- Обозы и новости ----------

    float DrawCarts(TradeGuild g, float w)
    {
        float y = UiKit.Section(0f, 0f, w, "Обозы купцов");
        if (MerchantCart.All.Count == 0)
        {
            GUI.Label(new Rect(8f, y, w, 20f), UiKit.Col(UiKit.MutedHex, "Сейчас никто не едет"), line);
            y += 22f;
        }
        foreach (var c in MerchantCart.All)
        {
            if (c == null) continue;
            string col = "#" + ColorUtility.ToHtmlStringRGB(c.Who.color);
            GUI.Label(new Rect(8f, y, w - 16f, 20f),
                $"<color={col}>■</color> <b>{c.Who.Name}</b> — {c.Purpose}: {UiKit.Col(UiKit.MutedHex, c.StatusText)}", line);
            y += 21f;
        }

        if (g.Deliveries.Count > 0)
        {
            y = UiKit.Section(0f, y + 8f, w, "Ваши покупки");
            foreach (var d in g.Deliveries)
            {
                GUI.Label(new Rect(8f, y, w - 16f, 20f), $"{d.label} от {d.from}: {UiKit.Col(UiKit.MutedHex, d.status)}", line);
                y += 21f;
            }
        }

        y = UiKit.Section(0f, y + 8f, w, "Лента рынка");
        if (g.News.Count == 0)
        {
            GUI.Label(new Rect(8f, y, w, 20f), UiKit.Col(UiKit.MutedHex, "Пока тихо"), line);
            y += 22f;
        }
        foreach (var n in g.News)
        {
            GUI.Label(new Rect(8f, y, w - 16f, 20f), "• " + n, line);
            y += 21f;
        }
        return y + 4f;
    }
}
