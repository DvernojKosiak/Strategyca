using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Военный строитель - рабочий Двора военного строителя (продолжение класса CitizenAgent).
///  - Война: из любого укрытия выходит к работе, которую дал двор (WarBuilderYard.FindJob):
///    отмеченное здание, самое побитое здание или полевое укрепление (стройка / починка).
///    Встаёт у здания с той стороны, где врагов нет, и стучит молотком (искры), пока не починит.
///    Враг подошёл вплотную или ударил - бросает работу и бежит прятаться (обычная паника жителя),
///    к этой цели какое-то время не возвращается.
///  - Мир: в рабочее время со двора выходит подлатать побитые здания (если на дворе включено).
/// Сделал работу - сразу ищет следующую, нет работы - обычный распорядок (укрытие / двор).
/// </summary>
public partial class CitizenAgent
{
    enum WarBuildPhase { None, ToSite, Working }

    static readonly Color WarBuilderColor = new Color(1f, 0.6f, 0.2f);

    WarBuildPhase wbPhase = WarBuildPhase.None;
    BuildingInstance wbBuilding;
    FieldPost wbPost;
    Vector2 wbSpot;
    float wbThink, wbSpotTimer, wbFxTimer, wbCooldownUntil;
    object wbSkip;
    float wbSkipUntil;
    readonly GridMover wbMover = new GridMover();
    SpriteRenderer wbTool;

    BuildingInstance wbYardFor;
    WarBuilderYard wbYardCached;

    /// <summary>Строитель занят ремонтом / стройкой (идёт или работает).</summary>
    public bool IsWarBuilding => wbPhase != WarBuildPhase.None;

    /// <summary>Над чем работает строитель (здание или укрепление), null - ни над чем.</summary>
    public object WarBuildTarget
    {
        get
        {
            if (wbPhase == WarBuildPhase.None || dead || IsMilitary) return null;
            if (wbBuilding != null) return wbBuilding;
            if (wbPost != null) return wbPost;
            return null;
        }
    }

    WarBuilderYard MyWarYard()
    {
        if (IsMilitary || !Usable(Workplace)) return null;
        if (!ReferenceEquals(wbYardFor, Workplace)) { wbYardFor = Workplace; wbYardCached = Workplace.GetComponent<WarBuilderYard>(); }
        return wbYardCached;
    }

    // Цель всё ещё нуждается в работе
    bool WarBuildTargetOk(bool war)
    {
        if (wbPost != null) return wbPost.Alive && !wbPost.Raid && wbPost.NeedsWork;
        if (wbBuilding != null)
        {
            if (wbBuilding.IsRuined || !wbBuilding.isActiveAndEnabled) return false;
            // Начатое доводим почти до конца
            return wbBuilding.Hp < wbBuilding.MaxHp - 0.5f;
        }
        return false;
    }

