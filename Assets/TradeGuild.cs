using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Торговая гильдия - общий рынок. Пока его участники - боты-купцы, потом их место займут живые игроки.
/// Валюта рынка - МАРКИ гильдии (ResourceManager.marks). Золотом не торгуют: его добывают шахты,
/// а марки входят в экономику только через гильдию:
///  - продажа своих товаров купцам (лавки);
///  - заказы гильдии (главный источник марок: гильдия платит выше рынка);
///  - подъёмные при постройке первого Торгового дома.
/// Уходят марки на покупки у купцов, новые лавки и комиссию гильдии.
///
/// ТОВАРЫ. Сырьё, материалы и зелья продаются лотами (GoodId, MarketGoods). Оружие, щиты, модули для башен
/// и бойцы - штучно (MarketItem): у каждой вещи своя цена по её ценности.
///
/// ЛАВКИ. Первая лавка бесплатна, следующие покупаются за марки (сколько всего - зависит от уровня Торгового дома).
/// В лавку выставляется один товар: N лотов (или одна вещь) по цене за лот. Товар физически несут в Торговый дом
/// (оружие - из Оружейной, модуль - со двора / из Ратуши; боец сам уходит в лагерь наёмников гильдии).
/// Купцы покупают тем охотнее, чем ниже цена относительно рыночной; купив, присылают обоз -
/// марки (за вычетом комиссии) приходят, когда купец забрал товар. Снять товар - его вернут в город.
///
/// ПОКУПКИ. Купцы выставляют свои предложения по вкладкам. Купили - платите марки сразу, купец везёт товар
/// с края карты в Торговый дом; оттуда сырьё несут в Ратушу, оружие - в Оружейную, модули - в Ратушу,
/// а наёмник сходит с повозки и идёт в казарму.
///
/// ЗАКАЗЫ ГИЛЬДИИ. Гильдии нужны партии товара к сроку; плата - выше рынка. Выполненные заказы растят
/// РЕПУТАЦИЮ: ниже комиссия, дороже заказы.
///
/// ЦЕНЫ живые: гуляют сами, падают от продаж, растут от покупок; события на время сильно двигают цены и спрос.
/// Во время войны купцы в город не заходят (ждут за краем карты) и ничего не покупают.
/// Создаётся сам, когда построен первый Торговый дом. Окно рынка - MarketWindow.
/// </summary>
public class TradeGuild : MonoBehaviour
{
    public static TradeGuild Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; nextId = 1; }

    public static TradeGuild Ensure()
    {
        if (Instance == null)
        {
            GameObject go = new GameObject("TradeGuild");
            Instance = go.AddComponent<TradeGuild>();
            go.AddComponent<MarketWindow>();
        }
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        int n = MarketGoods.KindCount;
        index = new float[n];
        prevIndex = new float[n];
        for (int i = 0; i < n; i++) index[i] = prevIndex[i] = Random.Range(0.9f, 1.1f);
        for (int i = 0; i < itemIndex.Length; i++) itemIndex[i] = Random.Range(0.9f, 1.1f);
        nextEventAt = Time.time + Random.Range(150f, 240f);
    }

    // ---------- Числа рынка ----------
    public const string Sym = "◈";
    public const int StartBonus = 15;                                     // подъёмные за первый Торговый дом
    static readonly int[] SlotCosts = { 0, 20, 45, 80, 130, 200, 300 };  // цена 1-й, 2-й... лавки
    static readonly int[] SlotsByLevel = { 3, 5, 7 };                    // сколько лавок можно держать
    static readonly int[] PacksByLevel = { 20, 40, 60 };                 // лотов в одной лавке
    static readonly int[] ContractsByLevel = { 2, 3, 4 };
    static readonly float[] FeeByLevel = { 0.08f, 0.06f, 0.04f };        // комиссия гильдии с продажи
    static readonly float[] ArrivalByLevel = { 30f, 22f, 15f };          // сколько купец едет до края карты
    static readonly int[] ItemOffersByCat = { 5, 2, 3 };                 // штучных предложений: оружие, модули, наёмники
    static readonly float[] ItemDemand = { 0.8f, 0.6f, 0.6f };           // как охотно боты покупают штучное
    const float SaleTick = 4f;
    const float SaleInterval = 22f;    // при рыночной цене и обычном спросе лавку навещают раз в ~22 с

    static int nextId = 1;

    // ---------- Состояние ----------
    float[] index, prevIndex;                       // множитель цены по виду товара
    readonly float[] itemIndex = new float[3];      // ... и по штучным вкладкам (оружие, модули, войска)
    public int SlotsOwned { get; private set; } = 1;
    public int ContractsDone { get; private set; }
    public int TotalEarned { get; private set; }
    public int TotalSpent { get; private set; }
    public int FeesPaid { get; private set; }
    public bool BonusGiven { get; private set; }

    public int Rank => Mathf.Min(5, ContractsDone / 3);
    public int ContractsToNextRank => Rank >= 5 ? 0 : (Rank + 1) * 3 - ContractsDone;

    public readonly List<Listing> Listings = new List<Listing>();
    public readonly List<Offer> Offers = new List<Offer>();
    public readonly List<Contract> Contracts = new List<Contract>();
    public readonly List<Delivery> Deliveries = new List<Delivery>();
    public readonly List<string> News = new List<string>();
    readonly List<MarketItem> heldGear = new List<MarketItem>();   // купленное оружие ждёт места в Оружейной

    // Что уже везут (чтобы не купить больше, чем влезет)
    int pendingGear, pendingSpace, pendingPeople;

    float saleTimer, driftTimer, offerTimer = 1f, contractTimer = 2f, gearTimer, nextEventAt;
    GuildEvent ev;

    // ---------- Торговый дом ----------
    public static MarketHouse House => MarketHouse.Main;
    public static bool Open => House != null && House.Works;
    int Lvl => House != null ? Mathf.Clamp(House.Building.level, 1, 3) - 1 : 0;
    public int MaxSlots => SlotsByLevel[Lvl];
    public int MaxPacks => PacksByLevel[Lvl];
    public int MaxContracts => ContractsByLevel[Lvl];
    public bool NoFeeEvent => ev != null && ev.noFee;
    public float FeeRate => NoFeeEvent ? 0f : Mathf.Max(0.01f, FeeByLevel[Lvl] - Rank * 0.01f);
    public int Fee(int gross) { return FeeRate <= 0f ? 0 : Mathf.Max(1, Mathf.RoundToInt(gross * FeeRate)); }
    public int FreeSlots => SlotsOwned - Listings.Count;
    public int NextSlotCost => SlotsOwned < SlotCosts.Length ? SlotCosts[SlotsOwned] : 0;
    float ArrivalDelay => ArrivalByLevel[Lvl] + Random.Range(0f, 10f);
    public int HeldGearCount => heldGear.Count;

    static BuildingInstance Hall()
    {
        var pm = PopulationManager.Instance;
        return pm != null ? pm.Hall : null;
    }

    static ResourceManager RM => ResourceManager.Instance;
    public static int Marks => RM != null ? RM.marks : 0;

    /// <summary>Подъёмные за первый Торговый дом (один раз за игру).</summary>
    public void GiveStartBonus()
    {
        if (BonusGiven || RM == null) return;
        BonusGiven = true;
        RM.AddMarks(StartBonus);
        AddNews($"Гильдия приветствует новый Торговый дом: подъёмные <b>{StartBonus} {Sym}</b> и первая лавка бесплатно");
    }

    public void AddNews(string text)
    {
        News.Insert(0, text);
        if (News.Count > 12) News.RemoveAt(News.Count - 1);
    }

    // ---------- Цены ----------

    public int Price(GoodId g)
    {
        var info = MarketGoods.Of(g.kind);
        float p = info.price * MarketGoods.TierMul(g.tier) * index[(int)g.kind] * EventPriceMul(g.kind);
        return Mathf.Max(1, Mathf.RoundToInt(p));
    }

    /// <summary>Рыночная цена штучной вещи.</summary>
    public int ItemPrice(MarketItem it)
    {
        if (it == null) return 1;
        int c = it.Category - 3;
        return Mathf.Max(1, Mathf.RoundToInt(it.Value * itemIndex[c] * EventItemMul(it.Category)));
    }

    /// <summary>+1 - цена растёт, -1 - падает, 0 - стоит.</summary>
    public int Trend(GoodKind k)
    {
        float d = index[(int)k] - prevIndex[(int)k];
        return d > 0.015f ? 1 : (d < -0.015f ? -1 : 0);
    }

    void Shift(GoodKind k, float delta)
    {
        int i = (int)k;
        index[i] = Mathf.Clamp(index[i] + delta, 0.55f, 1.9f);
    }

    void ShiftItems(int category, float delta)
    {
        int i = category - 3;
        itemIndex[i] = Mathf.Clamp(itemIndex[i] + delta, 0.6f, 1.7f);
    }

    void Drift()
    {
        for (int i = 0; i < index.Length; i++)
        {
            prevIndex[i] = index[i];
            index[i] += (1f - index[i]) * 0.06f + Random.Range(-0.045f, 0.045f);   // гуляет, но тянется к норме
            index[i] = Mathf.Clamp(index[i], 0.55f, 1.9f);
        }
        for (int i = 0; i < itemIndex.Length; i++)
        {
            itemIndex[i] += (1f - itemIndex[i]) * 0.06f + Random.Range(-0.03f, 0.03f);
            itemIndex[i] = Mathf.Clamp(itemIndex[i], 0.6f, 1.7f);
        }
    }

    /// <summary>Как охотно купят лот по такой цене: 1 - как по рыночной, больше - охотнее, 0 - не купят.</summary>
    public static float BuyChance(float ratio)
    {
        float x = (1.8f - ratio) / 0.8f;
        if (x <= 0f) return 0f;
        return Mathf.Min(2.5f, x * x);
    }

    public static string SpeedText(float chance)
    {
        if (chance >= 1.6f) return "<color=#80FF80>уйдёт мигом</color>";
        if (chance >= 0.85f) return "<color=#A0FF80>быстро</color>";
        if (chance >= 0.4f) return "<color=#FFE060>не спеша</color>";
        if (chance > 0.05f) return "<color=#FFA060>медленно</color>";
        return "<color=#FF6060>никто не купит</color>";
    }

    // ---------- События ----------

    public class GuildEvent
    {
        public string title, text;
        public GoodKind[] goods;     // null - все товары
        public int[] itemCats;       // штучные вкладки (3 - оружие, 4 - модули, 5 - войска), которые задевает событие
        public float priceMul = 1f, demandMul = 1f;
        public bool noFee;
        public bool allGoods;        // все товары-лоты
        public float until;
        public bool Has(GoodKind k) { return allGoods || (goods != null && System.Array.IndexOf(goods, k) >= 0); }
        public bool HasCat(int c) { return itemCats != null && System.Array.IndexOf(itemCats, c) >= 0; }
    }

    static GuildEvent[] EventPool()
    {
        return new[]
        {
            new GuildEvent { title = "Неурожай в долинах", text = "еда, овёс и ягоды в цене", goods = new[] { GoodKind.Food, GoodKind.Oats, GoodKind.Berries }, priceMul = 1.5f, demandMul = 1.6f },
            new GuildEvent { title = "Большая стройка в столице", text = "дерево, камень, доски, плиты и брус нарасхват", goods = new[] { GoodKind.Wood, GoodKind.Stone, GoodKind.Planks, GoodKind.Plates, GoodKind.Beams, GoodKind.Monolith }, priceMul = 1.4f, demandMul = 1.5f },
            new GuildEvent { title = "Война на западной границе", text = "сталь, оружие и наёмники дорожают", goods = new[] { GoodKind.Iron, GoodKind.Steel, GoodKind.Reinforced, GoodKind.Parts }, itemCats = new[] { 3, 5 }, priceMul = 1.45f, demandMul = 1.5f },
            new GuildEvent { title = "Мор в южных землях", text = "нужны травы, зелья и еда", goods = new[] { GoodKind.Herbs, GoodKind.Potions, GoodKind.SlowPotions, GoodKind.Food }, priceMul = 1.5f, demandMul = 1.4f },
            new GuildEvent { title = "Лесной пожар на севере", text = "дерево, доски и смола сильно подорожали", goods = new[] { GoodKind.Wood, GoodKind.Planks, GoodKind.Resin, GoodKind.Tar }, priceMul = 1.6f, demandMul = 1.6f },
            new GuildEvent { title = "Корабельные верфи", text = "смола, пропитка и резина нарасхват", goods = new[] { GoodKind.Resin, GoodKind.Tar, GoodKind.Rubber, GoodKind.Beams }, priceMul = 1.5f, demandMul = 1.6f },
            new GuildEvent { title = "Распущенный гарнизон", text = "наёмников и оружия много — подешевели", itemCats = new[] { 3, 5 }, priceMul = 0.7f, demandMul = 0.6f },
            new GuildEvent { title = "Золотая лихорадка", text = "золота слишком много — оно подешевело", goods = new[] { GoodKind.Gold }, priceMul = 0.6f, demandMul = 0.6f },
            new GuildEvent { title = "Затоваривание", text = "склады купцов полны: дерево, камень и еда дешевеют", goods = new[] { GoodKind.Wood, GoodKind.Stone, GoodKind.Food }, priceMul = 0.7f, demandMul = 0.6f },
            new GuildEvent { title = "Большая ярмарка", text = "купцы съехались: спрос выше, гильдия не берёт комиссию", allGoods = true, itemCats = new[] { 3, 4, 5 }, priceMul = 1.05f, demandMul = 1.4f, noFee = true },
        };
    }

    public GuildEvent ActiveEvent => ev;
    public float EventLeft => ev != null ? Mathf.Max(0f, ev.until - Time.time) : 0f;
    float EventPriceMul(GoodKind k) { return ev != null && ev.Has(k) ? ev.priceMul : 1f; }
    float EventDemandMul(GoodKind k) { return ev != null && ev.Has(k) ? ev.demandMul : 1f; }
    float EventItemMul(int cat) { return ev != null && ev.HasCat(cat) ? ev.priceMul : 1f; }
    float EventItemDemand(int cat) { return ev != null && ev.HasCat(cat) ? ev.demandMul : 1f; }
    public bool EventTouches(GoodKind k) { return ev != null && ev.Has(k); }
    public bool EventTouchesCat(int cat) { return ev != null && ev.HasCat(cat); }

    void TickEvent()
    {
        if (ev != null && Time.time >= ev.until)
        {
            AddNews($"Закончилось: «{ev.title}». Цены возвращаются");
            ev = null;
            nextEventAt = Time.time + Random.Range(300f, 480f);
        }
        if (ev == null && Time.time >= nextEventAt)
        {
            GuildEvent[] pool = EventPool();
            ev = pool[Random.Range(0, pool.Length)];
            ev.until = Time.time + Random.Range(180f, 300f);
            AddNews($"<color=#FFD060><b>{ev.title}</b></color>: {ev.text}");
            WarManager.Notify($"Рынок: {ev.title} — {ev.text}", true);
        }
    }

    // ---------- Лавки игрока ----------

    public class Listing
    {
        public int id;
        public GoodId good;
        public MarketItem item;    // штучный товар (тогда лот один)
        public int price;          // за лот
        public int incoming;       // лотов несут на рынок
        public int shelf;          // на витрине
        public int awaiting;       // проданы, ждут, пока купец заберёт
        public int returning;      // сняты, несут обратно в город
        public int sold, earned;
        public bool cancelled;     // снят с продажи
        public bool voided;        // Торговый дом исчез - всё вернули
        public bool IsItem => item != null;
        public bool Empty => incoming <= 0 && shelf <= 0 && awaiting <= 0 && returning <= 0;
    }

    public string LotName(Listing l) { return l.IsItem ? l.item.Name : MarketGoods.PackName(l.good); }
    public int MarketPriceOf(Listing l) { return l.IsItem ? ItemPrice(l.item) : Price(l.good); }

    public string BuySlotProblem()
    {
        if (!Open) return "Торговый дом закрыт";
        if (SlotsOwned >= MaxSlots) return SlotsOwned >= SlotCosts.Length ? "Все лавки куплены" : "Больше лавок — улучшите Торговый дом";
        if (Marks < NextSlotCost) return $"Нужно {NextSlotCost} {Sym}";
        return "";
    }

    public void BuySlot()
    {
        if (BuySlotProblem() != "") return;
        int cost = NextSlotCost;
        RM.SpendMarks(cost);
        TotalSpent += cost;
        SlotsOwned++;
        AddNews($"Куплена лавка №{SlotsOwned} за {cost} {Sym}");
    }

    public string ListProblem(GoodId g, int packs)
    {
        if (!Open) return "Торговый дом закрыт";
        if (FreeSlots <= 0) return "Нет свободной лавки";
        if (packs <= 0) return "Выберите количество";
        if (packs > MaxPacks) return $"В лавку влезает до {MaxPacks} лотов";
        if (MarketGoods.HavePacks(g) < packs) return "Столько товара нет";
        return "";
    }

    public bool CreateListing(GoodId g, int packs, int price)
    {
        if (ListProblem(g, packs) != "") return false;
        int units = packs * MarketGoods.Pack(g);
        if (!MarketGoods.Take(g, units, out BuildingInstance source)) return false;

        Listing l = new Listing { id = nextId++, good = g, price = Mathf.Max(1, price), incoming = packs };
        Listings.Add(l);
        AddNews($"Выставлено: {packs} × {MarketGoods.PackName(g)} по {l.price} {Sym}. Товар несут в Торговый дом");

        BuildingInstance market = House.Building;
        Logistics.Request(new HaulJob
        {
            from = source,
            to = market,
            prefer = market,
            onDeliver = () =>
            {
                l.incoming -= packs;
                if (l.voided) MarketGoods.Give(l.good, units);
                else if (l.cancelled) ReturnGoods(l, packs);
                else l.shelf += packs;
                Cleanup(l);
            },
            onAbort = loaded =>
            {
                l.incoming -= packs;
                MarketGoods.Give(l.good, units);   // товар не теряем
                Cleanup(l);
            }
        });
        return true;
    }

    public string ListItemProblem(MarketItem it)
    {
        if (!Open) return "Торговый дом закрыт";
        if (FreeSlots <= 0) return "Нет свободной лавки";
        if (it == null) return "Выберите вещь";
        return it.SellProblem();
    }

    /// <summary>Выставить штучную вещь: оружие несут из Оружейной, модуль - со склада, боец уходит в лагерь наёмников.</summary>
    public bool CreateItemListing(MarketItem it, int price)
    {
        if (ListItemProblem(it) != "") return false;
        string name = it.Name;
        if (!it.TakeFromTown(out BuildingInstance source)) return false;

        Listing l = new Listing { id = nextId++, item = it, price = Mathf.Max(1, price) };
        Listings.Add(l);

        if (it.kind == ItemKind.Soldier || source == null)
        {
            l.shelf = 1;
            AddNews(it.kind == ItemKind.Soldier
                ? $"{name} ушёл в лагерь наёмников гильдии — выставлен за {l.price} {Sym}"
                : $"Выставлено: {name} за {l.price} {Sym}");
            return true;
        }

        l.incoming = 1;
        AddNews($"Выставлено: {name} за {l.price} {Sym}. Несут в Торговый дом");
        BuildingInstance market = House.Building;
        Logistics.Request(new HaulJob
        {
            from = source,
            to = market,
            prefer = market,
            onDeliver = () =>
            {
                l.incoming = 0;
                if (l.voided) ReturnItemNow(l.item);
                else if (l.cancelled) ReturnGoods(l, 1);
                else l.shelf = 1;
                Cleanup(l);
            },
            onAbort = loaded =>
            {
                l.incoming = 0;
                ReturnItemNow(l.item);
                Cleanup(l);
            }
        });
        return true;
    }

    // Вещь сразу возвращается в город (без переноски)
    void ReturnItemNow(MarketItem it)
    {
        if (it == null) return;
        if (it.kind == ItemKind.Soldier)
        {
            // Боец возвращается из лагеря наёмников на повозке
            MerchantCart.Send(Merchant.Guild, Random.Range(10f, 16f), false, "привезти бойца из лагеря наёмников",
                () => true, at => it.Receive(at, null), () => it.Receive(null, null));
            return;
        }
        it.Receive(null, null);
    }

    public void ChangePrice(Listing l, int delta)
    {
        if (l == null || l.cancelled) return;
        l.price = Mathf.Clamp(l.price + delta, 1, 9999);
    }

    /// <summary>Снять с продажи: что на витрине - возвращают в город. Проданное купец всё равно заберёт.</summary>
    public void Cancel(Listing l)
    {
        if (l == null || l.cancelled) return;
        l.cancelled = true;
        if (l.shelf > 0)
        {
            int n = l.shelf;
            l.shelf = 0;
            ReturnGoods(l, n);
        }
        Cleanup(l);
    }

    // Лоты лежат в Торговом доме - житель несёт их обратно (сырьё - в Ратушу, оружие - в Оружейную...)
    void ReturnGoods(Listing l, int packs)
    {
        if (packs <= 0) return;

        if (l.IsItem)
        {
            l.returning += 1;
            MarketItem it = l.item;
            if (it.kind == ItemKind.Soldier)
            {
                AddNews($"{it.Name} возвращается из лагеря наёмников");
                MerchantCart.Send(Merchant.Guild, Random.Range(10f, 16f), false, "привезти бойца из лагеря наёмников",
                    () => true,
                    at => it.Receive(at, () => { l.returning -= 1; Cleanup(l); }),
                    () => it.Receive(null, () => { l.returning -= 1; Cleanup(l); }));
                return;
            }
            it.Receive(Open ? House.Building : null, () => { l.returning -= 1; Cleanup(l); });
            return;
        }

        int units = packs * MarketGoods.Pack(l.good);
        BuildingInstance hall = Hall();
        if (!Open || hall == null)
        {
            MarketGoods.Give(l.good, units);
            return;
        }
        l.returning += packs;
        Logistics.Request(new HaulJob
        {
            from = House.Building,
            to = hall,
            prefer = House.Building,
            onDeliver = () => { l.returning -= packs; MarketGoods.Give(l.good, units); Cleanup(l); },
            onAbort = loaded => { l.returning -= packs; MarketGoods.Give(l.good, units); Cleanup(l); }
        });
    }

    void Cleanup(Listing l)
    {
        if (l.Empty && Listings.Contains(l))
        {
            Listings.Remove(l);
            if (!l.voided && l.sold > 0)
                AddNews($"Лавка освободилась: продано {(l.IsItem ? l.item.Name : l.sold + " × " + MarketGoods.PackName(l.good))}, выручка {l.earned} {Sym}");
        }
    }

    void TickSales()
    {
        if (!Open || WarManager.IsWar) return;
        foreach (var l in Listings.ToArray())
        {
            if (l.cancelled || l.voided || l.shelf <= 0) continue;
            float ratio = l.price / (float)MarketPriceOf(l);
            float chance = BuyChance(ratio);
            if (chance <= 0f) continue;
            float demand = l.IsItem
                ? ItemDemand[l.item.Category - 3] * EventItemDemand(l.item.Category)
                : MarketGoods.Of(l.good.kind).demand * EventDemandMul(l.good.kind);
            float interval = SaleInterval / Mathf.Max(0.05f, demand);
            if (Random.value < SaleTick / interval * chance) BotBuys(l);
        }
    }

    // Купец купил лоты из лавки: присылает обоз, марки - когда заберёт товар
    void BotBuys(Listing l)
    {
        int n = l.IsItem ? 1 : Random.Range(1, Mathf.Min(l.shelf, 4) + 1);
        l.shelf -= n;
        l.awaiting += n;
        int price = l.price;
        string lot = l.IsItem ? l.item.Name : $"{n} × {MarketGoods.PackName(l.good)}";
        Merchant who = Merchant.Random();

        MerchantCart.Send(who, ArrivalDelay, true, "забрать " + lot,
            () => !l.voided,
            market =>
            {
                l.awaiting -= n;
                l.sold += n;
                int gross = n * price;
                int fee = Fee(gross);
                int net = gross - fee;
                RM.AddMarks(net);
                l.earned += net;
                TotalEarned += net;
                FeesPaid += fee;
                if (l.IsItem) ShiftItems(l.item.Category, -0.02f);
                else Shift(l.good.kind, -0.012f * n);   // предложение растёт - цена падает
                string feeText = fee > 0 ? $" (комиссия {fee})" : "";
                AddNews($"{who.Name} забрал {lot}: <color=#80FF80>+{net} {Sym}</color>{feeText}");
                WarManager.Notify($"Рынок: продано {lot} — +{net} {Sym}", true);
                Cleanup(l);
            },
            () =>
            {
                // Купец не смог забрать (Торговый дом закрыт) - сделка сорвалась, лоты снова на витрине
                l.awaiting -= n;
                if (!l.voided)
                {
                    if (l.cancelled) ReturnGoods(l, n);
                    else l.shelf += n;
                }
                Cleanup(l);
            });

        AddNews($"{who.Name} купил {lot} за {n * price} {Sym} — едет за товаром");
    }

    // ---------- Предложения купцов ----------

    public class Offer
    {
        public int id;
        public GoodId good;
        public MarketItem item;    // штучный товар (лот один)
        public int packs;
        public int price;          // за лот
        public Merchant seller;
        public bool bargain;       // дешевле рынка
        public float expires;
        public bool IsItem => item != null;
        public int Category => item != null ? item.Category : MarketGoods.Category(good.kind);
    }

    int TargetOffers => 9 + Lvl * 3;

    void TickOffers()
    {
        Offers.RemoveAll(o => o.packs <= 0 || Time.time >= o.expires || (!o.IsItem && !MarketGoods.Visible(o.good.kind)));

        int goods = 0;
        int[] items = new int[3];
        foreach (var o in Offers)
        {
            if (o.IsItem) items[o.item.Category - 3]++;
            else goods++;
        }

        int guard = 0;
        while (goods < TargetOffers && guard++ < 40) { Offers.Add(NewOffer()); goods++; }

        for (int c = 0; c < 3; c++)
        {
            int want = ItemOffersByCat[c] + Lvl;
            int tries = 0;
            while (items[c] < want && tries++ < 10)
            {
                Offer o = NewItemOffer(c + 3);
                if (o == null) break;
                Offers.Add(o);
                items[c]++;
            }
        }
    }

    Offer NewOffer()
    {
        List<GoodId> goods = MarketGoods.AllVisible();
        // Хорошего и наилучшего качества на рынке меньше
        GoodId g;
        int tries = 0;
        do
        {
            g = goods[Random.Range(0, goods.Count)];
            tries++;
        } while (tries < 10 && ((g.tier == QualityTier.Good && Random.value < 0.5f) || (g.tier == QualityTier.Best && Random.value < 0.8f)));

        int basePrice = Price(g);
        bool bargain = Random.value < 0.12f;
        float mul = bargain ? Random.Range(0.72f, 0.88f) : Random.Range(1.0f, 1.3f);
        int packs = MarketGoods.Pack(g) >= 10 ? Random.Range(4, 21) : Random.Range(2, 11);
        return new Offer
        {
            id = nextId++,
            good = g,
            packs = packs,
            price = Mathf.Max(1, Mathf.RoundToInt(basePrice * mul)),
            seller = Merchant.Random(),
            bargain = bargain,
            expires = Time.time + Random.Range(120f, 300f)
        };
    }

    Offer NewItemOffer(int category)
    {
        MarketItem it = MarketItem.RandomBotItem(category);
        if (it == null) return null;
        bool bargain = Random.value < 0.12f;
        float mul = bargain ? Random.Range(0.72f, 0.88f) : Random.Range(1.0f, 1.3f);
        return new Offer
        {
            id = nextId++,
            item = it,
            packs = 1,
            price = Mathf.Max(1, Mathf.RoundToInt(ItemPrice(it) * mul)),
            seller = Merchant.Random(),
            bargain = bargain,
            expires = Time.time + Random.Range(150f, 360f)
        };
    }

    public string BuyProblem(Offer o, int packs)
    {
        if (!Open) return "Торговый дом закрыт";
        if (o == null || packs <= 0) return "Выберите количество";
        if (packs > o.packs) return "У купца столько нет";
        if (o.IsItem)
        {
            string p = MarketItem.ReceiveProblem(o.item.kind, o.item.Space, pendingGear, pendingSpace, pendingPeople);
            if (p != "") return p;
        }
        if (Marks < packs * o.price) return $"Нужно {packs * o.price} {Sym}";
        return "";
    }

    public void Buy(Offer o, int packs)
    {
        if (BuyProblem(o, packs) != "") return;
        int cost = packs * o.price;
        RM.SpendMarks(cost);
        TotalSpent += cost;
        o.packs -= packs;

        if (o.IsItem) { BuyItem(o, cost); return; }

        Shift(o.good.kind, 0.008f * packs);   // спрос растёт - цена тоже
        GoodId g = o.good;
        int units = packs * MarketGoods.Pack(g);
        Delivery d = new Delivery { id = nextId++, label = MarketGoods.Colored(g, MarketGoods.UnitsText(g, units)), from = o.seller.Name, status = "купец в пути" };
        Deliveries.Add(d);
        AddNews($"Куплено у {o.seller.Name}: {packs} × {MarketGoods.PackName(g)} за {cost} {Sym}. Везут");

        MerchantCart.Send(o.seller, ArrivalDelay, false, "привезти " + MarketGoods.UnitsText(g, units),
            () => true,
            target =>
            {
                // Привезли в Торговый дом - житель несёт в Ратушу. Торгового дома нет - купец сгрузил прямо у Ратуши.
                BuildingInstance hall = Hall();
                if (House != null && target == House.Building && hall != null)
                {
                    d.status = "разгружено, несут в Ратушу";
                    Logistics.Request(new HaulJob
                    {
                        from = target,
                        to = hall,
                        prefer = target,
                        onDeliver = () => FinishDelivery(d, g, units),
                        onAbort = loaded => FinishDelivery(d, g, units)
                    });
                }
                else FinishDelivery(d, g, units);
            },
            () => FinishDelivery(d, g, units));
    }

    void BuyItem(Offer o, int cost)
    {
        MarketItem it = o.item;
        ShiftItems(it.Category, 0.02f);
        bool gear = it.kind == ItemKind.Weapon || it.kind == ItemKind.Shield;
        bool merc = it.kind == ItemKind.Soldier;
        if (gear) pendingGear++;
        if (merc) { pendingSpace += it.Space; pendingPeople++; }

        Delivery d = new Delivery { id = nextId++, label = "<b>" + it.Name + "</b>", from = o.seller.Name, status = "купец в пути" };
        Deliveries.Add(d);
        AddNews($"Куплено у {o.seller.Name}: {it.Name} за {cost} {Sym}. Везут");

        System.Action done = () =>
        {
            if (gear) pendingGear = Mathf.Max(0, pendingGear - 1);
            if (merc) { pendingSpace = Mathf.Max(0, pendingSpace - it.Space); pendingPeople = Mathf.Max(0, pendingPeople - 1); }
            Deliveries.Remove(d);
        };

        MerchantCart.Send(o.seller, ArrivalDelay, false, (merc ? "привезти наёмника: " : "привезти ") + it.Name,
            () => true,
            target =>
            {
                d.status = merc ? "наёмник идёт в казарму" : (it.kind == ItemKind.Module || it.kind == ItemKind.Trap ? "несут в Ратушу" : "несут в Оружейную");
                it.Receive(target, done);
            },
            () => it.Receive(null, done));
    }

    void FinishDelivery(Delivery d, GoodId g, int units)
    {
        if (!Deliveries.Remove(d)) return;
        MarketGoods.Give(g, units);
        WarManager.Notify($"Рынок: доставлено в Ратушу {MarketGoods.UnitsText(g, units)}", true);
    }

    public class Delivery
    {
        public int id;
        public string label;
        public string from, status;
    }

    /// <summary>Купленное оружие не влезло в Оружейные - ждёт, гильдия пробует положить раз в несколько секунд.</summary>
    public void HoldGear(MarketItem it)
    {
        if (it == null) return;
        heldGear.Add(it);
        WarManager.Notify($"{it.Name}: в Оружейных нет места — вещь ждёт в Торговом доме", false);
    }

    void TickHeldGear()
    {
        for (int i = heldGear.Count - 1; i >= 0; i--)
        {
            if (!heldGear[i].TryStash()) continue;
            WarManager.Notify($"В Оружейную принесли: {heldGear[i].Name}", true);
            heldGear.RemoveAt(i);
        }
    }

    // ---------- Заказы гильдии ----------

    public class Contract
    {
        public int id;
        public GoodId good;
        public int packs;
        public int reward;
        public float deadline;
        public int state;          // 0 - ждёт исполнителя, 1 - товар несут в Торговый дом, 2 - ждёт обоз гильдии
        public string client;
    }

    static readonly string[] Clients =
    {
        "Гарнизон Северной заставы", "Королевские верфи", "Монастырь Святого Ключа", "Гильдия каменщиков",
        "Двор наместника", "Караван на восток", "Городская управа Вельска", "Орден Серого Щита"
    };

    void TickContracts()
    {
        // Невзятые заказы с истёкшим сроком уходят другим
        foreach (var c in Contracts.ToArray())
        {
            if (c.state == 0 && (Time.time >= c.deadline || !MarketGoods.Visible(c.good.kind)))
            {
                Contracts.Remove(c);
                AddNews($"Заказ «{c.client}» ушёл другому городу");
            }
        }
        if (!Open) return;
        if (Contracts.Count < MaxContracts && Random.value < 0.5f) Contracts.Add(NewContract());
    }

    Contract NewContract()
    {
        List<GoodId> goods = MarketGoods.AllVisible();
        goods.RemoveAll(x => x.kind == GoodKind.Gold);   // золотом гильдия не берёт
        GoodId g = goods[Random.Range(0, goods.Count)];
        if (MarketGoods.IsRefined(g.kind))
        {
            float r = Random.value;
            g = new GoodId(g.kind, r < 0.6f ? QualityTier.Medium : (r < 0.9f ? QualityTier.Good : QualityTier.Best));
        }

        BuildingInstance hall = Hall();
        int hallLvl = hall != null ? hall.level : 1;
        int packs = Mathf.RoundToInt(Random.Range(4, 10) * (1f + (hallLvl - 1) * 0.5f));
        if (MarketGoods.Pack(g) == 1) packs = Mathf.Max(2, packs / 2);

        float mul = Random.Range(1.35f, 1.65f) * (1f + 0.1f * Rank);
        return new Contract
        {
            id = nextId++,
            good = g,
            packs = packs,
            reward = Mathf.CeilToInt(packs * Price(g) * mul),
            deadline = Time.time + Random.Range(420f, 780f),
            client = Clients[Random.Range(0, Clients.Length)]
        };
    }

    public string FulfillProblem(Contract c)
    {
        if (!Open) return "Торговый дом закрыт";
        if (c.state != 0) return "Уже выполняется";
        if (MarketGoods.HavePacks(c.good) < c.packs) return $"Нужно {MarketGoods.UnitsText(c.good, c.packs * MarketGoods.Pack(c.good))}";
        return "";
    }

    public void Fulfill(Contract c)
    {
        if (FulfillProblem(c) != "") return;
        int units = c.packs * MarketGoods.Pack(c.good);
        if (!MarketGoods.Take(c.good, units, out BuildingInstance source)) return;
        c.state = 1;
        AddNews($"Взят заказ «{c.client}»: {MarketGoods.UnitsText(c.good, units)} несут в Торговый дом");

        BuildingInstance market = House.Building;
        Logistics.Request(new HaulJob
        {
            from = source,
            to = market,
            prefer = market,
            onDeliver = () =>
            {
                if (c.state != 1) return;
                if (!Open) { ContractBack(c, units); return; }
                c.state = 2;
                SendGuildCart(c, units);
            },
            onAbort = loaded => ContractBack(c, units)
        });
    }

    // Не вышло: товар обратно в город, заказ снова ждёт (если срок не вышел)
    void ContractBack(Contract c, int units)
    {
        MarketGoods.Give(c.good, units);
        c.state = 0;
        c.deadline = Mathf.Max(c.deadline, Time.time + 60f);
    }

    void SendGuildCart(Contract c, int units)
    {
        MerchantCart.Send(Merchant.Guild, Random.Range(8f, 14f), true, $"забрать заказ «{c.client}»",
            () => c.state == 2,
            market =>
            {
                Contracts.Remove(c);
                int oldRank = Rank;
                ContractsDone++;
                RM.AddMarks(c.reward);
                TotalEarned += c.reward;
                AddNews($"Заказ «{c.client}» выполнен: <color=#80FF80>+{c.reward} {Sym}</color>");
                WarManager.Notify($"Гильдия забрала заказ «{c.client}» и заплатила {c.reward} {Sym}", true);
                if (Rank > oldRank)
                {
                    AddNews($"<color=#FFD060>Репутация в гильдии выросла: ранг {Rank}</color> — комиссия ниже, заказы дороже");
                    WarManager.Notify($"Репутация в гильдии: ранг {Rank}! Комиссия ниже, заказы дороже", true);
                }
            },
            () => { if (c.state == 2) ContractBack(c, units); });
    }

    // ---------- Торговый дом исчез ----------

    /// <summary>Торговый дом снесли: всё, что лежало в нём, возвращается в город, лавки закрываются.</summary>
    public void MarketGone()
    {
        foreach (var l in Listings)
        {
            int back = l.shelf + l.awaiting;
            if (back > 0)
            {
                if (l.IsItem) ReturnItemNow(l.item);
                else MarketGoods.Give(l.good, back * MarketGoods.Pack(l.good));
            }
            l.shelf = l.awaiting = 0;
            l.voided = true;
            // то, что несут туда / обратно, само вернётся через onAbort
        }
        Listings.RemoveAll(l => l.Empty);

        foreach (var c in Contracts)
            if (c.state == 2) ContractBack(c, c.packs * MarketGoods.Pack(c.good));

        if (RM != null) AddNews("Торговый дом снесён: товары из лавок вернули в город");
    }

    // ---------- Цикл ----------

    void Update()
    {
        float dt = Time.deltaTime;

        driftTimer -= dt;
        if (driftTimer <= 0f) { driftTimer = 30f; Drift(); }

        saleTimer -= dt;
        if (saleTimer <= 0f) { saleTimer = SaleTick; TickSales(); }

        offerTimer -= dt;
        if (offerTimer <= 0f) { offerTimer = 20f; TickOffers(); }

        contractTimer -= dt;
        if (contractTimer <= 0f) { contractTimer = 15f; TickContracts(); }

        gearTimer -= dt;
        if (gearTimer <= 0f) { gearTimer = 5f; if (heldGear.Count > 0) TickHeldGear(); }

        TickEvent();
    }
}

