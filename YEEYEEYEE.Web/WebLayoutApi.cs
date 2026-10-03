using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 整理布局的接口。两个动作：<c>plan</c>（只算不写）与 <c>apply</c>（写坐标）。
///
/// **为什么在服务端算**：泳道布局引擎 <c>CanvasSwimlaneLayout</c> 住在 Desktop.Shared 里，
/// 与桌面端是同一份代码，所以两端对同一张画布排出来的结果一定一致。网页端不来送坐标，
/// 只来说「整理整张画布」还是「只整理这一章」——客户端送来的坐标在写入前会被丢掉并重算。
///
/// **apply 要先拿到整棵树锁**：整理会移动结构，属于整棵树级的操作，所以它复用编辑锁的仲裁。
/// 两个人同时整理会自然排队；冲突时回的是同一个 <c>EDIT_CONFLICT</c>、同一个持有者身份，
/// 界面不用再学一套错误码。
/// </summary>
internal static class WebLayoutApi
{
    public sealed record PlanRequest(string? Scope, bool? OverrideManual);

    public sealed record ApplyRequest(long BaseRevision, string? Scope, bool? OverrideManual, string? Client);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    private static IResult Unavailable(WebCanvasMode mode) => mode.Error ?? Error(409, "LAYOUT_REQUIRES_PROJECT",
        "整理布局要的是项目画布（有章节才有泳道）；当前配置的是独立 Web 场景");

    /// <summary>身份与权限闸门。两个动作共用，免得「预览不要权限、写入要权限」这类差异悄悄长出来。</summary>
    private static IResult? Guard(HttpContext context, out WebUser? user)
    {
        user = WebAccessGuard.CurrentUser(context);
        if (user is null)
            return Error(403, "LAYOUT_REQUIRES_SESSION", "整理布局要记在某个账号名下，必须用账号登录（桌面桥的令牌不带用户身份）");
        if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
            return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
        return null;
    }

    public static void Map(WebApplication app)
    {
        var mode = WebCanvasMode.Resolve(app.Configuration);
        // 只有项目画布才有章节与泳道。独立场景是一份裸 JSON，引擎要的状态它没有。
        var store = mode is { IsProject: true, CanvasPath: not null }
            ? new ProjectCanvasSceneStore(mode.CanvasPath, mode.EntitiesPath)
            : null;
        // 锁与编辑锁接口共用同一份 TTL 规则，所以走 EditLeaseApi.LifetimeOf。
        var leases = mode.CanvasPath is null
            ? null
            : new EditLeaseStore(mode.CanvasPath, EditLeaseApi.LifetimeOf(app.Configuration));

        app.MapPost("/api/web/layout/plan", (PlanRequest body, HttpContext context) =>
        {
            if (store is null) return Unavailable(mode);
            var guard = Guard(context, out _);
            if (guard is not null) return guard;
            return store.PlanLayout(body.Scope, body.OverrideManual == true);
        });

        app.MapPost("/api/web/layout/apply", (ApplyRequest body, HttpContext context) =>
        {
            if (store is null) return Unavailable(mode);
            var guard = Guard(context, out var user);
            if (guard is not null) return guard;
            var client = string.IsNullOrWhiteSpace(body.Client) ? null : body.Client;
            if (client is not null && !EditClient.IsValid(client))
                return Error(400, "INVALID_REQUEST", "client 只能是 web 或 desktop");

            // 先干跑。注定失败的整理（修订陈旧、布局被阻断）**不该占住整棵树锁**——
            // 否则一次失败的手滑会把别人的结构改动挡在门外，直到锁自己过期。
            var refusal = store.PreflightApply(body.BaseRevision, body.Scope, body.OverrideManual == true,
                out var willWrite, out var revision, out _, out var summary);
            if (refusal is not null) return refusal;
            if (!willWrite)
                return Results.Json(new { revision, moved = 0, summary, records = Array.Empty<object>() });

            // 确实要写，这时才去拿整棵树锁：自己持有就是续期，别人持有则如实回冲突与持有者。
            var lease = leases!.Acquire(EditScope.Tree, null, user!, client, force: false);
            if (lease.Status != EditLeaseStatus.Ok) return EditLeaseApi.Failure(lease);
            return store.ApplyLayout(body.BaseRevision, body.Scope, body.OverrideManual == true);
        });
    }
}
