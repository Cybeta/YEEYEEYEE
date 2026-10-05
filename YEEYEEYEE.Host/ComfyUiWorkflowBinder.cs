using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YEEYEEYEE.Host;

/// <summary>
/// 一格里的一处素材槽：哪个节点的哪个输入。用于「服务器声明为文件选择」的那类槽
/// （见 <see cref="ComfyUiWorkflowSlots.FileSlotImages"/>）——它们**各自有自己的输入名**，
/// 不能像 `LoadImage` 家族那样所有入口共用一个 `image`。
/// </summary>
public sealed record ComfyUiFileSlot(string NodeId, string Input);

/// <summary>
/// 「文件选择槽」是收哪一类素材。判据来自服务器的节点定义（导入时算出来，见 <c>SiteProfile.FileSlots</c>），
/// 值就是这三个字符串。
/// </summary>
public static class ComfyUiFileSlotKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
}

/// <summary>
/// 一份 ComfyUI 工作流里「参数该往哪儿放」的答案。
///
/// 为什么需要它：从网页导出的 API 格式工作流是**那一刻的快照**——提示词、负面词、尺寸、种子
/// 都已经烤死在各个节点的 inputs 里。直接把它提交上去，跑的还是导出时那个提示词，
/// 这条流水线写的分镜提示词一个字都进不去。
///
/// 所以要把「哪个节点的哪个输入收什么」找出来。找得出来的是多数（文生图、图生图的正路写法都在下面几条规则里），
/// 找不出来的必须**如实说找不到**，而不是硬塞一个进去——塞错了轻则白跑一张，重则把工作流改坏。
/// </summary>
public sealed class ComfyUiWorkflowSlots
{
    /// <summary>收正向提示词的节点与输入名（找不到时为空）。</summary>
    public string PositiveNodeId { get; set; } = string.Empty;
    public string PositiveInput { get; set; } = string.Empty;

    /// <summary>收负面提示词的节点与输入名（这份工作流没有负面词时为空）。</summary>
    public string NegativeNodeId { get; set; } = string.Empty;
    public string NegativeInput { get; set; } = string.Empty;

    /// <summary>决定画幅的节点（EmptyLatentImage 这类），要改 width / height。</summary>
    public string LatentNodeId { get; set; } = string.Empty;

    /// <summary>收种子的采样器节点；出多张时要逐张换种子。</summary>
    public List<string> SeedNodeIds { get; set; } = new();

    /// <summary>收底图的节点（LoadImage 这类），图生图时要把参考图的名字放进去。</summary>
    public string ImageNodeId { get; set; } = string.Empty;
    public string ImageInput { get; set; } = string.Empty;

    /// <summary>
    /// **全部**底图入口，按节点 id 稳定排序。有多个时按顺序各收一张参考图——
    /// 「角色 + 道具 + 场景」一起喂就走这一串。`ImageNodeId` 仍是第 1 个（单图那条路照旧）。
    /// </summary>
    public List<string> ImageNodeIds { get; set; } = new();

    /// <summary>
    /// 底图入口的**分组**：同一组 = 这些入口能通到同一个输出（保存类）节点。每组内按节点 id 排。
    ///
    /// 为什么需要分组：有一类工作流是**几条各自独立的管线并排放在一个文件里**
    /// （`B03编辑Qwen2511单双三图编辑V2` 就是：1图组走 `SaveImage(216)`、2图组走 `243`、3图组走 `277`，
    /// 整份里一个开关都没有）。对这种，「按节点 id 顺序平铺前 N 个入口」会把 3 张图塞进 1 图组和
    /// 2 图组的第一槽，而真正能收三张的 3 图组一张都拿不到。按组填就没有这个问题：
    /// **每组各取「角色 → 道具 → 场景」的前 k 张**（k = 这组有几个并列槽），组与组互不干扰。
    ///
    /// 只有一组时（绝大多数工作流）等于不分——<see cref="Bind"/> 那条平铺的路一字未动。
    /// </summary>
    public List<List<string>> ImageGroups { get; set; } = new();

    /// <summary>
    /// **服务器声明为「文件选择」的图片槽**，按声明顺序：每项 = 哪个节点的哪个输入收了这一张。
    ///
    /// 与前面几种入口的区别（这是第三种形状）：`LoadImage` 家族是「每个节点一格、输入同叫 `image`」；
    /// 文件清单式是「一个字段装下全部、换行分隔」；而这一种是**一个节点上并排九格、每格一个自己的输入名**
    /// （实测 `NanFengH3MultiReferenceGeneratorV10`：`图片1`…`图片9`，候选清单就是服务器 input 目录的
    /// 文件列表，与 `LoadImage.image` 同一种形状；作者那份只在 `图片1` 放了一张，其余八格是 `未选择`）。
    ///
    /// 判据来自**服务器的节点定义**（导入时算好、存在站点文件里，见 `SiteProfile.FileSlots`），
    /// **不按输入名猜**：`图片1` 这种中文序号只是那个包自己的习惯，同一种形状在别处可能叫别的。
    /// 空着的格子同样算入口——一个格子算不算入口由**节点自己的能力**决定，不由作者那一份用过没用过决定
    /// （`未选择` 是候选清单里的一个合法取值，往里写是改一个值，不是补一个缺的必填项）。
    ///
    /// 同一张表里**视频 / 音频**那两档并进 <see cref="VideoNodeIds"/> / <see cref="AudioNodeIds"/>
    /// （同一种形状、同一条路），所以那两份清单里既有按类名认下的、也有声明的。
    /// </summary>
    public List<ComfyUiFileSlot> FileSlotImages { get; set; } = new();

    /// <summary>
    /// **「文件清单式」**的底图入口：一个多行文本框里放多行文件名（节点 id → 输入名）。
    ///
    /// 为什么要单列：`MultiImageLoader.image_paths` 这种入口既不是 `LoadImage` 家族（类名里没有它），
    /// 也不是「候选清单」形态——它是 `STRING`，值里存的是**换行分隔的文件名**，所以按前两条规则都会
    /// 整类漏掉。实测 316 份里 4 份是这样（`H22`/`H23` 首尾帧、`H25` 单图、`H42` 多图参考），
    /// 它们会被误报成「用不了参考图」——而首尾帧那一类**只靠这个入口**。
    ///
    /// 一个字段就装得下全部参考图，所以写入时把名字按行拼起来；
    /// 行与文件名**一一对应**（首行是首帧、末行是末帧），给几张就写几行。
    /// </summary>
    public Dictionary<string, string> ImageListInputs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 「文件清单式」入口原本列了几个文件名——**那就是它期望的图数**（首尾帧是 2 行）。
    /// 给不满时按行少写，条数由 <see cref="ImageCapacity"/> 报给用户。
    /// </summary>
    public int ImageListCapacity { get; set; }

    /// <summary>
    /// 这一次**最多能喂几张**参考图（按组填时，取最大那一组的槽数）。
    ///
    /// 不能用 <see cref="ImageNodeIds"/>.Count：合集型的份里那个数是各组的**总和**
    /// （B03 是 6），而没有任何一组收得下 6 张——拿它当上限会算出一个虚高的额度。
    /// 清单式入口另算：它一个字段能吃下全部，额度取它原本列的行数。
    /// 声明式文件槽（<see cref="FileSlotImages"/>）也算：那类槽一个节点上可能有九格，各收一张。
    /// </summary>
    public int ImageCapacity
    {
        get
        {
            var byNodes = ImageGroups.Count > 0
                ? ImageGroups.Max(group => group.Count)
                : ImageNodeId.Length > 0 ? 1 : 0;
            return Math.Max(Math.Max(byNodes, ImageListCapacity), FileSlotImages.Count);
        }
    }

    /// <summary>
    /// 这份工作流**故意**不要负面词（negative 指向 ConditioningZeroOut 这类显式置空节点）。
    ///
    /// 为什么单独立一项、而不是算作「没认出来」：这是正规写法，不是缺陷。
    /// 算成缺陷的话，界面会一直报一个用户既改不了、也不需要改的「问题」，
    /// 而这种假问题多了以后，真问题就没人在意了。
    /// </summary>
    public bool NegativeDeliberatelyEmpty { get; set; }

    /// <summary>
    /// 比例：工作流里那种「一串固定选项」的输入（`aspect_ratio` / `aspect` / `ratio`），值是字面量。
    ///
    /// 只记「当前值的写法」是不够的：合法选项在**服务端的节点定义**里，工作流文件里只有当前选中的那一个。
    /// 所以另配一张 <see cref="AspectOptions"/>——这台服务器上同一个节点类型 + 同一个输入名**真实用过的值**
    /// （导入时全库扫出来），改比例时从那里挑；只有那里也没有同比例的值时，才退回「照它当前值的写法造一个」。
    /// </summary>
    public string AspectNodeId { get; set; } = string.Empty;
    public string AspectInput { get; set; } = string.Empty;
    public string AspectCurrent { get; set; } = string.Empty;

    /// <summary>
    /// 这个比例控件见过的值（别的值都来自这份清单，写回去的一定是它认识的）。
    /// 空表示：这份工作流没有比例控件，或库里还没有第二份用过同一个节点 + 同一个输入。
    /// </summary>
    public List<string> AspectOptions { get; set; } = new();

    /// <summary>
    /// 帧数：收「这一镜出多少帧」的输入（`length` / `num_frames` / `video_frames` 这类），**只认生成侧**。
    ///
    /// 为什么不认输出侧的 fps：`VHS_VideoCombine.frame_rate` 决定的是**播放速度**，
    /// 往那儿写等于把片子放快或放慢，帧数一帧没多——那不是「时长」。
    /// </summary>
    public string LengthNodeId { get; set; } = string.Empty;
    public string LengthInput { get; set; } = string.Empty;
    public int? LengthCurrent { get; set; }

    /// <summary>
    /// 帧率：把「要几秒」换算成帧数要用它。优先取生成侧的，取不到才退到输出侧，
    /// 并在 <see cref="FrameRateSource"/> 里说清用的是哪一个。
    /// **我们只读它、不改它**：帧率一变，动作的快慢也跟着变，用户要的是「这么多秒的这段动」，不是「把这段调快」。
    /// </summary>
    public string FrameRateNodeId { get; set; } = string.Empty;
    public string FrameRateInput { get; set; } = string.Empty;
    public double? FrameRateValue { get; set; }
    public string FrameRateSource { get; set; } = string.Empty;

    /// <summary>
    /// 没认出来的项，以及为什么。**这份清单是给用户核对用的**：
    /// 「认出了提示词，但没认出画幅」和「什么都没认出来」是两种完全不同的处境。
    /// </summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>能不能当文生图用：至少要找得到收提示词的地方。</summary>
    public bool CanTextToImage => PositiveNodeId.Length > 0;

    /// <summary>能不能改画幅。</summary>
    public bool CanResize => LatentNodeId.Length > 0;

    /// <summary>能不能吃参考图。</summary>
    public bool CanTakeImage => ImageNodeId.Length > 0 || ImageListInputs.Count > 0 || FileSlotImages.Count > 0;

    /// <summary>
    /// 只吃首帧、不收文字：SVD 那种「给一张图让它动起来」的工作流就是这种形状。
    /// 它不是「认不出提示词」的残次品，而是一整类正当用法——所以单列出来，而不是让它落进「认不出」那堆里。
    /// </summary>
    public bool IsFrameDriven => PositiveNodeId.Length == 0 && CanTakeImage;

