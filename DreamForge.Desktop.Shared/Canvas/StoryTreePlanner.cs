namespace DreamForge.Desktop;

/// <summary>故事画布（左栏）里一行的种类。界面据此决定标记形状与颜色。</summary>
public enum StoryRowKind
{
    /// <summary>分组标题：项目 / 未绑定节点 / 资源库。</summary>
    Group,
    Chapter,
    Storyboard,
    /// <summary>章节里不是分镜的普通节点（章节节点、通用节点……）。</summary>
    Node,
    /// <summary>节点引用的一条设定。</summary>
    Reference,
    /// <summary>去重汇总行（本章出场）。</summary>
    Rollup
}

/// <summary>
/// 故事画布里的一行。
///
/// 关键约定：**引用行是「引用点」，不是副本**。同一个「林晚」出现在多个分镜下就是多行，
/// 改任何一处改的都是同一个设定；「第 4 章换装」的表达方式不是复制一个林晚，
/// 而是第 4 章那一条引用指向「换装」变体——所以变体与版本写在 <see cref="Detail"/> 里，
/// 二者各占一个词（换装变体 / 同名变体的第 2 版快照是两回事）。
/// </summary>
public sealed record StoryRow(
    StoryRowKind Kind,
    string Key,
    string Title,
    string Detail,
    NodeCategory Category,
    bool IsSubReference,
    string BlockedReason,
    Guid? NodeId,
    IReadOnlyList<Guid> NodeIds,
    Guid? EntityId,
    Guid? VariantId,
    IReadOnlyList<StoryRow> Children)
{
    /// <summary>章节行指向的工作树条目（双击可以编辑它）；其余行为 null。</summary>
    public Guid? WorkTreeItemId { get; init; }

    /// <summary>失效或锁定版本缺失的引用：写回之前必须先处理。</summary>
    public bool Blocked => BlockedReason.Length > 0;
}

public sealed record StoryTree(IReadOnlyList<StoryRow> Roots, string Summary)
{
    public static readonly StoryTree Empty = new(Array.Empty<StoryRow>(), string.Empty);
}

/// <summary>
/// 把一张画布算成「故事画布」那棵树：**章节 → 分镜 → 场景 / 人物 → 道具**。
///
/// 为什么是这个层次：分镜节点上的 `References` 是**直接引用**（场景 / 人物），
/// 而角色变体自己带的 `References` 是**子引用**（道具 / 服装）——这两层数据里本来就有。
/// 于是左栏与画布上的引用树终于是同一套结构。
///
/// 两个刻意的设计：
/// 1. 归属一律走稳定章节 ID（`CanvasChapters.ResolveChapterId`），**不按章节名文本猜**；
/// 2. 引用行挂在**节点**下面，而不是挂在章节下面——同一个「林晚」会被很多章引用，
///    挂到某一章就等于宣称她只属于那一章。所以「林晚在每章下面出现」是由各章的分镜累积出来的，
///    另外再给一行去重汇总（「本章出场」）回答另一个问题：这一章到底谁出场。
///
/// 它只产出数据结构，不碰界面、不改画布。
/// </summary>
public static class StoryTreePlanner
{
    public const string ProjectKey = "group:project";
    public const string UnboundKey = "group:unbound";
    public const string LibraryKey = "group:library";

