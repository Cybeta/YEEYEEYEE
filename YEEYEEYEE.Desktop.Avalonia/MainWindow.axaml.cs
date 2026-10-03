using Avalonia;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
// 第 127 轮起 AppPaths / ProjectContext 与其它共享类型同住 YEEYEEYEE.Desktop 命名空间
// （原先它们在 YEEYEEYEE.Desktop.Core 下，靠一个转发壳访问），那个 using 已删除。
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 沉浸式工作台主窗口。
/// 同时充当 Agent 面板的宿主（<see cref="IAgentSessionHost"/>）：面板只谈对话与审批，
/// 所有碰画布、文件与保存的动作都回到这里执行。
/// </summary>
public partial class MainWindow : Window, IAgentSessionHost
{
    private RecentCanvasState? currentCanvas;
    private string? currentCanvasPath;

    /// <summary>
    /// 画布标签（浏览器标签式）。顺序就是界面顺序，<see cref="activeCanvasTab"/> 指向正在编辑的那张。
    ///
    /// 与 <see cref="currentCanvas"/> 的分工要记住：currentCanvas / currentCanvasPath 是**活动标签的实时状态**
    /// （画布控件直接改的就是它），标签里的 Snapshot 是**离开这张画布时的独立副本**。
    /// 切换、关闭、落盘之前必须先 CaptureActiveTab()，否则那张画布的内容会停在上次离开时的样子。
    /// </summary>
    private readonly List<CanvasTab> canvasTabs = new();

    private CanvasTab? activeCanvasTab;

    private int zoomPercent = 84;
    private readonly Stack<string> undoSnapshots = new();
    private readonly Stack<string> redoSnapshots = new();
    private static readonly JsonSerializerOptions SnapshotOptions = new();
    private bool suppressSnapshot;
    private bool canEdit;
    private Point workTreeDragOrigin;
    private bool workTreeDragStarted;

    /// <summary>正在拖的那条窗口边（North / South / East / West / 四角），null = 没在拖。</summary>
    private string? resizeEdge;
    private PixelPoint resizeStartPointer;
    private PixelPoint resizeStartPosition;
    private Size resizeStartSize;

    /// <summary>
    /// 检查器这一屏在编辑谁：<c>null</c> = 画布上选中的那个节点（见 <see cref="CanvasSurfaceControl.SelectedNode"/>）；
    /// 非空 = 引用浮层里选中的那张引用卡（引用卡不是画布节点，所以要单独记一个）。
    /// 「应用修改」按这个字段决定往哪儿写：节点写节点，引用卡写它指向的那个设定。
    /// </summary>
    private ReferenceCard? selectedInspectorCard;

    /// <summary>当前选中的制作阶段（只影响画布显示哪些节点，不改数据）。规则见 ProductionStageRules。</summary>
    private ProductionStage activeStage = ProductionStage.All;

    /// <summary>最近一次 Agent 提交的回滚点；为空表示没有可撤销的批次。</summary>
    private AgentCommitPoint? lastAgentCommit;

    /// <summary>
    /// 左栏树里的一行。故事画布视角下它对应 <see cref="StoryRow"/>：
    /// 章节 → 分镜 → 引用（场景 / 人物）→ 子引用（道具 / 服装），另有「本章出场」汇总与「未绑定节点」两组。
    /// </summary>
    public sealed class WorkTreeViewNode : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>这一行在树里的稳定键：展开记忆与「定位到某一行」都靠它。</summary>
        public string Key { get; init; } = string.Empty;

        public string Header { get; init; } = string.Empty;

        /// <summary>行首标记（■ ● ○ ◇ ◆）与它的颜色：种类一眼可分，不靠读文字。</summary>
        public string Marker { get; init; } = string.Empty;
        public IBrush MarkerBrush { get; init; } = Brushes.Transparent;

        /// <summary>行尾小标签，例如「子引用」「去重汇总」；空就不显示。</summary>
        public string TagText { get; init; } = string.Empty;
        public IBrush TagBrush { get; init; } = Brushes.Transparent;

        public IBrush HeaderBrush { get; init; } = Brushes.Transparent;
        public WorkTreeItem? Item { get; init; }

        /// <summary>项目树里指向画布库文件的节点：双击就把它打开成标签。</summary>
        public string? CanvasPath { get; init; }

        public bool IsGroup { get; init; }

        /// <summary>这一行代表哪个画布节点（组行与引用行为 null，用 NodeIds 走一对多）。</summary>
        public Guid? NodeId { get; init; }

        /// <summary>一行对应多个画布节点时的全部目标（章节行、「本章出场」行）。</summary>
        public IReadOnlyList<Guid> NodeIds { get; init; } = Array.Empty<Guid>();

        public Guid? EntityId { get; init; }
        public Guid? VariantId { get; init; }

        public List<WorkTreeViewNode> Children { get; init; } = new();

        /// <summary>展开状态改变时的回调：MainWindow 用它把展开记忆记下来（重建左栏时恢复）。</summary>
        public Action<string, bool>? OnExpandedChanged { get; init; }

        /// <summary>
        /// 展开状态。双向绑到 <c>TreeViewItem.IsExpanded</c>：
        /// 用户点开要把状态记下来（否则每次画布一变、树重建就全塌回去）；
        /// 「定位到某一行」也要先把它和它的祖先展开。
        /// </summary>
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                if (Key.Length > 0) OnExpandedChanged?.Invoke(Key, value);
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        private bool isExpanded;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>左栏那棵树看的是「工作树」还是「项目文件」。</summary>
    private enum ResourceViewMode { WorkTree, Project }

    /// <summary>中央区显示的是画布、时间轴还是剧本。</summary>
    private enum CenterView { Canvas, Timeline, Script }

    private ResourceViewMode resourceView = ResourceViewMode.WorkTree;
    private CenterView centerView = CenterView.Canvas;

    public MainWindow()
    {
        InitializeComponent();
        // 版本号只有一处来源（程序集）：标题栏那枚胶囊与左栏最底下一行都从这里取。
        TitleVersionText.Text = AppVersion.Display;
        RailVersionText.Text = AppVersion.Display + " · 本地项目";
        Opened += MainWindow_OnOpened;
        AgentWorkbenchPanel.Attach(this);
        AgentWorkbenchPanel.CloseRequested += (_, _) => ShowInspectorMode();
        // 面板顶部状态每刷一次就同步右下角那枚常驻入口：换模型是在面板自己的下拉里做的，
        // 宿主这边不会有人通知（不然会出现「切到别家模型了、徽标还是旧的那家」）。
        AgentWorkbenchPanel.HeaderRefreshed += (_, _) => UpdateAgentBadge();
        CanvasSurfaceControl.SelectedNodeChanged += CanvasSurface_OnSelectedNodeChanged;
        CanvasSurfaceControl.EdgeSelectionChanged += CanvasSurface_OnEdgeSelectionChanged;
        CanvasSurfaceControl.Notice += (_, message) =>
        {
            StatusText.Text = message;
            // 画布里的 Esc / 删除会改掉连接模式，两个工具按钮的高亮跟着对一下，免得显示的和实际不一致。
            SetButtonActive(ConnectToolButton, CanvasSurfaceControl.IsConnecting);
            SetButtonActive(SelectToolButton, !CanvasSurfaceControl.IsConnecting);
        };
        CanvasSurfaceControl.NodeMutationCompleted += CanvasSurface_OnNodeMutationCompleted;
        CanvasSurfaceControl.NodeContextRequested += CanvasSurface_OnNodeContextRequested;
        CanvasSurfaceControl.ConnectionRequested += CanvasSurface_OnConnectionRequested;
        CanvasSurfaceControl.NodeDoubleClicked += CanvasSurface_OnNodeDoubleClicked;
        CanvasSurfaceControl.NodeReferenceDoubleClicked += CanvasSurface_OnReferenceDoubleClicked;
        // Esc：画布没东西可取消时退一层临时引用画布（一层层退，退到底就是真画布）。
        CanvasSurfaceControl.EscapePressed += (_, _) =>
        {
            if (referenceCanvas is not null) LeaveReferenceCanvas();
        };
        CanvasSurfaceControl.ReferenceSelected += CanvasSurface_OnReferenceSelected;
        CanvasSurfaceControl.ReferenceDoubleClicked += CanvasSurface_OnReferenceTagDoubleClicked;
        // 点了节点下方的引用预览框：图片放大看、视频交给系统播放器放。
        CanvasSurfaceControl.ReferencePreviewActivated += (_, preview) => ShowReferenceMedia(preview);
        // 点节点**自己**出的图 → 放大看。卡片上那排缩略图既是「挂上了」的凭据，也是看大图的入口。
        CanvasSurfaceControl.OwnMediaActivated += (_, media) => ShowImagePreview(media.Path, media.Title);
        // 右键这一张 → 可以删掉它（文件移入回收站）。删除的规矩在这里，画布只说「删哪个」。
        CanvasSurfaceControl.OwnMediaDeleteRequested += (_, media) => RemoveOwnMedia(media.NodeId, media.Reference);
        // 浮层里双击一张引用卡：以这条引用为源头另开一张临时画布，在那里改它的名称 / 描述 / 子引用。
        CanvasSurfaceControl.ReferenceCardDoubleClicked += (_, card) => EnterReferenceCanvasFromReference(card);
        // 浮层里单击一张引用卡：检查器要显示这张卡指向的那个设定（引用卡不是画布节点，走单独一路）。
        CanvasSurfaceControl.ReferenceCardSelected += CanvasSurface_OnReferenceCardSelected;
        // 候选图那批数据由主窗口保管（画布只画）：它是临时态，不写进画布 JSON，
        // 画布每次重建都从这里取，所以重建不会冲掉用户已经点中的那一张。
        CanvasSurfaceControl.ImageBatchOf = nodeId => imageBatches.TryGetValue(nodeId, out var batch) ? batch : null;
        CanvasSurfaceControl.BatchActionRequested = OnImageBatchAction;
        CanvasSurfaceControl.ResourceDropped += CanvasSurface_OnResourceDropped;
        CanvasSurfaceControl.BeforeCanvasMutation += (_, _) => RecordSnapshot();
        CanvasSurfaceControl.CanvasChanged += CanvasSurface_OnCanvasChanged;
        CanvasSurfaceControl.ZoomChanged += (_, scale) =>
        {
            zoomPercent = (int)Math.Round(scale * 100);
            ZoomText.Text = $"{zoomPercent}%";
        };
        ResetCanvasUi();
        // 先把右下角那枚入口按当前厂家算一遍：它是「面板收起来时才显示」，
        // 而面板天生是收起来的——不主动算一次就要等到第一次切模型才会出现。
        UpdateAgentBadge();
        // 出图开奖开关也先算一次：画布第一次画那排候选卡时就要知道该不该露图。
        RefreshRevealPreferences();

        // 标签条溢出入口：放不下才露出来（可见性只在真正变化时改，免得 LayoutUpdated 自己触发自己）。
        CanvasTabOverflowButton.Click += (_, _) => ShowCanvasTabMenu();
        CanvasTabScroller.LayoutUpdated += (_, _) => UpdateTabOverflowHint();

        // 关窗口时把标签记录写下来：标签里可能带着还没落盘的编辑，下次打开要能接上。
        // 这里不报错——窗口都要关了，弹出来的话用户也只能点确定；失败原因写进状态栏也来不及看。
        Closing += (_, _) =>
        {
            PersistCanvasTabs(reportFailure: false);
            // 图像工厂自己持有的执行宿主也要收干净（任务轮询与任务库连接都在它手里）。
            _ = ImageProviderFactory.DisposeSharedHostAsync();
        };

        // 启动先让用户选项目：选定之前整块工作台都不显示（含画布、工作树、Agent 面板与设置）。
        StartPage.CreateRequested += async (_, name) => await CreateProjectAsync(name);
        StartPage.OpenRequested += async (_, _) => await PickAndOpenProjectAsync();
        StartPage.RecentRequested += async (_, path) => await OpenProjectAtAsync(path);

        // Delete 的兜底：焦点在别处（比如刚点过左栏的树）时也要能删掉画布上选中的东西。
        // 用隧道阶段先看一眼：焦点在文本输入里就直接放行——那里的 Delete 是删字符。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // 窗口缩放：按下发生在抓手上，移动与松手由**窗口**统一接（指针被抓手捕获，
        // 事件路由里窗口仍在最外层，隧道阶段一定经过这里），这样 8 个抓手不用各写一套。
        AddHandler(PointerMovedEvent, ResizeGrip_OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, ResizeGrip_OnPointerReleased, RoutingStrategies.Tunnel);
        ShowStartPage();
    }

    // ==================== 更新 ====================

