using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Запас полевого снаряжения (заслоны, пункты, заслоны с шипами и на колёсах, полевые башни), изготовленного в Кузне мастера
/// (путь Военного дела). Лежит в Ратуше: оттуда берётся и в оборону дома, и в набег (окно «В бой!»).
/// У каждой вещи своя сила (растёт с уровнем Кузни) и свой остаток лечебного запаса:
/// пункт, который уже раздал часть здоровья, возвращается в запас таким же «потраченным».
/// </summary>
public static class FieldGear
{
    // Все виды PostKind (быстрый заслон не изготавливается - его запас всегда пуст, ставят прямо за дерево)
    public const int Kinds = 6;

    public class Item
    {
        public PostKind kind;
        public float strength = 1f;   // множитель прочности и лечебного запаса
        public float pool;            // сколько здоровья пункт ещё может раздать

        public Item(PostKind kind, float strength)
        {
            this.kind = kind;
            this.strength = Mathf.Max(0.1f, strength);
            pool = FieldPost.Specs[(int)kind].healPool * this.strength;
        }

        public float MaxPool { get { return FieldPost.Specs[(int)kind].healPool * strength; } }

        // Чем выше - тем лучше вещь (берём лучшую первой)
        public float Score { get { return strength + (MaxPool > 0f ? pool / MaxPool : 0f); } }
    }

    static readonly List<Item>[] stock = NewStock();

    /// <summary>Пустые списки на каждый вид (так же устроен набор похода в RaidManager).</summary>
    public static List<Item>[] NewStock()
    {
        var a = new List<Item>[Kinds];
        for (int i = 0; i < Kinds; i++) a[i] = new List<Item>();
        return a;
    }

    // Static не сбрасывается между запусками Play, если отключён Domain Reload
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { foreach (var l in stock) l.Clear(); }

    public static int Count(PostKind k) { return stock[(int)k].Count; }

    public static void Add(Item it)
    {
        if (it != null) stock[(int)it.kind].Add(it);
    }

    /// <summary>Взять лучшую вещь этого вида.</summary>
    public static bool Take(PostKind k, out Item item)
    {
        item = null;
        List<Item> l = stock[(int)k];
        int best = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < l.Count; i++)
            if (l[i].Score > bestScore) { bestScore = l[i].Score; best = i; }
        if (best < 0) return false;
        item = l[best];
        l.RemoveAt(best);
        return true;
    }
}
