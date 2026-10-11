using System.Collections.Concurrent;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

internal sealed class WebJobAttachmentService(SingleMachineExecutionService execution, UserStore users, CanvasEventHub hub) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, string> states = new();
    public string State(Job job) => !job.Inputs.ContainsKey("targetNodeId") ? "none" : states.GetValueOrDefault(job.JobId, "pending");
    public static bool InCurrentCanvas(Job job) => !job.Inputs.TryGetValue("webCanvasPath", out var path)
        ? true : string.Equals(path.GetString(), WebProjectApi.Scope, StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var job in execution.GetAllJobs().Where(j => j.State == JobState.Succeeded && j.Inputs.ContainsKey("targetNodeId")))
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
                            return System.Text.Json.JsonSerializer.Serialize((result as IValueHttpResult)?.Value);
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
