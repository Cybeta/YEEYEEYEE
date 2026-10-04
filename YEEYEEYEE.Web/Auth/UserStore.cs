using System.Globalization;
using Microsoft.Data.Sqlite;

namespace YEEYEEYEE.Web.Auth;

/// <summary>
/// 用户与会话的存放处（SQLite，一个文件，跟任务库分开）。
///
/// 为什么是 SQLite 而不是 JSON 文件：**这里会有并发的写**（登录要写 last_login、写会话，
/// 管理员改角色又要写），JSON 文件那种「读全文→改→整份写回」的做法在这种场景下会丢更新。
/// 而且这个库要跟任务库一样能在容器里挂一个卷持久化。
///
/// 三条刻意的取舍：
/// · **第一个用户必须是管理员**，且这件事靠 `BEGIN IMMEDIATE` 事务保证：
///   两个请求同时到达时，只有一个能建成管理员，另一个拿到「已经建过了」。
///   用普通的延迟事务会两边都读到 0 个用户，然后都插进去。
/// · **最后一个管理员不能降级、不能被停用、不能删**。否则这就是一个把自己锁在门外的开关。
/// · 会话在库里只存**令牌的哈希**：库被看到也换不出可用的登录态。
/// </summary>
internal sealed class UserStore(string databasePath)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>口令的最短长度等规则与哈希器共用一处。</summary>
    public static string? CheckPassword(string? password) => PasswordHasher.Check(password);

    /// <summary>用户名校验。允许字母数字与 . _ -，长度 3–32；不允许空白与其它符号。</summary>
    public static string? CheckUsername(string? username)
    {
        var value = (username ?? "").Trim();
        if (value.Length < 3) return "用户名至少 3 个字符";
        if (value.Length > 32) return "用户名最多 32 个字符";
        if (!value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            return "用户名只能用字母、数字与 . _ -";
        return null;
    }

    private SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>建表。每次开库都跑，全是 IF NOT EXISTS，重复调用没有副作用。</summary>
    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                username_key TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_login_at TEXT NULL,
                disabled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                user_agent TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS sessions_user ON sessions(user_id);
            """;
        command.ExecuteNonQuery();
    }

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseStamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static WebUser Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        UserRoles.Parse(reader.GetString(3)),
        ParseStamp(reader.GetString(4)),
        reader.IsDBNull(5) ? null : ParseStamp(reader.GetString(5)),
        reader.GetInt64(6) != 0);

    private const string Columns = "id, username, display_name, role, created_at, last_login_at, disabled";

    public int CountUsers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 建**第一个**用户，角色固定管理员。库里已经有用户时返回 <see cref="UserWriteStatus.SetupAlreadyDone"/>。
    ///
    /// 用 `BEGIN IMMEDIATE`：它一上来就拿写锁，所以「数到 0」和「插入」之间不会有第二个请求插进来。
    /// 这是整套流程里唯一一处必须防的竞态——两个请求同时到，只能有一个成为管理员。
    /// </summary>
    public UserWriteResult CreateFirstAdmin(string username, string password, string? displayName = null)
    {
        if (CheckUsername(username) is { } usernameError) return UserWriteResult.Fail(UserWriteStatus.InvalidUsername, usernameError);
        if (CheckPassword(password) is { } passwordError) return UserWriteResult.Fail(UserWriteStatus.WeakPassword, passwordError);

        var value = username.Trim();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM users";
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                return UserWriteResult.Fail(UserWriteStatus.SetupAlreadyDone, "管理员已经建过了，请直接登录");
        }
        var user = Insert(connection, transaction, value, password, UserRole.Admin, displayName);
        if (user is null) return UserWriteResult.Fail(UserWriteStatus.UsernameTaken, "用户名已被占用");
        transaction.Commit();
        return UserWriteResult.Ok(user);
    }

    /// <summary>管理员建号。角色由调用方给定；这里不检查最后管理员——新建不会减少管理员。</summary>
    public UserWriteResult CreateUser(string username, string password, UserRole role, string? displayName = null)
    {
        if (CheckUsername(username) is { } usernameError) return UserWriteResult.Fail(UserWriteStatus.InvalidUsername, usernameError);
        if (CheckPassword(password) is { } passwordError) return UserWriteResult.Fail(UserWriteStatus.WeakPassword, passwordError);

        using var connection = Open();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var user = Insert(connection, transaction, username.Trim(), password, role, displayName);
        if (user is null) return UserWriteResult.Fail(UserWriteStatus.UsernameTaken, "用户名已被占用");
        transaction.Commit();
        return UserWriteResult.Ok(user);
    }

    private static WebUser? Insert(SqliteConnection connection, SqliteTransaction transaction, string username, string password, UserRole role, string? displayName)
    {
        var user = new WebUser(Guid.NewGuid(), username, string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            role, DateTimeOffset.UtcNow, null, false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users (id, username, username_key, display_name, password_hash, role, created_at, last_login_at, disabled)
            VALUES ($id, $username, $key, $display, $hash, $role, $created, NULL, 0)
            """;
        command.Parameters.AddWithValue("$id", user.Id.ToString());
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$key", user.Username.ToLowerInvariant());
        command.Parameters.AddWithValue("$display", user.DisplayName);
        command.Parameters.AddWithValue("$hash", PasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$role", UserRoles.ToText(role));
        command.Parameters.AddWithValue("$created", Stamp(user.CreatedAt));
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // UNIQUE 冲突：用户名重复。这里连事务一起回退，否则后面的写会挂在半途。
            transaction.Rollback();
            return null;
        }
        return user;
    }

    public WebUser? Find(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<WebUser> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users ORDER BY created_at ASC";
        using var reader = command.ExecuteReader();
        var users = new List<WebUser>();
        while (reader.Read()) users.Add(Read(reader));
        return users;
    }

    /// <summary>登录。**用户名与口令错都用同一个结果**，不区分「用户不存在」与「口令不对」——区别对待等于告诉对方哪些用户名存在。</summary>
    public (UserWriteStatus Status, WebUser? User, string? Error) Authenticate(string username, string password)
    {
        using var connection = Open();
        string? hash = null;
        WebUser? user = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {Columns}, password_hash FROM users WHERE username_key = $key";
            command.Parameters.AddWithValue("$key", (username ?? "").Trim().ToLowerInvariant());
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                user = Read(reader);
                hash = reader.GetString(7);
            }
        }

        // 口令错、用户不存在，都先跑一次哈希校验再给结论：不这样做的话，
        // 「用户不存在」会立刻返回，响应时间明显更短，等于把用户名是否存在暴露出去。
        var verified = PasswordHasher.Verify(password, hash ?? Dummy);
        if (user is null || !verified)
            return (UserWriteStatus.InvalidCredentials, null, "用户名或口令不正确");
        if (user.Disabled)
            return (UserWriteStatus.AccountDisabled, null, "这个账号已被停用");
        return (UserWriteStatus.Ok, user, null);
    }

    /// <summary>给「用户不存在」这条路径用的一份固定哈希，只为消耗掉同样的计算时间。</summary>
    private static readonly string Dummy = PasswordHasher.Hash("yeeeyee-timing-equaliser");

    public void MarkLogin(Guid userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET last_login_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", userId.ToString());
        command.ExecuteNonQuery();
    }

    public UserWriteResult SetRole(Guid id, UserRole role)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var current = FindIn(connection, transaction, id);
        if (current is null) return UserWriteResult.Fail(UserWriteStatus.NotFound, "用户不存在");
        if (current.Role == UserRole.Admin && role != UserRole.Admin && AdminCount(connection, transaction, excludeId: id) == 0)
            return UserWriteResult.Fail(UserWriteStatus.LastAdmin, "这是唯一的管理员，不能降级；先把别人升为管理员");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE users SET role = $role WHERE id = $id";
            command.Parameters.AddWithValue("$role", UserRoles.ToText(role));
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return UserWriteResult.Ok(current with { Role = role });
    }

    public UserWriteResult SetDisabled(Guid id, bool disabled)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var current = FindIn(connection, transaction, id);
        if (current is null) return UserWriteResult.Fail(UserWriteStatus.NotFound, "用户不存在");
        if (disabled && current.Role == UserRole.Admin && AdminCount(connection, transaction, excludeId: id) == 0)
            return UserWriteResult.Fail(UserWriteStatus.LastAdmin, "这是唯一的管理员，不能停用");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE users SET disabled = $disabled WHERE id = $id";
            command.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        // 停用一个账号的同时把它的会话清掉：否则它手里的 cookie 还能用满 14 天。
        if (disabled) RevokeSessionsFor(id);
        return UserWriteResult.Ok(current with { Disabled = disabled });
    }

    public UserWriteResult Delete(Guid id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var current = FindIn(connection, transaction, id);
        if (current is null) return UserWriteResult.Fail(UserWriteStatus.NotFound, "用户不存在");
        if (current.Role == UserRole.Admin && AdminCount(connection, transaction, excludeId: id) == 0)
            return UserWriteResult.Fail(UserWriteStatus.LastAdmin, "这是唯一的管理员，不能删除");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM users WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        RevokeSessionsFor(id);
        return UserWriteResult.Ok(current);
    }

    private static WebUser? FindIn(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static int AdminCount(SqliteConnection connection, SqliteTransaction transaction, Guid excludeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM users WHERE role = $admin AND disabled = 0 AND id <> $id";
        command.Parameters.AddWithValue("$admin", UserRoles.Admin);
        command.Parameters.AddWithValue("$id", excludeId.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>管理员重置别人的口令。重置后**把那个人的会话全部作废**——改口令的意义就包括踢掉旧登录态。</summary>
    public UserWriteResult ResetPassword(Guid id, string password)
    {
        if (CheckPassword(password) is { } error) return UserWriteResult.Fail(UserWriteStatus.WeakPassword, error);
        using var connection = Open();
        var current = Find(id);
        if (current is null) return UserWriteResult.Fail(UserWriteStatus.NotFound, "用户不存在");
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id";
        command.Parameters.AddWithValue("$hash", PasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
        RevokeSessionsFor(id);
        return UserWriteResult.Ok(current);
    }

    /// <summary>自己改自己的口令。**必须先验旧口令**，否则一个偷到 cookie 的人就能直接换掉口令、把主人锁在外面。</summary>
    public UserWriteResult ChangePassword(Guid id, string current, string next)
    {
        if (CheckPassword(next) is { } error) return UserWriteResult.Fail(UserWriteStatus.WeakPassword, error);
        using var connection = Open();
        string? hash = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT password_hash FROM users WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            var value = command.ExecuteScalar();
            hash = value as string;
        }
        if (hash is null) return UserWriteResult.Fail(UserWriteStatus.NotFound, "用户不存在");
        if (!PasswordHasher.Verify(current, hash)) return UserWriteResult.Fail(UserWriteStatus.WrongPassword, "当前口令不正确");

        var user = Find(id)!;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id";
            command.Parameters.AddWithValue("$hash", PasswordHasher.Hash(next));
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        RevokeSessionsFor(id);
        return UserWriteResult.Ok(user);
    }

    // ---------- 会话 ----------

    public (string Token, DateTimeOffset ExpiresAt) CreateSession(Guid userId, string? userAgent)
    {
        var token = PasswordHasher.NewToken();
        var now = DateTimeOffset.UtcNow;
        var expires = now + SessionLifetime;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, user_id, token_hash, created_at, expires_at, last_seen_at, user_agent)
            VALUES ($id, $user, $hash, $created, $expires, $now, $agent)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$hash", PasswordHasher.HashToken(token));
        command.Parameters.AddWithValue("$created", Stamp(now));
        command.Parameters.AddWithValue("$expires", Stamp(expires));
        command.Parameters.AddWithValue("$now", Stamp(now));
        command.Parameters.AddWithValue("$agent", (object?)Truncate(userAgent, 200) ?? DBNull.Value);
        command.ExecuteNonQuery();
        return (token, expires);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>
    /// 用令牌换用户。过期的会话当作不存在，并且**顺手删掉**；被停用的用户同样换不出身份。
    /// 剩余有效期不足一半时自动续期——用着的人不该被踢，闲着 14 天的会话该消失。
    /// </summary>
    public WebUser? ResolveSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var now = DateTimeOffset.UtcNow;
        using var connection = Open();
        Guid userId;
        DateTimeOffset expires;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT user_id, expires_at FROM sessions WHERE token_hash = $hash";
            command.Parameters.AddWithValue("$hash", PasswordHasher.HashToken(token));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            userId = Guid.Parse(reader.GetString(0));
            expires = ParseStamp(reader.GetString(1));
        }
        if (expires <= now)
        {
            RevokeSession(token);
            return null;
        }

        var user = Find(userId);
        if (user is null || user.Disabled)
        {
            if (user is null) RevokeSessionsFor(userId);
            return null;
        }

        using var touch = connection.CreateCommand();
        if (expires - now < SessionLifetime / 2)
        {
            touch.CommandText = "UPDATE sessions SET last_seen_at = $now, expires_at = $expires WHERE token_hash = $hash";
            touch.Parameters.AddWithValue("$expires", Stamp(now + SessionLifetime));
        }
        else
        {
            touch.CommandText = "UPDATE sessions SET last_seen_at = $now WHERE token_hash = $hash";
        }
        touch.Parameters.AddWithValue("$now", Stamp(now));
        touch.Parameters.AddWithValue("$hash", PasswordHasher.HashToken(token));
        touch.ExecuteNonQuery();
        return user;
    }

    public void RevokeSession(string token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", PasswordHasher.HashToken(token));
        command.ExecuteNonQuery();
    }

    public void RevokeSessionsFor(Guid userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>清掉过期会话。登录时顺手跑一次，不做成后台任务——一个只写几十行的表不值得再起一个服务。</summary>
    public int PurgeExpiredSessions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE expires_at <= $now";
        command.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 最近活跃过的人（会话表的 <c>last_seen_at</c> 在 <paramref name="since"/> 之后）。
    ///
    /// 这是「在线」的第二条依据，专门给**没有订阅 SSE 的桌面端**、或**网页端刚断线**时兜底：
    /// 活连接才是「此刻在线」的主依据，这条只是一个「刚还在」的痕迹，所以调用方必须把两种依据分开标。
    ///
    /// 同一个人可能有多条会话（多标签、多台机器），按用户聚合取最晚的那次；被停用的账号不算在线。
    /// 客户端类型不在这里：会话表只存 User-Agent，认不出 wire 上的 web/desktop，宁可不标也不猜。
    /// </summary>
    public IReadOnlyList<SessionPresence> RecentSessions(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.username, u.display_name, MAX(s.last_seen_at) AS seen
            FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.last_seen_at >= $since AND s.expires_at > $now AND u.disabled = 0
            GROUP BY u.id, u.username, u.display_name
            ORDER BY seen DESC
            """;
        command.Parameters.AddWithValue("$since", Stamp(since));
        command.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
        var results = new List<SessionPresence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var username = reader.GetString(1);
            var displayName = reader.GetString(2);
            results.Add(new SessionPresence(
                Guid.Parse(reader.GetString(0)),
                string.IsNullOrWhiteSpace(displayName) ? username : displayName,
                ParseStamp(reader.GetString(3))));
        }
        return results;
    }
}

/// <summary>会话表里「最近活跃过」的一个人：给在线名单的第二条依据用（见 <see cref="UserStore.RecentSessions"/>）。</summary>
public sealed record SessionPresence(Guid UserId, string DisplayName, DateTimeOffset LastSeenAt);
