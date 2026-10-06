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
    [System.Text.Json.Serialization.JsonPropertyName("extra_data")]
    public ComfyUiExtraData? ExtraData { get; init; }
}

public sealed record ComfyUiExtraData
{
    [System.Text.Json.Serialization.JsonPropertyName("extra_pnginfo")]
    public ComfyUiExtraPngInfo? ExtraPngInfo { get; init; }
}

public sealed record ComfyUiExtraPngInfo
{
    public JsonElement Workflow { get; init; }
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
        var effective = await ResolveUploadsAsync(invocation, cancellationToken).ConfigureAwait(false);
        var workflow = workflowFactory(effective);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                "prompt",
                new ComfyUiPromptRequest
                {
                    Prompt = workflow,
                    ClientId = clientId,
                    ExtraData = new ComfyUiExtraData
                    {
                        ExtraPngInfo = new ComfyUiExtraPngInfo { Workflow = workflow }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException error)
        {
            throw new InvalidOperationException(UnreachableMessage(error), error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"提交到 ComfyUI 超时（{Endpoint()}）：约定时间内没有回应。"
                + "机器可能还在加载模型，或者这条隧道很慢——稍等一会儿再重试；"
                + "一直超时就到设置里确认地址。",
                error);
        }

        // **别用 EnsureSuccessStatusCode**：它只留一个状态码，而服务端的报错正文才是最有用的那一句话
        // （它会点名哪个节点类型这台机器上没装、哪个必填输入缺了）。实测排查一次提交被拒时，
        // 就是被「只有一个 404」拖住的——正文里其实写着原因。
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (detail.Length > 800) detail = detail[..800] + "…";
            throw new InvalidOperationException(
                $"ComfyUI 拒绝了这次提交（HTTP {(int)response.StatusCode}）："
                + (detail.Trim().Length > 0 ? detail.Trim() : "服务端没有给正文")
                + RejectionHint(response.StatusCode));
        }

