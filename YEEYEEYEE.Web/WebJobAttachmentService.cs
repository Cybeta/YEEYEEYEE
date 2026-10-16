using System.Collections.Concurrent;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

internal sealed class WebJobAttachmentService(SingleMachineExecutionService execution, UserStore users, CanvasEventHub hub) : BackgroundService
{
    /// <summary>
    /// 节点出图的**候选批次**任务在 inputs 里带的标记。
    ///
    /// 有它 = 产物先留在任务里，等用户在画布上采用（`/api/web/jobs/{id}/outputs/{index}/adopt`）才进节点，
    /// 后台**不**替它自动回挂。与聊天生成的 `webChatGeneration` 是同一个思路、各管各的入口：
    /// 一个入口一种回挂方式，谁也不用猜另一条路上发生了什么。
    /// </summary>
    internal const string CandidateInput = "nodeImageCandidate";

    private readonly ConcurrentDictionary<Guid, string> states = new();

    /// <summary>这条任务要不要走「挂到画布节点」这条路（决定 attachmentState 有没有意义、后台要不要处理）。</summary>
    internal static bool AttachApplies(Job job) => job.Inputs.ContainsKey("targetNodeId")
        && !job.Inputs.ContainsKey("webChatGeneration")
        && !IsCandidate(job);

    /// <summary>节点出图的候选任务（产物等用户采用）。</summary>
    internal static bool IsCandidate(Job job) => job.Inputs.ContainsKey(CandidateInput);

    public string State(Job job) => AttachApplies(job) ? states.GetValueOrDefault(job.JobId, "pending") : "none";
    public static bool InCurrentCanvas(Job job) => !job.Inputs.TryGetValue("webCanvasPath", out var path)
        ? true : string.Equals(path.GetString(), WebProjectApi.Scope, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 回挂被拒时写进 <c>attachmentState</c> 的那句话。
    ///
    /// 必须是一句「CODE：说明」——与上面几个状态（TARGET_UNAVAILABLE / TARGET_FORBIDDEN /
    /// TARGET_ATTACH_FAILED）同一个格式。直接把错误对象序列化过去，界面上就会出现一段
    /// <c>{"code":…,"message":…}</c> 的 JSON：字段名和花括号都摆在用户眼前，等于没说清原因。
    /// </summary>
    internal static string DescribeFailure(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        var code = value?.GetType().GetProperty("code")?.GetValue(value)?.ToString();
        var message = value?.GetType().GetProperty("message")?.GetValue(value)?.ToString();
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            return "TARGET_ATTACH_FAILED：产物未安全回挂";
        return $"{(string.IsNullOrWhiteSpace(code) ? "TARGET_ATTACH_FAILED" : code)}：{(string.IsNullOrWhiteSpace(message) ? "产物未安全回挂" : message)}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var job in execution.GetAllJobs().Where(j => j.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled))
            {
                var user = users.Find(job.UserId);
                if (user is null || user.Disabled) continue;
                try { WebChatApi.CompleteJob(job, user, hub); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException) { }
            }
            foreach (var job in execution.GetAllJobs().Where(j => j.State == JobState.Succeeded && AttachApplies(j)))
            {
                if (states.GetValueOrDefault(job.JobId) == "attached") continue;
                try
                {
                    var path = job.Inputs["webCanvasPath"].GetString()!;
                    var mode = WebProjectApi.RegisteredCanvas(path);
                    if (mode is null) { states[job.JobId] = "TARGET_UNAVAILABLE：原画布未登记"; continue; }
                    var user = users.Find(job.UserId);
                    if (user is null || user.Disabled || !UserRoles.ClaimsFor(user.Role).Contains("canvas.edit"))
                    { states[job.JobId] = "TARGET_FORBIDDEN：账号不能写入画布"; continue; }
                    states[job.JobId] = WebProjectApi.InCanvas(mode, () =>
                    {
                        var store = WebProjectApi.Store(mode)!;
                        var serial = store.LastCommit.Serial;
                        var result = WebProjectApi.Leases(mode)!.WithTreeTransaction<IResult>(user,
                            () => store.AttachJobOutputs(job.ToResult(), job.Inputs["targetNodeId"].GetGuid()), EditLeaseApi.Failure);
                        if (result is IStatusCodeHttpResult status && status.StatusCode >= 400)
                            return DescribeFailure(result);
                        if (store.LastCommit.Serial > serial)
                            hub.CanvasChanged(store.LastCommit.Revision, job.Inputs["targetNodeId"].GetString(), user.DisplayName, "job-output");
                        return "attached";
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { states[job.JobId] = "TARGET_ATTACH_FAILED：产物未安全回挂"; }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
