using UnityEngine;

/// <summary>Что надето на тело бойца: броня (ветка «Тяжёлая стратегия») или плащ из листвы («Мастер маскировки»).</summary>
public enum BodyKind { Armor, Cloak }

/// <summary>
/// Вещь «на тело» бойца. Делается в Оружейной, выдаётся бойцам из казармы (слот один: броня ИЛИ плащ).
///  - Броня (сталь + доски): режет физический урон (по качеству 25 / 30 / 36%), но боец на 10% медленнее.
///    Каждый поглощённый удар изнашивает её на 1.
///  - Плащ из листвы (листва + немного золота): враги замечают бойца вдвое ближе, пока он сам не бьёт
///    (после удара он виден ещё RevealSeconds). Разведчик в плаще виден только вплотную (NoticeScout клетки).
///    Плащ рвётся от ударов по бойцу.
/// Магический урон броня не берёт. Боец погиб - вещь погибла. Уволили - вещь возвращается в Оружейную.
/// Все числа - в таблицах, их можно менять.
/// </summary>
public class BodyGear
{
    public readonly BodyKind kind;
    public readonly QualityTier quality;
    public float durability;
    public float maxDurability;

    public const float ArmorSpeedMul = 0.9f;     // броня: скорость ×0.9
    public const float CloakNoticeMul = 0.5f;    // плащ: замечают вдвое ближе
    public const float NoticeScout = 1.2f;       // разведчик в плаще: виден только с этого расстояния
    public const float RevealSeconds = 3f;       // после своего удара боец в плаще виден ещё столько секунд

    // Броня: доля срезанного физического урона и прочность (в поглощённых ударах)
    static readonly float[] ArmorReduce = { 0.25f, 0.30f, 0.36f };
    static readonly float[] ArmorDurability = { 300f, 450f, 650f };
    // Плащ: прочность (в пропущенных ударах)
    static readonly float[] CloakDurability = { 140f, 210f, 300f };

    static readonly string[] ArmorNames = { "Обычная броня", "Хорошая броня", "Превосходная броня" };
    static readonly string[] CloakNames = { "Обычный плащ из листвы", "Хороший плащ из листвы", "Превосходный плащ из листвы" };

    public BodyGear(BodyKind kind, QualityTier quality)
    {
        this.kind = kind;
        this.quality = quality;
        maxDurability = (kind == BodyKind.Armor ? ArmorDurability : CloakDurability)[(int)quality];
        durability = maxDurability;
    }

    public bool IsArmor => kind == BodyKind.Armor;
    public bool IsCloak => kind == BodyKind.Cloak;

    public static string KindName(BodyKind k) { return k == BodyKind.Armor ? "броня" : "плащ из листвы"; }

    public string Name => (IsArmor ? ArmorNames : CloakNames)[(int)quality];

    /// <summary>Какую долю физического урона срезает (у плаща - 0).</summary>
    public float Reduce => IsArmor ? ArmorReduce[(int)quality] * (0.7f + 0.3f * Frac) : 0f;

    public float Frac { get { return maxDurability > 0f ? Mathf.Clamp01(durability / maxDurability) : 0f; } }
    public int Percent { get { return Mathf.RoundToInt(Frac * 100f); } }

    // Чем выше - тем лучше (для автовыдачи)
    public float Score { get { return ((int)quality + 1f) * (0.6f + 0.4f * Frac); } }

    // Цена полной починки, золото
    public int RepairCost
    {
        get
        {
            float missing = 1f - Frac;
            if (missing <= 0.001f) return 0;
            return Mathf.Max(1, Mathf.CeilToInt(missing * (IsArmor ? 80f : 40f) * ((int)quality + 1)));
        }
    }

    public void Repair() { durability = maxDurability; }

    // Один удар. true - вещь пришла в негодность.
    public bool Wear()
    {
        durability -= 1f;
        return durability <= 0f;
    }

