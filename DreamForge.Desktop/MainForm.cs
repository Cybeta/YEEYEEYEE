using System.Diagnostics;
using System.Text.Json;
using DreamForge.Core;
using DreamForge.Host;

namespace DreamForge.Desktop;

public sealed class MainForm : ScaledForm, IPluginHost
{
    private SingleMachineExecutionService execution;
    private DesktopExecutionHost? executionHost;
    private readonly SessionContext session;
    private readonly TextBox promptBox = new();
    private readonly TextBox negativePromptBox = new();
    private readonly NumericUpDown widthBox = new();
    private readonly NumericUpDown heightBox = new();
    private readonly NumericUpDown stepsBox = new();
    private readonly NumericUpDown cfgBox = new();
    private readonly TextBox seedBox = new();
    private readonly Button submitButton = new();
    private readonly Button cancelButton = new();
    private readonly Label statusLabel = new();
    private readonly Label jobLabel = new();
    private readonly Label queueLabel = new();
    private readonly Label timeLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly ListView jobList = new();
    private readonly ListView canvasList = new();
    private readonly Label canvasLibraryHint = new();
    private readonly Label canvasTitleLabel = new();
    private readonly Label projectLabel = new();
    private readonly Label canvasRevisionLabel = new();
    private readonly WorkflowCanvasControl canvas = new();
    private readonly TextBox nodeTitleBox = new();
    private readonly TextBox nodeChapterBox = new();
    private readonly Label nodeVersionStatusLabel = new();
    private readonly Button adoptEffectiveVersionButton = new();
    private readonly Button keepHistoricalVersionButton = new();
    private readonly Button compareVersionsButton = new();
    private readonly Label attachmentLabel = new();
    private readonly ListView attachmentList = new();
    private readonly Button removeAttachmentButton = new();
    private readonly Label referenceLabel = new();
    private readonly Button nodeLockButton = new();
    private readonly ListView entityList = new();
    private readonly ListView variantList = new();
    private readonly ListView pluginList = new();
    private readonly Label pluginHint = new();
    private readonly TextBox nodeContentBox = new();
    private readonly TextBox nodeAnswerBox = new();
    private readonly ComboBox contentSourceBox = new();
    private readonly ComboBox nodeCategoryBox = new();
    private readonly Label nodeStatusLabel = new();
    private readonly Label selectionLabel = new();
    private readonly Label providerLabel = new();
    private readonly Label pendingLabel = new();
    private readonly Button undoAgentButton = new();
    private readonly Button themeButton = new();
    private readonly Label statusBarText = new();
    private Control brandLabel = new();
    private TableLayoutPanel? titleBarPanel;
    private TableLayoutPanel? statusBarPanel;
    private RailStrip? railStrip;
    private FlowLayoutPanel? railFlow;
    private readonly System.Windows.Forms.Timer railSlide = new() { Interval = 30 };

    /// <summary>活动栏收起 / 展开的宽度：收起时正好是表格里预留的那条窄边，不遮内容。</summary>
    private const int RailCollapsedWidth = 10;
    private const int RailExpandedWidth = 128;

    private TableLayoutPanel? workspaceRoot;
    private readonly Panel drawer = new();
    private readonly Panel agentDrawer = new();
    private Panel? agentPaneShell;
    private AgentPane? agentPaneControl;
    private FlowLayoutPanel? canvasTabStrip;
    private readonly List<CanvasTabState> canvasTabs = new();
    private CanvasTabState? activeCanvasTab;
    private bool switchingCanvasTab;