    // true - строитель занят (остальной распорядок на этот кадр пропускаем)
    bool WarBuildTick(GridNavigation nav, float dt)
    {
        WarBuilderYard yard = MyWarYard();
        if (yard == null || !yard.Works) { StopWarBuild(); return false; }

        // Другие дела важнее: поручения, лазарет, обход целителя, паника
        if (IsCourier || IsHauling || IsHealerBusy || bed != null || IsGathering || IsCartDriver || IsPanicking)
        {
            StopWarBuild();
            return false;
        }

        bool war = WarManager.IsWar;

        if (wbPhase == WarBuildPhase.None)
        {
            if (Time.time < wbCooldownUntil) return false;
            if (HpFraction < 0.3f) return false;   // еле держится - сначала поесть / подлечиться
            if (inside && insideRef != null && !insideRef.isActiveAndEnabled) return false;

            if (!war)
            {
                // Мир: выходят только со двора, в рабочее время
                if (!yard.PeaceRepair) return false;
                GameClock clock = GameClock.Instance;
                if (clock != null && !clock.IsWorkTime) return false;
                if (!inside || !ReferenceEquals(insideRef, yard.Building)) return false;
            }

            wbThink -= dt;
            if (wbThink > 0f) return false;
            wbThink = war ? 1f : 3f;

            if (!TryStartWarJob(nav, yard, war)) return false;
            if (inside) ExitBuilding(nav);
            return true;
        }

        // --- В работе ---
        if (!WarBuildTargetOk(war))
        {
            // Готово - сразу ищем следующую работу рядом
            Vector2 at = transform.position;
            ClearWarJob();
            if (!TryStartWarJob(nav, yard, war)) { StopWarBuild(); return false; }
            if (wbPhase == WarBuildPhase.None) return false;
            if (Vector2.Distance(at, wbSpot) < 0.3f) wbPhase = WarBuildPhase.Working;
        }

        // Враг вплотную - бросаем работу и бежим (дальше - обычная паника жителя)
        if (war && EnemyAgent.AnyWithin(transform.position, WarBuilderYard.BuilderPanicRadius))
        {
            AvoidWarTarget(8f);
            StopWarBuild();
            wbCooldownUntil = Time.time + 4f;
            panicUntil = Time.time + 4f;
            return false;
        }

        if (inside)
        {
            if (insideRef != null && !insideRef.isActiveAndEnabled) return true;
            ExitBuilding(nav);
        }
        path = null;
        pathTarget = null;
        mealTrip = false;

        // Место работы пересчитываем: враги ходят
        wbSpotTimer -= dt;
        if (wbSpotTimer <= 0f)
        {
            wbSpotTimer = wbPhase == WarBuildPhase.Working ? 2.5f : 1.5f;
            Vector2 spot;
            if (!FindWarSpot(nav, out spot))
            {
                // Подойти не с какой стороны - позже
                AvoidWarTarget(6f);
                StopWarBuild();
                return false;
            }
            if (Vector2.Distance(spot, wbSpot) > 0.6f)
            {
                wbSpot = spot;
                wbMover.Clear();
                if (wbPhase == WarBuildPhase.Working && Vector2.Distance(transform.position, wbSpot) > 0.6f) wbPhase = WarBuildPhase.ToSite;
            }
        }

        if (wbPhase == WarBuildPhase.ToSite)
        {
            if (wbMover.Step(transform, nav, wbSpot, moveSpeed, 0.15f))
            {
                wbMover.Clear();
                wbPhase = WarBuildPhase.Working;
                wbFxTimer = 0f;
            }
            else if (wbMover.Fails >= 4)
            {
                // Не дойти (обнесено стеной, за рекой) - долго не пробуем
                AvoidWarTarget(20f);
                StopWarBuild();
                wbCooldownUntil = Time.time + 1f;
                return false;
            }
            return true;
        }

        // --- Работаем ---
        float skill = Mathf.Max(0.4f, WorkFactor);
        Vector2 hit;
        if (wbPost != null)
        {
            hit = wbPost.ClosestPoint(transform.position);
            if (!wbPost.Built)
            {
                float s = yard.BuildMul * skill * dt;
                wbPost.AddBuildWork(s);
                yard.CountPostWork(s);
            }
            else
            {
                float want = Mathf.Min(yard.RepairRate * skill * dt, Mathf.Max(0f, wbPost.MaxHp - wbPost.Hp));
                float got = yard.PayRepair(want);
                if (got > 0f) { wbPost.Repair(got); yard.CountRepair(got); }
            }
        }
        else
        {
            BuildingInstance b = wbBuilding;
            hit = b.ClosestPoint(transform.position);
            float cur = b.Hp;
            float want = Mathf.Min(yard.RepairRate * skill * dt, Mathf.Max(0f, b.MaxHp - cur));
            float got = yard.PayRepair(want);
            if (got > 0f)
            {
                b.hp = Mathf.Min(b.MaxHp, cur + got);
                yard.CountRepair(got);
            }
        }

        // Искры от молотка
        wbFxTimer -= dt;
        if (wbFxTimer <= 0f)
        {
            wbFxTimer = Random.Range(0.35f, 0.6f);
            Vector2 p = hit + Random.insideUnitCircle * 0.15f;
            CombatFx.Spark(p, new Color(1f, 0.78f, 0.35f, 0.95f), 0.12f, 0.18f);
            if (Random.value < 0.25f) CombatFx.Spark(p + Random.insideUnitCircle * 0.2f, new Color(1f, 0.95f, 0.7f, 0.9f), 0.08f, 0.15f);
        }
        return true;
    }

    bool TryStartWarJob(GridNavigation nav, WarBuilderYard yard, bool war)
    {
        object skip = Time.time < wbSkipUntil ? wbSkip : null;
        BuildingInstance b;
        FieldPost p;
        if (!yard.FindJob(transform.position, this, skip, war, out b, out p)) return false;

        wbBuilding = b;
        wbPost = p;
        Vector2 spot;
        if (!FindWarSpot(nav, out spot))
        {
            AvoidWarTarget(6f);
            ClearWarJob();
            return false;
        }
        wbSpot = spot;
        wbSpotTimer = 1.5f;
        wbPhase = WarBuildPhase.ToSite;
        wbMover.Clear();
        wbMover.ResetFails();
        mealTrip = false;
        StopGather();
        return true;
    }

    // Не возвращаться к текущей цели seconds секунд
    void AvoidWarTarget(float seconds)
    {
        object t = wbBuilding != null ? (object)wbBuilding : wbPost;
        if (t == null) return;
        wbSkip = t;
        wbSkipUntil = Time.time + seconds;
    }

