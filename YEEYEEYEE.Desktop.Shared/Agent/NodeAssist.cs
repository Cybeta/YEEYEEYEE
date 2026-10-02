namespace YEEYEEYEE.Desktop;

/// <summary>
/// 沿连线收集到的一条上游设定（角色卡 / 场景卡 / 章节…）。
/// Depth 是离目标节点的层数：1 = 直接连过来的，数字越大越远。
/// </summary>
public sealed record NodeAssistSource(
    Guid NodeId,
    string Title,
    NodeCategory Category,
    string KindLabel,
    string Content,
    int Depth);

/// <summary>
/// 素材的来源层级。**枚举顺序就是优先级**：算提示词时越靠前越重要。
///
/// 为什么本体设定排第一：对一张角色卡来说，决定出图的是「这个角色长什么样」（角色设定），
/// 而不是「这一章在讲什么」。只喂上游与本，出来的会是一段场景描写，不是角色提示词。
/// </summary>
public enum NodeAssistTier
{
    /// <summary>本体：节点自己引用的设定（角色 / 场景 / 道具的设定描述，也就是外观锚点）。</summary>
    Setting,

    /// <summary>上游：沿入边向上收集到的节点（分镜 ← 角色 / 场景，再往上到章节、企划）。</summary>
    Upstream,

    /// <summary>同镜：与本节点共用一个上游的兄弟（同一个分镜里的场景与道具，或同一章里的其他镜头）。</summary>
    Sibling,

    /// <summary>所属章节：章节工作树条目的叙事正文。它给的是情境，不是主体。</summary>
    Chapter
}

/// <summary>
/// 一条拼进提示词的素材。四条来源合到一起，<see cref="Tier"/> 决定它在提示词里的位置与说法。
/// </summary>
public sealed record NodeAssistMaterial(
    NodeAssistTier Tier,
    Guid? NodeId,
    Guid? WorkTreeItemId,
    string Title,
    NodeCategory Category,
    string KindLabel,
    string Content,
    int Depth)
{
    public bool HasContent => Content.Length > 0;

    public static string TierLabel(NodeAssistTier tier) => tier switch
    {
        NodeAssistTier.Setting => "本体设定",
        NodeAssistTier.Upstream => "上游设定",
        NodeAssistTier.Sibling => "同镜素材",
        _ => "所属章节"
    };
}

/// <summary>一条「Agent 协助」建议：菜单上的一行 + 它要用的提示词。</summary>
public sealed record NodeAssistSuggestion(
    string Id,
    string Title,
    NodeAssistKind Kind,
    string SkillId,
    string Prompt,
    string NegativePrompt,
    string Blocked)
{
    /// <summary>现在能不能跑；不能跑时 Blocked 里写着原因。</summary>
    public bool CanRun => Blocked.Length == 0;
}

/// <summary>这条建议要做的事：只给提示词、交给 Agent 跑、还是直接调图像接口。</summary>
public enum NodeAssistKind
{
    /// <summary>只生成提示词，用户自己复制走。</summary>
    Prompt,

    /// <summary>交给 Agent 面板执行（会产出节点 / 内容）。</summary>
    Agent,

    /// <summary>直接调图像链路出图，结果挂到节点上。</summary>
    Image,

    /// <summary>调出视频链路。**执行方当前尚未接入**，菜单上会如实标出这一条现在跑不了。</summary>
    Video
}

/// <summary>一个节点的协助计划：上游看清楚了什么 + 能做什么。</summary>
public sealed class NodeAssistPlan
{
    public IReadOnlyList<NodeAssistSource> Sources { get; init; } = Array.Empty<NodeAssistSource>();

    /// <summary>
    /// 拼提示词用的全部素材（本体设定 + 上游 + 同镜 + 所属章节）。
    /// 与 <see cref="Sources"/> 的区别：那个只是**连线上游**，用于「查看上游设定」窗口；
    /// 这个才是算提示词的口径——只有连线的话，角色 / 场景节点会因为自己是叶子而收不到任何素材。
    /// </summary>
    public IReadOnlyList<NodeAssistMaterial> Materials { get; init; } = Array.Empty<NodeAssistMaterial>();

    /// <summary>给菜单顶部那一行用：「素材：4 条 · 上游 4 条（角色 2 · 章节 1 · 企划 1）· 最远 3 层」。</summary>
    public string ContextSummary { get; init; } = string.Empty;

    /// <summary>格式化好的素材块，直接拼进提示词。</summary>
    public string ContextText { get; init; } = string.Empty;

