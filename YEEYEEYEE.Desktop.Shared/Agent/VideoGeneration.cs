using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;

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

    /// <summary>
    /// 源视频的本机绝对路径列表：影视二创、对口型、视频修复、补帧超分那一支工作流吃的是
    /// **一段片子**，不是一张图——首帧那条路对它们没用（它们真正的入口是 `VHS_LoadVideo.video` 这类）。
    /// 交给 ComfyUI 之前会先传到它的 input 目录，再按文件名写进那些入口。
    /// </summary>
    public IReadOnlyList<string> SourceVideos { get; init; } = Array.Empty<string>();

    /// <summary>源音频的本机绝对路径列表（对口型那一支还要一段音）。</summary>
    public IReadOnlyList<string> SourceAudios { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 选定的 ComfyUI 出视频工作流：站点 id + 正文文件名 + 工作流键，三个一起才认得出来源。
    ///
    /// 与出图侧（<see cref="ImageGenerationRequest.WorkflowPayloadFile"/>）同一套口径：
    /// 带的是**正文文件名**而不是工作流名——正文是导出那一刻的快照，靠名字回查会在重名或改名时指错文件。
    /// 三个都为空表示这次走接口站的视频池子（提交 → 轮询 → 下载）。
    /// </summary>
    public string WorkflowSiteId { get; init; } = string.Empty;
    public string WorkflowPayloadFile { get; init; } = string.Empty;
    public string WorkflowKey { get; init; } = string.Empty;

    public bool UsesWorkflow => WorkflowPayloadFile.Length > 0;

    /// <summary>
    /// 想要的比例，形如 <c>9:16</c>；空表示「跟随首帧」（图生视频时这是最稳的一种）。
    /// 只有那份工作流认得出比例/画幅入口时才写得进去，写不进去会如实说明。
    /// </summary>
    public string AspectRatio { get; init; } = string.Empty;

    /// <summary>
    /// 目标像素（百万像素），例如 1.0；0 表示不指定（按 1.0 算）。
    /// ComfyUI 里没有统一的「百万像素」字段，所以它只用来**换算出具体的宽高**，
    /// 再由工作流认哪个槽位决定写到哪儿。
    /// </summary>
    public double Megapixels { get; init; }

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

    /// <summary>
    /// 这次实际写进去了什么、哪些没写进去、为什么。**它是给人核对用的**，不是日志：
    /// 「你要的 15 秒 / 9:16 到底生效了没有」只有这句能回答，而这件事不说清楚，
    /// 用户会把「模型没按我要的出」当成模型的毛病，而不是「这份工作流改不了这一项」。
    /// </summary>
    public string Note { get; init; } = string.Empty;
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
/// 选了一份 ComfyUI 工作流就走 <see cref="ComfyUiVideoProvider"/>，
/// 否则配齐了接口站就走 <see cref="HttpVideoProvider"/>（异步提交 → 轮询 → 下载），
/// 都没配齐则如实说还差什么。
///
/// 为什么这里要**显式把工作流传进来**而不像出图那样只看配置：出视频有两种来源，
/// 而「接口站的视频池子」与「ComfyUI 的一份视频工作流」是两条完全不同的链
/// （一次 HTTP 调用 vs 一张节点图），配置里分不出用户这一次选了哪一种。
/// </summary>
public static class VideoProviderFactory
{
    /// <summary>自动化回归用的执行方注入点（生产代码从不设置）。</summary>
    internal static IVideoProvider? Override { get; set; }

    public static IVideoProvider Create(AiProviderConfig? config = null, SiteWorkflowChoice? workflow = null)
    {
        if (Override is { } injected) return injected;
        var effective = config ?? AiProviderSettings.Load();

        if (workflow is { } chosen)
        {
            var execution = ImageProviderFactory.SharedExecutionHost(effective);
            if (execution is not null)
                return new ComfyUiVideoProvider(execution, effective, DesktopSession(), chosen);
            return new UnconfiguredVideoProvider(
                $"选了 ComfyUI 的「{chosen.Workflow.Title}」，但 ComfyUI 那条链没配好："
                + "到「设置 → 生图与生视频 → ComfyUI」里填上地址与 checkpoint 再来。");
        }

        return effective.IsVideoConfigured
            ? new HttpVideoProvider(effective)
            : new UnconfiguredVideoProvider(
                "还没有可用的出视频链路：请在「设置 → 生图与生视频 → 视频接口」里填上地址与模型"
                + (string.IsNullOrWhiteSpace(effective.VideoModel) ? "（模型名现在是空的）" : string.Empty)
                + "，或者在出视频时选一份 ComfyUI 的出视频工作流。填好之后，"
                + "分镜节点右键的「出这一镜的视频」就能真的跑。");
    }

    /// <summary>桌面端的会话上下文：与出图那条链用的是同一份口径，别各写一套。</summary>
    private static SessionContext DesktopSession() => new()
    {
        SessionId = Guid.NewGuid(),
        UserId = ImageProviderFactory.DesktopUserId,
        ClientType = ClientType.Desktop,
        Role = MemberRole.Member,
        ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"])
    };
}

/// <summary>
/// 通过 ComfyUI 的一份**出视频工作流**出视频：复用桌面共享执行服务与 Host 的
/// ComfyUiExecutor / ComfyUiProvider（提交 prompt_id → 轮询 history → 下载产物），
/// 与出图那条路完全同源，不再单独实现一套。
///
/// 为什么必须带一份工作流：ComfyUI 本身不认识「视频」这件事——出图还是出视频、
/// 用 SVD 还是 Wan 还是 LTX，全在那张节点图里。我们编不出这份图（每个人装的节点包都不一样），
/// 所以这里**只绑认出来的那几个槽位**：提示词、负面词、画幅、种子，以及首帧（图生视频那份的 LoadImage）。
///
/// **时长刻意不进参数**：15 秒对应多少帧、多少 fps，是那份工作流的私有约定
/// （SVD 的 video_frames、Wan 的 length、LTX 的 frame_rate 语义各不相同）。硬塞一个数字进去，
/// 出来的可能是 15 帧而不是 15 秒——所以宁可在挑选那一刻就说清「时长由工作流自己决定」。
/// </summary>
public sealed class ComfyUiVideoProvider : IVideoProvider
{
    /// <summary>
    /// 比出图那条宽得多：视频工作流在消费级卡上跑一次常常十几分钟（Wan / LTX 更久）。
    /// 超时会真的中断任务（<c>execution.Cancel</c>），不是丢下不管。
    /// </summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(30);

    private readonly SingleMachineExecutionService execution;
    private readonly AiProviderConfig config;
    private readonly SessionContext session;
    private readonly SiteWorkflowChoice choice;
    private readonly ComfyUiWorkflowSlots? slots;

    public ComfyUiVideoProvider(
        SingleMachineExecutionService execution,
        AiProviderConfig config,
        SessionContext session,
        SiteWorkflowChoice choice)
    {
        this.execution = execution;
        this.config = config;
        this.session = session;
        this.choice = choice;
        slots = ReadSlots(choice);
    }

    public bool IsConfigured => config.IsComfyUiConfigured;
    public string Name => "ComfyUI";

    /// <summary>
    /// 这份工作流吃不吃首帧：true / false / null（正文读不到，判断不了）。
    /// 单列出来是给界面用的——「首帧会不会被用上」必须在点下去之前就写在窗口上，
    /// 不能等出完才发现画面跟这一镜无关。
    /// </summary>
    public bool? TakesFirstFrame => slots?.CanTakeImage;

    /// <summary>
    /// 这条链一次能用几张参考图：**由那份工作流自己说了算**（有没有 LoadImage 底图入口）。
    /// 与出图侧同理，认不出来就报 0 并说清，而不是假装能收。
    /// </summary>
    public ReferenceCapacity ReferenceCapacity => slots is null
        ? new ReferenceCapacity(0, $"读不到工作流「{choice.Workflow.Title}」的正文，无从判断它能收几张参考图。")
        : slots.CanTakeImage
            ? new ReferenceCapacity(1, "图生视频这份工作流收一张底图（= 这一镜的首帧）。")
            : new ReferenceCapacity(0, slots.CanTakeVideo
                // 「没有底图入口」有两种截然不同的原因：只能文生视频，或者它吃的是**一段片子**。
                // 混成一句话会让人以为这类工作流驱动不了，其实它要的是源视频（见 IsSourceDriven）。
                ? "这份工作流没有底图入口：它吃的是**一段片子**（源视频入口），首帧不会生效。"
                : "这份工作流没有底图入口，只能文生视频——首帧不会生效。");

    private static ComfyUiWorkflowSlots? ReadSlots(SiteWorkflowChoice choice)
    {
        try
        {
            var payload = SiteCatalog.LoadPayload(choice.Site.Id, choice.Workflow.PayloadFile);
            return string.IsNullOrWhiteSpace(payload) ? null : ComfyUiWorkflowBinder.Detect(payload);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            // 读不懂正文不等于「没有底图入口」——那是两种情况，所以返回 null，由 ReferenceCapacity 分别说明。
            return null;
        }
    }

    public async Task<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowPayloadFile))
            return Failed("这一路要用一份选定的 ComfyUI 工作流出视频，而这次没带上工作流正文。");

        string template;
        try
        {
            template = SiteCatalog.LoadPayload(request.WorkflowSiteId, request.WorkflowPayloadFile) ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException)
        {
            return Failed($"这份工作流的正文读不到（站点 {request.WorkflowSiteId} 的 {request.WorkflowPayloadFile}）："
                + $"{error.Message}。可能站点被删了或正文被清掉了，到「设置 → 技能管理 → 站点与池子」重新导入一次即可。");
        }

        if (template.Length == 0)
            return Failed($"这份工作流的正文读不到（站点 {request.WorkflowSiteId} 的 {request.WorkflowPayloadFile}）："
                + "可能站点被删了或正文被清掉了，到「设置 → 技能管理 → 站点与池子」重新导入一次即可。");

        ComfyUiWorkflowSlots detected;
        try
        {
            // 站点那张「见过的选项值」表一起带上：比例这一项要写出服务端认的值就得靠它
            //（见 SiteProfile.OptionValues；只照当前值的写法把数字换掉会造出它不认的字符串）。
            detected = ComfyUiWorkflowBinder.Detect(template, OptionValuesOf(request.WorkflowSiteId));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Failed($"这份工作流的形状读不懂（{error.GetType().Name}）：{error.Message}");
        }

        // 认不出收提示词的位置要分两种情况，不能一锅端：
        // ① 只吃首帧的那种（SVD / 动作迁移 / 人物替换）**是正当用法**——它按底图动起来，本来就不收文字。
        //    这时不写提示词，但要把「你写的提示词没进工作流」说出来（不说的话，用户会以为画面是自己那句话决定的）。
        // ② 既没有提示词入口、也没有底图入口，才是真的驱动不了。
        if (!detected.CanTextToImage && !detected.IsFrameDriven && !detected.IsSourceDriven)
            return Failed("这份工作流既收不到提示词、也没有底图或源视频入口，所以没提交："
                + string.Join("；", detected.Notes));

        // 提示词：只在**这份工作流确实收文字**时才必须要。视频修复 / 补帧超分 / 去水印那一支
        // 本来就不吃提示词（它按你给的那段片子干活），要求它等于把一整类正当用法挡在门外。
        if (detected.CanTextToImage && string.IsNullOrWhiteSpace(request.Prompt))
            return Failed("视频提示词为空，无法生成。");

        var references = request.ReferenceImages
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();
        var useReference = references.Count > 0 && detected.CanTakeImage;
        var promptBound = detected.CanTextToImage;

        // 源视频 / 源音频：影视二创、对口型、视频修复那一支吃的是**一段片子**（对口型还要一段音）。
        // 与参考图同一条规矩：本机找不到的**不静默丢掉**，直接说清（真正上传与写槽位在 Host 那一侧做）。
        var sourceVideos = request.SourceVideos
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        var sourceAudios = request.SourceAudios
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        var missingSources = sourceVideos.Concat(sourceAudios).Where(path => !File.Exists(path)).ToList();
        sourceVideos = sourceVideos.Where(File.Exists).ToList();
        sourceAudios = sourceAudios.Where(File.Exists).ToList();

        // ── 时长：把「要几秒」按那份工作流自己的帧率换算成帧数，并贴到它自己的帧数家族上 ──
        // 换算不出来（它没有帧数入口、或找不到帧率）时**什么都不写**，并在说明里讲清是哪种情况。
        var notes = new List<string>();
        foreach (var missing in missingSources)
            notes.Add($"你给的素材在本机找不到了，它不会被用上：{missing}");

        // ── 源视频 / 源音频 ──
        // 这一支工作流吃的是**一段片子**：给了要说清写到哪个节点，没给要说清它会拿自己的示例片子跑——
        // 「我给了素材、它却用了别人的片段」这件事从结果上完全看不出来（与参考图那条规矩同理）。
        if (detected.CanTakeVideo)
        {
            notes.Add(sourceVideos.Count == 0
                ? $"这份工作流要吃一段**源视频**（节点 {string.Join("、", detected.VideoNodeIds)}），这次没给："
                  + "它会拿它自己示例里的那段片子跑，出来的内容与你的素材无关。"
                : $"源视频已写入：节点 {string.Join("、", detected.VideoNodeIds.Take(sourceVideos.Count))}。");
        }
        else if (sourceVideos.Count > 0)
        {
            notes.Add("这次给了源视频，但这份工作流没有源视频入口（LoadVideo 这类），它用不上："
                + "要处理一段片子请换「影视二创 / 对口型 / 视频修复」那一支工作流。");
        }

        if (detected.CanTakeAudio && sourceAudios.Count > 0)
            notes.Add($"源音频已写入：节点 {string.Join("、", detected.AudioNodeIds.Take(sourceAudios.Count))}。");
        else if (!detected.CanTakeAudio && sourceAudios.Count > 0)
            notes.Add("这次给了源音频，但这份工作流没有音频入口（LoadAudio 这类），它用不上。");
        int? frames = null;
        double? secondsToWrite = null;
        if (request.Seconds > 0)
        {
            if (detected.CanSetLength)
            {
                if (VideoFrameMath.TryFrames(request.Seconds, detected.FrameRateValue ?? 0, detected.LengthCurrent,
                        out var computed, out var frameNote))
                {
                    frames = computed;
                    notes.Add(frameNote);
                }
                else
                {
                    notes.Add(frameNote);
                }
            }
            else if (detected.CanSetSeconds)
            {
                // 帧数是算出来的那种：写**秒**，让那份工作流自己的表达式去折帧数与对齐。
                secondsToWrite = request.Seconds;
                notes.Add($"时长按**秒数**写（{request.Seconds} 秒）：" + detected.SecondsChain);
            }
            else
            {
                notes.Add("这份工作流没有帧数入口，所以这次没能改时长：它出多少就是多少"
                    + (detected.LengthCurrent is { } fixedFrames ? $"（当前设定 {fixedFrames} 帧）。" : "。"));
            }
        }

        // ── 画幅与比例 ──
        // 只给了目标像素而没给比例时**不写画幅**：那等于「按首帧的比例来」，
        // 而首帧的比例是工作流照着输入图自己算的，我们再去写一个数反而会和它对不上。
        var width = request.Width;
        var height = request.Height;
        var aspectRatio = request.AspectRatio;
        if (width <= 0 || height <= 0)
        {
            if (aspectRatio.Length > 0 || request.Megapixels > 0)
            {
                var shape = aspectRatio.Length > 0
                    ? VideoShape.Resolve(aspectRatio, request.Megapixels)
                    : (Width: 0, Height: 0,
                        Note: "只给了目标像素、没给比例：这种情况按**首帧的比例**走，画幅我们不动它"
                              + "（要指定比例就选一个，例如 9:16）。");
                if (shape.Width > 0)
                {
                    width = shape.Width;
                    height = shape.Height;
                    notes.Add(shape.Note);
                }
                else
                {
                    notes.Add(shape.Note);
                }
            }
        }

        var sizeApplied = width > 0 && height > 0 && detected.CanResize;
        if (width > 0 && height > 0 && !detected.CanResize)
            notes.Add($"这份工作流改不了画幅（尺寸来自它上游的节点或输入图），所以 {width}×{height} 没能写进去："
                + "它出多大就是多大。要指定比例，最稳的办法是把首帧按那个比例出好再喂进来。");
        else if (sizeApplied)
            notes.Add($"画幅已写入：{width}×{height}（节点 {detected.LatentNodeId}）。");

        if (aspectRatio.Length > 0 && detected.CanSetAspect && !sizeApplied)
        {
            // 说清**真正写进去的是哪个字符串**：这类控件的值是「一串固定选项」，
            // 写错一个字符（例如把 `9:16 (Portrait Widescreen)` 写成 `9:16 (Widescreen)`）就是一次 400 拒收，
            // 而两串人看着差不多，不说出来根本对不上账。
            var written = VideoShape.FormatAspect(detected.AspectCurrent, aspectRatio, detected.AspectOptions);
            if (written.Length == 0)
                notes.Add($"比例这一项没能写进去：它的值「{detected.AspectCurrent}」我们造不出对应的写法"
                    + "（要改请在那份工作流里改）。");
            else if (string.Equals(written, detected.AspectCurrent, StringComparison.Ordinal))
                notes.Add($"比例已经是 {aspectRatio}（它当前写的就是「{detected.AspectCurrent}」），这一项没动。");
            else if (detected.AspectOptions.Contains(written))
                notes.Add($"比例已写入：{detected.AspectCurrent} → {written}"
                    + "（这是这台服务器上同一个节点用过的写法，不是我们拼的）。");
            else
                notes.Add($"比例已写入：{detected.AspectCurrent} → {written}"
                    + "（照它自己的分隔符拼的：这个值不一定在它的选项清单里，提交可能被服务端拒）。");
        }
        else if (aspectRatio.Length > 0 && !detected.CanSetAspect && !sizeApplied)
            notes.Add("这份工作流既没有可写的画幅、也没有比例选项，比例没能写进去。");

        var inputs = new Dictionary<string, JsonElement>
        {
            ["prompt"] = JsonSerializer.SerializeToElement(promptBound ? request.Prompt : string.Empty),
            ["negativePrompt"] = JsonSerializer.SerializeToElement(request.NegativePrompt ?? string.Empty),
            ["workflowTemplate"] = JsonSerializer.SerializeToElement(template),
            ["workflowSlots"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(detected))
        };
        // 画幅只在明确给了值时才写：写 0 会让某些工作流按 0×0 出图，而「不说」是让它用自己的默认值。
        if (width > 0) inputs["width"] = JsonSerializer.SerializeToElement(width);
        if (height > 0) inputs["height"] = JsonSerializer.SerializeToElement(height);
        // 帧数是**已经换算并贴到它自己的家族上**的（见上面的 VideoFrameMath），这里原样交给 Host 写进那一个槽位。
        if (frames is { } frameCount) inputs["videoFrames"] = JsonSerializer.SerializeToElement(frameCount);
        // 另一种写法：给**秒数**，由那份工作流自己的表达式折帧数（见 ComfyUiWorkflowSlots.CanSetSeconds）。
        if (secondsToWrite is { } secondsValue) inputs["videoSeconds"] = JsonSerializer.SerializeToElement(secondsValue);
        if (aspectRatio.Length > 0) inputs["aspectRatio"] = JsonSerializer.SerializeToElement(aspectRatio);
        if (useReference)
        {
            inputs["referenceImages"] = JsonSerializer.SerializeToElement(references);
            inputs["referenceMode"] = JsonSerializer.SerializeToElement("img2vid");
        }
        // 源视频 / 源音频：Host 那一侧会先把它们传到 ComfyUI 的 input 目录，再按文件名写进对应入口。
        if (sourceVideos.Count > 0) inputs["referenceVideos"] = JsonSerializer.SerializeToElement(sourceVideos);
        if (sourceAudios.Count > 0) inputs["referenceAudios"] = JsonSerializer.SerializeToElement(sourceAudios);

        // 只吃素材、不收文字的那类工作流：不写提示词，但必须说出来——
        // 不说的话，用户会以为画面是自己那句话决定的，出了偏差只会怪模型。
        if (!promptBound && request.Prompt.Length > 0)
            notes.Add(detected.CanTakeVideo
                ? "这份工作流不收文字提示词（它是按你给的那段片子干活的，视频修复 / 补帧超分 / 去水印这一类）："
                  + "你写的提示词没有进工作流，出来的内容由源素材与它自己的参数决定。"
                : "这份工作流不收文字提示词（它是靠首帧动起来的，SVD / 动作迁移 / 人物替换这一类）："
                  + "你写的提示词没有进工作流，画面由首帧与它自己的运动参数决定。");

        // 「你想要的是什么」单独留一份：出完之后要和**量出来的真实结果**并排说出来，
        // 不然「15 秒」变成 5 秒这件事没人会注意到。
        var wanted = new List<string>();
        if (request.Seconds > 0) wanted.Add($"{request.Seconds} 秒");
        if (request.AspectRatio.Length > 0) wanted.Add(request.AspectRatio);
        if (request.Megapixels > 0) wanted.Add($"{request.Megapixels:0.##}MP");
        var wantedSummary = string.Join(" · ", wanted);

        var invocation = new Invocation
        {
            Tool = useReference ? "image-to-video" : "text-to-video",
            Capability = useReference ? Capability.ImageToVideo : Capability.TextToVideo,
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
            var started = await execution.StartAsync(
                session, invocation, $"node-video-{invocation.InvocationId:N}", cancellationToken);
            jobId = started.JobId;
            if (started.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
                completion.TrySetResult(started);
            else
                // 提交成功之后才说这句：说在前面，任务还没发出去就报「可能要等十几分钟」是空话。
                Status?.Invoke($"已提交到 ComfyUI（{Label}），它是异步任务，出好之前请别关窗口…");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CompletionTimeout);
            await using var registration = timeout.Token.Register(() =>
            {
                completion.TrySetException(new TimeoutException(
                    $"等 ComfyUI 出视频超时（超过 {CompletionTimeout.TotalMinutes:0} 分钟）。"));
                if (jobId != Guid.Empty) execution.Cancel(session, jobId);
            }).ConfigureAwait(false);

            var result = await completion.Task.ConfigureAwait(false);
            return MapResult(result, useReference, references.Count, detected, notes, wantedSummary);
        }
        catch (OperationCanceledException) { return Failed("ComfyUI 出视频任务已取消。"); }
        catch (TimeoutException error) { return Failed(error.Message); }
        // 限定名字空间：System.Net 里也有一个同名的 ProtocolViolationException，这里要的是 Core 那个。
        catch (YEEYEEYEE.Core.ProtocolViolationException error) { return Failed($"提交 ComfyUI 任务失败：{error.Message}"); }
        catch (InvalidOperationException error) { return Failed($"ComfyUI 任务失败：{error.Message}"); }
        finally { execution.Updated -= OnUpdated; }
    }

    /// <summary>
    /// 进度那几句往哪儿说。设置它的人（界面）负责显示；没设置就什么都不说——
    /// 出视频要等十几分钟，一句「已提交」是这条路上唯一能让人安心等下去的话。
    /// </summary>
    public Action<string>? Status { get; set; }

    /// <summary>报出去的「模型」是那份工作流的名字：报 checkpoint 会让人以为跑的是配置里那个底模。</summary>
    private string Label => choice.Workflow.Title.Length > 0
        ? choice.Workflow.Title
        : (choice.Workflow.Key.Length > 0 ? choice.Workflow.Key : "站点工作流");

    private VideoGenerationResult MapResult(
        ExecutionResult result,
        bool usedReference,
        int referenceCount,
        ComfyUiWorkflowSlots slots,
        List<string> notes,
        string wantedSummary)
    {
        if (result.State != JobState.Succeeded)
            return Failed(result.ErrorMessage ?? $"ComfyUI 任务状态为 {result.State}。");

        // 先分清两件完全不同的事，否则报出来的话会把人指向错的地方：
        // ① **这次根本没提交到 ComfyUI**——配置没配齐时执行链会退回内存执行器，任务在本地「成功」，
        //    给一个 local:// 的假引用；这时该去看 ComfyUI 配置；
        // ② 提交了、跑完了，但产物不在我们认的四个桶里；这时该去看那份工作流的末端节点。
        // 混着报就是「跑完了却没有产物」这种谁看了都不知道从哪儿下手的话。
        if (string.IsNullOrWhiteSpace(result.ExternalTaskId))
            return Failed("这次没有真的提交到 ComfyUI（任务在本地就结束了，外部任务号是空的）："
                + "到「设置 → 生图与生视频 → ComfyUI」检查一下地址与 checkpoint 填好了没有。");

        const string prefix = "asset://";
        var assets = result.Outputs
            .Where(asset => asset.Ref.StartsWith(prefix, StringComparison.Ordinal))
            .Select(asset => (Asset: asset, Path: Path.Combine(AssetStore.Directory, asset.Ref[prefix.Length..])))
            .Where(pair => File.Exists(pair.Path))
            .ToList();

        // 优先收「视频」那一路：一份工作流可能同时存了预览图与视频，收错就等于拿一张静图当视频。
        var picked = assets.FirstOrDefault(pair => pair.Asset.Role == "video").Path
                     ?? assets.FirstOrDefault(pair => pair.Asset.Role == "image").Path
                     ?? string.Empty;

        if (picked.Length == 0)
            return Failed("ComfyUI 跑完了，但history 里没有任何我们能收下的产物"
                + "（只认 images / gifs / videos / audio 四种）。可能是这份工作流的末端节点不是存文件的"
                + "（例如只输出到 Preview），也可能是它把文件存到了别处。");

        // 出完了就**把真实结果量出来**再说一遍：用户要的是 15 秒 / 9:16，到底出成了什么，
        // 只有量过才算数（帧率换算、模型自己的对齐要求都可能让实际值跟想要的不一样）。
        var film = Mp4Concatenator.Probe(picked);
        if (film.Readable)
            notes.Add($"实际出的是：{film.Describe()}。"
                + (wantedSummary.Length > 0 ? $"你要的是：{wantedSummary}。" : string.Empty));

        return new VideoGenerationResult
        {
            Status = VideoGenerationStatus.Succeeded,
            FilePath = picked,
            Provider = Name,
            Model = Label,
            ReferenceNote = ReferenceNote(usedReference, referenceCount, slots),
            Note = string.Join("\n", notes)
        };
    }

    /// <summary>
    /// 这个站点那张「全库用过的固定选项值」表（比例这一项靠它写出服务端认的值）。
    /// 读不到就当没有——那种情况下退回到「照当前值的写法造一个」，与以前一样，不会因此不干活。
    /// </summary>
    private static IReadOnlyDictionary<string, List<string>>? OptionValuesOf(string siteId)
    {
        if (siteId.Length == 0) return null;
        try
        {
            return SiteCatalog.Load().Sites.FirstOrDefault(item => item.Id == siteId)?.OptionValues;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _ = error;
            return null;
        }
    }

    /// <summary>参考图的实际用法说明。**用不了就要说出来**：静默丢掉首帧，用户会以为是模型没画好。</summary>
    private static string ReferenceNote(bool usedReference, int referenceCount, ComfyUiWorkflowSlots slots)
    {
        if (referenceCount == 0) return string.Empty;
        if (!slots.CanTakeImage)
            return $"这份工作流没有底图入口，所以首帧（以及另外 {referenceCount - 1} 张设定图）没有被使用"
                + (slots.CanTakeVideo
                    ? "——它吃的是**源视频**（一段片子），不吃参考图。"
                    : "——它只能文生视频。要按首帧出视频得换一份带 LoadImage 的工作流。");
        if (referenceCount > slots.ImageCapacity)
            return $"这份工作流有 {slots.ImageCapacity} 个底图入口，喂进去 {referenceCount} 张，只用了前 {slots.ImageCapacity} 张"
                + "（多出来的没有去处）。要带上设定图得换一份底图入口更多的工作流。";
        return string.Empty;
    }

    private VideoGenerationResult Failed(string error) => new()
    {
        Status = VideoGenerationStatus.Failed,
        Provider = Name,
        Model = Label,
        Error = error
    };
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
