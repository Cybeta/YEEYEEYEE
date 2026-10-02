// PointF / RectangleF 来自 System.Drawing.Primitives（基础框架自带）。
// 这里显式 using：本文件要能被 Avalonia 端链接编译，不能依赖 WinForms SDK 的隐式全局 using。
using System.Drawing;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>泳道种类（返工批次 C / C-1）。</summary>
public enum CanvasLaneKind
{
    /// <summary>企划区：剧情源文本与企划/大纲节点，位于章节泳道之外。</summary>
    Planning,

    /// <summary>一个章节的独立泳道。</summary>
    Chapter,

    /// <summary>未分章与只作锚点的资源节点。</summary>
    Unassigned
}

/// <summary>
/// 一条泳道：企划区（泳道外）、某个章节、或未分章区。
/// 章节泳道的身份是稳定的 <see cref="ChapterInfo.Id"/>，不是章节名文本。
/// </summary>
public sealed record CanvasLane(
    CanvasLaneKind Kind,
    Guid? ChapterId,
    string Title,
    int Order,
    IReadOnlyList<Guid> NodeIds);

/// <summary>一个节点的位置改动。</summary>
public sealed record CanvasLayoutChange(Guid NodeId, string Title, float FromX, float FromY, float ToX, float ToY);

/// <summary>布局冲突代码（只增不改）。</summary>
public static class CanvasLayoutCodes
{
    /// <summary>要求局部重排的章节不存在。</summary>
    public const string ChapterMissing = "LAYOUT_CHAPTER_MISSING";

    /// <summary>两个「手动摆放」的节点互相重叠：自动布局不能替用户决定，阻断写入。</summary>
    public const string ManualOverlap = "LAYOUT_MANUAL_OVERLAP";

    /// <summary>布局计划里的节点已不在画布上（计划过期）。</summary>
    public const string StalePlan = "LAYOUT_STALE_PLAN";
}

/// <summary>布局冲突：<see cref="Blocking"/> 为 true 时整批拒绝写入。</summary>
public sealed record CanvasLayoutConflict(Guid NodeId, string Code, string Message, bool Blocking);

/// <summary>布局选项。</summary>
public sealed record CanvasLayoutOptions(
    /// <summary>自动布局覆盖：忽略手动坐标保护，把手动摆放的节点也纳入重排。</summary>
    bool OverrideManual = false);