    /// <summary>
    /// 只吃**一段片子**、不收文字：视频修复 / 补帧超分 / 去水印 / 影视二创那一支是「给一段它就干」，
    /// 本来就不需要提示词。和 <see cref="IsFrameDriven"/> 同理——是正当用法，不是「认不出提示词」的残次品，
    /// 不该在提交前被挡掉（实测 `M05-视频修复-SeedVR2.5`、`M10-视频高清放大-补帧`、`M09-LTX一键去字幕` 都被挡了）。
    /// </summary>
    public bool IsSourceDriven => PositiveNodeId.Length == 0 && CanTakeVideo;

    /// <summary>
    /// 秒数写在哪：当「帧数」不是字面量、而是由一个表达式从**秒数**折出来的时候用它。
    ///
    /// 实测形状（`U02-minimax_h3_图生视频基础版`）：`MiniMaxH3ImageToVideo.length` ←
    /// 一个数学表达式节点 `max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17`，
    /// 而 `a` 来自一个 `PrimitiveFloat`，`value = 5`，**节点标题就叫「Float (duration)」**。
    ///
    /// 这种工作流的正确改法是写**秒**、让它自己的表达式去折帧数与对齐（那个 `% 17` 是它要对齐的家族）——
    /// 直接写帧等于绕过它自己的规则，写错一个数就会被服务端拒。
    /// </summary>
    public string SecondsNodeId { get; set; } = string.Empty;
    public string SecondsInput { get; set; } = string.Empty;

    /// <summary>这个结论是怎么来的（要显示给人看：为什么我们认为那个常量就是秒数）。</summary>
    public string SecondsChain { get; set; } = string.Empty;

    /// <summary>能不能改这一镜的时长（要有帧数入口）。</summary>
    public bool CanSetLength => LengthNodeId.Length > 0;

    /// <summary>能不能改时长——但写法是「写秒」而不是「写帧」（帧数由它自己的表达式折出来）。</summary>
    public bool CanSetSeconds => SecondsNodeId.Length > 0;

    /// <summary>能不能改比例（要有那种固定选项的比例输入，且它的写法我们认得）。</summary>
    public bool CanSetAspect => AspectNodeId.Length > 0;

    /// <summary>
    /// 收「源视频」的入口（`VHS_LoadVideo` / `LoadVideoUI` / `LoadVideo` / `VideoLoader` 这类）。
    ///
    /// 为什么要单列：影视二创、对口型、视频修复、补帧超分那一支工作流吃的是**一段片子**，不是一张图——
    /// 底图那条路对它们没用（`VHS_LoadVideo.video` 的候选清单就是服务器 input 目录里的视频文件）。
    /// 按节点 id 稳定排序，多个入口按顺序各收一段；这个槽位里存的是**写哪个输入名**（视频是 `video`）。
    /// </summary>
    public List<string> VideoNodeIds { get; set; } = new();
    public List<string> VideoInputs { get; set; } = new();

    /// <summary>收「源音频」的入口（`VHS_LoadAudioUpload` / `LoadAudio` 这类）：对口型那一支还要一段音。</summary>
    public List<string> AudioNodeIds { get; set; } = new();
    public List<string> AudioInputs { get; set; } = new();

    public bool CanTakeVideo => VideoNodeIds.Count > 0;

    public bool CanTakeAudio => AudioNodeIds.Count > 0;

    /// <summary>
    /// **像底图入口、可我不认识**的地方（写法「类名.输入（节点 id）」）。
    ///
    /// 为什么要记：认不出入口时**不许断言「这份工作流用不了参考图」**——那句话的前提是「图入口我都认全了」，
    /// 而这一步恰恰是「我没认出来」。把「我没认出来」说成「它没有」，用户拿着这句话只会去找另一份工作流，
    /// 不会想到「指给我看」这条路。实测底图入口的类名并不都含 `LoadImage`（`ImageLoader` 那类一 `Contains` 就漏）。
    /// </summary>
    public List<string> UnrecognizedImageSlots { get; set; } = new();

    public bool HasUnrecognizedImageSlot => UnrecognizedImageSlots.Count > 0;

    /// <summary>
    /// **像源视频/源音频入口、可我不认识**的地方。<see cref="UnrecognizedImageSlots"/> 的同一条道理：
    /// 认不出的那一刻只能说自己没认出来，不能说这份吃不了片子。
    /// </summary>
    public List<string> UnrecognizedMediaSlots { get; set; } = new();

    public bool HasUnrecognizedMediaSlot => UnrecognizedMediaSlots.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(PositiveNodeId.Length > 0 ? "提示词✓" : IsFrameDriven ? "提示词—（只吃首帧）" : "提示词✗");
        parts.Add(NegativeNodeId.Length > 0 ? "负面词✓" : "负面词✗");
        parts.Add(CanResize ? "画幅✓" : "画幅✗");
        parts.Add(CanSetAspect ? "比例✓" : "比例✗");
        parts.Add(CanSetLength
            ? $"时长✓（{LengthCurrent} 帧 × {FrameRateValue:0.##}fps）"
            : CanSetSeconds ? "时长✓（写秒数，帧数由它自己折）" : "时长✗");
        parts.Add(SeedNodeIds.Count > 0 ? "种子✓" : "种子✗");
        parts.Add(CanTakeImage ? "底图✓" : "底图✗");
        // 源视频 / 源音频只在**这份工作流确实有**的时候出现：绝大多数工作流没有，
        // 每行都挂两个 ✗ 只会把真正要看的那几项淹掉。
        if (CanTakeVideo) parts.Add($"源视频✓（{VideoNodeIds.Count} 个入口）");
        if (CanTakeAudio) parts.Add($"源音频✓（{AudioNodeIds.Count} 个入口）");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// 从 API 格式工作流里认出参数槽位，并按一次调用把值写进去。
///
/// 识别规则（都从**连线**反推，不靠节点标题猜——标题是给人看的，随手就能改）：
///
///   1. 先找采样器：<c>inputs</c> 里同时有 <c>positive</c> / <c>negative</c> 的那一类节点
///      （KSampler、KSamplerAdvanced、自定义采样器都长这样），它的 <c>latent_image</c> 指向底图潜变量；
///   2. <c>positive</c> / <c>negative</c> 指向的节点若是 <c>CLIPTextEncode</c> 且 <c>text</c> 是**字面量**，
///      那就是收提示词的地方；如果 <c>text</c> 本身是连线（说明文字是别的节点生成的，例如内置的提示词改写），
///      就**认不出来**——那种工作流要改的是上游那个节点，不是这里；
///   3. <c>latent_image</c> 指向 <c>EmptyLatentImage</c> 时，那里收 <c>width</c> / <c>height</c>；
///   4. 采样器的 <c>seed</c> 是种子；出多张时逐张换，否则同一批会出成一模一样的 N 张；
///   5. 任何 <c>LoadImage</c> 的 <c>image</c> 收底图（图生图）。
/// </summary>
public static class ComfyUiWorkflowBinder
{
    /// <summary>
    /// 正向提示词的输入名，**按明确程度排序**。
    ///
    /// 为什么按名字找、而不是按节点类型找：实测同一件事在不同节点包里叫法不同——
    /// 常规出图是 CLIPTextEncode 接在采样器的 <c>positive</c> 上，
    /// 而 Wan 的视频编码器是 <c>WanVideoTextEncode.positive_prompt</c>（连出来一个「Text Multiline」）。
    /// 按名字找这两种形状用同一条规则就覆盖了；按类型找得每见一个新包加一条分支。
    /// </summary>
    private static readonly string[] PositiveNames =
    {
        "positive_prompt", "positive", "positive_text", "text_g", "prompt",
        // 中文包的写法。实测这台机器上 `NanFengH3MultiReferenceGeneratorV10/V15` 与 `ZealmanLLM_Generate`
        // 的输入叫 `提示词`，而这张名单原先**一个中文名都没有**——在中文包上等于没有名单，
        // 于是这些工作流认不出提示词入口，跑起来用的是**作者那份的示例提示词**（用户写的分镜进不去）。
        // 一律**排在最后**：同时存在 `prompt` 这类英文名时，仍然以英文名为准（更明确的那一档优先）。
        "提示词", "正向提示词", "正面提示词"
    };

    /// <summary>负面提示词的输入名，同样按明确程度排序。</summary>
    private static readonly string[] NegativeNames =
        { "negative_prompt", "negative", "negative_text", "text_l", "负面提示词", "负向提示词", "反向提示词" };

    /// <summary>
    /// 名字一个都对不上时的**最后兜底**只认这一个名字：经典 `CLIPTextEncode.text`（实测这台机器上
    /// 108 份工作流是这样写的）。别的像提示词的名字（`preset_prompt` / `system_prompt` / `Text4` /
    /// `prompt_1` 这些）不收：它们多半是**别的东西**（预设、给大模型的系统指令、分段列表），
    /// 认错比不认坏。
    /// </summary>
    private static readonly string[] FallbackTextNames = { "text" };

    /// <summary>跳一跳之后，认哪些输入名是「可以直接写文字的地方」。</summary>
    private static readonly string[] WritableTextNames = { "text", "value", "string", "prompt", "text_positive", "text_negative" };

    /// <summary>
    /// 帧数（这一镜出多少帧）的输入名，按明确程度排序。
    /// **只认生成侧**：输出节点的参与不了时长（见 <see cref="ResolveLength"/>）。
    /// </summary>
    private static readonly string[] LengthNames =
        { "video_frames", "num_frames", "video_length", "length", "max_frames", "frame_count", "frames", "total_frames" };

    /// <summary>帧率的输入名，按明确程度排序。</summary>
    private static readonly string[] FrameRateNames =
        { "frame_rate", "fps", "framerate", "video_fps", "frame_rate_value" };

    /// <summary>比例的输入名。这三个都是**一串固定选项**那种控件，值一定是字面量字符串。</summary>
    private static readonly string[] AspectNames = { "aspect_ratio", "aspect", "ratio" };

    /// <summary>认「秒数常量」时，常量节点上可能用的输入名。</summary>
    private static readonly string[] ConstantNames = { "value", "seconds", "duration", "int", "float", "number" };

    /// <summary>认槽位。工作流形状不对时不抛，只把「认不出什么」记进 Notes。</summary>
    /// <param name="optionValues">这台服务器上「固定选项」控件见过的值（见 <c>SiteProfile.OptionValues</c>）。</param>
    /// <param name="fileSlots">
    /// 「文件选择槽」表：`类名.输入名` → <see cref="ComfyUiFileSlotKinds"/> 里的一个值。
    /// **判据来自服务器的节点定义**（导入时算出来存在站点文件里，见 <c>SiteProfile.FileSlots</c>），
    /// 不是我们按输入名猜的。缺了它，只有 `LoadImage` 家族与「文件清单式」两种入口会被认出来。
    /// </param>
    public static ComfyUiWorkflowSlots Detect(
        string apiWorkflowJson,
        IReadOnlyDictionary<string, List<string>>? optionValues = null,
        IReadOnlyDictionary<string, string>? fileSlots = null)
    {
        var root = JsonNode.Parse(apiWorkflowJson) as JsonObject
            ?? throw new InvalidOperationException("工作流不是 JSON 对象");
        return Detect(root, optionValues, fileSlots);
    }