    private sealed class CanvasTabState
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Title { get; set; } = "未命名画布";
        public string? Path { get; set; }
        public RecentCanvasState Snapshot { get; set; } = EmptyCanvasState();
        public Panel? TabControl { get; set; }
        public Label? TitleControl { get; set; }
        public Panel? Underline { get; set; }
        public Button? CloseButton { get; set; }
    }

    private sealed class CanvasTabsFile
    {
        public Guid ActiveTabId { get; set; }
        public List<CanvasTabFileItem> Tabs { get; set; } = new();
    }

    private sealed class CanvasTabFileItem
    {
        public Guid Id { get; set; }
        public string? Path { get; set; }
        public RecentCanvasState? Snapshot { get; set; }
    }

    private const string CanvasTabsFileName = "canvas-tabs.json";

    private static RecentCanvasState EmptyCanvasState() =>
        new("未命名画布", 0, string.Empty, string.Empty, 1024, 1024, 28, 7, "随机", new WorkflowCanvasState());

    /// <summary>待提交的 Agent 改动：画布数据在用户保存前不受影响。</summary>
    private readonly PendingChanges pendingChanges = new();

    /// <summary>最近一次 Agent 提交的快照，用于「撤销上次提交」。</summary>
    private AgentCommitRecord? lastAgentCommit;
    private readonly Dictionary<string, Button> railButtons = new();
    private readonly Dictionary<string, Control> panes = new();
    private TreeView? workTreeView;
    private Label? workTreeMetaLabel;
    private TextBox? workTreePromptBox;
    private ListView? workTreeAssetList;
    private readonly ToolTip railTip = new();
    private string? activePaneKey;

    private sealed record PluginMenuItem(string Text, Func<ContextInfo, Task> Handler);
    private sealed record PluginRailItem(string Key, string Glyph, string Title, Func<Control> Factory);

    private readonly Dictionary<ContextTarget, List<PluginMenuItem>> pluginMenuItems = new();
    private readonly List<PluginRailItem> pluginRailItems = new();
    private readonly Dictionary<string, (string Title, Func<Form> Factory)> pluginWindows = new();
    private readonly List<LoadedPlugin> loadedPlugins = new();
    private readonly List<string> pluginErrors = new();
    private int pluginRailSequence;
    private IAiProvider aiProvider = AiProviderFactory.Create();
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private Guid? currentJobId;
    private DateTimeOffset? currentJobStarted;
    private int canvasRevision;
    private string canvasTitle = "未命名画布";
    private string? currentCanvasPath;
    private string? executionStoreWarning;
    private bool restartForProjectSelection;
    public bool RestartForProjectSelection => restartForProjectSelection;
    private static readonly HttpClient canvasPushClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private const string CanvasWebBaseUrl = "http://localhost:5000";
    private bool resourceReplacePollInFlight;
    private readonly Dictionary<Guid, ResourceReplaceOutcome> resourceReplaceOutcomes = new();

    public MainForm(SingleMachineExecutionService? execution = null)
    {
        if (execution is null)
        {
            executionHost = DesktopExecutionHost.Create();
            this.execution = executionHost.Execution;
            executionStoreWarning = executionHost.StoreWarning;
        }
        else
        {
            this.execution = execution;
        }
        session = new SessionContext { SessionId = Guid.NewGuid(), UserId = ImageProviderFactory.DesktopUserId, ClientType = ClientType.Desktop, Role = MemberRole.Member, ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
        // 先定主题再建界面：控件在构造时就读 Theme 的配色，晚了会先闪一帧浅色。
        ApplyStoredTheme();
        Text = "YeeYeeYee"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(1100, 700); Size = new Size(1360, 820); BackColor = Theme.ChromeBg; Font = Theme.UiFont;
        Theme.Changed += ApplyTheme;
        SkillLibrary.EnsureDefaultSkills();
        RegisterBuiltInPlugins();
        LoadExternalPlugins();
        BuildLayout(); LoadInitialCanvas(); this.execution.Updated += OnExecutionUpdated; refreshTimer.Interval = 500; refreshTimer.Tick += (_, _) => { RefreshJobList(); PollResourceReplaceRequests(); }; refreshTimer.Start();
        // 骨架建完立刻套一次主题：各面板里的列表、输入框、按钮由这一遍统一成同一套配色。
        ApplyTheme();
        // 进入画布后默认展开 Agent 面板；用户仍可通过面板右上角关闭按钮收起。
        // 首次启动必须让用户明确选择「接入真实模型」或「本地模拟」，不静默降级。
        Shown += (_, _) =>
        {
            if (!agentDrawer.Visible) ToggleAgentPane();
            RunStartupOnboarding();
        };
        // 有已应用但未保存的 Agent 改动时不能悄悄关掉：让用户明确选择保存还是撤销。
        FormClosing += (_, e) =>
        {
            if (pendingChanges.IsEmpty) return;
            var choice = MessageBox.Show(
                $"有 {pendingChanges.Count} 条 Agent 改动已应用但尚未保存。\n\n" +
                "「是」保存　「否」撤销这些改动　「取消」返回继续编辑",
                "Agent 改动", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) { e.Cancel = true; return; }
            if (choice == DialogResult.Yes) SaveAppliedAgentChanges();
            else UndoLastAgentCommit();
        };
        FormClosed += (_, _) => { refreshTimer.Stop(); refreshTimer.Dispose(); this.execution.Updated -= OnExecutionUpdated; autoExpandCancellation?.Cancel(); SaveAllCanvasTabs(); if (executionHost is not null) _ = executionHost.DisposeAsync(); }; RefreshJobList();
    }

    // ---------- 插件宿主 ----------

    // ---------- 接入大模型 ----------

    /// <summary>
    /// 启动接入检查：用户还没做过服务商选择时弹出接入引导。
    /// 允许明确选择本地模拟，但不允许在不知情的情况下一直用占位结果。
    /// </summary>
    private void RunStartupOnboarding()
    {
        if (AiProviderSettings.Load().ProviderChoiceMade) return;
        var outcome = AiSetupDialog.Show(this, firstRun: true);
        if (outcome is null)
        {
            MessageBox.Show(
                "还没有接入大模型。AI 生成与对话会使用本地模拟的占位结果。\n\n随时可以点顶栏右侧的模型状态或左侧「设」重新接入。",
                "未接入大模型", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplyProviderChange();
        if (outcome.Saved) MessageBox.Show($"已接入 {AiProviderSettings.Load().Model}。", "接入大模型");
    }

    /// <summary>接入或设置变化后重建 Provider 与执行宿主，并刷新顶栏与 Agent 面板状态。</summary>
    private void ApplyProviderChange()
    {
        aiProvider = AiProviderFactory.Create();
        RefreshProviderLabel();
        canvas.InvalidateThumbnails();
        RebuildExecutionHostIfNeeded();
        agentPaneControl?.RefreshState();
    }

    /// <summary>顶栏点模型状态时重新打开接入引导。</summary>
    private void ReconfigureProvider()
    {
        if (AiSetupDialog.Show(this, firstRun: false) is null) return;
        ApplyProviderChange();
    }

    /// <summary>当前文本模型的显示名，用于顶栏状态与 Agent 面板；设了展示名称就优先用它。</summary>
    private static string CurrentProviderLabel()
    {
        var config = AiProviderSettings.Load();
        if (config.UseLocalProvider || !config.IsConfigured) return "本地模拟（未接入大模型）";
        // 密钥解不开时请求必然鉴权失败，顶栏直接说明，别让用户以为是模型或网络的问题。
        if (config.ApiKeyUnreadable) return "密钥无法解密（点击重新接入）";
        return string.IsNullOrWhiteSpace(config.DisplayName) ? config.Model : config.DisplayName;
    }

    /// <summary>
    /// 显示一个属于主窗口的非模态子窗体，并显式前置。
    /// WinForms 里非模态子窗体若不前置，容易被主窗口（尤其是自绘画布）盖住。
    /// </summary>
    private void ShowOwnedForm(Form form)
    {
        form.Show(this);
        form.BringToFront();
        form.Activate();
    }

    /// <summary>显示一个等待/进度小窗，同样确保它落在主窗口之上。</summary>
    private void ShowWaitingForm(Form form, IWin32Window? owner)
    {
        form.Show(owner ?? this);
        form.BringToFront();
        form.Activate();
    }

    /// <summary>
    /// Agent 面板的上下文：选中节点 + 画布节点清单（含短 id，供模型精确指定 target）
    /// + 设定库摘要 + 工作文件夹文件清单。
    /// </summary>
    private AgentContext BuildAgentContext()
    {
        // Agent 读取真实画布叠加当前虚影试算结果，连续生成时能看到尚未审批的节点。
        var effective = pendingChanges.IsEmpty
            ? canvas.State
            : CanvasPreviewBuilder.Clone(canvas.State);
        if (!pendingChanges.IsEmpty)
            AgentActionExecutor.Apply(pendingChanges.Actions, effective, null, FileWriteMode.Skip);

        var selectedTitle = canvas.SelectedNode?.Title ?? string.Empty;
        var node = !string.IsNullOrWhiteSpace(selectedTitle)
            ? effective.Nodes.FirstOrDefault(item =>
                string.Equals(item.Title, selectedTitle, StringComparison.OrdinalIgnoreCase))
            : null;
        var nodes = string.Join("\n", effective.Nodes.Select(item =>
        {
            var content = item.Content.Replace('\n', ' ').Replace('\r', ' ');
            if (content.Length > 180) content = content[..180] + "…";
            return $"- {item.Id.ToString("N")[..8]} | {item.Title} | {item.ExecutionStatus} | {content}";
        }));
        var library = string.Join("；", effective.Entities.Select(entity =>
            $"{WorkflowEntity.KindName(entity.Kind)}{entity.Name}（{entity.Variants.Count} 个变体）"));
        var pending = pendingChanges.IsEmpty
            ? string.Empty
            : string.Join("\n", pendingChanges.Describe().Select(item => "- " + item));
        var versionNote = pendingChanges.IsEmpty
            ? string.Empty
            : "当前上下文包含已经显示在画布上但尚未保存的 Agent 改动；请在此基础上继续生成，不要重复创建已有节点。";
        return new AgentContext(
            node?.Title ?? selectedTitle,
            node?.Content ?? string.Empty,
            nodes,
            library,
            BuildWorkspaceSummary(),
            string.IsNullOrWhiteSpace(pending) ? versionNote : $"{versionNote}\n{pending}",
            DescribeWorkTree());
    }

    private string DescribeWorkTreeVersionRelation(WorkTreeItem item)
    {
        if (item.SupersedesVersionId is not { } previousId)
            return IsCurrentWorkTreeVersion(item) ? "当前版本" : "旧版本";

        var entity = item.SourceEntityId is { } entityId
            ? canvas.State.Entities.FirstOrDefault(candidate => candidate.Id == entityId)
            : null;
        var variant = entity is null || item.SourceVariantId is not { } variantId
            ? null
            : entity.Variants.FirstOrDefault(candidate => candidate.Id == variantId);
        var previous = variant?.Versions.FirstOrDefault(candidate => candidate.Id == previousId);
        var relation = previous is null ? "升级关系未知" : $"继承自 {previous.Label}";
        return $"{relation}；{(IsCurrentWorkTreeVersion(item) ? "当前版本" : "旧版本")}";
    }

    private string DescribeWorkTree()
    {
        if (canvas.State.WorkTree.Count == 0) return "（尚未建立工作树）";
        var lines = new List<string>();
        void Add(Guid? parentId, int depth)
        {
            foreach (var item in canvas.State.WorkTree.Where(candidate => candidate.ParentId == parentId))
            {
                var resources = string.Join("；", new[] { AttachmentKind.Image, AttachmentKind.Video }.Select(kind => $"{WorkflowAttachment.DisplayName(kind)}：{(item.Attachments.Any(attachment => attachment.Kind == kind) ? item.Attachments.Count(attachment => attachment.Kind == kind) + " 个" : "空")}"));
                var relation = DescribeWorkTreeVersionRelation(item);
                lines.Add($"{new string(' ', depth * 2)}- {item.Id.ToString("N")[..8]} | {WorkTreeItem.KindName(item.Kind)} | {item.Name} | {item.Version} | {item.Chapter} | {relation} | 叙事设定：{item.Prompt} | {resources}");
                Add(item.Id, depth + 1);
            }
        }
        Add(null, 0);
        return string.Join("\n", lines);
    }

    /// <summary>工作文件夹摘要：路径 + 顶层文件清单，让模型知道有哪些参考资料可用。</summary>
    private static string BuildWorkspaceSummary()
    {
        var path = AiProviderSettings.Load().AgentWorkspace;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return string.Empty;
        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(20)
                .ToList();
            return files.Count == 0 ? $"{path}（目录为空）" : $"{path}\n  可用文件：" + string.Join("、", files);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>把 Agent 的回复采纳为选中节点的内容，并记入生成历史。</summary>
    private void AcceptAgentReply(string reply)
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先在画布上选择一个节点。", "采纳到节点"); return; }
        if (RefuseWhenLocked(node, "写入内容")) return;
        var input = node.Content;
        node.Content = reply;
        node.ContentSource = ContentSource.Ai;
        node.ExecutionStatus = NodeExecutionStatus.Draft;
        node.GenerationId = Guid.NewGuid();
        node.GenerationHistory.Add(new GenerationHistory
        {
            Input = input,
            Instruction = "Agent 对话",
            Output = reply,
            Provider = "Agent",
            Model = AiProviderSettings.Load().Model,
            Accepted = true,
            Status = node.ExecutionStatus
        });
        canvas.NotifyContentChanged();
        RefreshNodeInspector();
        canvas.Invalidate();
        MessageBox.Show($"已写入节点「{node.Title}」。", "采纳到节点");
    }

    /// <summary>内置插件：演示扩展点用法，把三视图技能挂到变体右键菜单上。</summary>
    private void RegisterBuiltInPlugins()
    {
        try { new ThreeViewMenuPlugin().Register(this); }
        catch (Exception error) { pluginErrors.Add($"内置插件注册失败：{error.Message}"); }
    }

    private void LoadExternalPlugins()
    {
        try { PluginLoader.EnsureDirectory(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            pluginErrors.Add($"插件目录不可用：{error.Message}");
            return;
        }
        foreach (var plugin in PluginLoader.LoadAll())
        {
            if (plugin.Instance is null) { loadedPlugins.Add(plugin); continue; }
            try
            {
                plugin.Instance.Register(this);
                loadedPlugins.Add(plugin);
            }
            catch (Exception error)
            {
                // 单个插件注册失败只影响它自己，宿主与其它插件继续工作。
                loadedPlugins.Add(plugin with { Instance = null, Error = $"注册扩展点时出错：{error.Message}" });
            }
        }
    }

    public void AddContextMenuItem(ContextTarget target, string text, Func<ContextInfo, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(text) || handler is null) return;
        if (!pluginMenuItems.TryGetValue(target, out var items)) pluginMenuItems[target] = items = new List<PluginMenuItem>();
        items.Add(new PluginMenuItem(text, handler));
    }

    public void AddRailItem(string glyph, string title, Func<Control> paneFactory)
    {
        if (paneFactory is null) return;
        var key = $"plugin:{pluginRailSequence++}";
        pluginRailItems.Add(new PluginRailItem(key, string.IsNullOrWhiteSpace(glyph) ? "插" : glyph, title, paneFactory));
    }

    public void RegisterWindow(string id, string title, Func<Form> factory)
    {
        if (string.IsNullOrWhiteSpace(id) || factory is null) return;
        pluginWindows[id] = (string.IsNullOrWhiteSpace(title) ? id : title, factory);
    }

    public void OpenWindow(string id)
    {
        if (!pluginWindows.TryGetValue(id, out var window))
        {
            MessageBox.Show($"没有注册名为 {id} 的插件窗口。", "插件");
            return;
        }
        try { ShowOwnedForm(window.Factory()); }
        catch (Exception error) { MessageBox.Show($"打开插件窗口失败：{error.Message}", "插件", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public Task<SkillRunResult> RunSkillAsync(string skillId, SkillRunRequest request)
    {
        if (request.Entity is not { } entity || request.Variant is not { } variant)
            return Task.FromResult(new SkillRunResult { Succeeded = false, Message = "插件没有提供要运行的实体与变体。" });
        SkillLibrary.EnsureDefaultSkills();
        var (skills, _) = SkillLibrary.Load();
        var skill = skills.FirstOrDefault(candidate => string.Equals(candidate.Id, skillId, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            var missing = new SkillRunResult { Succeeded = false, Message = $"找不到技能 {skillId}。技能目录：{SkillLibrary.Directory}" };
            MessageBox.Show(missing.Message, "运行技能");
            return Task.FromResult(missing);
        }
        if (skill.Validate(entity) is { } problem)
        {
            MessageBox.Show(problem, "运行技能");
            return Task.FromResult(new SkillRunResult { Succeeded = false, Message = problem });
        }
        return ExecuteSkillWithProgressAsync(skill, new SkillTarget { Entity = entity, Variant = variant, Node = request.Node });
    }

    public WorkflowCanvasState? CurrentCanvas => canvas.State;

    public void RefreshCanvas()
    {
        canvas.InvalidateThumbnails();
        RefreshEntityList();
        RefreshNodeInspector();
        canvas.Invalidate();
    }

    /// <summary>按注册表构造某个界面位置上的插件菜单项；没有注册项时给出占位项。</summary>
    private List<ToolStripItem> BuildPluginMenuItems(ContextTarget target, ContextInfo info)
    {
        var items = new List<ToolStripItem>();
        if (pluginMenuItems.TryGetValue(target, out var registered))
            foreach (var entry in registered)
            {
                var handler = entry.Handler;
                var item = new ToolStripMenuItem(entry.Text);
                item.Click += async (_, _) =>
                {
                    try { await handler(info); }
                    catch (Exception error) { MessageBox.Show(error.Message, "插件执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                items.Add(item);
            }
        if (items.Count == 0) items.Add(new ToolStripMenuItem("（没有可用的插件操作）") { Enabled = false });
        return items;
    }

    private void BuildLayout()
    {
        panes["library"] = BuildCanvasLibraryPane();
        panes["entities"] = BuildEntityLibraryPane();
        panes["worktree"] = BuildWorkTreePane();
        panes["node"] = BuildNodePane();
        panes["tasks"] = BuildTaskPane();
        panes["plugins"] = BuildPluginPane();

        // 三段式骨架：整宽标题栏 → 活动栏 + 主体 → 整宽状态栏。
        // 活动栏是悬浮的滑出条，所以这里只给它留一条收起时的窄边。
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty, BackColor = Theme.ChromeBg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, RailCollapsedWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var titleBar = BuildTitleBar();
        root.Controls.Add(titleBar, 0, 0);
        root.SetColumnSpan(titleBar, 2);
        root.Controls.Add(BuildContent(), 1, 1);
        var statusBar = BuildStatusBar();
        root.Controls.Add(statusBar, 0, 2);
        root.SetColumnSpan(statusBar, 2);
        Controls.Add(root);

        // 活动栏浮在内容之上：鼠标靠过去滑出，离开自动收回，画布不会被挤来挤去。
        var rail = BuildRailStrip();
        Controls.Add(rail);
        rail.BringToFront();
        LayoutRail();
        Resize += (_, _) => LayoutRail();
    }

    /// <summary>设计时像素值 → 当前 DPI 实际像素（活动栏尺寸是运行时算的，不会跟着窗体缩放走）。</summary>
    private int Px(int value) => Dpi.Scale(this, value);

    /// <summary>
    /// 把活动栏摆到内容区最左侧并垂直居中。它是悬浮层、不参与表格布局，所以位置得自己算：
    /// 收起时宽度正好落在预留的窄边里（不遮内容），展开时压在画布左边。
    /// </summary>
    private void LayoutRail()
    {
        if (railStrip is null) return;
        var titleHeight = Px(34);
        var statusHeight = Px(24);
        var contentHeight = ClientSize.Height - titleHeight - statusHeight;
        if (contentHeight <= 0) return;
        var height = Math.Min(railStrip.Height, contentHeight - Px(16));
        railStrip.Bounds = new Rectangle(0, titleHeight + (contentHeight - height) / 2, railStrip.Width, Math.Max(Px(60), height));
    }

    /// <summary>缩放完成后再摆一次活动栏：缩放前 DeviceDpi 还不是真值，算出来的位置会偏。</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LayoutRail();
    }

    /// <summary>
    /// 滑出动画 + 悬停判定二合一：每帧看一次光标是否还在条上，决定展开还是收回。
    /// 光标静止在条上时状态不变，这一帧就什么都不做。
    /// </summary>
    private void OnRailSlideTick(object? sender, EventArgs e)
    {
        if (railStrip is null) return;
        var inside = railStrip.ClientRectangle.Contains(railStrip.PointToClient(Cursor.Position));
        var target = inside ? Px(RailExpandedWidth) : Px(RailCollapsedWidth);
        var width = railStrip.Width;
        if (width == target)
        {
            if (!inside) railSlide.Stop();
            return;
        }
        var step = Math.Max(Px(10), Math.Abs(target - width) / 3);
        SetRailWidth(width < target ? Math.Min(target, width + step) : Math.Max(target, width - step));
    }

    /// <summary>
    /// 太窄时只留那条窄边和「这里能滑出」的提示；
    /// 够宽了才把「图标 + 小字标签」露出来，避免动画中途图标被裁成碎片。
    /// </summary>
    private void SetRailWidth(int width)
    {
        if (railStrip is null) return;
        railStrip.Width = width;
        var expanded = width > Px(48);
        if (railFlow is not null) railFlow.Visible = expanded;
        railStrip.Collapsed = !expanded;
        // 动画每帧都在改宽度，露出来的新区域与子控件都要重画，
        // 否则上一帧的字会留在下面（就是"字叠字"）。
        railStrip.Invalidate(true);
    }

    /// <summary>
    /// 整宽标题栏：左侧品牌与画布名，右侧主题切换与模型状态。
    /// 画布名与模型状态都在这里，因为它们是最常看的两项，不该藏在面板里。
    /// </summary>
    private Control BuildTitleBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.ChromeBg, ColumnCount = 7, RowCount = 1, Padding = new Padding(12, 0, 12, 0), Margin = Padding.Empty };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = BrandAssets.CreateMark(new Size(112, 24), Theme.TitleFont, Theme.Text);
        brand.Anchor = AnchorStyles.Left;
        brand.Margin = new Padding(0, 0, 14, 0);
        brandLabel = brand;
        bar.Controls.Add(brand, 0, 0);

        projectLabel.Text = AppPaths.CurrentProject.Descriptor.Name;
        projectLabel.AutoSize = true;
        projectLabel.Anchor = AnchorStyles.Left;
        projectLabel.Margin = new Padding(0, 0, 12, 0);
        projectLabel.Font = Theme.UiFontBold;
        projectLabel.ForeColor = Theme.Accent;
        projectLabel.Cursor = Cursors.Hand;
        projectLabel.Click += (_, _) => SwitchProject();
        railTip.SetToolTip(projectLabel, $"当前项目：{AppPaths.CurrentProject.RootPath}\n点击切换项目");
        bar.Controls.Add(projectLabel, 1, 0);

        canvasTitleLabel.Text = canvasTitle;
        canvasTitleLabel.AutoSize = true;
        canvasTitleLabel.Anchor = AnchorStyles.Left;
        canvasTitleLabel.Margin = new Padding(0, 0, 12, 0);
        canvasTitleLabel.Font = Theme.UiFontBold;
        canvasTitleLabel.ForeColor = Theme.TextMuted;
        canvasTitleLabel.Cursor = Cursors.Hand;
        canvasTitleLabel.DoubleClick += (_, _) => RenameSelectedCanvas();
        railTip.SetToolTip(canvasTitleLabel, "双击重命名当前画布");
        bar.Controls.Add(canvasTitleLabel, 2, 0);

        themeButton.Text = ThemeButtonText();
        themeButton.AutoSize = true;
        themeButton.Anchor = AnchorStyles.Right;
        themeButton.FlatStyle = FlatStyle.Flat;
        themeButton.FlatAppearance.BorderSize = 0;
        themeButton.BackColor = Color.Transparent;
        themeButton.ForeColor = Theme.TextMuted;
        themeButton.Font = Theme.UiFont;
        themeButton.Cursor = Cursors.Hand;
        themeButton.Padding = new Padding(8, 3, 8, 3);
        themeButton.Margin = new Padding(0, 0, 12, 0);
        themeButton.Click += (_, _) =>
        {
            Theme.Set(Theme.Toggle());
            PersistTheme();
        };
        railTip.SetToolTip(themeButton, "切换深色 / 浅色");
        bar.Controls.Add(themeButton, 5, 0);

        var switchProjectButton = new Button { Text = "切换项目", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = Theme.TextMuted, Font = Theme.UiFont, Cursor = Cursors.Hand, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0) };
        switchProjectButton.FlatAppearance.BorderSize = 0;
        switchProjectButton.Click += (_, _) => SwitchProject();
        railTip.SetToolTip(switchProjectButton, "保存当前项目并切换到其他项目");
        bar.Controls.Add(switchProjectButton, 4, 0);

        providerLabel.AutoSize = true;
        providerLabel.Anchor = AnchorStyles.Right;
        providerLabel.ForeColor = Theme.TextMuted;
        providerLabel.Font = Theme.UiFont;
        providerLabel.Cursor = Cursors.Hand;
        providerLabel.Click += (_, _) => ReconfigureProvider();
        railTip.SetToolTip(providerLabel, "点击重新接入大模型");
        bar.Controls.Add(providerLabel, 6, 0);
        RefreshProviderLabel();
        titleBarPanel = bar;
        return bar;
    }

    /// <summary>整宽状态栏：左边是运行状态，右边是修订号。</summary>
    private Control BuildStatusBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.ChromeBg, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 0, 12, 0), Margin = Padding.Empty };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        statusBarText.AutoSize = true;
        statusBarText.Anchor = AnchorStyles.Left;
        statusBarText.ForeColor = Theme.TextMuted;
        statusBarText.Font = Theme.SmallFont;
        statusBarText.Text = "就绪";
        bar.Controls.Add(statusBarText, 0, 0);

        canvasRevisionLabel.Text = "修订 0";
        canvasRevisionLabel.AutoSize = true;
        canvasRevisionLabel.Anchor = AnchorStyles.Right;
        canvasRevisionLabel.ForeColor = Theme.TextDim;
        canvasRevisionLabel.Font = Theme.SmallFont;
        bar.Controls.Add(canvasRevisionLabel, 1, 0);
        statusBarPanel = bar;
        return bar;
    }

    /// <summary>
    /// 左侧活动栏：收起时只留一条窄边（画三道短横提示可以滑出），
    /// 鼠标靠过去滑出成「图标 + 小字标签」的一列，按组分隔。
    /// </summary>
    private Control BuildRailStrip()
    {
        var strip = new RailStrip();
        // 这一列自身也必须是不透明的实心块：滑出过程里它一直在被裁剪，
        // 透明子控件去请自绘的条画背景会把裁剩下的内容带进来（乱码/马赛克）。
        var flow = Theme.Surface(new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        }, SurfaceRole.Rail);

        foreach (var (icon, title, key) in new[]
        {
            (RailIcon.Library, "画布库", "library"),
            (RailIcon.Entity, "设定库", "entities"),
            (RailIcon.Skill, "工作树", "worktree"),
            (RailIcon.Node, "节点属性", "node"),
            (RailIcon.Task, "任务与出图", "tasks")
        })
            flow.Controls.Add(CreateRailButton(icon, title, key));

        flow.Controls.Add(RailSeparator());
        flow.Controls.Add(CreateRailButton(RailIcon.Chat, "Agent 对话", "agent"));

        if (pluginRailItems.Count > 0)
        {
            flow.Controls.Add(RailSeparator());
            flow.Controls.Add(CreateRailButton(RailIcon.Plugin, "插件", "plugins"));
            foreach (var item in pluginRailItems)
                flow.Controls.Add(CreatePluginRailButton(item.Glyph, item.Title, item.Key));
        }

        flow.Controls.Add(RailSeparator());
        var settings = CreateRailButton(RailIcon.Settings, "设置", null);
        settings.Click += (_, _) => OpenAiSettingsDialog();
        flow.Controls.Add(settings);

        strip.Controls.Add(flow);
        // 条高就是这一列按钮的总高（含外边距）——它就是"垂直居中"的那一块。
        strip.Size = new Size(RailCollapsedWidth, flow.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical));

        railStrip = strip;
        railFlow = flow;
        railSlide.Tick += OnRailSlideTick;
        strip.MouseEnter += (_, _) => railSlide.Start();
        SetRailWidth(RailCollapsedWidth);
        return strip;
    }

    /// <summary>组与组之间的 1px 分隔：让「创作 / 协作 / 扩展 / 设置」读起来是分开的几段。</summary>
    private static Control RailSeparator()
    {
        var line = Theme.Line(RailExpandedWidth - 24);
        line.Margin = new Padding(12, 6, 12, 6);
        return line;
    }

    private RailNavButton CreateRailButton(RailIcon icon, string title, string? paneKey)
    {
        var button = new RailNavButton(icon, title, railTip);
        if (paneKey is not null)
        {
            railButtons[paneKey] = button;
            button.Click += (_, _) => TogglePane(paneKey);
        }
        return button;
    }

    /// <summary>插件入口统一使用插件图形图标，避免不同插件的字符字形破坏导航栏一致性。</summary>
    private RailNavButton CreatePluginRailButton(string glyph, string title, string key)
    {
        var button = new RailNavButton(RailIcon.Plugin, title, railTip);
        railButtons[key] = button;
        button.Click += (_, _) => TogglePane(key);
        return button;
    }

    private Control BuildContent()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.EditorBg, Padding = new Padding(0) };
        host.Controls.Add(BuildWorkspace());
        host.Controls.Add(BuildAgentDrawer());
        host.Controls.Add(BuildDrawer());
        return host;
    }

    /// <summary>右侧的 Agent 面板容器：与画布并排，展开时挤占画布宽度而不是盖在画布上。</summary>
    private Control BuildAgentDrawer()
    {
        agentDrawer.Dock = DockStyle.Right;
        agentDrawer.Width = 400;
        agentDrawer.BackColor = Theme.PanelBg;
        agentDrawer.Padding = new Padding(0);
        agentDrawer.Margin = new Padding(0);
        agentDrawer.Visible = false;
        return agentDrawer;
    }

    /// <summary>展开式面板容器：默认隐藏，点击图标后才占用画布左侧空间。</summary>
    private Control BuildDrawer()
    {
        drawer.Dock = DockStyle.Left;
        drawer.Width = 340;
        drawer.BackColor = Theme.PanelBg;
        drawer.Padding = new Padding(0, 0, 0, 0);
        drawer.Visible = false;
        drawer.Margin = new Padding(0, 0, 1, 0);
        return drawer;
    }

    /// <summary>
    /// 面板外壳：一行标题栏（标题 + 关闭）加内容区。
    /// 标题栏用与活动栏一致的字号与分隔线，让每个面板看起来是同一个产品的一部分。
    /// </summary>
    private static Panel CreatePaneShell(string title, Control content, Action? onClose = null)
    {
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };
        var bar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.PanelBg };
        var close = new Button
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 32,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Font = Theme.SmallFont,
            TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Theme.Hover;
        close.Click += (_, _) =>
        {
            if (onClose is not null) { onClose(); return; }
            (shell.FindForm() as MainForm)?.HideDrawer();
        };
        bar.Controls.Add(close);
        bar.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Left,
            Width = 220,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Font = Theme.UiFontBold,
            ForeColor = Theme.Text
        });
        var separator = Theme.Line(DockStyle.Bottom);
        shell.Controls.Add(content);
        shell.Controls.Add(separator);
        shell.Controls.Add(bar);
        return shell;
    }

    private void TogglePane(string key)
    {
        // Agent 面板在右侧，与画布并排，单独一套容器。
        if (key == "agent") { ToggleAgentPane(); return; }
        if (!panes.TryGetValue(key, out var pane))
        {
            // 插件面板按需创建：插件代码要等宿主完全就绪后才执行。
            var pluginItem = pluginRailItems.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (pluginItem is null) return;
            try { pane = CreatePaneShell(pluginItem.Title, pluginItem.Factory()); }
            catch (Exception error)
            {
                MessageBox.Show($"插件面板创建失败：{error.Message}", pluginItem.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 启动时那次统一套色发生在这之前，按需创建的插件面板必须自己补一遍，
            // 否则它的输入框会停在系统默认色（深色界面上就是黑底黑字）。
            Theme.Apply(pane);
            panes[key] = pane;
        }
        if (activePaneKey == key) { HideDrawer(); return; }
        drawer.SuspendLayout();
        drawer.Controls.Clear();
        drawer.Controls.Add(pane);
        drawer.ResumeLayout();
        drawer.Visible = true;
        activePaneKey = key;
        UpdateRailSelection();
        if (key == "node") RefreshNodeInspector();
        if (key == "tasks") RefreshJobList();
        if (key == "entities") RefreshEntityList();
        if (key == "plugins") RefreshPluginList();
    }

    private void HideDrawer()
    {
        drawer.Visible = false;
        activePaneKey = null;
        UpdateRailSelection();
    }

    /// <summary>
    /// 展开 / 收起右侧的 Agent 面板。它是内嵌面板而不是独立窗口：与画布并排，
    /// 展开时挤占画布宽度，因此不会被画布遮挡，也不会盖住画布内容。
    /// </summary>
    private void ToggleAgentPane()
    {
        if (agentDrawer.Visible) { HideAgentDrawer(); return; }
        if (agentPaneShell is null)
        {
            agentPaneControl = BuildAgentPane();
            agentPaneShell = CreatePaneShell("Agent 对话", agentPaneControl, HideAgentDrawer);
            // 同上：Agent 面板是在启动套色之后才建的，必须自己补一遍。
            // 这正是"Agent 对话黑底黑字"的根因——RichTextBox 的 ForeColor 停在默认黑色。
            Theme.Apply(agentPaneShell);
        }
        agentDrawer.SuspendLayout();
        agentDrawer.Controls.Clear();
        agentDrawer.Controls.Add(agentPaneShell);
        // 1px 分界线：Agent 面板与画布是两个独立模块，不能糊成一片。
        agentDrawer.Controls.Add(Theme.Line(DockStyle.Left));
        agentDrawer.ResumeLayout();
        agentDrawer.Visible = true;
        agentDrawer.BringToFront();
        UpdateRailSelection();
        agentPaneControl?.FocusInput();
    }

    private void HideAgentDrawer()
    {
        agentDrawer.Visible = false;
        UpdateRailSelection();
    }

    /// <summary>Agent 面板的宿主契约：Provider 与工作文件夹都按需读取，重新接入后无需重建面板。</summary>
    private AgentPane BuildAgentPane() => new(new AgentPaneHost(
        Provider: () => aiProvider as IAiChatProvider,
        ProviderLabel: CurrentProviderLabel,
        Context: BuildAgentContext,
        WorkspacePath: () => AiProviderSettings.Load().AgentWorkspace,
        RequestWorkspaceSelection: SelectAgentWorkspace,
        PrecheckActions: actions => AgentActionExecutor.PrecheckBatch(actions, canvas.State, AiProviderSettings.Load().AgentWorkspace),
        ApplyActions: ApplyAgentBatch,
        PreviewActions: PreviewAgentBatch,
        PrepareActions: PrepareAgentActions,
        PendingActions: () => pendingChanges.Actions,
        RemovePending: RemovePendingAction,
        SaveApplied: SaveAppliedAgentChanges,
        UndoApplied: DiscardPendingChanges,
        FocusAction: FocusAgentAction,
        ContextCharacterBudget: () => AiProviderSettings.Load().ContextCharacterBudget,
        ImageInputEnabled: () => AiProviderSettings.Load().SupportsImageInput,
        RequestModelSettings: OpenAiSettingsDialog,
        ApplyModel: ApplyAgentModel,
        AcceptToNode: AcceptAgentReply));

    /// <summary>
    /// Agent 面板里换模型：只改模型名，其余接入参数（端点、密钥、上下文）保持不动，
    /// 这样「在同一服务商下换模型」不用重新走一遍接入引导。
    /// </summary>
    private void ApplyAgentModel(string model)
    {
        var config = AiProviderSettings.Load();
        if (string.Equals(config.Model, model, StringComparison.Ordinal)) return;
        config.Model = model;
        AiProviderSettings.Save(config);
        ApplyProviderChange();
    }

    // ---------- Agent 虚影预览与真实应用 ----------

    /// <summary>把人工审批批次试算为画布虚影，不修改真实画布或文件。</summary>
    private void PreviewAgentBatch(IReadOnlyList<AgentAction> actions)
    {
        if (actions.Count == 0) return;
        pendingChanges.Clear();
        pendingChanges.AddRange(actions);
        canvas.SetPreview(CanvasPreviewBuilder.Build(actions, canvas.State));
        RefreshPendingLabel();
        agentPaneControl?.RefreshState();
        canvas.Invalidate();
    }

    /// <summary>
    /// 将 Agent 批次立即应用到真实画布。自动模式使用此入口并随后保存。
    /// </summary>
    private void ApplyAgentBatch(IReadOnlyList<AgentAction> actions)
    {
        if (actions.Count == 0) return;
        var selected = canvas.SelectedNode;
        foreach (var action in actions)
        {
            if (string.Equals(action.Kind, "create_node", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(action.ParentTarget)
                && selected is not null)
                action.ParentTarget = selected.Title;
        }

        var record = new AgentCommitRecord
        {
            AppliedCount = actions.Count,
            Summary = string.Join("；", actions.Select(action => action.Describe())),
            SnapshotBefore = JsonSerializer.Serialize(BuildCanvasState()),
            RevisionAtCommit = canvasRevision
        };
        var result = AgentActionExecutor.Apply(
            actions, canvas.State, AiProviderSettings.Load().AgentWorkspace,
            FileWriteMode.Apply, record.FileSnapshots);

        pendingChanges.Clear();
        pendingChanges.AddRange(actions);
        lastAgentCommit = record;
        canvas.NotifyContentChanged();
        canvas.InvalidateThumbnails();
        RefreshNodeInspector();
        RefreshEntityList();
        RefreshCanvasLibrary();
        RefreshPendingState();
        RefreshWorkTreeView();
        canvas.Invalidate();

        var latest = actions.LastOrDefault(action =>
            string.Equals(action.Kind, "create_node", StringComparison.OrdinalIgnoreCase));
        if (latest is not null)
        {
            var node = canvas.State.Nodes.LastOrDefault(item =>
                string.Equals(item.Title, latest.Title, StringComparison.OrdinalIgnoreCase));
            if (node is not null) canvas.FocusNode(node.Id);
        }
        if (result.RemovedReferences.Count > 0) OfferRecycleOrphanedAssets(result.RemovedReferences);
        WriteAgentCommitDiagnostic($"apply count={actions.Count} applied={result.Applied} errors={result.Errors.Count} pending={pendingChanges.Count}");
        if (result.Errors.Count > 0)
            MessageBox.Show($"有 {result.Errors.Count} 条改动未能应用：\n\n{string.Join("\n", result.Errors)}",
                "Agent 应用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private IReadOnlyList<AgentAction> PrepareAgentActions(IReadOnlyList<AgentAction> actions, bool autoApprove)
    {
        var locked = actions
            .Where(action => string.Equals(action.Kind, "update_node", StringComparison.OrdinalIgnoreCase))
            .Select(action => canvas.State.Nodes.FirstOrDefault(node =>
                node.IsLocked && (string.Equals(node.Title, action.Target, StringComparison.OrdinalIgnoreCase)
                    || node.Id.ToString("N").StartsWith(action.Target, StringComparison.OrdinalIgnoreCase))))
            .Where(node => node is not null)
            .Cast<WorkflowNode>()
            .DistinctBy(node => node.Id)
            .ToArray();
        if (locked.Length == 0) return actions;

        var approved = autoApprove || ConfirmUnlockLockedNodes(locked);
        if (approved)
            foreach (var node in locked) node.IsLocked = false;
        return approved
            ? actions
            : actions.Where(action => !locked.Any(node =>
                string.Equals(action.Kind, "update_node", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(node.Title, action.Target, StringComparison.OrdinalIgnoreCase)
                    || node.Id.ToString("N").StartsWith(action.Target, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    private bool ConfirmUnlockLockedNodes(IReadOnlyList<WorkflowNode> nodes)
    {
        using var dialog = new Form
        {
            Text = "Agent 请求解锁节点",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(440, 190),
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.PanelBg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 90,
            Padding = new Padding(16),
            Text = $"Agent 要修改以下已锁定节点：\n\n{string.Join("、", nodes.Select(node => node.Title))}\n\n是否解锁并继续？（10 秒后默认同意）"
        };
        var accept = new Button { Text = "解锁并继续", Width = 110, Height = 32, DialogResult = DialogResult.Yes };
        var reject = new Button { Text = "拒绝本次修改", Width = 110, Height = 32, DialogResult = DialogResult.No };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(reject);
        dialog.Controls.Add(label);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = accept;
        dialog.CancelButton = reject;
        var timer = new System.Windows.Forms.Timer { Interval = 10000 };
        timer.Tick += (_, _) => { timer.Stop(); dialog.DialogResult = DialogResult.Yes; };
        dialog.Shown += (_, _) => timer.Start();
        dialog.FormClosed += (_, _) => timer.Dispose();
        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    private void FocusAgentAction(AgentAction action)
    {
        var target = action.Kind switch
        {
            "create_node" => action.Title,
            "update_node" or "delete_node" => action.Target,
            "create_edge" or "delete_edge" => action.Target,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(target)) return;

        var node = canvas.Preview?.AddedNodes.FirstOrDefault(item =>
            string.Equals(item.Title, target, StringComparison.OrdinalIgnoreCase));
        node ??= canvas.State.Nodes.FirstOrDefault(item =>
            string.Equals(item.Title, target, StringComparison.OrdinalIgnoreCase) ||
            item.Id.ToString("N").StartsWith(target, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;

        canvas.FocusNode(node.Id);
        TogglePane("node");
        RefreshNodeInspector();
    }

    private void RemovePendingAction(AgentAction action)
    {
        // 已应用批次不能逐条移除，否则快照无法表达审批结果；审批只支持整批保存或撤销。
    }

    private void RefreshPendingState()
    {
        WriteAgentCommitDiagnostic($"preview batch count={pendingChanges.Count}");
        canvas.SetPreview(pendingChanges.IsEmpty
            ? null
            : CanvasPreviewBuilder.Build(pendingChanges.Actions, canvas.State));
        RefreshPendingLabel();
        agentPaneControl?.RefreshState();
    }

    private void SaveAppliedAgentChanges()
    {
        if (pendingChanges.IsEmpty) return;
        var actions = pendingChanges.Actions.ToArray();
        ApplyAgentBatch(actions);
        SaveCanvasToLibrary(showResult: false);
    }

    private void DiscardPendingChanges()
    {
        pendingChanges.Clear();
        canvas.SetPreview(null);
        RefreshPendingState();
        RefreshWorkTreeView();
        canvas.Invalidate();
    }

    private static void WriteAgentCommitDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(
                AppPaths.CombineProgram("agent-diagnostics.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 诊断日志不能影响画布提交。
        }
    }

    /// <summary>窗口或标签切换时确认当前 Agent 虚影，并保存到画布文件。</summary>
    private AgentApplyResult? CommitPendingChanges()
    {
        if (pendingChanges.IsEmpty) return null;
        SaveAppliedAgentChanges();
        return null;
    }

    /// <summary>
    /// 撤销可用性：提交之后画布若又被改动过，回滚会连带回退用户自己的编辑，因此停用。
    /// </summary>
    private (bool Available, string Reason) UndoAvailability()
    {
        if (lastAgentCommit is null) return (false, "还没有可撤销的 Agent 提交。");
        if (canvasRevision != lastAgentCommit.RevisionAtCommit + 1)
            return (false, "提交之后画布又有其它改动，撤销会连带回退这些编辑，因此已停用。");
        return (true, string.Empty);
    }

    /// <summary>撤销上一次 Agent 提交：用提交前的整体快照恢复画布与画布参数。</summary>
    private void UndoLastAgentCommit()
    {
        var (available, reason) = UndoAvailability();
        if (!available) { MessageBox.Show(reason, "撤销上次提交", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var record = lastAgentCommit!;
        try
        {
            if (JsonSerializer.Deserialize<RecentCanvasState>(record.SnapshotBefore) is not { } state)
            {
                MessageBox.Show("提交快照无法解析，撤销失败。", "撤销上次提交", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ApplyCanvasState(state, advanceRevision: false);

            var failures = new List<string>();
            foreach (var snapshot in record.FileSnapshots)
                if (RestoreFile(snapshot) is { } failure) failures.Add(failure);

            lastAgentCommit = null;
            RefreshPendingState();
            canvas.Invalidate();
            var message = "已撤销上一次 Agent 提交，画布恢复到提交前的状态。";
            if (failures.Count > 0) message += "\n\n以下文件未能恢复：\n" + string.Join("\n", failures.Select(item => "· " + item));
            MessageBox.Show(message, "撤销上次提交", MessageBoxButtons.OK,
                failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "撤销失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>恢复一个被 Agent 覆盖或新建的文件：原本不存在就删除，原本有内容就写回。</summary>
    private static string? RestoreFile(FileSnapshot snapshot)
    {
        try
        {
            if (snapshot.OriginalContent is null)
            {
                if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path);
                return null;
            }
            File.WriteAllText(snapshot.Path, snapshot.OriginalContent);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{snapshot.Path}：{error.Message}";
        }
    }

    /// <summary>选择 Agent 的工作文件夹。Agent 只能把文件写在这个目录内。</summary>
    private void SelectAgentWorkspace()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Agent 的工作文件夹（Agent 只能把文件写在这个目录内）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        var current = AiProviderSettings.Load().AgentWorkspace;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var config = AiProviderSettings.Load();
        config.AgentWorkspace = dialog.SelectedPath;
        if (!AiProviderSettings.Save(config))
            MessageBox.Show("设置未能写入配置文件，本次只在当前会话生效。", "工作文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        agentPaneControl?.RefreshState();
    }

    private void UpdateRailSelection()
    {
        foreach (var (key, button) in railButtons)
        {
            if (button is not RailNavButton nav) continue;
            nav.Active = IsPaneActive(key);
            nav.Invalidate();
        }
    }

    private string ThemeButtonText() => Theme.IsDark ? "◐ 深色" : "◑ 浅色";

    /// <summary>启动时按配置定主题；配置值不认识就退回默认深色，不因一个坏值起不来。</summary>
    private static void ApplyStoredTheme()
    {
        var stored = AiProviderSettings.Load().Theme;
        Theme.Set(Enum.TryParse<AppTheme>(stored, ignoreCase: true, out var parsed) ? parsed : AppTheme.Dark);
    }

    /// <summary>把主题写进配置：它属于界面偏好，跟着接入配置一起存最省事。</summary>
    private static void PersistTheme()
    {
        var config = AiProviderSettings.Load();
        config.Theme = Theme.Current.ToString();
        AiProviderSettings.Save(config);
    }

    /// <summary>
    /// 主题切换后重新套色。画布自己绘制（见 WorkflowCanvasControl），Theme.Apply 会跳过它。
    /// 标题栏 / 状态栏 / 活动栏的颜色由各自构建方法写死，这里用保存的引用直接刷，不靠猜容器。
    /// </summary>
    private void ApplyTheme()
    {
        BackColor = Theme.ChromeBg;
        Font = Theme.UiFont;
        Theme.Apply(this);

        if (titleBarPanel is not null) titleBarPanel.BackColor = Theme.ChromeBg;
        if (statusBarPanel is not null) statusBarPanel.BackColor = Theme.ChromeBg;
        if (railStrip is not null) railStrip.BackColor = Theme.RailBg;

        themeButton.Text = ThemeButtonText();
        themeButton.ForeColor = Theme.TextMuted;
        brandLabel.ForeColor = Theme.Text;
        canvasTitleLabel.ForeColor = Theme.TextMuted;
        providerLabel.ForeColor = Theme.TextMuted;
        statusBarText.ForeColor = Theme.TextMuted;
        canvasRevisionLabel.ForeColor = Theme.TextDim;

        drawer.BackColor = Theme.PanelBg;
        agentDrawer.BackColor = Theme.PanelBg;
        // 画布区比侧栏更深一档，像编辑区之于侧边栏——Theme.Apply 会把它抹平，这里补回来。
        if (workspaceRoot is not null) workspaceRoot.BackColor = Theme.EditorBg;

        if (canvasTabStrip is not null) canvasTabStrip.BackColor = Theme.ChromeBg;
        foreach (var tab in canvasTabs)
        {
            if (tab.TabControl is not null) tab.TabControl.BackColor = tab == activeCanvasTab ? Theme.EditorBg : Theme.ChromeBg;
            if (tab.TitleControl is not null) tab.TitleControl.ForeColor = tab == activeCanvasTab ? Theme.Text : Theme.TextMuted;
            if (tab.Underline is not null) tab.Underline.BackColor = tab == activeCanvasTab ? Theme.Accent : Theme.ChromeBg;
        }

        canvas.Invalidate();
        canvas.InvalidateThumbnails();
        UpdateRailSelection();
    }

    private bool IsPaneActive(string key) =>
        string.Equals(key, "agent", StringComparison.Ordinal)
            ? agentDrawer.Visible
            : drawer.Visible && activePaneKey == key;

    private Control BuildCanvasLibraryPane()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        canvasList.View = View.Details; canvasList.FullRowSelect = true; canvasList.MultiSelect = false;
        canvasList.HeaderStyle = ColumnHeaderStyle.None; canvasList.HideSelection = false;
        canvasList.Columns.Add("画布", 200); canvasList.Dock = DockStyle.Fill;
        canvasList.DoubleClick += (_, _) => OpenSelectedLibraryCanvas();
        content.Controls.Add(canvasList);
        canvasLibraryHint.Text = "画布库为空，点击下方“新建”开始，再用“保存”存入画布库。";
        canvasLibraryHint.Dock = DockStyle.Fill; canvasLibraryHint.ForeColor = Color.Gray;
        canvasLibraryHint.BackColor = Color.Transparent; canvasLibraryHint.Visible = false;
        content.Controls.Add(canvasLibraryHint);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 108, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(CreatePaneButton("新建", 68, NewCanvasButton_Click));
        footer.Controls.Add(CreatePaneButton("保存", 68, (_, _) => SaveCanvasToLibrary()));
        footer.Controls.Add(CreatePaneButton("打开", 68, (_, _) => OpenSelectedLibraryCanvas()));
        footer.Controls.Add(CreatePaneButton("重命名", 68, (_, _) => RenameSelectedCanvas()));
        footer.Controls.Add(CreatePaneButton("删除", 68, (_, _) => DeleteSelectedLibraryCanvas()));
        footer.Controls.Add(CreatePaneButton("导入包", 74, (_, _) => ImportCanvasPackage()));
        footer.Controls.Add(CreatePaneButton("导出包", 74, (_, _) => ExportCanvasPackage()));
        footer.Controls.Add(CreatePaneButton("存储与清理", 96, (_, _) => OpenStorageDialog()));
        content.Controls.Add(footer);
        canvasLibraryHint.BringToFront();
        RefreshCanvasLibrary();
        return CreatePaneShell("画布库", content);
    }

    private static Button CreatePaneButton(string text, int width, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 0),
            BackColor = Theme.FieldBg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Theme.Hover;
        button.Click += handler;
        return button;
    }

    /// <summary>
    /// “设定库”面板：项目级角色 / 场景 / 道具，每个实体下挂多个变体。
    /// 变体是引用与出图的单位，场景变体另外携带结构化的空间布局。
    /// </summary>
    private Control BuildEntityLibraryPane()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 6, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        layout.Controls.Add(SectionTitle("实体"), 0, 0);
        entityList.View = View.Details; entityList.FullRowSelect = true; entityList.MultiSelect = false;
        entityList.GridLines = true; entityList.HideSelection = false; entityList.Dock = DockStyle.Fill;
        entityList.Columns.Add("种类", 58); entityList.Columns.Add("名称", 166); entityList.Columns.Add("变体", 52);
        entityList.SelectedIndexChanged += (_, _) => RefreshVariantList();
        entityList.DoubleClick += (_, _) => EditSelectedEntity();
        entityList.ContextMenuStrip = new ContextMenuStrip();
        entityList.ContextMenuStrip.Opening += (_, _) =>
        {
            var menu = entityList.ContextMenuStrip!;
            menu.Items.Clear();
            if (SelectedEntity is not { } entity) return;
            menu.Items.AddRange(BuildPluginMenuItems(ContextTarget.Entity,
                new ContextInfo { Target = ContextTarget.Entity, Canvas = canvas.State, Entity = entity }).ToArray());
        };
        layout.Controls.Add(entityList, 0, 1);

        var entityButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        entityButtons.Controls.Add(CreatePaneButton("新建角色", 84, (_, _) => CreateEntity(EntityKind.Character)));
        entityButtons.Controls.Add(CreatePaneButton("新建场景", 84, (_, _) => CreateEntity(EntityKind.Scene)));
        entityButtons.Controls.Add(CreatePaneButton("新建道具", 84, (_, _) => CreateEntity(EntityKind.Prop)));
        entityButtons.Controls.Add(CreatePaneButton("编辑", 68, (_, _) => EditSelectedEntity()));
        entityButtons.Controls.Add(CreatePaneButton("删除", 68, (_, _) => DeleteSelectedEntity()));
        layout.Controls.Add(entityButtons, 0, 2);

        layout.Controls.Add(SectionTitle("变体"), 0, 3);
        variantList.View = View.Details; variantList.FullRowSelect = true; variantList.MultiSelect = false;
        variantList.GridLines = true; variantList.HideSelection = false; variantList.Dock = DockStyle.Fill;
        variantList.Columns.Add("变体", 112); variantList.Columns.Add("版本", 86); variantList.Columns.Add("布局", 86);
        variantList.DoubleClick += (_, _) => EditSelectedVariant();
        variantList.ContextMenuStrip = new ContextMenuStrip();
        variantList.ContextMenuStrip.Opening += (_, _) =>
        {
            var menu = variantList.ContextMenuStrip!;
            menu.Items.Clear();
            if (SelectedEntity is not { } entity || SelectedVariant is not { } variant) return;
            menu.Items.AddRange(BuildPluginMenuItems(ContextTarget.Variant,
                new ContextInfo { Target = ContextTarget.Variant, Canvas = canvas.State, Entity = entity, Variant = variant }).ToArray());
            // 参考图单独一层：插件可以针对某一张图执行操作。
            if (pluginMenuItems.TryGetValue(ContextTarget.VariantImage, out var imageEntries) && imageEntries.Count > 0)
            {
                var images = variant.Attachments.Where(item => item.Kind == AttachmentKind.Image).ToList();
                if (images.Count > 0)
                {
                    var submenu = new ToolStripMenuItem("参考图");
                    foreach (var entry in imageEntries)
                    {
                        var handler = entry.Handler;
                        var action = new ToolStripMenuItem(entry.Text);
                        foreach (var image in images)
                        {
                            var attachment = image;
                            var child = new ToolStripMenuItem(string.IsNullOrWhiteSpace(attachment.Name) ? Path.GetFileName(attachment.Reference) : attachment.Name);
                            child.Click += async (_, _) =>
                            {
                                try
                                {
                                    await handler(new ContextInfo
                                    {
                                        Target = ContextTarget.VariantImage,
                                        Canvas = canvas.State,
                                        Entity = entity,
                                        Variant = variant,
                                        Attachment = attachment
                                    });
                                }
                                catch (Exception error) { MessageBox.Show(error.Message, "插件执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                            };
                            action.DropDownItems.Add(child);
                        }
                        submenu.DropDownItems.Add(action);
                    }
                    menu.Items.Add(submenu);
                }
            }
        };
        layout.Controls.Add(variantList, 0, 4);

        var variantButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        variantButtons.Controls.Add(CreatePaneButton("新建变体", 88, (_, _) => CreateVariant()));
        variantButtons.Controls.Add(CreatePaneButton("编辑变体", 88, (_, _) => EditSelectedVariant()));
        variantButtons.Controls.Add(CreatePaneButton("删除变体", 88, (_, _) => DeleteSelectedVariant()));
        variantButtons.Controls.Add(CreatePaneButton("引用到画布", 96, (_, _) => ReferenceVariantOnCanvas()));
        variantButtons.Controls.Add(CreatePaneButton("运行技能", 88, (_, _) => _ = RunSkillForSelectedVariantAsync()));
        layout.Controls.Add(variantButtons, 0, 5);

        return CreatePaneShell("设定库", layout);
    }

    /// <summary>
    /// “插件”面板：列出插件加载状态、已注册窗口与目录位置。
    /// 插件只负责挂界面入口，内容生产统一由技能完成。
    /// </summary>
    private Control BuildPluginPane()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 4, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        layout.Controls.Add(FieldLabel("已加载插件"), 0, 0);
        pluginList.View = View.Details; pluginList.FullRowSelect = true; pluginList.GridLines = true;
        pluginList.MultiSelect = false; pluginList.HideSelection = false; pluginList.Dock = DockStyle.Fill;
        pluginList.ShowItemToolTips = true;
        pluginList.Columns.Add("插件", 128); pluginList.Columns.Add("版本", 56); pluginList.Columns.Add("状态", 76);
        layout.Controls.Add(pluginList, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        buttons.Controls.Add(CreatePaneButton("打开插件目录", 108, (_, _) => OpenPluginDirectory()));
        buttons.Controls.Add(CreatePaneButton("重新加载", 84, (_, _) => ReloadPlugins()));
        buttons.Controls.Add(CreatePaneButton("打开窗口", 84, (_, _) => OpenSelectedPluginWindow()));
        buttons.Controls.Add(CreatePaneButton("技能目录", 84, (_, _) => OpenSkillDirectory()));
        layout.Controls.Add(buttons, 0, 2);

        pluginHint.Dock = DockStyle.Fill; pluginHint.AutoSize = false; pluginHint.ForeColor = Color.Gray;
        layout.Controls.Add(pluginHint, 0, 3);
        return CreatePaneShell("插件", layout);
    }

    private void RefreshPluginList()
    {
        pluginList.BeginUpdate();
        pluginList.Items.Clear();
        foreach (var plugin in loadedPlugins)
        {
            var item = new ListViewItem(string.IsNullOrWhiteSpace(plugin.Manifest.Name) ? plugin.Manifest.Id : plugin.Manifest.Name);
            item.SubItems.Add(plugin.Manifest.Version);
            item.SubItems.Add(plugin.IsLoaded ? "已加载" : "失败");
            item.ToolTipText = plugin.Error ?? plugin.Directory;
            item.Tag = plugin;
            pluginList.Items.Add(item);
        }
        if (loadedPlugins.Count == 0) pluginList.Items.Add(new ListViewItem(new[] { "（没有插件）", "-", "-" }));
        pluginList.EndUpdate();

        var windows = pluginWindows.Count == 0 ? "无" : string.Join("、", pluginWindows.Values.Select(window => window.Title));
        var menus = pluginMenuItems.Count == 0 ? "无" : string.Join("、", pluginMenuItems.Select(pair => $"{pair.Key}×{pair.Value.Count}"));
        var lines = new List<string>
        {
            $"插件目录：{PluginLoader.Directory}",
            $"技能目录：{SkillLibrary.Directory}",
            $"已注册窗口：{windows}",
            $"已注册菜单：{menus}"
        };
        lines.AddRange(pluginErrors.Take(3).Select(error => "宿主错误：" + error));
        lines.AddRange(loadedPlugins.Where(plugin => plugin.Error is not null).Take(3).Select(plugin => $"{plugin.Manifest.Id}：{plugin.Error}"));
        pluginHint.Text = string.Join("\n", lines);
    }

    private void OpenPluginDirectory()
    {
        try { Process.Start(new ProcessStartInfo(PluginLoader.EnsureDirectory()) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(error.Message, "插件目录", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenSkillDirectory()
    {
        try { Process.Start(new ProcessStartInfo(SkillLibrary.EnsureDirectory()) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(error.Message, "技能目录", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>
    /// 重新扫描插件。右键菜单在每次弹出时重建，因此立即生效；
    /// 左侧图标栏是启动时构建的，新增图标需要重启程序。
    /// </summary>
    private void ReloadPlugins()
    {
        pluginMenuItems.Clear();
        pluginRailItems.Clear();
        pluginWindows.Clear();
        loadedPlugins.Clear();
        pluginErrors.Clear();
        pluginRailSequence = 0;
        foreach (var key in panes.Keys.Where(key => key.StartsWith("plugin:", StringComparison.Ordinal)).ToList()) panes.Remove(key);
        if (activePaneKey is not null && activePaneKey.StartsWith("plugin:", StringComparison.Ordinal)) HideDrawer();
        RegisterBuiltInPlugins();
        LoadExternalPlugins();
        RefreshPluginList();
        MessageBox.Show("已重新加载插件。右键菜单立即生效；左侧图标栏的新增项需要重启程序后才会出现。", "插件");
    }

    private void OpenSelectedPluginWindow()
    {
        var ids = pluginWindows.Keys.ToList();
        if (ids.Count == 0) { MessageBox.Show("没有插件注册窗口。", "插件"); return; }
        if (ids.Count == 1) { OpenWindow(ids[0]); return; }
        var id = ShowTextInput("打开插件窗口", $"可用的窗口：{string.Join("、", ids)}", ids[0]);
        if (!string.IsNullOrWhiteSpace(id)) OpenWindow(id.Trim());
    }

    private void RefreshEntityList()
    {
        var selected = SelectedEntity;
        entityList.BeginUpdate();
        entityList.Items.Clear();
        foreach (var entity in canvas.State.Entities)
        {
            var item = new ListViewItem(WorkflowEntity.KindName(entity.Kind));
            item.SubItems.Add(entity.Name);
            item.SubItems.Add(entity.Variants.Count.ToString());
            item.Tag = entity;
            entityList.Items.Add(item);
        }
        entityList.EndUpdate();
        if (selected is not null) SelectInList(entityList, selected);
        RefreshVariantList();
    }

    private void RefreshVariantList()
    {
        variantList.BeginUpdate();
        variantList.Items.Clear();
        if (SelectedEntity is { } entity)
            foreach (var variant in entity.Variants)
            {
                var item = new ListViewItem(variant.Name);
                item.SubItems.Add(variant.HasUncommittedChanges ? $"{variant.CurrentVersionLabel} 有改动" : variant.CurrentVersionLabel);
                item.SubItems.Add(variant.Layout is null ? "-" : variant.Layout.IsEmpty ? "未填写" : $"{variant.Layout.Items.Count} 处");
                item.Tag = variant;
                variantList.Items.Add(item);
            }
        variantList.EndUpdate();
    }

    private WorkflowEntity? SelectedEntity =>
        entityList.SelectedItems.Count > 0 ? entityList.SelectedItems[0].Tag as WorkflowEntity : null;

    private WorkflowEntityVariant? SelectedVariant =>
        variantList.SelectedItems.Count > 0 ? variantList.SelectedItems[0].Tag as WorkflowEntityVariant : null;

    /// <summary>在列表中选中标签匹配的项，返回是否命中了目标。</summary>
    private static bool SelectInList(ListView list, object tag)
    {
        foreach (ListViewItem item in list.Items)
            if (ReferenceEquals(item.Tag, tag)) { item.Selected = true; item.EnsureVisible(); return true; }
        return false;
    }

    private void CreateEntity(EntityKind kind)
    {
        var entity = new WorkflowEntity { Kind = kind, Name = $"新{WorkflowEntity.KindName(kind)}" };
        entity.CreateVariant("默认");
        if (!OpenEntityEditor(entity)) return;
        canvas.State.Entities.Add(entity);
        canvas.NotifyContentChanged();
        RefreshEntityList();
        SelectInList(entityList, entity);
    }

    private void EditSelectedEntity()
    {
        if (SelectedEntity is not { } entity) { MessageBox.Show("请先选择一个实体。", "设定库"); return; }
        if (!OpenEntityEditor(entity)) return;
        canvas.NotifyContentChanged();
        RefreshEntityList();
        SelectInList(entityList, entity);
    }

    private void DeleteSelectedEntity()
    {
        if (SelectedEntity is not { } entity) { MessageBox.Show("请先选择一个实体。", "设定库"); return; }
        var referencing = canvas.State.Nodes.Count(node => node.References.Any(reference => reference.EntityId == entity.Id));
        var extra = referencing == 0 ? string.Empty : $"\n画布上有 {referencing} 个节点正在引用它，删除后这些引用会失效（节点本身与文本保留）。";
        if (MessageBox.Show($"删除「{entity.Name}」及其 {entity.Variants.Count} 个变体？参考图不会立即删除。{extra}",
                "删除实体", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        canvas.State.Entities.Remove(entity);
        canvas.NotifyContentChanged();
        RefreshEntityList();
        RefreshNodeInspector();
        canvas.Invalidate();
    }

    /// <summary>把当前选中的变体作为新节点放到画布上，节点创建时即引用该设定并锁定最新版本。</summary>
    private void ReferenceVariantOnCanvas()
    {
        if (SelectedEntity is not { } entity || SelectedVariant is not { } variant)
        {
            MessageBox.Show("请先选择一个实体和变体。", "引用到画布");
            return;
        }
        var latest = variant.EnsureInitialVersion();
        var node = canvas.AddNode();
        node.Title = entity.Name;
        node.References.Add(new NodeReference
        {
            EntityId = entity.Id,
            VariantId = variant.Id,
            VariantVersionId = variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault()?.Id ?? latest.Id
        });
        RefreshNodeInspector();
        canvas.Invalidate();
    }

    /// <summary>
    /// 对当前选中的变体运行技能。技能只能用大模型产出新资源，产出写入变体当前内容的参考图；
    /// 需要固化时再去「版本」页提交新版本。
    /// </summary>
    private async Task RunSkillForSelectedVariantAsync()
    {
        if (SelectedEntity is not { } entity || SelectedVariant is not { } variant)
        {
            MessageBox.Show("请先选择一个实体和变体。", "运行技能");
            return;
        }

        SkillLibrary.EnsureDefaultSkills();
        var (skills, errors) = SkillLibrary.Load();
        if (errors.Count > 0)
            MessageBox.Show($"以下技能清单有问题，已跳过：\n{string.Join("\n", errors.Take(5))}", "技能装载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (skills.Count == 0)
        {
            MessageBox.Show($"技能目录里没有可用的技能：\n{SkillLibrary.Directory}", "运行技能");
            return;
        }

        var applicable = skills.Where(skill => skill.Validate(entity) is null).ToList();
        if (applicable.Count == 0)
        {
            MessageBox.Show($"没有适用于「{WorkflowEntity.KindName(entity.Kind)}」的技能。技能目录：{SkillLibrary.Directory}", "运行技能");
            return;
        }
        if (PickSkill(applicable, entity) is not { } chosen) return;
        await ExecuteSkillWithProgressAsync(chosen, new SkillTarget { Entity = entity, Variant = variant });
    }

    /// <summary>
    /// 带进度提示地运行技能，结束后统一提示结果。技能运行的界面由宿主负责，
    /// 插件与界面入口都走这里，保证体验一致。产出需要固化时由用户去「版本」页提交。
    /// </summary>
    private async Task<SkillRunResult> ExecuteSkillWithProgressAsync(SkillDefinition skill, SkillTarget target)
    {
        var provider = ImageProviderFactory.Create(execution);
        if (!provider.IsConfigured)
        {
            var notConfigured = new SkillRunResult { Succeeded = false, Message = "尚未配置图像模型。请在“设置”中填写 ComfyUI 地址与 checkpoint，或填写图像模型名称。" };
            MessageBox.Show(notConfigured.Message, "运行技能");
            return notConfigured;
        }

        using var progressForm = new ScaledForm
        {
            Text = $"运行 {skill.Name}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ControlBox = false,
            ClientSize = new Size(420, 110),
            Font = Font
        };
        var progressLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "准备中…" };
        progressForm.Controls.Add(progressLabel);
        ShowWaitingForm(progressForm, null);

        SkillRunResult result;
        try
        {
            result = await SkillRunner.RunAsync(skill, target, provider, message =>
            {
                if (progressForm.IsDisposed) return;
                progressForm.BeginInvoke(() => { if (!progressForm.IsDisposed) progressLabel.Text = message; });
            });
        }
        finally
        {
            progressForm.Close();
        }

        if (result.Succeeded) RefreshCanvas();
        var hint = !result.Succeeded ? string.Empty
            : string.Equals(skill.OutputTarget, "node-base", StringComparison.OrdinalIgnoreCase)
                ? "\n\n合成结果已作为该镜头的出图底图，出图时会优先使用它。"
                : string.Equals(skill.OutputTarget, "node", StringComparison.OrdinalIgnoreCase)
                    ? "\n\n产出已写入该节点的附件。"
                    : "\n\n产出已写入该变体当前内容的参考图。到变体编辑的「版本」页提交新版本后，才能被节点引用锁定。";
        MessageBox.Show(result.Message + hint, "运行技能", MessageBoxButtons.OK, result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return result;
    }

    /// <summary>选择要运行的技能，并展示它的步骤说明。</summary>
    private SkillDefinition? PickSkill(IReadOnlyList<SkillDefinition> skills, WorkflowEntity entity)
    {
        using var dialog = new ScaledForm { Text = $"运行技能 · {entity.Name}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(580, 440), Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true };
        list.Columns.Add("技能", 200); list.Columns.Add("步骤", 60); list.Columns.Add("适用", 80); list.Columns.Add("版本", 70);
        var detail = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 252) };
        var confirm = new Button { Text = "运行", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

        void ShowDetail()
        {
            if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not SkillDefinition skill)
            {
                detail.Text = "选择一个技能查看步骤。";
                return;
            }
            var lines = new List<string> { skill.Name, skill.Description, string.Empty };
            for (var index = 0; index < skill.Steps.Count; index++)
            {
                var step = skill.Steps[index];
                var reference = string.IsNullOrWhiteSpace(step.ReferenceFrom)
                    ? "文生图"
                    : string.Equals(step.ReferenceFrom, "variant", StringComparison.OrdinalIgnoreCase) ? "以变体参考图为底图" : $"以步骤 {step.ReferenceFrom} 的产出为底图";
                lines.Add($"{index + 1}. {step.Name}（{step.Capability}，{reference}）");
            }
            detail.Text = string.Join("\n", lines);
        }

        list.BeginUpdate();
        foreach (var skill in skills)
        {
            var item = new ListViewItem(skill.Name);
            item.SubItems.Add(skill.Steps.Count.ToString());
            item.SubItems.Add(skill.TargetKind);
            item.SubItems.Add(skill.Version);
            item.Tag = skill;
            list.Items.Add(item);
        }
        list.EndUpdate();
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        list.SelectedIndexChanged += (_, _) => ShowDetail();
        list.DoubleClick += (_, _) => dialog.DialogResult = DialogResult.OK;
        confirm.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        ShowDetail();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(confirm);
        dialog.Controls.Add(list); dialog.Controls.Add(detail); dialog.Controls.Add(buttons);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as SkillDefinition : null;
    }

    private void CreateVariant()
    {
        if (SelectedEntity is not { } entity) { MessageBox.Show("请先选择一个实体。", "设定库"); return; }
        var variant = entity.CreateVariant($"变体 {entity.Variants.Count + 1}");
        if (!OpenVariantEditor(entity, variant))
        {
            entity.Variants.Remove(variant);
            return;
        }
        canvas.NotifyContentChanged();
        RefreshVariantList();
        SelectInList(variantList, variant);
    }

    private void EditSelectedVariant()
    {
        if (SelectedEntity is not { } entity || SelectedVariant is not { } variant)
        {
            MessageBox.Show("请先选择一个变体。", "设定库");
            return;
        }
        if (!OpenVariantEditor(entity, variant)) return;
        canvas.InvalidateThumbnails();
        canvas.NotifyContentChanged();
        RefreshVariantList();
        SelectInList(variantList, variant);
    }

    private void DeleteSelectedVariant()
    {
        if (SelectedEntity is not { } entity || SelectedVariant is not { } variant)
        {
            MessageBox.Show("请先选择一个变体。", "设定库");
            return;
        }
        if (entity.Variants.Count <= 1) { MessageBox.Show("每个实体至少保留一个变体。", "设定库"); return; }
        if (MessageBox.Show($"删除变体「{variant.Name}」？", "删除变体", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        entity.Variants.Remove(variant);
        canvas.NotifyContentChanged();
        RefreshVariantList();
    }

    /// <summary>编辑实体本身：种类、名称、别名与跨章节不变的核心设定。</summary>
    private bool OpenEntityEditor(WorkflowEntity entity)
    {
        using var dialog = new ScaledForm { Text = "实体设定", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(480, 430), Font = Font };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[1] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[3] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[5] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[7] = new RowStyle(SizeType.Absolute, 160);
        layout.RowStyles[8] = new RowStyle(SizeType.Absolute, 40);

        var kind = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        kind.Items.AddRange([WorkflowEntity.KindName(EntityKind.Character), WorkflowEntity.KindName(EntityKind.Scene), WorkflowEntity.KindName(EntityKind.Prop)]);
        kind.SelectedIndex = (int)entity.Kind;
        var nameBox = new TextBox { Dock = DockStyle.Fill, Text = entity.Name };
        var aliasesBox = new TextBox { Dock = DockStyle.Fill, Text = entity.Aliases, PlaceholderText = "可选，用、分隔，例如：小明、明哥" };
        var coreBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = entity.Core, PlaceholderText = "身份、性格、面相基准或地形骨架等不随章节变化的部分" };

        layout.Controls.Add(FieldLabel("种类"), 0, 0);
        layout.Controls.Add(kind, 0, 1);
        layout.Controls.Add(FieldLabel("名称"), 0, 2);
        layout.Controls.Add(nameBox, 0, 3);
        layout.Controls.Add(FieldLabel("别名"), 0, 4);
        layout.Controls.Add(aliasesBox, 0, 5);
        layout.Controls.Add(FieldLabel("核心设定"), 0, 6);
        layout.Controls.Add(coreBox, 0, 7);

        var save = new Button { Text = "保存", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { MessageBox.Show("名称不能为空。", "实体设定"); return; }
            var wasScene = entity.Kind == EntityKind.Scene;
            entity.Kind = (EntityKind)Math.Max(0, kind.SelectedIndex);
            entity.Name = nameBox.Text.Trim();
            entity.Aliases = aliasesBox.Text.Trim();
            entity.Core = coreBox.Text.Trim();
            if (entity.Kind == EntityKind.Scene && !wasScene)
                foreach (var variant in entity.Variants) variant.Layout ??= new SceneLayout();
            dialog.DialogResult = DialogResult.OK;
        };
        layout.Controls.Add(save, 0, 8);
        dialog.Controls.Add(layout);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    /// <summary>
    /// 编辑变体分成两页：「内容」管理名称、差异描述与场景空间布局；「版本」提交新版本并查看历史。
    /// 提交版本是独立动作，会立即生效；「保存」只写回内容页的编辑。
    /// </summary>
    private bool OpenVariantEditor(WorkflowEntity entity, WorkflowEntityVariant variant)
    {
        var isScene = entity.Kind == EntityKind.Scene;
        if (isScene) variant.Layout ??= new SceneLayout();
        variant.EnsureInitialVersion();

        using var dialog = new ScaledForm
        {
            Text = $"变体设定 · {entity.Name}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(640, isScene ? 700 : 540),
            Font = Font
        };

        var nameBox = new TextBox { Dock = DockStyle.Fill, Text = variant.Name, PlaceholderText = "例如：少年黑衣 / 战时宗门" };
        var descriptionBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = variant.Description, PlaceholderText = "只写与核心设定的差异，例如：16 岁，黑衣束发，眼神锐利" };
        var overviewBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = variant.Layout?.Overview ?? string.Empty, PlaceholderText = "整体布局，例如：三进院落，背山面水，中轴对齐" };
        var itemList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false, HideSelection = false };
        itemList.Columns.Add("方位", 110); itemList.Columns.Add("方位细化", 110); itemList.Columns.Add("元素", 120); itemList.Columns.Add("补充说明", 220);
        var preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 252) };
        var versionStatus = new Label { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        var commitButton = new Button { Text = "提交新版本", Width = 108, Height = 30, FlatStyle = FlatStyle.Flat };
        var commitHint = new Label { Text = "提交后该版本内容固定，引用可锁定到它", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 8, 0, 0) };
        var rollbackButton = new Button { Text = "回滚到此版本", Width = 112, Height = 30, FlatStyle = FlatStyle.Flat };
        var rollbackHint = new Label { Text = "详情区显示该版本与当前内容的逐项差异", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 8, 0, 0) };
        var versionList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false, HideSelection = false };
        versionList.Columns.Add("版本", 70); versionList.Columns.Add("提交时间", 120); versionList.Columns.Add("说明", 200); versionList.Columns.Add("参考图", 70);
        var versionDetail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 252) };
        var save = new Button { Text = "保存", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

        void RefreshPreview()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(descriptionBox.Text)) parts.Add(descriptionBox.Text.Trim());
            if (isScene && variant.Layout is { } current)
            {
                var snapshot = new SceneLayout { Overview = overviewBox.Text.Trim(), Items = current.Items };
                if (!snapshot.IsEmpty) parts.Add(snapshot.ToPromptText());
            }
            preview.Text = parts.Count == 0 ? "（尚未填写内容）" : string.Join("\n", parts);
        }

        void RefreshItems()
        {
            itemList.BeginUpdate();
            itemList.Items.Clear();
            foreach (var item in variant.Layout?.Items ?? new List<SceneLayoutItem>())
            {
                var row = new ListViewItem(item.DescribeDirection());
                row.SubItems.Add(item.DirectionNote);
                row.SubItems.Add(item.Element);
                row.SubItems.Add(item.Note);
                row.Tag = item;
                itemList.Items.Add(row);
            }
            itemList.EndUpdate();
            RefreshPreview();
        }

        void RefreshVersionTab()
        {
            versionStatus.Text = variant.HasUncommittedChanges
                ? $"当前 {variant.CurrentVersionLabel} · 有未提交改动"
                : $"当前 {variant.CurrentVersionLabel} · 已提交";
            versionStatus.ForeColor = variant.HasUncommittedChanges ? Color.FromArgb(196, 128, 32) : Color.FromArgb(40, 140, 90);
            versionList.BeginUpdate();
            versionList.Items.Clear();
            foreach (var version in variant.Versions.OrderByDescending(item => item.Number))
            {
                var row = new ListViewItem(version.Label);
                row.SubItems.Add(version.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"));
                row.SubItems.Add(string.IsNullOrWhiteSpace(version.Note) ? "（无说明）" : version.Note);
                row.SubItems.Add($"{version.Attachments.Count}");
                row.Tag = version;
                versionList.Items.Add(row);
            }
            versionList.EndUpdate();
            if (versionList.Items.Count > 0) versionList.Items[0].Selected = true;
            ShowVersionDetail();
        }

        void ShowVersionDetail()
        {
            if (versionList.SelectedItems.Count == 0 || versionList.SelectedItems[0].Tag is not EntityVariantVersion version)
            {
                versionDetail.Text = "选择一条版本查看内容与差异。";
                rollbackButton.Enabled = false;
                return;
            }
            rollbackButton.Enabled = true;
            var lines = new List<string>
            {
                $"{version.Label} · {version.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                $"说明：{(string.IsNullOrWhiteSpace(version.Note) ? "（无）" : version.Note)}",
                string.IsNullOrWhiteSpace(version.Description) ? "差异描述：（空）" : $"差异描述：{version.Description}"
            };
            if (version.Layout is { IsEmpty: false } layout) lines.Add(layout.ToPromptText());
            if (version.Attachments.Count > 0) lines.Add($"参考图：{version.Attachments.Count} 张");
            lines.Add(string.Empty);
            lines.Add($"与当前内容的差异（回滚后将变成 {version.Label} 的内容）：");
            var differences = VersionDiff.CompareToCurrent(version, variant);
            if (differences.Count == 0) lines.Add("· 没有差异");
            else lines.AddRange(differences.Take(12).Select(item => "· " + item.Describe()));
            if (differences.Count > 12) lines.Add($"· 其余 {differences.Count - 12} 项省略");
            versionDetail.Text = string.Join("\n", lines);
        }

        /// <summary>把变体当前内容重新填回内容页控件，用于回滚之后同步界面。</summary>
        void ReloadContentFields()
        {
            nameBox.Text = variant.Name;
            descriptionBox.Text = variant.Description;
            overviewBox.Text = variant.Layout?.Overview ?? string.Empty;
            RefreshItems();
            RefreshVersionTab();
        }

        /// <summary>把当前内容回滚到选中版本；回滚前先列出与当前内容的逐项差异。</summary>
        void RollbackVersion()
        {
            if (versionList.SelectedItems.Count == 0 || versionList.SelectedItems[0].Tag is not EntityVariantVersion version)
            {
                MessageBox.Show("请先选择要回滚到的版本。", "回滚版本");
                return;
            }
            if (!variant.HasUncommittedChanges)
            {
                MessageBox.Show("当前内容与最后一次提交一致，没有需要回滚的改动。", "回滚版本");
                return;
            }
            var differences = VersionDiff.CompareToCurrent(version, variant);
            var summary = differences.Count == 0
                ? "（没有差异）"
                : string.Join("\n", differences.Take(8).Select(item => "· " + item.Describe()));
            if (MessageBox.Show(
                    $"把当前内容改成 {version.Label}？\n\n差异如下：\n{summary}\n\n" +
                    "已提交的版本不会被删除；回滚后当前内容会标记为「有未提交改动」。",
                    "回滚版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            variant.RollbackTo(version);
            ReloadContentFields();
            MessageBox.Show($"已回滚到 {version.Label}。确认无误后可点「提交新版本」固化下来。", "回滚版本");
        }

        void CommitVersion()
        {
            var note = ShowTextInput("提交新版本", "这一版改了什么（可留空）", string.Empty);
            if (note is null) return;
            // 先把内容页的编辑写回变体，保证快照包含刚改的内容。
            variant.Name = nameBox.Text.Trim().Length == 0 ? variant.Name : nameBox.Text.Trim();
            variant.Description = descriptionBox.Text.Trim();
            if (variant.Layout is not null) variant.Layout.Overview = overviewBox.Text.Trim();
            var version = variant.Commit(note);
            RefreshVersionTab();
            RefreshPreview();
            MessageBox.Show($"已提交 {version.Label}。锁定了旧版本的引用不受影响，需要时可在节点上用「升级到最新」。", "提交版本");
        }

        void MoveItem(int offset)
        {
            if (itemList.SelectedItems.Count == 0 || itemList.SelectedItems[0].Tag is not SceneLayoutItem item) return;
            var items = variant.Layout?.Items;
            if (items is null) return;
            var index = items.IndexOf(item);
            var target = index + offset;
            if (index < 0 || target < 0 || target >= items.Count) return;
            items.RemoveAt(index); items.Insert(target, item);
            RefreshItems();
            if (target < itemList.Items.Count) itemList.Items[target].Selected = true;
        }

        void EditItem()
        {
            if (itemList.SelectedItems.Count == 0 || itemList.SelectedItems[0].Tag is not SceneLayoutItem item)
            {
                MessageBox.Show("请先选择一条布局元素。", "空间布局");
                return;
            }
            if (OpenLayoutItemEditor(item)) RefreshItems();
        }

        var itemButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        itemButtons.Controls.Add(CreatePaneButton("添加元素", 92, (_, _) =>
        {
            variant.Layout ??= new SceneLayout();
            var item = new SceneLayoutItem();
            if (!OpenLayoutItemEditor(item)) return;
            variant.Layout.Items.Add(item);
            RefreshItems();
        }));
        itemButtons.Controls.Add(CreatePaneButton("编辑元素", 92, (_, _) => EditItem()));
        itemButtons.Controls.Add(CreatePaneButton("删除元素", 92, (_, _) =>
        {
            if (itemList.SelectedItems.Count == 0 || itemList.SelectedItems[0].Tag is not SceneLayoutItem item) return;
            variant.Layout?.Items.Remove(item);
            RefreshItems();
        }));
        itemButtons.Controls.Add(CreatePaneButton("上移", 68, (_, _) => MoveItem(-1)));
        itemButtons.Controls.Add(CreatePaneButton("下移", 68, (_, _) => MoveItem(1)));
        itemList.DoubleClick += (_, _) => EditItem();
        descriptionBox.TextChanged += (_, _) => RefreshPreview();
        overviewBox.TextChanged += (_, _) => RefreshPreview();
        commitButton.Click += (_, _) => CommitVersion();
        rollbackButton.Click += (_, _) => RollbackVersion();
        versionList.SelectedIndexChanged += (_, _) => ShowVersionDetail();
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { MessageBox.Show("变体名称不能为空。", "变体设定"); return; }
            variant.Name = nameBox.Text.Trim();
            variant.Description = descriptionBox.Text.Trim();
            if (variant.Layout is not null) variant.Layout.Overview = overviewBox.Text.Trim();
            dialog.DialogResult = DialogResult.OK;
        };

        var contentLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = Padding.Empty, Margin = Padding.Empty };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 11; row++) contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles[1] = new RowStyle(SizeType.Absolute, 28);
        contentLayout.RowStyles[3] = new RowStyle(SizeType.Absolute, 108);
        contentLayout.RowStyles[5] = new RowStyle(SizeType.Absolute, 56);
        contentLayout.RowStyles[7] = new RowStyle(SizeType.Absolute, 170);
        contentLayout.RowStyles[8] = new RowStyle(SizeType.Absolute, 36);
        contentLayout.RowStyles[10] = new RowStyle(SizeType.Absolute, 110);
        if (!isScene)
            foreach (var index in new[] { 4, 5, 6, 7, 8 }) contentLayout.RowStyles[index] = new RowStyle(SizeType.Absolute, 0);
        contentLayout.Controls.Add(FieldLabel("变体名称"), 0, 0);
        contentLayout.Controls.Add(nameBox, 0, 1);
        contentLayout.Controls.Add(FieldLabel("差异描述"), 0, 2);
        contentLayout.Controls.Add(descriptionBox, 0, 3);
        contentLayout.Controls.Add(FieldLabel("空间布局概述"), 0, 4);
        contentLayout.Controls.Add(overviewBox, 0, 5);
        contentLayout.Controls.Add(FieldLabel("布局元素（按方位）"), 0, 6);
        contentLayout.Controls.Add(itemList, 0, 7);
        contentLayout.Controls.Add(itemButtons, 0, 8);
        contentLayout.Controls.Add(FieldLabel("提示词预览"), 0, 9);
        contentLayout.Controls.Add(preview, 0, 10);

        var contentPage = new TabPage("内容") { Padding = new Padding(12), BackColor = Color.White, UseVisualStyleBackColor = true };
        contentPage.Controls.Add(contentLayout);

        var commitRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        commitRow.Controls.Add(commitButton); commitRow.Controls.Add(commitHint);
        var versionActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        versionActions.Controls.Add(rollbackButton); versionActions.Controls.Add(rollbackHint);
        var versionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = Padding.Empty, Margin = Padding.Empty };
        versionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        versionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        versionLayout.Controls.Add(versionStatus, 0, 0);
        versionLayout.Controls.Add(commitRow, 0, 1);
        versionLayout.Controls.Add(FieldLabel("版本历史"), 0, 2);
        versionLayout.Controls.Add(versionList, 0, 3);
        versionLayout.Controls.Add(versionActions, 0, 4);
        versionLayout.Controls.Add(versionDetail, 0, 5);

        var versionPage = new TabPage("版本") { Padding = new Padding(12), BackColor = Color.White, UseVisualStyleBackColor = true };
        versionPage.Controls.Add(versionLayout);

        var imageBox = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true, HideSelection = false };
        imageBox.Columns.Add("类型", 60); imageBox.Columns.Add("名称", 236); imageBox.Columns.Add("来源", 110);
        var imageHint = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.Gray,
            Text = "参考图属于变体的当前内容，提交版本时会一并快照进该版本；出图时可用来保持角色一致。"
        };
        var addImageButton = new Button { Text = "添加图片", Width = 96, Height = 30, FlatStyle = FlatStyle.Flat };
        var removeImageButton = new Button { Text = "移除", Width = 76, Height = 30, FlatStyle = FlatStyle.Flat };
        var openImageButton = new Button { Text = "打开", Width = 76, Height = 30, FlatStyle = FlatStyle.Flat };

        void RefreshImages()
        {
            imageBox.BeginUpdate();
            imageBox.Items.Clear();
            foreach (var attachment in variant.Attachments)
            {
                var row = new ListViewItem(WorkflowAttachment.DisplayName(attachment.Kind));
                row.SubItems.Add(string.IsNullOrWhiteSpace(attachment.Name) ? Path.GetFileName(attachment.Reference) : attachment.Name);
                row.SubItems.Add(attachment.Source);
                row.Tag = attachment;
                imageBox.Items.Add(row);
            }
            imageBox.EndUpdate();
            removeImageButton.Enabled = variant.Attachments.Count > 0;
        }

        addImageButton.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "选择参考图", Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件|*.*" };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            var added = 0;
            var failed = new List<string>();
            try
            {
                var directory = AssetStore.EnsureDirectory();
                foreach (var source in picker.FileNames)
                {
                    try
                    {
                        var target = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(source)}");
                        File.Copy(source, target, overwrite: false);
                        variant.Attachments.Add(new WorkflowAttachment
                        {
                            Kind = WorkflowAttachment.KindOf(source),
                            Reference = AssetStore.ToReference(target),
                            Name = Path.GetFileName(source),
                            Source = "手动添加"
                        });
                        added++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add(Path.GetFileName(source)); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(error.Message, "添加参考图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RefreshImages();
            var detail = added == 0 ? "没有图片被添加。" : $"已添加 {added} 张参考图。记得点「提交新版本」把这次改动固化下来。";
            if (failed.Count > 0) detail += $"\n\n失败：{string.Join("、", failed.Take(5))}";
            MessageBox.Show(detail, "添加参考图", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };
        removeImageButton.Click += (_, _) =>
        {
            var targets = imageBox.SelectedItems.Cast<ListViewItem>()
                .Select(row => row.Tag as WorkflowAttachment)
                .Where(attachment => attachment is not null)
                .Select(attachment => attachment!)
                .ToList();
            if (targets.Count == 0) { MessageBox.Show("请先选择要移除的参考图。", "移除参考图"); return; }
            if (MessageBox.Show($"从当前内容移除 {targets.Count} 张参考图？已提交版本里的图不受影响。",
                    "移除参考图", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var attachment in targets) variant.Attachments.Remove(attachment);
            RefreshImages();
        };
        openImageButton.Click += (_, _) =>
        {
            if (imageBox.SelectedItems.Count == 0 || imageBox.SelectedItems[0].Tag is not WorkflowAttachment attachment) return;
            var path = AssetStore.Resolve(attachment.Reference);
            if (path is null) { MessageBox.Show("该参考图文件不存在。", "打开参考图", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };

        var imageButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        imageButtons.Controls.Add(addImageButton); imageButtons.Controls.Add(removeImageButton); imageButtons.Controls.Add(openImageButton);
        var imageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
        imageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        imageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        imageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        imageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        imageLayout.Controls.Add(imageHint, 0, 0);
        imageLayout.Controls.Add(imageBox, 0, 1);
        imageLayout.Controls.Add(imageButtons, 0, 2);
        var imagePage = new TabPage("参考图") { Padding = new Padding(12), BackColor = Color.White, UseVisualStyleBackColor = true };
        imagePage.Controls.Add(imageLayout);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(contentPage);
        tabs.TabPages.Add(imagePage);
        tabs.TabPages.Add(versionPage);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        footer.Controls.Add(cancel); footer.Controls.Add(save);
        dialog.Controls.Add(tabs);
        dialog.Controls.Add(footer);
        dialog.CancelButton = cancel;

        RefreshItems();
        RefreshImages();
        RefreshVersionTab();
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    /// <summary>编辑一条布局元素：方位、方位细化、元素与补充说明。</summary>
    private bool OpenLayoutItemEditor(SceneLayoutItem item)
    {
        using var dialog = new ScaledForm { Text = "布局元素", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(440, 340), Font = Font };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[1] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[3] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[5] = new RowStyle(SizeType.Absolute, 28);
        layout.RowStyles[7] = new RowStyle(SizeType.Absolute, 64);
        layout.RowStyles[8] = new RowStyle(SizeType.Absolute, 44);

        var directions = SceneLayoutItem.AllDirections;
        var direction = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        direction.Items.AddRange(directions.Select(SceneLayoutItem.DirectionName).Cast<object>().ToArray());
        direction.SelectedIndex = Math.Max(0, Array.IndexOf(directions, item.Direction));
        var directionNote = new TextBox { Dock = DockStyle.Fill, Text = item.DirectionNote, PlaceholderText = "可选，例如：右侧偏后" };
        var element = new TextBox { Dock = DockStyle.Fill, Text = item.Element, PlaceholderText = "例如：茅草屋" };
        var note = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = item.Note, PlaceholderText = "可选，例如：距大殿约五十步" };

        layout.Controls.Add(FieldLabel("方位"), 0, 0);
        layout.Controls.Add(direction, 0, 1);
        layout.Controls.Add(FieldLabel("方位细化"), 0, 2);
        layout.Controls.Add(directionNote, 0, 3);
        layout.Controls.Add(FieldLabel("元素"), 0, 4);
        layout.Controls.Add(element, 0, 5);
        layout.Controls.Add(FieldLabel("补充说明"), 0, 6);
        layout.Controls.Add(note, 0, 7);

        var save = new Button { Text = "确定", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(element.Text)) { MessageBox.Show("元素不能为空。", "布局元素"); return; }
            item.Direction = directions[Math.Max(0, direction.SelectedIndex)];
            item.DirectionNote = directionNote.Text.Trim();
            item.Element = element.Text.Trim();
            item.Note = note.Text.Trim();
            dialog.DialogResult = DialogResult.OK;
        };
        layout.Controls.Add(save, 0, 8);
        dialog.Controls.Add(layout);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private void RefreshCanvasLibrary()
    {
        canvasList.BeginUpdate();
        canvasList.Items.Clear();
        foreach (var summary in CanvasLibrary.List())
        {
            var item = new ListViewItem(summary.Title);
            item.SubItems.Add($"修订 {summary.Revision}");
            item.Tag = summary.Path;
            if (string.Equals(summary.Path, currentCanvasPath, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            canvasList.Items.Add(item);
        }
        canvasList.EndUpdate();
        var isEmpty = canvasList.Items.Count == 0;
        canvasList.Visible = !isEmpty;
        canvasLibraryHint.Visible = isEmpty;
        UpdateCanvasTitleLabel();
    }

    /// <summary>顶栏标题与画布标签：未保存到画布库时明确标出，避免与库中画布混淆。</summary>
    private void UpdateCanvasTitleLabel()
    {
        var text = string.IsNullOrWhiteSpace(currentCanvasPath) ? $"{canvasTitle}（未保存）" : canvasTitle;
        canvasTitleLabel.Text = text;
        if (activeCanvasTab is not null)
        {
            activeCanvasTab.Title = canvasTitle;
            activeCanvasTab.Path = currentCanvasPath;
            activeCanvasTab.Snapshot = BuildCanvasState();
            if (activeCanvasTab.TitleControl is not null)
                activeCanvasTab.TitleControl.Text = string.IsNullOrWhiteSpace(currentCanvasPath) ? $"{canvasTitle} *" : canvasTitle;
        }
        UpdateCanvasTabAppearance();
    }

    private string? SelectedLibraryPath =>
        canvasList.SelectedItems.Count > 0 ? canvasList.SelectedItems[0].Tag as string : null;

    /// <summary>更新当前画布与画布库文件的绑定，并持久化以便下次启动恢复。</summary>
    private void SetCurrentCanvasPath(string? path)
    {
        currentCanvasPath = path;
        CanvasLibrary.SaveCurrentCanvasPath(path);
    }

    /// <summary>关闭窗口时把已归属画布库的画布写回其文件，未保存过的新画布只留在最近画布草稿里。</summary>
    private void SaveCanvasToLibraryFile()
    {
        if (string.IsNullOrWhiteSpace(currentCanvasPath)) return;
        try { CanvasLibrary.Save(BuildCanvasState(), currentCanvasPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void SaveCanvasToLibrary(bool showResult = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentCanvasPath))
            {
                var conflict = CanvasLibrary.FindByTitle(canvasTitle);
                if (conflict is not null && MessageBox.Show(
                        $"画布库中已存在“{canvasTitle}”，继续会覆盖该画布。是否覆盖？",
                        "同名画布", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            var applied = pendingChanges.Count;
            SetCurrentCanvasPath(CanvasLibrary.Save(BuildCanvasState(), currentCanvasPath));
            pendingChanges.Clear();
            canvas.SetPreview(null);
            RefreshPendingState();
            RefreshCanvasLibrary();
            var saved = $"已保存到 {currentCanvasPath}";
            if (applied > 0) saved += $"\n（含 {applied} 条 Agent 改动，可用工具栏的「撤销上次 Agent 提交」回退）";
            if (showResult) MessageBox.Show(saved, "保存画布");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenSelectedLibraryCanvas()
    {
        var path = SelectedLibraryPath;
        if (path is null) { MessageBox.Show("请先在画布库中选择一个画布。", "打开画布"); return; }
        if (!CanvasLibrary.TryLoad(path, out var state) || state is null) { MessageBox.Show("画布文件无法读取。", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        ApplyCanvasState(state, advanceRevision: false);
        SetCurrentCanvasPath(path);
        SaveRecentCanvas();
        RefreshCanvasLibrary();
    }

    private void DeleteSelectedLibraryCanvas()
    {
        var path = SelectedLibraryPath;
        if (path is null) { MessageBox.Show("请先在画布库中选择一个画布。", "删除画布"); return; }
        if (MessageBox.Show($"确定删除画布文件 {Path.GetFileName(path)}？该操作不会删除已生成的图片资产。", "删除画布", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        CanvasLibrary.TryLoad(path, out var removedState);
        try
        {
            CanvasLibrary.Delete(path);
            if (string.Equals(path, currentCanvasPath, StringComparison.OrdinalIgnoreCase)) SetCurrentCanvasPath(null);
            RefreshCanvasLibrary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (removedState?.Canvas is not null)
            OfferRecycleOrphanedAssets(CanvasPackage.CollectReferences(removedState.Canvas).ToList());
    }

    /// <summary>加载画布库与草稿，作为引用统计的已知画布集合。</summary>
    private IReadOnlyList<WorkflowCanvasState> CollectKnownCanvases()
    {
        var canvases = new List<WorkflowCanvasState>();
        foreach (var summary in CanvasLibrary.List())
            if (CanvasLibrary.TryLoad(summary.Path, out var state) && state?.Canvas is not null)
                canvases.Add(state.Canvas);
        if (CanvasLibrary.TryLoad(StorageMaintenance.DraftCanvasPath, out var draft) && draft?.Canvas is not null)
            canvases.Add(draft.Canvas);
        return canvases;
    }

    /// <summary>
    /// 节点被删除后检查其图片资产：仍被其它节点、生成历史或其它画布引用时不动文件；
    /// 只有最后一个引用消失时才询问是否移入回收站。
    /// </summary>
    private void OfferRecycleOrphanedAssets(IReadOnlyList<string> removedReferences)
    {
        if (removedReferences.Count == 0) return;
        var known = CollectKnownCanvases().Append(canvas.State).ToArray();
        var orphaned = StorageMaintenance.FindUnreferenced(removedReferences, known);
        if (orphaned.Count == 0) return;

        var names = string.Join("、", orphaned.Take(5).Select(reference => Path.GetFileName(reference)));
        var more = orphaned.Count > 5 ? $" 等 {orphaned.Count} 个文件" : string.Empty;
        if (MessageBox.Show(
                $"以下文件已不再被任何画布引用：\n{names}{more}\n\n是否移入回收站？（可在回收站中手动清空）",
                "清理文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var moved = orphaned.Count(AssetStore.MoveToRecycleBin);
        canvas.InvalidateThumbnails();
        MessageBox.Show(moved > 0 ? $"已移入回收站 {moved} 个文件。" : "没有文件被移动。", "清理文件");
    }

    /// <summary>已知画布及其显示名，用于资产引用统计。</summary>
    private IReadOnlyList<(string Title, WorkflowCanvasState Canvas)> CollectNamedCanvases()
    {
        var canvases = new List<(string, WorkflowCanvasState)>();
        foreach (var summary in CanvasLibrary.List())
            if (CanvasLibrary.TryLoad(summary.Path, out var state) && state?.Canvas is not null)
                canvases.Add((summary.Title, state.Canvas));
        if (CanvasLibrary.TryLoad(StorageMaintenance.DraftCanvasPath, out var draft) && draft?.Canvas is not null)
            canvases.Add(("最近画布草稿", draft.Canvas));
        canvases.Add(($"{canvasTitle}（当前）", canvas.State));
        return canvases;
    }

    private void OpenStorageDialog()
    {
        using var dialog = new ScaledForm { Text = "存储与清理", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(820, 480), Font = Font };
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var locationsPage = new TabPage("存储位置");
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("位置", 110); list.Columns.Add("路径与占用", 640);
        void AddRow(string label, StorageUsage usage)
        {
            if (usage.FileCount == 0 && !File.Exists(usage.Path) && !Directory.Exists(usage.Path)) usage = usage with { Path = $"{usage.Path}（尚未创建）" };
            var item = new ListViewItem(label);
            item.SubItems.Add(usage.Display);
            item.Tag = usage.Path;
            list.Items.Add(item);
        }
        AddRow("画布库", StorageMaintenance.Measure(CanvasLibrary.Directory, "*.json"));
        AddRow("图片资产", StorageMaintenance.Measure(AssetStore.Directory));
        AddRow("任务记录", StorageMaintenance.Measure(StorageMaintenance.JobDatabasePath));
        AddRow("最近画布草稿", StorageMaintenance.Measure(StorageMaintenance.DraftCanvasPath));
        AddRow("配置文件", StorageMaintenance.Measure(StorageMaintenance.ConfigPath));

        var assetsPage = new TabPage("图片资产");
        var assetList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        assetList.Columns.Add("文件", 250); assetList.Columns.Add("大小", 80); assetList.Columns.Add("引用数", 70); assetList.Columns.Add("被哪些画布引用", 400);
        var assetButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };

        void RefreshAssetList()
        {
            var assets = StorageMaintenance.ListAssets(CollectNamedCanvases());
            assetList.BeginUpdate();
            assetList.Items.Clear();
            foreach (var asset in assets)
            {
                var item = new ListViewItem(asset.FileName);
                item.SubItems.Add(StorageUsage.Format(asset.Bytes));
                item.SubItems.Add(asset.ReferenceCount == 0 ? "无引用" : asset.ReferenceCount.ToString());
                item.SubItems.Add(asset.ReferencedBy.Count == 0 ? "-" : string.Join("、", asset.ReferencedBy));
                item.Tag = asset;
                assetList.Items.Add(item);
            }
            assetList.EndUpdate();
        }
        string? SelectedAssetFileName() =>
            assetList.SelectedItems.Count > 0 && assetList.SelectedItems[0].Tag is AssetReference asset ? asset.FileName : null;

        var locationButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var openFolder = new Button { Text = "打开所在目录", Width = 110, Height = 32 };
        openFolder.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) { MessageBox.Show("请先选择一行。", "存储与清理"); return; }
            var path = list.SelectedItems[0].Tag as string;
            var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            OpenInShell(directory);
        };
        var cleanAssets = new Button { Text = "清理无引用图片", Width = 124, Height = 32 };
        cleanAssets.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    "将把资产目录中不再被任何画布引用的图片移入回收站（可在回收站中手动清空）。是否继续？",
                    "清理无引用图片", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (moved, freed) = StorageMaintenance.RecycleUnreferencedAssets(canvas.State, CollectKnownCanvases());
            canvas.InvalidateThumbnails();
            MessageBox.Show(moved == 0 ? "没有需要清理的图片。" : $"已移入回收站 {moved} 个文件，释放 {StorageUsage.Format(freed)}。", "清理无引用图片");
            RefreshAssetList();
        };
        var clearJobs = new Button { Text = "清空任务记录", Width = 110, Height = 32 };
        clearJobs.Click += (_, _) =>
        {
            if (executionHost?.Store is not { } store) { MessageBox.Show("当前没有可清理的任务数据库。", "存储与清理"); return; }
            if (MessageBox.Show("将清空任务记录数据库中的全部记录（不影响画布与图片）。是否继续？", "清空任务记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                store.Clear();
                MessageBox.Show("任务记录已清空。已加载的历史 Job 需要重启程序后才会从列表消失。", "存储与清理");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                MessageBox.Show(error.Message, "清空失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        locationButtons.Controls.Add(openFolder); locationButtons.Controls.Add(cleanAssets); locationButtons.Controls.Add(clearJobs);
        locationsPage.Controls.Add(list); locationsPage.Controls.Add(locationButtons);

        var refresh = new Button { Text = "刷新", Width = 80, Height = 32 };
        refresh.Click += (_, _) => RefreshAssetList();
        var openAsset = new Button { Text = "打开图片", Width = 90, Height = 32 };
        openAsset.Click += (_, _) =>
        {
            var name = SelectedAssetFileName();
            if (name is null) { MessageBox.Show("请先选择一张图片。", "图片资产"); return; }
            OpenInShell(AssetStore.Resolve("asset://" + name));
        };
        var recycleAsset = new Button { Text = "移入回收站", Width = 110, Height = 32 };
        recycleAsset.Click += (_, _) =>
        {
            var name = SelectedAssetFileName();
            if (name is null) { MessageBox.Show("请先选择一张图片。", "图片资产"); return; }
            var reference = "asset://" + name;
            var count = StorageMaintenance.CountReferences(reference, CollectNamedCanvases().Select(entry => entry.Canvas));
            var warning = count > 0
                ? $"“{name}”仍被 {count} 处引用，移入回收站后相关节点会显示图片不可用。是否继续？"
                : $"“{name}”已无引用，将移入回收站（可在回收站中手动清空）。是否继续？";
            if (MessageBox.Show(warning, "移入回收站", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (!AssetStore.MoveToRecycleBin(reference)) { MessageBox.Show("文件未找到或无法移动。", "移入回收站", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            canvas.InvalidateThumbnails();
            RefreshAssetList();
        };
        assetButtons.Controls.Add(refresh); assetButtons.Controls.Add(openAsset); assetButtons.Controls.Add(recycleAsset);
        assetsPage.Controls.Add(assetList); assetsPage.Controls.Add(assetButtons);

        tabs.TabPages.Add(locationsPage); tabs.TabPages.Add(assetsPage);
        dialog.Controls.Add(tabs);
        RefreshAssetList();
        dialog.ShowDialog(this);
        RefreshCanvasLibrary();
    }

    private void OpenInShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
        {
            MessageBox.Show("路径不存在或尚未创建。", "存储与清理");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// 重命名画布：选中库中画布时改该画布；未选中时改当前画布标题。
    /// 属于画布库的文件会同步重命名文件，保持文件名与标题一致。
    /// </summary>
    private void RenameSelectedCanvas()
    {
        var path = SelectedLibraryPath;
        var isCurrent = path is not null && string.Equals(path, currentCanvasPath, StringComparison.OrdinalIgnoreCase);
        var initial = path is null || isCurrent
            ? canvasTitle
            : CanvasLibrary.TryLoad(path, out var state) && state is not null
                ? state.Title
                : Path.GetFileNameWithoutExtension(path);

        var newTitle = ShowTextInput("重命名画布", "画布名称", initial);
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        var excludesSelf = path is null || isCurrent ? currentCanvasPath : path;
        var conflict = CanvasLibrary.FindByTitle(newTitle, excludesSelf);
        if (conflict is not null && MessageBox.Show(
                $"画布库中已存在“{newTitle}”，继续会覆盖该画布。是否覆盖？",
                "同名画布", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            if (path is null || isCurrent) ApplyCurrentCanvasRename(newTitle, conflict is not null);
            else CanvasLibrary.Rename(path, newTitle, overwrite: conflict is not null);
            RefreshCanvasLibrary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "重命名失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>重命名当前画布；已绑定画布库文件时同步重命名文件。</summary>
    private void ApplyCurrentCanvasRename(string newTitle, bool overwrite)
    {
        canvasTitle = newTitle;
        UpdateCanvasTitleLabel();
        if (string.IsNullOrWhiteSpace(currentCanvasPath))
        {
            SaveRecentCanvas();
            return;
        }
        var saved = CanvasLibrary.Save(BuildCanvasState(), currentCanvasPath);
        SetCurrentCanvasPath(CanvasLibrary.Rename(saved, newTitle, overwrite) ?? saved);
    }

    /// <summary>简易文本输入对话框，取消或留空返回 null。</summary>
    private string? ShowTextInput(string title, string label, string initialValue)
    {
        using var dialog = new ScaledForm
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 130),
            Font = Font
        };
        var input = new TextBox { Text = initialValue, Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        var confirm = new Button { Text = "确定", Dock = DockStyle.Fill, Height = 30 };
        confirm.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        layout.Controls.Add(confirm, 0, 2);
        dialog.Controls.Add(layout);
        return dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }

    /// <summary>画布工作区：只有画布与一条细工具栏，下方不再固定任何面板。</summary>
    private Control BuildWorkspace()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.EditorBg, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildCanvasTabStrip(), 0, 0);
        root.Controls.Add(BuildCanvasToolbar(), 0, 1);
        canvas.Dock = DockStyle.Fill;
        canvas.StateChanged += (_, _) =>
        {
            canvasRevision++;
            canvasRevisionLabel.Text = $"修订 {canvasRevision}";
            RefreshNodeInspector();
            canvas.Invalidate();
            _ = PushCanvasToWebAsync("state-changed");
        };
        canvas.SelectionChanged += (_, _) => RefreshNodeInspector();
        canvas.NodeDoubleClicked += (_, _) => ShowNodePropertiesDialog();
        canvas.NodeDeleted += (_, node) => OfferRecycleOrphanedAssets(node.Attachments.Select(attachment => attachment.Reference).ToList());
        canvas.ContextMenuStrip = new ContextMenuStrip();
        canvas.ContextMenuStrip.Opening += (_, _) =>
        {
            var menu = canvas.ContextMenuStrip!;
            menu.Items.Clear();
            var point = canvas.PointToClient(Cursor.Position);
            var node = canvas.HitTestNode(point);
            if (node is not null) canvas.SelectNode(node);
            var target = node is null ? ContextTarget.CanvasBlank : ContextTarget.CanvasNode;
            if (node is not null)
            {
                var suggest = new ToolStripMenuItem("AI 结合整条节点树生成下游候选…");
                suggest.Click += async (_, _) => await GenerateNextNodeCandidatesAsync(node);
                menu.Items.Add(suggest);
                menu.Items.Add(new ToolStripSeparator());
            }
            var arrange = new ToolStripMenuItem("整理画布（按章节分块）")
            {
                Image = SystemIcons.Application.ToBitmap()
            };
            arrange.Click += (_, _) => canvas.AutoArrange();
            menu.Items.Add(arrange);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.AddRange(BuildPluginMenuItems(target,
                new ContextInfo { Target = target, Canvas = canvas.State, Node = node }).ToArray());
        };
        root.Controls.Add(canvas, 0, 2);
        workspaceRoot = root;
        return root;
    }

    /// <summary>
    /// 画布标签栏：像浏览器一样，当前画布是一个标签，标签后的「+」直接开新画布。
    /// 之前新建画布只能绕去「画布库」面板，入口太深——这里给它一个顺手的位置。
    /// </summary>
    private Control BuildCanvasTabStrip()
    {
        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.ChromeBg,
            Padding = new Padding(8, 5, 8, 0),
            Margin = Padding.Empty,
            AutoScroll = true
        };
        canvasTabStrip = strip;
        RebuildCanvasTabsUi();
        return strip;
    }

    private void RebuildCanvasTabsUi()
    {
        if (canvasTabStrip is null) return;
        canvasTabStrip.SuspendLayout();
        canvasTabStrip.Controls.Clear();
        foreach (var tabState in canvasTabs)
        {
            var tab = new Panel { Width = 220, Height = 27, BackColor = Theme.ChromeBg, Margin = new Padding(0, 0, 2, 0), Cursor = Cursors.Hand };
            var title = new Label { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 28, 0), Font = Theme.SmallFont, Cursor = Cursors.Hand };
            var close = new Button { Text = "×", Width = 24, Height = 25, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = Theme.TextMuted, Font = Theme.SmallFont, TabStop = false, Cursor = Cursors.Hand };
            close.FlatAppearance.BorderSize = 0;
            var underline = new Panel { Dock = DockStyle.Bottom, Height = 2 };
            title.Text = string.IsNullOrWhiteSpace(tabState.Path) ? $"{tabState.Title} *" : tabState.Title;
            title.DoubleClick += (_, _) => { ActivateCanvasTab(tabState); RenameSelectedCanvas(); };
            tab.Click += (_, _) => ActivateCanvasTab(tabState);
            title.Click += (_, _) => ActivateCanvasTab(tabState);
            close.Click += (_, _) => CloseCanvasTab(tabState);
            railTip.SetToolTip(title, "切换画布（双击重命名）");
            tab.Controls.Add(title);
            tab.Controls.Add(close);
            tab.Controls.Add(underline);
            tabState.TabControl = tab;
            tabState.TitleControl = title;
            tabState.CloseButton = close;
            tabState.Underline = underline;
            canvasTabStrip.Controls.Add(tab);
        }
        var add = new Button { Text = "+", Width = 27, Height = 27, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = Theme.TextMuted, Font = Theme.UiFontBold, Cursor = Cursors.Hand, TabStop = false, Margin = new Padding(2, 0, 0, 0) };
        add.FlatAppearance.BorderSize = 0;
        add.FlatAppearance.MouseOverBackColor = Theme.Hover;
        railTip.SetToolTip(add, "新建画布");
        add.Click += NewCanvasButton_Click;
        canvasTabStrip.Controls.Add(add);
        canvasTabStrip.ResumeLayout();
        UpdateCanvasTabAppearance();
    }

    private void UpdateCanvasTabAppearance()
    {
        foreach (var tab in canvasTabs)
        {
            var active = tab == activeCanvasTab;
            if (tab.TabControl is not null) tab.TabControl.BackColor = active ? Theme.EditorBg : Theme.ChromeBg;
            if (tab.TitleControl is not null) tab.TitleControl.ForeColor = active ? Theme.Text : Theme.TextMuted;
            if (tab.CloseButton is not null) tab.CloseButton.ForeColor = active ? Theme.TextMuted : Theme.TextDim;
            if (tab.Underline is not null) tab.Underline.BackColor = active ? Theme.Accent : Theme.ChromeBg;
        }
    }

    private Control BuildCanvasToolbar()
    {
        var toolbar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.EditorBg };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(10, 6, 10, 6), Margin = Padding.Empty };
        flow.Controls.Add(CreatePaneButton("+ 节点", 74, (_, _) => canvas.AddNode()));
        flow.Controls.Add(CreatePaneButton("添加附件", 84, (_, _) => AddAttachmentsToSelectedNode()));
        flow.Controls.Add(CreatePaneButton("示例模板", 84, (_, _) => canvas.ApplyTemplate()));
        flow.Controls.Add(CreatePaneButton("删除节点", 84, (_, _) => canvas.DeleteSelected()));
        flow.Controls.Add(CreatePaneButton("整理画布", 84, (_, _) => canvas.AutoArrange()));
        var zoom = new Label { Text = "100%", AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.SmallFont, Margin = new Padding(12, 7, 0, 0) };
        canvas.ZoomChanged += (_, _) => zoom.Text = $"{canvas.Zoom:P0}";
        flow.Controls.Add(zoom);
        // Agent 批次指示：面板收起时也要能看出画布上有已应用但未保存的改动。
        pendingLabel.AutoSize = true;
        pendingLabel.ForeColor = Theme.Accent;
        pendingLabel.Font = Theme.SmallFont;
        pendingLabel.Margin = new Padding(16, 7, 0, 0);
        pendingLabel.Cursor = Cursors.Hand;
        pendingLabel.Click += (_, _) => { ToggleAgentPane(); agentPaneControl?.SyncPending(); };
        railTip.SetToolTip(pendingLabel, "点击打开 Agent 面板处理这些改动");
        flow.Controls.Add(pendingLabel);
        // 撤销入口放在常驻工具栏：待提交列表在提交后会清空，撤销必须仍然可达。
        undoAgentButton.Text = "撤销上次 Agent 提交";
        undoAgentButton.Width = 148; undoAgentButton.Height = 26;
        undoAgentButton.FlatStyle = FlatStyle.Flat;
        undoAgentButton.FlatAppearance.BorderColor = Color.FromArgb(214, 214, 220);
        undoAgentButton.Margin = new Padding(16, 2, 0, 0);
        undoAgentButton.Click += (_, _) => UndoLastAgentCommit();
        flow.Controls.Add(undoAgentButton);
        toolbar.Controls.Add(flow);
        return toolbar;
    }

    /// <summary>刷新待提交指示、画布虚影与「撤销上次提交」的可用状态。</summary>
    private void RefreshPendingLabel()
    {
        pendingLabel.Text = pendingChanges.IsEmpty
            ? string.Empty
            : $"● {pendingChanges.Count} 项 Agent 虚影预览，待保存";

        var (available, reason) = UndoAvailability();
        undoAgentButton.Visible = lastAgentCommit is not null;
        undoAgentButton.Enabled = available;
        railTip.SetToolTip(undoAgentButton, available
            ? $"撤销 {lastAgentCommit!.CommittedAt:HH:mm:ss} 的提交（{lastAgentCommit.AppliedCount} 条改动）"
            : reason);
    }

    /// <summary>“任务与出图”面板：任务状态、创建图像任务与任务记录，全部收进抽屉，不再固定占位。</summary>
    private Control BuildWorkTreePane()
    {
        workTreeView = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.PanelBg, ForeColor = Theme.Text, HideSelection = false, ShowLines = true, ShowPlusMinus = true, ItemHeight = 28 };
        workTreeView.AfterSelect += (_, e) =>
        {
            var item = e.Node?.Tag as WorkTreeItem;
            RefreshWorkTreeDetails(item);
            HighlightWorkTreeItem(item);
            if (item is { Kind: WorkTreeKind.Chapter })
                _ = PushCanvasToWebAsync($"chapter-switched:{item.Name}");
        };
        workTreeMetaLabel = new Label { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true, Padding = new Padding(8, 8, 8, 4), ForeColor = Theme.TextMuted };
        workTreePromptBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.EditorBg, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
        workTreeAssetList = new ListView { Dock = DockStyle.Bottom, Height = 94, View = View.Details, FullRowSelect = true, GridLines = false, BackColor = Theme.PanelBg, ForeColor = Theme.Text, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        workTreeAssetList.Columns.Add("素材", 64);
        workTreeAssetList.Columns.Add("状态 / 路径", 230);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        details.Controls.Add(workTreeMetaLabel, 0, 0);
        details.Controls.Add(workTreePromptBox, 0, 1);
        details.Controls.Add(workTreeAssetList, 0, 2);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210, BackColor = Theme.Border };
        split.Panel1.Controls.Add(workTreeView);
        split.Panel2.Controls.Add(details);
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };
        host.Controls.Add(split);
        var pane = CreatePaneShell("工作树", host);
        RefreshWorkTreeView();
        return pane;
    }

    private void RefreshWorkTreeView()
    {
        if (workTreeView is null) return;
        workTreeView.BeginUpdate();
        workTreeView.Nodes.Clear();
        void Add(TreeNodeCollection nodes, Guid? parentId)
        {
            foreach (var item in canvas.State.WorkTree.Where(candidate => candidate.ParentId == parentId))
            {
                var versionState = string.IsNullOrWhiteSpace(item.Version)
                    ? string.Empty
                    : $"  · {FormatWorkTreeVersion(item)}{(IsCurrentWorkTreeVersion(item) ? " 当前" : " 旧版")}";
                var node = nodes.Add($"{item.Name}{versionState}");
                node.Tag = item;
                node.ForeColor = string.IsNullOrWhiteSpace(item.Version)
                    ? Theme.Text
                    : IsCurrentWorkTreeVersion(item) ? Theme.Success : Theme.TextMuted;
                Add(node.Nodes, item.Id);
                node.Expand();
            }
        }
        Add(workTreeView.Nodes, null);
        workTreeView.EndUpdate();
        var selectedItem = workTreeView.SelectedNode?.Tag as WorkTreeItem;
        RefreshWorkTreeDetails(selectedItem);
        HighlightWorkTreeItem(selectedItem);
    }

    private bool IsCurrentWorkTreeVersion(WorkTreeItem item)
    {
        if (item.SourceEntityId is { } entityId && item.SourceVariantId is { } variantId)
        {
            var entity = canvas.State.Entities.FirstOrDefault(candidate => candidate.Id == entityId);
            var variant = entity?.Variants.FirstOrDefault(candidate => candidate.Id == variantId);
            if (variant is not null && item.SourceVersionId is { } versionId)
            {
                var latest = variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault();
                if (latest is not null) return latest.Id == versionId;
            }
        }

        if (item.ParentId is not { } parentId) return true;
        var siblings = canvas.State.WorkTree.Where(candidate => candidate.ParentId == parentId && !string.IsNullOrWhiteSpace(candidate.Version)).ToList();
        if (siblings.Count == 0) return true;
        var current = siblings
            .Select(candidate => (Item: candidate, Number: ExtractVersionNumber(candidate.Version)))
            .OrderByDescending(candidate => candidate.Number)
            .First();
        return current.Item.Id == item.Id;
    }

    private string FormatWorkTreeVersion(WorkTreeItem item)
    {
        var version = item.Version;
        if (item.SupersedesVersionId is not { } previousId) return version;
        var entity = item.SourceEntityId is { } entityId
            ? canvas.State.Entities.FirstOrDefault(candidate => candidate.Id == entityId)
            : null;
        var variant = entity is null || item.SourceVariantId is not { } variantId
            ? null
            : entity.Variants.FirstOrDefault(candidate => candidate.Id == variantId);
        var previous = variant?.Versions.FirstOrDefault(candidate => candidate.Id == previousId);
        return previous is null ? version : $"{previous.Label} → {version}";
    }

    private static int ExtractVersionNumber(string version)
    {
        var digits = new string(version.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    private void HighlightWorkTreeItem(WorkTreeItem? item)
    {
        if (item is null || item.Kind != WorkTreeKind.Version)
        {
            canvas.ClearHighlightedNodes();
            return;
        }

        var matches = canvas.State.Nodes.Where(node => node.References.Any(reference =>
        {
            if (item.SourceEntityId is { } entityId && reference.EntityId != entityId)
                return false;
            if (item.SourceVariantId is { } variantId && reference.VariantId != variantId)
                return false;
            if (item.SourceVersionId is { } versionId)
                return reference.VariantVersionId == versionId;
            return string.IsNullOrWhiteSpace(item.Version) || reference.VariantVersionId is null ||
                   canvas.State.ResolveReferences(node).Any(content =>
                       string.Equals(content.VersionLabel, item.Version, StringComparison.OrdinalIgnoreCase));
        })).Select(node => node.Id).ToList();

        canvas.SetHighlightedNodes(matches);
        if (matches.Count > 0)
        {
            var first = canvas.State.Nodes.First(node => node.Id == matches[0]);
            canvas.FocusNode(first.Id);
        }
    }

    private void RefreshWorkTreeDetails(WorkTreeItem? item)
    {
        if (workTreeMetaLabel is null || workTreePromptBox is null || workTreeAssetList is null) return;
        workTreeMetaLabel.Text = item is null ? "未选择工作树条目" : $"{WorkTreeItem.KindName(item.Kind)}  ·  {item.Chapter}  ·  {item.Version}";
        workTreePromptBox.Text = item?.Prompt is { Length: > 0 } prompt ? prompt : "暂无叙事设定";
        workTreeAssetList.Items.Clear();
        var attachments = item?.Attachments ?? new List<WorkflowAttachment>();
        foreach (var kind in new[] { AttachmentKind.Image, AttachmentKind.Video })
        {
            var matching = attachments.Where(attachment => attachment.Kind == kind).ToList();
            if (matching.Count == 0)
            {
                workTreeAssetList.Items.Add(new ListViewItem(new[] { WorkflowAttachment.DisplayName(kind), "暂无素材" }));
                continue;
            }
            foreach (var attachment in matching)
                workTreeAssetList.Items.Add(new ListViewItem(new[] { WorkflowAttachment.DisplayName(kind), attachment.Reference }));
        }
    }

    private Control BuildTaskPane()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 19, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 19; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var index in new[] { 2, 6, 9, 11, 15, 16, 18 }) layout.RowStyles[index] = new RowStyle(SizeType.Absolute, index switch { 2 => 22, 6 => 34, 9 => 68, 11 => 50, 15 => 28, 16 => 38, _ => 200 });

        layout.Controls.Add(SectionTitle("任务状态"), 0, 0);
        statusLabel.Text = "空闲"; statusLabel.AutoSize = true; layout.Controls.Add(statusLabel, 0, 1);
        progressBar.Minimum = 0; progressBar.Maximum = 100; progressBar.Dock = DockStyle.Fill; layout.Controls.Add(progressBar, 0, 2);
        jobLabel.Text = "任务：-"; jobLabel.AutoSize = true; layout.Controls.Add(jobLabel, 0, 3);
        queueLabel.Text = "队列位置：-"; queueLabel.AutoSize = true; layout.Controls.Add(queueLabel, 0, 4);
        timeLabel.Text = "已用时间：-"; timeLabel.AutoSize = true; layout.Controls.Add(timeLabel, 0, 5);
        cancelButton.Text = "■  取消任务"; cancelButton.Enabled = false; cancelButton.Dock = DockStyle.Fill; cancelButton.FlatStyle = FlatStyle.Flat;
        cancelButton.Click += CancelButton_Click; layout.Controls.Add(cancelButton, 0, 6);

        layout.Controls.Add(SectionTitle("创建图像任务"), 0, 7);
        layout.Controls.Add(FieldLabel("提示词"), 0, 8);
        promptBox.Multiline = true; promptBox.Dock = DockStyle.Fill; promptBox.ScrollBars = ScrollBars.Vertical; layout.Controls.Add(promptBox, 0, 9);
        layout.Controls.Add(FieldLabel("反向提示词"), 0, 10);
        negativePromptBox.Multiline = true; negativePromptBox.Dock = DockStyle.Fill; negativePromptBox.ScrollBars = ScrollBars.Vertical; layout.Controls.Add(negativePromptBox, 0, 11);
        layout.Controls.Add(NumberRow(("宽度", widthBox, 64, 2048, 1024), ("高度", heightBox, 64, 2048, 1024)), 0, 12);
        layout.Controls.Add(NumberRow(("采样步数", stepsBox, 1, 150, 28), ("CFG", cfgBox, 1, 30, 7)), 0, 13);
        layout.Controls.Add(FieldLabel("种子"), 0, 14);
        seedBox.Text = "随机"; seedBox.Dock = DockStyle.Fill; layout.Controls.Add(seedBox, 0, 15);
        submitButton.Text = "▶  提交任务"; submitButton.BackColor = Color.FromArgb(75, 63, 227); submitButton.ForeColor = Color.White;
        submitButton.FlatStyle = FlatStyle.Flat; submitButton.Dock = DockStyle.Fill; submitButton.Click += SubmitButton_Click; layout.Controls.Add(submitButton, 0, 16);

        layout.Controls.Add(SectionTitle("任务记录"), 0, 17);
        jobList.View = View.Details; jobList.FullRowSelect = true; jobList.GridLines = true; jobList.Dock = DockStyle.Fill; jobList.ShowItemToolTips = true;
        jobList.Columns.Add("状态", 90); jobList.Columns.Add("进度", 60); jobList.Columns.Add("任务", 80);
        jobList.DoubleClick += (_, _) => OpenJobDetailDialog(); layout.Controls.Add(jobList, 0, 18);
        return CreatePaneShell("任务与出图", CreateScrollHost(layout));
    }

    /// <summary>抽屉里的统一标题样式。</summary>
    private static Label SectionTitle(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 11F, FontStyle.Bold), ForeColor = Color.FromArgb(38, 38, 46), Padding = new Padding(0, 14, 0, 6) };

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 6, 0, 2) };

    /// <summary>两个数值输入并排一行，窄抽屉里也能放下。</summary>
    private static TableLayoutPanel NumberRow(
        (string Label, NumericUpDown Box, decimal Min, decimal Max, decimal Value) left,
        (string Label, NumericUpDown Box, decimal Min, decimal Max, decimal Value) right)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        void Place(int index, (string Label, NumericUpDown Box, decimal Min, decimal Max, decimal Value) field)
        {
            field.Box.Minimum = field.Min; field.Box.Maximum = field.Max; field.Box.Value = field.Value; field.Box.Dock = DockStyle.Fill;
            row.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Gray, Margin = new Padding(index == 0 ? 0 : 10, 8, 4, 0) }, index * 2, 0);
            row.Controls.Add(field.Box, index * 2 + 1, 0);
        }
        Place(0, left); Place(1, right);
        return row;
    }

    /// <summary>把内容放进可滚动的容器，抽屉高度不够时可以滚动。</summary>
    private static Panel CreateScrollHost(Control content)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
        scroll.Controls.Add(content);
        return scroll;
    }

    /// <summary>
    /// 给选中节点添加附件：把外部文件复制进本机资产目录后以 asset:// 引用挂到节点上，
    /// 这样导出画布与引用清理都能覆盖到这些文件。支持一次选择多个文件（图片 / 视频 / 音频）。
    /// </summary>
    private void AddAttachmentsToSelectedNode()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "添加附件"); return; }
        if (RefuseWhenLocked(node, "添加附件")) return;
        using var dialog = new OpenFileDialog
        {
            Title = "选择要添加的附件",
            Multiselect = true,
            Filter = "媒体文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.mov;*.avi;*.mkv;*.webm;*.mp3;*.wav;*.flac;*.m4a;*.aac|图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|视频|*.mp4;*.mov;*.avi;*.mkv;*.webm|音频|*.mp3;*.wav;*.flac;*.m4a;*.aac|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var added = 0;
        var failed = new List<string>();
        try
        {
            var directory = AssetStore.EnsureDirectory();
            foreach (var source in dialog.FileNames)
            {
                try
                {
                    var name = Path.GetFileName(source);
                    var target = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(source)}");
                    File.Copy(source, target, overwrite: false);
                    node.Attachments.Add(new WorkflowAttachment
                    {
                        Kind = WorkflowAttachment.KindOf(source),
                        Reference = AssetStore.ToReference(target),
                        Name = name,
                        Source = "导入"
                    });
                    added++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{Path.GetFileName(source)}：{error.Message}");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "添加附件失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        canvas.InvalidateThumbnails();
        RefreshNodeInspector();
        canvas.Invalidate();
        var detail = added == 0 ? "没有文件被添加。" : $"已添加 {added} 个附件。";
        if (failed.Count > 0) detail += $"\n\n以下文件失败：\n{string.Join("\n", failed.Take(5))}";
        MessageBox.Show(detail, "添加附件", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ExportCanvasPackage()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择导出目录，画布 JSON 与图片资产会一并写入", ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (copied, missing) = CanvasPackage.Export(dialog.SelectedPath, BuildCanvasState());
            var detail = missing == 0 ? $"已导出画布与 {copied} 个资产。" : $"已导出画布与 {copied} 个资产，另有 {missing} 个资产在本机缺失，未包含在包内。";
            MessageBox.Show(detail, "导出画布", MessageBoxButtons.OK, missing == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ImportCanvasPackage()
    {
        using var dialog = new OpenFileDialog { Filter = "画布文件 (canvas.json)|canvas.json|JSON 文件 (*.json)|*.json", Title = "选择画布文件" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show("导入会替换当前画布编辑内容，但不会删除任务历史。是否继续？", "导入画布", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var (state, imported, missing) = CanvasPackage.Import(dialog.FileName);
            ApplyCanvasState(state, advanceRevision: true);
            SetCurrentCanvasPath(null);
            SaveRecentCanvas();
            RefreshCanvasLibrary();
            var detail = missing == 0 ? $"已导入画布与 {imported} 个资产。" : $"已导入画布与 {imported} 个资产，另有 {missing} 个资产在包内缺失，相关节点会显示图片不可用。";
            MessageBox.Show(detail, "导入画布", MessageBoxButtons.OK, missing == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            MessageBox.Show(error.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed record CanvasResourceReplaceRequest(
        Guid RequestId,
        Guid RecordId,
        Guid EntityId,
        Guid VariantId,
        Guid? VariantVersionId);

    private sealed record ResourceReplaceOutcome(bool Ok, string Message, int? Revision);

    private void PollResourceReplaceRequests()
    {
        if (resourceReplacePollInFlight || IsDisposed) return;
        resourceReplacePollInFlight = true;
        _ = PollResourceReplaceRequestsAsync();
    }

    private async Task PollResourceReplaceRequestsAsync()
    {
        try
        {
            using var response = await canvasPushClient.GetAsync(
                $"{CanvasWebBaseUrl}/api/canvas/resource-replace/next");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            if (!response.IsSuccessStatusCode) return;

            var request = await JsonSerializer.DeserializeAsync<CanvasResourceReplaceRequest>(
                await response.Content.ReadAsStreamAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request is not null)
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        ApplyResourceReplace(request);
                    }
                    catch (InvalidOperationException error)
                    {
                        var outcome = new ResourceReplaceOutcome(false, error.Message, null);
                        resourceReplaceOutcomes[request.RequestId] = outcome;
                        TrimResourceReplaceOutcomes();
                        _ = PushResourceReplaceResultAsync(request.RequestId, outcome.Ok, outcome.Message, outcome.Revision);
                    }
                });
            }
        }
        catch (HttpRequestException)
        {
            // Web 服务未启动或暂时不可达，下一次定时器周期继续尝试。
        }
        catch (TaskCanceledException)
        {
            // 网络请求超时不影响桌面画布操作。
        }
        finally
        {
            resourceReplacePollInFlight = false;
        }
    }

    private void ApplyResourceReplace(CanvasResourceReplaceRequest request)
    {
        if (resourceReplaceOutcomes.TryGetValue(request.RequestId, out var previous))
        {
            _ = PushResourceReplaceResultAsync(request.RequestId, previous.Ok, previous.Message, previous.Revision);
            return;
        }

        if (!canvas.State.ReplaceReferenceVersion(
                request.RecordId,
                request.EntityId,
                request.VariantId,
                request.VariantVersionId,
                out var error))
            throw new InvalidOperationException(error);

        canvasRevision++;
        canvasRevisionLabel.Text = $"修订 {canvasRevision}";
        RefreshNodeInspector();
        canvas.Invalidate();
        SaveRecentCanvas();
        SaveCurrentCanvasTab();
        _ = PushCanvasToWebAsync("resource-version-replaced");
        var outcome = new ResourceReplaceOutcome(true, "资源版本替换成功。", canvasRevision);
        resourceReplaceOutcomes[request.RequestId] = outcome;
        TrimResourceReplaceOutcomes();
        _ = PushResourceReplaceResultAsync(request.RequestId, outcome.Ok, outcome.Message, outcome.Revision);
    }

    private void TrimResourceReplaceOutcomes()
    {
        const int maxEntries = 256;
        if (resourceReplaceOutcomes.Count <= maxEntries) return;
        foreach (var key in resourceReplaceOutcomes.Keys.Take(resourceReplaceOutcomes.Count - maxEntries).ToArray())
            resourceReplaceOutcomes.Remove(key);
    }

    private async Task PushResourceReplaceResultAsync(Guid requestId, bool ok, string message, int? revision)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { requestId, ok, message, revision });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            await canvasPushClient.PostAsync($"{CanvasWebBaseUrl}/api/canvas/resource-replace/result", content);
        }
        catch { /* Web 服务未启动或不可达，下一次用户操作仍可重试。 */ }
    }

    private RecentCanvasState BuildCanvasState() =>
        new(canvasTitle, canvasRevision, promptBox.Text, negativePromptBox.Text, (int)widthBox.Value, (int)heightBox.Value, (int)stepsBox.Value, (double)cfgBox.Value, seedBox.Text, canvas.State);

    /// <summary>把当前画布状态投影为 records 并推送给 Web 画布前端。火灾安全：失败只记日志。</summary>
    private async Task PushCanvasToWebAsync(string reason = "scene-update")
    {
        try
        {
            var state = canvas.State;
            if (state is null) return;
            var records = NodeProjection.ProjectRecords(state.Nodes, state);
            var payload = JsonSerializer.Serialize(new { records, Revision = canvasRevision, Reason = reason });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            await canvasPushClient.PostAsync($"{CanvasWebBaseUrl}/api/canvas/scene", content);
        }
        catch { /* Web 服务未启动或不可达，静默忽略 */ }
    }

    private void ApplyCanvasState(RecentCanvasState state, bool advanceRevision)
    {
        if (advanceRevision) canvasRevision++;
        else canvasRevision = Math.Max(0, state.Revision);
        canvasTitle = string.IsNullOrWhiteSpace(state.Title) ? "未命名画布" : state.Title;
        UpdateCanvasTitleLabel(); canvasRevisionLabel.Text = $"修订 {canvasRevision}";
        promptBox.Text = state.Prompt ?? string.Empty;
        negativePromptBox.Text = state.NegativePrompt ?? string.Empty;
        widthBox.Value = Math.Clamp(state.Width, (int)widthBox.Minimum, (int)widthBox.Maximum);
        heightBox.Value = Math.Clamp(state.Height, (int)heightBox.Minimum, (int)heightBox.Maximum);
        stepsBox.Value = Math.Clamp(state.Steps, (int)stepsBox.Minimum, (int)stepsBox.Maximum);
        cfgBox.Value = (decimal)Math.Clamp(state.Cfg, (double)cfgBox.Minimum, (double)cfgBox.Maximum);
        seedBox.Text = string.IsNullOrWhiteSpace(state.Seed) ? "随机" : state.Seed;
        canvas.LoadState(state.Canvas ?? new WorkflowCanvasState());
        AssetStore.Normalize(canvas.State);
        RefreshNodeInspector();
        RefreshEntityList();
        RefreshWorkTreeView();
        _ = PushCanvasToWebAsync("canvas-loaded");
    }

    private void ShowNodePropertiesDialog()
    {
        if (canvas.SelectedNode is null) return;
        RefreshNodeInspector();
        var pane = BuildNodePane();
        pane.Dock = DockStyle.Fill;
        pane.Margin = Padding.Empty;
        var modalHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };
        var close = new Button { Text = "✕", Dock = DockStyle.Right, Width = 38, FlatStyle = FlatStyle.Flat, ForeColor = Theme.TextMuted, BackColor = Color.Transparent };
        close.FlatAppearance.BorderSize = 0;
        modalHost.Controls.Add(pane);
        modalHost.Controls.Add(close);
        using var dialog = new Form
        {
            Text = "节点属性",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            MinimumSize = new Size(520, 640),
            Size = new Size(620, 820),
            BackColor = Theme.PanelBg,
            Font = Theme.UiFont
        };
        dialog.Controls.Add(modalHost);
        close.Click += (_, _) => dialog.Close();
        Theme.Apply(dialog);
        dialog.ShowDialog(this);
        modalHost.Controls.Remove(pane);
        panes["node"] = pane;
        RefreshNodeInspector();
    }

    /// <summary>“节点属性”面板：标题、附件、文本与生成操作，抽屉和双击弹窗共用。</summary>
    private Control BuildNodePane()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, BackColor = Theme.EditorBg, ForeColor = Theme.Text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 25, Padding = Padding.Empty, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 25; row++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var index in new[] { 3, 5, 7, 9, 10, 12, 13, 15, 17, 19, 20, 21, 22, 23, 24 })
            panel.RowStyles[index] = new RowStyle(SizeType.Absolute, index switch { 3 => 28, 5 => 28, 7 => 34, 9 => 34, 10 => 34, 12 => 132, 13 => 34, 15 => 28, 17 => 92, 19 => 60, 23 => 68, 24 => 68, _ => 34 });

        selectionLabel.Text = "未选择节点"; selectionLabel.AutoSize = true; selectionLabel.Font = Theme.TitleFont; selectionLabel.ForeColor = Theme.Text; panel.Controls.Add(selectionLabel, 0, 0);
        nodeStatusLabel.Text = "状态：-"; nodeStatusLabel.AutoSize = true; nodeStatusLabel.MaximumSize = new Size(310, 0); panel.Controls.Add(nodeStatusLabel, 0, 1);
        panel.Controls.Add(FieldLabel("标题"), 0, 2);
        nodeTitleBox.PlaceholderText = "节点标题"; nodeTitleBox.Dock = DockStyle.Fill; panel.Controls.Add(nodeTitleBox, 0, 3);
        panel.Controls.Add(FieldLabel("所属章节（留空时从父节点推断）"), 0, 4);
        nodeChapterBox.PlaceholderText = "例如：第一章"; nodeChapterBox.Dock = DockStyle.Fill; panel.Controls.Add(nodeChapterBox, 0, 5);
        nodeVersionStatusLabel.AutoSize = true; nodeVersionStatusLabel.MaximumSize = new Size(306, 0); nodeVersionStatusLabel.ForeColor = Theme.TextMuted; panel.Controls.Add(nodeVersionStatusLabel, 0, 6);
        var versionDecisionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        adoptEffectiveVersionButton.Text = "采用本章有效版本"; adoptEffectiveVersionButton.Width = 146; adoptEffectiveVersionButton.Height = 30; adoptEffectiveVersionButton.FlatStyle = FlatStyle.Flat;
        adoptEffectiveVersionButton.Click += (_, _) => AdoptEffectiveVersionForSelectedNode();
        keepHistoricalVersionButton.Text = "保留历史版本"; keepHistoricalVersionButton.Width = 112; keepHistoricalVersionButton.Height = 30; keepHistoricalVersionButton.FlatStyle = FlatStyle.Flat;
        keepHistoricalVersionButton.Click += (_, _) => KeepHistoricalVersionForSelectedNode();
        compareVersionsButton.Text = "查看差异"; compareVersionsButton.Width = 90; compareVersionsButton.Height = 30; compareVersionsButton.FlatStyle = FlatStyle.Flat;
        compareVersionsButton.Click += (_, _) => ShowSelectedNodeVersionDiff();
        versionDecisionRow.Controls.Add(adoptEffectiveVersionButton); versionDecisionRow.Controls.Add(keepHistoricalVersionButton); versionDecisionRow.Controls.Add(compareVersionsButton);
        panel.Controls.Add(versionDecisionRow, 0, 7);
        panel.Controls.Add(FieldLabel("引用设定"), 0, 8);
        referenceLabel.Text = "未引用设定"; referenceLabel.AutoSize = true; referenceLabel.ForeColor = Color.Gray;
        referenceLabel.MaximumSize = new Size(306, 0); panel.Controls.Add(referenceLabel, 0, 9);
        var referenceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        var pickReference = new Button { Text = "添加引用", Width = 100, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2) };
        pickReference.Click += (_, _) => AddReferenceToSelectedNode();
        var manageReference = new Button { Text = "管理引用", Width = 100, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 0, 2) };
        manageReference.Click += (_, _) => ManageSelectedNodeReferences();
        referenceButtons.Controls.Add(pickReference); referenceButtons.Controls.Add(manageReference);
        panel.Controls.Add(referenceButtons, 0, 10);
        attachmentLabel.Text = "附件"; attachmentLabel.AutoSize = true; attachmentLabel.Padding = new Padding(0, 8, 0, 2); panel.Controls.Add(attachmentLabel, 0, 11);
        attachmentList.View = View.Details; attachmentList.FullRowSelect = true; attachmentList.GridLines = true; attachmentList.MultiSelect = true;
        attachmentList.Dock = DockStyle.Fill; attachmentList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        attachmentList.Columns.Add("类型", 56); attachmentList.Columns.Add("名称", 210);
        attachmentList.DoubleClick += (_, _) => OpenSelectedAttachment();
        panel.Controls.Add(attachmentList, 0, 12);
        var attachmentButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        var addAttachment = new Button { Text = "添加附件", Width = 102, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 0) };
        addAttachment.Click += (_, _) => AddAttachmentsToSelectedNode();
        removeAttachmentButton.Text = "移除附件"; removeAttachmentButton.Width = 102; removeAttachmentButton.Height = 30;
        removeAttachmentButton.FlatStyle = FlatStyle.Flat; removeAttachmentButton.Margin = new Padding(0, 2, 0, 0);
        removeAttachmentButton.Click += (_, _) => RemoveSelectedAttachments();
        attachmentButtons.Controls.Add(addAttachment); attachmentButtons.Controls.Add(removeAttachmentButton);
        panel.Controls.Add(attachmentButtons, 0, 13);
        panel.Controls.Add(FieldLabel("节点分类 / 内容来源"), 0, 14);
        nodeCategoryBox.DropDownStyle = ComboBoxStyle.DropDownList;
        nodeCategoryBox.Items.AddRange(new object[] { "通用", "角色", "场景", "分镜", "道具", "成品", "剧情", "企划", "章节" });
        nodeCategoryBox.Width = 142;
        contentSourceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        contentSourceBox.Items.AddRange(Enum.GetNames<ContentSource>());
        contentSourceBox.Width = 142;
        var nodeMetaRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        nodeMetaRow.Controls.Add(nodeCategoryBox);
        nodeMetaRow.Controls.Add(contentSourceBox);
        panel.Controls.Add(nodeMetaRow, 0, 15);
        panel.Controls.Add(FieldLabel("节点文本"), 0, 16);
        nodeContentBox.Multiline = true; nodeContentBox.Dock = DockStyle.Fill; nodeContentBox.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(nodeContentBox, 0, 17);
        panel.Controls.Add(FieldLabel("回答 AI 的提问"), 0, 18);
        nodeAnswerBox.PlaceholderText = "回答 AI 的提问（如有）"; nodeAnswerBox.Multiline = true; nodeAnswerBox.Dock = DockStyle.Fill; nodeAnswerBox.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(nodeAnswerBox, 0, 19);

        var apply = new Button { Text = "应用节点属性", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        apply.Click += (_, _) =>
        {
            if (canvas.SelectedNode is { } previewNode && canvas.IsPreviewNode(previewNode))
            {
                var oldTitle = previewNode.Title;
                previewNode.Title = nodeTitleBox.Text;
                previewNode.Content = nodeContentBox.Text;
                previewNode.Chapter = ResolveEditedChapter(previewNode);
                previewNode.Category = ParseNodeCategoryLabel(nodeCategoryBox.SelectedItem as string);
                pendingChanges.UpdateCreatedNode(oldTitle, previewNode.Title, previewNode.Content);
                RefreshPendingState();
                RefreshNodeInspector();
                canvas.Invalidate();
                return;
            }
            if (canvas.SelectedNode is { } locked && RefuseWhenLocked(locked, "应用节点属性")) return;
            canvas.UpdateSelected(nodeTitleBox.Text, nodeContentBox.Text);
            if (canvas.SelectedNode is { } node)
            {
                node.Category = ParseNodeCategoryLabel(nodeCategoryBox.SelectedItem as string);
                var previousChapter = node.Chapter;
                node.Chapter = ResolveEditedChapter(node);
                if (IsChapterNode(node) && !string.Equals(previousChapter, node.Chapter, StringComparison.OrdinalIgnoreCase))
                    SyncDescendantChapters(node, node.Chapter);
                if (contentSourceBox.SelectedItem is string source) node.ContentSource = Enum.Parse<ContentSource>(source);
                if (node.Question.Length > 0) node.Answer = nodeAnswerBox.Text;
                node.ExecutionStatus = NodeExecutionStatus.Draft;
            }
            RefreshNodeInspector(); canvas.Invalidate();
        };
        panel.Controls.Add(apply, 0, 20);

        var generate = new Button { Text = "AI 生成 / 继续", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(75, 63, 227), ForeColor = Color.White };
        generate.Click += async (_, _) => await GenerateSelectedNodeAsync(); panel.Controls.Add(generate, 0, 21);
        var accept = new Button { Text = "接受最新结果", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        accept.Click += (_, _) => AcceptSelectedGeneration(); panel.Controls.Add(accept, 0, 22);
        var imageRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        var image = new Button { Text = "生成参考图", Width = 124, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2) };
        image.Click += async (_, _) => await GenerateSelectedImageAsync();
        var compose = new Button { Text = "合成参考图", Width = 124, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2) };
        compose.Click += async (_, _) => await ComposeReferencesForSelectedNodeAsync();
        var imageParams = new Button { Text = "图像参数", Width = 124, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 0, 2) };
        imageParams.Click += (_, _) => OpenImageParametersDialog();
        imageRow.Controls.Add(image); imageRow.Controls.Add(compose); imageRow.Controls.Add(imageParams);
        panel.Controls.Add(imageRow, 0, 23);
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = Padding.Empty, Margin = Padding.Empty };
        var history = new Button { Text = "生成历史", Width = 124, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2) };
        history.Click += (_, _) => OpenNodeHistoryDialog();
        var expand = new Button { Text = "自动生成下游", Width = 124, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2) };
        expand.Click += AutoExpandButton_Click; autoExpandButton = expand;
        nodeLockButton.Text = "锁定节点"; nodeLockButton.Width = 124; nodeLockButton.Height = 30;
        nodeLockButton.FlatStyle = FlatStyle.Flat; nodeLockButton.Margin = new Padding(0, 2, 0, 2);
        nodeLockButton.Click += (_, _) => ToggleSelectedNodeLock();
        actionRow.Controls.Add(history); actionRow.Controls.Add(expand); actionRow.Controls.Add(nodeLockButton);
        panel.Controls.Add(actionRow, 0, 24);

        return CreatePaneShell("节点属性", CreateScrollHost(panel));
    }

    private void ShowSelectedNodeVersionDiff()
    {
        var node = canvas.SelectedNode;
        if (node is null || canvas.IsPreviewNode(node)) return;

        var references = canvas.State.ResolveReferencePairs(node)
            .Where(pair => pair.Content is not null)
            .ToList();
        if (references.Count != 1)
        {
            MessageBox.Show("查看版本差异要求节点只引用一个实体版本。", "版本差异");
            return;
        }

        var pair = references[0];
        var entity = canvas.State.Entities.FirstOrDefault(item => item.Id == pair.Reference.EntityId);
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == pair.Reference.VariantId);
        var effective = entity is null || variant is null
            ? null
            : canvas.GetEffectiveVersionForNode(node, entity, variant);
        if (entity is null || variant is null || pair.Content!.Version is null || effective is null)
        {
            MessageBox.Show("无法确定节点当前引用版本或本章有效版本。", "版本差异");
            return;
        }
        if (pair.Content.Version.Id == effective.Id)
        {
            MessageBox.Show("节点引用版本已经是本章有效版本，没有版本差异可查看。", "版本差异");
            return;
        }

        var currentVersion = pair.Content.Version;
        var differences = VersionDiff.CompareToCurrent(currentVersion, new WorkflowEntityVariant
        {
            Name = variant.Name,
            Description = effective.Description,
            Layout = effective.Layout,
            Attachments = effective.Attachments
        });
        var lines = new List<string>
        {
            $"实体：{entity.Name} · {variant.Name}",
            $"章节：{canvas.GetNodeChapterLabel(node)}",
            $"当前引用：{currentVersion.Label}",
            $"本章有效：{effective.Label}",
            string.IsNullOrWhiteSpace(currentVersion.Note) ? "" : $"{currentVersion.Label} 说明：{currentVersion.Note}",
            string.IsNullOrWhiteSpace(effective.Note) ? "" : $"{effective.Label} 说明：{effective.Note}",
            "",
            "差异："
        };
        if (differences.Count == 0)
            lines.Add("· 两个版本内容相同");
        else
            lines.AddRange(differences.Take(40).Select(difference => "· " + difference.Describe()));
        if (differences.Count > 40) lines.Add($"· 其余 {differences.Count - 40} 项省略");

        using var dialog = new Form
        {
            Text = "版本差异",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimumSize = new Size(560, 420),
            ClientSize = new Size(720, 560),
            BackColor = Theme.PanelBg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont
        };
        var detail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Theme.FieldBg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Text = string.Join(Environment.NewLine, lines)
        };
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
        dialog.Controls.Add(detail);
        dialog.Controls.Add(close);
        dialog.AcceptButton = close;
        dialog.ShowDialog(this);
    }

    private void AdoptEffectiveVersionForSelectedNode()
    {
        var node = canvas.SelectedNode;
        if (node is null || canvas.IsPreviewNode(node)) return;
        if (RefuseWhenLocked(node, "采用本章有效版本")) return;

        var pairs = canvas.State.ResolveReferencePairs(node)
            .Where(pair => pair.Content is not null)
            .ToList();
        if (pairs.Count != 1)
        {
            MessageBox.Show(
                "当前操作要求节点只引用一个实体版本。请先通过“管理引用”处理多引用节点。",
                "无法采用版本",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var pair = pairs[0];
        var entity = canvas.State.Entities.FirstOrDefault(item => item.Id == pair.Reference.EntityId);
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == pair.Reference.VariantId);
        if (entity is null || variant is null) return;

        var effective = canvas.GetEffectiveVersionForNode(node, entity, variant);
        if (effective is null || pair.Reference.VariantVersionId == effective.Id) return;

        ApplyAgentBatch(new[]
        {
            new AgentAction
            {
                Kind = "update_node",
                Target = node.Title,
                EntityTarget = entity.Name,
                VariantTarget = variant.Name,
                VariantVersion = effective.Label,
                MarkVersionAdopted = true,
                Reason = $"用户确认将该节点采用{canvas.GetNodeChapterLabel(node)}的有效版本"
            }
        });
        MessageBox.Show(
            $"已应用“采用 {effective.Label}”的操作。",
            "版本操作已应用",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void KeepHistoricalVersionForSelectedNode()
    {
        var node = canvas.SelectedNode;
        if (node is null || canvas.IsPreviewNode(node)) return;
        if (RefuseWhenLocked(node, "记录保留历史版本")) return;

        node.VersionDecision = VersionDecision.KeepHistorical;
        canvas.NotifyContentChanged();
        RefreshNodeInspector();
        MessageBox.Show(
            "已记录保留历史版本的决定。该节点不会因后续章节出现新版本而重复提示。",
            "保留历史版本",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private string ResolveEditedChapter(WorkflowNode node)
    {
        var chapter = nodeChapterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(chapter) && IsChapterNode(node)
            ? node.Title.Trim()
            : chapter;
    }

    private void SyncDescendantChapters(WorkflowNode chapterNode, string chapter)
    {
        var descendants = canvas.State.Nodes
            .Where(node => node.Id != chapterNode.Id && IsDescendantOf(node, chapterNode.Id))
            .ToList();
        foreach (var descendant in descendants)
            descendant.Chapter = chapter;
    }

    private bool IsDescendantOf(WorkflowNode node, Guid ancestorId)
    {
        var current = node.ParentNodeId;
        var visited = new HashSet<Guid>();
        while (current is { } parentId && visited.Add(parentId))
        {
            if (parentId == ancestorId) return true;
            current = canvas.State.Nodes.FirstOrDefault(candidate => candidate.Id == parentId)?.ParentNodeId;
        }
        return false;
    }

    private static bool IsChapterNode(WorkflowNode node) =>
        node.Title.Contains("章", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(node.Title, @"第\\s*(?:\\d+|[零〇一二两三四五六七八九十百千]+)\\s*章", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NodeCategoryLabel(NodeCategory category) => category switch
    {
        NodeCategory.Character => "角色",
        NodeCategory.Scene => "场景",
        NodeCategory.Storyboard => "分镜",
        NodeCategory.Prop => "道具",
        NodeCategory.Product => "成品",
        NodeCategory.StoryPlan => "剧情",
        NodeCategory.StoryOutline => "企划",
        NodeCategory.Chapter => "章节",
        _ => "通用"
    };

    private static NodeCategory ParseNodeCategoryLabel(string? value) => value switch
    {
        "角色" => NodeCategory.Character,
        "场景" => NodeCategory.Scene,
        "分镜" => NodeCategory.Storyboard,
        "道具" => NodeCategory.Prop,
        "成品" => NodeCategory.Product,
        "剧情" => NodeCategory.StoryPlan,
        "企划" => NodeCategory.StoryOutline,
        "章节" => NodeCategory.Chapter,
        _ => NodeCategory.General
    };

    private void RefreshNodeInspector()
    {
        var node = canvas.SelectedNode; var has = node is not null;
        var previewOnly = has && canvas.IsPreviewNode(node!);
        selectionLabel.Text = has ? (previewOnly ? $"{node!.Title} · 待提交预览" : node!.IsLocked ? $"{node.Title} · 已锁定" : node.Title) : "未选择节点";
        var latest = has ? node!.GenerationHistory.LastOrDefault() : null;
        var provider = latest is null ? string.Empty : $" · {latest.Provider}";
        nodeStatusLabel.Text = !has ? "状态：-" : string.IsNullOrWhiteSpace(node!.Question)
            ? $"状态：{node.ExecutionStatus} · 来源：{node.ContentSource}{provider}"
            : $"等待你的回答：{node.Question}";
        nodeTitleBox.Text = has ? node!.Title : string.Empty;
        nodeChapterBox.Text = has ? node!.Chapter : string.Empty;
        nodeVersionStatusLabel.Text = has
            ? $"{(string.IsNullOrWhiteSpace(node!.Chapter) ? "章节（父级推断）" : "章节（显式）")}：{canvas.GetNodeChapterLabel(node!)}\n{canvas.GetNodeVersionStatusSummary(node!)}"
            : "版本状态：-";
        nodeContentBox.Text = has ? node!.Content : string.Empty;
        nodeAnswerBox.Text = has ? node!.Answer : string.Empty;
        nodeCategoryBox.SelectedItem = has ? NodeCategoryLabel(node!.Category) : null;
        contentSourceBox.SelectedItem = has ? node!.ContentSource.ToString() : null;

        var references = has ? canvas.State.ReferenceLabels(node!) : new List<string>();
        referenceLabel.Text = references.Count == 0 ? "未引用设定" : string.Join("\n", references.Select(label => "◆ " + label));
        referenceLabel.ForeColor = references.Count == 0 ? Theme.TextMuted : Theme.Accent;
        nodeLockButton.Text = has && node!.IsLocked ? "解锁节点" : "锁定节点";
        nodeLockButton.Enabled = has && !previewOnly;
        adoptEffectiveVersionButton.Enabled = false;
        keepHistoricalVersionButton.Enabled = has && !previewOnly;
        compareVersionsButton.Enabled = false;
        if (has && !previewOnly && !node!.IsLocked)
        {
            var pairs = canvas.State.ResolveReferencePairs(node)
                .Where(pair => pair.Content is not null)
                .ToList();
            if (pairs.Count == 1)
            {
                var pair = pairs[0];
                var entity = canvas.State.Entities.FirstOrDefault(item => item.Id == pair.Reference.EntityId);
                var variant = entity?.Variants.FirstOrDefault(item => item.Id == pair.Reference.VariantId);
                var effective = entity is null || variant is null
                    ? null
                    : canvas.GetEffectiveVersionForNode(node, entity, variant);
                adoptEffectiveVersionButton.Enabled = effective is not null && pair.Reference.VariantVersionId != effective.Id;
                compareVersionsButton.Enabled = effective is not null && pair.Reference.VariantVersionId != effective.Id && pair.Content?.Version is not null;
            }
        }

        attachmentList.BeginUpdate();
        attachmentList.Items.Clear();
        if (has)
            foreach (var attachment in node!.Attachments)
            {
                var item = new ListViewItem(WorkflowAttachment.DisplayName(attachment.Kind));
                item.SubItems.Add(string.IsNullOrWhiteSpace(attachment.Name) ? Path.GetFileName(attachment.Reference) : attachment.Name);
                item.Tag = attachment;
                attachmentList.Items.Add(item);
            }
        attachmentList.EndUpdate();

        attachmentLabel.Text = has ? $"附件（{node!.Attachments.Count}）" : "附件";
        removeAttachmentButton.Enabled = has && node!.Attachments.Count > 0;
        var editable = has && (!node!.IsLocked || previewOnly);
        nodeTitleBox.Enabled = editable; nodeChapterBox.Enabled = editable; nodeContentBox.Enabled = editable; contentSourceBox.Enabled = editable;
        nodeAnswerBox.Enabled = editable && !string.IsNullOrWhiteSpace(node!.Question);
    }

    /// <summary>打开当前选中附件的本机文件，便于在系统默认程序中查看图片 / 视频 / 音频。</summary>
    private void OpenSelectedAttachment()
    {
        if (attachmentList.SelectedItems.Count == 0 || attachmentList.SelectedItems[0].Tag is not WorkflowAttachment attachment) return;
        var path = AssetStore.Resolve(attachment.Reference);
        if (path is null) { MessageBox.Show("该附件文件不存在，可能已被移动或删除。", "打开附件", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>移除选中节点的附件；若该资产已不再被任何画布引用，再询问是否移入回收站。</summary>
    private void RemoveSelectedAttachments()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "移除附件"); return; }
        if (RefuseWhenLocked(node, "移除附件")) return;
        if (attachmentList.SelectedItems.Count == 0) { MessageBox.Show("请先在附件列表中选择要移除的附件。", "移除附件"); return; }
        var selected = attachmentList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as WorkflowAttachment)
            .Where(attachment => attachment is not null)
            .Select(attachment => attachment!)
            .ToList();
        if (selected.Count == 0) return;
        var detail = string.Join("、", selected.Take(5).Select(attachment => attachment.Name));
        if (MessageBox.Show($"从节点移除以下附件？\n{detail}\n\n文件不会被删除，只有不再被任何画布引用时才会询问是否移入回收站。",
                "移除附件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var removed = new List<string>();
        foreach (var attachment in selected)
        {
            node.Attachments.Remove(attachment);
            if (!string.IsNullOrWhiteSpace(attachment.Reference)) removed.Add(attachment.Reference);
        }
        canvas.InvalidateThumbnails();
        RefreshNodeInspector();
        canvas.Invalidate();
        if (removed.Count > 0) OfferRecycleOrphanedAssets(removed);
    }

    /// <summary>
    /// 给选中节点添加一条引用（实体 + 变体 + 版本）。一个节点可以引用多个设定，
    /// 对应一个镜头里同时出现多个角色或场景。版本默认选最新已提交版本。
    /// </summary>
    private void AddReferenceToSelectedNode()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "添加引用"); return; }
        if (RefuseWhenLocked(node, "修改引用")) return;
        if (canvas.State.Entities.Count == 0)
        {
            MessageBox.Show("设定库还是空的。请先在左侧「定」里新建角色、场景或道具。", "添加引用");
            return;
        }

        using var dialog = new ScaledForm { Text = "添加引用", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(720, 470), Font = Font };
        var entityBox = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        entityBox.Columns.Add("种类", 66); entityBox.Columns.Add("名称", 150);
        var variantBox = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        variantBox.Columns.Add("变体", 130); variantBox.Columns.Add("版本", 80);
        var versionBox = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        versionBox.Columns.Add("版本", 76); versionBox.Columns.Add("内容", 170);
        var confirm = new Button { Text = "添加", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, Enabled = false };
        var cancel = new Button { Text = "取消", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        var hint = new Label
        {
            Text = "锁定到具体版本可避免后续修改影响这个节点；「跟随当前内容」会随变体一起变。",
            Dock = DockStyle.Bottom, Height = 30, ForeColor = Color.Gray, Padding = new Padding(12, 6, 12, 0)
        };

        void UpdateConfirm() => confirm.Enabled = variantBox.SelectedItems.Count > 0;

        void FillVersions()
        {
            versionBox.BeginUpdate();
            versionBox.Items.Clear();
            if (variantBox.SelectedItems.Count > 0 && variantBox.SelectedItems[0].Tag is WorkflowEntityVariant variant)
            {
                var follow = new ListViewItem("跟随当前内容");
                follow.SubItems.Add("始终使用变体当前内容");
                follow.Tag = null;
                versionBox.Items.Add(follow);
                foreach (var version in variant.Versions.OrderByDescending(item => item.Number))
                {
                    var row = new ListViewItem(version.Label);
                    row.SubItems.Add(string.IsNullOrWhiteSpace(version.Note) ? $"{version.CreatedAt.ToLocalTime():MM-dd HH:mm}" : version.Note);
                    row.Tag = version;
                    versionBox.Items.Add(row);
                }
                // 添加引用时默认落在最新已提交版本上（第 1 项为「跟随当前内容」）。
                var target = versionBox.Items.Count > 1 ? 1 : 0;
                if (target < versionBox.Items.Count) versionBox.Items[target].Selected = true;
            }
            versionBox.EndUpdate();
            UpdateConfirm();
        }

        void FillVariants()
        {
            variantBox.BeginUpdate();
            variantBox.Items.Clear();
            if (entityBox.SelectedItems.Count > 0 && entityBox.SelectedItems[0].Tag is WorkflowEntity entity)
                foreach (var variant in entity.Variants)
                {
                    var item = new ListViewItem(variant.Name);
                    item.SubItems.Add(variant.HasUncommittedChanges ? $"{variant.CurrentVersionLabel} 有改动" : variant.CurrentVersionLabel);
                    item.Tag = variant;
                    variantBox.Items.Add(item);
                }
            variantBox.EndUpdate();
            if (variantBox.Items.Count > 0) variantBox.Items[0].Selected = true;
            FillVersions();
        }

        entityBox.BeginUpdate();
        foreach (var entity in canvas.State.Entities)
        {
            var item = new ListViewItem(WorkflowEntity.KindName(entity.Kind));
            item.SubItems.Add(entity.Name);
            item.Tag = entity;
            entityBox.Items.Add(item);
        }
        entityBox.EndUpdate();
        if (entityBox.Items.Count > 0) entityBox.Items[0].Selected = true;
        entityBox.SelectedIndexChanged += (_, _) => FillVariants();
        variantBox.SelectedIndexChanged += (_, _) => FillVersions();
        variantBox.DoubleClick += (_, _) => { if (confirm.Enabled) dialog.DialogResult = DialogResult.OK; };
        confirm.Click += (_, _) => { if (confirm.Enabled) dialog.DialogResult = DialogResult.OK; };
        FillVariants();

        var left = new GroupBox { Text = "实体", Dock = DockStyle.Fill, Padding = new Padding(8) };
        left.Controls.Add(entityBox);
        var middle = new GroupBox { Text = "变体", Dock = DockStyle.Fill, Padding = new Padding(8) };
        middle.Controls.Add(variantBox);
        var right = new GroupBox { Text = "版本", Dock = DockStyle.Fill, Padding = new Padding(8) };
        right.Controls.Add(versionBox);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 12, 12, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(middle, 1, 0);
        body.Controls.Add(right, 2, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(confirm);
        dialog.Controls.Add(body); dialog.Controls.Add(hint); dialog.Controls.Add(buttons);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (variantBox.SelectedItems.Count == 0 || variantBox.SelectedItems[0].Tag is not WorkflowEntityVariant picked) return;
        var version = versionBox.SelectedItems.Count > 0 ? versionBox.SelectedItems[0].Tag as EntityVariantVersion : null;
        // 引用前确保变体至少有一个已提交版本，选「跟随当前内容」时也一样，便于后续随时锁定。
        picked.EnsureInitialVersion();
        var pickedEntity = entityBox.SelectedItems.Count > 0 ? entityBox.SelectedItems[0].Tag as WorkflowEntity : null;
        if (node.References.Any(reference => reference.VariantId == picked.Id && reference.VariantVersionId == version?.Id))
        {
            MessageBox.Show($"该节点已经引用了「{picked.Name} · {(version is null ? "跟随当前内容" : version.Label)}」，未重复添加。", "添加引用");
            return;
        }
        node.References.Add(new NodeReference
        {
            EntityId = pickedEntity?.Id ?? Guid.Empty,
            VariantId = picked.Id,
            VariantVersionId = version?.Id
        });
        canvas.InvalidateThumbnails();
        canvas.NotifyContentChanged();
        RefreshNodeInspector();
        canvas.Invalidate();
    }

    /// <summary>
    /// 管理选中节点的引用列表：逐条查看生效内容、把锁定版本升级到最新、移除单条或全部清除。
    /// 一个节点可以引用多个设定，这里也是查看「这个镜头有哪些角色」的地方。
    /// </summary>
    private void ManageSelectedNodeReferences()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "管理引用"); return; }
        if (node.References.Count == 0) { MessageBox.Show("该节点还没有引用设定，先点「添加引用」。", "管理引用"); return; }
        if (RefuseWhenLocked(node, "修改引用")) return;

        using var dialog = new ScaledForm { Text = $"引用管理 · {node.Title}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(680, 460), Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, GridLines = true };
        list.Columns.Add("引用", 240); list.Columns.Add("版本", 90); list.Columns.Add("状态", 220);
        var detail = new TextBox { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 252) };
        var upgrade = new Button { Text = "升级到最新", Width = 108, Height = 32, FlatStyle = FlatStyle.Flat };
        var remove = new Button { Text = "移除选中", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        var clear = new Button { Text = "全部清除", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        var close = new Button { Text = "关闭", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

        void ShowDetail()
        {
            if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not NodeReference reference)
            {
                detail.Text = "选择一条引用查看它生效的内容。";
                return;
            }
            var content = canvas.State.ResolveReferencePairs(node)
                .FirstOrDefault(pair => ReferenceEquals(pair.Reference, reference)).Content;
            if (content is null)
            {
                detail.Text = "这条引用已经失效：实体或变体可能已被删除。建议移除它。";
                return;
            }
            var lines = new List<string> { $"{content.Label}（{(content.IsLocked ? "已锁定版本" : "跟随当前内容")}）" };
            if (!string.IsNullOrWhiteSpace(content.Entity.Core)) lines.Add($"核心设定：{content.Entity.Core}");
            if (!string.IsNullOrWhiteSpace(content.Description)) lines.Add($"当前表现：{content.Description}");
            if (content.Layout is { IsEmpty: false } layout) lines.Add(layout.ToPromptText());
            lines.Add($"参考图：{content.Attachments.Count(attachment => attachment.Kind == AttachmentKind.Image)} 张");
            detail.Text = string.Join("\n", lines);
        }

        void RefreshList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var (reference, content) in canvas.State.ResolveReferencePairs(node))
            {
                var item = new ListViewItem(content is null ? "（引用已失效）" : content.Label);
                item.SubItems.Add(content is null ? "-" : content.VersionLabel);
                var latest = content?.Variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault();
                var status = content is null
                    ? "实体或变体已删除"
                    : !content.IsLocked
                        ? "跟随当前内容"
                        : latest is not null && latest.Id != content.Version!.Id ? $"可升级到 {latest.Label}" : "已是最新";
                item.SubItems.Add(status);
                item.Tag = reference;
                list.Items.Add(item);
            }
            list.EndUpdate();
            ShowDetail();
        }

        /// <summary>升级选中项（没有选中则升级全部可升级项）到变体最新已提交版本。</summary>
        void Upgrade()
        {
            var pairs = canvas.State.ResolveReferencePairs(node);
            var targets = list.SelectedItems.Count > 0
                ? list.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as NodeReference).Where(item => item is not null).Select(item => item!).ToList()
                : pairs.Select(pair => pair.Reference).ToList();
            var upgraded = 0;
            var skipped = 0;
            foreach (var target in targets)
            {
                var content = pairs.FirstOrDefault(pair => ReferenceEquals(pair.Reference, target)).Content;
                if (content is null) { skipped++; continue; }
                var latest = content.Variant.Versions.OrderByDescending(version => version.Number).FirstOrDefault();
                if (latest is null || (content.IsLocked && latest.Id == content.Version!.Id)) { skipped++; continue; }
                target.VariantVersionId = latest.Id;
                target.EntityId = content.Entity.Id;
                upgraded++;
            }
            if (upgraded == 0)
            {
                MessageBox.Show("没有可升级的引用：要么已经在最新版本，要么这条引用已经失效。", "升级引用");
                return;
            }
            RefreshList();
            RefreshNodeInspector();
            canvas.InvalidateThumbnails();
            canvas.Invalidate();
            MessageBox.Show($"已升级 {upgraded} 条引用到最新版本。" + (skipped > 0 ? $"另有 {skipped} 条无需升级。" : string.Empty), "升级引用");
        }

        void RemoveSelected()
        {
            var targets = list.SelectedItems.Cast<ListViewItem>()
                .Select(item => item.Tag as NodeReference)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();
            if (targets.Count == 0) { MessageBox.Show("请先选择要移除的引用。", "管理引用"); return; }
            foreach (var target in targets) node.References.Remove(target);
            RefreshList();
            RefreshNodeInspector();
            canvas.InvalidateThumbnails();
            canvas.Invalidate();
        }

        void ClearAll()
        {
            if (MessageBox.Show($"清除该节点的全部 {node.References.Count} 条引用？节点文本与附件不受影响。",
                    "管理引用", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            node.References.Clear();
            RefreshList();
            RefreshNodeInspector();
            canvas.InvalidateThumbnails();
            canvas.Invalidate();
        }

        upgrade.Click += (_, _) => Upgrade();
        remove.Click += (_, _) => RemoveSelected();
        clear.Click += (_, _) => ClearAll();
        list.SelectedIndexChanged += (_, _) => ShowDetail();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(close); buttons.Controls.Add(clear); buttons.Controls.Add(remove); buttons.Controls.Add(upgrade);
        dialog.Controls.Add(list); dialog.Controls.Add(detail); dialog.Controls.Add(buttons);
        dialog.CancelButton = close;
        RefreshList();
        dialog.ShowDialog(this);
        canvas.NotifyContentChanged();
    }

    /// <summary>节点已锁定时给出统一提示并返回 true，供各修改入口提前拦截。</summary>
    private static bool RefuseWhenLocked(WorkflowNode node, string action)
    {
        if (!node.IsLocked) return false;
        MessageBox.Show($"节点已锁定，无法{action}。请先点「解锁节点」。", "节点已锁定", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    /// <summary>锁定 / 解锁选中节点。锁定用于保护已定稿的内容与引用不被误改。</summary>
    private void ToggleSelectedNodeLock()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "锁定节点"); return; }
        node.IsLocked = !node.IsLocked;
        canvas.NotifyContentChanged();
        RefreshNodeInspector();
        canvas.Invalidate();
    }

    private string DescribeNodeTree(WorkflowNode selected)
    {
        var byId = canvas.State.Nodes.ToDictionary(node => node.Id);
        var lines = new List<string>();
        void AppendBranch(WorkflowNode node, int depth, HashSet<Guid> path)
        {
            if (!path.Add(node.Id)) return;
            var indent = new string(' ', depth * 2);
            lines.Add($"{indent}- [{NodeCategoryLabel(node.Category)}] {node.Title}: {Trim(node.Content, 1800)}");
            foreach (var child in canvas.State.Nodes.Where(candidate => candidate.ParentNodeId == node.Id))
                AppendBranch(child, depth + 1, path);
            path.Remove(node.Id);
        }

        var root = selected;
        var ancestors = new HashSet<Guid>();
        while (root.ParentNodeId is Guid parentId && byId.TryGetValue(parentId, out var parent) && ancestors.Add(parentId))
            root = parent;
        AppendBranch(root, 0, new HashSet<Guid>());
        if (!lines.Any(line => line.Contains(selected.Title, StringComparison.Ordinal)))
            lines.Add($"- [{NodeCategoryLabel(selected.Category)}] {selected.Title}: {Trim(selected.Content, 1800)}");
        lines.Add($"\n当前操作节点：{selected.Title}");
        return string.Join("\n", lines);
    }

    private async Task GenerateNextNodeCandidatesAsync(WorkflowNode node)
    {
        if (RefuseWhenLocked(node, "生成下游节点候选")) return;
        if (autoExpanding) { MessageBox.Show("自动生成进行中，请先停止再试。", "生成下游候选"); return; }

        statusLabel.Text = $"正在结合节点树生成「{node.Title}」的下游候选…";
        try
        {
            var result = await aiProvider.GenerateAsync(new AiGenerationRequest
            {
                Node = node,
                Instruction = "结合完整节点树，为当前节点提出 2 到 6 个有实际下游价值的节点候选。候选应避免重复已有节点，遵循当前叙事与制作流程；只生成候选，不要修改当前节点。每个候选标题具体，内容包含可继续执行的信息。",
                Reference = canvas.State.DescribeReferenceForPrompt(node),
                NodeTreeContext = DescribeNodeTree(node)
            });

            if (result.NeedsUserInput)
            {
                MessageBox.Show(result.Question, "需要补充信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (result.Proposals.Count == 0)
            {
                MessageBox.Show("AI 没有返回下游节点候选，请检查模型配置或重试。", "没有候选节点", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedProposals = SelectNodeProposals(node, result.Proposals);
            if (selectedProposals.Count == 0) return;
            var created = canvas.AddGeneratedNodes(node, selectedProposals);
            if (created.Count > 0)
            {
                canvas.FocusNode(created[0].Id);
                canvas.NotifyContentChanged();
            }
            statusLabel.Text = $"已创建 {created.Count} 个下游节点候选";
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "生成下游候选失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private IReadOnlyList<AiNodeProposal> SelectNodeProposals(WorkflowNode parent, IReadOnlyList<AiNodeProposal> proposals)
    {
        using var dialog = new ScaledForm
        {
            Text = $"选择「{parent.Title}」的下游节点",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 520),
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.EditorBg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont
        };
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.PanelBg,
            ForeColor = Theme.Text,
            OwnerDraw = true
        };
        list.Columns.Add("待选节点", 250);
        list.Columns.Add("内容预览", 300);
        list.DrawColumnHeader += (_, e) =>
        {
            using var brush = new SolidBrush(Theme.Hover);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Theme.UiFont, Rectangle.Inflate(e.Bounds, -10, 0), Theme.TextMuted, TextFormatFlags.VerticalCenter);
        };
        list.DrawItem += (_, e) => e.DrawDefault = false;
        list.DrawSubItem += (_, e) =>
        {
            if (e.Item is not { } item || e.SubItem is not { } subItem) return;
            using var brush = new SolidBrush(item.Selected ? Theme.Selected : Theme.PanelBg);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var color = e.ColumnIndex == 0 ? Theme.Text : Theme.TextMuted;
            TextRenderer.DrawText(e.Graphics, subItem.Text, Theme.UiFont, Rectangle.Inflate(e.Bounds, -10, 0), color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        var detail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.PanelBg,
            ForeColor = Theme.Text,
            Padding = new Padding(12)
        };
        foreach (var proposal in proposals)
        {
            var item = new ListViewItem(proposal.Title) { Checked = true, Tag = proposal };
            item.SubItems.Add(Trim(proposal.Content, 120));
            list.Items.Add(item);
        }
        void ShowSelected()
        {
            detail.Text = list.SelectedItems.Count == 0 ? "选择候选查看内容" : ((AiNodeProposal)list.SelectedItems[0].Tag!).Content;
        }
        list.SelectedIndexChanged += (_, _) => ShowSelected();
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = Theme.EditorBg,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1MinSize = 250;
        split.Panel2MinSize = 260;
        split.SplitterDistance = 360;
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(detail);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var create = new Button { Text = "创建所选节点", Width = 120, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 80, Height = 30, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(create);
        buttons.Controls.Add(cancel);
        dialog.Controls.Add(split);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return Array.Empty<AiNodeProposal>();
        return list.Items.Cast<ListViewItem>()
            .Where(item => item.Checked)
            .Select(item => (AiNodeProposal)item.Tag!)
            .ToArray();
    }

    private async Task GenerateSelectedNodeAsync()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }
        if (RefuseWhenLocked(node, "重新生成内容")) return;
        if (autoExpanding) { MessageBox.Show("自动生成进行中，请先停止再试。", "YeeYeeYee"); return; }
        node.ExecutionStatus = NodeExecutionStatus.Generating; RefreshNodeInspector();
        try
        {
            var result = await aiProvider.GenerateAsync(new AiGenerationRequest
            {
                Node = node,
                Answer = node.Answer,
                Reference = canvas.State.DescribeReferenceForPrompt(node)
            });
            node.Question = result.Question ?? string.Empty;
            if (result.NeedsUserInput)
            {
                node.ExecutionStatus = NodeExecutionStatus.WaitingForUser;
            }
            else
            {
                node.ExecutionStatus = NodeExecutionStatus.NeedsReview;
                node.ContentSource = ContentSource.Ai;
            }
            node.GenerationId = Guid.NewGuid();
            node.GenerationHistory.Add(new GenerationHistory
            {
                Input = node.Content,
                Instruction = "AI 生成",
                Output = result.Output ?? string.Empty,
                Provider = result.Provider,
                Model = result.Model,
                Question = result.Question,
                Answer = node.Answer,
                Status = node.ExecutionStatus,
                Proposals = result.Proposals.ToList()
            });
        }
        catch (Exception error)
        {
            node.ExecutionStatus = NodeExecutionStatus.Failed;
            node.Question = error.Message;
        }
        RefreshNodeInspector(); canvas.Invalidate();
    }

    private void AcceptSelectedGeneration()
    {
        if (canvas.SelectedNode is not { } node) return;
        var latest = node.GenerationHistory.LastOrDefault(); if (latest is null) { MessageBox.Show("该节点还没有生成结果。", "YeeYeeYee"); return; }
        latest.Accepted = true; node.ContentSource = ContentSource.Ai; node.ExecutionStatus = NodeExecutionStatus.Completed; node.Content = latest.Output;
        if (latest.Proposals.Count > 0)
        {
            var names = string.Join("、", latest.Proposals.Select(p => p.Title));
            var confirm = MessageBox.Show($"是否根据本次生成创建 {latest.Proposals.Count} 个下游节点？\n{names}", "创建下游节点", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                var created = canvas.AddGeneratedNodes(node, latest.Proposals);
                MessageBox.Show($"已创建 {created.Count} 个下游节点。", "YeeYeeYee");
            }
        }
        RefreshNodeInspector(); canvas.Invalidate();
    }

    private void OpenNodeHistoryDialog()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }
        if (node.GenerationHistory.Count == 0) { MessageBox.Show("该节点还没有生成记录。", "YeeYeeYee"); return; }

        using var dialog = new ScaledForm { Text = $"生成历史 · {node.Title}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 420), Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("时间", 130); list.Columns.Add("类型", 90); list.Columns.Add("提供方", 140); list.Columns.Add("模型", 130); list.Columns.Add("状态", 80); list.Columns.Add("已采纳", 60); list.Columns.Add("输出预览", 260);
        foreach (var record in node.GenerationHistory)
        {
            var item = new ListViewItem(record.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(record.Instruction);
            item.SubItems.Add(record.Provider);
            item.SubItems.Add(record.Model);
            item.SubItems.Add(record.Status.ToString());
            item.SubItems.Add(record.Accepted ? "是" : "否");
            item.SubItems.Add(Trim(record.Output, 40));
            item.Tag = record;
            list.Items.Add(item);
        }
        if (list.Items.Count > 0) list.Items[list.Items.Count - 1].Selected = true;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var open = new Button { Text = "打开资产", Width = 90, Height = 30, Enabled = false };
        var rollback = new Button { Text = "回滚到此版本", Width = 110, Height = 30, Enabled = false };
        var regenerate = new Button { Text = "重新生成", Width = 90, Height = 30 };
        var detail = new Label { Dock = DockStyle.Bottom, Height = 96, AutoSize = false, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8) };

        void SelectRecord()
        {
            var has = list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is GenerationHistory;
            rollback.Enabled = has;
            var record = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as GenerationHistory : null;
            open.Enabled = record is not null && AssetStore.Exists(record.Output);
            detail.Text = record is null ? "选择一条记录查看详情。" :
                $"产生时间：{record.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n输入：{Trim(record.Input, 80)}\n提问：{record.Question ?? "-"}\n回答：{record.Answer ?? "-"}\n输出：{Trim(record.Output, 200)}";
        }
        list.SelectedIndexChanged += (_, _) => SelectRecord();

        open.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not GenerationHistory record) return;
            var path = AssetStore.Resolve(record.Output);
            if (path is null) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        rollback.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not GenerationHistory record) return;
            if (string.IsNullOrWhiteSpace(record.Output)) { MessageBox.Show("该记录没有可回滚的输出。", "回滚版本"); return; }
            if (MessageBox.Show($"将节点内容回滚到 {record.CreatedAt.ToLocalTime():MM-dd HH:mm:ss} 的版本？", "回滚版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RollbackTo(node, record);
            RefreshNodeInspector(); canvas.Invalidate();
            dialog.DialogResult = DialogResult.OK;
        };
        regenerate.Click += (_, _) => { dialog.DialogResult = DialogResult.Retry; };

        buttons.Controls.Add(open); buttons.Controls.Add(rollback); buttons.Controls.Add(regenerate);
        dialog.Controls.Add(list); dialog.Controls.Add(detail); dialog.Controls.Add(buttons);
        SelectRecord();

        var result = dialog.ShowDialog(this);
        if (result == DialogResult.Retry) _ = GenerateSelectedNodeAsync();
    }

    private static void RollbackTo(WorkflowNode node, GenerationHistory record)
    {
        foreach (var item in node.GenerationHistory) item.Accepted = item == record;
        record.Accepted = true;
        var assetPath = AssetStore.Resolve(record.Output);
        if (assetPath is not null)
        {
            // 图像版本回滚：把该版本资产作为当前预览附件，并保持可移植引用。
            var reference = AssetStore.ToReference(assetPath);
            node.Attachments.RemoveAll(attachment => string.Equals(attachment.Reference, reference, StringComparison.OrdinalIgnoreCase));
            node.Attachments.Insert(0, new WorkflowAttachment
            {
                Kind = WorkflowAttachment.KindOf(assetPath),
                Reference = reference,
                Name = Path.GetFileName(assetPath),
                Source = "版本回滚"
            });
            node.ContentSource = ContentSource.Api;
        }
        else
        {
            node.Content = record.Output;
            node.ContentSource = record.Provider.StartsWith("OpenAi", StringComparison.Ordinal) || record.Provider == "LocalAiProvider" ? ContentSource.Ai : node.ContentSource;
        }
        node.ExecutionStatus = NodeExecutionStatus.Completed;
        node.Question = string.Empty;
    }

    private static string Trim(string? text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text ?? string.Empty : text[..length] + "…";

    /// <summary>节点出图用的提示词：节点文本（或标题）加上引用设定的描述。</summary>
    private string ComposeNodePrompt(WorkflowNode node)
    {
        var text = string.IsNullOrWhiteSpace(node.Content) ? node.Title : node.Content;
        var reference = canvas.State.DescribeReferenceForPrompt(node);
        return string.IsNullOrWhiteSpace(reference) ? text : $"{text}\n{reference}";
    }

    /// <summary>
    /// 收集节点出图可用的全部参考图（按引用顺序去重）。这里**不做数量截断**：
    /// 能用几张由执行方（ComfyUI 工作流或图像接口）决定，画布只负责说明
    /// 「这个镜头有哪些参考图」。
    /// 已经做过分步合成时，合成产出就是底图，其余参考图已并入其中。
    /// </summary>
    private List<string> ResolveReferenceImagePaths(WorkflowNode node)
    {
        if (string.Equals(ReadParameter(node, "useReference"), "false", StringComparison.OrdinalIgnoreCase)) return new List<string>();
        var composed = node.Attachments
            .Where(item => item.Kind == AttachmentKind.Image && item.Source == WorkflowAttachment.SourceComposition)
            .Select(item => AssetStore.Resolve(item.Reference))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (composed.Count > 0) return composed;
        return canvas.State.ResolveReferences(node)
            .SelectMany(reference => reference.Attachments)
            .Where(item => item.Kind == AttachmentKind.Image)
            .Select(item => AssetStore.Resolve(item.Reference))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ImageGenerationRequest BuildImageRequest(WorkflowNode node, int maxReferences = 0)
    {
        var config = AiProviderSettings.Load();
        var (defaultWidth, defaultHeight) = ParseDefaultSize(config.ImageSize);
        var references = ResolveReferenceImagePaths(node);
        // 超出链路能力且用户选择截断时，只保留前 N 张。
        if (maxReferences > 0 && references.Count > maxReferences)
            references = references.Take(maxReferences).ToList();
        return new ImageGenerationRequest
        {
            Prompt = ComposeNodePrompt(node),
            NegativePrompt = ReadParameter(node, "negativePrompt") ?? config.DefaultNegativePrompt,
            Width = ParseInt(ReadParameter(node, "imageWidth"), defaultWidth, 64, 2048),
            Height = ParseInt(ReadParameter(node, "imageHeight"), defaultHeight, 64, 2048),
            Steps = ParseNullableInt(ReadParameter(node, "steps") ?? config.DefaultImageSteps, 1, 150),
            Cfg = ParseNullableDouble(ReadParameter(node, "cfg") ?? config.DefaultImageCfg, 1, 30),
            Seed = ParseNullableLong(ReadParameter(node, "seed")),
            ReferenceImages = references,
            Denoise = ParseNullableDouble(ReadParameter(node, "denoise"), 0.05, 1)
        };
    }

    /// <summary>解析“设置”里的默认图像尺寸，供未单独设置尺寸的节点使用。</summary>
    private static (int Width, int Height) ParseDefaultSize(string? size)
    {
        if (!string.IsNullOrWhiteSpace(size))
        {
            var parts = size.Split('x', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
                return (Math.Clamp(width, 64, 2048), Math.Clamp(height, 64, 2048));
        }
        return (1024, 1024);
    }

    private static string? ReadParameter(WorkflowNode node, string key) =>
        node.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ParseInt(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static int? ParseNullableInt(string? raw, int min, int max) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : null;

    private static double? ParseNullableDouble(string? raw, double min, double max) =>
        double.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : null;

    private static long? ParseNullableLong(string? raw) =>
        long.TryParse(raw, out var value) && value >= 0 ? value : null;

    private sealed record ImageParameterValues(string Negative, int Width, int Height, string Steps, string Cfg, string Seed, bool UseReference, string Denoise);

    private void OpenImageParametersDialog()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }

        var config = AiProviderSettings.Load();
        var (defaultWidth, defaultHeight) = ParseDefaultSize(config.ImageSize);
        using var dialog = new ScaledForm { Text = $"图像参数 · {node.Title}", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(470, 680), Font = Font };
        var negative = new TextBox { Multiline = true, Height = 60, Dock = DockStyle.Fill, Text = ReadParameter(node, "negativePrompt") ?? config.DefaultNegativePrompt, PlaceholderText = "负向提示词，例如：低清晰度、畸形、文字水印" };
        var width = new NumericUpDown { Minimum = 64, Maximum = 2048, Dock = DockStyle.Fill, Value = ParseInt(ReadParameter(node, "imageWidth"), defaultWidth, 64, 2048) };
        var height = new NumericUpDown { Minimum = 64, Maximum = 2048, Dock = DockStyle.Fill, Value = ParseInt(ReadParameter(node, "imageHeight"), defaultHeight, 64, 2048) };
        var steps = new TextBox { Dock = DockStyle.Fill, Text = ReadParameter(node, "steps") ?? config.DefaultImageSteps, PlaceholderText = "留空使用服务默认" };
        var cfg = new TextBox { Dock = DockStyle.Fill, Text = ReadParameter(node, "cfg") ?? config.DefaultImageCfg, PlaceholderText = "留空使用服务默认" };
        var seed = new TextBox { Dock = DockStyle.Fill, Text = ReadParameter(node, "seed") ?? string.Empty, PlaceholderText = "留空表示每次随机" };
        var useReference = new CheckBox
        {
            Text = "有参考图时走图生图（保持角色一致）",
            AutoSize = true,
            Checked = !string.Equals(ReadParameter(node, "useReference"), "false", StringComparison.OrdinalIgnoreCase)
        };
        var denoise = new TextBox { Dock = DockStyle.Fill, Text = ReadParameter(node, "denoise") ?? string.Empty, PlaceholderText = "留空使用 0.6；越低越贴近参考图" };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 17, Padding = new Padding(16) };
        for (var row = 0; row < 16; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "负向提示词", AutoSize = true }, 0, 0);
        layout.Controls.Add(negative, 0, 1);
        layout.Controls.Add(new Label { Text = "宽度", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 2);
        layout.Controls.Add(width, 0, 3);
        layout.Controls.Add(new Label { Text = "高度", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 4);
        layout.Controls.Add(height, 0, 5);
        layout.Controls.Add(new Label { Text = "采样步数", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 6);
        layout.Controls.Add(steps, 0, 7);
        layout.Controls.Add(new Label { Text = "CFG", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 8);
        layout.Controls.Add(cfg, 0, 9);
        layout.Controls.Add(new Label { Text = "种子", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 10);
        layout.Controls.Add(seed, 0, 11);
        layout.Controls.Add(useReference, 0, 12);
        layout.Controls.Add(new Label { Text = "重绘强度", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 13);
        layout.Controls.Add(denoise, 0, 14);
        layout.Controls.Add(new Label { Text = "步数、CFG、种子留空时会使用服务默认或“设为默认”的值；仅 ComfyUI 链路会使用这些参数。图生图需要节点引用的设定带参考图。", AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.Gray, Padding = new Padding(0, 10, 0, 0) }, 0, 15);

        ImageParameterValues? Read()
        {
            if (!IsValidOptionalInt(steps.Text) || !IsValidOptionalDouble(cfg.Text) || !IsValidOptionalLong(seed.Text) || !IsValidOptionalDouble(denoise.Text))
            {
                MessageBox.Show("步数、CFG、种子、重绘强度需要填写数字，或留空。", "图像参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return new ImageParameterValues(negative.Text.Trim(), (int)width.Value, (int)height.Value, steps.Text.Trim(), cfg.Text.Trim(), seed.Text.Trim(), useReference.Checked, denoise.Text.Trim());
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Height = 40 };
        var saveHere = new Button { Text = "保存到此节点", Width = 110, Height = 30 };
        saveHere.Click += (_, _) =>
        {
            if (Read() is not { } values) return;
            ApplyImageParameterValues(node, values);
            dialog.DialogResult = DialogResult.OK;
        };
        var applySameKind = new Button { Text = "应用到全部节点", Width = 120, Height = 30 };
        applySameKind.Click += (_, _) =>
        {
            if (Read() is not { } values) return;
            var peers = canvas.State.Nodes.Where(candidate => candidate.Id != node.Id).ToList();
            if (peers.Count == 0) { MessageBox.Show("画布上没有其它节点。", "图像参数"); return; }
            if (MessageBox.Show($"将把当前参数写入画布上另外 {peers.Count} 个节点。是否继续？", "图像参数", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ApplyImageParameterValues(node, values);
            foreach (var peer in peers) ApplyImageParameterValues(peer, values);
            dialog.DialogResult = DialogResult.OK;
            RefreshNodeInspector(); canvas.Invalidate();
            MessageBox.Show($"已更新 {peers.Count} 个节点。", "图像参数");
        };
        var makeDefault = new Button { Text = "设为默认", Width = 90, Height = 30 };
        makeDefault.Click += (_, _) =>
        {
            if (Read() is not { } values) return;
            if (MessageBox.Show("将把这组参数保存为默认值，供未单独设置的节点使用。是否继续？", "图像参数", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AiProviderSettings.Save(new AiProviderConfig
            {
                Endpoint = config.Endpoint,
                Model = config.Model,
                ApiKey = config.ApiKey,
                Temperature = config.Temperature,
                ImageEndpoint = config.ImageEndpoint,
                ImageModel = config.ImageModel,
                ImageSize = $"{values.Width}x{values.Height}",
                ComfyUiBaseUrl = config.ComfyUiBaseUrl,
                ComfyUiCheckpoint = config.ComfyUiCheckpoint,
                ComfyUiClientId = config.ComfyUiClientId,
                AssetDirectory = config.AssetDirectory,
                AutoGenerationRounds = config.AutoGenerationRounds,
                ImageMaxReferenceImages = config.ImageMaxReferenceImages,
                ProviderChoiceMade = config.ProviderChoiceMade,
                UseLocalProvider = config.UseLocalProvider,
                DefaultNegativePrompt = values.Negative,
                DefaultImageSteps = values.Steps,
                DefaultImageCfg = values.Cfg
            });
            dialog.DialogResult = DialogResult.OK;
            MessageBox.Show("已保存为默认图像参数。已设置过参数的节点不受影响。", "图像参数");
        };
        buttons.Controls.Add(saveHere); buttons.Controls.Add(applySameKind); buttons.Controls.Add(makeDefault);
        layout.Controls.Add(buttons, 0, 16);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            node.ExecutionStatus = NodeExecutionStatus.Draft;
            RefreshNodeInspector(); canvas.Invalidate();
        }
    }

    /// <summary>把一组图像参数写入节点（空值会移除对应参数，回落到默认值）。</summary>
    private static void ApplyImageParameterValues(WorkflowNode node, ImageParameterValues values)
    {
        node.Parameters["negativePrompt"] = values.Negative;
        node.Parameters["imageWidth"] = values.Width.ToString();
        node.Parameters["imageHeight"] = values.Height.ToString();
        node.Parameters["useReference"] = values.UseReference ? "true" : "false";
        SaveOptional(node, "steps", values.Steps);
        SaveOptional(node, "cfg", values.Cfg);
        SaveOptional(node, "seed", values.Seed);
        SaveOptional(node, "denoise", values.Denoise);
    }

    private static bool IsValidOptionalInt(string raw) =>
        string.IsNullOrWhiteSpace(raw) || int.TryParse(raw, out _);

    private static bool IsValidOptionalDouble(string raw) =>
        string.IsNullOrWhiteSpace(raw) || double.TryParse(raw, out _);

    private static bool IsValidOptionalLong(string raw) =>
        string.IsNullOrWhiteSpace(raw) || long.TryParse(raw, out _);

    private static void SaveOptional(WorkflowNode node, string key, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) node.Parameters.Remove(key);
        else node.Parameters[key] = raw.Trim();
    }

    private bool autoExpanding;
    private CancellationTokenSource? autoExpandCancellation;
    private Button? autoExpandButton;

    /// <summary>单次自动展开允许创建的节点上限，避免不限制轮数时无限膨胀。</summary>
    private const int MaxAutoGenerationNodes = 500;

    private async void AutoExpandButton_Click(object? sender, EventArgs e)
    {
        if (autoExpanding)
        {
            autoExpandCancellation?.Cancel();
            return;
        }
        await ExpandDownstreamAsync();
    }

    /// <summary>
    /// 从选中节点开始自动展开下游节点：每轮对当前层节点调用 AI，采纳结果并创建其建议的下游节点，
    /// 直到用尽配置轮数（0 表示不限制）、达到节点上限、用户取消或没有新的建议为止。
    /// </summary>
    private async Task ExpandDownstreamAsync()
    {
        if (canvas.SelectedNode is not { } root) { MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }
        if (autoExpanding) { MessageBox.Show("已有自动生成任务在进行中。", "自动生成"); return; }

        var rounds = AiProviderSettings.Load().AutoGenerationRounds;
        var unlimited = rounds <= 0;
        var plan = unlimited
            ? $"当前设置为不限制轮数，会持续调用 AI 直到没有新的下游建议，最多创建 {MaxAutoGenerationNodes} 个节点"
            : $"最多 {rounds} 轮，且最多创建 {MaxAutoGenerationNodes} 个节点";
        if (MessageBox.Show(
                $"将从“{root.Title}”自动展开下游节点。\n{plan}。\n\n是否继续？",
                "自动生成下游节点", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        autoExpanding = true;
        autoExpandCancellation = new CancellationTokenSource();
        var token = autoExpandCancellation.Token;
        if (autoExpandButton is not null) autoExpandButton.Text = "停止自动生成";
        var created = 0;
        var round = 0;
        var reachedLimit = false;
        var cancelled = false;
        var level = new List<WorkflowNode> { root };
        try
        {
            while (level.Count > 0 && (unlimited || round < rounds))
            {
                round++;
                var next = new List<WorkflowNode>();
                for (var index = 0; index < level.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var node = level[index];
                    statusLabel.Text = $"自动生成 第 {round} 轮 {index + 1}/{level.Count} · 已创建 {created}";
                    var result = await aiProvider.GenerateAsync(new AiGenerationRequest
                    {
                        Node = node,
                        Answer = node.Answer,
                        Reference = canvas.State.DescribeReferenceForPrompt(node)
                    }, token);
                    if (result.NeedsUserInput || string.IsNullOrWhiteSpace(result.Output))
                    {
                        // 需要用户补充信息的分支不继续展开，保留提问等待人工处理。
                        node.Question = result.Question ?? string.Empty;
                        node.ExecutionStatus = result.NeedsUserInput ? NodeExecutionStatus.WaitingForUser : node.ExecutionStatus;
                        continue;
                    }
                    ApplyGeneratedResult(node, result, accepted: true);
                    var children = CreateProposedChildren(node, result.Proposals, MaxAutoGenerationNodes - created);
                    created += children.Count;
                    next.AddRange(children);
                    if (created >= MaxAutoGenerationNodes) { reachedLimit = true; break; }
                }
                if (reachedLimit) break;
                level = next;
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            autoExpanding = false;
            autoExpandCancellation?.Dispose();
            autoExpandCancellation = null;
            if (!IsDisposed)
            {
                if (autoExpandButton is not null) autoExpandButton.Text = "自动生成下游";
                statusLabel.Text = "空闲";
                RefreshNodeInspector();
                canvas.Invalidate();
            }
        }

        if (IsDisposed) return;
        var summary = cancelled
            ? $"已停止自动生成，共创建 {created} 个节点。"
            : reachedLimit
                ? $"已达到单次上限（{MaxAutoGenerationNodes} 个节点），共创建 {created} 个节点。"
                : $"自动生成完成，共创建 {created} 个节点。";
        MessageBox.Show(summary, "自动生成");
    }

    /// <summary>采纳一次生成结果，写入内容与生成历史。</summary>
    private static void ApplyGeneratedResult(WorkflowNode node, AiGenerationResult result, bool accepted)
    {
        var input = node.Content;
        node.Content = result.Output ?? node.Content;
        node.ContentSource = ContentSource.Ai;
        node.ExecutionStatus = NodeExecutionStatus.Completed;
        node.Question = string.Empty;
        node.GenerationId = Guid.NewGuid();
        node.GenerationHistory.Add(new GenerationHistory
        {
            Input = input,
            Instruction = "AI 生成",
            Output = result.Output ?? string.Empty,
            Provider = result.Provider,
            Model = result.Model,
            Accepted = accepted,
            Status = node.ExecutionStatus
        });
    }

    /// <summary>为建议中尚不存在的下游节点创建节点，避免重复生成同一分支，并受剩余额度限制。</summary>
    private IReadOnlyList<WorkflowNode> CreateProposedChildren(WorkflowNode parent, IReadOnlyList<AiNodeProposal> proposals, int remaining)
    {
        if (proposals.Count == 0 || remaining <= 0) return Array.Empty<WorkflowNode>();
        var existing = canvas.State.Edges
            .Where(edge => edge.SourceNodeId == parent.Id)
            .Select(edge => canvas.State.Nodes.FirstOrDefault(node => node.Id == edge.TargetNodeId))
            .Where(node => node is not null)
            .Select(node => node!.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = proposals
            .Where(proposal => !existing.Contains(proposal.Title))
            .Take(remaining)
            .ToList();
        return fresh.Count == 0 ? Array.Empty<WorkflowNode>() : canvas.AddGeneratedNodes(parent, fresh);
    }

    /// <summary>任务详情：展示状态、错误与输出资产，并可打开产出图片或取消未完成任务。</summary>
    private void OpenJobDetailDialog()
    {
        if (jobList.SelectedItems.Count == 0) { MessageBox.Show("请先在任务记录中选择一条任务。", "任务详情"); return; }
        if (jobList.SelectedItems[0].Tag is not Guid jobId) return;
        if (!execution.TryGet(jobId, out var job) || job is null) { MessageBox.Show("任务不存在，可能已被清空或不在当前会话中。", "任务详情"); return; }

        using var dialog = new ScaledForm { Text = $"任务详情 · {jobId.ToString()[..8]}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(620, 560), Font = Font };
        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 116,
            Padding = new Padding(12),
            Text = $"状态：{job.State}\n进度：{job.ProgressPercent}%\n"
                + $"工具：{(string.IsNullOrWhiteSpace(job.Tool) ? "-" : job.Tool)} · 能力：{job.Capability} · 通道：{(string.IsNullOrWhiteSpace(job.Channel) ? "-" : job.Channel)}\n"
                + $"外部任务：{job.ExternalTaskId ?? "-"} · 幂等键：{job.IdempotencyKey}\n"
                + $"错误码：{job.ErrorCode ?? "-"}\n错误信息：{job.ErrorMessage ?? "-"}"
        };
        var inputList = new ListView { Dock = DockStyle.Bottom, Height = 150, View = View.Details, FullRowSelect = true, GridLines = true };
        inputList.Columns.Add("输入参数", 120); inputList.Columns.Add("值", 470);
        foreach (var input in job.Inputs)
        {
            var item = new ListViewItem(input.Key);
            item.SubItems.Add(Trim(DescribeJson(input.Value), 300));
            inputList.Items.Add(item);
        }
        if (inputList.Items.Count == 0)
            inputList.Items.Add(new ListViewItem(new[] { "-", "该任务没有记录输入参数（可能是旧记录或本地模拟任务）" }));
        var outputList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        outputList.Columns.Add("角色", 90); outputList.Columns.Add("输出引用", 470);
        foreach (var output in job.Outputs)
        {
            var item = new ListViewItem(output.Role);
            item.SubItems.Add(output.Ref);
            item.Tag = output.Ref;
            outputList.Items.Add(item);
        }
        if (outputList.Items.Count > 0) outputList.Items[0].Selected = true;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var openAsset = new Button { Text = "打开产出", Width = 96, Height = 32 };
        openAsset.Click += (_, _) =>
        {
            if (outputList.SelectedItems.Count == 0) { MessageBox.Show("请先选择一条输出。", "任务详情"); return; }
            var reference = outputList.SelectedItems[0].Tag as string;
            var path = AssetStore.Resolve(reference);
            if (path is null)
            {
                MessageBox.Show("该输出不是本机可打开的图片文件（本地模拟任务或外部地址）。", "任务详情");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        var cancelJob = new Button { Text = "取消任务", Width = 96, Height = 32, Enabled = job.State is JobState.Queued or JobState.Running };
        cancelJob.Click += (_, _) =>
        {
            try
            {
                execution.Cancel(session, jobId);
                MessageBox.Show("已请求取消任务。", "任务详情");
            }
            catch (Exception error) when (error is ProtocolViolationException or InvalidOperationException)
            {
                MessageBox.Show(error.Message, "取消失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var resubmit = new Button { Text = "按相同参数重新发起", Width = 140, Height = 32, Enabled = !string.IsNullOrWhiteSpace(job.Tool) };
        resubmit.Click += async (_, _) => await ResubmitJobAsync(job);
        buttons.Controls.Add(openAsset); buttons.Controls.Add(resubmit); buttons.Controls.Add(cancelJob);
        dialog.Controls.Add(summary); dialog.Controls.Add(buttons); dialog.Controls.Add(inputList); dialog.Controls.Add(outputList);
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 按历史任务记录的调用信息与输入参数重新发起一次执行。
    /// 使用全新的幂等键，避免被幂等注册表判为重复而直接返回旧结果。
    /// </summary>
    private async Task ResubmitJobAsync(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.Tool))
        {
            MessageBox.Show("该任务没有记录调用信息（可能是旧记录），无法重新发起。", "重新发起");
            return;
        }
        if (MessageBox.Show(
                $"将按相同参数重新发起一次任务（工具 {job.Tool} · 能力 {job.Capability} · 通道 {job.Channel}）。是否继续？",
                "重新发起", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var invocation = new Invocation
        {
            Tool = job.Tool,
            Capability = job.Capability,
            Channel = job.Channel,
            Inputs = job.Inputs
        };
        try
        {
            var result = await execution.StartAsync(session, invocation, $"desktop-resubmit-{Guid.NewGuid():N}");
            SetCurrent(result);
            MessageBox.Show($"已重新发起，新任务 {result.JobId.ToString()[..8]}（{result.State}）。", "重新发起");
        }
        catch (Exception error) when (error is ProtocolViolationException or InvalidOperationException)
        {
            MessageBox.Show(error.Message, "重新发起失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>把输入参数值转成便于阅读的文本。</summary>
    private static string DescribeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
        JsonValueKind.Null or JsonValueKind.Undefined => "-",
        _ => element.GetRawText()
    };

    private void RefreshProviderLabel()
    {
        var config = AiProviderSettings.Load();
        var text = config.UseLocalProvider || !config.IsConfigured
            ? "AI：本地模拟（未接入大模型，点击接入）"
            : $"AI：{config.Model}";
        var image = config.IsComfyUiConfigured ? $"ComfyUI：{config.ComfyUiCheckpoint}"
            : config.IsImageConfigured ? $"图像：{config.ImageModel}"
            : "图像：未配置";
        providerLabel.Text = $"{text} · {image}";
    }

    private async Task GenerateSelectedImageAsync()
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }
        if (RefuseWhenLocked(node, "生成参考图")) return;
        if (autoExpanding) { MessageBox.Show("自动生成进行中，请先停止再试。", "YeeYeeYee"); return; }
        var prompt = ComposeNodePrompt(node);
        if (string.IsNullOrWhiteSpace(prompt)) { MessageBox.Show("请先填写节点内容作为图像提示词。", "YeeYeeYee"); return; }

        var provider = ImageProviderFactory.Create(execution);
        if (!provider.IsConfigured)
        {
            MessageBox.Show("尚未配置图像模型。请先在“设置”中填写 ComfyUI 地址与 checkpoint，或填写图像模型名称。", "图像生成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 提交前预检：参考图数量超出链路能力时先问用户，避免白跑一次。
        var capacity = provider.ReferenceCapacity;
        var maxReferences = 0;
        var available = ResolveReferenceImagePaths(node);
        if (capacity.IsLimited && available.Count > capacity.MaxImages)
        {
            switch (AskReferenceOverflow(available.Count, capacity))
            {
                case ReferenceOverflowChoice.Cancel: return;
                case ReferenceOverflowChoice.Compose:
                    await ComposeReferencesForSelectedNodeAsync(capacity);
                    return;
                default:
                    maxReferences = capacity.MaxImages;
                    break;
            }
        }

        node.ExecutionStatus = NodeExecutionStatus.Generating; RefreshNodeInspector();
        var request = BuildImageRequest(node, maxReferences);
        var mode = request.ReferenceImages.Count == 0 ? "图像生成" : $"图生图（{request.ReferenceImages.Count} 张参考图）";
        var result = await provider.GenerateAsync(request);

        if (result.Status == ImageGenerationStatus.Succeeded)
        {
            var assetReference = AssetStore.ToReference(result.FilePath);
            var fileName = Path.GetFileName(result.FilePath);
            node.Attachments.Add(new WorkflowAttachment
            {
                Kind = WorkflowAttachment.KindOf(result.FilePath),
                Reference = assetReference,
                Name = fileName,
                Source = "生成"
            });
            // 节点引用了设定时，询问是否把这张图一并作为某个变体当前内容的参考图。
            var referenced = canvas.State.ResolveReferences(node);
            if (referenced.Count > 0 && PickWriteBackTarget(referenced) is { } target)
            {
                target.Variant.Attachments.Add(new WorkflowAttachment
                {
                    Kind = AttachmentKind.Image,
                    Reference = assetReference,
                    Name = fileName,
                    Source = "节点出图"
                });
                canvas.InvalidateThumbnails();
                RefreshVariantList();
            }
            node.Parameters["imagePrompt"] = prompt;
            node.Parameters["imageProvider"] = result.Provider;
            node.Parameters["imageModel"] = result.Model;
            node.Parameters["imageReferenceCount"] = request.ReferenceImages.Count.ToString();
            if (!string.IsNullOrWhiteSpace(result.ReferenceNote)) node.Parameters["imageReferenceNote"] = result.ReferenceNote;
            else node.Parameters.Remove("imageReferenceNote");
            node.ContentSource = ContentSource.Api;
            node.ExecutionStatus = NodeExecutionStatus.Completed;
            node.Question = string.Empty;
            node.GenerationHistory.Add(new GenerationHistory
            {
                Input = prompt,
                Instruction = mode,
                Output = assetReference,
                Provider = result.Provider,
                Model = result.Model,
                Status = node.ExecutionStatus
            });
        }
        else
        {
            node.ExecutionStatus = NodeExecutionStatus.Failed;
            node.Question = result.Error;
            node.GenerationHistory.Add(new GenerationHistory
            {
                Input = prompt,
                Instruction = mode,
                Output = string.Empty,
                Provider = result.Provider,
                Model = result.Model,
                Question = result.Error,
                Status = node.ExecutionStatus
            });
        }

        RefreshNodeInspector(); canvas.Invalidate();
        if (result.Status != ImageGenerationStatus.Succeeded)
            MessageBox.Show(result.Error, "图像生成失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (!string.IsNullOrWhiteSpace(result.ReferenceNote))
            MessageBox.Show(result.ReferenceNote, "参考图使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private enum ReferenceOverflowChoice { Compose, Truncate, Cancel }

    /// <summary>
    /// 参考图数量超出链路能力时的提交前预检：在花时间、花额度之前就告知用户，
    /// 并给出「先分步合成」「只用前 N 张」「取消」三种选择。
    /// </summary>
    private ReferenceOverflowChoice AskReferenceOverflow(int total, ReferenceCapacity capacity)
    {
        var lines = new List<string>
        {
            $"本镜头有 {total} 张参考图，但{capacity.Description}最多使用 {capacity.MaxImages} 张。",
            string.Empty,
            "· 先分步合成：把多张参考图逐步并成一张底图再出图，每一步都不超过链路的上限。",
            $"· 只用前 {capacity.MaxImages} 张：直接截断，其余参考图不参与本次生成。"
        };
        if (!capacity.CanUseMultiple)
            lines.Add("\n注意：当前链路一次只能使用 1 张参考图，分步合成也用不上——第二张图传不进去。要合成多张请先换成支持多图的工作流模板。");

        using var dialog = new ScaledForm
        {
            Text = "参考图超出链路上限",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 240),
            Font = Font
        };
        var hint = new Label { Dock = DockStyle.Fill, Padding = new Padding(16), Text = string.Join("\n", lines) };
        var compose = new Button { Text = "先分步合成", Width = 130, Height = 34, FlatStyle = FlatStyle.Flat, Enabled = capacity.CanUseMultiple };
        var truncate = new Button { Text = $"只用前 {capacity.MaxImages} 张", Width = 140, Height = 34, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Width = 96, Height = 34, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        var choice = ReferenceOverflowChoice.Cancel;
        compose.Click += (_, _) => { choice = ReferenceOverflowChoice.Compose; dialog.DialogResult = DialogResult.OK; };
        truncate.Click += (_, _) => { choice = ReferenceOverflowChoice.Truncate; dialog.DialogResult = DialogResult.OK; };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(truncate); buttons.Controls.Add(compose);
        dialog.Controls.Add(hint); dialog.Controls.Add(buttons);
        dialog.CancelButton = cancel;
        dialog.ShowDialog(this);
        return choice;
    }

    /// <summary>
    /// 规划并执行参考图分步合成：把本镜头的多张参考图逐步并成一张底图，每步都不超过链路上限。
    /// 方案可以来自大模型建议，也可以由用户选备选策略或直接输入要求后重新规划。
    /// </summary>
    private async Task ComposeReferencesForSelectedNodeAsync(ReferenceCapacity? capacity = null)
    {
        if (canvas.SelectedNode is not { } node) { MessageBox.Show("请先选择一个节点。", "参考图合成"); return; }
        if (RefuseWhenLocked(node, "合成参考图")) return;

        var provider = ImageProviderFactory.Create(execution);
        if (!provider.IsConfigured)
        {
            MessageBox.Show("尚未配置图像模型，无法合成参考图。", "参考图合成");
            return;
        }
        var effective = capacity ?? provider.ReferenceCapacity;
        if (!effective.CanUseMultiple)
        {
            MessageBox.Show(
                $"当前链路{effective.Description}无法同时使用两张图，分步合成用不上——第二张图传不进去。\n\n" +
                "要合成多张参考图，需要先换成支持多图的工作流模板。",
                "参考图合成");
            return;
        }

        var planningRequest = BuildCompositionPlanningRequest(node, effective);
        if (planningRequest.References.Count < 2)
        {
            MessageBox.Show("分步合成至少需要两张带参考图的引用。", "参考图合成");
            return;
        }

        var planner = aiProvider as ICompositionPlanner ?? new LocalAiProvider();
        CompositionPlan initial;
        try
        {
            initial = await planner.PlanCompositionAsync(planningRequest);
        }
        catch (Exception error)
        {
            MessageBox.Show($"规划失败：{error.Message}", "参考图合成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (ReviewCompositionPlan(node, planningRequest, planner, initial) is not { } plan) return;
        var composition = new SkillDefinition
        {
            Id = "reference-compose",
            Name = "参考图合成",
            OutputTarget = "node-base",
            Steps = plan.Steps.Select(step => new SkillStep
            {
                Id = step.Id,
                Name = string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name,
                Capability = nameof(Capability.ImageToImage),
                ReferenceFrom = string.Join(",", step.Inputs),
                Prompt = step.Prompt,
                Denoise = step.Denoise
            }).ToList()
        };
        await ExecuteSkillWithProgressAsync(composition, new SkillTarget { Canvas = canvas.State, Node = node });
    }

    /// <summary>
    /// 构造规划请求。ref:N 的下标与 SkillRunner 解析参考图时的顺序一致：
    /// 按引用顺序展开每条引用的图片，一条引用有多张图就占多个下标。
    /// </summary>
    private CompositionPlanningRequest BuildCompositionPlanningRequest(WorkflowNode node, ReferenceCapacity capacity)
    {
        var items = new List<ReferenceItemInfo>();
        var imageIndex = 0;
        foreach (var (_, content) in canvas.State.ResolveReferencePairs(node))
        {
            if (content is null) continue;
            var images = content.Attachments.Where(attachment => attachment.Kind == AttachmentKind.Image).ToList();
            for (var offset = 0; offset < images.Count; offset++)
                items.Add(new ReferenceItemInfo(
                    imageIndex++,
                    content.Entity.Name,
                    WorkflowEntity.KindName(content.Entity.Kind),
                    images.Count > 1 ? $"{content.Variant.Name}#{offset + 1}" : content.Variant.Name,
                    content.Description,
                    true));
        }
        return new CompositionPlanningRequest
        {
            NodeTitle = node.Title,
            NodeContent = node.Content,
            References = items,
            MaxImagesPerStep = capacity.IsLimited ? Math.Max(2, capacity.MaxImages) : 2
        };
    }

    /// <summary>
    /// 展示并确认合成方案：可以看 AI 的分步建议、选备选策略或直接输入要求重新规划，
    /// 也能逐条改步骤提示词。确认后返回要执行的方案。
    /// </summary>
    private CompositionPlan? ReviewCompositionPlan(WorkflowNode node, CompositionPlanningRequest request, ICompositionPlanner planner, CompositionPlan plan)
    {
        var steps = plan.Steps.ToList();
        using var dialog = new ScaledForm { Text = $"参考图合成规划 · {node.Title}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(800, 620), Font = Font };
        var summary = new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(12), ForeColor = Color.FromArgb(60, 60, 70) };
        var stepList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true };
        stepList.Columns.Add("步骤", 70); stepList.Columns.Add("输入", 200); stepList.Columns.Add("提示词", 480);
        var promptBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "选中步骤后可在这里改它的提示词，然后点「保存提示词」" };
        var instructionBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "直接输入你的要求，例如：先合成角色与剑，最后再加背包" };
        var alternatives = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var savePrompt = new Button { Text = "保存提示词", Width = 104, Height = 32, FlatStyle = FlatStyle.Flat };
        var replan = new Button { Text = "按上面的策略重新规划", Width = 176, Height = 32, FlatStyle = FlatStyle.Flat };
        var run = new Button { Text = "执行合成", Width = 104, Height = 32, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

        void Render()
        {
            summary.Text = string.IsNullOrWhiteSpace(plan.Summary) ? "（没有方案说明）" : plan.Summary;
            stepList.BeginUpdate();
            stepList.Items.Clear();
            foreach (var step in steps)
            {
                var item = new ListViewItem(step.Id);
                item.SubItems.Add(string.Join(", ", step.Inputs));
                item.SubItems.Add(step.Prompt);
                item.Tag = step;
                stepList.Items.Add(item);
            }
            stepList.EndUpdate();
            if (stepList.Items.Count > 0) stepList.Items[0].Selected = true;
            alternatives.BeginUpdate();
            alternatives.Items.Clear();
            foreach (var alternative in plan.Alternatives) alternatives.Items.Add(alternative);
            alternatives.EndUpdate();
            if (alternatives.Items.Count > 0) alternatives.SelectedIndex = 0;
        }

        void ShowStep()
        {
            if (stepList.SelectedItems.Count == 0 || stepList.SelectedItems[0].Tag is not CompositionStepPlan step) return;
            promptBox.Text = step.Prompt;
        }

        CompositionPlanningRequest WithInstruction(string instruction) => new()
        {
            NodeTitle = request.NodeTitle,
            NodeContent = request.NodeContent,
            References = request.References,
            MaxImagesPerStep = request.MaxImagesPerStep,
            Instruction = instruction
        };

        async void ReplanWithUserInput()
        {
            var instruction = string.IsNullOrWhiteSpace(instructionBox.Text)
                ? alternatives.SelectedItem as string ?? string.Empty
                : instructionBox.Text.Trim();
            replan.Enabled = false;
            run.Enabled = false;
            summary.Text = "正在重新规划…";
            try
            {
                plan = await planner.PlanCompositionAsync(WithInstruction(instruction));
                steps = plan.Steps.ToList();
                Render();
            }
            catch (Exception error)
            {
                MessageBox.Show($"重新规划失败：{error.Message}", "参考图合成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Render();
            }
            finally
            {
                replan.Enabled = true;
                run.Enabled = true;
            }
        }

        savePrompt.Click += (_, _) =>
        {
            if (stepList.SelectedItems.Count == 0 || stepList.SelectedItems[0].Tag is not CompositionStepPlan step) return;
            step.Prompt = promptBox.Text.Trim();
            Render();
        };
        replan.Click += (_, _) => ReplanWithUserInput();
        stepList.SelectedIndexChanged += (_, _) => ShowStep();
        run.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        alternatives.SelectedIndexChanged += (_, _) => { if (alternatives.SelectedItem is string text) instructionBox.Text = text; };

        var alternativeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        alternativeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        alternativeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alternativeRow.Controls.Add(FieldLabel("备选策略"), 0, 0);
        alternativeRow.Controls.Add(alternatives, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[1] = new RowStyle(SizeType.Absolute, 76);
        layout.RowStyles[3] = new RowStyle(SizeType.Percent, 100);
        layout.RowStyles[5] = new RowStyle(SizeType.Absolute, 76);
        layout.RowStyles[6] = new RowStyle(SizeType.Absolute, 30);
        layout.RowStyles[8] = new RowStyle(SizeType.Absolute, 28);
        layout.Controls.Add(FieldLabel("方案说明"), 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(FieldLabel("合成步骤（每步不超过链路允许的参考图数量）"), 0, 2);
        layout.Controls.Add(stepList, 0, 3);
        layout.Controls.Add(FieldLabel("步骤提示词"), 0, 4);
        layout.Controls.Add(promptBox, 0, 5);
        layout.Controls.Add(alternativeRow, 0, 6);
        layout.Controls.Add(FieldLabel("直接输入要求（留空则使用上面选中的备选策略）"), 0, 7);
        layout.Controls.Add(instructionBox, 0, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = Padding.Empty, Margin = Padding.Empty };
        buttons.Controls.Add(cancel); buttons.Controls.Add(run); buttons.Controls.Add(replan); buttons.Controls.Add(savePrompt);
        layout.Controls.Add(buttons, 0, 9);

        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        Render();
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new CompositionPlan
        {
            Summary = plan.Summary,
            Question = plan.Question,
            Steps = steps,
            Alternatives = plan.Alternatives
        };
    }

    /// <summary>
    /// 出图后询问把这张图写入哪个引用的变体：只有一条引用时直接问是否写入，
    /// 多条引用时列出让用户选择，避免把图写到错误的角色上。
    /// </summary>
    private ReferenceContent? PickWriteBackTarget(IReadOnlyList<ReferenceContent> references)
    {
        if (references.Count == 1)
            return MessageBox.Show(
                    $"是否把这张图同时加入「{references[0].Label}」的参考图？\n\n" +
                    "加入后属于变体的当前内容，需要在设定库里「提交新版本」才会固化，届时才能被引用锁定。",
                    "写入设定库", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
                ? references[0]
                : null;

        using var dialog = new ScaledForm
        {
            Text = "写入哪个设定",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(480, 320),
            Font = Font
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true };
        list.Columns.Add("引用", 340); list.Columns.Add("版本", 110);
        foreach (var reference in references)
        {
            var item = new ListViewItem(reference.Label);
            item.SubItems.Add(reference.VersionLabel);
            item.Tag = reference;
            list.Items.Add(item);
        }
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        list.DoubleClick += (_, _) => dialog.DialogResult = DialogResult.OK;
        var ok = new Button { Text = "写入", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        var skip = new Button { Text = "跳过", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(skip); buttons.Controls.Add(ok);
        var hint = new Label
        {
            Text = "这张图要作为哪条引用对应变体的参考图？",
            Dock = DockStyle.Top, Height = 32, ForeColor = Color.Gray, Padding = new Padding(12, 8, 12, 0)
        };
        dialog.Controls.Add(list); dialog.Controls.Add(hint); dialog.Controls.Add(buttons);
        dialog.CancelButton = skip;
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ReferenceContent : null;
    }

    /// <summary>
    /// 链路自检：探测当前后端**实际**支持哪些提交方式，作为编写工作流模板的依据。
    /// ComfyUI 走 /object_info 拿到真实节点与必填输入名；OpenAI 兼容接口列出可用模型。
    /// 写多图或视频模板前应先跑这个，而不是照文档猜节点名。
    /// </summary>
    private async Task RunBackendProbeAsync(IWin32Window? owner = null)
    {
        var config = AiProviderSettings.Load();
        using var waiting = new ScaledForm
        {
            Text = "链路自检",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ControlBox = false,
            ClientSize = new Size(380, 90),
            Font = Font
        };
        waiting.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "正在探测后端接受的提交方式…" });
        ShowWaitingForm(waiting, owner);

        var results = new List<CapabilityProbeResult>();
        try
        {
            if (!string.IsNullOrWhiteSpace(config.ComfyUiBaseUrl))
                results.Add(await ComfyUiProbe.RunAsync(config.ComfyUiBaseUrl));
            if (!string.IsNullOrWhiteSpace(config.Endpoint))
                results.Add(await OpenAiCompatibleProbe.RunAsync(config));
            if (results.Count == 0)
                results.Add(CapabilityProbeResult.Unreachable("未配置后端", "设置里既没有 ComfyUI 地址，也没有 OpenAI 兼容接口地址"));
        }
        finally
        {
            waiting.Close();
        }
        ShowProbeResults(results, config, owner);
    }

    private void ShowProbeResults(IReadOnlyList<CapabilityProbeResult> results, AiProviderConfig config, IWin32Window? owner)
    {
        using var dialog = new ScaledForm { Text = "链路自检 · 提交方式探测", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(880, 640), Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
        list.Columns.Add("后端 · 分类", 190); list.Columns.Add("项目", 230); list.Columns.Add("状态", 60); list.Columns.Add("说明 / 提交字段", 370);
        foreach (var result in results)
            foreach (var finding in result.Findings)
            {
                var item = new ListViewItem($"{result.Backend} · {finding.Category}");
                item.SubItems.Add(finding.Name);
                item.SubItems.Add(finding.Available ? "可用" : "缺失");
                item.SubItems.Add(finding.Detail);
                list.Items.Add(item);
            }
        if (list.Items.Count == 0) list.Items.Add(new ListViewItem(new[] { "-", "-", "-", "没有探测到任何结果。" }));

        var detail = new TextBox { Dock = DockStyle.Bottom, Height = 200, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 252) };
        var lines = new List<string>();
        foreach (var result in results)
        {
            lines.Add($"【{result.Backend}】{(result.Reachable ? "可达" : "不可达")} · {result.Summary}");
            lines.AddRange(result.Notes.Select(note => "· " + note));
            lines.Add(string.Empty);
        }
        var comfy = ComfyUiSubmissionProfiles.SingleBaseImage;
        lines.Add("当前已实现的提交方式声明：");
        lines.Add($"· ComfyUI：{comfy.Name}｜上限 {comfy.ReferenceCapacity.MaxImages} 张参考图｜{comfy.Kind}｜{comfy.Description}");
        lines.Add($"· OpenAI 兼容图像接口：声明上限 {config.ImageMaxReferenceImages} 张参考图（0 表示不限）｜同步返回｜" +
                  (config.ImageMaxReferenceImages >= 2 || config.ImageMaxReferenceImages == 0 ? "可做分步合成" : "只支持单张底图"));
        lines.Add(string.Empty);
        lines.Add("下一步：按上面探测到的节点名与必填输入名，在 ComfyUiWorkflowFactory 里加对应分支，并在 ComfyUiSubmissionProfiles 里声明上限与同步/异步。");
        detail.Text = string.Join("\n", lines);

        var copy = new Button { Text = "复制结论", Width = 100, Height = 32, FlatStyle = FlatStyle.Flat };
        copy.Click += (_, _) =>
        {
            try
            {
                var rows = list.Items.Cast<ListViewItem>()
                    .Select(row => string.Join(" | ", row.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(sub => sub.Text)));
                Clipboard.SetText(detail.Text + "\n" + string.Join("\n", rows));
                MessageBox.Show("已复制到剪贴板。", "链路自检");
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var close = new Button { Text = "关闭", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        buttons.Controls.Add(close); buttons.Controls.Add(copy);
        dialog.Controls.Add(list); dialog.Controls.Add(detail); dialog.Controls.Add(buttons);
        dialog.CancelButton = close;
        dialog.ShowDialog(owner ?? this);
    }

    private void OpenAiSettingsDialog()
    {
        var config = AiProviderSettings.Load();
        using var dialog = new ScaledForm { Text = "AI Provider 设置", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(480, 700), Font = Font };
        var endpoint = new TextBox { Text = config.Endpoint, Dock = DockStyle.Fill, PlaceholderText = "https://api.example.com/v1" };
        var model = new TextBox { Text = config.Model, Dock = DockStyle.Fill, PlaceholderText = "模型名称" };
        var apiKey = new TextBox { Text = config.ApiKey, Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var imageModel = new TextBox { Text = config.ImageModel, Dock = DockStyle.Fill, PlaceholderText = "图像模型名称，留空表示不启用" };
        var imageEndpoint = new TextBox { Text = config.ImageEndpoint, Dock = DockStyle.Fill, PlaceholderText = "留空则复用上方接口地址" };
        var imageSize = new TextBox { Text = config.ImageSize, Dock = DockStyle.Fill, PlaceholderText = "1024x1024" };
        var maxReferencesLabel = new Label { Text = "图像接口最多能同时使用几张参考图（0 表示不限制；1 表示只支持单张底图）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var comfyUrl = new TextBox { Text = config.ComfyUiBaseUrl, Dock = DockStyle.Fill, PlaceholderText = "http://127.0.0.1:8188" };
        var comfyCheckpoint = new TextBox { Text = config.ComfyUiCheckpoint, Dock = DockStyle.Fill, PlaceholderText = "checkpoint 文件名，填写后优先走 ComfyUI" };
        var assetDirectory = new TextBox { Text = config.AssetDirectory, Dock = DockStyle.Fill, PlaceholderText = $"留空使用默认的 {Path.Combine(AppPaths.Root, "assets")}" };
        var autoRounds = new NumericUpDown { Minimum = 0, Maximum = 20, Value = Math.Clamp(config.AutoGenerationRounds, 0, 20), Dock = DockStyle.Fill };
        var maxReferences = new NumericUpDown { Minimum = 0, Maximum = 64, Value = Math.Clamp(config.ImageMaxReferenceImages, 0, 64), Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 27, Padding = new Padding(16) };
        for (var row = 0; row < 26; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "接口地址（OpenAI 兼容 /chat/completions）", AutoSize = true }, 0, 0);
        layout.Controls.Add(endpoint, 0, 1);
        layout.Controls.Add(new Label { Text = "文本模型名称", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 2);
        layout.Controls.Add(model, 0, 3);
        layout.Controls.Add(new Label { Text = "API 密钥（仅保存在本机配置文件）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 4);
        layout.Controls.Add(apiKey, 0, 5);
        layout.Controls.Add(new Label { Text = "图像模型名称（用于 /images/generations）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 6);
        layout.Controls.Add(imageModel, 0, 7);
        layout.Controls.Add(new Label { Text = "图像接口地址（选填）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 8);
        layout.Controls.Add(imageEndpoint, 0, 9);
        layout.Controls.Add(new Label { Text = "图像尺寸（节点未单独设置时的默认尺寸）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 10);
        layout.Controls.Add(imageSize, 0, 11);
        layout.Controls.Add(maxReferencesLabel, 0, 12);
        layout.Controls.Add(maxReferences, 0, 13);
        layout.Controls.Add(new Label { Text = "ComfyUI 地址（填写后优先使用本地 ComfyUI 出图）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 14);
        layout.Controls.Add(comfyUrl, 0, 15);
        layout.Controls.Add(new Label { Text = "ComfyUI checkpoint", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 16);
        layout.Controls.Add(comfyCheckpoint, 0, 17);
        layout.Controls.Add(new Label { Text = "资产目录（图片存放位置，留空使用默认）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 18);
        layout.Controls.Add(assetDirectory, 0, 19);
        layout.Controls.Add(new Label { Text = "AI 自动生成轮数（0 表示不限制）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 20);
        layout.Controls.Add(autoRounds, 0, 21);
        layout.Controls.Add(new Label { Text = $"配置文件：{AiProviderSettings.ConfigFilePath}", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 8, 0, 0) }, 0, 22);
        var save = new Button { Text = "保存", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) =>
        {
            AiProviderSettings.Save(new AiProviderConfig
            {
                Endpoint = endpoint.Text.Trim(),
                Model = model.Text.Trim(),
                ApiKey = apiKey.Text.Trim(),
                Temperature = config.Temperature,
                ImageEndpoint = imageEndpoint.Text.Trim(),
                ImageModel = imageModel.Text.Trim(),
                ImageSize = string.IsNullOrWhiteSpace(imageSize.Text) ? "1024x1024" : imageSize.Text.Trim(),
                ComfyUiBaseUrl = comfyUrl.Text.Trim(),
                ComfyUiCheckpoint = comfyCheckpoint.Text.Trim(),
                ComfyUiClientId = config.ComfyUiClientId,
                AssetDirectory = assetDirectory.Text.Trim(),
                AutoGenerationRounds = (int)autoRounds.Value,
                ImageMaxReferenceImages = (int)maxReferences.Value,
                ProviderChoiceMade = true,
                // 在这个对话框里填了地址与模型就说明要接真实模型；留空则明确是本地模拟。
                UseLocalProvider = string.IsNullOrWhiteSpace(endpoint.Text) || string.IsNullOrWhiteSpace(model.Text),
                DefaultNegativePrompt = config.DefaultNegativePrompt,
                DefaultImageSteps = config.DefaultImageSteps,
                DefaultImageCfg = config.DefaultImageCfg
            });
            dialog.DialogResult = DialogResult.OK;
        };
        var probe = new Button { Text = "链路自检（探测提交方式）", Width = 190, Height = 32, FlatStyle = FlatStyle.Flat };
        probe.Click += async (_, _) => await RunBackendProbeAsync(dialog);
        var setup = new Button { Text = "接入引导", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        setup.Click += (_, _) =>
        {
            if (AiSetupDialog.Show(dialog, firstRun: false) is null) return;
            ApplyProviderChange();
            MessageBox.Show("已更新接入配置。", "接入引导");
        };
        var settingsButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = Padding.Empty, Margin = Padding.Empty };
        settingsButtons.Controls.Add(save); settingsButtons.Controls.Add(probe); settingsButtons.Controls.Add(setup);
        layout.Controls.Add(settingsButtons, 0, 23);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ApplyProviderChange();
            MessageBox.Show("AI Provider 设置已更新。", "YeeYeeYee");
        }
    }

    /// <summary>
    /// ComfyUI 配置发生变化时重建执行服务，使节点出图与任务区立即走同一条执行链路；其余情况保留既有 Job 记录。
    /// </summary>
    private void RebuildExecutionHostIfNeeded()
    {
        if (executionHost is null) return;
        var shouldUseComfyUi = AiProviderSettings.Load().IsComfyUiConfigured;
        if (shouldUseComfyUi == executionHost.ComfyUiBacked && string.Equals(AssetStore.Directory, executionHost.AssetDirectory, StringComparison.OrdinalIgnoreCase)) return;

        var previous = executionHost;
        previous.Execution.Updated -= OnExecutionUpdated;
        executionHost = DesktopExecutionHost.Create();
        execution = executionHost.Execution;
        executionStoreWarning = executionHost.StoreWarning;
        execution.Updated += OnExecutionUpdated;
        currentJobId = null;
        RefreshJobList();
        _ = previous.DisposeAsync();
    }

    /// <summary>
    /// 启动时恢复上次的画布：优先恢复绑定的画布库文件，其次是草稿，
    /// 再其次是画布库中最近修改的画布；都没有则保持空白画布。
    /// </summary>
    private void SwitchProject()
    {
        if (!pendingChanges.IsEmpty)
        {
            var choice = MessageBox.Show(
                $"有 {pendingChanges.Count} 条 Agent 待提交改动。\n\n是：提交并切换\n否：丢弃并切换\n取消：留在当前项目",
                "切换项目", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            if (choice == DialogResult.Yes) CommitPendingChanges();
            else DiscardPendingChanges();
        }

        SaveAllCanvasTabs();
        restartForProjectSelection = true;
        Close();
    }

    private void LoadInitialCanvas()
    {
        if (TryLoadCanvasTabs()) return;
        var bound = CanvasLibrary.LoadCurrentCanvasPath();
        if (bound is not null && CanvasLibrary.TryLoad(bound, out var boundState) && boundState is not null)
        {
            AddCanvasTab(boundState, bound);
            return;
        }
        if (CanvasLibrary.TryLoad(StorageMaintenance.DraftCanvasPath, out var draft) && draft is not null)
        {
            AddCanvasTab(draft, null);
            return;
        }
        var recent = CanvasLibrary.List().FirstOrDefault();
        if (recent is not null && CanvasLibrary.TryLoad(recent.Path, out var state) && state is not null)
        {
            AddCanvasTab(state, recent.Path);
            return;
        }
        AddCanvasTab(EmptyCanvasState(), null);
    }

    private void AddCanvasTab(RecentCanvasState state, string? path)
    {
        var tab = new CanvasTabState { Path = path, Title = string.IsNullOrWhiteSpace(state.Title) ? "未命名画布" : state.Title, Snapshot = state };
        canvasTabs.Add(tab);
        activeCanvasTab = tab;
        currentCanvasPath = path;
        ApplyCanvasState(state, advanceRevision: false);
        RebuildCanvasTabsUi();
        SetCurrentCanvasPath(path);
    }

    private void SaveCurrentCanvasTab()
    {
        if (activeCanvasTab is null) return;
        activeCanvasTab.Title = canvasTitle;
        activeCanvasTab.Path = currentCanvasPath;
        activeCanvasTab.Snapshot = BuildCanvasState();
        if (!string.IsNullOrWhiteSpace(currentCanvasPath))
        {
            try { CanvasLibrary.Save(activeCanvasTab.Snapshot, currentCanvasPath); } catch (IOException) { }
        }
    }

    private void SaveAllCanvasTabs()
    {
        SaveCurrentCanvasTab();
        try
        {
            var file = new CanvasTabsFile { ActiveTabId = activeCanvasTab?.Id ?? Guid.Empty };
            file.Tabs = canvasTabs.Select(tab => new CanvasTabFileItem { Id = tab.Id, Path = tab.Path, Snapshot = tab.Snapshot }).ToList();
            File.WriteAllText(AppPaths.Combine(CanvasTabsFileName), JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            if (activeCanvasTab is not null) SaveRecentCanvas();
        }
        catch (IOException) { }
    }

    private bool TryLoadCanvasTabs()
    {
        try
        {
            var path = AppPaths.Combine(CanvasTabsFileName);
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<CanvasTabsFile>(File.ReadAllText(path));
            if (file?.Tabs is not { Count: > 0 }) return false;
            foreach (var item in file.Tabs)
            {
                var state = item.Snapshot;
                if (state is null && !string.IsNullOrWhiteSpace(item.Path)) CanvasLibrary.TryLoad(item.Path, out state);
                if (state is not null) canvasTabs.Add(new CanvasTabState { Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id, Path = item.Path, Title = state.Title, Snapshot = state });
            }
            activeCanvasTab = canvasTabs.FirstOrDefault(tab => tab.Id == file.ActiveTabId) ?? canvasTabs[0];
            currentCanvasPath = activeCanvasTab.Path;
            ApplyCanvasState(activeCanvasTab.Snapshot, advanceRevision: false);
            RebuildCanvasTabsUi();
            SetCurrentCanvasPath(currentCanvasPath);
            return true;
        }
        catch (Exception error) when (error is IOException or JsonException) { return false; }
    }

    private void SaveRecentCanvas()
    {
        try { var directory = Path.GetDirectoryName(StorageMaintenance.DraftCanvasPath); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory); File.WriteAllText(StorageMaintenance.DraftCanvasPath, JsonSerializer.Serialize(BuildCanvasState(), new JsonSerializerOptions { WriteIndented = true })); } catch (IOException) { }
    }

    private void ActivateCanvasTab(CanvasTabState target)
    {
        if (switchingCanvasTab || target == activeCanvasTab) return;
        if (!pendingChanges.IsEmpty)
        {
            var choice = MessageBox.Show($"有 {pendingChanges.Count} 条 Agent 待提交改动。\n\n是：提交后切换\n否：丢弃后切换\n取消：留在当前标签", "切换画布", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            if (choice == DialogResult.Yes) CommitPendingChanges(); else DiscardPendingChanges();
        }
        switchingCanvasTab = true;
        try
        {
            SaveCurrentCanvasTab();
            activeCanvasTab = target;
            currentCanvasPath = target.Path;
            ApplyCanvasState(target.Snapshot, advanceRevision: false);
            SetCurrentCanvasPath(currentCanvasPath);
            RebuildCanvasTabsUi();
            RefreshCanvasLibrary();
        }
        finally { switchingCanvasTab = false; }
    }

    private void CloseCanvasTab(CanvasTabState tab)
    {
        if (canvasTabs.Count == 1) return;
        if (tab == activeCanvasTab) SaveCurrentCanvasTab();
        var index = canvasTabs.IndexOf(tab);
        canvasTabs.Remove(tab);
        if (tab == activeCanvasTab) activeCanvasTab = canvasTabs[Math.Clamp(index - 1, 0, canvasTabs.Count - 1)];
        currentCanvasPath = activeCanvasTab?.Path;
        if (activeCanvasTab is not null) ApplyCanvasState(activeCanvasTab.Snapshot, advanceRevision: false);
        RebuildCanvasTabsUi();
        SetCurrentCanvasPath(currentCanvasPath);
    }

    private void NewCanvasButton_Click(object? sender, EventArgs e)
    {
        if (activeCanvasTab is not null) SaveCurrentCanvasTab();
        AddCanvasTab(EmptyCanvasState() with { Title = $"画布 {DateTime.Now:yyyyMMdd-HHmmss}" }, null);
        RefreshCanvasLibrary();
        RefreshJobList();
    }

    private async void SubmitButton_Click(object? sender, EventArgs e)
    {
        submitButton.Enabled = false; try { var inputs = new Dictionary<string, JsonElement> { ["prompt"] = JsonSerializer.SerializeToElement(promptBox.Text), ["negativePrompt"] = JsonSerializer.SerializeToElement(negativePromptBox.Text), ["width"] = JsonSerializer.SerializeToElement((int)widthBox.Value), ["height"] = JsonSerializer.SerializeToElement((int)heightBox.Value), ["steps"] = JsonSerializer.SerializeToElement((int)stepsBox.Value), ["cfg"] = JsonSerializer.SerializeToElement((double)cfgBox.Value), ["seed"] = JsonSerializer.SerializeToElement(seedBox.Text) }; var result = await execution.StartAsync(session, new Invocation { Tool = "text-to-image", Capability = Capability.TextToImage, Channel = "comfyui", Inputs = inputs }, $"desktop-{Guid.NewGuid():N}"); SetCurrent(result); } catch (Exception error) { MessageBox.Show(error.Message, "提交失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); } finally { submitButton.Enabled = true; }
    }

    private void CancelButton_Click(object? sender, EventArgs e) { if (currentJobId is Guid jobId) execution.Cancel(session, jobId); }
    private void OnExecutionUpdated(ExecutionResult result) { if (IsDisposed) return; BeginInvoke(() => SetCurrent(result)); }
    private void SetCurrent(ExecutionResult result) { currentJobId = result.JobId; currentJobStarted ??= DateTimeOffset.UtcNow; statusLabel.Text = result.State.ToString(); progressBar.Value = Math.Clamp(result.ProgressPercent, 0, 100); jobLabel.Text = $"任务：{result.JobId.ToString()[..8]}"; cancelButton.Enabled = result.State is JobState.Queued or JobState.Running; if (result.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled) currentJobStarted = null; RefreshJobList(); }
    private void RefreshJobList() { jobList.BeginUpdate(); jobList.Items.Clear(); foreach (var job in execution.GetJobs(session.UserId)) { var item = new ListViewItem(job.State.ToString()); item.SubItems.Add($"{job.ProgressPercent}%"); item.SubItems.Add(job.JobId.ToString()[..8]); item.Tag = job.JobId; if (job.ErrorMessage is { Length: > 0 }) item.ToolTipText = job.ErrorMessage; jobList.Items.Add(item); } queueLabel.Text = executionStoreWarning is null ? $"队列任务：{jobList.Items.Count}" : $"{executionStoreWarning} · 队列任务：{jobList.Items.Count}"; timeLabel.Text = currentJobStarted is null ? "已用时间：-" : $"已用时间：{(DateTimeOffset.UtcNow - currentJobStarted.Value):mm\\:ss}"; jobList.EndUpdate(); }
}
