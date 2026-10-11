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
    private static ProjectCanvasSceneStore? store => WebProjectApi.Current is { } mode ? WebProjectApi.Store(mode) : null;
    private static EditLeaseStore? leases => WebProjectApi.Current is { } mode ? WebProjectApi.Leases(mode) : null;
    public sealed record FilmRequest(long BaseRevision, Guid[] ShotIds);
    public sealed record HistoryRequest(long BaseRevision);
    public sealed record WorkTreeRequest(long BaseRevision, string Direction, string BaselineHash, bool Confirmed);
    public sealed record ChapterSplitRequest(long BaseRevision, Guid SourceId);
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        var mode = WebCanvasMode.Resolve(app.Configuration);

        var hub = app.Services.GetRequiredService<CanvasEventHub>();
        app.MapGet("/api/web/records/{nodeId:guid}/film", (Guid nodeId) =>
            store?.FilmPlan(nodeId) ?? Error(409, "CANVAS_REQUIRES_PROJECT", "成片需要项目画布"));
        app.MapPost("/api/web/records/{nodeId:guid}/film", (HttpContext context, Guid nodeId, FilmRequest input) =>
        {
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || !WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "成片需要登录及画布编辑权限");
            if (store is null) return Error(409, "CANVAS_REQUIRES_PROJECT", "成片需要项目画布");
            if (input.ShotIds is null || input.ShotIds.Length > 500) return Error(400, "FILM_SELECTION_INVALID", "镜头选择无效");
            return leases!.WithTreeTransaction<IResult>(user, () =>
            {
                var before = store.LastCommit;
                var result = store.ConcatFilm(input.BaseRevision, nodeId, input.ShotIds);
                var after = store.LastCommit;
                if (after.Serial > before.Serial) hub.CanvasChanged(after.Revision, nodeId.ToString(), WebAccessGuard.ActorName(context), "record");
                return result;
            }, EditLeaseApi.Failure);
        });
        var historyGate = new SemaphoreSlim(1, 1);
        var histories = new Dictionary<string, (List<byte[]> Undo, List<byte[]> Redo, long Revision)>();
        const int historyLimit = 30;
        const int snapshotLimit = 8 * 1024 * 1024;
        bool OrdinaryWrite(HttpContext c)
        {
            var path = c.Request.Path.Value ?? "";
            return c.Request.Method is "POST" or "PUT" or "DELETE" &&
                (path.StartsWith("/api/web/records", StringComparison.Ordinal) ||
                 path.StartsWith("/api/web/edges", StringComparison.Ordinal) ||
                 path == "/api/web/layout/apply" || path.StartsWith("/api/web/worktree/", StringComparison.Ordinal) || path == "/api/web/canvas");
        }
        // Record authoritative bytes only after a successful ordinary edit. A changed external
        // revision invalidates this user's history; undo never overwrites later collaborators.
        app.Use(async (context, next) =>
        {
            var user = WebAccessGuard.CurrentUser(context);
            if (store is null || user is null || !OrdinaryWrite(context)) { await next(context); return; }
            await historyGate.WaitAsync(context.RequestAborted);
            try
            {
                byte[]? beforeBytes = null;
                long? beforeRevision = null;
                try
                {
                    if (new FileInfo(mode.CanvasPath!).Length <= snapshotLimit)
                    {
                        beforeBytes = File.ReadAllBytes(mode.CanvasPath!);
                        beforeRevision = YEEYEEYEE.Desktop.CanvasRevision.Of(beforeBytes);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                await next(context);
                if (context.Response.StatusCode is >= 200 and < 300 && beforeBytes is not null &&
                    store.CurrentRevision() is { } afterRevision && afterRevision != beforeRevision)
                {
                    if (!histories.TryGetValue(WebProjectApi.ScopedOwner(user.Id.ToString()), out var history) || history.Revision != beforeRevision)
                        history = (new List<byte[]>(), new List<byte[]>(), afterRevision);
                    history.Undo.Add(beforeBytes);
                    if (history.Undo.Count > historyLimit) history.Undo.RemoveAt(0);
                    history.Redo.Clear();
                    histories[WebProjectApi.ScopedOwner(user.Id.ToString())] = (history.Undo, history.Redo, afterRevision);
                }
            }
            finally { historyGate.Release(); }
        });
        app.MapGet("/api/web/canvas/history", async (HttpContext context) =>
        {
            if (store is null) return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT", "撤销重做需要项目画布");
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null) return Error(403, "CANVAS_WRITE_REQUIRES_SESSION", "必须用账号登录");
            await historyGate.WaitAsync(context.RequestAborted);
            try
            {
                var revision = store.CurrentRevision();
                histories.TryGetValue(WebProjectApi.ScopedOwner(user.Id.ToString()), out var history);
                var valid = revision.HasValue && history.Revision == revision;
                return Results.Json(new { revision, undo = valid ? history.Undo?.Count ?? 0 : 0, redo = valid ? history.Redo?.Count ?? 0 : 0 });
            }
            finally { historyGate.Release(); }
        });
        app.MapPost("/api/web/canvas/history/{operation}", async (HttpContext context, string operation, HistoryRequest input) =>
        {
            if (store is null) return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT", "撤销重做需要项目画布");
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || !WebAccessGuard.Permissions(context).Contains("canvas.edit")) return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要登录及 canvas.edit 权限");
            if (operation is not ("undo" or "redo")) return Error(400, "INVALID_REQUEST", "操作只能是 undo 或 redo");
            await historyGate.WaitAsync(context.RequestAborted);
            try
            {
                return leases!.WithTreeTransaction<IResult>(user, () =>
                {
                    if (store.CurrentRevision() != input.BaseRevision || !histories.TryGetValue(WebProjectApi.ScopedOwner(user.Id.ToString()), out var history) || history.Revision != input.BaseRevision)
                        return Error(409, "HISTORY_REVISION_CONFLICT", "画布已变化，不能覆盖后续编辑，请重新加载");
                    var source = operation == "undo" ? history.Undo : history.Redo;
                    var destination = operation == "undo" ? history.Redo : history.Undo;
                    if (source.Count == 0) return Error(409, "HISTORY_EMPTY", "没有可撤销或重做的操作");
                    var current = File.ReadAllBytes(mode.CanvasPath!);
                    if (current.Length > snapshotLimit) return Error(409, "HISTORY_TOO_LARGE", "画布超过撤销快照上限");
                    var before = store.LastCommit;
                    var result = store.ReplaceCanvas(input.BaseRevision, source[^1]);
                    var after = store.LastCommit;
                    if (after.Serial > before.Serial)
                    {
                        source.RemoveAt(source.Count - 1); destination.Add(current);
                        histories[WebProjectApi.ScopedOwner(user.Id.ToString())] = (history.Undo, history.Redo, after.Revision);
                        hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(context), "canvas");
                    }
                    return result;
                }, EditLeaseApi.Failure);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(503, "HISTORY_READ_FAILED", "画布快照读取失败"); }
            finally { historyGate.Release(); }
        });

        app.MapPost("/api/web/worktree/split", (HttpContext context, ChapterSplitRequest input) =>
        {
            if (store is null) return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT", "拆章需要项目画布");
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || !WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要登录及 canvas.edit 权限");
            return leases!.WithTreeTransaction<IResult>(user, () =>
            {
                var before = store.LastCommit;
                var result = store.SplitChapters(input.BaseRevision, input.SourceId);
                var after = store.LastCommit;
                if (after.Serial > before.Serial) hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(context), "structure");
                return result;
            }, EditLeaseApi.Failure);
        });
        bool ParseDirection(string? value, out YEEYEEYEE.Desktop.SyncDirection direction) =>
            Enum.TryParse(value ?? "Both", true, out direction) && Enum.IsDefined(direction);
        app.MapGet("/api/web/worktree/plan", (string? direction) =>
        {
            if (store is null) return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT", "同步需要项目画布");
            if (!ParseDirection(direction, out var parsed)) return Error(400, "INVALID_REQUEST", "同步方向无效");
            return store.WorkTreePlan(parsed);
        });
        app.MapPost("/api/web/worktree/apply", (HttpContext context, WorkTreeRequest input) =>
        {
            if (store is null) return mode.Error ?? Error(409, "CANVAS_REQUIRES_PROJECT", "同步需要项目画布");
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || !WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要登录及 canvas.edit 权限");
            if (!ParseDirection(input.Direction, out var direction)) return Error(400, "INVALID_REQUEST", "同步方向无效");
            return leases!.WithTreeTransaction<IResult>(user, () =>
            {
                var before = store.LastCommit;
                var result = store.ApplyWorkTree(input.BaseRevision, direction, input.BaselineHash, input.Confirmed);
                var after = store.LastCommit;
                if (after.Serial > before.Serial)
                    hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(context), "structure");
                return result;
            }, EditLeaseApi.Failure);
        });

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
