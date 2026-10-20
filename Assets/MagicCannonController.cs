using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Магическая пушка. Стреляет навесом (через стены) магическим ядром по области.
///  - Выбирает цель там, где больше всего врагов рядом (по площади взрыва).
///  - Ядро летит сверху дугой; урон наносится в момент падения всем врагам в радиусе взрыва
///    (в центре 100%, к краю падает до EdgeDamage).
///  - Урон зависит от маны дня (ManaManager.Factor) на момент выстрела.
///  - Ближе MinRange не стреляет (мёртвая зона). Разрушенная пушка молчит. Рабочие не нужны.
/// Три уровня: растут урон, радиус взрыва, дальность, быстрее перезарядка.
/// Добавляется на здание "Магическая пушка" в BuildingPlacer.
/// </summary>
public class MagicCannonController : MonoBehaviour
{
    public const string TypeName = "Магическая пушка";

    // Параметры по уровням (индекс = уровень - 1)
    static readonly float[] DamageByLevel   = { 45f, 70f, 100f };
    static readonly float[] RadiusByLevel   = { 2.0f, 2.4f, 2.8f };
    static readonly float[] RangeByLevel    = { 11f, 13f, 15f };
    static readonly float[] CooldownByLevel = { 3.5f, 3.0f, 2.5f };

    public const float MinRange = 2.5f;      // мёртвая зона у самой пушки
    public const float EdgeDamage = 0.5f;    // на краю взрыва проходит 50% урона
    const float ShellSpeed = 12f;            // клеток в секунду (для времени полёта)

    BuildingInstance bld;
    float timer;

    public int ShotsFired { get; private set; }
    public int HitsTotal { get; private set; }

    int Lvl => bld != null ? Mathf.Clamp(bld.level, 1, 3) - 1 : 0;
    public float BaseDamage => DamageByLevel[Lvl];
    public float Radius => RadiusByLevel[Lvl];
    public float Range => RangeByLevel[Lvl];
    public float Cooldown => CooldownByLevel[Lvl];

    void Update()
    {
        if (bld == null) bld = GetComponent<BuildingInstance>();
        UpdateRangeCircle();
        if (bld == null || bld.IsRuined || !bld.InGreenZone) return;   // вне зелёной зоны не стреляет

        timer -= Time.deltaTime;
        if (timer > 0f) return;

        Vector2 c = bld.Center;
        EnemyAgent target = PickTarget(c);
        if (target == null) { timer = 0.3f; return; }

        timer = Cooldown;
        Fire(c, target.Position);
    }