    public static StoryTree Build(WorkflowCanvasState state, string canvasTitle)
    {
        ArgumentNullException.ThrowIfNull(state);

        var chapters = CanvasChapters.ChapterItems(state);
        var chapterIds = chapters.Select(item => item.Id).ToHashSet();

        // 节点归章：走稳定章节 ID；没有归属的进「未绑定节点」。
        var byChapter = new Dictionary<Guid, List<WorkflowNode>>();
        var unbound = new List<WorkflowNode>();
        foreach (var node in state.Nodes)
        {
            var chapterId = CanvasChapters.ResolveChapterId(state, node);
            if (chapterId is { } id && chapterIds.Contains(id))
            {
                if (!byChapter.TryGetValue(id, out var list)) byChapter[id] = list = new List<WorkflowNode>();
                list.Add(node);
            }
            else
            {
                unbound.Add(node);
            }
        }

        var chapterRows = new List<StoryRow>();
        foreach (var chapter in chapters)
        {
            var nodes = byChapter.TryGetValue(chapter.Id, out var list) ? list : new List<WorkflowNode>();
            chapterRows.Add(BuildChapter(state, chapter, nodes));
        }

        var project = new StoryRow(
            StoryRowKind.Group, ProjectKey,
            string.IsNullOrWhiteSpace(canvasTitle) ? "项目" : $"项目 · {canvasTitle}",
            string.Empty, NodeCategory.General, false, string.Empty,
            null, Array.Empty<Guid>(), null, null, chapterRows);

        var roots = new List<StoryRow> { project };

        if (unbound.Count > 0)
        {
            var unboundRows = unbound
                .OrderBy(node => node.X).ThenBy(node => node.Y).ThenBy(node => node.Title, StringComparer.Ordinal)
                .Select(NodeRow)
                .ToList();
            roots.Add(new StoryRow(
                StoryRowKind.Group, UnboundKey,
                $"未绑定节点 · {unboundRows.Count}",
                "没有工作树锚点：Agent 新建的、自由拖出来的节点都在这里",
                NodeCategory.General, false, string.Empty,
                null, Array.Empty<Guid>(), null, null, unboundRows));
        }

        roots.Add(BuildLibrary(state));

        var chapterCount = chapterRows.Count;
        var referenceCount = chapterRows
            .SelectMany(row => row.Children)
            .Where(row => row.Kind == StoryRowKind.Storyboard)
            .Sum(row => row.Children.Count(child => child.Kind == StoryRowKind.Reference));
        var summary = $"{chapterCount} 章 · {state.Nodes.Count} 个节点 · {referenceCount} 条直接引用"
            + (unbound.Count > 0 ? $" · {unbound.Count} 个未绑定" : string.Empty);

        return new StoryTree(roots, summary);
    }

    // ---------------------------------------------------------------- 章节 / 分镜

    private static StoryRow BuildChapter(WorkflowCanvasState state, WorkTreeItem chapter, List<WorkflowNode> nodes)
    {
        var storyboards = nodes
            .Where(node => node.Category == NodeCategory.Storyboard)
            .OrderBy(node => node.X).ThenBy(node => node.Y).ThenBy(node => node.Title, StringComparer.Ordinal)
            .ToList();
        var storyboardIds = storyboards.Select(node => node.Id).ToHashSet();

        var children = new List<StoryRow>();
        foreach (var storyboard in storyboards) children.Add(BuildStoryboard(state, storyboard));

        // 挂在不属于本章的分镜下的成品不算「本章的其他节点」——它们已经跟着父分镜出现了。
        var orphans = nodes
            .Where(node => node.Category != NodeCategory.Storyboard)
            .Where(node => node.ParentNodeId is not { } parent || !storyboardIds.Contains(parent))
            .OrderBy(node => node.X).ThenBy(node => node.Title, StringComparer.Ordinal)
            .ToList();
        if (orphans.Count > 0)
        {
            children.Add(new StoryRow(
                StoryRowKind.Group, $"ch:{chapter.Id:N}:others",
                $"其他节点 · {orphans.Count}",
                "本章里不是分镜的节点",
                NodeCategory.General, false, string.Empty,
                null, Array.Empty<Guid>(), null, null,
                orphans.Select(NodeRow).ToList()));
        }

        var rollup = BuildRollup(state, chapter, storyboards);
        if (rollup is not null) children.Add(rollup);

        var name = string.IsNullOrWhiteSpace(chapter.Name) ? "未命名章节" : chapter.Name;
        return new StoryRow(
            StoryRowKind.Chapter, $"ch:{chapter.Id:N}",
            $"章节 · {name}",
            storyboards.Count == 0 ? "本章还没有分镜节点" : $"{storyboards.Count} 个分镜",
            NodeCategory.Chapter, false, string.Empty,
            null, nodes.Select(node => node.Id).ToList(), null, null, children)
        {
            WorkTreeItemId = chapter.Id
        };
    }

