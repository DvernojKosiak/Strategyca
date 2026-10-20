using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Английские имена жителей: имя + фамилия из списков. Повторы возможны, но редки.
/// </summary>
public static class CitizenNames
{
    static readonly string[] First =
    {
        "Adam", "Alice", "Arthur", "Bella", "Ben", "Clara", "Daniel", "Edith", "Edward", "Eleanor",
        "Emma", "Felix", "Florence", "George", "Grace", "Harold", "Harriet", "Henry", "Isaac", "Ivy",
        "Jack", "James", "Jane", "John", "Kate", "Leo", "Lily", "Lucy", "Mabel", "Martin",
        "Mary", "Matilda", "Nora", "Oliver", "Oscar", "Peter", "Rose", "Ruth", "Samuel", "Sarah",
        "Simon", "Sophie", "Thomas", "Victor", "Walter", "William", "Agnes", "Hugh", "Roger", "Joan"
    };

    static readonly string[] Last =
    {
        "Archer", "Baker", "Bell", "Brewer", "Brooks", "Butler", "Carter", "Clark", "Cooper", "Fisher",
        "Fletcher", "Ford", "Gardner", "Green", "Hall", "Hill", "Hunter", "King", "Knight", "Lane",
        "Mason", "Miller", "Moore", "Parker", "Potter", "Reed", "Shepherd", "Smith", "Stone", "Taylor",
        "Thatcher", "Turner", "Walker", "Ward", "Webb", "Weaver", "Wells", "White", "Wood", "Wright"
    };

    static readonly HashSet<string> used = new HashSet<string>();

    public static string Next()
    {
        // Несколько попыток найти ещё не занятое сочетание
        string n = "";
        for (int i = 0; i < 8; i++)
        {
            n = First[Random.Range(0, First.Length)] + " " + Last[Random.Range(0, Last.Length)];
            if (used.Add(n)) return n;
        }
        return n;
    }
}

/// <summary>
/// Карточка жителя: открывается кликом по жителю на карте (BuildingPlacer).
/// Только просмотр: имя, кто он, где работает и живёт, здоровье, чем занят, что несёт.
/// Кнопка "Следить" - камера едет за жителем.
/// </summary>
public static class CitizenInfoPanel
{
    public static bool Follow;   // камера следует за выбранным жителем

    public static Rect Draw(CitizenAgent a, float x, float y, float w)
    {
        string carry = a.CarryText;
        string combat = a.CombatStatsText;
        string equip = a.EquipmentText;
        string cond = a.ConditionText;
        string stealth = a.StealthText;   // плащ из листвы / дозор
        if (!a.IsMilitary && a.Workplace != null)
            cond += "  " + UiKit.Col(UiKit.MutedHex, $"работает на {Mathf.RoundToInt(a.WorkFactor * 100f)}%");
        bool hasMore = combat != "" || equip != "";
        bool more = hasMore && UiKit.IsOpen("citizen-more");

        float h = UiKit.TitleH + 6f
                + UiKit.RowH                       // роль
                + 24f                              // здоровье (полоска)
                + KvH(w, a.ActivityText)
                + KvH(w, a.HomeText)
                + KvH(w, cond)
                + KvH(w, carry)
                + (stealth != "" ? KvH(w, stealth) : 0f)
                + (hasMore ? UiKit.FoldH + 2f : 0f)
                + (more && combat != "" ? KvH(w, combat) : 0f)
                + (more && equip != "" ? KvH(w, equip) : 0f)
                + 6f + UiKit.BtnH + 8f;            // кнопки
        Rect panel = new Rect(x, y, w, h);
        UiKit.Window(panel, a.CitizenName);
        float cy = y + UiKit.TitleH + 6f;

        GUI.Label(new Rect(x + 10f, cy, w - 20f, UiKit.RowH), a.RoleText, UiKit.MutedStyle);
        cy += UiKit.RowH;

        // Здоровье
        float frac = a.HpFraction;
        UiKit.Bar(new Rect(x + 10f, cy, w - 20f, 18f), frac, Color.Lerp(UiKit.Bad, UiKit.Good, frac),
            $"Здоровье {Mathf.CeilToInt(a.Hp)}/{Mathf.CeilToInt(a.MaxHp)}");
        cy += 24f;

        Kv(x, ref cy, w, "Сейчас", a.ActivityText);
        Kv(x, ref cy, w, "Живёт", a.HomeText);
        Kv(x, ref cy, w, "Состояние", cond);
        Kv(x, ref cy, w, "Несёт", carry);
        if (stealth != "") Kv(x, ref cy, w, "Скрытность", stealth);

        if (hasMore)
        {
            UiKit.Fold(new Rect(x + 10f, cy, w - 20f, UiKit.FoldH), "citizen-more", "Бой и снаряжение");
            cy += UiKit.FoldH + 2f;
            if (more && combat != "") Kv(x, ref cy, w, "В бою", combat);
            if (more && equip != "") Kv(x, ref cy, w, "Снаряж.", equip);
        }

        // Кнопки (только камера - менять в жителе ничего нельзя)
        cy += 6f;
        float bw = (w - 26f) / 2f;
        if (UiKit.Button(new Rect(x + 10f, cy, bw, UiKit.BtnH), "Показать", UiKit.Kind.Normal, "Навести камеру на жителя") && CameraController.Instance != null)
            CameraController.Instance.FocusOn(a.transform.position);
        if (UiKit.Button(new Rect(x + 16f + bw, cy, bw, UiKit.BtnH), Follow ? "Следить ✓" : "Следить",
                Follow ? UiKit.Kind.Primary : UiKit.Kind.Normal, "Камера едет за жителем"))
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

    // Жёлтая стрелка над выбранным жителем
    public static void DrawMarker(CitizenAgent a)
    {
        if (a == null || a.IsInsideBuilding) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 sp = cam.WorldToScreenPoint(a.transform.position + Vector3.up * 0.7f);
        if (sp.z < 0f) return;

        GUIStyle st = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        Rect rc = new Rect(sp.x - 15f, Screen.height - sp.y - 15f, 30f, 30f);
        st.normal.textColor = Color.black;
        GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), "▼", st);
        st.normal.textColor = new Color(0.4f, 1f, 1f);
        GUI.Label(rc, "▼", st);
    }
}