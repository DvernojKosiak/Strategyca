using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Аккаунт игрока: экран входа при запуске, вкладка «Аккаунт» в меню «Город» [F].
/// Создаётся сам (добавлять в сцену не нужно). Хранилище - AccountManager.Backend:
/// сейчас LocalAccountBackend (имитация на этом компьютере), потом - онлайн-сервер с тем же интерфейсом.
///
/// Для остальной игры:
///   AccountManager.LoggedIn / IsGuest / Profile - кто играет;
///   AccountManager.Blocking - открыт экран входа (карта и время стоят).
/// </summary>
public class AccountManager : MonoBehaviour
{
    public static AccountManager Instance { get; private set; }

    static IAccountBackend backend;
    /// <summary>Откуда берутся аккаунты. Подменить можно до старта сцены (или до первого входа).</summary>
    public static IAccountBackend Backend
    {
        get { if (backend == null) backend = new LocalAccountBackend(); return backend; }
        set { backend = value; }
    }

    public static AccountProfile Profile { get; private set; }
    public static bool LoggedIn => Profile != null;
    public static bool IsGuest => Instance != null && !LoggedIn && !Instance.screenOpen;
    /// <summary>Открыт экран входа: игра на паузе, карта не принимает клики.</summary>
    public static bool Blocking => Instance != null && Instance.screenOpen;

    const string TokenKey = "Strategyca.AccountToken";
    const string LastLoginKey = "Strategyca.LastLogin";
    const float AutoSaveSeconds = 60f;

    string token;
    bool screenOpen;
    bool busy;
    float savedTimeScale = 1f;
    float saveTimer;

    // Экран входа
    int tab;               // 0 - вход, 1 - регистрация
    string login = "", password = "", password2 = "", displayName = "";
    bool remember = true;
    string error = "";
    string note = "";

    // Вкладка «Аккаунт»: смена имени и пароля
    string newName = "";
    string oldPass = "", newPass = "";
    string pageMsg = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; Profile = null; backend = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("AccountManager");
        DontDestroyOnLoad(go);
        go.AddComponent<AccountManager>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        login = PlayerPrefs.GetString(LastLoginKey, "");