/// <summary>
/// 章节泳道布局计划（只读预览）：把节点位置算好但不改动画布。
/// <see cref="CanvasSwimlaneLayout.Apply"/> 才在副本上写入坐标。
/// </summary>
public sealed record CanvasLayoutPlan(
    IReadOnlyList<CanvasLane> Lanes,
    IReadOnlyList<CanvasLayoutChange> Changes,
    IReadOnlyList<Guid> ProtectedNodeIds,
    IReadOnlyList<CanvasLayoutConflict> Conflicts,
    bool WholeCanvas)
{
    public bool HasBlockingConflicts => Conflicts.Any(item => item.Blocking);

    public bool Changed => Changes.Count > 0;

    /// <summary>存在手动摆放的节点：需要显式确认「自动布局覆盖」才写入这些节点。</summary>
    public bool RequiresConfirmation => ProtectedNodeIds.Count > 0;

    public string ToText(WorkflowCanvasState? state = null)
    {
        var lines = new List<string>
        {
            WholeCanvas ? $"整画布泳道布局：{Lanes.Count} 条泳道，改动 {Changes.Count} 项。" : $"章节局部重排：改动 {Changes.Count} 项。"
        };

        if (Lanes.Count > 0 && WholeCanvas)
        {
            lines.Add("泳道顺序：");
            foreach (var lane in Lanes)
                lines.Add($"  · {LaneLabel(lane)}（{lane.NodeIds.Count} 个节点）");
        }

        if (Changes.Count > 0)
        {
            lines.Add("位置改动：");
            foreach (var change in Changes.Take(40))
                lines.Add($"  · {change.Title}：({change.FromX:0},{change.FromY:0}) → ({change.ToX:0},{change.ToY:0})");
            if (Changes.Count > 40) lines.Add($"  …其余 {Changes.Count - 40} 项略");
        }
        else
        {
            lines.Add("· 位置已符合泳道布局，无需改动。");
        }

        if (ProtectedNodeIds.Count > 0)
        {
            lines.Add($"手动摆放（保留原位，需确认覆盖）：{ProtectedNodeIds.Count} 个");
            foreach (var id in ProtectedNodeIds.Take(20))
                lines.Add($"  · {state?.Nodes.FirstOrDefault(node => node.Id == id)?.Title ?? id.ToString()}");
            if (ProtectedNodeIds.Count > 20) lines.Add($"  …其余 {ProtectedNodeIds.Count - 20} 个略");
        }

        if (Conflicts.Count > 0)
        {
            lines.Add("冲突：");
            foreach (var conflict in Conflicts)
                lines.Add($"  · [{(conflict.Blocking ? "阻断" : "提示")}] {conflict.Code} {conflict.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string LaneLabel(CanvasLane lane) => lane.Kind switch
    {
        CanvasLaneKind.Planning => "企划区（泳道外）",
        CanvasLaneKind.Unassigned => "未分章 / 资源锚点",
        _ => lane.Order == 0 ? lane.Title : $"#{lane.Order} {lane.Title}"
    };
}

/// <summary>
/// 章节泳道局部布局（返工批次 C / C-1、C-2）。
///
/// 规则：
/// · 企划节点（剧情源文本 L1、企划/大纲 L2）位于章节泳道之外；
/// · 每个章节一条独立泳道，泳道顺序取 <see cref="CanvasChapters.List"/> 的显式顺序；
/// · 分镜节点在泳道内横向排列，顺序按工作树显式顺序（锚点 <see cref="WorkTreeItem.Order"/>），再按当前阅读顺序；
/// · 成品节点挂在对应分镜下方，靠 <see cref="WorkflowNode.ParentNodeId"/> 关联，不复制任何节点；
/// · 归属按稳定 ID/父链判定（<see cref="CanvasChapters.ResolveChapterId"/>），不按章节名文本绑定；
/// · 手动摆放的节点（<see cref="WorkflowNode.ManualPosition"/>）默认保留原位，其余节点绕开它排列；
/// · 引擎只计算位置，不改动节点身份、锚点、引用与父子关系，也不新增任何节点。
/// </summary>
public static class CanvasSwimlaneLayout
{
    public const float BaseX = 80f;
    public const float BaseY = 80f;
    /// <summary>泳道内左侧/右侧内边距。</summary>
    public const float LanePaddingX = 28f;
    /// <summary>泳道顶部留给标题条的高度。</summary>
    public const float LaneHeaderHeight = 34f;
    /// <summary>泳道底部内边距。</summary>
    public const float LanePaddingBottom = 26f;
    /// <summary>泳道之间的竖向间距。</summary>
    public const float LaneGapY = 72f;
    /// <summary>同一行内相邻节点的横向间距。</summary>
    public const float ColumnGap = 60f;
    /// <summary>成品与它所属分镜之间的竖向间距。</summary>
    public const float ProductGapY = 26f;

    /// <summary>按章节把画布上的节点分到各条泳道。企划区恒为第一条；章节泳道按显式顺序；未分章排最后。</summary>
    public static IReadOnlyList<CanvasLane> Lanes(WorkflowCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var planning = new List<Guid>();
        var unassigned = new List<Guid>();
        var byChapter = new Dictionary<Guid, List<Guid>>();

        foreach (var node in state.Nodes)
        {
            if (IsPlanningCategory(node.Category)) { planning.Add(node.Id); continue; }
            if (IsResourceCategory(node.Category)) { unassigned.Add(node.Id); continue; }
            var chapterId = CanvasChapters.ResolveChapterId(state, node);
            if (chapterId is not { } id) { unassigned.Add(node.Id); continue; }
            if (!byChapter.TryGetValue(id, out var members)) byChapter[id] = members = new List<Guid>();
            members.Add(node.Id);
        }

        var lanes = new List<CanvasLane>
        {
            new(CanvasLaneKind.Planning, null, "企划区", 0, planning)
        };

        foreach (var chapter in CanvasChapters.List(state))
        {
            var members = byChapter.TryGetValue(chapter.Id, out var found) ? found : new List<Guid>();
            lanes.Add(new CanvasLane(CanvasLaneKind.Chapter, chapter.Id, chapter.Name, chapter.Order, members));
        }

        lanes.Add(new CanvasLane(CanvasLaneKind.Unassigned, null, "未分章 / 资源锚点", int.MaxValue, unassigned));
        return lanes;
    }

    /// <summary>企划类节点：剧情源文本与企划/大纲。它们位于章节泳道之外。</summary>
    public static bool IsPlanningCategory(NodeCategory category) =>
        category is NodeCategory.StoryPlan or NodeCategory.StoryOutline;

    /// <summary>资源类节点（角色/场景/道具）只作锚点，不进章节泳道。</summary>
    public static bool IsResourceCategory(NodeCategory category) =>
        category is NodeCategory.Character or NodeCategory.Scene or NodeCategory.Prop;

    /// <summary>整画布泳道布局：从左上角开始按泳道自上而下排列。</summary>
    public static CanvasLayoutPlan PlanAll(WorkflowCanvasState state, CanvasLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        options ??= new CanvasLayoutOptions();
        var lanes = Lanes(state);
        var positions = new Dictionary<Guid, PointF>();
        var reserved = new Dictionary<Guid, RectangleF>();
        var changes = new List<CanvasLayoutChange>();
        var protectedIds = new List<Guid>();

        var laneY = BaseY;
        foreach (var lane in lanes)
        {
            var members = lane.NodeIds
                .Select(id => state.Nodes.FirstOrDefault(node => node.Id == id))
                .Where(node => node is not null)
                .Select(node => node!)
                .ToList();
            if (members.Count == 0) continue;

            var height = LayoutLane(members, BaseX, laneY, state, options, positions, reserved, protectedIds);
            laneY += height + LaneGapY;
        }

        CollectChanges(state, positions, changes);
        var conflicts = DetectConflicts(state, reserved, protectedIds, options);
        return new CanvasLayoutPlan(lanes, changes, protectedIds, conflicts, WholeCanvas: true);
    }

    /// <summary>
    /// 只重排某一个章节的泳道（C-2）：锚定该泳道当前的左上角，其他泳道与其它章节的节点一律不动。
    /// </summary>
    public static CanvasLayoutPlan PlanChapter(WorkflowCanvasState state, Guid chapterId, CanvasLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        options ??= new CanvasLayoutOptions();
        var lanes = Lanes(state);
        if (lanes.All(lane => lane.ChapterId != chapterId))
        {
            var conflict = new CanvasLayoutConflict(chapterId, CanvasLayoutCodes.ChapterMissing, "要重排的章节不存在或已被删除。", true);
            return new CanvasLayoutPlan(lanes, Array.Empty<CanvasLayoutChange>(), Array.Empty<Guid>(), new[] { conflict }, WholeCanvas: false);
        }

        return PlanLane(state, lanes.First(item => item.ChapterId == chapterId), options);
    }

    /// <summary>泳道的左上锚点（含内边距与标题条）；泳道为空时返回 null。</summary>
    public static PointF? LaneTopLeft(WorkflowCanvasState state, CanvasLane lane)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lane);
        var members = lane.NodeIds
            .Select(id => state.Nodes.FirstOrDefault(node => node.Id == id))
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList();
        if (members.Count == 0) return null;
        return new PointF(
            members.Min(node => node.X) - LanePaddingX,
            members.Min(node => node.Y) - LaneHeaderHeight);
    }

    /// <summary>
    /// 只重排某一条泳道：默认锚定该泳道当前左上角（其它泳道一律不动），
    /// 也可传入 <paramref name="anchor"/>（例如 Agent 落位时用「加节点之前」的锚点，避免新节点的默认坐标把泳道拖走）。
    /// 泳道为空且没有锚点时用默认起点。
    /// </summary>
    public static CanvasLayoutPlan PlanLane(
        WorkflowCanvasState state,
        CanvasLane lane,
        CanvasLayoutOptions? options = null,
        PointF? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lane);
        options ??= new CanvasLayoutOptions();
        var lanes = Lanes(state);
        var members = lane.NodeIds
            .Select(id => state.Nodes.FirstOrDefault(node => node.Id == id))
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList();

        var positions = new Dictionary<Guid, PointF>();
        var reserved = new Dictionary<Guid, RectangleF>();
        var protectedIds = new List<Guid>();
        if (members.Count > 0)
        {
            var topLeft = anchor ?? LaneTopLeft(state, lane) ?? new PointF(BaseX, BaseY);
            LayoutLane(members, topLeft.X, topLeft.Y, state, options, positions, reserved, protectedIds);
        }

        var changes = new List<CanvasLayoutChange>();
        CollectChanges(state, positions, changes);
        var conflicts = DetectConflicts(state, reserved, protectedIds, options);
        return new CanvasLayoutPlan(lanes, changes, protectedIds, conflicts, WholeCanvas: false);
    }

    /// <summary>把布局计划写到副本上：只改 X/Y 与手动标记，绝不新增/删除节点或改动引用。</summary>
    public static WorkflowCanvasState Apply(WorkflowCanvasState state, CanvasLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        var clone = CanvasCloner.Clone(state);
        foreach (var change in plan.Changes)
        {
            var node = clone.Nodes.FirstOrDefault(item => item.Id == change.NodeId);
            if (node is null) continue;
            node.X = change.ToX;
            node.Y = change.ToY;
            // 自动布局写回后，这个坐标不再是用户手动摆放的。
            node.ManualPosition = false;
        }
        return clone;
    }

    /// <summary>按当前节点位置算出每条泳道的包围框（含标题条与内边距），供绘制泳道带使用。</summary>
    public static List<(CanvasLane Lane, RectangleF Bounds)> LaneBounds(WorkflowCanvasState state, IReadOnlyList<CanvasLane> lanes)
    {
        ArgumentNullException.ThrowIfNull(state);
        var result = new List<(CanvasLane, RectangleF)>();
        foreach (var lane in lanes)
        {
            if (lane.NodeIds.Count == 0) continue;
            var minX = float.MaxValue; var minY = float.MaxValue; var maxX = float.MinValue; var maxY = float.MinValue;
            foreach (var id in lane.NodeIds)
            {
                var node = state.Nodes.FirstOrDefault(item => item.Id == id);
                if (node is null) continue;
                minX = Math.Min(minX, node.X);
                minY = Math.Min(minY, node.Y);
                maxX = Math.Max(maxX, node.X + CanvasNodeGeometry.NodeBoxWidth);
                maxY = Math.Max(maxY, node.Y + CanvasNodeGeometry.NodeHeightFor(state, node));
            }
            if (minX > maxX) continue;
            result.Add((lane, new RectangleF(
                minX - LanePaddingX,
                minY - LaneHeaderHeight,
                maxX - minX + LanePaddingX * 2,
                maxY - minY + LaneHeaderHeight + LanePaddingBottom)));
        }
        return result;
    }

    // ---- 内部实现 ----

    /// <summary>铺一条泳道，返回泳道高度。手动摆放的节点保留原位并作为障碍物。</summary>
    private static float LayoutLane(
        List<WorkflowNode> members,
        float laneX,
        float laneY,
        WorkflowCanvasState state,
        CanvasLayoutOptions options,
        Dictionary<Guid, PointF> positions,
        Dictionary<Guid, RectangleF> reserved,
        List<Guid> protectedIds)
    {
        var rowY = laneY + LaneHeaderHeight;
        var cursorX = laneX + LanePaddingX;

        // 手动摆放的节点先登记为障碍物并保留原位。
        foreach (var node in members.Where(node => node.ManualPosition && !options.OverrideManual))
        {
            reserved[node.Id] = Rect(state, node);
            protectedIds.Add(node.Id);
        }

        var products = members.Where(node => node.Category == NodeCategory.Product).ToList();
        var flow = members
            .Where(node => node.Category != NodeCategory.Product)
            .OrderBy(node => node.Category == NodeCategory.Chapter ? 0 : node.Category == NodeCategory.Storyboard ? 1 : 2)
            .ThenBy(node => WorkTreeSortKey(state, node))
            .ThenBy(node => node.Y)
            .ThenBy(node => node.X)
            .ThenBy(node => node.Title, StringComparer.Ordinal)
            .ToList();

        foreach (var node in flow)
        {
            if (protectedIds.Contains(node.Id)) continue;
            var rect = new RectangleF(cursorX, rowY, CanvasNodeGeometry.NodeBoxWidth, CanvasNodeGeometry.NodeHeightFor(state, node));
            while (reserved.Values.Any(other => other.IntersectsWith(rect)))
            {
                cursorX = reserved.Values.Where(other => other.IntersectsWith(rect)).Max(other => other.Right) + ColumnGap;
                rect = new RectangleF(cursorX, rowY, CanvasNodeGeometry.NodeBoxWidth, CanvasNodeGeometry.NodeHeightFor(state, node));
            }
            positions[node.Id] = new PointF(rect.X, rect.Y);
            reserved[node.Id] = rect;
            cursorX = rect.Right + ColumnGap;
        }

        // 成品挂在对应分镜下方；父节点不在本泳道或找不到时，排到最后一行。
        var stacked = new Dictionary<Guid, float>();
        var trailingX = laneX + LanePaddingX;
        foreach (var product in products
                     .OrderBy(node => ProductSortKey(state, node))
                     .ThenBy(node => node.Title, StringComparer.Ordinal))
        {
            if (protectedIds.Contains(product.Id)) continue;
            var parentId = product.ParentNodeId;
            if (parentId is { } id && reserved.TryGetValue(id, out var parentRect))
            {
                stacked.TryGetValue(id, out var offset);
                var rect = new RectangleF(parentRect.X, parentRect.Bottom + ProductGapY + offset, CanvasNodeGeometry.NodeBoxWidth, CanvasNodeGeometry.NodeHeightFor(state, product));
                rect = AvoidReserved(rect, reserved.Values, vertical: true);
                positions[product.Id] = new PointF(rect.X, rect.Y);
                reserved[product.Id] = rect;
                stacked[id] = offset + rect.Height + ProductGapY;
            }
            else
            {
                var rect = new RectangleF(trailingX, rowY + RowHeight(state, flow) + ProductGapY, CanvasNodeGeometry.NodeBoxWidth, CanvasNodeGeometry.NodeHeightFor(state, product));
                rect = AvoidReserved(rect, reserved.Values, vertical: false);
                positions[product.Id] = new PointF(rect.X, rect.Y);
                reserved[product.Id] = rect;
                trailingX = rect.Right + ColumnGap;
            }
        }

        var bottom = reserved.Count == 0 ? laneY + LaneHeaderHeight : reserved.Values.Max(rect => rect.Bottom);
        return bottom + LanePaddingBottom - laneY;
    }

    private static RectangleF AvoidReserved(RectangleF rect, IEnumerable<RectangleF> reserved, bool vertical)
    {
        var guard = 0;
        while (reserved.Any(other => other.IntersectsWith(rect)) && guard++ < 500)
        {
            var hits = reserved.Where(other => other.IntersectsWith(rect)).ToList();
            rect = vertical
                ? rect with { Y = hits.Max(other => other.Bottom) + ProductGapY }
                : rect with { X = hits.Max(other => other.Right) + ColumnGap };
        }
        return rect;
    }

    private static float RowHeight(WorkflowCanvasState state, IReadOnlyList<WorkflowNode> row) =>
        row.Count == 0 ? 0f : row.Max(node => CanvasNodeGeometry.NodeHeightFor(state, node));

    private static RectangleF Rect(WorkflowCanvasState state, WorkflowNode node) =>
        new(node.X, node.Y, CanvasNodeGeometry.NodeBoxWidth, CanvasNodeGeometry.NodeHeightFor(state, node));

    /// <summary>工作树显式顺序：锚点条目的 Order；没有锚点时排到最后，再看当前阅读顺序。</summary>
    private static int WorkTreeSortKey(WorkflowCanvasState state, WorkflowNode node)
    {
        if (node.WorkTreeItemId is not { } anchorId) return int.MaxValue;
        return state.WorkTree.FirstOrDefault(item => item.Id == anchorId)?.Order is { } order && order > 0 ? order : int.MaxValue;
    }

    private static int ProductSortKey(WorkflowCanvasState state, WorkflowNode product)
    {
        if (product.ParentNodeId is not { } parentId) return int.MaxValue;
        var parent = state.Nodes.FirstOrDefault(node => node.Id == parentId);
        return parent is null ? int.MaxValue : WorkTreeSortKey(state, parent);
    }

    private static void CollectChanges(WorkflowCanvasState state, Dictionary<Guid, PointF> positions, List<CanvasLayoutChange> changes)
    {
        foreach (var (id, target) in positions)
        {
            var node = state.Nodes.FirstOrDefault(item => item.Id == id);
            if (node is null) continue;
            if (Math.Abs(node.X - target.X) < 0.5f && Math.Abs(node.Y - target.Y) < 0.5f) continue;
            changes.Add(new CanvasLayoutChange(id, node.Title, node.X, node.Y, target.X, target.Y));
        }
    }

    private static List<CanvasLayoutConflict> DetectConflicts(
        WorkflowCanvasState state,
        Dictionary<Guid, RectangleF> reserved,
        List<Guid> protectedIds,
        CanvasLayoutOptions options)
    {
        var conflicts = new List<CanvasLayoutConflict>();
        if (options.OverrideManual) return conflicts;

        // 两个手动摆放的节点互相重叠：自动布局不能替用户取舍，阻断。
        for (var i = 0; i < protectedIds.Count; i++)
        for (var j = i + 1; j < protectedIds.Count; j++)
        {
            if (!reserved.TryGetValue(protectedIds[i], out var left) || !reserved.TryGetValue(protectedIds[j], out var right)) continue;
            if (!left.IntersectsWith(right)) continue;
            var title = state.Nodes.FirstOrDefault(node => node.Id == protectedIds[i])?.Title ?? protectedIds[i].ToString();
            conflicts.Add(new CanvasLayoutConflict(protectedIds[i], CanvasLayoutCodes.ManualOverlap,
                $"手动摆放的节点「{title}」与另一个手动节点重叠，已阻断自动布局，请先手动分开或选择覆盖。", true));
        }
        return conflicts;
    }
}

