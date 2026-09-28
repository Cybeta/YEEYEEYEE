using System.Drawing.Drawing2D;
using System.Text.Json.Serialization;

namespace DreamForge.Desktop;

public enum ContentSource { User, Ai, Api }
public enum VersionDecision
{
    None,
    KeepHistorical,
    Adopted
}

public enum NodeCategory
{
    General, Character, Scene, Storyboard, Prop, Product }
public enum NodeExecutionStatus { Draft, WaitingForUser, Generating, Completed, Failed, NeedsReview }

/// <summary>节点附件的媒体种类。</summary>
public enum AttachmentKind { Image, Video, Audio, Other }

/// <summary>节点附件：文本之外的内容（图片 / 视频 / 音频）都作为附件挂在节点上，一个节点可有多个。</summary>
public sealed class WorkflowAttachment
{
    /// <summary>
    /// 参考图合成的产出标记。带这个标记的节点附件会被当作该镜头的出图底图优先使用，
    /// 它是「分步合成」与「正式出图」之间的接口。
    /// </summary>
    public const string SourceComposition = "合成底图";

    public Guid Id { get; set; } = Guid.NewGuid();
    public AttachmentKind Kind { get; set; } = AttachmentKind.Image;
    public string Reference { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public static AttachmentKind KindOf(string reference)
    {
        var extension = Path.GetExtension(reference).ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" => AttachmentKind.Image,
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => AttachmentKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => AttachmentKind.Audio,
            _ => AttachmentKind.Other
        };
    }

    public static string DisplayName(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => "图片",
        AttachmentKind.Video => "视频",
        AttachmentKind.Audio => "音频",
        _ => "文件"
    };
}

public sealed class GenerationHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Input { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Accepted { get; set; }
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public NodeExecutionStatus Status { get; set; }
    public List<AiNodeProposal> Proposals { get; set; } = new();
}

