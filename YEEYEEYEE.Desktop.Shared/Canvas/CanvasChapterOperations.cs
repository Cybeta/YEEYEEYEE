namespace YEEYEEYEE.Desktop;

/// <summary>章节结构操作做过的改动。</summary>
public enum ChapterChangeKind
{
    Created,
    Renamed,
    Reordered,
    Moved,
    Split,
    Merged,
    Deleted,
    NodeReanchored,
    ChapterReparented,
    OrderNormalized
}

/// <summary>一次章节结构改动。</summary>
public sealed record ChapterChange(ChapterChangeKind Kind, string ObjectType, string ObjectId, string Detail)
{
    public override string ToString() => $"[{Kind}] {ObjectType}({ObjectId})：{Detail}";
}

/// <summary>
/// 章节结构冲突。<see cref="Blocking"/> 为真时操作不会执行（结果画布不含任何改动），
/// 为假时操作照常执行，但界面必须把冲突显示出来。
/// </summary>
public sealed record ChapterConflict(
    string Code,
    string ObjectType,
    string ObjectId,
    string Detail,
    string Handling,
    bool Blocking)
{
    public override string ToString() =>
        $"{(Blocking ? "[阻断]" : "[提示]")} {Code} {ObjectType}({ObjectId})：{Detail}（处理：{Handling}）";
}

