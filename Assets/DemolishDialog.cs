using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Вопрос «Точно снести?» поверх всех окон.
/// BuildingPlacer спрашивает через DemolishDialog.Ask(...); «Снести» / Enter - подтвердить, «Отмена» / Esc - закрыть.
/// Пока окно открыто, карта не реагирует на мышь (см. BuildingPlacer.Update).
/// </summary>
public class DemolishDialog : MonoBehaviour
{
    public static DemolishDialog Instance { get; private set; }

    GameObject target;
    string caption;
    string body;
    Action onYes;
    int openedFrame;

    /// <summary>Открыт ли вопрос (и здание ещё существует).</summary>
    public static bool IsOpen => Instance != null && Instance.target != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; }

    static DemolishDialog Ensure()
    {
        if (Instance == null) new GameObject("DemolishDialog").AddComponent<DemolishDialog>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // Тревога (WarManager.RaiseAlarm): закрыть вопрос, чтобы было видно бой
    void OnEnable() { WarManager.OnAlarm += CollapseForAlarm; }
    void OnDisable() { WarManager.OnAlarm -= CollapseForAlarm; }
    bool CollapseForAlarm()
    {
        if (!IsOpen) return false;
        Close();
        return true;
    }

    public static void Ask(GameObject building, string caption, string body, Action onYes)
    {
        DemolishDialog d = Ensure();
        d.target = building;
        d.caption = caption;
        d.body = body;
        d.onYes = onYes;
        d.openedFrame = Time.frameCount;
    }

    public static void Close()
    {
        if (Instance == null) return;
        Instance.target = null;
        Instance.onYes = null;
    }

    void Confirm()
    {
        Action a = onYes;
        Close();
        if (a != null) a();
    }

    void Update()
    {
        if (target == null) { onYes = null; return; }
        if (Time.frameCount == openedFrame) return;   // та же X не должна сразу подтвердить
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame) Close();
        else if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) Confirm();
    }

    void OnGUI()
    {
        if (target == null) return;
        UiKit.Apply();
        GUI.depth = -50;   // поверх окон зданий

        // Затемнение: окно модальное
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;

        float w = 380f;
        float textH = UiKit.TextH(body, w - UiKit.Pad * 2f);
        float h = UiKit.WindowH(textH + 8f + UiKit.BtnH);
        Rect r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

        Rect c = UiKit.Window(r, caption, out bool close);
        if (close) { Close(); return; }

        GUI.Label(new Rect(c.x, c.y, c.width, textH), body, UiKit.Body);

        float by = c.y + textH + 8f;
        float bw = (c.width - 8f) * 0.5f;
        if (UiKit.Button(new Rect(c.x, by, bw, UiKit.BtnH), "Снести", UiKit.Kind.Danger, "Enter")) { Confirm(); return; }
        if (UiKit.Button(new Rect(c.x + bw + 8f, by, bw, UiKit.BtnH), "Отмена", UiKit.Kind.Normal, "Esc")) Close();

        // Клики мимо окна не доходят до окон игры под затемнением
        EventType et = Event.current.type;
        if (et == EventType.MouseDown || et == EventType.MouseUp || et == EventType.ScrollWheel) Event.current.Use();
    }
}
