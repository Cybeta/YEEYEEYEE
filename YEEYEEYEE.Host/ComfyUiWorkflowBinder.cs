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

    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(PositiveNodeId.Length > 0 ? "提示词✓" : "提示词✗");
        parts.Add(NegativeNodeId.Length > 0 ? "负面词✓" : "负面词✗");
        parts.Add(CanResize ? "画幅✓" : "画幅✗");
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
        ResolveSeeds(apiWorkflow, slots);
        ResolveImage(apiWorkflow, slots);
        NoteVideoLength(apiWorkflow, slots);

        if (!slots.CanTextToImage)
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
            .OrderBy(item => ClassTypeOf(item.Node).Contains("Latent", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.Id, NodeIdComparer.Instance)
            .ToList();

        if (carriers.Count == 0)
        {
            slots.Notes.Add("没找到带 width / height 的节点：出图尺寸沿用这份工作流自己的设置。");
            return;
        }

        var (id, node) = carriers[0];
        var inputs = node["inputs"]!.AsObject();
        if (inputs["width"] is JsonValue && inputs["height"] is JsonValue)
        {
            slots.LatentNodeId = id;
            return;
        }

        slots.Notes.Add(DescribeComputedSize(graph, inputs, id, ClassTypeOf(node)));
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
    /// 视频长度：帧数由上游节点算出来时如实说明。**这一项我们不动**——
    /// 帧数怎么换算成秒是这份工作流自己的约定（实测是 a*24+1 这种表达式），
    /// 按我们的秒数去写它的帧数，等于替它改约定。
    /// </summary>
    private static void NoteVideoLength(JsonObject graph, ComfyUiWorkflowSlots slots)
    {
        var frames = graph.FirstOrDefault(pair => pair.Value is JsonObject node
            && node["inputs"] is JsonObject inputs
            && inputs["num_frames"] is not null);
        if (frames.Value is not JsonObject frameNode || frameNode["inputs"] is not JsonObject frameInputs) return;

        var source = ReadLink(frameInputs, "num_frames");
        if (source is { } origin && graph[origin.NodeId] is JsonObject computer && computer["inputs"] is JsonObject computerInputs)
        {
            slots.Notes.Add($"视频长度由节点 {origin.NodeId}（{ClassTypeOf(computer)}）算出来"
                + DescribeLiterals(computerInputs, new[] { "expression", "value", "a" })
                + "：帧数沿用这份工作流自己的约定，我们不按秒去改它。");
            return;
        }

        slots.Notes.Add($"视频帧数写在节点 {frames.Key} 的 num_frames 上"
            + DescribeLiterals(frameInputs, new[] { "num_frames" })
            + "：沿用这份工作流自己的设定。");
    }

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

    /// <summary>已上传到 ComfyUI 的底图文件名；空表示这次不用底图。</summary>
    public string ImageName { get; init; } = string.Empty;

    /// <summary>
    /// 这次要喂的底图文件名，按顺序对到每个底图入口（`ImageName` 是它的第 1 个）。
    /// 参考图的顺序由装配那一侧定死，这里不重排。
    /// </summary>
    public IReadOnlyList<string> ImageNames { get; init; } = Array.Empty<string>();
}