    public string StatsText
    {
        get
        {
            if (IsArmor)
                return $"урон по бойцу −{Reduce * 100f:F0}% (магию не держит), скорость −{(1f - ArmorSpeedMul) * 100f:F0}%, прочность {Percent}%";
            return $"враги замечают вдвое ближе, пока боец не бьёт; разведчика — лишь вплотную ({NoticeScout:0.#} кл.); прочность {Percent}%";
        }
    }
}

/// <summary>
/// Броня и плащ бойца, скрытность (продолжение класса CitizenAgent).
///  - NoticeRange: с какого расстояния враги замечают бойца (EnemyAgent дома, защитники в набеге - RaidUnit / RaidDefense).
///  - hiddenLookout ставит дозорный-разведчик (CamoScouts): его не видит никто, кроме игрока.
///  - pushingPost: колёсная баррикада, которую боец сейчас толкает (FieldPost.TickPusher).
///  - Вид: на бойце в броне - стальные пластины, в плаще - зелёный капюшон с листьями.
///    Пока плащ скрывает бойца, игрок видит его полупрозрачным.
/// </summary>
public partial class CitizenAgent
{
    /// <summary>Разведчик сидит в дозоре (ставит CamoScouts): враги его не видят вовсе.</summary>
    public bool hiddenLookout;
    /// <summary>Колёсная баррикада, которую толкает боец (ставит FieldPost / FieldPostManager).</summary>
    public FieldPost pushingPost;

    BodyGear bodyGear;
    float lastAttackAt = -99f;
    SpriteRenderer armorSr, armorBeltSr, cloakSr, leafASr, leafBSr;

    static readonly Color ArmorColor = new Color(0.66f, 0.7f, 0.76f);
    static readonly Color ArmorBeltColor = new Color(0.3f, 0.32f, 0.36f);
    static readonly Color CloakColor = new Color(0.25f, 0.5f, 0.2f);
    static readonly Color LeafColor = new Color(0.5f, 0.8f, 0.3f);
    const float HiddenAlpha = 0.45f, LookoutAlpha = 0.32f;

    public BodyGear EquippedBody => unit != null ? bodyGear : null;
    public bool HasArmor => unit != null && bodyGear != null && bodyGear.IsArmor;
    public bool HasCloak => unit != null && bodyGear != null && bodyGear.IsCloak;

    /// <summary>Броню или плащ можно надеть: любой боец, кроме полевой пушки и катапульты.</summary>
    public bool CanWearBody => unit != null && !IsFieldCannon && !IsCatapult;

    /// <summary>Боец только что бил (дома или в набеге) - плащ его не скрывает RevealSeconds.</summary>
    public void MarkAttacked() { lastAttackAt = Time.time; }

    /// <summary>Плащ сейчас прячет бойца (он не бил последние RevealSeconds).</summary>
    public bool CloakConcealed => HasCloak && Time.time - lastAttackAt >= BodyGear.RevealSeconds;

    /// <summary>Скрыт от врагов сильнее обычного (плащ или дозор).</summary>
    public bool IsConcealed => hiddenLookout || CloakConcealed;

    /// <summary>
    /// С какого расстояния враги могут заметить этого бойца (и выбрать его целью).
    /// Обычно baseRange; в плаще, пока не бьёт, - вдвое ближе; разведчик в плаще - только вплотную; дозорный - никогда.
    /// </summary>
    public float NoticeRange(float baseRange)
    {
        if (hiddenLookout) return 0f;
        if (!CloakConcealed) return baseRange;
        if (IsScout) return Mathf.Min(baseRange, BodyGear.NoticeScout);
        return baseRange * BodyGear.CloakNoticeMul;
    }

