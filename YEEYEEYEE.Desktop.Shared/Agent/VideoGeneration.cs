using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

public enum VideoGenerationStatus { NotConfigured, Succeeded, Failed }

/// <summary>一次出视频请求：提示词、模型、画幅、时长与参考帧本机路径。</summary>
public sealed class VideoGenerationRequest
{
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;

    /// <summary>本次请求使用的视频模型名；留空表示用设置里的默认视频模型。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>本次请求使用的接口根地址；留空用设置里的视频接口地址（再退到主接口地址）。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>本次请求使用的接口路径，例如 /videos；留空按 OpenAI 兼容约定走 /videos。</summary>
    public string EndpointPath { get; init; } = string.Empty;

    /// <summary>
    /// 本次请求使用的密钥；留空时用设置里的视频接口密钥（再退到当前选中模型的密钥）。
    ///
    /// 与图像一致：**站点池子必须能带它自己那一家的密钥**——一家站一把账号，
    /// 用别家的密钥去打它的地址只会拿到 401。
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>目标画幅；0 表示不指定，由服务端或执行方决定。</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>目标时长（秒）；0 表示由服务端默认。</summary>
    public int Seconds { get; init; }

    /// <summary>参考帧的本机绝对路径列表（图生视频用；文生视频为空）。</summary>
    public IReadOnlyList<string> ReferenceImages { get; init; } = Array.Empty<string>();

    public bool HasReferenceImages => ReferenceImages.Count > 0;
}

public sealed class VideoGenerationResult
{
    public VideoGenerationStatus Status { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// 执行方对参考图的实际使用说明。**用不了全部时必须在这里说明，不许静默丢弃**——
    /// 与出图那条路（<see cref="ImageGenerationResult.ReferenceNote"/>）同一条规矩：
    /// 一镜引用了角色 / 道具 / 场景，视频却只用了首帧，不说的话用户会以为设定带上了、
    /// 只是模型没画好。
    /// </summary>
    public string ReferenceNote { get; init; } = string.Empty;
}

public interface IVideoProvider
{
    bool IsConfigured { get; }

    string Name { get; }

    /// <summary>
    /// 这条链路一次能用几张参考图。与出图侧的 <see cref="IImageProvider.ReferenceCapacity"/> 对称。
    /// 现在恒为 1（图生视频的首帧）——**不是**猜的：我们这家接口的提交体只有一个 <c>image</c> 字段。
    /// 将来哪家接口支持多图，改这里一处即可，调用方不必跟着动。
    /// </summary>
    ReferenceCapacity ReferenceCapacity { get; }

    Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 「还没配出视频链路」的兜底：设置里连地址与模型都没填时用它。
/// **这不是占位视频，也不是伪造结果**：它如实告诉用户还差什么，不产出任何文件。
/// </summary>
public sealed class UnconfiguredVideoProvider : IVideoProvider
{
    private readonly string detail;

    public UnconfiguredVideoProvider(string detail) => this.detail = detail;

    public bool IsConfigured => false;
    public string Name => "出视频未配置";

    public ReferenceCapacity ReferenceCapacity => new(0, "还没配出视频链路，无从判断能收几张参考图。");

    public Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationResult
        {
            Status = VideoGenerationStatus.NotConfigured,
            Provider = Name,
            Model = request.Model,
            Error = detail
        });
}

/// <summary>
/// 出视频链路的唯一入口。与 <see cref="ImageProviderFactory"/> 对称：
/// 配置齐了就走 <see cref="HttpVideoProvider"/>（异步提交 → 轮询 → 下载），
/// 没配齐则如实说还差什么。
/// </summary>
public static class VideoProviderFactory
{
    /// <summary>自动化回归用的执行方注入点（生产代码从不设置）。</summary>
    internal static IVideoProvider? Override { get; set; }

