using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>每个 FIFO worker 独占页面；任何失败销毁页面，避免迟到的 Promise 污染下一份。</summary>
internal sealed class ComfyUiNativeWebViewExporter(ComfyUiNativeWebViewService service, string baseUrl, int workerId) : IComfyUiFrontendExporter
{
    private ComfyUiNativeWebViewHost? host;
    internal JsonObject? LastEvidence { get; private set; }
    public async Task<JsonObject> ExportAsync(JsonObject workflow, CancellationToken cancellationToken)
    {
        LastEvidence = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 初始化含另一个 worker 的错峰等待，不能占用本份工作流的导出预算。
        budget.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            host ??= await service.CreateAsync(baseUrl, workerId, budget.Token);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            var raw = workflow.ToJsonString();
            var request = new ExportRequest(Guid.NewGuid().ToString("N"), workerId, host.Session,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), raw);
            LastEvidence = null;
            var result = await host.ExportAsync(request, budget.Token);
            LastEvidence = result.DeepClone().AsObject();
            if (result["error"] is JsonValue error) throw new InvalidOperationException(error.GetValue<string>());
            return result["api"]?.AsObject().DeepClone().AsObject()
                ?? throw new InvalidOperationException("官方前端 output 不是 JSON 对象");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new TimeoutException("ComfyUI 官方前端导出超过两分钟，已销毁页面");
        }
        catch { await DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        var old = host;
        host = null;
        if (old is not null) await Dispatcher.UIThread.InvokeAsync(() => service.Release(old));
    }
    internal sealed record ExportRequest(string RequestId, int WorkerId, string Session, string SourceHash, string Raw);
}

internal sealed class ComfyUiNativeWebViewHost : IDisposable
{
    private readonly Window window;
    private readonly NativeWebView webView = new();
    private readonly TaskCompletionSource<bool> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<JsonObject>? pending;
    private ComfyUiNativeWebViewExporter.ExportRequest? active;
    private bool disposed;
    public string Session { get; } = Guid.NewGuid().ToString("N");

