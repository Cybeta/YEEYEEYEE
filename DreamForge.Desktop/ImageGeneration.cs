using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DreamForge.Core;
using DreamForge.Host;

namespace DreamForge.Desktop;

public enum ImageGenerationStatus { NotConfigured, Succeeded, Failed }

public sealed class ImageGenerationRequest
{
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;
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
    /// 优先使用本地 ComfyUI 执行链路（需要传入共享执行服务），其次使用 OpenAI 兼容图像接口。
    /// </summary>
    public static IImageProvider Create(SingleMachineExecutionService? execution = null)
    {
        var config = AiProviderSettings.Load();
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
        var url = $"{config.EffectiveImageEndpoint.TrimEnd('/')}/images/{(useReference ? "edits" : "generations")}";
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

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
                form.Add(new StringContent(config.ImageModel), "model");
                form.Add(new StringContent(ComposePrompt(request.Prompt, request.NegativePrompt)), "prompt");
                form.Add(new StringContent(request.SizeText), "size");
                form.Add(new StringContent("1"), "n");
                message.Content = form;
            }
            else
            {
                var payload = new
                {
                    model = config.ImageModel,
                    prompt = ComposePrompt(request.Prompt, request.NegativePrompt),
                    size = request.SizeText,
                    n = 1
                };
                message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Failed($"图像接口返回 {(int)response.StatusCode}：{Trim(body, 300)}");

            var bytes = await ReadImageBytesAsync(body, cancellationToken);
            if (bytes is null || bytes.Length == 0)
                return Failed("图像接口没有返回可用的图片数据。");

            var path = SaveAssets(bytes);
            return new ImageGenerationResult
            {
                Status = ImageGenerationStatus.Succeeded,
                FilePath = path,
                Provider = Name,
                Model = config.ImageModel,
                ReferenceNote = references.Count > 1
                    ? $"已按 image[] 提交 {references.Count} 张参考图；能否全部生效取决于所用模型（多数模型只使用第一张）。"
                    : string.Empty
            };
        }
        catch (HttpRequestException error) { return Failed($"图像接口请求失败：{error.Message}"); }
        catch (TaskCanceledException) { return Failed("图像接口请求超时。"); }
        catch (IOException error) { return Failed($"保存图片失败：{error.Message}"); }
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
        var path = Path.Combine(AssetStore.EnsureDirectory(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ImageGenerationResult Failed(string error) => new()
    {
        Status = ImageGenerationStatus.Failed,
        Provider = Name,
        Model = config.ImageModel,
        Error = error
    };

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
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
