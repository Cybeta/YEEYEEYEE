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
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
        SessionContext Session()
        {
            var token = LegacyConfig.Text(app.Configuration, "WebToken") ?? "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("yeeeyee-web-user:" + token));
            var claims = (LegacyConfig.Text(app.Configuration, "WebClaims") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new SessionContext { UserId = new Guid(hash.AsSpan(0, 16)), SessionId = new Guid(hash.AsSpan(16, 16)), ClientType = ClientType.Web,
                Role = MemberRole.Member, ServerClaims = new HashSet<string>(claims, StringComparer.Ordinal) };
        }
        bool Approved() => string.Equals(LegacyConfig.Text(app.Configuration, "WebSkillApprovalMode"), "preapproved-local-image", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(app.Configuration["ComfyUI:Checkpoint"]);
        bool InvokeAllowed(SessionContext session) => Approved() && session.ServerClaims.Contains("skill.invoke");
        IResult Denied() => Error(403, "SKILL_NOT_APPROVED", "本机图像技能未获服务端预授权或缺少 skill.invoke 权限");
        object View(Job job, SessionContext session)
        {
            var result = job.ToResult();
            var canRetry = InvokeAllowed(session) && result.Tool == SkillId && execution.CanRetryJob(job.JobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out _);
            return new { result.JobId, result.InvocationId, state = result.State.ToString(), result.ProgressPercent,
                result.ErrorCode, result.ErrorMessage, result.ExternalTaskId, result.Outputs, result.Attempt,
                result.RetryOfJobId, result.RootJobId, result.Tool, canRetry };
        }
        IResult Find(Guid id, SessionContext session, out Job? job)
        {
            if (!execution.TryGet(id, out job)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job!.UserId != session.UserId) { job = null; return Error(404, "JOB_NOT_FOUND", "任务不存在"); }
            return Results.Empty;
        }
        app.MapGet("/api/web/skills", () =>
        {
            var session = Session();
            return Results.Json(new { skills = InvokeAllowed(session) ? new[] { new { id = SkillId, name = "本机 ComfyUI 文生图", capability = "TextToImage", approval = "preapproved-local-image" } } : Array.Empty<object>() });
        });
        app.MapPost("/api/web/skills/{skillId}/invoke", async (string skillId, HttpRequest request) =>
        {
            var session = Session();
            if (!InvokeAllowed(session)) return Denied();
            if (skillId != SkillId) return Error(404, "SKILL_NOT_FOUND", "技能不存在");
            JsonElement body;
            try { using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted); body = document.RootElement.Clone(); }
            catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(prompt.GetString()) || prompt.GetString()!.Length > 4000 ||
                !body.TryGetProperty("idempotencyKey", out var key) || key.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) || key.GetString()!.Length > 128 ||
                body.EnumerateObject().Any(p => p.Name is not ("prompt" or "idempotencyKey")))
                return Error(400, "INVALID_REQUEST", "仅接受 prompt（最多 4000 字符）与 idempotencyKey（最多 128 字符）");
            var existing = execution.GetJobs(session.UserId).FirstOrDefault(j => j.IdempotencyKey == key.GetString());
            if (existing is not null && (existing.Tool != SkillId || !existing.Inputs.TryGetValue("prompt", out var savedPrompt) ||
                savedPrompt.ValueKind != JsonValueKind.String || savedPrompt.GetString() != prompt.GetString()))
                return Error(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他调用");
            var inputs = new Dictionary<string, JsonElement> { ["prompt"] = prompt.Clone() };
            var invocation = new Invocation { Tool = SkillId, Capability = Capability.TextToImage, Channel = "comfyui", Inputs = inputs };
            // StartAsync returns after submission; failed jobs remain visible rather than becoming false positives.
            var result = await execution.StartAsync(session, invocation, key.GetString()!);
            execution.TryGet(result.JobId, out var job);
            return Results.Json(View(job!, session));
        });
        app.MapGet("/api/web/jobs", () =>
        {
            var session = Session();
            return Results.Json(new { jobs = execution.GetJobs(session.UserId).Where(j => j.Tool == SkillId).Select(j => View(j, session)).ToArray() });
        });
        app.MapGet("/api/web/jobs/{jobId:guid}", (Guid jobId) =>
        {
            var session = Session();
            var found = Find(jobId, session, out var job);
            return job is null ? found : job.Tool == SkillId ? Results.Json(View(job, session)) : Error(404, "JOB_NOT_FOUND", "任务不存在");
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/cancel", (Guid jobId) =>
        {
            var session = Session();
            if (!session.ServerClaims.Contains("job.cancel")) return Error(403, "JOB_FORBIDDEN", "没有取消权限");
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (job.Tool != SkillId) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State is not (JobState.Queued or JobState.Running)) return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消");
            try { execution.Cancel(session, jobId); return Results.Json(View(job, session)); }
            catch (ProtocolViolationException) { return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消"); }
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/retry", async (Guid jobId) =>
        {
            var session = Session();
            if (!InvokeAllowed(session)) return Denied();
            var found = Find(jobId, session, out var source);
            if (source is null) return found;
            if (source.Tool != SkillId) return Error(404, "JOB_NOT_FOUND", "任务不存在");
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
