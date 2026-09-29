using System.Text.Json;
using DreamForge.Core;

namespace DreamForge.Host;

public interface IInvocationExecutor
{
    Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken);
}

public sealed class InMemoryInvocationExecutor : IInvocationExecutor
{
    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        return new ExecutionOutput { ExternalTaskId = $"local-{job.JobId:N}", Outputs = [new AssetRef { Role = "output", Ref = $"local://jobs/{job.JobId:N}/{invocation.Tool}" }] };
    }
}

public sealed class SingleMachineExecutionService
{
    private readonly IInvocationExecutor executor;
    private readonly IExternalTaskCanceller? externalCanceller;
    private readonly IJobStore? store;
    private readonly IdempotencyRegistry idempotency = new();
    private readonly Dictionary<Guid, Job> jobs = new();
    private readonly Dictionary<Guid, CancellationTokenSource> cancellation = new();
    private readonly Dictionary<(Guid UserId, string Key), Task<ExecutionResult>> inFlight = new();
    private readonly object gate = new();

    public SingleMachineExecutionService(IInvocationExecutor? executor = null, IJobStore? store = null, IExternalTaskCanceller? externalCanceller = null)
    {
        this.executor = executor ?? new InMemoryInvocationExecutor();
        this.externalCanceller = externalCanceller;
        this.store = store;
        if (store is not null)
        {
            store.Initialize();
            Restore(store.LoadAll());
        }
    }

    public event Action<ExecutionResult>? Updated;

    public Task<ExecutionResult> StartAsync(SessionContext session, Invocation invocation, string idempotencyKey, CancellationToken cancellationToken = default,
        int attempt = 1, Guid? retryOfJobId = null, Guid? rootJobId = null)
    {
        AccessPolicy.Require(session.ServerClaims.Contains("skill.invoke"), "当前会话无 skill.invoke 权限");
        if (idempotency.TryGet(session.UserId, idempotencyKey, out var existing))
        {
            Updated?.Invoke(existing);
            return Task.FromResult(existing);
        }

        var key = (session.UserId, idempotencyKey);
        lock (gate)
        {
            if (inFlight.TryGetValue(key, out var running)) return running;
            var job = new Job(invocation.InvocationId, session.UserId, idempotencyKey, jobId: null,
                attempt: attempt, retryOfJobId: retryOfJobId, rootJobId: rootJobId);
            job.AttachInvocation(invocation);
            var controller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            jobs[job.JobId] = job;
            cancellation[job.JobId] = controller;
            var task = Task.Run(() => RunAsync(session, invocation, job, controller, key));
            inFlight[key] = task;
            return task;
        }
    }

    public bool TryGet(Guid jobId, out Job? job)
    {
        lock (gate) return jobs.TryGetValue(jobId, out job);
    }

    public IReadOnlyList<Job> GetJobs(Guid userId)
    {
        lock (gate) return jobs.Values.Where(job => job.UserId == userId).ToArray();
    }

    public IReadOnlyList<Job> GetAllJobs()
    {
        lock (gate) return jobs.Values.ToArray();
    }

