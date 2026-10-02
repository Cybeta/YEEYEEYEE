namespace YEEYEEYEE.Desktop;

/// <summary>一个章节的稳定身份信息。章节的权威身份是工作树里的 <see cref="WorkTreeKind.Chapter"/> 条目 ID，
/// 名称只是显示文本，不作为绑定依据。</summary>
public sealed record ChapterInfo(
    Guid Id,
    string Name,
    Guid? ParentChapterId,
    int Order,
    int NodeCount,
    int UnanchoredNodeCount,
    bool IsOrderExplicit);

/// <summary>章节诊断问题代码（只增不改，供界面与测试引用）。</summary>
public static class CanvasChapterCodes
{
    /// <summary>节点只有章节文本、没有稳定锚，无法按 ID 判定归属。</summary>
    public const string TextWithoutAnchor = "CHAPTER_TEXT_WITHOUT_ANCHOR";

    /// <summary>节点锚点指向的章节条目不存在。</summary>
    public const string AnchorMissing = "CHAPTER_ANCHOR_MISSING";

    /// <summary>同名章节存在多个，禁止按名称绑定。</summary>
    public const string NameAmbiguous = "CHAPTER_NAME_AMBIGUOUS";

    /// <summary>显式顺序冲突：同一父章节下出现重复顺序值。</summary>
    public const string OrderDuplicated = "CHAPTER_ORDER_DUPLICATED";

    /// <summary>章节条目缺少显式顺序，已按现有顺序确定性补齐或需要补齐。</summary>
    public const string OrderMissing = "CHAPTER_ORDER_MISSING";

    /// <summary>章节父子关系构成循环。</summary>
    public const string ParentCycle = "CHAPTER_PARENT_CYCLE";
}

/// <summary>
/// 章节身份层（大目标 B / 任务 1）：把「章节」从文本约定升级为可追踪结构。
///
/// 规则：
/// · 章节身份 = 工作树中 <see cref="WorkTreeKind.Chapter"/> 条目的 ID，父子关系用 <see cref="WorkTreeItem.ParentId"/>；
/// · 排序用显式字段 <see cref="WorkTreeItem.Order"/>（同一父章节下比较），缺失时由
///   <see cref="EnsureExplicitOrder"/> 按「第N章」数字、再按条目出现顺序确定性补齐；
/// · 节点归属靠稳定的 <see cref="WorkflowNode.WorkTreeItemId"/> 锚点或父节点链解析，
///   <see cref="WorkflowNode.Chapter"/> 文本只作显示与旧数据兜底，绝不作为绑定依据；
/// · 同名章节不合并、不猜测：<see cref="Diagnose"/> 会报 <see cref="CanvasChapterCodes.NameAmbiguous"/>。
/// </summary>
public static class CanvasChapters
{
    /// <summary>显式顺序的步长，留出插入空间。</summary>
    public const int OrderStep = 10;

    /// <summary>按显式顺序列出全部章节（顶层在先，同层按 Order，其次按名称数字，最后按条目出现顺序）。</summary>
    public static IReadOnlyList<ChapterInfo> List(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var chapterItems = ChapterItems(canvas);
        var entries = new List<(ChapterInfo Info, int Index)>();
        for (var index = 0; index < chapterItems.Count; index++)
        {
            var item = chapterItems[index];
            var anchored = canvas.Nodes.Count(node => node.WorkTreeItemId == item.Id);
            var inherited = canvas.Nodes.Count(node =>
                node.WorkTreeItemId != item.Id && ResolveChapterId(canvas, node) == item.Id);
            entries.Add((new ChapterInfo(
                item.Id,
                item.Name,
                ParentChapterId(canvas, item),
                item.Order,
                anchored,
                inherited,
                item.Order != 0), index));
        }

        return entries
            .OrderBy(entry => entry.Info.ParentChapterId is null ? 0 : 1)
            .ThenBy(entry => entry.Info.Order == 0 ? int.MaxValue : entry.Info.Order)
            .ThenBy(entry => SortKey(entry.Info.Name).Number)
            .ThenBy(entry => SortKey(entry.Info.Name).Text, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Info)
            .ToArray();
    }

