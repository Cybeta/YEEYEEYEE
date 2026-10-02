using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

public enum ImageGenerationStatus { NotConfigured, Succeeded, Failed }

public sealed class ImageGenerationRequest
{
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;

    /// <summary>
    /// 本次请求使用的模型名；留空表示用设置里的默认图像模型。
    /// 由接口文档生成的池子技能会给每一步指定模型，因此模型名必须能逐次覆盖。
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>本次请求使用的接口根地址（返工 R4）；留空用设置里的图像接口地址。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>本次请求使用的接口路径（返工 R4），例如 /v1/images/generations；留空按能力走默认路径。</summary>
    public string EndpointPath { get; init; } = string.Empty;

    /// <summary>HTTP 方法（返工 R4）；留空按 POST。</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>鉴权方式（返工 R4）：bearer / x-api-key / query；留空按 bearer。</summary>
    public string AuthStyle { get; init; } = string.Empty;

    /// <summary>
    /// 本次请求使用的密钥；留空时用设置里的（图像接口密钥 → 当前选中模型的密钥）。
    ///
    /// 站点池子必须能带**它自己那一家**的密钥：一家站一把账号，用别家的密钥去打它的地址只会拿到 401。
    /// 只有一个全局密钥的话，导入第二家站就会把第一家的密钥覆盖掉，第一家那些池子从此全部用不了。
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public int? Steps { get; init; }
    public double? Cfg { get; init; }
    public long? Seed { get; init; }

    /// <summary>
    /// 参考图的本机绝对路径列表，按引用顺序排列。这里**不设数量上限**：
    /// 能用几张是执行方（ComfyUI 工作流或图像接口）的能力问题，画布只负责说明
    /// 「这个镜头有哪些参考图」。将来接入支持多图的工作流时，这一层无需改动。
    /// </summary>
    public IReadOnlyList<string> ReferenceImages { get; init; } = Array.Empty<string>();

    /// <summary>图生图的重绘强度，越大变化越多；未设置时用服务默认。</summary>
    public double? Denoise { get; init; }

    public bool HasReferenceImages => ReferenceImages.Count > 0;

    public string SizeText => $"{Width}x{Height}";
}

public sealed class ImageGenerationResult
{
    public ImageGenerationStatus Status { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// 执行方对参考图的实际使用说明，例如「当前工作流只支持单张底图，已使用第 1 张，忽略 2 张」。
    /// 执行方用不了全部参考图时，必须在这里说明，而不是静默丢弃。
    /// </summary>
    public string ReferenceNote { get; init; } = string.Empty;
}

/// <summary>
/// 链路对参考图的能力声明：一次最多能同时使用几张参考图。
/// 这是执行方（ComfyUI 工作流模板或图像接口）的能力，**不属于画布**；
/// 画布只负责说明「这个镜头有哪些参考图」，超限时由宿主提前告知用户。
/// </summary>
public sealed record ReferenceCapacity(int MaxImages, string Description)
{
    /// <summary>0 表示不限制。</summary>
    public bool IsLimited => MaxImages > 0;

    /// <summary>是否具备分步合成的前提：至少能同时使用两张参考图。</summary>
    public bool CanUseMultiple => MaxImages <= 0 || MaxImages >= 2;
}

/// <summary>提交方式：决定这条链路是同步返回，还是提交任务后再轮询取结果。</summary>
public enum SubmissionKind
{
    /// <summary>同步出图：一次请求直接返回结果。</summary>
    SyncImage,

    /// <summary>异步出图：提交后拿到任务 id，轮询取结果。ComfyUI 走这条。</summary>
    AsyncImage,

