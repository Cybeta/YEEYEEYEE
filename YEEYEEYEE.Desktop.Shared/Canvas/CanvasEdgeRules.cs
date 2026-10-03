namespace YEEYEEYEE.Desktop;

/// <summary>创建连线被拒的原因。它是**领域**说法，不带 HTTP 词汇——两端各自决定怎么呈现。</summary>
public enum EdgeRefusalKind
{
    /// <summary>起点与终点是同一个节点。</summary>
    SelfLoop,

    /// <summary>这两个节点之间已经有连线。</summary>
    Duplicate
}

/// <summary>一条拒绝：种类（给接口层映射错误码）+ 一句可以直接显示给人看的话。</summary>
public readonly record struct EdgeRefusal(EdgeRefusalKind Kind, string Message);

/// <summary>
/// 「能不能连一根线」这条规则只有一份。
///
/// 桌面端拖拽连接（MainWindow 的 CanvasSurface_OnConnectionRequested）与网页端的「连接」动作
/// 都问它：两处各写一遍，同一次操作迟早会在两端得到不同的结果——而这正是「两端同一份规则」
/// 想避免的事。端点存不存在**不在这里**判：那是「这条边读不读得懂」，由画布校验负责，
/// 这里只管「许不许连」。
///
/// 判重**按节点对**（忽略端口）：界面上一个出口只有一根线，端口是协议字段，不是界面概念。
/// 与桌面端拖拽一致——`CanvasCommandService.CreateEdge` 里那份按端口判重的是另一条路（Agent 用），
/// 两条路上的差别是刻意的，别顺手把这里改成按端口。
/// </summary>
public static class CanvasEdgeRules
{
    /// <summary>可以连就返回 null；否则返回原因。</summary>
    public static EdgeRefusal? Refusal(WorkflowCanvasState canvas, Guid sourceId, Guid targetId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (sourceId == targetId) return new EdgeRefusal(EdgeRefusalKind.SelfLoop, "不能连到自身");
        return canvas.Edges.Any(edge => edge.SourceNodeId == sourceId && edge.TargetNodeId == targetId)
            ? new EdgeRefusal(EdgeRefusalKind.Duplicate, "这条连线已经存在")
            : null;
    }
}
