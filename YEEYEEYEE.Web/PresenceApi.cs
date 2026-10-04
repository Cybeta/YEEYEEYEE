using Microsoft.Extensions.Configuration;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 在线名单：<c>GET /api/web/presence</c>。**只回答「谁此刻连着我们」**，不参与锁与权限——
/// 锁是目标级的（谁正在编辑哪个节点），在线是人级的；用一个去推另一个会把两件事混成一团。
///
/// 返回的每一项都带 <c>basis</c>，如实说明它是靠哪条依据算出来的：
/// · <c>connection</c>：这个人有一条活着的 SSE 连接（「此刻在线」的主依据）。
/// · <c>recent</c>：这个人最近的会话还在 <c>last_seen_at</c> 的 TTL 内（没订阅的桌面端、或网页端刚断的兜底）。
/// 两种依据**不合并成一个「在线」**：主依据能说「现在」，兜底只能说「刚还在」。
/// </summary>
internal static class PresenceApi
{
    public static void Map(WebApplication app, UserStore users)
    {
        var presence = app.Services.GetRequiredService<PresenceRegistry>();
        var lifetime = LifetimeOf(app.Configuration);
        var lifetimeSeconds = (int)lifetime.TotalSeconds;

        app.MapGet("/api/web/presence", (HttpContext context) =>
        {
            // 与其它接口一个口径：没有身份就 401（匿名请求其实在守卫那一层已经被拦掉了，
            // 这里挡的是「有令牌但没用户身份」的桌面桥）。
            if (WebAccessGuard.CurrentUser(context) is null)
                return Results.Json(
                    new { code = "PRESENCE_REQUIRES_SESSION", message = "在线名单要登录后才能查看" },
                    statusCode: StatusCodes.Status401Unauthorized);

            var now = DateTimeOffset.UtcNow;
            var people = new List<object>();
            var listed = new HashSet<Guid>();

            // ① 活连接：主依据。
            foreach (var entry in presence.List(lifetime))
            {
                listed.Add(entry.UserId);
                people.Add(new
                {
                    userId = entry.UserId,
                    displayName = entry.DisplayName,
                    clients = entry.Clients,
                    connections = entry.Connections,
                    basis = "connection",
                    lastSeenSeconds = Seconds(now - entry.LastSeenAt)
                });
            }

            // ② 会话表 last_seen_at：兜底。只补①里没有的人，一个人只出现一次。
            foreach (var session in users.RecentSessions(now - lifetime))
            {
                if (!listed.Add(session.UserId)) continue;
                people.Add(new
                {
                    userId = session.UserId,
                    displayName = session.DisplayName,
                    // 会话表认不出 wire 上的 web/desktop，宁可不标也不猜。
                    clients = Array.Empty<string>(),
                    connections = 0,
                    basis = "recent",
                    lastSeenSeconds = Seconds(now - session.LastSeenAt)
                });
            }

            return Results.Json(new { people, lifetimeSeconds });
        });
    }

    private static long Seconds(TimeSpan elapsed) =>
        (long)Math.Max(0, elapsed.TotalSeconds);

    /// <summary>
    /// 在线兜底的 TTL。与编辑锁一样可配、越界落回默认：TTL 越长，断线的人被多算一会儿；
    /// 越短，一次网络抖动就会让「在线」闪一下。默认两分钟。
    /// </summary>
    private static TimeSpan LifetimeOf(IConfiguration configuration)
    {
        var raw = LegacyConfig.Text(configuration, "PresenceLifetimeSeconds");
        return int.TryParse(raw, out var seconds) && seconds is >= 1 and <= 86400
            ? TimeSpan.FromSeconds(seconds)
            : PresenceRegistry.DefaultLifetime;
    }
}