    /// <summary>工作树里的章节条目，按落盘顺序。</summary>
    public static List<WorkTreeItem> ChapterItems(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return canvas.WorkTree.Where(item => item.Kind == WorkTreeKind.Chapter).ToList();
    }

    /// <summary>章节条目所属的上级章节（沿 ParentId 向上找第一个章节条目）。</summary>
    public static Guid? ParentChapterId(WorkflowCanvasState canvas, WorkTreeItem chapter)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var visited = new HashSet<Guid>();
        var current = chapter.ParentId;
        while (current is { } parentId && visited.Add(parentId))
        {
            var parent = canvas.WorkTree.FirstOrDefault(item => item.Id == parentId);
            if (parent is null) return null;
            if (parent.Kind == WorkTreeKind.Chapter) return parent.Id;
            current = parent.ParentId;
        }

        return null;
    }

    /// <summary>
    /// 解析节点所属章节的稳定 ID：先看 <see cref="WorkflowNode.WorkTreeItemId"/> 锚点（章节或能力上溯到章节），
    /// 再沿 <see cref="WorkflowNode.ParentNodeId"/> 父链找带锚点的祖先。解析不到返回 null，不用文本猜。
    /// </summary>
    public static Guid? ResolveChapterId(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        var visited = new HashSet<Guid>();
        var current = node;
        while (visited.Add(current.Id))
        {
            if (ChapterOfAnchor(canvas, current.WorkTreeItemId) is { } chapterId) return chapterId;
            if (current.ParentNodeId is not { } parentId) break;
            var parent = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent is null) break;
            current = parent;
        }

        return null;
    }

    /// <summary>锚点条目对应的章节 ID：条目本身是章节就直接用，是能力/版本等则向上找所属章节。</summary>
    public static Guid? ChapterOfAnchor(WorkflowCanvasState canvas, Guid? anchorId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (anchorId is not { } id) return null;
        var visited = new HashSet<Guid>();
        var current = id;
        while (visited.Add(current))
        {
            var item = canvas.WorkTree.FirstOrDefault(candidate => candidate.Id == current);
            if (item is null) return null;
            if (item.Kind == WorkTreeKind.Chapter) return item.Id;
            if (item.ParentId is not { } parentId) return null;
            current = parentId;
        }

        return null;
    }

    /// <summary>名称排序键：优先取「第N章 / 第N集 / N」里的数字，取不到时退化为原文本。</summary>
    public static (int Number, string Text) SortKey(string name)
    {
        var text = (name ?? string.Empty).Trim();
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? (number, text) : (int.MaxValue, text);
    }

    /// <summary>
    /// 为缺显式顺序的章节条目确定性补齐 <see cref="WorkTreeItem.Order"/>：同一父章节下
    /// 先按「第N章」数字、再按条目出现顺序，按 <see cref="OrderStep"/> 递增。
    /// 已显式指定顺序的条目保持原值；返回是否发生了改动。
    /// </summary>
    public static bool EnsureExplicitOrder(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var changed = false;
        var chapters = ChapterItems(canvas);
        foreach (var group in chapters.GroupBy(item => ParentChapterId(canvas, item)))
        {
            var siblings = group.ToList();
            if (siblings.All(item => item.Order != 0)) continue;

            var ordered = siblings
                .Select((item, index) => (item, index))
                .OrderBy(pair => pair.item.Order == 0 ? int.MaxValue : pair.item.Order)
                .ThenBy(pair => SortKey(pair.item.Name).Number)
                .ThenBy(pair => SortKey(pair.item.Name).Text, StringComparer.Ordinal)
                .ThenBy(pair => pair.index)
                .ToList();

            var step = 1;
            foreach (var (item, _) in ordered)
            {
                if (item.Order == 0)
                {
                    item.Order = step * OrderStep;
                    changed = true;
                }

                step++;
            }
        }

        return changed;
    }

    /// <summary>章节诊断：缺锚点、悬空锚点、同名歧义、顺序重复/缺失与父子循环。</summary>
    public static IReadOnlyList<CanvasIdentityIssue> Diagnose(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var issues = new List<CanvasIdentityIssue>();
        var chapters = ChapterItems(canvas);

        void Add(string code, CanvasIdentitySeverity severity, string objectType, Guid objectId, string fieldPath, string message) =>
            issues.Add(new CanvasIdentityIssue(code, severity, objectType, objectId.ToString(), fieldPath, string.Empty, message));

        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";
            if (node.WorkTreeItemId is { } anchorId)
            {
                if (canvas.WorkTree.All(item => item.Id != anchorId))
                    Add(CanvasChapterCodes.AnchorMissing, CanvasIdentitySeverity.Error, "WorkflowNode", node.Id, path + ".WorkTreeItemId",
                        "节点的工作树锚指向不存在的条目，章节归属无法按 ID 判定；请重新指定锚点或清空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Chapter)) continue;
            var matches = chapters.Count(item => string.Equals(item.Name.Trim(), node.Chapter.Trim(), StringComparison.Ordinal));
            if (matches == 0)
                Add(CanvasChapterCodes.TextWithoutAnchor, CanvasIdentitySeverity.Warning, "WorkflowNode", node.Id, path + ".Chapter",
                    $"节点只写了章节文本「{node.Chapter.Trim()}」而没有稳定锚点，也没有同名章节条目；可用「章节同步」建立锚点。");
            else if (matches > 1)
                Add(CanvasChapterCodes.NameAmbiguous, CanvasIdentitySeverity.Warning, "WorkflowNode", node.Id, path + ".Chapter",
                    $"章节文本「{node.Chapter.Trim()}」对应 {matches} 个同名章节条目，禁止按名称绑定，需要人工指定锚点。");
            else
                Add(CanvasChapterCodes.TextWithoutAnchor, CanvasIdentitySeverity.Warning, "WorkflowNode", node.Id, path + ".Chapter",
                    $"节点只有章节文本「{node.Chapter.Trim()}」，存在唯一同名章节条目但尚未建立锚点；可用「章节同步」确认后关联。");
        }

        foreach (var group in chapters.GroupBy(item => item.Name.Trim(), StringComparer.Ordinal))
        {
            if (group.Count() < 2 || string.IsNullOrWhiteSpace(group.Key)) continue;
            foreach (var item in group)
                Add(CanvasChapterCodes.NameAmbiguous, CanvasIdentitySeverity.Warning, "WorkTreeItem", item.Id, $"WorkTree({item.Name}).Name",
                    $"存在 {group.Count()} 个同名章节「{group.Key}」；章节身份是 ID，不按名称合并。");
        }

        foreach (var group in chapters.GroupBy(item => (Parent: ParentChapterId(canvas, item), item.Order)))
        {
            if (group.Key.Order == 0)
            {
                foreach (var item in group)
                    Add(CanvasChapterCodes.OrderMissing, CanvasIdentitySeverity.Warning, "WorkTreeItem", item.Id, $"WorkTree({item.Name}).Order",
                        "章节缺少显式顺序；可用「章节同步」按当前顺序补齐。");
                continue;
            }

            if (group.Count() < 2) continue;
            foreach (var item in group)
                Add(CanvasChapterCodes.OrderDuplicated, CanvasIdentitySeverity.Warning, "WorkTreeItem", item.Id, $"WorkTree({item.Name}).Order",
                    $"同一父章节下有 {group.Count()} 个条目共用顺序 {group.Key.Order}；展示顺序不稳定，需要人工调整。");
        }

        foreach (var chapter in chapters)
        {
            var visited = new HashSet<Guid> { chapter.Id };
            var current = ParentChapterId(canvas, chapter);
            while (current is { } parentId)
            {
                if (!visited.Add(parentId))
                {
                    Add(CanvasChapterCodes.ParentCycle, CanvasIdentitySeverity.Error, "WorkTreeItem", chapter.Id, $"WorkTree({chapter.Name}).ParentId",
                        "章节父链构成循环，无法确定章节层级。");
                    break;
                }

                var parent = chapters.FirstOrDefault(item => item.Id == parentId);
                if (parent is null) break;
                current = ParentChapterId(canvas, parent);
            }
        }

        return issues;
    }
}