    /// <summary>Скрытность для карточки бойца ("" - нечего сказать).</summary>
    public string StealthText
    {
        get
        {
            if (hiddenLookout) return UiKit.Col(UiKit.GoodHex, "в дозоре — враги не видят");
            if (!HasCloak) return "";
            if (!CloakConcealed) return UiKit.Col(UiKit.WarnHex, "раскрыт: бьёт врага");
            return UiKit.Col(UiKit.GoodHex, IsScout ? $"скрыт — заметят лишь вплотную ({BodyGear.NoticeScout:0.#} кл.)" : "скрыт — замечают вдвое ближе");
        }
    }

    // ---------- Снаряжение ----------

    public bool EquipBody(BodyGear g)
    {
        if (g == null || dead || !CanWearBody || bodyGear != null) return false;
        bodyGear = g;
        if (g.IsArmor) moveSpeed *= BodyGear.ArmorSpeedMul;
        if (GetComponent<BodyGearKeeper>() == null) gameObject.AddComponent<BodyGearKeeper>();
        return true;
    }

    public BodyGear UnequipBody()
    {
        BodyGear g = bodyGear;
        if (g == null) return null;
        bodyGear = null;
        if (g.IsArmor && unit != null) moveSpeed /= BodyGear.ArmorSpeedMul;
        return g;
    }

    /// <summary>Снять броню / плащ на склад Оружейной (уволили, продали). Места нет - вещь пропадает.</summary>
    public void ReturnBodyToArmory()
    {
        BodyGear g = UnequipBody();
        if (g != null && !ArmoryController.StashBody(g))
            WarManager.Notify($"В Оружейных нет места: {g.Name} потеряна", false);
    }

    /// <summary>Броня срезает физический удар, плащ рвётся от него. Возвращает, что осталось от урона.</summary>
    public float MitigateBody(float dmg, bool magical)
    {
        if (magical || dmg <= 0f || unit == null || bodyGear == null) return dmg;
        BodyGear g = bodyGear;
        if (g.IsArmor) dmg *= 1f - g.Reduce;
        if (g.Wear())
        {
            UnequipBody();
            WarManager.Notify($"{(g.IsArmor ? "Пробита" : "Изорван")}: {g.Name} ({unit.unitName} {citizenName})", false);
        }
        return dmg;
    }

    /// <summary>Строка снаряжения для текстов ("" - ничего не надето).</summary>
    public string BodyText => EquippedBody != null ? $"{bodyGear.Name} ({bodyGear.Percent}%)" : "";

    // Вещь осталась без хозяина-бойца (житель исчез из города или погиб): BodyGearKeeper.OnDestroy
    internal void BodyGearOnGone()
    {
        BodyGear g = bodyGear;
        bodyGear = null;
        if (g == null) return;
        if (dead) WarManager.Notify($"Погиб {citizenName}: вместе с ним потеряна {g.Name}", false);
        else if (!ArmoryController.StashBody(g)) WarManager.Notify($"В Оружейных нет места: {g.Name} потеряна", false);
    }

    // Не боец больше (уволили мимо Discharge) - вещь на склад
    internal void BodyGearCheck()
    {
        if (bodyGear != null && !dead && unit == null) BodyGearOnGone();
    }

    // ---------- Вид ----------