    public static IVideoProvider Create(AiProviderConfig? config = null)
    {
        if (Override is { } injected) return injected;
        var effective = config ?? AiProviderSettings.Load();
        return effective.IsVideoConfigured
            ? new HttpVideoProvider(effective)
            : new UnconfiguredVideoProvider(
                "还没有可用的出视频链路：请在「设置 → 生图与生视频 → 视频接口」里填上地址与模型"
                + (string.IsNullOrWhiteSpace(effective.VideoModel) ? "（模型名现在是空的）" : string.Empty)
                + "。填好之后，分镜节点右键的「出这一镜的视频」就能真的跑。");
    }
}

/// <summary>
/// 通过**我们这家视频接口**（AnyAIAPI）出视频：**提交 → 轮询 → 下载**，落盘后返回本机路径。
///
/// 形状：`POST /videos/generations`（文生视频走 JSON、图生视频走 multipart 的 `image` 字段）
/// → `GET /videos/{id}` 看 `status` → 服务端给的 `url`，没有就取 `GET /videos/{id}/content`。
/// 字段名是 `model / prompt / duration / resolution`，**不是** OpenAI 那套 `seconds / size / input_reference`。
/// 提交与轮询的路径都从「提交路径」推出来，所以站点池子换一家的路径时两条一起对。
///
/// 为什么必须异步：视频生成几乎没有同步返回的，一次要几十秒到几分钟，HTTP 连接撑着不现实。
///
/// 三条刻意的做法：
/// ① **不猜字段名也不猜状态词**：任务号在各家叫 id / task_id / video_id，下载地址叫 url / video_url / output_url……
///    这里按常见名字依次找；一个都找不到就**把返回体原文摘一段报出来**，而不是编一个结果或一直空转。
/// ② **密钥不外送**：下载地址若是**另一个域**（预设签名的 CDN），绝不把我们的 API Key 附上去——
///    附上去等于把密钥交给了第三方。只有同源地址才带鉴权。
/// ③ **失败就说失败**：超时、状态 failed、拿不到字节，一律如实报，绝不产出一个空文件冒充视频。
/// </summary>
public sealed class HttpVideoProvider : IVideoProvider
{
    /// <summary>
    /// 轮询间隔。视频任务通常要几十秒到几分钟，5 秒一次足够及时又不至于打爆接口。
    /// 回归测试会把它调成零，免得每个用例白等 5 秒（生产代码从不改它）。
    /// </summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>整个「提交 + 轮询 + 下载」的总时限。超了就如实报超时，不无限等下去。</summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(20);

    /// <summary>轮询遇到「服务端忙」时最多重试几次（每次退避等待，见 <see cref="RetryDelay"/>）。</summary>
    private const int TransientRetries = 3;

    private readonly AiProviderConfig config;
    private readonly HttpClient http;

    public HttpVideoProvider(AiProviderConfig config, HttpClient? http = null)
    {
        this.config = config;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public bool IsConfigured => config.IsVideoConfigured;
    public string Name => "OpenAiCompatibleVideo";

    /// <summary>
    /// 一次只收一张图。**不是保守，是接口就这样**：提交时只有一个 <c>image</c> 字段（见 SubmitAsync）。
    /// 所以一镜引用的角色 / 道具 / 场景在这条路上带不上——那就得说出来，不能装作带上了。
    /// </summary>
    public ReferenceCapacity ReferenceCapacity => new(1, "视频接口一次只收一张图（图生视频的首帧）。");

    public async Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) return Failed("视频提示词为空，无法生成。", request, request.Model);