    // Цель - враг, рядом с которым больше всего других врагов (чтобы взрыв задел больше); при равенстве - ближний
    EnemyAgent PickTarget(Vector2 c)
    {
        EnemyAgent best = null;
        float bestScore = float.MinValue;
        float r = Radius;

        foreach (var e in EnemyAgent.All)
        {
            if (!EnemyAgent.Alive(e)) continue;
            float d = Vector2.Distance(c, e.Position);
            if (d > Range || d < MinRange) continue;

            int near = 0;
            foreach (var o in EnemyAgent.All)
                if (EnemyAgent.Alive(o) && Vector2.Distance(o.Position, e.Position) <= r) near++;

            float score = near * 10f - d * 0.1f;
            if (score > bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    void Fire(Vector2 from, Vector2 to)
    {
        float dmg = BaseDamage * ManaManager.Factor;   // мана на момент выстрела
        float radius = Radius;
        float dur = Mathf.Clamp(Vector2.Distance(from, to) / ShellSpeed, 0.45f, 1.2f);
        ShotsFired++;
        CombatFx.Shell(from, to, dur, radius, () => Explode(to, radius, dmg));
    }

    // Ядро упало: урон всем врагам в радиусе, к краю слабее
    void Explode(Vector2 at, float radius, float dmg)
    {
        var victims = new List<EnemyAgent>(EnemyAgent.All);
        foreach (var e in victims)
        {
            if (!EnemyAgent.Alive(e)) continue;
            float d = Vector2.Distance(at, e.Position);
            if (d > radius) continue;
            float k = Mathf.Lerp(1f, EdgeDamage, d / radius);
            HitsTotal++;
            e.TakeDamage(dmg * k, null, bld);
        }
    }

    // ---------- Круг дальности (виден, пока здание выбрано) ----------

    LineRenderer circle;
    int shownFrame = -10;

    public void ShowRadiusThisFrame() { shownFrame = Time.frameCount; }

    void UpdateRangeCircle()
    {
        bool show = Time.frameCount - shownFrame <= 2 && bld != null && !bld.IsRuined;
        if (!show) { if (circle != null) circle.enabled = false; return; }

        if (circle == null)
        {
            GameObject go = new GameObject("CannonRange");
            circle = go.AddComponent<LineRenderer>();
            circle.useWorldSpace = true;
            circle.loop = true;
            circle.material = new Material(Shader.Find("Sprites/Default"));
            circle.widthMultiplier = 0.08f;
            circle.startColor = circle.endColor = new Color(0.8f, 0.5f, 1f, 0.5f);
            circle.sortingOrder = -40;
            circle.positionCount = 72;
        }

        circle.enabled = true;
        Vector2 c = bld.Center;
        float r = Range;
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72;
            circle.SetPosition(i, new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f));
        }
    }

    void OnDestroy()
    {
        if (circle != null) Destroy(circle.gameObject);
    }

    // ---------- Панель здания ----------

    public Rect DrawPanel(float x, float y, float w)
    {
        ShowRadiusThisFrame();
        if (bld == null) bld = GetComponent<BuildingInstance>();
        BuildingInstance b = bld;

        GUIStyle st = UiKit.Body;
        GUIStyle small = UiKit.Small;

        float stateY = UiKit.TitleH + 6f;
        float dmgY = stateY + 22f;
        float statsY = dmgY + 20f;
        float upgradeY = statsY + 26f;
        float h = upgradeY + UpgradeRules.SectionHeight(b) + 8f;

        UiKit.Window(new Rect(x, y, w, h), $"{TypeName} · Ур. {b.level}");

        int manaPct = ManaManager.Instance != null ? ManaManager.Instance.Percent : 100;
        float now = BaseDamage * ManaManager.Factor;
        string state = b.IsRuined ? UiKit.Col(UiKit.BadHex, "Разрушена")
                     : (!b.InGreenZone ? UiKit.Col(UiKit.BadHex, "Вне зелёной зоны - не стреляет") : UiKit.Col(UiKit.GoodHex, "Готова к бою"));
        GUI.Label(new Rect(x + 10f, y + stateY, w - 20f, 20f), state, st);

        string nowCol = now < BaseDamage ? UiKit.WarnHex : UiKit.GoodHex;
        Rect dr = new Rect(x + 10f, y + dmgY, w - 20f, 18f);
        GUI.Label(dr, $"Урон сейчас: <color={nowCol}><b>{now:F0}</b></color>  " + UiKit.Col(UiKit.MutedHex, $"из {BaseDamage:F0} · мана {manaPct}%"), small);
        UiKit.Tip(dr, "Урон зависит от маны дня в момент выстрела.");

        Rect sr = new Rect(x + 10f, y + statsY, w - 20f, 18f);
        GUI.Label(sr, $"Взрыв {Radius:F1} кл. · дальность {Range:F0} кл. · {Cooldown:F1} с", small);
        UiKit.Tip(sr,
            $"Бьёт навесом через стены по месту, где больше всего врагов. Урон слабеет к краю взрыва (до {EdgeDamage * 100f:F0}%).\n" +
            $"Ближе {MinRange:F1} кл. не стреляет. Выстрелов: {ShotsFired}, попаданий: {HitsTotal}.");

        UpgradeRules.DrawUpgradeSection(x + 10f, y + upgradeY, w - 20f, b);
        return new Rect(x, y, w + 5f, h);
    }
}