/// <summary>章节结构操作结果：结果画布（调用方传入的对象不被改动）+ 改动与冲突。</summary>
public sealed record ChapterOperationResult(
    WorkflowCanvasState Canvas,
    IReadOnlyList<ChapterChange> Changes,
    IReadOnlyList<ChapterConflict> Conflicts)
{
    public bool Changed => Changes.Count > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasBlockingConflicts => Conflicts.Any(conflict => conflict.Blocking);

    public string ToText()
    {
        var lines = new List<string>
        {
            $"章节操作：改动 {Changes.Count} 项，冲突 {Conflicts.Count} 项（其中阻断 {Conflicts.Count(conflict => conflict.Blocking)} 项）。"
        };
        lines.AddRange(Changes.Select(change => "· " + change));
        lines.AddRange(Conflicts.Select(conflict => "! " + conflict));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>章节结构冲突代码（只增不改）。</summary>
public static class CanvasChapterConflictCodes
{
    public const string NotFound = "CHAPTER_NOT_FOUND";
    public const string MoveCycle = "CHAPTER_MOVE_CYCLE";
    public const string NameDuplicated = "CHAPTER_NAME_DUPLICATED";
    public const string DeleteHasReferences = "CHAPTER_DELETE_HAS_REFERENCES";
    public const string MergeSameTarget = "CHAPTER_MERGE_SAME_TARGET";
    public const string SplitWithoutNodes = "CHAPTER_SPLIT_WITHOUT_NODES";
}

/// <summary>
/// 章节结构操作（大目标 B / 任务 1）。所有操作先在副本上执行：
/// · 有阻断冲突时不做任何改动，调用方对象保持原样，也不存在半完成状态；
/// · 章节移动/拆分/合并/删除都按 ID 重接引用（节点的 <see cref="WorkflowNode.WorkTreeItemId"/> 锚点、
///   子章节的 <see cref="WorkTreeItem.ParentId"/>），并按需同步显示文本；
/// · 删除有引用的章节默认阻断，必须显式选择把节点改挂到上级章节；
/// · 名称只用于显示：同名章节不合并、不按名称绑定，只报冲突。
/// </summary>
public static class CanvasChapterOperations
{
    /// <summary>新建章节；顺序排在同级末尾。</summary>
    public static ChapterOperationResult Create(WorkflowCanvasState canvas, string name, Guid? parentChapterId = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NotFound, "WorkTreeItem", string.Empty,
                "章节名称不能为空。", "填写章节名称后重试", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        if (parentChapterId is { } parentId && FindChapter(working, parentId) is null)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NotFound, "WorkTreeItem", parentId.ToString(),
                "指定的上级章节不存在。", "重新选择上级章节，或改为顶层章节", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        NoteDuplicateName(working, trimmed, null, conflicts);
        var order = NextOrder(working, parentChapterId);
        var item = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = trimmed, ParentId = parentChapterId, Order = order };
        working.WorkTree.Add(item);
        changes.Add(new ChapterChange(ChapterChangeKind.Created, "WorkTreeItem", item.Id.ToString(),
            $"新建章节「{trimmed}」（顺序 {order}）。"));
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>重命名章节；名称只作显示，重命名不会改变节点锚点。</summary>
    public static ChapterOperationResult Rename(WorkflowCanvasState canvas, Guid chapterId, string newName)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var chapter = FindChapter(working, chapterId);
        if (chapter is null) return NotFound(working, chapterId, conflicts);

        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NotFound, "WorkTreeItem", chapterId.ToString(),
                "章节名称不能为空。", "填写章节名称后重试", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        var previous = chapter.Name;
        NoteDuplicateName(working, trimmed, chapterId, conflicts);
        chapter.Name = trimmed;
        changes.Add(new ChapterChange(ChapterChangeKind.Renamed, "WorkTreeItem", chapterId.ToString(),
            $"章节「{previous}」改名为「{trimmed}」；节点锚点不变。"));

        // 显示文本跟随：只有仍旧等于旧名称的节点文本会被更新，避免误改人工文本。
        foreach (var node in FollowersOf(working, chapterId, previous))
        {
            node.Chapter = trimmed;
            changes.Add(new ChapterChange(ChapterChangeKind.Renamed, "WorkflowNode", node.Id.ToString(),
                $"节点「{node.Title}」的章节显示文本同步为「{trimmed}」。"));
        }

        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>
    /// 章节改名 / 合并时要跟着改**显示文本**的那些节点：只动「文本仍等于旧名」的（人工改过的文本不动）。
    ///
    /// 为什么要两个条件一起看：有锚点的节点按**章节 ID** 判它属不属于这一章——只按文本选的话，
    /// 两个同名章节里改一个，另一个的节点文本也会被一起改掉（章节身份本来就是 ID，文本只是显示）。
    /// 没有锚点的那种老节点（只有文本字段）只能照旧按文本认，否则它们的显示文本从此不再跟随。
    /// </summary>
    private static IEnumerable<WorkflowNode> FollowersOf(WorkflowCanvasState canvas, Guid chapterId, string previousName) =>
        canvas.Nodes.Where(node =>
            string.Equals(node.Chapter.Trim(), previousName.Trim(), StringComparison.Ordinal)
            && (CanvasChapters.ResolveChapterId(canvas, node) is not { } owner || owner == chapterId));

    /// <summary>设置章节显式顺序；与同级冲突时按请求值重排同级并记录归一化。</summary>
    public static ChapterOperationResult Reorder(WorkflowCanvasState canvas, Guid chapterId, int order)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var chapter = FindChapter(working, chapterId);
        if (chapter is null) return NotFound(working, chapterId, conflicts);

        var previous = chapter.Order;
        chapter.Order = order;
        changes.Add(new ChapterChange(ChapterChangeKind.Reordered, "WorkTreeItem", chapterId.ToString(),
            $"章节「{chapter.Name}」顺序 {Describe(previous)} → {order}。"));
        NormalizeSiblings(working, CanvasChapters.ParentChapterId(working, chapter), changes);
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>移动章节到另一个上级章节（null 为顶层）；成环时阻断。</summary>
    public static ChapterOperationResult Move(WorkflowCanvasState canvas, Guid chapterId, Guid? newParentChapterId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var chapter = FindChapter(working, chapterId);
        if (chapter is null) return NotFound(working, chapterId, conflicts);

        if (newParentChapterId is { } parentId)
        {
            if (FindChapter(working, parentId) is null)
            {
                conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NotFound, "WorkTreeItem", parentId.ToString(),
                    "目标上级章节不存在。", "重新选择上级章节", Blocking: true));
                return new ChapterOperationResult(working, changes, conflicts);
            }

            if (parentId == chapterId || IsDescendant(working, parentId, chapterId))
            {
                conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.MoveCycle, "WorkTreeItem", chapterId.ToString(),
                    "移动会让章节成为自己的下级，父链会成环。", "改选别的上级章节", Blocking: true));
                return new ChapterOperationResult(working, changes, conflicts);
            }
        }

        var previousParent = chapter.ParentId;
        var previousParentChapter = CanvasChapters.ParentChapterId(working, chapter);
        chapter.ParentId = newParentChapterId;
        chapter.Order = NextOrder(working, newParentChapterId);
        changes.Add(new ChapterChange(ChapterChangeKind.Moved, "WorkTreeItem", chapterId.ToString(),
            $"章节「{chapter.Name}」从 {DescribeParent(working, previousParent)} 移动到 {DescribeParent(working, newParentChapterId)}；"
            + "节点靠锚点跟随，未改动节点引用。"));

        // 换父后原上级与目标上级各自归一化顺序，避免出现重复顺序值。
        NormalizeSiblings(working, previousParentChapter, changes);
        NormalizeSiblings(working, newParentChapterId, changes);
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>拆分章节：新建同级章节，并把指定节点改挂到新章节。</summary>
    public static ChapterOperationResult Split(WorkflowCanvasState canvas, Guid chapterId, string newChapterName, IReadOnlyList<Guid> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var chapter = FindChapter(working, chapterId);
        if (chapter is null) return NotFound(working, chapterId, conflicts);

        var wanted = (nodeIds ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).ToHashSet();
        if (wanted.Count == 0)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.SplitWithoutNodes, "WorkTreeItem", chapterId.ToString(),
                "拆分没有指定要移出的节点，无法确定拆分边界。", "先选择要移入新章节的节点", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        var trimmed = (newChapterName ?? string.Empty).Trim();
        if (trimmed.Length == 0) trimmed = chapter.Name + "（拆分）";
        NoteDuplicateName(working, trimmed, null, conflicts);

        var target = new WorkTreeItem
        {
            Kind = WorkTreeKind.Chapter,
            Name = trimmed,
            ParentId = chapter.ParentId,
            Order = chapter.Order + 1
        };
        working.WorkTree.Add(target);
        changes.Add(new ChapterChange(ChapterChangeKind.Split, "WorkTreeItem", target.Id.ToString(),
            $"从「{chapter.Name}」拆出章节「{trimmed}」。"));
        NormalizeSiblings(working, CanvasChapters.ParentChapterId(working, chapter), changes);

        var moved = 0;
        for (var index = 0; index < working.Nodes.Count; index++)
        {
            var node = working.Nodes[index];
            if (!wanted.Contains(node.Id)) continue;
            if (CanvasChapters.ResolveChapterId(working, node) != chapterId)
            {
                conflicts.Add(new ChapterConflict(CanvasChapterCodes.TextWithoutAnchor, "WorkflowNode", node.Id.ToString(),
                    $"节点「{node.Title}」的章节归属不是被拆分的章节，未改动。", "确认节点归属后重新拆分", Blocking: false));
                continue;
            }

            Reanchor(working, node, target, changes, $"拆分到「{trimmed}」");
            moved++;
        }

        changes.Add(new ChapterChange(ChapterChangeKind.Split, "WorkTreeItem", chapterId.ToString(),
            $"拆出 {moved} 个节点到「{trimmed}」。"));
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>合并章节：源章节的节点改挂到目标章节，源章节删除，其子章节改挂到源章节的上级。</summary>
    public static ChapterOperationResult Merge(WorkflowCanvasState canvas, Guid sourceChapterId, Guid targetChapterId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var source = FindChapter(working, sourceChapterId);
        if (source is null) return NotFound(working, sourceChapterId, conflicts);
        var target = FindChapter(working, targetChapterId);
        if (target is null) return NotFound(working, targetChapterId, conflicts);

        if (sourceChapterId == targetChapterId)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.MergeSameTarget, "WorkTreeItem", sourceChapterId.ToString(),
                "合并的源与目标是同一个章节。", "选择不同的目标章节", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        var sourceName = source.Name;
        foreach (var node in FollowersOf(working, sourceChapterId, sourceName))
            node.Chapter = target.Name;

        var reparented = 0;
        foreach (var child in working.WorkTree.Where(item => item.ParentId == sourceChapterId).ToList())
        {
            child.ParentId = source.ParentId;
            reparented++;
        }

        working.WorkTree.Remove(source);
        changes.Add(new ChapterChange(ChapterChangeKind.Merged, "WorkTreeItem", targetChapterId.ToString(),
            $"章节「{sourceName}」合并进「{target.Name}」；{reparented} 个子条目改挂到上级，节点锚点按 ID 转发。"));
        changes.Add(new ChapterChange(ChapterChangeKind.Deleted, "WorkTreeItem", sourceChapterId.ToString(),
            $"源章节「{sourceName}」已删除（内容未丢失）。"));

        ReanchorNodesOfChapter(working, sourceChapterId, target, changes, "合并到「" + target.Name + "」");
        NormalizeSiblings(working, CanvasChapters.ParentChapterId(working, target), changes);
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>
    /// 删除章节。有节点引用时默认阻断；<paramref name="reassignNodesToParent"/> 为真时把节点改挂到上级章节
    /// （没有上级则清空锚点并保留文本，避免静默丢引用）。
    /// </summary>
    public static ChapterOperationResult Delete(WorkflowCanvasState canvas, Guid chapterId, bool reassignNodesToParent = false)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        var chapter = FindChapter(working, chapterId);
        if (chapter is null) return NotFound(working, chapterId, conflicts);

        var anchored = working.Nodes.Where(node => node.WorkTreeItemId == chapterId).ToList();
        if (anchored.Count > 0 && !reassignNodesToParent)
        {
            conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.DeleteHasReferences, "WorkTreeItem", chapterId.ToString(),
                $"章节「{chapter.Name}」被 {anchored.Count} 个节点直接用锚点引用，直接删除会留下悬空引用。",
                "选择「把节点改挂到上级章节」后再删除，或先手工调整节点", Blocking: true));
            return new ChapterOperationResult(working, changes, conflicts);
        }

        var parentId = chapter.ParentId;
        var parentChapterId = CanvasChapters.ParentChapterId(working, chapter);
        var parentChapter = parentChapterId is { } id ? FindChapter(working, id) : null;
        var reparented = 0;
        foreach (var child in working.WorkTree.Where(item => item.ParentId == chapterId).ToList())
        {
            child.ParentId = parentId;
            reparented++;
        }

        working.WorkTree.Remove(chapter);
        changes.Add(new ChapterChange(ChapterChangeKind.Deleted, "WorkTreeItem", chapterId.ToString(),
            $"删除章节「{chapter.Name}」；{reparented} 个子条目改挂到上级。"));
        if (anchored.Count > 0)
        {
            if (parentChapter is not null)
                ReanchorNodesOfChapter(working, chapterId, parentChapter, changes, $"改挂到上级「{parentChapter.Name}」");
            else
                ClearAnchors(working, chapterId, changes);
        }

        NormalizeSiblings(working, parentId, changes);
        return new ChapterOperationResult(working, changes, conflicts);
    }

    /// <summary>补齐并归一化显式顺序（不改动名称、锚点与父子关系）。</summary>
    public static ChapterOperationResult NormalizeOrder(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var working = CanvasCloner.Clone(canvas);
        var conflicts = new List<ChapterConflict>();
        var changes = new List<ChapterChange>();
        CanvasChapters.EnsureExplicitOrder(working);
        foreach (var group in CanvasChapters.ChapterItems(working).GroupBy(item => CanvasChapters.ParentChapterId(working, item)))
            NormalizeSiblings(working, group.Key, changes);
        return new ChapterOperationResult(working, changes, conflicts);
    }

    // ── 内部实现 ───────────────────────────────────────────────────────────

    private static ChapterOperationResult NotFound(WorkflowCanvasState working, Guid chapterId, List<ChapterConflict> conflicts)
    {
        conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NotFound, "WorkTreeItem", chapterId.ToString(),
            "找不到该章节条目。", "刷新后重新选择章节", Blocking: true));
        return new ChapterOperationResult(working, new List<ChapterChange>(), conflicts);
    }

    private static WorkTreeItem? FindChapter(WorkflowCanvasState canvas, Guid chapterId) =>
        canvas.WorkTree.FirstOrDefault(item => item.Id == chapterId && item.Kind == WorkTreeKind.Chapter);

    private static void NoteDuplicateName(WorkflowCanvasState canvas, string name, Guid? exceptId, List<ChapterConflict> conflicts)
    {
        var others = CanvasChapters.ChapterItems(canvas)
            .Where(item => item.Id != exceptId && string.Equals(item.Name.Trim(), name, StringComparison.Ordinal))
            .ToList();
        if (others.Count == 0) return;
        conflicts.Add(new ChapterConflict(CanvasChapterConflictCodes.NameDuplicated, "WorkTreeItem", others[0].Id.ToString(),
            $"已存在 {others.Count} 个名为「{name}」的章节；章节身份是 ID，不会按名称合并。",
            "如需区分请改名；引用与同步都按 ID 判定", Blocking: false));
    }

    private static bool IsDescendant(WorkflowCanvasState canvas, Guid candidateId, Guid ancestorId)
    {
        var visited = new HashSet<Guid>();
        var current = canvas.WorkTree.FirstOrDefault(item => item.Id == candidateId)?.ParentId;
        while (current is { } parentId && visited.Add(parentId))
        {
            if (parentId == ancestorId) return true;
            current = canvas.WorkTree.FirstOrDefault(item => item.Id == parentId)?.ParentId;
        }

        return false;
    }

    private static int NextOrder(WorkflowCanvasState canvas, Guid? parentChapterId)
    {
        var siblings = CanvasChapters.ChapterItems(canvas)
            .Where(item => CanvasChapters.ParentChapterId(canvas, item) == parentChapterId)
            .ToList();
        return siblings.Count == 0 ? CanvasChapters.OrderStep : siblings.Max(item => item.Order) + CanvasChapters.OrderStep;
    }

    /// <summary>把同一父章节下的顺序重排为 OrderStep 的整数倍，顺序值冲突时确定性收敛。</summary>
    private static void NormalizeSiblings(WorkflowCanvasState canvas, Guid? parentChapterId, List<ChapterChange> changes)
    {
        var siblings = CanvasChapters.ChapterItems(canvas)
            .Where(item => CanvasChapters.ParentChapterId(canvas, item) == parentChapterId)
            .ToList();
        if (siblings.Count == 0) return;

        var ordered = siblings
            .OrderBy(item => item.Order == 0 ? int.MaxValue : item.Order)
            .ThenBy(item => CanvasChapters.SortKey(item.Name).Number)
            .ThenBy(item => CanvasChapters.SortKey(item.Name).Text, StringComparer.Ordinal)
            .ToList();

        var step = 1;
        foreach (var item in ordered)
        {
            var expected = step * CanvasChapters.OrderStep;
            if (item.Order != expected)
            {
                changes.Add(new ChapterChange(ChapterChangeKind.OrderNormalized, "WorkTreeItem", item.Id.ToString(),
                    $"章节「{item.Name}」顺序 {Describe(item.Order)} → {expected}。"));
                item.Order = expected;
            }

            step++;
        }
    }

    private static void ReanchorNodesOfChapter(
        WorkflowCanvasState canvas,
        Guid chapterId,
        WorkTreeItem target,
        List<ChapterChange> changes,
        string reason)
    {
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            if (node.WorkTreeItemId != chapterId) continue;
            Reanchor(canvas, node, target, changes, reason);
        }
    }

    private static void Reanchor(WorkflowCanvasState canvas, WorkflowNode node, WorkTreeItem target, List<ChapterChange> changes, string reason)
    {
        var previous = node.WorkTreeItemId;
        node.WorkTreeItemId = target.Id;
        if (!string.IsNullOrWhiteSpace(node.Chapter) && !string.Equals(node.Chapter.Trim(), target.Name.Trim(), StringComparison.Ordinal))
            node.Chapter = target.Name;
        changes.Add(new ChapterChange(ChapterChangeKind.NodeReanchored, "WorkflowNode", node.Id.ToString(),
            $"节点「{node.Title}」锚点 {Describe(previous)} → {target.Id}（{reason}）。"));
    }

    private static void ClearAnchors(WorkflowCanvasState canvas, Guid chapterId, List<ChapterChange> changes)
    {
        foreach (var node in canvas.Nodes.Where(node => node.WorkTreeItemId == chapterId))
        {
            var title = node.Title;
            var text = node.Chapter;
            node.WorkTreeItemId = null;
            if (string.IsNullOrWhiteSpace(node.Chapter)) node.Chapter = text;
            changes.Add(new ChapterChange(ChapterChangeKind.NodeReanchored, "WorkflowNode", node.Id.ToString(),
                $"节点「{title}」的章节锚点被清空（章节已删除且没有上级章节），显示文本「{text}」保留待人工处理。"));
        }
    }

    internal static string Describe(Guid? id) => id is { } value ? value.ToString() : "（无）";

    internal static string Describe(int order) => order == 0 ? "（未指定）" : order.ToString();

    private static string DescribeParent(WorkflowCanvasState canvas, Guid? parentId)
    {
        if (parentId is not { } id) return "顶层";
        var item = canvas.WorkTree.FirstOrDefault(candidate => candidate.Id == id);
        return item is null ? $"（缺失的上级 {id}）" : $"「{item.Name}」";
    }
}
