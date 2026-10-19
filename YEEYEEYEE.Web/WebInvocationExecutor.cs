using System.Net;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

internal sealed class WebInvocationExecutor : IInvocationExecutor
{
    private const string VideoTool = "web.text-to-video";
    /// <summary>
    /// 图片 API 的同一条上游一次只跑一个请求。实测这家接口按密钥串行：候选批次并发提交时，
    /// 后一张连接会一直挂着直到客户端 5 分钟超时，整批只剩一张成功。上游本来就串行，
    /// 在这里排队不会让总耗时更差，却能让第二张真正开始并完成。
    /// </summary>
    private static readonly SemaphoreSlim ImageApiGate = new(1, 1);
    private readonly IInvocationExecutor imageExecutor;
    private readonly IVideoProvider videoProvider;
    private readonly ComfyUiExecutor comfyUiExecutor;
    private readonly bool comfyUiVideoBackend;
    private readonly ComfyUiConversion workflowConversion;
    private readonly IConfiguration configuration;

    public WebInvocationExecutor(
        IInvocationExecutor imageExecutor,
        IVideoProvider videoProvider,
        ComfyUiExecutor comfyUiExecutor,
        bool comfyUiVideoBackend, ComfyUiConversion workflowConversion, IConfiguration configuration)
    {
        this.imageExecutor = imageExecutor;
        this.videoProvider = videoProvider;
        this.comfyUiExecutor = comfyUiExecutor;
        this.comfyUiVideoBackend = comfyUiVideoBackend;
        this.workflowConversion = workflowConversion;
        this.configuration = configuration;
    }

    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        WebGenerationReferences.ValidateEvidence(invocation.Inputs);
        if (WebWorkflowCatalog.IsWorkflow(invocation.Tool))
        {
            if (!session.ServerClaims.Contains("skill.invoke") || configuration["WebWorkflowApprovalMode"] != "preapproved-local-workflows")
                throw new InvalidOperationException("工作流未获服务端预授权");
            var entry = WebWorkflowCatalog.Load(ComfyUiConversion.BaseUrl(configuration)).FirstOrDefault(e => e.Id == invocation.Tool)
                ?? throw new InvalidOperationException("工作流已停用、删除或输入声明不可用");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Payload)));
            if (!invocation.Inputs.TryGetValue("workflowRevision", out var revision) || revision.GetString() != hash)
                throw new InvalidOperationException("工作流正文已变化，请重新调用");
            using var http = workflowConversion.Client(ComfyUiConversion.Target(entry.Site.BaseUrl));
            var output = await new ComfyUiExecutor(http, call => WebWorkflowCatalog.Bind(entry, call))
                .ExecuteAsync(session, invocation, job, cancellationToken).ConfigureAwait(false);
            return output with { ExternalTaskId = WebComfyUiTasks.Bind(entry.Site.BaseUrl, output.ExternalTaskId!) };
        }
        if (invocation.Tool == WebImageGeneration.SkillId)
        {
            if (!session.ServerClaims.Contains("skill.invoke")) throw new InvalidOperationException("缺少 skill.invoke 权限");
            var config = WebSettingsApi.LoadModels();
            if (!WebImageGeneration.IsConfigured(config)) throw new InvalidOperationException("Web 图片 API 未配置");
            var storedImageKey = config.ImageApiKey;
            config.ImageApiKey = SecretProtector.Unprotect(storedImageKey)
                ?? throw new InvalidOperationException("Web 图片 API 密钥无法解密，请在原账户和设备上检查配置；未提交生成请求");
            var size = WebImageGeneration.Size(ReadString(invocation.Inputs, "aspectRatio"))
                ?? throw new InvalidOperationException("请先选择图片比例");
            var references = invocation.Inputs.TryGetValue("referenceImages", out var images)
                ? images.Deserialize<string[]>() ?? [] : Array.Empty<string>();
            config.ImageMaxReferenceImages = WebImageGeneration.ReferenceCapacity(config);
            if (references.Length > config.ImageMaxReferenceImages)
                throw new InvalidOperationException("当前图片模型不支持所需参考图，请选择支持参考图的模型");
            await ImageApiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 串行闸门让一张一张来；单张本身仍可能很慢（实测 235s，偶尔超过 5 分钟）。
                // 5 分钟会把「慢但最终成功」的请求误判成失败，整批又只剩一张，所以默认放宽到 10 分钟。
                using var imageHttp = CreateMediaHttp(config.ImageProxyUrl,
                    configuration.GetValue("ImageHttpTimeout", TimeSpan.FromMinutes(10)));
                var image = await new OpenAiCompatibleImageProvider(config, imageHttp).GenerateAsync(new ImageGenerationRequest
                {
                    Prompt = ReadString(invocation.Inputs, "prompt"), Width = size.Width, Height = size.Height,
                    // 反向提示词由 provider 拼进 prompt；这是声明里放开 negativePrompt 的落地处，
                    // 不传下去就等于声明了却没人用（静默丢参数）。
                    NegativePrompt = ReadString(invocation.Inputs, "negativePrompt"),
                    ReferenceImages = references
                }, cancellationToken).ConfigureAwait(false);
                if (image.Status != ImageGenerationStatus.Succeeded)
                {
                    var error = string.IsNullOrWhiteSpace(image.Error) ? "图片 API 未返回可用产物" : image.Error;
                    foreach (var secret in new[] { storedImageKey, config.ImageApiKey, config.ApiKey })
                        if (!string.IsNullOrEmpty(secret)) error = error.Replace(secret, "[已隐藏密钥]", StringComparison.Ordinal);
                    throw new InvalidOperationException(error + $" Web显式代理={DiagnosticProxy(config.ImageProxyUrl)}");
                }
                if (!File.Exists(image.FilePath))
                    throw new InvalidOperationException("图片 API 声称成功，但输出文件不存在");
                return new ExecutionOutput { Outputs = [new AssetRef { Role = "image", Ref = AssetStore.ToReference(image.FilePath) }] };
            }
            finally
            {
                ImageApiGate.Release();
            }
        }
        if (!string.Equals(invocation.Tool, VideoTool, StringComparison.Ordinal))
            return await imageExecutor.ExecuteAsync(session, invocation, job, cancellationToken).ConfigureAwait(false);

        if (comfyUiVideoBackend)
        {
            if (invocation.Inputs.TryGetValue("referenceImages", out var comfyReferences) && comfyReferences.GetArrayLength() > 0)
                throw new InvalidDataException("当前 ComfyUI 视频模板不支持首帧参考，禁止忽略参考生成");
            return await comfyUiExecutor.ExecuteAsync(session, invocation, job, cancellationToken).ConfigureAwait(false);
        }

        var videoConfig = WebSettingsApi.LoadModels();
        var storedVideoKey = videoConfig.VideoApiKey;
        var useWebVideo = HasWebVideoSettings(videoConfig);
        if (useWebVideo)
        {
            if (!IsWebVideoConfigured(videoConfig)) throw new InvalidOperationException("Web 视频 API 未配置完整");
            videoConfig.VideoApiKey = SecretProtector.Unprotect(storedVideoKey)
                ?? throw new InvalidOperationException("Web 视频 API 密钥无法解密，请在原账户和设备上检查配置；未提交生成请求");
        }
        else
        {
            videoConfig.VideoEndpoint = LegacyConfig.Text(configuration, "VideoEndpoint") ?? string.Empty;
            videoConfig.VideoModel = LegacyConfig.Text(configuration, "VideoModel") ?? string.Empty;
            videoConfig.VideoApiKey = LegacyConfig.Text(configuration, "VideoApiKey") ?? string.Empty;
        }
        using var videoHttp = CreateMediaHttp(videoConfig.VideoProxyUrl,
            configuration.GetValue("VideoHttpTimeout", TimeSpan.FromMinutes(5)));
        var provider = useWebVideo || !string.IsNullOrWhiteSpace(videoConfig.VideoProxyUrl)
            ? new HttpVideoProvider(videoConfig, videoHttp) : videoProvider;
        var result = await provider.GenerateAsync(new VideoGenerationRequest
        {
            Prompt = ReadString(invocation.Inputs, "prompt"),
            // 反向提示词此前被这条路吞掉了（声明收下、请求里没有）；ComfyUI 视频工作流是认它的。
            NegativePrompt = ReadString(invocation.Inputs, "negativePrompt"),
            Model = ReadString(invocation.Inputs, "model"),
            Seconds = ReadInt(invocation.Inputs, "seconds"),
            Width = ReadInt(invocation.Inputs, "width"),
            Height = ReadInt(invocation.Inputs, "height"),
            ReferenceImages = invocation.Inputs.TryGetValue("referenceImages", out var videoImages)
                ? videoImages.Deserialize<string[]>() ?? [] : Array.Empty<string>()
        }, cancellationToken).ConfigureAwait(false);

        if (result.Status != VideoGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
        {
            var error = string.IsNullOrWhiteSpace(result.Error) ? "视频 provider 未返回成功结果" : result.Error;
            foreach (var secret in new[] { storedVideoKey, videoConfig.VideoApiKey, videoConfig.ApiKey })
                if (!string.IsNullOrEmpty(secret)) error = error.Replace(secret, "[已隐藏密钥]", StringComparison.Ordinal);
            throw new InvalidOperationException(error);
        }
        if (!File.Exists(result.FilePath))
            throw new InvalidOperationException("视频 provider 声称成功，但输出文件不存在");
        if (!IsVideoFile(result.FilePath))
            throw new InvalidOperationException("视频 provider 返回的产物不是受支持的视频文件，已拒绝作为成功结果");

        return new ExecutionOutput
        {
            // 接口站那条任务号一并带回任务记录：视频这条路超时是常态，落下来才能续查/取回。
            ExternalTaskId = result.ExternalTaskId,
            Outputs = [new AssetRef { Role = "video", Ref = AssetStore.ToReference(result.FilePath) }]
        };
    }

    internal static bool HasWebVideoSettings(AiProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.VideoEndpoint) || !string.IsNullOrWhiteSpace(config.VideoModel) || !string.IsNullOrWhiteSpace(config.VideoApiKey);

    internal static bool IsWebVideoConfigured(AiProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.VideoEndpoint) && !string.IsNullOrWhiteSpace(config.VideoModel);

    /// <summary>
    /// 出视频这条**链路配好了吗**。与「这个账号准不准跑」无关——那是审批模式（见技能授权那边）。
    ///
    /// 为什么要单独有它：节点右键菜单里的「出视频」能不能点，是共享的协助计划按这个答案算出来的
    /// （桌面端传的就是 VideoProviderFactory 的 IsConfigured）。网页端不回答的话，那份计划永远
    /// 认为「还没有可用的出视频链路」，菜单上就会挂着一句与事实相反的说明。
    /// </summary>
    internal static bool VideoLinkReady(IConfiguration configuration)
    {
        if (string.Equals(configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(configuration["ComfyUI:BaseUrl"]);
        var config = WebSettingsApi.LoadModels();
        if (HasWebVideoSettings(config)) return IsWebVideoConfigured(config);
        return !string.IsNullOrWhiteSpace(LegacyConfig.Text(configuration, "VideoModel"))
            && !string.IsNullOrWhiteSpace(LegacyConfig.Text(configuration, "VideoEndpoint"));
    }

    internal static string DiagnosticProxy(string proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return "未配置（使用系统默认）";
        var uri = new Uri(proxyUrl.Trim());
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    internal static HttpClient CreateMediaHttp(string proxyUrl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (!WebSettingsApi.ValidProxyUrl(proxyUrl))
            throw new InvalidOperationException("媒体 API 代理地址无效，请检查网络代理设置");
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl.Trim());
            handler.UseProxy = true;
        }
        return new HttpClient(handler)
        {
            Timeout = timeout,
            // 显式用 HTTP/1.1：部分 OpenAI 兼容网关在 ALPN 协商到 h2 后对 POST /images/generations
            // 不返回响应（请求会一直挂到超时）；同一地址用 1.1 正常。这里只影响媒体 API，
            // 协议降级由 RequestVersionOrLower 保证，服务器不支持 1.1 时仍会回退。
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> inputs, string name) =>
        inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> inputs, string name) =>
        inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool IsVideoFile(string path) =>
        string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".webm", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".mov", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase);
}