    /// <summary>异步视频：提交后轮询取视频。</summary>
    AsyncVideo
}

/// <summary>
/// 一条链路的提交方式声明：能力、参考图上限、同步/异步、是否需要先上传参考图。
/// **写工作流模板前必须先确定这里的内容**——ComfyUI 的多图能力取决于装了哪些自定义节点，
/// 视频几乎都是异步任务，两者的提交方式完全不同，靠猜写出来的模板跑不起来。
/// </summary>
public sealed record SubmissionProfile(
    string Id,
    string Name,
    Capability Capability,
    SubmissionKind Kind,
    ReferenceCapacity ReferenceCapacity,
    bool RequiresUpload,
    string Description)
{
    /// <summary>提交后是否需要等待外部任务完成。</summary>
    public bool IsAsync => Kind is SubmissionKind.AsyncImage or SubmissionKind.AsyncVideo;
}

/// <summary>
/// 已实现并声明的提交方式。新增一条（例如支持更多参考图的模板、或视频模板）时：
/// 1) 在这里加一条声明；2) 在 ComfyUiWorkflowFactory 里按同一个 Id 加分支。
/// 画布、请求、执行器与技能都不需要改动。
/// </summary>
public static class ComfyUiSubmissionProfiles
{
    /// <summary>单图底图（img2img）：异步提交工作流，参考图需先上传。</summary>
    public static readonly SubmissionProfile SingleBaseImage = new(
        "img2img",
        "单图底图（img2img）",
        Capability.ImageToImage,
        SubmissionKind.AsyncImage,
        new ReferenceCapacity(1, "当前 img2img 工作流只支持单张底图。"),
        RequiresUpload: true,
        "提交 /prompt 后轮询任务结果；参考图先上传到 ComfyUI 输入目录再按文件名引用。");
}

/// <summary>兼容旧名字：参考图模式即提交方式声明。</summary>
public static class ComfyUiReferenceModes
{
    public static readonly SubmissionProfile SingleBaseImage = ComfyUiSubmissionProfiles.SingleBaseImage;

    public static IReadOnlyList<SubmissionProfile> All { get; } = new[] { ComfyUiSubmissionProfiles.SingleBaseImage };

    /// <summary>当前实现并默认使用的提交方式。</summary>
    public static SubmissionProfile Current => ComfyUiSubmissionProfiles.SingleBaseImage;

    public static SubmissionProfile Find(string? id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Current;
}

public interface IImageProvider
{
    bool IsConfigured { get; }
    string Name { get; }

    /// <summary>
    /// 该链路的提交方式声明：一次能同时用几张参考图、是否异步、是否需要上传。
    /// 宿主用它做提交前预检（超限时提示分步合成或截断），并据此规划合成步数。
    /// </summary>
    ReferenceCapacity ReferenceCapacity { get; }

    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}

public static class ImageProviderFactory
{
    /// <summary>
    /// 自动化回归用的执行方注入点（生产代码从不设置）：非空时 <see cref="CreateFor"/> 与 <see cref="Create"/>
    /// 一律返回它，便于用计数桩证明「被阻断时一次请求都没发出去」（复核 G6-S1）。
    /// </summary>
    internal static IImageProvider? Override { get; set; }

    /// <summary>
    /// 按技能选执行方（返工 S4）：**导入的 API 技能带着自己的执行配置（Endpoint）**，
    /// 必须走 OpenAI 兼容链路去打它记下来的地址与模型，不能被「已配置 ComfyUI」抢过去——
    /// 否则界面上写的是「按导入的接口执行」，实际却打到本地 ComfyUI，导入结果等于没生效。
    /// 没有执行配置的技能保持原有优先级（ComfyUI 优先）。
    /// 技能运行与导入向导的最小测试都走这一个入口，保证「测过的就是会跑的」。
    /// </summary>
    public static IImageProvider CreateFor(SkillDefinition? skill, SingleMachineExecutionService? execution = null)
    {
        if (Override is { } injected) return injected;
        var config = AiProviderSettings.Load();
        // 导入技能（带来源标识）一律走 API 链路：即使它因为归属不明而没有写死路径，
        // 也不能回退到默认执行方去跑（返工 U4）。
        if (skill is not null && (skill.IsImported || UsesImportedEndpoints(skill)))
            return new OpenAiCompatibleImageProvider(config);
        return Create(execution);
    }

    /// <summary>该技能的步骤里是否带导入的接口执行配置（有就说明它按文档里的接口执行）。</summary>
    public static bool UsesImportedEndpoints(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Steps.Any(step => step.Endpoint is { IsEmpty: false });
    }

