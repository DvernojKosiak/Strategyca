using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Движение "в погоне" для боя (солдаты, враги).
/// Если до цели прямая свободна - идём напрямую, иначе по A*.
/// Цель может двигаться, поэтому маршрут перестраивается не чаще раза в 0.6 с.
/// </summary>
public class GridMover
{
    List<Vector2Int> path;
    int index;
    float repathTimer;
    Vector2Int pathGoal;

    // Сколько раз подряд путь не нашёлся (цель недостижима)
    public int Fails { get; private set; }

    public void Clear()
    {
        path = null;
        repathTimer = 0f;
    }

    public void ResetFails() { Fails = 0; }

    /// <summary>Шаг к точке target. Возвращает true, если уже стоим в ней (ближе stopDist).</summary>
    public bool Step(Transform tr, GridNavigation nav, Vector2 target, float speed, float stopDist = 0.05f)
    {
        Vector2 pos = tr.position;
        if (Vector2.Distance(pos, target) <= stopDist) { path = null; return true; }

        // Стоим в заблокированной клетке (рядом поставили здание) - выталкиваемся наружу
        Vector2Int cur = nav.WorldToCell(pos);
        if (nav.IsBlocked(cur))
        {
            cur = nav.NearestFree(cur);
            tr.position = nav.CellCenter(cur);
            pos = tr.position;
            path = null;
        }

        // Прямая дорога свободна - идём напрямую
        if (nav.LineWalkable(pos, target))
        {
            path = null;
            Fails = 0;
            Vector2Int nextCell = nav.WorldToCell(Vector2.MoveTowards(pos, target, 0.5f));
            float sp = speed * nav.SpeedFactor(pos, nextCell);
            tr.position = Vector2.MoveTowards(pos, target, sp * Time.deltaTime);
            return Vector2.Distance(tr.position, target) <= stopDist;
        }

        Vector2Int goalCell = nav.WorldToCell(target);
        if (nav.IsBlocked(goalCell)) goalCell = nav.NearestFree(goalCell);

        repathTimer -= Time.deltaTime;
        bool pathValid = path != null && index < path.Count;
        bool wantNew = !pathValid || goalCell != pathGoal;

        if (wantNew && repathTimer <= 0f && goalCell != cur && GridNavigation.TryUsePathBudget(6))
        {
            repathTimer = 0.6f;
            List<Vector2Int> p = nav.FindPath(cur, goalCell);
            if (p != null && p.Count > 0)
            {
                path = p;
                index = 0;
                pathGoal = goalCell;
                Fails = 0;
                pathValid = true;
            }
            else
            {
                path = null;
                pathValid = false;
                Fails++;
            }
        }

        if (!pathValid) return false;

        Vector2Int next = path[index];
        if (nav.IsBlocked(next)) { path = null; repathTimer = 0f; return false; }

        Vector2 t = nav.CellCenter(next);
        tr.position = Vector2.MoveTowards(pos, t, speed * nav.SpeedFactor(pos, next) * Time.deltaTime);
        if (Vector2.Distance(tr.position, t) < 0.05f) index++;
        return false;
    }
}