    public static ComfyUiWorkflowSlots Detect(
        JsonObject apiWorkflow,
        IReadOnlyDictionary<string, List<string>>? optionValues = null,
        IReadOnlyDictionary<string, string>? fileSlots = null)
    {
        ArgumentNullException.ThrowIfNull(apiWorkflow);
        var slots = new ComfyUiWorkflowSlots();

        ResolvePrompt(apiWorkflow, PositiveNames, "正向提示词", positive: true, slots);
        ResolvePrompt(apiWorkflow, NegativeNames, "负面词", positive: false, slots);
        ResolveSize(apiWorkflow, slots);
        ResolveAspect(apiWorkflow, slots);
        AttachAspectOptions(apiWorkflow, slots, optionValues);
        ResolveSeeds(apiWorkflow, slots);
        // 先认影音入口再认底图：底图「没找到」时那句说明要能分辨
        // 「这份工作流只能文生图」和「它吃的是片子、不吃图」——那是两回事。
        ResolveMedia(apiWorkflow, slots, fileSlots);
        ResolveImage(apiWorkflow, slots, fileSlots);
        ResolveLength(apiWorkflow, slots);

        if (!slots.CanTextToImage && !slots.IsFrameDriven && !slots.IsSourceDriven)
            slots.Notes.Add("认不出收提示词的节点：这份工作流的文字可能是由别的节点生成的"
                + "（例如内置的提示词改写），直接往里塞提示词不会生效，换一份工作流。");

        return slots;
    }

    /// <summary>
    /// 认提示词槽位：先按输入名在图里找候选，是字面量就直接写；是连线就**跳一跳**，
    /// 在它指向的节点上找可写的文字输入（实测两种形状都要跳：常规形状跳到 CLIPTextEncode，
    /// Wan 形状跳到 Text Multiline）。
    /// </summary>
    private static void ResolvePrompt(
        JsonObject graph, string[] names, string label, bool positive, ComfyUiWorkflowSlots slots)
    {
        var candidates = new List<(string NodeId, int Rank, string Input, JsonNode? Value, JsonObject Inputs)>();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            for (var rank = 0; rank < names.Length; rank++)
                if (inputs[names[rank]] is { } value)
                    candidates.Add((pair.Key, rank, names[rank], value, inputs));
        }

        if (candidates.Count == 0)
        {
            if (ResolvePromptByGraph(graph, positive, slots)) return;
            slots.Notes.Add($"没找到{label}：图里没有任何节点的输入叫 {string.Join(" / ", names)}。");
            return;
        }

        // 名字越靠前越优先；同名时按节点 id 排，保证同一份工作流每次认出来的是同一个位置。
        var ordered = candidates
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.NodeId, NodeIdComparer.Instance)
            .ToList();

        var reasons = new List<string>();
        foreach (var candidate in ordered)
        {
            if (candidate.Value is JsonValue)
            {
                // 同一个输入不能既当正向又当负向：另一路已经写下的那一格要跳过
                //（实测有两份工作流原先两边都指到同一个 `prompt` 上——那样会互相覆盖）。
                if (TakenByOtherSide(slots, positive, candidate.NodeId, candidate.Input))
                {
                    reasons.Add($"{candidate.Input}（节点 {candidate.NodeId}）已经判给另一路了");
                    continue;
                }

                Assign(slots, positive, candidate.NodeId, candidate.Input);
                if (candidate.Value is JsonValue literal && literal.TryGetValue<string>(out var current)
                    && SectionNames(current) is { Count: >= 3 } sections)
                    slots.Notes.Add($"这一份的{label}是一段**结构化模板**（{string.Join(" / ", sections.Take(4))} "
                        + "这几节）：写的时候**按节处理**——名字说的是画面 / 描述的那几节换成这一镜的描述；"
                        + "内容里点了 `<Picture 1>` 这类素材的那几节**原样保留**（那正是它跟参考图之间的约定）；"
                        + "音效 / 配乐那几节我们手里没这个信息，写成 `N/A`（不留作者那份示例里的内容）。");
                NoteAmbiguity(slots, label, ordered, candidate);
                return;
            }

            if (ReadLink(candidate.Inputs, candidate.Input) is not { } link)
            {
                reasons.Add($"{candidate.Input}（节点 {candidate.NodeId}）的形状看不懂");
                continue;
            }

            if (graph[link.NodeId] is not JsonObject source)
            {
                reasons.Add($"{candidate.Input} 指向的节点 {link.NodeId} 不在图里");
                continue;
            }

            var type = ClassTypeOf(source);

            // 「这份工作流不要这一路」的正规写法（ConditioningZeroOut）。
            // 算成缺陷的话，界面上会留一个用户既改不了、也不需要改的假问题。
            // 「显式置空」这件事是从类名里的一截字（`ZeroOut`）认出来的，名字对不上就认不出。
            // 认出来了也**别把话说到「这份工作流不需要它」那么满**——照我看到的说：这一路接的是
            // 显式置空的节点，我们不往里写。夸大说成「不需要」，用户就没法判断是不是我们要错了地方。
            if (IsDeliberatelyEmpty(type))
            {
                if (!positive) slots.NegativeDeliberatelyEmpty = true;
                slots.Notes.Add($"{(positive ? "正向提示词" : "负面词")}这一路接的是一个**显式置空**的节点"
                    + $"（{type}，节点 {link.NodeId}，按类名认的）：那是有意不要这一路，所以我们不往里写，"
                    + "写了也不生效。");
                return;
            }

            // 跳一跳：文字往往挂在被指向的那个节点上，也可能隔着一两个中转节点（Reroute / 拼接）。
            var walked = WalkToWritableText(graph, link.NodeId);
            if (walked is not { } target)
            {
                reasons.Add($"{candidate.Input}（节点 {candidate.NodeId}）指向 {type}（节点 {link.NodeId}），"
                    + "从那里往回走找不到一处能写字的输入（或者分叉了，分不清该走哪一条）");
                continue;
            }

            if (TakenByOtherSide(slots, positive, target.NodeId, target.Input))
            {
                reasons.Add($"{candidate.Input} 最后指到的是已经判给另一路的那一格");
                continue;
            }

            Assign(slots, positive, target.NodeId, target.Input);
            slots.Notes.Add($"{label}写在节点 {target.NodeId}（{target.Type}）的 "
                + $"{target.Input} 上：它是从节点 {candidate.NodeId} 的 {candidate.Input} 沿连线找过来的。");
            NoteAmbiguity(slots, label, ordered, candidate);
            return;
        }