    /// <summary>
    /// 优先使用本地 ComfyUI 执行链路（需要执行服务），其次使用 OpenAI 兼容图像接口。
    ///
    /// **没传执行服务时自己解析一份**：早先只有旧端会传，Avalonia 端没有任何构造点，
    /// 结果「设置了 ComfyUI 却永远走不到 ComfyUI」——配置页上那一整块等于装饰。
    /// 现在由工厂自己持有应用级共享宿主，所有调用点都自动受益。
    /// </summary>
    public static IImageProvider Create(SingleMachineExecutionService? execution = null)
    {
        if (Override is { } injected) return injected;
        var config = AiProviderSettings.Load();
        execution ??= ResolveSharedHost(config);
        if (config.IsComfyUiConfigured && execution is not null)
            return new ComfyUiImageProvider(execution, config, new SessionContext
            {
                SessionId = Guid.NewGuid(),
                UserId = DesktopUserId,
                ClientType = ClientType.Desktop,
                Role = MemberRole.Member,
                ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"])
            });
        return config.IsImageConfigured
            ? new OpenAiCompatibleImageProvider(config)
            : new UnconfiguredImageProvider();
    }

    private static DesktopExecutionHost? sharedHost;
    private static string sharedHostSignature = string.Empty;

    /// <summary>
    /// ComfyUI 链路要一个执行服务才跑得起来，这里按当前配置解析一个（只建一份，像旧端那样长驻）。
    /// **配置变了就重建**：地址 / checkpoint / 客户端 ID / 资产目录一变，旧的执行器还指着旧地址，
    /// 继续用会往错的地方发任务。没配 ComfyUI 时返回 null——不建宿主，省一个数据库连接与轮询定时器。
    /// </summary>
    private static SingleMachineExecutionService? ResolveSharedHost(AiProviderConfig config)
    {
        if (!config.IsComfyUiConfigured)
        {
            if (sharedHost is not null)
            {
                var stale = sharedHost;
                sharedHost = null;
                sharedHostSignature = string.Empty;
                _ = stale.DisposeAsync();
            }
            return null;
        }

        var signature = $"{config.ComfyUiBaseUrl}|{config.ComfyUiCheckpoint}|{config.ComfyUiClientId}|{AssetStore.Directory}";
        if (sharedHost is not null && signature == sharedHostSignature) return sharedHost.Execution;
        if (sharedHost is not null)
        {
            var stale = sharedHost;
            sharedHost = null;
            _ = stale.DisposeAsync();
        }
        sharedHost = DesktopExecutionHost.Create();
        sharedHostSignature = signature;
        return sharedHost.Execution;
    }

    /// <summary>退出时释放共享宿主：任务轮询与数据库连接都要收干净。</summary>
    public static async ValueTask DisposeSharedHostAsync()
    {
        if (sharedHost is null) return;
        var host = sharedHost;
        sharedHost = null;
        sharedHostSignature = string.Empty;
        await host.DisposeAsync().ConfigureAwait(false);
    }

    public static readonly Guid DesktopUserId = Guid.Parse("4d7265616d466f726765000000000042");
}

public sealed class UnconfiguredImageProvider : IImageProvider
{
    public bool IsConfigured => false;
    public string Name => "未配置";
    public ReferenceCapacity ReferenceCapacity => new(0, "未配置图像模型，无法判断参考图能力。");

    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImageGenerationResult
        {
            Status = ImageGenerationStatus.NotConfigured,
            Provider = Name,
            Error = "尚未配置图像模型。请在“设置”中填写 ComfyUI 地址与 checkpoint，或填写图像模型名称。"
        });
}

/// <summary>
/// 通过 OpenAI 兼容的 /images/generations 接口生成参考图，并把图片保存到本地资产目录。
/// 只返回真实保存的文件路径，不会生成占位图片。
/// </summary>
public sealed class OpenAiCompatibleImageProvider : IImageProvider
{
    private readonly HttpClient http;
    private readonly AiProviderConfig config;

