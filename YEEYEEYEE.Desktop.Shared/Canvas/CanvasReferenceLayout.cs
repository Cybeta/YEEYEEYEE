namespace YEEYEEYEE.Desktop;

/// <summary>
/// 引用树里的一张卡（纯计算结果，**不是**画布节点）。
/// </summary>
public sealed record ReferenceCard(
    string Id,
    string? ParentId,
    string Key,
    int Depth,
    int Order,
    Guid EntityId,
    Guid VariantId,
    Guid? VersionId,
    string Kind,
    NodeCategory Category,
    string Title,
    string Subtitle,
    string Excerpt,
    string BlockedReason,
    int ChildReferenceCount)
{
    /// <summary>失效或锁定版本缺失的引用：卡上要标出来，写回之前必须先处理。</summary>
    public bool Blocked => BlockedReason.Length > 0;

    /// <summary>源头卡（就是被点开的那个节点本身，Depth=0）。</summary>
    public bool IsSource => Depth == 0;
}

/// <summary>
/// 一棵引用树：源头节点 + 各层引用 + 父子关系。只描述**数据**，不含位置——
/// 摆在哪儿由各自的界面算（画布上的浮层与临时画布的树形排版规则不一样）。
/// </summary>
public sealed record ReferenceTree(
    Guid OwnerNodeId,
    string OwnerTitle,
    string OwnerKey,
    IReadOnlyList<ReferenceCard> Cards,
    string Summary,
    bool Truncated)
{
    public static readonly ReferenceTree Empty = new(Guid.Empty, string.Empty, string.Empty, Array.Empty<ReferenceCard>(), string.Empty, false);

    public bool IsEmpty => Cards.Count == 0;

    /// <summary>直接引用（画布上的浮层只看这一层）。</summary>
    public IReadOnlyList<ReferenceCard> Direct => Cards.Where(card => card.Depth == 1).ToList();

    public int MaxDepth => Cards.Count == 0 ? 0 : Cards.Max(card => card.Depth);
}

/// <summary>引用树的构建参数：递归到第几层、总共最多几张卡。</summary>
public sealed record ReferenceTreeLimits(int MaxDepth = 3, int MaxCards = 60);

/// <summary>
/// 把一个节点的引用展开成一张表（树）：直接引用 + 引用的设定自己带的子引用（衣服、配饰……）。
///
/// 为什么是纯计算：它不认识任何界面技术、也不改任何状态——画布上的浮层与「临时引用画布」
/// 用的是同一份结果，而它自身可以脱离界面测。**它绝不新增节点、连线、锚点或落盘字段。**
/// </summary>
public static class CanvasReferenceLayout
{
    /// <summary>直接引用的顺序：先按种类分组（角色 → 场景 → 道具），组内保留作者写的顺序。</summary>
    private static int KindRank(EntityKind kind) => kind switch
    {
        EntityKind.Character => 0,
        EntityKind.Scene => 1,
        _ => 2
    };

    /// <summary>只展开直接引用（画布上的浮层用这一条）。</summary>
    public static ReferenceTree Direct(WorkflowCanvasState state, WorkflowNode node) =>
        Build(state, node, new ReferenceTreeLimits(MaxDepth: 1));

