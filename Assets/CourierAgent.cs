using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Курьер: физически переносит накопленные ресурсы из производственного здания в казну.
///  1. Появляется у двери Ратуши.
///  2. Идёт к двери здания-источника (A* через GridNavigation) и забирает со склада здания ресурсы.
///  3. Несёт их обратно к Ратуше; на месте ресурсы попадают в ResourceManager, курьер исчезает.
/// Создаётся только через CourierAgent.Dispatch(здание).
/// Курьер - отдельный лёгкий агент, он не занимает жителя из населения.
/// </summary>
public class CourierAgent : MonoBehaviour
{
    public static readonly List<CourierAgent> All = new List<CourierAgent>();

    [Header("Настройки")]
    public float moveSpeed = 4f;
    public int carryCapacity = 60;   // сколько КАЖДОГО ресурса курьер уносит за одну ходку
    public int maxPathFails = 8;     // сколько раз подряд не удалось найти путь, прежде чем сдаться

    enum Phase { ToSource, ToHall }

    Phase phase = Phase.ToSource;
    BuildingInstance source;
    int cGold, cWood, cStone, cFood;

    List<Vector2Int> path;
    int pathIndex;
    float retryTimer;
    int fails;

    SpriteRenderer sr;
    static Sprite squareSprite;

    static readonly Color EmptyColor = new Color(0.85f, 0.85f, 0.85f);
    static readonly Color LoadedColor = new Color(1f, 0.9f, 0.2f);

    // ---------- Запуск ----------

    /// <summary>
    /// Отправить курьера за ресурсами здания. Возвращает false, если отправлять некого/незачем
    /// (склад пуст или за этим зданием уже идёт курьер).
    /// </summary>
    public static bool Dispatch(BuildingInstance src)
    {
        if (src == null || src.type == null) return false;
        if (src.courierOnTheWay) return false;
        if (src.StoredTotal <= 0) return false;

        var pm = PopulationManager.Instance;
        var nav = GridNavigation.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;

        // Нет Ратуши или навигации - собираем мгновенно, как раньше
        if (hall == null || nav == null)
        {
            src.CollectToTreasury();
            return true;
        }

        Vector2 spawn = hall.Center;
        if (nav.TryGetDoorCell(hall, nav.WorldToCell(hall.Center), out Vector2Int door))
            spawn = nav.CellCenter(door);

        GameObject go = new GameObject("Courier");
        go.transform.position = spawn;
        CourierAgent c = go.AddComponent<CourierAgent>();
        c.source = src;
        src.courierOnTheWay = true;
        return true;
    }

    // ---------- Жизненный цикл ----------

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        // Не дошли до здания - снимаем отметку "за мной уже идёт курьер"
        if (phase == Phase.ToSource && source != null) source.courierOnTheWay = false;
    }

    static Sprite GetSquare()
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

    void Awake()
    {
        GameObject gfx = new GameObject("Gfx");
        gfx.transform.SetParent(transform, false);
        gfx.transform.localScale = new Vector3(0.35f, 0.35f, 1f);
        sr = gfx.AddComponent<SpriteRenderer>();
        sr.sprite = GetSquare();
        sr.sortingOrder = 12;
        sr.color = EmptyColor;
    }

    void Update()
    {
        var nav = GridNavigation.Instance;
        if (nav == null) return;

        if (phase == Phase.ToSource)
        {
            if (source == null) { Destroy(gameObject); return; }  // здание снесли
            if (!source.isActiveAndEnabled) return;               // здание переносят - ждём
        }

        var pm = PopulationManager.Instance;
        BuildingInstance hall = pm != null ? pm.Hall : null;
        if (phase == Phase.ToHall && hall == null) { Deposit(); return; } // Ратуши нет - сдаём напрямую

        BuildingInstance target = phase == Phase.ToSource ? source : hall;
        Move(nav, target);
    }

    // ---------- Движение ----------

    void Move(GridNavigation nav, BuildingInstance target)
    {
        if (path != null && pathIndex < path.Count)
        {
            // Впереди построили здание - перепланируем
            if (nav.IsBlocked(path[pathIndex]))
            {
                path = null;
                retryTimer = 0f;
                return;
            }

            Vector2 t = nav.CellCenter(path[pathIndex]);
            transform.position = Vector2.MoveTowards(transform.position, t, moveSpeed * nav.SpeedFactor(transform.position, path[pathIndex]) * Time.deltaTime);

            if (Vector2.Distance(transform.position, t) < 0.05f)
            {
                pathIndex++;
                if (pathIndex >= path.Count) Arrive();
            }
            return;
        }

        path = null;

        retryTimer -= Time.deltaTime;
        if (retryTimer > 0f) return;
        if (!GridNavigation.TryUsePathBudget()) return; // очередь на поиск пути

        if (!PlanRoute(nav, target))
        {
            retryTimer = 1f;
            if (++fails >= maxPathFails) GiveUp();
        }
    }

    bool PlanRoute(GridNavigation nav, BuildingInstance target)
    {
        Vector2Int cur = nav.WorldToCell(transform.position);
        if (nav.IsBlocked(cur))
        {
            cur = nav.NearestFree(cur);
            transform.position = nav.CellCenter(cur);
        }

        if (!nav.TryGetDoorCell(target, cur, out Vector2Int door)) return false;
        if (door == cur) { Arrive(); return true; }

        List<Vector2Int> p = nav.FindPath(cur, door);
        if (p == null) return false;
        if (p.Count == 0) { Arrive(); return true; }

        path = p;
        pathIndex = 0;
        fails = 0;
        return true;
    }

    // ---------- Погрузка и разгрузка ----------

    void Arrive()
    {
        path = null;
        fails = 0;
        retryTimer = 0f;

        if (phase == Phase.ToSource)
        {
            source.courierOnTheWay = false; // дальше можно слать нового курьера за остатком
            source.TakeStored(carryCapacity, out cGold, out cWood, out cStone, out cFood);

            if (cGold + cWood + cStone + cFood <= 0)
            {
                Destroy(gameObject); // склад уже опустел
                return;
            }

            phase = Phase.ToHall;
            sr.color = LoadedColor;
        }
        else
        {
            Deposit();
        }
    }

    void Deposit()
    {
        if (ResourceManager.Instance != null)
            ResourceManager.Instance.Add(cGold, cWood, cStone, cFood);
        cGold = cWood = cStone = cFood = 0;
        Destroy(gameObject);
    }

    // Не смогли найти дорогу: до здания - просто отменяем рейс, с грузом - сдаём прямо на месте
    void GiveUp()
    {
        if (phase == Phase.ToHall) Deposit();
        else Destroy(gameObject);
    }
}