    public OpenAiCompatibleImageProvider(AiProviderConfig config, HttpClient? http = null)
    {
        this.config = config;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public bool IsConfigured => true;
    public string Name => "OpenAiCompatibleImage";

    /// <summary>上限来自配置：接口能吃几张取决于所用模型，无法自动探测，因此交给用户声明。</summary>
    public ReferenceCapacity ReferenceCapacity => new(
        Math.Max(0, config.ImageMaxReferenceImages),
        config.ImageMaxReferenceImages <= 0
            ? "未声明上限，按不限制处理。"
            : $"接口一次最多使用 {config.ImageMaxReferenceImages} 张参考图（可在设置里调整）。");

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return Failed("图像提示词为空，无法生成。");

        var references = request.ReferenceImages
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();
        var useReference = references.Count > 0;
        // 每次请求的模型名优先于设置里的默认模型：池子技能靠它把「生图池1 / 生图池2」区分开。
        var model = string.IsNullOrWhiteSpace(request.Model) ? config.ImageModel : request.Model;
        // 来源、路径、方法、鉴权都可以逐次覆盖（返工 R4）：导入的自定义接口按技能里记的配置打，
        // 不再一律去打 /images/generations 加 Bearer，否则导入的接口根本证明不了可用。
        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? config.EffectiveImageEndpoint : request.BaseUrl.Trim();
        var endpointPath = string.IsNullOrWhiteSpace(request.EndpointPath)
            ? $"/images/{(useReference ? "edits" : "generations")}"
            : NormalizePath(request.EndpointPath);
        var method = string.IsNullOrWhiteSpace(request.Method) ? "POST" : request.Method.Trim().ToUpperInvariant();
        var authStyle = NormalizeAuthStyle(request.AuthStyle);
        var url = $"{baseUrl.TrimEnd('/')}{AvoidDuplicatedPrefix(baseUrl, endpointPath)}";
        // 密钥三层，从具体到笼统：
        // ① 请求自带的（站点池子带的是那家站自己的账号）；
        // ② 设置里图像接口**自己的**密钥；
        // ③ 聊天模型那一把（与设置页上的说明一致：出图常常是另一家服务，共用一把密钥在多数情况下根本用不了）。
        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey.Trim()
            : string.IsNullOrWhiteSpace(config.ImageApiKey) ? config.ApiKey ?? string.Empty
            : config.ImageApiKey;
        if (authStyle == "query" && apiKey.Length > 0)
            url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(apiKey);

        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(method), url);
            ApplyAuth(message, apiKey, authStyle);

            if (useReference)
            {
                // 图生图走标准 /images/edits：multipart 形式提交底图与提示词。
                // 多张参考图按 image[] 重复提交；实际能吃几张由所用模型决定。
                var form = new MultipartFormDataContent();
                var field = references.Count > 1 ? "image[]" : "image";
                foreach (var referencePath in references)
                {
                    var imageBytes = await File.ReadAllBytesAsync(referencePath, cancellationToken);
                    var file = new ByteArrayContent(imageBytes);
                    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    form.Add(file, field, Path.GetFileName(referencePath));
                }
                form.Add(new StringContent(model), "model");
                form.Add(new StringContent(ComposePrompt(request.Prompt, request.NegativePrompt)), "prompt");
                form.Add(new StringContent(request.SizeText), "size");
                form.Add(new StringContent("1"), "n");
                message.Content = form;
            }
            else
            {
                var payload = new
                {
                    model,
                    prompt = ComposePrompt(request.Prompt, request.NegativePrompt),
                    size = request.SizeText,
                    n = 1
                };
                message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Failed($"图像接口返回 {(int)response.StatusCode}：{Trim(body, 300)}", model);

            var bytes = await ReadImageBytesAsync(body, cancellationToken);
            if (bytes is null || bytes.Length == 0)
                return Failed("图像接口没有返回可用的图片数据。", model);

            var path = SaveAssets(bytes);
            return new ImageGenerationResult
            {
                Status = ImageGenerationStatus.Succeeded,
                FilePath = path,
                Provider = Name,
                Model = model,
                ReferenceNote = references.Count > 1
                    ? $"已按 image[] 提交 {references.Count} 张参考图；能否全部生效取决于所用模型（多数模型只使用第一张）。"
                    : string.Empty
            };
        }
        catch (HttpRequestException error) { return Failed($"图像接口请求失败：{error.Message}", model); }
        catch (TaskCanceledException) { return Failed("图像接口请求超时。", model); }
        catch (IOException error) { return Failed($"保存图片失败：{error.Message}", model); }
    }

    private static string ComposePrompt(string prompt, string negativePrompt) =>
        string.IsNullOrWhiteSpace(negativePrompt) ? prompt : $"{prompt}\n避免出现：{negativePrompt}";

