using UnityEngine;

/// <summary>
/// Отравление: цель получает урон каждые полсекунды, пока действует яд. Над целью - зелёная капля.
/// Повторное отравление не складывается, а продлевает яд (и берёт более сильный).
/// Яд магический: его урон уже умножен на силу маны (см. ManaManager.Factor) тем, кто отравил.
/// Вешается на врага в деревне (EnemyAgent) или на защитника в набеге (RaidUnit).
/// </summary>
public class PoisonEffect : MonoBehaviour
{
    const float Tick = 0.5f;

    float dps, until, acc;
    System.Action<float> hurt;
    SpriteRenderer mark;
    Renderer body;

    public static bool IsPoisoned(Component c) { return c != null && c.GetComponent<PoisonEffect>() != null; }

    /// <summary>Отравить: dps урона в секунду на duration секунд. hurt - как нанести урон этой цели.</summary>
    public static void Apply(Component target, float dps, float duration, System.Action<float> hurt)
    {
        if (target == null || dps <= 0f || duration <= 0f || hurt == null) return;
        PoisonEffect p = target.GetComponent<PoisonEffect>();
        if (p == null) p = target.gameObject.AddComponent<PoisonEffect>();
        p.dps = Mathf.Max(p.dps, dps);
        p.until = Mathf.Max(p.until, Time.time + duration);
        p.hurt = hurt;
    }

    void Awake()
    {
        body = GetComponentInChildren<Renderer>();   // по нему узнаём, спрятана ли цель туманом

        GameObject go = new GameObject("PoisonMark");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 0.55f, 0f);
        go.transform.localScale = new Vector3(0.16f, 0.16f, 1f);
        mark = go.AddComponent<SpriteRenderer>();
        mark.sprite = CombatFx.Circle();
        mark.color = new Color(0.45f, 1f, 0.25f, 0.9f);
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

        // Капля мигает; в тумане её не видно, как и саму цель
        if (mark != null)
        {
            mark.forceRenderingOff = body != null && body.forceRenderingOff;
            float a = 0.55f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 6f));
            mark.color = new Color(0.45f, 1f, 0.25f, a);
        }

        acc += Time.deltaTime;
        while (acc >= Tick)
        {
            acc -= Tick;
            hurt(dps * Tick);
            if (this == null) return;   // цель могла погибнуть от яда
        }
    }
}
