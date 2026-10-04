using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Host;

public sealed record ComfyUiPromptRequest
{
    public JsonElement Prompt { get; init; }
    public string? ClientId { get; init; }
}

public sealed record ComfyUiPromptResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("prompt_id")]
    public string PromptId { get; init; } = string.Empty;
}

public sealed record ComfyUiUploadResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Subfolder { get; init; }
    public string? Type { get; init; }
}

public sealed class ComfyUiExecutor : IInvocationExecutor
{
    private readonly HttpClient http;
    private readonly Func<Invocation, JsonElement> workflowFactory;
    private readonly string? clientId;

    public ComfyUiExecutor(HttpClient http, Func<Invocation, JsonElement> workflowFactory, string? clientId = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.workflowFactory = workflowFactory ?? throw new ArgumentNullException(nameof(workflowFactory));
        this.clientId = clientId;
    }

    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        var effective = await ResolveReferenceImagesAsync(invocation, cancellationToken).ConfigureAwait(false);
        var response = await http.PostAsJsonAsync(
            "prompt",
            new ComfyUiPromptRequest { Prompt = workflowFactory(effective), ClientId = clientId },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ComfyUiPromptResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.PromptId))
            throw new InvalidOperationException("ComfyUI 未返回 prompt_id");
        return new ExecutionOutput { ExternalTaskId = body.PromptId, AwaitExternalCompletion = true };
    }

    /// <summary>
    /// 把本机参考图全部上传到 ComfyUI 的输入目录，LoadImage 节点才能按文件名引用它们。
    /// 上传后把 referenceImages 替换成 ComfyUI 侧的文件名；能用几张由工作流模板决定。
    /// </summary>
    private async Task<Invocation> ResolveReferenceImagesAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        var localPaths = ReadReferencePaths(invocation);
        if (localPaths.Count == 0) return invocation;

        var uploaded = new List<string>();
        foreach (var path in localPaths)
        {
            if (!File.Exists(path)) continue;
            uploaded.Add(await UploadImageAsync(path, cancellationToken).ConfigureAwait(false));
        }
        if (uploaded.Count == 0) return invocation;

        var inputs = new Dictionary<string, JsonElement>(invocation.Inputs)
        {
            ["referenceImages"] = JsonSerializer.SerializeToElement(uploaded)
        };
        inputs.Remove("referenceImage");
        return invocation with { Inputs = inputs };
    }

    /// <summary>读取参考图的本机路径：优先列表形式，兼容旧的单图输入。</summary>
    private static List<string> ReadReferencePaths(Invocation invocation)
    {
        var paths = new List<string>();
        if (invocation.Inputs.TryGetValue("referenceImages", out var value))
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        paths.Add(item.GetString()!);
            }
            else if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                paths.Add(value.GetString()!);
            }
        }
        if (paths.Count == 0
            && invocation.Inputs.TryGetValue("referenceImage", out var single)
            && single.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(single.GetString()))
        {
            paths.Add(single.GetString()!);
        }
        return paths;
    }

    /// <summary>上传单张图片，返回 ComfyUI 侧的文件名。</summary>
    private async Task<string> UploadImageAsync(string localPath, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", Path.GetFileName(localPath));
        form.Add(new StringContent("true"), "overwrite");

        var response = await http.PostAsync("upload/image", form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var uploaded = await response.Content.ReadFromJsonAsync<ComfyUiUploadResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (uploaded is null || string.IsNullOrWhiteSpace(uploaded.Name))
            throw new InvalidOperationException("ComfyUI 未返回上传后的图片名");

        return string.IsNullOrWhiteSpace(uploaded.Subfolder) ? uploaded.Name : $"{uploaded.Subfolder}/{uploaded.Name}";
    }
}

public sealed class ComfyUiProvider : IExternalTaskProvider, IExternalTaskCanceller, IExternalTaskProgressSource
{
    private readonly HttpClient http;
    private readonly Uri? webSocketBaseUri;
    private readonly string? clientId;
    private readonly string? assetDirectory;
    private readonly TimeSpan websocketReceiveTimeout;
    private readonly TimeSpan websocketReconnectDelay;
    private readonly int websocketMaxReconnectAttempts;
    private readonly SemaphoreSlim assetDownloadLock = new(1, 1);

