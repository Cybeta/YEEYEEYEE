using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 账号相关的 HTTP 接口。**这些路由不在 <see cref="WebAccessGuard"/> 的守卫范围内**
/// （守卫只管 /api/web、/api/canvas、/ws/canvas），所以每个需要身份的接口都自己判一次——
/// 登录与首次建号必须能匿名访问，其余一律要会话。
///
/// 错误一律走既有契约 <c>{ code, message }</c>，与 Web 端其它接口一致。
/// </summary>
internal static class AuthApi
{
    public sealed record SetupRequest(string? Username, string? Password, string? DisplayName);
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);
    public sealed record NewUserRequest(string? Username, string? Password, string? DisplayName, string? Role);
    public sealed record UserPatchRequest(string? Role, bool? Disabled);

    /// <summary>
    /// 登录失败的次数统计。**只在内存里**，进程重启就清空——这是有意的：
    /// 记到库里等于让攻击者能用「猜错」把库撑大。它挡的是脚本化的连续尝试，不是审计。
    /// 键里带上用户名与来源地址，免得一个人猜错把别人也一起锁住。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Attempt> Attempts = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    private const int MaxFailures = 8;

    private sealed record Attempt(int Failures, DateTimeOffset Until);

    public static void Map(WebApplication app, UserStore users)
    {
        app.MapGet("/api/auth/state", (HttpContext context) =>
        {
            var current = WebAccessGuard.CurrentUser(context);
            return Results.Json(new
            {
                initialized = users.CountUsers() > 0,
                setupTokenRequired = SetupToken(app) is not null,
                passwordMinLength = PasswordHasher.MinLength,
                user = current is null ? null : Describe(current)
            });
        });

        // 第一个用户 = 管理员。库里已有用户时直接拒绝，不留「再建一个管理员」这种后门。
        app.MapPost("/api/auth/setup", (SetupRequest body, HttpContext context) =>
        {
            var required = SetupToken(app);
            if (required is not null && !FixedMatch(context.Request.Headers["X-Setup-Token"].ToString(), required))
                return Task.FromResult(Results.Json(new
                {
                    code = "SETUP_TOKEN_REQUIRED",
                    message = "首次建号需要部署时配置的初始化令牌（请求头 X-Setup-Token）"
                }, statusCode: StatusCodes.Status403Forbidden));

            var result = users.CreateFirstAdmin(body.Username ?? "", body.Password ?? "", body.DisplayName);
            if (result.Status != UserWriteStatus.Ok || result.User is null)
                return Task.FromResult(FromUserStatus(result.Status, result.Error));
            return Task.FromResult(SignedIn(users, context, result.User));
        });

        app.MapPost("/api/auth/login", (LoginRequest body, HttpContext context) =>
        {
            var key = AttemptKey(context, body.Username);
            if (Attempts.TryGetValue(key, out var attempt) && attempt.Until > DateTimeOffset.UtcNow)
            {
                var minutes = Math.Max(1, Math.Ceiling((attempt.Until - DateTimeOffset.UtcNow).TotalMinutes));
                return Task.FromResult(Results.Json(new
                {
                    code = "TOO_MANY_ATTEMPTS",
                    message = $"登录失败次数过多，请 {minutes} 分钟后再试"
                }, statusCode: StatusCodes.Status429TooManyRequests));
            }

            var (status, user, error) = users.Authenticate(body.Username ?? "", body.Password ?? "");
            if (status != UserWriteStatus.Ok || user is null)
            {
                var disabled = status == UserWriteStatus.AccountDisabled;
                if (!disabled)
                {
                    var failures = Attempts.TryGetValue(key, out var previous) ? previous.Failures + 1 : 1;
                    Attempts[key] = failures >= MaxFailures
                        ? new Attempt(0, DateTimeOffset.UtcNow + Lockout)
                        : new Attempt(failures, DateTimeOffset.MinValue);
                }
                return Task.FromResult(Results.Json(
                    new { code = disabled ? "ACCOUNT_DISABLED" : "INVALID_CREDENTIALS", message = error ?? "用户名或口令不正确" },
                    statusCode: disabled ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized));
            }

            Attempts.TryRemove(key, out _);
            users.MarkLogin(user.Id);
            users.PurgeExpiredSessions();
            return Task.FromResult(SignedIn(users, context, user));
        });

        app.MapPost("/api/auth/logout", (HttpContext context) =>
        {
            var token = WebAccessGuard.Token(context);
            if (!string.IsNullOrWhiteSpace(token)) users.RevokeSession(token);
            WebAccessGuard.ClearCookie(context);
            return Results.Json(new { status = "ok" });
        });

        app.MapGet("/api/auth/me", (HttpContext context) =>
        {
            var user = WebAccessGuard.CurrentUser(context);
            return user is null
                ? Results.Json(new { code = "UNAUTHORIZED", message = "需要登录" }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(new { user = Describe(user) });
        });

        // 自己改自己的口令：**必须先给旧口令**，否则一个偷到 cookie 的人就能换掉口令、把主人锁在外面。
        // 改完这个人的会话全部作废（含当前这条），所以前端拿到 200 之后应当回到登录页。
        app.MapPost("/api/auth/password", (PasswordChangeRequest body, HttpContext context) =>
        {
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null) return Task.FromResult(Unauthorized());
            var result = users.ChangePassword(user.Id, body.CurrentPassword ?? "", body.NewPassword ?? "");
            return Task.FromResult(result.Status == UserWriteStatus.Ok
                ? Results.Json(new { status = "ok", message = "口令已修改，请重新登录" })
                : FromUserStatus(result.Status, result.Error));
        });

        // ---------- 管理员：管用户 ----------

        app.MapGet("/api/auth/users", (HttpContext context) =>
        {
            if (Denied(context) is { } denial) return denial;
            return Results.Json(new { users = users.List().Select(Describe).ToArray() });
        });

        app.MapPost("/api/auth/users", (NewUserRequest body, HttpContext context) =>
        {
            if (Denied(context) is { } denial) return denial;
            if (!TryRole(body.Role, out var role, out var error))
                return Results.Json(new { code = "INVALID_ROLE", message = error }, statusCode: StatusCodes.Status400BadRequest);
            var result = users.CreateUser(body.Username ?? "", body.Password ?? "", role, body.DisplayName);
            return result.Status == UserWriteStatus.Ok && result.User is not null
                ? Results.Json(new { user = Describe(result.User) })
                : FromUserStatus(result.Status, result.Error);
        });

        app.MapPatch("/api/auth/users/{id:guid}", (Guid id, UserPatchRequest body, HttpContext context) =>
        {
            if (Denied(context) is { } denial) return denial;
            if (body.Role is not null)
            {
                if (!TryRole(body.Role, out var role, out var error))
                    return Results.Json(new { code = "INVALID_ROLE", message = error }, statusCode: StatusCodes.Status400BadRequest);
                var result = users.SetRole(id, role);
                if (result.Status != UserWriteStatus.Ok) return FromUserStatus(result.Status, result.Error);
            }
            if (body.Disabled is { } disabled)
            {
                var result = users.SetDisabled(id, disabled);
                if (result.Status != UserWriteStatus.Ok) return FromUserStatus(result.Status, result.Error);
            }
            var updated = users.Find(id);
            return updated is null
                ? Results.Json(new { code = "USER_NOT_FOUND", message = "用户不存在" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(new { user = Describe(updated) });
        });

        app.MapPost("/api/auth/users/{id:guid}/password", (Guid id, PasswordChangeRequest body, HttpContext context) =>
        {
            if (Denied(context) is { } denial) return denial;
            var result = users.ResetPassword(id, body.NewPassword ?? "");
            return result.Status == UserWriteStatus.Ok
                ? Results.Json(new { status = "ok" })
                : FromUserStatus(result.Status, result.Error);
        });

        app.MapDelete("/api/auth/users/{id:guid}", (Guid id, HttpContext context) =>
        {
            if (Denied(context) is { } denial) return denial;
            var result = users.Delete(id);
            return result.Status == UserWriteStatus.Ok
                ? Results.Json(new { status = "ok" })
                : FromUserStatus(result.Status, result.Error);
        });
    }

    /// <summary>登录成功的统一收尾：建会话、下发 cookie、回用户信息。</summary>
    private static IResult SignedIn(UserStore users, HttpContext context, WebUser user)
    {
        var (token, expires) = users.CreateSession(user.Id, context.Request.Headers.UserAgent.ToString());
        WebAccessGuard.SetCookie(context, token, expires);
        return Results.Json(new { user = Describe(user) });
    }

    /// <summary>管理员接口的守卫：没有会话 → 401；登录了但不是管理员 → 403。</summary>
    private static IResult? Denied(HttpContext context)
    {
        var user = WebAccessGuard.CurrentUser(context);
        if (user is null)
            return Results.Json(new { code = "UNAUTHORIZED", message = "需要登录" }, statusCode: StatusCodes.Status401Unauthorized);
        if (user.Role != UserRole.Admin)
            return Results.Json(new { code = "ADMIN_REQUIRED", message = "只有管理员能管用户" }, statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    private static IResult Unauthorized() =>
        Results.Json(new { code = "UNAUTHORIZED", message = "需要登录" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult FromUserStatus(UserWriteStatus status, string? error) => status switch
    {
        UserWriteStatus.SetupAlreadyDone => Results.Json(new { code = "SETUP_ALREADY_DONE", message = error }, statusCode: StatusCodes.Status409Conflict),
        UserWriteStatus.InvalidUsername => Results.Json(new { code = "INVALID_USERNAME", message = error }, statusCode: StatusCodes.Status400BadRequest),
        UserWriteStatus.WeakPassword => Results.Json(new { code = "WEAK_PASSWORD", message = error }, statusCode: StatusCodes.Status400BadRequest),
        UserWriteStatus.UsernameTaken => Results.Json(new { code = "USERNAME_TAKEN", message = error }, statusCode: StatusCodes.Status409Conflict),
        UserWriteStatus.LastAdmin => Results.Json(new { code = "LAST_ADMIN", message = error }, statusCode: StatusCodes.Status409Conflict),
        UserWriteStatus.NotFound => Results.Json(new { code = "USER_NOT_FOUND", message = error ?? "用户不存在" }, statusCode: StatusCodes.Status404NotFound),
        // 改口令给错了当前口令：这是「凭据不对」，回 401 而不是 404——回 404 会让人以为账号没了。
        UserWriteStatus.WrongPassword or UserWriteStatus.InvalidCredentials =>
            Results.Json(new { code = "INVALID_CREDENTIALS", message = error ?? "凭据不正确" }, statusCode: StatusCodes.Status401Unauthorized),
        UserWriteStatus.AccountDisabled => Results.Json(new { code = "ACCOUNT_DISABLED", message = error }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new { code = "USER_WRITE_FAILED", message = error ?? "操作失败" }, statusCode: StatusCodes.Status400BadRequest)
    };

    private static bool TryRole(string? text, out UserRole role, out string? error)
    {
        role = UserRoles.Parse(text);
        error = null;
        var normalised = (text ?? "").Trim().ToLowerInvariant();
        if (normalised.Length == 0 || normalised is "admin" or "editor" or "viewer") return true;
        error = "角色只能是 Admin、Editor 或 Viewer";
        return false;
    }

    private static bool FixedMatch(string presented, string expected) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static string AttemptKey(HttpContext context, string? username) =>
        (username ?? "").Trim().ToLowerInvariant() + "|" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>
    /// 部署时配置的初始化令牌；**空字符串按「没配」算**。
    /// 这条不是洁癖：`docker compose` 里习惯写 <c>${YEEYEEYEE_SETUP_TOKEN:-}</c>，
    /// 没设变量时传进来就是一个空串。若把它当成「配了令牌」，
    /// 界面会多出一个谁也填不出的初始化令牌输入框，首次建号直接卡死。
    /// </summary>
    private static string? SetupToken(WebApplication app)
    {
        var token = LegacyConfig.Text(app.Configuration, "SetupToken");
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static object Describe(WebUser user) => new
    {
        id = user.Id,
        username = user.Username,
        displayName = user.DisplayName,
        role = UserRoles.ToText(user.Role),
        roleLabel = UserRoles.Label(user.Role),
        createdAt = user.CreatedAt,
        lastLoginAt = user.LastLoginAt,
        disabled = user.Disabled
    };
}
