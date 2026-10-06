using System.Net;
using System.Text;
using System.Text.Json;

internal sealed class MockComfyUiServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, MockPrompt> prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> promptAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] outputBytes;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private int nextId;

    public MockComfyUiServer()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var fixture = Path.Combine(root.FullName, "YEEYEEYEE.Web.Tests", "fixtures", "mock-comfyui-output.mp4");
            if (File.Exists(fixture))
            {
                outputBytes = File.ReadAllBytes(fixture);
                return;
            }
            root = root.Parent;
        }
        throw new FileNotFoundException("本地 Mock MP4 fixture 不存在");
    }

    public Uri BaseAddress { get; private set; } = null!;
    public int InterruptCount { get; private set; }

    public Task StartAsync()
    {
        var port = GetFreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseAddress.ToString());
        listener.Start();
        lifetime = new CancellationTokenSource();
        loop = Task.Run(() => ServeAsync(lifetime.Token));
        return Task.CompletedTask;
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { return; }
            _ = Task.Run(() => HandleAsync(context), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var segments = (context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (context.Request.HttpMethod, segments.FirstOrDefault())
            {
                case ("POST", "prompt"): await PromptAsync(context); break;
                case ("GET", "system_stats"): await SystemStatsAsync(context); break;
                case ("GET", "queue"): await QueueAsync(context); break;
                case ("GET", "history") when segments.Length == 2: await HistoryAsync(context, segments[1]); break;
                case ("POST", "interrupt"): await InterruptAsync(context); break;
                case ("GET", "view"): await ViewAsync(context); break;
                default: context.Response.StatusCode = 404; await JsonAsync(context, new { error = "mock route not found" }); break;
            }
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            await JsonAsync(context, new { error = exception.Message });
        }
        finally { context.Response.Close(); }
    }

    /// <summary>
    /// 开机自检探的就是这一条。Mock 提供它，是为了让「自检说通了」这条路也有人走一遍——
    /// 否则那一句判断永远只在真机上才被执行到。
    /// </summary>
    private async Task SystemStatsAsync(HttpListenerContext context) =>
        await JsonAsync(context, new
        {
            system = new { comfyui_version = "0.0.0-mock" },
            devices = new object[] { new { name = "mock-gpu", vram_free = 1024L } }
        });

    private async Task PromptAsync(HttpListenerContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
        var workflow = document.RootElement.GetProperty("prompt");
        var promptText = workflow.GetProperty("2").GetProperty("inputs").GetProperty("local_prompt").GetString() ?? string.Empty;
        var id = $"mock-{Interlocked.Increment(ref nextId)}";
        var key = promptText.Trim();
        var attempt = promptAttempts.TryGetValue(key, out var previous) ? previous + 1 : 1;
        promptAttempts[key] = attempt;
        var mode = promptText.Contains("mock-fail", StringComparison.OrdinalIgnoreCase)
            ? MockMode.Failed
            : promptText.Contains("mock-cancel-error", StringComparison.OrdinalIgnoreCase)
                ? MockMode.CancellableError
                : promptText.Contains("mock-cancel", StringComparison.OrdinalIgnoreCase)
                    ? MockMode.Cancellable
                    : MockMode.Succeeded;
        if (promptText.Contains("mock-retry", StringComparison.OrdinalIgnoreCase) && attempt == 1)
            mode = MockMode.Failed;
        lock (prompts) prompts[id] = new MockPrompt(id, mode);
        await JsonAsync(context, new { prompt_id = id });
    }

    private async Task QueueAsync(HttpListenerContext context)
    {
        var running = new List<object>();
        lock (prompts)
        {
            foreach (var prompt in prompts.Values)
            {
                if (!prompt.Terminal)
                    running.Add(new object[] { 0, prompt.Id, Array.Empty<object>() });
            }
        }
        await JsonAsync(context, new { queue_running = running, queue_pending = Array.Empty<object>() });
    }

    private async Task HistoryAsync(HttpListenerContext context, string id)
    {
        MockPrompt? prompt;
        lock (prompts) prompts.TryGetValue(id, out prompt);
        if (prompt is null) { await JsonAsync(context, new Dictionary<string, object>()); return; }

        prompt.HistoryCalls++;
        if (prompt.Cancelled)
        {
            prompt.Terminal = true;
            if (prompt.Mode == MockMode.CancellableError)
            {
                // 取消竞态里远端是**真的报错**了：history 里没有 execution_interrupted，
                // 只有一条 execution_error。Job 必须落到 Failed，而不是停在 Cancelling。
                await JsonAsync(context, History(id, "error", new object[]
                {
                    new object[] { "execution_start", new { prompt_id = id } },
                    new object[] { "execution_error", new { prompt_id = id, node_id = "4", node_type = "easy ltxSamplerSimple", exception_message = "mock sampler error during cancel" } }
                }, completed: false));
                return;
            }

            // **复刻真实 ComfyUI 的取消回包**：`/interrupt` 打断掉的任务，history 里写的是
            // status_str = "error"（与节点异常共用同一个状态位），真正的证据是 messages 里
            // 那条 execution_interrupted。Mock 要是图省事直接回 "cancelled"，就永远测不出
            // 「取消被当成失败」这一类真实存在的偏差。
            await JsonAsync(context, History(id, "error", new object[]
            {
                new object[] { "execution_start", new { prompt_id = id } },
                new object[] { "execution_interrupted", new { prompt_id = id, node_id = "2", node_type = "easy ltxMultiTrackEncode" } }
            }, completed: false));
            return;
        }
        if (prompt.Mode == MockMode.Failed)
        {
            prompt.Terminal = true;
            await JsonAsync(context, History(id, "error", new object[] { new object[] { "execution_error", new { node_id = "7", node_type = "VHS_VideoCombine", exception_message = "mock failure" } } }));
            return;
        }
        if (prompt.Mode is MockMode.Cancellable or MockMode.CancellableError)
        {
            await JsonAsync(context, History(id, "running", Array.Empty<object>()));
            return;
        }
        if (prompt.HistoryCalls < 3)
        {
            await JsonAsync(context, History(id, "running", Array.Empty<object>()));
            return;
        }
        prompt.Terminal = true;
        await JsonAsync(context, new Dictionary<string, object>
        {
            [id] = new
            {
                status = new { status_str = "success", completed = true, messages = Array.Empty<object>() },
                outputs = new Dictionary<string, object>
                {
                    ["7"] = new { videos = new object[] { new { filename = "mock-output.mp4", subfolder = "", type = "output" } } }
                }
            }
        });
    }

    private async Task InterruptAsync(HttpListenerContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
        var id = document.RootElement.GetProperty("prompt_id").GetString();
        if (id is not null)
        {
            InterruptCount++;
            lock (prompts) if (prompts.TryGetValue(id, out var prompt)) prompt.Cancelled = true;
        }
        await JsonAsync(context, new { });
    }

    private async Task ViewAsync(HttpListenerContext context)
    {
        context.Response.ContentType = "video/mp4";
        context.Response.ContentLength64 = outputBytes.Length;
        await context.Response.OutputStream.WriteAsync(outputBytes);
    }

    /// <summary>
    /// 组装一条 history 记录。<c>completed</c> 默认 false：只有「跑完了」那一条会自己写，
    /// 而 running / error / interrupted 这三种在真实 ComfyUI 里都是 <c>completed = false</c>。
    /// </summary>
    private static Dictionary<string, object> History(string id, string status, object messages, bool completed = false) =>
        new() { [id] = new { status = new { status_str = status, completed, messages } } };

    private static async Task JsonAsync(HttpListenerContext context, object value)
    {
        context.Response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static int GetFreePort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        lifetime?.Cancel();
        listener.Stop();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        lifetime?.Dispose();
        listener.Close();
    }

    private sealed class MockPrompt(string id, MockMode mode)
    {
        public string Id { get; } = id;
        public MockMode Mode { get; } = mode;
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }
        public int HistoryCalls { get; set; }
    }

    private enum MockMode { Succeeded, Failed, Cancellable, CancellableError }
}
