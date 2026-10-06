using System.Text.Json;

namespace YEEYEEYEE.Core;

public sealed class Job
{
    private readonly object gate = new();
    public Guid JobId { get; } = Guid.NewGuid();
    public Guid InvocationId { get; }
    public Guid UserId { get; }
    public string IdempotencyKey { get; }
    public JobState State { get; private set; } = JobState.Queued;
    public int ProgressPercent { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ExternalTaskId { get; private set; }
    public IReadOnlyList<AssetRef> Outputs { get; private set; } = Array.Empty<AssetRef>();
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; private set; } = new Dictionary<string, JsonElement>();
    public string Tool { get; private set; } = string.Empty;
    public Capability Capability { get; private set; }
    public string Channel { get; private set; } = string.Empty;

    /// <summary>第几次尝试：首次为 1，重试逐次递增。</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>本次尝试重试的 Job；首次尝试为 null。</summary>
    public Guid? RetryOfJobId { get; private set; }

    /// <summary>同一次输入的反复尝试共享的根 Job。</summary>
    public Guid RootJobId { get; private set; }

    public Job(Guid invocationId, Guid userId, string idempotencyKey, Guid? jobId = null,
        int attempt = 1, Guid? retryOfJobId = null, Guid? rootJobId = null)
    {
        if (invocationId == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Job 标识和幂等键不能为空");
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "尝试次数从 1 开始");
        JobId = jobId ?? Guid.NewGuid();
        InvocationId = invocationId; UserId = userId; IdempotencyKey = idempotencyKey;
        Attempt = attempt; RetryOfJobId = retryOfJobId; RootJobId = rootJobId ?? JobId;
        if (retryOfJobId is not null && rootJobId is null) throw new ArgumentException("重试任务必须带上根任务标识");
    }