        if (ResolvePromptByGraph(graph, positive, slots)) return;
        foreach (var reason in reasons) slots.Notes.Add($"没找到{label}：{reason}。");
    }

    /// <summary>
    /// 从某个节点往回走，找「写着字的地方」（一个可以直接写文字的输入）；找不到返回 null。
    ///
    /// 只沿**唯一那条连线**往下走（最多 6 跳）：一个节点上有多条连线、或者一处节点上能写字的地方不止一处，
    /// 就停手——那是判不清，宁可照实说「跟不到」，也不挑一条猜（挑错的代价是把提示词写到别的输入上）。
    /// </summary>
    private static (string NodeId, string Input, string Type)? WalkToWritableText(JsonObject graph, string startNodeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodeId = startNodeId;
        for (var hop = 0; hop < 7; hop++)
        {
            if (!seen.Add(nodeId)) return null;
            if (graph[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inputs) return null;

            var writable = WritableTextNames.Where(name => inputs[name] is JsonValue).ToList();
            if (writable.Count == 1) return (nodeId, writable[0], ClassTypeOf(node));
            if (writable.Count > 1) return null;

            var links = inputs.Where(field => ReadLink(inputs, field.Key) is not null).ToList();
            if (links.Count != 1) return null;
            if (ReadLink(inputs, links[0].Key) is not { } jump) return null;
            nodeId = jump.NodeId;
        }
        return null;
    }

    /// <summary>
    /// 名字一个都对不上时的**最后兜底**：经典 `CLIPTextEncode.text` 那一套写法。
    ///
    /// 凭什么敢往里写：**把「另一路」排掉之后，只剩一处能写字的地方**。两条结构证据一起用——
    ///   ① 候选池取**「名字说明是这一路」的那些输入能追到的节点**（采样器 `positive` 往回追到的那一支，
    ///      文字节点绝大多数就在里面）；那一支追不到时（这份没有这种写法）才在全图里找。
    ///   ② 把**另一路**（`negative` / `negative_prompt` 那些名字往回追到的节点）整支排掉。
    /// 这是结构证据，不是按名字猜。剩多处、一处不剩、或者那一处根本没被谁读过，就照旧说「没找到」：
    /// 名字分不出正负，猜错的代价是把用户的分镜描述写进**负面词**里，那比不写坏得多。
    /// </summary>
    private static bool ResolvePromptByGraph(JsonObject graph, bool positive, ComfyUiWorkflowSlots slots)
    {
        var side = ReachableFromNamedInputs(graph, positive ? PositiveNames : NegativeNames);
        var blocked = ReachableFromNamedInputs(graph, positive ? NegativeNames : PositiveNames);
        // 另一路已经写下的那一格也排掉（同一个输入不能既当正向又当负向）。
        if (positive && slots.NegativeNodeId.Length > 0) blocked.Add(slots.NegativeNodeId);
        if (!positive && slots.PositiveNodeId.Length > 0) blocked.Add(slots.PositiveNodeId);

        // 只收「真的被谁读过」的那些：没人读的文本框（比如作者留在画布上的草稿）不算入口。
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in graph)
            if (pair.Value?["inputs"] is JsonObject inputs)
                foreach (var field in inputs)
                    if (ReadLink(inputs, field.Key) is { } link) referenced.Add(link.NodeId);

        var left = new List<(string NodeId, string Input)>();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            if (side.Count > 0 && !side.Contains(pair.Key)) continue;
            if (!referenced.Contains(pair.Key) || blocked.Contains(pair.Key)) continue;
            foreach (var name in FallbackTextNames)
                if (inputs[name] is JsonValue) left.Add((pair.Key, name));
        }
        if (left.Count != 1) return false;

        Assign(slots, positive, left[0].NodeId, left[0].Input);
        slots.Notes.Add($"{(positive ? "正向提示词" : "负面词")}写在节点 {left[0].NodeId} 的 {left[0].Input} 上："
            + "这一份的输入名分不出正负（就叫 `text`），是**顺着连线把另一路排掉之后只剩这一处**才定下来的。");
        return true;
    }

    /// <summary>这一格现在放的字面量文字（不是字面量、或者没有，就回空串）。</summary>
    private static string CurrentText(JsonObject graph, string nodeId, string input)
        => graph[nodeId]?["inputs"]?[input] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    /// <summary>「这一节说的是画面 / 描述」的小节名（分节模板里要换成这一镜描述的那几节）。</summary>
    private static readonly string[] SceneSectionNames =
    {
        "summary", "detailed_description", "description", "scene", "shot", "prompt", "content",
        "描述", "画面", "场景", "分镜", "镜头", "摘要"
    };

    /// <summary>这一行是不是「单独成行的小节标题」（`subject_definitions:` 这种）。</summary>
    private static bool IsSectionHeader(string trimmed) => SectionNames(trimmed).Count == 1;

    /// <summary>
    /// 往**分节模板**式提示词里写这一镜的描述（不是模板就原样替换，一个字都不绕）。
    ///
    /// 为什么不能整段替换：实测 H3 那族的 `提示词` 是六节模板，其中
    /// `subject_definitions` / `retention_analysis` **正是把 `<Picture 1>` 这些参考图点起来**的那两段。
    /// 整段换成一段白话描述，参考图在提示词里就没人点了——所以按节处理，三种走法各有依据：
    ///   · 名字说的是**画面 / 描述**的那几节（`summary` / `detailed_description` / `描述` 这类）：
    ///     换成这一镜的描述；
    ///   · 内容里**点了素材**（出现 `<...>`）的那几节：**原样保留**（那是这份工作流与它参考图之间的约定）；
    ///   · 其余（音效 / 配乐那类）：我们手里没有对应信息，就写成 `N/A` ——
    ///     **不留作者那份示例里的内容**（留着会让人以为我们说过音频，其实那是他的示例场景）。
    /// 首次出现这种模板就是这套走法，出来不像就把上面三条按实测再调。
    /// </summary>
    private static string MergeIntoPromptTemplate(string current, string prompt)
    {
        var lines = (current ?? string.Empty).Split('\n');
        var sections = new List<(string Header, List<string> Body)>();
        var prefix = new List<string>();
        var index = 0;
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();
            if (!IsSectionHeader(trimmed))
            {
                prefix.Add(lines[index]);
                index++;
                continue;
            }

            var body = new List<string>();
            index++;
            while (index < lines.Length && !IsSectionHeader(lines[index].Trim()))
            {
                body.Add(lines[index]);
                index++;
            }
            sections.Add((trimmed, body));
        }

        if (sections.Count < 3) return prompt;

        var output = new List<string>(prefix);
        foreach (var (header, body) in sections)
        {
            output.Add(header);
            var head = header.TrimEnd(':', '：').Trim();
            if (!SceneSectionNames.Any(name => head.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                output.AddRange(string.Join('\n', body).Contains('<') ? body : new List<string> { "N/A" });
                continue;
            }

            output.AddRange(prompt.Split('\n'));
        }
        return string.Join('\n', output);
    }

    /// <summary>这一格是不是已经判给**另一路**了（一个输入不能既当正向又当负向）。</summary>
    private static bool TakenByOtherSide(ComfyUiWorkflowSlots slots, bool positive, string nodeId, string input)
        => positive
            ? slots.NegativeNodeId == nodeId && slots.NegativeInput == input
            : slots.PositiveNodeId == nodeId && slots.PositiveInput == input;

    /// <summary>
    /// 一段文字里**单独成行的小节标题**（`subject_definitions:` 这种）。
    ///
    /// 干什么用：实测 H3 那一族的 `提示词` 是**六节模板**（`subject_definitions` / `summary` /
    /// `retention_analysis` / `detailed_description` / `overall_soundscape` / `non_diegetic_music`），
    /// 而那种模板往往正是**把 `<Picture 1>` 这些参考图点起来**的那一段。我们按名字认出来之后会把整段换成
    /// 这一镜的描述、结构就没了——所以要把这件事**说出来**（不是在拦：拦着不写，分镜照样进不去，
    /// 那正是这些工作流本来的毛病）。
    /// </summary>
    private static List<string> SectionNames(string text)
    {
        var names = new List<string>();
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length is < 4 or > 24) continue;
            if (trimmed[^1] is not (':' or '：')) continue;
            var head = trimmed[..^1].Trim();
            if (head.Length is < 3 or > 20) continue;
            if (!head.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == ' ' || ch > 127)) continue;
            names.Add(head);
        }
        return names;
    }

    /// <summary>
    /// 从「名字里已经说明是哪一路」的那些输入出发，把它们**往回能追到的节点**全收起来
    /// （`negative` / `negative_prompt` 这些名字，或 `positive` / `prompt` 那些名字）。
    ///
    /// 用途只有一个：给「名字分不出正负」的兜底那一档**排掉另一路**。
    /// </summary>
    private static HashSet<string> ReachableFromNamedInputs(JsonObject graph, string[] names)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in graph)
        {
            if (pair.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (var name in names)
                if (ReadLink(inputs, name) is { } link)
                    set.UnionWith(BackwardReachable(graph, link.NodeId));
        }
        return set;
    }

    /// <summary>沿连线往回追，能到达哪些节点（含起点自己）；上限 400 个节点防病态图。</summary>
    private static HashSet<string> BackwardReachable(JsonObject graph, string startNodeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(startNodeId);
        while (pending.Count > 0)
        {
            var nodeId = pending.Pop();
            if (!seen.Add(nodeId) || seen.Count > 400) continue;
            if (graph[nodeId]?["inputs"] is not JsonObject inputs) continue;
            foreach (var field in inputs)
                if (ReadLink(inputs, field.Key) is { } link) pending.Push(link.NodeId);
        }
        return seen;
    }

    private static void Assign(ComfyUiWorkflowSlots slots, bool positive, string nodeId, string input)
    {
        if (positive)
        {
            slots.PositiveNodeId = nodeId;
            slots.PositiveInput = input;
        }
        else
        {
            slots.NegativeNodeId = nodeId;
            slots.NegativeInput = input;
        }
    }

    /// <summary>候选不止一处时说清用了哪一个：不然「我改了提示词怎么没生效」会变成一场猜谜。</summary>
    private static void NoteAmbiguity(
        ComfyUiWorkflowSlots slots, string label,
        List<(string NodeId, int Rank, string Input, JsonNode? Value, JsonObject Inputs)> ordered,
        (string NodeId, int Rank, string Input, JsonNode? Value, JsonObject Inputs) chosen)
    {
        if (ordered.Count <= 1) return;
        var others = ordered.Where(item => item != chosen).Select(item => $"节点 {item.NodeId} 的 {item.Input}");
        slots.Notes.Add($"{label}在图里有 {ordered.Count} 处候选（还有 {string.Join("、", others)}）："
            + $"用了节点 {chosen.NodeId} 的 {chosen.Input}。");
    }

    /// <summary>
    /// 认画幅。先找潜变量类节点（EmptyLatentImage 这种），没有再退而求其次找任何同时带
    /// width / height 的节点。宽高是**字面量**才改得动；是连线时尺寸由上游算出来。
    /// </summary>
    private static void ResolveSize(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        var carriers = graph
            .Where(pair => pair.Value is JsonObject node
                && node["inputs"] is JsonObject inputs
                && inputs["width"] is not null
                && inputs["height"] is not null)
            .Select(pair => (Id: pair.Key, Node: pair.Value!.AsObject()))
            .OrderBy(item => SizeCarrierRank(ClassTypeOf(item.Node)))
            .ThenBy(item => item.Id, NodeIdComparer.Instance)
            .ToList();

        if (carriers.Count == 0)
        {
            slots.Notes.Add("没找到带 width / height 的节点：出图尺寸沿用这份工作流自己的设置。");
            return;
        }

        var literal = carriers.FirstOrDefault(item =>
            item.Node["inputs"]!.AsObject()["width"] is JsonValue
            && item.Node["inputs"]!.AsObject()["height"] is JsonValue);
        if (literal.Node is not null)
        {
            slots.LatentNodeId = literal.Id;
            if (!ClassTypeOf(literal.Node).Contains("Latent", StringComparison.Ordinal))
                slots.Notes.Add($"画幅认在节点 {literal.Id}（{ClassTypeOf(literal.Node)}）的 width / height 上"
                    + "（这份工作流没有空潜变量节点）：改这两个数就是改它的出图尺寸。");
            return;
        }

        // 尺寸是连线时再走**一跳**：视频工作流的 width / height 常常指向上游一个缩放或编码节点，
        // 那两个数在**那里**是字面量（实测 Wan 与 LTX 两族都是这个形状）——
        // 写上游那两个数一样改得动尺寸，而只认「同节点上同时有字面量宽高」会把它们全判成改不了。
        foreach (var (id, node) in carriers)
        {
            var inputs = node["inputs"]!.AsObject();
            if (ReadLink(inputs, "width") is not { } origin) continue;
            if (graph[origin.NodeId] is not JsonObject upstream || upstream["inputs"] is not JsonObject upstreamInputs) continue;
            if (upstreamInputs["width"] is not JsonValue || upstreamInputs["height"] is not JsonValue) continue;

            slots.LatentNodeId = origin.NodeId;
            slots.Notes.Add($"画幅认在节点 {origin.NodeId}（{ClassTypeOf(upstream)}）的 width / height 上："
                + $"节点 {id}（{ClassTypeOf(node)}）的尺寸就是从那里来的，改上游那两个数即改出图尺寸"
                + DescribeLiterals(upstreamInputs, new[] { "width", "height" }) + "。");
            return;
        }

        var fallback = carriers[0];
        slots.Notes.Add(DescribeComputedSize(
            graph, fallback.Node["inputs"]!.AsObject(), fallback.Id, ClassTypeOf(fallback.Node)));
    }

    /// <summary>
    /// 挑画幅载体时谁更该当「那个节点」：潜变量节点最正，缩放/编码节点次之，
    /// 保存类节点排最后（往那儿写宽度不改变生成尺寸，只改变存下来的大小）。
    /// </summary>
    private static int SizeCarrierRank(string classType)
    {
        if (IsOutputSideNode(classType)) return 3;
        if (classType.Contains("Latent", StringComparison.Ordinal)) return 0;
        if (classType.Contains("Resize", StringComparison.Ordinal)
            || classType.Contains("Scale", StringComparison.Ordinal)
            || classType.Contains("Encode", StringComparison.Ordinal)
            || classType.Contains("Video", StringComparison.Ordinal)) return 1;
        return 2;
    }

    /// <summary>
    /// 认比例：`aspect_ratio` / `aspect` / `ratio` 这类**固定选项**控件，值是字面量字符串。
    ///
    /// 只取一个（按输入名的明确程度、再按节点 id），并记下它当前那个值。
    /// 能不能真的改成别的比例由 <see cref="VideoShape.FormatAspect"/> 说了算：
    /// 优先用 <see cref="ComfyUiWorkflowSlots.AspectOptions"/> 里见过的值，其次才照它当前的写法造。
    /// </summary>
    private static void ResolveAspect(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        var candidates = new List<(string NodeId, int Rank, string Input, string Value)>();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            for (var rank = 0; rank < AspectNames.Length; rank++)
            {
                if (inputs[AspectNames[rank]] is not JsonValue value
                    || !value.TryGetValue<string>(out var text)
                    || text.Length == 0) continue;
                candidates.Add((pair.Key, rank, AspectNames[rank], text));
                break;
            }
        }

        if (candidates.Count == 0) return;

        var chosen = candidates
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.NodeId, NodeIdComparer.Instance)
            .First();
        slots.AspectNodeId = chosen.NodeId;
        slots.AspectInput = chosen.Input;
        slots.AspectCurrent = chosen.Value;
    }

    /// <summary>
    /// 把「这个比例控件见过的值」贴上去（导入时全库扫出来的那张小表，键是 <c>节点类型.输入名</c>）。
    ///
    /// 顺带决定「认不出它当前这个写法」要不要报：库里有同一个节点 + 同一个输入用过的值，
    /// 就意味着我们**知道**它的选项长什么样，直接照那里写就行——这时再报「改不了」是假问题。
    /// </summary>
    private static void AttachAspectOptions(
        JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyDictionary<string, List<string>>? optionValues)
    {
        if (!slots.CanSetAspect) return;

        if (optionValues is not null
            && graph[slots.AspectNodeId] is JsonObject node
            && node["class_type"] is JsonValue classValue
            && classValue.TryGetValue<string>(out var classType)
            && classType.Length > 0
            && optionValues.TryGetValue(classType + "." + slots.AspectInput, out var known))
            slots.AspectOptions = new List<string>(known);

        if (VideoShape.TryParseRatio(slots.AspectCurrent, out _, out _)) return;
        if (slots.AspectOptions.Any(value => VideoShape.TryParseRatio(value, out _, out _))) return;

        slots.Notes.Add($"节点 {slots.AspectNodeId} 的 {slots.AspectInput} 是比例，但它的值「{slots.AspectCurrent}」不是 a:b 这种写法，"
            + "库里也没有第二份工作流在同一个节点上用过别的比例：我们不认识它的选项格式，"
            + "这一项改不了（要改请在那份工作流里改）。");
    }

    /// <summary>
    /// 认时长：帧数（生成侧的 `length` / `num_frames` 这类）与帧率。
    ///
    /// **只认生成侧**是这一项的要害：输出节点（`VHS_VideoCombine` / `SaveWEBM` / `CreateVideo`）上的
    /// `frame_rate` 决定的是**播放速度**——往那儿写只是把片子放快或放慢，帧数一帧没多。
    /// 帧率我们**只读不写**，原因同上：改了它，动作的快慢也跟着变。
    /// </summary>
    private static void ResolveLength(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        var lengths = new List<(string NodeId, int Rank, string Input, int Value)>();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            if (IsOutputSideNode(ClassTypeOf(node))) continue;
            for (var rank = 0; rank < LengthNames.Length; rank++)
            {
                if (inputs[LengthNames[rank]] is not JsonValue value
                    || !value.TryGetValue<int>(out var frames)
                    || frames <= 1) continue;
                lengths.Add((pair.Key, rank, LengthNames[rank], frames));
                break;
            }
        }

        if (lengths.Count > 0)
        {
            var chosen = lengths
                .OrderBy(item => item.Rank)
                .ThenBy(item => item.NodeId, NodeIdComparer.Instance)
                .First();
            slots.LengthNodeId = chosen.NodeId;
            slots.LengthInput = chosen.Input;
            slots.LengthCurrent = chosen.Value;
        }

        // 帧率：先找生成侧的，找不到才用输出侧的（只用来把秒换算成帧，不改它）。
        var rates = new List<(string NodeId, int Rank, string Input, double Value, bool OutputSide)>();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var outputSide = IsOutputSideNode(ClassTypeOf(node));
            for (var rank = 0; rank < FrameRateNames.Length; rank++)
            {
                if (inputs[FrameRateNames[rank]] is not JsonValue value
                    || !value.TryGetValue<double>(out var rate)
                    || rate <= 0) continue;
                rates.Add((pair.Key, rank, FrameRateNames[rank], rate, outputSide));
                break;
            }
        }

        var rate0 = rates
            .Where(item => !item.OutputSide)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.NodeId, NodeIdComparer.Instance)
            .FirstOrDefault();
        if (rate0.NodeId is null)
        {
            rate0 = rates
                .OrderBy(item => item.Rank)
                .ThenBy(item => item.NodeId, NodeIdComparer.Instance)
                .FirstOrDefault();
            if (rate0.NodeId is not null) rate0.OutputSide = true;
        }

        if (rate0.NodeId is not null)
        {
            slots.FrameRateValue = rate0.Value;
            slots.FrameRateSource = rate0.OutputSide
                ? $"输出侧节点 {rate0.NodeId} 的 {rate0.Input}（只在换算时用，不改它）"
                : $"节点 {rate0.NodeId} 的 {rate0.Input}";
            if (!rate0.OutputSide)
            {
                slots.FrameRateNodeId = rate0.NodeId;
                slots.FrameRateInput = rate0.Input;
            }
        }

        if (slots.CanSetLength)
            slots.Notes.Add($"时长可以改：帧数写在节点 {slots.LengthNodeId} 的 {slots.LengthInput} 上"
                + $"（现在是 {slots.LengthCurrent} 帧），换算用"
                + (slots.FrameRateValue is { } rate ? $"{rate:0.##}fps（{slots.FrameRateSource}）" : "不到帧率")
                + "。帧率本身不动：改了它动作的快慢也跟着变。");
        else
        {
            // 帧数不是字面量时，可能是**算出来的**——那就去找那个真正收秒的常量。
            ResolveSeconds(graph, slots);
            if (slots.CanSetSeconds)
                slots.Notes.Add("时长可以改（写秒数）：" + slots.SecondsChain);
            else if (lengths.Count == 0)
                slots.Notes.Add("这份工作流里找不到帧数入口（生成侧的 length / num_frames 这类）："
                    + "它出多少帧就是多少帧，时长改不了。");
        }
    }

    /// <summary>
    /// 认「秒数」：帧数是算出来的时候，找出那个真正收秒的常量。
    ///
    /// 判据要**三条都成立**才认，少一条就放弃（宁可说「不懂」，也不要写到一个无关的数上）：
    /// ① 帧数入口是连线，且它指向的节点带一个 <c>expression</c> 字面量（是个算数节点）；
    /// ② 表达式里出现了这份工作流的帧率（说明那个自变量是**秒**而不是别的量）；
    /// ③ 顺着算数节点的变量输入再走一跳，落在一个带数字字面量的常量节点上。
    ///
    /// 为什么这么谨慎：写错一个数不会有任何报错，只会安静地出一段时长不对的视频——
    /// 而那比「改不了」难查得多。
    /// </summary>
    private static void ResolveSeconds(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        if (slots.FrameRateValue is not { } fps || fps <= 0) return;
        var fpsText = fps.ToString("0.##", CultureInfo.InvariantCulture);

        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            if (IsOutputSideNode(ClassTypeOf(node))) continue;

            string? lengthKey = null;
            foreach (var name in LengthNames)
            {
                if (inputs[name] is JsonArray) { lengthKey = name; break; }
            }
            if (lengthKey is null) continue;
            if (ReadLink(inputs, lengthKey) is not { } framesFrom) continue;
            if (graph[framesFrom.NodeId] is not JsonObject math || math["inputs"] is not JsonObject mathInputs) continue;
            if (mathInputs["expression"] is not JsonValue expressionValue
                || !expressionValue.TryGetValue<string>(out var expression)
                || expression.Length == 0) continue;
            if (!expression.Contains(fpsText, StringComparison.Ordinal)) continue;   // ②

            foreach (var mathInput in mathInputs)
            {
                if (mathInput.Key == "expression") continue;
                if (mathInput.Value is not JsonArray) continue;                      // 只跟连线走
                if (ReadLink(mathInputs, mathInput.Key) is not { } variableFrom) continue;
                if (graph[variableFrom.NodeId] is not JsonObject constant
                    || constant["inputs"] is not JsonObject constantInputs) continue;

                foreach (var candidate in ConstantNames)
                {
                    if (constantInputs[candidate] is not JsonValue value) continue;
                    if (!value.TryGetValue<double>(out var seconds) || seconds <= 0) continue;

                    slots.SecondsNodeId = variableFrom.NodeId;
                    slots.SecondsInput = candidate;
                    slots.SecondsChain = $"帧数由节点 {framesFrom.NodeId}（{ClassTypeOf(math)}）按表达式「{expression}」算出来，"
                        + $"其中 {mathInput.Key} 来自节点 {variableFrom.NodeId}（{ClassTypeOf(constant)}）的 {candidate}"
                        + $"（现在是 {seconds:0.##}）——表达式里带着帧率 {fpsText}，所以那个量是**秒**。"
                        + "写秒数，帧数与对齐由它自己折。";
                    return;
                }
            }
        }
    }

    /// <summary>认种子：类型像采样器、并且 seed 是字面量的那些节点。</summary>
    private static void ResolveSeeds(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node) continue;
            if (!ClassTypeOf(node).Contains("Sampler", StringComparison.Ordinal)) continue;
            if (node["inputs"] is JsonObject inputs && inputs["seed"] is JsonValue)
                slots.SeedNodeIds.Add(pair.Key);
        }

        if (slots.SeedNodeIds.Count == 0)
            // 认不出就**退回保守行为**（什么都不写、保持原样），并且把「这是我按类名没认出来」说清楚——
            // 说成「没有种子输入」是假的，用户会以为这份工作流本来就不带种子。
            slots.Notes.Add("**没认出种子**：我是按「类名里带 Sampler、且 seed 是字面量」找的，"
                + "这份可能用的是别的写法——所以**不往任何地方乱写**（保持原样），"
                + "代价是同一批的多张可能出一模一样的几张，只能靠服务端自己决定。");
        else if (slots.SeedNodeIds.Count > 1)
            slots.Notes.Add($"这份工作流有 {slots.SeedNodeIds.Count} 段采样（节点 {string.Join("、", slots.SeedNodeIds)}）："
                + "同一张图会往每一段写同一个种子。");
    }

    /// <summary>认底图入口：全部都认，按节点 id 稳定排序。</summary>
    private static void ResolveImage(
        JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyDictionary<string, string>? fileSlots)
    {
        var loaders = graph
            .Where(pair => pair.Value is JsonObject loader
                && ClassTypeOf(loader).Contains("LoadImage", StringComparison.Ordinal)
                && loader["inputs"] is JsonObject inputs
                && inputs["image"] is JsonValue)
            .OrderBy(pair => pair.Key, NodeIdComparer.Instance)
            .ToList();

        if (loaders.Count == 0)
        {
            // 没有 LoadImage 家族，先看是不是「文件清单式」入口（MultiImageLoader.image_paths 那种）。
            ResolveImageList(graph, slots);
            if (slots.ImageListInputs.Count > 0) return;

            // 再看**服务器声明为「文件选择」**的图片槽（`图片1`…`图片9` 那种）。它一直没被认出来，
            // 因为类名里既没有 `LoadImage`、输入名也不以 `image` 开头——判据只能看服务器的声明。
            if (ResolveDeclaredImageSlots(graph, slots, fileSlots)) return;

            CollectUnrecognizedImageSlots(graph, slots);

            // 「我不认识」和「它没有」是两回事。这份里放着图片、而那个节点类型我不认识时，
            // 只能如实说自己没认出来，并把地方指出来请用户认领——
            // 说成「用不了参考图」是假话，用户拿着它只会去找另一份本来就在的工作流。
            if (slots.HasUnrecognizedImageSlot)
            {
                slots.Notes.Add("**没认出底图入口**：这份工作流里有几处放着图片文件名，可它们的节点类型我不认识——"
                    + string.Join("、", slots.UnrecognizedImageSlots)
                    + "。**请指给我**哪一处是收参考图的（是的话我下次就按那里走；都不是就照旧只文生图）。");
                return;
            }

            slots.Notes.Add(slots.CanTakeVideo
                ? "没有底图入口（LoadImage / 文件清单）：这份工作流吃的是**一段片子**（源视频入口），不吃参考图。"
                : "没找到底图入口（LoadImage / 文件清单）：这份工作流用不了参考图，只能文生图。");
            return;
        }

        slots.ImageNodeId = loaders[0].Key;
        slots.ImageInput = "image";
        slots.ImageNodeIds.AddRange(loaders.Select(pair => pair.Key));
        slots.ImageGroups.AddRange(GroupByOutput(graph, slots, slots.ImageNodeIds));
        // 两种入口同时出现在一份里，是我**没设计过**的形状（这台机器 316 份里一份都没有）：
        // 排序怎么排、额度怎么算都没定，所以只用 LoadImage 那条，并把这件事说出来——
        // 不猜，也不悄悄丢掉一边。**已经按类名认下的那几格不算「另一边的入口」**
        // （`LoadImage.image` 本身也在那张表里，不过滤的话每份 LoadImage 工作流都会冒出一句假的）。
        var mixedDeclared = DeclaredSlots(graph, fileSlots, ComfyUiFileSlotKinds.Image)
            .Where(slot => !slots.ImageNodeIds.Contains(slot.NodeId) && !slots.ImageListInputs.ContainsKey(slot.NodeId))
            .ToList();
        if (mixedDeclared.Count > 0)
            slots.Notes.Add("这份工作流**既有** LoadImage 那样的入口、**又有**服务器声明为文件选择的图片槽（"
                + string.Join("、", mixedDeclared.Select(slot => $"{slot.NodeId}.{slot.Input}"))
                + "）：混着两种该怎么排、额度怎么算我没设计过，所以这次只按 LoadImage 那几格填，文件槽保持原样。");
        if (loaders.Count > 1)
            slots.Notes.Add($"这份工作流有 {loaders.Count} 个底图入口（节点 {string.Join("、", slots.ImageNodeIds)}）："
                + "按顺序各收一张参考图——「角色 + 道具 + 场景」一起喂就走这里。"
                + "给不满时多出来的入口保持它原来的示例图，不拿同一张图去凑数。");
        if (slots.ImageGroups.Count > 1)
            slots.Notes.Add($"这份工作流是 {slots.ImageGroups.Count} 组并列（每组各带一个输出）："
                + string.Join("；", slots.ImageGroups.Select(group => "[" + string.Join("、", group) + "]"))
                + "。参考图**按组填**：每组各取「角色 → 道具 → 场景」的前几张（前面那组少拿几张），"
                + "不再按节点顺序把图平铺到前几个入口上——那样会喂错组。");
    }

    /// <summary>
    /// 认**「文件清单式」**的底图入口：一个多行文本框里放多行文件名（`MultiImageLoader.image_paths`）。
    ///
    /// 判据（不猜类名、也不维护一张输入名清单）：输入名以 `image` 开头、值是**字面量字符串**，
    /// 且按换行拆开后**每一项都以图片扩展名结尾**。三条都占才认。
    ///
    /// 空字段**不算**：空着说明那份工作流自己也没用它，这时宁可照旧报「没有底图入口」——
    /// 往一个我们没把握的字段里塞文件名，比不塞更坏（用户会以为喂进去了）。
    /// </summary>
    private static void ResolveImageList(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        var extensions = ImageExtensions;
        foreach (var pair in graph.OrderBy(pair => pair.Key, NodeIdComparer.Instance))
        {
            if (pair.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (var field in inputs)
            {
                if (!field.Key.StartsWith("image", StringComparison.OrdinalIgnoreCase)) continue;
                if (field.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;

                var names = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => item.Trim().Trim('"'))
                    .Where(item => item.Length > 0)
                    .ToList();
                if (names.Count == 0) continue;
                if (!names.All(item => extensions.Any(ext => item.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
                    continue;
                // 值是**路径**的不算（`image_path` 这种）：我们写进去的只有文件名（文件先传到 input 目录），
                // 往一个要路径的字段里写裸文件名，反而让它找不到文件。
                if (names.Any(item => item.Contains('/') || item.Contains('\\'))) continue;

                slots.ImageListInputs[pair.Key] = field.Key;
                slots.ImageListCapacity = Math.Max(slots.ImageListCapacity, names.Count);
                if (slots.ImageNodeId.Length == 0)
                {
                    slots.ImageNodeId = pair.Key;
                    slots.ImageInput = field.Key;
                }
            }
        }

        if (slots.ImageListInputs.Count > 0)
            slots.Notes.Add($"底图入口是**文件清单式**的（节点 {string.Join("、", slots.ImageListInputs.Keys)} 上的 "
                + $"{string.Join("、", slots.ImageListInputs.Values)}）：那是一个多行文本框，一行一个文件名，"
                + $"行与图**按顺序对应**（首行是首帧、末行是末帧），这份现在列了 {slots.ImageListCapacity} 行。"
                + "喂进来的参考图会按行写进去。");
    }

    /// <summary>图片文件名的后缀（认「像图的字面量」用；文件清单式入口与「认不出的入口」共用一份）。</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    /// <summary>视频文件名的后缀。</summary>
    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv" };

    /// <summary>音频文件名的后缀。</summary>
    private static readonly string[] AudioExtensions =
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma" };

    /// <summary>影音文件名的后缀（视频 + 音频）。</summary>
    private static readonly string[] MediaExtensions = VideoExtensions.Concat(AudioExtensions).ToArray();

    /// <summary>
    /// 这个文件名收的是哪一类素材（<see cref="ComfyUiFileSlotKinds"/> 里那三个值之一）；认不出返回空串。
    ///
    /// 判据只有一条：**后缀**。只写一份、上下游共用——导入时给「文件选择槽」定种类、生成时认「像片源的地方」，
    /// 两处各写一份的话早晚会说岔（多行值取第一行）。
    /// </summary>
    public static string MediaKindOfFileName(string? text)
    {
        var first = (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        var name = first.ToLowerInvariant();
        if (name.Length == 0) return string.Empty;
        if (ImageExtensions.Any(ext => name.EndsWith(ext, StringComparison.Ordinal))) return ComfyUiFileSlotKinds.Image;
        if (VideoExtensions.Any(ext => name.EndsWith(ext, StringComparison.Ordinal))) return ComfyUiFileSlotKinds.Video;
        if (AudioExtensions.Any(ext => name.EndsWith(ext, StringComparison.Ordinal))) return ComfyUiFileSlotKinds.Audio;
        return string.Empty;
    }

    /// <summary>
    /// 从「文件选择槽」表里挑出这份正文中属于某一类的那些格子：哪个节点的哪个输入。
    ///
    /// 只认**字面量字符串**那种输入：值是连线（数组）说明这一格是**图里喂过来的**，
    /// 不是让人选文件的地方——往那儿写文件名会把它原来的连线顶掉。
    /// </summary>
    private static List<ComfyUiFileSlot> DeclaredSlots(
        JsonObject graph, IReadOnlyDictionary<string, string>? fileSlots, string kind)
    {
        var found = new List<ComfyUiFileSlot>();
        if (fileSlots is null || fileSlots.Count == 0) return found;

        foreach (var pair in graph.OrderBy(pair => pair.Key, NodeIdComparer.Instance))
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var classType = ClassTypeOf(node);
            foreach (var field in inputs)
            {
                if (field.Value is not JsonValue value || !value.TryGetValue<string>(out _)) continue;
                if (fileSlots.TryGetValue(classType + "." + field.Key, out var declared) && declared == kind)
                    found.Add(new ComfyUiFileSlot(pair.Key, field.Key));
            }
        }
        return found;
    }

    /// <summary>
    /// 认**服务器声明为「文件选择」**的图片槽：一个节点上并排几格、每格有自己输入名的那种
    /// （实测 `NanFengH3MultiReferenceGeneratorV10` 的 `图片1`…`图片9`）。
    ///
    /// 凭什么认：这张表**导入时从节点定义算出来的**（存在站点文件的 `FileSlots` 里）——那个输入的
    /// 候选清单就是服务器 input 目录的文件列表（与 `LoadImage.image` 同一种形状），并且素材种类是图。
    /// **空着的格子照样算入口**：一个格子算不算入口由**节点自己的能力**决定，不由作者那一份用过没用过
    /// 决定；`未选择` 是候选清单里的一个合法取值，往里写只是改一个值，不是补一个缺的必填项。
    ///
    /// 只在这份**没有别的底图入口**时才走这条（调用点是那两条路都没认到之后）。
    /// </summary>
    private static bool ResolveDeclaredImageSlots(
        JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyDictionary<string, string>? fileSlots)
    {
        var found = DeclaredSlots(graph, fileSlots, ComfyUiFileSlotKinds.Image);
        if (found.Count == 0) return false;

        slots.FileSlotImages.AddRange(found);
        var groups = slots.FileSlotImages
            .GroupBy(slot => slot.NodeId, StringComparer.Ordinal)
            .Select(group => $"{group.Key}（{string.Join("、", group.Select(slot => slot.Input))}）");
        slots.Notes.Add("底图入口是**文件选择槽**式的（节点 " + string.Join("；", groups) + "）：一共 "
            + $"{slots.FileSlotImages.Count} 格，**每格各收一张**参考图，按顺序填；"
            + "给不满时剩下那几格保持它们原来的选择（缺省是「未选择」，那不是缺文件）。");
        return true;
    }

    /// <summary>
    /// 声明式文件槽里**视频 / 音频**那两档：并进 <see cref="VideoNodeIds"/> / <see cref="AudioNodeIds"/>
    /// （与按类名认下的入口走**同一条路**——写入、说明、影子判据都不必再各写一份）。
    ///
    /// 为什么并进去而不是另立一档：它们与那两种入口是**同一件事**（按文件名选、按顺序各收一段），
    /// 分开只会多出一份要同步的账。已经按类名认下的那几格要排掉（`VHS_LoadVideo.video` 也在那张表里），
    /// 否则同一格会被写两遍、还会在说明里被数两遍。
    /// </summary>
    private static void ResolveDeclaredMediaSlots(
        JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyDictionary<string, string>? fileSlots)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < slots.VideoNodeIds.Count && index < slots.VideoInputs.Count; index++)
            taken.Add(slots.VideoNodeIds[index] + "." + slots.VideoInputs[index]);
        for (var index = 0; index < slots.AudioNodeIds.Count && index < slots.AudioInputs.Count; index++)
            taken.Add(slots.AudioNodeIds[index] + "." + slots.AudioInputs[index]);

        int Add(List<ComfyUiFileSlot> found, List<string> nodeIds, List<string> inputs)
        {
            var added = 0;
            foreach (var slot in found)
            {
                if (!taken.Add(slot.NodeId + "." + slot.Input)) continue;
                nodeIds.Add(slot.NodeId);
                inputs.Add(slot.Input);
                added++;
            }
            return added;
        }

        Add(DeclaredSlots(graph, fileSlots, ComfyUiFileSlotKinds.Video), slots.VideoNodeIds, slots.VideoInputs);
        Add(DeclaredSlots(graph, fileSlots, ComfyUiFileSlotKinds.Audio), slots.AudioNodeIds, slots.AudioInputs);
        // 不加单独的说明：下面那两句「有几个源视频 / 源音频入口，按顺序各收一段」已经把行为说清了，
        // 再补一句「这是声明式文件槽」只是实现细节，白占一行。
    }

    /// <summary>
    /// 找「像底图入口、可我不认识」的地方，填进 <see cref="ComfyUiWorkflowSlots.UnrecognizedImageSlots"/>。
    ///
    /// 判据**只看事实，不猜类名**：某个输入是**字面量字符串**、拆行后至少有一行以图片后缀结尾，
    /// 而它既不是认下的 `LoadImage.image`、也不是认下的「文件清单式」入口（那两种走到这一步之前都已经被认走了），
    /// 且不在提示词节点上（提示词里恰好写着一行 `.png` 那种不该被算进来），也不在输出侧节点上
    /// （保存类的 `filename_prefix` 决定的是存下来的名字，不是入口）。
    ///
    /// 为什么是「至少有一行像图」而不是「每一行都像」：认成文件清单那一步更严（每一行都得是图片后缀），
    /// 这里要的恰恰相反——**宁可多报一处请人认领**，也不能把「我没认出来」说成「它没有」。
    /// </summary>
    private static void CollectUnrecognizedImageSlots(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        foreach (var pair in graph.OrderBy(pair => pair.Key, NodeIdComparer.Instance))
        {
            if (pair.Key == slots.PositiveNodeId || pair.Key == slots.NegativeNodeId) continue;
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var classType = ClassTypeOf(node);
            if (IsOutputSideNode(classType)) continue;
            foreach (var field in inputs)
            {
                if (slots.ImageListInputs.TryGetValue(pair.Key, out var claimed) && claimed == field.Key) continue;
                if (field.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                if (text.Length == 0) continue;
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 0) continue;
                if (!lines.Any(line => ImageExtensions.Any(ext => line.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
                    continue;
                slots.UnrecognizedImageSlots.Add($"{classType}.{field.Key}（节点 {pair.Key}）");
            }
        }
    }

    /// <summary>
    /// 找「像源视频 / 源音频入口、可我不认识」的地方。与
    /// <see cref="CollectUnrecognizedImageSlots"/> **同一套判据**（这里只看值，不看名字）：
    /// 某个输入是字面量字符串、拆行后至少有一行以影音后缀结尾，且它不在输出侧节点上。
    ///
    /// 为什么不按名字判（第一版就是那么写的，实测当场打脸）：这台机器上 `MiniMaxH3Director.shift_video`、
    /// `FeiHouEasyH3Loader.video_vae`、`FeiHouEasyH3.audio_duration_auto` 这些**参数**名字里都带
    /// `video` / `audio`，可它们一个文件都不放——照着名字报，就是拿三处噪音去请用户认领，
    /// 「请指给我」立刻变成骚扰。**入口的判据只能是「那里真的放着一个影音文件名」。**
    /// </summary>
    private static void CollectUnrecognizedMediaSlots(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        foreach (var pair in graph.OrderBy(pair => pair.Key, NodeIdComparer.Instance))
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var classType = ClassTypeOf(node);
            if (IsOutputSideNode(classType)) continue;
            foreach (var field in inputs)
            {
                if (field.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                if (text.Length == 0) continue;
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 0) continue;
                if (!MediaExtensions.Any(ext =>
                    lines.Any(line => line.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))) continue;

                slots.UnrecognizedMediaSlots.Add($"{classType}.{field.Key}（节点 {pair.Key}）");
            }
        }
    }

    /// <summary>
    /// 认「源视频 / 源音频」入口：影视二创、对口型、视频修复那一支吃的是**一段片子**。
    ///
    /// 判据分两步，都不猜：
    /// ① 节点类型里有 <c>LoadVideo</c> / <c>VideoLoader</c> → 视频，有 <c>LoadAudio</c> / <c>AudioLoader</c> → 音频。
    ///    实测这台服务器上这一类节点是 <c>VHS_LoadVideo.video</c>、<c>VHS_LoadVideoFFmpeg.video</c>、
    ///    <c>LoadVideoUI.video</c>、<c>LoadVideo.file</c>、<c>VideoLoader.file</c>（音频有
    ///    <c>VHS_LoadAudioUpload.audio</c>、<c>LoadAudio.audio</c>、<c>LoadAudioUI.audio</c>、
    ///    <c>YusuLoadAudioUI.audio</c>、<c>CSLoadAudioUI.audio</c>、<c>VRGDG_LoadAudioWithPath.audio</c>）。
    /// ② 输入名只认 <c>video</c> / <c>audio</c> / <c>file</c>：**故意不收 <c>video_path</c> 这类**——
    ///    那种槽位要的是服务器上的**绝对路径**，而我们是把本机文件传到 input 目录、按**文件名**引用，
    ///    写进去只会让服务端找不到文件。
    /// </summary>
    private static void ResolveMedia(
        JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyDictionary<string, string>? fileSlots)
    {
        foreach (var pair in graph.OrderBy(pair => pair.Key, NodeIdComparer.Instance))
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var flat = new string(ClassTypeOf(node).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var video = flat.Contains("loadvideo", StringComparison.Ordinal)
                || flat.Contains("videoloader", StringComparison.Ordinal);
            var audio = !video && (flat.Contains("loadaudio", StringComparison.Ordinal)
                || flat.Contains("audioloader", StringComparison.Ordinal));
            if (!video && !audio) continue;

            var names = video ? new[] { "video", "file" } : new[] { "audio", "file" };
            var name = names.FirstOrDefault(candidate => inputs[candidate] is JsonValue);
            if (name is null) continue;

            if (video)
            {
                slots.VideoNodeIds.Add(pair.Key);
                slots.VideoInputs.Add(name);
            }
            else
            {
                slots.AudioNodeIds.Add(pair.Key);
                slots.AudioInputs.Add(name);
            }
        }

        // 声明式文件槽里视频 / 音频那两档并进入口清单：要在下面那两句「有几个源视频入口」**之前**做，
        // 否则说明里的数会把它们漏掉。
        ResolveDeclaredMediaSlots(graph, slots, fileSlots);

        if (slots.VideoNodeIds.Count > 1)
            slots.Notes.Add($"这份工作流有 {slots.VideoNodeIds.Count} 个源视频入口（节点 {string.Join("、", slots.VideoNodeIds.Distinct())}）："
                + "按顺序各收一段片子（例如「主片 + 参考片」这种两张图的用法）。");

        // 音频也要说：两个音频口的工作流（双人对白那一类）**按顺序各收一段音**，
        // 只给一段的话第二个入口会留着它自己的示例——那样出来的对话说的还是例子里的内容。
        if (slots.AudioNodeIds.Count > 1)
            slots.Notes.Add($"这份工作流有 {slots.AudioNodeIds.Count} 个源音频入口（节点 {string.Join("、", slots.AudioNodeIds)}）："
                + "按顺序各收一段音（例如「双人对白」一人一段）；只给一段时，后面的入口还留着它自己的示例。");

        if (slots.CanTakeVideo || slots.CanTakeAudio) return;

        CollectUnrecognizedMediaSlots(graph, slots);

        // 一个源视频/源音频入口都没认出来时，**不许把话说成「这份吃不了片子」**：
        // 「我不认识这个类型」不等于「它没有」。把像片源的地方指出来请用户认领，
        // 并说清有一类槽位我是**故意不写**的——要服务器绝对路径那些（`video_path` 那种），
        // 我们按文件名引用，写进去服务端反而找不到文件。
        if (slots.HasUnrecognizedMediaSlot)
            slots.Notes.Add("**没认出源视频 / 源音频入口**：这份工作流里有几处像片源的地方，"
                + "可它们的节点类型（或输入名）我不认识——" + string.Join("、", slots.UnrecognizedMediaSlots)
                + "。**请指给我**哪一处是收片子 / 收声音的（要服务器上**绝对路径**的那种我不写："
                + "我们是把文件传到 input 目录、按文件名引用，写绝对路径反而找不到文件）。");
    }

    /// <summary>
    /// 把入口按「能通到哪个输出节点」分组（并查集：共用同一个输出的算一组）。
    ///
    /// 为什么要按**输出**分：合集型的工作流（`B03` 那种「单双三图」并排三档）里，三组各自走向
    /// 自己的 `SaveImage`，组与组之间在图上是连通的、没有任何开关——只有顺着连线走到输出才分得开。
    ///
    /// **分不清就退回一整组**（等于原先的平铺行为），不硬分：只要有一个入口走不到任何保存节点，
    /// 说明这份的图没接进产出、或输出节点不是保存类，这时分组没有依据。
    ///
    /// 退回时**要说出来**（只在真的有两个以上入口、分组本来有用的时候）：`IsSaveNode` 是按类名里的
    /// `SaveImage` / `VideoCombine` 这类字眼认的，换台装了别的保存节点的服务器就认不出——
    /// 那时分组会悄悄失效、回到平铺，用户看到的是「按顺序喂」的结果，却以为自己在按组喂。
    /// </summary>
    private static List<List<string>> GroupByOutput(JsonObject graph, ComfyUiWorkflowSlots slots, IReadOnlyList<string> entries)
    {
        var sinks = entries.ToDictionary(entry => entry, entry => SaveNodesUnder(graph, entry), StringComparer.Ordinal);
        if (sinks.Values.Any(set => set.Count == 0))
        {
            if (entries.Count > 1)
            {
                var orphaned = entries.Where(entry => sinks[entry].Count == 0).ToList();
                slots.Notes.Add("这份工作流有多个底图入口（节点 " + string.Join("、", entries)
                    + "），可**我分不出组**：其中有入口（节点 " + string.Join("、", orphaned)
                    + "）走不到任何我认得的保存类输出——它的保存节点可能是我没见过的写法。"
                    + "所以参考图**按节点顺序平铺**（老办法），不按组填。");
            }
            return new List<List<string>> { entries.ToList() };
        }

        var parent = entries.ToDictionary(entry => entry, entry => entry, StringComparer.Ordinal);
        string Find(string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        for (var i = 0; i < entries.Count; i++)
            for (var j = i + 1; j < entries.Count; j++)
                if (sinks[entries[i]].Overlaps(sinks[entries[j]]))
                    parent[Find(entries[i])] = Find(entries[j]);

        return entries
            .GroupBy(Find)
            .Select(group => group.ToList())          // entries 本身就是 id 序，组内因此天然有序
            .OrderBy(group => group[0], NodeIdComparer.Instance)
            .ToList();
    }

    /// <summary>从一个节点往下游走，能到达的「保存类」节点（这些才是真输出）。</summary>
    private static HashSet<string> SaveNodesUnder(JsonObject graph, string start)
    {
        var savers = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var pair in graph)
            {
                if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
                if (!FeedsInto(inputs, current)) continue;
                if (IsSaveNode(ClassTypeOf(node))) savers.Add(pair.Key);
                if (seen.Add(pair.Key)) queue.Enqueue(pair.Key);
            }
        }
        return savers;
    }

    /// <summary>某个节点的 inputs 里是否有连线指向 <paramref name="nodeId"/>。</summary>
    private static bool FeedsInto(JsonObject inputs, string nodeId)
    {
        foreach (var pair in inputs)
        {
            if (pair.Value is not JsonArray link || link.Count < 2) continue;
            var origin = link[0] switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty
            };
            if (origin.Length > 0 && string.Equals(origin, nodeId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>保存类节点：真正会落文件的那种。预览类不算——它只是给你看一眼。</summary>
    private static bool IsSaveNode(string classType) =>
        classType.Contains("SaveImage", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("SaveVideo", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("SaveAudio", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("VideoCombine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 「只在末端起作用」的节点：保存、预览、合成视频的都在这一类。
    ///
    /// 为什么要单列出来：这些节点上的 `frame_rate` 决定的是**播放速度**而不是帧数，
    /// 上面的 `width` / `height` 决定的是**存下来的大小**而不是生成尺寸。把它们当参数入口，
    /// 会出现「界面说改了时长/画幅，实际只是把片子放快了、或只是缩放了一下」这种最坏的结果。
    /// </summary>
    private static bool IsOutputSideNode(string classType) =>
        IsSaveNode(classType)
        || classType.Contains("CreateVideo", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("SaveWEBM", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("SaveAnimated", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("PreviewImage", StringComparison.OrdinalIgnoreCase)
        || classType.Contains("PreviewVideo", StringComparison.OrdinalIgnoreCase);

    private static string DescribeLiterals(JsonObject inputs, IReadOnlyList<string> keys)
    {
        var parts = new List<string>();
        foreach (var key in keys)
        {
            if (inputs[key] is not JsonValue value) continue;
            if (value.TryGetValue<string>(out var text) && text.Length > 0) parts.Add($"{key} {text}");
            else if (value.TryGetValue<int>(out var amount)) parts.Add($"{key} {amount}");
        }
        return parts.Count > 0 ? $"（{string.Join("、", parts)}）" : string.Empty;
    }

    /// <summary>节点 id 的稳定顺序：能当数字比的按数字比，其余按字符串比（不抛，认不出来也能排）。</summary>
    private sealed class NodeIdComparer : IComparer<string>
    {
        public static readonly NodeIdComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (long.TryParse(left, out var a) && long.TryParse(right, out var b)) return a.CompareTo(b);
            return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
        }
    }

    /// <summary>
    /// 按一次调用把值写进这份工作流，返回可提交的新图。**不改传入的那一份**——
    /// 站点里存的是模板，改坏了下一张图就跟着错。
    /// </summary>
    public static JsonObject Bind(
        string apiWorkflowJson,
        ComfyUiWorkflowSlots slots,
        ComfyUiBindValues values)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var root = JsonNode.Parse(apiWorkflowJson) as JsonObject
            ?? throw new InvalidOperationException("工作流不是 JSON 对象");
        return Bind(root, slots, values);
    }

    public static JsonObject Bind(JsonObject apiWorkflow, ComfyUiWorkflowSlots slots, ComfyUiBindValues values)
    {
        ArgumentNullException.ThrowIfNull(apiWorkflow);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(values);

        var graph = apiWorkflow.DeepClone().AsObject();

        if (slots.PositiveNodeId.Length > 0 && values.Prompt.Length > 0)
            SetInput(graph, slots.PositiveNodeId, slots.PositiveInput, JsonValue.Create(
                MergeIntoPromptTemplate(CurrentText(graph, slots.PositiveNodeId, slots.PositiveInput), values.Prompt)));

        if (slots.NegativeNodeId.Length > 0)
            SetInput(graph, slots.NegativeNodeId, slots.NegativeInput, JsonValue.Create(values.Negative));

        if (slots.LatentNodeId.Length > 0)
        {
            if (values.Width > 0) SetInput(graph, slots.LatentNodeId, "width", JsonValue.Create(values.Width));
            if (values.Height > 0) SetInput(graph, slots.LatentNodeId, "height", JsonValue.Create(values.Height));
            // 出多张时 batch_size 交给工作流自己：它是模板作者的决定（有的工作流靠它一次出多张）。
        }

        // 比例：优先写「这台服务器上同一个节点 + 同一个输入用过的值」，其次才照它当前那个值的写法造新值。
        // 都造不出来（"adaptive" 这种）就不写——写一个它不认识的字符串，换来的是服务端的一次报错。
        if (slots.AspectNodeId.Length > 0 && values.AspectRatio.Length > 0)
        {
            var text = VideoShape.FormatAspect(slots.AspectCurrent, values.AspectRatio, slots.AspectOptions);
            if (text.Length > 0) SetInput(graph, slots.AspectNodeId, slots.AspectInput, JsonValue.Create(text));
        }

        // 时长：写的是**帧数**（已经由 VideoFrameMath 按帧率换算并贴到它原来的家族上）。
        // 帧率一个字不动——改了它动作的快慢也跟着变。
        if (slots.LengthNodeId.Length > 0 && values.Length is { } frames && frames > 1)
            SetInput(graph, slots.LengthNodeId, slots.LengthInput, JsonValue.Create(frames));

        // 帧数是算出来的那种：写**秒**，让那份工作流自己的表达式去折帧数与对齐。
        if (slots.SecondsNodeId.Length > 0 && values.Seconds is { } seconds && seconds > 0)
            SetInput(graph, slots.SecondsNodeId, slots.SecondsInput, JsonValue.Create(seconds));

        if (values.Seed is { } seed)
            foreach (var seedNodeId in slots.SeedNodeIds)
                SetInput(graph, seedNodeId, "seed", JsonValue.Create(seed));

        // 源视频 / 源音频：视频二创、对口型、视频修复那一支吃的是**一段片子**（对口型还要一段音）。
        // 与底图同一条规矩：按入口顺序对号入座，给不满就只写前几个，多出来的保持它自己的示例，
        // **不拿同一段去凑数**——那样等于谎报输入。
        for (var index = 0; index < slots.VideoNodeIds.Count && index < values.VideoNames.Count; index++)
            if (values.VideoNames[index].Length > 0)
                SetInput(graph, slots.VideoNodeIds[index], slots.VideoInputs[index], JsonValue.Create(values.VideoNames[index]));

        for (var index = 0; index < slots.AudioNodeIds.Count && index < values.AudioNames.Count; index++)
            if (values.AudioNames[index].Length > 0)
                SetInput(graph, slots.AudioNodeIds[index], slots.AudioInputs[index], JsonValue.Create(values.AudioNames[index]));

        // 底图：值为空时**不动**原来的那张（工作流里往往自带一张示例图，
        // 清掉会让它连示例都跑不了）；有值时写上传后的名字。
        //
        // 有多个底图入口时按顺序各写一张：参考图的顺序由装配那一侧定死（角色 → 道具 → 场景），
        // 这里只负责照顺序对号入座。给不满就只写前几个入口，多出来的保持它原来的示例图——
        // **不拿同一张图去凑数**，那样等于谎报输入，出来的东西不像还没法解释。
        var imageNames = values.ImageNames.Count > 0
            ? values.ImageNames
            : values.ImageName.Length > 0
                ? (IReadOnlyList<string>)new[] { values.ImageName }
                : Array.Empty<string>();
        var imageInput = slots.ImageInput.Length > 0 ? slots.ImageInput : "image";

        // 合集型（多组并列、每组各带一个输出）要**按组填**，不能平铺：平铺会把图塞进前几个入口，
        // 而那几个入口很可能全落在同一个小组里，真正能收多张的那组一张都拿不到（实测 B03）。
        // 每组各取前几张（角色 → 道具 → 场景 的前缀），组与组互不干扰。
        if (slots.ImageGroups.Count > 1)
        {
            foreach (var group in slots.ImageGroups)
                for (var index = 0; index < group.Count && index < imageNames.Count; index++)
                {
                    if (imageNames[index].Length == 0) continue;
                    SetInput(graph, group[index], imageInput, JsonValue.Create(imageNames[index]));
                }
            return graph;
        }

        var imageSlots = slots.ImageNodeIds.Count > 0
            ? (IReadOnlyList<string>)slots.ImageNodeIds
            : slots.ImageNodeId.Length > 0
                ? new[] { slots.ImageNodeId }
                : Array.Empty<string>();
        for (var index = 0; index < imageSlots.Count && index < imageNames.Count; index++)
        {
            if (imageNames[index].Length == 0) continue;
            // 清单式入口一个字段装全部（见下），不走这里一槽一张。
            if (slots.ImageListInputs.ContainsKey(imageSlots[index])) continue;
            SetInput(graph, imageSlots[index], imageInput, JsonValue.Create(imageNames[index]));
        }

        // 「文件清单式」入口：按行装下参考图，行与图一一对应。
        // 给不满时**保留它原来的后几行**：首尾帧那一类工作流的末行是「末帧」，我们给不出就替它留着，
        // 不拿首帧去凑数（与槽位给不满时「多余的入口保持示例图」同一个口径）。
        foreach (var (listNodeId, listInput) in slots.ImageListInputs)
        {
            var given = imageNames.Where(name => name.Length > 0).ToList();
            if (given.Count == 0) continue;
            var kept = ReadImageListLines(graph, listNodeId, listInput).Skip(given.Count);
            var lines = given.Concat(kept).Where(line => line.Length > 0).ToList();
            SetInput(graph, listNodeId, listInput, JsonValue.Create(string.Join('\n', lines)));
        }

        // 声明式文件槽（服务器声明为「文件选择」的图片槽，见 ComfyUiWorkflowSlots.FileSlotImages）：
        // 一格有**自己的输入名**，所以不能借用上面那个 imageInput（那是「所有入口同名」那条路的写法）。
        // 同样按顺序对号入座、给不满就只写前几个，剩下那几格保持它们原来的选择（通常是「未选择」）——
        // 不拿同一张图去凑数，也不去动没给的那几格。
        for (var index = 0; index < slots.FileSlotImages.Count && index < imageNames.Count; index++)
        {
            if (imageNames[index].Length == 0) continue;
            SetInput(graph, slots.FileSlotImages[index].NodeId, slots.FileSlotImages[index].Input,
                JsonValue.Create(imageNames[index]));
        }

        return graph;
    }

    /// <summary>读出「文件清单式」入口现在列的那几行（按换行拆开、去掉空行）。</summary>
    private static IEnumerable<string> ReadImageListLines(JsonObject graph, string nodeId, string inputName)
    {
        if (graph[nodeId]?["inputs"] is not JsonObject inputs) yield break;
        if (inputs[inputName] is not JsonValue value || !value.TryGetValue<string>(out var text)) yield break;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line;
    }

    /// <summary>
    /// 尺寸由上游算出来时的那句说明。**把它当前会算出的画幅一起报出来**：
    /// 「改不了画幅」只说了坏消息，用户真正要知道的是「那我会拿到多大的图」。
    /// </summary>
    private static string DescribeComputedSize(JsonObject graph, JsonObject latentInputs, string latentId, string latentType)
    {
        var source = ReadLink(latentInputs, "width") ?? ReadLink(latentInputs, "height");
        if (source is not { } origin
            || graph[origin.NodeId] is not JsonObject computer
            || computer["inputs"] is not JsonObject computerInputs)
        {
            return $"画幅改不了：潜变量（{latentType}，节点 {latentId}）的宽高不是字面量，"
                + "由上游决定；出图尺寸沿用这份工作流自己的设置。";
        }

        var detail = new List<string>();
        foreach (var key in new[] { "aspect_ratio", "megapixels", "resolution", "width", "height" })
        {
            if (computerInputs[key] is not JsonValue value) continue;
            if (value.TryGetValue<string>(out var text) && text.Length > 0) detail.Add($"{key} {text}");
            else if (value.TryGetValue<int>(out var amount)) detail.Add($"{key} {amount}");
        }

        return $"画幅改不了：尺寸由上游节点 {origin.NodeId}（{ClassTypeOf(computer)}）算出来"
            + (detail.Count > 0 ? $"（{string.Join("、", detail)}）" : string.Empty)
            + "；选这份工作流就按它自己的设置出图。";
    }

    private static void SetInput(JsonObject graph, string nodeId, string input, JsonNode? value)
    {
        if (input.Length == 0) return;
        // 只有**字面量**输入才写。是连线（[节点, 槽]）的时候写进去等于把连线拆了——
        // 那会把工作流改坏，所以宁可不动，由 Detect 在 Notes 里说明。
        if (graph[nodeId]?["inputs"] is not JsonObject inputs) return;
        if (inputs[input] is JsonArray) return;
        inputs[input] = value;
    }

    private static (string NodeId, int Slot)? ReadLink(JsonObject inputs, string key)
    {
        if (inputs[key] is not JsonArray link || link.Count < 2) return null;
        var nodeId = link[0] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
        if (nodeId.Length == 0) return null;
        var slot = link[1] is JsonValue slotValue && slotValue.TryGetValue<int>(out var index) ? index : 0;
        return (nodeId, slot);
    }

    private static string ClassTypeOf(JsonObject node) =>
        node["class_type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : string.Empty;

    /// <summary>「显式置空这一路条件」的节点类（ConditioningZeroOut 这种）：有意的空，不是缺失。</summary>
    private static bool IsDeliberatelyEmpty(string classType) =>
        classType.Contains("ZeroOut", StringComparison.Ordinal);
}

/// <summary>一次调用要写进工作流的值。没值的那几项**不写**，保留工作流模板里的原样。</summary>
public sealed record ComfyUiBindValues
{
    public string Prompt { get; init; } = string.Empty;
    public string Negative { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public long? Seed { get; init; }

    /// <summary>要写进去的帧数（已按帧率由秒换算好）；null 表示这次不改时长。</summary>
    public int? Length { get; init; }

    /// <summary>要写进去的**秒数**：帧数由那份工作流自己的表达式折出来时用这一项。</summary>
    public double? Seconds { get; init; }

    /// <summary>想要的比例，形如 <c>9:16</c>；空表示不改（沿用工作流自己的）。</summary>
    public string AspectRatio { get; init; } = string.Empty;

    /// <summary>已上传到 ComfyUI 的底图文件名；空表示这次不用底图。</summary>
    public string ImageName { get; init; } = string.Empty;

    /// <summary>
    /// 这次要喂的底图文件名，按顺序对到每个底图入口（`ImageName` 是它的第 1 个）。
    /// 参考图的顺序由装配那一侧定死，这里不重排。
    /// </summary>
    public IReadOnlyList<string> ImageNames { get; init; } = Array.Empty<string>();

    /// <summary>这次要喂的源视频文件名（已上传到 ComfyUI），按顺序对到每个源视频入口。</summary>
    public IReadOnlyList<string> VideoNames { get; init; } = Array.Empty<string>();

    /// <summary>这次要喂的源音频文件名（已上传到 ComfyUI），按顺序对到每个源音频入口。</summary>
    public IReadOnlyList<string> AudioNames { get; init; } = Array.Empty<string>();
}