    private async Task<byte[]?> ReadImageBytesAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
        var first = data[0];
        if (first.TryGetProperty("b64_json", out var base64) && !string.IsNullOrWhiteSpace(base64.GetString()))
            return Convert.FromBase64String(base64.GetString()!);
        if (first.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
            return await http.GetByteArrayAsync(url.GetString()!, cancellationToken);
        return null;
    }

    private static string SaveAssets(byte[] bytes)
    {
        var path = Path.Combine(AssetStore.EnsureDirectory(), $"{Guid.NewGuid():N}{ImageFormatSniffer.ExtensionOf(bytes)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ImageGenerationResult Failed(string error, string? model = null) => new()
    {
        Status = ImageGenerationStatus.Failed,
        Provider = Name,
        Model = model ?? config.ImageModel,
        Error = error
    };

    /// <summary>把接口路径规整成以 / 开头（返工 R4）。</summary>
    internal static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// 基础地址已经带了版本段（例如 https://host/v2）而接口路径也以它开头时，去掉路径里的重复段，
    /// 免得拼出 https://host/v2/v2/render/generations。文档里写的是绝对路径，很容易两边都带 /v2。
    /// </summary>
    internal static string AvoidDuplicatedPrefix(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return path;
        var basePath = uri.AbsolutePath.TrimEnd('/');
        if (basePath.Length <= 1) return path;
        return path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
            ? path[basePath.Length..]
            : path;
    }

    /// <summary>
    /// 鉴权方式规整（返工 R4）：只认 bearer / x-api-key / query，其余（含空）按 bearer。
    /// 用包含匹配而不是全等：文档里抽出来的写法五花八门（"X-Api-Key"、"api_key 参数"、"Authorization: Bearer"）。
    /// </summary>
    internal static string NormalizeAuthStyle(string? style)
    {
        var value = (style ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("x-api-key", StringComparison.Ordinal) || value.Contains("xapikey", StringComparison.Ordinal)
            || value.Contains("api-key header", StringComparison.Ordinal))
            return "x-api-key";
        if (value.Contains("api_key", StringComparison.Ordinal) || value.Contains("query", StringComparison.Ordinal))
            return "query";
        return "bearer";
    }

    /// <summary>按鉴权方式把密钥带上（返工 R4）；query 方式在拼 URL 时已附加，这里只处理头。</summary>
    internal static void ApplyAuth(HttpRequestMessage message, string apiKey, string authStyle)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        switch (authStyle)
        {
            case "x-api-key":
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                break;
            case "query":
                break;   // 已经拼在 URL 上
            default:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }
    }

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}

/// <summary>
/// 按文件头判断图片类型。接口返回的不一定是 PNG——真实站点实测返回的是 JPEG，
/// 这时候存成 <c>.png</c> 就是扩展名说谎，会让缩略图、外部打开、后续转码判错格式。
/// </summary>
public static class ImageFormatSniffer
{
    public static string ExtensionOf(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 12) return ".png";
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return ".bmp";
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        return ".png";
    }
}