    public static Job Restore(ExecutionResult snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.JobId == Guid.Empty || snapshot.UserId == Guid.Empty) throw new ArgumentException("Job 快照标识不能为空");
        var job = new Job(
            snapshot.InvocationId, snapshot.UserId, snapshot.IdempotencyKey, snapshot.JobId,
            snapshot.Attempt < 1 ? 1 : snapshot.Attempt,
            snapshot.RetryOfJobId,
            snapshot.RootJobId == Guid.Empty ? snapshot.JobId : snapshot.RootJobId);
        job.ProgressPercent = snapshot.ProgressPercent;
        job.ErrorCode = snapshot.ErrorCode;
        job.ErrorMessage = snapshot.ErrorMessage;
        job.ExternalTaskId = snapshot.ExternalTaskId;
        job.Outputs = snapshot.Outputs.ToArray();
        job.Inputs = snapshot.Inputs;
        job.Tool = snapshot.Tool;
        job.Capability = snapshot.Capability;
        job.Channel = snapshot.Channel;
        job.State = snapshot.State;
        return job;
    }

    /// <summary>记录本次执行的调用信息与输入参数快照。</summary>
    public void AttachInvocation(Invocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Inputs = invocation.Inputs ?? new Dictionary<string, JsonElement>();
        Tool = invocation.Tool;
        Capability = invocation.Capability;
        Channel = invocation.Channel;
    }

    public void Transition(JobState target)
    {
        lock (gate)
        {
            var valid = (State, target) switch
            {
                (JobState.Queued, JobState.Running) => true,
                (JobState.Queued, JobState.Cancelled) => true,
                (JobState.Running, JobState.Cancelling) => true,
                (JobState.Running, JobState.Succeeded) => true,
                (JobState.Running, JobState.Failed) => true,
                (JobState.Cancelling, JobState.Cancelled) => true,
                _ => false
            };
            if (!valid) throw new JobStateException(State, target);
            State = target;
            if (target == JobState.Succeeded) ProgressPercent = 100;
        }
    }

    public void AttachExternalTask(string externalTaskId)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId)) throw new ArgumentException("外部任务 ID 不能为空", nameof(externalTaskId));
        lock (gate)
        {
            if (State is JobState.Succeeded or JobState.Failed or JobState.Cancelled) throw new JobStateException(State, State);
            if (ExternalTaskId is not null && !string.Equals(ExternalTaskId, externalTaskId, StringComparison.Ordinal)) throw new InvalidOperationException("Job 已绑定其他外部任务 ID");
            ExternalTaskId = externalTaskId;
        }
    }

    public void ReportProgress(int percent)
    {
        lock (gate)
        {
            if (State is not (JobState.Running or JobState.Cancelling) || percent is < 0 or > 100 || percent < ProgressPercent) throw new InvalidOperationException("Job 当前不可更新进度");
            ProgressPercent = percent;
        }
    }

    /// <summary>Applies and validates an update received from the external task provider.</summary>
    public void ApplyExternalUpdate(ExternalTaskUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.ExternalTaskId)) throw new ArgumentException("外部任务 ID 不能为空", nameof(update));
        if (update.ProgressPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(update), "外部任务进度必须在 0 到 100 之间");
        lock (gate)
        {
            if (!string.Equals(ExternalTaskId, update.ExternalTaskId, StringComparison.Ordinal)) throw new InvalidOperationException("外部任务 ID 与 Job 不匹配");
            if (State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
            {
                var terminal = update.State switch { ExternalTaskState.Succeeded => JobState.Succeeded, ExternalTaskState.Failed => JobState.Failed, ExternalTaskState.Cancelled => JobState.Cancelled, _ => State };
                if (terminal != State) throw new JobStateException(State, terminal);
                return;
            }
            if (update.ProgressPercent >= ProgressPercent) ProgressPercent = update.ProgressPercent;
            switch (update.State)
            {
                case ExternalTaskState.Queued:
                    break;
                case ExternalTaskState.Running:
                    if (State == JobState.Queued) State = JobState.Running;
                    break;
                case ExternalTaskState.Succeeded:
                    if (State == JobState.Queued) State = JobState.Running;
                    if (State == JobState.Running) { Outputs = update.Outputs.ToArray(); State = JobState.Succeeded; ProgressPercent = 100; }
                    break;
                case ExternalTaskState.Failed:
                    if (State == JobState.Queued) State = JobState.Running;
                    if (State == JobState.Running) { ErrorCode = update.ErrorCode ?? "EXTERNAL_TASK_FAILED"; ErrorMessage = update.ErrorMessage; State = JobState.Failed; }
                    break;
                case ExternalTaskState.Cancelled:
                    if (State == JobState.Queued) State = JobState.Cancelled;
                    else if (State is JobState.Running or JobState.Cancelling) { State = JobState.Cancelled; }
                    break;
                default:
                    throw new InvalidOperationException("未知外部任务状态");
            }
        }
    }

    public void Fail(string code, string message)
    {
        lock (gate)
        {
            if (State != JobState.Running) throw new JobStateException(State, JobState.Failed);
            ErrorCode = code; ErrorMessage = message;
            State = JobState.Failed;
        }
    }

    public void Complete(IReadOnlyList<AssetRef> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        lock (gate)
        {
            if (State != JobState.Running) throw new JobStateException(State, JobState.Succeeded);
            Outputs = outputs.ToArray(); State = JobState.Succeeded; ProgressPercent = 100;
        }
    }

    public ExecutionResult ToResult()
    {
        lock (gate) return new() { UserId = UserId, InvocationId = InvocationId, JobId = JobId, State = State, ProgressPercent = ProgressPercent, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage, ExternalTaskId = ExternalTaskId, IdempotencyKey = IdempotencyKey, Outputs = Outputs.ToArray(), Inputs = Inputs, Tool = Tool, Capability = Capability, Channel = Channel, Attempt = Attempt, RetryOfJobId = RetryOfJobId, RootJobId = RootJobId };
    }
}

public sealed class IdempotencyRegistry
{
    private readonly Dictionary<(Guid UserId, string Key), ExecutionResult> results = new();
    private readonly object gate = new();
    public bool TryGet(Guid userId, string key, out ExecutionResult result) { lock (gate) return results.TryGetValue((userId, key), out result!); }
    public void Store(Guid userId, string key, ExecutionResult result) { lock (gate) results[(userId, key)] = result; }
}

public sealed class LocalUndoStack : IUndoStack
{
    private readonly Stack<OperationRecord> records = new();
    public int Count => records.Count;
    public void Push(OperationRecord record) => records.Push(record ?? throw new ArgumentNullException(nameof(record)));
    public OperationRecord? Pop() => records.Count == 0 ? null : records.Pop();
}

public interface IUndoStack
{
    void Push(OperationRecord record);
    OperationRecord? Pop();
}