    public ComfyUiNativeWebViewHost(string baseUrl)
    {
        window = new Window { Width = 800, Height = 600, ShowInTaskbar = false, ShowActivated = false,
            Position = new global::Avalonia.PixelPoint(-10000, -10000),
            WindowDecorations = WindowDecorations.None, Content = webView };
        webView.EnvironmentRequested += async (_, e) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var deferral = e.GetDeferral();
            try
            {
                if (e is not global::Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows)
                    throw new InvalidOperationException("NativeWebView 未使用 WebView2 环境");
                windows.ExplicitEnvironment = await SharedWebView2Environment.GetAsync(windows);
            }
            catch (Exception ex) { loaded.TrySetException(new InvalidOperationException("共享 WebView2 环境初始化失败", ex)); }
        };
        webView.NavigationCompleted += (_, _) => loaded.TrySetResult(true);
        webView.WebMessageReceived += OnMessage;
        webView.Source = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public void Show() => window.Show();
    private async Task<string?> JS(string script, CancellationToken token)
    {
        // NativeWebView 的调用和异步延续均留在 UI 线程；取消等待不会复用旧控件。
        var operation = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var value = await webView.InvokeScript(script);
            return value?.StartsWith('"') == true ? JsonSerializer.Deserialize<string>(value) : value;
        });
        return await operation.WaitAsync(token);
    }

    public async Task ReadyAsync(int worker, CancellationToken token)
    {
        await loaded.Task.WaitAsync(token);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string? state = null;
        while (true)
        {
            state = await JS("JSON.stringify((()=>{const pinia=document.getElementById('vue-app')?.__vue_app__?.config.globalProperties.$pinia;const stores=pinia?Array.from(pinia._s.values()).filter(s=>'spinner' in s):[];return {canvas:!!window.comfyAPI?.app?.app?.canvas,vue:!!window.comfyAPI?.app?.app?.vueAppReady,ksampler:!!window.LiteGraph?.registered_node_types?.KSampler,bootstrap:stores.length>0&&stores.every(s=>s.spinner===false),spinners:stores.map(s=>({id:s.$id,value:s.spinner})),url:location.href,document:document.readyState}})())", token);
            var ready = JsonNode.Parse(state ?? "{}");
            if (ready?["canvas"]?.GetValue<bool>() == true && ready?["vue"]?.GetValue<bool>() == true && ready?["ksampler"]?.GetValue<bool>() == true && ready?["bootstrap"]?.GetValue<bool>() == true) break;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("ComfyUI 前端未就绪：" + state);
            await Task.Delay(250, token);
        }
        if (await JS(Script("Guard"), token) != "guard-ready") throw new InvalidOperationException("前端写请求保护安装失败");
        await JS(Script("Inspect"), token);
        await JS($"window.__worker={worker};window.__session={JsonSerializer.Serialize(Session)};sessionStorage.setItem('library-session',window.__session);'session-ready'", token);
        await JS(Script("Export"), token);
    }

    public async Task<JsonObject> ExportAsync(ComfyUiNativeWebViewExporter.ExportRequest request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() => { active = request; pending = completion; });
        try
        {
            await JS("window.__exportLibrary(" + JsonSerializer.Serialize(request) + ");'started'", token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(100), token);
        }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { active = null; pending = null; }); }
    }

    private void OnMessage(object? sender, global::Avalonia.Controls.WebMessageReceivedEventArgs args)
    {
        try
        {
            var body = args.Body ?? string.Empty;
            var node = JsonNode.Parse(body);
            if (node is JsonValue value)
            {
                var nested = value.GetValue<string>();
                if (nested is null) return;
                node = JsonNode.Parse(nested);
            }
            if (node is not JsonObject message || active is null || pending is null) return;
            if (message["kind"]?.GetValue<string>() != "library-export"
                || message["requestId"]?.GetValue<string>() != active.RequestId
                || message["worker"]?.GetValue<int>() != active.WorkerId
                || message["session"]?.GetValue<string>() != active.Session
                || message["sourceHash"]?.GetValue<string>() != active.SourceHash) return;
            pending.TrySetResult(message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* 非协议消息不接受。 */ }
    }

    private static string Script(string name)
    {
        using var stream = typeof(ComfyUiNativeWebViewHost).Assembly.GetManifestResourceStream("ComfyUi." + name + ".js")
            ?? throw new InvalidOperationException("缺少官方前端导出脚本：" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pending?.TrySetCanceled();
        webView.WebMessageReceived -= OnMessage;
        window.Content = null; // 脱离视觉树销毁原生 adapter/controller。
        window.Close();
    }
}

/// <summary>12.1 只有原生指针公开；隔离对固定版本 internal 创建器的适配，不增加 WebView2 包。</summary>
internal static class SharedWebView2Environment
{
    private static Task<nint>? environment;
    private static object? lifetimeRoot;
    public static Task<nint> GetAsync(global::Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs args)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("共享 WebView2 环境仅适用于 Windows");
        return environment ??= CreateAsync(args);
    }
    private static async Task<nint> CreateAsync(global::Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs args)
    {
        args.UserDataFolder = Environment.GetEnvironmentVariable("YEEYEEYEE_WEBVIEW_PROFILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YEEYEEYEE", "ComfyUiWebView");
        var type = typeof(NativeWebView).Assembly.GetType("Avalonia.Controls.Win.WebView2.CoreWebView2Environment")
            ?? throw new NotSupportedException("Avalonia WebView 12.1 环境适配类型不存在");
        var method = type.GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.Public)
            ?? throw new NotSupportedException("Avalonia WebView 12.1 环境创建入口不存在");
        var task = (Task)(method.Invoke(null, [args]) ?? throw new InvalidOperationException("环境创建未返回任务"));
        await task;
        lifetimeRoot = task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw new InvalidOperationException("环境创建返回空对象");
        if (!ComWrappers.TryGetComInstance(lifetimeRoot, out var pointer) || pointer == 0)
            throw new NotSupportedException("无法取得 CoreWebView2Environment COM 指针，禁止退回独立环境");
        // TryGetComInstance 返回的引用保留至进程结束，避免多个控件间环境被释放。
        return pointer;
    }
}

internal sealed class ComfyUiNativeWebViewService : IDisposable
{
    private readonly HashSet<ComfyUiNativeWebViewHost> hosts = [];
    private readonly SemaphoreSlim initialization = new(1, 1);
    private bool disposed;
    public static ComfyUiNativeWebViewService Start() => new();
    public ComfyUiFrontendExporterFactory Factory => (url, worker) => new ComfyUiNativeWebViewExporter(this, url, worker);
    public async Task<ComfyUiNativeWebViewHost> CreateAsync(string url, int worker, CancellationToken token)
    {
        await initialization.WaitAsync(token);
        ComfyUiNativeWebViewHost? host = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                host = new ComfyUiNativeWebViewHost(url);
                hosts.Add(host);
                host.Show();
            });
            await host!.ReadyAsync(worker, token);
            await Task.Delay(350, token);
            return host;
        }
        catch { if (host is not null) await Dispatcher.UIThread.InvokeAsync(() => Release(host)); throw; }
        finally { initialization.Release(); }
    }
    public void Release(ComfyUiNativeWebViewHost host) { hosts.Remove(host); host.Dispose(); }
    public void Dispose()
    {
        disposed = true;
        foreach (var host in hosts.ToArray()) Release(host);
    }
}
