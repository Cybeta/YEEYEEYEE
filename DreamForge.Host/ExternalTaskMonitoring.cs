using DreamForge.Core;
using Microsoft.Extensions.Hosting;

namespace DreamForge.Host;

public interface IExternalTaskProvider
{
    Task<ExternalTaskUpdate> GetStatusAsync(string externalTaskId, CancellationToken cancellationToken);
}

public interface IExternalTaskCanceller
{
    Task CancelAsync(string externalTaskId, CancellationToken cancellationToken);
}

public interface IExternalTaskProgressSource
{
    Task ListenForProgressAsync(
        string externalTaskId,
        Action<ExternalTaskUpdate> onUpdate,
        CancellationToken cancellationToken);
}

public sealed class ExternalTaskCallbackReceiver
{
    private readonly SingleMachineExecutionService execution;

    public ExternalTaskCallbackReceiver(SingleMachineExecutionService execution)
    {
        this.execution = execution;
    }

    public bool Receive(Guid userId, ExternalTaskUpdate update)
    {
        if (userId == Guid.Empty) throw new ArgumentException("用户 ID 不能为空", nameof(userId));
        ArgumentNullException.ThrowIfNull(update);
        return execution.ApplyExternalUpdate(userId, update);
    }
}

public sealed class ExternalTaskPollingHostedService : IHostedService
{
    private readonly ExternalTaskPoller poller;

    public ExternalTaskPollingHostedService(ExternalTaskPoller poller)
    {
        this.poller = poller;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        poller.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await poller.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class ExternalTaskPoller : IAsyncDisposable
{
    private readonly SingleMachineExecutionService execution;
    private readonly IExternalTaskProvider provider;
    private readonly TimeSpan interval;
    private readonly Func<IReadOnlyList<Job>> jobs;
    private readonly IExternalTaskProgressSource? progressSource;
    private readonly Dictionary<string, Task> progressTasks = new(StringComparer.Ordinal);
    private CancellationTokenSource? stop;
    private Task? loop;

    public ExternalTaskPoller(SingleMachineExecutionService execution, IExternalTaskProvider provider, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        this.execution = execution;
        this.provider = provider;
        this.interval = interval;
        progressSource = provider as IExternalTaskProgressSource;
        jobs = () => execution.GetAllJobs();
    }

    public void Start()
    {
        var controller = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref stop, controller, null) is not null)
        {
            controller.Dispose();
            throw new InvalidOperationException("外部任务轮询器已启动");
        }

        var task = Task.Run(() => RunAsync(controller.Token));
        if (Interlocked.CompareExchange(ref loop, task, null) is not null)
        {
            controller.Cancel();
            controller.Dispose();
            throw new InvalidOperationException("外部任务轮询器已启动");
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        if (loop is not null) await loop.ConfigureAwait(false);

        Task[] listeners;
        lock (progressTasks)
            listeners = progressTasks.Values.ToArray();
        if (listeners.Length > 0)
            await Task.WhenAll(listeners).ConfigureAwait(false);

        stop?.Dispose();
    }

    private void StartProgressListener(Job job, CancellationToken cancellationToken)
    {
        if (progressSource is null || job.ExternalTaskId is null)
            return;
        lock (progressTasks)
        {
            if (progressTasks.ContainsKey(job.ExternalTaskId))
                return;
            var task = progressSource.ListenForProgressAsync(
                job.ExternalTaskId,
                update =>
                {
                    try
                    {
                        if (update.State == ExternalTaskState.Running)
                            execution.ApplyExternalProgress(job.UserId, update.ExternalTaskId, update.ProgressPercent);
                        else
                            execution.ApplyExternalUpdate(job.UserId, update);
                    }
                    catch (InvalidOperationException) { }
                },
                cancellationToken);
            progressTasks[job.ExternalTaskId] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (progressTasks) progressTasks.Remove(job.ExternalTaskId!);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var job in jobs().Where(candidate =>
                    candidate.ExternalTaskId is not null
                    && candidate.State is not (JobState.Succeeded or JobState.Failed or JobState.Cancelled)))
                {
                    StartProgressListener(job, cancellationToken);
                    try
                    {
                        var update = await provider.GetStatusAsync(
                            job.ExternalTaskId!,
                            cancellationToken).ConfigureAwait(false);
                        execution.ApplyExternalUpdate(job.UserId, update);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // 单个供应商查询失败不影响其他 Job，下一轮重试。
                    }
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
