using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

// Only server-approved skills and installed local workflows are exposed;
// never accept a workflow graph or tool definition from the client.
internal static class WebSkillJobApi
{
    private const string SkillId = "comfyui.text-to-image";
    private const string VideoSkillId = "web.text-to-video";
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

    public static Func<string, HttpContext, JsonElement, Task<IResult>> Map(WebApplication app)
    {
        var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
        var attachments = app.Services.GetRequiredService<WebJobAttachmentService>();
        var assetDirectory = Path.GetFullPath(
            LegacyConfig.Text(app.Configuration, "AssetDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "assets"));

        static string? ResolveAsset(string? reference, string directory)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var fileName = reference.StartsWith("asset://", StringComparison.Ordinal)
                ? Path.GetFileName(reference["asset://".Length..])
                : null;
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var root = Path.GetFullPath(directory);
            var path = Path.GetFullPath(Path.Combine(root, fileName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(path) ? path : null;
        }
        SessionContext Session(HttpContext context)
        {
            var user = WebAccessGuard.CurrentUser(context);
            return new SessionContext { UserId = user?.Id ?? Guid.Empty, SessionId = user?.Id ?? Guid.Empty, ClientType = ClientType.Web,
                Role = MemberRole.Member, ServerClaims = user is null ? new HashSet<string>() : new HashSet<string>(WebAccessGuard.Permissions(context), StringComparer.Ordinal) };
        }
        bool ImageApproved() => string.Equals(LegacyConfig.Text(app.Configuration, "WebSkillApprovalMode"), "preapproved-local-image", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(app.Configuration["ComfyUI:Checkpoint"]);
        bool VideoApproved()
        {
            if (!string.Equals(LegacyConfig.Text(app.Configuration, "WebVideoApprovalMode"), "preapproved-local-video", StringComparison.Ordinal))
                return false;
            if (string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(app.Configuration["ComfyUI:BaseUrl"]);
            return !string.IsNullOrWhiteSpace(LegacyConfig.Text(app.Configuration, "VideoModel"))
                && !string.IsNullOrWhiteSpace(LegacyConfig.Text(app.Configuration, "VideoEndpoint"));
        }
        WebWorkflowCatalog.Entry? Workflow(string id) => WebWorkflowCatalog.Load(app.Configuration["ComfyUI:BaseUrl"]!).FirstOrDefault(e => e.Id == id);
        bool Known(string id) => id is SkillId or VideoSkillId || WebWorkflowCatalog.IsWorkflow(id);
        bool InvokeAllowed(SessionContext session) => session.ServerClaims.Contains("skill.invoke");
        bool SkillAllowed(SessionContext session, string skillId) => InvokeAllowed(session)
            && (skillId == SkillId ? ImageApproved() : skillId == VideoSkillId ? VideoApproved()
                : app.Configuration["WebWorkflowApprovalMode"] == "preapproved-local-workflows" && Workflow(skillId) is not null);
        IResult Denied() => Error(403, "SKILL_NOT_APPROVED", "技能未获服务端预授权或缺少 skill.invoke 权限");
        object View(Job job, SessionContext session)
        {
            var result = job.ToResult();
            var canRetry = SkillAllowed(session, result.Tool) && execution.CanRetryJob(job.JobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out _);
            return new { result.JobId, result.InvocationId, state = result.State.ToString(), capability = result.Capability.ToString(), result.ProgressPercent,
                result.ErrorCode, result.ErrorMessage, result.ExternalTaskId, result.Outputs, result.Attempt,
                result.RetryOfJobId, result.RootJobId, result.Tool, canRetry,
                targetNodeId = job.Inputs.TryGetValue("targetNodeId", out var target) ? target.GetString() : null,
                attachmentState = attachments.State(job) };
        }
        bool InputsEqual(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
            left.Count == right.Count && right.All(pair => left.TryGetValue(pair.Key, out var value) && value.GetRawText() == pair.Value.GetRawText());
        IResult Find(Guid id, SessionContext session, out Job? job)
        {
            if (!execution.TryGet(id, out job)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job!.UserId != session.UserId || !WebJobAttachmentService.InCurrentCanvas(job)) { job = null; return Error(404, "JOB_NOT_FOUND", "任务不存在"); }
            return Results.Empty;
        }
        app.MapGet("/api/web/skills", (HttpContext context) =>
        {
            var session = Session(context);
            var skills = new List<object>();
            if (SkillAllowed(session, SkillId)) skills.Add(new { id = SkillId, name = "本机 ComfyUI 文生图", capability = "TextToImage", approval = "preapproved-local-image" });
            if (SkillAllowed(session, VideoSkillId)) skills.Add(new { id = VideoSkillId, name = "Web 文生视频", capability = "TextToVideo", approval = "preapproved-local-video" });
            if (InvokeAllowed(session) && app.Configuration["WebWorkflowApprovalMode"] == "preapproved-local-workflows")
                skills.AddRange(WebWorkflowCatalog.Load(app.Configuration["ComfyUI:BaseUrl"]!).Select(e => (object)new {
                    id = e.Id, name = e.Workflow.Title, capability = e.Capability.ToString(), approval = "preapproved-local-workflows",
                    inputs = new[] { "prompt" }, outputs = new[] { e.Workflow.Kind } }));
            return Results.Json(new { skills = skills.ToArray() });
        });
        async Task<IResult> Invoke(string skillId, HttpContext context, JsonElement body)
        {
            var session = Session(context);
            if (session.UserId == Guid.Empty) return Error(403, "LOGIN_REQUIRED", "技能任务需要账号登录");
            var workflow = WebWorkflowCatalog.IsWorkflow(skillId) ? Workflow(skillId) : null;
            if (skillId != SkillId && skillId != VideoSkillId && workflow is null) return Error(404, "SKILL_NOT_FOUND", "技能不存在");
            if (!SkillAllowed(session, skillId)) return Denied();
            if (body.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_REQUEST", "请求必须是 JSON 对象");
            var isVideo = skillId == VideoSkillId;
            var allowedFields = workflow is not null ? new[] { "prompt", "idempotencyKey", "targetNodeId" } : isVideo
                ? new[] { "prompt", "negativePrompt", "model", "seconds", "width", "height", "videoFrames", "seed", "idempotencyKey", "targetNodeId", "chatSessionId", "referenceAttachmentIds" }
                : new[] { "prompt", "idempotencyKey", "targetNodeId", "chatSessionId", "referenceAttachmentIds" };
            var invalidVideoFields = isVideo && (
                (body.TryGetProperty("negativePrompt", out var negative) && (negative.ValueKind != JsonValueKind.String || negative.GetString()!.Length > 4000)) ||
                (body.TryGetProperty("width", out var width) && (width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var widthValue) || widthValue is < 64 or > 2048)) ||
                (body.TryGetProperty("height", out var height) && (height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out var heightValue) || heightValue is < 64 or > 2048)) ||
                (body.TryGetProperty("videoFrames", out var frames) && (frames.ValueKind != JsonValueKind.Number || !frames.TryGetInt32(out var frameValue) || frameValue is < 1 or > 4096)) ||
                (body.TryGetProperty("seed", out var seed) && (seed.ValueKind != JsonValueKind.Number || !seed.TryGetInt64(out _))));
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(prompt.GetString()) || prompt.GetString()!.Length > 4000 ||
                !body.TryGetProperty("idempotencyKey", out var key) || key.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) || key.GetString()!.Length > 128 ||
                (body.TryGetProperty("model", out var model) && (model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()) || model.GetString()!.Length > 256)) ||
                (body.TryGetProperty("seconds", out var seconds) && (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var secondsValue) || secondsValue is < 1 or > 600)) ||
                body.EnumerateObject().Any(p => !allowedFields.Contains(p.Name, StringComparer.Ordinal)) || invalidVideoFields)
                return Error(400, "INVALID_REQUEST", isVideo
                    ? "仅接受 prompt、negativePrompt、model、seconds（1-600）、width、height、videoFrames、seed 与 idempotencyKey"
                    : "图片技能仅接受 prompt 与 idempotencyKey");
            if (isVideo && !string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase) &&
                new[] { "negativePrompt", "width", "height", "videoFrames", "seed" }.Any(field => body.TryGetProperty(field, out _)))
                return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前视频执行器仅支持 prompt、model、seconds，拒绝忽略额外参数");
            var inputs = new Dictionary<string, JsonElement> { ["prompt"] = prompt.Clone(), ["webCanvasPath"] = JsonSerializer.SerializeToElement(WebProjectApi.Scope) };
            if (body.TryGetProperty("referenceAttachmentIds", out var references))
            {
                if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() is < 1 or > 8 || !body.TryGetProperty("chatSessionId", out var chatSession) || chatSession.ValueKind != JsonValueKind.String)
                    return Error(400, "REFERENCE_INVALID", "参考图须来自当前会话，最多8张");
                if (isVideo && !string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase))
                    return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前视频执行器不支持参考图");
                if (!isVideo && references.GetArrayLength() > 1) return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前图片工作流仅支持一张底图，不能静默忽略多图");
                var files = new List<WebChatStore.Attachment>();
                foreach (var reference in references.EnumerateArray())
                {
                    if (reference.ValueKind != JsonValueKind.String || WebChatApi.Attachment(context, chatSession.GetString()!, reference.GetString()!) is not { } file || !file.Type.StartsWith("image/"))
                        return Error(400, "REFERENCE_INVALID", "参考图不存在或不是当前会话图片");
                    files.Add(file);
                }
                var referenceDirectory = Path.Combine(assetDirectory, "web-references"); Directory.CreateDirectory(referenceDirectory);
                var paths = new List<string>();
                foreach (var file in files)
                {
                    var filePath = Path.Combine(referenceDirectory, file.Id + (file.Type == "image/png" ? ".png" : ".jpg"));
                    await File.WriteAllBytesAsync(filePath, file.Data, context.RequestAborted); paths.Add(filePath);
                }
                inputs["referenceImages"] = JsonSerializer.SerializeToElement(paths);
            }
            else if (body.TryGetProperty("chatSessionId", out _)) return Error(400, "REFERENCE_INVALID", "会话参数须与参考图一起提供");
            if (body.TryGetProperty("targetNodeId", out var targetNode))
            {
                if (targetNode.ValueKind != JsonValueKind.String || !targetNode.TryGetGuid(out _) || WebProjectApi.Current is not { IsProject: true } mode)
                    return Error(400, "TARGET_INVALID", "目标必须是当前项目画布的节点 ID");
                if (!session.ServerClaims.Contains("canvas.edit")) return Error(403, "TARGET_FORBIDDEN", "回挂产物需要 canvas.edit 权限");
                try
                {
                    var snapshot = WebProjectApi.Store(mode)!.AgentContextSnapshot(null, targetNode.GetString());
                    inputs["targetBaseRevision"] = JsonSerializer.SerializeToElement(snapshot.Revision);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return Error(409, "TARGET_INVALID", "目标节点不可用"); }
                inputs["targetNodeId"] = targetNode.Clone();
            }
            if (isVideo && body.TryGetProperty("model", out var selectedModel)) inputs["model"] = selectedModel.Clone();
            if (isVideo && body.TryGetProperty("seconds", out var selectedSeconds)) inputs["seconds"] = selectedSeconds.Clone();
            if (isVideo && body.TryGetProperty("negativePrompt", out var negativePrompt)) inputs["negativePrompt"] = negativePrompt.Clone();
            if (isVideo && body.TryGetProperty("width", out var selectedWidth)) inputs["width"] = selectedWidth.Clone();
            if (isVideo && body.TryGetProperty("height", out var selectedHeight)) inputs["height"] = selectedHeight.Clone();
            if (isVideo && body.TryGetProperty("videoFrames", out var selectedFrames)) inputs["videoFrames"] = selectedFrames.Clone();
            if (isVideo && body.TryGetProperty("seed", out var selectedSeed)) inputs["seed"] = selectedSeed.Clone();
            if (workflow is not null)
            {
                inputs["workflowRevision"] = JsonSerializer.SerializeToElement(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflow.Payload))));
                inputs["declaredOutputRole"] = JsonSerializer.SerializeToElement(workflow.Workflow.Kind);
            }
            var existing = execution.GetJobs(session.UserId).FirstOrDefault(j => j.IdempotencyKey == key.GetString());
            if (existing is not null)
            {
                if (existing.Inputs.TryGetValue("targetBaseRevision", out var originalRevision))
                    inputs["targetBaseRevision"] = originalRevision;
                else inputs.Remove("targetBaseRevision");
                if (existing.Tool != skillId || !InputsEqual(existing.Inputs, inputs))
                    return Error(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他调用");
                return Results.Json(View(existing, session));
            }
            var invocation = new Invocation { Tool = skillId, Capability = workflow?.Capability ?? (isVideo ? Capability.TextToVideo : Capability.TextToImage), Channel = isVideo ? "video" : "comfyui", Inputs = inputs };
            // StartAsync returns after submission; failed jobs remain visible rather than becoming false positives.
            var result = await execution.StartAsync(session, invocation, key.GetString()!);
            execution.TryGet(result.JobId, out var job);
            return Results.Json(View(job!, session));
        }
        app.MapPost("/api/web/skills/{skillId}/invoke", async (string skillId, HttpRequest request) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
                return await Invoke(skillId, request.HttpContext, document.RootElement.Clone());
            }
            catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
        });
        app.MapGet("/api/web/jobs", (HttpContext context) =>
        {
            var session = Session(context);
            return Results.Json(new { jobs = execution.GetJobs(session.UserId)
                .Where(j => Known(j.Tool) && WebJobAttachmentService.InCurrentCanvas(j))
                .Select(j => View(j, session)).ToArray() });
        });
        app.MapGet("/api/web/jobs/{jobId:guid}", (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var job);
            return job is null ? found : Known(job.Tool) ? Results.Json(View(job, session)) : Error(404, "JOB_NOT_FOUND", "任务不存在");
        });
        app.MapGet("/api/web/jobs/{jobId:guid}/outputs/{outputIndex:int}", (HttpContext context, Guid jobId, int outputIndex) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (!Known(job.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State != JobState.Succeeded || outputIndex < 0 || outputIndex >= job.Outputs.Count)
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物不存在");
            var path = ResolveAsset(job.Outputs[outputIndex].Ref, assetDirectory);
            if (path is null) return Error(404, "OUTPUT_NOT_FOUND", "任务产物文件不存在");

            // Open the resolved asset explicitly. This keeps the response tied to the
            // ownership-checked path and avoids relying on late physical-file resolution.
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var contentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".mp4" => "video/mp4",
                    ".webm" => "video/webm",
                    ".mov" => "video/quicktime",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                };
                return Results.Stream(stream, contentType, enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物文件不存在");
            }
            catch (DirectoryNotFoundException)
            {
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物目录不存在");
            }
            catch (UnauthorizedAccessException)
            {
                return Error(403, "OUTPUT_FORBIDDEN", "任务产物不可读取");
            }
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/cancel", (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            if (!session.ServerClaims.Contains("job.cancel")) return Error(403, "JOB_FORBIDDEN", "没有取消权限");
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (!Known(job.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State is not (JobState.Queued or JobState.Running)) return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消");
            try { execution.Cancel(session, jobId); return Results.Json(View(job, session)); }
            catch (ProtocolViolationException) { return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消"); }
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/retry", async (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var source);
            if (source is null) return found;
            if (!Known(source.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (!SkillAllowed(session, source.Tool)) return Denied();
            if (!execution.CanRetryJob(jobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out var reason))
                return Error(409, "JOB_NOT_RETRYABLE", reason);
            try
            {
                var result = await execution.RetryAsync(session, jobId, Guid.NewGuid().ToString("N"));
                execution.TryGet(result.JobId, out var job);
                return Results.Json(View(job!, session));
            }
            catch (ProtocolViolationException ex) { return Error(409, ex.Code, ex.Message); }
        });
        return Invoke;
    }
}