        // Есть сохранённая сессия - входим без пароля, иначе экран входа
        string saved = PlayerPrefs.GetString(TokenKey, "");
        if (saved != "")
        {
            Backend.Resume(saved, r =>
            {
                if (r.ok) SignedIn(r, true, false);
                else
                {
                    PlayerPrefs.DeleteKey(TokenKey);
                    note = r.error;
                    OpenScreen();
                }
            });
        }
        else OpenScreen();
    }

    void Start()
    {
        GameMenu.AddPage("Аккаунт", PageHeight, DrawPage);
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        SaveNow();
        if (screenOpen) Time.timeScale = savedTimeScale;
        Instance = null;
    }

    void OnApplicationQuit() { SaveNow(); }

    // ---------- Вход / выход ----------

    void OpenScreen()
    {
        if (screenOpen) return;
        screenOpen = true;
        savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        Time.timeScale = 0f;
        password = password2 = "";
        error = "";
    }

    void CloseScreen()
    {
        if (!screenOpen) return;
        screenOpen = false;
        Time.timeScale = savedTimeScale;
        // Пока вводили логин, буквы могли открыть окна игры (F, L, T, B…) - закрываем их
        WarManager.CollapseWindows();
    }

    void SignedIn(AccountResult r, bool rememberMe, bool fresh)
    {
        busy = false;
        Profile = r.profile;
        token = r.token;
        if (rememberMe) PlayerPrefs.SetString(TokenKey, token);
        else PlayerPrefs.DeleteKey(TokenKey);
        PlayerPrefs.SetString(LastLoginKey, Profile.login);
        PlayerPrefs.Save();
        password = password2 = "";
        error = note = "";
        newName = Profile.displayName;
        CloseScreen();
        WarManager.Notify(fresh ? $"Аккаунт создан. Добро пожаловать, {Profile.displayName}!" : $"Вы вошли как {Profile.displayName}", true);
    }

    void SubmitLogin()
    {
        if (busy) return;
        busy = true;
        error = "";
        bool rem = remember;
        Backend.Login(login, password, r =>
        {
            busy = false;
            if (r.ok) SignedIn(r, rem, false);
            else error = r.error;
        });
    }

    void SubmitRegister()
    {
        if (busy) return;
        if (password != password2) { error = "Пароли не совпадают"; return; }
        busy = true;
        error = "";
        bool rem = remember;
        Backend.Register(login, displayName, password, r =>
        {
            busy = false;
            if (r.ok) SignedIn(r, rem, true);
            else error = r.error;
        });
    }

    void PlayAsGuest()
    {
        CloseScreen();
        WarManager.Notify("Игра без аккаунта. Войти можно в меню «Город» [F] → «Аккаунт»", false);
    }

    /// <summary>Выйти из аккаунта и показать экран входа.</summary>
    public void Logout()
    {
        SaveNow();
        string t = token;
        Profile = null;
        token = null;
        PlayerPrefs.DeleteKey(TokenKey);
        PlayerPrefs.Save();
        if (!string.IsNullOrEmpty(t)) Backend.Logout(t, null);
        tab = 0;
        OpenScreen();
    }

    void SaveNow()
    {
        if (!LoggedIn || string.IsNullOrEmpty(token)) return;
        Backend.SaveProfile(token, Profile, null);
    }

    // ---------- Время ----------

    void Update()
    {
        if (screenOpen)
        {
            Time.timeScale = 0f;   // часы игры могли выставить скорость на старте
            Keyboard kb = Keyboard.current;
            if (kb != null && !busy && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
            {
                if (tab == 0) SubmitLogin(); else SubmitRegister();
            }
            return;
        }

        if (!LoggedIn) return;
        Profile.playSeconds += Time.unscaledDeltaTime;
        saveTimer += Time.unscaledDeltaTime;
        if (saveTimer >= AutoSaveSeconds) { saveTimer = 0f; SaveNow(); }
    }

    void LateUpdate()
    {
        if (screenOpen) Time.timeScale = 0f;
    }

    // ---------- Экран входа ----------

    void OnGUI()
    {
        if (!screenOpen) return;
        UiKit.Apply();
        GUI.depth = -100;   // поверх всех окон игры

        Color old = GUI.color;
        GUI.color = new Color(0.03f, 0.035f, 0.045f, 0.93f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;

        float w = 360f;
        float rows = tab == 0 ? 2 : 4;
        float contentH = UiKit.BtnH + 10f              // вкладки
            + rows * (18f + 28f + 6f)                  // подпись + поле
            + 24f                                      // «Запомнить меня»
            + 22f                                      // ошибка
            + UiKit.BtnH + 8f + UiKit.BtnH             // кнопки
            + 40f;                                     // подпись о хранилище
        float h = UiKit.WindowH(contentH);
        Rect r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        Rect c = UiKit.Window(r, "Strategyca · Аккаунт");

        float x = c.x, y = c.y, cw = c.width;
        int newTab = UiKit.Tabs(new Rect(x, y, cw + 2f, UiKit.BtnH), new[] { "Вход", "Регистрация" }, tab);
        if (newTab != tab) { tab = newTab; error = ""; }
        y += UiKit.BtnH + 10f;

        bool en = GUI.enabled;
        GUI.enabled = en && !busy;

        login = Field(ref y, x, cw, "Логин", login, false);
        if (tab == 1) displayName = Field(ref y, x, cw, "Имя в игре", displayName, false);
        password = Field(ref y, x, cw, "Пароль", password, true);
        if (tab == 1) password2 = Field(ref y, x, cw, "Повторите пароль", password2, true);

        remember = GUI.Toggle(new Rect(x, y, cw, 22f), remember, " Запомнить меня");
        y += 24f;

        string msg = error != "" ? UiKit.Col(UiKit.BadHex, error) : (note != "" ? UiKit.Col(UiKit.MutedHex, note) : "");
        GUI.Label(new Rect(x, y, cw, 22f), msg, UiKit.Small);
        y += 22f;

        string main = busy ? "Подождите…" : (tab == 0 ? "Войти" : "Создать аккаунт");
        if (UiKit.Button(new Rect(x, y, cw, UiKit.BtnH), main, UiKit.Kind.Primary, "Enter"))
        {
            if (tab == 0) SubmitLogin(); else SubmitRegister();
        }
        y += UiKit.BtnH + 8f;
        if (UiKit.Button(new Rect(x, y, cw, UiKit.BtnH), "Играть без аккаунта", UiKit.Kind.Ghost,
                "Можно войти позже: меню «Город» [F] → «Аккаунт»")) PlayAsGuest();
        y += UiKit.BtnH + 6f;

        GUI.enabled = en;
        GUI.Label(new Rect(x, y, cw, 36f), UiKit.Col(UiKit.MutedHex, Backend.Name +
            (Backend.IsOnline ? "" : ". Онлайн-сервер подключим позже, аккаунт сохранится.")), UiKit.Small);

        // Клики мимо окна не доходят до окон игры под затемнением
        EventType et = Event.current.type;
        if (et == EventType.MouseDown || et == EventType.MouseUp || et == EventType.ScrollWheel) Event.current.Use();
    }

    static string Field(ref float y, float x, float w, string label, string value, bool secret)
    {
        GUI.Label(new Rect(x, y, w, 18f), label, UiKit.MutedStyle);
        y += 18f;
        Rect fr = new Rect(x, y, w, 28f);
        string v = secret ? GUI.PasswordField(fr, value ?? "", '•', 32) : GUI.TextField(fr, value ?? "", 32);
        y += 28f + 6f;
        return v;
    }

    // ---------- Вкладка «Аккаунт» в меню «Город» ----------

    float PageHeight(float w)
    {
        if (!LoggedIn) return 22f * 3f + UiKit.BtnH + 8f;
        return 22f * 5f + 6f            // строки профиля
            + 18f + 28f + 6f + UiKit.BtnH + 8f   // имя
            + UiKit.SectionH + 2f * (18f + 28f + 6f) + UiKit.BtnH + 8f   // пароль
            + 22f + UiKit.BtnH;          // сообщение + выход
    }

    void DrawPage(Rect r)
    {
        float x = r.x, y = r.y, w = r.width;

        if (!LoggedIn)
        {
            GUI.Label(new Rect(x, y, w, 44f), "Вы играете без аккаунта. С аккаунтом игра будет знать вас, когда станет онлайн.", UiKit.Body);
            y += 66f;
            if (UiKit.Button(new Rect(x, y, w, UiKit.BtnH), "Войти или создать аккаунт", UiKit.Kind.Primary))
            {
                tab = 0;
                OpenScreen();
            }
            return;
        }

        UiKit.Row(new Rect(x, y, w, 22f), "Игрок", UiKit.B(Profile.displayName)); y += 22f;
        UiKit.Row(new Rect(x, y, w, 22f), "Логин", Profile.login); y += 22f;
        UiKit.Row(new Rect(x, y, w, 22f), "Создан", Profile.Created == System.DateTime.MinValue ? "—" : Profile.Created.ToLocalTime().ToString("dd.MM.yyyy")); y += 22f;
        int mins = Mathf.FloorToInt(Profile.playSeconds / 60f);
        UiKit.Row(new Rect(x, y, w, 22f), "В игре", mins >= 60 ? $"{mins / 60} ч {mins % 60} мин" : $"{mins} мин"); y += 22f;
        UiKit.Row(new Rect(x, y, w, 22f), "Где хранится", Backend.IsOnline ? "сервер" : "этот компьютер"); y += 22f + 6f;

        newName = Field(ref y, x, w, "Имя в игре", newName, false);
        bool nameChanged = newName.Trim() != Profile.displayName && LocalAccountBackend.CheckName(newName) == null;
        if (UiKit.Button(new Rect(x, y, w, UiKit.BtnH), "Сохранить имя", nameChanged, UiKit.Kind.Normal,
                nameChanged ? null : LocalAccountBackend.CheckName(newName) ?? "Имя не изменилось"))
        {
            Profile.displayName = newName.Trim();
            Backend.SaveProfile(token, Profile, ok => pageMsg = ok ? UiKit.Col(UiKit.GoodHex, "Имя сохранено") : UiKit.Col(UiKit.BadHex, "Не удалось сохранить"));
        }
        y += UiKit.BtnH + 8f;

        y = UiKit.Section(x, y, w, "Пароль");
        oldPass = Field(ref y, x, w, "Старый пароль", oldPass, true);
        newPass = Field(ref y, x, w, "Новый пароль", newPass, true);
        if (UiKit.Button(new Rect(x, y, w, UiKit.BtnH), "Сменить пароль", oldPass != "" && newPass != "", UiKit.Kind.Normal, null))
        {
            Backend.ChangePassword(token, oldPass, newPass, res =>
            {
                pageMsg = res.ok ? UiKit.Col(UiKit.GoodHex, "Пароль изменён") : UiKit.Col(UiKit.BadHex, res.error);
                if (res.ok) oldPass = newPass = "";
            });
        }
        y += UiKit.BtnH + 8f;

        GUI.Label(new Rect(x, y, w, 22f), pageMsg, UiKit.Small);
        y += 22f;
        if (UiKit.Button(new Rect(x, y, w, UiKit.BtnH), "Выйти из аккаунта", UiKit.Kind.Danger, "Откроется экран входа")) Logout();
    }
}