    public static ReferenceTree Build(WorkflowCanvasState state, WorkflowNode node, ReferenceTreeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(node);
        var bounds = limits ?? new ReferenceTreeLimits();

        var ownerKey = "node:" + node.Id.ToString("N");
        var source = new ReferenceCard(
            "n1",
            null,
            ownerKey,
            0,
            1,
            Guid.Empty,
            Guid.Empty,
            null,
            "节点",
            node.Category,
            Title(node),
            string.IsNullOrWhiteSpace(node.Chapter) ? "源头节点" : node.Chapter,
            FirstLine(node.Content),
            string.Empty,
            node.References.Count);

        var cards = new List<ReferenceCard> { source };
        var truncated = false;
        var counter = 1;

        // 深度优先展开：同层按「种类分组 + 组内原序」排，读起来就是一棵从上到下的表。
        // 环用**当前分支的祖先链**挡，而且是指向祖先就整张卡都不画（不是只停止往下走）：
        // 只停递归的话 A→B→A→B 会一直往返到层数上限，把画布铺满没用的重复卡。
        // 不是全局去重：同一个道具被两个角色引用时，两边都该看到它。
        void Visit(string parentId, IReadOnlyList<NodeReference> references, int depth, HashSet<string> branch)
        {
            foreach (var (reference, content) in Order(state, references))
            {
                if (depth > bounds.MaxDepth) return;

                var key = KeyOf(reference);
                if (branch.Contains(key)) continue;
                if (cards.Count >= bounds.MaxCards) { truncated = true; return; }

                counter++;
                var id = "n" + counter;
                cards.Add(new ReferenceCard(
                    id,
                    parentId,
                    key,
                    depth,
                    counter,
                    reference.EntityId,
                    reference.VariantId,
                    reference.VariantVersionId,
                    content is null ? "失效" : WorkflowEntity.KindName(content.Entity.Kind),
                    content is null ? NodeCategory.General : NodeKindPalette.CategoryOf(content.Entity.Kind),
                    content?.Entity.Name ?? "引用失效",
                    content is null
                        ? "目标设定不存在，或已被移出项目"
                        : $"{content.Variant.Name} · {content.VersionLabel}",
                    content is null ? string.Empty : FirstLine(content.Description),
                    content is null ? "引用失效" : content.VersionMissing ? "锁定版本缺失" : string.Empty,
                    content?.References.Count ?? 0));

                if (content is null || content.References.Count == 0) continue;
                branch.Add(key);
                Visit(id, content.References, depth + 1, branch);
                branch.Remove(key);
            }
        }

        Visit("n1", node.References, 1, new HashSet<string>(StringComparer.Ordinal) { ownerKey });

        return new ReferenceTree(node.Id, Title(node), ownerKey, cards, Summarize(node, cards), truncated);
    }

    /// <summary>同一层的顺序：先按种类分组，**组内保留引用原来的顺序**——种类相同的排在一起才叫「排好」，
    /// 而组内不替作者重排（他写「老巷、林晚」是有意的）。解析不出来的排最后。</summary>
    private static IEnumerable<(NodeReference Reference, ReferenceContent? Content)> Order(
        WorkflowCanvasState state,
        IReadOnlyList<NodeReference> references) =>
        references
            .Select((reference, index) => (Reference: reference, Content: state.ResolveReferenceContent(reference), Index: index))
            .OrderBy(item => item.Content is null ? 9 : KindRank(item.Content.Entity.Kind))
            .ThenBy(item => item.Index)
            .Select(item => (item.Reference, item.Content));

    /// <summary>去重与连线的稳定标识：实体 / 变体 / 锁定版本（跟随最新与锁到版本要分开）。</summary>
    public static string KeyOf(NodeReference reference) =>
        $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";

    private static string Summarize(WorkflowNode node, IReadOnlyList<ReferenceCard> cards)
    {
        var direct = cards.Where(card => card.Depth == 1).ToList();
        if (node.References.Count == 0) return $"「{Title(node)}」没有引用";

        var groups = direct
            .GroupBy(card => card.Kind)
            .Select(group => $"{group.Key} {group.Count()}")
            .ToList();
        var text = $"「{Title(node)}」的 {node.References.Count} 条引用（{string.Join(" · ", groups)}）";
        var nested = cards.Count - 1 - direct.Count;
        if (nested > 0) text += $"，含下级 {nested} 张";
        var blocked = cards.Count(card => card.Blocked);
        if (blocked > 0) text += $"，其中 {blocked} 条失效或版本缺失";
        return text;
    }

    private static string Title(WorkflowNode node) => string.IsNullOrWhiteSpace(node.Title) ? "未命名节点" : node.Title;

    private static string FirstLine(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;
        var index = trimmed.IndexOf('\n');
        var line = index < 0 ? trimmed : trimmed[..index];
        line = line.Trim();
        return line.Length <= 60 ? line : line[..60] + "…";
    }
}
