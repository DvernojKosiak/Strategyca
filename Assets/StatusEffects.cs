using UnityEngine;

/// <summary>
/// Замедление (зелье замедления на луке, путь Магии): цель ходит медленнее, пока действует эффект.
/// Повторное попадание не складывается, а продлевает (и берёт более сильное). Над целью - голубая капля.
/// Скорость читают сами враги: EnemyAgent (дома) и RaidUnit (в набеге) через SlowEffect.Mul.
/// </summary>
public class SlowEffect : MonoBehaviour
{
    float factor = 1f, until;
    SpriteRenderer mark;
    Renderer body;

    /// <summary>Множитель скорости цели (1 - не замедлена).</summary>
    public static float Mul(Component c)
    {
        if (c == null) return 1f;
        SlowEffect s = c.GetComponent<SlowEffect>();
        return s != null && Time.time < s.until ? s.factor : 1f;
    }

    public static bool IsSlowed(Component c) { return Mul(c) < 0.999f; }

    /// <summary>Замедлить: скорость × factor (0.5 = вдвое медленнее) на duration секунд.</summary>
    public static void Apply(Component target, float factor, float duration)
    {
        if (target == null || duration <= 0f || factor >= 1f) return;
        SlowEffect s = target.GetComponent<SlowEffect>();
        if (s == null) s = target.gameObject.AddComponent<SlowEffect>();
        s.factor = Time.time < s.until ? Mathf.Min(s.factor, factor) : factor;
        s.until = Mathf.Max(s.until, Time.time + duration);
    }

    void Awake()
    {
        body = GetComponentInChildren<Renderer>();
        GameObject go = new GameObject("SlowMark");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(-0.22f, 0.55f, 0f);
        go.transform.localScale = new Vector3(0.15f, 0.15f, 1f);
        mark = go.AddComponent<SpriteRenderer>();
        mark.sprite = CombatFx.Circle();
        mark.color = new Color(0.5f, 0.8f, 1f, 0.9f);
        mark.sortingOrder = 16;
    }

    void Update()
    {
        if (Time.time > until)
        {
            if (mark != null) Destroy(mark.gameObject);
            Destroy(this);
            return;
        }
        if (mark != null)
        {
            mark.forceRenderingOff = body != null && body.forceRenderingOff;   // в тумане не видно
            float a = 0.5f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 4f));
            mark.color = new Color(0.5f, 0.8f, 1f, a);
        }
    }
}

/// <summary>
/// Горение (горящая смола Поджигателя, путь Военного дела): цель получает урон каждые полсекунды, пока горит.
/// Повторный поджог не складывается, а продлевает (и берёт более сильный). Над целью - оранжевый огонёк.
/// Огонь - не магия, но и щит от него не спасает.
/// </summary>
public class BurnEffect : MonoBehaviour
{
    const float Tick = 0.5f;

    float dps, until, acc, fxTimer;
    System.Action<float> hurt;
    SpriteRenderer mark;
    Renderer body;

    public static bool IsBurning(Component c) { return c != null && c.GetComponent<BurnEffect>() != null; }

    /// <summary>Поджечь: dps урона в секунду на duration секунд. hurt - как нанести урон этой цели.</summary>
    public static void Apply(Component target, float dps, float duration, System.Action<float> hurt)
    {
        if (target == null || dps <= 0f || duration <= 0f || hurt == null) return;
        BurnEffect b = target.GetComponent<BurnEffect>();
        if (b == null) b = target.gameObject.AddComponent<BurnEffect>();
        b.dps = Mathf.Max(b.dps, dps);
        b.until = Mathf.Max(b.until, Time.time + duration);
        b.hurt = hurt;
    }

    void Awake()
    {
        body = GetComponentInChildren<Renderer>();
        GameObject go = new GameObject("BurnMark");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0.22f, 0.55f, 0f);
        go.transform.localScale = new Vector3(0.18f, 0.18f, 1f);
        mark = go.AddComponent<SpriteRenderer>();
        mark.sprite = CombatFx.Circle();
        mark.color = new Color(1f, 0.5f, 0.1f, 0.9f);
        mark.sortingOrder = 16;
    }

    void Update()
    {
        if (Time.time > until || hurt == null)
        {
            if (mark != null) Destroy(mark.gameObject);
            Destroy(this);
            return;
        }

        bool hidden = body != null && body.forceRenderingOff;
        if (mark != null)
        {
            mark.forceRenderingOff = hidden;
            float k = Mathf.Abs(Mathf.Sin(Time.time * 9f));
            mark.color = Color.Lerp(new Color(1f, 0.35f, 0.05f, 0.95f), new Color(1f, 0.85f, 0.2f, 0.95f), k);
        }

        // Искры от горящего
        fxTimer -= Time.deltaTime;
        if (fxTimer <= 0f && !hidden)
        {
            fxTimer = 0.3f;
            CombatFx.Spark((Vector2)transform.position + Random.insideUnitCircle * 0.25f, new Color(1f, 0.55f, 0.15f), 0.12f, 0.25f);
        }

        acc += Time.deltaTime;
        while (acc >= Tick)
        {
            acc -= Tick;
            hurt(dps * Tick);
            if (this == null) return;   // цель могла сгореть
        }
    }
}