    private static StoryRow BuildStoryboard(WorkflowCanvasState state, WorkflowNode storyboard)
    {
        var children = new List<StoryRow>();

        // 直接引用（场景 / 人物 / 道具）连同它们的子引用一起出来 ——
        // 复用画布上那套引用树，两处口径同一份（排序、环、上限都在那一份里）。
        var tree = CanvasReferenceLayout.Build(state, storyboard);
        var byParent = tree.Cards
            .Where(card => card.ParentId is not null)
            .GroupBy(card => card.ParentId!)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<StoryRow> Rows(string parentId, int depth) =>
            byParent.TryGetValue(parentId, out var cards)
                ? cards.Select(card => BuildReferenceCard(card, storyboard.Id, depth)).ToList()
                : new List<StoryRow>();

        foreach (var card in tree.Cards.Where(card => card.Depth == 1))
            children.Add(BuildReferenceCard(card, storyboard.Id, 1));

        // 挂在分镜下面的成品（ParentNodeId 指向它）。
        foreach (var product in state.Nodes
                     .Where(node => node.ParentNodeId == storyboard.Id)
                     .OrderBy(node => node.X).ThenBy(node => node.Title, StringComparer.Ordinal))
            children.Add(NodeRow(product));

        var referenceCount = children.Count(child => child.Kind == StoryRowKind.Reference);
        return new StoryRow(
            StoryRowKind.Storyboard, $"node:{storyboard.Id:N}",
            $"分镜 · {Title(storyboard)}",
            referenceCount == 0 ? "没有引用设定" : $"{referenceCount} 项引用",
            NodeCategory.Storyboard, false, string.Empty,
            storyboard.Id, Array.Empty<Guid>(), null, null, children);

        StoryRow BuildReferenceCard(ReferenceCard card, Guid ownerNodeId, int depth) => new(
            StoryRowKind.Reference,
            $"ref:{card.Key}@{ownerNodeId:N}",
            $"{card.Kind} · {card.Title}",
            card.Blocked ? card.BlockedReason : card.Subtitle,
            card.Category,
            // 分镜直接引用的在「场景 / 人物」这一层；再往下的（角色自带的道具 / 服装）是子引用。
            depth > 1,
            card.BlockedReason,
            null, Array.Empty<Guid>(),
            card.EntityId, card.VariantId,
            Rows(card.Id, depth + 1));
    }

    private static StoryRow NodeRow(WorkflowNode node) => new(
        StoryRowKind.Node, $"node:{node.Id:N}",
        Title(node),
        string.IsNullOrWhiteSpace(node.Chapter) ? CategoryText(node.Category) : node.Chapter,
        node.Category, false, string.Empty,
        node.Id, Array.Empty<Guid>(), null, null, Array.Empty<StoryRow>());

    // ---------------------------------------------------------------- 去重汇总与资源库

