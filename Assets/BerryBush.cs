using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Ягодный куст на карте. Проходу не мешает. Ягоды понемногу отрастают (до MaxBerries).
/// Собирают их собиратели Станции собирателей (см. BerryStation и CitizenAgent.Gatherer.cs).
///  - У куста одновременно работает только один собиратель (Claim / Release).
///  - Почти пустой куст (меньше MinToPick ягод) выглядит блёклым, и за ним не ходят.
///  - Если на куст поставили здание или стену, он "переезжает" на свободное место (см. BerryField).
/// Кусты создаёт BerryField.
/// </summary>
public class BerryBush : MonoBehaviour
{
    public static readonly List<BerryBush> All = new List<BerryBush>();

    public const int MaxBerries = 24;          // сколько ягод помещается на кусте
    public const float RegrowSeconds = 8f;     // одна ягода отрастает за столько секунд
    public const int MinToPick = 3;            // меньше - за кустом не ходят

    public int Berries { get; private set; }
    public Vector2Int Cell { get; private set; }
    public Vector2 Position => transform.position;
    public CitizenAgent ClaimedBy { get; private set; }

    // Свободен: никто не занял, или занявший уже погиб / исчез
    public bool IsFree => ClaimedBy == null || ClaimedBy.IsDead;
    public bool Ripe => Berries >= MinToPick;

    float regrow;
    SpriteRenderer leaves, fruit;

    static readonly Color LeafColor = new Color(0.16f, 0.45f, 0.22f);
    static readonly Color LeafEmpty = new Color(0.27f, 0.33f, 0.24f);
    static readonly Color FruitColor = new Color(0.86f, 0.16f, 0.38f);

    public static BerryBush Create(Vector2Int cell, Transform parent)
    {
        GameObject go = new GameObject("BerryBush");
        if (parent != null) go.transform.SetParent(parent, false);
        BerryBush b = go.AddComponent<BerryBush>();
        b.Build();
        b.MoveTo(cell);
        b.Berries = Random.Range(MaxBerries / 2, MaxBerries + 1);
        b.regrow = Random.Range(0f, RegrowSeconds);
        b.Refresh();
        return b;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Build()
    {
        leaves = Part("Leaves", 0.8f, LeafColor, -6);
        fruit = Part("Berries", 0.4f, FruitColor, -5);
    }

    SpriteRenderer Part(string partName, float size, Color c, int order)
    {
        GameObject go = new GameObject(partName);
        go.transform.SetParent(transform, false);
        go.transform.localScale = new Vector3(size, size, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = CombatFx.Circle();
        sr.color = c;
        sr.sortingOrder = order;
        return sr;
    }

    // Переставить куст в клетку (старый собиратель его теряет)
    public void MoveTo(Vector2Int cell)
    {
        Cell = cell;
        transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0f);
        ClaimedBy = null;
    }

    public bool Claim(CitizenAgent c)
    {
        if (c == null) return false;
        if (!IsFree && ClaimedBy != c) return false;
        ClaimedBy = c;
        return true;
    }

    public void Release(CitizenAgent c)
    {
        if (ClaimedBy == null || ClaimedBy == c) ClaimedBy = null;
    }

    /// <summary>Сорвать до max ягод. Возвращает, сколько сорвали.</summary>
    public int Pick(int max)
    {
        int n = Mathf.Clamp(max, 0, Berries);
        if (n <= 0) return 0;
        Berries -= n;
        Refresh();
        return n;
    }

    // Для теста (F9): куст полон
    public void Refill()
    {
        Berries = MaxBerries;
        Refresh();
    }

    void Update()
    {
        if (Berries >= MaxBerries) { regrow = 0f; return; }
        regrow += Time.deltaTime * ResourceZones.BushRegrowMul(Cell);
        if (regrow < RegrowSeconds) return;
        regrow -= RegrowSeconds;
        Berries++;
        Refresh();
    }

    void Refresh()
    {
        if (leaves == null || fruit == null) return;
        float k = Berries / (float)MaxBerries;
        leaves.color = Color.Lerp(LeafEmpty, LeafColor, Mathf.Clamp01(k * 1.5f));
        fruit.enabled = Ripe;
        float s = Mathf.Lerp(0.16f, 0.44f, k);
        fruit.transform.localScale = new Vector3(s, s, 1f);
    }
}
