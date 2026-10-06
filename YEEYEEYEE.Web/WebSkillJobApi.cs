using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

// Only the local, explicitly pre-approved image workflow is exposed. There is no Web
// approval ledger or general-purpose skill registry; never accept a tool from the client.
internal static class WebSkillJobApi
{
    private const string SkillId = "comfyui.text-to-image";
    private const string VideoSkillId = "web.text-to-video";
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
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
        SessionContext Session()
        {
            var token = LegacyConfig.Text(app.Configuration, "WebToken") ?? "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("yeeeyee-web-user:" + token));
            var claims = (LegacyConfig.Text(app.Configuration, "WebClaims") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new SessionContext { UserId = new Guid(hash.AsSpan(0, 16)), SessionId = new Guid(hash.AsSpan(16, 16)), ClientType = ClientType.Web,
                Role = MemberRole.Member, ServerClaims = new HashSet<string>(claims, StringComparer.Ordinal) };
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
        bool InvokeAllowed(SessionContext session) => session.ServerClaims.Contains("skill.invoke");
        bool SkillAllowed(SessionContext session, string skillId) => InvokeAllowed(session)
            && (skillId == SkillId ? ImageApproved() : skillId == VideoSkillId && VideoApproved());
        IResult Denied() => Error(403, "SKILL_NOT_APPROVED", "技能未获服务端预授权或缺少 skill.invoke 权限");
        object View(Job job, SessionContext session)
        {
            var result = job.ToResult();
            var canRetry = SkillAllowed(session, result.Tool) && execution.CanRetryJob(job.JobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out _);
            return new { result.JobId, result.InvocationId, state = result.State.ToString(), capability = result.Capability.ToString(), result.ProgressPercent,
                result.ErrorCode, result.ErrorMessage, result.ExternalTaskId, result.Outputs, result.Attempt,
                result.RetryOfJobId, result.RootJobId, result.Tool, canRetry };
        }
        bool InputsEqual(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
            left.Count == right.Count && right.All(pair => left.TryGetValue(pair.Key, out var value) && value.GetRawText() == pair.Value.GetRawText());
        IResult Find(Guid id, SessionContext session, out Job? job)
        {
            if (!execution.TryGet(id, out job)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job!.UserId != session.UserId) { job = null; return Error(404, "JOB_NOT_FOUND", "任务不存在"); }
            return Results.Empty;
        }
        app.MapGet("/api/web/skills", () =>
        {
            var session = Session();
            var skills = new List<object>();
            if (SkillAllowed(session, SkillId)) skills.Add(new { id = SkillId, name = "本机 ComfyUI 文生图", capability = "TextToImage", approval = "preapproved-local-image" });
            if (SkillAllowed(session, VideoSkillId)) skills.Add(new { id = VideoSkillId, name = "Web 文生视频", capability = "TextToVideo", approval = "preapproved-local-video" });
            return Results.Json(new { skills = skills.ToArray() });
        });
        app.MapPost("/api/web/skills/{skillId}/invoke", async (string skillId, HttpRequest request) =>
        {
            var session = Session();
            if (skillId != SkillId && skillId != VideoSkillId) return Error(404, "SKILL_NOT_FOUND", "技能不存在");
            if (!SkillAllowed(session, skillId)) return Denied();
            JsonElement body;
            try { using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted); body = document.RootElement.Clone(); }
            catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
            var isVideo = skillId == VideoSkillId;
            var allowedFields = isVideo
                ? new[] { "prompt", "negativePrompt", "model", "seconds", "width", "height", "videoFrames", "seed", "idempotencyKey" }
                : new[] { "prompt", "idempotencyKey" };
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
            var inputs = new Dictionary<string, JsonElement> { ["prompt"] = prompt.Clone() };
            if (isVideo && body.TryGetProperty("model", out var selectedModel)) inputs["model"] = selectedModel.Clone();
            if (isVideo && body.TryGetProperty("seconds", out var selectedSeconds)) inputs["seconds"] = selectedSeconds.Clone();
            if (isVideo && body.TryGetProperty("negativePrompt", out var negativePrompt)) inputs["negativePrompt"] = negativePrompt.Clone();
            if (isVideo && body.TryGetProperty("width", out var selectedWidth)) inputs["width"] = selectedWidth.Clone();
            if (isVideo && body.TryGetProperty("height", out var selectedHeight)) inputs["height"] = selectedHeight.Clone();
            if (isVideo && body.TryGetProperty("videoFrames", out var selectedFrames)) inputs["videoFrames"] = selectedFrames.Clone();
            if (isVideo && body.TryGetProperty("seed", out var selectedSeed)) inputs["seed"] = selectedSeed.Clone();
            var existing = execution.GetJobs(session.UserId).FirstOrDefault(j => j.IdempotencyKey == key.GetString());
            if (existing is not null && (existing.Tool != skillId || !InputsEqual(existing.Inputs, inputs)))
                return Error(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他调用");
            var invocation = new Invocation { Tool = skillId, Capability = isVideo ? Capability.TextToVideo : Capability.TextToImage, Channel = isVideo ? "video" : "comfyui", Inputs = inputs };
            // StartAsync returns after submission; failed jobs remain visible rather than becoming false positives.
            var result = await execution.StartAsync(session, invocation, key.GetString()!);
            execution.TryGet(result.JobId, out var job);
            return Results.Json(View(job!, session));
        });
        app.MapGet("/api/web/jobs", () =>
        {
            var session = Session();
            return Results.Json(new { jobs = execution.GetJobs(session.UserId)
                .Where(j => j.Tool == SkillId || j.Tool == VideoSkillId)
                .Select(j => View(j, session)).ToArray() });
        });
        app.MapGet("/api/web/jobs/{jobId:guid}", (Guid jobId) =>
        {
            var session = Session();
            var found = Find(jobId, session, out var job);
            return job is null ? found : job.Tool is SkillId or VideoSkillId ? Results.Json(View(job, session)) : Error(404, "JOB_NOT_FOUND", "任务不存在");
        });
        app.MapGet("/api/web/jobs/{jobId:guid}/outputs/{outputIndex:int}", (Guid jobId, int outputIndex) =>
        {
            var session = Session();
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (job.Tool is not (SkillId or VideoSkillId)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
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
        app.MapPost("/api/web/jobs/{jobId:guid}/cancel", (Guid jobId) =>
        {
            var session = Session();
            if (!session.ServerClaims.Contains("job.cancel")) return Error(403, "JOB_FORBIDDEN", "没有取消权限");
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (job.Tool is not (SkillId or VideoSkillId)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State is not (JobState.Queued or JobState.Running)) return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消");
            try { execution.Cancel(session, jobId); return Results.Json(View(job, session)); }
            catch (ProtocolViolationException) { return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消"); }
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/retry", async (Guid jobId) =>
        {
            var session = Session();
            var found = Find(jobId, session, out var source);
            if (source is null) return found;
            if (source.Tool is not (SkillId or VideoSkillId)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
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
    }
}
