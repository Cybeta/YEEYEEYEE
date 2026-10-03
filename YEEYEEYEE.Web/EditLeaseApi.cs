using Microsoft.Extensions.Configuration;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 编辑锁的接口。四个动作：查询、申请、续期、释放。
///
/// 两条规矩在这里落地：
/// · **申请与释放要 <c>canvas.edit</c> 权限**，只读账号只能看不能占（不然只读也能把人挡在外面）。
/// · **强制接管是管理员专属**，而且要显式带 <c>force</c>，不提供「悄悄踢掉别人」的默认行为。
///
/// 身份一律用账号会话：锁的意义就是「显示谁在编辑」，而桌面桥的 Bearer 令牌不带用户身份。
/// 所以桥那条路在这里会被拒（403），等桌面端登录接进来之后自然就通了。
/// </summary>
internal static class EditLeaseApi
{
    /// <summary>申请。请求体的字段名沿用仓库里其他接口的写法（camelCase，由框架按 Web 默认规则绑定）。</summary>
    public sealed record AcquireRequest(string? Scope, Guid? TargetId, string? Client, bool? Force);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    private static object Wire(EditLease lease) => new
    {
        leaseId = lease.LeaseId,
        scope = lease.Scope,
        targetId = lease.TargetId,
        userId = lease.UserId,
        displayName = lease.DisplayName,
        client = lease.Client,
        acquiredAt = lease.AcquiredAt,
        expiresAt = lease.ExpiresAt
    };

    private static IResult From(EditLeaseResult result) => result.Status switch
    {
        EditLeaseStatus.Ok => Results.Json(new
        {
            lease = Wire(result.Lease!),
            displaced = result.Displaced?.Select(Wire).ToArray()
        }),
        _ => Failure(result)
    };

    /// <summary>
    /// 冲突 / 失效这类结果的统一回法。整理布局那个接口也要按同一套码与同一句文案回，
    /// 所以它是 internal 的——两处各写一份，同一个条件迟早会出现两种错误码。
    /// </summary>
    internal static IResult Failure(EditLeaseResult result) => result.Status switch
    {
        // 冲突要把对方是谁一并回给界面，否则用户只看到「被占用」而不知道该等谁。
        EditLeaseStatus.Conflict => Results.Json(
            new { code = "EDIT_CONFLICT", message = result.Error, holder = result.Holder is null ? null : Wire(result.Holder) },
            statusCode: 409),
        EditLeaseStatus.NotFound => Error(404, "EDIT_LEASE_NOT_FOUND", result.Error!),
        EditLeaseStatus.Expired => Error(410, "EDIT_LEASE_EXPIRED", result.Error!),
        EditLeaseStatus.NotHolder => Error(403, "EDIT_LEASE_NOT_HOLDER", result.Error!),
        _ => Error(503, "EDIT_LEASE_STORAGE_FAILED", result.Error!)
    };

    internal static TimeSpan LifetimeOf(IConfiguration configuration)
    {
        var raw = LegacyConfig.Text(configuration, "EditLeaseLifetimeSeconds");
        // 越界的值直接落回默认：不让人用一个笔误把所有人的锁变成一秒或一天。
        return int.TryParse(raw, out var seconds) && seconds is >= 1 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : EditLeaseStore.DefaultLifetime;
    }

    public static void Map(WebApplication app)
    {
        // 锁文件挨着画布放，所以必须和场景接口解出同一张画布——两边共用 WebCanvasMode。
        var mode = WebCanvasMode.Resolve(app.Configuration);
        var store = mode.CanvasPath is null ? null : new EditLeaseStore(mode.CanvasPath, LifetimeOf(app.Configuration));
        var lifetimeSeconds = (int)(store?.Lifetime ?? EditLeaseStore.DefaultLifetime).TotalSeconds;
        var hub = app.Services.GetRequiredService<CanvasEventHub>();

        app.MapGet("/api/web/edits", () =>
            store is null
                ? mode.Error!
                : Results.Json(new { leases = store.List().Select(Wire).ToArray(), lifetimeSeconds }));

        app.MapPost("/api/web/edits", (AcquireRequest body, HttpContext context) =>
        {
            if (store is null) return mode.Error!;
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null)
                return Error(403, "EDIT_LEASE_REQUIRES_SESSION", "编辑锁要显示「谁在编辑」，必须用账号登录（桌面桥的令牌不带用户身份）");
            if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");

            if (!EditScope.IsValid(body.Scope))
                return Error(400, "INVALID_REQUEST", "scope 必须是 node 或 tree");
            var node = body.Scope == EditScope.Node;
            if (node && (body.TargetId is null || body.TargetId == Guid.Empty))
                return Error(400, "INVALID_REQUEST", "节点级锁必须给 targetId");
            if (!node && body.TargetId is not null)
                return Error(400, "INVALID_REQUEST", "整棵树锁不该带 targetId");
            var client = string.IsNullOrWhiteSpace(body.Client) ? null : body.Client;
            if (client is not null && !EditClient.IsValid(client))
                return Error(400, "INVALID_REQUEST", "client 只能是 web 或 desktop");
            var force = body.Force == true;
            if (force && user.Role != UserRole.Admin)
                return Error(403, "ADMIN_REQUIRED", "强制接管别人的编辑锁需要管理员");

            var acquired = store.Acquire(body.Scope!, body.TargetId, user, client, force);
            // 幂等的重复申请（同一个人同一个目标）也会广播一次。代价是别人的一次 GET，
            // 换来的是服务端不用为了判断「是不是新锁」再读一遍锁文件——这笔账划算。
            if (acquired.Status == EditLeaseStatus.Ok)
                hub.EditsChanged(force ? "force" : "acquire", WebAccessGuard.ActorName(context));
            return From(acquired);
        });
        app.MapPut("/api/web/edits/{leaseId}", (string leaseId, HttpContext context) =>
        {
            if (store is null) return mode.Error!;
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null)
                return Error(403, "EDIT_LEASE_REQUIRES_SESSION", "编辑锁要显示「谁在编辑」，必须用账号登录");
            if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
            // 认不出 LeaseId 的形状就当成「这个锁不在」，与「已被释放」在界面上是同一件事。
            // 续期**不发广播**：它只把到期时间往后推，界面上没有任何东西会变。
            return Guid.TryParse(leaseId, out var parsed)
                ? From(store.Renew(parsed, user))
                : Error(404, "EDIT_LEASE_NOT_FOUND", "锁不存在或已过期");
        });

        app.MapDelete("/api/web/edits/{leaseId}", (string leaseId, HttpContext context) =>
        {
            if (store is null) return mode.Error!;
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null)
                return Error(403, "EDIT_LEASE_REQUIRES_SESSION", "编辑锁要显示「谁在编辑」，必须用账号登录");
            if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
            var force = string.Equals(context.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);
            if (force && user.Role != UserRole.Admin)
                return Error(403, "ADMIN_REQUIRED", "强制释放别人的编辑锁需要管理员");
            if (!Guid.TryParse(leaseId, out var parsed))
                return Error(404, "EDIT_LEASE_NOT_FOUND", "锁不存在或已过期");
            var result = store.Release(parsed, user, force);
            if (result.Status != EditLeaseStatus.Ok) return From(result);
            hub.EditsChanged(force ? "force-release" : "release", WebAccessGuard.ActorName(context));
            return Results.Json(new { released = Wire(result.Lease!) });
        });
    }
}
