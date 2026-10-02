using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>
/// Agent 最近一批尚未审批的变更：动作只显示为画布虚影，真实画布状态保持不变。
/// 保存时才执行动作并写入画布，撤销只需清除虚影。
/// </summary>
public sealed class PendingChanges
{
    private readonly List<AgentAction> actions = new();

    public IReadOnlyList<AgentAction> Actions => actions;
    public int Count => actions.Count;
    public bool IsEmpty => actions.Count == 0;

    /// <summary>
    /// 本批动作的批次号（目标 5）：提交时由 <see cref="AgentCommitLedger"/> 消费，
    /// 保证同一批动作只提交一次。新的一批会换新批次号。
    /// </summary>
    public Guid BatchId { get; private set; } = AgentCommitLedger.NewBatchId();

    public void AddRange(IEnumerable<AgentAction> items) => actions.AddRange(items);

    /// <summary>开始一批新的待审批动作：换新批次号，上一批的提交记录不再影响这一批。</summary>
    public void BeginBatch(IEnumerable<AgentAction> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        BatchId = AgentCommitLedger.NewBatchId();
        actions.Clear();
        actions.AddRange(items);
    }

    /// <summary>
    /// 提交成功后沿用同一个批次号（供撤销预览展示这批动作，但不换号，
    /// 这样同一批再被提交时会被账本拒绝）。
    /// </summary>
    public void AdoptBatch(Guid batchId, IEnumerable<AgentAction> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        BatchId = batchId;
        actions.Clear();
        actions.AddRange(items);
    }

    public bool UpdateCreatedNode(string oldTitle, string title, string content)
    {
        var action = actions.FirstOrDefault(item =>
            string.Equals(item.Kind, "create_node", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Title, oldTitle, StringComparison.Ordinal));
        if (action is null) return false;
        action.Title = title;
        action.Content = content;
        return true;
    }

    /// <summary>保留旧接口以兼容已有调用；新审批流程不支持单条移除。</summary>
    public bool Remove(AgentAction action) => actions.Remove(action);

    public void Clear() => actions.Clear();

    /// <summary>按顺序列出摘要，用于面板显示与模型上下文。</summary>
    public IEnumerable<string> Describe() => actions.Select(action => action.Describe());
}

/// <summary>
/// 旧版待提交改动投影。Agent 新流程直接修改真实画布，不再使用此投影。
/// 它只是差异描述，不参与命中测试，也不改变任何数据。
/// </summary>
public sealed class CanvasPreview
{
    /// <summary>待提交新增的节点（画布上以虚影绘制）。</summary>
    public List<WorkflowNode> AddedNodes { get; } = new();

    /// <summary>待提交删除的现有节点 id。</summary>
    public HashSet<Guid> RemovedNodeIds { get; } = new();

    /// <summary>待提交修改的现有节点 id。</summary>
    public HashSet<Guid> UpdatedNodeIds { get; } = new();

    public List<WorkflowEdge> AddedEdges { get; } = new();
    public HashSet<Guid> RemovedEdgeIds { get; } = new();

    public bool IsEmpty =>
        AddedNodes.Count == 0 && RemovedNodeIds.Count == 0 && UpdatedNodeIds.Count == 0
        && AddedEdges.Count == 0 && RemovedEdgeIds.Count == 0;
}

/// <summary>
/// 把待提交的操作试算到一份画布副本上，再与当前画布对比，得到预览差异。
/// 试算过程**绝不触碰真实画布，也不写文件**。
/// </summary>
public static class CanvasPreviewBuilder
{
    public static CanvasPreview Build(IReadOnlyList<AgentAction> actions, WorkflowCanvasState current)
    {
        var preview = new CanvasPreview();
        if (actions.Count == 0) return preview;

        var working = Clone(current);
        // 预览阶段跳过文件写入：预览不能有副作用。
        AgentActionExecutor.Apply(actions, working, null, FileWriteMode.Skip);

        var currentNodes = current.Nodes.ToDictionary(node => node.Id);
        var previewNodes = working.Nodes.ToDictionary(node => node.Id);
        foreach (var node in working.Nodes)
        {
            if (!currentNodes.TryGetValue(node.Id, out var existing)) { preview.AddedNodes.Add(node); continue; }
            if (NodeChanged(existing, node)) preview.UpdatedNodeIds.Add(node.Id);
        }
        foreach (var node in current.Nodes)
            if (!previewNodes.ContainsKey(node.Id)) preview.RemovedNodeIds.Add(node.Id);

        var currentEdges = current.Edges.Select(edge => edge.Id).ToHashSet();
        var previewEdges = working.Edges.Select(edge => edge.Id).ToHashSet();
        foreach (var edge in working.Edges)
            if (!currentEdges.Contains(edge.Id)) preview.AddedEdges.Add(edge);
        foreach (var edge in current.Edges)
            if (!previewEdges.Contains(edge.Id)) preview.RemovedEdgeIds.Add(edge.Id);

        return preview;
    }