/// <summary>
/// 通过桌面共享执行服务提交 ComfyUI 出图任务，等待 Job 终态后返回本地资产路径。
/// 复用 Host 的 ComfyUiExecutor、ComfyUiProvider 与外部任务轮询，不再单独实现一套出图逻辑。
/// </summary>
public sealed class ComfyUiImageProvider : IImageProvider
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(10);

    private readonly SingleMachineExecutionService execution;
    private readonly AiProviderConfig config;
    private readonly SessionContext session;

    public ComfyUiImageProvider(SingleMachineExecutionService execution, AiProviderConfig config, SessionContext session)
    {
        this.execution = execution;
        this.config = config;
        this.session = session;
    }

    public bool IsConfigured => config.IsComfyUiConfigured;
    public string Name => "ComfyUI";

    /// <summary>上限与提交方式由当前工作流模板声明，见 ComfyUiSubmissionProfiles。</summary>
    public ReferenceCapacity ReferenceCapacity => profile.ReferenceCapacity;

    private readonly SubmissionProfile profile = ComfyUiSubmissionProfiles.SingleBaseImage;

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) return Failed("图像提示词为空，无法生成。");
        var inputs = new Dictionary<string, JsonElement>
        {
            ["prompt"] = JsonSerializer.SerializeToElement(request.Prompt),
            ["negativePrompt"] = JsonSerializer.SerializeToElement(request.NegativePrompt ?? string.Empty),
            ["width"] = JsonSerializer.SerializeToElement(Math.Clamp(request.Width, 64, 2048)),
            ["height"] = JsonSerializer.SerializeToElement(Math.Clamp(request.Height, 64, 2048)),
            ["checkpoint"] = JsonSerializer.SerializeToElement(config.ComfyUiCheckpoint)
        };
        if (request.Steps is { } steps) inputs["steps"] = JsonSerializer.SerializeToElement(steps);
        if (request.Cfg is { } cfg) inputs["cfg"] = JsonSerializer.SerializeToElement(cfg);
        if (request.Seed is { } seed) inputs["seed"] = JsonSerializer.SerializeToElement(seed);
        // 参考图全部原样交给执行方：由它负责上传到 ComfyUI，工作流决定实际用几张。
        var references = request.ReferenceImages
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();
        var useReference = references.Count > 0;
        if (useReference)
        {
            inputs["referenceImages"] = JsonSerializer.SerializeToElement(references);
            inputs["referenceMode"] = JsonSerializer.SerializeToElement(profile.Id);
            inputs["denoise"] = JsonSerializer.SerializeToElement(request.Denoise ?? 0.6);
        }
        var invocation = new Invocation
        {
            Tool = useReference ? "image-to-image" : "text-to-image",
            Capability = useReference ? Capability.ImageToImage : Capability.TextToImage,
            Channel = "comfyui",
            Inputs = inputs
        };

        var completion = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = Guid.Empty;
        void OnUpdated(ExecutionResult result)
        {
            if (jobId == Guid.Empty || result.JobId != jobId) return;
            if (result.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
                completion.TrySetResult(result);
        }

        execution.Updated += OnUpdated;
        try
        {
            var started = await execution.StartAsync(session, invocation, $"node-image-{invocation.InvocationId:N}", cancellationToken);
            jobId = started.JobId;
            if (started.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
                completion.TrySetResult(started);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CompletionTimeout);
            await using var registration = timeout.Token.Register(() =>
            {
                completion.TrySetException(new TimeoutException("等待 ComfyUI 任务完成超时。"));
                if (jobId != Guid.Empty) execution.Cancel(session, jobId);
            }).ConfigureAwait(false);

            var result = await completion.Task.ConfigureAwait(false);
            return MapResult(result, references.Count);
        }
        catch (OperationCanceledException) { return Failed("ComfyUI 任务已取消。"); }
        catch (TimeoutException error) { return Failed(error.Message); }
        catch (ProtocolViolationException error) { return Failed($"提交 ComfyUI 任务失败：{error.Message}"); }
        catch (InvalidOperationException error) { return Failed($"ComfyUI 任务失败：{error.Message}"); }
        finally { execution.Updated -= OnUpdated; }
    }

    private ImageGenerationResult MapResult(ExecutionResult result, int referenceCount)
    {
        if (result.State != JobState.Succeeded)
            return Failed(result.ErrorMessage ?? $"ComfyUI 任务状态为 {result.State}。");

        foreach (var asset in result.Outputs)
        {
            const string prefix = "asset://";
            if (!asset.Ref.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var path = Path.Combine(AssetStore.Directory, asset.Ref[prefix.Length..]);
            if (File.Exists(path))
                return new ImageGenerationResult
                {
                    Status = ImageGenerationStatus.Succeeded,
                    FilePath = path,
                    Provider = Name,
                    Model = config.ComfyUiCheckpoint,
                    ReferenceNote = referenceCount > 1
                        ? $"当前 img2img 工作流只支持单张底图，已使用第 1 张，忽略其余 {referenceCount - 1} 张。" +
                          "要真正合成多角色，需要换成支持多参考的工作流模板（例如 IPAdapter）。"
                        : string.Empty
                };
        }
        return Failed("ComfyUI 任务完成但没有返回可用图片。");
    }

    private ImageGenerationResult Failed(string error) => new()
    {
        Status = ImageGenerationStatus.Failed,
        Provider = Name,
        Model = config.ComfyUiCheckpoint,
        Error = error
    };
}
