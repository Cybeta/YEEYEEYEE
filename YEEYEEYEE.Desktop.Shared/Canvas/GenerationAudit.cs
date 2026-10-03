namespace YEEYEEYEE.Desktop;

/// <summary>
/// 生成链上的四层，**枚举顺序就是依赖顺序**：
///
/// 设定图（角色 / 场景 / 道具）→ 分镜图 → 分镜视频 → 成品视频。
///
/// 为什么要有这么一条链：上一层的产物是下一层的输入。第 3 张分镜图在角色还没出图时也能出来一张，
/// 但那张脸是模型现编的，跟第 1 张对不上；到出视频时更是每一镜都在换人。
/// 所以「能不能出下一层」的正确判断不是「这一层有没有内容」，而是「它依赖的那一层有没有产物」。
/// </summary>
public enum GenerationStage
{
    /// <summary>角色 / 场景 / 道具的设定图。整条链的地基。</summary>
    SettingImage,

    /// <summary>每一个分镜的画面。</summary>
    StoryboardImage,

    /// <summary>每一个分镜的视频。</summary>
    StoryboardVideo,

    /// <summary>成品（成片）的视频。</summary>
    ProductVideo
}

/// <summary>
/// 用户点自检时打算做的事。它决定**哪几层会挡路**——只补图时，「分镜视频」那一层缺多少并不挡事；
/// 要出视频时，从设定图到分镜视频三层缺任何一件都会让这次出视频变成「每一镜换一张脸」。
/// </summary>
public enum GenerationIntent
{
    Images,
    Video
}

/// <summary>
/// 自检发现的一处缺口。<see cref="ActionNodeId"/> 是「要动手就该动这个节点」：
/// 为空表示画布上没有能承载它的节点（那种设定只能到引用画廊里补图）。
/// </summary>
public sealed record GenerationAuditItem(
    GenerationStage Stage,
    Guid ActionNodeId,
    string ActionNodeTitle,
    string Target,
    string Reason,
    bool Actionable)
{
    public string Display => ActionNodeTitle.Length == 0 ? Target : $"{ActionNodeTitle} · {Target}";

    public string ActionHint => ActionNodeId == Guid.Empty ? "引用画廊" : "定位";
}

/// <summary>
/// 一层缺口的汇总。<see cref="TargetCount"/> 是「这一层在范围内一共有多少个该有的产物」，
/// 只报缺几个的话看不出严重程度——缺 1 / 共 2 和缺 1 / 共 40 是两回事。
/// </summary>
public sealed record GenerationAuditLayer(
    GenerationStage Stage,
    string Title,
    string What,
    int TargetCount,
    IReadOnlyList<GenerationAuditItem> Missing,
    bool Executable,
    string ExecutableNote)
{
    public int MissingCount => Missing.Count;
    public bool IsClean => MissingCount == 0;
}

