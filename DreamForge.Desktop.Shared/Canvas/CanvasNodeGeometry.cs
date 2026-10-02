using System.Drawing;

namespace DreamForge.Desktop;

/// <summary>
/// 画布节点的几何与摘要计算。
///
/// **为什么单独抽一个类**：这些规则（卡片宽高、占位矩形、附件摘要）原本挂在
/// <c>WorkflowCanvasControl</c>（WinForms 控件）上，但落位算法、AI 提示词组装都要用它们。
/// Avalonia 端要复用同一份 Agent 服务层源码（AgentActions / CanvasSwimlaneLayout 等），
/// 如果规则留在 UI 控件里，服务层就没法脱离 WinForms 编译——于是两份实现必然漂移。
/// 抽到这里之后：数值与公式只有一份，控件只保留转发，服务层不再依赖任何界面技术。
///
/// 只用到 System.Drawing.Primitives（基础框架自带），因此两端都能编译。
/// </summary>
public static class CanvasNodeGeometry
{
    /// <summary>节点卡片在世界坐标里的固定宽度（布局引擎与绘制共用同一数值）。</summary>
    public const float NodeBoxWidth = 190f;

    /// <summary>普通卡片高度。</summary>
    public const float NodeHeight = 100f;

    /// <summary>带图卡片高度。</summary>
    public const float ImageNodeHeight = 176f;

    /// <summary>
    /// 节点卡片高度。放置逻辑与绘制必须共用这一条规则：带图/引用参考图的节点高 176，
    /// 若按固定 100 计算，新建节点会叠到这些节点上。
    /// </summary>
    public static float NodeHeightFor(WorkflowCanvasState state, WorkflowNode node) =>
        node.Attachments.Count > 0 || HasReferenceImage(state, node) ? ImageNodeHeight : NodeHeight;

    /// <summary>节点引用的设定里是否有参考图（决定卡片是否加高）。</summary>
    public static bool HasReferenceImage(WorkflowCanvasState state, WorkflowNode node) =>
        state.ResolveReferences(node)
            .SelectMany(reference => reference.Attachments)
            .Any(attachment => attachment.Kind == AttachmentKind.Image);

    /// <summary>节点在世界坐标里占用的矩形（宽度固定，高度按是否带图）。落位算法用它算占位。</summary>
    public static RectangleF NodeRect(WorkflowCanvasState state, WorkflowNode node) =>
        new(node.X, node.Y, NodeBoxWidth, NodeHeightFor(state, node));

    /// <summary>节点卡片的附件摘要，例如“图片 2 · 视频 1”。</summary>
    public static string AttachmentSummary(WorkflowNode node)
    {
        if (node.Attachments.Count == 0) return string.Empty;
        return string.Join(" · ", node.Attachments
            .GroupBy(attachment => attachment.Kind)
            .OrderBy(group => group.Key)
            .Select(group => $"{WorkflowAttachment.DisplayName(group.Key)} {group.Count()}"));
    }
}
