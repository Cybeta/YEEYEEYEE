using System.Text.Json;
using Microsoft.Data.Sqlite;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 判档的两个入口：判一次、以及明确跳过。
///
/// **前端只能给任务 ID**。产物路径由服务端从任务自己的 outputs 里解析，
/// 任务归属、当前画布、候选批次标记、成功状态与图片类型逐层校验；
/// 模型调用、密钥解密与结果落盘都在服务端。
/// </summary>
internal static class WebImageQualityApi
{
    /// <summary>
    /// `Retry` 只有用户明确点「重试判档」时才为 true：重复请求与页面重渲染带来的第二次调用
    /// 不会把已经失败/跳过的图再判一遍（那会重复付费）。</summary>
    public sealed record GradeInput(string[]? JobIds, bool Retry = false);

    public static void Map(WebApplication app)
    {
        var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
        var service = app.Services.GetRequiredService<WebImageQualityService>();

        static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

        IResult? Resolve(HttpContext context, GradeInput input, bool requireInvoke, out List<Job> jobs)
        {
            jobs = new List<Job>();
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || user.Disabled) return Error(403, "LOGIN_REQUIRED", "判档需要账号登录");
            // 判档会消耗全局模型配额，与技能调用同一档权限；不能只凭“登录了”就让任何人反复触发。
            // 跳过不调用模型、只写一条“这次不判”，因此只要求登录与任务归属。
            if (requireInvoke && !WebAccessGuard.Permissions(context).Contains("skill.invoke"))
                return Error(403, "SKILL_FORBIDDEN", "没有调用模型判档的权限");

            var ids = input.JobIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            if (ids.Length == 0 || ids.Length > 8) return Error(400, "INVALID_REQUEST", "一次判档需要 1–8 个任务");

            var seen = new HashSet<Guid>();
            foreach (var text in ids)
            {
                if (!Guid.TryParse(text, out var id)) return Error(400, "INVALID_REQUEST", "任务 ID 格式不正确");
                // 同一个任务用不同大小写写两遍也只算一次：否则会重复送图、重复计数。
                if (!seen.Add(id)) continue;
                execution.TryGet(id, out var job);
                // 归属、当前画布、候选标记、成败与类型都在服务层一处校验。
                if (WebImageQualityService.ValidateJob(
                    job, user.Id, job is not null && WebJobAttachmentService.InCurrentCanvas(job),
                    job is not null && WebJobAttachmentService.IsCandidate(job)) is { } invalid)
                    return Error(invalid.Status, invalid.Code, invalid.Message);
                jobs.Add(job!);
            }
            return null;
        }

        app.MapPost("/api/web/image-quality/grade", async (HttpContext context, GradeInput input) =>
        {
            if (Resolve(context, input, requireInvoke: true, out var jobs) is { } rejected) return rejected;
            try
            {
                var (results, modelMessage) = await service.GradeAsync(jobs, input.Retry, context.RequestAborted);
                return Results.Json(new { results, modelMessage });
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 客户端先走了：不把中断写成失败，也不改动任何任务状态。
                return Results.Empty;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException
                or InvalidOperationException or JsonException)
            {
                return Error(503, "QUALITY_STORE_FAILED", "判档结果暂时无法保存；图不受影响，可以稍后重试");
            }
        });

        app.MapPost("/api/web/image-quality/skip", (HttpContext context, GradeInput input) =>
        {
            if (Resolve(context, input, requireInvoke: false, out var jobs) is { } rejected) return rejected;
            try
            {
                return Results.Json(new { results = service.Skip(jobs) });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException
                or InvalidOperationException or JsonException)
            {
                return Error(503, "QUALITY_STORE_FAILED", "跳过状态暂时无法保存；图仍可直接查看与采用");
            }
        });
    }
}
