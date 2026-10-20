using UnityEngine;

/// <summary>
/// Простые боевые эффекты: летящая стрела, вспышка удара, след гибели на земле,
/// магическое ядро пушки (летит дугой, на падении вызывает взрыв и урон),
/// ядро Парового отбойника (летит прямо, на попадании - облако пара), огонь и искры Литейной,
/// заряды Мага стихий: водяной снаряд, огненный шар, волна из реки.
/// Каждый эффект - маленький спрайт, который сам себя удаляет.
/// </summary>
public class CombatFx : MonoBehaviour
{
    enum Kind { Arrow, Flash, Death, Shell, Blast, Slug, Ring, Bucket, Wave }

    static Sprite squareSprite, circleSprite;

    Kind kind;
    Vector3 from, to;
    float t, duration;
    float arc, radius;                 // для ядра и взрыва
    System.Action onLand;              // что сделать, когда ядро упало
    SpriteRenderer sr;
    Color baseColor;

    // Общий белый квадрат 1x1 (им же рисуются враги)
    public static Sprite Square()
    {
        if (squareSprite == null)
        {
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            squareSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }
        return squareSprite;
    }

    // Белый круг диаметром 1 клетка (для ядра и взрыва)
    public static Sprite Circle()
    {
        if (circleSprite == null)
        {
            const int n = 64;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f;
                    float dy = (y + 0.5f) / n * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) * 8f)));
                }
            tex.Apply();
            circleSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }
        return circleSprite;
    }

    static CombatFx Make(string fxName, Kind kind, Vector2 pos, Vector2 scale, Color color, float duration, int order)
    {
        GameObject go = new GameObject(fxName);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(scale.x, scale.y, 1f);

        CombatFx fx = go.AddComponent<CombatFx>();
        fx.kind = kind;
        fx.duration = duration;
        fx.baseColor = color;
        fx.sr = go.AddComponent<SpriteRenderer>();
        fx.sr.sprite = Square();
        fx.sr.color = color;
        fx.sr.sortingOrder = order;
        return fx;
    }

    // Стрела летит от лучника (или башни) к цели
    public static void Arrow(Vector2 a, Vector2 b)
    {
        float dur = Mathf.Clamp(Vector2.Distance(a, b) / 18f, 0.08f, 0.35f);
        CombatFx fx = Make("Arrow", Kind.Arrow, a, new Vector2(0.35f, 0.06f), new Color(0.95f, 0.9f, 0.7f), dur, 20);
        fx.from = a;
        fx.to = b;
        Vector2 d = b - a;
        fx.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    // Магическое ядро: летит дугой "сверху" от a к b, на падении взрывается и вызывает onLand (там считается урон)
    public static void Shell(Vector2 a, Vector2 b, float dur, float blastRadius, System.Action onLand)
    {
        CombatFx fx = Make("MagicShell", Kind.Shell, a, new Vector2(0.3f, 0.3f), new Color(0.75f, 0.5f, 1f), dur, 23);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
        fx.arc = Mathf.Clamp(Vector2.Distance(a, b) * 0.4f, 1.5f, 5f);
        fx.radius = blastRadius;
        fx.onLand = onLand;
    }

    // Ведро горящей смолы (Поджигатель): летит дугой, на падении - разлив огня и onLand (там урон и поджог)
    public static void Bucket(Vector2 a, Vector2 b, float radius, System.Action onLand)
    {
        CombatFx fx = Make("ResinBucket", Kind.Bucket, a, new Vector2(0.24f, 0.24f), new Color(1f, 0.45f, 0.1f), 0.45f, 23);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
        fx.arc = Mathf.Clamp(Vector2.Distance(a, b) * 0.35f, 0.8f, 2.5f);
        fx.radius = radius;
        fx.onLand = onLand;
    }

    // Огненный шар Мага стихий: летит невысокой дугой, на падении - огонь по площади и onLand
    public static void Fireball(Vector2 a, Vector2 b, float radius, System.Action onLand)
    {
        CombatFx fx = Make("Fireball", Kind.Bucket, a, new Vector2(0.3f, 0.3f), new Color(1f, 0.38f, 0.08f), 0.5f, 23);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
        fx.arc = Mathf.Clamp(Vector2.Distance(a, b) * 0.2f, 0.4f, 1.4f);
        fx.radius = radius;
        fx.onLand = onLand;
    }

    // Обычный снаряд Мага стихий: голубой сгусток
    public static void Bolt(Vector2 a, Vector2 b)
    {
        float dur = Mathf.Clamp(Vector2.Distance(a, b) / 14f, 0.08f, 0.4f);
        CombatFx fx = Make("Bolt", Kind.Arrow, a, new Vector2(0.17f, 0.17f), new Color(0.55f, 0.85f, 1f), dur, 22);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
    }

    // Волна из реки: вода катится от берега к цели, на месте - всплеск и onLand (там урон и замедление)
    public static void Wave(Vector2 a, Vector2 b, float radius, System.Action onLand)
    {
        float dur = Mathf.Clamp(Vector2.Distance(a, b) / 7f, 0.25f, 0.8f);
        CombatFx fx = Make("Wave", Kind.Wave, a, new Vector2(0.4f, 0.4f), new Color(0.35f, 0.65f, 1f, 0.85f), dur, 23);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
        fx.radius = radius;
        fx.onLand = onLand;
    }

    // Разлив воды: синие кольца и брызги
    public static void Splash(Vector2 at, float radius)
    {
        Ring(at, radius, new Color(0.3f, 0.6f, 1f, 0.7f), 0.8f);
        Ring(at, radius * 0.6f, new Color(0.75f, 0.9f, 1f, 0.8f), 0.5f);
        int n = Mathf.Clamp(Mathf.RoundToInt(radius * 8f), 8, 22);
        for (int i = 0; i < n; i++)
        {
            Vector2 p = at + Random.insideUnitCircle * radius;
            Color c = Random.value < 0.5f ? new Color(0.45f, 0.75f, 1f) : new Color(0.85f, 0.95f, 1f);
            Spark(p, c, Random.Range(0.12f, 0.26f), Random.Range(0.3f, 0.8f));
        }
    }

    // Разлитая горящая смола: огненные кольца и языки пламени по всей площади
    public static void FirePool(Vector2 at, float radius)
    {
        Ring(at, radius, new Color(1f, 0.35f, 0.05f, 0.75f), 0.9f);
        Ring(at, radius * 0.7f, new Color(1f, 0.75f, 0.2f, 0.8f), 0.6f);
        int n = Mathf.Clamp(Mathf.RoundToInt(radius * 10f), 8, 24);
        for (int i = 0; i < n; i++)
        {
            Vector2 p = at + Random.insideUnitCircle * radius;
            Color c = Random.value < 0.5f ? new Color(1f, 0.4f, 0.08f) : new Color(1f, 0.8f, 0.25f);
            Spark(p, c, Random.Range(0.14f, 0.3f), Random.Range(0.4f, 1.1f));
        }
    }

    // Расширяющееся магическое кольцо взрыва
    public static void Blast(Vector2 at, float radius)
    {
        CombatFx fx = Make("MagicBlast", Kind.Blast, at, new Vector2(0.3f, 0.3f), new Color(0.8f, 0.5f, 1f, 0.6f), 0.4f, 24);
        fx.sr.sprite = Circle();
        fx.radius = radius;
    }

    // Тяжёлое ядро Парового отбойника: летит прямо и быстро, на попадании - облачко пара и onHit (там отбрасывание)
    public static void Slug(Vector2 a, Vector2 b, float puffRadius, System.Action onHit)
    {
        float dur = Mathf.Clamp(Vector2.Distance(a, b) / 22f, 0.08f, 0.3f);
        CombatFx fx = Make("SteamSlug", Kind.Slug, a, new Vector2(0.26f, 0.26f), new Color(0.55f, 0.55f, 0.6f), dur, 23);
        fx.sr.sprite = Circle();
        fx.from = a;
        fx.to = b;
        fx.radius = puffRadius;
        fx.onLand = onHit;
    }

    // Расширяющееся кольцо любого цвета (пар отбойника, жар и пожар Литейной)
    public static void Ring(Vector2 at, float radius, Color color, float duration = 0.4f)
    {
        CombatFx fx = Make("Ring", Kind.Ring, at, new Vector2(0.3f, 0.3f), color, duration, 24);
        fx.sr.sprite = Circle();
        fx.radius = radius;
    }

    // Искра / язычок огня: короткая вспышка заданного цвета
    public static void Spark(Vector2 at, Color color, float size = 0.18f, float duration = 0.25f)
    {
        Make("Spark", Kind.Flash, at, new Vector2(size, size), color, duration, 22);
    }

    // Мешочек добычи перелетает от одного к другому (враг -> убийца, здание -> налётчик, солдат -> склад)
    public static void LootFly(Vector2 a, Vector2 b)
    {
        float dur = Mathf.Clamp(Vector2.Distance(a, b) / 10f, 0.15f, 0.5f);
        CombatFx fx = Make("Loot", Kind.Arrow, a, new Vector2(0.2f, 0.2f), new Color(1f, 0.8f, 0.25f), dur, 22);
        fx.from = a;
        fx.to = b;
    }

    // Зелёная искра лечения: летит от целителя к пациенту
    public static void HealSpark(Vector2 a, Vector2 b)
    {
        Vector2 from = a + Random.insideUnitCircle * 0.15f;
        float dur = Mathf.Clamp(Vector2.Distance(from, b) / 8f, 0.12f, 0.4f);
        CombatFx fx = Make("Heal", Kind.Arrow, from, new Vector2(0.13f, 0.13f), new Color(0.45f, 1f, 0.6f, 0.9f), dur, 22);
        fx.from = from;
        fx.to = b + Random.insideUnitCircle * 0.2f;
    }

    // Вспышка удара мечом
    public static void Flash(Vector2 at)
    {
        Make("Hit", Kind.Flash, at + Random.insideUnitCircle * 0.15f, new Vector2(0.22f, 0.22f), new Color(1f, 1f, 1f, 0.9f), 0.12f, 21);
    }

    // Тёмная отметина на месте гибели, медленно исчезает
    public static void Death(Vector2 at)
    {
        CombatFx fx = Make("Fallen", Kind.Death, at, new Vector2(0.45f, 0.12f), new Color(0.15f, 0.1f, 0.1f, 0.85f), 6f, 2);
        fx.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(-40f, 40f));
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;

        if (kind == Kind.Arrow || kind == Kind.Slug)
        {
            transform.position = Vector3.Lerp(from, to, k);
        }
        else if (kind == Kind.Wave)
        {
            transform.position = Vector3.Lerp(from, to, k);
            float s = Mathf.Lerp(0.35f, Mathf.Max(0.6f, radius), k);   // волна растёт на ходу
            transform.localScale = new Vector3(s, s * 0.7f, 1f);
        }
        else if (kind == Kind.Shell || kind == Kind.Bucket)
        {
            Vector3 p = Vector3.Lerp(from, to, k);
            float h = 4f * k * (1f - k);              // 0 в начале и конце, 1 на вершине дуги
            p.y += arc * h;
            transform.position = p;
            float s = 0.25f + 0.25f * h;              // на вершине ядро "ближе к камере"
            transform.localScale = new Vector3(s, s, 1f);
        }
        else if (kind == Kind.Blast || kind == Kind.Ring)
        {
            float e = 1f - (1f - k) * (1f - k);
            float s = Mathf.Lerp(0.3f, radius * 2f, e);
            transform.localScale = new Vector3(s, s, 1f);
            Color c = baseColor;
            c.a = baseColor.a * (1f - k);
            sr.color = c;
        }
        else
        {
            Color c = baseColor;
            c.a = baseColor.a * (1f - k);
            sr.color = c;
        }

        if (t >= duration)
        {
            if (kind == Kind.Shell || kind == Kind.Slug || kind == Kind.Bucket || kind == Kind.Wave)
            {
                if (kind == Kind.Shell) Blast(to, radius);
                else if (kind == Kind.Bucket) FirePool(to, radius);
                else if (kind == Kind.Wave) Splash(to, radius);
                else Ring(to, radius, new Color(0.92f, 0.94f, 1f, 0.7f), 0.35f);   // облако пара
                System.Action cb = onLand;
                onLand = null;
                if (cb != null) cb();
            }
            Destroy(gameObject);
        }
    }
}