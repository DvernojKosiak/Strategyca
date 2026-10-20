using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Задание на перенос: житель идёт в здание "from", забирает груз, несёт в здание "to".
/// Сами ресурсы/материалы уже списаны заказчиком (мастерской, улучшением) в момент заказа -
/// житель их "несёт" визуально, а результат наступает в onDeliver.
/// </summary>
public class HaulJob
{
    public BuildingInstance from;       // откуда забрать (обычно Ратуша или мастерская)
    public BuildingInstance to;         // куда принести
    public BuildingInstance prefer;     // чьих рабочих звать в первую очередь (может быть null)
    public Action onPickup;             // житель дошёл до "from" и взял груз
    public Action onDeliver;            // житель дошёл до "to"
    public Action<bool> onAbort;        // рейс сорван (здание снесли, жителя забрали и т.п.); bool = груз уже был взят
}

/// <summary>
/// Очередь рейсов. Если свободного жителя пока нет - задание ждёт и пробуется снова.
/// Если Ратуши нет - всё происходит мгновенно (как раньше).
/// Создаётся сама при первом заказе.
/// </summary>
public class Logistics : MonoBehaviour
{
    static Logistics instance;

    readonly List<HaulJob> pending = new List<HaulJob>();
    float timer;

    static Logistics Ensure()
    {
        if (instance == null)
        {
            GameObject go = new GameObject("Logistics");
            instance = go.AddComponent<Logistics>();
        }
        return instance;
    }

    void Awake()
    {
        if (instance == null) instance = this;
        else if (instance != this) Destroy(gameObject);
    }

    public static void Request(HaulJob job)
    {
        if (job == null) return;

        var pm = PopulationManager.Instance;
        bool noHall = pm == null || pm.Hall == null;

        // Некуда/некому носить - делаем всё мгновенно
        if (noHall || job.from == null || job.to == null || ReferenceEquals(job.from, job.to))
        {
            if (job.onPickup != null) job.onPickup();
            if (job.onDeliver != null) job.onDeliver();
            return;
        }

        Logistics l = Ensure();
        l.pending.Add(job);
        l.TryDispatch();
    }

    void Update()
    {
        if (pending.Count == 0) return;
        timer -= Time.deltaTime;
        if (timer > 0f) return;
        timer = 0.5f;
        TryDispatch();
    }

    void TryDispatch()
    {
        var pm = PopulationManager.Instance;

        for (int i = 0; i < pending.Count;)
        {
            HaulJob job = pending[i];

            // Здание, откуда/куда несли, снесли, пока заказ ждал жителя
            if (job.from == null || job.to == null)
            {
                pending.RemoveAt(i);
                if (job.onAbort != null) job.onAbort(false);
                continue;
            }

            if (pm != null && pm.DispatchHaul(job)) pending.RemoveAt(i);
            else i++;
        }
    }
}
