using UnityEngine;

/// <summary>
/// Ягодные кусты на карте: где растут и как их найти.
///  - При старте рассаживает bushCount кустов на свободные клетки, в основном ВНЕ зелёной зоны
///    (у Ратуши кусты давно вырубили - внутрь зоны попадает только малая доля insideZoneShare).
///  - Раз в 2 секунды проверяет кусты: на кусте построили здание / стену - куст переезжает на свободное место.
///  - FindBush ищет собирателю ближайший спелый свободный куст в радиусе Станции.
/// Создаётся в BuildingPlacer.Start (после стартовых зданий).
/// </summary>
public class BerryField : MonoBehaviour
{
    public static BerryField Instance { get; private set; }

    [Header("Кусты")]
    public int bushCount = 40;
    [Range(0f, 1f)] public float insideZoneShare = 0.15f;   // какая доля кустов может вырасти внутри зелёной зоны
    public float minDistanceFromCenter = 5f;                 // совсем рядом с Ратушей кустов нет
    public float minSpacing = 1.8f;                          // кусты не растут вплотную друг к другу

    bool spawned;
    float checkTimer;

    public static BerryField Create(int count)
    {
        GameObject go = new GameObject("BerryField");
        BerryField f = go.AddComponent<BerryField>();
        f.bushCount = count;
        return f;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Update()
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        if (!spawned)
        {
            spawned = true;
            for (int i = 0; i < bushCount; i++)
                if (TryPickCell(nav, out Vector2Int c)) BerryBush.Create(c, transform);
        }

        checkTimer -= Time.deltaTime;
        if (checkTimer > 0f) return;
        checkTimer = 2f;

        // На куст поставили здание - пересаживаем
        foreach (var b in BerryBush.All)
        {
            if (b == null || !nav.IsBlocked(b.Cell)) continue;
            if (TryPickCell(nav, out Vector2Int c)) b.MoveTo(c);
        }
    }

    // Свободная клетка для куста: в основном вне зелёной зоны, не у самой Ратуши и не вплотную к другим кустам
    bool TryPickCell(GridNavigation nav, out Vector2Int cell)
    {
        int half = nav.mapSize / 2;
        Territory t = Territory.Instance;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < 80; i++)
            {
                Vector2Int c = new Vector2Int(Random.Range(-half + 1, half - 1), Random.Range(-half + 1, half - 1));
                if (nav.IsBlocked(c)) continue;
                Vector2 p = nav.CellCenter(c);
                if (p.magnitude < minDistanceFromCenter) continue;
                if (TooClose(p)) continue;

                // Первый проход - соблюдаем «в основном вне города», второй - лишь бы нашлось место
                if (pass == 0 && t != null && t.InZone(c) && Random.value > insideZoneShare) continue;

                cell = c;
                return true;
            }
        }
        cell = Vector2Int.zero;
        return false;
    }

    bool TooClose(Vector2 p)
    {
        foreach (var b in BerryBush.All)
            if (b != null && Vector2.Distance(b.Position, p) < minSpacing) return true;
        return false;
    }

    /// <summary>
    /// Куст для собирателя: спелый, свободный, не дальше radius от станции.
    /// Чем ближе к собирателю и чем больше ягод - тем лучше. skip - куст, к которому не дойти.
    /// </summary>
    public static BerryBush FindBush(Vector2 station, float radius, Vector2 from, int want, BerryBush skip)
    {
        BerryBush best = null;
        float bestScore = float.MaxValue;
        foreach (var b in BerryBush.All)
        {
            if (b == null || b == skip || !b.Ripe || !b.IsFree) continue;
            if (Vector2.Distance(b.Position, station) > radius) continue;
            float score = Vector2.Distance(b.Position, from) + Mathf.Max(0, want - b.Berries) * 0.8f;
            if (score < bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    /// <summary>Сколько кустов (и ягод на них) в радиусе от точки - для панели станции.</summary>
    public static int CountNear(Vector2 p, float radius, out int berries)
    {
        int n = 0;
        berries = 0;
        foreach (var b in BerryBush.All)
        {
            if (b == null || Vector2.Distance(b.Position, p) > radius) continue;
            n++;
            berries += b.Berries;
        }
        return n;
    }

    // Для теста (F9): все кусты полны
    public static void RefillAll()
    {
        foreach (var b in BerryBush.All) if (b != null) b.Refill();
    }
}
