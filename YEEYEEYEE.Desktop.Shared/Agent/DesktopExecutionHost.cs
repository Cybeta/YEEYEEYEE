using System.Net.Http;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using Microsoft.Data.Sqlite;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 桌面端唯一的执行服务宿主：配置了 ComfyUI 时使用真实执行链路并启动外部任务轮询，
/// 否则退回到内存执行器。任务区与节点出图共用同一个实例，保证 Job 记录一致。
/// </summary>
public sealed class DesktopExecutionHost : IAsyncDisposable
{
    private readonly ExternalTaskPoller? poller;
    private readonly HttpClient? http;
    private readonly IJobStore? store;

    private DesktopExecutionHost(
        SingleMachineExecutionService execution,
        ExternalTaskPoller? poller,
        HttpClient? http,
        IJobStore? store,
        bool comfyUiBacked,
        string assetDirectory,
        string? storeWarning)
    {
        Execution = execution;
        this.poller = poller;
        this.http = http;
        this.store = store;
        ComfyUiBacked = comfyUiBacked;
        AssetDirectory = assetDirectory;
        StoreWarning = storeWarning;
    }

    public SingleMachineExecutionService Execution { get; }
    public bool ComfyUiBacked { get; }

    /// <summary>创建时使用的资产目录，用于判断配置变化后是否需要重建执行链路。</summary>
    public string AssetDirectory { get; }

    /// <summary>任务记录存储；数据库不可用时为 null。</summary>
    public IJobStore? Store => store;

    /// <summary>任务记录未能持久化时的原因；正常时为 null。</summary>
    public string? StoreWarning { get; }

    /// <summary>任务数据库位置：默认在项目根文件夹，可用 YEEYEEYEE_JOB_DB 覆盖。</summary>
    public static string JobDatabasePath
    {
        get
        {
            var overridePath = EnvCompat.Get("JOB_DB");
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            return AppPaths.Combine("jobs.db");
        }
    }

    /// <summary>
    /// 建一份执行宿主。<paramref name="config"/> 给了就用它，没给才去读配置文件。
    ///
    /// **必须能传配置进来**：调用方有时拿着一份临时副本（例如用户选了某个站点的池子，
    /// 地址与密钥从池子来，只在那一次调用里生效）。早先这里一律自己读配置文件，
    /// 于是「调用方以为在打 A，实际拿到的宿主按文件里的 B 建」——最坏的一种错：
    /// 配置里没开 ComfyUI 时，它会悄悄退回**内存执行器**，任务在本地「成功」并产出一个
    /// <c>local://</c> 的假引用，而调用方看到的是「跑完了却没有产物」。
    /// </summary>
    public static DesktopExecutionHost Create(AiProviderConfig? config = null)
    {
        config ??= AiProviderSettings.Load();
        string? storeWarning = null;
        if (!config.IsComfyUiConfigured)
        {
            var inMemory = CreateExecution(new InMemoryInvocationExecutor(), null, out var inMemoryStore, out storeWarning);
            return new DesktopExecutionHost(inMemory, null, null, inMemoryStore, false, AssetStore.Directory, storeWarning);
        }

        var baseUrl = config.ComfyUiBaseUrl.TrimEnd('/') + "/";
        var http = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(30) };
        var executor = new ComfyUiExecutor(http, new ComfyUiWorkflowFactory(config.ComfyUiCheckpoint).Create, config.ComfyUiClientId);
        var provider = new ComfyUiProvider(
            http,
            AssetStore.EnsureDirectory(),
            new Uri(baseUrl, UriKind.Absolute),
            config.ComfyUiClientId);
        var execution = CreateExecution(executor, provider, out var store, out storeWarning);
        var poller = new ExternalTaskPoller(execution, provider, TimeSpan.FromSeconds(2));
        poller.Start();
        return new DesktopExecutionHost(execution, poller, http, store, true, AssetStore.Directory, storeWarning);
    }

    /// <summary>
    /// 优先接入 SQLite 任务持久化；数据库不可用时退回不持久化，并把原因交给界面展示。
    /// </summary>
    private static SingleMachineExecutionService CreateExecution(
        IInvocationExecutor executor,
        IExternalTaskCanceller? externalCanceller,
        out IJobStore? store,
        out string? warning)
    {
        warning = null;
        try
        {
            var directory = Path.GetDirectoryName(JobDatabasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var candidate = new SqliteJobStore(JobDatabasePath);
            var service = new SingleMachineExecutionService(executor, candidate, externalCanceller);
            store = candidate;
            return service;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warning = $"任务记录未持久化（{error.Message}）";
            store = null;
            return new SingleMachineExecutionService(executor, store: null, externalCanceller);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (poller is not null) await poller.DisposeAsync().ConfigureAwait(false);
        http?.Dispose();
        store?.Dispose();
    }
}