        var body = await response.Content.ReadFromJsonAsync<ComfyUiPromptResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.PromptId))
            throw new InvalidOperationException("ComfyUI 未返回 prompt_id");
        return new ExecutionOutput { ExternalTaskId = body.PromptId, AwaitExternalCompletion = true };
    }

    /// <summary>
    /// 自己这边的地址，出错时写进正文里。写出来是有意的：用户手里那台机器到底是哪个地址，
    /// 只有配置里才知道，而这条任务失败的答案往往就在「地址变了」这四个字上。
    /// </summary>
    private string Endpoint() => http.BaseAddress?.ToString() ?? "（未配置地址）";

    /// <summary>
    /// 「连不上」要说成连不上。
    ///
    /// 与「提交被拒」是两回事：被拒时服务端还在，它会点名缺哪个节点；连不上则说明**那台机器
    /// 或那条隧道已经不在了**，该去看地址，而不是去改提示词。原始的 socket 报错是英文的、
    /// 还带着端口和内部异常链，直接塞给用户等于什么都没说。
    /// </summary>
    private string UnreachableMessage(System.Net.Http.HttpRequestException error) =>
        $"连不上 ComfyUI（{Endpoint()}）：{FirstLine(error.Message)}。"
        + "确认那台机器上的 ComfyUI 还在跑、地址还是当前这个——云上临时隧道的地址每次重启都可能变。";

    private static string FirstLine(string message)
    {
        var index = message.IndexOfAny(new[] { '\r', '\n' });
        return (index < 0 ? message : message[..index]).Trim();
    }

    /// <summary>
    /// 404 这一种要额外说一句：ComfyUI 本身没有「路由不存在」这种回答，
    /// 所以 404 几乎总是**地址不对**或**那台机器上的 ComfyUI 没在跑**（云上的临时隧道尤其常见——
    /// 每次重启地址都可能变）。不补这一句，用户只会看到一个光秃秃的 404。
    /// </summary>
    private static string RejectionHint(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.NotFound
            ? "（404 通常意味着这个地址后面没有 ComfyUI：到「设置 → 生图与生视频 → ComfyUI」点一下「测试连接」，"
              + "确认地址还是那台在跑的机器——云上临时隧道的地址每次重启都可能变）"
            : string.Empty;

    /// <summary>
    /// 把本机的**素材**（参考图 / 源视频 / 源音频）全部上传到 ComfyUI 的输入目录——
    /// 工作流里的 <c>LoadImage</c> / <c>VHS_LoadVideo</c> / <c>LoadAudio</c> 这些节点是按**文件名**引用它们的。
    /// 上传后把这些字段换成 ComfyUI 侧的文件名；能用几个由那份工作流说了算。
    ///
    /// 为什么三样都走同一个端点：实测 <c>/upload/image</c> 收视频与音频一样好使——它就是往 input 目录里
    /// 存一个文件，不校验内容类型。传一段 mp4 上去之后，它立刻出现在 <c>VHS_LoadVideo.video</c> 的候选清单里
    /// （这就是那些工作流「从服务器文件里挑一个」的清单）。
    /// </summary>
    private async Task<Invocation> ResolveUploadsAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        var images = ReadLocalPaths(invocation, "referenceImages", "referenceImage");
        var videos = ReadLocalPaths(invocation, "referenceVideos", null);
        var audios = ReadLocalPaths(invocation, "referenceAudios", null);
        if (images.Count == 0 && videos.Count == 0 && audios.Count == 0) return invocation;

        var uploadedImages = await UploadAllAsync(images, "参考图", cancellationToken).ConfigureAwait(false);
        var uploadedVideos = await UploadAllAsync(videos, "源视频", cancellationToken).ConfigureAwait(false);
        var uploadedAudios = await UploadAllAsync(audios, "源音频", cancellationToken).ConfigureAwait(false);

        var inputs = new Dictionary<string, JsonElement>(invocation.Inputs);
        if (uploadedImages.Count > 0) inputs["referenceImages"] = JsonSerializer.SerializeToElement(uploadedImages);
        if (uploadedVideos.Count > 0) inputs["referenceVideos"] = JsonSerializer.SerializeToElement(uploadedVideos);
        if (uploadedAudios.Count > 0) inputs["referenceAudios"] = JsonSerializer.SerializeToElement(uploadedAudios);
        inputs.Remove("referenceImage");
        return invocation with { Inputs = inputs };
    }

    /// <summary>
    /// 逐个上传。**素材在本机找不到时不提交**：报错比「悄悄用工作流自带的那段示例素材」
    /// 好得多——后者出来的东西跟用户给的素材毫无关系，而且看不出哪里错了。
    /// </summary>
    private async Task<List<string>> UploadAllAsync(
        IReadOnlyList<string> paths, string label, CancellationToken cancellationToken)
    {
        var uploaded = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"这段{label}在本机找不到了：{path}。没有提交——不然这份工作流会拿它自己带的示例素材出片，"
                    + "出来的是别的东西，而你从结果上看不出来。重新选一次素材再出。");
            uploaded.Add(await UploadFileAsync(path, label, cancellationToken).ConfigureAwait(false));
        }
        return uploaded;
    }

    /// <summary>读素材的本机路径：优先列表形式，兼容旧的单图输入（<paramref name="legacyName"/> 为空表示没有旧写法）。</summary>
    private static List<string> ReadLocalPaths(Invocation invocation, string name, string? legacyName)
    {
        var paths = new List<string>();
        if (invocation.Inputs.TryGetValue(name, out var value))
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
        if (paths.Count == 0 && legacyName is not null
            && invocation.Inputs.TryGetValue(legacyName, out var single)
            && single.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(single.GetString()))
        {
            paths.Add(single.GetString()!);
        }
        return paths;
    }

    /// <summary>上传单个文件，返回 ComfyUI 侧的文件名（含子目录时带上）。</summary>
    private async Task<string> UploadFileAsync(string localPath, string label, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", Path.GetFileName(localPath));
        form.Add(new StringContent("true"), "overwrite");

        var response = await http.PostAsync("upload/image", form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (detail.Length > 800) detail = detail[..800] + "…";
            throw new InvalidOperationException(
                $"往 ComfyUI 传{label}被拒（HTTP {(int)response.StatusCode}）："
                + (detail.Trim().Length > 0 ? detail.Trim() : "服务端没有给正文")
                + RejectionHint(response.StatusCode));
        }
        var uploaded = await response.Content.ReadFromJsonAsync<ComfyUiUploadResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (uploaded is null || string.IsNullOrWhiteSpace(uploaded.Name))
            throw new InvalidOperationException($"ComfyUI 未返回上传后的文件名（{label}）");

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
            if (string.Equals(value, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                return new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Cancelled,
                    ProgressPercent = 100
                };
            }
            if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase))
            {
                // **被打断不等于是失败**：ComfyUI 对 `/interrupt` 打断掉的任务，history 里写的是
                // `status_str = "error"`、`completed = false`，真正的证据是 messages 末尾那条
                // `execution_interrupted`。**我们要的是用户点了取消这件事本身**，不是它借用了
                // 「错误」这个状态位；否则用户按下取消，界面最后告诉他「执行失败」，还会把一句
                // 与取消无关的报错塞到他面前。
                if (HistoryWasInterrupted(status))
                {
                    return new ExternalTaskUpdate
                    {
                        ExternalTaskId = externalTaskId,
                        State = ExternalTaskState.Cancelled,
                        ProgressPercent = 100
                    };
                }

                return new ExternalTaskUpdate
                {
                    ExternalTaskId = externalTaskId,
                    State = ExternalTaskState.Failed,
                    ProgressPercent = 100,
                    ErrorCode = "COMFYUI_TASK_FAILED",
                    // **把服务端说的那句话带出来**：`status.messages` 里写着是哪个节点、
                    // 什么异常、缺哪个文件——这才是用户要的答案。只回一句「工作流执行失败」，
                    // 用户手里一点线索都没有：是模型没装？是显存不够？还是提示词写坏了？
                    ErrorMessage = "ComfyUI 工作流执行失败" + DescribeHistoryError(status)
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

    /// <summary>
    /// history 的 <c>status.messages</c> 里有没有 <c>execution_interrupted</c>。
    ///
    /// 这是 ComfyUI 表示「这个 prompt 是被 /interrupt 打断的」的唯一凭据：它把打断记成
    /// <c>status_str = "error"</c>，与真正的节点异常共用同一个状态位，所以只能从消息里认。
    /// </summary>
    private static bool HistoryWasInterrupted(JsonElement status)
    {
        if (!status.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Array || message.GetArrayLength() < 1) continue;
            var kind = message[0].ValueKind == JsonValueKind.String ? message[0].GetString() : null;
            if (string.Equals(kind, "execution_interrupted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "execution_cancelled", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 从 history 的 <c>status.messages</c> 里挑出**真正有用的那一句**。
    ///
    /// 形状是 <c>[["execution_start",{…}],["execution_error",{"node_id":"5","node_type":"KSampler","exception_message":"…"}]]</c>。
    /// 找不到就给空串——宁可只说「执行失败」，也不要编一句原因出来。
    /// </summary>
    private static string DescribeHistoryError(JsonElement status)
    {
        if (!status.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Array || message.GetArrayLength() < 2) continue;
            var kind = message[0].ValueKind == JsonValueKind.String ? message[0].GetString() : null;
            if (!string.Equals(kind, "execution_error", StringComparison.OrdinalIgnoreCase)) continue;

            var detail = message[1];
            if (detail.ValueKind != JsonValueKind.Object) continue;

            var nodeId = ReadText(detail, "node_id") ?? string.Empty;
            var nodeType = ReadText(detail, "node_type") ?? string.Empty;
            var exception = ReadText(detail, "exception_message") ?? ReadText(detail, "exception_type")
                ?? string.Empty;
            var where = nodeId.Length > 0 && nodeType.Length > 0
                ? $"节点 {nodeId}（{nodeType}）"
                : nodeId.Length > 0 ? $"节点 {nodeId}" : string.Empty;

            if (where.Length == 0 && exception.Length == 0) continue;
            if (where.Length == 0) return $"：{exception}";
            return exception.Length == 0
                ? $"：{where}报错（服务端没给原因）"
                : $"：{where}报「{exception}」";
        }

        return string.Empty;
    }

    /// <summary>读一个可能是字符串、也可能是数字的字段（ComfyUI 的 <c>node_id</c> 两种都出现过）。</summary>
    private static string? ReadText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
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
