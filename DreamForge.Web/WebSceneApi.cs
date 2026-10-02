using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DreamForge.Web;

internal static class WebSceneApi
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        // The desktop bridge is a separate protocol. Its old anonymous HTTP and WebSocket
        // entry points must not bypass the web editor's authentication boundary.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/web") ||
                context.Request.Path.StartsWithSegments("/api/canvas") ||
                context.Request.Path.StartsWithSegments("/ws/canvas"))
            {
                if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
                {
                    await Error(403, "LOCAL_ONLY", "仅允许本机访问").ExecuteAsync(context);
                    return;
                }
                var configured = app.Configuration["DreamForge:WebToken"];
                if (string.IsNullOrWhiteSpace(configured))
                {
                    await Error(503, "TOKEN_NOT_CONFIGURED", "Web 访问令牌未配置").ExecuteAsync(context);
                    return;
                }
                var authorization = context.Request.Headers.Authorization.ToString();
                var candidate = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization[7..] : string.Empty;
                if (candidate.Length == 0 || candidate.Contains(' ') ||
                    !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
                        SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
                {
                    await Error(401, "UNAUTHORIZED", "Bearer 令牌无效").ExecuteAsync(context);
                    return;
                }
            }
            await next(context);
        });

        // A standalone JSON scene is not a desktop project canvas. Never silently fall back
        // to it when a project canvas is requested (or when no editor mode was selected).
        var projectCanvasPath = app.Configuration["DreamForge:ProjectCanvasPath"];
        var standalonePath = app.Configuration["DreamForge:WebScenePath"];
        WebSceneStore? store = null;
        ProjectCanvasSceneStore? projectStore = null;
        IResult? unavailable = null;
        if (!string.IsNullOrWhiteSpace(projectCanvasPath))
        {
            try { projectStore = new ProjectCanvasSceneStore(projectCanvasPath, app.Configuration["DreamForge:ProjectEntitiesPath"]); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                unavailable = Error(503, "PROJECT_CANVAS_UNAVAILABLE", "项目上下文不可信，拒绝回退到独立 Web 场景：" + ex.Message);
            }
        }
        else if (!app.Configuration.GetValue<bool>("DreamForge:AllowStandaloneWebScene"))
            unavailable = Error(503, "SCENE_MODE_NOT_CONFIGURED", "独立 Web 场景需显式启用；项目画布编辑当前不可用");
        else if (string.IsNullOrWhiteSpace(standalonePath) || !Path.IsPathFullyQualified(standalonePath))
            unavailable = Error(503, "SCENE_PATH_NOT_CONFIGURED", "独立 Web 场景需配置绝对路径");
        else
            store = new WebSceneStore(Path.GetFullPath(standalonePath));
        app.MapGet("/api/web/scene", () => projectStore is not null ? projectStore.Read() : store is not null ? store.Read() : unavailable!);
        app.MapPut("/api/web/records/{recordId}", (string recordId, HttpRequest request) =>
        {
            // Read the server-side claims for every write; Bearer alone is not edit authority.
            var claims = (app.Configuration["DreamForge:WebClaims"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!claims.Contains("canvas.edit", StringComparer.Ordinal))
                return Task.FromResult(Error(403, "CANVAS_EDIT_FORBIDDEN", "缺少 canvas.edit 权限"));
            return projectStore is not null ? projectStore.Update(recordId, request) :
                store is not null ? store.Update(recordId, request) : Task.FromResult(unavailable!);
        });
        app.MapGet("/api/web/assets", () =>
        {
            var entitiesPath = app.Configuration["DreamForge:ProjectEntitiesPath"];
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
