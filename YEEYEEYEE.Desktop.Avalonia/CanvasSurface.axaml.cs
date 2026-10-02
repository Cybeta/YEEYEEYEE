using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

public sealed record CanvasResourceDrop(Guid EntityId, Guid VariantId, Guid? WorkTreeItemId, Point Position);

/// <summary>
/// 点了节点下方的引用预览框：哪个节点的哪条引用、那个设定现在有哪些媒体。
/// 界面据此开窗放大图片 / 播放视频——画布只负责说「点了什么」，不自己开窗。
/// </summary>
public sealed record CanvasReferencePreview(
    WorkflowNode Node,
    WorkflowEntity Entity,
    WorkflowEntityVariant Variant,
    IReadOnlyList<WorkflowAttachment> Media);

/// <summary>节点上的一个连接点（左=输入、右=输出）。</summary>
public sealed record CanvasPort(WorkflowNode Node, bool IsInput);

/// <summary>右键请求：只带「点了哪个节点」。菜单本身贴在鼠标位置弹出（PopupFlyoutBase.ShowAt 的 showAtPointer）。</summary>
public sealed record CanvasNodeContextRequest(WorkflowNode Node);

public partial class CanvasSurface : UserControl
{
    private const double NodeWidth = 232;
    private const double NodeHeight = 104;
    private const double GridStep = 48;

    public const string ResourceDragFormat = "YEEYEEYEE.ResourceReference";

    /// <summary>选择态与品牌光效的主色。种类色见 <see cref="NodeKindPalette"/>（一处定义、三处共用）。</summary>
    private static readonly Color AccentPrimary = Color.Parse("#4D9BFF");

    private WorkflowCanvasState? state;
    private WorkflowNode? selectedNode;

    /// <summary>当前选中的连线（与节点二选一）：删掉一条线得先能选中它。</summary>
    private WorkflowEdge? selectedEdge;

    /// <summary>每条连线的三根线（本体 / 流光 / 透明的加粗命中线），拖动节点时按 id 精确更新。</summary>
    private readonly Dictionary<Guid, (Line Body, Line Beam, Line Hit)> edgeVisuals = new();
    private WorkflowNode? draggingNode;

    /// <summary>正在拖动的卡片控件（重建后会换成新控件，所以每次按下都重新取）。</summary>
    private Border? draggingCard;
    private Point dragOrigin;
    private double nodeOriginX;
    private double nodeOriginY;
    private bool dragSnapshotRecorded;
    private bool dragMoved;
    private WorkflowNode? connectionSource;
    private bool connectionMode;

    /// <summary>正在从一个端口拖出来的连线：起点节点 + 是从输入端口还是输出端口出发。</summary>
    private WorkflowNode? portDragNode;
    private bool portDragFromInput;
    private Line? portPreview;

    private double zoom = 0.86;
    private Point pan = new(24, 24);
    private bool panning;
    private bool pendingFit;

    /// <summary>当前制作阶段：只影响「画哪些节点」，不动画布数据（筛选不是删除）。</summary>
    private ProductionStage stageFilter = ProductionStage.All;

    /// <summary>
    /// 待审批改动的虚影（旧桌面端的行为）：新增的节点画成半透明虚线卡、要改的要删的加虚线框、
    /// 连线用虚线。**只画不写**——审批后虚影消失、真实节点接上；丢弃则虚影直接消失。
    /// 虚影不参与命中测试（点不中、框选不到），所以它不会让用户以为数据已经改了。
    /// </summary>
    private CanvasPreview? pendingPreview;

    /// <summary>
    /// 引用展开的归属节点：点一个有引用的节点，就把它的引用临时铺到画布右边（再点一次收起）。
    /// 这些卡片**不是画布节点**——不写文件、不落盘、不参与命中测试与框选，只是给眼睛看的；
    /// 想真改引用得走「引用画布」（双击带引用的节点）。
    /// </summary>
    private WorkflowNode? referenceExpansionOwner;

    /// <summary>
    /// 是否在节点下方排出「引用的媒体预览」。默认开（工具栏那个勾选框的默认态）。
    /// 它只是**画法**开关：关掉不删任何东西，节点上的「引用 · …」标签照旧在。
    /// </summary>
    private bool showReferencePreviews = true;

    /// <summary>
    /// 单击有引用的节点时要不要铺出引用浮层。默认开。
    ///
    /// **临时引用画布里必须关掉**：那张画布本身就是「把这个节点的引用往外铺」，
    /// 再盖一层压暗浮层有两个后果——①点一下节点整屏变暗，看着像是「点一下就退出了二级画布」；
    /// ②双击的第一下被浮层吃掉（第二下落在压暗层上只会把浮层关掉），
    /// 「双击节点进临时画布」这条入口直接失效。临时画布里要的是「单击选中、双击往下钻」。
    /// </summary>
    private bool referenceOverlayEnabled = true;

    /// <summary>浮层里被选中的那张引用卡（Key）。单击卡片只改这个，**不关浮层**。</summary>
    private string? selectedReferenceCardKey;

    /// <summary>
    /// 每张节点卡最近一次量到的尺寸（世界坐标）。
    /// 卡片是 MinHeight、内容多了会长高，而连线端点在卡片**竖向中线**上，所以得拿真实高度算；
    /// 尺寸要等布局跑完才知道，于是先记下来、量到了再把线挪到新中线（见 <see cref="OnCardSized"/>）。
    /// </summary>
    private readonly Dictionary<Guid, Size> cardSizes = new();

    /// <summary>本次重建算出来的引用树（直接引用那一层）：浮层与提示行共用一份，不重复算。</summary>
    private ReferenceTree? referenceExpansion;

    /// <summary>
    /// 源头节点卡片的真实尺寸（世界坐标，最近一次量到的）。
    /// 节点卡是 MinHeight、内容多了会长高，浮层要按真实尺寸画那张源头卡，所以得缓存着；
    /// 布局还没跑完时先沿用上一次的值，别让卡片在第一次打开时闪一下错尺寸。
    /// </summary>
    private (double Width, double Height) ownerCardSize;

    private double notifiedZoom = -1;
    private Point panOrigin;
    private Point panStart;
    private Point pointerPosition = new(-500, -500);

    private readonly List<Line> beams = new();
    /// <summary>
    /// 正在生成的节点身上的整套光效（外圈白光呼吸 + 一圈绕节点转的光弧）。
    ///
    /// 颜色是**白的**，不用种类色：种类色里场景是绿、道具是黄绿，出图时整张卡片罩着一层绿光，
    /// 看起来像「出错」而不是「在生成」。白色在深色画布上是中性的「有光」，也不跟种类色带抢注意力。
    /// </summary>
    private readonly List<GenerationGlow> glows = new();
    /// <summary>出图虚影的位置登记（供拖动时跟着节点走，见 <see cref="RebuildEdgesForDrag"/>）。</summary>
    private readonly List<BatchGhostLayout> batchGhostVisuals = new();
    /// <summary>
    /// 出图预览窗口的「AI 创作」脉动。跑着的每一格登记一份，由同一个 33ms 时钟驱动。
    ///
    /// 为什么要把光效落到**这个窗口**上、而不是只留在节点卡上：用户的眼睛盯着的是这个窗口
    /// （图会从它里面出来），光效在别处闪，等于让他去看另一块地方。中间那行实时读数同理——
    /// 它是这一格里唯一在变的东西，一眼就知道「还在跑、跑了多久」。
    /// </summary>
    private readonly List<BatchPulse> batchPulses = new();
    private readonly DispatcherTimer ambientTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double ambientClock;
    private double scanProgress = -0.35;

    /// <summary>一个正在跑的预览窗口：外圈光晕（可能没有）+ 窗口本体 + 中间那行实时读数 + 来回扫的那条线。</summary>
    private sealed record BatchPulse(
        Border? Glow, Border Frame, TextBlock Headline, Border Sweep, BatchSlot Slot, double Phase);

    /// <summary>
    /// 一个「正在生成」的节点身上的整套光效：<see cref="Glow"/> 是背后那圈白色呼吸光晕，
    /// <see cref="Orbit"/> 是绕着节点转的那圈光弧，<see cref="Sweep"/> 是它的锥形渐变——
    /// 每 33ms 改一次 <c>Angle</c>，光弧就绕着节点转起来了。
    /// </summary>
    private sealed record GenerationGlow(
        WorkflowNode Node, Border Glow, Border Orbit, ConicGradientBrush Sweep, double Phase, ScaleTransform Scale);

    /// <summary>一个出图虚影（或它那一行标题）的位置登记：拖动节点时按「节点坐标 + 偏移」重排才跟得住。</summary>
    private sealed record BatchGhostLayout(WorkflowNode Node, Control Visual, double OffsetX, double OffsetY);

    public event EventHandler<WorkflowNode?>? SelectedNodeChanged;

    /// <summary>选中 / 取消选中一条连线（null = 没有选中的连线）。</summary>
    public event EventHandler<WorkflowEdge?>? EdgeSelectionChanged;

    /// <summary>
    /// 右键点节点：菜单内容由界面负责（内容是 NodeAssistPlanner 按节点与上游设定算出来的），
    /// 画布只负责把「点了哪个节点」告诉出去。
    /// </summary>
    public event EventHandler<CanvasNodeContextRequest>? NodeContextRequested;

    /// <summary>一次性提示（删了线、取消了连接…），由界面写到状态栏。</summary>
    public event EventHandler<string>? Notice;

    /// <summary>是否处在「连接」模式（点起点再点终点），界面用它显示按钮的选中态。</summary>
    public bool IsConnecting => connectionMode;
    public event EventHandler<(WorkflowNode Source, WorkflowNode Target)>? ConnectionRequested;
    public event EventHandler<WorkflowNode>? NodeMutationCompleted;
    public event EventHandler<WorkflowNode>? NodeDoubleClicked;
    public event EventHandler<WorkflowNode>? NodeReferenceDoubleClicked;

    /// <summary>
    /// Esc：画布自己没东西可取消时抛出来（没在连线、没有引用浮层、没有待确认动作）。
    /// 临时引用画布用它退到上一层——画布本身不知道层级的存在，所以不在这里做决定。
    /// </summary>
    public event EventHandler? EscapePressed;
    /// <summary>点了引用预览框（放大图片 / 播放视频）；由界面开窗。</summary>
    public event EventHandler<CanvasReferencePreview>? ReferencePreviewActivated;

    /// <summary>点了节点**自己**出的媒体（放大看）；由界面开窗。</summary>
    public event EventHandler<(string Path, string Title)>? OwnMediaActivated;

    /// <summary>
    /// 要对节点上**某一个附件**动手（删除这一张）。
    /// 画布只说「删哪个节点上的哪一条引用」，移文件与记账的规矩全在主窗口——
    /// 「删除必须真的把文件移走」这条规矩只能有一个地方守着。
    /// </summary>
    public event EventHandler<(Guid NodeId, string Reference)>? OwnMediaDeleteRequested;

    /// <summary>浮层里双击了一张引用卡：以这条引用为源头开临时画布（能改它的名称 / 描述 / 子引用）。</summary>
    public event EventHandler<ReferenceCard>? ReferenceCardDoubleClicked;

    /// <summary>
    /// 浮层里选中了哪张引用卡（<c>null</c> = 没选中任何卡，回到节点本身）。
    ///
    /// 为什么单独一路：引用卡**不是画布节点**，所以它不会走 <see cref="SelectedNodeChanged"/>。
    /// 少了这一路，点卡片只有一圈高亮、右边的检查器一动不动，用户看到的就是「选中了引用节点，
    /// 检查器内容不跟着变」——而卡片上明明写着名字，看着像坏了。
    /// </summary>
    public event EventHandler<ReferenceCard?>? ReferenceCardSelected;

    public event EventHandler<CanvasReferenceActivation>? ReferenceSelected;
    public event EventHandler<CanvasReferenceActivation>? ReferenceDoubleClicked;
    public event EventHandler? CanvasChanged;
    public event EventHandler? BeforeCanvasMutation;
    public event EventHandler<CanvasResourceDrop>? ResourceDropped;

    /// <summary>缩放比例变化（0.86 = 86%），用于同步工具栏读数。</summary>
    public event EventHandler<double>? ZoomChanged;

    public WorkflowCanvasState? State
    {
        get => state;
        set
        {
            state = value;
            selectedNode = null;
            // 虚影属于上一张画布：换画布时必须收掉，否则会拿着 A 画的虚影去看 B 的节点。
            pendingPreview = null;
            // 引用展开也属于上一张画布，同理收掉。
            referenceExpansionOwner = null;
            referenceExpansion = null;
            selectedReferenceCardKey = null;
            Rebuild();
            // **必须把「取消选中」也说出去**：换了画布（进临时画布、往下钻一层、切标签）之后
            // 选中的是「没有」，而右边的检查器还留着上一张画布那个节点的名称与内容——
            // 看起来就是「点了引用节点，右边的编辑器没跟着变」。谁换的画布谁负责说清楚当前选中的是什么。
            ReferenceCardSelected?.Invoke(this, null);
            SelectedNodeChanged?.Invoke(this, null);
        }
    }

    public WorkflowNode? SelectedNode => selectedNode;

    /// <summary>
    /// 浮层里当前那张引用卡（按 Key 找）；浮层没开就返回 null。
    /// 检查器改完设定的名称 / 描述之后要拿**重算过的那张卡**回填（卡上的标题是构建那一刻的快照）。
    /// </summary>
    public ReferenceCard? CurrentReferenceCard(string key) =>
        referenceExpansion?.Cards.FirstOrDefault(card => string.Equals(card.Key, key, StringComparison.Ordinal));

    public double Scale => zoom;
    public string? StatusTextHint { get; private set; }

