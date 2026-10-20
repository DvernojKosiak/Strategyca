using UnityEngine;

/// <summary>
/// Инженерная казарма (путь Инженерии, направление «Военный инженер», Ратуша Ур.3, одна на город).
/// Здесь собирают катапульты: расчёт - один житель (обычный новобранец), плюс золото, еда, доски и детали.
/// Внутри это обычная казарма (BarracksController) в режиме инженеров: в списке только инженерные войска,
/// 8 мест за уровень. Отряды, приказы («Внутрь», «Охранять», «Послать...»), лагеря, набеги, продажа на рынке -
/// всё как у казармы. Панель тоже казарменная.
/// </summary>
public class EngineerBarracks : MonoBehaviour
{
    public const string TypeName = "Инженерная казарма";
    public const int RequiredHallLevel = 3;

    BarracksController ctrl;

    public BarracksController Controller
    {
        get
        {
            if (ctrl == null) ctrl = GetComponent<BarracksController>();
            if (ctrl == null) ctrl = gameObject.AddComponent<BarracksController>();
            return ctrl;
        }
    }

    void Awake()
    {
        // Контроллер казармы видит EngineerBarracks в своём Awake и включает режим инженеров
        if (ctrl == null) ctrl = GetComponent<BarracksController>();
        if (ctrl == null) ctrl = gameObject.AddComponent<BarracksController>();
    }

    /// <summary>Панель (вызывает BuildingPlacer). Пока выбирают точку для отряда - маленькая плашка.</summary>
    public Rect DrawPanel(float x, float y, float w)
    {
        BarracksController c = Controller;
        if (c == null) return new Rect(x, y, 0f, 0f);
        if (c.PickingTarget) return c.DrawPickBanner(x, y, Mathf.Min(w, 420f));
        return c.DrawPanel(x, y, w);
    }
}