        var model = string.IsNullOrWhiteSpace(request.Model) ? config.VideoModel : request.Model.Trim();
        // 地址 / 路径 / 密钥都可以逐次覆盖（站点池子带着自己那一家的配置）：
        // 与出图那条链同一条规矩，池子里的地址与密钥说了算，设置里的只当兜底。
        var baseUrl = (string.IsNullOrWhiteSpace(request.BaseUrl) ? config.EffectiveVideoEndpoint : request.BaseUrl)
            .Trim().TrimEnd('/');
        // 路径默认按**我们这家视频接口**（AnyAIAPI）的形状：
        // POST /videos/generations 建任务 → GET /videos/{id} 轮询 → GET /videos/{id}/content 下载。
        var submitPath = string.IsNullOrWhiteSpace(request.EndpointPath)
            ? "/videos/generations"
            : OpenAiCompatibleImageProvider.NormalizePath(request.EndpointPath);
        // 轮询与下载用的是**上一级**（/videos），不是提交那一级（/videos/generations）——
        // 这条从提交路径推出来，所以池子改路径时轮询也跟着对。
        var collectionPath = CollectionPathOf(submitPath);
        var apiKey = EffectiveApiKey(request);
        var seconds = request.Seconds > 0 ? request.Seconds : config.VideoDefaultSeconds;
        var references = request.ReferenceImages
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();
        // 用不完的参考图要**说出来**，不静默丢弃（与出图那条路同一条规矩）。视频这条路上
        // 「用不完」是常态：接口只收一张，而一镜可能引用了角色 / 道具 / 场景几张。
        var capacity = ReferenceCapacity;
        var referenceNote = capacity.IsLimited && references.Count > capacity.MaxImages
            ? $"视频接口一次只收 {capacity.MaxImages} 张图：已用第 1 张（首帧），"
              + $"忽略其余 {references.Count - capacity.MaxImages} 张——设定图在这条路上带不上。"
            : string.Empty;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(CompletionTimeout);
            var token = budget.Token;

            var probe = await SubmitAsync(baseUrl, submitPath, apiKey, model, request, seconds, references, token);
            if (probe.Error.Length > 0) return Failed(probe.Error, request, model);

            if (probe.DownloadUrl.Length == 0)
            {
                // 拿到的只有任务号：按 id 轮询到终态。
                var statusUrl = probe.StatusUrl.Length > 0
                    ? probe.StatusUrl
                    : $"{baseUrl}{collectionPath}/{Uri.EscapeDataString(probe.TaskId)}";
                while (true)
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    probe = await PollAsync(baseUrl, collectionPath, apiKey, statusUrl, token).ConfigureAwait(false);
                    if (probe.Error.Length > 0) return Failed(probe.Error, request, model);
                    if (!probe.Pending) break;
                }
            }

            if (probe.DownloadUrl.Length == 0)
                return Failed("视频任务已结束，但返回体里没有可下载的地址。", request, model);

            var bytes = await DownloadAsync(baseUrl, apiKey, probe.DownloadUrl, token).ConfigureAwait(false);
            if (bytes.Length == 0) return Failed("视频下载回来是空的，没有可用文件。", request, model);