    /// <summary>
    /// 历史记录里认哪些输出数组、它们各是什么东西。
    ///
    /// **必须认全**：ComfyUI 按产物种类分桶——`SaveImage` 放 <c>images</c>，
    /// `SaveWEBM` / `SaveAnimatedWEBP` 放 <c>gifs</c>（历史叫法，里面其实是视频或动图），
    /// `SaveVideo` 放 <c>videos</c>，`SaveAudio` 放 <c>audio</c>。
    ///
    /// 早先只认 <c>images</c>，后果不是「少收一张」而是**整条视频链走不通**：
    /// 一份出视频的工作流跑完了，产物就在 <c>gifs</c> 里，而下载那一步什么都没找到，
    /// 任务于是永远停在「运行中」，最后以超时收场——报出来的是超时，真正的原因却在桶名上。
    /// </summary>
    private static readonly KeyValuePair<string, string>[] OutputBuckets =
    {
        new("images", "image"),
        new("gifs", "video"),
        new("videos", "video"),
        new("audio", "audio")
    };

    public ComfyUiProvider(
        HttpClient http,
        string? assetDirectory = null,
        Uri? webSocketBaseUri = null,
        string? clientId = null,
        TimeSpan? websocketReceiveTimeout = null,
        TimeSpan? websocketReconnectDelay = null,
        int websocketMaxReconnectAttempts = 5)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.webSocketBaseUri = webSocketBaseUri;
        this.clientId = clientId;
        this.websocketReceiveTimeout = websocketReceiveTimeout ?? TimeSpan.FromSeconds(30);
        this.websocketReconnectDelay = websocketReconnectDelay ?? TimeSpan.FromSeconds(1);
        if (this.websocketReceiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(websocketReceiveTimeout));
        if (this.websocketReconnectDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(websocketReconnectDelay));
        if (websocketMaxReconnectAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(websocketMaxReconnectAttempts));
        this.websocketMaxReconnectAttempts = websocketMaxReconnectAttempts;
        this.assetDirectory = string.IsNullOrWhiteSpace(assetDirectory)
            ? null
            : Path.GetFullPath(assetDirectory);
        if (this.assetDirectory is not null)
            Directory.CreateDirectory(this.assetDirectory);
    }

    public async Task<ExternalTaskUpdate> GetStatusAsync(string externalTaskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId))
            throw new ArgumentException("ComfyUI prompt_id 不能为空", nameof(externalTaskId));

        using var response = await http.GetAsync(
            $"history/{Uri.EscapeDataString(externalTaskId)}",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty(externalTaskId, out var history))
            return await GetQueueStatusAsync(externalTaskId, cancellationToken).ConfigureAwait(false);

        if (history.TryGetProperty("status", out var status)
            && status.TryGetProperty("status_str", out var statusText))
        {
            var value = statusText.GetString();
            if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Failed,
                    ProgressPercent = 100,
                    ErrorCode = "COMFYUI_TASK_FAILED",
                    ErrorMessage = "ComfyUI 工作流执行失败"
                };
            }
        }

        if (history.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Object)
        {
            var assets = new List<AssetRef>();
            foreach (var node in outputs.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var bucket in OutputBuckets)
                {
                    if (!node.Value.TryGetProperty(bucket.Key, out var items)
                        || items.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        // 图片那一路沿用「缺 filename 就报错」的老口径（既有行为，改了会静默少收图）；
                        // 视频 / 音频这几路是新认的，自定义节点包产出的条目形状不一定一样，
                        // 缺字段就跳过这一条，不因为一条怪条目把整次任务判死。
                        var filename = bucket.Key == "images"
                            ? GetRequiredString(item, "filename")
                            : GetOptionalString(item, "filename");
                        if (string.IsNullOrWhiteSpace(filename)) continue;

                        var subfolder = GetOptionalString(item, "subfolder") ?? string.Empty;
                        var type = GetOptionalString(item, "type") ?? "output";
                        var assetRef = await DownloadAssetAsync(
                            filename,
                            subfolder,
                            type,
                            cancellationToken).ConfigureAwait(false);
                        assets.Add(new AssetRef { Role = bucket.Value, Ref = assetRef });
                    }
                }
            }

            return new ExternalTaskUpdate
            {
                ExternalTaskId = externalTaskId,
                State = ExternalTaskState.Succeeded,
                ProgressPercent = 100,
                Outputs = assets
            };
        }

        return new ExternalTaskUpdate
        {
            ExternalTaskId = externalTaskId,
            State = ExternalTaskState.Running,
            ProgressPercent = 50
        };
    }

    public async Task CancelAsync(string externalTaskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId))
            throw new ArgumentException("ComfyUI prompt_id 不能为空", nameof(externalTaskId));

        using var response = await http.PostAsJsonAsync(
            "interrupt",
            new { prompt_id = externalTaskId },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ListenForProgressAsync(
        string externalTaskId,
        Action<ExternalTaskUpdate> onUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTaskId);
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (webSocketBaseUri is null)
            return;

        var builder = new UriBuilder(webSocketBaseUri)
        {
            Scheme = webSocketBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
            Path = "/ws",
            Query = string.IsNullOrWhiteSpace(clientId)
                ? string.Empty
                : $"clientId={Uri.EscapeDataString(clientId)}"
        };
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
                reconnectAttempt = 0;
                var terminal = await ReceiveProgressMessagesAsync(
                    socket,
                    externalTaskId,
                    onUpdate,
                    cancellationToken).ConfigureAwait(false);
                if (terminal)
                    return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException) when (reconnectAttempt < websocketMaxReconnectAttempts)
            {
            }
            catch (TimeoutException) when (reconnectAttempt < websocketMaxReconnectAttempts)
            {
            }
            catch (WebSocketException)
            {
                return;
            }
            catch (TimeoutException)
            {
                return;
            }

            if (reconnectAttempt >= websocketMaxReconnectAttempts)
                return;

            var delayMilliseconds = Math.Min(
                websocketReconnectDelay.TotalMilliseconds * Math.Pow(2, reconnectAttempt),
                TimeSpan.FromSeconds(30).TotalMilliseconds);
            reconnectAttempt++;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReceiveProgressMessagesAsync(
        ClientWebSocket socket,
        string externalTaskId,
        Action<ExternalTaskUpdate> onUpdate,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(websocketReceiveTimeout);
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, receiveTimeout.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return false;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("ComfyUI WebSocket 接收超时");
            }

            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || !root.TryGetProperty("data", out var data))
                continue;
            if (!data.TryGetProperty("prompt_id", out var prompt)
                || !string.Equals(prompt.GetString(), externalTaskId, StringComparison.Ordinal))
                continue;

            var type = typeElement.GetString();
            if (string.Equals(type, "progress", StringComparison.OrdinalIgnoreCase)
                && data.TryGetProperty("value", out var value)
                && data.TryGetProperty("max", out var max)
                && value.TryGetInt32(out var current)
                && max.TryGetInt32(out var total)
                && total > 0)
            {
                onUpdate(new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Running,
                    ProgressPercent = Math.Clamp(current * 100 / total, 0, 99)
                });
            }
            else if (string.Equals(type, "execution_success", StringComparison.OrdinalIgnoreCase))
            {
                onUpdate(new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Succeeded,
                    ProgressPercent = 100
                });
                return true;
            }
            else if (string.Equals(type, "execution_error", StringComparison.OrdinalIgnoreCase))
            {
                onUpdate(new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Failed,
                    ProgressPercent = 100,
                    ErrorCode = "COMFYUI_EXECUTION_ERROR",
                    ErrorMessage = "ComfyUI 节点执行失败"
                });
                return true;
            }
        }

        return cancellationToken.IsCancellationRequested;
    }

    private async Task<ExternalTaskUpdate> GetQueueStatusAsync(
        string externalTaskId,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("queue", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var running = ContainsPrompt(root, "queue_running", externalTaskId);
        var pending = ContainsPrompt(root, "queue_pending", externalTaskId);
        return new ExternalTaskUpdate
        {
            ExternalTaskId = externalTaskId,
            State = running ? ExternalTaskState.Running : ExternalTaskState.Queued,
            ProgressPercent = running ? 1 : 0
        };
    }

    private static bool ContainsPrompt(JsonElement root, string property, string externalTaskId)
    {
        if (!root.TryGetProperty(property, out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Array
                && entry.GetArrayLength() > 1
                && string.Equals(entry[1].GetString(), externalTaskId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private async Task<string> DownloadAssetAsync(
        string filename,
        string subfolder,
        string type,
        CancellationToken cancellationToken)
    {
        if (assetDirectory is null)
            return $"comfyui://{filename}";

        var safeName = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("ComfyUI 输出文件名无效");

        var extension = Path.GetExtension(safeName);
        var localName = $"{Guid.NewGuid():N}{extension}";
        var localPath = Path.Combine(assetDirectory, localName);
        var query = $"view?filename={Uri.EscapeDataString(filename)}"
            + $"&subfolder={Uri.EscapeDataString(subfolder)}"
            + $"&type={Uri.EscapeDataString(type)}";

        using var response = await http.GetAsync(
            query,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            localPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        return $"asset://{localName}";
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        var value = GetOptionalString(element, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"ComfyUI 输出缺少 {property}")
            : value;
    }

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
