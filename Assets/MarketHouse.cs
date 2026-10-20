using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Торговый дом - вход на рынок Торговой гильдии (см. TradeGuild). Можно построить только один.
///  - Клик по зданию открывает окно рынка (MarketWindow): покупки у купцов, свои лавки, заказы гильдии, цены.
///  - Приказчики (рабочие здания) носят товары между Ратушей / Хранилищами и Торговым домом.
///    Сюда же приезжают обозы купцов: забирают проданное и привозят купленное.
///  - Уровень: больше лавок, больше лотов в лавке, ниже комиссия, больше заказов, купцы приезжают быстрее.
///  - Руины / вне зелёной зоны - рынок закрыт: купцы ждут за краем карты. Снесли - товары из лавок вернутся в Ратушу.
/// За первый Торговый дом гильдия даёт подъёмные (TradeGuild.StartBonus марок).
/// Добавляется на здание в BuildingPlacer.
/// </summary>
public class MarketHouse : MonoBehaviour
{
    public const string TypeName = "Торговый дом";

    public static readonly List<MarketHouse> All = new List<MarketHouse>();

    /// <summary>Действующий Торговый дом (он один).</summary>
    public static MarketHouse Main
    {
        get
        {
            foreach (var m in All) if (m != null && m.isActiveAndEnabled && m.Building != null) return m;
            return null;
        }
    }

    public BuildingInstance Building { get; private set; }
    public bool Works => Building != null && Building.isActiveAndEnabled && !Building.IsRuined && Building.InGreenZone;

    void Awake() { Building = GetComponent<BuildingInstance>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Start()
    {
        TradeGuild.Ensure().GiveStartBonus();
    }

    void OnDestroy()
    {
        All.Remove(this);
        if (Main == null)
        {
            if (TradeGuild.Instance != null) TradeGuild.Instance.MarketGone();
            MarketWindow.Close();
        }
    }

    public Rect DrawPanel(float x, float y, float w)
    {
        BuildingInstance b = Building;
        TradeGuild g = TradeGuild.Ensure();
        float cx = x + UiKit.Pad, cw = w - UiKit.Pad * 2f;

        // Состояние рынка: главное - одной строкой
        string state = !Works ? UiKit.Col(UiKit.BadHex, "Рынок закрыт — купцы ждут за краем карты")
                     : (WarManager.IsWar ? UiKit.Col(UiKit.BadHex, "Война — купцы в город не заходят") : UiKit.Col(UiKit.GoodHex, "Рынок открыт"));
        string status = b.isWorking ? "" : b.StatusText;
        float statusH = status != "" ? UiKit.TextH(status, cw, UiKit.Small) + 4f : 0f;

        int shelf = 0;
        foreach (var l in g.Listings) shelf += l.shelf;
        string news = g.News.Count > 0 ? g.News[0] : "Новостей нет";

        float stateY = UiKit.TitleH + 6f;
        float rowsY = stateY + UiKit.RowH + statusH;
        float newsH = UiKit.TextH(news, cw, UiKit.MutedStyle) + 2f;
        float btnY = rowsY + UiKit.RowH * 3f + newsH + 4f;
        float workersY = btnY + 38f + 6f;
        float upgradeY = workersY + 30f;
        float h = upgradeY + UiKit.SectionH + UpgradeRules.SectionHeight(b) + 8f;
        UiKit.Window(new Rect(x, y, w, h), $"{TypeName}  {UiKit.Col(UiKit.MutedHex, "· Ур. " + b.level)}");

        GUI.Label(new Rect(cx, y + stateY, cw, UiKit.RowH), state);
        if (status != "") GUI.Label(new Rect(cx, y + stateY + UiKit.RowH, cw, statusH), UiKit.Col(UiKit.BadHex, status), UiKit.Small);

        float cy = y + rowsY;
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Счёт в гильдии", UiKit.Col(UiKit.GoldHex, $"{TradeGuild.Sym} {TradeGuild.Marks}") + UiKit.Col(UiKit.MutedHex, $"  · ранг {g.Rank}"));
        cy += UiKit.RowH;
        Rect lr = new Rect(cx, cy, cw, UiKit.RowH);
        UiKit.Row(lr, "Лавки · лоты", $"{g.Listings.Count}/{g.SlotsOwned} · {shelf}");
        UiKit.Tip(lr, $"Сейчас: до {g.MaxSlots} лавок, по {g.MaxPacks} лотов, комиссия {g.FeeRate * 100f:F0}%.\nУлучшение — больше лавок и заказов, ниже комиссия, купцы приезжают быстрее.");
        cy += UiKit.RowH;
        UiKit.Row(new Rect(cx, cy, cw, UiKit.RowH), "Обозы · заказы", $"{MerchantCart.All.Count} · {g.Contracts.Count}");
        cy += UiKit.RowH;
        GUI.Label(new Rect(cx, cy, cw, newsH), news, UiKit.MutedStyle);

        GUIStyle big = new GUIStyle(UiKit.ButtonStyle(UiKit.Kind.Primary)) { fontSize = 15, fontStyle = FontStyle.Bold };
        Rect mb = new Rect(cx, y + btnY, cw, 38f);
        if (GUI.Button(mb, MarketWindow.IsOpen ? "Рынок открыт ▸" : "Открыть рынок", big)) MarketWindow.Open();
        UiKit.Tip(mb, "Покупки у купцов, свои лавки, заказы гильдии и цены");

        if (PopulationManager.Instance != null) PopulationManager.Instance.DrawWorkersRow(cx, y + workersY, b);

        float uy = UiKit.Section(cx, y + upgradeY, cw, "Улучшение");
        UpgradeRules.DrawUpgradeSection(cx, uy, cw, b);
        return new Rect(x, y, w + 5f, h);
    }
}