    public IReadOnlyList<NodeAssistSuggestion> Suggestions { get; init; } = Array.Empty<NodeAssistSuggestion>();

    public bool HasUpstream => Sources.Count > 0;
}

/// <summary>
/// 「右键 → Agent 协助」的内容由这里算出来：**先沿入边把上游设定收齐，再按节点类型给建议与提示词**。
///
/// 为什么把规则放在共享的 UI-free 代码里：菜单文字、提示词模板、上游收集顺序都要能被测试固定住，
/// 而且旧端将来接同一套建议时不该再抄一份。这里只依赖画布模型（Core），不碰界面、不碰网络。
/// </summary>
public static class NodeAssistPlanner
{
    /// <summary>默认向上追溯的层数：够把「分镜 ← 场景/角色 ← 章节/企划」串起来，又不会把整张图都拖进提示词。</summary>
    public const int DefaultMaxDepth = 3;

    /// <summary>每条上游设定截断到多少字：提示词是要拿去出图的，塞太多会淹没主体。</summary>
    private const int SourceContentLimit = 160;

    /// <summary>同镜兄弟最多收几条：同一个分镜里的场景与道具够用就行。</summary>
    private const int SiblingLimit = 6;

    /// <summary>章节正文截断到多少字：章节可以很长，全塞进提示词会把主体淹掉。</summary>
    private const int ChapterContentLimit = 200;

    /// <summary>
    /// 沿**入边**（Target = 自己）向上收集设定。广度优先、按层数从近到远，
    /// 同一层按「角色 → 场景 → 道具 → 其它」排，保证菜单与提示词每次一致；
    /// 用已访问集合挡环（画布允许连成环，收集不能因此转不出来）。
    ///
    /// 这是**连线**那一路来源；引用、同镜、章节由 <see cref="CollectMaterial"/> 补齐。
    /// </summary>
    public static IReadOnlyList<NodeAssistSource> CollectUpstream(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        int maxDepth = DefaultMaxDepth)
    {
        var result = new List<NodeAssistSource>();
        var visited = new HashSet<Guid> { node.Id };
        var frontier = new List<Guid> { node.Id };

        for (var depth = 1; depth <= Math.Max(1, maxDepth) && frontier.Count > 0; depth++)
        {
            var next = new List<WorkflowNode>();
            foreach (var id in frontier)
            {
                foreach (var edge in canvas.Edges.Where(edge => edge.TargetNodeId == id))
                {
                    var source = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == edge.SourceNodeId);
                    if (source is null || !visited.Add(source.Id)) continue;
                    next.Add(source);
                }
            }

            foreach (var source in next.OrderBy(item => CategoryOrder(item.Category)).ThenBy(item => item.Title, StringComparer.Ordinal))
            {
                result.Add(new NodeAssistSource(
                    source.Id,
                    source.Title,
                    source.Category,
                    KindLabelOf(source.Category),
                    Trim(source.Content, SourceContentLimit),
                    depth));
            }

            frontier = next.Select(item => item.Id).ToList();
        }