    // Вызывает BodyGearKeeper.LateUpdate - после UpdateLook, чтобы прозрачность не перебивалась
    internal void BodyGearLook()
    {
        if (sr == null) return;
        bool shown = sr.enabled && !dead;
        BodyGear g = EquippedBody;
        bool armor = shown && g != null && g.IsArmor;
        bool cloak = shown && g != null && g.IsCloak;

        if (armor && armorSr == null)
        {
            armorSr = MakePart("Armor", Vector2.zero, Vector2.one, ArmorColor, 11);
            armorBeltSr = MakePart("ArmorBelt", Vector2.zero, Vector2.one, ArmorBeltColor, 12);
        }
        if (cloak && cloakSr == null)
        {
            cloakSr = MakePart("Cloak", Vector2.zero, Vector2.one, CloakColor, 11);
            leafASr = MakePart("LeafA", Vector2.zero, Vector2.one, LeafColor, 12);
            leafBSr = MakePart("LeafB", Vector2.zero, Vector2.one, LeafColor, 12);
            leafASr.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            leafBSr.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
        }

        float s = bodyScale;
        if (armorSr != null)
        {
            armorSr.enabled = armorBeltSr.enabled = armor;
            if (armor)
            {
                // Нагрудник с наплечниками и тёмный пояс
                armorSr.transform.localPosition = new Vector3(0f, s * 0.08f, 0f);
                armorSr.transform.localScale = new Vector3(s * 1.1f, s * 0.55f, 1f);
                armorBeltSr.transform.localPosition = new Vector3(0f, -s * 0.22f, 0f);
                armorBeltSr.transform.localScale = new Vector3(s * 0.8f, s * 0.12f, 1f);
                Color c = Color.Lerp(new Color(0.45f, 0.4f, 0.36f), ArmorColor, g.Frac);   // побитая броня тускнеет
                armorSr.color = c;
            }
        }
        if (cloakSr != null)
        {
            cloakSr.enabled = leafASr.enabled = leafBSr.enabled = cloak;
            if (cloak)
            {
                // Капюшон и плечи плаща, по бокам - листья
                cloakSr.transform.localPosition = new Vector3(0f, s * 0.2f, 0f);
                cloakSr.transform.localScale = new Vector3(s * 1.12f, s * 0.68f, 1f);
                leafASr.transform.localPosition = new Vector3(-s * 0.52f, -s * 0.05f, 0f);
                leafBSr.transform.localPosition = new Vector3(s * 0.5f, s * 0.38f, 0f);
                leafASr.transform.localScale = leafBSr.transform.localScale = new Vector3(s * 0.26f, s * 0.26f, 1f);
            }
        }

        // Прозрачность для игрока: плащ прячет бойца - он полупрозрачный, в дозоре - ещё прозрачнее
        float alpha = !shown ? 1f : (hiddenLookout ? LookoutAlpha : (CloakConcealed ? HiddenAlpha : 1f));
        SetAlpha(sr, alpha);
        if (horseSr != null && horseSr.enabled) SetAlpha(horseSr, alpha);
        if (cloak)
        {
            SetAlpha(cloakSr, Mathf.Min(1f, alpha + 0.15f));
            SetAlpha(leafASr, Mathf.Min(1f, alpha + 0.15f));
            SetAlpha(leafBSr, Mathf.Min(1f, alpha + 0.15f));
        }
        if (armor) { SetAlpha(armorSr, alpha); SetAlpha(armorBeltSr, alpha); }
    }

    static void SetAlpha(SpriteRenderer r, float a)
    {
        if (r == null) return;
        Color c = r.color;
        if (Mathf.Abs(c.a - a) < 0.001f) return;
        c.a = a;
        r.color = c;
    }
}

/// <summary>
/// Следит за бронёй / плащом бойца: рисует их (после UpdateLook жителя) и не даёт вещи пропасть молча.
/// Боец погиб - вещь потеряна (сообщение); житель ушёл из города или его уволили - вещь на склад Оружейной.
/// Вешается на жителя при первой выдаче (CitizenAgent.EquipBody).
/// </summary>
public class BodyGearKeeper : MonoBehaviour
{
    CitizenAgent agent;
    float checkTimer;

    void Awake() { agent = GetComponent<CitizenAgent>(); }

    void Update()
    {
        if (agent == null) return;
        checkTimer -= Time.deltaTime;
        if (checkTimer > 0f) return;
        checkTimer = 0.5f;
        agent.BodyGearCheck();
    }

    void LateUpdate()
    {
        if (agent != null) agent.BodyGearLook();
    }

    void OnDestroy()
    {
        if (!gameObject.scene.isLoaded) return;   // игра закрывается
        if (agent != null) agent.BodyGearOnGone();
    }
}
