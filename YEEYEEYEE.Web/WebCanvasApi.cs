namespace YEEYEEYEE.Web;

/// <summary>
/// 整张画布的写入：<c>PUT /api/web/canvas?baseRevision={修订}&amp;client=desktop</c>，
/// **请求体就是画布文件的字节**。
///
/// 谁在用：桌面端。它编辑的是本地文件，自己保存时既不经锁仲裁、别人也收不到通知；
/// 把保存交给服务端之后，「谁在编辑」这条锁才真正管得住它，改动也顺便推给所有订阅者。
///
/// 为什么请求体直接是字节而不是包一层 JSON：包一层就得把整份画布塞进字符串再转义一遍，
/// 而服务端要的本来就是「这份字节」——它会被当作画布文件的内容重新解析与校验。
///
/// 这是**结构级**写入（它可能改动任何东西），所以按结构级对待：要整棵树锁。
/// 网页端的单节点写入目前仍是「界面挡住 + 修订 CAS」，服务端强制是后续的事。
/// </summary>
internal static class WebCanvasApi
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        var mode = WebCanvasMode.Resolve(app.Configuration);
        var store = mode is { IsProject: true, CanvasPath: not null }
            ? new ProjectCanvasSceneStore(mode.CanvasPath, mode.EntitiesPath)
            : null;
        var leases = mode.CanvasPath is null
            ? null
            : new EditLeaseStore(mode.CanvasPath, EditLeaseApi.LifetimeOf(app.Configuration));
        var hub = app.Services.GetRequiredService<CanvasEventHub>();

        app.MapPut("/api/web/canvas", async (HttpContext context, long? baseRevision, string? client) =>
        {
            if (store is null)
                return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT",
                    "整画布写入要的是项目画布；当前配置的是独立 Web 场景");

            // 身份：整画布写入要记在某个账号名下（桌面端登录后用自己的账号），
            // 否则「谁在编辑」这条链上就没有人。
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null)
                return Error(403, "CANVAS_WRITE_REQUIRES_SESSION", "整画布写入要记在某个账号名下，必须用账号登录");
            if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
            if (baseRevision is not { } revision)
                return Error(400, "INVALID_REQUEST", "缺少 baseRevision（你手上那份的修订号）");

            var source = string.IsNullOrWhiteSpace(client) ? "desktop" : client;
            if (!EditClient.IsValid(source))
                return Error(400, "INVALID_REQUEST", "client 只能是 web 或 desktop");

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            if (buffer.Length == 0)
                return Error(400, "INVALID_REQUEST", "请求体是空的（这里要的是画布文件的字节）");

            // 先握手：送来的基准修订必须与**磁盘上这份**一致。不一致说明别人已经改过，
            // 让调用方去重新加载，而不是把别人的改动盖掉。握手放在拿锁之前——
            // 注定失败的写入不该占住整棵树锁（与整理布局同一条规矩）。
            if (store.CurrentRevision() is not { } current)
                return Error(503, "PROJECT_CANVAS_READ_FAILED", "读不到项目画布文件");
            if (current != revision)
                return Error(409, "SCENE_REVISION_CONFLICT", "项目画布已被修改，请重新加载后再保存");

            var before = store.LastCommit;
            var lease = leases!.Acquire(EditScope.Tree, null, user, source, force: false);
            if (lease.Status != EditLeaseStatus.Ok) return EditLeaseApi.Failure(lease);

            IResult result;
            try
            {
                result = store.ReplaceCanvas(revision, buffer.ToArray());
            }
            finally
            {
                // 与整理布局同理：锁只为这一次写入排队，写完就还回去。
                leases.Release(lease.Lease!.LeaseId, user, force: false);
            }

            // 真的落了盘才广播：别人据此刷新，而不是靠自己去猜。
            var after = store.LastCommit;
            if (after.Serial > before.Serial)
                hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(context), "canvas");
            return result;
        });
    }
}