    void ClearWarJob()
    {
        wbBuilding = null;
        wbPost = null;
        wbPhase = WarBuildPhase.None;
        wbMover.Clear();
    }

    /// <summary>Работа строителя закончена или прервана.</summary>
    void StopWarBuild()
    {
        bool was = wbPhase != WarBuildPhase.None;
        ClearWarJob();
        wbMover.ResetFails();
        if (was)
        {
            thinkTimer = 0f;
            retryTimer = 0f;
            if (sr != null) sr.color = currentTint;
        }
    }

    // ---------- Где встать ----------

    /// <summary>
    /// Свободная клетка вплотную к цели: без врагов рядом (SpotSafeRadius), подальше от врагов и поближе к строителю.
    /// </summary>
    bool FindWarSpot(GridNavigation nav, out Vector2 spot)
    {
        spot = transform.position;
        if (nav == null) return false;
        var cand = new List<Vector2Int>();

        if (wbBuilding != null && wbBuilding.type != null)
        {
            Vector2Int o = wbBuilding.OriginCell, s = wbBuilding.type.size;
            for (int x = o.x - 1; x <= o.x + s.x; x++)
                for (int y = o.y - 1; y <= o.y + s.y; y++)
                {
                    bool edge = x == o.x - 1 || x == o.x + s.x || y == o.y - 1 || y == o.y + s.y;
                    if (edge) cand.Add(new Vector2Int(x, y));
                }
        }
        else if (wbPost != null && wbPost.Cells != null && wbPost.Cells.Count > 0)
        {
            var own = new HashSet<Vector2Int>(wbPost.Cells);
            var seen = new HashSet<Vector2Int>();
            foreach (var c in wbPost.Cells)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        Vector2Int n = new Vector2Int(c.x + dx, c.y + dy);
                        if (own.Contains(n) || !seen.Add(n)) continue;
                        cand.Add(n);
                    }
        }
        else if (wbPost != null)
        {
            Vector2Int c = nav.WorldToCell(wbPost.Position);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) cand.Add(new Vector2Int(c.x + dx, c.y + dy));
        }
        else return false;

        Vector2 me = transform.position;
        bool war = WarManager.IsWar;
        float best = float.MaxValue;
        bool found = false;
        foreach (var c in cand)
        {
            if (nav.IsBlocked(c)) continue;
            Vector2 cc = nav.CellCenter(c);
            float score = Vector2.Distance(me, cc);
            if (war)
            {
                EnemyAgent e = EnemyAgent.FindNearest(cc, 7f);
                if (e != null)
                {
                    float d = Vector2.Distance(cc, e.Position);
                    if (d < WarBuilderYard.SpotSafeRadius) continue;
                    score += (7f - d) * 3f;
                }
            }
            // Свои строители уже стоят тут - встанем рядом, а не друг на друге
            foreach (var a in All)
                if (a != null && a != this && a.wbPhase != WarBuildPhase.None && Vector2.Distance(a.wbSpot, cc) < 0.5f) score += 2f;
            if (score < best) { best = score; spot = cc + Random.insideUnitCircle * 0.12f; found = true; }
        }
        return found;
    }

    // ---------- Вид ----------

    void WarBuildLook(bool shown)
    {
        bool on = shown && wbPhase != WarBuildPhase.None;
        if (on && wbTool == null)
        {
            wbTool = MakePart("Hammer", Vector2.zero, new Vector2(0.09f, 0.26f), new Color(0.75f, 0.55f, 0.3f), 12);
        }
        if (wbTool == null) return;
        wbTool.enabled = on;
        if (!on) return;
        float swing = wbPhase == WarBuildPhase.Working ? Mathf.Sin(Time.time * 14f) * 50f : 20f;
        wbTool.transform.localPosition = new Vector3(bodyScale * 0.5f, bodyScale * 0.2f, 0f);
        wbTool.transform.localRotation = Quaternion.Euler(0f, 0f, -30f + swing);
    }

    // Для карточки жителя
    string WarBuildActivityText
    {
        get
        {
            if (wbPhase == WarBuildPhase.None) return "";
            string what = wbPost != null ? (wbPost.Built ? "чинит: " : "строит: ") + wbPost.S.name
                                         : "чинит: " + NameOf(wbBuilding);
            if (wbPhase == WarBuildPhase.ToSite)
                what = "бежит " + (wbPost != null && !wbPost.Built ? "строить: " + wbPost.S.name : "чинить: " + (wbPost != null ? wbPost.S.name : NameOf(wbBuilding)));
            return "<color=#FFA040>" + what + "</color>";
        }
    }
}