/// <summary>
/// 通用节点：不区分类型，任何节点都可以同时承载文本内容与多个附件。
/// </summary>
public sealed class WorkflowNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新节点";
    public NodeCategory Category { get; set; } = NodeCategory.General;
    public ContentSource ContentSource { get; set; } = ContentSource.User;
    public NodeExecutionStatus ExecutionStatus { get; set; } = NodeExecutionStatus.Draft;
    public string Content { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public Guid? ParentNodeId { get; set; }

    /// <summary>
    /// 指向工作树条目（章节或能力）的稳定锚。工作树是叙事轴的上游，
    /// 节点靠它跟随项目树更新；<see cref="Chapter"/> 只作显示用的冗余文本。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WorkTreeItemId { get; set; }

    public Guid? GenerationId { get; set; }
    public bool IsCollapsed { get; set; }

    /// <summary>引用的设定列表；一个镜头可以同时引用多个角色与场景。</summary>
    public List<NodeReference> References { get; set; } = new();

    /// <summary>旧版单引用字段，仅用于加载时迁移为 <see cref="References"/>，新数据不再写入。</summary>
    [JsonPropertyName("EntityId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyEntityId { get; set; }

    [JsonPropertyName("VariantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyVariantId { get; set; }

    [JsonPropertyName("VariantVersionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyVariantVersionId { get; set; }

    /// <summary>节点已锁定：用于保护定稿的章节，锁定后不允许再改内容与引用。</summary>
    public bool IsLocked { get; set; }

    /// <summary>节点对章节版本变更的人工决策，会随画布保存。</summary>
    public VersionDecision VersionDecision { get; set; } = VersionDecision.None;

    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
    public List<GenerationHistory> GenerationHistory { get; set; } = new();
    public float X { get; set; } = 80;
    public float Y { get; set; } = 80;
    public int InputCount { get; set; } = 1;
    public int OutputCount { get; set; } = 1;

    /// <summary>
    /// 旧画布里的图片路径字段。仅用于加载时迁移为 <see cref="Attachments"/>，
    /// 迁移后会清空，不再写入新数据。
    /// </summary>
    [JsonPropertyName("AssetPaths")]
    public List<string> LegacyAssetPaths { get; set; } = new();
}

public sealed class WorkflowEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceNodeId { get; set; }
    public int SourcePort { get; set; }
    public Guid TargetNodeId { get; set; }
    public int TargetPort { get; set; }
}

public sealed class WorkflowCanvasState
{
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowEdge> Edges { get; set; } = new();

    /// <summary>项目级设定库：角色 / 场景 / 道具，供画布上各节点跨章节引用。</summary>
    public List<WorkflowEntity> Entities { get; set; } = new();

    public List<WorkTreeItem> WorkTree { get; set; } = new();

    public WorkflowEntity? FindEntity(Guid id) => Entities.FirstOrDefault(entity => entity.Id == id);

    public WorkflowEntityVariant? FindVariant(Guid id) =>
        Entities.SelectMany(entity => entity.Variants).FirstOrDefault(variant => variant.Id == id);

    /// <summary>
    /// 解析节点引用的全部设定，跳过已失效的引用。
    /// 变体被删除时退回实体的第一个变体，实体也被删除时该条引用被忽略。
    /// </summary>
    public List<ReferenceContent> ResolveReferences(WorkflowNode node)
    {
        var results = new List<ReferenceContent>();
        foreach (var reference in node.References)
        {
            var resolved = ResolveReference(reference);
            if (resolved is not null) results.Add(resolved);
        }
        return results;
    }

    /// <summary>
    /// 逐条解析引用并保留与原始引用项的对应关系，解析失败时内容为 null。
    /// 引用管理界面用它展示「哪一条引用出了问题」。
    /// </summary>
    public List<(NodeReference Reference, ReferenceContent? Content)> ResolveReferencePairs(WorkflowNode node) =>
        node.References.Select(reference => (reference, ResolveReference(reference))).ToList();

    private ReferenceContent? ResolveReference(NodeReference reference)
    {
        var entity = reference.EntityId != Guid.Empty ? FindEntity(reference.EntityId) : null;
        if (entity is null && reference.VariantId != Guid.Empty)
            entity = Entities.FirstOrDefault(candidate => candidate.Variants.Any(variant => variant.Id == reference.VariantId));
        if (entity is null) return null;
        var variant = reference.VariantId != Guid.Empty
            ? entity.Variants.FirstOrDefault(candidate => candidate.Id == reference.VariantId)
            : null;
        variant ??= entity.Variants.FirstOrDefault();
        if (variant is null) return null;

        var version = reference.VariantVersionId is { } versionId ? variant.FindVersion(versionId) : null;
        return version is null
            ? new ReferenceContent(entity, variant, null, variant.Description, variant.Layout, variant.Attachments)
            : new ReferenceContent(entity, variant, version, version.Description, version.Layout, version.Attachments);
    }

    /// <summary>节点卡片上的引用标签：单条直接显示，多条显示首条并标注数量。</summary>
    public string ReferenceLabel(WorkflowNode node)
    {
        var references = ResolveReferences(node);
        if (references.Count == 0) return string.Empty;
        return references.Count == 1 ? references[0].Label : $"{references[0].Label} 等 {references.Count} 个";
    }

    /// <summary>逐条列出引用标签，供节点属性面板显示。</summary>
    public List<string> ReferenceLabels(WorkflowNode node) =>
        ResolveReferences(node).Select(reference => reference.Label).ToList();

    /// <summary>
    /// 把节点引用的全部设定拼成提示词片段：每条引用给出种类与名称、核心设定、生效版本的差异描述，
    /// 场景再附空间布局。未引用设定时返回空串。
    /// </summary>
    public string DescribeReferenceForPrompt(WorkflowNode node)
    {
        var references = ResolveReferences(node);
        if (references.Count == 0) return string.Empty;
        var lines = new List<string>();
        foreach (var reference in references)
        {
            lines.Add($"{WorkflowEntity.KindName(reference.Entity.Kind)}「{reference.Label}」");
            if (!string.IsNullOrWhiteSpace(reference.Entity.Core)) lines.Add($"核心设定：{reference.Entity.Core.Trim()}");
            if (!string.IsNullOrWhiteSpace(reference.Description)) lines.Add($"当前表现：{reference.Description.Trim()}");
            if (reference.Layout is { IsEmpty: false } layout) lines.Add(layout.ToPromptText());
        }
        return string.Join("\n", lines);
    }
}

public sealed class WorkflowCanvasControl : Control
{
    private const int NodeWidth = 190;
    private const int NodeHeight = 100;
    private const int ImageNodeHeight = 176;
    private const int ThumbnailHeight = 64;
    private const int PortRadius = 10;
    private const float PortHitRadius = 20f;
    private readonly Dictionary<Guid, RectangleF> nodeBounds = new();
    private readonly record struct ResourceIndicator(Color Color, string Version, bool Upload);

    private List<ResourceIndicator> GetResourceIndicators(WorkflowNode node)
    {
        var indicators = new List<ResourceIndicator>();
        foreach (var entity in State.Entities)
        foreach (var variant in entity.Variants)
        {
            var latest = variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault();
            var written = variant.Attachments.Any(resource => node.Attachments.Any(attachment =>
                string.Equals(attachment.Reference, resource.Reference, StringComparison.OrdinalIgnoreCase)));
            if (written)
            {
                var matching = variant.Versions
                    .OrderByDescending(version => version.Number)
                    .FirstOrDefault(version => version.Attachments.Any(resource => node.Attachments.Any(attachment =>
                        string.Equals(attachment.Reference, resource.Reference, StringComparison.OrdinalIgnoreCase))));
                var isLatest = matching is null
                    ? variant.HasUncommittedChanges
                    : latest is not null && matching.Id == latest.Id;
                indicators.Add(new ResourceIndicator(isLatest ? Color.SteelBlue : Color.Goldenrod, matching?.Label ?? latest?.Label ?? $"v{variant.CurrentVersion}", true));
            }
        }
        foreach (var reference in State.ResolveReferences(node))
        {
            var latest = reference.Variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault();
            var isOld = reference.Version is not null && latest is not null && reference.Version.Number < latest.Number;
            indicators.Add(new ResourceIndicator(isOld ? Color.Goldenrod : Color.SteelBlue, reference.VersionLabel, false));
        }
        return indicators;
    }
    private readonly Dictionary<string, Image?> thumbnailCache = new();
    private readonly HashSet<Guid> highlightedNodeIds = new();
    private Point lastMouse;
    private WorkflowNode? draggingNode;
    private PointF dragOffset;
    private WorkflowNode? linkSourceNode;
    private int linkSourcePort = -1;
    private bool panning;
    private PointF panOrigin;
    private float zoom = 1f;
    private float previewGlowPhase;
    private readonly System.Windows.Forms.Timer previewGlowTimer = new() { Interval = 55 };

    public WorkflowCanvasState State { get; } = new();
    public WorkflowNode? SelectedNode { get; private set; }
    public WorkflowEdge? SelectedEdge { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>
    /// 待提交的 Agent 改动预览：以虚影叠加绘制，让用户在保存前看到会发生什么。
    /// 它不参与命中测试，也不影响任何数据。
    /// </summary>
    public CanvasPreview? Preview { get; private set; }

    /// <summary>设置待提交改动的预览；传 null 清除。由主窗体在变更集变化时调用。</summary>
    public void SetPreview(CanvasPreview? preview)
    {
        Preview = preview;
        Invalidate();
    }
    public event EventHandler? SelectionChanged;
    public event EventHandler<WorkflowNode>? NodeDoubleClicked;
    public event EventHandler? ZoomChanged;

    /// <summary>节点被删除后触发，携带被删除的节点，便于外层处理其资产的引用与清理。</summary>
    public event EventHandler<WorkflowNode>? NodeDeleted;

    public WorkflowCanvasControl()
    {
        DoubleBuffered = true;
        BackColor = Theme.CanvasBg;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        MouseWheel += CanvasMouseWheel;
        MouseDown += CanvasMouseDown;
        MouseDoubleClick += CanvasMouseDoubleClick;
        MouseMove += CanvasMouseMove;
        MouseUp += CanvasMouseUp;
        KeyDown += CanvasKeyDown;
        previewGlowTimer.Tick += (_, _) =>
        {
            previewGlowPhase = (previewGlowPhase + 0.08f) % 1f;
            if (Preview?.UpdatedNodeIds.Count > 0) Invalidate();
        };
        previewGlowTimer.Start();
    }

    public float Zoom => zoom;

    public void SetHighlightedNodes(IEnumerable<Guid> nodeIds)
    {
        highlightedNodeIds.Clear();
        highlightedNodeIds.UnionWith(nodeIds);
        Invalidate();
    }

    public void ClearHighlightedNodes()
    {
        if (highlightedNodeIds.Count == 0) return;
        highlightedNodeIds.Clear();
        Invalidate();
    }

    public void SelectNode(WorkflowNode node)
    {
        if (!State.Nodes.Any(item => item.Id == node.Id)) return;
        SelectedNode = node;
        SelectedEdge = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void LoadState(WorkflowCanvasState state)
    {
        State.Nodes.Clear(); State.Nodes.AddRange(state.Nodes ?? new());
        State.Edges.Clear(); State.Edges.AddRange(state.Edges ?? new());
        State.Entities.Clear(); State.Entities.AddRange(state.Entities ?? new());
        State.WorkTree.Clear(); State.WorkTree.AddRange(state.WorkTree ?? new());
        foreach (var node in State.Nodes) { MigrateLegacyAssets(node); MigrateLegacyReference(node); }
        SelectedNode = null; SelectedEdge = null; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把旧画布的单引用字段迁移为引用列表，迁移后清空旧字段，避免重复迁移。</summary>
    private static void MigrateLegacyReference(WorkflowNode node)
    {
        if (node.LegacyEntityId is null && node.LegacyVariantId is null)
        {
            node.LegacyVariantVersionId = null;
            return;
        }
        if (node.References.Count == 0)
            node.References.Add(new NodeReference
            {
                EntityId = node.LegacyEntityId ?? Guid.Empty,
                VariantId = node.LegacyVariantId ?? Guid.Empty,
                VariantVersionId = node.LegacyVariantVersionId
            });
        node.LegacyEntityId = null; node.LegacyVariantId = null; node.LegacyVariantVersionId = null;
    }

    /// <summary>把旧画布的图片路径字段迁移为附件，迁移后清空旧字段，避免重复迁移。</summary>
    private static void MigrateLegacyAssets(WorkflowNode node)
    {
        if (node.LegacyAssetPaths.Count == 0) return;
        foreach (var reference in node.LegacyAssetPaths)
        {
            if (string.IsNullOrWhiteSpace(reference)) continue;
            node.Attachments.Add(new WorkflowAttachment
            {
                Kind = WorkflowAttachment.KindOf(reference),
                Reference = reference,
                Name = Path.GetFileName(reference),
                Source = "旧画布迁移"
            });
        }
        node.LegacyAssetPaths.Clear();
    }

    public void Clear()
    {
        ClearGraph(); State.Entities.Clear(); Invalidate(); NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只清空画布上的节点与连线，保留设定库（示例模板会用到）。</summary>
    private void ClearGraph()
    {
        State.Nodes.Clear(); State.Edges.Clear(); SelectedNode = null; SelectedEdge = null;
    }

    /// <summary>新建一个不区分类型的空节点。</summary>
    public WorkflowNode AddNode()
    {
        var node = new WorkflowNode { Title = "新节点" };
        var slot = FindFreeSlot(80, 80, NodeHeightOf(node));
        node.X = slot.X; node.Y = slot.Y;
        State.Nodes.Add(node); SelectedNode = node; NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate(); return node;
    }

    public void ApplyTemplate()
    {
        ClearGraph();
        var template = new (string Title, string Content, float X, float Y)[]
        {
            ("企划文本", "输入故事主题、世界观与主角方向", 60, 60),
            ("第一章", "粘贴或撰写第一章正文", 300, 60),
            ("分镜", "由章节正文自动拆分镜头", 540, 60),
            ("人物", "从分镜中提取的角色设定", 460, 250),
            ("场景", "从分镜中提取的场景设定", 700, 250),
            ("人物参考图", "调用图像接口生成角色参考图", 460, 440)
        };
        foreach (var (title, content, x, y) in template)
            State.Nodes.Add(new WorkflowNode { Title = title, Content = content, X = x, Y = y });
        void Link(int from, int to)
        {
            State.Edges.Add(new WorkflowEdge { SourceNodeId = State.Nodes[from].Id, TargetNodeId = State.Nodes[to].Id });
            State.Nodes[to].ParentNodeId = State.Nodes[from].Id;
        }
        Link(0, 1); Link(1, 2); Link(2, 3); Link(2, 4); Link(3, 5);
        SelectedNode = State.Nodes[0]; NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    /// <summary>节点卡片的附件摘要，例如“图片 2 · 视频 1”。</summary>
    public static string AttachmentSummary(WorkflowNode node)
    {
        if (node.Attachments.Count == 0) return string.Empty;
        return string.Join(" · ", node.Attachments
            .GroupBy(attachment => attachment.Kind)
            .OrderBy(group => group.Key)
            .Select(group => $"{WorkflowAttachment.DisplayName(group.Key)} {group.Count()}"));
    }

    public IReadOnlyList<WorkflowNode> AddGeneratedNodes(WorkflowNode parent, IReadOnlyList<AiNodeProposal> proposals)
    {
        var created = new List<WorkflowNode>();
        var childX = parent.X + NodeWidth + 60;
        var childY = parent.Y;
        const float rowGap = 40;
        foreach (var proposal in proposals)
        {
            var node = new WorkflowNode
            {
                Title = proposal.Title,
                Content = proposal.Content,
                ContentSource = ContentSource.Ai,
                ExecutionStatus = NodeExecutionStatus.Draft,
                ParentNodeId = parent.Id,
                X = childX,
                Y = childY
            };
            while (!IsFree(new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node))))
                node.Y += NodeHeightOf(node) + rowGap;
            State.Nodes.Add(node);
            State.Edges.Add(new WorkflowEdge { SourceNodeId = parent.Id, TargetNodeId = node.Id });
            created.Add(node);
            childY = node.Y + NodeHeightOf(node) + rowGap;
        }
        if (created.Count > 0) { SelectedNode = created[0]; NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
        return created;
    }

    /// <summary>
    /// 从给定起点寻找不与既有节点重叠的位置；同列放不下时换到下一列。
    /// </summary>
    private PointF FindFreeSlot(float startX, float startY, float height)
    {
        const float columnGap = 60;
        const float rowGap = 40;
        var x = startX;
        var y = startY;
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var rect = new RectangleF(x, y, NodeWidth, height);
            if (IsFree(rect)) return new PointF(x, y);
            y += rowGap;
            if (y - startY > rowGap * 40)
            {
                x += NodeWidth + columnGap;
                y = startY;
            }
        }
        return new PointF(x, y);
    }

    private bool IsFree(RectangleF rect)
    {
        foreach (var node in State.Nodes)
        {
            var bounds = new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node));
            if (bounds.IntersectsWith(rect)) return false;
        }
        return true;
    }

    /// <summary>
    /// 按连线层级重新排布全部节点：列号取该节点到起点的最长路径深度，同列按原纵向位置排序。
    /// 存在环时深度会收敛在上限内，不会进入死循环。
    /// </summary>
    /// <summary>
    /// 自动排版画布。未选中节点时整理全部节点；选中节点时只整理它的多级后代，父节点保持原位。
    /// </summary>
    public void AutoArrange()
    {
        var root = SelectedNode;
        var nodes = root is null
            ? State.Nodes.ToList()
            : DescendantsOf(root).ToList();
        if (nodes.Count == 0) return;

        const float columnGap = 60;
        const float rowGap = 40;
        var depths = ComputeDepths(nodes, root);
        var baseX = root is null ? 80f : root.X + NodeWidth + columnGap;
        var baseY = root is null ? 80f : root.Y;
        var maxDepth = depths.Values.DefaultIfEmpty(0).Max();
        for (var depth = 0; depth <= maxDepth; depth++)
        {
            var columnNodes = nodes
                .Where(node => depths.GetValueOrDefault(node.Id) == depth)
                .OrderBy(node => node.Y)
                .ThenBy(node => node.Title, StringComparer.Ordinal)
                .ToList();
            var y = baseY;
            foreach (var node in columnNodes)
            {
                node.X = baseX + depth * (NodeWidth + columnGap);
                node.Y = y;
                y += NodeHeightOf(node) + rowGap;
            }
        }
        NotifyChanged(); Invalidate();
    }

    private IReadOnlyList<WorkflowNode> DescendantsOf(WorkflowNode root)
    {
        var children = State.Nodes
            .Where(node => node.ParentNodeId is Guid parentId && parentId == root.Id)
            .ToDictionary(node => node.Id);
        foreach (var edge in State.Edges.Where(edge => edge.SourceNodeId == root.Id || State.Nodes.Any(node => node.Id == edge.SourceNodeId && children.ContainsKey(node.Id))))
        {
            var child = State.Nodes.FirstOrDefault(node => node.Id == edge.TargetNodeId);
            if (child is not null) children.TryAdd(child.Id, child);
        }
        var result = new List<WorkflowNode>();
        var queue = new Queue<WorkflowNode>(children.Values);
        var seen = new HashSet<Guid> { root.Id };
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node.Id)) continue;
            result.Add(node);
            foreach (var child in State.Nodes.Where(candidate => candidate.ParentNodeId == node.Id)) queue.Enqueue(child);
            foreach (var edge in State.Edges.Where(edge => edge.SourceNodeId == node.Id))
            {
                var child = State.Nodes.FirstOrDefault(candidate => candidate.Id == edge.TargetNodeId);
                if (child is not null) queue.Enqueue(child);
            }
        }
        return result;
    }

    private Dictionary<Guid, int> ComputeDepths(IReadOnlyList<WorkflowNode> nodes, WorkflowNode? root)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var depths = nodes.ToDictionary(node => node.Id, _ => 0);
        var incoming = State.Edges
            .Where(edge => nodeIds.Contains(edge.TargetNodeId))
            .GroupBy(edge => edge.TargetNodeId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.SourceNodeId).ToArray());
        var iterations = Math.Min(nodes.Count, 50);
        for (var pass = 0; pass < iterations; pass++)
        {
            var changed = false;
            foreach (var node in State.Nodes)
            {
                if (!incoming.TryGetValue(node.Id, out var parents) || parents.Length == 0) continue;
                var depth = parents.Where(depths.ContainsKey).Select(parent => depths[parent]).DefaultIfEmpty(-1).Max();
                if (depth < 0 || depths[node.Id] >= depth + 1) continue;
                depths[node.Id] = depth + 1;
                changed = true;
            }
            if (!changed) break;
        }
        return depths;
    }

    public void DeleteSelected()
    {
        if (SelectedEdge is not null)
        {
            DeleteSelectedEdge();
            return;
        }
        if (SelectedNode is null) return;
        var removed = SelectedNode;
        var id = removed.Id; State.Nodes.Remove(removed); State.Edges.RemoveAll(e => e.SourceNodeId == id || e.TargetNodeId == id); SelectedNode = null; SelectedEdge = null; NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate();
        NodeDeleted?.Invoke(this, removed);
    }

    public void DeleteSelectedEdge()
    {
        if (SelectedEdge is null) return;
        if (!State.Edges.Remove(SelectedEdge)) return;
        SelectedEdge = null;
        NotifyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    public void UpdateSelected(string? title, string? content)
    {
        if (SelectedNode is null) return;
        if (title is not null) SelectedNode.Title = title;
        if (content is not null) SelectedNode.Content = content;
        NotifyChanged(); Invalidate();
    }

    private void NotifyChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>设定库等画布外部数据变动后调用，让外层刷新修订号并标记画布待保存。</summary>
    public void NotifyContentChanged() => NotifyChanged();

    private PointF CanvasPoint(Point point) => new((point.X - panOrigin.X) / zoom, (point.Y - panOrigin.Y) / zoom);
    private RectangleF NodeBounds(WorkflowNode node) => new(node.X * zoom + panOrigin.X, node.Y * zoom + panOrigin.Y, NodeWidth * zoom, NodeHeightOf(node) * zoom);

    private bool IsNodeVisible(WorkflowNode node)
    {
        var current = node.ParentNodeId;
        var visited = new HashSet<Guid>();
        while (current is Guid parentId && visited.Add(parentId))
        {
            var parent = State.Nodes.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent is null) break;
            if (parent.IsCollapsed) return false;
            current = parent.ParentNodeId;
        }
        return true;
    }

    private bool IsCollapseButtonHit(WorkflowNode node, PointF point) =>
        new RectangleF(node.X + NodeWidth - 30, node.Y + 6, 24, 24).Contains(point);

    private bool ToggleCollapsed(WorkflowNode node)
    {
        if (!State.Nodes.Any(candidate => candidate.ParentNodeId == node.Id)) return false;
        node.IsCollapsed = !node.IsCollapsed;
        NotifyChanged();
        Invalidate();
        return true;
    }

    /// <summary>节点高度：只要会绘制预览块（自带附件或引用设定有参考图）就用加高卡片。</summary>
    private float NodeHeightOf(WorkflowNode node) =>
        node.Attachments.Count > 0 || ResolveReferenceImage(node) is not null ? ImageNodeHeight : NodeHeight;

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        var before = CanvasPoint(e.Location); zoom = Math.Clamp(zoom + (e.Delta > 0 ? .1f : -.1f), .5f, 2f); var after = CanvasPoint(e.Location); panOrigin.X += (after.X - before.X) * zoom; panOrigin.Y += (after.Y - before.Y) * zoom; ZoomChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    private void CanvasMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || HitTestNode(e.Location) is not { } node) return;
        SelectedNode = node;
        SelectedEdge = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        NodeDoubleClicked?.Invoke(this, node);
        Invalidate();
    }

    private void CanvasMouseDown(object? sender, MouseEventArgs e)
    {
        Focus(); lastMouse = e.Location;
        if (e.Button == MouseButtons.Middle) { StartPan(e.Location); return; }
        if (e.Button == MouseButtons.Right)
        {
            // 右键命中连线时直接删除，作为取消连线的快捷方式。
            var rightClickedEdge = HitTestEdge(e.Location);
            if (rightClickedEdge is not null)
            {
                State.Edges.Remove(rightClickedEdge);
                SelectedNode = null;
                SelectedEdge = null;
                NotifyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
                return;
            }
            SelectedNode = HitTestNode(e.Location);
            SelectedEdge = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var point = CanvasPoint(e.Location);
        var edge = HitTestEdge(e.Location);
        if (edge is not null)
        {
            SelectedNode = null;
            SelectedEdge = edge;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }
        if (edge is not null)
        {
            SelectedNode = null;
            SelectedEdge = edge;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }
        foreach (var node in Preview?.AddedNodes.AsEnumerable().Reverse() ?? Enumerable.Empty<WorkflowNode>())
        {
            var bounds = new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node));
            if (!bounds.Contains(point)) continue;
            SelectedNode = node;
            SelectedEdge = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }
        foreach (var node in State.Nodes.AsEnumerable().Reverse())
        {
            if (!IsNodeVisible(node)) continue;
            var bounds = new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node));
            if (!bounds.Contains(point)) continue;
            if (IsCollapseButtonHit(node, point) && ToggleCollapsed(node)) return;
            SelectedNode = node; SelectedEdge = null; SelectionChanged?.Invoke(this, EventArgs.Empty);
            if (TryPort(node, point, true, out var outputPort)) { linkSourceNode = node; linkSourcePort = outputPort; }
            else if (TryPort(node, point, false, out var inputPort)) { linkSourceNode = node; linkSourcePort = -1 - inputPort; }
            else { draggingNode = node; dragOffset = new PointF(point.X - node.X, point.Y - node.Y); }
            Invalidate(); return;
        }
        // 空白处按下：清除选择并开始平移画布，左键与中键都可以拖动。
        SelectedNode = null; SelectedEdge = null; SelectionChanged?.Invoke(this, EventArgs.Empty);
        StartPan(e.Location);
        Invalidate();
    }

    private void StartPan(Point location)
    {
        panning = true;
        lastMouse = location;
        Cursor = Cursors.SizeAll;
    }

    private void EndPan()
    {
        if (!panning) return;
        panning = false;
        Cursor = Cursors.Default;
    }

    private void CanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (panning) { panOrigin.X += e.X - lastMouse.X; panOrigin.Y += e.Y - lastMouse.Y; lastMouse = e.Location; Invalidate(); return; }
        if (draggingNode is not null && e.Button == MouseButtons.Left) { var point = CanvasPoint(e.Location); draggingNode.X = Math.Max(0, point.X - dragOffset.X); draggingNode.Y = Math.Max(0, point.Y - dragOffset.Y); NotifyChanged(); Invalidate(); }
        else if (linkSourceNode is not null) Invalidate();
    }

    private void CanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (panning)
        {
            EndPan();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (linkSourceNode is not null)
        {
            var point = CanvasPoint(e.Location);
            foreach (var target in State.Nodes)
                if (IsNodeVisible(target) && target != linkSourceNode && new RectangleF(target.X, target.Y, NodeWidth, NodeHeightOf(target)).Contains(point) && TryPort(target, point, false, out var targetPort))
                { if (linkSourcePort >= 0)
                    {
                        if (!State.Edges.Any(x => x.SourceNodeId == linkSourceNode.Id && x.SourcePort == linkSourcePort && x.TargetNodeId == target.Id && x.TargetPort == targetPort))
                            State.Edges.Add(new WorkflowEdge { SourceNodeId = linkSourceNode.Id, SourcePort = linkSourcePort, TargetNodeId = target.Id, TargetPort = targetPort });
                    }
                    else
                    {
                        State.Edges.RemoveAll(x => x.TargetNodeId == linkSourceNode.Id && x.TargetPort == -1 - linkSourcePort);
                    }
                    NotifyChanged(); break; }
        }
        draggingNode = null; linkSourceNode = null; Invalidate();
    }

    private void CanvasKeyDown(object? sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete && (SelectedEdge is not null || SelectedNode is not null)) { DeleteSelected(); e.Handled = true; } }

    private bool TryPort(WorkflowNode node, PointF point, bool output, out int index)
    {
        var count = output ? node.OutputCount : node.InputCount;
        for (var i = 0; i < count; i++) { var p = PortPoint(node, output, i); if (Math.Abs(point.X - p.X) <= PortHitRadius / zoom && Math.Abs(point.Y - p.Y) <= PortHitRadius / zoom) { index = i; return true; } }
        index = -1; return false;
    }

    /// <summary>命中测试：返回给定控件坐标处的节点；用于右键菜单等宿主扩展。</summary>
    public WorkflowNode? HitTestNode(Point location)
    {
        var point = CanvasPoint(location);
        var previewNode = Preview?.AddedNodes.LastOrDefault(node =>
            new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node)).Contains(point));
        return previewNode ?? State.Nodes.LastOrDefault(node =>
            IsNodeVisible(node) && new RectangleF(node.X, node.Y, NodeWidth, NodeHeightOf(node)).Contains(point));
    }

    public void FocusNode(Guid nodeId)
    {
        var node = Preview?.AddedNodes.FirstOrDefault(item => item.Id == nodeId)
            ?? State.Nodes.FirstOrDefault(item => item.Id == nodeId);
        if (node is null) return;
        SelectedNode = node;
        SelectedEdge = null;
        panOrigin = new PointF(
            Width / 2f - (node.X + NodeWidth / 2f) * zoom,
            Height / 2f - (node.Y + NodeHeightOf(node) / 2f) * zoom);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public bool IsPreviewNode(WorkflowNode node) =>
        Preview?.AddedNodes.Any(item => item.Id == node.Id) == true;

    /// <summary>命中测试：按连线实际绘制的贝塞尔路径查找最近连线。</summary>
    public WorkflowEdge? HitTestEdge(Point location)
    {
        var bestDistance = 12f;
        WorkflowEdge? best = null;
        foreach (var edge in State.Edges)
        {
            var source = State.Nodes.FirstOrDefault(node => node.Id == edge.SourceNodeId);
            var target = State.Nodes.FirstOrDefault(node => node.Id == edge.TargetNodeId);
            if (source is null || target is null || !IsNodeVisible(source) || !IsNodeVisible(target)) continue;
            var a = ScreenPoint(PortPoint(source, true, edge.SourcePort));
            var b = ScreenPoint(PortPoint(target, false, edge.TargetPort));
            var dx = Math.Max(30, Math.Abs(b.X - a.X) * .45f);
            var c1 = new PointF(a.X + dx, a.Y);
            var c2 = new PointF(b.X - dx, b.Y);
            var previous = a;
            for (var step = 1; step <= 24; step++)
            {
                var t = step / 24f;
                var current = CubicPoint(a, c1, c2, b, t);
                var distance = DistanceToSegment(location, previous, current);
                if (distance < bestDistance) { bestDistance = distance; best = edge; }
                previous = current;
            }
        }
        return best;
    }

    private static PointF CubicPoint(PointF a, PointF c1, PointF c2, PointF b, float t)
    {
        var u = 1 - t;
        return new PointF(
            u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
            u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
    }

    private static float DistanceToSegment(Point point, PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return Distance(point, a);
        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Distance(point, new PointF(a.X + t * dx, a.Y + t * dy));
    }

    private static float Distance(Point point, PointF target) =>
        MathF.Sqrt(MathF.Pow(point.X - target.X, 2) + MathF.Pow(point.Y - target.Y, 2));

    private PointF PortPoint(WorkflowNode node, bool output, int index) => new(node.X + (output ? NodeWidth : 0), node.Y + 34 + index * 20);
    private PointF ScreenPoint(PointF point) => new(point.X * zoom + panOrigin.X, point.Y * zoom + panOrigin.Y);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gridPen = new Pen(Theme.GridLine);
        var spacing = 24 * zoom; for (var x = panOrigin.X % spacing; x < Width; x += spacing) e.Graphics.DrawLine(gridPen, x, 0, x, Height); for (var y = panOrigin.Y % spacing; y < Height; y += spacing) e.Graphics.DrawLine(gridPen, 0, y, Width, y);
        if (State.Nodes.Count == 0)
        {
            const string hint = "画布为空\n\n从上方「+ 节点」新建节点，或点「示例模板」加载示例链路\n\n拖动空白处或中键平移 · 滚轮缩放 · 拖动节点端口连线";
            using var hintBrush = new SolidBrush(Theme.TextDim);
            using var hintFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(hint, Font, hintBrush, new RectangleF(0, 0, Width, Height), hintFormat);
        }
        nodeBounds.Clear();
        foreach (var edge in State.Edges)
        {
            var source = State.Nodes.FirstOrDefault(n => n.Id == edge.SourceNodeId); var target = State.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId); if (source is null || target is null || !IsNodeVisible(source) || !IsNodeVisible(target)) continue;
            var edgeColor = SelectedEdge == edge ? Theme.Accent : Theme.Edge;
            DrawArrow(e.Graphics, ScreenPoint(PortPoint(source, true, edge.SourcePort)), ScreenPoint(PortPoint(target, false, edge.TargetPort)), edgeColor, selected: SelectedEdge == edge);
        }
        if (linkSourceNode is not null) DrawArrow(e.Graphics, ScreenPoint(PortPoint(linkSourceNode, linkSourcePort >= 0, linkSourcePort >= 0 ? linkSourcePort : -1 - linkSourcePort)), PointToClient(MousePosition), Theme.Accent);
        foreach (var node in State.Nodes)
        {
            if (!IsNodeVisible(node)) continue;
            var rect = NodeBounds(node); nodeBounds[node.Id] = rect; var selected = SelectedNode == node; var highlighted = highlightedNodeIds.Contains(node.Id);
            using var fill = new SolidBrush(selected ? Theme.NodeBgSelected : highlighted ? Theme.AccentSoft : Theme.NodeBg); using var border = new Pen(selected ? Theme.Accent : highlighted ? Theme.Warning : Theme.NodeBorder, selected || highlighted ? 2 : 1); e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
            using var titleBrush = new SolidBrush(Theme.Text); using var typeBrush = new SolidBrush(Theme.TextMuted); using var contentBrush = new SolidBrush(Theme.NodeBody);
            using var metaFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));
            var titleWidth = DrawVersionBadge(e.Graphics, rect, node, metaFont);
            e.Graphics.DrawString(Trim(node.Title, 18), Font, titleBrush, new RectangleF(rect.X + 12, rect.Y + 8, titleWidth, Font.Height + 4));
            DrawResourceIndicators(e.Graphics, rect, node);
            if (State.Nodes.Any(candidate => candidate.ParentNodeId == node.Id))
            {
                using var collapsePen = new Pen(Theme.TextMuted, 1.5f);
                var button = new RectangleF(rect.Right - 30, rect.Top + 6, 24, 24);
                e.Graphics.DrawRectangle(collapsePen, button.X, button.Y, button.Width, button.Height);
                e.Graphics.DrawLine(collapsePen, button.Left + 6, button.Top + 12, button.Right - 6, button.Top + 12);
                if (node.IsCollapsed) e.Graphics.DrawLine(collapsePen, button.Left + 12, button.Top + 6, button.Left + 12, button.Bottom - 6);
            }
            var attachmentSummary = BuildNodeMetaLine(node);
            e.Graphics.DrawString(Trim(attachmentSummary, 18), metaFont, typeBrush, rect.X + 12, rect.Y + 32);
            DrawStatusBadge(e.Graphics, rect, node, metaFont);
            e.Graphics.DrawString(Trim(node.Content, 30), metaFont, contentBrush, rect.X + 12, rect.Y + 56);
            e.Graphics.DrawString(Trim(node.GenerationHistory.Count > 0 ? $"生成 {node.GenerationHistory.Count} 次" : node.ContentSource.ToString(), 16), metaFont, contentBrush, rect.X + 12, rect.Y + 76);
            DrawThumbnail(e.Graphics, rect, node);
            for (var i = 0; i < node.InputCount; i++) DrawPort(e.Graphics, ScreenPoint(PortPoint(node, false, i)), Theme.Port); for (var i = 0; i < node.OutputCount; i++) DrawPort(e.Graphics, ScreenPoint(PortPoint(node, true, i)), Theme.Success);
        }
        if (Preview is { IsEmpty: false }) DrawPreview(e.Graphics);
    }

    /*
    private void DrawVersionUpgradeLinks(Graphics graphics)
    {
        var groups = State.Nodes
            .SelectMany(node => node.References.Select(reference => (Node: node, Reference: reference)))
            .Where(item => item.Reference.VariantVersionId is not null)
            .GroupBy(item => (item.Reference.EntityId, item.Reference.VariantId))
            .ToList();

        using var pen = new Pen(Color.FromArgb(180, Theme.Warning), 1.5f) { DashStyle = DashStyle.Dash };
        using var font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));
        using var brush = new SolidBrush(Theme.Warning);

        foreach (var group in groups)
        {
            var versions = group
                .GroupBy(item => item.Reference.VariantVersionId)
                .Select(item => new
                {
                    Version = State.FindVariant(group.Key.VariantId)?.Versions.FirstOrDefault(version => version.Id == item.Key),
                    Nodes = item.Select(value => value.Node).Where(IsNodeVisible).ToList()
                })
                .Where(item => item.Version is not null && item.Nodes.Count > 0)
                .OrderBy(item => item.Version!.Number)
                .ToList();

            for (var index = 1; index < versions.Count; index++)
            {
                var previous = versions[index - 1];
                var current = versions[index];
                var source = ScreenPoint(NodeBounds(previous.Nodes[0]).Location);
                var target = ScreenPoint(NodeBounds(current.Nodes[0]).Location);
                using var linkPen = (Pen)pen.Clone();
                linkPen.CustomEndCap = new AdjustableArrowCap(5, 5);
                graphics.DrawLine(linkPen, source, target);
                var label = $"{previous.Version!.Label} → {current.Version!.Label}";
                var midpoint = new PointF((source.X + target.X) / 2, (source.Y + target.Y) / 2);
                graphics.DrawString(label, font, brush, midpoint);
            }
        }
    }
    */

    /// <summary>
    /// 叠加绘制待提交改动的虚影：将新增的节点画成半透明虚线卡，将删除的覆白并加红色虚线框，
    /// 将修改的套一圈强调色虚线框。纯视觉提示，不参与命中测试。
    /// </summary>
    private void DrawPreview(Graphics g)
    {
        if (Preview is not { } preview) return;
        var accent = Theme.Accent;
        var danger = Theme.Danger;
        using var tagFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));

        foreach (var id in preview.RemovedNodeIds)
        {
            if (State.Nodes.FirstOrDefault(item => item.Id == id) is not { } node) continue;
            var rect = NodeBounds(node);
            using var veil = new SolidBrush(Color.FromArgb(Theme.IsDark ? 210 : 185, Theme.CanvasBg));
            g.FillRectangle(veil, rect);
            using var pen = new Pen(danger, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            DrawPreviewTag(g, rect, "将删除", danger, tagFont);
        }

        foreach (var id in preview.UpdatedNodeIds)
        {
            if (State.Nodes.FirstOrDefault(item => item.Id == id) is not { } node) continue;
            var rect = NodeBounds(node);
            var glow = (MathF.Sin(previewGlowPhase * MathF.PI * 2f) + 1f) / 2f;
            var glowColor = Blend(Color.White, accent, glow);
            using var halo = new Pen(Color.FromArgb(70 + (int)(100 * glow), glowColor), 7 + glow * 3)
            {
                DashStyle = DashStyle.Custom,
                DashPattern = new[] { 2f, 5f },
                DashOffset = -previewGlowPhase * 14f
            };
            g.DrawRectangle(halo, rect.X, rect.Y, rect.Width, rect.Height);
            using var pen = new Pen(accent, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            DrawPreviewTag(g, rect, "将修改", accent, tagFont);
        }

        // 将被删除的连线：在它原来的位置上画一条浅红虚线（节点删除会连带删边，那时也走这里）。
        foreach (var id in preview.RemovedEdgeIds)
        {
            if (State.Edges.FirstOrDefault(item => item.Id == id) is not { } edge) continue;
            var source = State.Nodes.FirstOrDefault(node => node.Id == edge.SourceNodeId);
            var target = State.Nodes.FirstOrDefault(node => node.Id == edge.TargetNodeId);
            if (source is null || target is null) continue;
            DrawArrow(g, ScreenPoint(PortPoint(source, true, edge.SourcePort)),
                ScreenPoint(PortPoint(target, false, edge.TargetPort)), Color.FromArgb(170, Theme.Danger), dashed: true);
        }

        foreach (var edge in preview.AddedEdges)
        {
            var source = PreviewNode(preview, edge.SourceNodeId);
            var target = PreviewNode(preview, edge.TargetNodeId);
            if (source is null || target is null) continue;
            DrawArrow(g, ScreenPoint(PortPoint(source, true, edge.SourcePort)),
                ScreenPoint(PortPoint(target, false, edge.TargetPort)), Color.FromArgb(170, Theme.Accent), dashed: true);
        }

        foreach (var node in preview.AddedNodes)
        {
            var rect = NodeBounds(node);
            using var fill = new SolidBrush(Color.FromArgb(205, Theme.PreviewOverlay));
            g.FillRectangle(fill, rect);
            using var pen = new Pen(accent, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            using var titleBrush = new SolidBrush(Theme.TextMuted);
            using var bodyBrush = new SolidBrush(Theme.TextDim);
            g.DrawString(Trim(node.Title, 18), Font, titleBrush, rect.X + 12, rect.Y + 8);
            var body = string.IsNullOrWhiteSpace(node.Content) ? "（无内容）" : node.Content;
            g.DrawString(Trim(body, 30), tagFont, bodyBrush, rect.X + 12, rect.Y + 32);
            DrawPreviewTag(g, rect, "将新增", accent, tagFont);
        }
    }

    /// <summary>预览里的节点：优先取新增节点，其次取当前画布上的节点。</summary>
    private WorkflowNode? PreviewNode(CanvasPreview preview, Guid id) =>
        preview.AddedNodes.FirstOrDefault(node => node.Id == id) ?? State.Nodes.FirstOrDefault(node => node.Id == id);

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            255,
            from.R + (int)((to.R - from.R) * amount),
            from.G + (int)((to.G - from.G) * amount),
            from.B + (int)((to.B - from.B) * amount));
    }

    private static void DrawPreviewTag(Graphics g, RectangleF rect, string text, Color color, Font font)
    {
        var size = g.MeasureString(text, font);
        var tag = new RectangleF(rect.Right - size.Width - 12, rect.Bottom - size.Height - 6, size.Width + 4, size.Height);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, tag.X, tag.Y);
    }

    /// <summary>
    /// 节点卡片第二行的摘要：优先显示引用的设定（带 ◆ 标记），再补充附件概况；
    /// 两者都没有时说明这是纯文本节点。
    /// </summary>
    public string GetNodeChapterLabel(WorkflowNode node)
    {
        var chapter = GetNodeChapterNumber(node);
        return chapter > 0 ? $"第{chapter}章" : "未识别";
    }

    public EntityVariantVersion? GetEffectiveVersionForNode(
        WorkflowNode node,
        WorkflowEntity entity,
        WorkflowEntityVariant variant)
    {
        var chapter = GetNodeChapterNumber(node);
        if (chapter == 0) return null;

        return variant.Versions
            .Where(version => GetVersionSourceChapter(entity, variant, version) <= chapter)
            .OrderByDescending(version => version.Number)
            .FirstOrDefault();
    }

    public string GetNodeVersionStatusSummary(WorkflowNode node)
    {
        var references = State.ResolveReferences(node)
            .Where(reference => reference.Version is not null)
            .ToList();
        if (references.Count == 0) return "版本状态：未引用固定版本";

        var labels = references.Select(reference =>
        {
            var version = reference.Version!;
            var status = GetVersionStatus(node, reference.Entity, reference.Variant, version);
            return $"{reference.Entity.Name} {version.Label} · {status}";
        });
        return "版本状态：" + string.Join("；", labels);
    }

    private float DrawVersionBadge(Graphics graphics, RectangleF rect, WorkflowNode node, Font font)
    {
        var references = State.ResolveReferences(node);
        var versioned = references.Where(reference => reference.Version is not null).ToList();
        if (versioned.Count == 0) return rect.Width - 24;

        var statuses = versioned.Select(reference =>
        {
            var version = reference.Version!;
            var status = GetVersionStatus(node, reference.Entity, reference.Variant, version);
            return (version.Number, Status: status);
        }).Distinct().ToList();
        var first = statuses[0];
        var label = $"V{first.Number} · {first.Status}";
        if (statuses.Count > 1) label += $" +{statuses.Count - 1}";
        var hasWarning = statuses.Any(status => status.Status == "需要确认");
        var hasFuture = statuses.Any(status => status.Status == "未来版本");
        var color = hasWarning || hasFuture ? Theme.Warning : Theme.Success;
        var size = graphics.MeasureString(label, font);
        var badge = new RectangleF(rect.Right - size.Width - 16, rect.Top + 7, size.Width + 8, size.Height + 2);
        using var fill = new SolidBrush(hasWarning || hasFuture ? Theme.Hover : Theme.AccentSoft);
        using var text = new SolidBrush(color);
        graphics.FillRectangle(fill, badge);
        graphics.DrawString(label, font, text, badge.X + 4, badge.Y + 1);
        return Math.Max(40, badge.Left - rect.Left - 20);
    }

    private string GetVersionStatus(
        WorkflowNode node,
        WorkflowEntity entity,
        WorkflowEntityVariant variant,
        EntityVariantVersion referencedVersion)
    {
        if (node.VersionDecision == VersionDecision.KeepHistorical)
            return "历史保留";
        if (node.VersionDecision == VersionDecision.Adopted &&
            referencedVersion.Id == GetEffectiveVersionForNode(node, entity, variant)?.Id)
            return "已采纳";

        var chapter = GetNodeChapterNumber(node);
        if (chapter == 0) return "当前适用";

        var effectiveVersion = variant.Versions
            .Where(version => GetVersionSourceChapter(entity, variant, version) <= chapter)
            .OrderByDescending(version => version.Number)
            .FirstOrDefault();
        if (effectiveVersion is null || referencedVersion.Number == effectiveVersion.Number)
            return "当前适用";
        return referencedVersion.Number < effectiveVersion.Number ? "需要确认" : "未来版本";
    }

    private int GetNodeChapterNumber(WorkflowNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Chapter))
            return ExtractChapterNumber(node.Chapter);

        var current = node;
        while (current.ParentNodeId is { } parentId)
        {
            var parent = State.Nodes.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent is null) break;
            if (IsChapterNode(parent)) return GetChapterNumber(parent.Title);
            current = parent;
        }

        return IsChapterNode(node) ? GetChapterNumber(node.Title) : 0;
    }

    private int GetVersionSourceChapter(
        WorkflowEntity entity,
        WorkflowEntityVariant variant,
        EntityVariantVersion version)
    {
        var item = State.WorkTree.FirstOrDefault(candidate =>
            candidate.Kind == WorkTreeKind.Version &&
            candidate.SourceEntityId == entity.Id &&
            candidate.SourceVariantId == variant.Id &&
            candidate.SourceVersionId == version.Id);
        return item is null ? 0 : ExtractChapterNumber(item.Chapter);
    }

    private bool IsWorkTreeDescendantOf(WorkTreeItem item, string name)
    {
        var parentId = item.ParentId;
        while (parentId is { } id)
        {
            var parent = State.WorkTree.FirstOrDefault(candidate => candidate.Id == id);
            if (parent is null) break;
            if (parent.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
            parentId = parent.ParentId;
        }
        return false;
    }

    private bool IsAncestorOrSelf(WorkflowNode ancestor, WorkflowNode node)
    {
        var current = node;
        while (true)
        {
            if (current.Id == ancestor.Id) return true;
            if (current.ParentNodeId is not { } parentId) return false;
            var parent = State.Nodes.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent is null) return false;
            current = parent;
        }
    }

    private static bool IsChapterNode(WorkflowNode node) =>
        node.Title.Contains("章", StringComparison.Ordinal) && GetChapterNumber(node.Title) > 0;

    private static int GetChapterNumber(string text) => ExtractChapterNumber(text);

    private static int ExtractChapterNumber(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"第\s*(\d+)\s*章");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var arabicNumber)) return arabicNumber;
        var chinese = System.Text.RegularExpressions.Regex.Match(text, @"第([零〇一二两三四五六七八九十百千]+)章");
        if (!chinese.Success) return 0;
        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
            ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6,
            ['七'] = 7, ['八'] = 8, ['九'] = 9
        };
        var total = 0;
        var section = 0;
        var number = 0;
        foreach (var character in chinese.Groups[1].Value)
        {
            if (digits.TryGetValue(character, out var digit))
            {
                number = digit;
                continue;
            }
            var unit = character switch { '十' => 10, '百' => 100, '千' => 1000, _ => 1 };
            section += (number == 0 && unit == 10 ? 1 : number) * unit;
            number = 0;
        }
        total = section + number;
        return total;
    }

    private static int ExtractWorkVersion(string version)
    {
        var digits = new string(version.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    private void DrawResourceIndicators(Graphics graphics, RectangleF rect, WorkflowNode node)
    {
        var indicators = GetResourceIndicators(node);
        var x = rect.Left + 12;
        foreach (var indicator in indicators.Take(3))
        {
            using var pen = new Pen(indicator.Color, 2f) { DashStyle = indicator.Color == Color.Goldenrod ? DashStyle.Dash : DashStyle.Solid };
            var center = new PointF(x + 7, indicator.Upload ? rect.Top - 3 : rect.Bottom + 3);
            var tip = indicator.Upload ? new PointF(center.X, center.Y - 6) : new PointF(center.X, center.Y + 6);
            var tail = indicator.Upload ? new PointF(center.X, center.Y + 5) : new PointF(center.X, center.Y - 5);
            graphics.DrawLine(pen, tail, tip);
            graphics.DrawLine(pen, tip, new PointF(tip.X - 3, tip.Y + (indicator.Upload ? 3 : -3)));
            graphics.DrawLine(pen, tip, new PointF(tip.X + 3, tip.Y + (indicator.Upload ? 3 : -3)));
            using var font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 2f));
            using var brush = new SolidBrush(indicator.Color);
            graphics.DrawString(indicator.Version, font, brush, x + 12, indicator.Upload ? rect.Top - 11 : rect.Bottom + 1);
            x += 54;
        }
    }

    private string BuildNodeMetaLine(WorkflowNode node)
    {
        var reference = State.ReferenceLabel(node);
        var summary = AttachmentSummary(node);
        var category = NodeCategoryLabel(node.Category);
        if (!string.IsNullOrEmpty(reference))
            return string.IsNullOrEmpty(summary) ? $"{category} · ◆ {reference}" : $"{category} · ◆ {reference}（{summary}）";
        return string.IsNullOrEmpty(summary) ? $"{category} · 纯文本节点" : $"{category} · {summary}";
    }

    private static string NodeCategoryLabel(NodeCategory category) => category switch
    {
        NodeCategory.Character => "角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Prop => "道具",
        NodeCategory.Product => "成品",
        _ => "通用"
    };

    /// <summary>节点引用生效的第一张参考图；未引用或都没有图片时返回 null。</summary>
    private WorkflowAttachment? ResolveReferenceImage(WorkflowNode node) =>
        State.ResolveReferences(node)
            .SelectMany(reference => reference.Attachments)
            .FirstOrDefault(attachment => attachment.Kind == AttachmentKind.Image);

    /// <summary>
    /// 在节点内绘制预览：优先用引用设定的参考图，其次节点自带的第一个图片附件；
    /// 都没有图片时只标注媒体类型与文件名，不伪造视频或音频预览。
    /// </summary>
    private void DrawThumbnail(Graphics g, RectangleF rect, WorkflowNode node)
    {
        var referenceImage = ResolveReferenceImage(node);
        var preview = referenceImage ?? node.Attachments.FirstOrDefault(attachment => attachment.Kind == AttachmentKind.Image);
        if (preview is null && node.Attachments.Count == 0) return;
        var target = new RectangleF(rect.X + 12, rect.Y + 98, rect.Width - 24, ThumbnailHeight * zoom);
        using var border = new Pen(Theme.NodeBorder);
        if (preview is null)
        {
            var fallback = node.Attachments[0];
            using var placeholder = new SolidBrush(Theme.PreviewOverlay);
            g.FillRectangle(placeholder, target); g.DrawRectangle(border, target.X, target.Y, target.Width, target.Height);
            using var labelBrush = new SolidBrush(Theme.TextMuted);
            var label = $"{WorkflowAttachment.DisplayName(fallback.Kind)}：{fallback.Name}";
            g.DrawString(Trim(label, 22), Font, labelBrush, target.X + 6, target.Y + 6);
            return;
        }
        var thumbnail = LoadThumbnail(preview.Reference);
        if (thumbnail is null)
        {
            using var placeholder = new SolidBrush(Theme.PreviewOverlay);
            g.FillRectangle(placeholder, target); g.DrawRectangle(border, target.X, target.Y, target.Width, target.Height);
            using var warnBrush = new SolidBrush(Theme.Warning);
            g.DrawString("图片不可用", Font, warnBrush, target.X + 6, target.Y + 6);
            return;
        }
        var previous = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(thumbnail, target);
        g.InterpolationMode = previous;
        g.DrawRectangle(border, target.X, target.Y, target.Width, target.Height);
        if (referenceImage is not null)
        {
            using var sourceBrush = new SolidBrush(Theme.Accent);
            g.DrawString("设定参考图", Font, sourceBrush, target.X + 6, target.Bottom - 18);
        }
        if (node.Attachments.Count > 1)
        {
            using var badgeBrush = new SolidBrush(Theme.TextMuted);
            g.DrawString($"共 {node.Attachments.Count} 个附件", Font, badgeBrush, target.Right - 84, target.Bottom - 18);
        }
    }

    /// <summary>资产目录等外部位置变化后清空缩略图缓存，避免继续显示旧文件。</summary>
    public void InvalidateThumbnails()
    {
        foreach (var image in thumbnailCache.Values) image?.Dispose();
        thumbnailCache.Clear();
        Invalidate();
    }

    private Image? LoadThumbnail(string reference)
    {
        if (thumbnailCache.TryGetValue(reference, out var cached)) return cached;
        Image? image = null;
        try
        {
            var path = AssetStore.Resolve(reference);
            if (path is not null)
            {
                using var stream = File.OpenRead(path);
                image = Image.FromStream(stream);
            }
        }
        catch (Exception) { image = null; }
        thumbnailCache[reference] = image;
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            previewGlowTimer.Stop();
            previewGlowTimer.Dispose();
            foreach (var image in thumbnailCache.Values) image?.Dispose();
            thumbnailCache.Clear();
        }
        base.Dispose(disposing);
    }

    private static void DrawStatusBadge(Graphics g, RectangleF rect, WorkflowNode node, Font font)
    {
        var text = node.ExecutionStatus switch
        {
            NodeExecutionStatus.WaitingForUser => "待补充",
            NodeExecutionStatus.Generating => "生成中",
            NodeExecutionStatus.Completed => "已完成",
            NodeExecutionStatus.Failed => "失败",
            NodeExecutionStatus.NeedsReview => "待确认",
            _ => "草稿"
        };
        var color = node.ExecutionStatus switch
        {
            NodeExecutionStatus.WaitingForUser => Theme.Warning,
            NodeExecutionStatus.Generating => Theme.Accent,
            NodeExecutionStatus.Completed => Theme.Success,
            NodeExecutionStatus.Failed => Theme.Danger,
            NodeExecutionStatus.NeedsReview => Theme.Warning,
            _ => Theme.TextMuted
        };
        var size = g.MeasureString(text, font);
        var x = rect.Right - size.Width - 12;
        var y = rect.Y + 32;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x - 11, y + size.Height / 2 - 3, 6, 6);
        g.DrawString(text, font, brush, x, y);
    }

    private static string Trim(string text, int length) => string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..length] + "…";
    private static void DrawPort(Graphics g, PointF p, Color color) { using var brush = new SolidBrush(color); g.FillEllipse(brush, p.X - PortRadius, p.Y - PortRadius, PortRadius * 2, PortRadius * 2); }
    private static void DrawArrow(Graphics g, PointF a, PointF b, Color color, bool dashed = false, bool selected = false)
    {
        using var pen = new Pen(color, selected ? 4 : 2);
        if (dashed) pen.DashStyle = DashStyle.Dash;
        var dx = Math.Max(30, Math.Abs(b.X - a.X) * .45f);
        g.DrawBezier(pen, a, new PointF(a.X + dx, a.Y), new PointF(b.X - dx, b.Y), b);
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        var p1 = new PointF(b.X - 9 * (float)Math.Cos(angle - .45), b.Y - 9 * (float)Math.Sin(angle - .45));
        var p2 = new PointF(b.X - 9 * (float)Math.Cos(angle + .45), b.Y - 9 * (float)Math.Sin(angle + .45));
        using var headBrush = new SolidBrush(color);
        g.FillPolygon(headBrush, new[] { b, p1, p2 });
    }
}