    /// <summary>「本章出场」：把本章各分镜的引用按实体去重，回答「这一章谁出场」。</summary>
    private static StoryRow? BuildRollup(WorkflowCanvasState state, WorkTreeItem chapter, List<WorkflowNode> storyboards)
    {
        var byEntity = new Dictionary<Guid, (WorkflowEntity Entity, HashSet<Guid> Nodes, HashSet<Guid> Variants)>();
        foreach (var storyboard in storyboards)
        {
            foreach (var card in CanvasReferenceLayout.Direct(state, storyboard).Cards.Where(card => card.Depth == 1))
            {
                if (card.EntityId == Guid.Empty || card.Blocked) continue;
                var entity = state.FindEntity(card.EntityId);
                if (entity is null) continue;
                if (!byEntity.TryGetValue(card.EntityId, out var entry))
                    byEntity[card.EntityId] = entry = (entity, new HashSet<Guid>(), new HashSet<Guid>());
                entry.Nodes.Add(storyboard.Id);
                entry.Variants.Add(card.VariantId);
            }
        }
        if (byEntity.Count == 0) return null;

        var rows = byEntity.Values
            .OrderBy(entry => entry.Entity.Kind)
            .ThenBy(entry => entry.Entity.Name, StringComparer.Ordinal)
            .Select(entry => new StoryRow(
                StoryRowKind.Rollup, $"rollup:{chapter.Id:N}:{entry.Entity.Id:N}",
                $"{WorkflowEntity.KindName(entry.Entity.Kind)} · {entry.Entity.Name}",
                entry.Variants.Count > 1 ? $"{entry.Variants.Count} 个变体" : "1 个变体",
                KindCategory(entry.Entity.Kind),
                false, string.Empty,
                null, entry.Nodes.ToList(),
                entry.Entity.Id, null,
                Array.Empty<StoryRow>()))
            .ToList();

        return new StoryRow(
            StoryRowKind.Rollup, $"rollup:{chapter.Id:N}",
            $"本章出场 · {rows.Count} 位",
            "按人去重；分镜那一层回答的是「这个镜头用什么」",
            NodeCategory.General, false, string.Empty,
            null, rows.SelectMany(row => row.NodeIds).Distinct().ToList(),
            null, null, rows);
    }

    private static StoryRow BuildLibrary(WorkflowCanvasState state)
    {
        var rows = new List<StoryRow>();
        foreach (var entity in state.Entities.Where(entity => !entity.IsProjectMissing)
                     .OrderBy(entity => entity.Kind).ThenBy(entity => entity.Name, StringComparer.Ordinal))
        {
            var variants = entity.Variants
                .OrderBy(variant => variant.Name, StringComparer.Ordinal)
                .Select(variant => new StoryRow(
                    StoryRowKind.Reference, $"lib:{entity.Id:N}:{variant.Id:N}",
                    $"变体 · {variant.Name}",
                    variant.CurrentVersionLabel,
                    KindCategory(entity.Kind),
                    false, string.Empty,
                    null, Array.Empty<Guid>(),
                    entity.Id, variant.Id,
                    Array.Empty<StoryRow>()))
                .ToList();
            rows.Add(new StoryRow(
                StoryRowKind.Reference, $"lib:{entity.Id:N}",
                $"{WorkflowEntity.KindName(entity.Kind)} · {entity.Name}",
                variants.Count > 1 ? $"{variants.Count} 个变体" : "1 个变体",
                KindCategory(entity.Kind),
                false, string.Empty,
                null, Array.Empty<Guid>(),
                entity.Id, null, variants));
        }

        return new StoryRow(
            StoryRowKind.Group, LibraryKey,
            $"资源库 · {rows.Count} 项设定",
            "项目级设定（跨章节共享）",
            NodeCategory.General, false, string.Empty,
            null, Array.Empty<Guid>(), null, null, rows);
    }

    private static NodeCategory KindCategory(EntityKind kind) => NodeKindPalette.CategoryOf(kind);

    /// <summary>引用了某个设定的画布节点（左栏点一条引用 → 画布要定位到所有引用它的节点）。</summary>
    public static IReadOnlyList<WorkflowNode> NodesReferencing(WorkflowCanvasState state, Guid entityId, Guid? variantId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Nodes
            .Where(node => node.References.Any(reference =>
                reference.EntityId == entityId && (variantId is null || reference.VariantId == variantId)))
            .ToList();
    }

    private static string Title(WorkflowNode node) => string.IsNullOrWhiteSpace(node.Title) ? "未命名节点" : node.Title;

    private static string CategoryText(NodeCategory category) => category switch
    {
        NodeCategory.Chapter => "章节节点",
        NodeCategory.Character => "角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Prop => "道具",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Product => "成品",
        NodeCategory.StoryPlan => "故事企划",
        NodeCategory.StoryOutline => "故事大纲",
        _ => "通用节点"
    };
}