        return result;
    }

    /// <summary>
    /// 收集拼提示词用的全部素材：**本体设定 + 上游 + 同镜 + 所属章节**，按优先级排序返回。
    ///
    /// 为什么不是只有「上游」：早先只沿入边收，而角色 / 场景节点在画布上通常是**叶子**（没有入边），
    /// 于是收集结果为空、整排提示词按钮被标成「没有素材」；更要紧的是角色的真正描写根本不在节点上，
    /// 而在它引用的设定变体里——不把引用算进来，生成的就不是角色提示词。
    /// </summary>
    public static IReadOnlyList<NodeAssistMaterial> CollectMaterial(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        var result = new List<NodeAssistMaterial>();
        var seenNodes = new HashSet<Guid> { node.Id };
        var seenReferences = new HashSet<string>(StringComparer.Ordinal);

        // ① 本体：节点自己引用的设定（角色 / 场景 / 道具的设定描述）。失效引用照样记一条，但写明原因。
        foreach (var reference in node.References)
        {
            var content = canvas.ResolveReferenceContent(reference);
            if (content is null)
            {
                var key = $"broken:{reference.EntityId:N}:{reference.VariantId:N}";
                if (!seenReferences.Add(key)) continue;
                var label = ContentLabelOf(reference);
                result.Add(new NodeAssistMaterial(
                    NodeAssistTier.Setting, null, null, "（已失效的引用）", NodeCategory.General, label,
                    "已失效：引用指向的设定已经不存在了，先修好这条引用再生成", 0));
                continue;
            }

            if (!seenReferences.Add($"{content.Entity.Id:N}:{content.Variant.Id:N}")) continue;
            result.Add(new NodeAssistMaterial(
                NodeAssistTier.Setting,
                null,
                null,
                $"{content.Entity.Name} · {content.Variant.Name}",
                NodeKindPalette.CategoryOf(content.Entity.Kind),
                WorkflowEntity.KindName(content.Entity.Kind),
                Trim(content.Description, SourceContentLimit),
                0));
        }

        // ② 上游：沿入边向上（这一层沿用原来的收集规则，连线的语义没变）。
        var upstream = CollectUpstream(canvas, node, maxDepth);
        var upstreamIds = new HashSet<Guid>();
        foreach (var source in upstream)
        {
            if (!seenNodes.Add(source.NodeId)) continue;
            upstreamIds.Add(source.NodeId);
            result.Add(new NodeAssistMaterial(
                NodeAssistTier.Upstream, source.NodeId, null, source.Title, source.Category,
                source.KindLabel, source.Content, source.Depth));
        }

        // ③ 同镜：与本节点共用一个上游的兄弟。一个分镜里的角色、场景、道具互为兄弟——
        //    生成角色提示词时，同镜的场景与道具正好是它的情境。
        foreach (var sibling in Siblings(canvas, node, upstreamIds, seenNodes))
        {
            seenNodes.Add(sibling.Id);
            result.Add(new NodeAssistMaterial(
                NodeAssistTier.Sibling, sibling.Id, null, sibling.Title, sibling.Category,
                KindLabelOf(sibling.Category), Trim(sibling.Content, SourceContentLimit), 0));
        }

        // ④ 所属章节：走稳定章节 ID，不按章节名文本猜。给的是情境。
        if (CanvasChapters.ResolveChapterId(canvas, node) is { } chapterId
            && canvas.WorkTree.FirstOrDefault(item => item.Id == chapterId) is { } chapter)
        {
            result.Add(new NodeAssistMaterial(
                NodeAssistTier.Chapter, null, chapter.Id, chapter.Name, NodeCategory.Chapter, "章节",
                Trim(chapter.Prompt, ChapterContentLimit), 0));
        }

        return result;
    }

    /// <summary>与本节点共用一个上游的兄弟节点（不含自己、不含已经收过的）。</summary>
    private static IEnumerable<WorkflowNode> Siblings(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        HashSet<Guid> upstreamIds,
        HashSet<Guid> seenNodes)
    {
        var parents = canvas.Edges.Where(edge => edge.TargetNodeId == node.Id)
            .Select(edge => edge.SourceNodeId)
            .ToHashSet();
        if (parents.Count == 0) return Array.Empty<WorkflowNode>();

        var siblingIds = canvas.Edges
            .Where(edge => parents.Contains(edge.SourceNodeId) && edge.TargetNodeId != node.Id)
            .Select(edge => edge.TargetNodeId)
            .Where(id => !upstreamIds.Contains(id) && !seenNodes.Contains(id))
            .Distinct()
            .ToHashSet();

        return canvas.Nodes
            .Where(candidate => siblingIds.Contains(candidate.Id))
            .OrderBy(candidate => CategoryOrder(candidate.Category))
            .ThenBy(candidate => candidate.Title, StringComparer.Ordinal)
            .Take(SiblingLimit);
    }

    /// <summary>失效引用连实体都找不到了：只能记一条「引用」并写明原因。</summary>
    private static string ContentLabelOf(NodeReference reference) => "引用";

    /// <summary>把素材拼成一段稳定的文字块（提示词里用它，不额外调模型）。</summary>
    public static string FormatContext(IReadOnlyList<NodeAssistMaterial> materials)
    {
        if (materials.Count == 0) return string.Empty;

        var lines = new List<string> { "素材（按优先级：本体设定 > 上游 > 同镜 > 所属章节）：" };
        foreach (var tier in new[] { NodeAssistTier.Setting, NodeAssistTier.Upstream, NodeAssistTier.Sibling, NodeAssistTier.Chapter })
        {
            var group = materials.Where(material => material.Tier == tier).ToList();
            if (group.Count == 0) continue;
            lines.Add($"[{NodeAssistMaterial.TierLabel(tier)}]");
            foreach (var material in group)
            {
                var content = material.HasContent ? material.Content : "（还没写内容）";
                var depth = material.Depth > 1 ? $"（上 {material.Depth} 层）" : string.Empty;
                lines.Add($"- {material.KindLabel}「{material.Title}」{depth}：{content}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>按节点类型算这一份计划（素材 + 可跑的建议 + 每条建议的提示词）。</summary>
    public static NodeAssistPlan BuildPlan(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        int maxDepth = DefaultMaxDepth)
    {
        var sources = CollectUpstream(canvas, node, maxDepth);
        var materials = CollectMaterial(canvas, node, maxDepth);
        var contextText = FormatContext(materials);
        var summary = DescribeMaterials(materials);
        var hasMaterial = !string.IsNullOrWhiteSpace(node.Content)
            || materials.Any(material => material.HasContent);

        // 没内容又没素材时，任何需要素材的建议都是空转：明说原因，菜单上灰着而不是点了没反应。
        var blocked = hasMaterial
            ? string.Empty
            : "这个节点还没写内容，也没有引用或上游设定；先写点内容、挂一条引用，或者连一条上游节点，再来让 Agent 协助";

        var suggestions = new List<NodeAssistSuggestion>();
        switch (node.Category)
        {
            case NodeCategory.Character:
                suggestions.Add(Image(node, contextText, blocked, "character-front", "出角色图（正面全身）", "正面全身，站姿，全身入镜"));
                suggestions.Add(Image(node, contextText, blocked, "character-bust", "出角色图（半身特写）", "半身特写，面部清晰，肩部以上"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "character-sheet", "生成角色设定提示词（外观锚点）", "角色设定表，含外观锚点、服装、神态与常用道具，按正面/侧面/四分之三/半身/全身分组描述"));
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "character-generation", "让 Agent 补全角色小传"));
                break;

            case NodeCategory.Scene:
                suggestions.Add(Image(node, contextText, blocked, "scene-wide", "出场景基准图（远景宽幅）", "远景宽幅，交代空间关系与光线，无主要人物"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "scene-sheet", "生成场景设定提示词（远中近景）", "场景设定，含时间天气、光线方向、材质细节，并给出远/中/近三档构图"));
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "scene-generation", "让 Agent 补全场景设定"));
                break;

            case NodeCategory.Prop:
                suggestions.Add(Image(node, contextText, blocked, "prop-closeup", "出道具特写图", "道具特写，居中，干净背景，材质与磨损细节清晰"));
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "prop-generation", "让 Agent 补全道具设定"));
                break;

            case NodeCategory.Storyboard:
                suggestions.Add(Image(node, contextText, blocked, "storyboard-frame", "出这一镜的画面", "电影感单帧：按镜头描述构图，人物与场景沿用素材设定，注意景别与光线方向"));
                // 出视频：先如实摆出来，再如实说它现在跑不了。
                // 藏起来的话，用户会以为这个应用根本没有出视频这条路——而站点里明明已经导进视频池子了。
                suggestions.Add(Video(node, contextText, "storyboard-video", "出这一镜的视频",
                    "把这一镜拍成一段镜头：以这一镜的画面为首帧，人物与场景沿用素材设定，一个镜头内完成主体动作"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "storyboard-sheet", "生成镜头提示词（含景别与运镜）", "把这一个镜头写成可执行的出图提示词：景别、主体动作、环境、光线、画幅与运镜备注"));
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "storyboard-generation", "让 Agent 把这一镜拆细"));
                break;

            case NodeCategory.Chapter:
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "chapter-decomposition", "让 Agent 把这一章拆成分镜与角色/场景节点"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "chapter-summary", "生成章节梗概提示词", "把这一章压成一段可直接喂给模型的梗概：主线事件、人物动机、转折与结尾钩子"));
                break;

            case NodeCategory.StoryPlan:
            case NodeCategory.StoryOutline:
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "story-generation", "让 Agent 生成剧情（选题 / 冲突 / 钩子）"));
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "storyboard-generation", "让 Agent 生成分镜表"));
                break;

            case NodeCategory.Product:
                suggestions.Add(Video(node, contextText, "product-video", "出这一版的成片视频",
                    "把这一版做成成片：按分镜顺序串起每一镜的视频，保持人物与场景一致"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "product-note", "生成成品说明提示词", "描述这一版成品与前一版的差别、采纳理由与后续修改点"));
                break;

            default:
                suggestions.Add(Agent(node, hasMaterial, contextText, blocked, "story-generation", "让 Agent 按这段内容续写"));
                suggestions.Add(PromptOnly(node, contextText, blocked, "generic-visual", "生成画面提示词", "把这段内容写成一个画面的提示词：主体、动作、环境、光线、画幅"));
                break;
        }

        return new NodeAssistPlan
        {
            Sources = sources,
            Materials = materials,
            ContextSummary = summary,
            ContextText = contextText,
            Suggestions = suggestions
        };
    }

    /// <summary>
    /// 摘要：一眼看出「这条提示词是靠什么算出来的」。各层各几条，上游再按种类拆开；
    /// 这是界面上唯一说明素材来源的一行，所以不能只报总数。
    /// </summary>
    private static string DescribeMaterials(IReadOnlyList<NodeAssistMaterial> materials)
    {
        if (materials.Count == 0) return "素材：没有（这个节点没写内容，也没有引用或上游连线）";

        var parts = new List<string>();
        foreach (var tier in new[] { NodeAssistTier.Setting, NodeAssistTier.Upstream, NodeAssistTier.Sibling, NodeAssistTier.Chapter })
        {
            var group = materials.Where(material => material.Tier == tier).ToList();
            if (group.Count == 0) continue;

            if (tier == NodeAssistTier.Upstream)
            {
                var kinds = group.GroupBy(material => material.KindLabel)
                    .OrderByDescending(kind => kind.Count())
                    .ThenBy(kind => kind.Key, StringComparer.Ordinal)
                    .Select(kind => $"{kind.Key} {kind.Count()}");
                parts.Add($"上游 {group.Count} 条（{string.Join(" · ", kinds)}）");
                continue;
            }

            parts.Add($"{NodeAssistMaterial.TierLabel(tier)} {group.Count} 条");
        }

        var deepest = materials.Max(material => material.Depth);
        var deepestText = deepest > 1 ? $" · 最远 {deepest} 层" : string.Empty;
        return $"素材：{materials.Count} 条 · {string.Join(" · ", parts)}{deepestText}";
    }

    private static NodeAssistSuggestion Image(
        WorkflowNode node,
        string contextText,
        string blocked,
        string id,
        string title,
        string shot)
    {
        var prompt = ComposeShotPrompt(node, contextText, shot);
        return new NodeAssistSuggestion(id, title, NodeAssistKind.Image, "image-generation", prompt, NegativeFor(node), blocked);
    }

    private static NodeAssistSuggestion PromptOnly(
        WorkflowNode node,
        string contextText,
        string blocked,
        string id,
        string title,
        string ask)
    {
        var prompt = ComposeTextPrompt(node, contextText, ask);
        return new NodeAssistSuggestion(id, title, NodeAssistKind.Prompt, string.Empty, prompt, NegativeFor(node), blocked);
    }

    /// <summary>
    /// 出视频那一条。**恒定不可执行**：执行方还没接入。
    ///
    /// 为什么照样列在菜单里：站点下面确实能导进视频池子，菜单里一个字都没有的话，
    /// 用户会以为「这个应用不支持出视频」，而不是「代码还没接完」——前者会让他去找别的工具。
    /// 说明写清楚，他就知道东西已经备好了、只等执行方。
    /// </summary>
    private static NodeAssistSuggestion Video(
        WorkflowNode node,
        string contextText,
        string id,
        string title,
        string shot) =>
        new(id, title, NodeAssistKind.Video, "video-generation",
            ComposeShotPrompt(node, contextText, shot), NegativeFor(node),
            "出视频执行方还没接入：技能与站点的视频池子都已经能导入，但真正发请求的那一段还没写。"
            + "这一条现在跑不了，先按上面的「出图」把每一镜的底图做出来，接入后可以直接出视频。");

    private static NodeAssistSuggestion Agent(
        WorkflowNode node,
        bool hasMaterial,
        string contextText,
        string blocked,
        string skillId,
        string title)
    {
        var prompt = ComposeAgentInstruction(node, hasMaterial, contextText, title);
        return new NodeAssistSuggestion(skillId + "-ask", title, NodeAssistKind.Agent, skillId, prompt, string.Empty, blocked);
    }

    private const string DefaultNegative = "低清，模糊，多余的手指，变形的手，多余肢体，文字水印，logo，杂乱背景，过度磨皮";

    /// <summary>
    /// 负面提示词按节点种类取。角色要挡「多余手指 / 五官不对称 / 换装」，场景要挡「死平光 / 光源不明 /
    /// 透视错乱」，两边要避的东西不一样，用一份通用清单等于两边都没挡住。没有基线的种类才用通用那一条。
    /// </summary>
    private static string NegativeFor(WorkflowNode node) =>
        PromptBaseline.HasBaseline(node.Category) ? PromptBaseline.NegativeFor(node.Category) : DefaultNegative;

    /// <summary>出图用的提示词：主体（节点标题）+ 节点内容（外观锚点）+ 上游设定 + 这一镜怎么拍。</summary>
    public static string ComposeShotPrompt(
        WorkflowNode node,
        string contextText,
        string shot)
    {
        var builder = new List<string> { $"{KindLabelOf(node.Category)}：{node.Title}" };

        if (!string.IsNullOrWhiteSpace(node.Content))
            builder.Add($"设定：{Flatten(node.Content)}");

        if (contextText.Length > 0) builder.Add(contextText);

        if (!string.IsNullOrWhiteSpace(node.Chapter))
            builder.Add($"所属章节：{node.Chapter}");

        builder.Add($"画面要求：{shot}");
        return string.Join("\n", builder);
    }

    /// <summary>文字类提示词：让模型基于同样的素材产出设定 / 梗概，而不是凭空编。</summary>
    private static string ComposeTextPrompt(
        WorkflowNode node,
        string contextText,
        string ask)
    {
        var builder = new List<string>
        {
            $"节点：{KindLabelOf(node.Category)}「{node.Title}」"
        };
        if (!string.IsNullOrWhiteSpace(node.Content)) builder.Add($"已有内容：{Flatten(node.Content)}");
        if (contextText.Length > 0) builder.Add(contextText);
        if (!string.IsNullOrWhiteSpace(node.Chapter)) builder.Add($"所属章节：{node.Chapter}");
        builder.Add($"请产出：{ask}");
        if (PromptBaseline.HasBaseline(node.Category))
            builder.Add($"请按这些小标题分段写全：{PromptBaseline.SectionList(node.Category)}"
                + "（缺段会让后续镜头对不上，属性要写具体值，不要写「正常」「普通」这种等于没写的词）。");
        builder.Add("要求：只写这一段内容本身，不要解释你的做法；以【本体设定】为准描述主体，【上游 / 同镜 / 所属章节】用来补情境，冲突时以本体设定为准。");
        return string.Join("\n", builder);
    }

    /// <summary>交给 Agent 面板的指令：带上节点、素材与要用的技能，让 Agent 去产出 actions。</summary>
    private static string ComposeAgentInstruction(
        WorkflowNode node,
        bool hasMaterial,
        string contextText,
        string request)
    {
        var builder = new List<string>
        {
            $"针对画布上的节点「{node.Title}」（类型：{KindLabelOf(node.Category)}）：{request}。"
        };
        if (!string.IsNullOrWhiteSpace(node.Content)) builder.Add($"节点现有内容：{Flatten(node.Content)}");
        if (contextText.Length > 0) builder.Add(contextText);
        if (!hasMaterial) builder.Add("注意：这个节点没有引用也没有上游连线，缺少参考设定时请先按现有内容给出最小可用结果，不要编造与已有设定冲突的内容。");
        if (PromptBaseline.HasBaseline(node.Category))
            builder.Add($"写回节点正文时必须按这些小标题分段写全：{PromptBaseline.SectionList(node.Category)}，"
                + $"末尾另起一段「出图提示词」并附「负面提示词」（负面可参考：{PromptBaseline.NegativeFor(node.Category)}）。");
        return string.Join("\n", builder);
    }

    public static string KindLabelOf(NodeCategory category) => category switch
    {
        NodeCategory.Chapter => "章节",
        NodeCategory.Character => "角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Prop => "道具",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Product => "成品",
        NodeCategory.StoryPlan => "故事企划",
        NodeCategory.StoryOutline => "故事大纲",
        _ => "通用"
    };

    /// <summary>同一层里的排序优先级：人 → 地 → 物 → 其它。提示词里先给角色，模型更容易抓住主体。</summary>
    private static int CategoryOrder(NodeCategory category) => category switch
    {
        NodeCategory.Character => 0,
        NodeCategory.Scene => 1,
        NodeCategory.Prop => 2,
        NodeCategory.Storyboard => 3,
        NodeCategory.StoryPlan => 4,
        NodeCategory.StoryOutline => 5,
        NodeCategory.Chapter => 6,
        NodeCategory.Product => 7,
        _ => 8
    };

    private static string Flatten(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Trim();

    private static string Trim(string text, int limit)
    {
        var flat = Flatten(text);
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }
}
