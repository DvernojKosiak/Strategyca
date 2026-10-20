using UnityEngine;

/// <summary>
/// Карточка врага: открывается кликом по врагу на карте (BuildingPlacer), как карточка жителя.
/// Только просмотр: имя, класс и номер набега, здоровье, чем занят, что несёт (добыча,
/// которая выпадет при гибели), оружие / щит / конь, боевые характеристики.
/// Кнопка "Следить" - камера едет за врагом.
/// </summary>
public static class EnemyInfoPanel
{
    public static bool Follow;   // камера следует за выбранным врагом

    public static Rect Draw(EnemyAgent e, float x, float y, float w)
    {
        string carry = e.CarryText;
        string equip = e.EquipmentText;
        string combat = e.CombatStatsText;
        bool hasMore = combat != "" || equip != "";
        bool more = hasMore && UiKit.IsOpen("enemy-more");

        float h = UiKit.TitleH + 6f
                + UiKit.RowH                       // класс
                + 24f                              // здоровье (полоска)
                + KvH(w, e.ActivityText)
                + KvH(w, carry)
                + (hasMore ? UiKit.FoldH + 2f : 0f)
                + (more && combat != "" ? KvH(w, combat) : 0f)
                + (more && equip != "" ? KvH(w, equip) : 0f)
                + 6f + UiKit.BtnH + 8f;            // кнопки
        Rect panel = new Rect(x, y, w, h);
        UiKit.Window(panel, UiKit.Col(UiKit.BadHex, e.Name));
        float cy = y + UiKit.TitleH + 6f;

        GUI.Label(new Rect(x + 10f, cy, w - 20f, UiKit.RowH), e.RoleText, UiKit.MutedStyle);
        cy += UiKit.RowH;

        // Здоровье
        float frac = e.HpFraction;
        UiKit.Bar(new Rect(x + 10f, cy, w - 20f, 18f), frac, UiKit.Bad,
            $"Здоровье {Mathf.CeilToInt(e.Hp)}/{Mathf.CeilToInt(e.MaxHp)}");
        cy += 24f;

        Kv(x, ref cy, w, "Сейчас", e.ActivityText);
        Rect carryR = new Rect(x + 10f, cy, w - 20f, KvH(w, carry));
        Kv(x, ref cy, w, "Несёт", carry);
        UiKit.Tip(carryR, "Эта добыча выпадет, когда враг погибнет.");

        if (hasMore)
        {
            UiKit.Fold(new Rect(x + 10f, cy, w - 20f, UiKit.FoldH), "enemy-more", "Бой и снаряжение");
            cy += UiKit.FoldH + 2f;
            if (more && combat != "") Kv(x, ref cy, w, "В бою", combat);
            if (more && equip != "") Kv(x, ref cy, w, "Снаряж.", equip);
        }

        // Кнопки (только камера - врагом управлять нельзя)
        cy += 6f;
        float bw = (w - 26f) / 2f;
        if (UiKit.Button(new Rect(x + 10f, cy, bw, UiKit.BtnH), "Показать", UiKit.Kind.Normal, "Навести камеру на врага") && CameraController.Instance != null)
            CameraController.Instance.FocusOn(e.Position);
        if (UiKit.Button(new Rect(x + 16f + bw, cy, bw, UiKit.BtnH), Follow ? "Следить ✓" : "Следить",
                Follow ? UiKit.Kind.Primary : UiKit.Kind.Normal, "Камера едет за врагом"))
            Follow = !Follow;

        return new Rect(x, y, w + 5f, h);
    }

    // Строка «ключ — значение»: значение переносится, высота по тексту
    static float KvH(float w, string value)
    {
        return Mathf.Max(UiKit.RowH, UiKit.TextH(value ?? "", w - 20f - KeyW)) + 2f;
    }

    static void Kv(float x, ref float cy, float w, string label, string value)
    {
        float h = KvH(w, value);
        GUI.Label(new Rect(x + 10f, cy, KeyW, UiKit.RowH), label, UiKit.MutedStyle);
        GUI.Label(new Rect(x + 10f + KeyW, cy, w - 20f - KeyW, h), value);
        cy += h;
    }

    const float KeyW = 78f;

    static int CountLines(string s)
    {
        int n = 0;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    // Красная стрелка над выбранным врагом
    public static void DrawMarker(EnemyAgent e)
    {
        if (!EnemyAgent.Alive(e)) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 sp = cam.WorldToScreenPoint(e.Position + Vector2.up * 0.8f);
        if (sp.z < 0f) return;

        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        Rect rc = new Rect(sp.x - 15f, Screen.height - sp.y - 15f, 30f, 30f);
        st.normal.textColor = Color.black;
        GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), "▼", st);
        st.normal.textColor = new Color(1f, 0.35f, 0.35f);
        GUI.Label(rc, "▼", st);
    }
}