/// <summary>Купец-бот: имя, город и цвет повозки. Потом здесь будет живой игрок.</summary>
public class Merchant
{
    public string Name;
    public Color color;

    static readonly string[] Men = { "Ефим", "Гаврила", "Савва", "Прохор", "Фома", "Тихон", "Вавила", "Нестор", "Борислав", "Елисей", "Митрофан", "Игнат" };
    static readonly string[] Women = { "Агафья", "Марфа", "Ульяна", "Пелагея", "Василиса", "Евдокия" };
    static readonly string[] Towns = { "Вельска", "Заречья", "Каменного Брода", "Ольховки", "Северной Гавани", "Медвежьего Лога", "Солеварни", "Трёх Ключей", "Белой Пади" };

    public static Merchant Random()
    {
        bool woman = UnityEngine.Random.value < 0.3f;
        string n = woman ? Women[UnityEngine.Random.Range(0, Women.Length)] : Men[UnityEngine.Random.Range(0, Men.Length)];
        string t = Towns[UnityEngine.Random.Range(0, Towns.Length)];
        return new Merchant
        {
            Name = (woman ? "Купчиха " : "Купец ") + n + " из " + t,
            color = Color.HSVToRGB(UnityEngine.Random.value, 0.55f, 0.85f)
        };
    }

    public static Merchant Guild => new Merchant { Name = "Обоз гильдии", color = new Color(0.95f, 0.8f, 0.3f) };
}
