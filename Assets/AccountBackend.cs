using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// ---------- Аккаунты: данные и «сервер» ----------
// Сейчас сервера нет: LocalAccountBackend хранит аккаунты в файле на этом компьютере (имитация).
// Когда появится онлайн-база, достаточно написать класс с тем же интерфейсом IAccountBackend
// (запросы через UnityWebRequest, ответ - в callback) и подставить его до старта:
//     AccountManager.Backend = new MyOnlineBackend("https://...");
// Остальная игра работает только с AccountManager и AccountProfile и ничего не заметит.

/// <summary>Профиль игрока - то, что игра знает об аккаунте.</summary>
[Serializable]
public class AccountProfile
{
    public string id;            // постоянный id аккаунта (на сервере - его id в базе)
    public string login;         // для входа, без учёта регистра
    public string displayName;   // имя в игре
    public string createdUtc;    // дата создания (ISO 8601)
    public string lastLoginUtc;
    public int logins;           // сколько раз входили
    public float playSeconds;    // сколько времени провели в игре

    public DateTime Created => ParseDate(createdUtc);
    public DateTime LastLogin => ParseDate(lastLoginUtc);

    static DateTime ParseDate(string s)
    {
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MinValue;
    }

    public AccountProfile Copy() { return (AccountProfile)MemberwiseClone(); }
}

/// <summary>Ответ «сервера»: успех, профиль и токен сессии, либо текст ошибки для игрока.</summary>
public class AccountResult
{
    public bool ok;
    public string error;
    public AccountProfile profile;
    public string token;   // токен сессии: по нему входят без пароля («Запомнить меня»)

    public static AccountResult Fail(string error) { return new AccountResult { ok = false, error = error }; }
    public static AccountResult Ok(AccountProfile p, string token) { return new AccountResult { ok = true, profile = p, token = token }; }
}

/// <summary>
/// Всё общение с хранилищем аккаунтов. Все методы асинхронные (ответ в callback),
/// чтобы онлайн-версия могла ждать сервер, не меняя код игры.
/// </summary>
public interface IAccountBackend
{
    /// <summary>Подпись для игрока: где хранятся аккаунты.</summary>
    string Name { get; }
    bool IsOnline { get; }

    void Register(string login, string displayName, string password, Action<AccountResult> done);
    void Login(string login, string password, Action<AccountResult> done);
    /// <summary>Вход по сохранённому токену (без пароля).</summary>
    void Resume(string token, Action<AccountResult> done);
    void Logout(string token, Action done);
    /// <summary>Сохранить изменения профиля (имя, время в игре).</summary>
    void SaveProfile(string token, AccountProfile profile, Action<bool> done);
    void ChangePassword(string token, string oldPassword, string newPassword, Action<AccountResult> done);
}

/// <summary>
/// Имитация сервера: аккаунты в файле accounts.json в Application.persistentDataPath.
/// Пароли не хранятся - только соль и хеш SHA-256 (на настоящем сервере это делает сервер).
/// </summary>
public class LocalAccountBackend : IAccountBackend
{
    public const int MinLogin = 3, MaxLogin = 16, MinPassword = 4, MaxName = 20;
    const float SessionDays = 30f;

    [Serializable]
    class StoredAccount
    {
        public AccountProfile profile;
        public string salt;
        public string hash;
    }

    [Serializable]
    class StoredSession
    {
        public string token;
        public string accountId;
        public string expiresUtc;
    }

    [Serializable]
    class Db
    {
        public int version = 1;
        public List<StoredAccount> accounts = new List<StoredAccount>();
        public List<StoredSession> sessions = new List<StoredSession>();
    }

    readonly string path;
    Db db;

    public LocalAccountBackend(string fileName = "accounts.json")
    {
        path = Path.Combine(Application.persistentDataPath, fileName);
    }

    public string Name => "Локальный аккаунт (на этом компьютере)";
    public bool IsOnline => false;

    // ---------- Файл ----------

    Db Data
    {
        get
        {
            if (db != null) return db;
            try
            {
                if (File.Exists(path)) db = JsonUtility.FromJson<Db>(File.ReadAllText(path));
            }
            catch (Exception e) { Debug.LogWarning("[Аккаунт] Не удалось прочитать " + path + ": " + e.Message); }
            if (db == null) db = new Db();
            if (db.accounts == null) db.accounts = new List<StoredAccount>();
            if (db.sessions == null) db.sessions = new List<StoredSession>();
            return db;
        }
    }

