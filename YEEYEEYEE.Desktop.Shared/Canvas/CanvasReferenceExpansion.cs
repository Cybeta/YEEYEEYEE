namespace YEEYEEYEE.Desktop;

/// <summary>
/// 引用卡临时展开状态（批次 C / C-3）。
///
/// 这是纯内存的界面状态：它不认识 <see cref="WorkflowCanvasState"/>，
/// 所以展开/收起引用**不可能**改动节点、锚点、引用关系或任何落盘字段，
/// 也不会生成常驻画布节点；画布关闭即失效，不进画布文件。
/// </summary>
public sealed class CanvasReferenceExpansionState
{
    private readonly HashSet<Guid> expanded = new();

    /// <summary>当前处于展开状态的节点 ID。</summary>
    public IReadOnlyCollection<Guid> ExpandedNodeIds => expanded;

    public int Count => expanded.Count;

    public bool IsExpanded(Guid nodeId) => expanded.Contains(nodeId);

    /// <summary>切换展开状态，返回切换之后是否处于展开。</summary>
    public bool Toggle(Guid nodeId)
    {
        if (expanded.Remove(nodeId)) return false;
        expanded.Add(nodeId);
        return true;
    }

    public void CollapseAll() => expanded.Clear();
}

/// <summary>
/// 引用查询（C-3）：反向定位与锁定版本缺失检查。纯读取，不改动任何状态，
/// 匹配一律按稳定实体 ID，不按名称。
/// </summary>
public static class CanvasReferences
{
    /// <summary>引用了该资源实体的节点，按画布顺序返回。</summary>
    public static IReadOnlyList<WorkflowNode> NodesReferencing(WorkflowCanvasState state, Guid entityId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Nodes
            .Where(node => node.References.Any(reference => reference.EntityId == entityId))
            .ToList();
    }

    /// <summary>锁定版本缺失的引用：显式锁定了版本号，但该版本已不在变体里（界面显示为阻断状态）。</summary>
    public static IReadOnlyList<(WorkflowNode Node, NodeReference Reference)> MissingLockedVersions(WorkflowCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Nodes
            .SelectMany(node => state.ResolveReferencePairs(node)
                .Where(pair => pair.Reference.VariantVersionId is not null && pair.Content is not null && pair.Content.Version is null)
                .Select(pair => (node, pair.Reference)))
            .ToList();
    }

    /// <summary>某节点上锁定版本缺失的引用条数。</summary>
    public static int MissingLockedVersionCount(WorkflowCanvasState state, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(node);
        return state.ResolveReferencePairs(node)
            .Count(pair => pair.Reference.VariantVersionId is not null && pair.Content is not null && pair.Content.Version is null);
    }

    /// <summary>引用条的版本显示文本：跟随最新 / 版本号 / 版本缺失。</summary>
    public static string VersionLabel(NodeReference reference, ReferenceContent? content) =>
        reference.VariantVersionId is null
            ? "跟随最新"
            : content?.Version is null ? "版本缺失" : content.Version.Label;
}
