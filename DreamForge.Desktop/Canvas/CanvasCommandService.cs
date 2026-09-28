using System.Text.Json;

namespace DreamForge.Desktop;

public sealed record CreateCanvasNodeCommand(
    string Title = "新节点",
    NodeCategory Category = NodeCategory.General,
    string Content = "",
    float X = 80,
    float Y = 80);

public sealed record CreateCanvasEdgeCommand(
    Guid SourceNodeId,
    Guid TargetNodeId,
    int SourcePort = 0,
    int TargetPort = 0);

public sealed record CanvasStateSnapshot(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges);

/// <summary>提供不依赖 UI 控件的画布读取、编辑、保存与快照撤销命令。</summary>
public sealed class CanvasCommandService
{
    private static readonly JsonSerializerOptions SnapshotOptions = new();
    private readonly Stack<string> undoSnapshots = new();
    private readonly WorkflowCanvasState canvas;

    public CanvasCommandService(WorkflowCanvasState canvas)
    {
        this.canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
    }

    /// <summary>读取当前节点与连线集合的深拷贝快照，调用方修改结果不会改动画布。</summary>
    public CanvasStateSnapshot ReadCurrentState()
    {
        var copy = Clone(canvas);
        return new CanvasStateSnapshot(copy.Nodes.AsReadOnly(), copy.Edges.AsReadOnly());
    }

    /// <summary>创建节点并返回其领域模型；该操作会记录完整画布以支持撤销。</summary>
    public WorkflowNode CreateNode(CreateCanvasNodeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!float.IsFinite(command.X) || !float.IsFinite(command.Y))
            throw new ArgumentOutOfRangeException(nameof(command), "节点坐标必须是有限数值。");

        RecordUndo();
        var node = new WorkflowNode
        {
            Title = string.IsNullOrWhiteSpace(command.Title) ? "新节点" : command.Title.Trim(),
            Category = command.Category,
            Content = command.Content ?? string.Empty,
            X = command.X,
            Y = command.Y
        };
        canvas.Nodes.Add(node);
        return node;
    }

    /// <summary>创建连线；要求两个端点存在且端口索引有效。</summary>
    public WorkflowEdge CreateEdge(CreateCanvasEdgeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var source = canvas.Nodes.FirstOrDefault(node => node.Id == command.SourceNodeId)
            ?? throw new ArgumentException("源节点不存在。", nameof(command));
        var target = canvas.Nodes.FirstOrDefault(node => node.Id == command.TargetNodeId)
            ?? throw new ArgumentException("目标节点不存在。", nameof(command));
        if (command.SourcePort < 0 || command.SourcePort >= source.OutputCount)
            throw new ArgumentOutOfRangeException(nameof(command), "源端口索引无效。");
        if (command.TargetPort < 0 || command.TargetPort >= target.InputCount)
            throw new ArgumentOutOfRangeException(nameof(command), "目标端口索引无效。");
        if (canvas.Edges.Any(edge => edge.SourceNodeId == command.SourceNodeId && edge.SourcePort == command.SourcePort
            && edge.TargetNodeId == command.TargetNodeId && edge.TargetPort == command.TargetPort))
            throw new InvalidOperationException("相同端口之间的连线已存在。");

        RecordUndo();
        var edge = new WorkflowEdge
        {
            SourceNodeId = command.SourceNodeId,
            SourcePort = command.SourcePort,
            TargetNodeId = command.TargetNodeId,
            TargetPort = command.TargetPort
        };
        canvas.Edges.Add(edge);
        return edge;
    }

    /// <summary>通过既有画布库保存完整状态，并返回画布文件路径。</summary>
    public string Save(RecentCanvasState state, string? existingPath = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CanvasLibrary.Save(state, existingPath);
    }

    /// <summary>撤销最近一次由本服务创建节点或连线的命令，恢复完整画布内容。</summary>
    public bool Undo()
    {
        if (!undoSnapshots.TryPop(out var json)) return false;
        var previous = JsonSerializer.Deserialize<WorkflowCanvasState>(json, SnapshotOptions)
            ?? throw new InvalidDataException("画布撤销快照无效。");
        canvas.Nodes = previous.Nodes;
        canvas.Edges = previous.Edges;
        canvas.Entities = previous.Entities;
        canvas.WorkTree = previous.WorkTree;
        return true;
    }

    private void RecordUndo() => undoSnapshots.Push(JsonSerializer.Serialize(canvas, SnapshotOptions));

    private static WorkflowCanvasState Clone(WorkflowCanvasState state) =>
        JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(state, SnapshotOptions), SnapshotOptions)
        ?? throw new InvalidDataException("无法读取画布状态。");
}