    bool Save()
    {
        try
        {
            File.WriteAllText(path, JsonUtility.ToJson(Data, true));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Аккаунт] Не удалось сохранить " + path + ": " + e.Message);
            return false;
        }
    }

    // ---------- Проверки ----------

    public static string CheckLogin(string login)
    {
        login = (login ?? "").Trim();
        if (login.Length < MinLogin) return $"Логин - от {MinLogin} символов";
        if (login.Length > MaxLogin) return $"Логин - не длиннее {MaxLogin} символов";
        foreach (char ch in login)
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-') return "В логине только буквы, цифры, _ и -";
        return null;
    }

    public static string CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPassword) return $"Пароль - от {MinPassword} символов";
        return null;
    }

    public static string CheckName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Укажите имя в игре";
        if (name.Length > MaxName) return $"Имя - не длиннее {MaxName} символов";
        return null;
    }

    static string Key(string login) { return (login ?? "").Trim().ToLowerInvariant(); }
    static string Now() { return DateTime.UtcNow.ToString("o"); }

    static string NewSalt()
    {
        byte[] b = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return Convert.ToBase64String(b);
    }

    static string Hash(string salt, string password)
    {
        using (var sha = SHA256.Create())
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + password)));
    }

    StoredAccount FindByLogin(string login)
    {
        string k = Key(login);
        return Data.accounts.Find(a => a.profile != null && Key(a.profile.login) == k);
    }

    StoredAccount FindById(string id)
    {
        return Data.accounts.Find(a => a.profile != null && a.profile.id == id);
    }

    StoredAccount FindByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        Data.sessions.RemoveAll(s => s == null || ParseUtc(s.expiresUtc) < DateTime.UtcNow);
        StoredSession ss = Data.sessions.Find(s => s.token == token);
        return ss != null ? FindById(ss.accountId) : null;
    }

    static DateTime ParseUtc(string s)
    {
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d) ? d.ToUniversalTime() : DateTime.MinValue;
    }

    string OpenSession(StoredAccount a)
    {
        string token = Guid.NewGuid().ToString("N");
        Data.sessions.Add(new StoredSession { token = token, accountId = a.profile.id, expiresUtc = DateTime.UtcNow.AddDays(SessionDays).ToString("o") });
        a.profile.logins++;
        a.profile.lastLoginUtc = Now();
        Save();
        return token;
    }

    // ---------- IAccountBackend ----------

    public void Register(string login, string displayName, string password, Action<AccountResult> done)
    {
        string err = CheckLogin(login) ?? CheckName(displayName) ?? CheckPassword(password);
        if (err != null) { done(AccountResult.Fail(err)); return; }
        if (FindByLogin(login) != null) { done(AccountResult.Fail("Такой логин уже занят")); return; }

        string salt = NewSalt();
        var a = new StoredAccount
        {
            profile = new AccountProfile
            {
                id = Guid.NewGuid().ToString("N"),
                login = login.Trim(),
                displayName = displayName.Trim(),
                createdUtc = Now(),
            },
            salt = salt,
            hash = Hash(salt, password),
        };
        Data.accounts.Add(a);
        string token = OpenSession(a);
        done(AccountResult.Ok(a.profile.Copy(), token));
    }

    public void Login(string login, string password, Action<AccountResult> done)
    {
        StoredAccount a = FindByLogin(login);
        if (a == null || a.hash != Hash(a.salt, password ?? "")) { done(AccountResult.Fail("Неверный логин или пароль")); return; }
        string token = OpenSession(a);
        done(AccountResult.Ok(a.profile.Copy(), token));
    }

    public void Resume(string token, Action<AccountResult> done)
    {
        StoredAccount a = FindByToken(token);
        if (a == null) { done(AccountResult.Fail("Сессия устарела, войдите заново")); return; }
        a.profile.logins++;
        a.profile.lastLoginUtc = Now();
        Save();
        done(AccountResult.Ok(a.profile.Copy(), token));
    }

    public void Logout(string token, Action done)
    {
        Data.sessions.RemoveAll(s => s == null || s.token == token);
        Save();
        if (done != null) done();
    }

    public void SaveProfile(string token, AccountProfile profile, Action<bool> done)
    {
        StoredAccount a = FindByToken(token);
        if (a == null || profile == null || a.profile.id != profile.id) { if (done != null) done(false); return; }
        // Логин, id и даты меняет только «сервер»; игра присылает имя и время в игре
        if (CheckName(profile.displayName) == null) a.profile.displayName = profile.displayName.Trim();
        a.profile.playSeconds = Mathf.Max(a.profile.playSeconds, profile.playSeconds);
        bool ok = Save();
        if (done != null) done(ok);
    }

    public void ChangePassword(string token, string oldPassword, string newPassword, Action<AccountResult> done)
    {
        StoredAccount a = FindByToken(token);
        if (a == null) { done(AccountResult.Fail("Сессия устарела, войдите заново")); return; }
        if (a.hash != Hash(a.salt, oldPassword ?? "")) { done(AccountResult.Fail("Старый пароль неверный")); return; }
        string err = CheckPassword(newPassword);
        if (err != null) { done(AccountResult.Fail(err)); return; }
        a.salt = NewSalt();
        a.hash = Hash(a.salt, newPassword);
        // Остальные сессии закрываются, текущая остаётся
        Data.sessions.RemoveAll(s => s == null || (s.accountId == a.profile.id && s.token != token));
        Save();
        done(AccountResult.Ok(a.profile.Copy(), token));
    }
}