/// <summary>
/// 一次自检的结果。**这是一份只读报告**：列出缺什么、缺多少、要花多少钱，但不生成任何东西。
///
/// 为什么不顺手生成：「按节点把缺的全部补上」是一件要花钱、要等、还会覆盖现有产物的事。
/// 先把清单和花费摆出来让人确认，再接自动生成——否则点一下菜单就悄悄跑掉几十次调用。
/// </summary>
public sealed record GenerationAuditReport(
    Guid RootNodeId,
    string RootTitle,
    string RootKind,
    GenerationIntent Intent,
    IReadOnlyList<GenerationAuditLayer> Layers,
    string Note)
{
    public int MissingCount => Layers.Sum(layer => layer.Missing.Count);

    public bool IsClean => MissingCount == 0;

    /// <summary>会挡路的层：补图时只看设定图，出视频时前三层全看。</summary>
    public IReadOnlyList<GenerationStage> BlockingStages => Intent == GenerationIntent.Video
        ? new[] { GenerationStage.SettingImage, GenerationStage.StoryboardImage, GenerationStage.StoryboardVideo }
        : new[] { GenerationStage.SettingImage };

    /// <summary>
    /// 打开报告时默认该勾上的：**会挡住这次操作的那些**，而且必须是现在真能跑的。
    ///
    /// 不是「一键全补」：用户要出的是第三章的视频，把其它章节缺的图也勾上，等于让他在别的章上花钱。
    /// 「能不能跑」这一条问的是层自己（<see cref="GenerationAuditLayer.Executable"/>）——第 183 轮之前
    /// 视频那两层恒为 false，所以它们从不出现在预勾里；现在它们能跑了，出视频意图下就该一起勾上。
    /// </summary>
    public IReadOnlyList<GenerationAuditItem> DefaultChecked =>
        Layers.Where(layer => layer.Executable && BlockingStages.Contains(layer.Stage))
            .SelectMany(layer => layer.Missing)
            .Where(item => item.Actionable)
            .ToList();

    /// <summary>
    /// 换一个意图（补图 / 出视频）。层与缺口都不变，变的只是「哪几层会挡路」，
    /// 于是对话框里切换意图不需要再去问画布一次。
    /// </summary>
    public GenerationAuditReport WithIntent(GenerationIntent intent) => this with { Intent = intent };

    /// <summary>一句话摘要，给状态栏与对话框标题用。</summary>
    public string Describe()
    {
        if (Note.Length > 0) return Note;
        if (IsClean) return $"「{RootTitle}」的依赖链是齐的：{string.Join(" → ", Layers.Select(layer => $"{layer.Title} {layer.TargetCount}/{layer.TargetCount}"))}";

        var parts = Layers.Where(layer => !layer.IsClean)
            .Select(layer => $"{layer.Title} 缺 {layer.MissingCount}/{layer.TargetCount}");
        return $"「{RootTitle}」缺：{string.Join(" · ", parts)}";
    }

    /// <summary>
    /// 花费预估（报告自带的这一句）。图片那一层能按传进来的单价算；
    /// **视频这里只报段数、不报价**——报价要知道用的是哪个视频池子，而这份报告是纯计算、不知道；
    /// 真正两笔都算的是「一键」那一栏（<see cref="OneClickCost"/>），它拿得到两个池子的单价。
    /// 单价未知时如实说算不出来，不要给一个看起来像报价的 0。
    /// </summary>
    public string EstimateCost(double? unitPrice)
    {
        var imageCount = Layers
            .Where(layer => layer.Stage is GenerationStage.SettingImage or GenerationStage.StoryboardImage)
            .Sum(layer => layer.MissingCount);
        var videoCount = Layers
            .Where(layer => layer.Stage is GenerationStage.StoryboardVideo or GenerationStage.ProductVideo)
            .Sum(layer => layer.MissingCount);

        if (imageCount == 0 && videoCount == 0) return "没有要补的产物。";

        var image = imageCount == 0
            ? string.Empty
            : unitPrice is { } price && price > 0
                ? $"要补 {imageCount} 张图，按单价 {price:0.####} 计，预计 {price * imageCount:0.####}"
                : $"要补 {imageCount} 张图；当前池子没有登记单价，算不出花费";

        var video = videoCount == 0
            ? string.Empty
            : $"另有 {videoCount} 段视频要出；视频的钱在「一键」那一栏里报（它知道你选的是哪个池子）";

        return string.Join("；", new[] { image, video }.Where(part => part.Length > 0)) + "。";
    }

    /// <summary>报告的纯文本版（复制到剪贴板、贴进笔记都直接可用）。</summary>
    public string ToText(double? unitPrice)
    {
        var lines = new List<string>
        {
            $"自检：{RootKind}「{RootTitle}」· 意图：{(Intent == GenerationIntent.Video ? "出视频" : "补图")}",
            Describe(),
            EstimateCost(unitPrice),
            string.Empty
        };

        foreach (var layer in Layers)
        {
            lines.Add($"[{layer.Title}] {layer.What} —— {(layer.IsClean ? $"齐了（共 {layer.TargetCount} 个）" : $"缺 {layer.MissingCount} / 共 {layer.TargetCount}")}");
            if (!layer.Executable) lines.Add($"  （{layer.ExecutableNote}）");
            foreach (var item in layer.Missing)
                lines.Add($"  - {(item.Actionable ? string.Empty : "（不可直接生成）")}{item.Display}：{item.Reason}");
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 生成链自检：**先沿依赖把「该有什么」问清楚，再看「实际有没有」**。
///
/// 这一层刻意不碰网络、不碰界面、不生成任何东西，只读画布与产物引用，
/// 于是它能被测试完整钉住（见 YEEYEEYEE.Agent.Tests 的 GenerationAudit* 用例）。
/// 花钱的动作留给调用方，在用户看过这份报告之后。
/// </summary>
public static class GenerationAudit
{
    /// <summary>沿连线向上追溯的层数：够把「成品 ← 分镜 ← 角色/场景」串起来。</summary>
    public const int MaxDepth = 3;

    private static readonly NodeCategory[] SettingCategories =
    {
        NodeCategory.Character, NodeCategory.Scene, NodeCategory.Prop
    };

    /// <summary>
    /// 对这个节点做一次自检。
    ///
    /// <paramref name="mediaExists"/> 用来判定产物还在不在（默认问资产目录）。测试传假函数进来，
    /// 免得用例依赖本机 assets 目录里有没有文件——那种测试在别人机器上会红。
    /// </summary>
    public static GenerationAuditReport Build(
        WorkflowCanvasState canvas,
        WorkflowNode root,
        GenerationIntent? intent = null,
        Func<string, bool>? mediaExists = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(root);

        var exists = mediaExists ?? AssetStore.Exists;
        var resolvedIntent = intent ?? DefaultIntentFor(root.Category);
        var scope = Scope(canvas, root, out var scopeNote);

        var settings = scope.Where(node => SettingCategories.Contains(node.Category)).ToList();
        var storyboards = scope.Where(node => node.Category == NodeCategory.Storyboard).ToList();
        var products = scope.Where(node => node.Category == NodeCategory.Product).ToList();

        var layers = new List<GenerationAuditLayer>
        {
            SettingImageLayer(canvas, settings, scope, exists),
            StoryboardImageLayer(canvas, storyboards, exists),
            StoryboardVideoLayer(canvas, storyboards, exists),
            ProductVideoLayer(canvas, products, exists)
        };

        return new GenerationAuditReport(
            root.Id,
            root.Title,
            NodeAssistPlanner.KindLabelOf(root.Category),
            resolvedIntent,
            layers,
            scopeNote);
    }

    /// <summary>默认意图：章节 / 成品这两类节点，用户点自检多半是在准备出视频；其余是要图。</summary>
    private static GenerationIntent DefaultIntentFor(NodeCategory category) =>
        category is NodeCategory.Chapter or NodeCategory.Product ? GenerationIntent.Video : GenerationIntent.Images;

    /// <summary>
    /// 自检的范围。「这个节点的依赖链」在四种节点上指的不是同一批东西，
    /// 而范围错一点，报告就变成「漏报」或「把别的章节也算进来」——两种都不能接受。
    /// </summary>
    private static List<WorkflowNode> Scope(WorkflowCanvasState canvas, WorkflowNode root, out string note)
    {
        note = string.Empty;
        var result = new List<WorkflowNode>();

        switch (root.Category)
        {
            case NodeCategory.Chapter:
            {
                // 章节：只认工作树锚点解析出来的成员，不按章节名猜（规则与章节身份层一致）。
                if (CanvasChapters.ResolveChapterId(canvas, root) is not { } chapterId)
                {
                    result.Add(root);
                    note = $"「{root.Title}」还没有稳定章节锚点，无法确定它下面有哪些节点："
                        + "先用「章节同步」建立锚点，再回来自检。";
                    return result;
                }

                foreach (var node in canvas.Nodes)
                {
                    if (node.Id == root.Id) continue;
                    if (CanvasChapters.ResolveChapterId(canvas, node) == chapterId) result.Add(node);
                }
                return result;
            }

            case NodeCategory.Product:
                result.Add(root);
                result.AddRange(Upstream(canvas, root));
                return result;

            case NodeCategory.Storyboard:
                result.Add(root);
                result.AddRange(Upstream(canvas, root));
                result.AddRange(CarriersOfReferences(canvas, root, result));
                return result;

            default:
                result.Add(root);
                // 角色 / 场景 / 道具节点：它引用的子设定（服装、配饰）也各是一张要出的图。
                result.AddRange(Upstream(canvas, root));
                result.AddRange(CarriersOfReferences(canvas, root, result));
                return result;
        }
    }

    /// <summary>沿入边向上收集（含环保护）。上游是「这一镜靠什么拍出来」的第一手线索。</summary>
    private static List<WorkflowNode> Upstream(WorkflowCanvasState canvas, WorkflowNode root)
    {
        var result = new List<WorkflowNode>();
        var visited = new HashSet<Guid> { root.Id };
        var frontier = new List<Guid> { root.Id };

        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<Guid>();
            foreach (var id in frontier)
            {
                foreach (var edge in canvas.Edges.Where(edge => edge.TargetNodeId == id))
                {
                    if (!visited.Add(edge.SourceNodeId)) continue;
                    next.Add(edge.SourceNodeId);
                }
            }

            foreach (var id in next)
            {
                var node = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == id);
                if (node is not null) result.Add(node);
            }

            frontier = next;
        }

        return result;
    }

    /// <summary>
    /// 承载了这个节点引用的角色 / 场景 / 道具节点。
    /// 分镜引用了角色「林晚」，真正出设定图的是画布上那个角色节点——不把它的引用拉到范围里，
    /// 从分镜自检就看不到「林晚还没出图」，而那正是这张分镜最要紧的前置。
    /// </summary>
    private static List<WorkflowNode> CarriersOfReferences(
        WorkflowCanvasState canvas,
        WorkflowNode owner,
        List<WorkflowNode> already)
    {
        var keys = owner.References.Select(reference => (reference.EntityId, reference.VariantId)).ToHashSet();
        if (keys.Count == 0) return new List<WorkflowNode>();

        return canvas.Nodes
            .Where(node => SettingCategories.Contains(node.Category))
            .Where(node => !already.Any(known => known.Id == node.Id))
            .Where(node => node.References.Any(reference => keys.Contains((reference.EntityId, reference.VariantId))))
            .ToList();
    }

    /// <summary>第一层：范围内的角色 / 场景 / 道具节点，各自有没有图。</summary>
    private static GenerationAuditLayer SettingImageLayer(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkflowNode> settings,
        IReadOnlyList<WorkflowNode> scope,
        Func<string, bool> exists)
    {
        var missing = new List<GenerationAuditItem>();

        foreach (var node in settings.Where(node => !HasKind(node.Attachments, AttachmentKind.Image, exists)))
            missing.Add(new GenerationAuditItem(
                GenerationStage.SettingImage,
                node.Id,
                node.Title,
                NodeAssistPlanner.KindLabelOf(node.Category),
                $"「{node.Title}」这个设定还没有图：它的外观锚点没定下来，后面每一镜的脸和景都会各编一套。",
                Actionable: true));

        // 分镜引用到、但画布上没有任何角色 / 场景 / 道具节点承载的设定：只能在引用画廊里补图。
        foreach (var need in GalleryOnlyNeeds(canvas, scope, exists))
            missing.Add(new GenerationAuditItem(
                GenerationStage.SettingImage,
                Guid.Empty,
                string.Empty,
                need,
                $"设定「{need}」被分镜引用了，但画布上没有承载它的角色 / 场景 / 道具节点，"
                + "也没有图：到引用画廊里给这个变体补一张设定图。",
                Actionable: false));

        return new GenerationAuditLayer(
            GenerationStage.SettingImage,
            "设定图",
            "角色 / 场景 / 道具各自的外观锚点",
            settings.Count + missing.Count(item => !item.Actionable),
            missing,
            Executable: true,
            ExecutableNote: "现在就能出：节点右键 → 出图。");
    }

    /// <summary>
    /// 被范围里的节点引用、却没有任何画布节点承载的设定实体（去重、带名字）。
    /// 这些设定的图只能在引用画廊里出，画布上点不到，所以报告里要单独列出来并标明去处。
    /// </summary>
    private static List<string> GalleryOnlyNeeds(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkflowNode> scope,
        Func<string, bool> exists)
    {
        var result = new List<string>();
        var seen = new HashSet<(Guid, Guid)>();

        foreach (var owner in scope)
        foreach (var reference in owner.References)
        {
            if (!seen.Add((reference.EntityId, reference.VariantId))) continue;

            var carried = canvas.Nodes.Any(node =>
                SettingCategories.Contains(node.Category)
                && node.References.Any(item => item.EntityId == reference.EntityId && item.VariantId == reference.VariantId));
            if (carried) continue;

            var content = canvas.ResolveReferenceContent(reference);
            if (content is null)
            {
                result.Add($"（引用失效）{owner.Title} 引用的设定已经不存在，先修好这条引用");
                continue;
            }

            if (HasKind(content.Attachments, AttachmentKind.Image, exists)) continue;
            result.Add($"{content.Entity.Name} · {content.Variant.Name}");
        }

        return result;
    }

    /// <summary>第二层：范围内的分镜，各自有没有画面。</summary>
    private static GenerationAuditLayer StoryboardImageLayer(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkflowNode> storyboards,
        Func<string, bool> exists)
    {
        var missing = storyboards
            .Where(node => !HasKind(node.Attachments, AttachmentKind.Image, exists))
            .Select(node => new GenerationAuditItem(
                GenerationStage.StoryboardImage,
                node.Id,
                node.Title,
                "这一镜的画面",
                $"「{node.Title}」还没出图：没有画面，后面这一镜的视频就没有可用的底图。",
                Actionable: true))
            .ToList();

        return new GenerationAuditLayer(
            GenerationStage.StoryboardImage,
            "分镜图",
            "每一个分镜的画面",
            storyboards.Count,
            missing,
            Executable: true,
            ExecutableNote: "现在就能出：节点右键 →「出这一镜的画面」。");
    }

    /// <summary>
    /// 第三层：范围内的分镜，各自有没有视频。
    ///
    /// 第 183 轮起这一层**可执行**了（出视频执行方接上：提交 → 轮询 → 下载）。
    /// 但这份报告是**纯计算**：它不读配置、不知道这台机器配没配视频接口，
    /// 所以它说的是「这个应用有没有这条能力」，不是「你现在跑不跑得动」；
    /// 真跑不动时由出视频那条链自己如实拒绝（见 <see cref="VideoProviderFactory"/>）。
    /// </summary>
    private static GenerationAuditLayer StoryboardVideoLayer(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkflowNode> storyboards,
        Func<string, bool> exists)
    {
        var missing = storyboards
            .Where(node => !HasKind(node.Attachments, AttachmentKind.Video, exists))
            .Select(node => new GenerationAuditItem(
                GenerationStage.StoryboardVideo,
                node.Id,
                node.Title,
                "这一镜的视频",
                $"「{node.Title}」还没有视频。",
                Actionable: true))
            .ToList();

        return new GenerationAuditLayer(
            GenerationStage.StoryboardVideo,
            "分镜视频",
            "每一个分镜的镜头",
            storyboards.Count,
            missing,
            Executable: true,
            ExecutableNote: VideoHowTo);
    }

    /// <summary>视频那两层可执行时给的那句「怎么出」——与图片层同一副面孔，别让人两头找入口。</summary>
    private const string VideoHowTo = "现在就能出：节点右键 →「出这一镜的视频」（需要先在设置里配好视频接口）。";

    /// <summary>第四层：范围内的成品，各自有没有视频。</summary>
    private static GenerationAuditLayer ProductVideoLayer(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkflowNode> products,
        Func<string, bool> exists)
    {
        var missing = products
            .Where(node => !HasKind(node.Attachments, AttachmentKind.Video, exists))
            .Select(node => new GenerationAuditItem(
                GenerationStage.ProductVideo,
                node.Id,
                node.Title,
                "这一版成片",
                $"「{node.Title}」还没有成片视频。",
                Actionable: true))
            .ToList();

        return new GenerationAuditLayer(
            GenerationStage.ProductVideo,
            "成品视频",
            "成品（成片）的视频",
            products.Count,
            missing,
            Executable: false,
            ExecutableNote: ProductVideoNote);
    }

    /// <summary>
    /// 成片那一层**仍然不可执行**，而这次不是「执行方没接」：出视频接口只会按提示词生成**一段**画面，
    /// 而这一层要的是「把每一镜串起来」。把它标成可执行，用户拿到的就是一段凭空生成的镜头，
    /// 而不是他那一版成片——那正是「不偷偷换成别的东西」这条规矩要挡的。串片那一步还没做。
    /// </summary>
    private const string ProductVideoNote =
        "把每一镜串成成片这一步还没实现：先在各个分镜节点出视频，成片暂时要自己拼。";

    private static bool HasKind(
        IReadOnlyList<WorkflowAttachment> attachments,
        AttachmentKind kind,
        Func<string, bool> exists) =>
        attachments.Any(item => item.Kind == kind && exists(item.Reference));
}
