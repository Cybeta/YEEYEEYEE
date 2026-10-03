using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YEEYEEYEE.Web;

internal static class WebSceneApi
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        // 身份与权限由 WebAccessGuard 统一处理（会话 cookie 或桌面桥的 Bearer + 本机），
        // 它必须在这个方法之前注册。这里只管「解析出来的权限够不够改画布」。
        // A standalone JSON scene is not a desktop project canvas. Never silently fall back
        // to it when a project canvas is requested (or when no editor mode was selected).
        var mode = WebCanvasMode.Resolve(app.Configuration);
        WebSceneStore? store = null;
        ProjectCanvasSceneStore? projectStore = null;
        var unavailable = mode.Error;
        if (mode.Error is null)
        {
            // 解析在 WebCanvasMode 里（编辑锁接口共用同一份规则），这里只负责按模式建对应的存储。
            if (mode.IsProject) projectStore = new ProjectCanvasSceneStore(mode.CanvasPath!, mode.EntitiesPath);
            else store = new WebSceneStore(mode.CanvasPath!);
        }
        // 变更推送的中枢：写成功之后往它发一条「画布变了」。
        var hub = app.Services.GetRequiredService<CanvasEventHub>();
        // 两个模式共用一句：谁都不是就回 (0,0)，序号比较自然不成立（也就不会广播）。
        (long Serial, long Revision) CommitToken() => projectStore?.LastCommit ?? store?.LastCommit ?? (0, 0);

        app.MapGet("/api/web/scene", () => projectStore is not null ? projectStore.Read() : store is not null ? store.Read() : unavailable!);

        // 结构级写入（新建 / 删除节点）要**整棵树锁**：它们改的是画布结构（多一个节点、少几条连线），
        // 别人正占着任何节点时都可能被这次改动影响——与整理布局同一条规矩。
        var structureLeases = mode.CanvasPath is null
            ? null
            : new EditLeaseStore(mode.CanvasPath, EditLeaseApi.LifetimeOf(app.Configuration));

        // 新建节点：position 由服务端算（见 ProjectCanvasSceneStore.CreateNode），
        // 客户端只说「在哪一章建一个什么类别的」。
        app.MapPost("/api/web/records", async (HttpRequest request) =>
        {
            if (projectStore is null || structureLeases is null)
                return Error(409, "CANVAS_REQUIRES_PROJECT", "新建节点要的是项目画布；当前配置的是独立 Web 场景");
            var user = WebAccessGuard.CurrentUser(request.HttpContext);
            if (user is null)
                return Error(403, "CANVAS_WRITE_REQUIRES_SESSION", "结构改动要记在某个账号名下，必须用账号登录");
            if (!WebAccessGuard.Permissions(request.HttpContext).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");

            JsonObject body;
            try
            {
                if (await JsonNode.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted) is not JsonObject parsed)
                    return Error(400, "INVALID_REQUEST", "请求体要是一个 JSON 对象");
                body = parsed;
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_REQUEST", "请求体不是合法 JSON");
            }

            if (body["baseRevision"] is not JsonValue revisionValue || !revisionValue.TryGetValue<long>(out var baseRevision))
                return Error(400, "INVALID_REQUEST", "缺少 baseRevision（你手上那份的修订号）");
            var recordType = body["recordType"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeText)
                ? typeText : null;
            // 认不出的类别**如实拒绝**，不猜一个 general 收下：猜错了会静默地建出一个错类别的节点。
            if (YEEYEEYEE.Desktop.NodeProjection.RecordTypeToCategory(recordType) is not { } category)
                return Error(400, "CANVAS_UNKNOWN_RECORD_TYPE", "认不出这个类别：" + (recordType ?? "(缺失)"));
            var title = body["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var titleText) ? titleText : null;
            Guid? chapterId = body["chapterId"] is JsonValue chapterValue &&
                              chapterValue.TryGetValue<string>(out var chapterText) && Guid.TryParse(chapterText, out var chapterGuid)
                ? chapterGuid : null;

            var before = CommitToken();
            var lease = structureLeases.Acquire(EditScope.Tree, null, user, "web", force: false);
            if (lease.Status != EditLeaseStatus.Ok) return EditLeaseApi.Failure(lease);

            IResult result;
            try { result = projectStore.CreateNode(baseRevision, category, title, chapterId); }
            finally
            {
                // 与整理布局同理：锁只为这一次结构改动排队，写完就还回去。
                structureLeases.Release(lease.Lease!.LeaseId, user, force: false);
            }

            var after = CommitToken();
            if (after.Serial > before.Serial)
                hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(request.HttpContext), "structure");
            return result;
        });

        // 删除节点（连带它的连线）。章节是工作树条目、不在画布节点里，所以这里天生删不到它。
        app.MapDelete("/api/web/records/{recordId}", (string recordId, long? baseRevision, HttpRequest request) =>
        {
            if (projectStore is null || structureLeases is null)
                return Error(409, "CANVAS_REQUIRES_PROJECT", "删除节点要的是项目画布；当前配置的是独立 Web 场景");
            var user = WebAccessGuard.CurrentUser(request.HttpContext);
            if (user is null)
                return Error(403, "CANVAS_WRITE_REQUIRES_SESSION", "结构改动要记在某个账号名下，必须用账号登录");
            if (!WebAccessGuard.Permissions(request.HttpContext).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
            if (baseRevision is not { } revision)
                return Error(400, "INVALID_REQUEST", "缺少 baseRevision（你手上那份的修订号）");
            if (!Guid.TryParse(recordId, out var nodeId))
                return Error(400, "INVALID_REQUEST", "这个 recordId 不是一个画布节点（章节是工作树条目，不从这条路删）");

            var before = CommitToken();
            var lease = structureLeases.Acquire(EditScope.Tree, null, user, "web", force: false);
            if (lease.Status != EditLeaseStatus.Ok) return EditLeaseApi.Failure(lease);

            IResult result;
            try { result = projectStore.DeleteNode(revision, nodeId); }
            finally
            {
                structureLeases.Release(lease.Lease!.LeaseId, user, force: false);
            }

            var after = CommitToken();
            if (after.Serial > before.Serial)
                hub.CanvasChanged(after.Revision, null, WebAccessGuard.ActorName(request.HttpContext), "structure");
            return result;
        });
        app.MapPut("/api/web/records/{recordId}", async (string recordId, HttpRequest request) =>
        {
            // Read the resolved permissions for every write; neither Bearer nor a session cookie alone
            // is edit authority. For a logged-in browser user they come from the role (Viewer gets none),
            // for the desktop bridge from the server-side WebClaims config (re-read live, so revoking
            // a claim takes effect on the next request without a restart).
            var claims = WebAccessGuard.Permissions(request.HttpContext);
            if (!claims.Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限");
            // 写入前后比一次提交序号：只有真的落了盘才广播。失败与「本来就无需改动」都不广播——
            // 让所有人白刷一次的后果是，久了就没人信这条推送了。比的是序号而不是修订号：
            // 项目模式的修订号是内容哈希，新哈希不保证比旧的大。
            var before = CommitToken();
            var result = projectStore is not null ? await projectStore.Update(recordId, request)
                : store is not null ? await store.Update(recordId, request) : unavailable!;
            var after = CommitToken();
            if (after.Serial > before.Serial)
                hub.CanvasChanged(after.Revision, recordId, WebAccessGuard.ActorName(request.HttpContext), "record");
            return result;
        });
        app.MapGet("/api/web/assets", () =>
        {
            var entitiesPath = LegacyConfig.Text(app.Configuration, "ProjectEntitiesPath");
            if (string.IsNullOrWhiteSpace(entitiesPath))
                return Error(503, "ASSETS_NOT_CONFIGURED", "项目库 entities.json 路径未配置");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(entitiesPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("entities", out var entities) ||
                    entities.ValueKind != JsonValueKind.Array)
                    return Error(500, "ASSETS_INVALID", "项目库 entities.json 格式无效");
                return Results.Json(new { entities = entities.Clone() });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return Error(500, "ASSETS_READ_FAILED", "项目库 entities.json 读取失败");
            }
        });
    }

    private sealed class WebSceneStore(string filePath)
    {
        private readonly object gate = new();
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>
        /// 最近一次成功提交的（提交序号，对外修订号）。与项目画布那份同理，只是这里简单得多：
        /// 独立场景的修订号就是文件里那个整数，本来就单调，两个数一样能用。
        /// 形状保持一致，接口层才不必为两种模式写两套判断。
        /// </summary>
        public (long Serial, long Revision) LastCommit { get; private set; }

        private static bool Valid(JsonNode? root)
        {
            if (root is not JsonObject scene || scene["records"] is not JsonArray records ||
                scene["revision"] is not JsonValue revision || !revision.TryGetValue<int>(out var number) || number < 0)
                return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in records)
            {
                if (entry is not JsonObject record || record["recordId"] is not JsonValue id ||
                    !id.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value) ||
                    record["recordType"] is not JsonValue type || !type.TryGetValue<string>(out _) ||
                    record["record"] is not JsonObject || !ids.Add(value))
                    return false;
            }
            return true;
        }

        private JsonNode Load() => File.Exists(filePath)
            ? JsonNode.Parse(File.ReadAllText(filePath)) ?? throw new JsonException("空场景")
            : new JsonObject { ["revision"] = 0, ["records"] = new JsonArray() };

        public IResult Read()
        {
            lock (gate)
            {
                try
                {
                    var scene = Load();
                    return Valid(scene) ? Results.Json(scene) : Error(500, "SCENE_INVALID", "场景文件格式无效");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    return Error(500, "SCENE_READ_FAILED", "场景文件读取失败");
                }
            }
        }

        public async Task<IResult> Update(string recordId, HttpRequest request)
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted); }
            catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
            if (body is not JsonObject input || input["baseRevision"] is not JsonValue baseValue ||
                !baseValue.TryGetValue<int>(out var baseRevision) || baseRevision < 0 ||
                input["title"] is not JsonValue titleValue || !titleValue.TryGetValue<string>(out var title) ||
                input["content"] is not JsonValue contentValue || !contentValue.TryGetValue<string>(out var content))
                return Error(400, "INVALID_REQUEST", "需要 baseRevision、title、content");

            lock (gate)
            {
                try
                {
                    var scene = Load();
                    if (!Valid(scene)) return Error(500, "SCENE_INVALID", "场景文件格式无效");
                    var current = scene["revision"]!.GetValue<int>();
                    if (baseRevision != current) return Error(409, "SCENE_REVISION_CONFLICT", "场景修订冲突，请重新加载");
                    var record = ((JsonArray)scene["records"]!).OfType<JsonObject>()
                        .FirstOrDefault(item => item["recordId"]!.GetValue<string>() == recordId);
                    if (record is null) return Error(404, "RECORD_NOT_FOUND", "记录不存在");
                    if (current == int.MaxValue) return Error(409, "SCENE_REVISION_CONFLICT", "场景修订已达上限");
                    var next = scene.DeepClone();
                    var updated = ((JsonArray)next["records"]!).OfType<JsonObject>()
                        .First(item => item["recordId"]!.GetValue<string>() == recordId);
                    var fields = (JsonObject)updated["record"]!;
                    fields["title"] = title;
                    fields["content"] = content;
                    next["revision"] = current + 1;
                    var directory = Path.GetDirectoryName(filePath)!;
                    Directory.CreateDirectory(directory);
                    var temp = Path.Combine(directory, $".web-scene-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllText(temp, next.ToJsonString(Options), Encoding.UTF8);
                        File.Move(temp, filePath, overwrite: true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    LastCommit = (LastCommit.Serial + 1, current + 1);
                    return Results.Json(new { revision = current + 1, record = updated });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    return Error(500, "SCENE_WRITE_FAILED", "场景保存失败，旧版本保持不变");
                }
            }
        }
    }
}