            var path = Path.Combine(AssetStore.EnsureDirectory(), $"{Guid.NewGuid():N}{VideoFormatSniffer.ExtensionOf(bytes)}");
            File.WriteAllBytes(path, bytes);
            return new VideoGenerationResult
            {
                Status = VideoGenerationStatus.Succeeded,
                FilePath = path,
                Provider = Name,
                Model = model,
                ReferenceNote = referenceNote
            };
        }
        catch (OperationCanceledException)
        {
            return Failed(cancellationToken.IsCancellationRequested
                ? "出视频已取消。"
                : $"等待视频任务完成超时（超过 {CompletionTimeout.TotalMinutes:0} 分钟）。", request, model);
        }
        catch (HttpRequestException error) { return Failed($"视频接口请求失败：{error.Message}", request, model); }
        catch (JsonException error) { return Failed($"视频接口返回的不是合法 JSON：{error.Message}", request, model); }
        catch (IOException error) { return Failed($"保存视频失败：{error.Message}", request, model); }
    }

    /// <summary>
    /// 提交任务。带上参考帧时走 multipart（图生视频的首帧，字段名 <c>image</c>），否则走 JSON。
    ///
    /// 字段名按**我们这家接口**（AnyAIAPI）的约定：<c>model / prompt / duration / resolution</c>（+ 可选 fps）。
    /// 注意它**不是** OpenAI 那套 <c>seconds / size / input_reference</c>——发错字段名多半换回一个 400，
    /// 而 400 在这种「按次计费」的接口上不花钱，但会让人以为服务坏了。
    /// </summary>
    private async Task<VideoProbe> SubmitAsync(
        string baseUrl, string submitPath, string apiKey, string model,
        VideoGenerationRequest request, int seconds, IReadOnlyList<string> references, CancellationToken token)
    {
        var url = $"{baseUrl}{OpenAiCompatibleImageProvider.AvoidDuplicatedPrefix(baseUrl, submitPath)}";
        var resolution = ResolutionOf(request);

        async Task<HttpResponseMessage> PostAsync(bool durationAsText)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, url);
            OpenAiCompatibleImageProvider.ApplyAuth(message, apiKey, "bearer");

            string DurationText() => durationAsText ? $"{seconds}s" : seconds.ToString();

            if (references.Count > 0)
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(model), "model" },
                    { new StringContent(request.Prompt), "prompt" }
                };
                if (seconds > 0) form.Add(new StringContent(DurationText()), "duration");
                if (resolution.Length > 0) form.Add(new StringContent(resolution), "resolution");
                var frame = await File.ReadAllBytesAsync(references[0], token).ConfigureAwait(false);
                var file = new ByteArrayContent(frame);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "image", Path.GetFileName(references[0]));
                message.Content = form;
            }
            else
            {
                var payload = new Dictionary<string, object> { ["model"] = model, ["prompt"] = request.Prompt };
                if (seconds > 0) payload["duration"] = durationAsText ? $"{seconds}s" : seconds;
                if (resolution.Length > 0) payload["resolution"] = resolution;
                message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }

            return await http.SendAsync(message, token).ConfigureAwait(false);
        }

        var response = await PostAsync(durationAsText: false).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        // 少数部署把 duration 当字符串收（文档里写的是「可重试 15s 形式」）：
        // 只在 400 且带上了时长时重试一次，别的 400 直接如实报出来——重试改不了模型名写错这类问题。
        if (response.StatusCode == HttpStatusCode.BadRequest && seconds > 0)
        {
            response.Dispose();
            response = await PostAsync(durationAsText: true).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return VideoProbe.Fail($"视频接口提交返回 {(int)response.StatusCode}：{ExtractMessage(body)}"
                    + ((int)response.StatusCode == 400
                        ? "（检查必填的 duration、模型 id 与请求格式）"
                        : string.Empty));

            return ParseProbe(body, baseUrl, CollectionPathOf(submitPath), submitting: true);
        }
    }

    /// <summary>
    /// 轮询一次任务状态：还没好就是 Pending，好了带上下载地址，坏了带上原因。
    /// 429 / 503 / 504 是「服务端忙」，**有限重试**而不是立刻判死——视频任务动辄几分钟，一次限流不代表失败。
    /// 409 表示还在处理，按 Pending 算。
    /// </summary>
    private async Task<VideoProbe> PollAsync(string baseUrl, string collectionPath, string apiKey, string statusUrl, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            OpenAiCompatibleImageProvider.ApplyAuth(message, apiKey, "bearer");
            using var response = await http.SendAsync(message, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict) return VideoProbe.Waiting(string.Empty, statusUrl);
            if (IsTransient(response.StatusCode) && attempt < TransientRetries)
            {
                await Task.Delay(RetryDelay(attempt), token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return VideoProbe.Fail($"查询视频任务状态返回 {(int)response.StatusCode}：{ExtractMessage(body)}");

            // 轮询阶段没给出状态字段、却已经带上了下载地址的，按「已完成」处理——那是终态的样子。
            return ParseProbe(body, baseUrl, collectionPath, submitting: false);
        }
    }

    /// <summary>服务端忙：值得重试的状态码。</summary>
    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>重试等多久：2s、4s、8s…… 不无限退避，因为外层还有总时限兜着。</summary>
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

    /// <summary>
    /// 从提交路径推出「集合路径」（轮询与内容下载用的那一级）：
    /// <c>/videos/generations</c> → <c>/videos</c>；已经是一级（<c>/videos</c>）就原样保留。
    /// </summary>
    internal static string CollectionPathOf(string submitPath)
    {
        var trimmed = (submitPath ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0) return "/videos";
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return trimmed;
        var parent = trimmed[..lastSlash];
        return parent.Length == 0 ? "/videos" : parent;
    }

    /// <summary>
    /// 画幅。有明确的宽高就发 <c>1280x720</c> 这种等效尺寸（接口文档里竖屏写 <c>720x1280</c>、横屏写 <c>1280x720</c>，
    /// 是我们能确定表达的形状）；没有就不发，由服务端按模型的默认档位决定——**不替它猜一个档位**。
    /// </summary>
    private static string ResolutionOf(VideoGenerationRequest request) =>
        request.Width > 0 && request.Height > 0 ? $"{request.Width}x{request.Height}" : string.Empty;

    /// <summary>
    /// 解析响应体。<paramref name="submitting"/> 区分两个阶段的容错口径：
    /// 提交时**必须**拿到任务号（或直接给出下载地址），否则说明这个接口的形状我们不认识，要报出来；
    /// 轮询时没状态也没地址才算「还在跑」。
    /// </summary>
    private static VideoProbe ParseProbe(string body, string baseUrl, string collectionPath, bool submitting)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return VideoProbe.Fail($"接口返回的不是合法 JSON：{Trim(body, 300)}");
        }

        var carrier = NestedObject(root) ?? root;
        var status = ReadString(carrier, "status", "state", "task_status", "status_text");
        var id = ReadString(carrier, "id", "task_id", "taskId", "video_id", "videoId", "job_id");
        if (id.Length == 0) id = ReadString(root, "id", "task_id", "taskId", "video_id", "videoId", "job_id");
        var downloadUrl = ReadDownloadUrl(carrier);
        if (downloadUrl.Length == 0) downloadUrl = ReadDownloadUrl(root);

        var normalized = status.Trim().ToLowerInvariant();
        if (IsFailure(normalized))
            return VideoProbe.Fail($"视频任务失败（{status}）：{ReadError(root, carrier)}");

        if (IsSuccess(normalized))
        {
            // 完成但没给地址：回退到集合路径下的内容端点（/videos/{id}/content）。
            if (downloadUrl.Length == 0)
                downloadUrl = id.Length > 0 ? $"{baseUrl}{collectionPath}/{Uri.EscapeDataString(id)}/content" : string.Empty;
            return VideoProbe.Done(downloadUrl);
        }

        if (downloadUrl.Length > 0) return VideoProbe.Done(downloadUrl);
        if (id.Length > 0)
        {
            var statusUrl = ReadString(root, "status_url", "statusUrl");
            return VideoProbe.Waiting(id, statusUrl);
        }

        if (submitting)
            return VideoProbe.Fail(
                "接口没有返回任务 id，也没有直接给出视频地址——这个接口的形状与预期不符。返回体：" + Trim(body, 300));
        return VideoProbe.Fail("查询任务状态既没有状态字段，也没有任务 id。返回体：" + Trim(body, 300));
    }

    /// <summary>
    /// 取视频字节。**只有同源地址才带鉴权**：外部 CDN 的预签名地址自带票据，
    /// 往上附我们的 API Key 等于把密钥交给第三方。
    /// </summary>
    private async Task<byte[]> DownloadAsync(string baseUrl, string apiKey, string downloadUrl, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (IsSameOrigin(baseUrl, downloadUrl)) OpenAiCompatibleImageProvider.ApplyAuth(message, apiKey, "bearer");
        using var response = await http.SendAsync(message, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载视频返回 {(int)response.StatusCode}：{ExtractMessage(body)}");
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private string EffectiveApiKey(VideoGenerationRequest request) =>
        !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey
        : !string.IsNullOrWhiteSpace(config.VideoApiKey) ? config.VideoApiKey
        : !string.IsNullOrWhiteSpace(config.ApiKey) ? config.ApiKey
        : string.Empty;

    /// <summary>宿主地址是否与下载地址同源（协议 + 主机 + 端口）。解析不出来就按不同源处理，宁可不带密钥。</summary>
    internal static bool IsSameOrigin(string baseUrl, string other)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var left)) return false;
        if (!Uri.TryCreate(other, UriKind.Absolute, out var right)) return false;
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
    }

    /// <summary>响应体里那个「装着结果的对象」：<c>data[0]</c> 或 <c>data</c> 本身。</summary>
    private static JsonElement? NestedObject(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.Array)
            return data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.Object ? data[0] : null;
        return data.ValueKind == JsonValueKind.Object ? data : null;
    }

    private static string ReadDownloadUrl(JsonElement element) =>
        ReadString(element, "url", "video_url", "videoUrl", "output_url", "outputUrl", "download_url", "downloadUrl", "content_url");

    private static string ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
        }
        return string.Empty;
    }

    /// <summary>失败原因：<c>error.message</c>（对象）→ <c>error</c>（字符串）→ <c>message</c>，都取不到就回一句「接口没给」。 </summary>
    private static string ReadError(JsonElement root, JsonElement carrier)
    {
        foreach (var element in new[] { carrier, root })
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (element.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    var nested = ReadString(error, "message", "detail", "reason");
                    if (nested.Length > 0) return nested;
                }
                if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                    return error.GetString()!;
            }
            var message = ReadString(element, "message", "detail", "reason");
            if (message.Length > 0) return message;
        }
        return "接口没有说明原因。";
    }

    private static bool IsSuccess(string status) =>
        status is "completed" or "complete" or "succeeded" or "success" or "done" or "finished";

    private static bool IsFailure(string status) =>
        status is "failed" or "failure" or "error" or "cancelled" or "canceled";

    private static string ExtractMessage(string body) => Trim(body, 300);

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";

    private VideoGenerationResult Failed(string error, VideoGenerationRequest request, string? model = null) => new()
    {
        Status = VideoGenerationStatus.Failed,
        Provider = Name,
        Model = string.IsNullOrWhiteSpace(model) ? config.VideoModel : model,
        Error = error
    };

    /// <summary>
    /// 一次提交 / 轮询的结果。三者互斥：**还在跑**（有任务号）、**可以下载了**（有地址）、**出错了**。
    /// </summary>
    private readonly record struct VideoProbe(string TaskId, string StatusUrl, string DownloadUrl, bool Pending, string Error)
    {
        public static VideoProbe Waiting(string taskId, string statusUrl) => new(taskId, statusUrl, string.Empty, true, string.Empty);
        public static VideoProbe Done(string downloadUrl) => new(string.Empty, string.Empty, downloadUrl, false, string.Empty);
        public static VideoProbe Fail(string error) => new(string.Empty, string.Empty, string.Empty, false, error);
    }
}

/// <summary>
/// 按文件头判断视频容器类型。接口回的不一定是 mp4；存错扩展名会让播放器与后续转码判错格式
/// （与图片那边的 <see cref="ImageFormatSniffer"/> 同一个理由）。
/// </summary>
public static class VideoFormatSniffer
{
    public static string ExtensionOf(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 12) return ".mp4";
        // ISO BMFF / MP4：第 4-8 字节是 'ftyp'
        if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70) return ".mp4";
        // Matroska / WebM：EBML 魔数
        if (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3) return ".webm";
        return ".mp4";
    }
}