    public bool ApplyExternalUpdate(Guid userId, ExternalTaskUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Job? job;
        lock (gate)
        {
            job = jobs.Values.SingleOrDefault(candidate => candidate.UserId == userId && string.Equals(candidate.ExternalTaskId, update.ExternalTaskId, StringComparison.Ordinal));
        }
        if (job is null) return false;
        job.ApplyExternalUpdate(update);
        Publish(job.ToResult());
        if (job.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
        {
            idempotency.Store(job.UserId, job.IdempotencyKey, job.ToResult());
        }
        return true;
    }

    public bool ApplyExternalProgress(Guid userId, string externalTaskId, int progressPercent)
    {
        if (progressPercent is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(progressPercent));
        Job? job;
        lock (gate)
        {
            job = jobs.Values.SingleOrDefault(candidate => candidate.UserId == userId
                && string.Equals(candidate.ExternalTaskId, externalTaskId, StringComparison.Ordinal));
        }
        if (job is null) return false;
        if (job.State == JobState.Running)
            job.ReportProgress(progressPercent);
        Publish(job.ToResult());
        return true;
    }

    public void Cancel(SessionContext session, Guid jobId)
    {
        AccessPolicy.Require(session.ServerClaims.Contains("job.cancel"), "当前会话无 job.cancel 权限");
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var job)) throw new ProtocolViolationException("JOB_NOT_FOUND", "Job 不存在", "warning");
            if (job.UserId != session.UserId) throw new ProtocolViolationException("JOB_FORBIDDEN", "当前会话不能取消此 Job", "warning");
            if (job.State == JobState.Queued)
            {
                job.Transition(JobState.Cancelled);
                Publish(job.ToResult());
                if (cancellation.TryGetValue(jobId, out var queuedCancellation))
                    queuedCancellation.Cancel();
                _ = CancelExternalAsync(job.ExternalTaskId);
            }
            else if (job.State == JobState.Running)
            {
                job.Transition(JobState.Cancelling);
                Publish(job.ToResult());
                cancellation[jobId].Cancel();
                _ = CancelExternalAsync(job.ExternalTaskId);
            }
        }
    }

    /// <summary>
    /// 重试一个失败或已取消的 Job（目标 5 / 失败重试，返工 R1 后按整条尝试链判定）：
    /// · 只有终态（Failed / Cancelled）可重试；
    /// · 尝试号按**同根任务已用过的最大号**递增，且**在同一个锁里分配并立刻建作业**——
    ///   否则反复重试历史源会永远停在第 2 次、并发重试还会拿到同一个号，两条路径都能绕过总上限；
    /// · 同根已有成功尝试、或已有进行中的尝试，都直接拒绝（前者是重复执行，后者会同时跑两份）；
    /// · 新 Job 沿用首次的工具、能力、通道与输入参数，并记录重试来源与根任务，便于追溯整条尝试链；
    /// · 尝试号与血缘都落库，宿主重启后规则不变；
    /// · 幂等键由调用方给出（通常每次尝试用新键，否则会被幂等注册表判为重复）。
    /// </summary>
    public Task<ExecutionResult> RetryAsync(
        SessionContext session,
        Guid jobId,
        string idempotencyKey,
        int maxAttempts = JobRetryPolicy.DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        AccessPolicy.Require(session.ServerClaims.Contains("skill.invoke"), "当前会话无 skill.invoke 权限");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "重试请求缺少幂等键");

        // 整段都在 gate 里：查链 → 定尝试号 → 建作业。中途放锁会让两次并发重试拿到同一个尝试号。
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var source)) throw new ProtocolViolationException("JOB_NOT_FOUND", "Job 不存在", "warning");
            if (source.UserId != session.UserId) throw new ProtocolViolationException("JOB_FORBIDDEN", "当前会话不能重试此 Job", "warning");

            var chain = jobs.Values.Where(job => job.RootJobId == source.RootJobId).ToArray();
            if (!JobRetryPolicy.PlanRetry(chain, source, maxAttempts, out var attempt, out var reason))
                throw new ProtocolViolationException("JOB_NOT_RETRYABLE", reason, "warning");

            var invocation = new Invocation
            {
                Tool = source.Tool,
                Capability = source.Capability,
                Channel = source.Channel,
                Inputs = source.Inputs
            };
            return StartAsync(session, invocation, idempotencyKey, cancellationToken,
                attempt: attempt, retryOfJobId: source.JobId, rootJobId: source.RootJobId);
        }
    }

    /// <summary>
    /// 该 Job 现在能不能重试（按整条尝试链判定），供界面在点之前就把按钮置灰并给出原因。
    /// 分配尝试号仍然只在 <see cref="RetryAsync"/> 里做。
    /// </summary>
    public bool CanRetryJob(Guid jobId, Guid userId, int maxAttempts, out string reason)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var source)) { reason = "Job 不存在"; return false; }
            if (source.UserId != userId) { reason = "当前会话不能重试此 Job"; return false; }
            var chain = jobs.Values.Where(job => job.RootJobId == source.RootJobId).ToArray();
            return JobRetryPolicy.PlanRetry(chain, source, maxAttempts, out _, out reason);
        }
    }

    /// <summary>返回某个 Job 所属尝试链上的全部尝试，按尝试次数排序，用于任务详情与追溯。</summary>
    public IReadOnlyList<Job> GetAttemptChain(Guid jobId)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var target)) return Array.Empty<Job>();
            var root = target.RootJobId;
            return jobs.Values
                .Where(job => job.RootJobId == root)
                .OrderBy(job => job.Attempt)
                .ThenBy(job => job.JobId)
                .ToArray();
        }
    }

    private async Task CancelExternalAsync(string? externalTaskId)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId) || externalCanceller is null)
            return;
        try
        {
            await externalCanceller.CancelAsync(externalTaskId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 本地取消已生效；远端失败由状态轮询最终收敛。
        }
    }

    private async Task<ExecutionResult> RunAsync(SessionContext session, Invocation invocation, Job job, CancellationTokenSource controller, (Guid UserId, string Key) key)
    {
        Publish(job.ToResult());
        try
        {
            if (job.State == JobState.Cancelled)
            {
                // 排队取消已完成，保留终态并进入统一清理路径。
            }
            else
            {
                if (job.State == JobState.Cancelling) throw new OperationCanceledException(controller.Token);
                job.Transition(JobState.Running);
                Publish(job.ToResult());
                job.ReportProgress(25);
                Publish(job.ToResult());
                var execution = await executor.ExecuteAsync(session, invocation, job, controller.Token);
                if (job.State == JobState.Cancelling) throw new OperationCanceledException(controller.Token);
                if (!string.IsNullOrWhiteSpace(execution.ExternalTaskId))
                {
                    job.AttachExternalTask(execution.ExternalTaskId);
                    Publish(job.ToResult());
                }
                if (!execution.AwaitExternalCompletion)
                {
                    job.ReportProgress(90);
                    Publish(job.ToResult());
                    job.Complete(execution.Outputs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (job.State == JobState.Running) job.Transition(JobState.Cancelling);
            if (job.State == JobState.Cancelling) job.Transition(JobState.Cancelled);
        }
        catch (Exception error)
        {
            if (job.State == JobState.Running) job.Fail("JOB_EXECUTION_FAILED", error.Message);
        }

        var result = job.ToResult();
        idempotency.Store(session.UserId, job.IdempotencyKey, result);
        Publish(result);
        controller.Dispose();
        lock (gate)
        {
            inFlight.Remove(key);
            cancellation.Remove(job.JobId);
        }
        return result;
    }

    private void Restore(IReadOnlyList<ExecutionResult> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var recovered = snapshot;
            if (snapshot.State is JobState.Queued or JobState.Running or JobState.Cancelling)
            {
                recovered = snapshot with { State = JobState.Failed, ErrorCode = "HOST_RESTARTED", ErrorMessage = "宿主重启时未完成的 Job 已标记失败，可在任务详情里重试" };
                store!.Save(recovered);
            }
            var job = Job.Restore(recovered);
            lock (gate) jobs[job.JobId] = job;
            if (recovered.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled) idempotency.Store(recovered.UserId, recovered.IdempotencyKey, recovered);
        }
    }

    private void Publish(ExecutionResult result)
    {
        store?.Save(result);
        Updated?.Invoke(result);
    }
}

public static class InvocationRequestParser
{
    public static (Invocation Invocation, string IdempotencyKey) Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("invocation", out var invocationElement) || invocationElement.ValueKind != JsonValueKind.Object) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "执行请求缺少 invocation");
        if (!payload.TryGetProperty("idempotencyKey", out var keyElement) || keyElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyElement.GetString())) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "执行请求缺少幂等键");
        Invocation? invocation;
        try { invocation = JsonSerializer.Deserialize<Invocation>(invocationElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException error) { throw new ProtocolViolationException("PROTOCOL_MALFORMED", $"invocation 格式无效：{error.Message}"); }
        if (invocation is null || invocation.InvocationId == Guid.Empty || string.IsNullOrWhiteSpace(invocation.Tool)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "invocation 标识或工具不能为空");
        return (invocation, keyElement.GetString()!);
    }
}