    public CanvasSurface()
    {
        InitializeComponent();
        EmptyStateText.IsVisible = false;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, SurfaceDragOver);
        DragDrop.AddDropHandler(this, SurfaceDrop);
        DragDrop.SetAllowDrop(NodeCanvas, true);
        DragDrop.AddDragOverHandler(NodeCanvas, SurfaceDragOver);
        DragDrop.AddDropHandler(NodeCanvas, SurfaceDrop);
        ambientTimer.Tick += OnAmbientTick;
        ambientTimer.Start();
    }

    public void Refresh() => Rebuild();

    public void SetScale(double value)
    {
        zoom = Math.Clamp(value, 0.25, 2.2);
        ApplyTransform();
    }

    public void SetConnectionMode(bool enabled)
    {
        connectionMode = enabled;
        connectionSource = null;
        StatusTextHint = enabled ? "请选择连接起点" : null;
        UpdateHint();
    }

    /// <summary>Request a fit; the actual computation runs once layout size is stable.</summary>
    public void FitToContent() => pendingFit = true;

    private void ApplyFit()
    {
        pendingFit = false;
        var nodes = state is null ? new List<WorkflowNode>() : VisibleNodes(state.Nodes);
        if (nodes.Count == 0)
        {
            pan = new Point(40, 40);
            zoom = 1.0;
            ApplyTransform();
            UpdateHint();
            return;
        }
        if (Bounds.Width < 80 || Bounds.Height < 80)
        {
            pendingFit = true;
            return;
        }

        var minX = nodes.Min(n => (double)n.X) - 70;
        var minY = nodes.Min(n => (double)n.Y) - 70;
        var maxX = nodes.Max(n => (double)n.X) + NodeWidth + 70;
        var maxY = nodes.Max(n => (double)n.Y) + NodeHeight + 70;
        var viewWidth = Bounds.Width;
        var viewHeight = Bounds.Height;
        var scale = Math.Clamp(Math.Min(viewWidth / (maxX - minX), viewHeight / (maxY - minY)), 0.25, 1.0);
        zoom = scale;
        pan = new Point(
            (viewWidth - (maxX - minX) * scale) / 2 - minX * scale,
            (viewHeight - (maxY - minY) * scale) / 2 - minY * scale);
        ApplyTransform();
        UpdateHint();
    }

    /// <summary>Delete the currently selected node together with its edges.</summary>
    public WorkflowNode? DeleteSelectedNode()
    {
        if (state is null || selectedNode is null) return null;
        var target = selectedNode;
        BeforeCanvasMutation?.Invoke(this, EventArgs.Empty);
        state.Nodes.Remove(target);
        state.Edges.RemoveAll(edge => edge.SourceNodeId == target.Id || edge.TargetNodeId == target.Id);
        selectedNode = null;
        Rebuild();
        SelectedNodeChanged?.Invoke(this, null);
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return target;
    }

    /// <summary>当前选中的是连线还是节点（给界面上「还有什么可以删」用）。</summary>
    public bool HasSelection => selectedEdge is not null || selectedNode is not null;

    /// <summary>当前连线的两端名字，用于提示文案；没有选中连线时返回 null。</summary>
    public string? DescribeSelectedEdge()
    {
        if (state is null || selectedEdge is null) return null;
        var source = state.Nodes.FirstOrDefault(node => node.Id == selectedEdge.SourceNodeId);
        var target = state.Nodes.FirstOrDefault(node => node.Id == selectedEdge.TargetNodeId);
        return $"{source?.Title ?? "?"} → {target?.Title ?? "?"}";
    }

    /// <summary>
    /// 删除当前选中的东西：**连线优先**，其次是节点。以前只能删节点，
    /// 连错了线就只能撤销整批改动——删一条线不该有这么大的代价。
    /// 返回一句可以直接显示的结论，没东西可删就返回 null。
    /// </summary>
    public string? DeleteSelection()
    {
        if (state is null) return null;

        if (selectedEdge is { } edge)
        {
            var description = DescribeSelectedEdge() ?? "连线";
            BeforeCanvasMutation?.Invoke(this, EventArgs.Empty);
            state.Edges.Remove(edge);
            selectedEdge = null;
            Rebuild();
            EdgeSelectionChanged?.Invoke(this, null);
            CanvasChanged?.Invoke(this, EventArgs.Empty);
            return $"已删除连线：{description}";
        }

        var removed = DeleteSelectedNode();
        return removed is null ? null : $"已删除节点：{removed.Title}（相连的线一起删了）";
    }

    /// <summary>取消当前的选择与「连接」的半截状态（Esc）。返回一句提示，没东西可取消就返回 null。</summary>
    public string? CancelInteraction()
    {
        if (connectionSource is not null)
        {
            connectionSource = null;
            StatusTextHint = null;
            Rebuild();
            return "已取消这次连接：重新点起点即可";
        }
        if (connectionMode)
        {
            connectionMode = false;
            StatusTextHint = null;
            Rebuild();
            return "已退出连接模式";
        }
        // 铺在画布上的引用卡先收：Esc 一次收一层，第二次才取消选择。
        if (referenceExpansionOwner is not null)
        {
            referenceExpansionOwner = null;
            Rebuild();
            return "已收起展开的引用";
        }
        // 临时引用画布里 Esc 的语义是「退回上一层」：这里**不**用选中状态把它吃掉，
        // 否则第一次按 Esc 只取消了选中，看起来像「Esc 没反应」（浮层在这张画布上本来就是关的）。
        if (!referenceOverlayEnabled) return null;
        if (selectedEdge is not null || selectedNode is not null)
        {
            selectedEdge = null;
            selectedNode = null;
            Rebuild();
            SelectedNodeChanged?.Invoke(this, null);
            EdgeSelectionChanged?.Invoke(this, null);
            return "已取消选择";
        }
        return null;
    }

    /// <summary>Create a node at the centre of the current viewport.</summary>
    public WorkflowNode? AddNodeAtViewportCentre(string title, NodeCategory category)
    {
        if (state is null) return null;
        BeforeCanvasMutation?.Invoke(this, EventArgs.Empty);
        var world = new Point(
            (Bounds.Width / 2 - pan.X) / zoom - NodeWidth / 2,
            (Bounds.Height / 2 - pan.Y) / zoom - NodeHeight / 2);
        var node = new WorkflowNode
        {
            Title = title,
            Category = category,
            ExecutionStatus = NodeExecutionStatus.WaitingForUser,
            X = (float)Math.Max(0, world.X),
            Y = (float)Math.Max(0, world.Y),
            ManualPosition = true
        };
        state.Nodes.Add(node);
        selectedNode = node;
        Rebuild();
        SelectedNodeChanged?.Invoke(this, node);
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return node;
    }

    /// <summary>
    /// 按制作阶段过滤画布。界面只负责说「现在选了哪个阶段」，怎么筛由 <see cref="ProductionStageRules"/> 定——
    /// 左栏按钮、画布上方的芯片、画布本身的过滤必须是同一份口径。
    /// </summary>
    public void SetStageFilter(ProductionStage stage)
    {
        if (stageFilter == stage) return;
        stageFilter = stage;

        // 被筛掉的节点不能还保持选中：否则「删除」会对着一个看不见的东西动手。
        if (selectedNode is not null && !IsVisible(selectedNode))
        {
            selectedNode = null;
            SelectedNodeChanged?.Invoke(this, null);
        }
        if (selectedEdge is not null)
        {
            var source = state?.Nodes.FirstOrDefault(node => node.Id == selectedEdge.SourceNodeId);
            var target = state?.Nodes.FirstOrDefault(node => node.Id == selectedEdge.TargetNodeId);
            if (source is null || target is null || !IsVisible(source) || !IsVisible(target)) selectedEdge = null;
        }

        Rebuild();
        // 这里**不发** CanvasChanged：筛选只改「画哪些」，不改画布数据，
        // 发了会被上层当成「画布已修改」，把项目标成脏。
    }

    /// <summary>当前阶段。</summary>
    public ProductionStage StageFilter => stageFilter;

    /// <summary>
    /// 设置待审批虚影（传 null 清除）。虚影由 <see cref="CanvasPreviewBuilder"/> 试算得到，
    /// 画布**只画不写**：审批后宿主会清除虚影并由真实数据接管。
    /// </summary>
    public void SetPendingPreview(CanvasPreview? preview)
    {
        pendingPreview = preview;
        Rebuild();
    }

    /// <summary>
    /// 是否在节点下方排出「引用的媒体预览」（工具栏那个默认勾选的开关）。
    /// 只改画法，不动数据：关掉之后引用标签、引用浮层、双击进临时画布都照旧。
    /// </summary>
    public bool ShowReferencePreviews
    {
        get => showReferencePreviews;
        set
        {
            if (showReferencePreviews == value) return;
            showReferencePreviews = value;
            Rebuild();
        }
    }

    /// <summary>
    /// 取某个节点当前那批候选图（同时出 N 张时用来在卡片上画进度与挑选）。
    ///
    /// 候选图**不放在画布数据里**：它是临时态，写进画布 JSON 的话，程序在挑选前退出一次就会永远留着几张小图。
    /// 所以由主窗口拿一个按节点索引的内存表，画布每次重建都从表里读——重建不会冲掉用户的选中态。
    /// </summary>
    public Func<Guid, NodeImageBatch?>? ImageBatchOf { get; set; }

    /// <summary>
    /// 卡片上点候选图触发的动作：<c>pick:&lt;序号&gt;</c> 选中某一张，<c>redo</c> 全部不选、按原参数重做。
    /// 画布只负责「点了哪里」，保存 / 抛弃 / 重开的规矩都在主窗口那一边。
    /// </summary>
    public Action<Guid, string>? BatchActionRequested { get; set; }

    /// <summary>
    /// 出图「开奖」（设置里的开关，默认关）。
    ///
    /// 开启后：整批出完之前那排卡不出缩略图，出完之后也是**背面朝上**，点一下才走全屏揭晓——
    /// 图要到揭晓里才第一次露面。关闭时是原来的样子：出好一张显示一张，右键挑 / 删 / 重做。
    ///
    /// 画布只按这个标志换画法；「开奖时点了哪一张、剩下的怎么处理」全在主窗口（与
    /// <see cref="BatchActionRequested"/> 的分工一致）。
    /// </summary>
    public bool GachaReveal { get; set; }

    /// <summary>
    /// 单击有引用的节点时要不要铺出引用浮层（临时画布里由主窗口设成 false，原因见字段注释）。
    /// 关掉时如果浮层正开着，会立刻收起——不能留一张盖在临时画布上的浮层。
    /// </summary>
    public bool ReferenceOverlayEnabled
    {
        get => referenceOverlayEnabled;
        set
        {
            if (referenceOverlayEnabled == value) return;
            referenceOverlayEnabled = value;
            if (!value) CollapseReferenceExpansion();
        }
    }

    /// <summary>这个节点在当前阶段下显不显示。</summary>
    public bool IsVisible(WorkflowNode node) => ProductionStageRules.Matches(stageFilter, node);

    /// <summary>当前阶段下真正会画出来的节点。</summary>
    private List<WorkflowNode> VisibleNodes(IReadOnlyList<WorkflowNode> nodes) =>
        stageFilter == ProductionStage.All
            ? nodes.ToList()
            : nodes.Where(node => ProductionStageRules.Matches(stageFilter, node)).ToList();

    /// <summary>
    /// 选中某个节点并把它挪到视野中央（搜索结果、工作树定位都用这条路）。
    /// 节点被当前阶段筛掉时先取消筛选——否则「跳过去」是跳到一个看不见的地方。
    /// </summary>
    public void FocusNode(WorkflowNode node)
    {
        if (state?.Nodes.Contains(node) != true) return;
        if (!IsVisible(node)) SetStageFilter(ProductionStage.All);

        selectedNode = node;
        selectedEdge = null;
        if (Bounds.Width >= 80 && Bounds.Height >= 80)
            pan = new Point(
                Bounds.Width / 2 - (node.X + NodeWidth / 2) * zoom,
                Bounds.Height / 2 - (node.Y + NodeHeight / 2) * zoom);
        Rebuild();
        SelectedNodeChanged?.Invoke(this, node);
    }

    /// <summary>
    /// 定位到**一组**节点：把视野装到它们的整体范围里，并选中第一个。
    ///
    /// 为什么需要「一组」：左栏一个条目常常对应多个节点——点「第1章」是这一章的所有分镜，
    /// 点「人物 · 林晚」是所有引用她的分镜。这时「定位」应该是「把这一片框出来」，
    /// 而不是随便挑一个说「定位到了」。
    /// </summary>
    public void FocusNodes(IReadOnlyList<WorkflowNode> nodes)
    {
        if (state is null || nodes.Count == 0) return;
        var targets = nodes.Where(node => state.Nodes.Contains(node)).ToList();
        if (targets.Count == 0) return;

        // 被制作阶段筛掉的先放出来：跳到看不见的地方等于没定位。
        if (targets.Any(node => !IsVisible(node))) SetStageFilter(ProductionStage.All);

        if (targets.Count == 1)
        {
            FocusNode(targets[0]);
            return;
        }

        var minX = targets.Min(node => (double)node.X);
        var minY = targets.Min(node => (double)node.Y);
        var maxX = targets.Max(node => (double)node.X + NodeWidth);
        var maxY = targets.Max(node => (double)node.Y + NodeHeight);
        const double padding = 60;

        if (Bounds.Width >= 120 && Bounds.Height >= 120)
        {
            var scale = Math.Min(
                (Bounds.Width - padding * 2) / Math.Max(1, maxX - minX),
                (Bounds.Height - padding * 2) / Math.Max(1, maxY - minY));
            zoom = Math.Clamp(scale, 0.25, 1.2);
            pan = new Point(
                padding + (Bounds.Width - padding * 2 - (maxX - minX) * zoom) / 2 - minX * zoom,
                padding + (Bounds.Height - padding * 2 - (maxY - minY) * zoom) / 2 - minY * zoom);
        }

        selectedNode = targets[0];
        selectedEdge = null;
        Rebuild();
        SelectedNodeChanged?.Invoke(this, selectedNode);
    }

    // ---------------------------------------------------------------- rendering

    private void ApplyTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(pan.X, pan.Y));
        group.Children.Add(new ScaleTransform(zoom, zoom));
        NodeCanvas.RenderTransform = group;

        // 引用浮层按视口坐标摆，所以每次平移/缩放/重排都要跟着重画一次（没展开时它就是隐藏的）。
        RebuildReferenceOverlay();

        if (Math.Abs(notifiedZoom - zoom) > 0.0001)
        {
            notifiedZoom = zoom;
            ZoomChanged?.Invoke(this, zoom);
        }
    }

    private void Rebuild()
    {
        if (!IsInitialized) return;
        beams.Clear();
        glows.Clear();
        batchGhostVisuals.Clear();
        // 每一趟重建都会重新登记：不跟着清的话，列表里会攒着一堆已经不在这张画布上的控件，
        // 每 33ms 去改一批看不见的对象。
        batchPulses.Clear();
        edgeVisuals.Clear();
        // 连线被删掉之后，选中的那条也要跟着失效，否则「删除」会对着一个已经不存在的东西动手。
        if (selectedEdge is not null && state?.Edges.Contains(selectedEdge) != true) selectedEdge = null;
        foreach (var child in NodeCanvas.Children.OfType<Control>().Where(c => c != EmptyStateText).ToList())
            NodeCanvas.Children.Remove(child);
        EmptyStateText.IsVisible = false;

        var allNodes = state?.Nodes ?? new List<WorkflowNode>();
        if (state is null)
        {
            referenceExpansion = null;
            ApplyTransform();
            UpdateHint();
            return;
        }

        var nodes = VisibleNodes(allNodes);
        // 有待审批虚影时不算「空画布」：虚影正要铺上来，这时挂一句「暂无节点」会自相矛盾。
        var hasGhosts = pendingPreview is { IsEmpty: false };
        if ((allNodes.Count == 0 && !hasGhosts) || (nodes.Count == 0 && !hasGhosts))
        {
            // 两种空态要分开说：画布本来就空，和「筛完是空的」——后者要告诉人怎么回到全部。
            EmptyStateText.Text = allNodes.Count == 0
                ? ProductionStageRules.EmptyHintOf(ProductionStage.All)
                : ProductionStageRules.EmptyHintOf(stageFilter);
            EmptyStateText.Foreground = new SolidColorBrush(Color.Parse("#5C6A7C"));
            EmptyStateText.FontSize = 14;
            EmptyStateText.IsVisible = true;
            Canvas.SetLeft(EmptyStateText, 60);
            Canvas.SetTop(EmptyStateText, 60);
        }

        AddGrid();
        AddEdges(state.Edges, nodes);
        foreach (var node in nodes)
        {
            var card = CreateNodeCard(node);
            // 卡片量到尺寸后要做两件事：①把「源头节点卡」按真实高度重画（浮层用）；
            // ②把连线端点挪到新的竖向中线上（卡片是 MinHeight，加了引用标签或预览框之后会高出来）。
            var captured = node;
            card.SizeChanged += (_, _) => OnCardSized(captured, card);
            NodeCanvas.Children.Add(card);
        }
        AddGhosts(nodes);
        // 出图虚影跟着一起画：它们浮在源节点上方，随节点移动、随筛选显隐，所以必须在同一趟里重建。
        AddBatchGhosts(nodes);
        // 光效的尺寸要按卡片的**实测**高度定：卡片是 MinHeight，加了引用标签或媒体之后会长高，
        // 按最小值画出来的光晕只包住卡片上半截——看起来就像「光晕没挂在节点上」。
        FitGlows();
        // 引用浮层在 ApplyTransform 里一起刷：它的位置按视口算，平移缩放后必须跟着重排。
        referenceExpansion = ComputeReferenceExpansion(nodes);
        ApplyTransform();
        UpdateHint();
    }

    // ---------------------------------------------------------------- 待审批虚影

    /// <summary>
    /// 把待审批的改动画成虚影（旧桌面端的做法）：新增节点是半透明虚线卡、要改/要删的加虚线框、
    /// 连线用虚线。整层 IsHitTestVisible=false——虚影点不中、也不会被框选，
    /// 免得用户以为改动已经落进画布了。
    ///
    /// 可见性跟真实节点走同一条规则：当前阶段筛掉的节点，它的「将修改/将删除」也不显示，
    /// 否则会出现一个浮在空处的框。
    /// </summary>
    private void AddGhosts(IReadOnlyList<WorkflowNode> visibleNodes)
    {
        if (pendingPreview is not { } preview || preview.IsEmpty) return;

        foreach (var id in preview.UpdatedNodeIds)
            if (RealNode(visibleNodes, id) is { } updated)
                NodeCanvas.Children.Add(CreateGhostMark(updated, "将修改", GhostUpdatedStroke, GhostUpdatedFill));

        foreach (var id in preview.RemovedNodeIds)
            if (RealNode(visibleNodes, id) is { } removed)
                NodeCanvas.Children.Add(CreateGhostMark(removed, "将删除", GhostRemovedStroke, GhostRemovedFill));

        var added = preview.AddedNodes.Where(IsVisible).ToList();
        foreach (var node in added) NodeCanvas.Children.Add(CreateGhostCard(node));

        foreach (var edge in preview.AddedEdges)
            if (CreateGhostEdge(edge, visibleNodes, added, GhostAddedStroke) is { } line)
                NodeCanvas.Children.Add(line);

        foreach (var id in preview.RemovedEdgeIds)
        {
            var edge = state?.Edges.FirstOrDefault(item => item.Id == id);
            if (edge is not null && CreateGhostEdge(edge, visibleNodes, added, GhostRemovedStroke) is { } line)
                NodeCanvas.Children.Add(line);
        }
    }

    private static WorkflowNode? RealNode(IReadOnlyList<WorkflowNode> nodes, Guid id) =>
        nodes.FirstOrDefault(node => node.Id == id);

    private Line? CreateGhostEdge(WorkflowEdge edge, IReadOnlyList<WorkflowNode> realNodes, IReadOnlyList<WorkflowNode> ghostNodes, Color color)
    {
        var source = RealNode(realNodes, edge.SourceNodeId) ?? ghostNodes.FirstOrDefault(node => node.Id == edge.SourceNodeId);
        var target = RealNode(realNodes, edge.TargetNodeId) ?? ghostNodes.FirstOrDefault(node => node.Id == edge.TargetNodeId);
        if (source is null || target is null) return null;
        return new Line
        {
            StartPoint = new Point(source.X + NodeWidth, CardCenterY(source)),
            EndPoint = new Point(target.X, CardCenterY(target)),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 5, 4 },
            Opacity = 0.85,
            IsHitTestVisible = false
        };
    }

    /// <summary>待新增节点：半透明卡片 + 虚线框 + 「将新增」角标。</summary>
    private static Control CreateGhostCard(WorkflowNode node)
    {
        var panel = new Panel { Width = NodeWidth, Height = NodeHeight, Opacity = 0.78, IsHitTestVisible = false };
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(GhostAddedFill),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = node.Title,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#E9EFF7")),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2
                    },
                    new TextBlock
                    {
                        Text = "将新增 · 审批后写入",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(GhostAddedStroke)
                    }
                }
            }
        });
        panel.Children.Add(GhostOutline(GhostAddedStroke));
        Canvas.SetLeft(panel, node.X);
        Canvas.SetTop(panel, node.Y);
        return panel;
    }

    /// <summary>要改 / 要删的现有节点：在真实卡片上盖一层虚线框 + 角标。</summary>
    private static Control CreateGhostMark(WorkflowNode node, string badge, Color stroke, Color fill)
    {
        var panel = new Panel { Width = NodeWidth, Height = NodeHeight, IsHitTestVisible = false };
        panel.Children.Add(new Border { Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(12) });
        panel.Children.Add(GhostOutline(stroke));
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E60D131B")),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 6, 6, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Child = new TextBlock { Text = badge, FontSize = 10, Foreground = new SolidColorBrush(stroke) }
        });
        Canvas.SetLeft(panel, node.X);
        Canvas.SetTop(panel, node.Y);
        return panel;
    }

    private static Rectangle GhostOutline(Color color) => new()
    {
        Width = NodeWidth,
        Height = NodeHeight,
        RadiusX = 12,
        RadiusY = 12,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = 2,
        StrokeDashArray = new AvaloniaList<double> { 6, 4 }
    };

    private static readonly Color GhostAddedStroke = Color.Parse("#4D9BFF");
    private static readonly Color GhostAddedFill = Color.Parse("#2E4D9BFF");
    private static readonly Color GhostUpdatedStroke = Color.Parse("#DFB45A");
    private static readonly Color GhostUpdatedFill = Color.Parse("#2EDFB45A");
    private static readonly Color GhostRemovedStroke = Color.Parse("#E0707E");
    private static readonly Color GhostRemovedFill = Color.Parse("#2EE0707E");

    // ---------------------------------------------------------------- 引用浮层

    /// <summary>
    /// 归属节点不在画布上、或被当前阶段筛掉时整块收起，免得留一堆无主的卡。
    ///
    /// 这里建的是**整棵引用树**（源头 → 第 1 层 → 子引用 → 孙引用，最多 3 层 / 60 张），
    /// 不是只有直接引用：用户点开一层是想看清楚「这个节点引用了谁、那些设定自己又引用了什么」，
    /// 只列直接引用的话，角色下面挂的道具、服装全看不见（早先就是这样，用户报「没有列出子节点的引用」）。
    /// </summary>
    private ReferenceTree? ComputeReferenceExpansion(IReadOnlyList<WorkflowNode> visibleNodes)
    {
        if (state is null || referenceExpansionOwner is null) return null;
        if (!state.Nodes.Contains(referenceExpansionOwner) || !visibleNodes.Contains(referenceExpansionOwner))
        {
            referenceExpansionOwner = null;
            return null;
        }
        var tree = CanvasReferenceLayout.Build(state, referenceExpansionOwner);
        return tree.Direct.Count == 0 ? null : tree;
    }

    /// <summary>
    /// 点一个有引用的节点：把它引用了什么铺到**画布之上的一层**（再点一次收起）。
    /// 只改「画什么」——卡片不是画布节点，不写画布、不进保存、不参与拖拽与框选。
    /// </summary>
    private void ToggleReferenceExpansion(WorkflowNode node)
    {
        // 临时引用画布里不铺浮层（原因见 referenceOverlayEnabled）：那张画布本身就是这棵树，
        // 单击只负责选中，双击负责往下钻。
        if (!referenceOverlayEnabled)
        {
            referenceExpansionOwner = null;
            selectedReferenceCardKey = null;
            ReferenceCardSelected?.Invoke(this, null);
            return;
        }
        if (node.References.Count == 0)
        {
            referenceExpansionOwner = null;
            selectedReferenceCardKey = null;
            ReferenceCardSelected?.Invoke(this, null);
            return;
        }
        var opening = !ReferenceEquals(referenceExpansionOwner, node);
        referenceExpansionOwner = opening ? node : null;
        // 换一个节点展开时清掉上一张被选中的卡：选中状态是「这一屏里的」，不该跨节点留着。
        // 同时告诉界面「引用卡这一路已经没有选中了」，让它回到节点本身（检查器不能停在上一张卡上）。
        if (opening)
        {
            selectedReferenceCardKey = null;
            ReferenceCardSelected?.Invoke(this, null);
        }
        // 展开时必须让它自己先出现在视野里：卡片是浮层（一定看得见），
        // 但「谁引用了这些」要看不见的话，连线就是指向画外的。
        if (referenceExpansionOwner is not null) EnsureNodeInView(node);
    }

    /// <summary>把节点挪进视野（只在它跑到视口外时才动 pan，免得每次点节点画面都跳）。</summary>
    private void EnsureNodeInView(WorkflowNode node)
    {
        if (Bounds.Width < 80 || Bounds.Height < 80) return;
        var x = pan.X + node.X * zoom;
        var y = pan.Y + node.Y * zoom;
        var width = NodeWidth * zoom;
        var height = NodeHeight * zoom;
        const double padding = 28;
        if (x >= padding && y >= padding && x + width <= Bounds.Width - padding && y + height <= Bounds.Height - padding) return;

        // 放到视口左侧三分之一处：右边留给浮层的卡片。
        pan = new Point(
            Math.Max(padding, Bounds.Width / 3 - width / 2) - node.X * zoom,
            Bounds.Height / 2 - (node.Y + NodeHeight / 2) * zoom);
    }

    public void CollapseReferenceExpansion()
    {
        if (referenceExpansionOwner is null) return;
        referenceExpansionOwner = null;
        selectedReferenceCardKey = null;
        // 浮层收了，「引用卡这一路」的选中也到此为止：检查器回到节点本身，而不是停在刚收起的那张卡上。
        ReferenceCardSelected?.Invoke(this, null);
        Rebuild();
    }

    private void ReferenceScrim_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CollapseReferenceExpansion();
        e.Handled = true;
    }

    /// <summary>
    /// 压暗整块原画布。
    ///
    /// 早先这里是「围着源头节点一圈、节点那块留空」的做法（为了让人还能点到那个节点），
    /// 但留空等于让浮层和真实节点卡各画各的，一旦两者尺寸算不一致就「框不住」。
    /// 现在源头节点本身就是浮层里的一张真卡（见 <see cref="CreateSourceCard"/>），
    /// 底下的节点不需要再被点到，所以整块压暗即可，也不用再对齐两套东西。
    /// </summary>
    private void AddScrim(double viewWidth, double viewHeight)
    {
        var scrim = new Border
        {
            Width = viewWidth,
            Height = viewHeight,
            Background = new SolidColorBrush(Color.Parse("#B8060A0F"))
        };
        scrim.PointerPressed += ReferenceScrim_OnPointerPressed;
        ReferenceLayer.Children.Add(Place(scrim, 0, 0));
    }

    /// <summary>
    /// 画引用浮层。位置全部按**视口坐标**算，不跟着世界坐标走——
    /// 上一版把卡片铺在世界坐标上，节点一靠边卡片就跑到画布外，于是「引用的节点看不到」。
    ///
    /// 形状是**一棵从上到下的树**：源头节点 → 它引用的角色 / 场景 / 道具 → 那些设定自己引用的
    /// 道具 / 服装（缩进 + 「└」标记）。早先这里是一个只放**直接引用**的平铺格子，
    /// 子引用压根不出现，用户点开一层只能看到一半的东西。
    /// </summary>
    private void RebuildReferenceOverlay()
    {
        ReferenceLayer.Children.Clear();
        var tree = referenceExpansion;
        var owner = referenceExpansionOwner;
        if (tree is null || tree.IsEmpty || owner is null)
        {
            ReferenceOverlay.IsVisible = false;
            return;
        }

        var viewWidth = Bounds.Width;
        var viewHeight = Bounds.Height;
        if (tree.Direct.Count == 0 || viewWidth < 360 || viewHeight < 280)
        {
            ReferenceOverlay.IsVisible = false;
            return;
        }
        ReferenceOverlay.IsVisible = true;

        const double blockWidth = 306;
        const double margin = 22;
        const double indentStep = 15;

        // 源头节点的**真实尺寸**（世界坐标）：节点卡是 MinHeight，内容多了会长高，
        // 按固定的 104 画就会「框不住」。布局完成后尺寸才准，所以拿不到时先用上次量到的，
        // 再不行才回落固定值，并靠卡片的 SizeChanged 触发一次重画（见 OnOwnerCardSized）。
        var measured = CardFor(owner)?.Bounds ?? default;
        if (measured.Width > 1 && measured.Height > 1) ownerCardSize = (measured.Width, measured.Height);
        var nodeWidthWorld = ownerCardSize.Width > 1 ? ownerCardSize.Width : NodeWidth;
        var nodeHeightWorld = ownerCardSize.Height > 1 ? ownerCardSize.Height : NodeHeight;

        var nodeX = pan.X + owner.X * zoom;
        var nodeY = pan.Y + owner.Y * zoom;
        var nodeWidth = nodeWidthWorld * zoom;
        var nodeHeight = nodeHeightWorld * zoom;

        // 树放节点对面那一半：节点在左半屏就放右边，反之放左边。
        var onRight = nodeX + nodeWidth / 2 < viewWidth / 2;
        var blockX = onRight ? viewWidth - blockWidth - margin : margin;
        blockX = Math.Clamp(blockX, margin, Math.Max(margin, viewWidth - blockWidth - margin));
        var blockY = margin;

        // 压暗原画布：整块压暗，源头节点改由浮层里的真卡代表（底下的节点不用再被点到）。
        AddScrim(viewWidth, viewHeight);

        // 先连线、再放卡：折线要压在卡片下面（跨列时不会盖住别人的字）。
        var anchor = new Point(nodeX + nodeWidth, nodeY + nodeHeight / 2);
        ReferenceLayer.Children.Add(Elbow(anchor, new Point(blockX, Math.Clamp(nodeY + nodeHeight / 2, blockY + 40, blockY + 140))));

        // 源头节点：画成一张**真卡**（原来只画了个空框，既框不准、也看不到节点自己的内容）。
        ReferenceLayer.Children.Add(Place(CreateSourceCard(owner, nodeWidthWorld, nodeHeightWorld), nodeX, nodeY));

        var rows = new StackPanel { Spacing = 6 };
        rows.Children.Add(BuildOverlayHeader(tree, blockWidth - 20));
        // 引用树是深度优先的**前序**：一张卡的子引用紧跟在它后面，所以照原顺序铺下来就是一棵树。
        foreach (var card in tree.Cards)
        {
            if (card.IsSource) continue;
            rows.Children.Add(BuildReferenceRow(card, blockWidth - 20, indentStep));
        }

        // 树可能比屏幕高（3 层 60 张），所以装进可滚动的壳里，并且**壳本身要有底**：
        // 不留底的话，点在行与行的缝里会漏到压暗层上，把整层关掉。
        var shell = new Border
        {
            Width = blockWidth,
            MaxHeight = Math.Max(180, viewHeight - margin * 2),
            Background = new SolidColorBrush(Color.Parse("#E60D131B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#594D9BFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 10, 10, 10),
            Child = new ScrollViewer
            {
                Content = rows,
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
        ReferenceLayer.Children.Add(Place(shell, blockX, blockY));
    }

    /// <summary>
    /// 引用树里的一行：一张卡，深度 ≥ 2 的加缩进与「└ / │」标记。
    /// 标记用等宽字体画，宽度固定，所以同一层的卡片左缘是同一条竖线——看起来才是一棵树而不是一坨缩进。
    /// </summary>
    private Control BuildReferenceRow(ReferenceCard card, double width, double indentStep)
    {
        var indent = Math.Max(0, (card.Depth - 1) * indentStep);
        var control = CreateReferenceCard(card, width - indent);
        if (indent <= 0) return control;

        var row = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
        for (var level = 1; level < card.Depth; level++)
            row.Children.Add(new TextBlock
            {
                // 最后一层用「└」，中间的用「│」：三层的卡就是「│└」，一眼看出它挂在谁下面。
                Text = level == card.Depth - 1 ? "└" : "│",
                Width = indentStep,
                FontSize = 11,
                FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#4A5C74")),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0)
            });
        row.Children.Add(control);
        return row;
    }

    /// <summary>节点卡尺寸变了（内容变多、加了引用标签或预览框……）：浮层里那张源头卡要重新量一次。</summary>
    private void OnOwnerCardSized(WorkflowNode node)
    {
        if (referenceExpansion is null || !ReferenceEquals(node, referenceExpansionOwner)) return;
        RebuildReferenceOverlay();
    }

    /// <summary>
    /// 卡片布局完量到尺寸了：记下来，并在尺寸真的变了时把连线端点挪到新的中线上。
    ///
    /// 为什么要有这一步：卡片是 <c>MinHeight = 104</c>，有引用标签或预览框时会高出一大截，
    /// 而连线端点一直是按固定的 104 算的——卡片一高，线就挂在了卡片上半截，看着像是连错了地方。
    /// 尺寸只有布局跑完才知道，所以先画（按已知尺寸），量到之后再校正一次；之后稳定下来不再重算。
    /// </summary>
    private void OnCardSized(WorkflowNode node, Control card)
    {
        var size = card.Bounds.Size;
        if (size.Width <= 1 || size.Height <= 1) return;

        var changed = !cardSizes.TryGetValue(node.Id, out var previous)
            || Math.Abs(previous.Height - size.Height) > 0.5
            || Math.Abs(previous.Width - size.Width) > 0.5;
        cardSizes[node.Id] = size;

        if (changed)
        {
            RebuildEdgesForDrag();
            // 卡片长高/变矮了，光效要重新贴合（原因见 FitGlows）。
            FitGlows();
        }
        OnOwnerCardSized(node);
    }

    /// <summary>卡片竖向中线（世界坐标）：连线的两端都落在这条线上。</summary>
    private double CardCenterY(WorkflowNode node) =>
        node.Y + (cardSizes.TryGetValue(node.Id, out var size) && size.Height > 1 ? size.Height : NodeHeight) / 2;

    private static T Place<T>(T control, double x, double y) where T : Control
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        return control;
    }

    /// <summary>
    /// 浮层里的「源头节点」：就是被点开的那个节点，画成一张**真卡** ——
    /// 种类色带 + 标题 + 正文摘录 + 「N 条引用」，并按 zoom 缩放，字体与比例跟画布上的节点卡一致。
    ///
    /// 为什么要有它：早先浮层只画了个空框代表源头节点，既框不准（节点卡会长高）、也看不到节点自己的内容。
    /// 现在它就是浮层里的一张卡，双击这张卡进临时引用画布 —— 底下的真节点被压暗层盖住，所以入口就是它。
    /// </summary>
    private Control CreateSourceCard(WorkflowNode node, double worldWidth, double worldHeight)
    {
        var accent = NodeKindBrushes.ColorOf(node.Category);
        var content = new StackPanel { Spacing = 7 };

        // 种类色带：与画布上的节点卡同一套做法（种类在左、代号在右，铺满整行）。
        var kindRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        kindRow.Children.Add(new TextBlock
        {
            Text = CategoryName(node.Category),
            Foreground = new SolidColorBrush(accent),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var code = new TextBlock
        {
            Text = CodeFor(node),
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD")),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(code, 1);
        kindRow.Children.Add(code);
        content.Children.Add(new Border
        {
            Background = NodeKindBrushes.SoftFillOf(node.Category),
            BorderBrush = NodeKindBrushes.SoftStrokeOf(node.Category),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3),
            Child = kindRow
        });

        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(node.Title) ? "未命名节点" : node.Title,
            Foreground = new SolidColorBrush(Color.Parse("#E9EFF7")),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 节点自己的内容 —— 这正是上一版「没有原始节点内容显示」缺的那一块。
        var body = string.IsNullOrWhiteSpace(node.Content) ? node.Chapter : node.Content;
        if (!string.IsNullOrWhiteSpace(body))
            content.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = new SolidColorBrush(Color.Parse("#93A1B3")),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        foot.Children.Add(new TextBlock
        {
            Text = node.References.Count == 0 ? "没有引用" : $"{node.References.Count} 条引用",
            Foreground = new SolidColorBrush(accent),
            FontSize = 10
        });
        var hint = new TextBlock
        {
            Text = "双击改引用",
            Foreground = new SolidColorBrush(Color.Parse("#6F7F94")),
            FontSize = 10
        };
        Grid.SetColumn(hint, 1);
        foot.Children.Add(hint);
        content.Children.Add(foot);

        var card = new Border
        {
            Width = worldWidth,
            MinHeight = worldHeight,
            Background = new SolidColorBrush(Color.Parse("#18222F")),
            BorderBrush = new SolidColorBrush(AccentPrimary),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            BoxShadow = BoxShadows.Parse("0 0 0 1 #594D9BFF, 0 0 30 -6 #9E4D9BFF"),
            // 按世界坐标尺寸画、再整体缩到 zoom：字体与比例才和画布上的节点卡完全一致。
            RenderTransform = new ScaleTransform(zoom, zoom),
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = node,
            Child = content
        };
        card.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            NodeReferenceDoubleClicked?.Invoke(this, node);
        };
        return card;
    }

    /// <summary>「源头节点 → 引用卡」的折线：先横着出来，再纵向对齐卡片中线，最后拐进卡片左边。</summary>
    private static Control Elbow(Point origin, Point end)
    {
        var midX = origin.X + (end.X - origin.X) / 2;
        return new Polyline
        {
            Points = new List<Point> { origin, new Point(midX, origin.Y), new Point(midX, end.Y), end },
            Stroke = new SolidColorBrush(Color.Parse("#B37FB2E8")),
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            IsHitTestVisible = false
        };
    }

    /// <summary>浮层顶部：这是谁的引用、共几张（含几层子引用）、怎么关掉。</summary>
    private Control BuildOverlayHeader(ReferenceTree tree, double width)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            // 说清楚「几项直接引用 + 一共几张卡」：只报一个数字的话，
            // 用户看到树里有一串子引用会以为是重复的卡。
            Text = tree.MaxDepth > 1
                ? $"「{tree.OwnerTitle}」引用了 {tree.Direct.Count} 项（含子引用共 {tree.Cards.Count - 1} 张）"
                : $"「{tree.OwnerTitle}」引用了 {tree.Direct.Count} 项",
            Foreground = new SolidColorBrush(Color.Parse("#E9EFF7")),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        });
        var close = new Button { Content = "收起 ✕", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
        close.Classes.Add("miniButton");
        close.Click += (_, _) => CollapseReferenceExpansion();
        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        var panel = new StackPanel { Width = width, Spacing = 4 };
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = tree.Truncated
                ? "缩进的那些是子引用（角色自己引用的道具 / 服装）。这棵树最多画到第 3 层、60 张，更深的请双击那张卡进临时画布看。"
                : "缩进的那些是子引用（角色自己引用的道具 / 服装）。卡片单击选中（右侧检查器会显示它的内容，可以直接改）；双击以它为源头另开一张临时画布。",
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        });
        // 顶部这一块不再单独套壳：外层（可滚动的那个壳）已经有底了，再套一层只会多一道框。
        return panel;
    }

    /// <summary>
    /// 一张引用卡。单击**选中**（只改高亮，浮层不关——点卡片是想选它，不是想收起来），
    /// 双击以它为源头进临时画布，那里才是改它的地方。
    /// 卡片本身仍然不写画布：选中状态只活在这一次浮层里。
    /// </summary>
    private Control CreateReferenceCard(ReferenceCard card, double width)
    {
        // 失效的引用用错误色；其余按种类取色 —— 与画布节点、临时画布同一份口径。
        var accent = card.Blocked ? Color.Parse("#E0707E") : NodeKindBrushes.ColorOf(card.Category);
        var selected = string.Equals(selectedReferenceCardKey, card.Key, StringComparison.Ordinal);
        var border = new Border
        {
            Width = width,
            // 高度自适应（早先是固定的 142，子引用那几行内容少也占一整格，一棵树看着像一堵墙）。
            MinHeight = 84,
            Background = new SolidColorBrush(selected ? Color.Parse("#1E2B3B") : Color.Parse("#1A2431")),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = card
        };
        if (selected) border.BoxShadow = BoxShadows.Parse("0 0 0 1 #594D9BFF, 0 0 26 -8 #9E4D9BFF");
        border.PointerPressed += ReferenceCardPointerPressed;
        border.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ReferenceCardDoubleClicked?.Invoke(this, card);
        };

        var content = new StackPanel { Spacing = 5 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            Child = new TextBlock { Text = card.Kind, Foreground = new SolidColorBrush(accent), FontSize = 10, FontWeight = FontWeight.SemiBold }
        });
        var order = new TextBlock
        {
            // 深度 ≥ 2 的是「某个设定自己引用的东西」，写清楚它挂在谁下面，不然和直接引用混在一起看。
            Text = card.Depth >= 2 ? $"子引用 #{card.Order - 1}" : $"引用 #{card.Order - 1}",
            Foreground = new SolidColorBrush(Color.Parse("#6F7F94")),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(order, 1);
        head.Children.Add(order);
        content.Children.Add(head);

        content.Children.Add(new TextBlock
        {
            Text = card.Title,
            Foreground = new SolidColorBrush(Color.Parse("#E9EFF7")),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        content.Children.Add(new TextBlock
        {
            Text = card.Subtitle,
            Foreground = new SolidColorBrush(Color.Parse("#93A1B3")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        if (card.Excerpt.Length > 0)
            content.Children.Add(new TextBlock
            {
                Text = card.Excerpt,
                Foreground = new SolidColorBrush(Color.Parse("#7C8A9C")),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        var foot = new List<string>();
        if (card.Blocked) foot.Add($"⚠ {card.BlockedReason}");
        if (card.ChildReferenceCount > 0) foot.Add($"含 {card.ChildReferenceCount} 个子引用");
        if (foot.Count > 0)
            content.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", foot),
                Foreground = new SolidColorBrush(card.Blocked ? Color.Parse("#E0707E") : Color.Parse("#6F7F94")),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });

        border.Child = content;
        return border;
    }

    /// <summary>
    /// 单击浮层里的一张引用卡：**只**改选中高亮，绝不收起浮层；并把「选中了哪张卡」说出去
    /// （检查器据此显示这条引用指向的那个设定）。
    /// （之前卡片完全不接点击，点上去等于没反应；而点偏一点落到压暗层上就直接把浮层收掉了，
    /// 用户看到的就成了「点一下子就退出了」，还以为是二级画布被关掉了。）
    /// </summary>
    private void ReferenceCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ReferenceCard card } border) return;
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            ReferenceCardDoubleClicked?.Invoke(this, card);
            e.Handled = true;
            return;
        }
        selectedReferenceCardKey = string.Equals(selectedReferenceCardKey, card.Key, StringComparison.Ordinal)
            ? null
            : card.Key;
        RebuildReferenceOverlay();
        ReferenceCardSelected?.Invoke(this, selectedReferenceCardKey is null ? null : card);
        e.Handled = true;
    }

    private void AddGrid()
    {
        var grid = new SolidColorBrush(Color.Parse("#0E4D9BFF"));
        const double maxX = 3000;
        const double maxY = 2200;
        for (var x = 0d; x <= maxX; x += GridStep)
            NodeCanvas.Children.Add(new Line { StartPoint = new Point(x, 0), EndPoint = new Point(x, maxY), Stroke = grid, StrokeThickness = 1 });
        for (var y = 0d; y <= maxY; y += GridStep)
            NodeCanvas.Children.Add(new Line { StartPoint = new Point(0, y), EndPoint = new Point(maxX, y), Stroke = grid, StrokeThickness = 1 });
    }

    private void UpdateHint()
    {
        var total = state?.Nodes.Count ?? 0;
        var shown = state is null ? 0 : VisibleNodes(state.Nodes).Count;
        var info = stageFilter == ProductionStage.All
            ? $"{total} 节点 · 拖节点改位置 / 拖端口连线 / 点线选中后 Delete 删除 / 空白处拖拽平移 / Esc 取消"
            : $"筛选「{ProductionStageRules.LabelOf(stageFilter)}」（{ProductionStageRules.RuleOf(stageFilter)}）：显示 {shown} / {total} 个节点，再点一次该阶段回到全部";
        HintText.Text = string.IsNullOrWhiteSpace(StatusTextHint) ? info : $"{StatusTextHint} · {info}";

        // 有待审批改动时把虚影摘要顶在最前面：虚线框是什么、能不能点，都在这句里交代。
        var ghost = DescribePendingPreview();
        if (ghost.Length > 0) HintText.Text = $"待审批虚影：{ghost} · " + HintText.Text;

        // 展开引用时同理：铺出来的卡片是什么、写不写画布、怎么收起来，都写在这一句里。
        if (referenceExpansion is { IsEmpty: false } expansion)
            HintText.Text = $"已展开引用：{expansion.Summary}（含子引用，缩进的那些；浮层只画不写；点空白处、再点一次该节点或 Esc 收起；双击节点进临时画布改引用）· " + HintText.Text;
    }

    private string DescribePendingPreview()
    {
        if (pendingPreview is not { IsEmpty: false } preview) return string.Empty;
        var parts = new List<string>();
        if (preview.AddedNodes.Count > 0) parts.Add($"新增 {preview.AddedNodes.Count}");
        if (preview.UpdatedNodeIds.Count > 0) parts.Add($"修改 {preview.UpdatedNodeIds.Count}");
        if (preview.RemovedNodeIds.Count > 0) parts.Add($"删除 {preview.RemovedNodeIds.Count}");
        if (preview.AddedEdges.Count > 0) parts.Add($"连线 {preview.AddedEdges.Count}");
        if (preview.RemovedEdgeIds.Count > 0) parts.Add($"删线 {preview.RemovedEdgeIds.Count}");
        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts) + "（虚影点不中；审批后写入，丢弃即消失）";
    }

    private void AddEdges(IReadOnlyList<WorkflowEdge> edges, IReadOnlyList<WorkflowNode> nodes)
    {
        var baseBrush = new SolidColorBrush(Color.Parse("#2C3D52"));
        var liveBrush = new SolidColorBrush(Color.Parse("#594D9BFF"));
        var beamBrush = new SolidColorBrush(Color.Parse("#B48CC5FF"));
        var selectedBrush = new SolidColorBrush(Color.Parse("#4D9BFF"));
        var index = 0;
        foreach (var edge in edges)
        {
            var source = nodes.FirstOrDefault(n => n.Id == edge.SourceNodeId);
            var target = nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
            if (source is null || target is null) continue;

            var start = new Point(source.X + NodeWidth, CardCenterY(source));
            var end = new Point(target.X, CardCenterY(target));
            var isSelected = ReferenceEquals(edge, selectedEdge);

            var body = new Line
            {
                StartPoint = start,
                EndPoint = end,
                Stroke = isSelected ? selectedBrush : index % 3 == 0 ? liveBrush : baseBrush,
                StrokeThickness = isSelected ? 3 : 2,
                StrokeLineCap = PenLineCap.Round
            };
            NodeCanvas.Children.Add(body);

            var beam = new Line
            {
                StartPoint = start,
                EndPoint = end,
                Stroke = beamBrush,
                StrokeThickness = 2,
                StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 8, 22 },
                StrokeDashOffset = -index * 7 % 30,
                StrokeLineCap = PenLineCap.Round,
                IsVisible = !isSelected
            };
            NodeCanvas.Children.Add(beam);
            beams.Add(beam);

            // 命中线：2px 的线用鼠标点不中，铺一条透明的粗线专门接点击。放在最后 = 盖在上面。
            // 用 alpha=1 而不是全透明：某些渲染后端下完全透明的画刷不参与命中测试。
            var hit = new Line
            {
                StartPoint = start,
                EndPoint = end,
                Stroke = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                StrokeThickness = 16,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = edge
            };
            ToolTip.SetTip(hit, "点击选中这条连线，再按 Delete（或点工具栏「删除」）移除它");
            hit.PointerPressed += EdgePointerPressed;
            NodeCanvas.Children.Add(hit);

            edgeVisuals[edge.Id] = (body, beam, hit);
            index++;
        }
    }

    private void EdgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Line { Tag: WorkflowEdge edge }) return;
        selectedEdge = edge;
        selectedNode = null;      // 与节点二选一，否则「删除」不知道该删哪个
        Rebuild();
        SelectedNodeChanged?.Invoke(this, null);
        EdgeSelectionChanged?.Invoke(this, edge);
        e.Handled = true;
    }

    private Control CreateNodeCard(WorkflowNode node)
    {
        var accent = AccentFor(node.Category);
        var isSelected = ReferenceEquals(node, selectedNode);
        var isActive = node.ExecutionStatus is NodeExecutionStatus.Generating or NodeExecutionStatus.NeedsReview;

        var border = new Border
        {
            Width = NodeWidth,
            MinHeight = NodeHeight,
            Background = new SolidColorBrush(Color.Parse(isActive ? "#1A2431" : "#131B25")),
            BorderBrush = new SolidColorBrush(isSelected ? AccentPrimary : isActive ? Color.Parse("#594D9BFF") : Color.Parse("#1F2C3D")),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            // 整张卡片都能拖（判别在松手时做，见 OnPointerReleased），光标如实说明这一点：
            // 以前只有从卡片上某几条窄带才抓得动，用户得先猜「哪儿才拖得动」，猜错就弹出二级画布。
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Tag = node
        };
        if (isSelected) border.BoxShadow = BoxShadows.Parse("0 0 0 1 #594D9BFF, 0 0 30 -6 #9E4D9BFF");
        else if (isActive) border.BoxShadow = BoxShadows.Parse("0 0 24 -8 #7A4D9BFF");

        var content = new StackPanel { Spacing = 7 };

        // 分类标签行：整行一条色带。
        // 原来是一个贴着文字的小胶囊（「章节」只有两字宽），扫一眼根本分不出类型；
        // 单纯左右加内边距只是把空的地方撑开，看着更松但并不更醒目。
        // 现在种类在左、节点代号在右，同装在一条带子里铺满行宽 —— 颜色占了整行，类型一眼可辨。
        var kindRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        kindRow.Children.Add(new TextBlock
        {
            Text = CategoryName(node.Category),
            Foreground = new SolidColorBrush(accent),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var code = new TextBlock
        {
            Text = CodeFor(node),
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD")),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(code, 1);
        kindRow.Children.Add(code);
        content.Children.Add(new Border
        {
            // 铺满整行之后底色可以比原来那点小胶囊更实一点：面积大了，太淡会显得脏。
            Background = NodeKindBrushes.SoftFillOf(node.Category),
            BorderBrush = NodeKindBrushes.SoftStrokeOf(node.Category),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3),
            Child = kindRow
        });

        // 标题
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(node.Title) ? "未命名节点" : node.Title,
            Foreground = new SolidColorBrush(Color.Parse("#E9EFF7")),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 摘要
        var summary = string.IsNullOrWhiteSpace(node.Content) ? node.Chapter : node.Content;
        if (!string.IsNullOrWhiteSpace(summary))
            content.Children.Add(new TextBlock
            {
                Text = summary,
                Foreground = new SolidColorBrush(Color.Parse("#93A1B3")),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        foreach (var reference in node.References)
        {
            var activation = new CanvasReferenceActivation(node, reference, state?.ResolveReferenceContent(reference));
            var label = activation.Content?.Label ?? "引用失效";
            var refTag = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#14263F")),
                BorderBrush = new SolidColorBrush(Color.Parse("#594D9BFF")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                Tag = activation,
                Child = new TextBlock { Text = "引用 · " + label, Foreground = new SolidColorBrush(Color.Parse("#A7C7EA")), FontSize = 9 }
            };
            refTag.PointerPressed += ReferenceTagPointerPressed;
            content.Children.Add(refTag);
        }

        // 「你引用的设定改了图」——这条提示紧跟在引用标签后面，因为它说的就是上面那几条引用。
        //
        // 判定的口径是**指纹比对**（出图时把当时那版设定的指纹记在产物上，现在再算一遍比对），
        // 不是「改设定时去通知下游」：通知那条路要覆盖所有能改设定的入口，漏一个就永远不报，
        // 而静默不报正是这个功能要解决的问题。详见 ReferenceStaleness。
        var stale = state is null ? Array.Empty<StaleReference>() : ReferenceStaleness.Of(state, node);
        if (stale.Count > 0)
        {
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2A2010")),
                BorderBrush = new SolidColorBrush(Color.Parse("#7A5A1F")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 3),
                Child = new TextBlock
                {
                    Text = "⟳ " + ReferenceStaleness.Describe(stale),
                    Foreground = new SolidColorBrush(Color.Parse("#E8C67A")),
                    FontSize = 9,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            // 点一下把「哪几条变了」摊开——提示只有一行，但用户要能问「具体是哪一条」。
            // 走状态栏（一行）而不是弹窗：这是「看一眼就走」的信息，不该挡住画布。
            hint.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                Notice?.Invoke(this, $"这一镜照着的设定已经变了：{ReferenceStaleness.DescribeAll(stale)}。"
                    + "重出之后这张产物会重新记下当时用的那一版。");
            };
            ToolTip.SetTip(hint, "点一下看具体是哪几条变了");
            content.Children.Add(hint);
        }
        else if (state is not null && ReferenceStaleness.LacksBaseline(node))
        {
            // 早先出的图没有依据可查。**如实说明**，不用「补记当前指纹」把它填平——
            // 那等于宣称它当时用的就是现在这一版，设定真变过就永远不报了（伪造凭据）。
            var note = new TextBlock
            {
                Text = "⟳ 这些图是早先出的，没记下当时用的设定；重出一次之后才会提示设定变更",
                Foreground = new SolidColorBrush(Color.Parse("#6F7F94")),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap
            };
            ToolTip.SetTip(note, "「引用的设定后来改了图，这一镜该不该重出」是从这一版才开始记录的");
            content.Children.Add(note);
        }

        // 进度条
        var progress = ProgressFor(node);
        if (progress > 0)
        {
            var track = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.Parse("#202D3D")), Margin = new Thickness(0, 2, 0, 0) };
            var fill = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(accent),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                Width = Math.Max(3, (NodeWidth - 28) * progress)
            };
            var wrap = new Panel();
            wrap.Children.Add(track);
            wrap.Children.Add(fill);
            content.Children.Add(wrap);
        }

        // 状态行
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        foot.Children.Add(new TextBlock
        {
            Text = StatusText(node.ExecutionStatus),
            Foreground = StatusBrush(node.ExecutionStatus),
            FontSize = 10
        });
        var locked = new TextBlock
        {
            Text = node.IsLocked ? "已锁定" : "可编辑",
            Foreground = new SolidColorBrush(Color.Parse("#5C6A7C")),
            FontSize = 10
        };
        Grid.SetColumn(locked, 1);
        foot.Children.Add(locked);
        content.Children.Add(foot);

        // 引用预览：勾选「预览引用节点」时，有引用的节点下方排一串小小的媒体预览框。
        // 放在最后（节点卡的「下方」），并且与上面的正文之间加一条淡分隔线——
        // 它是「这个节点引用了谁」，不是这个节点自己的内容，混在一起会读错。
        if (showReferencePreviews && node.References.Count > 0)
        {
            content.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.Parse("#1B2634")),
                Margin = new Thickness(0, 3, 0, 5)
            });
            content.Children.Add(CreateReferencePreviewStrip(node));
        }

        // 这个节点**自己**出的图 / 视频。
        //
        // 加这一块是因为一个真实的困惑：用户出了一张图、选了「用这一张」，界面上却没有任何变化，
        // 于是以为没挂上。数据一直是好的（`node.Attachments` 里就有），只是卡片从来没画过它——
        // 上面那排缩略图画的是「这个节点**引用了谁**」，不是「这个节点**自己出了什么**」。
        var media = node.Attachments.Where(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video).ToList();
        if (media.Count > 0)
        {
            content.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.Parse("#1B2634")),
                Margin = new Thickness(0, 3, 0, 5)
            });
            var strip = new WrapPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
            foreach (var attachment in media)
            {
                var tile = new Border
                {
                    Width = 66,
                    Height = 48,
                    Margin = new Thickness(0, 0, 5, 5),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.Parse("#0E141C")),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
                    ClipToBounds = true,
                    Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
                };
                var bitmap = attachment.Kind == AttachmentKind.Image ? LoadThumbnail(attachment.Reference) : null;
                var isVideo = attachment.Kind == AttachmentKind.Video;
                tile.Child = bitmap is not null
                    ? new Image { Source = bitmap, Stretch = global::Avalonia.Media.Stretch.UniformToFill }
                    : new TextBlock
                    {
                        Text = isVideo ? "▶" : "无图",
                        FontSize = isVideo ? 16 : 9,
                        Foreground = new SolidColorBrush(Color.Parse("#C6D3E2")),
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    };

                var captured = attachment;
                tile.PointerPressed += (_, args) =>
                {
                    // 吃掉事件：不然这一下会冒泡成「选中节点」。
                    args.Handled = true;
                    // 右键：这一张图自己的菜单（放大 / 删除）。删除是「节点上这个附件」的事，
                    // 不牵动节点本身——用户说「已经生成的图片没有删除选项」，指的就是这里。
                    if (args.GetCurrentPoint(tile).Properties.IsRightButtonPressed)
                    {
                        ShowOwnMediaMenu(node, captured, tile);
                        return;
                    }
                    var path = AssetStore.Resolve(captured.Reference) ?? string.Empty;
                    if (path.Length > 0) OwnMediaActivated?.Invoke(this, (path, node.Title));
                };
                ToolTip.SetTip(tile, $"{attachment.Name}\n{attachment.Source}\n（单击放大 · 右键可删除）");
                strip.Children.Add(tile);
            }
            content.Children.Add(strip);
        }

        border.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                CreatePort(node, isInput: true),
                content,
                CreatePort(node, isInput: false)
            }
        };
        // 端口与内容一起排：输入端口贴左边缘、输出端口贴右边缘，靠负外边距各露一半在卡片外，
        // 这样它们随卡片一起移动，不用在拖拽时额外跟着搬。
        var ports = (Grid)border.Child;
        ports.Children[0].SetValue(Grid.ColumnProperty, 0);
        ((Border)ports.Children[0]).Margin = new Thickness(-19, 0, 6, 0);
        ((Border)ports.Children[0]).VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        ports.Children[1].SetValue(Grid.ColumnProperty, 1);
        ((Border)ports.Children[2]).Margin = new Thickness(6, 0, -19, 0);
        ((Border)ports.Children[2]).VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        ports.Children[2].SetValue(Grid.ColumnProperty, 2);

        // 正在生成的节点：**背后**一圈白色呼吸光晕 + 一圈绕着节点转的光弧。
        //
        // 为什么要有：生成中的节点得在画布上一眼认出来——用户正等着它，而画布上通常有十几张卡片。
        // 为什么选中时也要有：生成时往往正是选中着一路盯着它的时候，原来「选中就不发光」等于
        // 在最需要看到它在跑的时候把光效关掉了。
        if (node.ExecutionStatus == NodeExecutionStatus.Generating)
        {
            // 只对「正在生成」发光。NeedsReview（图出来了但没存进项目）是**出错**状态，
            // 让它跟正常生成一样呼吸，等于把一个问题显示成一件正在进行的事。
            // 颜色固定用白：种类色里场景是绿、道具是黄绿，罩一层绿光看起来像出错，不像在生成。
            // 尺寸只是初值，真正的贴合尺寸由 FitGlows() 按卡片实测高度给。
            var glow = new Border
            {
                Width = NodeWidth + 14,
                Height = NodeHeight + 14,
                CornerRadius = new CornerRadius(16),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                IsHitTestVisible = false,
                // 光晕本体靠 **BoxShadow**（模糊过的白色阴影），不是那 1px 的边——
                // 一条细边读起来是「描边」，不是「在发光」。
                BoxShadow = BoxShadows.Parse("0 0 34 4 #99FFFFFF"),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };
            var scale = new ScaleTransform(1, 1);
            glow.RenderTransform = scale;
            Canvas.SetLeft(glow, node.X - 7);
            Canvas.SetTop(glow, node.Y - 7);
            NodeCanvas.Children.Add(glow);

            // 绕着节点转的那圈光弧：锥形渐变里只有一小段是亮的，每 33ms 改一次 Angle 就转起来了。
            // 为什么不去转这个元素本身：节点是圆角矩形，转元素会让它歪掉、跟卡片错开；
            // 转**渐变的角度**，形状始终贴着节点，只有亮的那一段在动。
            var sweep = new ConicGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                Angle = 0,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.52),
                    new GradientStop(Color.FromArgb(235, 255, 255, 255), 0.75),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.98),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            };
            var orbit = new Border
            {
                Width = NodeWidth + 20,
                Height = NodeHeight + 20,
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1.5),
                BorderBrush = sweep,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(orbit, node.X - 10);
            Canvas.SetTop(orbit, node.Y - 10);
            NodeCanvas.Children.Add(orbit);

            // 相位按节点坐标错开：同一批生成的几个节点不会整齐划一地一起亮，
            // 那样看起来像整块画布在闪，反而盖住了「哪个节点在动」。
            glows.Add(new GenerationGlow(
                node, glow, orbit, sweep, node.X * 0.01 + node.Y * 0.013 % 6.28, scale));
        }

        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y);
        border.PointerPressed += NodePointerPressed;
        return border;
    }

    /// <summary>
    /// 节点下方的「引用预览」：把这条节点引用到的角色 / 场景 / 道具各摆一个小预览框，
    /// 有图片给缩略图、只有视频给 ▶ 占位，点一下放大 / 播放。
    ///
    /// 为什么是「一张引用一个框」而不是「一张媒体一个框」：用户问的是「这个节点引用了谁」；
    /// 一个角色挂了三张参考图时摆三个框，会看不出它们其实属于同一个角色。
    /// 多出来的媒体用角标「+N」说明，点开那张框就能逐个看。
    ///
    /// 用 WrapPanel 按固定格宽排，所以无论几张都是**整齐的网格**（每行 3 个），
    /// 不会因为名字长短变得参差不齐。
    /// </summary>
    private Control CreateReferencePreviewStrip(WorkflowNode node)
    {
        const double slotWidth = 62;
        const double slotHeight = 64;
        var strip = new WrapPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            ItemWidth = slotWidth,
            ItemHeight = slotHeight
        };

        foreach (var reference in node.References)
        {
            var content = state?.ResolveReferenceContent(reference);
            var media = MediaOf(content);
            // 种类配色沿用同一套口径：画布节点、引用浮层、临时画布、这里的预览框四处同色。
            var category = content is null ? node.Category : NodeKindPalette.CategoryOf(content.Entity.Kind);
            var label = content?.Label ?? "引用失效";

            var slot = new StackPanel { Width = slotWidth - 6, Spacing = 3 };
            slot.Children.Add(CreatePreviewTile(node, content, media, category, label));
            slot.Children.Add(new TextBlock
            {
                Text = label,
                // 名字要看得清：9px 的 #93A1B3 在卡片底色上偏暗，扫一眼读不出是谁。
                Foreground = new SolidColorBrush(Color.Parse("#C6D3E2")),
                FontSize = 10,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
            });
            strip.Children.Add(slot);
        }
        return strip;
    }

    /// <summary>
    /// 出图虚影：在源节点**上方**浮出一排小卡，各自显示状态与预览。
    ///
    /// 为什么浮在上方、而不是画进节点卡片里：一次出 N 张的目的是「挑一张」，而挑的时候必须能同时看见原节点
    /// ——构图对不对，要跟这一镜的设定对着看。塞进卡片底部会把卡片撑高、把邻居挤开，挑图还得来回滚。
    ///
    /// 它们**不是画布节点**：不进 JSON、不能被连线、也不参与框选；程序重开就没了（见 NodeImageBatch）。
    /// 但它们**必须可点**——这跟「将修改 / 将删除」那种纯提示的虚影不同：挑图、看图、删图都在它们身上做。
    /// </summary>
    private void AddBatchGhosts(IReadOnlyList<WorkflowNode> visibleNodes)
    {
        if (ImageBatchOf is null) return;

        foreach (var node in visibleNodes)
        {
            if (ImageBatchOf(node.Id) is not { } batch || batch.IsEmpty) continue;

            const double width = 92;
            const double height = 68;
            const double gap = 8;
            // 位置按格子总数排：删掉中间一张时其余虚影不挪窝（编号与位置都稳定），
            // 空出来的位置就是「这里原来有一张、你把它删了」，比让整排跳一下更好读。
            var total = batch.Count * width + Math.Max(0, batch.Count - 1) * gap;
            var left = node.X + (NodeWidth - total) / 2;
            // 默认浮在节点**上方**（不遮住卡片）；上方真放不下就翻到下方去。
            // 原来写死「在上方」：节点靠画布顶边时整排窗口被推到画布外，屏幕上什么都不出现——
            // 「出图时那个预览窗口是不是被删了」就是这么来的：它其实一直在画布外面画着。
            var above = node.Y - height - 22;
            var placeAbove = above >= 20;
            var cardHeight = Math.Max(NodeHeight, CardFor(node)?.Bounds.Height ?? 0);
            var top = placeAbove ? above : node.Y + cardHeight + 22;
            var captionTop = placeAbove ? top - 17 : top + height + 6;

            for (var index = 0; index < batch.Count; index++)
            {
                if (batch.Slots[index].Removed) continue;
                var ghost = CreateBatchGhost(node, batch, index, width, height);
                var ghostLeft = left + index * (width + gap);
                Canvas.SetLeft(ghost, ghostLeft);
                Canvas.SetTop(ghost, top);
                NodeCanvas.Children.Add(ghost);
                // 登记成「节点坐标 + 偏移」：拖动节点时按偏移重排，这排窗口才跟得住节点
                // （不然拖一下，节点走了、窗口留在原地）。
                batchGhostVisuals.Add(new BatchGhostLayout(node, ghost, ghostLeft - node.X, top - node.Y));
            }

            // 开奖模式下，出完那一刻的提示要直接说出下一步动作：这一排盖着，只有点它才会开。
            var captionText = batch.StatusLine();
            if (GachaReveal && batch.CanPick) captionText += " · 点卡片开奖";

            var caption = new TextBlock
            {
                Text = captionText,
                FontSize = 10,
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(batch.IsRunning ? Color.Parse("#A7C7EA")
                    : batch.CanPick ? Color.Parse("#8FE0A8")
                    : Color.Parse("#E5A3A3"))
            };
            Canvas.SetLeft(caption, left);
            Canvas.SetTop(caption, captionTop);
            NodeCanvas.Children.Add(caption);
            batchGhostVisuals.Add(new BatchGhostLayout(node, caption, left - node.X, captionTop - node.Y));
        }
    }

    /// <summary>一张出图虚影。左键选中（Ctrl / Shift 加选）、双击看大图、右键出菜单。</summary>
    private Control CreateBatchGhost(WorkflowNode node, NodeImageBatch batch, int index, double width, double height)
    {
        var slot = batch.Slots[index];
        var isSelected = batch.SelectedIndices.Contains(index);
        // 「开奖」模式下，**出好的格子也不露图**：先出好的那几张一旦露了脸，开奖就退化成补一个仪式，
        // 惊喜已经被自己看掉了。所以这一支要盖过下面那个「出好就铺缩略图」的默认分支。
        var faceDown = GachaReveal && slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0;

        var frame = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(8),
            // 「虚影」要一眼看出是**暂时的**：底色半透、描边走浅亮色。
            // 画得跟真实节点一样的话，用户会以为画布上真多了几个节点。
            // 背面朝上的那几张再亮一档并偏蓝：整排盖着的时候，「这排是待开的卡」要看得出来。
            Background = new SolidColorBrush(faceDown ? Color.FromArgb(232, 22, 32, 46) : Color.FromArgb(205, 13, 19, 27)),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = new SolidColorBrush(isSelected ? AccentPrimary
                : faceDown ? Color.FromArgb(190, 138, 168, 255)
                : Color.FromArgb(160, 122, 155, 255)),
            ClipToBounds = true,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
        };
        if (isSelected) frame.BoxShadow = BoxShadows.Parse("0 0 18 -4 #9E4D9BFF");

        var face = new Panel();
        TextBlock? headline = null;
        Border? sweep = null;
        if (faceDown)
        {
            var back = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                IsHitTestVisible = false
            };
            back.Children.Add(new TextBlock
            {
                Text = "◈",
                FontSize = 19,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(165, 154, 182, 255))
            });
            back.Children.Add(new TextBlock
            {
                Text = "已出好",
                FontSize = 9,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#8FA6BD"))
            });
            face.Children.Add(back);
        }
        else if (slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0)
        {
            var bitmap = LoadThumbnail(AssetStore.ToReference(slot.Path));
            if (bitmap is not null)
                face.Children.Add(new Image { Source = bitmap, Stretch = global::Avalonia.Media.Stretch.UniformToFill });
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            // 中间那行是**实时读数**：跑着的时候每 33ms 由环境时钟改一次（「生成中 12s」）。
            // 以前这里是一个写死的「生成中」——三张并排时，用户看不出哪一张真的在动、哪一张在等。
            headline = new TextBlock
            {
                Text = slot.Status == BatchSlotStatus.Failed ? "失败" : slot.Headline(DateTimeOffset.Now),
                FontSize = 10,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(slot.Status switch
                {
                    BatchSlotStatus.Running => Color.Parse("#A7C7EA"),
                    BatchSlotStatus.Failed => Color.Parse("#E5A3A3"),
                    _ => Color.Parse("#5C6A7C")
                })
            };
            stack.Children.Add(headline);
            if (slot.Status == BatchSlotStatus.Running)
            {
                // 一条**来回扫**的短线，而不是一条不动的线：出图接口不吐百分比，能给的只有
                // 「还在做」这一件事，但「在动」本身就是真实信息——一条静止的线看起来像卡死了。
                var segment = new Border
                {
                    Height = 2,
                    Width = 16,
                    CornerRadius = new CornerRadius(1),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.Parse("#9EBBFF")),
                    RenderTransform = new TranslateTransform(0, 0)
                };
                var track = new Border
                {
                    Height = 2,
                    Width = 46,
                    CornerRadius = new CornerRadius(1),
                    ClipToBounds = true,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#26374D")),
                    Child = segment
                };
                stack.Children.Add(track);
                sweep = segment;
            }
            face.Children.Add(stack);
        }

        // 序号角标：六张并排时「哪张是哪张」必须能对上，光看图对不出来（构图接近时尤其）。
        face.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 10, 15, 22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0),
            Margin = new Thickness(3),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse("#C6D3E2"))
            }
        });

        frame.Child = face;

        // 跑着的那一格：**背后**一圈会呼吸的光晕（和节点卡上那圈同一个做法：BoxShadow + 相位错开）。
        // 光晕画在窗口外面一点点，于是「一闪一闪」的范围比窗口本身大一圈——这正是「AI 创作中」的观感。
        // 颜色同样是白的：种类色里场景绿、道具黄绿，绿光看着像失败不像在跑。
        Border? glow = null;
        if (slot.Status == BatchSlotStatus.Running && headline is not null && sweep is not null)
        {
            glow = new Border
            {
                Width = width + 6,
                Height = height + 6,
                Margin = new Thickness(-3),
                CornerRadius = new CornerRadius(11),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                BoxShadow = BoxShadows.Parse("0 0 20 2 #B3FFFFFF")
            };
            // 相位按格子错开：同时出 3 张时三圈光晕不会整齐划一地一起亮（那样像整块画布在闪，
            // 反而盖住了「哪一格还在跑」）。
            batchPulses.Add(new BatchPulse(
                glow, frame, headline, sweep, slot, index * 1.7 + node.X * 0.01 + node.Y * 0.013));
        }

        if (slot.Status == BatchSlotStatus.Failed)
            ToolTip.SetTip(frame, slot.Error.Length > 0 ? slot.Error : "这一张没出来（服务端没有给出原因）。");
        else if (faceDown)
            ToolTip.SetTip(frame, batch.IsRunning ? batch.SingleActionBlockedNote : "点一下开奖（这一批的图在揭晓里才露面）");
        else if (slot.Status == BatchSlotStatus.Done)
            ToolTip.SetTip(frame, "双击看大图 · 右键选用或删除");

        frame.PointerPressed += (_, args) =>
        {
            // 吃掉事件：不然这一下会接着冒泡成「选中节点」，点一下图就先跳去选节点了。
            args.Handled = true;
            var point = args.GetCurrentPoint(frame);

            if (faceDown)
            {
                // 背面朝上的一格：**点一下就是「开这一批」**。右键只留「开奖」与「都不要重做」——
                // 针对单张的「用这一张 / 放大看看 / 删除」在图露面之前没有意义（也没法判断该不该留）。
                if (point.Properties.IsRightButtonPressed)
                {
                    ShowFaceDownMenu(node, batch, index, frame);
                    return;
                }
                BatchActionRequested?.Invoke(node.Id, $"ghost-gacha:{index}");
                return;
            }

            if (point.Properties.IsRightButtonPressed)
            {
                // 右键作用于「这一张」：先把焦点挪到它身上，再弹菜单。
                BatchActionRequested?.Invoke(node.Id, $"ghost-focus:{index}");
                ShowBatchGhostMenu(node, batch, index, frame);
                return;
            }
            if (args.ClickCount == 2)
            {
                BatchActionRequested?.Invoke(node.Id, $"ghost-preview:{index}");
                return;
            }
            // Ctrl / Shift 加选，普通点一下就是单选。
            var additive = args.KeyModifiers is global::Avalonia.Input.KeyModifiers.Control
                or global::Avalonia.Input.KeyModifiers.Shift;
            BatchActionRequested?.Invoke(node.Id, additive ? $"ghost-toggle:{index}" : $"ghost-select:{index}");
        };

        // 光晕与窗口捆在一个容器里返回：外层的 Canvas.Left/Top 只设一次，两者永远对齐
        // （各自算坐标的话，改一次偏移量就会出现「光晕和窗口错开半格」）。
        return glow is null ? frame : new Panel { Children = { glow, frame } };
    }

    /// <summary>节点**自己出的一张图**上的右键菜单：放大 / 删除。</summary>
    private void ShowOwnMediaMenu(WorkflowNode node, WorkflowAttachment attachment, Control anchor)
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "放大看看" };
        open.Click += (_, _) =>
        {
            var path = AssetStore.Resolve(attachment.Reference) ?? string.Empty;
            if (path.Length > 0) OwnMediaActivated?.Invoke(this, (path, node.Title));
        };
        menu.Items.Add(open);

        // 删除**只动这一张**：文件移入回收站、引用从这个节点上摘掉，节点本身与别的图都不受影响。
        // 移文件与记账的规矩在主窗口（画布只说「要删哪一个」）——这样「删除必须真的移走文件」
        // 这条规矩只有一个地方需要守住。
        var remove = new MenuItem { Header = "删除这一张（移入回收站）" };
        remove.Click += (_, _) => OwnMediaDeleteRequested?.Invoke(this, (node.Id, attachment.Reference));
        menu.Items.Add(remove);

        menu.Open(anchor);
    }

    /// <summary>
    /// 背面朝上的卡上的右键菜单：只看得到「背面」的时候，能给的就只有开奖与整批重做。
    /// 单张那几项（用这一张 / 放大看看 / 删除）在图露面之前没法判断，所以不摆出来。
    /// </summary>
    private void ShowFaceDownMenu(WorkflowNode node, NodeImageBatch batch, int index, Control anchor)
    {
        var menu = new ContextMenu();
        var open = new MenuItem
        {
            Header = batch.IsRunning ? batch.SingleActionBlockedNote : "开奖（这一批的图在揭晓里露面）",
            IsEnabled = batch.CanPick
        };
        open.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, $"ghost-gacha:{index}");
        menu.Items.Add(open);

        menu.Items.Add(new Separator());
        var redo = new MenuItem { Header = "全部不要，重做", IsEnabled = !batch.HasUnfinished };
        redo.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, "redo");
        menu.Items.Add(redo);

        menu.Open(anchor);
    }

    /// <summary>虚影上的右键菜单。画布只说「点了哪一张、要干什么」，规矩都在主窗口。</summary>
    private void ShowBatchGhostMenu(WorkflowNode node, NodeImageBatch batch, int index, Control anchor)
    {
        var slot = batch.Slots[index];
        var menu = new ContextMenu();
        // Control 而不是 MenuItem：中间要插 Separator，它不是 MenuItem。
        var items = new List<Control>();

        if (slot.Status == BatchSlotStatus.Done)
        {
            var use = new MenuItem { Header = "用这一张", IsEnabled = batch.CanActOnSlot(index) };
            var captured = index;
            use.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, $"ghost-use:{captured}");
            items.Add(use);

            var preview = new MenuItem { Header = "放大看看", IsEnabled = batch.CanActOnSlot(index) };
            preview.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, $"ghost-preview:{captured}");
            items.Add(preview);

            var remove = new MenuItem { Header = "删除这一张", IsEnabled = batch.CanActOnSlot(index) };
            remove.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, $"ghost-delete:{captured}");
            items.Add(remove);

            // 整批还在跑时为什么一样都不给做：规则在模型里（NodeImageBatch.CanActOnSlot），这里只负责显示。
            // 它不是「挑到一半没意义」那么轻——中途采用会把整批丢掉，却不取消还在跑的请求，
            // 那些请求跑完后仍会把图写进资产目录，可那时批次已经不在表里了，那些文件没人引用（漏文件）。
            if (batch.IsRunning)
            {
                items.Add(new Separator());
                items.Add(new MenuItem { Header = batch.SingleActionBlockedNote, IsEnabled = false });
            }
        }
        else if (slot.Status == BatchSlotStatus.Failed)
        {
            items.Add(new MenuItem { Header = "这一张没出来（悬停看原因）", IsEnabled = false });
            // 失败的那一格也要能删掉：否则一排失败会永远挂在画布上，用户没有任何办法收掉它。
            // 它没有文件，所以「删除」只是把这一格去掉，不涉及回收站。
            var remove = new MenuItem { Header = "删除这一格", IsEnabled = batch.CanActOnSlot(index) };
            var capturedFailed = index;
            remove.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, $"ghost-delete:{capturedFailed}");
            items.Add(remove);

            if (batch.IsRunning)
            {
                items.Add(new Separator());
                items.Add(new MenuItem { Header = batch.SingleActionBlockedNote, IsEnabled = false });
            }
        }
        else
        {
            items.Add(new MenuItem { Header = batch.IsRunning ? batch.SingleActionBlockedNote : "这一张还在出", IsEnabled = false });
        }

        // 批量删除也是一次「挑」，同样要等整批出完。
        if (!batch.IsRunning && batch.SelectedIndices.Count > 1)
        {
            items.Add(new Separator());
            var removeSelected = new MenuItem { Header = $"删除选中的 {batch.SelectedIndices.Count} 张" };
            removeSelected.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, "ghost-delete-selected");
            items.Add(removeSelected);
        }

        items.Add(new Separator());
        var redo = new MenuItem
        {
            Header = "全部不要，重做",
            IsEnabled = !batch.HasUnfinished
        };
        redo.Click += (_, _) => BatchActionRequested?.Invoke(node.Id, "redo");
        items.Add(redo);

        foreach (var item in items) menu.Items.Add(item);
        menu.Open(anchor);
    }

    /// <summary>
    /// 画布上当前被选中的虚影键（`节点Id|序号`）。Delete 键要用它——
    /// 虚影浮在节点上方又被明确选中了，这时候用户想删的一定是它们，而不是底下那个节点。
    /// </summary>
    private IReadOnlyList<string> SelectedBatchGhostKeys()
    {
        if (ImageBatchOf is null || state is null) return Array.Empty<string>();
        var keys = new List<string>();
        foreach (var node in state.Nodes)
        {
            if (ImageBatchOf(node.Id) is not { } batch) continue;
            foreach (var index in batch.SelectedIndices)
            {
                if (batch.Slots[index].Removed) continue;
                keys.Add($"{node.Id}|{index}");
            }
        }
        return keys;
    }


    private Control CreatePreviewTile(
        WorkflowNode node,
        ReferenceContent? content,
        IReadOnlyList<WorkflowAttachment> media,
        NodeCategory category,
        string label)
    {
        var accent = NodeKindBrushes.ColorOf(category);
        var first = media.FirstOrDefault();
        var thumbnail = first is { Kind: AttachmentKind.Image } ? LoadThumbnail(first.Reference) : null;

        var tile = new Border
        {
            Width = 56,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#0E141C")),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };

        var face = new Panel();
        if (thumbnail is not null)
        {
            face.Children.Add(new Image { Source = thumbnail, Stretch = global::Avalonia.Media.Stretch.UniformToFill });
        }
        else
        {
            // 视频给 ▶、什么都没有给一个「无图」：都不假装有画面。
            // 「无图」原来用半透明的种类色（alpha 150）压在近黑底上，几乎读不出来——改成实色。
            var empty = first is null;
            face.Children.Add(new TextBlock
            {
                Text = empty ? "无图" : "▶",
                Foreground = new SolidColorBrush(empty ? Color.Parse("#A9B8CA") : Color.Parse("#E9EFF7")),
                FontSize = empty ? 10 : 16,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            });
        }

        if (media.Count > 1)
        {
            face.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 10, 15, 22)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 2, 2),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = $"+{media.Count - 1}",
                    Foreground = new SolidColorBrush(Color.Parse("#CFE0F2")),
                    FontSize = 9
                }
            });
        }
        tile.Child = face;

        // 引用解析不出来（设定被删了 / 项目库里找不到）时不挂点击：点开一个空窗只会更困惑。
        if (content is not null)
        {
            tile.Tag = new CanvasReferencePreview(node, content.Entity, content.Variant, media);
            tile.Cursor = new Cursor(StandardCursorType.Hand);
            tile.PointerPressed += ReferencePreviewPointerPressed;
        }
        ToolTip.SetTip(tile, first is null
            ? $"{label}：还没有图片或视频（双击节点进引用画布可以补一张）"
            : $"{label}：{media.Count} 个媒体 · 点击放大 / 播放");
        return tile;
    }

    /// <summary>
    /// 节点下方的引用预览框。单击**先把节点选中**（检查器因此跟着换），再开预览窗口——
    /// 「点卡片上的东西 = 选中这张卡」是与直觉一致的，不然会出现「点了没反应、右边还是上一个节点的内容」。
    /// 预览窗口延到本轮事件走完之后再开：指针按下还没走完就弹模态窗口容易把捕获搞乱。
    /// 双击当一次处理（不然两次单击会开两个窗口）。
    /// </summary>
    private void ReferencePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: CanvasReferencePreview activation } tile) return;
        if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
        SelectNodeOnly(activation.Node);
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            return;
        }
        Dispatcher.UIThread.Post(() => ReferencePreviewActivated?.Invoke(this, activation), DispatcherPriority.Input);
        e.Handled = true;
    }

    /// <summary>
    /// 只做「选中这个节点」这一件事：不改当前工具、不铺引用浮层、不起拖拽。
    /// 卡片内部的小控件（引用标签、媒体预览框）用它——它们会吃掉按下事件，卡片自己收不到，
    /// 所以得由它们主动把节点选中，否则检查器会一直停在上一个节点上。
    /// </summary>
    private void SelectNodeOnly(WorkflowNode node)
    {
        if (ReferenceEquals(selectedNode, node))
        {
            SelectedNodeChanged?.Invoke(this, node);
            return;
        }
        selectedEdge = null;      // 选中节点就取消连线的选中：两种选择不能同时成立
        selectedNode = node;
        Rebuild();
        SelectedNodeChanged?.Invoke(this, node);
    }

    /// <summary>这条引用能拿到的媒体（只认图片与视频；音频与其它文件在这里没有可看的东西）。</summary>
    private static List<WorkflowAttachment> MediaOf(ReferenceContent? content) =>
        content is null
            ? new List<WorkflowAttachment>()
            : content.Attachments.Where(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video).ToList();

    /// <summary>
    /// 缩略图缓存：节点卡在选中 / 拖动 / 换阶段时整体重建，每次都重新解码原图会明显卡
    /// （参考图动辄几 MB）。键带上文件的写入时间，图换了自然会重新解一次；
    /// 超过上限就整体清空——缩略图很小，重解一份不心疼，比维护 LRU 简单也不容易出错。
    /// </summary>
    private static readonly Dictionary<string, Bitmap?> thumbnailCache = new(StringComparer.Ordinal);

    private static Bitmap? LoadThumbnail(string reference)
    {
        var path = AssetStore.Resolve(reference);
        if (path is null) return null;

        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        var key = $"{path}|{stamp}";
        if (thumbnailCache.TryGetValue(key, out var cached)) return cached;

        Bitmap? bitmap = null;
        try
        {
            using var stream = File.OpenRead(path);
            // 只在缩略图尺寸上解码：整图（可能 4000px 宽）解出来再缩，翻页时会一顿一顿的。
            bitmap = Bitmap.DecodeToWidth(stream, 128);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException)
        {
            bitmap = null;
        }

        if (thumbnailCache.Count > 160) thumbnailCache.Clear();
        thumbnailCache[key] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// 节点两端的连接点（左=输入、右=输出）。用它连着线：**从端口拖到另一个节点上松手**即可，
    /// 不用先点「连接」再点两个节点——那条路径还在，只是拖拽更符合直觉。
    /// </summary>
    private Border CreatePort(WorkflowNode node, bool isInput)
    {
        var port = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.Parse("#0D131B")),
            BorderBrush = new SolidColorBrush(Color.Parse(isInput ? "#2C3D52" : "#594D9BFF")),
            BorderThickness = new Thickness(1.6),
            Cursor = new Cursor(StandardCursorType.Cross),
            Tag = new CanvasPort(node, isInput)
        };
        ToolTip.SetTip(port, isInput
            ? "输入端口：从这里拖到另一个节点，把它的输出连到这里"
            : "输出端口：从这里拖到另一个节点，建一条连线");
        port.PointerPressed += PortPointerPressed;
        return port;
    }

    private void PortPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: CanvasPort port } || !e.GetCurrentPoint((Border)sender).Properties.IsLeftButtonPressed) return;

        // 捕获在画布上而不是端口本身：端口会随卡片重建，捕获在端口上会立刻丢掉后续的移动事件。
        portDragNode = port.Node;
        portDragFromInput = port.IsInput;
        selectedNode = port.Node;
        UpdatePortPreview(e.GetPosition(NodeCanvas));
        SelectedNodeChanged?.Invoke(this, port.Node);
        e.Pointer.Capture(this);
        e.Handled = true;   // 别让卡片把它当成「选中 + 拖动卡片」
    }

    private void UpdatePortPreview(Point world)
    {
        var start = AnchoredPortPoint(portDragNode!, portDragFromInput);
        if (portPreview is null)
        {
            portPreview = new Line
            {
                Stroke = new SolidColorBrush(Color.Parse("#8CC5FF")),
                StrokeThickness = 2,
                StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 5, 4 },
                StrokeLineCap = PenLineCap.Round
            };
            NodeCanvas.Children.Add(portPreview);
        }
        portPreview.StartPoint = portDragFromInput ? world : start;
        portPreview.EndPoint = portDragFromInput ? start : world;
    }

    private static Point AnchoredPortPoint(WorkflowNode node, bool isInput) =>
        isInput
            ? new Point(node.X, node.Y + NodeHeight / 2)
            : new Point(node.X + NodeWidth, node.Y + NodeHeight / 2);

    /// <summary>松手：落点在哪个节点上就连哪个（落空就什么也不做，不猜）。</summary>
    private void FinishPortDrag(Point world)
    {
        var source = portDragNode;
        var fromInput = portDragFromInput;
        portDragNode = null;
        if (portPreview is not null)
        {
            NodeCanvas.Children.Remove(portPreview);
            portPreview = null;
        }
        if (source is null || state is null) return;

        var target = NodeAt(world);
        if (target is null)
        {
            StatusTextHint = "松在空白处，没有连线；要连接请拖到目标节点上";
            UpdateHint();
            return;
        }
        if (target.Id == source.Id)
        {
            StatusTextHint = "不能连到自己";
            UpdateHint();
            return;
        }

        // 从输入端口出发 = 把对方的输出接到我这里，方向与从输出端口出发相反。
        var connection = fromInput ? (target, source) : (source, target);
        ConnectionRequested?.Invoke(this, connection);
        StatusTextHint = "拖拽已结束";
        Rebuild();
    }

    /// <summary>世界坐标落在哪个节点卡片上。用**卡片渲染出来的实际高度**判断，比常量高度准。</summary>
    private WorkflowNode? NodeAt(Point world)
    {
        foreach (var card in NodeCanvas.Children.OfType<Border>())
        {
            if (card.Tag is not WorkflowNode node) continue;
            var width = card.Bounds.Width > 1 ? card.Bounds.Width : NodeWidth;
            var height = card.Bounds.Height > 1 ? card.Bounds.Height : NodeHeight;
            if (world.X >= node.X && world.X <= node.X + width + 8 &&
                world.Y >= node.Y && world.Y <= node.Y + height + 8) return node;
        }
        return null;
    }

    private static string CodeFor(WorkflowNode node) => node.Category switch
    {
        NodeCategory.Chapter => "CH",
        NodeCategory.Character => "CAST",
        NodeCategory.Scene => "SCN",
        NodeCategory.Prop => "PROP",
        NodeCategory.Storyboard => "SHOT",
        NodeCategory.Product => "OUT",
        NodeCategory.StoryPlan => "PLAN",
        NodeCategory.StoryOutline => "OUTL",
        _ => "NODE"
    };

    private static double ProgressFor(WorkflowNode node)
    {
        if (node.Parameters.TryGetValue("progress", out var raw) && double.TryParse(raw, out var parsed))
            return Math.Clamp(parsed, 0, 1);
        return node.ExecutionStatus switch
        {
            NodeExecutionStatus.Completed => 1.0,
            NodeExecutionStatus.Generating => 0.55,
            NodeExecutionStatus.NeedsReview => 0.4,
            NodeExecutionStatus.Failed => 0.3,
            _ => 0
        };
    }

    private static Color AccentFor(NodeCategory category) => NodeKindBrushes.ColorOf(category);

    private static string CategoryName(NodeCategory category) => category switch
    {
        NodeCategory.Chapter => "章节",
        NodeCategory.Character => "出场角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Prop => "道具",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Product => "成品",
        NodeCategory.StoryPlan => "故事企划",
        NodeCategory.StoryOutline => "故事大纲",
        _ => "通用"
    };

    private static string StatusText(NodeExecutionStatus status) => status switch
    {
        NodeExecutionStatus.WaitingForUser => "待补充",
        NodeExecutionStatus.Generating => "生成中",
        NodeExecutionStatus.Completed => "已完成",
        NodeExecutionStatus.Failed => "失败",
        NodeExecutionStatus.NeedsReview => "待确认",
        _ => "草稿"
    };

    private static IBrush StatusBrush(NodeExecutionStatus status) => new SolidColorBrush(status switch
    {
        NodeExecutionStatus.Completed => Color.Parse("#5CC99A"),
        NodeExecutionStatus.Failed => Color.Parse("#E0707E"),
        NodeExecutionStatus.Generating => Color.Parse("#4D9BFF"),
        NodeExecutionStatus.NeedsReview or NodeExecutionStatus.WaitingForUser => Color.Parse("#DFB45A"),
        _ => Color.Parse("#93A1B3")
    });

    // ---------------------------------------------------------------- ambient animation

    private void OnAmbientTick(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;

        // 布局完成后补做一次「适应窗口」
        if (pendingFit) ApplyFit();

        ambientClock += 0.033;

        // 扫描光带
        scanProgress += 0.0055;
        if (scanProgress > 1.35) scanProgress = -0.35;
        var bandHeight = Math.Max(240, Bounds.Height);
        Canvas.SetTop(ScanBand, scanProgress * (bandHeight + 360) - 260);
        ScanBand.Width = Math.Max(400, Bounds.Width);

        // 光标聚光跟随
        Canvas.SetLeft(GlowSpot, pointerPosition.X - 230);
        Canvas.SetTop(GlowSpot, pointerPosition.Y - 230);

        // 连线流光
        foreach (var beam in beams)
            beam.StrokeDashOffset = -(ambientClock * 46) % 30;

        // 正在生成的节点：白色光晕呼吸（亮度与范围一起起伏）+ 那圈光弧绕着节点转。
        // 只动透明度会像「闪一下」，只动尺寸会像「在呼吸但不亮」；两个一起动才像光晕。
        // 复用同一个 ScaleTransform（而不是每帧 new 一个）：33ms 一跳，每帧分配对象是白给的 GC 压力。
        foreach (var item in glows)
        {
            var pulse = 0.5 + 0.5 * Math.Sin(ambientClock * 4.2 + item.Phase);
            item.Glow.Opacity = 0.30 + 0.70 * pulse;
            var grow = 1 + 0.030 * pulse;
            item.Scale.ScaleX = grow;
            item.Scale.ScaleY = grow;
            // 光弧：约 2.4 秒绕节点一圈。改的是渐变的角度，形状始终贴着节点。
            item.Sweep.Angle = ambientClock * 150 % 360;
        }

        // 出图预览窗口：同一套呼吸，外加中间的实时读数与那条来回扫的线。
        // 只对**还在跑**的格子动手——出好的那一格已经在显示图，让它继续闪就成了干扰。
        if (batchPulses.Count > 0)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in batchPulses)
            {
                if (item.Slot.Status != BatchSlotStatus.Running) continue;
                var wave = 0.5 + 0.5 * Math.Sin(ambientClock * 4.2 + item.Phase);
                if (item.Glow is not null) item.Glow.Opacity = 0.18 + 0.62 * wave;
                // 窗口本体也跟着轻微起伏：只有外圈亮、里面一动不动，看起来像两个不相干的东西。
                item.Frame.Opacity = 0.86 + 0.14 * wave;
                item.Headline.Text = item.Slot.Headline(now);
                if (item.Sweep.RenderTransform is TranslateTransform move) move.X = 30 * wave;
            }
        }
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // 键盘操作（Delete / Esc）打给「有焦点的控件」，画布必须在被点时拿到焦点——
        // 否则焦点还在别处，按 Delete 谁也收不到（这正是「节点按 Del 删不掉」的原因）。
        Focus();

        // 节点卡片自行处理按下事件，其余区域视为空白画布
        if (e.Source is Border { Tag: WorkflowNode }) return;

        var hadSelection = selectedNode is not null || selectedEdge is not null;
        var hadExpansion = referenceExpansionOwner is not null;
        selectedNode = null;
        selectedEdge = null;
        // 点空白处把展开的引用一起收掉：铺在画布上的东西要有一条明确的消失路径。
        referenceExpansionOwner = null;
        if (hadSelection || hadExpansion)
        {
            Rebuild();
            SelectedNodeChanged?.Invoke(this, null);
            EdgeSelectionChanged?.Invoke(this, null);
        }
        panning = true;
        panStart = e.GetPosition(this);
        panOrigin = pan;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        pointerPosition = e.GetPosition(this);

        // 从端口拖出来的连线：跟着指针画预览线，松手时再决定连到哪里。
        if (portDragNode is not null)
        {
            UpdatePortPreview(e.GetPosition(NodeCanvas));
            e.Handled = true;
            return;
        }

        // 拖动节点。这一段必须由画布（本控件）来处理：指针被捕获在本控件上，
        // 而卡片每次重建都会换成新控件，挂在卡片上的 PointerMoved 收不到后续事件——这正是以前拖不动的根因。
        if (draggingNode is not null)
        {
            var worldPoint = e.GetPosition(NodeCanvas);
            var dx = worldPoint.X - dragOrigin.X;
            var dy = worldPoint.Y - dragOrigin.Y;
            if (!dragMoved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
            if (!dragSnapshotRecorded)
            {
                BeforeCanvasMutation?.Invoke(this, EventArgs.Empty);
                dragSnapshotRecorded = true;
            }
            dragMoved = true;
            draggingNode.X = (float)Math.Max(0, nodeOriginX + dx);
            draggingNode.Y = (float)Math.Max(0, nodeOriginY + dy);
            if (draggingCard is not null)
            {
                Canvas.SetLeft(draggingCard, draggingNode.X);
                Canvas.SetTop(draggingCard, draggingNode.Y);
            }
            RebuildEdgesForDrag();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (!panning) return;
        var point = e.GetPosition(this);
        pan = new Point(
            Math.Clamp(panOrigin.X + (point.X - panStart.X), -6000, 6000),
            Math.Clamp(panOrigin.Y + (point.Y - panStart.Y), -6000, 6000));
        ApplyTransform();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (portDragNode is not null)
        {
            e.Pointer.Capture(null);
            FinishPortDrag(e.GetPosition(NodeCanvas));
            e.Handled = true;
            return;
        }

        if (draggingNode is not null)
        {
            var node = draggingNode;
            var moved = dragMoved;
            draggingNode = null;
            draggingCard = null;
            e.Pointer.Capture(null);
            if (moved)
            {
                node.ManualPosition = true;
                NodeMutationCompleted?.Invoke(this, node);
                Rebuild();
                CanvasChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // 几乎没移动 = 这一下是「点」，不是「拖」。判别复用拖动那条路已有的 3px 阈值
                // （见 OnPointerMoved 里的 Math.Abs(dx) + Math.Abs(dy) < 3），不新增机制，
                // 于是**整张卡片都能拖**，用户不需要先分清「哪一块才抓得动」。
                ToggleReferenceExpansion(node);
                Rebuild();
            }
            e.Handled = true;
            return;
        }

        if (!panning) return;
        panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var factor = Math.Exp(e.Delta.Y * 0.12);
        var next = Math.Clamp(zoom * factor, 0.25, 2.2);
        if (Math.Abs(next - zoom) < 0.0005) return;
        pan = new Point(
            point.X - (point.X - pan.X) * (next / zoom),
            point.Y - (point.Y - pan.Y) * (next / zoom));
        zoom = next;
        ApplyTransform();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back)
        {
            // 出图虚影上有选中时，Delete 先删虚影：它们浮在节点上方、又被明确选中了，
            // 这时候用户想删的一定是它们，而不是底下那个节点——把节点删掉是无法撤销的意外。
            var ghostKeys = SelectedBatchGhostKeys();
            if (ghostKeys.Count > 0)
            {
                // 键里带着节点与序号，够主窗口自己去定位；跨批次多选也照样成立。
                BatchActionRequested?.Invoke(Guid.Empty, "ghost-delete-selected:" + string.Join(',', ghostKeys));
                e.Handled = true;
                return;
            }

            // 连线与节点都走这里：选中什么删什么（连线优先，见 DeleteSelection）。
            var message = DeleteSelection();
            if (message is not null)
            {
                Notice?.Invoke(this, message);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            var message = CancelInteraction();
            if (message is not null)
            {
                Notice?.Invoke(this, message);
                e.Handled = true;
                return;
            }

            // 画布自己没东西可取消（没在连线、没有引用浮层），把 Esc 交出去：
            // 临时引用画布靠它退到上一层。谁在用谁决定怎么处理，画布不猜。
            EscapePressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not WorkflowNode node) return;
        var point = e.GetPosition(NodeCanvas);
        Focus();
        selectedEdge = null;      // 选中节点就取消连线的选中：两种选择不能同时成立

        // 右键：不拖不选工具，直接把「这个节点能做什么」交给界面弹出来。
        if (e.GetCurrentPoint(border).Properties.IsRightButtonPressed)
        {
            selectedNode = node;
            Rebuild();
            SelectedNodeChanged?.Invoke(this, node);
            NodeContextRequested?.Invoke(this, new CanvasNodeContextRequest(node));
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed && e.ClickCount >= 2)
        {
            if (node.References.Count > 0) NodeReferenceDoubleClicked?.Invoke(this, node);
            else NodeDoubleClicked?.Invoke(this, node);
            e.Handled = true;
            return;
        }

        selectedNode = node;

        if (connectionMode)
        {
            // 连接模式下卡片不参与拖动（见下面的赋值），按下时铺浮层是无害的，保持原样。
            // 点一个有引用的节点：把它的引用临时铺到画布上（再点一次收起）。只改显示——
            // 引用卡不写画布；它们可以单击选中、双击进临时画布（见 CreateReferenceCard）。
            // 临时引用画布里这一步是空操作（referenceOverlayEnabled=false），单击只负责选中。
            ToggleReferenceExpansion(node);
            if (connectionSource is null)
            {
                connectionSource = node;
                StatusTextHint = $"已选择起点：{node.Title}，请选择终点";
            }
            else if (connectionSource.Id != node.Id)
            {
                ConnectionRequested?.Invoke(this, (connectionSource, node));
                connectionSource = null;
                StatusTextHint = "请选择连接起点";
            }
            Rebuild();
            SelectedNodeChanged?.Invoke(this, node);
            e.Handled = true;
            return;
        }

        // 铺引用浮层的动作**不在这里**，而是等到松手且几乎没移动时（见 OnPointerReleased）：
        // 只要它发生在按下这一瞬间，想挪一个带引用的节点时浮层就会立刻盖上来，
        // 节点反而拖不动——这正是「点节点就弹二级画布，影响移动节点」的根因。
        draggingNode = node;
        dragMoved = false;
        dragSnapshotRecorded = false;
        dragOrigin = point;
        nodeOriginX = node.X;
        nodeOriginY = node.Y;

        // 捕获在画布上（不是卡片上）：卡片会被下面的 Rebuild 换掉，捕获在被换掉的控件上会收不到后续事件。
        e.Pointer.Capture(this);
        Rebuild();
        draggingCard = CardFor(node);
        SelectedNodeChanged?.Invoke(this, node);
        e.Handled = true;
    }

    private Border? CardFor(WorkflowNode node) =>
        NodeCanvas.Children.OfType<Border>().FirstOrDefault(card => ReferenceEquals(card.Tag, node));

    /// <summary>
    /// 拖动与松手都由画布自己处理（见 OnPointerMoved / OnPointerReleased）：
    /// 卡片在按下时会被重建，挂在卡片上的处理器收不到后续事件，节点因此拖不动。
    /// </summary>
    private void RebuildEdgesForDrag()
    {
        if (state is null) return;
        foreach (var edge in state.Edges)
        {
            if (!edgeVisuals.TryGetValue(edge.Id, out var visual)) continue;
            var source = state.Nodes.FirstOrDefault(n => n.Id == edge.SourceNodeId);
            var target = state.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
            if (source is null || target is null) continue;
            var start = new Point(source.X + NodeWidth, CardCenterY(source));
            var end = new Point(target.X, CardCenterY(target));
            visual.Body.StartPoint = start;
            visual.Body.EndPoint = end;
            visual.Beam.StartPoint = start;
            visual.Beam.EndPoint = end;
            visual.Hit.StartPoint = start;
            visual.Hit.EndPoint = end;
        }

        // 光效与出图虚影也要跟着走。它们和卡片是三个各自 SetLeft/Top 的 Canvas 子元素，
        // 拖动时只挪卡片的话，光晕和那排预览窗口会留在原地——看起来就是「光晕没挂在节点上」。
        foreach (var item in glows)
        {
            Canvas.SetLeft(item.Glow, item.Node.X - 7);
            Canvas.SetTop(item.Glow, item.Node.Y - 7);
            Canvas.SetLeft(item.Orbit, item.Node.X - 10);
            Canvas.SetTop(item.Orbit, item.Node.Y - 10);
        }

        foreach (var item in batchGhostVisuals)
        {
            Canvas.SetLeft(item.Visual, item.Node.X + item.OffsetX);
            Canvas.SetTop(item.Visual, item.Node.Y + item.OffsetY);
        }
    }

    /// <summary>
    /// 把光晕与光弧按卡片的**实测**尺寸贴到节点上。
    ///
    /// 为什么必须单独算一遍：卡片是 <c>MinHeight</c>，挂上引用标签或媒体缩略图之后会长高，
    /// 而光效是按最小值画的——于是光晕只包住卡片上半截，看起来完全没「挂在节点上」。
    /// 卡片量的尺寸存在 <c>cardSizes</c> 里（连线端点也用它），这里直接复用同一个口径。
    /// </summary>
    private void FitGlows()
    {
        if (state is null || glows.Count == 0) return;

        foreach (var item in glows)
        {
            var height = cardSizes.TryGetValue(item.Node.Id, out var size) && size.Height > 1
                ? size.Height
                : NodeHeight;

            item.Glow.Width = NodeWidth + 14;
            item.Glow.Height = height + 14;
            Canvas.SetLeft(item.Glow, item.Node.X - 7);
            Canvas.SetTop(item.Glow, item.Node.Y - 7);

            item.Orbit.Width = NodeWidth + 20;
            item.Orbit.Height = height + 20;
            Canvas.SetLeft(item.Orbit, item.Node.X - 10);
            Canvas.SetTop(item.Orbit, item.Node.Y - 10);
        }
    }

    /// <summary>
    /// 卡片上的「引用 · …」标签。单击时**节点也要被选中**：以前这里无条件 <c>e.Handled = true</c>，
    /// 卡片自己收不到按下，于是点在这种小标签上时右边检查器还是上一个节点的内容，像是「点了没反应」。
    /// 双击由标签自己接手（进临时画布）。
    /// </summary>
    private void ReferenceTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: CanvasReferenceActivation activation } tag) return;
        if (!e.GetCurrentPoint(tag).Properties.IsLeftButtonPressed) return;
        SelectNodeOnly(activation.Node);
        if (e.ClickCount >= 2)
        {
            ReferenceDoubleClicked?.Invoke(this, activation);
            e.Handled = true;
            return;
        }
        ReferenceSelected?.Invoke(this, activation);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- drag & drop

    private static void SurfaceDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(ResourceDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SurfaceDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(ResourceDragFormat) || e.Data.Get(ResourceDragFormat) is not string payload)
            return;
        var values = payload.Split('|');
        if (values.Length < 2 || values.Length > 3 || !Guid.TryParse(values[0], out var entityId) || !Guid.TryParse(values[1], out var variantId))
            return;
        Guid? workTreeItemId = values.Length == 3 && Guid.TryParse(values[2], out var itemId) ? itemId : null;
        var point = e.GetPosition(NodeCanvas);
        ResourceDropped?.Invoke(this, new CanvasResourceDrop(entityId, variantId, workTreeItemId, point));
        e.Handled = true;
    }
}
