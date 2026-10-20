using UnityEngine;
using UnityEngine.InputSystem;

public class CameraController : MonoBehaviour
{
    [Header("Карта")]
    [Tooltip("Если оставить 0, размер возьмется автоматически из BuildingPlacer")]
    public int mapSize = 0;        
    public float edgeMargin = 2f;   // Сколько пустого места видно за краем карты

    [Header("Зум")]
    public bool autoCalculateZoom = true; // Автоматически рассчитывать зум под размер карты
    public float startZoom = 15f;
    public float minZoom = 5f;      // Максимальное приближение
    public float maxZoom = 25f;     // Максимальное отдаление
    public float zoomStep = 2.5f;   // На сколько меняется зум за один щелчок
    public float zoomSmooth = 12f;  // Скорость приближения/отдаления

    private Camera cam;
    private float targetZoom;
    private Vector3 dragOrigin;
    private BuildingPlacer placerRef;

    public static CameraController Instance { get; private set; }

    // Камера может смотреть не на нашу карту, а на другую область (поле боя набега)
    static bool areaOverride;
    static Vector2 areaCenter;
    static int areaSize;

    public static void SetViewArea(Vector2 center, int size)
    {
        areaOverride = true;
        areaCenter = center;
        areaSize = Mathf.Max(10, size);
    }

    public static void ClearViewArea() { areaOverride = false; }

    int ViewSize => areaOverride ? areaSize : mapSize;
    Vector2 ViewCenter => areaOverride ? areaCenter : Vector2.zero;

    void Awake() { Instance = this; }

    static int zoomBlockedFrame = -10;
    public static void BlockZoom() { zoomBlockedFrame = Time.frameCount; }
    static bool ZoomBlocked => Time.frameCount - zoomBlockedFrame <= 1;

    public void FocusOn(Vector2 p)
    {
        transform.position = new Vector3(p.x, p.y, -10f);
    }

    void Start()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;

        placerRef = FindObjectOfType<BuildingPlacer>();
        UpdateMapSize();
        UpdateZoomLimits();

        transform.position = new Vector3(0f, 0f, -10f);
        cam.orthographicSize = startZoom;
        targetZoom = startZoom;
    }

    void Update()
    {
        if (Mouse.current == null) return;

        UpdateMapSize();

        if (autoCalculateZoom)
        {
            UpdateZoomLimits();
        }

        HandleZoom();
        HandleDrag();
        ClampToMap();
    }

    void UpdateMapSize()
    {
        if (placerRef == null)
        {
            placerRef = FindObjectOfType<BuildingPlacer>();
        }

        if (placerRef != null && mapSize != placerRef.mapSize)
        {
            mapSize = placerRef.mapSize;
        }
        else if (mapSize <= 0)
        {
            mapSize = 50; // Защита по умолчанию
        }
    }

    void UpdateZoomLimits()
    {
        if (!autoCalculateZoom) return;

        maxZoom = (ViewSize / 2f) + edgeMargin;
        startZoom = Mathf.Clamp(ViewSize * 0.3f, minZoom, maxZoom);
        targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
    }

    void HandleZoom()
    {
        float scroll = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f && !ZoomBlocked)
        {
            targetZoom -= Mathf.Sign(scroll) * zoomStep;
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        }

        if (Mathf.Abs(cam.orthographicSize - targetZoom) < 0.001f) return;

        Vector2 mouse = Mouse.current.position.ReadValue();
        Vector3 before = cam.ScreenToWorldPoint(mouse);

        cam.orthographicSize = Mathf.Lerp(
            cam.orthographicSize, targetZoom, 1f - Mathf.Exp(-zoomSmooth * Time.unscaledDeltaTime));

        Vector3 after = cam.ScreenToWorldPoint(mouse);
        transform.position += new Vector3(before.x - after.x, before.y - after.y, 0f);
    }

    void HandleDrag()
    {
        bool pressed = Mouse.current.rightButton.wasPressedThisFrame
                    || Mouse.current.middleButton.wasPressedThisFrame;
        bool held = Mouse.current.rightButton.isPressed
                 || Mouse.current.middleButton.isPressed;

        if (pressed)
        {
            dragOrigin = cam.ScreenToWorldPoint(Mouse.current.position.ReadValue());
        }

        if (held)
        {
            Vector3 current = cam.ScreenToWorldPoint(Mouse.current.position.ReadValue());
            Vector3 diff = dragOrigin - current;
            transform.position += new Vector3(diff.x, diff.y, 0f);
        }
    }

    void ClampToMap()
    {
        float limit = ViewSize / 2f + edgeMargin;
        Vector2 c = ViewCenter;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        float maxX = Mathf.Max(0f, limit - halfW);
        float maxY = Mathf.Max(0f, limit - halfH);

        Vector3 p = transform.position;
        p.x = Mathf.Clamp(p.x, c.x - maxX, c.x + maxX);
        p.y = Mathf.Clamp(p.y, c.y - maxY, c.y + maxY);
        transform.position = p;
    }
}