    /// <summary>
    /// 窗口第一次显示之后再处理更新：先把上一次替换的结果说清楚（成功列更新内容，失败说失败在哪），
    /// 再静默查一次有没有新版本。放在 Opened 而不是构造函数里，是因为要弹的对话框需要一个已经存在的宿主窗口。
    /// </summary>
    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_OnOpened;
        await UpdateFlow.HandleStartupAsync(this);
    }

    // ==================== 启动页：先选项目 ====================

    /// <summary>本次会话是否已经处理过接入引导（换个项目不该再问一遍）。</summary>
    private bool onboardingHandled;

    /// <summary>展示启动页并把工作台整体藏起来。</summary>
    private void ShowStartPage()
    {
        WorkbenchRoot.IsVisible = false;
        StartPage.IsVisible = true;
        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        var items = new List<RecentProjectItem>();
        foreach (var path in ProjectHistory.List())
        {
            var project = ProjectContext.Open(path);
            var name = project?.Descriptor.Name is { Length: > 0 } descriptorName ? descriptorName : Path.GetFileName(path);
            items.Add(new RecentProjectItem(name, path, DescribeRecent(project, path), Directory.Exists(path)));
        }

        StartPage.SetRecent(items);
    }

    /// <summary>最近项目那行小字：先说能不能打开，再说最后改动时间。</summary>
    private static string DescribeRecent(ProjectContext? project, string path)
    {
        if (project is null) return "认不出是 YEEYEEYEE 项目（缺少或损坏 project.json）";
        if (!Directory.Exists(path)) return "文件夹不存在";

        try
        {
            var stamp = File.GetLastWriteTime(project.ProjectFilePath);
            return $"最后修改 {stamp:yyyy-MM-dd HH:mm}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "最后修改时间读不到";
        }
    }

    /// <summary>
    /// 建项目 / 开项目之后统一进入工作台。四处入口（启动页新建、启动页打开、启动页最近、左侧栏两个按钮）共用，
    /// 免得有的入口记得写最近列表、有的忘了加载画布。
    /// </summary>
    private async Task EnterWorkbenchAsync(ProjectContext project)
    {
        // 换项目之前先把上一个项目的标签记录写好（写的是**上一个**项目根目录，所以必须在 UseProject 之前）。
        if (canvasTabs.Count > 0) PersistCanvasTabs(reportFailure: false);

        AppPaths.UseProject(project);
        ProjectHistory.Add(project.RootPath);
        UpdateProjectUi(project);
        LoadProjectCanvasTabs();

        StartPage.IsVisible = false;
        WorkbenchRoot.IsVisible = true;
        StatusText.Text = $"项目已打开：{project.Descriptor.Name}";

        // 先选项目、再问模型：两件事同时糊在脸上，用户不知道该先答哪个。
        await RunPendingOnboardingAsync();
    }

    /// <summary>接入引导：选完项目之后再问一次（已做过选择就不再问）。</summary>
    private async Task RunPendingOnboardingAsync()
    {
        if (onboardingHandled) return;
        onboardingHandled = true;
        if (AiProviderSettings.Load().ProviderChoiceMade) return;

        var outcome = await AgentOnboardingDialog.ShowAsync(this, firstRun: true);
        if (outcome is null)
        {
            // 用户选了「稍后再说」：不写 ProviderChoiceMade（下次启动还会问），只在状态栏说明当前处境。
            StatusText.Text = "还没有接入大模型：AI 生成与对话会使用本地模拟的占位结果（Agent 面板右上角 ★ 接入）";
            return;
        }

        SyncProviderOutcome(outcome);
    }

    /// <summary>把提示送到当前真正看得见的地方：还在启动页就写在启动页上，进了工作台就写状态栏。</summary>
    private void Report(string message, bool warning)
    {
        if (StartPage.IsVisible) StartPage.ShowStatus(message, warning);
        else StatusText.Text = message;
    }

    private async Task CreateProjectAsync(string? name)
    {
        try
        {
            var parent = AppPaths.DefaultProjectsRoot;
            Directory.CreateDirectory(parent);
            var project = ProjectContext.Create(parent, NewProjectFolderName(parent, name), ProjectType.Other);
            await EnterWorkbenchAsync(project);
            StatusText.Text = $"项目已创建：{project.Descriptor.Name}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Report($"项目创建失败：{error.Message}", true);
        }
    }

    /// <summary>
    /// 项目文件夹名：用户填了就用他填的（去掉文件名非法字符），留空按时间命名。
    /// 重名就加序号——**不能默默写进已有文件夹**，那等于把别人的项目当新项目改。
    /// </summary>
    private static string NewProjectFolderName(string parent, string? name)
    {
        var desired = string.IsNullOrWhiteSpace(name) ? $"未命名项目-{DateTime.Now:yyyyMMdd-HHmmss}" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) desired = desired.Replace(invalid, '-');

        var candidate = desired;
        for (var index = 2; Directory.Exists(Path.Combine(parent, candidate)); index++) candidate = $"{desired}-{index}";
        return candidate;
    }

    private async Task PickAndOpenProjectAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择项目文件夹",
                AllowMultiple = false
            });

            var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                Report("未选择项目文件夹", false);
                return;
            }

            await OpenProjectAtAsync(folderPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Report($"项目打开失败：{error.Message}", true);
        }
    }

    private async Task OpenProjectAtAsync(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                Report($"项目文件夹不存在：{folderPath}", true);
                RefreshRecentProjects();
                return;
            }

            var project = ProjectContext.Open(folderPath)
                ?? throw new InvalidDataException("所选文件夹不是有效的 YEEYEEYEE 项目。");
            await EnterWorkbenchAsync(project);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Report($"项目打开失败：{error.Message}", true);
        }
    }

    /// <summary>
    /// 接入引导结束后刷新面板与状态栏。两处入口（首次启动、面板上的重新接入）共用这一个，
    /// 否则容易出现「配置写进去了但面板上的模型标签还是旧的」这类只刷新了一半的状态。
    /// </summary>
    private void SyncProviderOutcome(AgentOnboardingOutcome outcome)
    {
        AgentWorkbenchPanel.SyncHostState();
        StatusText.Text = outcome.UseLocal
            ? "已选择本地模拟：AI 生成与对话只是关键词占位结果"
            : $"已接入 {AiProviderSettings.Load().Model}，下一轮对话生效";
    }

    // 左侧栏这两个按钮与启动页走同一条路径：建/开项目、写最近列表、加载画布只有一份实现。
    private async void NewProject_OnClick(object? sender, RoutedEventArgs e) => await CreateProjectAsync(null);

    private async void OpenProject_OnClick(object? sender, RoutedEventArgs e) => await PickAndOpenProjectAsync();

    private void UpdateProjectUi(ProjectContext project)
    {
        ProjectNameText.Text = project.Descriptor.Name;
        // 左栏的项目卡与顶栏显示同一个项目名：不写它就会一直挂着界面自带的示例名（雾港来信）。
        WorkTreeProjectLabel.Text = project.Descriptor.Name;
        ProjectPathText.Text = project.RootPath;
        canEdit = CanEditProject(project);
        ProjectStatusText.Text = canEdit ? "可编辑" : "只读";
        ResetCanvasUi();
        // 换项目等于换了可写性与画布，Agent 面板的顶部状态与撤销入口都要跟着刷新。
        AgentWorkbenchPanel.SyncHostState();
    }

    internal static bool CanEditProject(ProjectContext project)
    {
        try
        {
            if (!File.Exists(project.ProjectFilePath) || (File.GetAttributes(project.ProjectFilePath) & FileAttributes.ReadOnly) != 0) return false;
            var projectDirectory = Path.GetDirectoryName(project.ProjectFilePath);
            if (string.IsNullOrWhiteSpace(projectDirectory)) return false;
            if ((File.GetAttributes(projectDirectory) & FileAttributes.ReadOnly) != 0) return false;
            var libraryDirectory = ProjectLibrary.Directory;
            Directory.CreateDirectory(libraryDirectory);
            var probe = Path.Combine(libraryDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty); File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ResetCanvasUi()
    {
        currentCanvas = null;
        currentCanvasPath = null;
        canvasTabs.Clear();
        activeCanvasTab = null;
        // 临时引用画布属于上一个项目：它的源头节点、设定对象都来自那边，留着就是悬空引用。
        // 整栈清掉——钻了几层就得清几层。
        referenceCanvasStack.Clear();
        SyncReferenceOverlayFlag();
        lastAgentCommit = null;
        ClearEditHistory();
        CanvasTabStrip.Children.Clear();
        CanvasSurfaceControl.State = null;
        WorkTreeView.ItemsSource = Array.Empty<WorkTreeViewNode>();
        CanvasTitleText.Text = "尚未创建画布";
        CanvasNodeCountText.Text = "--";
        CanvasEdgeCountText.Text = "--";
        CanvasRevisionText.Text = "--";
        CanvasSizeText.Text = "尚未创建画布";
        ProjectModifiedText.Text = "--";
        // 检查器也要跟着清空：界面自带的那份「第一章 · 雨夜码头 / 已拆分为 42 章」不清掉就会挂在空项目上。
        ShowInspectorEmptyState(updateStatus: false);
        // 时间轴 / 剧本开着的时候换项目：那一屏也得跟着变成「还没有画布」。
        RefreshOpenCenterView();
        // 制作阶段的按钮与芯片也回到「全部」：新项目里还什么都没拆。
        SelectStage(ProductionStage.All);
    }

    /// <summary>
    /// 清空撤销 / 重做记录。换画布、换项目之后必须清：撤销栈里存的是**上一张画布**的快照，
    /// 留着它一点「撤销」，就会把 A 画布的内容套到 B 画布上。
    /// </summary>
    private void ClearEditHistory()
    {
        undoSnapshots.Clear();
        redoSnapshots.Clear();
    }

    // ==================== 画布标签：切换 / 新建 / 关闭 ====================

    /// <summary>
    /// 进项目时把画布标签还原出来，顺序是：①上次的标签记录（可能带着未保存的编辑）
    /// ②绑定的画布 / 最近改过的画布 ③一张都没有就**自动建「画布1」**。
    ///
    /// 第三条是这次改动的主因：进项目就该有一张能画东西的画布，而不是一片空白加一句「尚未创建画布」——
    /// 用户会以为程序没打开成功。
    /// </summary>
    private void LoadProjectCanvasTabs()
    {
        canvasTabs.Clear();
        activeCanvasTab = null;
        referenceCanvasStack.Clear();
        SyncReferenceOverlayFlag();
        if (CanvasTabStore.TryLoad(out var restored, out var activeTabId) && restored.Count > 0)
        {
            canvasTabs.AddRange(restored);
            var target = canvasTabs.FirstOrDefault(tab => tab.Id == activeTabId) ?? canvasTabs[0];
            AdoptCanvasTab(target);
            StatusText.Text = $"已恢复 {canvasTabs.Count} 张画布：{target.Title}";
            return;
        }

        var recent = CanvasLibrary.List().FirstOrDefault();
        if (recent is not null && CanvasLibrary.TryLoad(recent.Path, out var state) && state is not null)
        {
            var tab = new CanvasTab(state) { Path = recent.Path };
            canvasTabs.Add(tab);
            AdoptCanvasTab(tab);
            StatusText.Text = $"已打开画布：{tab.Title}";
            return;
        }

        CreateCanvasTab();
    }

    /// <summary>把当前编辑中的状态收进活动标签（独立副本），并记下未保存标记。</summary>
    private void CaptureActiveTab()
    {
        // 临时引用画布不属于任何标签：这时抓快照会把标签内容覆盖成那棵树，绝对不能做。
        if (referenceCanvas is not null) return;
        if (activeCanvasTab is null || currentCanvas is null) return;
        activeCanvasTab.Title = currentCanvas.Title;
        activeCanvasTab.Path = currentCanvasPath;
        activeCanvasTab.Snapshot = CanvasCloner.Clone(currentCanvas);
    }

    /// <summary>
    /// 把某个标签装进工作区（切换画布的最后一步）：画布控件、状态栏读数、标签条、Agent 面板一起换。
    /// 给界面编辑的是**副本**，标签里留着「离开时的样子」——两者共用同一个对象就会互相盖。
    /// </summary>
    private void AdoptCanvasTab(CanvasTab tab)
    {
        activeCanvasTab = tab;
        currentCanvas = CanvasCloner.Clone(tab.Snapshot);
        currentCanvasPath = tab.Path;
        CanvasSurfaceControl.State = currentCanvas.Canvas;
        ClearEditHistory();
        RefreshResourceList();
        RefreshOpenCenterView();
        RebuildCanvasTabStrip();
        UpdateCanvasUi(tab.Path);
        AgentWorkbenchPanel.SyncHostState();
    }

    /// <summary>
    /// 切换标签：先把当前这张收进它自己的标签，再装载目标标签。
    ///
    /// **不写盘**：切换画布不等于保存。悄悄改文件（还会顶一次修订号）会让用户以为内容已经存过了；
    /// 未保存的内容留在标签里（标题带 *），要不要落盘由「保存修订」决定。
    /// </summary>
    private void ActivateCanvasTab(CanvasTab tab)
    {
        // 在临时引用画布上时点任何标签都等于「返回原画布」，再按标签本来的语义走。
        if (referenceCanvas is not null) LeaveAllReferenceCanvases();
        if (ReferenceEquals(tab, activeCanvasTab)) return;
        CaptureActiveTab();
        AdoptCanvasTab(tab);
        StatusText.Text = $"已切换到 {tab.Title}{DirtyMark(tab)}";
    }

    /// <summary>
    /// 新建一张画布标签并切过去。左栏的「新建画布」与标签条上的 ＋ 共用这一个入口。
    ///
    /// 能写盘就立刻落盘：只有落了盘它才是一个真文件，而「保存」「Agent 落改动」都要求画布有路径；
    /// 写不进去（只读项目）就只在内存里建，并如实说明它不会存下来。
    /// </summary>
    private void CreateCanvasTab()
    {
        if (!HasOpenProject()) return;
        if (referenceCanvas is not null) LeaveAllReferenceCanvases();
        CaptureActiveTab();

        var title = CanvasTabRules.NextTitle(ExistingCanvasTitles());
        var state = CanvasTabRules.CreateBlank(title);

        string? path = null;
        var saveFailure = string.Empty;
        if (canEdit)
        {
            try
            {
                path = CanvasLibrary.Save(state, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // 建不出来不是「画布坏了」，是写盘失败：画布照给，但要说清它只存在于本次会话。
                saveFailure = $"（写盘失败：{error.Message}；这张画布只存在于本次会话，关掉程序就没了）";
            }
        }
        else
        {
            saveFailure = "（当前项目只读，这张画布不会写入磁盘，Agent 的改动也落不下来）";
        }

        var tab = new CanvasTab(state) { Path = path, Dirty = path is null };
        canvasTabs.Add(tab);
        AdoptCanvasTab(tab);
        PersistCanvasTabs(reportFailure: true);
        StatusText.Text = $"已新建画布：{tab.Title}{saveFailure}";
    }

    /// <summary>
    /// 关闭标签：至少留一张。只剩一张时按钮本来就是隐藏的，这里再兜一次底——
    /// 一张不剩的话工作区会变成「没有画布可编辑」，而用户并没有要这个结果。
    /// 关闭**不删文件**，但会把这张标签从记录里去掉，所以带未保存编辑时必须先问清楚：
    /// 这是唯一一条「不保存就真的丢内容」的路（关窗口 / 换项目都会把标签快照写进记录）。
    /// </summary>
    private async Task CloseCanvasTabAsync(CanvasTab tab)
    {
        if (referenceCanvas is not null) LeaveAllReferenceCanvases();
        if (canvasTabs.Count <= 1) return;
        if (tab.Dirty && !await ConfirmClosingDirtyTabAsync(tab)) return;

        if (!ReferenceEquals(tab, activeCanvasTab))
        {
            canvasTabs.Remove(tab);
            RebuildCanvasTabStrip();
            PersistCanvasTabs(reportFailure: true);
            StatusText.Text = $"已关闭标签 {tab.Title}（画布文件仍在画布库里）";
            return;
        }

        var index = canvasTabs.IndexOf(tab);
        if (index < 0) return;
        canvasTabs.Remove(tab);
        // 关掉当前这张之后，落到左边一张（没有左边就落到新的第一张），与浏览器标签的习惯一致。
        AdoptCanvasTab(canvasTabs[Math.Clamp(index - 1, 0, canvasTabs.Count - 1)]);
        PersistCanvasTabs(reportFailure: true);
        StatusText.Text = $"已关闭标签 {tab.Title}（画布文件仍在画布库里）";
    }

    /// <summary>
    /// 关掉带未保存编辑的标签之前问一次。能保存的就给「先保存再关闭」这条路；
    /// 保存不了（项目只读 / 这张画布从没落盘 / 它不是当前画布）就只用两选一，并把「为什么」写清楚——
    /// 给一个点了必然失败的「先保存」比不给更糟。
    /// </summary>
    private async Task<bool> ConfirmClosingDirtyTabAsync(CanvasTab tab)
    {
        var isActive = ReferenceEquals(tab, activeCanvasTab);
        if (isActive && tab.Path is not null && canEdit)
        {
            var choice = await AgentDialogUi.AskUnsavedAsync(
                this,
                "关闭画布标签",
                $"「{tab.Title}」还有没保存的编辑（标签标题上的 * 就是它）。\n\n"
                + "关闭标签不会删除画布文件，但没有保存的内容只会留在这个标签里，关掉就找不回来了。",
                "先保存再关闭",
                "不保存直接关闭");
            if (choice == AgentUnsavedChoice.Cancel) return false;
            if (choice == AgentUnsavedChoice.SaveFirst && !TrySaveCanvas(out var saveMessage))
            {
                StatusText.Text = $"{saveMessage}；标签没有关闭";
                return false;
            }
            return true;
        }

        var reason = tab.Path is null
            ? "这张画布从没写进磁盘（项目只读或写盘失败），关掉标签后内容就找不回来了。"
            : isActive
                ? "项目或项目库不可写，没法保存：关掉标签后没保存的内容就找不回来了。"
                : "它现在不是当前画布：想保下来的话，先点这个标签切过去、点「保存修订」，再关它。";
        return await AgentDialogUi.ConfirmAsync(
            this,
            "关闭画布标签",
            $"「{tab.Title}」还有没保存的编辑（标签标题上的 * 就是它）。\n\n{reason}",
            "仍然关闭");
    }

    // ==================== 重命名 ====================

    /// <summary>
    /// 双击标签重命名。画布标题就是画布库里的**文件名**，所以改名要连文件一起改：
    /// 先写新文件、成功了再删旧文件，中途失败时旧文件还在，内容不会丢。重名一律拒绝（见 DescribeNameConflict）。
    /// 只改当前画布这一张——改名要写盘，写盘写的是「当前画布」那份状态，改别人就是把当前内容写进别人的文件。
    /// </summary>
    private async Task RenameCanvasTabAsync(CanvasTab tab)
    {
        if (!ReferenceEquals(tab, activeCanvasTab)) ActivateCanvasTab(tab);
        if (!ReferenceEquals(tab, activeCanvasTab) || currentCanvas is null) return;

        var newTitle = await AskCanvasTitleAsync(tab.Title);
        if (newTitle is null) return;

        // 查重集合 = 画布库里的其它标题 / 文件名 + 别的标签标题（去掉自己）。
        var taken = ExistingCanvasTitles()
            .Where(title => !string.Equals(title?.Trim(), tab.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .Concat(canvasTabs.Where(other => !ReferenceEquals(other, tab)).Select(other => other.Title));
        if (CanvasTabRules.DescribeNameConflict(newTitle, taken) is { } conflict)
        {
            StatusText.Text = conflict;
            return;
        }

        var oldPath = currentCanvasPath;
        if (oldPath is not null && !canEdit)
        {
            StatusText.Text = "项目或项目库不可写，改名写不进磁盘，已取消（画布与文件都没动）";
            return;
        }

        string? newPath = null;
        if (oldPath is not null)
        {
            var candidate = currentCanvas with { Title = newTitle, Revision = currentCanvas.Revision + 1 };
            try
            {
                newPath = CanvasLibrary.Save(candidate, CanvasLibrary.PathForTitle(newTitle));
                currentCanvas = candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                StatusText.Text = $"改名失败：{error.Message}（画布与文件都没动）";
                return;
            }

            if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                try
                {
                    File.Delete(oldPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // 新文件已经写好了，只差清理：如实说清楚，别让用户以为改名失败了。
                    StatusText.Text = $"新文件 {Path.GetFileName(newPath)} 已写入，但旧文件 {Path.GetFileName(oldPath)} 删不掉，请手工清理。{error.Message}";
                }
            }
        }
        else
        {
            // 还没落盘的画布（只读项目）：只改内存里的标题，标签记录会把它带上。
            currentCanvas = currentCanvas with { Title = newTitle };
        }

        currentCanvasPath = newPath;
        tab.Title = newTitle;
        tab.Path = newPath;
        tab.Dirty = newPath is null;          // 写进磁盘了就不算未保存；从没落盘的仍然算
        tab.Snapshot = CanvasCloner.Clone(currentCanvas);
        RefreshResourceList();                // 左栏「项目 · 画布名」读的是标题，不刷新就还挂着旧名字
        UpdateCanvasUi(currentCanvasPath);
        RebuildCanvasTabStrip();
        PersistCanvasTabs(reportFailure: true);
        StatusText.Text = newPath is null
            ? $"已重命名为 {newTitle}（这张画布还没写进磁盘）"
            : $"画布已重命名为 {newTitle}，文件同步改为 {Path.GetFileName(newPath)}";
    }

    /// <summary>
    /// 改名输入框。留空就地提示、不关窗口；名字没变直接当取消——
    /// 空名字会被写成「未命名画布」文件，那不是用户想看到的结果。
    /// </summary>
    private async Task<string?> AskCanvasTitleAsync(string current)
    {
        var field = AgentDialogUi.Field("画布名称（同时是画布库里的文件名）", current);
        var error = AgentDialogUi.Note(string.Empty, AgentNoteLevel.Error);
        error.IsVisible = false;

        var cancel = AgentDialogUi.Secondary("取消");
        var apply = AgentDialogUi.Primary("应用");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, apply }
        };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(AgentDialogUi.Header("重命名画布"));
        body.Children.Add(field.Label);
        body.Children.Add(field.Box);
        body.Children.Add(error);

        var dialog = DialogShell.Create(
            "重命名画布",
            AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)),
            460);

        string? result = null;
        void Apply()
        {
            var candidate = field.Box.Text?.Trim() ?? string.Empty;
            if (candidate.Length == 0)
            {
                error.Text = "画布名称不能为空。";
                error.IsVisible = true;
                return;
            }
            result = string.Equals(candidate, current.Trim(), StringComparison.Ordinal) ? null : candidate;
            dialog.Close();
        }

        cancel.Click += (_, _) => dialog.Close();
        apply.Click += (_, _) => Apply();
        field.Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Apply();
        };
        dialog.Opened += (_, _) =>
        {
            field.Box.Focus();
            field.Box.SelectAll();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    /// <summary>
    /// 重建标签条。标签是动态的，所以整条在代码里建，样式与活动状态只在这一个地方定义。
    /// </summary>
    private void RebuildCanvasTabStrip()
    {
        CanvasTabStrip.Children.Clear();
        foreach (var tab in canvasTabs)
        {
            // 在临时引用画布上时，真画布标签都不算活动——否则看不出自己到底在编辑哪一张。
            var active = referenceCanvas is null && ReferenceEquals(tab, activeCanvasTab);

            var title = new TextBlock
            {
                Text = $"{tab.Title}{DirtyMark(tab)}",
                FontSize = 11,
                MaxWidth = 180,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush(active ? "#E9EFF7" : "#93A1B3")
            };

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            content.Children.Add(title);

            // 只剩一张时不显示 ×：按钮点不动比没有按钮更让人困惑。
            if (canvasTabs.Count > 1)
            {
                var close = new Button { Content = "×", Padding = new Thickness(5, 0), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
                close.Classes.Add("miniButton");
                ToolTip.SetTip(close, "关闭标签（不会删除画布文件）");
                close.Click += async (_, _) => await CloseCanvasTabAsync(tab);
                Grid.SetColumn(close, 1);
                content.Children.Add(close);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 3, 6, 1),
                Background = active ? Brush("#1A2431") : Brushes.Transparent,
                BorderBrush = active ? Brush("#594D9BFF") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            card.Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    content,
                    // 活动标签下面的那道亮线：不靠颜色深浅，一眼能看出在看哪张。
                    new Border
                    {
                        Height = 2,
                        CornerRadius = new CornerRadius(1),
                        Margin = new Thickness(2, 0, 2, 0),
                        Background = active ? Brush("#4D9BFF") : Brushes.Transparent
                    }
                }
            };
            ToolTip.SetTip(card, "点击切换画布 · 双击重命名");
            card.PointerPressed += (_, e) =>
            {
                // 点在 × 上也要切标签是明显的骚扰（关掉别的标签却把当前画布换掉），这里避开按钮上的点击。
                if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
                ActivateCanvasTab(tab);
            };
            card.DoubleTapped += async (_, _) => await RenameCanvasTabAsync(tab);
            CanvasTabStrip.Children.Add(card);
        }

        var add = new Button { Content = "＋", Padding = new Thickness(9, 1), FontSize = 13, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        add.Classes.Add("miniButton");
        ToolTip.SetTip(add, "新建画布");
        add.Click += (_, _) => CreateCanvasTab();
        CanvasTabStrip.Children.Add(add);

        // 临时引用画布不是标签（不能切走、不能关闭、不落盘），所以在标签条上单独摆一枚标记，
        // 点它就是「退回上一层」（最底一层即返回原画布）——铺在画布上的东西必须有一条明确的回去的路。
        if (referenceCanvas is { } session)
        {
            var depthText = ReferenceCanvasDepth > 1 ? $" 第 {ReferenceCanvasDepth} 层" : string.Empty;
            var chipTitle = new TextBlock
            {
                Text = $"⟲ {session.Title}{depthText}{(session.Dirty ? " *" : string.Empty)}",
                FontSize = 11,
                MaxWidth = 210,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("#E9EFF7")
            };
            var backLabel = ReferenceCanvasDepth > 1 ? "返回上一层" : "返回原画布";
            var back = new Button { Content = backLabel, Padding = new Thickness(6, 0), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            back.Classes.Add("miniButton");
            ToolTip.SetTip(back, ReferenceCanvasDepth > 1 ? "退回上一层临时画布（Esc 同效）" : "回到原来的画布（Esc 同效）");
            back.Click += (_, _) => LeaveReferenceCanvas();

            var chipContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            chipContent.Children.Add(chipTitle);
            Grid.SetColumn(back, 1);
            chipContent.Children.Add(back);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 3, 6, 3),
                Margin = new Thickness(6, 0, 0, 2),
                Background = Brush("#17283B"),
                BorderBrush = Brush("#7FB2E8"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = chipContent
            };
            ToolTip.SetTip(chip, "临时引用画布：不落盘、不进标签记录。「保存修订」= 把改动写回设定库与源头节点");
            CanvasTabStrip.Children.Add(chip);
        }
        UpdateTabOverflowHint();
    }

    /// <summary>
    /// 标签条放不下时把「全部标签」入口露出来（浏览器也是这么处理的）。
    /// 只在可见性真的变化时改属性：LayoutUpdated 里改属性会再触发一次布局，改来改去就成了死循环。
    /// </summary>
    private void UpdateTabOverflowHint()
    {
        var overflowing = CanvasTabStrip.Bounds.Width > CanvasTabScroller.Bounds.Width + 1;
        if (CanvasTabOverflowButton.IsVisible != overflowing) CanvasTabOverflowButton.IsVisible = overflowing;
    }

    /// <summary>溢出时的兜底入口：一次列出全部画布标签，点哪个切哪个（顺便带一个新建）。</summary>
    private void ShowCanvasTabMenu()
    {
        var menu = new MenuFlyout();
        foreach (var tab in canvasTabs)
        {
            // 用 ● 标出当前标签：下拉里没有高亮，不标的话用户不知道自己在哪一张上。
            var active = ReferenceEquals(tab, activeCanvasTab);
            var item = new MenuItem { Header = $"{(active ? "●" : "　")} {tab.Title}{DirtyMark(tab)}" };
            item.Click += (_, _) => ActivateCanvasTab(tab);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "＋ 新建画布" };
        create.Click += (_, _) => CreateCanvasTab();
        menu.Items.Add(create);
        menu.ShowAt(CanvasTabOverflowButton);
    }

    /// <summary>画布标题上是否带未保存标记（与旧端一致：标题后一个 *）。</summary>
    private static string DirtyMark(CanvasTab tab) => tab.Dirty ? " *" : string.Empty;

    /// <summary>
    /// 已经用掉的画布标题：标签上的 + 画布库里的 + 库目录里的文件名。
    /// 最后一项也要算上——读不出来的坏文件不会出现在画布库里，但同样不能被新画布覆盖掉。
    /// </summary>
    private IEnumerable<string> ExistingCanvasTitles()
    {
        var titles = canvasTabs.Select(tab => tab.Title).ToList();
        titles.AddRange(CanvasLibrary.List().Select(item => item.Title));
        try
        {
            if (Directory.Exists(CanvasLibrary.Directory))
                foreach (var file in Directory.EnumerateFiles(CanvasLibrary.Directory, "*.json"))
                    titles.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 列不出来就按已知的标题算：编号可能重复，但那只是命名不理想，不会破坏已有画布。
        }
        return titles;
    }

    /// <summary>把标签记录写到项目里（含每张标签的画布内容）。</summary>
    private void PersistCanvasTabs(bool reportFailure)
    {
        // 在临时引用画布上时不写标签记录：那份记录里只该有真画布。
        if (referenceCanvas is not null) return;
        if (canvasTabs.Count == 0) return;
        CaptureActiveTab();
        if (CanvasTabStore.TrySave(canvasTabs, activeCanvasTab?.Id ?? Guid.Empty, out var error)) return;
        // 切标签时写不进必须说：那份还没保存的编辑只有标签记录里有一份。
        if (reportFailure) StatusText.Text = $"标签记录写入失败：{error}（画布文件本身不受影响）";
    }

    private void UpdateCanvasUi(string? path)
    {
        if (currentCanvas is null)
        {
            ResetCanvasUi();
            return;
        }

        CanvasTitleText.Text = currentCanvas.Title;
        CanvasNodeCountText.Text = currentCanvas.Canvas.Nodes.Count.ToString();
        CanvasEdgeCountText.Text = currentCanvas.Canvas.Edges.Count.ToString();
        CanvasRevisionText.Text = currentCanvas.Revision.ToString();
        CanvasSizeText.Text = $"{currentCanvas.Width} × {currentCanvas.Height}";
        ProjectModifiedText.Text = referenceCanvas is not null ? "临时画布 · 不落盘" : DescribeCanvasSavedAt(path);
        // 制作阶段的计数每次都要重算：拆章、出图、绑工作树都会改变这些数字。
        RefreshStageUi();
    }

    /// <summary>画布文件的最后写入时间。还没保存过就如实说「尚未保存」，不摆一个读不出来的假日期。</summary>
    private static string DescribeCanvasSavedAt(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "尚未保存";
        try
        {
            return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "保存时间读不到";
        }
    }

    /// <summary>新建画布：左栏按钮与标签条 ＋ 共用同一个入口，行为完全一致。</summary>
    private void NewCanvas_OnClick(object? sender, RoutedEventArgs e) => CreateCanvasTab();

    private async void SaveCanvas_OnClick(object? sender, RoutedEventArgs e)
    {
        // 保存期间来的推送不当成「可以重载」：那会儿内存里的画布正要落盘，替换掉它是最坏的事。
        savingCanvas = true;
        try
        {
            // 配了服务器并登录着，就把这次保存交给服务端：锁与修订都由它仲裁，改动顺便推给别人。
            // 返回 null 表示这条不适用（没配服务器、没登录、这张画布还没落盘…），照旧走本地保存。
            if (await TrySaveCanvasThroughServerAsync() is { } serverMessage) StatusText.Text = serverMessage;
            else
            {
                TrySaveCanvas(out var message);
                StatusText.Text = message;
            }
        }
        finally
        {
            savingCanvas = false;
        }

        AgentWorkbenchPanel.SyncHostState();
    }

    /// <summary>
    /// 把这次保存交给服务端。返回 null = **这条不适用**，调用方照旧本地保存；
    /// 返回一句话 = 这次的结论（成功或失败都算，本地不再写）。
    ///
    /// 几个刻意的决定：
    /// · **保存前先握手**：服务端那张画布的修订号必须等于我本地这份文件的修订号。不等就说明
    ///   别人改过、或者这台服务器管的根本不是这张画布——两种都不该硬写。
    /// · **服务器叫不到时退回本地写**，但把那句话说出口：桌面端本地优先，写盘不该被服务器绑住。
    /// · 服务端写完**再核对一次本地文件**：修订对得上，才算这次保存真的落在同一张画布上。
    /// </summary>
    private async Task<string?> TrySaveCanvasThroughServerAsync(bool allowLocalFallback = true)
    {
        // 临时引用画布、没有落盘的新画布、只读项目：都不归服务端管，走原来的路。
        if (referenceCanvas is not null || currentCanvas is null || string.IsNullOrWhiteSpace(currentCanvasPath)) return null;
        if (!canEdit) return null;
        var session = collaboration;
        if (session is null || !session.IsSignedIn) return null;

        long localRevision;
        try { localRevision = CanvasRevision.OfFile(currentCanvasPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }

        var (serverRevision, handshakeError) = await session.CanvasRevisionAsync();
        if (serverRevision is null)
        {
            // 自动同步这条路上**不许**退回本地写：打开那个开关的人要的是「推给协作者」，
            // 不是「偷偷替我落盘」——「什么时候落盘由我决定」这条默认行为不该被它推翻。
            if (!allowLocalFallback)
                return "没联上服务器（" + handshakeError + "），这一轮没写；改动还在本地，点「保存修订」可以只写本地。";

            // 叫不到服务器（或会话失效）：不算错误，退回本地写，但说清楚这次没广播给协作者。
            TrySaveCanvas(out var fallback);
            return fallback + "（没联上服务器，这次只写了本地：" + handshakeError + "）";
        }

        if (serverRevision != localRevision)
            return "服务端上这张画布的修订和你本地这份对不上：可能别人刚改过，也可能这台服务器管的不是这张画布。"
                 + "先到「设置 → 协作」核对服务器地址，或重新打开这张画布，再保存。";

        CanvasSurfaceControl.Refresh();
        // 与本地保存同样的准备：托管实体先发布到项目库（服务端会校验画布的实体引用）。
        // 发布过的实体留在库里无害（没被引用的实体不影响任何东西），所以失败时不必回滚。
        try
        {
            foreach (var entity in currentCanvas.Canvas.Entities.Where(entity => entity.ManagedByProject))
                if (!ProjectEntityScope.TryPublish(entity, out var publishError)) return $"项目库写入失败：{publishError}";
            ProjectEntityScope.RefreshSnapshots(currentCanvas.Canvas);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "项目库写入失败：" + error.Message;
        }

        // 修订由服务端推进，所以送出去的是**没自己 +1 的那份**，字节与本地保存时逐字节一致。
        AssetStore.Normalize(currentCanvas.Canvas);
        var result = await session.SaveCanvasAsync(localRevision, CanvasFileWriter.Serialize(currentCanvas));
        if (!result.Ok)
        {
            if (result.Holder is { } holder)
                return $"{DescribeHolder(holder)}正在编辑，这次保存没写进去——等他保存，或让管理员接管。";
            return "保存到服务端失败：" + result.Message;
        }

        long written;
        try { written = CanvasRevision.OfFile(currentCanvasPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "服务端说存好了，但读不回本地文件：" + error.Message; }
        if (written != result.Revision)
            return "服务端说存好了，可你本地这份文件没变——它管的不是这张画布（到「设置 → 协作」核对地址与部署）。";

        // 服务端写完，本地这份就是新的：把内存修订与标签状态对齐，别让「未保存」的标记骗人。
        currentCanvas = currentCanvas with { Revision = currentCanvas.Revision + 1 };
        if (activeCanvasTab is not null)
        {
            activeCanvasTab.Dirty = false;
            activeCanvasTab.Snapshot = CanvasCloner.Clone(currentCanvas);
        }

        // 服务端那边用的是整棵树锁，它会把我们自己的节点锁吸收掉：还在编辑就把节点锁占回来。
        if (CanvasSurfaceControl.SelectedNode is { } editing &&
            (InspectorNameText.IsFocused || InspectorSummaryText.IsFocused))
        {
            var again = await session.AcquireNodeLeaseAsync(editing.Id);
            if (again.Ok) EnsureCollaborationHeartbeat().Start();
            else if (again.Holder is { } blocker)
                SetCollaborationHint($"{DescribeHolder(blocker)}正在编辑这个节点，你现在的改动可能会盖掉他的。");
        }

        return result.Message;
    }

    /// <summary>
    /// 保存当前画布：先发布项目库实体，再写画布文件；任一步失败都整体回滚内存状态。
    /// 返回是否成功，<paramref name="message"/> 是可以直接显示的结论。
    /// **Agent 的改动也走这一条保存入口**，保证「改画布」和「存画布」只有一套语义。
    /// </summary>
    private bool TrySaveCanvas(out string message)
    {
        // 临时引用画布不是画布库里的画布：它的「保存」是把改动写回设定库与源头节点。
        if (referenceCanvas is not null) return TrySaveReferenceCanvas(out message);

        if (currentCanvas is null || string.IsNullOrWhiteSpace(currentCanvasPath))
        {
            // 只读项目 / 写盘失败时新建的画布没有文件路径：这时说「尚未创建画布」会误导（画布明明在眼前）。
            message = currentCanvas is null ? "尚未创建画布" : "这张画布还没有落到磁盘上（只读项目或写盘失败），无法保存";
            return false;
        }

        if (!canEdit)
        {
            message = "项目或项目库不可写，当前为只读";
            return false;
        }

        CanvasSurfaceControl.Refresh();
        var canvasBeforeSave = JsonSerializer.Serialize(currentCanvas, SnapshotOptions);
        var candidate = currentCanvas with { Revision = currentCanvas.Revision + 1 };
        AssetStore.Normalize(candidate.Canvas);
        var libraryPath = ProjectLibrary.FilePath;
        var libraryExisted = File.Exists(libraryPath);
        var libraryBeforeSave = libraryExisted ? File.ReadAllBytes(libraryPath) : null;
        try
        {
            foreach (var entity in candidate.Canvas.Entities.Where(entity => entity.ManagedByProject))
                if (!ProjectEntityScope.TryPublish(entity, out var publishError)) throw new IOException($"项目库写入失败：{publishError}");
            ProjectEntityScope.RefreshSnapshots(candidate.Canvas);
            var savedPath = CanvasLibrary.Save(candidate, currentCanvasPath);
            currentCanvas = candidate;
            currentCanvasPath = savedPath;
            // 存过了就把标签上的未保存标记撤掉，并把新的标题 / 路径记进标签：不然标签会一直挂着 *，
            // 用户会以为「保存修订」没生效。
            if (activeCanvasTab is not null)
            {
                activeCanvasTab.Title = candidate.Title;
                activeCanvasTab.Path = savedPath;
                activeCanvasTab.Dirty = false;
                activeCanvasTab.Snapshot = CanvasCloner.Clone(candidate);
                RebuildCanvasTabStrip();
            }
            UpdateCanvasUi(savedPath);
            message = $"画布已保存：{currentCanvas.Title}";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (libraryExisted && libraryBeforeSave is not null) File.WriteAllBytes(libraryPath, libraryBeforeSave);
            else if (File.Exists(libraryPath)) File.Delete(libraryPath);
            currentCanvas = JsonSerializer.Deserialize<RecentCanvasState>(canvasBeforeSave, SnapshotOptions)!;
            CanvasSurfaceControl.State = currentCanvas.Canvas;
            RefreshResourceList();
            message = $"画布保存失败：{error.Message}";
            return false;
        }
    }

    private void AddResourceReference_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentCanvas is null) { StatusText.Text = "尚未创建画布"; return; }
        var source = currentCanvas.Canvas.Entities.FirstOrDefault(entity => !entity.IsProjectMissing && entity.Variants.Count > 0);
        var variant = source?.Variants.FirstOrDefault();
        if (source is null || variant is null) { StatusText.Text = "工作树暂无可插入资源"; return; }
        InsertResourceNode(source, variant, new Point(32 + (currentCanvas.Canvas.Nodes.Count % 3) * 246, 32 + (currentCanvas.Canvas.Nodes.Count / 3) * 150));
    }

    /// <summary>
    /// 刷新左栏那棵树。它有两个视角：故事画布 = 工作树（章节 / 出场 / 资源），
    /// 项目树 = 项目文件（画布库 / 项目库 / 资产 / 项目文件）。
    /// 两个视角共用同一个 TreeView，所以「素材库」不再单列——资源库本来就是这棵树里的一个分组。
    /// </summary>
    private void RefreshResourceList()
    {
        if (resourceView == ResourceViewMode.Project) BuildProjectTreeList();
        else BuildWorkTreeList();
    }

    /// <summary>
    /// 项目树：这个项目在磁盘上到底有什么。全部读真实文件——画布库里的每张画布、
    /// 项目库文件、资产目录、项目文件；读不到就如实说读不到，不摆一个看着很像的占位。
    /// </summary>
    private void BuildProjectTreeList()
    {
        var project = new WorkTreeViewNode { Header = $"项目 · {ProjectNameText.Text}", IsGroup = true };

        var canvases = CanvasLibrary.List();
        var canvasGroup = new WorkTreeViewNode { Header = $"画布（{canvases.Count} 张）", IsGroup = true };
        if (canvases.Count == 0) canvasGroup.Children.Add(new WorkTreeViewNode { Header = "（画布库为空）" });
        foreach (var canvas in canvases.OrderBy(item => item.Title))
        {
            var current = string.Equals(canvas.Path, currentCanvasPath, StringComparison.OrdinalIgnoreCase) ? " · 当前打开" : string.Empty;
            canvasGroup.Children.Add(new WorkTreeViewNode
            {
                Header = $"{canvas.Title} · 修订 {canvas.Revision}{current}",
                CanvasPath = canvas.Path
            });
        }
        project.Children.Add(canvasGroup);

        var library = new WorkTreeViewNode { Header = "项目库（跨画布共享的设定）", IsGroup = true };
        library.Children.Add(new WorkTreeViewNode { Header = DescribeFile(ProjectLibrary.FilePath) });
        project.Children.Add(library);

        var assets = new WorkTreeViewNode { Header = "资产目录", IsGroup = true };
        foreach (var entry in DescribeDirectory(AssetStore.Directory, 20)) assets.Children.Add(entry);
        project.Children.Add(assets);

        var files = new WorkTreeViewNode { Header = "项目文件", IsGroup = true };
        files.Children.Add(new WorkTreeViewNode { Header = DescribeFile(AppPaths.CurrentProject.ProjectFilePath) });
        project.Children.Add(files);

        WorkTreeView.ItemsSource = new List<WorkTreeViewNode> { project };
    }

    /// <summary>一个文件在树里怎么显示：文件名 + 大小 / 修改时间；不存在就直说。</summary>
    private static string DescribeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return $"{Path.GetFileName(path)}（不存在）";
            var info = new FileInfo(path);
            return $"{info.Name} · {info.Length / 1024.0:0.#} KB · {info.LastWriteTime:MM-dd HH:mm}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{Path.GetFileName(path)}（读不到：{error.Message}）";
        }
    }

    /// <summary>目录内容（最多列 limit 个）：没有目录、读不出来都如实写在树里。</summary>
    private static IEnumerable<WorkTreeViewNode> DescribeDirectory(string directory, int limit)
    {
        var nodes = new List<WorkTreeViewNode>();
        try
        {
            if (!Directory.Exists(directory))
            {
                nodes.Add(new WorkTreeViewNode { Header = "（目录还不存在）" });
                return nodes;
            }

            var files = Directory.EnumerateFiles(directory).OrderBy(path => path).ToList();
            if (files.Count == 0) nodes.Add(new WorkTreeViewNode { Header = "（空目录）" });
            foreach (var file in files.Take(limit))
                nodes.Add(new WorkTreeViewNode { Header = $"{Path.GetFileName(file)} · {new FileInfo(file).Length / 1024.0:0.#} KB" });
            if (files.Count > limit) nodes.Add(new WorkTreeViewNode { Header = $"…另有 {files.Count - limit} 个文件" });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            nodes.Add(new WorkTreeViewNode { Header = $"（读不到：{error.Message}）" });
        }
        return nodes;
    }

    /// <summary>
    /// 故事画布视角：**章节 → 分镜 → 场景 / 人物 → 道具（子引用）**，
    /// 另外两行是「本章出场（按人去重）」与「未绑定节点」，最后是资源库。
    ///
    /// 层次本身由共享的 <see cref="StoryTreePlanner"/> 算（可测），这里只负责翻成界面行：
    /// 标记形状、种类颜色、行尾标签、展开记忆。
    /// </summary>
    private void BuildWorkTreeList()
    {
        if (currentCanvas is null)
        {
            WorkTreeView.ItemsSource = Array.Empty<WorkTreeViewNode>();
            return;
        }

        var tree = StoryTreePlanner.Build(currentCanvas.Canvas, currentCanvas.Title);
        WorkTreeView.ItemsSource = tree.Roots.Select(root => ToViewRow(root)).ToList();
    }

    /// <summary>把一行结构数据翻成界面行（含标记、颜色、标签与展开状态）。</summary>
    private WorkTreeViewNode ToViewRow(StoryRow row)
    {
        var accent = row.Kind == StoryRowKind.Group
            ? Brush("#5C6A7C")
            : NodeKindBrushes.BrushOf(row.Category);

        var marker = row.Kind switch
        {
            StoryRowKind.Group => string.Empty,
            StoryRowKind.Chapter => "■",
            StoryRowKind.Storyboard => "●",
            StoryRowKind.Node => "○",
            StoryRowKind.Rollup => "◆",
            _ => "◇"
        };
        // 子引用要跟「分镜直接引用的」区分开：它来自角色变体自带的道具 / 服装，不是分镜引的。
        var tag = row.IsSubReference ? "子引用"
            : row.Kind == StoryRowKind.Rollup && row.Children.Count > 0 ? "去重汇总"
            : string.Empty;

        var view = new WorkTreeViewNode
        {
            Key = row.Key,
            Header = row.Detail.Length == 0 ? row.Title : $"{row.Title} · {row.Detail}",
            Marker = marker,
            MarkerBrush = accent,
            TagText = tag,
            TagBrush = Brush("#6F7F94"),
            HeaderBrush = row.Kind == StoryRowKind.Group ? Brush("#93A1B3") : Brush("#C6D2E0"),
            Item = row.WorkTreeItemId is { } itemId ? currentCanvas?.Canvas.WorkTree.FirstOrDefault(item => item.Id == itemId) : null,
            NodeId = row.NodeId,
            NodeIds = row.NodeIds,
            EntityId = row.EntityId,
            VariantId = row.VariantId,
            IsGroup = row.Kind == StoryRowKind.Group,
            OnExpandedChanged = RememberStoryExpansion
        };
        foreach (var child in row.Children) view.Children.Add(ToViewRow(child));

        // 默认展开：项目与章节（不然一眼只能看到章节名）；其余保持折叠，但用户展开过的要记住。
        view.IsExpanded = expandedStoryRows.Contains(row.Key)
            || row.Key == StoryTreePlanner.ProjectKey
            || row.Kind == StoryRowKind.Chapter;
        return view;
    }

    /// <summary>左栏展开记忆：画布一变左栏就整体重建，不记的话每次重建都会塌回默认。</summary>
    private readonly HashSet<string> expandedStoryRows = new(StringComparer.Ordinal);

    /// <summary>程序化选中左栏某一行时置上：避免「画布→左栏」又立刻触发「左栏→画布」来回弹。</summary>
    private bool suppressTreeLocate;

    private void RememberStoryExpansion(string key, bool expanded)
    {
        if (expanded) expandedStoryRows.Add(key);
        else expandedStoryRows.Remove(key);
    }

    private void WorkTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WorkTreeView.SelectedItem is not WorkTreeViewNode node) return;
        if (node.CanvasPath is { } canvasPath)
        {
            StatusText.Text = $"画布文件：{Path.GetFileName(canvasPath)}（双击打开）";
            return;
        }
        if (node.Item is { } item)
            StatusText.Text = $"已选中{WorkTreeItem.KindName(item.Kind)}：{item.Name}";

        // 点一行就把画布带过去：左栏看起来像目录，点了不带你去任何地方是最伤的。
        LocateStoryRow(node);
    }

    /// <summary>
    /// 左栏某一行 → 画布定位。
    ///
    /// 一行可能对应**多个**节点（一章有十几个分镜；一个人被多个分镜引用），
    /// 这时「定位」应该是把这一片框出来并如实说几个，而不是随便挑一个说「定位到了」。
    /// </summary>
    private void LocateStoryRow(WorkTreeViewNode row)
    {
        if (suppressTreeLocate || currentCanvas is null) return;
        var state = currentCanvas.Canvas;

        List<WorkflowNode> Resolve(IEnumerable<Guid> ids) => ids
            .Select(id => state.Nodes.FirstOrDefault(node => node.Id == id))
            .Where(node => node is not null)
            .Cast<WorkflowNode>()
            .ToList();

        var targets = row.NodeId is { } nodeId
            ? Resolve(new[] { nodeId })
            : row.NodeIds.Count > 0
                ? Resolve(row.NodeIds)
                // 引用行：定位到**所有**引用它的节点（同一个设定会被多处引用）。
                : row.EntityId is { } entityId
                    ? StoryTreePlanner.NodesReferencing(state, entityId, row.VariantId).ToList()
                    : new List<WorkflowNode>();

        if (targets.Count == 0)
        {
            // 组行、资源库里没人引用的设定：没有画布落点是正常的，不当成失败报错。
            if (row.IsGroup || row.EntityId is not null && row.Item is null && row.NodeIds.Count == 0)
                StatusText.Text = $"「{row.Header}」在画布上没有对应节点（它还没有被任何分镜引用）。";
            return;
        }

        CanvasSurfaceControl.FocusNodes(targets);
        StatusText.Text = targets.Count == 1
            ? $"已定位到画布上的「{targets[0].Title}」"
            : $"「{row.Header}」在画布上对应 {targets.Count} 个节点，已把它们一起框出来";
    }

    /// <summary>
    /// 画布 → 左栏：选中一个节点就把左栏展开、选中并滚到它那一行。
    ///
    /// 原先这里是「点了没反应」的根源，两个原因叠在一起：
    ///
    /// 1. **拿重建之前的行对象去选中**。<c>BuildWorkTreeList</c> 每次都会按数据重新造一批全新的
    ///    <see cref="WorkTreeViewNode"/>，而这里原来是先从旧 ItemsSource 里找出 <c>path</c>、
    ///    再调 <c>BuildWorkTreeList()</c>、然后拿**旧**对象去 <c>SelectedItem</c> 与找容器——
    ///    那两个对象根本不在新树里，于是永远选不中、也永远滚不过去，只剩状态栏一句话。
    ///    现在改成按 <see cref="StoryRow.Key"/>（重建前后稳定）定位：先算出该展开哪些行，重建之后再找目标行。
    /// 2. **左栏不在工作树就直接 return**。用户在画布上点节点，要的就是「它在故事里哪儿」，
    ///    这时把左栏切回工作树是符合意图的；树里**确实没有**这一行时才如实说明（不再静默）。
    /// </summary>
    private async void RevealStoryRowAsync(Guid nodeId)
    {
        if (currentCanvas is null) return;

        // 用**刚算出来的**故事树找行，而不是界面上那一份：界面上那份在「项目文件」视图下根本不是故事树。
        var story = StoryTreePlanner.Build(currentCanvas.Canvas, currentCanvas.Title);
        var path = FindStoryPath(story.Roots, nodeId);
        if (path is null)
        {
            // 树里没有这一行是**正常**的（没有章节锚点的自由节点、筛选看不出来的东西），
            // 但要说一句：以前这里直接 return，用户看到的就是「点了没反应」。
            StatusText.Text = $"「{currentCanvas.Canvas.Nodes.FirstOrDefault(node => node.Id == nodeId)?.Title}」"
                + "不在左栏的故事树里（它还没有章节归属，看看「未绑定节点」那一组）。";
            return;
        }

        // 左栏若停在「项目文件」上，先切回工作树：这一步之后树里才会有这一行。
        if (resourceView != ResourceViewMode.WorkTree) SetResourceView(ResourceViewMode.WorkTree);

        // 先把祖先链展开（写进展开记忆，重建后也保持展开），再重建左栏让行出现。
        foreach (var row in path) expandedStoryRows.Add(row.Key);
        BuildWorkTreeList();

        // **重建之后**再按 Key 找那一行：新树里的对象才是能被选中、能找到容器的那个。
        if (WorkTreeView.ItemsSource is not IEnumerable<WorkTreeViewNode> refreshed) return;
        var target = FindViewRowByKey(refreshed, path[^1].Key);
        if (target is null) return;

        suppressTreeLocate = true;
        try
        {
            WorkTreeView.SelectedItem = target;

            // 展开后的容器要等布局跑完才存在，所以延后一拍再滚；滚动本身失败不算错。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (FindTreeContainer(target) is not { } container) continue;
                container.BringIntoView();
                return;
            }
            StatusText.Text = $"已在左栏选中「{target.Header}」（树较深时可能需要手动展开才能看到它）。";
        }
        finally
        {
            suppressTreeLocate = false;
        }
    }

    /// <summary>从**数据**故事树里找出「这个节点」那一行，返回包含目标在内的整条链（用来逐级展开）。</summary>
    private static List<StoryRow>? FindStoryPath(IEnumerable<StoryRow> roots, Guid nodeId)
    {
        foreach (var root in roots)
        {
            var found = Walk(root, new List<StoryRow>());
            if (found is not null) return found;
        }
        return null;

        List<StoryRow>? Walk(StoryRow row, List<StoryRow> path)
        {
            path.Add(row);
            if (row.NodeId == nodeId) return new List<StoryRow>(path);
            foreach (var child in row.Children)
            {
                var found = Walk(child, path);
                if (found is not null) return found;
            }
            path.RemoveAt(path.Count - 1);
            return null;
        }
    }

    /// <summary>
    /// 从界面行里按 <see cref="StoryRow.Key"/> 找那一行。
    /// 按 Key 而不是按对象：树每重建一次就是一批新对象，Key 是重建前后唯一稳定的身份。
    /// </summary>
    private static WorkTreeViewNode? FindViewRowByKey(IEnumerable<WorkTreeViewNode> roots, string key)
    {
        foreach (var root in roots)
        {
            var found = Walk(root);
            if (found is not null) return found;
        }
        return null;

        WorkTreeViewNode? Walk(WorkTreeViewNode row)
        {
            if (string.Equals(row.Key, key, StringComparison.Ordinal)) return row;
            foreach (var child in row.Children)
            {
                var found = Walk(child);
                if (found is not null) return found;
            }
            return null;
        }
    }

    /// <summary>找某一行的容器控件。Avalonia 的 TreeView 没有 ScrollIntoView，只能自己找容器再 BringIntoView。</summary>
    private TreeViewItem? FindTreeContainer(WorkTreeViewNode row) =>
        WorkTreeView.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, row));

    private async void WorkTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WorkTreeView.SelectedItem is not WorkTreeViewNode selected) return;

        // 项目树里的画布：双击直接打开（已经在标签里就切过去，不重复开）
        if (selected.CanvasPath is { } canvasPath)
        {
            OpenCanvasFromLibrary(canvasPath);
            return;
        }

        if (currentCanvas is null) return;

        // 章节行：编辑那条工作树条目（改章节名 / 内容）。引用行只定位，没有「编辑」这一步。
        if (selected.Item is { } item)
        {
            if (item.Kind == WorkTreeKind.Chapter) await EditWorkTreeItem(item);
            else if (item.Kind == WorkTreeKind.Resource) StatusText.Text = $"资源“{item.Name}”请通过关联出场编辑";
            return;
        }

        // 节点行（分镜 / 普通节点 / 成品）：直接编辑这个节点。
        if (selected.NodeId is { } nodeId && currentCanvas.Canvas.Nodes.FirstOrDefault(node => node.Id == nodeId) is { } node)
            CanvasSurface_OnNodeDoubleClicked(null, node);
    }

    /// <summary>
    /// 打开画布库里的某张画布：已经在标签里就切过去，没有就作为新标签打开。
    /// 画布标题必须与文件名一致（保存是按标题定文件的），所以这里以库里的标题为准。
    /// </summary>
    private void OpenCanvasFromLibrary(string path)
    {
        if (!HasOpenProject()) return;

        var existing = canvasTabs.FirstOrDefault(tab => tab.Path is not null && string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateCanvasTab(existing);
            StatusText.Text = $"已切换到 {existing.Title}";
            return;
        }

        if (!CanvasLibrary.TryLoad(path, out var state) || state is null)
        {
            StatusText.Text = $"画布读取失败：{Path.GetFileName(path)}（文件可能已损坏或被删）";
            return;
        }

        CaptureActiveTab();
        var tab = new CanvasTab(state) { Path = path };
        canvasTabs.Add(tab);
        AdoptCanvasTab(tab);
        PersistCanvasTabs(reportFailure: true);
        StatusText.Text = $"已打开画布：{tab.Title}";
    }

    private async Task EditWorkTreeItem(WorkTreeItem item)
    {
        if (currentCanvas is null || !canEdit) { StatusText.Text = "项目只读，无法编辑工作树"; return; }
        var title = new TextBox { Text = item.Name };
        var content = new TextBox { Text = item.Kind == WorkTreeKind.Appearance ? item.LocalState : item.Prompt, AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Height = 180 };
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
        save.Classes.Add("primary");
        var fields = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children = { AgentDialogUi.Note("名称"), title, AgentDialogUi.Note("内容"), content }
        };
        var saveRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { save }
        };
        var dialog = DialogShell.Create(
            $"编辑{WorkTreeItem.KindName(item.Kind)}",
            AgentDialogUi.Layout(fields, AgentDialogUi.Footer(saveRow)),
            520);
        save.Click += (_, _) => dialog.Close(true);
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            RecordSnapshot();
            UnifiedWorkTree.Edit(currentCanvas.Canvas, item, title.Text?.Trim() ?? string.Empty, content.Text ?? string.Empty);
            CanvasSurfaceControl.Refresh();
            RefreshResourceList();
            UpdateCanvasUi(currentCanvasPath ?? string.Empty);
            StatusText.Text = $"已编辑工作树节点：{item.Name}";
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
    }

    private void WorkTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        workTreeDragOrigin = e.GetPosition(WorkTreeView);
        workTreeDragStarted = e.GetCurrentPoint(WorkTreeView).Properties.IsLeftButtonPressed;
    }

    private async void WorkTree_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!workTreeDragStarted || WorkTreeView.SelectedItem is not WorkTreeViewNode { Item: { } item }) return;
        var point = e.GetPosition(WorkTreeView);
        if (Math.Abs(point.X - workTreeDragOrigin.X) + Math.Abs(point.Y - workTreeDragOrigin.Y) < 8) return;
        workTreeDragStarted = false;
        var resource = currentCanvas?.Canvas.FindEntity(item.ResourceId ?? item.SourceEntityId ?? Guid.Empty);
        var variant = item.SourceVariantId is { } sourceVariantId
            ? resource?.Variants.FirstOrDefault(candidate => candidate.Id == sourceVariantId)
            : resource?.Variants.FirstOrDefault();
        if (resource is null || variant is null || item.Kind is not (WorkTreeKind.Resource or WorkTreeKind.Appearance)) return;
        var data = new DataObject();
        data.Set(CanvasSurface.ResourceDragFormat, $"{resource.Id}|{variant.Id}|{item.Id}");
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        e.Handled = true;
    }

    private void CanvasSurface_OnResourceDropped(object? sender, CanvasResourceDrop drop)
    {
        if (currentCanvas is null) { StatusText.Text = "尚未创建画布"; return; }
        var entity = currentCanvas.Canvas.FindEntity(drop.EntityId);
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == drop.VariantId);
        if (entity is null || variant is null || entity.IsProjectMissing) { StatusText.Text = "拖入的资源已失效"; return; }
        InsertResourceNode(entity, variant, drop.WorkTreeItemId, drop.Position);
    }

    private void InsertResourceNode(WorkflowEntity entity, WorkflowEntityVariant variant, Point position)
        => InsertResourceNode(entity, variant, null, position);

    private void InsertResourceNode(WorkflowEntity entity, WorkflowEntityVariant variant, Guid? workTreeItemId, Point position)
    {
        if (!canEdit) { StatusText.Text = "项目或项目库不可写，无法插入资源"; return; }
        var state = currentCanvas!.Canvas;
        var item = workTreeItemId is { } id ? state.WorkTree.FirstOrDefault(candidate => candidate.Id == id) : null;
        var createdAppearance = false;
        if (item is null || item.Kind == WorkTreeKind.Resource)
        {
            var chapter = state.WorkTree.FirstOrDefault(candidate => candidate.Kind == WorkTreeKind.Chapter);
            if (chapter is null)
            {
                chapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "默认章节", Order = state.WorkTree.Count };
                state.WorkTree.Add(chapter);
            }
            item = UnifiedWorkTree.AddAppearance(state, entity.Id, chapter.Id, variant.Id);
            createdAppearance = true;
        }
        if (item.Kind != WorkTreeKind.Appearance && item.Kind != WorkTreeKind.Chapter)
        {
            StatusText.Text = "该工作树节点不能创建画布节点";
            return;
        }
        RecordSnapshot();
        UnifiedWorkTree.CreateNode(state, item, (float)Math.Max(0, position.X - 12), (float)Math.Max(0, position.Y - 12));
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        UpdateCanvasUi(currentCanvasPath ?? string.Empty);
        StatusText.Text = createdAppearance
            ? $"资源已自动归入“默认章节”并创建出场：{entity.Name} · {variant.Name}"
            : $"已插入工作树出场：{item.Name}";
    }

    private static string EntityKindLabel(EntityKind kind) => kind switch { EntityKind.Character => "角色", EntityKind.Scene => "场景", _ => "道具" };
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    /// <summary>节点分类的中文名（与画布卡片上的标签一致）。</summary>
    private static string CategoryNameOf(NodeCategory category) => category switch
    {
        NodeCategory.Chapter => "章节",
        NodeCategory.Character => "角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Prop => "道具",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Product => "成品",
        NodeCategory.StoryPlan => "故事企划",
        NodeCategory.StoryOutline => "故事大纲",
        _ => "通用"
    };

    /// <summary>分类对应「新建一个什么」：建出来就看得懂，不必先改名字。</summary>
    private static string DefaultNodeTitle(NodeCategory category) => category switch
    {
        NodeCategory.Chapter => "新章节",
        NodeCategory.Character => "新角色",
        NodeCategory.Scene => "新场景",
        NodeCategory.Prop => "新道具",
        NodeCategory.Storyboard => "新分镜",
        NodeCategory.Product => "新成品",
        NodeCategory.StoryPlan => "新故事企划",
        NodeCategory.StoryOutline => "新故事大纲",
        _ => "新节点"
    };

    /// <summary>
    /// 「＋ 节点」先选类型再建。以前只有一个「通用」，建完还要自己改回想要的类型，等于每次多点一步。
    /// </summary>
    private void AddNodeMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var category in new[]
                 {
                     NodeCategory.General, NodeCategory.Chapter, NodeCategory.Character, NodeCategory.Scene,
                     NodeCategory.Prop, NodeCategory.Storyboard, NodeCategory.StoryPlan, NodeCategory.StoryOutline,
                     NodeCategory.Product
                 })
        {
            var chosen = category;
            var item = new MenuItem { Header = $"{CategoryNameOf(chosen)} · {DefaultNodeTitle(chosen)}" };
            item.Click += (_, _) => AddNode(chosen);
            menu.Items.Add(item);
        }
        menu.ShowAt(AddNodeButton);
    }

    private void AddNode(NodeCategory category)
    {
        try
        {
            if (!HasOpenProject()) return;
            if (currentCanvas is null || string.IsNullOrWhiteSpace(currentCanvasPath))
            {
                StatusText.Text = "请先新建画布";
                return;
            }

            var visibleWidth = Math.Max(0, CanvasSurfaceControl.Bounds.Width - 260);
            var visibleHeight = Math.Max(0, CanvasSurfaceControl.Bounds.Height - 150);
            RecordSnapshot();
            var index = currentCanvas.Canvas.Nodes.Count;
            var node = new WorkflowNode
            {
                Title = $"{DefaultNodeTitle(category)} {index + 1}",
                Category = category,
                X = 32 + (index % 3) * 246,
                Y = 32 + (index / 3) * 136,
                ManualPosition = true
            };
            node.X = Math.Min(node.X, (float)visibleWidth);
            node.Y = Math.Min(node.Y, (float)visibleHeight);
            currentCanvas.Canvas.Nodes.Add(node);
            CanvasSurfaceControl.Refresh();
            RefreshOpenCenterView();
            UpdateCanvasUi(currentCanvasPath);
            StatusText.Text = $"已添加{CategoryNameOf(category)}节点：{node.Title}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"添加节点失败：{error.Message}";
        }
    }

    /// <summary>章节节点之间的纵向间距（比节点高度多一点，留出连线的余地）。</summary>
    private const double ChapterRowSpacing = 260;

    /// <summary>同一章里多个出场节点的纵向间距。</summary>
    private const double AppearanceRowSpacing = 150;

    /// <summary>
    /// 「生成章节工作树」以前是**演示按钮**：不读源文本，直接铺一串写死的节点（林默 / 阿岚 / 24 镜…），
    /// 用户会以为项目里已经有内容。现在拆成两条**真路**，让人自己选：
    /// ① 本地按文本拆（不联网，只做分章与出场匹配，编不出分镜）；② 交给 Agent（要模型，能做分镜那一层）。
    /// </summary>
    private void WorkTreeMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new MenuFlyout();

        var local = new MenuItem { Header = "按文本拆分章节（本地 · 不联网）" };
        ToolTip.SetTip(local, "只做两件事：按文本里的章节标题（没有标题就按长度）分章、按项目设定名匹配每章的出场资源。分镜需要模型，不走这条路。");
        local.Click += (_, _) => BuildChapterWorkTreeFromText();
        menu.Items.Add(local);

        var agent = new MenuItem { Header = "让 Agent 拆分（含分镜 · 需要已接入模型）" };
        ToolTip.SetTip(agent, "把选中节点的内容与「拆解章节」技能拼成指令填进 Agent 面板（不自动发送），由模型产出章节 / 出场 / 分镜节点。");
        agent.Click += (_, _) => HandOffChapterDecomposition();
        menu.Items.Add(agent);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "本地拆分不会编内容；匹配不到出场的章节就是空出场。",
            IsEnabled = false
        });
        menu.ShowAt(button);
    }

    /// <summary>
    /// 本地按文本拆分：**只用源文本里真实存在的东西**——
    /// 章节来自文本自身结构，出场来自项目设定库里真的出现过名字的资源。
    /// </summary>
    private void BuildChapterWorkTreeFromText()
    {
        try
        {
            if (!HasOpenProject()) return;
            if (currentCanvas is null || string.IsNullOrWhiteSpace(currentCanvasPath))
            {
                StatusText.Text = "请先新建画布";
                return;
            }
            if (!canEdit)
            {
                StatusText.Text = "项目只读，无法写入工作树";
                return;
            }

            var canvas = currentCanvas.Canvas;
            var source = ChapterSplitSource(canvas);
            if (source is null)
            {
                StatusText.Text = "先在画布上写一个总纲 / 正文节点（或先选中它），再拆分";
                return;
            }

            var split = ChapterSplitPlanner.Split(source.Content);
            if (split.IsEmpty)
            {
                StatusText.Text = $"「{source.Title}」{split.Note}";
                return;
            }

            RecordSnapshot();
            var baseX = Math.Max(0, source.X);
            var baseY = Math.Max(0, source.Y);
            var chapters = 0;
            var appearances = 0;
            var row = 0;

            foreach (var draft in split.Chapters)
            {
                var item = new WorkTreeItem
                {
                    Kind = WorkTreeKind.Chapter,
                    Name = draft.Title,
                    Chapter = draft.Title,
                    Order = draft.Order,
                    Prompt = draft.Content
                };
                canvas.WorkTree.Add(item);

                var chapterY = baseY + row * ChapterRowSpacing;
                var chapterNode = UnifiedWorkTree.CreateNode(canvas, item, (float)(baseX + 420), (float)chapterY);
                canvas.Edges.Add(new WorkflowEdge { SourceNodeId = source.Id, TargetNodeId = chapterNode.Id });
                chapters++;

                // 出场只按名字匹配：正文里没提到的设定不会被"补"进来，也不会新造一个角色。
                var slot = 0;
                foreach (var entity in ChapterSplitPlanner.MatchEntities(canvas, draft.Content))
                {
                    var variant = entity.Variants.FirstOrDefault() ?? entity.CreateVariant("默认");
                    var appearance = UnifiedWorkTree.AddAppearance(canvas, entity.Id, item.Id, variant.Id);
                    var node = UnifiedWorkTree.CreateNode(
                        canvas, appearance,
                        (float)(baseX + 840),
                        (float)(chapterY + slot * AppearanceRowSpacing));
                    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = chapterNode.Id, TargetNodeId = node.Id });
                    appearances++;
                    slot++;
                }

                row++;
            }

            CanvasSurfaceControl.Refresh();
            CanvasSurfaceControl.FitToContent();
            SelectStage(ProductionStage.All);           // 拆完先给全景，免得当前阶段把它们筛没了
            RefreshResourceList();
            RefreshOpenCenterView();
            if (currentCanvasPath is not null) UpdateCanvasUi(currentCanvasPath);
            MarkCanvasDirty();
            StatusText.Text = $"已按「{split.Note}」建立 {chapters} 章 · 匹配到 {appearances} 项出场；"
                + "分镜那一层需要模型：选中章节节点后用「让 Agent 拆分」。记得点「保存修订」";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"建立工作树失败：{error.Message}";
        }
    }

    /// <summary>本地拆分的源节点：选中优先，其次是带内容的企划 / 大纲，最后是任意带内容的节点。</summary>
    private WorkflowNode? ChapterSplitSource(WorkflowCanvasState canvas)
    {
        if (CanvasSurfaceControl.SelectedNode is { } selected && !string.IsNullOrWhiteSpace(selected.Content))
            return selected;
        return canvas.Nodes.FirstOrDefault(node =>
                   node.Category is NodeCategory.StoryPlan or NodeCategory.StoryOutline && !string.IsNullOrWhiteSpace(node.Content))
               ?? canvas.Nodes.FirstOrDefault(node => !string.IsNullOrWhiteSpace(node.Content));
    }

    /// <summary>
    /// 把「拆解章节」交给 Agent：指令直接用右键菜单那条建议拼好的提示词（技能、上游设定都在里面），
    /// 避免了这里再抄一份模板。**只填不发**——发送是一次真实的模型调用，要让人先看一眼。
    /// </summary>
    private void HandOffChapterDecomposition()
    {
        if (!HasOpenProject()) return;
        if (currentCanvas is null) return;
        var canvas = currentCanvas.Canvas;
        var source = ChapterSplitSource(canvas);
        if (source is null)
        {
            StatusText.Text = "先选中一个带内容的总纲 / 章节节点，再把拆分交给 Agent";
            return;
        }

        var plan = NodeAssistPlanner.BuildPlan(canvas, source);
        var suggestion = plan.Suggestions.FirstOrDefault(item => item.SkillId == "chapter-decomposition")
            ?? plan.Suggestions.FirstOrDefault(item => item.Kind == NodeAssistKind.Agent);
        if (suggestion is null)
        {
            StatusText.Text = $"「{source.Title}」没有可用的拆分建议：{plan.ContextSummary}";
            return;
        }

        ShowAgentMode();
        AgentWorkbenchPanel.Prefill(suggestion.Prompt);
        StatusText.Text = $"已把「{suggestion.Title}」填进 Agent 面板（源节点：{source.Title}）；确认后发送，产出的节点走 Agent 的改动确认";
    }

    /// <summary>
    /// 「预览引用节点」开关：勾上之后，有引用的节点卡下方按引用顺序排出一串小小的预览框
    /// （图片给缩略图、视频给 ▶ 占位），点预览放大图片 / 播放视频。
    ///
    /// 它只是**画法**开关：关掉不删任何东西，节点上的「引用 · …」标签照旧在，
    /// 引用浮层与双击进临时画布这些入口也都不受影响。
    /// </summary>
    private void ReferencePreviewToggle_OnChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = ReferencePreviewToggle.IsChecked == true;
        CanvasSurfaceControl.ShowReferencePreviews = enabled;
        StatusText.Text = enabled
            ? "已打开引用预览：有引用的节点下方会排出它引用的媒体，点一下放大 / 播放"
            : "已关闭引用预览（关掉的只是画法，引用本身没动）";
    }

    /// <summary>删除画布上当前选中的节点及其连线。</summary>
    /// <summary>工具栏「删除」：删掉当前选中的东西——连线优先，其次节点（与 Delete 键同一条路径）。</summary>
    private void DeleteNode_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (currentCanvas is null) { StatusText.Text = "请先新建画布"; return; }
            var message = CanvasSurfaceControl.DeleteSelection();
            if (message is null)
            {
                StatusText.Text = "先选中一个节点或一条连线再删（点节点选节点、点连线选连线）";
                return;
            }
            if (currentCanvasPath is not null) UpdateCanvasUi(currentCanvasPath);
            RefreshOpenCenterView();
            StatusText.Text = message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"删除失败：{error.Message}";
        }
    }

    /// <summary>
    /// 按章节泳道整理整张画布的位置。
    ///
    /// 用的是那个**无界面、两端共享**的泳道引擎（<see cref="CanvasLayoutSession"/> →
    /// <see cref="CanvasSwimlaneLayout"/>）：网页端的服务端跑的是同一份代码，所以两端排出来的样子一致。
    /// 在这之前它只有 Agent 与测试在用，桌面上一直没有手动入口——这条补的就是那个缺口。
    ///
    /// 三个与网页端对齐的规矩：阻断冲突整批拒绝（确认覆盖也不写——覆盖只解决「保护坐标」，不解决冲突）；
    /// 手动摆放的节点默认不动、要一起动得显式点头；只改内存里的坐标，「保存修订」时才落盘
    /// （与拖动节点、Agent 落位是同一条路子）。
    /// </summary>
    private async void ArrangeLayout_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (currentCanvas is null) { StatusText.Text = "请先新建画布"; return; }
            if (referenceCanvas is not null) { StatusText.Text = "临时引用画布不参与整理（它不落盘）"; return; }

            var session = new CanvasLayoutSession(currentCanvas.Canvas);
            var plan = session.PreviewAll();
            if (plan.HasBlockingConflicts)
            {
                StatusText.Text = "布局被阻断，画布未改动。" + session.LastSummary;
                return;
            }

            var confirmManual = false;
            if (plan.RequiresConfirmation)
            {
                confirmManual = await CanvasLayoutDialog.ConfirmOverrideManualAsync(
                    this, plan.ProtectedNodeIds.Count, session.LastSummary);
                if (!confirmManual)
                {
                    StatusText.Text = "已取消整理：手动摆放的节点留在原位，画布未改动。";
                    return;
                }
            }

            // 引擎自己给的理由（「没有产生改动」「计划已过期」）直接显示，不在这里另编一句。
            if (!session.Apply(confirmManual)) { StatusText.Text = session.LastSummary; return; }

            // 会话是写在**副本**上的，所以要把它换回当前画布——否则接着保存下去的还是旧坐标。
            currentCanvas = currentCanvas with { Canvas = session.State };
            CanvasSurfaceControl.Refresh();
            if (currentCanvasPath is not null) UpdateCanvasUi(currentCanvasPath);
            RefreshOpenCenterView();
            StatusText.Text = session.LastSummary;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"整理失败：{error.Message}";
        }
    }

    private void CanvasSurface_OnReferenceSelected(object? sender, CanvasReferenceActivation activation)
    {
        StatusText.Text = activation.Content?.Label ?? "引用失效";
    }

    private void CanvasSurface_OnReferenceTagDoubleClicked(object? sender, CanvasReferenceActivation activation) =>
        EnterReferenceCanvas(activation.Node);

    private void CanvasSurface_OnReferenceDoubleClicked(object? sender, WorkflowNode node)
    {
        if (node.References.Count == 0) return;
        EnterReferenceCanvas(node);
    }

    /// <summary>
    /// 窗口级的 Delete 兜底：焦点不在画布上时也能删掉选中的节点 / 连线。
    /// 只有焦点不在文本输入里、且画布确实有选中项时才动手，否则会跟「输入框里删字符」打架。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back)) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (!CanvasSurfaceControl.HasSelection) return;

        var message = CanvasSurfaceControl.DeleteSelection();
        if (message is null) return;
        StatusText.Text = message;
        e.Handled = true;
    }

    /// <summary>选中一条连线：检查器切到「未选择节点」，状态栏说清楚怎么删。</summary>
    private void CanvasSurface_OnEdgeSelectionChanged(object? sender, WorkflowEdge? edge)
    {
        if (edge is null) return;
        ShowInspectorEmptyState(updateStatus: false);
        StatusText.Text = $"已选中连线：{CanvasSurfaceControl.DescribeSelectedEdge()}（按 Delete 或点工具栏「删除」移除）";
    }

    // ==================== 节点右键：按内容算出来的「Agent 协助」 ====================

    /// <summary>
    /// 右键节点 → 菜单。菜单里的条目**不是写死的**：由 <see cref="NodeAssistPlanner"/> 沿入边收集上游设定、
    /// 再按节点类型给出「提示词 / 交给 Agent / 直接出图」三类动作（角色给角色图，分镜给镜头画面，章节给拆解）。
    /// </summary>
    private void CanvasSurface_OnNodeContextRequested(object? sender, CanvasNodeContextRequest request)
    {
        if (currentCanvas is null) return;

        var plan = NodeAssistPlanner.BuildPlan(currentCanvas.Canvas, request.Node);
        var menu = new MenuFlyout();

        // 第一行是「看到了什么」，让用户能判断待会儿的提示词是靠什么算出来的。
        var summary = new MenuItem { Header = plan.ContextSummary, IsEnabled = false };
        menu.Items.Add(summary);
        if (plan.HasUpstream)
        {
            var inspect = new MenuItem { Header = "查看上游设定…" };
            inspect.Click += (_, _) => ShowUpstreamDialog(request.Node, plan);
            menu.Items.Add(inspect);
        }
        menu.Items.Add(new Separator());

        foreach (var suggestion in plan.Suggestions)
        {
            var item = new MenuItem { Header = suggestion.Title };
            if (!suggestion.CanRun)
            {
                item.IsEnabled = false;
                ToolTip.SetTip(item, suggestion.Blocked);
            }
            var chosen = suggestion;
            item.Click += async (_, _) => await RunNodeAssistAsync(request.Node, chosen);
            menu.Items.Add(item);
        }

        // 站点池子那条路：先问「用哪个接口」，再走与「Agent 协助 · 出图」同一套提示词与参考图逻辑。
        // 它和上面那几条建议并列，而不是被藏进设置里——「这一镜用哪个池子」是每次出图都要回答的问题。
        menu.Items.Add(new Separator());
        var runSkill = new MenuItem { Header = "运行技能：选一个池子…" };
        var sites = SiteCatalog.Load().Sites;
        if (sites.All(site => site.UsablePools.Count == 0))
        {
            runSkill.IsEnabled = false;
            ToolTip.SetTip(runSkill, "还没有可用的池子：到「设置 → 生图与生视频」用顶端的「智能导入」导入一个接口说明网页");
        }
        var targetNode = request.Node;
        runSkill.Click += async (_, _) => await RunSitePoolAsync(targetNode, sites);
        menu.Items.Add(runSkill);

        // 自检：把这条依赖链上缺什么、缺多少、要花多少钱一次列清楚（只读，不生成）。
        // 它和上面那条并排，因为「这一镜/这一章到底能不能开工」是每次动手前都要问一遍的问题。
        var audit = new MenuItem { Header = "自检：这条链缺什么…" };
        var auditNode = request.Node;
        audit.Click += async (_, _) => await RunGenerationAuditAsync(auditNode);
        menu.Items.Add(audit);

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = UiText.Text("nodeMenu.edit") };
        // 这里**不再**转给双击那条路：双击在临时画布里是「再钻一层」，
        // 而菜单里的「编辑节点…」意思一直很明确——就是要开那个窗口。
        edit.Click += async (_, _) =>
        {
            if (!canEdit || request.Node.IsLocked) { StatusText.Text = "项目只读或节点已锁定，不能编辑"; return; }
            if (BlockedByOtherEditor(request.Node.Id)) return;
            await ShowNodeEditorAsync(request.Node);
        };
        menu.Items.Add(edit);
        var remove = new MenuItem { Header = UiText.Text("nodeMenu.delete") };
        remove.Click += (_, _) =>
        {
            var message = CanvasSurfaceControl.DeleteSelection();
            if (message is null) return;
            if (currentCanvasPath is not null) UpdateCanvasUi(currentCanvasPath);
            RefreshOpenCenterView();
            StatusText.Text = message;
        };
        menu.Items.Add(remove);

        // showAtPointer: true —— 菜单贴在鼠标位置弹出，右键菜单的常规手感。
        menu.ShowAt(CanvasSurfaceControl, true);
        StatusText.Text = plan.HasUpstream
            ? $"Agent 协助：{plan.ContextSummary}"
            : "Agent 协助：这个节点没有上游连线，建议会以它自己的内容为准";
    }

    /// <summary>
    /// 「运行技能」：先让用户从已登记站点的池子里挑一个，再走与「Agent 协助 · 出图」**同一套**
    /// 提示词与参考图逻辑——只是模型、接口地址、画幅换成选定池子的。
    ///
    /// 提示词仍然要过一遍那个可改的窗口：出图前让人看到会发出去的话，是这条链路一直守着的规矩。
    /// </summary>
    private async Task RunSitePoolAsync(WorkflowNode node, IReadOnlyList<SiteProfile> sites)
    {
        if (currentCanvas is null) return;
        if (canEdit is false || node.IsLocked)
        {
            StatusText.Text = "项目只读或节点已锁定，不能出图";
            return;
        }
        if (BlockedByOtherEditor(node.Id)) return;

        // 预选上一次用过的那一个：这条路每次都要挑一次池子，记住能省掉一连串点击。
        var choice = await SitePoolPicker.ShowAsync(this, sites, node.Title, RememberedPool());
        if (choice is null) return;
        RememberPool(choice);

        if (choice.Pool.IsVideo)
        {
            // 出视频执行方还没接入：如实拒绝，不偷偷起一个花钱的异步任务（这也是本轮明确的要求）。
            StatusText.Text = $"「{choice.Pool.Label}」是出视频池子，但出视频执行方尚未接入：这次不生成。"
                + "它会留在站点下，等接入执行方后可以直接用。";
            return;
        }

        var plan = NodeAssistPlanner.BuildPlan(currentCanvas.Canvas, node);
        var suggestion = plan.Suggestions.FirstOrDefault(item => item.Kind == NodeAssistKind.Image)
            ?? plan.Suggestions.FirstOrDefault();
        if (suggestion is null)
        {
            StatusText.Text = "这个节点没有可用的出图建议：它既没有内容，也没有可参考的上游设定。";
            return;
        }

        StatusText.Text = $"准备用「{choice.Site.Label} · {choice.Pool.Label}」（{choice.Pool.Describe()}）出图";
        await ShowPromptDialogAsync(node, suggestion, allowGenerate: true, choice);
    }

    /// <summary>
    /// 生成链自检：**先看清楚缺什么、缺多少、要花多少钱，再决定动不动手**。
    ///
    /// 这一步刻意只读——不生成、不落盘、不改画布。理由很实际：一次「按节点把缺的补上」
    /// 会花掉几十次调用、等上十几分钟，还可能覆盖已经挑好的图。让用户看过清单与报价之后再动手，
    /// 是这条链一直守着的规矩（与出图前先过一遍可改的提示词窗口同一个道理）。
    ///
    /// 报告里的「定位」是唯一会改界面的动作：把那个节点选中并挪进视野，接下来怎么出图由用户决定。
    /// </summary>
    private async Task RunGenerationAuditAsync(WorkflowNode node)
    {
        if (currentCanvas is null) return;

        var report = GenerationAudit.Build(currentCanvas.Canvas, node);
        // 报价用「上次用过的那个池子」的单价。池子换了、或者没登记价格，就如实说算不出来——
        // 报一个 0 比不报价更坏，用户会当成「免费」。
        var pooled = RememberedPool();
        var unitPrice = pooled is { Pool.UnitPrice: > 0 } ? pooled.Pool.UnitPrice : (double?)null;

        StatusText.Text = report.Describe();
        var locate = await GenerationAuditDialog.ShowAsync(this, report, unitPrice);

        if (locate is not { } nodeId) return;
        if (currentCanvas.Canvas.Nodes.FirstOrDefault(item => item.Id == nodeId) is not { } target)
        {
            StatusText.Text = "要定位的节点已经不在这张画布上了（自检之后画布被改过？）";
            return;
        }

        CanvasSurfaceControl.FocusNode(target);
        StatusText.Text = $"已定位「{target.Title}」：在这一步右键就能出图（也可以回到这份自检里继续看别的）。";
    }

    /// <summary>跑一条协助建议：三类动作分别走提示词窗口 / Agent 面板 / 图像链路。</summary>
    private async Task RunNodeAssistAsync(WorkflowNode node, NodeAssistSuggestion suggestion)
    {
        if (currentCanvas is null) return;
        if (!suggestion.CanRun)
        {
            StatusText.Text = suggestion.Blocked;
            return;
        }

        switch (suggestion.Kind)
        {
            case NodeAssistKind.Agent:
                // 只填不发：一次发送就是一次模型调用，先让人看一眼再按发送。
                ShowAgentMode();
                AgentWorkbenchPanel.Prefill(suggestion.Prompt);
                StatusText.Text = $"已把「{suggestion.Title}」的指令填进 Agent 面板，确认后发送";
                return;

            case NodeAssistKind.Prompt:
                await ShowPromptDialogAsync(node, suggestion, allowGenerate: false);
                return;

            case NodeAssistKind.Video:
                // 必须**显式**挡住：default 那条路是「出图」。真让它落进 default，
                // 将来一旦把这条建议的 CanRun 放开，用户点「出视频」会拿到一张静图——
                // 那正是之前「图生图被偷偷改成文生图」同一类的事，不能再犯。
                StatusText.Text = "出视频执行方还没接入：技能与站点的视频池子已经能导入，"
                    + "但真正发请求的那一段还没写。这次不会生成任何东西。";
                return;

            default:
                await ShowPromptDialogAsync(node, suggestion, allowGenerate: true);
                return;
        }
    }

    /// <summary>
    /// 提示词窗口：**提示词可改**，再决定复制、交给 Agent，或直接出图。
    /// 出图前必须让用户看到会发出去的提示词——不然出了不想要的图，只能靠猜是哪句话的问题。
    /// </summary>
    private async Task ShowPromptDialogAsync(WorkflowNode node, NodeAssistSuggestion suggestion, bool allowGenerate, SitePoolChoice? poolChoice = null)
    {
        var prompt = new TextBox
        {
            Text = suggestion.Prompt,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 220,
            FontSize = 12
        };
        var negative = new TextBox
        {
            Text = suggestion.NegativePrompt,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 56,
            FontSize = 12
        };

        var copy = AgentDialogUi.Secondary("复制提示词");
        var hand = AgentDialogUi.Secondary("发给 Agent 面板");
        var generate = AgentDialogUi.Primary("出图并挂到节点");
        var cancel = AgentDialogUi.Secondary("取消");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, copy, hand }
        };
        if (allowGenerate) buttons.Children.Add(generate);

        // 出图方式：**默认按剧情判断**，但把理由摊开，并且让人能推翻它。
        // 判定是关键词启发式（中文分镜写法千变万化），做成看不见的自动行为比做错更糟。
        var decision = NodeImageModePlanner.Decide(currentCanvas!.Canvas, node);
        var candidates = NodeImageModePlanner.BaseCandidates(currentCanvas.Canvas, node);
        // 用两张平行的表，而不是硬编码下标：「图生图」那一项有没有，取决于这个节点有没有底图可选，
        // 硬编码下标迟早会把「用出图技能」读成「图生图」。
        var modeItems = new List<string> { "自动（按剧情判断）", "文生图" };
        var modeApproaches = new List<NodeImageApproach?> { null, NodeImageApproach.TextToImage };
        if (candidates.Count > 0)
        {
            modeItems.Add("图生图（用现有图当底图）");
            modeApproaches.Add(NodeImageApproach.ImageToImage);
        }
        var modeBox = new ComboBox { ItemsSource = modeItems, SelectedIndex = 0, Width = 260, Classes = { "panelCombo" } };

        // 从右键「运行技能」进来时池子已经选好了，这里只是让它可见、可改。
        // 否则用**上一次用过的那个**：多数人的出图是同一家、同一个模型反复用，
        // 每次重挑一遍纯属浪费。它只是「预选」——价格与档位都摆在上面，改动也随时可以。
        var chosenPool = poolChoice ?? RememberedPool();
        var poolButton = AgentDialogUi.Secondary("选池子…");
        var poolNote = AgentDialogUi.Note(string.Empty);
        poolNote.TextWrapping = TextWrapping.Wrap;
        // 「出图技能」是与「出图方式」**平行**的一项，不是它的一个取值。
        // 两者本来就正交：出图方式决定发不发参考图，出图技能决定用哪个模型 / 哪档 / 打哪个接口。
        // 上一版把池子做成「出图方式」的第四项，等于把两件独立的事塞进同一个下拉里，
        // 用户就得分清「选了第四项时前三个还算不算数」——那是设计造成的疑问，不是用户的疑问。
        var clearPoolButton = AgentDialogUi.Secondary("不用技能");
        clearPoolButton.FontSize = 10;
        clearPoolButton.IsVisible = false;
        var poolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { poolButton, clearPoolButton, poolNote }
        };

        // 数量：上限 6。并行发 6 个请求已经有些站会限流，再往上加只会多出一堆限流失败，
        // 而每一次失败都可能是花过钱的。
        var countBox = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 6,
            Value = 1,
            Increment = 1,
            Width = 96,
            FontSize = 12
        };
        var costNote = AgentDialogUi.Note(string.Empty);
        costNote.TextWrapping = TextWrapping.Wrap;
        var countRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { countBox, costNote }
        };
        // 数量一变，下面那句「同时出 N 张」与预估花费必须立刻跟着变。原先只有换池子才会重算，
        // 于是把数量从 1 调到 6 时，旁边一直写着「出 1 张：出好直接收进节点」——
        // 而这一行正是「点下去之前看得见要花多少钱」的唯一地方，停在旧值等于把花钱的地方藏起来了。
        countBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == NumericUpDown.ValueProperty) SyncCost();
        };

        void SyncCost()
        {
            var many = (int)(countBox.Value ?? 1);
            if (many <= 1)
            {
                costNote.Text = "出 1 张：出好直接收进节点。";
                return;
            }
            var pool = chosenPool?.Pool;
            if (pool is null)
            {
                costNote.Text = $"同时出 {many} 张：出完在节点卡上点一张保存，其余的作废；都不满意可以「全部不要，重做」。";
                return;
            }
            // 花了钱的地方：N 倍的钱要在点下去之前看得见。
            costNote.Text = pool.UnitPrice > 0
                ? $"同时出 {many} 张：单价 {pool.UnitPrice:0.##} × {many} ≈ **{pool.UnitPrice * many:0.##} 积分**。"
                  + "某一张失败了只重试那一张，不会整批重来。"
                : $"同时出 {many} 张：这个池子的清单里没写单价，算不出预估花费——按上面的单价表自己估一下。";
        }

        void DescribePool()
        {
            clearPoolButton.IsVisible = chosenPool is not null;
            poolNote.Text = chosenPool is null
                ? "不用技能：按设置里的默认图像模型出图。"
                : $"{chosenPool.Site.Label} · {chosenPool.Pool.Label}"
                  + (chosenPool.Pool.Price.Length > 0 ? $"（{chosenPool.Pool.Price}）" : "（清单没写单价）");
            SyncCost();
        }

        var modeNote = AgentDialogUi.Note(string.Empty);
        void SyncModeNote()
        {
            if (modeApproaches[modeBox.SelectedIndex] == NodeImageApproach.ImageToImage)
            {
                // 手动图生图：用判定选的那张，没有就用候选里的第一张（本节点上一版镜头图）。
                var label = decision.UsesBaseImage ? decision.BaseImageLabel : candidates[0].Label;
                var strength = decision.UsesBaseImage ? decision.Denoise : NodeImageModePlanner.SceneDenoise;
                var others = candidates.Count > 1
                    ? $"（也可用：{string.Join(" / ", candidates.Skip(1).Select(item => item.Label))}）"
                    : string.Empty;
                modeNote.Text = $"图生图：底图 = {label}，重绘幅度 {strength:0.00}{others}";
                return;
            }
            modeNote.Text = modeApproaches[modeBox.SelectedIndex] == NodeImageApproach.TextToImage
                ? "文生图：不传参考图，构图完全按上面的提示词。"
                : "自动：" + decision.Reason;
        }
        SyncModeNote();
        modeBox.SelectionChanged += (_, _) => SyncModeNote();
        poolButton.Click += async (_, _) =>
        {
            var picked = await SitePoolPicker.ShowAsync(this, SiteCatalog.Load().Sites, node.Title);
            if (picked is null) return;
            if (picked.Pool.IsVideo)
            {
                poolNote.Text = $"「{picked.Pool.Label}」是出视频池子：出视频执行方还没接入，选它出不了图。";
                return;
            }
            chosenPool = picked;
            DescribePool();
        };
        clearPoolButton.Click += (_, _) =>
        {
            chosenPool = null;
            DescribePool();
        };
        DescribePool();

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(AgentDialogUi.Header(suggestion.Title));
        body.Children.Add(AgentDialogUi.Note($"{node.Title} · {NodeAssistPlanner.KindLabelOf(node.Category)} · {NodeAssistPlanner.BuildPlan(currentCanvas!.Canvas, node).ContextSummary}"));
        if (allowGenerate)
        {
            body.Children.Add(new TextBlock
            {
                Text = "出图方式",
                Foreground = AgentDialogUi.Brush("DfInk2"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });
            body.Children.Add(modeBox);
            body.Children.Add(modeNote);
            // 与「出图方式」平行的一项。两者互不影响，所以各占一行、各说各的。
            body.Children.Add(new TextBlock
            {
                Text = "出图技能",
                Foreground = AgentDialogUi.Brush("DfInk2"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });
            body.Children.Add(poolRow);
            body.Children.Add(new TextBlock
            {
                Text = "数量",
                Foreground = AgentDialogUi.Brush("DfInk2"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });
            body.Children.Add(countRow);
        }
        body.Children.Add(AgentDialogUi.Note("提示词（可改，改完再决定下一步）"));
        body.Children.Add(prompt);
        if (allowGenerate)
        {
            body.Children.Add(AgentDialogUi.Note("负面提示词"));
            body.Children.Add(negative);
        }

        var dialog = DialogShell.Create(
            "Agent 协助 · 提示词",
            AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)),
            620);

        var generateRequested = false;
        NodeImageApproach? forcedApproach = null;
        cancel.Click += (_, _) => dialog.Close();
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(prompt.Text ?? string.Empty);
            StatusText.Text = "提示词已复制到剪贴板";
        };
        hand.Click += (_, _) =>
        {
            ShowAgentMode();
            AgentWorkbenchPanel.Prefill($"按这个提示词干活：\n{prompt.Text}");
            StatusText.Text = "已把提示词填进 Agent 面板，确认后发送";
        };
        generate.Click += (_, _) =>
        {
            // 出图技能是可选的：没选就用设置里的默认图像模型，不需要拦。
            generateRequested = true;
            forcedApproach = modeApproaches[modeBox.SelectedIndex];
            // 真正要出图了才记：只是点开窗口看一眼不该改掉记忆。
            RememberPool(chosenPool);
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        if (generateRequested)
            await GenerateNodeImageAsync(node, suggestion, prompt.Text?.Trim() ?? string.Empty,
                negative.Text?.Trim() ?? string.Empty, forcedApproach, chosenPool,
                (int)(countBox.Value ?? 1));
    }

    private void ShowUpstreamDialog(WorkflowNode node, NodeAssistPlan plan)
    {
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(AgentDialogUi.Header($"「{node.Title}」的上游设定"));
        body.Children.Add(AgentDialogUi.Note(plan.ContextSummary));
        body.Children.Add(new TextBox
        {
            Text = plan.ContextText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 260,
            FontSize = 12
        });

        var copy = AgentDialogUi.Secondary("复制");
        var close = AgentDialogUi.Primary("关闭");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { copy, close }
        };
        var dialog = DialogShell.Create(
            "上游设定",
            AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)),
            560);
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(plan.ContextText);
        };
        close.Click += (_, _) => dialog.Close();
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 节点上那批候选图（同时出 N 张）的内存表。
    /// 键是节点 Id；画布只负责画，保存 / 抛弃 / 重开的规矩都在这里。
    /// </summary>
    private readonly Dictionary<Guid, NodeImageBatch> imageBatches = new();

    /// <summary>
    /// 出图「开奖」这个开关现在开着没有（设置 → 生图与生视频 → 出图观感）。
    ///
    /// 缓存一份、而不是每次重绘时读盘：画布重建很频繁，把一次配置读盘塞进渲染路径不合适。
    /// 刷新点就三个：窗口起来时、设置窗口关掉之后、以及发起新一批出图之前——
    /// 这三处覆盖了「开关可能变过」的全部时机（改了设置必然经过设置窗口）。
    /// </summary>
    private bool gachaReveal;

    /// <summary>出图后要不要让模型判档（设置里的开关，默认关——它要多花一次模型调用）。</summary>
    private bool judgeImageQuality;

    /// <summary>按磁盘上的配置刷新「出图观感」那两个开关，并把开奖开关同步给画布。</summary>
    private void RefreshRevealPreferences()
    {
        try
        {
            var settings = AiProviderSettings.Load();
            gachaReveal = settings.GachaReveal;
            judgeImageQuality = settings.JudgeImageQuality;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            // 读不出来就当它们关着——降级到「原来的样子」，与这两个开关的默认值一致。
            // 它们只影响观感，为它们把主流程拦下来（出图、开画布）不值得，所以这里不报错。
            gachaReveal = false;
            judgeImageQuality = false;
        }
        CanvasSurfaceControl.GachaReveal = gachaReveal;
    }

    /// <summary>
    /// 上一次出图用的那个池子。找不到（站点被删、池子被清单刷掉、同名不同档）就返回 null——
    /// **不退到「第一个」**：退一个别的池子上去，用户会以为上次那个还在，而真正发出去的是另一个模型、另一个价。
    /// </summary>
    private static SitePoolChoice? RememberedPool()
    {
        var config = AiProviderSettings.Load();
        if (config.LastImagePoolSiteId.Length == 0) return null;
        return SiteCatalog.Find(SiteCatalog.Load().Sites,
            config.LastImagePoolSiteId, config.LastImagePoolModel, config.LastImagePoolTier);
    }

    /// <summary>记下这一次用的池子（空 = 用户改回了「不用技能」，这件事也要记住）。</summary>
    private void RememberPool(SitePoolChoice? choice)
    {
        var config = AiProviderSettings.Load();
        var siteId = choice?.Site.Id ?? string.Empty;
        var model = choice?.Pool.Model ?? string.Empty;
        var tier = choice?.Pool.Tier ?? string.Empty;
        if (config.LastImagePoolSiteId == siteId && config.LastImagePoolModel == model && config.LastImagePoolTier == tier) return;

        config.LastImagePoolSiteId = siteId;
        config.LastImagePoolModel = model;
        config.LastImagePoolTier = tier;
        // 记不住不该挡住出图，但也不能默认成功——配置目录可能只读，那就如实说一句。
        if (!AiProviderSettings.Save(config))
            StatusText.Text = "这一次用的池子没能记住（配置文件写不进去）：下次要重新选一遍。";
    }

    /// <summary>
    /// 组装一次出图请求。**单张与多张共用这一处**——池子覆盖了哪几个字段、图生图走哪个接口路径，
    /// 复制成两份的话，改了一处另一处就会悄悄按设置里的默认值跑。
    /// </summary>
    private ImageGenerationRequest BuildImageRequest(
        string prompt, string negative, SitePoolChoice? poolChoice,
        NodeImageDecision decision, IReadOnlyList<string> references, double? denoise)
    {
        var canvas = currentCanvas!;
        return new ImageGenerationRequest
        {
            Prompt = prompt,
            NegativePrompt = negative,
            // 走站点池子时，模型 / 接口 / 画幅 / **密钥**都由选定的那个池子说了算——
            // 「调用前询问用哪个接口」问的就是这几项，问完又按设置里的默认值跑就白问了。
            // 密钥尤其不能漏：一家站一把账号，拿别家的密钥去打它的地址只会拿到 401。
            ApiKey = poolChoice?.Site.ApiKey ?? string.Empty,
            Model = poolChoice?.Pool.Model ?? string.Empty,
            BaseUrl = poolChoice?.Site.BaseUrl ?? string.Empty,
            EndpointPath = poolChoice is null
                ? string.Empty
                : decision.UsesBaseImage && poolChoice.Site.ImageEditPath.Length > 0
                    ? poolChoice.Site.ImageEditPath
                    : poolChoice.Site.ImagePath,
            Method = poolChoice?.Site.Method ?? string.Empty,
            AuthStyle = poolChoice?.Site.AuthStyle ?? string.Empty,
            Width = poolChoice is { Pool.Width: > 0 } sized ? sized.Pool.Width : canvas.Width,
            Height = poolChoice is { Pool.Height: > 0 } measured ? measured.Pool.Height : canvas.Height,
            Steps = canvas.Steps,
            Cfg = canvas.Cfg,
            ReferenceImages = references,
            Denoise = denoise
        };
    }

    /// <summary>
    /// 把「这一步在节点树里的位置」拼成几句话，交给看图评审当上下文。
    ///
    /// 为什么要给：只看到「一张少女立绘」的评审没法判断「这张符不符合要求」——
    /// 要求是由这一步在整条链路里的用途决定的（这是角色设定图，还是某一场的分镜图）。
    /// 只交代节点名、类别、章节与引用的设定，**不塞整棵树的坐标**：评审要的是「这一步在干什么」。
    /// </summary>
    private string DescribeNodeContext(WorkflowNode node)
    {
        var lines = new List<string> { $"节点：{node.Title}（{CategoryNameOf(node.Category)}）" };
        if (node.Chapter.Length > 0) lines.Add($"所属章节：{node.Chapter}");

        var names = new List<string>();
        foreach (var reference in node.References)
        {
            var entity = currentCanvas?.Canvas.FindEntity(reference.EntityId);
            if (entity is null) continue;
            var variant = entity.Variants.FirstOrDefault(candidate => candidate.Id == reference.VariantId);
            names.Add(variant is { Name.Length: > 0 } ? $"{entity.Name} · {variant.Name}" : entity.Name);
        }

        lines.Add(names.Count > 0
            ? "这一步引用的设定（画面里应当与这些设定一致）：" + string.Join("、", names)
            : "这一步没有引用任何设定。");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 同时出 N 张：先在卡片上排 N 个格子，每张自己走一步、自己刷一格，出完让用户挑一张。
    ///
    /// 一张都不直接挂到节点上——一次出 N 张的目的是**从里面挑**，先挂上去再让用户删，
    /// 就把挑选变成了清理。
    /// </summary>
    private async Task RunImageBatchAsync(
        WorkflowNode node, string prompt, string negative, string modeNote,
        IImageProvider provider, SitePoolChoice? poolChoice, NodeImageDecision decision,
        IReadOnlyList<string> references, double? denoise, int count,
        string sourceLabel = "")
    {
        // 这一批马上要画那排候选卡了，先按磁盘上的配置取一次「出图观感」开关：
        // 这样改了设置之后，即使没重开窗口，新发起的一批也立刻是新样子。
        RefreshRevealPreferences();

        // 同一个节点上重出一批时，先把上一批的候选连同文件一起清掉：用户点「重做」就是不要它们了。
        DiscardBatch(node.Id, keepPath: null);

        var batch = new NodeImageBatch
        {
            NodeId = node.Id,
            NodeTitle = node.Title,
            IsRunning = true,
            StartedAt = DateTimeOffset.Now,
            Prompt = prompt,
            Negative = negative,
            NodeContext = DescribeNodeContext(node),
            ModeNote = modeNote,
            SourceLabel = sourceLabel,
            Pool = poolChoice,
            Request = BuildImageRequest(prompt, negative, poolChoice, decision, references, denoise)
        };
        for (var index = 0; index < count; index++) batch.Slots.Add(new BatchSlot());
        imageBatches[node.Id] = batch;

        // 撤销点放在整批之前：N 张图算一次操作，undo 一次退回出这一整批之前。
        RecordSnapshot();
        node.ExecutionStatus = NodeExecutionStatus.Generating;
        CanvasSurfaceControl.Refresh();
        StatusText.Text = $"正在同时出 {count} 张（{provider.Name} · {decision.ApproachLabel}"
            + (poolChoice is null ? string.Empty : $" · {poolChoice.Pool.Label}")
            + "）；节点上方那排窗口会亮着显示进度，出完在节点卡上点一张再点「保存这张」。";

        await Task.WhenAll(batch.Slots.Select(slot => GenerateIntoSlotAsync(node, batch, slot, provider)));

        // 整批可能在跑的过程中就被丢掉了（那时 imageBatches 里已经没有它）。这种情况下不该再改节点状态：
        // 用户已经做了别的决定（采用了一张、或者重做），这里再改一次就是隔空覆盖他的选择。
        if (!IsLiveBatch(node.Id, batch)) return;

        batch.IsRunning = false;
        node.ExecutionStatus = batch.DoneCount > 0 ? NodeExecutionStatus.NeedsReview : NodeExecutionStatus.Failed;
        CanvasSurfaceControl.Refresh();

        // 判档（可选）：先本地筛客观坏图（免费、确定、能指出是哪一条），再让模型给剩下的排名次。
        // 放在整批出完之后，而不是一张一张跟着出：名次要有可比的对象，单张自己排不出名次。
        // 它**不挡住挑图**——图早就出好了，判档只是给开奖添一层档位效果。
        var gradeNote = string.Empty;
        if (judgeImageQuality && batch.DoneCount > 0)
        {
            batch.IsGrading = true;
            CanvasSurfaceControl.Refresh();
            // 先把「正在判」说出去：否则那排卡一动不动，用户会以为卡住了。
            StatusText.Text = $"出好了 {batch.DoneCount} 张：正在判定档位（要花一次模型调用）…";
            gradeNote = await ImageQualityRunner.RunAsync(batch);
            // 判的过程中这一批可能已经被丢掉（采用了一张 / 重做）——那时不该再改它的状态。
            if (!IsLiveBatch(node.Id, batch)) return;
            batch.IsGrading = false;
            CanvasSurfaceControl.Refresh();
        }

        // **不自动采用、不自动弹大图**。理由是一个真实反馈：出了 1 张之后照片自己弹出来、图也已经挂上了，
        // 用户既没法丢弃、也没法选择重出——因为选择权在弹出窗口之前就被替他做完了。
        // 现在单张与多张一样：图先落在节点上方那个窗口里，用不用、删不删、重做不重做都由用户点。
        RefreshResourceList();

        // 判档那句说明放在最前面：它是这一批最新的状态；后面那段「怎么挑」是稳定的说明。
        StatusText.Text = (gradeNote.Length > 0 ? gradeNote + "  " : string.Empty) + (batch.DoneCount > 0
            ? gachaReveal
                // 开奖模式：这一排是盖着的，下一步动作只有「点它」。所以文案直说这件事，
                // 不提右键那几项——它们在图露面之前根本没有意义（菜单里也不摆）。
                ? $"出好了 {batch.DoneCount} 张"
                  + (batch.FailedCount > 0 ? $"，{batch.FailedCount} 张失败（把鼠标停在格子上看原因）" : string.Empty)
                  + "：在节点上方那排背面朝上的卡片上点一下开奖，挑一张收进节点。"
                : $"出好了 {batch.DoneCount} 张"
                  + (batch.FailedCount > 0 ? $"，{batch.FailedCount} 张失败（把鼠标停在格子上看原因）" : string.Empty)
                  + "：在节点上方那排窗口里挑——右键「用这一张」收进节点，「删除这一张」扔掉它，"
                  + "都不要就「全部不要，重做」；双击可以放大看。"
            : $"这一批 {count} 张都没出来：把鼠标停在格子上看原因，或点「重做这一批」。");
    }

    /// <summary>
    /// 这一批还是不是该节点当前那一批。
    ///
    /// 出图是「生成即落盘」的，而一批要跑好一会儿，这中间用户可能把整批丢掉（重做、采用一张、
    /// 或者在同一个节点上又发起了一批）。丢掉的批次再落盘就是没人引用的孤儿文件，
    /// 再改节点状态就是隔空覆盖用户已经做出的选择。所以落盘前和写状态前都问一句。
    /// </summary>
    private bool IsLiveBatch(Guid nodeId, NodeImageBatch batch) =>
        imageBatches.TryGetValue(nodeId, out var live) && ReferenceEquals(live, batch);

    /// <summary>出其中一张，把结果落进它自己那一格。**每张各失败各的**，不整批连坐。</summary>
    private async Task GenerateIntoSlotAsync(WorkflowNode node, NodeImageBatch batch, BatchSlot slot, IImageProvider provider)
    {
        slot.Status = BatchSlotStatus.Running;
        // 记下开始时刻：预览窗口中间那行字要报「生成中 12s」——接口不吐进度，
        // 能诚实给的实时信息就是「还在跑、跑了多久」。
        slot.StartedAt = DateTimeOffset.Now;
        CanvasSurfaceControl.Refresh();
        var index = batch.Slots.IndexOf(slot) + 1;

        try
        {
            var result = await provider.GenerateAsync(batch.Request!);

            // 等结果的这段时间里，这一批可能已经被丢掉了（「全部不要，重做」，或者将来别的新路径）。
            // 出图是「生成即落盘」的：丢掉的批次里再写文件，那些文件不会有任何地方引用，
            // 就成了磁盘上的孤儿。所以落盘前先问一句「这一批还是当前那一批吗」。
            if (!IsLiveBatch(node.Id, batch)) return;

            if (result.Status != ImageGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
            {
                slot.Status = BatchSlotStatus.Failed;
                slot.Error = result.Status == ImageGenerationStatus.NotConfigured
                    ? "这条图像链路还没配置好"
                    : result.Error ?? "服务端没有给出原因";
                return;
            }

            var directory = AssetStore.EnsureDirectory();
            var extension = Path.GetExtension(result.FilePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
            var fileName = $"{SafeFileName(node.Title)}-{DateTime.Now:yyyyMMdd-HHmmss}-{index}{extension}";
            var savedPath = Path.Combine(directory, fileName);
            // overwrite: false —— 同一秒内出两张时文件名会撞，撞了就换一个名字，不覆盖别人的文件。
            if (File.Exists(savedPath)) savedPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}"[..40] + extension);
            File.Copy(result.FilePath, savedPath, overwrite: false);
            slot.Path = savedPath;
            slot.Status = BatchSlotStatus.Done;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            slot.Status = BatchSlotStatus.Failed;
            slot.Error = error.Message;
        }
        finally
        {
            // 每张出完立刻刷一次卡：三张同时出时，用户要看到的是「每张各自走到哪了」，不是一个总进度。
            CanvasSurfaceControl.Refresh();
        }
    }

    /// <summary>
    /// 画布上出图虚影的动作。画布只说「点了哪一张、要干什么」，规矩全在这里。
    ///
    /// 三条约定：
    /// · **选中与采用是两件事**：点一下只是高亮（为了比较、也为了批量删），「用这一张」才写盘。
    /// · **删除是真的删**：文件移入回收站（可还原），并把那一格标成已删除（编号不挪位）。
    /// · **采用一张 = 其余作废**：留下的那张收进节点，其余移入回收站。
    /// </summary>
    private void OnImageBatchAction(Guid nodeId, string action)
    {
        if (currentCanvas is null) return;

        // 「删除选中的」可能跨批次（Ctrl 加选跨了两排虚影），所以它自带键、不靠外面那个 nodeId。
        if (action.StartsWith("ghost-delete-selected:", StringComparison.Ordinal))
        {
            DeleteGhosts(action["ghost-delete-selected:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        if (!imageBatches.TryGetValue(nodeId, out var batch)) return;
        var node = currentCanvas.Canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
        if (node is null) return;

        if (action.StartsWith("ghost-select:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-select:", out var selectIndex))
        {
            batch.SelectedIndices.Clear();
            if (batch.SlotAt(selectIndex) is { Status: BatchSlotStatus.Done }) batch.SelectedIndices.Add(selectIndex);
            CanvasSurfaceControl.Refresh();
            StatusText.Text = batch.SelectedIndices.Count > 0
                // 选中本身无害，但这一批还没出完时那几项动作都不能用——文案要跟着说清楚，
                // 否则会出现「已经选中了、右键那几项却是灰的」这种自相矛盾的状态。
                ? (batch.IsRunning
                    ? $"第 {selectIndex + 1} 张已经出好，但{batch.SingleActionBlockedNote}。"
                    : $"选中第 {selectIndex + 1} 张：右键可以「用这一张」，Delete 删除；Ctrl 点击可多选")
                : $"第 {selectIndex + 1} 张还没出好。";
            return;
        }

        if (action.StartsWith("ghost-toggle:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-toggle:", out var toggleIndex))
        {
            if (batch.SlotAt(toggleIndex) is not { Status: BatchSlotStatus.Done })
            {
                StatusText.Text = $"第 {toggleIndex + 1} 张还没出好，不能选。";
                return;
            }
            if (!batch.SelectedIndices.Add(toggleIndex)) batch.SelectedIndices.Remove(toggleIndex);
            CanvasSurfaceControl.Refresh();
            StatusText.Text = batch.SelectedIndices.Count == 0
                ? "已取消选中。"
                : (batch.IsRunning
                    ? $"已选中 {batch.SelectedIndices.Count} 张，但{batch.SingleActionBlockedNote}。"
                    : $"已选中 {batch.SelectedIndices.Count} 张：Delete 删除，右键可「用这一张」。");
            return;
        }

        // 右键之前先把焦点挪到这一张上：菜单里那几项都是作用于「这一张」的。
        if (action.StartsWith("ghost-focus:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-focus:", out var focusIndex))
        {
            if (batch.SlotAt(focusIndex) is { Status: BatchSlotStatus.Done } && !batch.SelectedIndices.Contains(focusIndex))
            {
                batch.SelectedIndices.Clear();
                batch.SelectedIndices.Add(focusIndex);
                CanvasSurfaceControl.Refresh();
            }
            return;
        }

        if (action.StartsWith("ghost-preview:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-preview:", out var previewIndex))
        {
            if (!batch.CanActOnSlot(previewIndex))
            {
                StatusText.Text = batch.SingleActionBlockedNote + "。";
                return;
            }
            if (batch.SlotAt(previewIndex) is not { Status: BatchSlotStatus.Done, Path.Length: > 0 } previewSlot)
            {
                StatusText.Text = "这一张还没出好，没东西可看。";
                return;
            }
            ShowImagePreview(previewSlot.Path, $"{node.Title} · 候选第 {previewIndex + 1} 张");
            return;
        }

        if (action.StartsWith("ghost-use:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-use:", out var useIndex))
        {
            // 这条守卫是**必须**的，不是双保险：中途采用会把整批丢掉，却不会取消还在跑的请求，
            // 它们跑完后仍会把图写进资产目录，而那时这一批已经不在表里了——那些文件没人引用（漏文件）。
            // 规则在模型里，菜单只是显示，所以动作侧也得挡一道。
            if (!batch.CanActOnSlot(useIndex))
            {
                StatusText.Text = batch.SingleActionBlockedNote + "。";
                return;
            }
            if (batch.SlotAt(useIndex) is not { Status: BatchSlotStatus.Done } useSlot)
            {
                StatusText.Text = "这一张还没出好，不能采用。";
                return;
            }
            SavePickedSlot(node, batch, useSlot, useIndex);
            return;
        }

        // 开奖：那一排卡盖着，点一下才走全屏揭晓。开奖过程要等用户挑，所以是异步的，
        // 这里不 await——动作入口是同步的，等下去会把整个界面卡在这儿。
        if (action.StartsWith("ghost-gacha:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-gacha:", out var gachaIndex))
        {
            _ = OpenGachaRevealAsync(node, batch, gachaIndex);
            return;
        }

        if (action.StartsWith("ghost-delete:", StringComparison.Ordinal) && ParseGhostIndex(action, "ghost-delete:", out var deleteIndex))
        {
            DeleteGhosts(new[] { $"{nodeId}|{deleteIndex}" });
            return;
        }

        if (action == "redo")
        {
            if (batch.HasUnfinished)
            {
                StatusText.Text = "这一批还在出，等出完再重做。";
                return;
            }
            var discarded = DiscardBatch(nodeId, keepPath: null);
            CanvasSurfaceControl.Refresh();
            StatusText.Text = $"这一批都不要了（{discarded} 张图移入回收站，可还原），按同一套参数重出 {batch.Count} 张…";
            _ = RerunBatchAsync(node, batch);
        }
    }

    /// <summary>
    /// 开奖：把这一批已经出好的卡摆到全屏里去揭晓，用户挑中的那张收进节点。
    ///
    /// 挑完之后复用 <see cref="SavePickedSlot"/>——「采用一张 = 其余移入回收站」这条规矩只有那一份实现，
    /// 开奖这条路不该自带一套（否则迟早会出现「从这里挑的，剩下的没进回收站」）。
    /// </summary>
    private async Task OpenGachaRevealAsync(WorkflowNode node, NodeImageBatch batch, int index)
    {
        // 点开之前再确认一次：整批没出完就不开（那排卡还是背面，没什么可开的）。
        if (!batch.CanPick)
        {
            StatusText.Text = batch.SingleActionBlockedNote + "。";
            return;
        }

        var cards = new List<GachaCard>();
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch.Slots[i] is { Status: BatchSlotStatus.Done, Removed: false, Path.Length: > 0 } slot)
                // 判定结果一起带进去：没判过就是 null，**不等于白档**——揭晓里两者长得不一样。
                cards.Add(new GachaCard(i, slot.Path, slot.Quality));
        }
        if (cards.Count == 0)
        {
            StatusText.Text = "这一批没有可以开的图（都失败了，或者都被删掉了）。";
            return;
        }

        var (abbreviation, colorHex, line, providerId) = RevealSource(batch);
        // 这一家的形象（你自己放进 provider-art 的那张图）；没放就是 null，揭晓里用回自绘徽记。
        var avatar = ProviderAvatar.Load(providerId);
        var picked = await GachaRevealDialog.ShowAsync(this, node.Title, cards, abbreviation, colorHex, line, avatar);
        // 没挑就关掉：不留痕迹。那一排卡还在画布上盖着，随时可以再点开。
        if (picked is not { } chosenIndex) return;

        // 开奖这几十秒里这一批可能已经变了（重做、被删、或者又发起了一批）。
        // 那种情况下这一格已经不是当初那张图了，如实说明，不把一张不相干的图收进节点。
        if (batch.SlotAt(chosenIndex) is not { Status: BatchSlotStatus.Done } chosenSlot)
        {
            StatusText.Text = "这一批在开奖期间变了，没有采用任何一张。";
            return;
        }
        SavePickedSlot(node, batch, chosenSlot, chosenIndex);
    }

    /// <summary>
    /// 开奖时「这次是哪一家在出图」该报什么。
    ///
    /// 报的是**图像链路**那一家（按地址认厂，与「当前模型」用同一套规则），不是聊天那一家——
    /// 出图的开奖报成聊天模型就答非所问了。走池子出图时报站点与池子：
    /// 那才是这次真正打过去的去处，比报厂名更接近事实。
    /// 认不出来时用中性徽标「AI」，不硬安一家上去。
    /// </summary>
    private static (string Abbreviation, string ColorHex, string Line, string ProviderId) RevealSource(NodeImageBatch batch)
    {
        AiProviderConfig config;
        try
        {
            config = AiProviderSettings.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            // 报不出「是哪一家」不该拦住开奖：退回中性徽标，池子信息仍然照报。
            var fallback = batch.Pool is { } poolChoice
                ? $"{poolChoice.Site.DisplayName} · {poolChoice.Pool.Label}"
                : batch.SourceLabel;
            return (ProviderBadges.NeutralAbbreviation, ProviderBadges.NeutralColorHex, fallback, "custom");
        }

        var endpoint = config.ImageEndpoint.Length > 0 ? config.ImageEndpoint : config.Endpoint;
        var preset = ProviderPreset.Match(endpoint);
        var badge = ProviderBadges.Of(preset.Id);

        var line = batch.Pool is { } pool
            ? $"{pool.Site.DisplayName} · {pool.Pool.Label}"
            : config.ImageModel.Length > 0 ? $"{preset.Name} · {config.ImageModel}" : preset.Name;

        return (badge.Abbreviation, badge.ColorHex, line, preset.Id);
    }

    private static bool ParseGhostIndex(string action, string prefix, out int index) =>
        int.TryParse(action[prefix.Length..], out index);

    /// <summary>
    /// 删掉指定的虚影（键是 `节点Id|序号`）：文件移入回收站，那一格标成已删除。
    /// 一批全删光就把这一批撤掉——画布上不该留一排空位。
    /// </summary>
    private void DeleteGhosts(IReadOnlyList<string> keys)
    {
        if (currentCanvas is null || keys.Count == 0) return;
        var moved = 0;
        var failed = 0;
        var blocked = 0;
        var touched = new HashSet<Guid>();

        foreach (var key in keys)
        {
            var parts = key.Split('|');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var nodeId) || !int.TryParse(parts[1], out var index)) continue;
            if (!imageBatches.TryGetValue(nodeId, out var batch)) continue;
            if (batch.SlotAt(index) is not { Removed: false } slot) continue;
            // 整批还在跑时不能删：一是「删除」也是一次挑，二是删掉之后重绘会让一排里少一格，
            // 与「出完再一起看」的规则对不上。菜单已经禁用了，这里是动作侧的守卫（Delete 键也走这条路）。
            if (!batch.CanActOnSlot(index))
            {
                blocked++;
                continue;
            }

            if (slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0)
            {
                // 真的移走：出图链路是「生成即落盘」的，只做「界面上不显示」等于把文件留在磁盘上骗用户。
                if (AssetRecycle.Move(slot.Path) is not null) moved++;
                else failed++;
            }
            slot.Removed = true;
            batch.SelectedIndices.Remove(index);
            touched.Add(nodeId);
        }

        foreach (var nodeId in touched)
            if (imageBatches.TryGetValue(nodeId, out var batch) && batch.IsEmpty) imageBatches.Remove(nodeId);

        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        if (blocked > 0 && moved == 0 && failed == 0)
        {
            StatusText.Text = "这一批还在出，出完再挑。";
            return;
        }
        StatusText.Text = (moved > 0 ? $"已删除 {moved} 张（移入回收站，可还原）。" : "已从这一批里去掉。")
            + (blocked > 0 ? $"另有 {blocked} 张要等这一批出完才能删。" : string.Empty)
            + (failed > 0 ? $"另有 {failed} 张没能移入回收站（文件可能已被移动或占用）。" : string.Empty);
    }


    private async Task RerunBatchAsync(WorkflowNode node, NodeImageBatch previous)
    {
        if (currentCanvas is null || previous.Request is null) return;
        IImageProvider provider;
        try { provider = ImageProviderFactory.Create(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText.Text = $"重做失败：图像链路创建不出来（{error.Message}）";
            return;
        }
        var decision = NodeImageModePlanner.Decide(currentCanvas.Canvas, node);
        var approach = previous.Request.ReferenceImages.Count > 0 ? NodeImageApproach.ImageToImage : NodeImageApproach.TextToImage;
        // 池子直接从上一批取（含站点与密钥）：不靠显示文案反推，改一次文案就不会悄悄失效。
        await RunImageBatchAsync(node, previous.Prompt, previous.Negative, previous.ModeNote, provider, previous.Pool,
            decision with { Approach = approach }, previous.Request.ReferenceImages, previous.Request.Denoise, previous.Count,
            sourceLabel: previous.SourceLabel);
    }

    /// <summary>
    /// 把选中的那一张收进节点，其余**真的**移入回收站，然后撤掉这一批。
    ///
    /// 单张与多张走的是**同一段代码**，只有文案不同。分成两条路写的话，那份
    /// 「收尾三步不能少」的规矩迟早只在一条路上成立（早先正是漏了 <c>MarkCanvasDirty</c>，
    /// 切一次标签页那张图就没了）。
    /// </summary>
    private void SavePickedSlot(WorkflowNode node, NodeImageBatch batch, BatchSlot slot, int index)
    {
        RecordSnapshot();
        var reference = AssetStore.ToReference(slot.Path);
        var sourceLabel = batch.SourceLabel.Length > 0 ? batch.SourceLabel : "Agent 协助 · 出图";
        // 记下「这张是照着哪一版设定做的」：以后设定换了图，卡片上就能提示这一镜该重出。
        // 必须**在出图的这一刻**记——只有此刻才知道当时用的是哪一版（判定见 ReferenceStaleness）。
        var fingerprints = currentCanvas is null
            ? new Dictionary<string, string>()
            : ReferenceStaleness.Snapshot(currentCanvas.Canvas, node);
        if (!node.Attachments.Any(item => item.Reference == reference))
            node.Attachments.Add(new WorkflowAttachment
            {
                Kind = AttachmentKind.Image,
                Reference = reference,
                Name = Path.GetFileName(slot.Path),
                Source = batch.Count == 1
                    ? $"{sourceLabel} · {batch.ModeNote}"
                    : $"{sourceLabel} · 一批 {batch.Count} 张里选的第 {index + 1} 张 · {batch.ModeNote}",
                Prompt = batch.Prompt,
                NegativePrompt = batch.Negative,
                SourceFingerprints = fingerprints
            });
        node.ExecutionStatus = NodeExecutionStatus.Completed;

        // 没选中的那些必须**真的**移走：出图链路是「生成即落盘」的，只做「不挂到节点上」的话，
        // 那几张图还躺在资产目录里——用户以为删了，其实没删。
        var discarded = DiscardBatch(batch.NodeId, keepPath: slot.Path);
        imageBatches.Remove(batch.NodeId);
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        // 这三步一步都不能少：
        // MarkCanvasDirty 让这次改动进入标签快照，否则切一次标签页回来（快照被重新克隆）这张图就没了。
        RefreshOpenCenterView();
        MarkCanvasDirty();
        UpdateCanvasUi(currentCanvasPath ?? string.Empty);
        StatusText.Text = batch.Count == 1
            ? $"已出图并挂到「{node.Title}」：{Path.GetFileName(slot.Path)}（{batch.ModeNote}）—— 记得点「保存修订」"
            : (discarded > 0
                ? $"已把第 {index + 1} 张挂到「{node.Title}」；其余 {discarded} 张移入回收站（可还原）。"
                : $"已把第 {index + 1} 张挂到「{node.Title}」。")
              + "—— 记得点「保存修订」";
    }

    /// <summary>
    /// 删掉节点上**已经生成的一张图**：文件移入回收站，引用从节点上摘掉。
    ///
    /// 两件事都要做：只摘引用的话，文件还躺在资产目录里（用户以为删了，其实没删）；
    /// 只移文件的话，卡片上会留下一个指向不存在文件的「无图」方块。
    ///
    /// 文件已经不在了也照样摘引用（那个引用已经没有对应文件），但**如实说明**只摘了引用，
    /// 不谎报「已删除」。
    /// </summary>
    private void RemoveOwnMedia(Guid nodeId, string reference)
    {
        if (currentCanvas is null) return;
        if (canEdit is false)
        {
            StatusText.Text = "项目只读：不能删除节点上的图";
            return;
        }

        var node = currentCanvas.Canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
        if (node is null) return;
        var index = node.Attachments.FindIndex(item => string.Equals(item.Reference, reference, StringComparison.Ordinal));
        if (index < 0)
        {
            StatusText.Text = "这一张已经不在了（可能刚删过）。";
            return;
        }

        // 撤销点放在前面：与「抛弃候选图」那条路一致（N 张图算一次操作）。
        RecordSnapshot();
        var moved = AssetRecycle.MoveReference(reference) is not null;
        node.Attachments.RemoveAt(index);
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        RefreshOpenCenterView();
        MarkCanvasDirty();
        UpdateCanvasUi(currentCanvasPath ?? string.Empty);
        StatusText.Text = moved
            ? $"已从「{node.Title}」删掉这一张（文件移入回收站 _recycle，需要时能从那里取回）。"
            : $"这一张的文件已经不在项目里了，只把引用摘掉了（{node.Title}）。";
    }

    /// <summary>
    /// 撤掉一批：把已出好的图（除 <paramref name="keepPath"/> 之外）移入回收站，返回移走了几张。
    /// 移不动的那些**如实计数**，不谎报「已清理」。
    /// </summary>
    private int DiscardBatch(Guid nodeId, string? keepPath)
    {
        if (!imageBatches.TryGetValue(nodeId, out var batch)) return 0;
        var paths = keepPath is null
            ? batch.AllFinishedPaths()
            : batch.Slots.Where(item => !item.Removed && item.Status == BatchSlotStatus.Done && item.Path.Length > 0
                && !string.Equals(item.Path, keepPath, StringComparison.OrdinalIgnoreCase)).Select(item => item.Path).ToList();
        if (keepPath is null) imageBatches.Remove(nodeId);

        var moved = 0;
        var failed = 0;
        foreach (var path in paths)
        {
            if (AssetRecycle.Move(path) is not null) moved++;
            else failed++;
        }
        if (failed > 0) StatusText.Text = $"有 {failed} 张候选图没能移入回收站（文件可能已被移动或占用）。";
        return moved;
    }

    /// <summary>
    /// 真出图：走已经配好的图像链路（ComfyUI 或 OpenAI 兼容图像接口），成功后把文件收进项目资产目录并挂到节点上。
    /// 没配链路就说清去哪儿配，**不假装出图**；出完图立刻给一个预览窗口，不然用户只看到一句状态栏文字。
    ///
    /// <paramref name="forcedApproach"/> 是界面上手动指定的出图方式（null = 按剧情自动判断）。
    /// 图生图会**真的把底图当参考图发出去**——这条链路里传参考图就等于切图生图，
    /// 所以「用不用底图」必须是个明确决定，不能顺手全传（理由见 <see cref="NodeImageModePlanner"/>）。
    /// </summary>
    private async Task GenerateNodeImageAsync(
        WorkflowNode node, NodeAssistSuggestion suggestion, string prompt, string negative,
        NodeImageApproach? forcedApproach = null, SitePoolChoice? poolChoice = null, int count = 1)
    {
        if (currentCanvas is null || string.IsNullOrWhiteSpace(prompt)) return;
        if (!canEdit)
        {
            StatusText.Text = "项目只读：可以复制提示词到别处出图，但结果写不回这个项目";
            return;
        }

        var decision = NodeImageModePlanner.Decide(currentCanvas.Canvas, node);
        var candidates = NodeImageModePlanner.BaseCandidates(currentCanvas.Canvas, node);
        var approved = true;
        if (forcedApproach == NodeImageApproach.TextToImage)
        {
            decision = decision with
            {
                Approach = NodeImageApproach.TextToImage,
                BaseImageReference = string.Empty,
                BaseImageLabel = string.Empty,
                Denoise = 0,
                Reason = "手动指定：文生图。"
            };
        }
        else if (forcedApproach == NodeImageApproach.ImageToImage)
        {
            // 手动要图生图：优先用判定选的那张，没有就用候选里的第一张（本节点上一版）。
            if (!decision.UsesBaseImage)
                decision = decision with
                {
                    Approach = NodeImageApproach.ImageToImage,
                    BaseImageReference = candidates.Count > 0 ? candidates[0].Reference : string.Empty,
                    BaseImageLabel = candidates.Count > 0 ? candidates[0].Label : string.Empty,
                    Denoise = NodeImageModePlanner.SceneDenoise,
                    Reason = "手动指定：图生图。"
                };
            if (!decision.UsesBaseImage)
            {
                approved = false;
                decision = decision with { Approach = NodeImageApproach.TextToImage, Reason = "手动指定图生图，但手上没有能当底图的图。" };
            }
        }

        var references = Array.Empty<string>();
        var denoise = (double?)null;
        if (decision.UsesBaseImage && AssetStore.Resolve(decision.BaseImageReference) is { } basePath && File.Exists(basePath))
        {
            references = new[] { basePath };
            denoise = decision.Denoise;
        }
        else if (decision.UsesBaseImage)
        {
            // 判定是图生图，但底图文件在项目里已经找不到了：如实退回文生图，而不是发一张空参考图去骗模型。
            decision = decision with
            {
                Approach = NodeImageApproach.TextToImage,
                BaseImageReference = string.Empty,
                BaseImageLabel = string.Empty,
                Denoise = 0,
                Reason = "原来要用的底图在项目里找不到了，退回文生图。"
            };
        }
        // 「图生图 + 一个**明确声明**不支持参考图的池子」是唯一一个不成立的组合。
        // 这时候如实拒绝并说明，**不偷偷改成文生图**：用户明确要的是图生图，给他一张文生图，
        // 钱花了、要的东西也还没拿到。其余组合都成立——文生图 / 图生图决定发不发参考图，
        // 池子决定用哪个模型、哪档、打哪个接口，两者是正交的。
        if (poolChoice is { Pool.SupportsReference: false } && decision.UsesBaseImage)
        {
            StatusText.Text = $"没发出去（没有产生费用）：「{poolChoice.Pool.Label}」的清单里明确写着不支持参考图，"
                + "做不了图生图。把「出图方式」改成「文生图」，或者换一个支持参考图的池子。";
            return;
        }

        if (!approved && references.Length == 0)
            StatusText.Text = "没有可用底图：这个节点和它的引用里都没有现成的图，这一张按文生图出。";

        // 这一张是怎么来的要记在附件上：过一阵想知道它是不是从某张图改出来的，只有这里写着。
        var modeNote = decision.UsesBaseImage
            ? $"图生图 · 底图={decision.BaseImageLabel} · denoise {decision.Denoise:0.00}"
            : "文生图";
        // 走站点池子时把池子的身份也记上：同一句话用 1K 还是 4K，出的钱差好几倍，
        // 事后只看附件根本看不出这张是哪个池子跑的。
        if (poolChoice is { } chosen)
            modeNote = $"{chosen.Site.Label} · {chosen.Pool.Label} · {modeNote}";

        IImageProvider provider;
        try
        {
            provider = ImageProviderFactory.Create();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText.Text = $"图像链路创建失败：{error.Message}";
            return;
        }

        if (!provider.IsConfigured)
        {
            StatusText.Text = "还没配置图像模型：在设置里填「图像接口地址 / 图像模型」（或设 YEEYEEYEE_IMAGE_MODEL），也可以先用「复制提示词」到别处出图";
            return;
        }

        // 一张也好、六张也好，**都走「节点上方那个预览窗口」这条路**：差别只在
        // 「出一张时出好就直接收进节点，不多要一次点击」。
        //
        // 为什么不再给单张留一条自己的路：那条路上画布上**什么都不会出现**——只有节点卡亮着一圈光晕、
        // 状态栏写着「正在出图」，等图出来才跳出一个大图窗口。于是「出图的时候那个自动弹出的节点预览
        // 窗口没有了」这个报告就是这么来的。现在预览窗口在跑的时候就浮在节点上方闪着、报着秒数，
        // 1 张和 6 张是同一套观感（判定、参考图、池子覆盖本来就已经完全共用了）。
        await RunImageBatchAsync(node, prompt, negative, modeNote, provider, poolChoice, decision,
            references, denoise, count,
            sourceLabel: $"Agent 协助 · {suggestion.Title}");
    }

    /// <summary>出完图给一眼能看到的结果：不然「生成成功」只体现在状态栏和文件里。</summary>
    private void ShowImagePreview(string path, string title)
    {
        try
        {
            var bitmap = new Bitmap(path);
            var image = new Image { Source = bitmap, Stretch = global::Avalonia.Media.Stretch.Uniform };
            var body = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
            body.Children.Add(image);
            body.Children.Add(AgentDialogUi.Note($"文件：{path}"));

            var open = AgentDialogUi.Secondary("打开所在文件夹");
            var close = AgentDialogUi.Primary("关闭");
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6,
                Children = { open, close }
            };
            var dialog = DialogShell.Create(
                $"生成结果 · {title}",
                AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)),
                720,
                640);
            open.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    StatusText.Text = $"打不开文件夹：{error.Message}";
                }
            };
            close.Click += (_, _) => dialog.Close();
            dialog.ShowDialog(this);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = $"图已存好，但预览打不开：{error.Message}（文件：{path}）";
        }
    }

    /// <summary>画廊里的一格：一个媒体（可能没有）属于哪个设定的哪个变体。</summary>
    private sealed record GalleryItem(WorkflowEntity Entity, WorkflowEntityVariant Variant, WorkflowAttachment? Media, string Note);

    /// <summary>
    /// 点节点下方的引用预览框：开一个**引用画廊**——上面是放大的那个媒体，下面是这一簇引用的缩略图带。
    ///
    /// 为什么把「子引用」放这里，而不是塞进节点卡：卡片可用宽度只有 204px，一条引用一旦要挂子缩略图
    /// 就得独占一整行，三条引用就把卡从 64px 撑到 190px 以上，缩略图还会被压到 44×32（看不出材质）。
    /// 这个窗口有 760px 宽、还能滚动——同一件事放在放得下的地方做。
    ///
    /// 分三节：**本设定**（这条引用自己的媒体，可逐张切换）/ **它引用的**（子引用：角色挂的道具、服装）/
    /// **同一节点上的其他引用**（这个分镜还引用了谁）。点缩略图就地换大图，所以一个窗口就能把这一簇看完。
    ///
    /// **为什么不内嵌播放器**：Avalonia 自己不带视频解码，要在窗口里播 mp4 就得引入 LibVLCSharp
    /// 这类上百 MB 的原生依赖——为一个「看一眼」的入口不值当。「真的能播放」比「嵌在窗口里」重要，
    /// 所以视频走系统播放器：点一下就开始放，只是不在我们这个窗口里。
    /// </summary>
    private void ShowReferenceMedia(CanvasReferencePreview preview)
    {
        var sections = BuildGallerySections(preview);
        var all = sections.SelectMany(section => section.Items).ToList();

        var mainHost = new Panel();
        var captionHost = new StackPanel { Spacing = 6 };

        // 提示词区跟着「当前在看哪个设定」走：同一个窗口里既看得到图，也改得了这条提示词、重新出图。
        var promptOwner = new TextBlock
        {
            Text = "生成提示词",
            Foreground = AgentDialogUi.Brush("DfInk2"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold
        };
        var promptBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 96,
            FontSize = 12
        };
        var negativeBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 48,
            FontSize = 11
        };
        var promptStatus = AgentDialogUi.Note("改完提示词点「按提示词重新生成」，新图会挂到这个设定的参考图里。");
        var promptEntityId = Guid.Empty;
        var promptAttachmentId = Guid.Empty;
        var lastFilled = string.Empty;
        var lastFilledNegative = string.Empty;
        var current = all[0];

        void LoadPrompt(GalleryItem item)
        {
            var attachmentId = item.Media?.Id ?? Guid.Empty;
            // 同一个设定的同一张图之间切换：不重填，免得把刚改的字顶掉。
            if (item.Entity.Id == promptEntityId && attachmentId == promptAttachmentId) return;

            var edited = promptBox.Text != lastFilled || negativeBox.Text != lastFilledNegative;
            promptEntityId = item.Entity.Id;
            promptAttachmentId = attachmentId;
            if (edited)
            {
                // 换到另一张图时框里是用户改过的字：**保留**，只说明它属于哪一张。
                // 悄悄换掉比留着更糟——人会以为那串字是对面这张图的。
                promptStatus.Text = "已切到另一张：上面的提示词还是你改过的那份；点「重置为推荐提示词」换回这张图本来的";
                return;
            }

            // 附件上存着上次出图实际发出去的提示词就用它（那里面可能有你改过的措辞），没有才按设定现拼。
            var stored = !string.IsNullOrWhiteSpace(item.Media?.Prompt);
            promptBox.Text = SettingPrompt.Prefer(item.Media?.Prompt, item.Entity, item.Variant);
            negativeBox.Text = string.IsNullOrWhiteSpace(item.Media?.NegativePrompt)
                ? SettingPrompt.Negative(item.Entity)
                : item.Media!.NegativePrompt.Trim();
            lastFilled = promptBox.Text;
            lastFilledNegative = negativeBox.Text;
            promptOwner.Text = $"生成提示词 · {WorkflowEntity.KindName(item.Entity.Kind)}「{item.Entity.Name}」（{item.Variant.Name}）"
                + (stored ? " · 上次出图用的那一份" : string.Empty);
            promptStatus.Text = stored
                ? "这是上次出图真发出去的那条（改完再生成会存下新的一份）。"
                : "改完提示词点「按提示词重新生成」，新图会挂到这个设定的参考图里。";
        }

        void Show(GalleryItem item)
        {
            current = item;
            mainHost.Children.Clear();
            mainHost.Children.Add(BuildGalleryMain(item));
            captionHost.Children.Clear();
            captionHost.Children.Add(BuildGalleryCaption(item));
            LoadPrompt(item);
        }
        // 首选「有东西可看」的那一张当大图；整簇都没图时显示说明，而不是一个空框。
        Show(all.FirstOrDefault(item => item.Media is not null) ?? all[0]);

        var regenerate = AgentDialogUi.Primary("按提示词重新生成");
        var rewrite = AgentDialogUi.Secondary("让 Agent 改写提示词");
        var resetPrompt = AgentDialogUi.Secondary("重置为推荐提示词");
        var handOver = AgentDialogUi.Secondary("发给 Agent 面板");
        var promptButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { regenerate, rewrite, resetPrompt, handOver }
        };

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Spacing = 12 };
        body.Children.Add(mainHost);
        body.Children.Add(captionHost);
        foreach (var (title, items) in sections)
        {
            if (items.Count == 0) continue;
            body.Children.Add(BuildGalleryStrip(title, items, Show));
        }
        body.Children.Add(AgentDialogUi.Note(
            "点下面的缩略图就地换大图；视频用系统播放器播放。缩略图右下角标着它属于哪个设定。"));
        body.Children.Add(new Border
        {
            Height = 1,
            Background = AgentDialogUi.Brush("DfLine")
        });
        body.Children.Add(promptOwner);
        body.Children.Add(promptBox);
        body.Children.Add(AgentDialogUi.Note("负面提示词"));
        body.Children.Add(negativeBox);
        body.Children.Add(promptButtons);
        body.Children.Add(promptStatus);

        var close = AgentDialogUi.Primary("关闭");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { close }
        };
        var dialog = DialogShell.Create(
            $"引用 · {preview.Entity.Name}（{WorkflowEntity.KindName(preview.Entity.Kind)} · {preview.Variant.Name}）",
            AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)),
            760,
            560);
        close.Click += (_, _) => dialog.Close();

        resetPrompt.Click += (_, _) =>
        {
            promptBox.Text = SettingPrompt.Compose(current.Entity, current.Variant);
            negativeBox.Text = SettingPrompt.Negative(current.Entity);
            // 重置之后要算「没被改过」：否则换到另一张图时会以为你还想留着这份手改的字。
            lastFilled = promptBox.Text;
            lastFilledNegative = negativeBox.Text;
            promptStatus.Text = "已按设定重新拼一份推荐提示词（还没出图）";
        };

        handOver.Click += (_, _) =>
        {
            ShowAgentMode();
            AgentWorkbenchPanel.Prefill(
                $"请改一改这条出图提示词，让它更具体、更贴合模型习惯（顺序即权重：主体外观在前、画风画幅在后）。\n\n"
                + $"设定：{WorkflowEntity.KindName(current.Entity.Kind)}「{current.Entity.Name}」（{current.Variant.Name}）\n"
                + $"设定描述：{SettingPrompt.Compose(current.Entity, current.Variant)}\n\n"
                + $"当前提示词：\n{promptBox.Text}\n\n负面提示词：{negativeBox.Text}\n\n"
                + "只输出改写后的提示词本身，不要解释、不要代码块。");
            promptStatus.Text = "已把改写指令填进 Agent 面板，确认后发送；把它的回复复制回上面的提示词框即可";
        };

        rewrite.Click += async (_, _) =>
        {
            rewrite.IsEnabled = false;
            promptStatus.Text = "正在让模型改写提示词…";
            var rewritten = await RewriteSettingPromptAsync(current, promptBox.Text ?? string.Empty, negativeBox.Text ?? string.Empty, promptStatus);
            rewrite.IsEnabled = true;
            if (rewritten is null) return;
            promptBox.Text = rewritten;
            promptStatus.Text = "已按模型建议改写（还没出图）：看过没问题再点「按提示词重新生成」";
        };

        regenerate.Click += async (_, _) =>
        {
            regenerate.IsEnabled = false;
            var item = current;
            var outcome = await GenerateSettingImageAsync(item, promptBox.Text ?? string.Empty, negativeBox.Text ?? string.Empty,
                message => promptStatus.Text = message);
            regenerate.IsEnabled = true;
            // 出图前的拦截（没提示词 / 只读 / 没配图像模型 / 接口报错）之后**不关窗**：提示词是用户改的，留住。
            if (outcome == SettingImageOutcome.Failed) return;

            dialog.Close();
            // 成功、或「已生成但没写进库、内存已还原」：两种都要重开画廊，
            // 让它重新读画布上**当前的**设定对象——还原会把变体换成副本，手里这份引用不再有效。
            var entity = currentCanvas?.Canvas.Entities.FirstOrDefault(candidate => candidate.Id == item.Entity.Id);
            var variant = entity?.Variants.FirstOrDefault(candidate => candidate.Id == item.Variant.Id);
            ShowReferenceMedia(entity is null || variant is null
                ? preview
                : new CanvasReferencePreview(preview.Node, entity, variant, MediaOfVariant(variant)));
        };

        dialog.ShowDialog(this);
    }

    /// <summary>一个变体现在能看的媒体（图片与视频）。</summary>
    private static List<WorkflowAttachment> MediaOfVariant(WorkflowEntityVariant variant) =>
        variant.Attachments.Where(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video).ToList();

    /// <summary>
    /// 让模型改写提示词（一次性调用，不是开对话）：只把改写结果填回提示词框，**不自动出图**——
    /// 改写是「建议」，出图是「花钱且要落盘」的动作，中间必须留一次确认。
    /// 没接入文本模型时如实说明，不退到本地模拟（那会产出一段看起来像提示词的假东西）。
    /// </summary>
    private async Task<string?> RewriteSettingPromptAsync(GalleryItem item, string prompt, string negative, TextBlock report)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { report.Text = "提示词是空的，先写点什么"; return null; }
        var config = AiProviderSettings.Load();
        if (config.UseLocalProvider || !config.IsConfigured)
        {
            report.Text = "还没接入文本模型（当前是本地模拟）：去 ⚙ 接一家，或用「发给 Agent 面板」在对话里改";
            return null;
        }
        if (AiProviderFactory.Create() is not IAiChatProvider provider) { report.Text = "创建模型调用失败"; return null; }

        var messages = new List<AiChatMessage>
        {
            new()
            {
                Role = "system",
                Content = "你是出图提示词编辑。只输出改写后的提示词本身：不要解释、不要前言、不要代码块、不要引号。"
                    + "顺序即权重（主体外观在前、情境在中、画风与画幅在后），属性写具体值，避免「正常」「普通」这类空词。"
            },
            new()
            {
                Role = "user",
                Content = $"设定：{WorkflowEntity.KindName(item.Entity.Kind)}「{item.Entity.Name}」（{item.Variant.Name}）\n"
                    + $"设定描述：{SettingPrompt.Compose(item.Entity, item.Variant)}\n\n"
                    + $"当前提示词：\n{prompt}\n\n负面提示词：{negative}\n\n请改写得更具体、更贴合模型习惯。"
            }
        };

        try
        {
            var reply = await provider.ChatAsync(messages);
            var cleaned = (reply ?? string.Empty).Trim().Trim('`').Trim();
            if (cleaned.Length == 0) { report.Text = "模型没有给出内容，换个说法再试"; return null; }
            return cleaned;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            report.Text = $"改写失败：{error.Message}";
            return null;
        }
    }

    /// <summary>给设定出图的结果：拦截失败（不关窗）/ 成功 / 已生成但写库失败并已还原内存。</summary>
    private enum SettingImageOutcome { Failed, Succeeded, RolledBack }

    /// <summary>
    /// 按提示词给**设定**出图：成功后把文件收进项目资产目录、挂到这个变体的参考图上并提交一版。
    /// 与节点那条路（<see cref="GenerateNodeImageAsync"/>）共用同一套图像链路与「不假装出图」的口径，
    /// 区别只在落点：节点是节点附件，这里是设定的参考图。
    /// </summary>
    private async Task<SettingImageOutcome> GenerateSettingImageAsync(GalleryItem item, string prompt, string negative, Action<string> report)
    {
        if (currentCanvas is null) return SettingImageOutcome.Failed;
        if (string.IsNullOrWhiteSpace(prompt)) { report("提示词是空的，先写点什么"); return SettingImageOutcome.Failed; }
        if (!canEdit) { report("项目只读：可以复制提示词到别处出图，但结果写不回这个项目"); return SettingImageOutcome.Failed; }

        IImageProvider provider;
        try
        {
            provider = ImageProviderFactory.Create();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            report($"图像链路创建失败：{error.Message}");
            return SettingImageOutcome.Failed;
        }
        if (!provider.IsConfigured)
        {
            report("还没配置图像模型：在设置里填「图像接口地址 / 图像模型」（或设 YEEYEEYEE_IMAGE_MODEL），也可以先「复制提示词」到别处出图");
            return SettingImageOutcome.Failed;
        }

        report($"正在出图（{provider.Name}）…");
        ImageGenerationResult result;
        try
        {
            result = await provider.GenerateAsync(new ImageGenerationRequest
            {
                Prompt = prompt,
                NegativePrompt = negative,
                Width = currentCanvas.Width,
                Height = currentCanvas.Height,
                Steps = currentCanvas.Steps,
                Cfg = currentCanvas.Cfg
            });
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            report($"出图失败：{error.Message}");
            return SettingImageOutcome.Failed;
        }

        if (result.Status != ImageGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
        {
            report(result.Status == ImageGenerationStatus.NotConfigured
                ? "这条图像链路还没配置好：先填图像模型"
                : $"出图失败：{result.Error}");
            return SettingImageOutcome.Failed;
        }

        // 写库失败要还原内存：克隆一份存底，用 RestoreInto 换回去（它保留实体对象身份，
        // 所以画廊手里那份引用不会失效——但变体列表会被换成副本，所以调用方仍然要重开画廊）。
        var snapshot = ProjectEntityScope.CloneEntity(item.Entity);
        try
        {
            RecordSnapshot();
            var directory = AssetStore.EnsureDirectory();
            var extension = Path.GetExtension(result.FilePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
            var fileName = $"{SafeFileName(item.Entity.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
            var savedPath = Path.Combine(directory, fileName);
            File.Copy(result.FilePath, savedPath, overwrite: false);

            item.Variant.Attachments.Add(new WorkflowAttachment
            {
                Kind = AttachmentKind.Image,
                Reference = AssetStore.ToReference(savedPath),
                Name = fileName,
                Source = $"引用画廊 · 按提示词重新生成（{result.Provider} {result.Model}）",
                // 把**实际发出去**的提示词存在附件上：下次打开这一张就是上次那一份，不用重敲。
                Prompt = prompt,
                NegativePrompt = negative
            });
            // 提交一版：新图进入这个变体的版本历史，和「保存修订」出来的是同一种快照。
            item.Variant.Commit("引用画廊 · 重新生成");

            if (item.Entity.ManagedByProject && !ProjectEntityScope.TryPublish(item.Entity, out var publishError))
            {
                ProjectEntityScope.RestoreInto(item.Entity, snapshot);
                CanvasSurfaceControl.Refresh();
                RefreshResourceList();
                report($"图已生成，但设定没能写进项目库，内存已还原：{publishError}（原文件：{result.FilePath}）");
                return SettingImageOutcome.RolledBack;
            }

            CanvasSurfaceControl.Refresh();
            RefreshResourceList();
            MarkCanvasDirty();
            UpdateCanvasUi(currentCanvasPath ?? string.Empty);
            report($"已出图并挂到「{item.Entity.Name}」的参考图：{fileName}（{result.Provider}"
                + $"{(string.IsNullOrWhiteSpace(result.ReferenceNote) ? string.Empty : " · " + result.ReferenceNote)}）——记得点「保存修订」");
            return SettingImageOutcome.Succeeded;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProjectEntityScope.RestoreInto(item.Entity, snapshot);
            CanvasSurfaceControl.Refresh();
            report($"图已生成但没存进项目：{error.Message}（原文件：{result.FilePath}）");
            return SettingImageOutcome.RolledBack;
        }
    }

    /// <summary>把这一簇引用分成三节：本设定 / 它引用的（子引用）/ 同一节点上的其他引用。</summary>
    private List<(string Title, List<GalleryItem> Items)> BuildGallerySections(CanvasReferencePreview preview)
    {
        var state = currentCanvas?.Canvas;
        var sections = new List<(string Title, List<GalleryItem> Items)>();
        // 同一个设定可能既是子引用又是兄弟引用，只列一次（它是引用点，不是副本）。
        var seen = new HashSet<string>(StringComparer.Ordinal) { KeyOf(preview.Entity, preview.Variant) };

        sections.Add(($"本设定 · {preview.Entity.Name}", MediaItems(preview.Entity, preview.Variant, preview.Media)));

        if (state is not null)
        {
            var children = new List<GalleryItem>();
            foreach (var reference in preview.Variant.References)
            {
                var content = state.ResolveReferenceContent(reference);
                if (content is null) { children.Add(new GalleryItem(preview.Entity, preview.Variant, null, "有一条子引用已经失效")); continue; }
                if (!seen.Add(KeyOf(content.Entity, content.Variant))) continue;
                children.AddRange(MediaItems(content.Entity, content.Variant, ImageOrVideoOf(content)));
            }
            if (children.Count > 0) sections.Add(($"它引用的（子引用）· {children.Count} 格", children));

            var siblings = new List<GalleryItem>();
            foreach (var reference in preview.Node.References)
            {
                if (reference.EntityId == preview.Entity.Id && reference.VariantId == preview.Variant.Id) continue;
                var content = state.ResolveReferenceContent(reference);
                if (content is null) continue;
                if (!seen.Add(KeyOf(content.Entity, content.Variant))) continue;
                siblings.AddRange(MediaItems(content.Entity, content.Variant, ImageOrVideoOf(content)));
            }
            if (siblings.Count > 0) sections.Add(($"同一节点上的其他引用 · {siblings.Count} 格", siblings));
        }
        return sections;
    }

    private static string KeyOf(WorkflowEntity entity, WorkflowEntityVariant variant) => $"{entity.Id:N}/{variant.Id:N}";

    /// <summary>这个设定能看的媒体；一张都没有时给一格说明，别让那一节凭空消失（消失了会让人以为没引用）。</summary>
    private static List<GalleryItem> MediaItems(
        WorkflowEntity entity, WorkflowEntityVariant variant, IReadOnlyList<WorkflowAttachment> media) =>
        media.Count > 0
            ? media.Select(item => new GalleryItem(entity, variant, item, string.Empty)).ToList()
            : new List<GalleryItem> { new(entity, variant, null, $"「{entity.Name}」还没有图片或视频") };

    private static List<WorkflowAttachment> ImageOrVideoOf(ReferenceContent? content) =>
        content is null
            ? new List<WorkflowAttachment>()
            : content.Attachments.Where(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video).ToList();

    /// <summary>大图区：图片按比例放大；视频给 ▶（本身就是播放按钮）；没图 / 解不开都如实说明。</summary>
    private Control BuildGalleryMain(GalleryItem item)
    {
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0E141C")),
            BorderBrush = AgentDialogUi.Brush("DfLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6)
        };

        if (item.Media is null)
        {
            frame.MinHeight = 90;
            frame.Child = AgentDialogUi.Note(item.Note, AgentNoteLevel.Info);
            return frame;
        }

        var path = AssetStore.Resolve(item.Media.Reference);
        if (item.Media.Kind == AttachmentKind.Video)
        {
            frame.Height = 190;
            frame.Child = new TextBlock
            {
                Text = "▶",
                FontSize = 34,
                Foreground = AgentDialogUi.Brush("DfPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 那块 ▶ 本身就是播放按钮：从「点预览」到「开始放」只隔一次点击。
            if (path is not null)
            {
                frame.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(frame, "点击用系统播放器播放");
                frame.PointerPressed += (_, _) => OpenInShell(path, "视频");
            }
            return frame;
        }

        var bitmap = path is null ? null : TryDecode(path, 900);
        if (bitmap is not null)
        {
            frame.Child = new Image
            {
                Source = bitmap,
                Stretch = global::Avalonia.Media.Stretch.Uniform,
                MaxHeight = 380
            };
        }
        else
        {
            frame.Child = AgentDialogUi.Note(path is null
                ? $"这张图在项目里找不到了（引用：{item.Media.Reference}）"
                : $"这张图解不开，可能文件损坏（{path}）", AgentNoteLevel.Warning);
        }
        return frame;
    }

    /// <summary>大图下面那行说明与按钮：这是谁的哪张文件、多大，顺带「打开所在文件夹 / 播放」。</summary>
    private Control BuildGalleryCaption(GalleryItem item)
    {
        var caption = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var label = item.Media is null
            ? $"「{item.Entity.Name}」· {item.Note}"
            : $"{WorkflowEntity.KindName(item.Entity.Kind)}「{item.Entity.Name}」· {item.Variant.Name} · "
              + $"{WorkflowAttachment.DisplayName(item.Media.Kind)} · {Describe(item.Media, AssetStore.Resolve(item.Media.Reference))}";
        caption.Children.Add(AgentDialogUi.Note(label));

        var path = item.Media is null ? null : AssetStore.Resolve(item.Media.Reference);
        if (path is null) return caption;

        if (item.Media!.Kind == AttachmentKind.Video)
        {
            var play = AgentDialogUi.Primary("用系统播放器播放");
            Grid.SetColumn(play, 1);
            play.Margin = new Thickness(8, 0, 0, 0);
            caption.Children.Add(play);
            play.Click += (_, _) => OpenInShell(path, "视频");
        }
        var open = AgentDialogUi.Secondary("打开所在文件夹");
        Grid.SetColumn(open, 2);
        open.Margin = new Thickness(6, 0, 0, 0);
        caption.Children.Add(open);
        open.Click += (_, _) => OpenInShell(Path.GetDirectoryName(path) ?? path, "文件夹");
        return caption;
    }

    /// <summary>
    /// 一节缩略图带：固定格宽（74×78）所以是整齐的网格，点一格就地换上面的大图。
    /// 格子右下角标着它属于哪个设定：跨节看下来（本设定 / 子引用 / 同节点的其他引用）才分得清谁是谁。
    /// </summary>
    private Control BuildGalleryStrip(string title, IReadOnlyList<GalleryItem> items, Action<GalleryItem> onPick)
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = AgentDialogUi.Brush("DfInk2"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold
        });

        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 74,
            ItemHeight = 78
        };
        foreach (var item in items)
        {
            var slot = new StackPanel { Width = 68, Spacing = 3 };
            var tile = new Border
            {
                Width = 68,
                Height = 48,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse("#0E141C")),
                BorderBrush = AgentDialogUi.Brush("DfLine"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var path = item.Media is null ? null : AssetStore.Resolve(item.Media.Reference);
            var bitmap = item.Media is { Kind: AttachmentKind.Image } && path is not null ? TryDecode(path, 220) : null;
            var face = new Panel();
            if (bitmap is not null) face.Children.Add(new Image { Source = bitmap, Stretch = global::Avalonia.Media.Stretch.UniformToFill });
            else face.Children.Add(new TextBlock
            {
                Text = item.Media is null ? "无图" : item.Media.Kind == AttachmentKind.Video ? "▶" : "解不开",
                Foreground = AgentDialogUi.Brush("DfInk2"),
                FontSize = item.Media is null ? 10 : 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            face.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#B30A0F16")),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = item.Entity.Name.Length > 4 ? item.Entity.Name[..4] : item.Entity.Name,
                    Foreground = AgentDialogUi.Brush("DfInk"),
                    FontSize = 9
                }
            });
            tile.Child = face;
            var captured = item;
            tile.PointerPressed += (_, _) => onPick(captured);
            ToolTip.SetTip(tile, item.Media is null ? item.Note : $"{item.Entity.Name} · {Describe(item.Media, path)}");
            slot.Children.Add(tile);
            slot.Children.Add(new TextBlock
            {
                Text = item.Media is null ? "无图" : WorkflowAttachment.DisplayName(item.Media.Kind),
                Foreground = AgentDialogUi.Brush("DfInk3"),
                FontSize = 10,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            strip.Children.Add(slot);
        }
        section.Children.Add(strip);
        return section;
    }

    private static string Describe(WorkflowAttachment attachment, string? path)
    {
        var name = string.IsNullOrWhiteSpace(attachment.Name)
            ? (path is null ? attachment.Reference : Path.GetFileName(path))
            : attachment.Name;
        if (path is null) return $"{name}（文件不在）";
        try
        {
            var size = new FileInfo(path).Length;
            return $"{name} · {size / 1024d / 1024d:0.#} MB";
        }
        catch (IOException) { return name; }
    }

    /// <summary>交给系统打开（图片 / 视频 / 文件夹都走这里）：文本与媒体的打开方式由系统决定，不自己实现一套。</summary>
    private void OpenInShell(string target, string what)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"打不开{what}：{error.Message}";
        }
    }

    /// <summary>解码一张可显示大小的位图；解不开返回 null（由调用方如实说明）。</summary>
    private static Bitmap? TryDecode(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>文件名里只留安全字符：节点标题可能带 / : * 之类，直接用会写盘失败。</summary>
    private static string SafeFileName(string title)
    {
        var cleaned = new string(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "节点";
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }

    private void CanvasSurface_OnNodeMutationCompleted(object? sender, WorkflowNode node)
    {
        if (currentCanvas is null || node.WorkTreeItemId is null) return;
        UnifiedWorkTree.FromNode(currentCanvas.Canvas, node);
        RefreshResourceList();
    }

    private void CanvasSurface_OnConnectionRequested(object? sender, (WorkflowNode Source, WorkflowNode Target) request)
    {
        if (currentCanvas is null || !canEdit) return;
        // 「许不许连」问共享的那一份规则（网页端的「连接」动作问的是同一条）：
        // 两处各写一遍，同一次操作迟早会在两端得到不同的结果。文案也由它给，比一句「或」准确。
        if (CanvasEdgeRules.Refusal(currentCanvas.Canvas, request.Source.Id, request.Target.Id) is { } refusal)
        {
            StatusText.Text = refusal.Message;
            return;
        }
        RecordSnapshot();
        currentCanvas.Canvas.Edges.Add(new WorkflowEdge { SourceNodeId = request.Source.Id, TargetNodeId = request.Target.Id });
        CanvasSurfaceControl.Refresh();
        UpdateCanvasUi(currentCanvasPath ?? string.Empty);
        StatusText.Text = $"已连接：{request.Source.Title} → {request.Target.Title}";
    }

    private void CanvasSurface_OnSelectedNodeChanged(object? sender, WorkflowNode? node)
    {
        if (node is null)
        {
            if (selectedInspectorCard is null) ShowInspectorEmptyState(updateStatus: true);
            return;
        }
        ShowNodeInInspector(node);
    }

    /// <summary>检查器显示一个画布节点（可改）。</summary>
    private void ShowNodeInInspector(WorkflowNode node)
    {
        // 走节点这一路就不再是「在看某张引用卡」：两路都汇到「当前编辑对象」这一个概念上。
        selectedInspectorCard = null;
        StatusText.Text = $"已选择节点：{node.Title}";
        InspectorNameText.Text = node.Title;
        // 所属章节用节点自己的数据；没有就显示破折号，不拿界面自带的示例章节顶上。
        InspectorChapterText.Text = string.IsNullOrWhiteSpace(node.Chapter) ? "—" : node.Chapter;
        // 内容原样填进输入框：这里显示的就是节点的正文，不是「摘要」——用户要在这一格上直接改它。
        InspectorSummaryText.Text = node.Content;
        // 这一格空的时候就靠占位文字说明「这是什么节点、为什么空」：正文空的引用卡（引用失效、
        // 或这个设定还没写描述）点开只看一个空框，很容易被误当成「面板没跟着换」。
        InspectorSummaryText.Watermark = string.IsNullOrWhiteSpace(node.Chapter)
            ? "这个节点还没有内容；可以直接在这一格里写"
            : $"这个节点还没有内容（{node.Chapter}）；可以直接在这一格里写";
        // 说明里带上**正在看哪一个节点**：正文只有一行字的时候，「面板到底跟着选中变了没有」
        // 要有一个不依赖正文的凭据。
        // 别人占着这个节点时，面板直接是只读的——锁的用处就在这里：不是等保存时才回一句，
        // 而是**别让人白改一通**。（自己占的那条不算，见 `HeldByOthers`。）
        var blockedBy = Collaboration().HeldByOthers(node.Id);
        SetInspectorEditable(canEdit && !node.IsLocked && blockedBy is null,
            node.IsLocked
                ? $"正在看「{node.Title}」（{NodeAssistPlanner.KindLabelOf(node.Category)}）· 节点已锁定，先在节点上解锁再改"
                : blockedBy is not null
                    ? $"正在看「{node.Title}」（{NodeAssistPlanner.KindLabelOf(node.Category)}）· {DescribeHolder(blockedBy)}正在编辑这个节点，等他保存或让管理员接管"
                    : $"正在看「{node.Title}」（{NodeAssistPlanner.KindLabelOf(node.Category)}）· {UiText.Text("inspector.applyHint")}");
        if (node.References.Count > 0) StatusText.Text = $"已选中 {node.References.Count} 个引用，双击节点进入临时引用画布";

        // 画布 → 左栏：选中一个节点就把故事画布展开、选中并滚到它那一行。
        RevealStoryRowAsync(node.Id);
    }

    /// <summary>
    /// 浮层里选中 / 取消选中一张引用卡：检查器跟着显示**这张卡指向的那个设定**。
    ///
    /// 引用卡不是画布节点（它没有 WorkflowNode），所以它不走 <see cref="CanvasSurface.SelectedNodeChanged"/>。
    /// 少了这一路，点卡片只有一圈高亮、右边一动不动——用户看到的就是「选中了引用节点，检查器内容不跟着变」。
    /// </summary>
    private void CanvasSurface_OnReferenceCardSelected(object? sender, ReferenceCard? card)
    {
        if (card is not null)
        {
            ShowReferenceCardInInspector(card);
            return;
        }
        // 取消选中（收起浮层 / 换了一个节点展开）：回到节点本身，没有节点就回到空态。
        if (CanvasSurfaceControl.SelectedNode is { } node) ShowNodeInInspector(node);
        else ShowInspectorEmptyState(updateStatus: false);
    }

    /// <summary>检查器显示一张引用卡指向的设定。有多个变体时改的是这张卡指定的那一个变体。</summary>
    private void ShowReferenceCardInInspector(ReferenceCard card)
    {
        selectedInspectorCard = card;
        var content = ResolveCardContent(card);
        var kind = NodeAssistPlanner.KindLabelOf(card.Category);

        InspectorNameText.Text = card.Title;
        InspectorChapterText.Text = string.IsNullOrWhiteSpace(card.Subtitle) ? card.Kind : $"{card.Kind} · {card.Subtitle}";
        InspectorSummaryText.Text = content is null ? $"引用失效：{card.BlockedReason}" : content.Description;
        InspectorSummaryText.Watermark = "这个设定还没有描述；可以直接在这一格里写";

        // 锁定到某个版本快照的引用是**只读**的：改它会让人以为改到了那一版。
        var locked = card.VersionId is not null;
        var editable = canEdit && content is not null && !locked;
        SetInspectorEditable(editable, locked
            ? $"正在看引用「{card.Title}」（{kind}）· 它锁定了某个版本快照，改不了；要改当前描述先在引用里改回跟随最新"
            : content is null
                ? $"正在看引用「{card.Title}」（{kind}）· 引用失效，改不了"
                : $"正在看引用「{card.Title}」（{kind}）· 改完点「应用修改」写回设定库（引用本身仍在原画布上）");
        StatusText.Text = $"已选中引用：{card.Title}（{kind}）· 检查器里能直接改它的名称与描述";
    }

    /// <summary>一张引用卡现在指向的内容（实体 / 变体 / 版本都对得上才有值）。</summary>
    private ReferenceContent? ResolveCardContent(ReferenceCard card) => currentCanvas?.Canvas.ResolveReferenceContent(
        new NodeReference
        {
            EntityId = card.EntityId,
            VariantId = card.VariantId,
            VariantVersionId = card.VersionId
        });

    /// <summary>
    /// 检查器回到「未选择节点」。进入项目、切换项目、清空画布时都要调用：
    /// 界面里那份「第一章 · 雨夜码头 / 已拆分为 42 章」是自带占位文案，不主动清掉就会挂在空项目上，像是真的。
    /// </summary>
    private void ShowInspectorEmptyState(bool updateStatus)
    {
        if (updateStatus) StatusText.Text = "未选择节点";
        selectedInspectorCard = null;
        InspectorNameText.Text = "未选择节点";
        InspectorSummaryText.Text = string.Empty;
        // 占位文字也要跟着回到「还没选」：它是上一个节点留下的，不清掉会让人以为还看着那个节点。
        InspectorSummaryText.Watermark = "在画布上点一个节点，这里会显示它的内容，并可以直接修改";
        InspectorChapterText.Text = "—";
        SetInspectorEditable(false, "在画布上点一个节点，这里就能直接改它的名称与内容");
    }

    /// <summary>
    /// 检查器里那两格什么时候能改：项目可写、且选中的节点没被锁。
    /// 不能改的时候把原因写在旁边——摆着两个输入框却打不了字，比只读更让人困惑。
    /// </summary>
    private void SetInspectorEditable(bool editable, string hint)
    {
        InspectorNameText.IsEnabled = editable;
        InspectorSummaryText.IsEnabled = editable;
        InspectorApplyButton.IsEnabled = editable;
        inspectorHint = hint;
        // 不能编辑的时候（未选择节点、只读项目）不显示协作那句：
        // 它说的是上一个节点的事，留着只会张冠李戴。
        if (!editable) collaborationHint = string.Empty;
        RefreshInspectorHint();
    }

    /// <summary>检查器里按 Ctrl+Enter 等于点「应用修改」：不改键位习惯，但省一次鼠标。</summary>
    private void InspectorField_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        ApplyInspectorEdits();
    }

    /// <summary>
    /// 把检查器里改过的名称与内容写回选中的节点。与双击节点的编辑窗口是同一条写入路径：
    /// 记一次撤销快照、改节点、按锚点同步工作树、刷新画布，剩下的交给「保存修订」。
    /// </summary>
    private void InspectorApply_OnClick(object? sender, RoutedEventArgs e) => ApplyInspectorEdits();

    private void ApplyInspectorEdits()
    {
        if (currentCanvas is null) { StatusText.Text = "没有打开的画布"; return; }
        if (!canEdit) { StatusText.Text = "项目或项目库不可写，当前为只读，改不了"; return; }

        var title = InspectorNameText.Text?.Trim() ?? string.Empty;
        var content = InspectorSummaryText.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title)) { StatusText.Text = "名称不能为空"; return; }

        // 两路：在看画布节点就写节点（临时引用画布上的节点由「保存修订」落库），
        // 在看引用浮层里的那张卡就写它指向的设定。
        if (selectedInspectorCard is { } card) { ApplyReferenceCardEdits(card, title, content); return; }

        if (CanvasSurfaceControl.SelectedNode is not { } node) { StatusText.Text = "先在画布上选中一个节点"; return; }
        if (node.IsLocked) { StatusText.Text = "节点已锁定，先在节点上解锁再改"; return; }
        if (title == node.Title && content == node.Content) { StatusText.Text = "名称与内容没有改动"; return; }

        RecordSnapshot();
        node.Title = title;
        node.Content = content;
        // 临时引用画布有自己的落点（「保存修订」写回设定库），不在这里顺手改工作树：
        // 那个副本带着章节锚点，走 FromNode 会当场改到真实的工作树条目上。
        if (referenceCanvas is null) UnifiedWorkTree.FromNode(currentCanvas.Canvas, node);
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        // 名称变了，检查器与状态栏跟着改；内容不回填（避免把用户的光标位置顶掉）。
        InspectorNameText.Text = node.Title;
        StatusText.Text = $"已更新节点：{node.Title}（记得点「保存修订」）";
    }

    // ==================== 协作：编辑时占住节点锁 ====================

    /// <summary>
    /// 应用持有的协作会话（**只有一个**：设置页登录的、这里占锁的是同一个实例）。
    /// 地址取自配置，没配就是空地址 = 不接协作——那不是错误状态，是默认状态。
    /// </summary>
    private CollaborationSession? collaboration;

    /// <summary>正在保存。保存期间来的推送不该触发自动重载：那会儿内存里的画布正要落盘。</summary>
    private bool savingCanvas;

    /// <summary>设置里「改完自动同步」的当前值（只有设置页能改，所以只在打开设置回来时重读一次）。</summary>
    private bool autoSyncEnabled;

    /// <summary>自动同步的计时器（开关关着、或没登录时它不跑）。</summary>
    private DispatcherTimer? autoSyncTimer;

    /// <summary>自动同步失败后退避到什么时候（失败了就别每两秒再撞一次）。</summary>
    private DateTimeOffset autoSyncBackoffUntil;

    /// <summary>上一次自动同步说过的话（同一句不重复喊）。</summary>
    private string autoSyncLastNote = string.Empty;

    /// <summary>重读「改完自动同步」开关并据此起停计时器。</summary>
    private void RefreshAutoSyncSetting()
    {
        autoSyncEnabled = AiProviderSettings.Load().CollaborationAutoSync;
        var shouldRun = autoSyncEnabled && collaboration is { IsSignedIn: true, IsConfigured: true };
        if (!shouldRun)
        {
            autoSyncTimer?.Stop();
            return;
        }

        if (autoSyncTimer is null)
        {
            autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            autoSyncTimer.Tick += AutoSyncTick;
        }

        if (!autoSyncTimer.IsEnabled) autoSyncTimer.Start();
    }

    /// <summary>
    /// 改完自动同步：每 2 秒看一眼「有没有还没落盘的改动」，有就交给服务端。
    ///
    /// 只走服务端那一条路（<c>allowLocalFallback: false</c>）：服务器叫不到时**不改成写本地**——
    /// 打开这个开关的人要的是「同步给协作者」，不是「偷偷替我落盘」。
    /// 失败退避 30 秒，并且同一句话不重复喊：每两秒喊一遍同样的错是最吵的设计。
    /// </summary>
    private async void AutoSyncTick(object? sender, EventArgs e)
    {
        var session = collaboration;
        var dirty = activeCanvasTab?.Dirty == true;
        var busy = savingCanvas || DateTimeOffset.UtcNow < autoSyncBackoffUntil;
        if (session is null ||
            !AutoSync.ShouldPush(autoSyncEnabled, session.IsConfigured, session.IsSignedIn, dirty, busy)) return;

        savingCanvas = true;
        string? message;
        try { message = await TrySaveCanvasThroughServerAsync(allowLocalFallback: false); }
        finally { savingCanvas = false; }

        // 「成了没有」看未保存标记有没有被撤掉——比去嗅一句话的内容可靠。
        var ok = activeCanvasTab?.Dirty != true;
        if (!ok) autoSyncBackoffUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        if (message is not null && message != autoSyncLastNote)
        {
            StatusText.Text = message;
            autoSyncLastNote = message;
        }
    }

    private CollaborationSession Collaboration()
    {
        collaboration ??= new CollaborationSession(AiProviderSettings.Load().CollaborationServerUrl);
        // 登录之后（比如刚从设置里登录回来）顺手把订阅起起来；已经起着就不动。
        if (collaboration.IsSignedIn) StartCollaborationWatch(collaboration);
        return collaboration;
    }

    /// <summary>订阅服务端的变更推送。回调跑在后台线程上，所以这里一律切回 UI 线程再动界面。</summary>
    private void StartCollaborationWatch(CollaborationSession session)
    {
        session.StartWatching(
            notice => Dispatcher.UIThread.Post(() => OnRemoteCanvasChanged(notice)),
            // 别人抢了/放了锁：顺手把锁列表刷新一遍，设置页里那行「谁在编辑」就不会停在旧状态。
            () => Dispatcher.UIThread.Post(() => _ = session.RefreshLeasesAsync()));
    }

    /// <summary>
    /// 别人改了画布：能跟上就跟上，跟不上就只说。
    ///
    /// **自己那次保存的回声要认出来丢掉**（actor 就是我）：不认的话，每保存一次就对着自己喊一句
    /// 「画布有变动」。代价是「我的另一台机器、同一个账号」的改动也会被当成回声——这一点写在
    /// 代码注释里，而不是假装没有。
    ///
    /// 该不该自动重载由 <see cref="RemoteCanvasChange.Decide"/> 判断（纯函数、有回归）：
    /// 本地干净就直接重载，手上还有东西（未保存改动、正在编辑、正在保存）就只提示。
    /// </summary>
    private void OnRemoteCanvasChanged(CanvasChangedNotice notice)
    {
        var mine = collaboration?.User is { } me && notice.Actor == CollaborationSession.Display(me);
        var busy = savingCanvas || collaboration?.HeldNodeLease is not null
            || InspectorNameText.IsFocused || InspectorSummaryText.IsFocused;
        var action = RemoteCanvasChange.Decide(mine, currentCanvas is not null, activeCanvasTab?.Dirty == true, busy);
        if (action == RemoteChangeAction.Ignore) return;

        var node = notice.RecordId is { } id && Guid.TryParse(id, out var parsed)
            ? currentCanvas?.Canvas.Nodes.FirstOrDefault(item => item.Id == parsed)
            : null;
        var what = notice.Scope == "layout" ? "重新整理了画布布局"
            : notice.Scope == "canvas" ? "把整张画布存了一遍"
            : node is not null ? $"改了「{node.Title}」"
            : "改了一个节点";

        if (action == RemoteChangeAction.NotifyOnly)
        {
            StatusText.Text = $"画布有变动：{notice.Actor}{what}——你手上这份已经不是最新的，保存前先重新打开这张画布。";
            return;
        }

        StatusText.Text = ReloadActiveCanvas()
            ? $"画布有变动：{notice.Actor}{what}——已重新载入最新版本。"
            : $"画布有变动：{notice.Actor}{what}——自动重新载入失败，请自己重新打开这张画布。";
    }

    /// <summary>
    /// 把当前画布从磁盘重读一遍。
    ///
    /// 走「换一个标签」这条路而不是让标签自己重读：装载只有一条实现（<see cref="OpenCanvasFromLibrary"/>），
    /// 重读要另写一份就迟早会和它分叉。代价是这个标签的撤销历史会丢——跨一次远端改动，
    /// 那份历史本来也已经是错的。
    /// </summary>
    private bool ReloadActiveCanvas()
    {
        if (activeCanvasTab?.Path is not { } path) return false;
        // 先确认读得动，再动标签：读不动还把标签丢了，就是从「旧内容」掉进「什么都没有」。
        if (!CanvasLibrary.TryLoad(path, out _)) return false;

        canvasTabs.Remove(activeCanvasTab);
        OpenCanvasFromLibrary(path);
        return activeCanvasTab?.Path is not null && string.Equals(activeCanvasTab.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>检查器下面那句提示 = 原有的一句 + 协作要说的一句。</summary>
    private string inspectorHint = string.Empty;
    private string collaborationHint = string.Empty;

    /// <summary>占着锁的时候每 20 秒续一次（锁的 TTL 是两分钟，这个间隔够稳）。没占锁它就不跑。</summary>
    private DispatcherTimer? collaborationHeartbeat;

    private void RefreshInspectorHint() =>
        InspectorEditHintText.Text = collaborationHint.Length == 0 ? inspectorHint : $"{inspectorHint} {collaborationHint}";

    private void SetCollaborationHint(string text)
    {
        collaborationHint = text;
        RefreshInspectorHint();
    }

    /// <summary>
    /// 进编辑框就占住这个节点的编辑锁——**只有真的打算改才占**：点着看一圈不算，
    /// 否则等于把别人挡在外面而自己什么也没改。
    ///
    /// 拿不到锁时**只有一种情况要说话**：别人正占着。其余（没配服务器、没登录、连不上服务器）
    /// 都只是不占——桌面端是本地优先的，协作不可用不该让画布变得不能改。
    /// </summary>
    private async void InspectorField_OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (!canEdit || CanvasSurfaceControl.SelectedNode is not { } node) return;

        var session = Collaboration();
        if (!session.IsSignedIn) return;

        SetCollaborationHint(string.Empty);
        if (session.HeldNodeLease is { TargetId: { } held } && held == node.Id) return;

        // 换了节点：先把上一个还掉，别一手占两个。
        if (session.HeldNodeLease is not null) await session.ReleaseHeldLeaseAsync();

        var result = await session.AcquireNodeLeaseAsync(node.Id);
        if (result.Ok)
        {
            EnsureCollaborationHeartbeat().Start();
            return;
        }

        if (result.Code == "EDIT_CONFLICT" && result.Holder is { } holder)
        {
            // 如实说是谁在编辑，照服务端那句的意思说。**但不阻止本地编辑**：
            // 桌面端写的是本地文件，这条锁在这里是「告诉别人、也告诉用户」，还不是权限。
            SetCollaborationHint($"{DescribeHolder(holder)}正在编辑这个节点，你现在的改动可能会盖掉他的。");
            return;
        }

        if (result.Code is "UNAUTHORIZED" or "NOT_SIGNED_IN")
            SetCollaborationHint("协作登录已失效，这条锁没占上（去 设置 → 协作）。");
    }

    /// <summary>
    /// 离开编辑框就还回去。焦点在两个框之间移动时也会先失焦，
    /// 所以等一次调度再判断：两个都不在焦点上，才算真的离开了这一格。
    /// </summary>
    private async void InspectorField_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (InspectorNameText.IsFocused || InspectorSummaryText.IsFocused) return;
        await ReleaseCollaborationLeaseAsync();
    }

    private async Task ReleaseCollaborationLeaseAsync()
    {
        collaborationHeartbeat?.Stop();
        if (collaboration is not { HeldNodeLease: not null } session) return;
        await session.ReleaseHeldLeaseAsync();
    }

    private DispatcherTimer EnsureCollaborationHeartbeat()
    {
        if (collaborationHeartbeat is { } existing) return existing;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += async (_, _) =>
        {
            if (collaboration is not { HeldNodeLease: not null } session)
            {
                timer.Stop();
                return;
            }

            var result = await session.RenewHeldLeaseAsync();
            // 续不上（被管理员接管、或已经过期）要说清楚，别让用户以为还占着。
            // 连不上服务器不算：TTL 内锁还归我们，为这个惊动用户只会添乱。
            if (!result.Ok && result.Code != "NETWORK") SetCollaborationHint(result.Message);
        };
        return collaborationHeartbeat = timer;
    }

    /// <summary>持有者的说法与服务端、网页端用同一套：名字 + 来源端。</summary>
    private static string DescribeHolder(CollaborationLease holder) =>
        $"{holder.DisplayName}（{CollaborationSession.ClientLabel(holder.Client)}）";

    /// <summary>
    /// 这个节点现在**该只读**吗——别人占着它的编辑锁就是。
    ///
    /// 写一个节点的三条路（检查器、节点编辑窗口、出图）都先问这一句，命中时统一说同一句话，
    /// 不各自编措辞。返回 true 表示「已经在这里处理过了，调用方直接 return」。
    ///
    /// **只在真的要用到这个节点时问**，不做全局只读——别人的锁挡的是「改这个节点」，
    /// 不是「打开这张画布看看」。
    /// </summary>
    private bool BlockedByOtherEditor(Guid nodeId)
    {
        if (Collaboration().HeldByOthers(nodeId) is not { } holder) return false;
        StatusText.Text = $"{DescribeHolder(holder)}正在编辑这个节点，等他保存或让管理员接管";
        return true;
    }

    /// <summary>
    /// 把检查器里改过的名称与描述写回**这张引用卡指向的设定**（实体名 + 变体描述）。
    ///
    /// 与「保存修订」是同一条写回路径：托管资源要落进项目库，写不进去就把内存改回去——
    /// 不允许出现「界面说改好了、库里还是旧的」。
    /// 锁定版本的引用不走到这里（那些卡在检查器里是只读的）。
    /// </summary>
    private void ApplyReferenceCardEdits(ReferenceCard card, string title, string content)
    {
        var entity = currentCanvas!.Canvas.FindEntity(card.EntityId);
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == card.VariantId);
        if (entity is null || variant is null) { StatusText.Text = "这条引用指向的设定已经不存在了，改不了"; return; }
        if (card.VersionId is not null) { StatusText.Text = "这条引用锁定了版本快照，改不了"; return; }
        if (title == entity.Name && content == variant.Description) { StatusText.Text = "名称与描述没有改动"; return; }

        var snapshot = ProjectEntityScope.CloneEntity(entity);
        RecordSnapshot();
        entity.Name = title;
        variant.Description = content;
        variant.Commit("节点检查器编辑");

        if (entity.ManagedByProject && !ProjectEntityScope.TryPublish(entity, out var error))
        {
            var index = currentCanvas.Canvas.Entities.FindIndex(item => item.Id == snapshot.Id);
            if (index >= 0) currentCanvas.Canvas.Entities[index] = snapshot;
            CanvasSurfaceControl.Refresh();
            RefreshResourceList();
            ShowReferenceCardInInspector(card);
            StatusText.Text = $"设定没写进项目库，已恢复：{error}";
            return;
        }

        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        // 刷新之后浮层与卡片文字都重建了，检查器要按新内容再填一次（否则还显示旧名字）。
        // 优先取重算过的那张卡：卡片上的标题是构建那一刻的快照。
        ShowReferenceCardInInspector(CanvasSurfaceControl.CurrentReferenceCard(card.Key) ?? card with { Title = title });
        StatusText.Text = $"已更新设定：{title}（画布上的引用卡与左栏都跟着变了；引用本身的位置没动）";
    }

    /// <summary>
    /// 双击节点：开「编辑画布节点」窗口。
    ///
    /// **带引用的节点不会走到这里**：<c>NodePointerPressed</c> 把「双击 + 有引用」分流给了
    /// <c>NodeReferenceDoubleClicked</c>（进临时引用画布），只有没有引用可展开的节点才落到这。
    /// 这里**不要**再判断「在临时画布里且有引用就往下钻」——那条判断会和
    /// <c>EnterReferenceCanvas</c> 里的「源头节点」分支互相调用，一层层递归下去直接把程序打崩（栈溢出）。
    /// 往下钻由 <c>NodeReferenceDoubleClicked</c> 那条路负责。
    /// </summary>
    private async void CanvasSurface_OnNodeDoubleClicked(object? sender, WorkflowNode node)
    {
        if (currentCanvas is null || !canEdit || node.IsLocked) { StatusText.Text = "项目只读、节点已锁定或画布不可编辑"; return; }
        if (BlockedByOtherEditor(node.Id)) return;
        await ShowNodeEditorAsync(node);
    }

    /// <summary>
    /// 「编辑画布节点」：改名称与内容，并**就地**给出这个节点的 Agent 协作提示词。
    ///
    /// 提示词是按节点类型 + 素材算出来的，素材口径与右键「Agent 协助」**完全同一份**
    /// （都是 <see cref="NodeAssistPlanner.BuildPlan"/>），所以原画布和临时引用画布上双击同一个节点，
    /// 看到的是同一套东西，不会长成两个样子。
    ///
    /// 素材是「往上推」推出来的：本体设定（它引用的角色 / 场景 / 道具设定，也就是外观锚点）→ 上游 →
    /// 同镜（同一个分镜里的场景与道具）→ 所属章节。对角色 / 场景节点来说本体必须排第一：
    /// 决定出图的是「这个角色长什么样」，而不是「这一章在讲什么」。
    ///
    /// 默认**只给不写**：提示词待在自己的框里，想落盘就点「写回节点内容」再按「应用」——
    /// 绝不会因为打开过这个窗口，就把用户写的正文悄悄覆盖掉。
    /// </summary>
    private async Task ShowNodeEditorAsync(WorkflowNode node)
    {
        if (currentCanvas is null) return;

        var plan = NodeAssistPlanner.BuildPlan(currentCanvas.Canvas, node);
        // 优先给「纯提示词」那条（角色 → 外观锚点设定、场景 → 远中近景设定…），没有才退回第一条建议。
        var suggestion = plan.Suggestions.FirstOrDefault(item => item.Kind == NodeAssistKind.Prompt)
            ?? plan.Suggestions.FirstOrDefault();

        var title = new TextBox { Text = node.Title };
        var content = new TextBox
        {
            Text = node.Content,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 170
        };
        var prompt = new TextBox
        {
            Text = suggestion?.Prompt ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Height = 140,
            FontSize = 12
        };

        var copy = AgentDialogUi.Secondary("复制提示词");
        var writeBack = AgentDialogUi.Secondary("写回节点内容");
        var hand = AgentDialogUi.Secondary("发给 Agent 面板");
        var assistRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { copy, writeBack, hand }
        };

        var apply = new Button { Content = "应用", HorizontalAlignment = HorizontalAlignment.Right };
        apply.Classes.Add("primary");
        var cancel = AgentDialogUi.Secondary("取消");

        var fields = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                AgentDialogUi.Note("节点名称"), title,
                AgentDialogUi.Note("节点内容"), content,
                AgentDialogUi.Header("Agent 协助"),
                AgentDialogUi.Note(plan.ContextSummary),
                AgentDialogUi.Note($"按「{NodeAssistPlanner.KindLabelOf(node.Category)}」算出来的提示词（可改；默认不写回节点）"),
                prompt,
                assistRow
            }
        };
        if (suggestion is { CanRun: false })
            fields.Children.Insert(6, AgentDialogUi.Note(suggestion.Blocked));

        var applyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, apply }
        };
        var dialog = DialogShell.Create(
            "编辑画布节点",
            AgentDialogUi.Layout(fields, AgentDialogUi.Footer(applyRow)),
            560);

        cancel.Click += (_, _) => dialog.Close(false);
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(prompt.Text ?? string.Empty);
            StatusText.Text = "提示词已复制到剪贴板";
        };
        writeBack.Click += (_, _) =>
        {
            content.Text = prompt.Text ?? string.Empty;
            StatusText.Text = "已把提示词填进「节点内容」：按「应用」才会落盘";
        };
        hand.Click += (_, _) =>
        {
            // 与「Agent 协助 · 提示词」窗口同一个做法：先填不发，一次发送就是一次模型调用。
            ShowAgentMode();
            AgentWorkbenchPanel.Prefill($"按这个提示词干活：\n{prompt.Text}");
            StatusText.Text = "已把提示词填进 Agent 面板，确认后发送";
        };
        apply.Click += (_, _) => dialog.Close(true);

        if (!await dialog.ShowDialog<bool>(this)) return;

        RecordSnapshot();
        node.Title = title.Text?.Trim() ?? string.Empty;
        node.Content = content.Text ?? string.Empty;
        // 临时引用画布有自己的落点（「保存修订」写回设定库），不在这里顺手改工作树：
        // 那个副本现在带着章节锚点，走 FromNode 会当场改到真实的工作树条目上。
        if (referenceCanvas is null) UnifiedWorkTree.FromNode(currentCanvas.Canvas, node);
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        StatusText.Text = $"已编辑节点：{node.Title}";
    }

    private void RecordSnapshot()
    {
        if (suppressSnapshot || currentCanvas is null) return;
        undoSnapshots.Push(JsonSerializer.Serialize(currentCanvas.Canvas, SnapshotOptions));
        redoSnapshots.Clear();
        MarkCanvasDirty();
    }

    /// <summary>
    /// 记下「当前画布有未保存的编辑」：标签标题后面会多一个 *。
    /// 所有改画布的入口都汇到 RecordSnapshot（画布控件在改动前会触发 BeforeCanvasMutation），
    /// 所以这里标一次就够，不用在每个按钮回调里各标一遍（那样迟早漏一处）。
    /// </summary>
    private void MarkCanvasDirty()
    {
        // 临时引用画布的「未保存」是「还没写回设定库」，不属于任何画布标签。
        if (referenceCanvas is not null)
        {
            if (referenceCanvas.Dirty) return;
            referenceCanvas.Dirty = true;
            RebuildCanvasTabStrip();
            return;
        }
        if (activeCanvasTab is null || activeCanvasTab.Dirty) return;
        activeCanvasTab.Dirty = true;
        RebuildCanvasTabStrip();
    }

    private void RestoreSnapshot(Stack<string> source, Stack<string> destination)
    {
        if (currentCanvas is null || source.Count == 0) return;
        destination.Push(JsonSerializer.Serialize(currentCanvas.Canvas, SnapshotOptions));
        var snapshot = JsonSerializer.Deserialize<WorkflowCanvasState>(source.Pop(), SnapshotOptions);
        if (snapshot is null) return;
        suppressSnapshot = true;
        currentCanvas = currentCanvas with { Canvas = snapshot };
        CanvasSurfaceControl.State = snapshot;
        RefreshResourceList();
        suppressSnapshot = false;
        // 撤销 / 重做同样改了画布内容：它也一样是「还没保存的编辑」。
        MarkCanvasDirty();
        if (!string.IsNullOrWhiteSpace(currentCanvasPath)) UpdateCanvasUi(currentCanvasPath);
        StatusText.Text = "画布编辑已恢复，保存后写入项目";
    }

    private void CanvasSurface_OnCanvasChanged(object? sender, EventArgs e)
    {
        if (currentCanvas is not null)
        {
            RefreshResourceList();
            UpdateCanvasUi(currentCanvasPath ?? string.Empty);
            MarkCanvasDirty();
            RefreshOpenCenterView();
        }
        StatusText.Text = "画布已修改，拖拽位置将在保存时写入";
    }

    private void ProjectMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "项目菜单可用：可通过项目树管理项目，或使用新建/打开项目操作";
    }

    /// <summary>项目树：看这个项目在磁盘上有什么（画布 / 项目库 / 资产 / 项目文件）。</summary>
    private void ProjectTree_OnClick(object? sender, RoutedEventArgs e)
    {
        SetResourceView(ResourceViewMode.Project);
        StatusText.Text = "已切到项目树：双击画布可打开（资源库也在这棵树里）";
    }

    /// <summary>故事画布：左栏看当前画布的工作树（章节 / 出场 / 资源）。</summary>
    private void StoryCanvas_OnClick(object? sender, RoutedEventArgs e)
    {
        SetResourceView(ResourceViewMode.WorkTree);
        StatusText.Text = "已切到故事画布：左栏是工作树（章节 / 出场 / 资源）";
    }

    private void SetResourceView(ResourceViewMode mode)
    {
        resourceView = mode;
        ResourceSectionLabel.Text = mode == ResourceViewMode.Project ? "项 目 文 件" : "工 作 树 资 源";
        SetButtonActive(ProjectTreeNavButton, mode == ResourceViewMode.Project);
        SetButtonActive(StoryCanvasNavButton, mode == ResourceViewMode.WorkTree);
        RefreshResourceList();
    }

    /// <summary>分段 / 导航按钮的高亮状态：只在这一个地方改 class，避免各处自己拼样式。</summary>
    private static void SetButtonActive(Button button, bool active)
    {
        if (active) { if (!button.Classes.Contains("active")) button.Classes.Add("active"); }
        else button.Classes.Remove("active");
    }

    // ==================== 中央区三种视图：画布 / 时间轴 / 剧本 ====================

    private void CanvasView_OnClick(object? sender, RoutedEventArgs e) => SetCenterView(CenterView.Canvas);
    private void TimelineView_OnClick(object? sender, RoutedEventArgs e) => SetCenterView(CenterView.Timeline);
    private void ScriptView_OnClick(object? sender, RoutedEventArgs e) => SetCenterView(CenterView.Script);

    /// <summary>
    /// 切换中央区显示什么。三种视图读的是同一份画布数据，只是看法不同：
    /// 画布是空间布局，时间轴按章节摊开，剧本把文字按顺序排出来。
    /// </summary>
    private void SetCenterView(CenterView view)
    {
        centerView = view;
        var isCanvas = view == CenterView.Canvas;
        CanvasViewHost.IsVisible = isCanvas;
        CanvasToolbarRow.IsVisible = isCanvas;
        CanvasStageRow.IsVisible = isCanvas;
        TimelineViewHost.IsVisible = view == CenterView.Timeline;
        ScriptViewHost.IsVisible = view == CenterView.Script;
        SetButtonActive(CanvasSegButton, view == CenterView.Canvas);
        SetButtonActive(TimelineSegButton, view == CenterView.Timeline);
        SetButtonActive(ScriptSegButton, view == CenterView.Script);

        if (view == CenterView.Timeline) BuildTimelineView();
        else if (view == CenterView.Script) BuildScriptView();
        else CanvasSurfaceControl.Refresh();

        StatusText.Text = view switch
        {
            CenterView.Timeline => "时间轴：按章节摊开当前画布的分镜与状态",
            CenterView.Script => "剧本：当前画布里的章节与分镜文字",
            _ => "故事画布：中央是无限画布"
        };
    }

    /// <summary>画布数据变了：当前开着时间轴 / 剧本的话，那一屏也要跟着重画。</summary>
    private void RefreshOpenCenterView()
    {
        if (centerView == CenterView.Timeline) BuildTimelineView();
        else if (centerView == CenterView.Script) BuildScriptView();
    }

    /// <summary>
    /// 时间轴：先给一排真实计数（节点 / 章节 / 工作树项 / 分镜 / 成品），再按章节一列列摊开，
    /// 每列列出这一章的分镜节点与状态。计数与列表都从当前画布来，没有数据就说明没有数据。
    /// </summary>
    private void BuildTimelineView()
    {
        TimelineContent.Children.Clear();
        if (currentCanvas is null)
        {
            TimelineContent.Children.Add(Label("还没有画布：先新建或打开一个项目，画布会自动建出来。", 12, "#93A1B3"));
            return;
        }

        var state = currentCanvas.Canvas;
        // 章节列表与归属都走共享的 CanvasChapters：身份是工作树里那些章节条目的 ID，章节名只用来显示。
        // 早先这里是「章节名相同 **或** 锚点指向该章」——按名字绑定正是 C-4 明确禁止的猜法
        // （同名章节会被串在一起、改个显示名就会换章），于是同一个节点在时间轴、画布泳道
        // 与网页端的整理预览里可能落到不同的章。现在三处只有一条规则：稳定 ID。
        var chapters = CanvasChapters.List(state);
        var shots = state.Nodes.Where(node => node.Category == NodeCategory.Storyboard).ToList();
        var products = state.Nodes.Where(node => node.Category == NodeCategory.Product).ToList();

        TimelineContent.Children.Add(Label($"{currentCanvas.Title} · 时间轴", 14, "#E9EFF7", semiBold: true));
        TimelineContent.Children.Add(Label(
            $"节点 {state.Nodes.Count} · 章节 {chapters.Count} · 工作树项 {state.WorkTree.Count} · 分镜节点 {shots.Count} · 成品 {products.Count} · 修订 {currentCanvas.Revision}",
            11, "#93A1B3"));

        var sortedShots = shots.OrderBy(node => node.X).ToList();
        var lane = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var placed = new HashSet<Guid>();

        foreach (var chapter in chapters)
        {
            var chapterNodes = sortedShots.Where(node => CanvasChapters.ResolveChapterId(state, node) == chapter.Id).ToList();
            foreach (var node in chapterNodes) placed.Add(node.Id);

            var card = new Border
            {
                Width = 224,
                Background = Brush("#131B25"),
                BorderBrush = Brush("#2C3D52"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10)
            };
            var column = new StackPanel { Spacing = 6 };
            column.Children.Add(Label(chapter.Name, 12, "#E9EFF7", semiBold: true));
            column.Children.Add(Label($"分镜 {chapterNodes.Count} · 工作树状态 {chapter.Order}", 10, "#5C6A7C"));
            if (chapterNodes.Count == 0) column.Children.Add(Label("这一章还没有分镜节点", 10, "#5C6A7C"));
            foreach (var node in chapterNodes)
                column.Children.Add(Label($"· {node.Title}（{StatusName(node.ExecutionStatus)}）", 10, "#93A1B3"));
            card.Child = column;
            lane.Children.Add(card);
        }

        if (chapters.Count == 0)
            lane.Children.Add(Label("这张画布还没有章节：在工作树里建一个章节，或放一个「章节」节点。", 11, "#5C6A7C"));
        TimelineContent.Children.Add(lane);

        // 只写了章节名、没有稳定锚点的分镜落在这里（诊断会报 CHAPTER_TEXT_WITHOUT_ANCHOR）。
        // 这是如实的「还没归章」，不是把它猜进某一章——猜错了比承认没归章更坏。
        var loose = sortedShots.Where(node => !placed.Contains(node.Id)).ToList();
        if (loose.Count > 0)
        {
            TimelineContent.Children.Add(Label("还没归章的分镜", 12, "#93A1B3", semiBold: true));
            foreach (var node in loose)
                TimelineContent.Children.Add(Label($"· {node.Title}（{StatusName(node.ExecutionStatus)}）{(string.IsNullOrWhiteSpace(node.Content) ? string.Empty : " — " + Shorten(node.Content, 60))}", 11, "#93A1B3"));
        }
    }

    /// <summary>
    /// 剧本：把画布里带文字的节点按章节摊开（章节为空归到「未分章」），按 X 排序就是阅读顺序。
    /// 这里只读不写：要改内容仍走双击画布节点。
    /// </summary>
    private void BuildScriptView()
    {
        ScriptContent.Children.Clear();
        if (currentCanvas is null)
        {
            ScriptContent.Children.Add(Label("还没有画布：先新建或打开一个项目。", 12, "#93A1B3"));
            return;
        }

        var state = currentCanvas.Canvas;
        var chapters = CanvasChapters.List(state);
        var nodes = state.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Content) || !string.IsNullOrWhiteSpace(node.Chapter))
            .ToList();
        if (nodes.Count == 0)
        {
            ScriptContent.Children.Add(Label("这张画布里还没有写内容的节点。", 13, "#E9EFF7", semiBold: true));
            ScriptContent.Children.Add(Label("在画布上双击任意节点填「内容」与「所属章节」，这里就会按章节把文字摊开——现在不摆示例文字。", 11, "#5C6A7C"));
            return;
        }

        ScriptContent.Children.Add(Label($"{currentCanvas.Title} · 剧本", 14, "#E9EFF7", semiBold: true));

        // 分组键是稳定章节 ID，不是章节名：同名章节不会并成一组，改个显示名也不会换组。
        // 组内按 X 排（画布从左到右就是阅读顺序），组间按共享的显式章节顺序——
        // 早先这里按章节名的字符串排序，「第十章」会排到「第二章」前面。
        var byChapter = new Dictionary<Guid, List<WorkflowNode>>();
        var loose = new List<WorkflowNode>();
        foreach (var node in nodes)
        {
            var chapterId = CanvasChapters.ResolveChapterId(state, node);
            if (chapterId is { } id && chapters.Any(chapter => chapter.Id == id))
            {
                if (!byChapter.TryGetValue(id, out var members)) byChapter[id] = members = new List<WorkflowNode>();
                members.Add(node);
            }
            else loose.Add(node);
        }

        void AddEntry(WorkflowNode node)
        {
            var body = string.IsNullOrWhiteSpace(node.Content) ? "（这个节点还没有内容）" : node.Content.Trim();
            ScriptContent.Children.Add(Label($"{node.Title} · {StatusName(node.ExecutionStatus)}", 11, "#5C6A7C"));
            ScriptContent.Children.Add(Label(body, 12, "#D6E2F0", wrap: true));
        }

        foreach (var chapter in chapters)
        {
            if (!byChapter.TryGetValue(chapter.Id, out var members) || members.Count == 0) continue;
            ScriptContent.Children.Add(Label(chapter.Name, 13, "#A7C7EA", semiBold: true));
            foreach (var node in members.OrderBy(item => item.X).ThenBy(item => item.Y)) AddEntry(node);
        }

        if (loose.Count > 0)
        {
            // 只有章节名、没有稳定锚点的节点落在这里：如实说「未分章」。按名字猜进某一章，
            // 猜错了比承认没归章更坏（同名章节会被并到一起，改个显示名还会整批换章）。
            ScriptContent.Children.Add(Label("未分章", 13, "#A7C7EA", semiBold: true));
            foreach (var node in loose.OrderBy(item => item.X).ThenBy(item => item.Y)) AddEntry(node);
        }
    }

    private static TextBlock Label(string text, double size, string color, bool semiBold = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(color),
        TextWrapping = wrap ? global::Avalonia.Media.TextWrapping.Wrap : global::Avalonia.Media.TextWrapping.NoWrap,
        LineHeight = wrap ? 20 : double.NaN,
        FontWeight = semiBold ? FontWeight.SemiBold : FontWeight.Normal
    };

    private static string Shorten(string text, int limit)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }

    private static string StatusName(NodeExecutionStatus status) => CanvasSearch.StatusNameOf(status);
    private void AgentCollab_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowAgentMode();
        StatusText.Text = "已打开 Agent 协作面板";
    }
    // ==================== 制作阶段：按钮与芯片共用一套规则 ====================

    /// <summary>左栏按钮与画布芯片是同一批控件，都按 <see cref="ProductionStageRules.Order"/> 生成。</summary>
    private void Stage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ProductionStage stage) return;
        // 再点一次当前阶段 = 回到全部：不用额外放一个「取消筛选」按钮，也不会误以为卡在筛选里出不来。
        SelectStage(stage == activeStage ? ProductionStage.All : stage);
    }

    private void SelectStage(ProductionStage stage)
    {
        activeStage = stage;
        CanvasSurfaceControl.SetStageFilter(stage);
        RefreshStageUi();
        var canvas = currentCanvas?.Canvas;
        if (canvas is null) return;
        StatusText.Text = stage == ProductionStage.All
            ? $"显示全部阶段：{canvas.Nodes.Count} 个节点"
            : $"只看「{ProductionStageRules.LabelOf(stage)}」（{ProductionStageRules.RuleOf(stage)}）："
              + $"{ProductionStageRules.DescribeCount(stage, ProductionStageRules.Count(canvas, stage))}，再点一次这个阶段回到全部";
    }

    /// <summary>
    /// 重建画布上方那排阶段芯片：计数全部来自当前画布的真实数据，选中态也跟着 activeStage 走。
    /// 以前这里是写死的 ✓ / ● / ○ 与「—」占位，看着像有进度其实什么都没有。
    ///
    /// 左栏曾经还有一套竖排按钮（共用同一份规则与同一个点击处理器），已按用户要求去掉：
    /// 同一件事在芯片上就能做，左栏留着它只是把故事树挤短。
    /// </summary>
    private void RefreshStageUi()
    {
        StageChipPanel.Children.Clear();

        var canvas = currentCanvas?.Canvas;
        if (canvas is null)
        {
            activeStage = ProductionStage.All;
            return;
        }

        foreach (var summary in ProductionStageRules.Summarize(canvas, activeStage))
        {
            var chip = new Button
            {
                Content = $"{summary.Label} · {ProductionStageRules.DescribeCount(summary.Stage, summary.Count)}",
                Tag = summary.Stage
            };
            chip.Classes.Add("chip");
            if (summary.IsActive) chip.Classes.Add("active");
            ToolTip.SetTip(chip, StageTipOf(summary));
            chip.Click += Stage_OnClick;
            StageChipPanel.Children.Add(chip);
        }
    }

    private static string StageTipOf(StageSummary summary) => summary.Stage == ProductionStage.All
        ? $"显示全部节点（{summary.Count} 个）"
        : $"只显示{ProductionStageRules.LabelOf(summary.Stage)}（{ProductionStageRules.RuleOf(summary.Stage)}）："
          + $"{ProductionStageRules.DescribeCount(summary.Stage, summary.Count)}；再点一次回到全部";

    // ==================== 搜索：节点 / 工作树条目 / 设定库 ====================

    private void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RunSearch();
    }

    /// <summary>
    /// 搜索只匹配真实存在的文字（标题 / 正文 / 工作树条目 / 设定名与别名），
    /// 命中的结果点在画布上有对应节点就能直接跳过去。以前这个框什么都不做。
    /// </summary>
    private void RunSearch()
    {
        if (currentCanvas is null)
        {
            StatusText.Text = "先打开一个项目再搜";
            return;
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            StatusText.Text = "输入关键词再回车：搜节点标题与正文、工作树条目、设定库（角色 / 场景 / 道具）";
            return;
        }

        var canvas = currentCanvas.Canvas;
        var hits = CanvasSearch.Find(canvas, query);
        if (hits.Count == 0)
        {
            StatusText.Text = $"没有匹配「{query}」的节点、工作树条目或设定";
            return;
        }

        var menu = new MenuFlyout();
        foreach (var hit in hits)
        {
            var target = hit.NodeId is { } id ? canvas.Nodes.FirstOrDefault(node => node.Id == id) : null;
            var item = new MenuItem { Header = $"{hit.KindName} · {hit.Title}" };
            ToolTip.SetTip(item, $"{hit.Detail}\n{hit.Snippet}");
            if (target is not null)
            {
                var focus = target;
                item.Click += (_, _) => FocusNodeFromSearch(focus);
            }
            else
            {
                // 没有画布节点也要说清它在哪，而不是给一个点不动的死条目。
                var detail = hit.Detail;
                item.Click += (_, _) => StatusText.Text = $"「{hit.Title}」还没有画布节点：{detail}";
            }
            menu.Items.Add(item);
        }

        menu.ShowAt(SearchBox);
        StatusText.Text = $"找到 {hits.Count} 条与「{query}」相关的节点 / 工作树条目 / 设定";
    }

    private void FocusNodeFromSearch(WorkflowNode node)
    {
        CanvasSurfaceControl.FocusNode(node);
        ShowInspectorMode();
        StatusText.Text = $"已定位到「{node.Title}」";
    }

    private void Search_OnClick(object? sender, RoutedEventArgs e) => RunSearch();

    private void Undo_OnClick(object? sender, RoutedEventArgs e) => RestoreSnapshot(undoSnapshots, redoSnapshots);
    private void Redo_OnClick(object? sender, RoutedEventArgs e) => RestoreSnapshot(redoSnapshots, undoSnapshots);
    private void InspectorToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        // 右侧栏只有一格：Agent 面板开着就切回检查器，否则照旧显示/隐藏。
        if (AgentWorkbenchPanel.IsVisible)
        {
            ShowInspectorMode();
            return;
        }
        InspectorPanel.IsVisible = !InspectorPanel.IsVisible;
        StatusText.Text = InspectorPanel.IsVisible ? "检查器已显示" : "检查器已隐藏";
    }
    private void InspectorClose_OnClick(object? sender, RoutedEventArgs e)
    {
        InspectorPanel.IsVisible = false;
        StatusText.Text = "检查器已隐藏";
    }

    /// <summary>把右侧栏切到 Agent 协作面板（点左侧「Agent 协作」导航或检查器里的入口）。</summary>
    internal void ShowAgentMode()
    {
        InspectorPanel.IsVisible = false;
        AgentWorkbenchPanel.IsVisible = true;
        AgentWorkbenchPanel.SyncHostState();
        AgentWorkbenchPanel.FocusInput();
        UpdateAgentBadge();
    }

    /// <summary>把右侧栏切回节点检查器。</summary>
    internal void ShowInspectorMode()
    {
        AgentWorkbenchPanel.IsVisible = false;
        InspectorPanel.IsVisible = true;
        StatusText.Text = "已切回节点检查器";
        // Agent 面板收起来（点「叉」就是走这条路）之后，右下角那枚常驻入口才出现。
        UpdateAgentBadge();
    }

    // ==================== 右下角常驻的 Agent 入口 ====================

    /// <summary>
    /// 右下角那枚 Agent 入口：**面板收起来时才出现**，字母与颜色跟着当前模型的厂家走。
    ///
    /// 为什么要有：Agent 面板点「叉」之后就没有看得见的入口了——只剩右键菜单里那几条
    /// 「交给 Agent」，用户想再打开它得先想起来去哪找。一枚常驻图标把这条路补上。
    ///
    /// 为什么是字母徽标而不是厂家 logo：各家的标识都是注册商标（详见 <see cref="ProviderBadges"/>），
    /// 而「两个字母 + 一个区分色」已经足够回答「现在用的是哪一家」。
    /// </summary>
    private void UpdateAgentBadge()
    {
        var open = AgentWorkbenchPanel.IsVisible;
        AgentBadgeButton.IsVisible = !open;
        // 每次显形都回到半透明：面板是点这枚徽标打开的，那一刻鼠标就停在它上面，
        // PointerExited 不一定会补发，不重置的话它会一直保持实心。
        if (!open) AgentBadgeButton.Opacity = AgentBadgeIdleOpacity;

        var preset = CurrentProviderPreset();
        var badge = ProviderBadges.Of(preset.Id);
        var color = Color.Parse(badge.ColorHex);

        AgentBadgeText.Text = badge.Abbreviation;
        AgentBadgeText.Foreground = new SolidColorBrush(color);
        // 圆环自己不填色：底色压到很淡，只留一圈厂家色的边与一层同色光晕，
        // 这样它在画布、左栏、右栏上面都不会变成一块突兀的色块。
        AgentBadgeRing.Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
        AgentBadgeRing.BorderBrush = new SolidColorBrush(color);
        AgentBadgeRing.BoxShadow = BoxShadows.Parse($"0 0 18 -4 #{color.R:X2}{color.G:X2}{color.B:X2}");
        ToolTip.SetTip(AgentBadgeButton,
            $"打开 Agent 协作 · {ProviderBadges.Describe(preset.Id, preset.Name)}");
    }

    /// <summary>
    /// 当前在用的厂家。反推走的是 <see cref="ProviderPreset.Match"/>（按已保存的地址认），
    /// 与接入引导回显用的是**同一套规则**——否则会出现「设置里说是 DeepSeek、右下角却显示别家」。
    /// 没接入真模型时按「本地模拟」算，与 <see cref="ProviderLabel"/> 的口径一致。
    /// </summary>
    private static ProviderPreset CurrentProviderPreset()
    {
        var config = AiProviderSettings.Load();
        if (config.UseLocalProvider || !config.IsConfigured) return ProviderPreset.Local;
        var profile = AiProviderSettings.EnabledProfiles(config)
            .FirstOrDefault(item => item.Id == config.SelectedProfileId);
        return ProviderPreset.Match(profile?.Endpoint ?? config.Endpoint);
    }

    private void AgentBadge_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowAgentMode();
        StatusText.Text = $"已打开 Agent 协作（{ProviderLabel}）";
    }

    /// <summary>徽标不做悬停时的透明度。放在这里是为了让「设成多少」只有一处。</summary>
    private const double AgentBadgeIdleOpacity = 0.5;

    /// <summary>
    /// 徽标平时是半透明的：它浮在画布右下角，不透明时会实实在在挡住底下的节点。
    /// 鼠标移上来才变实——那时用户已经在看它了，挡住底下无所谓；移开再淡回去。
    /// </summary>
    private void AgentBadge_OnPointerEntered(object? sender, PointerEventArgs e) =>
        AgentBadgeButton.Opacity = 1.0;

    private void AgentBadge_OnPointerExited(object? sender, PointerEventArgs e) =>
        AgentBadgeButton.Opacity = AgentBadgeIdleOpacity;

    private void SelectTool_OnClick(object? sender, RoutedEventArgs e)
    {
        CanvasSurfaceControl.SetConnectionMode(false);
        SetButtonActive(SelectToolButton, true);
        SetButtonActive(ConnectToolButton, false);
        StatusText.Text = "已切换到选择工具（拖节点改位置 / 拖端口连线 / 点线选中后 Delete 删除）";
    }

    /// <summary>
    /// 「连接」是个开关：再点一次就退出。以前只能靠点「选择」退出，
    /// 用户会以为进了就出不来（连接后想取消时尤其明显）。
    /// </summary>
    private void ConnectTool_OnClick(object? sender, RoutedEventArgs e)
    {
        var enable = !CanvasSurfaceControl.IsConnecting;
        CanvasSurfaceControl.SetConnectionMode(enable);
        SetButtonActive(SelectToolButton, !enable);
        SetButtonActive(ConnectToolButton, enable);
        StatusText.Text = enable
            ? "连接模式：先点起点节点、再点终点节点；也可以直接从节点右侧的端口拖到目标节点（Esc 取消）"
            : "已退出连接模式";
    }
    private void ZoomOut_OnClick(object? sender, RoutedEventArgs e) => SetZoom(zoomPercent - 10);
    private void ZoomIn_OnClick(object? sender, RoutedEventArgs e) => SetZoom(zoomPercent + 10);
    private void FitCanvas_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentCanvas?.Canvas.Nodes.Count is not > 0)
        {
            SetZoom(100);
            return;
        }
        CanvasSurfaceControl.FitToContent();
        StatusText.Text = "画布已适配";
    }

    /// <summary>
    /// 无边框窗口四周 / 四角的缩放抓手。
    ///
    /// **为什么不用 BeginResizeDrag（交给系统拖）**：窗口是 <c>SystemDecorations="None"</c>，
    /// 造型上是一个 <c>WS_POPUP</c>、没有 <c>WS_THICKFRAME</c>；系统那套「按在边框上开始缩放」的
    /// 非客户区流程在这种窗口上不成立——按下确实会走到 <c>WM_NCLBUTTONDOWN</c>，但没有可缩放的边框，
    /// 于是什么都不会发生（用户看到的就是「窗口还是拖不动」）。要它生效得把窗口样式改回带粗边框，
    /// 而 <c>WS_THICKFRAME</c> 会带出一条系统画的边框，跟我们自绘的圆角外壳打架。
    /// 所以这里自己算：按下记起点，移动时按**屏幕坐标差**改尺寸与位置。
    ///
    /// 用屏幕坐标而不是窗口内坐标：拖左边 / 上边时窗口自己在动，窗口内坐标会跟着变，
    /// 拿它当基准会越拖越偏（光标跑得比窗口快）。
    /// </summary>
    private void ResizeGrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } grip) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 最大化 / 最小化时没有「边」可拖；先自动还原再拖会更让人困惑，直接不管。
        if (WindowState != WindowState.Normal) return;

        resizeEdge = tag;
        resizeStartPointer = this.PointToScreen(e.GetPosition(this));
        resizeStartPosition = Position;
        resizeStartSize = ClientSize;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void ResizeGrip_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (resizeEdge is not { } edge) return;

        // 位移有两种单位：窗口位置是**物理像素**、宽高是**逻辑像素**，高分屏下两者差一个缩放比，
        // 混用会让窗口按 1/1.5 的速度跑。
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        var current = this.PointToScreen(e.GetPosition(this));
        var dxPhysical = current.X - resizeStartPointer.X;
        var dyPhysical = current.Y - resizeStartPointer.Y;
        var dx = dxPhysical / scaling;
        var dy = dyPhysical / scaling;

        var width = resizeStartSize.Width;
        var height = resizeStartSize.Height;
        var left = resizeStartPosition.X;
        var top = resizeStartPosition.Y;

        if (edge.Contains("East", StringComparison.Ordinal)) width += dx;
        if (edge.Contains("West", StringComparison.Ordinal)) { width -= dx; left = resizeStartPosition.X + dxPhysical; }
        if (edge.Contains("South", StringComparison.Ordinal)) height += dy;
        if (edge.Contains("North", StringComparison.Ordinal)) { height -= dy; top = resizeStartPosition.Y + dyPhysical; }

        // 最小尺寸是布局的硬底线（左右栏固定、中间画布吃剩余空间，再小内容就会互相压）。
        // 从左边 / 上边缩小时，位置要停在「刚好等于最小尺寸」那一格，不能继续跟着鼠标走。
        var minWidth = MinWidth is > 0 and < double.PositiveInfinity ? MinWidth : 640;
        var minHeight = MinHeight is > 0 and < double.PositiveInfinity ? MinHeight : 480;
        if (width < minWidth)
        {
            if (edge.Contains("West", StringComparison.Ordinal))
                left = resizeStartPosition.X + (int)Math.Round((resizeStartSize.Width - minWidth) * scaling);
            width = minWidth;
        }
        if (height < minHeight)
        {
            if (edge.Contains("North", StringComparison.Ordinal))
                top = resizeStartPosition.Y + (int)Math.Round((resizeStartSize.Height - minHeight) * scaling);
            height = minHeight;
        }

        Width = width;
        Height = height;
        if (left != resizeStartPosition.X || top != resizeStartPosition.Y) Position = new PixelPoint(left, top);
        e.Handled = true;
    }

    private void ResizeGrip_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (resizeEdge is null) return;
        resizeEdge = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button)
                return;

            if (visual == TitleBar)
                break;
        }

        if (e.ClickCount >= 2)
        {
            MaximizeWindow_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetZoom(int value)
    {
        zoomPercent = Math.Clamp(value, 20, 200);
        CanvasSurfaceControl.SetScale(zoomPercent / 100d);
        ZoomText.Text = $"{zoomPercent}%";
        StatusText.Text = $"画布缩放：{zoomPercent}%";
    }

    private bool HasOpenProject()
    {
        try
        {
            _ = AppPaths.CurrentProject;
            return true;
        }
        catch (InvalidOperationException)
        {
            StatusText.Text = "请先新建或打开项目";
            return false;
        }
    }

    // ==================== Agent 协作：宿主实现（IAgentSessionHost） ====================
    // 约定：面板只负责对话与审批；凡是碰画布、文件、保存的动作都在这里执行，
    // 于是 Agent 的改动必然经过 TrySaveCanvas 这一条保存入口，没办法绕开保存去改数据。

    public bool IsProjectEditable => canEdit;

    public string ProviderLabel
    {
        get
        {
            var config = AiProviderSettings.Load();
            // 未接入时 AiProviderFactory 会退回本地模拟 Provider：对话仍然可用（它会明说自己没接大模型），
            // 所以这里不能只说「未接入」，得把「能用但没接真模型」这层意思讲清楚。
            if (config.UseLocalProvider || !config.IsConfigured) return "本地模拟 · 点 ⚙ 接入";
            return string.IsNullOrWhiteSpace(config.DisplayName) ? config.Model : config.DisplayName;
        }
    }

    public IReadOnlyList<AgentModelChoice> ModelChoices()
    {
        var config = AiProviderSettings.Load();
        return AiProviderSettings.EnabledProfiles(config)
            .Select(profile => new AgentModelChoice(
                profile.Id,
                profile.Label,
                profile.Id == config.SelectedProfileId,
                profile.IsConfigured))
            .ToList();
    }

    public string? SelectModel(string profileId)
    {
        // 每次都重新读盘：配置可能刚在设置窗口里改过，用内存里的旧副本会把那次改动覆盖掉。
        var config = AiProviderSettings.Load();
        var target = config.Profiles?.FirstOrDefault(item => item.Id == profileId);
        if (target is null) return "找不到这份配置（可能已被删除）。";
        if (!target.Enabled) return "这份配置已被停用，请先在「设置 → 模型接入」里启用它。";
        if (!target.IsConfigured) return $"「{target.Label}」还没填全接口地址与模型，切过去就没法对话了。";

        AiProviderSettings.ApplyProfile(config, target);
        if (!AiProviderSettings.Save(config))
            return $"切换没能写入配置文件（{AiProviderSettings.ConfigFilePath}）。";

        StatusText.Text = $"已切换到「{target.Label}」，下一轮对话起生效";
        AgentWorkbenchPanel.SyncHostState();
        return null;
    }

    public IAiChatProvider? CreateProvider() => AiProviderFactory.Create() as IAiChatProvider;

    public bool ImageInputEnabled => AiProviderSettings.Load().SupportsImageInput;

    public int ContextCharacterBudget => AiProviderSettings.Load().ContextCharacterBudget;

    public int ContextWindowTokens => AiProviderSettings.Load().ContextWindow;

    public string? WorkspacePath
    {
        get
        {
            var workspace = AiProviderSettings.Load().AgentWorkspace;
            return string.IsNullOrWhiteSpace(workspace) ? null : workspace;
        }
    }

    /// <summary>组装上下文：选中节点 + 画布节点清单 + 设定库 + 工作树 + 工作文件夹。</summary>
    public AgentContext BuildContext()
    {
        var selected = CanvasSurfaceControl.SelectedNode;
        if (currentCanvas?.Canvas is not { } state)
            return new AgentContext(string.Empty, string.Empty, string.Empty, string.Empty, DescribeWorkspace(), string.Empty, string.Empty);

        var nodes = string.Join("\n", state.Nodes.Select(node =>
        {
            var content = node.Content.Replace('\n', ' ').Replace('\r', ' ');
            if (content.Length > 180) content = content[..180] + "…";
            return $"- {node.Id.ToString("N")[..8]} | {node.Title} | {node.ExecutionStatus} | {content}";
        }));
        var library = string.Join("；", state.Entities.Select(entity =>
            $"{EntityKindLabel(entity.Kind)}{entity.Name}（{entity.Variants.Count} 个变体）"));
        return new AgentContext(
            selected?.Title ?? string.Empty,
            selected?.Content ?? string.Empty,
            nodes,
            library,
            DescribeWorkspace(),
            string.Empty,
            DescribeWorkTree(state));
    }

    /// <summary>工作文件夹清单：write_file 只能落在这个目录内，所以必须让模型知道里面已经有什么。</summary>
    private static string DescribeWorkspace()
    {
        var workspace = AiProviderSettings.Load().AgentWorkspace;
        if (string.IsNullOrWhiteSpace(workspace)) return "（未设置：写文件动作会被拒绝）";
        if (!Directory.Exists(workspace)) return $"{workspace}（目录不存在）";
        try
        {
            var files = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
                .Take(60)
                .Select(path => Path.GetRelativePath(workspace, path))
                .ToList();
            return files.Count == 0 ? $"{workspace}（目录为空）" : $"{workspace}：{string.Join("、", files)}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{workspace}（无法列出：{error.Message}）";
        }
    }

    private static string DescribeWorkTree(WorkflowCanvasState state)
    {
        if (state.WorkTree.Count == 0) return "（尚未建立工作树）";
        var lines = new List<string>();
        void Add(Guid? parentId, int depth)
        {
            foreach (var item in state.WorkTree.Where(candidate => candidate.ParentId == parentId))
            {
                lines.Add($"{new string(' ', depth * 2)}- {item.Id.ToString("N")[..8]} | {WorkTreeItem.KindName(item.Kind)} | {item.Name} | {item.Version} | {item.Chapter} | 叙事设定：{item.Prompt}");
                Add(item.Id, depth + 1);
            }
        }
        Add(null, 0);
        return lines.Count == 0 ? "（工作树里还没有条目）" : string.Join("\n", lines);
    }

    public IReadOnlyList<string?> Precheck(IReadOnlyList<AgentAction> actions) =>
        currentCanvas is null
            ? Array.Empty<string?>()
            : AgentActionExecutor.PrecheckBatch(actions, currentCanvas.Canvas, WorkspacePath, currentCanvasPath);

    public CanvasPreview Preview(IReadOnlyList<AgentAction> actions) =>
        currentCanvas is null ? new CanvasPreview() : CanvasPreviewBuilder.Build(actions, currentCanvas.Canvas);

    /// <summary>待审批改动的画布虚影：只画不写，画布数据一个字节都不动。</summary>
    public void ShowPendingPreview(IReadOnlyList<AgentAction> actions)
    {
        if (currentCanvas?.Canvas is not { } canvas || actions.Count == 0)
        {
            CanvasSurfaceControl.SetPendingPreview(null);
            return;
        }
        CanvasSurfaceControl.SetPendingPreview(CanvasPreviewBuilder.Build(actions, canvas));
    }

    public void ClearPendingPreview() => CanvasSurfaceControl.SetPendingPreview(null);

    /// <summary>
    /// 把一批动作应用到画布并保存。分三段如实交代：应用 → 写盘 → 个别动作未生效。
    /// 写盘失败时整批回滚（画布与文件一起），绝不留「改了内存但没落盘」的中间态。
    /// </summary>
    public AgentCommitReport Commit(IReadOnlyList<AgentAction> actions)
    {
        if (actions.Count == 0) return new AgentCommitReport(0, "这批改动是空的。", Array.Empty<string>());
        if (referenceCanvas is not null)
            return new AgentCommitReport(0, "这里是临时引用画布：它不是一张会落盘的画布，Agent 的改动没有落点。先点标签条上的「返回原画布」，再让 Agent 改。", Array.Empty<string>());
        if (currentCanvas is null || string.IsNullOrWhiteSpace(currentCanvasPath))
            return new AgentCommitReport(0, "还没有打开画布：先新建或载入一张画布，Agent 才能把改动落到画布上。", Array.Empty<string>());
        if (!canEdit)
            return new AgentCommitReport(0, "项目或项目库不可写（当前为只读），这批改动没有写入。", Array.Empty<string>());

        var canvasPath = currentCanvasPath;
        var canvas = currentCanvas.Canvas;
        var before = JsonSerializer.Serialize(canvas, SnapshotOptions);
        var fileSnapshots = new List<FileSnapshot>();
        var result = AgentActionExecutor.Apply(actions, canvas, WorkspacePath, FileWriteMode.Apply, fileSnapshots, canvasPath);

        if (result.Applied == 0)
        {
            // 一条都没成功：执行器已逐条回滚过画布，这里把文件也还原，再如实报错。
            RestoreCanvas(before, fileSnapshots);
            return new AgentCommitReport(0,
                result.Errors.Count > 0 ? string.Join("；", result.Errors) : "这批动作没有产生任何可应用的改动。",
                result.Errors);
        }

        if (!TrySaveCanvas(out var saveMessage))
        {
            RestoreCanvas(before, fileSnapshots);
            return new AgentCommitReport(0, $"{saveMessage}；这批改动已整体回滚", result.Errors);
        }

        undoSnapshots.Push(before);
        redoSnapshots.Clear();
        lastAgentCommit = new AgentCommitPoint(canvasPath, before, fileSnapshots, result.Applied, result.Errors);
        RefreshCanvasSurface();
        return new AgentCommitReport(result.Applied, null, result.Errors);
    }

    /// <summary>
    /// 有没有「可以撤销的 Agent 批次」。必须同时满足「当前画布就是这批改动所在的画布」：
    /// 加了画布标签之后，切到别的画布还显示可撤销，一点就报「不是这批改动所在的画布」，是拿用户当测试。
    /// 切回原画布时按钮会自己回来（撤销记录没有被丢掉）。
    /// </summary>
    public bool CanUndoLastCommit =>
        lastAgentCommit is { } commit && string.Equals(commit.CanvasPath, currentCanvasPath, StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// 撤销上一批 Agent 改动：画布与文件一起回滚，并把回滚结果写回画布文件。
    /// 只允许撤销**同一张画布**上的批次——否则会把 A 画布的快照套到 B 上。
    /// </summary>
    public string? UndoLastCommit()
    {
        if (lastAgentCommit is not { } commit) return "没有可撤销的 Agent 批次。";
        if (currentCanvas is null) return "画布已关闭，无法撤销。";
        if (!string.Equals(commit.CanvasPath, currentCanvasPath, StringComparison.OrdinalIgnoreCase))
            return "当前画布不是这批改动所在的画布，已拒绝撤销。";
        if (!canEdit) return "项目只读，无法把撤销结果写回。";

        var failures = RestoreCanvas(commit.CanvasSnapshot, commit.FileSnapshots);
        lastAgentCommit = null;
        var saved = TrySaveCanvas(out var saveMessage);
        RefreshCanvasSurface();
        var restoreNote = failures.Count == 0 ? string.Empty : $"；有 {failures.Count} 个文件没恢复：{string.Join("；", failures)}";
        return saved ? (failures.Count == 0 ? null : $"画布已撤销{restoreNote}") : $"画布已在内存里回滚，但写回失败：{saveMessage}";
    }

    /// <summary>把画布恢复到指定快照，并按快照恢复被 Agent 改写过的文件。返回未恢复成功的文件说明。</summary>
    private IReadOnlyList<string> RestoreCanvas(string snapshot, IReadOnlyList<FileSnapshot> fileSnapshots)
    {
        if (currentCanvas is not null
            && JsonSerializer.Deserialize<WorkflowCanvasState>(snapshot, SnapshotOptions) is { } restored)
        {
            currentCanvas = currentCanvas with { Canvas = restored };
            CanvasSurfaceControl.State = restored;
            RefreshResourceList();
        }
        return fileSnapshots.Count == 0 ? Array.Empty<string>() : FileSnapshot.RestoreAll(fileSnapshots);
    }

    public void RefreshCanvasSurface()
    {
        CanvasSurfaceControl.Refresh();
        RefreshResourceList();
        RefreshOpenCenterView();
        if (!string.IsNullOrWhiteSpace(currentCanvasPath)) UpdateCanvasUi(currentCanvasPath);
    }

    public async Task<bool> OpenModelSettingsAsync()
    {
        if (!await SettingsWindow.ShowAsync(this, Collaboration(), initialPage: 0)) return false;
        // 设置里可能刚登录、或刚改「改完自动同步」：回来重读一遍开关，订阅与自动同步据此起停。
        Collaboration();
        RefreshAutoSyncSetting();
        StatusText.Text = "模型配置已保存，下一轮对话生效";
        // 这一页里也可能改了「出图观感」那个开关（它跟模型配置同在一个窗口），重新取一次并重画。
        RefreshRevealPreferences();
        CanvasSurfaceControl.Refresh();
        AgentWorkbenchPanel.SyncHostState();
        return true;
    }

    /// <summary>
    /// 左下角「设置」：模型接入 / 生图生视频 / 技能管理 / 协作四页。
    ///
    /// 用同一个窗口、同一条落盘路径（<see cref="SettingsWindow"/>）打开，不再每类配置各开一扇门：
    /// 用户问「配个出图接口该去哪」时，答案应该是一个地方，而不是「先点 Agent 面板的 ⚙，但那是聊天模型的设置」。
    /// </summary>
    private async void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await SettingsWindow.ShowAsync(this, Collaboration())) return;
        // 设置里可能刚登录、或刚改「改完自动同步」：回来重读一遍开关，订阅与自动同步据此起停。
        Collaboration();
        RefreshAutoSyncSetting();
        StatusText.Text = "设置已保存";
        // 「出图观感」那个开关就在这个窗口里：改了它要立刻反映到画布上那排候选卡的画法。
        RefreshRevealPreferences();
        CanvasSurfaceControl.Refresh();
        // 模型 / 密钥可能变了：面板头部的「当前模型」与后续请求都要跟着换。
        AgentWorkbenchPanel.SyncHostState();
    }

    /// <summary>面板上的「接入引导」：重新走一遍选服务商 → 密钥 → 测试连接。</summary>
    public async Task<bool> RunOnboardingAsync()
    {
        var outcome = await AgentOnboardingDialog.ShowAsync(this, firstRun: false);
        if (outcome is null) return false;
        SyncProviderOutcome(outcome);
        return true;
    }
}
