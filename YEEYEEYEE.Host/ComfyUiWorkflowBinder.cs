using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YEEYEEYEE.Host;

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
    /// 这一次**最多能喂几张**参考图（按组填时，取最大那一组的槽数）。
    ///
    /// 不能用 <see cref="ImageNodeIds"/>.Count：合集型的份里那个数是各组的**总和**
    /// （B03 是 6），而没有任何一组收得下 6 张——拿它当上限会算出一个虚高的额度。
    /// </summary>
    public int ImageCapacity => ImageGroups.Count > 0
        ? ImageGroups.Max(group => group.Count)
        : ImageNodeId.Length > 0 ? 1 : 0;

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
    /// 为什么只记「当前值的写法」而不记选项清单：合法选项在**服务端的节点定义**里，
    /// 工作流文件里只有当前选中的那一个。所以我们只能照它自己那个值的写法造一个新值
    /// （`16:9` → `9:16`）。造不出来（写法不认识，例如 `adaptive`）就如实说改不了。
    /// </summary>
    public string AspectNodeId { get; set; } = string.Empty;
    public string AspectInput { get; set; } = string.Empty;
    public string AspectCurrent { get; set; } = string.Empty;

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
    public bool CanTakeImage => ImageNodeId.Length > 0;

    /// <summary>
    /// 只吃首帧、不收文字：SVD 那种「给一张图让它动起来」的工作流就是这种形状。
    /// 它不是「认不出提示词」的残次品，而是一整类正当用法——所以单列出来，而不是让它落进「认不出」那堆里。
    /// </summary>
    public bool IsFrameDriven => PositiveNodeId.Length == 0 && CanTakeImage;

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
    private static readonly string[] PositiveNames = { "positive_prompt", "positive", "positive_text", "text_g", "prompt" };

    /// <summary>负面提示词的输入名，同样按明确程度排序。</summary>
    private static readonly string[] NegativeNames = { "negative_prompt", "negative", "negative_text", "text_l" };

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
    public static ComfyUiWorkflowSlots Detect(string apiWorkflowJson)
    {
        var root = JsonNode.Parse(apiWorkflowJson) as JsonObject
            ?? throw new InvalidOperationException("工作流不是 JSON 对象");
        return Detect(root);
    }

    public static ComfyUiWorkflowSlots Detect(JsonObject apiWorkflow)
    {
        ArgumentNullException.ThrowIfNull(apiWorkflow);
        var slots = new ComfyUiWorkflowSlots();

        ResolvePrompt(apiWorkflow, PositiveNames, "正向提示词", positive: true, slots);
        ResolvePrompt(apiWorkflow, NegativeNames, "负面词", positive: false, slots);
        ResolveSize(apiWorkflow, slots);
        ResolveAspect(apiWorkflow, slots);
        ResolveSeeds(apiWorkflow, slots);
        ResolveImage(apiWorkflow, slots);
        ResolveLength(apiWorkflow, slots);

        if (!slots.CanTextToImage && !slots.IsFrameDriven)
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
                Assign(slots, positive, candidate.NodeId, candidate.Input);
                NoteAmbiguity(slots, label, ordered, candidate);
                return;
            }

            if (ReadLink(candidate.Inputs, candidate.Input) is not { } link)
            {
                reasons.Add($"{candidate.Input}（节点 {candidate.NodeId}）的形状看不懂");
                continue;
            }

            if (graph[link.NodeId] is not JsonObject source || source["inputs"] is not JsonObject sourceInputs)
            {
                reasons.Add($"{candidate.Input} 指向的节点 {link.NodeId} 不在图里");
                continue;
            }

            var type = ClassTypeOf(source);

            // 「这份工作流不要这一路」的正规写法（ConditioningZeroOut）。
            // 算成缺陷的话，界面上会留一个用户既改不了、也不需要改的假问题。
            if (IsDeliberatelyEmpty(type))
            {
                if (!positive) slots.NegativeDeliberatelyEmpty = true;
                slots.Notes.Add($"{(positive ? "正向提示词" : "负面词")}这一路是显式置空的（{type}，节点 {link.NodeId}）："
                    + "这份工作流不需要它，填什么都不生效。");
                return;
            }

            // 跳一跳：文字往往挂在被指向的那个节点上。
            var target = WritableTextNames.FirstOrDefault(name => sourceInputs[name] is JsonValue);
            if (target is null)
            {
                reasons.Add($"{candidate.Input}（节点 {candidate.NodeId}）指向 {type}（节点 {link.NodeId}），"
                    + "那里没有可直接写的文字输入");
                continue;
            }

            Assign(slots, positive, link.NodeId, target);
            slots.Notes.Add($"{label}写在节点 {link.NodeId}（{type}）的 {target} 上："
                + $"它是由节点 {candidate.NodeId} 的 {candidate.Input} 引用过来的。");
            NoteAmbiguity(slots, label, ordered, candidate);
            return;
        }

        foreach (var reason in reasons) slots.Notes.Add($"没找到{label}：{reason}。");
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
    /// 只取一个（按输入名的明确程度、再按节点 id），并且**记下它当前那个值**：
    /// 合法选项清单在服务端，工作流里只有选中的那一个，所以我们只能照它的写法造新值。
    /// 造不出来时（写法不认识）由 <see cref="VideoShape.FormatAspect"/> 返回空，界面那一刻如实说改不了。
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

        if (!VideoShape.TryParseRatio(chosen.Value, out _, out _))
            slots.Notes.Add($"节点 {chosen.NodeId} 的 {chosen.Input} 是比例，但它的值「{chosen.Value}」不是 a:b 这种写法："
                + "我们不认识它的选项格式，比例这一项改不了（要改请在那份工作流里改）。");
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
            slots.Notes.Add("没找到种子输入：多张之间可能出一模一样的几张，得靠服务端自己决定。");
        else if (slots.SeedNodeIds.Count > 1)
            slots.Notes.Add($"这份工作流有 {slots.SeedNodeIds.Count} 段采样（节点 {string.Join("、", slots.SeedNodeIds)}）："
                + "同一张图会往每一段写同一个种子。");
    }

    /// <summary>认底图入口：全部都认，按节点 id 稳定排序。</summary>
    private static void ResolveImage(JsonObject graph, ComfyUiWorkflowSlots slots)
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
            slots.Notes.Add("没找到底图入口（LoadImage）：这份工作流用不了参考图，只能文生图。");
            return;
        }

        slots.ImageNodeId = loaders[0].Key;
        slots.ImageInput = "image";
        slots.ImageNodeIds.AddRange(loaders.Select(pair => pair.Key));
        slots.ImageGroups.AddRange(GroupByOutput(graph, slots.ImageNodeIds));
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
    /// 把入口按「能通到哪个输出节点」分组（并查集：共用同一个输出的算一组）。
    ///
    /// 为什么要按**输出**分：合集型的工作流（`B03` 那种「单双三图」并排三档）里，三组各自走向
    /// 自己的 `SaveImage`，组与组之间在图上是连通的、没有任何开关——只有顺着连线走到输出才分得开。
    ///
    /// **分不清就退回一整组**（等于原先的平铺行为），不硬分：只要有一个入口走不到任何保存节点，
    /// 说明这份的图没接进产出、或输出节点不是保存类，这时分组没有依据。
    /// </summary>
    private static List<List<string>> GroupByOutput(JsonObject graph, IReadOnlyList<string> entries)
    {
        var sinks = entries.ToDictionary(entry => entry, entry => SaveNodesUnder(graph, entry), StringComparer.Ordinal);
        if (sinks.Values.Any(set => set.Count == 0)) return new List<List<string>> { entries.ToList() };

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
            SetInput(graph, slots.PositiveNodeId, slots.PositiveInput, JsonValue.Create(values.Prompt));

        if (slots.NegativeNodeId.Length > 0)
            SetInput(graph, slots.NegativeNodeId, slots.NegativeInput, JsonValue.Create(values.Negative));

        if (slots.LatentNodeId.Length > 0)
        {
            if (values.Width > 0) SetInput(graph, slots.LatentNodeId, "width", JsonValue.Create(values.Width));
            if (values.Height > 0) SetInput(graph, slots.LatentNodeId, "height", JsonValue.Create(values.Height));
            // 出多张时 batch_size 交给工作流自己：它是模板作者的决定（有的工作流靠它一次出多张）。
        }

        // 比例：只有那种「固定选项」的输入才写，而且**照它自己当前那个值的写法**造新值。
        // 造不出来（"adaptive" 这种）就不写——写一个它不认识的字符串，换来的是服务端的一次报错。
        if (slots.AspectNodeId.Length > 0 && values.AspectRatio.Length > 0)
        {
            var text = VideoShape.FormatAspect(slots.AspectCurrent, values.AspectRatio);
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
            SetInput(graph, imageSlots[index], imageInput, JsonValue.Create(imageNames[index]));
        }

        return graph;
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
}