    /// <summary>深拷贝画布状态（JSON 往返）。预览试算与撤销快照都用它。</summary>
    public static WorkflowCanvasState Clone(WorkflowCanvasState source) =>
        JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(source)) ?? new WorkflowCanvasState();

    private static bool NodeChanged(WorkflowNode before, WorkflowNode after) =>
        !string.Equals(before.Title, after.Title, StringComparison.Ordinal)
        || !string.Equals(before.Content, after.Content, StringComparison.Ordinal)
        || before.IsLocked != after.IsLocked
        || before.VersionDecision != after.VersionDecision
        || before.WorkTreeItemId != after.WorkTreeItemId
        || !before.Attachments.Select(item => item.Reference)
            .SequenceEqual(after.Attachments.Select(item => item.Reference))
        || !before.References.Select(DescribeReference)
            .SequenceEqual(after.References.Select(DescribeReference));

    private static string DescribeReference(NodeReference reference) =>
        $"{reference.EntityId}|{reference.VariantId}|{reference.VariantVersionId}";
}

/// <summary>被 Agent 覆盖或新建的文件快照，撤销时用于恢复原内容或删除新文件。</summary>
public sealed record FileSnapshot(string Path, string? OriginalContent)
{
    /// <summary>
    /// 按快照把文件恢复回去：原本不存在就删除，原本有内容就写回。
    /// 返回未能恢复的说明（空表示全部恢复）。**撤销与补偿只用这一份实现**，
    /// 免得界面路径和测试路径出现两套语义。
    ///
    /// 返工 S3：同一个路径可能被同一批动作写过多次（原始 → A → B），正序回写会停在中间态（最终变成 A）。
    /// 因此**按路径只认最早的快照**——最早那份才是提交前的状态；新建文件的第一个快照是 null，
    /// 于是“恢复”就等于把它删掉，语义也对得上。
    /// </summary>
    public static IReadOnlyList<string> RestoreAll(IEnumerable<FileSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var earliest = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
            if (!earliest.ContainsKey(snapshot.Path)) earliest[snapshot.Path] = snapshot;

        var failures = new List<string>();
        foreach (var snapshot in earliest.Values)
        {
            try
            {
                if (snapshot.OriginalContent is null)
                {
                    if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path);
                    continue;
                }

                File.WriteAllText(snapshot.Path, snapshot.OriginalContent);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add($"{snapshot.Path}：{error.Message}");
            }
        }
        return failures;
    }
}

/// <summary>
/// 一次 Agent 提交的记录，用于撤销。
/// 用「整体快照」而不是为每种操作写逆操作：实现简单且不会漏掉某类副作用。
/// </summary>
public sealed class AgentCommitRecord
{
    public DateTimeOffset CommittedAt { get; init; } = DateTimeOffset.Now;
    public int AppliedCount { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// 这次提交属于哪一批（返工 R3）：撤销本批时要判断「当前待处理清单」与「已落画的记录」是不是同一批，
    /// 免得把别的批次的改动当成这一批回退掉。
    /// </summary>
    public Guid BatchId { get; init; }

    /// <summary>提交前的画布快照（含标题、修订号与出图参数）。</summary>
    public string SnapshotBefore { get; init; } = string.Empty;

    /// <summary>提交前主窗体的修订号。只有修订号仍等于它 +1 时才允许撤销。</summary>
    public int RevisionAtCommit { get; init; }

    public List<FileSnapshot> FileSnapshots { get; init; } = new();

    /// <summary>
    /// 本批提交时被移出的资产（返工 U1）：走应用管理的回收目录，撤销时按记录原样移回。
    /// 系统回收站不可控（用户清空即永久丢失、也拿不到目标路径），因此不再使用。
    /// </summary>
    public List<AssetMove> AssetMoves { get; init; } = new();

    /// <summary>
    /// 这批改动属于哪个画布（返工 U3/V3）。撤销与提交都必须作用在同一个画布上：
    /// 否则在 A 画布提交、切到 B 再撤销，会把 A 的快照套到 B 上。
    /// 取的是**标签的稳定 Id**（不是文件路径）：未命名画布没有路径、另存为之后路径又会变，
    /// 用路径当身份会让「同一个画布」看起来是两个、或两个未命名画布看起来是同一个。
    /// </summary>
    public string CanvasKey { get; init; } = string.Empty;

    /// <summary>
    /// 这份记录的**画布部分**是否已经回退过（返工 R16-3）。
    /// 恢复失败后用户重试撤销时，画布不能再套一次提交前快照——那会把用户在两次尝试之间做的编辑
    /// 一起回退掉；置为 true 之后重试只继续恢复剩余的文件与资产。
    /// </summary>
    public bool CanvasRestored { get; set; }
}
