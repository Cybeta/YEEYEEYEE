using System.Net;
using System.Security.Cryptography;
using System.Text;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 访问守卫：**谁能碰 /api/web、/api/canvas、/ws/canvas**。
///
/// 现在有两条身份路径，各自服务一类调用方：
///
/// · **浏览器用会话 cookie**（`yeeeyee_session`）。不要求来源是本机——容器部署时用户就是从别的机器访问的，
///   还要求 loopback 等于把 Docker 那条路堵死。权限由**角色**换算，前端传什么都不作数。
/// · **桌面桥用 Bearer**（配置项 `WebToken`）+ **必须来自本机**。这条是给桌面端推场景用的老路径，
///   它不该因为加了登录系统而变得能从外网调用，所以 loopback 与静态令牌这两道门原样保留。
///
/// 两者的关系是「或」：任一条通过即可。没有凭据时回 401；带 Bearer 但令牌没配置时回 503
/// （这是部署漏配，得让运维一眼看出是哪一项没配，而不是笼统的「未授权」）。
/// </summary>
internal static class WebAccessGuard
{
    public const string CookieName = "yeeeyee_session";
    public const string UserItem = "yeeeyee.user";
    public const string PermissionsItem = "yeeeyee.permissions";

    private static bool Guarded(PathString path) =>
        path.StartsWithSegments("/api/web") || path.StartsWithSegments("/api/canvas") || path.StartsWithSegments("/ws/canvas");

    public static void Map(WebApplication app, UserStore users)
    {
        app.Use(async (context, next) =>
        {
            // 会话 cookie 的身份解析对**所有**请求都做，不只对守卫的路径：
            // /api/auth/me、/api/auth/users 这些接口自己也要知道「现在是谁」，
            // 如果只有守卫路径才解析，它们会永远拿到「未登录」（这条踩过）。
            if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrWhiteSpace(token))
            {
                var user = users.ResolveSession(token);
                if (user is not null)
                {
                    context.Items[UserItem] = user;
                    context.Items[PermissionsItem] = UserRoles.ClaimsFor(user.Role);
                    await next(context);
                    return;
                }

                // cookie 过期、被作废、或用户被停用：守卫路径当成没登录并清掉坏 cookie，
                // 否则浏览器会一直带着它、每次请求都白跑一次库查询。
                // 非守卫路径（比如 /api/auth/state）不在这里拦——它本来就该如实回答「未登录」。
                if (Guarded(context.Request.Path))
                {
                    ClearCookie(context);
                    await Fail(context, 401, "UNAUTHORIZED", "登录已失效，请重新登录");
                    return;
                }
                await next(context);
                return;
            }

            if (!Guarded(context.Request.Path))
            {
                await next(context);
                return;
            }

            // ② 桌面桥的 Bearer + 本机
            var authorization = context.Request.Headers.Authorization.ToString();
            var presented = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : string.Empty;
            var configured = LegacyConfig.Text(app.Configuration, "WebToken");

            if (presented.Length == 0 && configured is null or "")
            {
                // 什么都没有，而且桥的令牌也没配：这是「还没登录」，不是「服务配错了」。
                await Fail(context, 401, "UNAUTHORIZED", "需要登录");
                return;
            }

            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
            {
                await Fail(context, 403, "LOCAL_ONLY", "桌面桥仅允许本机访问；从外部访问请用账号登录");
                return;
            }
            if (string.IsNullOrWhiteSpace(configured))
            {
                await Fail(context, 503, "TOKEN_NOT_CONFIGURED", "Web 访问令牌未配置");
                return;
            }
            if (presented.Length == 0 || presented.Contains(' ') ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
            {
                await Fail(context, 401, "UNAUTHORIZED", "Bearer 令牌无效");
                return;
            }

            // 桌面桥的权限来自服务端配置（`WebClaims`），且**每次请求都重读**——
            // 这样改配置能立刻收回权限，不用重启进程（有测试盯着这一点）。
            var claims = (LegacyConfig.Text(app.Configuration, "WebClaims") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            context.Items[PermissionsItem] = new HashSet<string>(claims, StringComparer.Ordinal);
            await next(context);
        });
    }

    /// <summary>当前请求的权限集合。没有身份时是空集（**空集不等于放行**，调用方必须自己判）。</summary>
    public static IReadOnlySet<string> Permissions(HttpContext context) =>
        context.Items.TryGetValue(PermissionsItem, out var value) && value is IReadOnlySet<string> set
            ? set
            : new HashSet<string>(StringComparer.Ordinal);

    public static WebUser? CurrentUser(HttpContext context) =>
        context.Items.TryGetValue(UserItem, out var value) ? value as WebUser : null;

    /// <summary>
    /// 「谁做的」这件事的显示名，用在变更推送与锁提示上。
    /// 桌面桥没有用户身份（Bearer 令牌不带用户），就报它自己的名字——
    /// 总比留空让界面写一句「有人改了」强。
    /// </summary>
    public static string ActorName(HttpContext context) =>
        CurrentUser(context) is { } user
            ? (string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName)
            : "桌面桥";

    /// <summary>写入 cookie。HttpOnly（脚本读不到）、SameSite=Lax（跨站请求不带它，够挡 CSRF）。</summary>
    public static void SetCookie(HttpContext context, string token, DateTimeOffset expires)
    {
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = expires
        });
    }

    public static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax });

    public static string? Token(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var token) ? token : null;

    public static async Task Fail(HttpContext context, int status, string code, string message) =>
        await Results.Json(new { code, message }, statusCode: status).ExecuteAsync(context);
}