/// <summary>
/// 泳道布局会话（C-2）：预览（只算不改）→ 应用（副本写入）→ 撤销（内存快照），保存继续走统一保存入口。
/// </summary>
public sealed class CanvasLayoutSession
{
    private static readonly JsonSerializerOptions Options = new();
    private readonly Stack<string> undo = new();
    private string savedSnapshot;
    private bool pendingWholeCanvas = true;
    private Guid? pendingChapterId;
    private CanvasLayoutOptions pendingOptions = new();

    public CanvasLayoutSession(WorkflowCanvasState state, Func<WorkflowCanvasState, bool>? save = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Save = save;
        savedSnapshot = Serialize(State);
    }

    public WorkflowCanvasState State { get; private set; }

    public Func<WorkflowCanvasState, bool>? Save { get; }

    /// <summary>当前待应用的布局计划（预览）；没有预览时为 null。</summary>
    public CanvasLayoutPlan? PendingPlan { get; private set; }

    public bool HasPreview => PendingPlan is { Changed: true };

    public IReadOnlyList<CanvasLayoutChange> PendingChanges => PendingPlan?.Changes ?? Array.Empty<CanvasLayoutChange>();

    public bool CanUndo => undo.Count > 0;

    public string LastSummary { get; private set; } = string.Empty;

    public bool LastBlocked { get; private set; }

    public bool? LastSaveSucceeded { get; private set; }

    public bool HasUnsavedChanges => !string.Equals(Serialize(State), savedSnapshot, StringComparison.Ordinal);

    /// <summary>预览整画布泳道布局（只算不改）。</summary>
    public CanvasLayoutPlan PreviewAll(CanvasLayoutOptions? options = null)
    {
        pendingWholeCanvas = true;
        pendingChapterId = null;
        pendingOptions = options ?? new CanvasLayoutOptions();
        return Preview(CanvasSwimlaneLayout.PlanAll(State, pendingOptions));
    }

    /// <summary>预览单个章节的局部重排（只算不改，其他章节不动）。</summary>
    public CanvasLayoutPlan PreviewChapter(Guid chapterId, CanvasLayoutOptions? options = null)
    {
        pendingWholeCanvas = false;
        pendingChapterId = chapterId;
        pendingOptions = options ?? new CanvasLayoutOptions();
        return Preview(CanvasSwimlaneLayout.PlanChapter(State, chapterId, pendingOptions));
    }

    private CanvasLayoutPlan Preview(CanvasLayoutPlan plan)
    {
        PendingPlan = plan;
        LastBlocked = plan.HasBlockingConflicts;
        LastSummary = plan.ToText(State);
        return plan;
    }

    public void ClearPreview()
    {
        PendingPlan = null;
        LastBlocked = false;
    }

    /// <summary>
    /// 应用当前预览：阻断冲突整批拒绝；存在手动摆放节点时必须显式确认覆盖（确认后按同一范围
    /// 以 <see cref="CanvasLayoutOptions.OverrideManual"/> 重新计算，把手动节点也纳入重排）；
    /// 计划过期的节点视为阻断。成功后在副本上写入并压入撤销快照。
    /// </summary>
    public bool Apply(bool confirmManual = false)
    {
        if (PendingPlan is not { } plan)
        {
            LastBlocked = false;
            LastSummary = "没有待应用的布局预览。";
            return false;
        }

        // 阻断冲突按预览原样判定：即使确认覆盖也不写入（覆盖只解决「保护坐标」，不解决冲突）。
        if (plan.HasBlockingConflicts)
        {
            LastBlocked = true;
            LastSummary = "布局被阻断，画布未改动。" + Environment.NewLine + plan.ToText(State);
            return false;
        }

        if (plan.RequiresConfirmation && !confirmManual)
        {
            LastBlocked = true;
            LastSummary = $"有 {plan.ProtectedNodeIds.Count} 个手动摆放的节点会被移动，需要确认「自动布局覆盖」后才能应用。"
                + Environment.NewLine + plan.ToText(State);
            return false;
        }

        // 确认覆盖：重新计算把手动摆放的节点也纳入重排的计划（原计划出于保护已排除它们）。
        if (confirmManual && !pendingOptions.OverrideManual && plan.RequiresConfirmation)
        {
            var overrideOptions = new CanvasLayoutOptions(OverrideManual: true);
            plan = pendingWholeCanvas || pendingChapterId is null
                ? CanvasSwimlaneLayout.PlanAll(State, overrideOptions)
                : CanvasSwimlaneLayout.PlanChapter(State, pendingChapterId.Value, overrideOptions);
            PendingPlan = plan;
            pendingOptions = overrideOptions;
            LastSummary = plan.ToText(State);
        }

        if (!plan.Changed)
        {
            LastBlocked = false;
            LastSummary = "布局没有产生改动。";
            ClearPreview();
            return false;
        }

        if (plan.Changes.Any(change => State.Nodes.All(node => node.Id != change.NodeId)))
        {
            LastBlocked = true;
            LastSummary = "布局计划已过期（节点已变化），请重新预览。";
            return false;
        }

        undo.Push(Serialize(State));
        State = CanvasSwimlaneLayout.Apply(State, plan);
        LastBlocked = false;
        LastSummary = $"已应用 {plan.Changes.Count} 项位置改动（磁盘未变，保存后写入）。";
        ClearPreview();
        return true;
    }

    /// <summary>撤销最近一次已应用的布局；只改内存，磁盘不变。</summary>
    public bool Undo()
    {
        if (!undo.TryPop(out var json)) return false;
        State = JsonSerializer.Deserialize<WorkflowCanvasState>(json, Options) ?? State;
        PendingPlan = null;
        LastBlocked = false;
        LastSummary = "已撤销最近一次布局改动。";
        return true;
    }

    /// <summary>通过注入的保存回调保存当前状态；返回真实结果。失败时不刷新已保存快照。</summary>
    public bool SaveCurrent()
    {
        if (Save is null) { LastSaveSucceeded = false; return false; }
        var ok = Save(State);
        LastSaveSucceeded = ok;
        if (ok) savedSnapshot = Serialize(State);
        return ok;
    }

    public void Reset()
    {
        undo.Clear();
        PendingPlan = null;
        LastBlocked = false;
        LastSaveSucceeded = null;
        LastSummary = string.Empty;
    }

    private static string Serialize(WorkflowCanvasState state) => JsonSerializer.Serialize(state, Options);
}
