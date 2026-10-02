using System.Diagnostics;
using System.Net.Http.Headers;
using HttpStatusCode = System.Net.HttpStatusCode;
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
    private readonly Panel mediaDrawer = new();
    private Panel? agentPaneShell;
    private Panel? mediaPaneShell;
    private CanvasReferenceActivation? activeReferenceActivation;
    private AgentPane? agentPaneControl;
    private FlowLayoutPanel? canvasTabStrip;
    private readonly List<CanvasTabState> canvasTabs = new();
    private CanvasTabState? activeCanvasTab;
    private bool switchingCanvasTab;

    private sealed class CanvasTabState
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// 这个标签当前承载的是「第几份文档」（返工 R16-2）。同一个标签被从画布库打开或导入替换成
        /// 另一份画布时递增——标签 Id 只说明「哪一格标签」，不能说明「现在装的是哪份文档」。
        /// </summary>
        public int DocumentGeneration { get; set; }

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

    /// <summary>
    /// 标签快照用的画布状态：**必须是独立副本**（返工 R16-1）。
    ///
    /// 为什么不能直接引用 <c>canvas.State</c>：那样「标签快照」与「当前画布」就是同一个对象，
    /// 切到别的标签时 <c>LoadState</c> 会**原地**改这份对象，于是刚被切走的标签的快照被写成了
    /// 新标签的内容；切回来时又把自身当来源加载（先清空、再从自己这个空集合里取）→ 节点清零。
    /// </summary>
    private RecentCanvasState BuildCanvasSnapshot() =>
        CloneCanvasSnapshot(BuildCanvasState());

    /// <summary>把一份画布状态连同它的节点等集合一起复制成独立副本（标签快照与标签间传递都用它）。</summary>
    private static RecentCanvasState CloneCanvasSnapshot(RecentCanvasState state) =>
        state with { Canvas = CloneCanvasState(state.Canvas ?? new WorkflowCanvasState()) };

    private static WorkflowCanvasState CloneCanvasState(WorkflowCanvasState state) =>
        JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(state)) ?? new WorkflowCanvasState();

    /// <summary>待提交的 Agent 改动：画布数据在用户保存前不受影响。</summary>
    private readonly PendingChanges pendingChanges = new();

    /// <summary>批次提交账本：保证同一批 Agent 动作只提交一次（审批与自动模式共用）。</summary>
    private readonly AgentBatchCommitter agentBatchCommitter = new();

    /// <summary>最近一次 Agent 提交的快照，用于「撤销上次提交」。</summary>
    private AgentCommitRecord? lastAgentCommit;
    private readonly Dictionary<string, Button> railButtons = new();
    private readonly Dictionary<string, Control> panes = new();
    private WorkTreeCanvasControl? workTreeView;

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

    /// <summary>
    /// 当前画布的来源格式版本高于当前支持版本时记录该版本号，此时只读查看、拒绝覆盖保存（返工 R11）；0 表示可写。
    /// </summary>
    private int currentCanvasUnsupportedFormat;

    /// <summary>章节同步的预览/应用/撤销会话（大目标 B）；切换画布时重置，避免跨画布撤销。</summary>
    private CanvasSyncSession? chapterSyncSession;
    private string? executionStoreWarning;
    private bool restartForProjectSelection;
    public bool RestartForProjectSelection => restartForProjectSelection;
    private static readonly HttpClient canvasPushClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private const string CanvasWebBaseUrl = "http://localhost:5000";
    // 与 Web 的 DreamForge:WebToken 配置对应；仅从进程环境显式提供，不持久化或输出令牌。
    private static readonly string? canvasWebToken = Environment.GetEnvironmentVariable("DreamForge__WebToken");
    private volatile bool canvasWebAuthRejected;
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
        // 返工 V2：关窗也走统一守卫——待恢复的批次必须先把恢复做完（原先只看待处理清单会放行）。
        FormClosing += (_, e) =>
        {
            if (!ConfirmPendingBeforeLeaving()) e.Cancel = true;
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
    /// + 工作树资源摘要 + 工作文件夹文件清单。
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
        RefreshWorkTreeView();
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
        host.Controls.Add(BuildMediaDrawer());
        host.Controls.Add(BuildAgentDrawer());
        host.Controls.Add(BuildDrawer());
        return host;
    }

    private Control BuildMediaDrawer()
    {
        mediaDrawer.Dock = DockStyle.Right;
        mediaDrawer.Width = 420;
        mediaDrawer.BackColor = Theme.PanelBg;
        mediaDrawer.Padding = Padding.Empty;
        mediaDrawer.Margin = Padding.Empty;
        mediaDrawer.Visible = false;
        return mediaDrawer;
    }

    private bool SaveReferenceLayer(WorkflowEntity entity, WorkflowEntity before)
    {
        if (!EnsureNoProjectCompensation("保存引用画布") || !EnsureCurrentCanvasWritable()) return false;
        if (!ProjectEntityScope.TryPublish(entity, out var error))
        {
            MessageBox.Show(error, "资源保存失败");
            return false;
        }
        UnifiedWorkTree.RefreshResources(canvas.State);
        if (!SaveCanvasToLibrary(showResult: false))
        {
            if (!ProjectEntityScope.TryPublish(before, out var rollbackError))
                pendingProjectCompensation = new ProjectCompensation(entity.Id, before, currentCanvasPath ?? canvasTitle, rollbackError);
            ProjectEntityScope.RestoreInto(entity, before);
            UnifiedWorkTree.RefreshResources(canvas.State);
            RefreshWorkTreeView();
            return false;
        }
        UnifiedWorkTree.RefreshResources(canvas.State);
        canvas.NotifyContentChanged();
        canvas.InvalidateThumbnails();
        RefreshWorkTreeView();
        return true;
    }

    private void OpenReferenceMediaDrawer(CanvasReferenceActivation activation)
    {
        HideMediaDrawer();
        activeReferenceActivation = activation;
        var content = BuildReferenceMediaEditor(activation);
        mediaPaneShell = CreatePaneShell("引用媒体", content, HideMediaDrawer);
        Theme.Apply(mediaPaneShell);
        mediaDrawer.SuspendLayout();
        mediaDrawer.Controls.Clear();
        mediaDrawer.Controls.Add(mediaPaneShell);
        mediaDrawer.Controls.Add(Theme.Line(DockStyle.Left));
        mediaDrawer.ResumeLayout();
        mediaDrawer.Visible = true;
        mediaDrawer.BringToFront();
        agentDrawer.Visible = false;
    }

    private void HideMediaDrawer()
    {
        if (mediaPaneShell is not null) DisposeMediaPreviews(mediaPaneShell);
        mediaDrawer.Visible = false;
        if (mediaPaneShell?.Parent == mediaDrawer) mediaDrawer.Controls.Remove(mediaPaneShell);
        activeReferenceActivation = null;
        mediaPaneShell?.Dispose();
        mediaPaneShell = null;
    }

    private static void DisposeMediaPreviews(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is WebBrowser browser)
            {
                try
                {
                    browser.Stop();
                    browser.DocumentText = string.Empty;
                }
                catch { }
            }
            else if (child is PictureBox picture && picture.Image is not null)
            {
                var image = picture.Image;
                picture.Image = null;
                image.Dispose();
            }
            DisposeMediaPreviews(child);
        }
    }

    private static WorkflowEntityVariant CloneReferenceDraft(ReferenceContent content)
    {
        var draft = new WorkflowEntityVariant
        {
            Name = content.Variant.Name,
            Description = content.Description,
            Layout = content.Layout is null ? null : JsonSerializer.Deserialize<SceneLayout>(JsonSerializer.Serialize(content.Layout)),
            Attachments = JsonSerializer.Deserialize<List<WorkflowAttachment>>(JsonSerializer.Serialize(content.Attachments)) ?? new()
        };
        draft.EnsureInitialVersion();
        return draft;
    }

    private Control BuildReferenceMediaEditor(CanvasReferenceActivation activation)
    {
        var content = activation.Content;
        var entity = content?.Entity ?? canvas.State.Entities.FirstOrDefault(item => item.Id == activation.Reference.EntityId);
        var sourceVariant = content?.Variant ?? entity?.Variants.FirstOrDefault(item => item.Id == activation.Reference.VariantId);
        var draft = content is null ? null : CloneReferenceDraft(content);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(14), AutoScroll = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var summary = new Label
        {
            Text = content is null ? "引用目标不可用" : $"{WorkflowEntity.KindName(content.Entity.Kind)} · {content.Entity.Name}\n变体：{content.Variant.Name} · {content.VersionLabel}\n来源：{activation.Node.Title}",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = content?.VersionMissing == true ? Theme.Danger : Theme.Text
        };
        panel.Controls.Add(summary, 0, 0);
        var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.EditorBg, BorderStyle = BorderStyle.FixedSingle };
        var previewAttachment = draft?.Attachments.FirstOrDefault(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video)
            ?? content?.Attachments.FirstOrDefault(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video);
        ReplaceMediaPreview(previewHost, previewAttachment);
        panel.Controls.Add(previewHost, 0, 1);
        var mediaSummary = new Label
        {
            Text = draft is null ? "实体或变体不存在。" : string.Join(Environment.NewLine, draft.Attachments.Select(item => $"{WorkflowAttachment.DisplayName(item.Kind)} · {item.Name}")),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted
        };
        panel.Controls.Add(mediaSummary, 0, 2);
        var description = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Text = content?.Description ?? string.Empty };
        panel.Controls.Add(description, 0, 3);
        var note = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "版本说明" };
        panel.Controls.Add(note, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true };
        var writable = entity is not null && sourceVariant is not null && draft is not null && !activation.Node.IsLocked && !IsCurrentCanvasReadOnly() && content?.VersionMissing != true;
        var openMedia = new Button { Text = "打开媒体", AutoSize = true, Enabled = content?.Attachments.Count > 0 };
        openMedia.Click += (_, _) =>
        {
            var attachment = draft?.Attachments.FirstOrDefault() ?? content?.Attachments.FirstOrDefault();
            var path = attachment is null ? null : AssetStore.Resolve(attachment.Reference);
            if (path is null) { MessageBox.Show("该媒体文件不存在。", "打开媒体", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        var replace = new Button { Text = "替换媒体", AutoSize = true, Enabled = writable };
        replace.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择替换媒体", Filter = "媒体文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.mov;*.avi;*.mkv;*.webm|所有文件|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var source = dialog.FileName;
            var directory = AssetStore.EnsureDirectory();
            var target = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(source)}");
            File.Copy(source, target, overwrite: false);
            draft!.Attachments = new List<WorkflowAttachment> { new() { Kind = WorkflowAttachment.KindOf(target), Reference = AssetStore.ToReference(target), Name = Path.GetFileName(target), Source = "引用媒体替换" } };
            ReplaceMediaPreview(previewHost, draft.Attachments[0]);
            mediaSummary.Text = string.Join(Environment.NewLine, draft.Attachments.Select(item => $"{WorkflowAttachment.DisplayName(item.Kind)} · {item.Name}"));
            openMedia.Enabled = true;
        };
        List<(WorkflowNode Node, NodeReference Reference)> FindMatchingReferences() => entity is null || sourceVariant is null ? new() :
            canvas.State.Nodes.SelectMany(node => node.References.Where(reference => reference.EntityId == entity.Id && reference.VariantId == sourceVariant.Id).Select(reference => (Node: node, Reference: reference))).ToList();
        var allReferences = new RadioButton { Text = "所有引用切换到新版本", AutoSize = true, Enabled = writable };
        var save = new Button { Text = "提交新版本并回写", AutoSize = true, Enabled = writable };
        save.Click += (_, _) =>
        {
            if (IsCurrentCanvasReadOnly() || activation.Node.IsLocked || entity is null || sourceVariant is null || draft is null) return;
            var allMatches = FindMatchingReferences();
            if (allReferences.Checked && allMatches.Any(item => item.Node.IsLocked))
            {
                MessageBox.Show("匹配引用中包含锁定节点。为保持锁定保护，不能执行全部切换；请改选仅当前镜头。", "无法提交", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var prompt = allReferences.Checked
                ? $"将提交新版本并切换 {allMatches.Count} 个引用。确认全部切换？"
                : "提交后仅当前镜头的这条引用切换到新版本。是否继续？";
            if (MessageBox.Show(prompt, "确认提交", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            draft.Description = description.Text;
            if (allReferences.Checked)
            {
                sourceVariant.Description = draft.Description;
                sourceVariant.Attachments = JsonSerializer.Deserialize<List<WorkflowAttachment>>(JsonSerializer.Serialize(draft.Attachments)) ?? new();
                var created = sourceVariant.Commit(note.Text);
                foreach (var target in allMatches) target.Reference.VariantVersionId = created.Id;
            }
            else
            {
                var isolated = entity.CreateIsolatedVariant(
                    content!,
                    $"{sourceVariant.Name} · 当前镜头变体",
                    draft.Description,
                    draft.Attachments);
                var created = isolated.Versions.OrderByDescending(item => item.Number).First();
                activation.Reference.VariantId = isolated.Id;
                activation.Reference.VariantVersionId = created.Id;
            }
            canvas.NotifyContentChanged();
            canvas.InvalidateThumbnails();
            RefreshWorkTreeView();
            RefreshNodeInspector();
            HideMediaDrawer();
        };
        buttons.Controls.Add(openMedia);
        buttons.Controls.Add(replace);
        buttons.Controls.Add(save);
        panel.Controls.Add(buttons, 0, 5);
        panel.Controls.Add(new Label
        {
            Text = writable ? "保存为新版本后，仅当前分镜引用切换到新版本。" : "只读画布、锁定分镜或失效引用不可写回。",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = writable ? Theme.TextMuted : Theme.Warning
        }, 0, 6);
        var versions = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(EntityVariantVersion.Label), Enabled = writable };
        if (sourceVariant is not null)
            foreach (var version in sourceVariant.Versions.OrderByDescending(item => item.Number)) versions.Items.Add(version);
        var versionDetail = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.TextMuted };
        versions.SelectedIndexChanged += (_, _) =>
        {
            if (versions.SelectedItem is not EntityVariantVersion selected || draft is null) return;
            var diff = VersionDiff.CompareToCurrent(selected, draft);
            versionDetail.Text = $"{selected.Label} · {selected.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {selected.Note}{Environment.NewLine}" +
                (diff.Count == 0 ? "当前草稿与该版本一致。" : string.Join(Environment.NewLine, diff.Select(item => item.Describe())));
        };
        var rollback = new Button { Text = "回滚到选中版本（仅草稿）", AutoSize = true, Enabled = writable };
        rollback.Click += (_, _) =>
        {
            if (versions.SelectedItem is not EntityVariantVersion selected || draft is null) return;
            draft.RollbackTo(selected);
            description.Text = draft.Description;
            var attachment = draft.Attachments.FirstOrDefault(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video);
            ReplaceMediaPreview(previewHost, attachment);
            mediaSummary.Text = string.Join(Environment.NewLine, draft.Attachments.Select(item => $"{WorkflowAttachment.DisplayName(item.Kind)} · {item.Name}"));
            versionDetail.Text = $"草稿已回滚到 {selected.Label}。源变体尚未变更；提交后才会落盘。";
        };
        var versionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        versions.Width = 150;
        versionRow.Controls.Add(versions);
        versionRow.Controls.Add(versionDetail);
        panel.Controls.Add(versionRow, 0, 7);
        var rollbackRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        rollbackRow.Controls.Add(rollback);
        var generate = new Button { Text = "生成变体（图片）", AutoSize = true, Enabled = writable };
        generate.Click += async (_, _) =>
        {
            if (draft is null || sourceVariant is null || activation.Node.IsLocked || IsCurrentCanvasReadOnly()) return;
            if (canvas.SelectedNode != activation.Node)
            {
                MessageBox.Show("请先选中触发此抽屉的镜头，再生成图片。", "生成变体");
                return;
            }
            var provider = ImageProviderFactory.Create(execution);
            if (!provider.IsConfigured) { MessageBox.Show("尚未配置图像模型。请先在“设置”中配置后再生成。", "生成变体", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var prompt = ComposeNodePrompt(activation.Node);
            if (string.IsNullOrWhiteSpace(prompt)) { MessageBox.Show("当前镜头没有可用提示词。", "生成变体", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var result = await provider.GenerateAsync(BuildImageRequest(activation.Node));
            if (result.Status != ImageGenerationStatus.Succeeded || !File.Exists(result.FilePath))
            {
                MessageBox.Show(string.IsNullOrWhiteSpace(result.Error) ? "图像生成未返回有效文件，草稿未修改。" : result.Error, "生成变体失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var generated = new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = AssetStore.ToReference(result.FilePath), Name = Path.GetFileName(result.FilePath), Source = "媒体抽屉生成变体" };
            draft.Attachments = new List<WorkflowAttachment> { generated };
            ReplaceMediaPreview(previewHost, generated);
            mediaSummary.Text = $"图片 · {generated.Name}（草稿，待提交）";
        };
        rollbackRow.Controls.Add(generate);
        panel.Controls.Add(rollbackRow, 0, 8);
        var scope = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var currentOnly = new RadioButton { Text = "仅当前镜头使用", Checked = true, AutoSize = true, Enabled = writable };
        scope.Controls.Add(currentOnly);
        scope.Controls.Add(allReferences);
        panel.Controls.Add(scope, 0, 9);
        var impact = new Label { Text = "默认仅当前镜头。", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.TextMuted };
        allReferences.CheckedChanged += (_, _) => impact.Text = allReferences.Checked
            ? $"将切换 {FindMatchingReferences().Count} 个画布引用（按实体与变体 ID 匹配）。" : "默认仅当前镜头。";
        panel.Controls.Add(impact, 0, 10);
        panel.RowCount = 11;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return panel;
    }

    private static void ReplaceMediaPreview(Control host, WorkflowAttachment? attachment)
    {
        DisposeMediaPreviews(host);
        host.Controls.Clear();

        if (attachment is null)
        {
            host.Controls.Add(CreateMediaMessage("没有可预览的图片或视频附件。"));
            return;
        }

        if (attachment.Kind == AttachmentKind.Video)
        {
            var path = AssetStore.Resolve(attachment.Reference);
            if (path is null)
            {
                host.Controls.Add(CreateMediaMessage("视频文件不存在，无法播放。"));
                return;
            }

            try
            {
                var browser = new WebBrowser
                {
                    Dock = DockStyle.Fill,
                    AllowNavigation = false,
                    ScriptErrorsSuppressed = true,
                    WebBrowserShortcutsEnabled = false,
                    IsWebBrowserContextMenuEnabled = false
                };
                browser.DocumentText = BuildVideoPreviewHtml(path);
                browser.DocumentCompleted += (_, _) =>
                {
                    if (browser.IsDisposed || browser.Document is null) return;
                    try
                    {
                        browser.Document.InvokeScript("eval", new object[] { "document.querySelector('video').addEventListener('error', function(){document.body.innerHTML='<div style=\\\"color:#d7d9df;text-align:center;padding:24px;font:14px sans-serif\\\">视频格式不受支持或加载失败，请使用“打开媒体”查看文件。</div>';});" });
                    }
                    catch { }
                };
                host.Controls.Add(browser);
            }
            catch
            {
                host.Controls.Add(CreateMediaMessage("视频播放器加载失败，请使用“打开媒体”查看文件。"));
            }
            return;
        }

        var image = LoadMediaPreview(attachment.Reference);
        if (image is not null)
        {
            host.Controls.Add(new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Theme.EditorBg,
                Image = image
            });
        }
        else
        {
            host.Controls.Add(CreateMediaMessage("图片文件不存在或无法预览。"));
        }
    }

    private static Label CreateMediaMessage(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.TextMuted,
        BackColor = Theme.EditorBg,
        Padding = new Padding(12)
    };

    private static string BuildVideoPreviewHtml(string path)
    {
        var source = HtmlEncode(new Uri(path, UriKind.Absolute).AbsoluteUri);
        return $"<!doctype html><html><head><meta charset='utf-8'><style>html,body{{margin:0;width:100%;height:100%;background:#1b1d21;overflow:hidden}}video{{width:100%;height:100%;object-fit:contain;background:#1b1d21}}</style></head><body><video controls preload='metadata'><source src='{source}'></video></body></html>";
    }

    private static string HtmlEncode(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static Image? LoadMediaPreview(string reference)
    {
        try
        {
            var path = AssetStore.Resolve(reference);
            if (path is null) return null;
            using var stream = File.OpenRead(path);
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
        catch { return null; }
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
        if (key == "worktree") RefreshWorkTreeView();
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
        PrecheckActions: actions => AgentActionExecutor.PrecheckBatch(actions, canvas.State, AiProviderSettings.Load().AgentWorkspace, currentCanvasPath),
        ApplyActions: actions => ApplyAgentBatch(actions),
        PreviewActions: PreviewAgentBatch,
        PrepareActions: PrepareAgentActions,
        PendingActions: () => pendingChanges.Actions,
        RemovePending: RemovePendingAction,
        SaveApplied: SavePendingForPane,
        UndoApplied: UndoPendingForPane,
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
        // 返工 R17-2：还有批次等待恢复时**不开始新批**。换批号会让旧批的恢复记录失去入口
        // （新批一旦落画就会覆盖 lastAgentCommit），而账本里旧批仍停在待恢复——
        // 结果是恢复记录不可达、离开入口又全被拦住。这里连同后面的统一提交入口一起挡住。
        if (agentBatchCommitter.Ledger.HasPendingRecovery(out var recovering))
        {
            WriteAgentCommitDiagnostic($"preview-blocked pending-recovery batch={recovering:N}");
            return;
        }

        pendingChanges.BeginBatch(actions);
        canvas.SetPreview(CanvasPreviewBuilder.Build(actions, canvas.State));
        RefreshPendingLabel();
        agentPaneControl?.RefreshState();
        canvas.Invalidate();
    }

    /// <summary>
    /// 将 Agent 批次应用到真实画布并保存（审批与自动模式都走这一条入口）。
    ///
    /// 阶段与结论由 <see cref="AgentBatchCommitter"/> 裁决（返工 R2/R3）：只有「动作全部应用成功 **且** 保存成功」
    /// 才算提交完成；部分应用、保存失败都如实返回、弹窗说明，并且：
    /// · 画布一旦被改动就登记撤销记录（含文件快照），「撤销本批 / 撤销上次提交」都能真的回退；
    /// · 失败时**不动**待处理清单，用户可以直接「重试保存」（只重存，不重复应用动作）。
    /// </summary>
    private AgentCommitResult ApplyAgentBatch(IReadOnlyList<AgentAction> actions, Guid? batchId = null, bool saveOnly = false)
    {
        var effectiveBatch = batchId ?? pendingChanges.BatchId;

        // 返工 R17-2：统一提交入口再挡一次（手动、自动、直接提交都从这里进）——
        // 只要还有批次在等待恢复，就先让它恢复完；新批会覆盖 lastAgentCommit，
        // 把旧批的恢复记录变成不可达的死记录。
        if (agentBatchCommitter.Ledger.HasPendingRecovery(out var waiting))
            return new AgentCommitResult(AgentCommitStage.Rejected, effectiveBatch, 0, Array.Empty<string>(),
                "还有一批改动的撤销没做完（待恢复批次 " + ShortKey(waiting.ToString())
                + "）：请先点「撤销上次提交」把画布与文件恢复好（可重试），再提交新的改动。");
        var snapshots = new List<FileSnapshot>();
        var snapshotBefore = JsonSerializer.Serialize(BuildCanvasState());
        var revisionAtCommit = canvasRevision;
        var removed = new List<string>();
        var appliedThisCall = false;

        var result = agentBatchCommitter.Commit(
            effectiveBatch,
            actions,
            apply: () =>
            {
                appliedThisCall = true;
                var selected = canvas.SelectedNode;
                foreach (var action in actions)
                {
                    if (string.Equals(action.Kind, "create_node", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(action.ParentTarget)
                        && selected is not null)
                        action.ParentTarget = selected.Title;
                }

                var applied = AgentActionExecutor.Apply(
                    actions, canvas.State, AiProviderSettings.Load().AgentWorkspace,
                    FileWriteMode.Apply, snapshots, currentCanvasPath);
                removed = applied.RemovedReferences.ToList();
                return applied;
            },
            save: () => SaveCanvasToLibrary(showResult: false),
            saveOnly: saveOnly,
            canvasKey: CurrentCanvasKey());

        // 只有「这次真的应用了动作」才登记新的撤销记录（返工 S2）：只重存的那一次不再重拍快照、
        // 也不覆盖原记录（否则 AppliedCount 会变成 0，撤销会退到错误状态或干脆不回退）。
        if (appliedThisCall && (result.ActionsApplied || result.Partial))
        {
            // 画布已被改动：登记撤销记录，之后「撤销本批」与「撤销上次提交」都按它回退。
            lastAgentCommit = new AgentCommitRecord
            {
                BatchId = result.BatchId,
                CanvasKey = CurrentCanvasKey(),
                AppliedCount = result.AppliedCount,
                Summary = string.Join("；", actions.Select(action => action.Describe())),
                SnapshotBefore = snapshotBefore,
                RevisionAtCommit = revisionAtCommit,
                FileSnapshots = snapshots
            };

            canvas.NotifyContentChanged();
            canvas.InvalidateThumbnails();
            RefreshNodeInspector();
            RefreshWorkTreeView();
            RefreshCanvasLibrary();
            canvas.Invalidate();

            var latest = actions.LastOrDefault(action =>
                string.Equals(action.Kind, "create_node", StringComparison.OrdinalIgnoreCase));
            if (latest is not null)
            {
                var node = canvas.State.Nodes.LastOrDefault(item =>
                    string.Equals(item.Title, latest.Title, StringComparison.OrdinalIgnoreCase));
                if (node is not null) canvas.FocusNode(node.Id);
            }
        }

        if (result.Succeeded)
        {
            // 提交完成：待处理清单收口（撤销走撤销记录），同一批次号再提交会被账本拒绝。
            pendingChanges.Clear();
            canvas.SetPreview(null);
        }

        if (removed.Count > 0) RecordAssetMoves(OfferRecycleOrphanedAssets(removed));
        WriteAgentCommitDiagnostic(
            $"commit stage={result.Stage} batch={result.BatchId:N} applied={result.AppliedCount} "
            + $"errors={result.ActionErrors.Count} pendingCount={pendingChanges.Count}");
        if (!result.Succeeded)
        {
            var detail = result.ActionErrors.Count == 0
                ? string.Empty
                : "\n\n未能完成的条目：\n" + string.Join("\n", result.ActionErrors.Select(item => "· " + item));
            MessageBox.Show(result.Message + detail, "Agent 提交未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshPendingState();
        return result;
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

    /// <summary>
    /// 「保存」入口：应用本批动作并落盘。**结果照实返回**（返工 R3）——
    /// 保存失败时不报成功、不清掉待处理清单、不把已应用的改动说成已保存；
    /// 调用方（面板按钮、关窗、切标签）必须按返回结果决定后续动作。
    /// </summary>
    private AgentCommitResult SaveAppliedAgentChanges(bool saveOnly = false)
    {
        if (pendingChanges.IsEmpty)
            return new AgentCommitResult(AgentCommitStage.Rejected, pendingChanges.BatchId, 0,
                Array.Empty<string>(), "没有待提交的 Agent 改动。");
        return ApplyAgentBatch(pendingChanges.Actions.ToArray(), pendingChanges.BatchId, saveOnly);
    }

    /// <summary>
    /// 撤销本批（返工 R3/S3）：如果这批动作**已经落到画布上**，必须真的回退画布与文件副作用，
    /// 而不是只清掉列表——否则界面说「已撤销」、画布却还留着改动，之后保存会把「已撤销」的内容写回文件。
    /// 还没应用（只是虚影）时才只清虚影。
    /// 返回未能恢复的说明；**恢复失败时不清待处理内容、也不声称撤销完成**，调用方须据此中止后续切换。
    /// </summary>
    private IReadOnlyList<string> DiscardPendingChanges()
    {
        var applied = lastAgentCommit is not null
            && pendingChanges.Count > 0
            && lastAgentCommit.BatchId == pendingChanges.BatchId
            && lastAgentCommit.AppliedCount > 0;

        if (!applied)
        {
            pendingChanges.Clear();
            canvas.SetPreview(null);
            RefreshPendingState();
            RefreshWorkTreeView();
            canvas.Invalidate();
            return Array.Empty<string>();
        }

        var failures = RollbackLastCommit();
        if (failures.Count == 0)
        {
            pendingChanges.Clear();
            canvas.SetPreview(null);
            RefreshPendingState();
            RefreshWorkTreeView();
            canvas.Invalidate();
            MessageBox.Show("已撤销本批 Agent 改动：画布恢复到本批动作执行前的状态。", "撤销本批",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Array.Empty<string>();
        }

        // 恢复失败：保留待处理内容与恢复记录，如实报出未恢复的项目（不再谎报「已撤销」）。
        RefreshPendingState();
        RefreshWorkTreeView();
        canvas.Invalidate();
        MessageBox.Show("撤销没有完全成功，画布与文件可能停在中途状态：\n\n"
            + string.Join("\n", failures.Select(item => "· " + item))
            + "\n\n已保留待处理内容与恢复记录，可稍后重试撤销。",
            "撤销未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return failures;
    }

    /// <summary>把资产移出登记进本次撤销记录（返工 U1）。</summary>
    private void RecordAssetMoves(IReadOnlyList<AssetMove> moved)
    {
        if (moved.Count == 0 || lastAgentCommit is null) return;
        lastAgentCommit.AssetMoves.AddRange(moved);
        WriteAgentCommitDiagnostic($"asset-moves batch={lastAgentCommit.BatchId:N} count={moved.Count}");
    }

    /// <summary>
    /// 用撤销记录回退画布与文件副作用，返回未恢复成功的文件说明。
    /// 撤销本批与「撤销上次提交」共用它，保证两条路径的语义完全一致。
    /// 返工 S3：**只有全部恢复成功才清掉记录**；失败时保留记录（用户可以重试撤销），
    /// 并把被移入系统回收站的资产列进说明——那些文件不会自动回来。
    /// </summary>
    private List<string> RollbackLastCommit()
    {
        var failures = new List<string>();
        if (lastAgentCommit is null) return failures;
        var record = lastAgentCommit;

        // 返工 U3：撤销只能作用在提交它的那个画布上——在 A 提交、切到 B 再撤销，
        // 会把 A 的快照套到 B 上（画布被覆盖成另一个文档的内容）。
        // 返工 R16-3：这只是「现在不能撤销」，**不是**「恢复做了一半」，因此**不能**把批次标成
        // 待恢复——那会让「离开入口守卫」把用户锁死（连切回原画布都被拦住）。
        // 记录原样保留，用户切回原画布后仍可正常撤销。
        var currentKey = CurrentCanvasKey();
        if (record.CanvasKey.Length > 0 && !string.Equals(record.CanvasKey, currentKey, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"这批改动是在另一份画布上提交的（记录 {ShortKey(record.CanvasKey)}，当前 {ShortKey(currentKey)}）：已拒绝跨画布撤销。请切回原画布再撤销。");
            return failures;
        }

        // 返工 R16-3：画布回退只做一次。重试恢复时若再套一次提交前快照，会把用户在两次尝试之间
        // 做的编辑一起回退掉；已经回退过就只继续恢复剩余的副作用（文件与资产）。
        if (!record.CanvasRestored)
        {
            try
            {
                if (JsonSerializer.Deserialize<RecentCanvasState>(record.SnapshotBefore) is { } state)
                {
                    ApplyCanvasState(state, advanceRevision: false);
                    record.CanvasRestored = true;
                }
                else
                {
                    failures.Add("提交快照无法解析：画布未能回退。");
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                failures.Add(error.Message);
            }
        }

        failures.AddRange(FileSnapshot.RestoreAll(record.FileSnapshots));
        // 返工 U1：资产移出也纳入恢复条件——它和文件一样是真实副作用，不还原就不算撤销成功。
        // 返工 R16-4：已经成功移回的资产会被标记为已完成，重试时不再重复处理（否则会把
        // 「已经恢复好」报成「回收目录里已找不到该文件」，永远收敛不了）。
        failures.AddRange(AssetRecycle.RestoreAll(record.AssetMoves));

        if (failures.Count == 0)
        {
            lastAgentCommit = null;
            agentBatchCommitter.Ledger.MarkRolledBack(record.BatchId);
        }
        else
        {
            // 返工 U2：回滚只完成了一部分 → 记账为「待恢复」，禁止按原来的 SaveFailed 继续只重存，
            // 否则会把「已经回退过的画布」当成这批动作的结果存下来。
            agentBatchCommitter.Ledger.MarkNeedsRecovery(record.BatchId);
        }
        return failures;
    }

    /// <summary>
    /// 当前画布的身份（返工 V3/R16-2）：**标签 Id + 该标签当前的文档世代**，不用文件路径——
    /// 未命名画布的路径是 null（两个未命名画布会看起来像同一个），另存为之后路径又会变
    /// （同一个画布会看起来像两个）。而只取标签 Id 也不够：同一个标签可以从画布库打开或导入
    /// 换成**另一份文档**（R16-2），那时标签没变，装的内容却换了——世代号让新旧文档不共用身份。
    /// </summary>
    private string CurrentCanvasKey() =>
        activeCanvasTab is null ? string.Empty : $"{activeCanvasTab.Id:N}:{activeCanvasTab.DocumentGeneration}";

    /// <summary>
    /// 同一个标签改成承载**另一份画布**（从画布库打开、导入替换）时调用（返工 R16-2）：
    /// 换掉画布身份并作废旧的提交/恢复上下文。
    ///
    /// 为什么必须作废：旧记录里的「提交前快照」属于上一份文档，而修订号很可能与新文档对得上
    /// （不同画布的修订号相同是常态），于是「撤销上次提交」会把上一份文档的状态套到新文档上，
    /// 随后的保存还会顺着新文档的路径把它写下去。
    /// </summary>
    private void BeginNewDocumentOnCurrentTab()
    {
        if (activeCanvasTab is not null) activeCanvasTab.DocumentGeneration++;
        lastAgentCommit = null;
        pendingChanges.Clear();
        canvas.SetPreview(null);
        WriteAgentCommitDiagnostic($"new-document key={ShortKey(CurrentCanvasKey())}");
    }

    /// <summary>画布身份在提示文案里的短写（身份本身是标签 Id，全量太长且对用户没意义）。</summary>
    private static string ShortKey(string key) =>
        key.Length == 0 ? "（无）" : (key.Length <= 8 ? key : key[..8]);

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

    /// <summary>
    /// 面板「保存」：返回 null 表示成功，否则返回要显示在面板上的失败说明（返工 R3）。
    /// 保存失败时给一次「就地重试保存」的机会——动作已经应用过，重试只重存，不会重复副作用。
    /// </summary>
    private string? SavePendingForPane()
    {
        // 返工 R17-2：面板「保存」与自动模式都走这里。待恢复时要说清「先做什么」，
        // 而不是让用户看到「没有待提交的 Agent 改动」这种与现场不符的提示。
        if (agentBatchCommitter.Ledger.HasPendingRecovery(out var waiting))
            return "还有一批改动的撤销没做完（待恢复批次 " + ShortKey(waiting.ToString())
                + "）：请先点「撤销上次提交」把画布与文件恢复好，再提交新的改动。";

        var result = SaveAppliedAgentChanges();
        if (result.Succeeded) return null;
        if (!result.CanRetrySave) return result.Message;

        var retry = MessageBox.Show(
            result.Message + "\n\n是否现在重试保存？（只重新保存一次，不会重复应用动作）",
            "保存未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (retry != DialogResult.Yes) return result.Message;

        var second = SaveAppliedAgentChanges(saveOnly: true);
        return second.Succeeded ? null : second.Message;
    }

    /// <summary>面板「撤销本批」：撤销会真的回退画布与文件；未完全恢复时返回失败说明（返工 S3）。</summary>
    private string? UndoPendingForPane()
    {
        var failures = DiscardPendingChanges();
        return failures.Count == 0 ? null : string.Join("；", failures);
    }

    /// <summary>
    /// 窗口或标签切换前确认当前 Agent 改动：**返回真实结果**，调用方在未保存成功时必须中止切换
    /// （返工 R3：不能一边报失败一边把用户切走，把改动留在内存里丢掉）。
    /// </summary>
    private AgentCommitResult? CommitPendingChanges()
    {
        if (pendingChanges.IsEmpty) return null;
        return SaveAppliedAgentChanges();
    }

    /// <summary>
    /// 撤销可用性：提交之后画布若又被改动过，回滚会连带回退用户自己的编辑，因此停用。
    /// 返工 R16-3 的两个例外：
    /// · 批次**还在等待恢复**时放行——否则「先重试撤销把画布与文件恢复好」这句提示没有入口可走，
    ///   而所有离开入口又被守卫拦着，用户被卡死（此时画布已回退到提交前，继续恢复剩余的副作用是对的）；
    /// · 记录属于**另一份画布**时停用并说明原因——撤销不可能在这里完成。
    /// </summary>
    private (bool Available, string Reason) UndoAvailability()
    {
        if (lastAgentCommit is null) return (false, "还没有可撤销的 Agent 提交。");

        var key = CurrentCanvasKey();
        if (lastAgentCommit.CanvasKey.Length > 0 && !string.Equals(lastAgentCommit.CanvasKey, key, StringComparison.OrdinalIgnoreCase))
            return (false, "这批改动属于另一份画布：请切回原画布再撤销。");

        if (agentBatchCommitter.Ledger.StateOf(lastAgentCommit.BatchId) == AgentBatchState.NeedsRecovery)
            return (true, string.Empty);

        if (canvasRevision != lastAgentCommit.RevisionAtCommit + 1)
            return (false, "提交之后画布又有其它改动，撤销会连带回退这些编辑，因此已停用。");
        return (true, string.Empty);
    }

    /// <summary>
    /// 撤销上一次 Agent 提交：用提交前的整体快照恢复画布与画布参数。
    /// 返工 R16-3：画布已经回退过（上一次撤销没做完）时，这一次只继续恢复剩余的文件与资产，
    /// 不再重复回退画布，也不谎称「画布恢复到提交前」。
    /// </summary>
    private void UndoLastAgentCommit()
    {
        var (available, reason) = UndoAvailability();
        if (!available) { MessageBox.Show(reason, "撤销上次提交", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var continuing = lastAgentCommit is { CanvasRestored: true };
        var failures = RollbackLastCommit();
        RefreshPendingState();
        RefreshWorkTreeView();
        canvas.Invalidate();
        var message = continuing
            ? "已把上一次没做完的恢复完成（画布早已回退，这次只把剩余的文件与资产恢复好）。"
            : "已撤销上一次 Agent 提交，画布恢复到提交前的状态。";
        if (failures.Count > 0) message += "\n\n以下项目未能恢复：\n" + string.Join("\n", failures.Select(item => "· " + item));
        MessageBox.Show(message, "撤销上次提交", MessageBoxButtons.OK,
            failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };
        canvasList.View = View.Details; canvasList.FullRowSelect = true; canvasList.MultiSelect = false;
        canvasList.HeaderStyle = ColumnHeaderStyle.None; canvasList.HideSelection = false;
        canvasList.Columns.Add("画布", 200); canvasList.Dock = DockStyle.Fill;
        canvasList.DoubleClick += (_, _) => OpenSelectedLibraryCanvas();
        content.Controls.Add(canvasList);
        canvasLibraryHint.Text = "画布库为空，点击下方“新建”开始，再用“保存”存入画布库。";
        canvasLibraryHint.Dock = DockStyle.Fill; canvasLibraryHint.ForeColor = Theme.TextMuted;
        canvasLibraryHint.BackColor = Color.Transparent; canvasLibraryHint.Visible = false;
        content.Controls.Add(canvasLibraryHint);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 144, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(CreatePaneButton("新建", 68, NewCanvasButton_Click));
        footer.Controls.Add(CreatePaneButton("保存", 68, (_, _) => SaveCanvasToLibrary()));
        footer.Controls.Add(CreatePaneButton("打开", 68, (_, _) => OpenSelectedLibraryCanvas()));
        footer.Controls.Add(CreatePaneButton("重命名", 68, (_, _) => RenameSelectedCanvas()));
        footer.Controls.Add(CreatePaneButton("删除", 68, (_, _) => DeleteSelectedLibraryCanvas()));
        footer.Controls.Add(CreatePaneButton("导入包", 74, (_, _) => ImportCanvasPackage()));
        footer.Controls.Add(CreatePaneButton("导出包", 74, (_, _) => ExportCanvasPackage()));
        footer.Controls.Add(CreatePaneButton("复制画布", 84, (_, _) => DuplicateCurrentCanvas()));
        footer.Controls.Add(CreatePaneButton("检查画布", 84, (_, _) => ShowCanvasDiagnostics()));
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
    /// 兼容旧数据的资源编辑面板。新界面不再暴露独立入口，资源统一从工作树进入。
    /// </summary>

    /// <summary>
    /// “插件”面板：列出插件加载状态、已注册窗口与目录位置。
    /// 插件只负责挂界面入口，内容生产统一由技能完成。
    /// </summary>
    private Control BuildPluginPane()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg, ColumnCount = 1, RowCount = 4, Padding = Padding.Empty, Margin = Padding.Empty };
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

        pluginHint.Dock = DockStyle.Fill; pluginHint.AutoSize = false; pluginHint.ForeColor = Theme.TextMuted;
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



    /// <summary>在列表中选中标签匹配的项，返回是否命中了目标。</summary>
    private static bool SelectInList(ListView list, object tag)
    {
        foreach (ListViewItem item in list.Items)
            if (ReferenceEquals(item.Tag, tag)) { item.Selected = true; item.EnsureVisible(); return true; }
        return false;
    }



    /// <summary>
    /// 托管实体的改动落地（目标 6 / G6-R1）：项目库是权威，改动必须写回库；
    /// 写库失败时把内存内容整体还原并如实报错，绝不出现「误报成功」或「丢旧数据」。
    /// </summary>
    private bool PublishSharedEntity(WorkflowEntity entity, WorkflowEntity before, string action)
    {
        if (ProjectEntityScope.TryPublish(entity, out var error)) return true;
        ProjectEntityScope.RestoreInto(entity, before);
        MessageBox.Show(
            $"「{before.Name}」的{action}没能写入项目库：{error}\n\n本次改动已整体撤回，画布与项目库保持原样。",
            "项目级资源写入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }


    /// <summary>跨画布/草稿扫描引用，供删除保护与「引用与回收站」使用（目标 4 / 4.1、4.3）。</summary>
    private ReferenceScanReport ScanReferences() => CanvasReferenceScanner.Scan(canvas.State, currentCanvasPath);

    /// <summary>
    /// 把本画布（以及其它画布/草稿）里的**本地资源**迁移进项目库（目标 6 / G6-2）：
    /// 先给预览、用户确认后才落盘；过程中会备份项目库与受影响的画布文件，失败整批回滚；
    /// 已共享的实体直接跳过（幂等），同名不同实体绝不自动合并。
    /// </summary>
    /// <param name="quiet">自动化（回归/冒烟）用：不弹预览与确认框，直接按目标执行并返回结果。</param>

    /// <summary>把当前选中的变体作为新节点放到画布上，节点创建时即引用该设定并锁定最新版本。</summary>

    /// <summary>
    /// 对当前选中的变体运行技能。技能只能用大模型产出新资源，产出写入变体当前内容的参考图；
    /// 需要固化时再去「版本」页提交新版本。
    /// </summary>

    /// <summary>
    /// 带进度提示地运行技能，结束后统一提示结果。技能运行的界面由宿主负责，
    /// 插件与界面入口都走这里，保证体验一致。产出需要固化时由用户去「版本」页提交。
    /// </summary>
    private async Task<SkillRunResult> ExecuteSkillWithProgressAsync(SkillDefinition skill, SkillTarget target)
    {
        // 按技能选执行方（返工 S4）：导入的 API 技能走它自己的接口配置，不会被 ComfyUI 抢走。
        var provider = ImageProviderFactory.CreateFor(skill, execution);
        if (skill.NeedsImageProvider && !provider.IsConfigured)
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
            }, cancellationToken: default, videoProvider: VideoProviderFactory.Create(AiProviderSettings.Load()));
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
        var detail = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.FieldBg };
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



    /// <summary>
    /// 外层取消、但对话框里已有"确认并落库"的提交（目标 6 / G6-S2）：只丢掉未持久化的编辑，
    /// 用项目库内容对齐内存，绝不撤销已经提示成功的提交。
    /// </summary>

    /// <summary>项目库补偿失败留下的待恢复记录（复核 G6-T3）：重试成功之前不再向项目库写入。</summary>
    private ProjectCompensation? pendingProjectCompensation;

    /// <summary>
    /// 有待恢复的项目库补偿时，阻止继续写库（复核 G6-T3）：先重试恢复，别让不一致的写入互相叠加。
    /// </summary>
    private bool EnsureNoProjectCompensation(string action, bool quiet = false)
    {
        if (pendingProjectCompensation is not { } pending) return true;
        if (!quiet)
            MessageBox.Show(
                $"还有一次项目库恢复没做完（{pending.Reason}）。\n\n请先点「恢复项目库」把它重试成功，再做{action}。",
                "有待恢复的项目库改动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>
    /// 重试上次失败的项目库补偿（复核 G6-T3）：成功即清除待恢复记录，并用库内容把内存对齐；
    /// 再次失败就保留记录、如实报告原因，等故障解除后再试。
    /// </summary>
    private bool RetryProjectCompensation(bool quiet = false)
    {
        if (pendingProjectCompensation is not { } pending)
        {
            if (!quiet) MessageBox.Show("没有待恢复的项目库改动。", "恢复项目库", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!ProjectEntityScope.TryPublish(pending.Content, out var error))
        {
            pendingProjectCompensation = pending with { Reason = error };
            WriteAgentCommitDiagnostic($"project-compensation retry-failed entity={pending.EntityId:N}");
            if (!quiet)
                MessageBox.Show($"仍未恢复成功：{error}\n（记录保留，故障解除后可再试）", "恢复项目库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var restoredName = pending.Content.Name;
        pendingProjectCompensation = null;
        var canvasEntity = canvas.State.Entities.FirstOrDefault(item => item.Id == pending.EntityId);
        if (canvasEntity is not null && ProjectLibrary.Find(pending.EntityId) is { } authoritative)
        {
            ProjectEntityScope.RestoreInto(canvasEntity, authoritative);
            RefreshWorkTreeView();
            canvas.Invalidate();
        }

        canvas.NotifyContentChanged();
        WriteAgentCommitDiagnostic($"project-compensation recovered entity={pending.EntityId:N}");
        if (!quiet)
            MessageBox.Show($"已把「{restoredName}」恢复到项目库，画布与项目库重新一致。", "恢复项目库", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    /// <summary>编辑工作树资源本身：种类、名称、别名与跨章节不变的核心设定。</summary>
    private bool OpenWorkTreeResourceEditor(WorkflowEntity entity)
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
            // 返工 R16-1：快照必须是独立副本，不能与当前画布共享同一个状态对象。
            activeCanvasTab.Snapshot = BuildCanvasSnapshot();
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
        if (IsCurrentCanvasReadOnly()) return;
        try { CanvasLibrary.Save(BuildCanvasState(), currentCanvasPath); }
        catch (CanvasSaveAbortedException aborted) { ShowSaveAborted(aborted.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("关闭时写回画布失败：" + error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 当前画布是否处于「只读查看」状态：来源格式版本高于当前支持版本时不允许覆盖保存，
    /// 否则会把未知格式的数据降级写回（返工 R11）。
    /// </summary>
    private bool IsCurrentCanvasReadOnly() => currentCanvasUnsupportedFormat > 0;

    private bool EnsureCurrentCanvasWritable()
    {
        if (!IsCurrentCanvasReadOnly()) return true;
        MessageBox.Show(
            $"该画布的格式版本是 {currentCanvasUnsupportedFormat}，高于当前支持的 {CanvasFormat.Current}。\n"
            + "当前版本只做只读查看，不会覆盖保存，以免丢失未知数据；请用更高版本的程序打开。",
            "只读查看", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void ShowSaveAborted(string message) =>
        MessageBox.Show(message, "已中止保存（原文件未被覆盖）", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>
    /// 统一保存入口：返回真实结果（返工 B-UI-02）。只要出现只读拒绝、同名冲突取消、备份失败、
    /// 权限/写入失败或原子替换失败，一律返回 <c>false</c>，调用方不得据此显示成功。
    /// </summary>
    private bool SaveCanvasToLibrary(bool showResult = true)
    {
        if (!EnsureCurrentCanvasWritable()) return false;
        try
        {
            if (string.IsNullOrWhiteSpace(currentCanvasPath))
            {
                var conflict = CanvasLibrary.FindByTitle(canvasTitle);
                if (conflict is not null && MessageBox.Show(
                        $"画布库中已存在“{canvasTitle}”，继续会覆盖该画布（覆盖前会自动留一份备份，备份失败则中止保存）。是否覆盖？",
                        "同名画布", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            }

            var applied = pendingChanges.Count;
            var target = string.IsNullOrWhiteSpace(currentCanvasPath) ? CanvasLibrary.PathForTitle(canvasTitle) : currentCanvasPath;
            var saved = CanvasSaveService.Save(BuildCanvasState(), target);
            SetCurrentCanvasPath(saved.Path);
            pendingChanges.Clear();
            canvas.SetPreview(null);
            RefreshPendingState();
            RefreshCanvasLibrary();

            var detail = $"已保存到 {saved.Path}";
            detail += saved.BackupPath is not null ? $"\n覆盖前备份：{saved.BackupPath}" : "\n（首次保存，无需备份）";
            if (applied > 0) detail += $"\n（含 {applied} 条 Agent 改动，可用工具栏的「撤销上次 Agent 提交」回退）";
            ReportCanvasHealth("保存画布", saved.Migration, saved.Validation, detail, quiet: !showResult);
            return true;
        }
        catch (CanvasSaveAbortedException aborted)
        {
            ShowSaveAborted(aborted.Message);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void OpenSelectedLibraryCanvas()
    {
        var path = SelectedLibraryPath;
        if (path is null) { MessageBox.Show("请先在画布库中选择一个画布。", "打开画布"); return; }
        // 返工 V3：从画布库打开会整体替换当前画布，必须先过统一守卫——
        // 否则 A 保存失败后打开 B，B 上的提交会按 A 的失败记录只重存，把 A 记成成功。
        if (!ConfirmPendingBeforeLeaving()) return;
        if (!CanvasOpenService.TryOpen(path, out var outcome, out var error))
        {
            MessageBox.Show(error, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 旧数据先能打开查看：迁移只作用于内存副本，校验问题只做提示，不阻断访问。
        // 返工 R16-2：这是**同一标签换成另一份文档**——先换画布身份并作废旧的提交/恢复上下文，
        // 再装载内容；否则旧提交的快照可能在修订号上恰好对得上，撤销会把上一份文档套到这一份上。
        BeginNewDocumentOnCurrentTab();
        ApplyCanvasState(outcome.State, advanceRevision: false);
        SetCurrentCanvasPath(path);
        SaveRecentCanvas();
        RefreshCanvasLibrary();
        if (outcome.UnsupportedFormat)
        {
            MessageBox.Show(
                $"该画布的格式版本是 {outcome.State.FormatVersion}，高于当前支持的 {CanvasFormat.Current}。\n"
                + "已按只读方式打开：可以查看和检查，但覆盖保存与导入会被拒绝，以免丢失未知数据。",
                "只读查看", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ReportCanvasHealth("打开画布", outcome.Migration, outcome.Validation,
            outcome.Migrated ? "打开时已按当前格式补齐可确定的缺失标识；保存后才会写入文件。" : null,
            quiet: !outcome.NeedsAttention);
    }

    /// <summary>
    /// 把打开/保存/导入后的迁移与校验结果汇总成一次提示。画布已经可用，这里只做提示并给出查看详情的入口，
    /// 不因为校验错误而拒绝访问。
    /// </summary>
    private void ReportCanvasHealth(
        string action,
        MigrationReport migration,
        CanvasIdentityReport validation,
        string? extra = null,
        bool quiet = false)
    {
        var problems = validation.Issues.Count + migration.Ambiguities.Count;
        if (problems == 0)
        {
            if (!quiet && !string.IsNullOrWhiteSpace(extra)) MessageBox.Show(extra, action);
            return;
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(extra)) lines.Add(extra);
        lines.Add($"校验问题 {validation.Issues.Count} 项（错误 {validation.ErrorCount}，警告 {validation.WarningCount}），待处理歧义 {migration.Ambiguities.Count} 项。");
        if (validation.HasErrors)
        {
            var samples = validation.Issues.Where(issue => issue.Severity == CanvasIdentitySeverity.Error).Take(3).Select(issue => "· " + issue);
            lines.AddRange(samples);
        }

        lines.Add(string.Empty);
        lines.Add("是否现在查看详情？（可查看完整报告、执行显式修复或恢复备份）");
        var choice = MessageBox.Show(string.Join("\n", lines), action + " — 数据检查", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (choice == DialogResult.Yes) ShowCanvasDiagnostics(migration, validation);
    }

    /// <summary>检查画布：只读校验 + 迁移歧义，并提供显式处理入口（丢弃无法解析的旧引用、空锁定版本改为跟随当前、恢复备份）。</summary>
    private void ShowCanvasDiagnostics(MigrationReport? migration = null, CanvasIdentityReport? validation = null)
    {
        var report = validation ?? CanvasIdentityValidator.Validate(canvas.State, AssetStore.Exists);
        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Font = new Font("Consolas", 9f)
        };
        var notes = new List<string>();
        void Refresh()
        {
            var body = new List<string>();
            if (notes.Count > 0)
            {
                body.Add("== 本次结果 ==");
                body.AddRange(notes);
                body.Add(string.Empty);
            }
            if (migration is not null)
            {
                body.Add("== 打开时的迁移报告 ==");
                body.Add(migration.ToText());
                body.Add(string.Empty);
            }
            body.Add("== 只读 ID 与引用校验 ==");
            body.Add(report.ToText());
            body.Add(string.Empty);
            body.Add("== 章节与工作树诊断（大目标 B）==");
            body.Add($"章节：{CanvasChapters.ChapterItems(canvas.State).Count} 个");
            var chapterIssues = CanvasChapters.Diagnose(canvas.State);
            body.Add(chapterIssues.Count == 0
                ? "章节诊断：没有问题。"
                : $"章节诊断：{chapterIssues.Count} 项" + Environment.NewLine
                    + string.Join(Environment.NewLine, chapterIssues.Select(issue => "· " + issue)));
            var syncPlan = CanvasWorkTreeSync.Plan(canvas.State, SyncDirection.Both);
            body.Add($"同步计划：改动 {syncPlan.Changes.Count} 项，冲突 {syncPlan.Conflicts.Count} 项"
                + (syncPlan.HasBlockingConflicts ? "（含阻断项）" : string.Empty));
            body.Add("（可用工具栏「章节同步」预览并应用）");
            body.Add(string.Empty);
            if (IsCurrentCanvasReadOnly())
            {
                body.Add("== 只读查看 ==");
                body.Add($"该画布格式版本 {currentCanvasUnsupportedFormat} 高于当前支持的 {CanvasFormat.Current}："
                    + "可以查看与检查，但覆盖保存与导入会被拒绝，两个修复操作也已禁用。");
                body.Add(string.Empty);
            }
            body.Add("== 备份 ==");
            var backups = string.IsNullOrWhiteSpace(currentCanvasPath) ? Array.Empty<string>() : CanvasBackup.ListFor(currentCanvasPath);
            body.Add(backups.Count == 0 ? "当前画布还没有备份（保存前会自动创建）。" : string.Join(Environment.NewLine, backups.Select(path => "· " + path)));
            text.Text = string.Join(Environment.NewLine, body);
        }

        using var dialog = new ScaledForm
        {
            Text = $"检查画布 — {canvasTitle}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(900, 620),
            Font = Theme.UiFont,
            BackColor = Theme.PanelBg
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 84, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(8) };

        var dropReferences = new Button { Text = "丢弃无法解析的旧引用", Width = 172, Height = 30 };
        dropReferences.Click += (_, _) =>
        {
            if (!EnsureCurrentCanvasWritable()) return;
            if (MessageBox.Show("会把节点上既没有实体也没有变体、或目标已不存在的引用删除，此操作不可撤销。继续？",
                    "丢弃无法解析的引用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var outcome = CanvasMigration.DropUnresolvableReferences(canvas.State);
            canvas.NotifyContentChanged();
            notes.Add(outcome.Summary);
            report = CanvasIdentityValidator.Validate(canvas.State, AssetStore.Exists);
            Refresh();
        };
        buttons.Controls.Add(dropReferences);

        var followCurrent = new Button { Text = "空锁定版本改为跟随当前", Width = 184, Height = 30 };
        followCurrent.Click += (_, _) =>
        {
            if (!EnsureCurrentCanvasWritable()) return;
            if (MessageBox.Show("会把锁定版本是空 GUID 的引用改成「跟随当前版本」。只有确认这些引用本意是跟随当前时才使用。继续？",
                    "空锁定版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var outcome = CanvasMigration.TreatEmptyLockedVersionAsFollowCurrent(canvas.State);
            canvas.NotifyContentChanged();
            notes.Add(outcome.Summary);
            report = CanvasIdentityValidator.Validate(canvas.State, AssetStore.Exists);
            Refresh();
        };
        buttons.Controls.Add(followCurrent);

        var restore = new Button { Text = "恢复备份…", Width = 96, Height = 30 };
        restore.Click += (_, _) =>
        {
            if (RestoreCanvasBackup()) { notes.Add("已从备份恢复画布文件；重新打开该画布即可看到恢复结果。"); Refresh(); }
        };
        buttons.Controls.Add(restore);

        var revalidate = new Button { Text = "重新检查", Width = 84, Height = 30 };
        revalidate.Click += (_, _) =>
        {
            report = CanvasIdentityValidator.Validate(canvas.State, AssetStore.Exists);
            notes.Add("已按当前画布内容重新检查。");
            Refresh();
        };
        buttons.Controls.Add(revalidate);

        var close = new Button { Text = "关闭", Width = 68, Height = 30 };
        close.Click += (_, _) => dialog.Close();
        buttons.Controls.Add(close);

        Refresh();
        dialog.Controls.Add(text);
        dialog.Controls.Add(buttons);
        dialog.ShowDialog(this);
    }

    /// <summary>从备份恢复画布文件；返回是否真的恢复。</summary>
    private bool RestoreCanvasBackup()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "画布备份 (*.json)|*.json",
            Title = "选择要恢复的备份",
            InitialDirectory = Directory.Exists(CanvasBackup.Directory) ? CanvasBackup.Directory : CanvasLibrary.Directory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        var target = string.IsNullOrWhiteSpace(currentCanvasPath)
            ? DialogTarget()
            : currentCanvasPath;
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            CanvasBackup.Restore(dialog.FileName, target);
            MessageBox.Show($"已恢复 {target}。当前内容在恢复前也留了一份备份。", "恢复备份");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            MessageBox.Show(error.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string? DialogTarget()
        {
            using var save = new SaveFileDialog
            {
                Filter = "画布文件 (*.json)|*.json",
                FileName = CanvasLibrary.SanitizeFileName(canvasTitle) + ".json",
                InitialDirectory = CanvasLibrary.Directory
            };
            return save.ShowDialog(this) == DialogResult.OK ? save.FileName : null;
        }
    }

    /// <summary>复制当前画布为一份新的独立画布：所有 ID 重发，集合内关系按映射改写，集合外引用保持共享。</summary>
    /// <param name="quiet">自动化（冒烟/回归）用：不弹守卫与结果对话框，判定与副作用完全一致。</param>
    private void DuplicateCurrentCanvas(bool quiet = false)
    {
        // 返工 R17-1：复制画布会**落盘一份新文件并切到新标签**，等于离开当前画布——
        // 必须走统一离开守卫：还有批次等待恢复时不放行（否则复制会先落盘再切走，
        // 而撤销被身份检查拒绝、切回来又被守卫拦住，用户被卡在中间），有未提交改动则先问一次。
        if (!ConfirmPendingBeforeLeaving(quiet)) return;
        try
        {
            var copy = CanvasDuplication.DuplicateCanvas(canvas.State);
            var title = CopyTitle(canvasTitle);
            var state = BuildCanvasState() with
            {
                Title = title,
                Revision = 0,
                Canvas = copy.Canvas,
                FormatVersion = CanvasFormat.Current
            };

            var path = CanvasLibrary.PathForTitle(title);
            var saved = CanvasSaveService.Save(state, path);
            AddCanvasTab(state, path);
            RefreshCanvasLibrary();
            var detail = copy.Report.ToText();
            if (copy.Report.NeedsAttention)
                detail += "\n\n注意：复制中有无法唯一重映射的引用，已在报告中列出并保留原引用，请人工处理后再保存。";
            ReportCanvasHealth("复制画布", saved.Migration, saved.Validation, detail, quiet: quiet);
        }
        catch (CanvasSaveAbortedException aborted)
        {
            ShowSaveAborted(aborted.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "复制画布失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>复制选中的节点：新 ID、带偏移，父节点与连线按集合内部映射处理，引用保持共享。</summary>
    private void DuplicateSelectedNode(WorkflowNode? node)
    {
        if (node is null) { MessageBox.Show("请先选中一个节点。", "复制节点"); return; }

        var copy = CanvasDuplication.DuplicateNodes(canvas.State, new[] { node.Id });
        if (copy.Nodes.Count == 0)
        {
            var reason = copy.Report.Ambiguities.Count > 0
                ? "无法唯一确定要复制哪一个节点：\n" + string.Join("\n", copy.Report.Ambiguities)
                : "没有复制到节点。";
            MessageBox.Show(reason, "复制节点", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var added = CanvasDuplication.Append(canvas.State, copy.Nodes, copy.Edges);
        canvas.SelectNode(copy.Nodes[0]);
        canvas.NotifyContentChanged();
        canvas.Invalidate();
        RefreshNodeInspector();
        var detail = $"已复制 {added} 个节点。\n{copy.Report.ToText()}";
        if (copy.Report.NeedsAttention)
            detail += "\n\n注意：存在无法唯一重映射的引用，已保留原引用，请人工处理。";
        ReportCanvasHealth("复制节点", MigrationReport.NotMigrated,
            CanvasIdentityValidator.Validate(canvas.State, AssetStore.Exists), detail);
    }

    /// <summary>
    /// 章节同步（大目标 B）：预览工作树与章节画布的双向差异，按 ID 与上下文同步，
    /// 有阻断冲突时整批拒绝；应用后可选立即保存（走统一的备份与原子写入入口），并支持撤销。
    /// </summary>
    private void ShowChapterSyncDialog()
    {
        if (!EnsureCurrentCanvasWritable()) return;
        var session = chapterSyncSession ??= new CanvasSyncSession();

        using var dialog = new ScaledForm
        {
            Text = $"章节同步 — {canvasTitle}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(1040, 700),
            Font = Theme.UiFont,
            BackColor = Theme.PanelBg
        };

        var direction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Margin = new Padding(8, 4, 4, 4) };
        direction.Items.AddRange(new object[] { "双向", "画布 → 工作树", "工作树 → 画布" });
        direction.SelectedIndex = 0;

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 4, 8, 4) };
        header.Controls.Add(new Label { Text = "同步方向", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(8, 8, 0, 0) });
        header.Controls.Add(direction);
        var statusLabel = new Label { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(16, 8, 0, 0) };
        header.Controls.Add(statusLabel);

        var chapterList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false, BackColor = Theme.PanelBg, ForeColor = Theme.Text, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        chapterList.Columns.Add("顺序", 70);
        chapterList.Columns.Add("章节", 220);
        chapterList.Columns.Add("父章节", 160);
        chapterList.Columns.Add("节点", 60);
        chapterList.Columns.Add("待处理", 80);

        var planBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.White,
            Font = new Font("Consolas", 9f)
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 520, BackColor = Theme.Border };
        split.Panel1.Controls.Add(chapterList);
        split.Panel2.Controls.Add(planBox);

        SyncDirection SelectedDirection() => direction.SelectedIndex switch
        {
            1 => SyncDirection.CanvasToWorkTree,
            2 => SyncDirection.WorkTreeToCanvas,
            _ => SyncDirection.Both
        };

        void RefreshPlan()
        {
            var plan = session.Plan(canvas.State, SelectedDirection());
            var issues = CanvasChapters.Diagnose(canvas.State);

            chapterList.BeginUpdate();
            chapterList.Items.Clear();
            foreach (var chapter in CanvasChapters.List(canvas.State))
            {
                var row = new ListViewItem(chapter.Order == 0 ? "（未指定）" : chapter.Order.ToString());
                row.SubItems.Add((chapter.ParentChapterId is null ? string.Empty : "    └ ") + chapter.Name);
                row.SubItems.Add(ChapterParentName(chapter) ?? "（顶层）");
                row.SubItems.Add((chapter.NodeCount + chapter.UnanchoredNodeCount).ToString());
                var pending = plan.Changes.Count(change => string.Equals(change.ObjectId, chapter.Id.ToString(), StringComparison.Ordinal));
                row.SubItems.Add(pending == 0 ? "—" : pending.ToString());
                row.Tag = chapter;
                chapterList.Items.Add(row);
            }

            chapterList.EndUpdate();

            var body = new List<string>
            {
                plan.ToText(),
                string.Empty,
                $"章节诊断：{issues.Count} 项（{CanvasChapters.ChapterItems(canvas.State).Count} 个章节）",
                string.Join(Environment.NewLine, issues.Select(issue => "· " + issue)),
                string.Empty,
                "说明：同步按稳定 ID 与上下文判定；同名多条目、悬空锚点、重复章节 ID 会报冲突。",
                "有阻断冲突时整批拒绝，画布不会留下半同步状态；待确认项需要显式选择才会应用。"
            };
            planBox.Text = string.Join(Environment.NewLine, body);
            statusLabel.Text = plan.HasBlockingConflicts
                ? $"阻断冲突 {plan.Conflicts.Count(conflict => conflict.Blocking)} 项：不能应用"
                : $"可直接应用 {plan.Automatic.Count} 项，待确认 {plan.PendingConfirmation.Count} 项";
        }

        void ApplySync(bool includeUnconfirmed)
        {
            var result = session.Apply(canvas.State, includeUnconfirmed);
            if (result.Refused)
            {
                MessageBox.Show(result.ToText(), "同步被拒绝", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshPlan();
                return;
            }

            if (result.Applied.Count > 0)
            {
                canvas.LoadState(result.Canvas);
                canvas.NotifyContentChanged();
                canvas.Invalidate();
                RefreshWorkTreeView();
                RefreshNodeInspector();
            }

            var summary = result.ToText();
            if (result.Applied.Count > 0
                && MessageBox.Show(summary + "\n\n是否立即保存？（使用带备份与原子写入的统一保存入口）", "章节同步",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                SaveCanvasToLibrary(showResult: false);
            }
            else if (result.Applied.Count == 0)
            {
                MessageBox.Show(summary, "章节同步");
            }

            RefreshPlan();
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 88, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(8) };

        var replan = new Button { Text = "重新计划", Width = 96, Height = 30 };
        replan.Click += (_, _) => RefreshPlan();
        buttons.Controls.Add(replan);

        var applyAutomatic = new Button { Text = "应用可直接项", Width = 128, Height = 30 };
        applyAutomatic.Click += (_, _) =>
        {
            if (session.LastPlan?.HasBlockingConflicts == true)
            {
                MessageBox.Show("存在阻断冲突，无法应用。请先按报告处理冲突。", "章节同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplySync(includeUnconfirmed: false);
        };
        buttons.Controls.Add(applyAutomatic);

        var applyAll = new Button { Text = "应用（含待确认）", Width = 138, Height = 30 };
        applyAll.Click += (_, _) => ApplySync(includeUnconfirmed: true);
        buttons.Controls.Add(applyAll);

        var undo = new Button { Text = "撤销上次同步", Width = 116, Height = 30 };
        undo.Click += (_, _) =>
        {
            var restored = session.Undo();
            if (restored is null)
            {
                MessageBox.Show("没有可撤销的同步。", "章节同步");
                return;
            }

            canvas.LoadState(restored);
            canvas.NotifyContentChanged();
            canvas.Invalidate();
            RefreshWorkTreeView();
            RefreshNodeInspector();
            MessageBox.Show("已撤销最近一次同步，画布回到同步前的状态（磁盘文件未变，保存后才会写入）。", "章节同步");
            RefreshPlan();
        };
        buttons.Controls.Add(undo);

        var locate = new Button { Text = "定位选中章节", Width = 116, Height = 30 };
        locate.Click += (_, _) =>
        {
            if (chapterList.SelectedItems.Count == 0 || chapterList.SelectedItems[0].Tag is not ChapterInfo chapter)
            {
                MessageBox.Show("请先在左侧选择一个章节。", "章节同步");
                return;
            }

            var node = canvas.State.Nodes.FirstOrDefault(candidate => CanvasChapters.ResolveChapterId(canvas.State, candidate) == chapter.Id);
            if (node is null)
            {
                statusLabel.Text = $"章节「{chapter.Name}」还没有节点。";
                return;
            }

            canvas.SelectNode(node);
            canvas.Invalidate();
            statusLabel.Text = $"已定位到章节「{chapter.Name}」的节点「{node.Title}」。";
        };
        buttons.Controls.Add(locate);

        var save = new Button { Text = "保存画布", Width = 96, Height = 30 };
        save.Click += (_, _) => SaveCanvasToLibrary();
        buttons.Controls.Add(save);

        var close = new Button { Text = "关闭", Width = 68, Height = 30 };
        close.Click += (_, _) => dialog.Close();
        buttons.Controls.Add(close);

        direction.SelectedIndexChanged += (_, _) => RefreshPlan();
        RefreshPlan();

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        host.Controls.Add(split);
        dialog.Controls.Add(host);
        dialog.Controls.Add(header);
        dialog.Controls.Add(buttons);
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 章节结构入口（返工 B-UI-01）：把 <see cref="CanvasChapterOperations"/> 的建立/改名/排序/移动/拆分/合并/删除
    /// 接到桌面端。只读画布进入后按钮禁用；成功应用后回写画布，保存走统一保存入口。
    /// </summary>
    private void ShowChapterStructureDialog()
    {
        var readOnly = IsCurrentCanvasReadOnly();
        using var dialog = new ChapterStructureDialog(
            canvas.State,
            readOnly,
            apply: state =>
            {
                canvas.LoadState(state);
                canvas.NotifyContentChanged();
                canvas.Invalidate();
                RefreshWorkTreeView();
                RefreshNodeInspector();
                chapterSyncSession?.Reset();
            },
            saveHandler: _ => SaveCanvasToLibrary(showResult: false));
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 整理画布（C-1/C-2）：与「章节布局」共用同一套泳道语义与手动坐标保护，
    /// 结果如实提示（保留了几个手动节点 / 因重叠阻断而未改动）。
    /// </summary>
    private void ArrangeCanvasBySwimlanes()
    {
        if (canvas.State.Nodes.Count == 0)
        {
            MessageBox.Show("画布还没有节点。", "整理画布", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var plan = canvas.AutoArrange();
        if (plan.HasBlockingConflicts)
        {
            MessageBox.Show("有手动摆放的节点互相重叠，已取消整理（画布未改动）。\n\n" + plan.ToText(canvas.State),
                "整理画布", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (plan.Changes.Count == 0)
        {
            MessageBox.Show(
                plan.ProtectedNodeIds.Count > 0
                    ? $"位置已符合章节泳道布局；保留了 {plan.ProtectedNodeIds.Count} 个手动摆放的节点。"
                    : "位置已符合章节泳道布局，无需改动。",
                "整理画布", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var detail = $"已按章节泳道整理 {plan.Changes.Count} 个节点。"
            + (plan.ProtectedNodeIds.Count > 0
                ? $"\n保留了 {plan.ProtectedNodeIds.Count} 个手动摆放的节点（要一并重排请在「章节布局」里勾选自动布局覆盖）。"
                : string.Empty)
            + "\n（磁盘未变，保存后写入）";
        MessageBox.Show(detail, "整理画布", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 章节泳道布局入口（批次 C：C-1 泳道布局、C-2 局部重排与手动坐标保护、C-3 引用检查）。
    /// 预览只算不改；应用在副本上写入并可撤销；保存走统一保存入口。
    /// </summary>
    private void ShowChapterLayoutDialog()
    {
        var readOnly = IsCurrentCanvasReadOnly();
        using var dialog = new ChapterLayoutDialog(
            canvas.State,
            readOnly,
            saveHandler: _ => SaveCanvasToLibrary(showResult: false),
            apply: state =>
            {
                canvas.LoadState(state);
                canvas.NotifyContentChanged();
                canvas.Invalidate();
                RefreshWorkTreeView();
                RefreshNodeInspector();
                chapterSyncSession?.Reset();
            },
            describeReferences: entityId =>
            {
                var owners = canvas.NodesReferencing(entityId);
                if (owners.Count == 0) return "没有节点引用该资源。";
                return $"引用该资源的节点 {owners.Count} 个（按稳定实体 ID 匹配）：" + Environment.NewLine
                    + string.Join(Environment.NewLine, owners.Select(node => $"· {node.Title}"));
            },
            focusNode: entityId =>
            {
                if (canvas.NodesReferencing(entityId).FirstOrDefault() is { } owner)
                {
                    canvas.FocusNode(owner.Id);
                    return true;
                }
                return false;
            });
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 引用与回收站入口（目标 4 / 4.1~4.4）：跨画布与草稿列出引用、改写版本策略、
    /// 解除本画布引用、带删除保护的删除，以及从回收站还原。
    /// </summary>
    private void ShowReferenceDialog()
    {
        var readOnly = IsCurrentCanvasReadOnly();
        using var dialog = new ReferenceDialog(
            canvas.State,
            currentCanvasPath,
            readOnly,
            apply: state =>
            {
                canvas.LoadState(state);
                canvas.NotifyContentChanged();
                canvas.Invalidate();
                RefreshWorkTreeView();
                RefreshNodeInspector();
            },
            focusNode: nodeId =>
            {
                if (canvas.State.Nodes.All(node => node.Id != nodeId)) return false;
                canvas.FocusNode(nodeId);
                return true;
            });
        dialog.ShowDialog(this);
    }

    /// <summary>临时展开/收起选中节点的引用卡（C-3）：只改绘制，不写入画布。</summary>
    private void ToggleSelectedNodeReferences()
    {
        if (canvas.SelectedNode is not { } node)
        {
            MessageBox.Show("请先在画布上选择一个节点。", "展开引用", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (node.References.Count == 0)
        {
            MessageBox.Show($"节点「{node.Title}」没有引用。", "展开引用", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        canvas.ToggleReferenceExpansion(node);
    }

    /// <summary>章节所属父章节的名称，用于章节同步列表显示。</summary>
    private string? ChapterParentName(ChapterInfo chapter)
    {
        if (chapter.ParentChapterId is not { } parentId) return null;
        return canvas.State.WorkTree.FirstOrDefault(item => item.Id == parentId)?.Name;
    }

    /// <summary>副本标题：优先「原标题 副本」，已存在时追加时间戳，避免覆盖同名画布。</summary>
    private string CopyTitle(string sourceTitle)
    {
        var baseTitle = string.IsNullOrWhiteSpace(sourceTitle) ? "未命名画布" : sourceTitle.Trim();
        var candidate = $"{baseTitle} 副本";
        if (CanvasLibrary.FindByTitle(candidate) is null) return candidate;
        return $"{baseTitle} 副本 {DateTime.Now:yyyyMMdd-HHmmss}";
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
    /// <summary>
    /// 询问是否把不再被引用的资产移入系统回收站，返回**实际移动**的引用（返工 S3：要登记进撤销记录）。
    /// </summary>
    private IReadOnlyList<AssetMove> OfferRecycleOrphanedAssets(IReadOnlyList<string> removedReferences)
    {
        if (removedReferences.Count == 0) return Array.Empty<AssetMove>();
        var known = CollectKnownCanvases().Append(canvas.State).ToArray();
        var orphaned = StorageMaintenance.FindUnreferenced(removedReferences, known);
        if (orphaned.Count == 0) return Array.Empty<AssetMove>();

        var names = string.Join("、", orphaned.Take(5).Select(reference => Path.GetFileName(reference)));
        var more = orphaned.Count > 5 ? $" 等 {orphaned.Count} 个文件" : string.Empty;
        if (MessageBox.Show(
                $"以下文件已不再被任何画布引用：\n{names}{more}\n\n是否移入本应用的回收目录？（撤销本批会原样移回）",
                "清理文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return Array.Empty<AssetMove>();

        // 返工 U1：移出改成应用管理的可逆移动（记录原路径），撤销时能真的移回来；
        // 旧的系统回收站移动既拿不到目标路径、也无法还原，却让撤销报成功。
        var (moved, failed) = RecycleOrphanedAssets(orphaned);
        canvas.InvalidateThumbnails();
        var message = moved.Count > 0
            ? $"已移入本应用回收目录 {moved.Count} 个文件；撤销本批会把它们移回原位。"
            : "没有文件被移动。";
        if (failed.Count > 0)
            message += "\n\n以下文件没能移动（引用已解析不到实际文件，可能已被改名或不在资产目录里）：\n"
                + string.Join("\n", failed.Select(name => "· " + name));
        MessageBox.Show(message, "清理文件");
        return moved;
    }

    /// <summary>
    /// 真正的移出动作（不含询问，便于在真实入口上反复验证；返工 V1）。
    /// 扫描与节点删除给出的是**可移植引用**（<c>asset://文件名</c>），不是文件路径——
    /// 必须先经 <see cref="AssetStore.Resolve"/> 解析成本机路径，否则一个文件都移不动却照样报「已清理」。
    /// 返回实际移动的记录与没能移动的说明，未移动的不计入撤销记录（没有可恢复的东西）。
    /// </summary>
    private (IReadOnlyList<AssetMove> Moved, IReadOnlyList<string> Failed) RecycleOrphanedAssets(
        IReadOnlyList<string> orphanedReferences)
    {
        var moved = new List<AssetMove>();
        var failed = new List<string>();
        foreach (var reference in orphanedReferences)
        {
            if (AssetRecycle.MoveReference(reference) is { } move) moved.Add(move);
            else failed.Add(Path.GetFileName(reference));
        }

        if (failed.Count > 0)
            WriteAgentCommitDiagnostic($"asset-recycle move-failed count={failed.Count} first={failed[0]}");
        return (moved, failed);
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

        if (!EnsureCurrentCanvasWritable()) return;
        try
        {
            var saved = CanvasLibrary.Save(BuildCanvasState(), currentCanvasPath);
            SetCurrentCanvasPath(CanvasLibrary.Rename(saved, newTitle, overwrite) ?? saved);
        }
        catch (CanvasSaveAbortedException aborted)
        {
            ShowSaveAborted(aborted.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "重命名失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildCanvasTabStrip(), 0, 0);
        root.Controls.Add(BuildCanvasToolbar(), 0, 1);
        canvas.Dock = DockStyle.Fill;
        canvas.StateChanged += (_, _) =>
        {
            if (canvas.SelectedNode is { } editedNode) UnifiedWorkTree.FromNode(canvas.State, editedNode);
            RefreshWorkTreeView();
            canvasRevision++;
            canvasRevisionLabel.Text = $"修订 {canvasRevision}";
            RefreshNodeInspector();
            canvas.Invalidate();
            _ = PushCanvasToWebAsync("state-changed");
        };
        canvas.SelectionChanged += (_, _) => RefreshNodeInspector();
        canvas.ReferenceActivated += (_, activation) =>
        {
            using var layer = new ReferenceLayerCanvasForm(
                canvas,
                activation,
                OpenReferenceMediaDrawer,
                (entity, before) => SaveReferenceLayer(entity, before),
                () => !IsCurrentCanvasReadOnly() && !activation.Node.IsLocked && pendingProjectCompensation is null);
            layer.ShowDialog(this);
        };
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

                var duplicate = new ToolStripMenuItem("复制节点（新 ID，引用保持共享）");
                duplicate.Click += (_, _) => DuplicateSelectedNode(node);
                menu.Items.Add(duplicate);
                menu.Items.Add(new ToolStripSeparator());
            }
            var arrange = new ToolStripMenuItem("整理画布（按章节泳道）")
            {
                Image = SystemIcons.Application.ToBitmap()
            };
            arrange.Click += (_, _) => ArrangeCanvasBySwimlanes();
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
        var toolbar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg, Padding = new Padding(8, 3, 8, 3) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Padding = new Padding(2), Margin = Padding.Empty, BackColor = Theme.PanelBg };
        void Group(string title, params (string Text, EventHandler Click)[] actions)
        {
            var button = CreatePaneButton(title + " ▾", 76, (_, _) => { });
            var menu = new ContextMenuStrip { Font = Theme.UiFont, BackColor = Theme.PanelBg, ForeColor = Theme.Text };
            foreach (var action in actions)
            {
                var item = new ToolStripMenuItem(action.Text);
                item.Click += action.Click;
                menu.Items.Add(item);
            }
            button.ContextMenuStrip = menu;
            button.Click += (_, _) =>
            {
                menu.BackColor = Theme.PanelBg;
                menu.ForeColor = Theme.Text;
                menu.Show(button, new Point(0, button.Height));
            };
            flow.Controls.Add(button);
        }
        Group("编辑", ("+ 节点", (_, _) => canvas.AddNode()), ("附件", (_, _) => AddAttachmentsToSelectedNode()), ("复制", (_, _) => DuplicateSelectedNode(canvas.SelectedNode)), ("删除", (_, _) => canvas.DeleteSelected()));
        Group("章节", ("结构", (_, _) => ShowChapterStructureDialog()), ("同步", (_, _) => ShowChapterSyncDialog()), ("泳道整理", (_, _) => ArrangeCanvasBySwimlanes()), ("布局", (_, _) => ShowChapterLayoutDialog()));
        Group("引用", ("展开 / 收起", (_, _) => ToggleSelectedNodeReferences()), ("引用与回收站", (_, _) => ShowReferenceDialog()));
        Group("起步", ("示例模板", (_, _) => canvas.ApplyTemplate()));









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
        workTreeView = new WorkTreeCanvasControl { Dock = DockStyle.Fill };
        workTreeView.ItemSelected += (_, item) =>
        {
            RefreshWorkTreeDetails(item);
            HighlightWorkTreeItem(item);
            if (item is { Kind: WorkTreeKind.Chapter })
                _ = PushCanvasToWebAsync($"chapter-switched:{item.Name}");
        };
        workTreeView.ItemActivated += item =>
        {
            if (item.ResourceId is not { } resourceId) return;
            var entity = canvas.State.FindEntity(resourceId);
            if (entity is null) return;
            if (!EnsureCurrentCanvasWritable()) return;
            var before = ProjectEntityScope.CloneEntity(entity);
            if (!OpenWorkTreeResourceEditor(entity) || !PublishSharedEntity(entity, before, "工作树资源编辑"))
            {
                ProjectEntityScope.RestoreInto(entity, before);
                return;
            }
            item.Name = entity.Name;
            item.Prompt = entity.Core;
            canvas.NotifyContentChanged();
            RefreshWorkTreeView();
            canvas.InvalidateThumbnails();
        };
        workTreeView.SetState(canvas.State);
        void Mutate(Action change)
        {
            if (!EnsureCurrentCanvasWritable()) return;
            var before = JsonSerializer.Serialize(canvas.State);
            try
            {
                change();
                var old = JsonSerializer.Deserialize<WorkflowCanvasState>(before)!;
                var changed = canvas.State.Entities.Where(e =>
                    JsonSerializer.Serialize(old.FindEntity(e.Id)) != JsonSerializer.Serialize(e)).ToList();
                if (changed.Count > 0)
                {
                    var library = ProjectLibrary.Upsert(changed, out _, out _);
                    if (!ProjectLibrary.TrySave(library, out var error)) throw new InvalidOperationException(error);
                    foreach (var entity in changed) entity.ManagedByProject = true;
                }
                RefreshWorkTreeView(); canvas.NotifyContentChanged();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                var restored = JsonSerializer.Deserialize<WorkflowCanvasState>(before)!;
                canvas.State.Nodes = restored.Nodes; canvas.State.Edges = restored.Edges;
                canvas.State.Entities = restored.Entities; canvas.State.WorkTree = restored.WorkTree;
                RefreshWorkTreeView();
                MessageBox.Show(this, ex.Message, "工作树");
            }
        }
        workTreeView.Edited += (item, value, name) => Mutate(() =>
            UnifiedWorkTree.Edit(canvas.State, item, name ? value : item.Name,
                name ? (item.Kind == WorkTreeKind.Appearance ? item.LocalState : item.Prompt) : value));
        workTreeView.ResourceDropped += (resource, chapter) => Mutate(() =>
        {
            if (resource.Kind != WorkTreeKind.Resource || chapter.Kind != WorkTreeKind.Chapter)
                throw new InvalidOperationException("请将资源拖到章节上。");
            UnifiedWorkTree.AddAppearance(canvas.State, resource.ResourceId!.Value, chapter.Id);
        });
        workTreeView.Command += command => Mutate(() =>
        {
            if (command == "chapter")
                canvas.State.WorkTree.Add(new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = $"第 {canvas.State.WorkTree.Count(x => x.Kind == WorkTreeKind.Chapter) + 1} 章" });
            else if (command == "resource")
            {
                var entity = new WorkflowEntity { Name = $"新资源 {canvas.State.Entities.Count + 1}" };
                entity.CreateVariant("默认");
                canvas.State.Entities.Add(entity);
            }
            else if (workTreeView.SelectedItem is { } item)
            {
                var variant = canvas.State.FindEntity(item.ResourceId ?? Guid.Empty)?.Variants.FirstOrDefault(v => v.Id == item.SourceVariantId);
                if (command == "lock" && variant is null) throw new InvalidOperationException("请选择章节出场引用。");
                if (command == "lock") variant!.Commit("锁定出场版本");
                UnifiedWorkTree.SetVersion(canvas.State, item, command == "latest" ? null : variant!.Versions.OrderByDescending(v => v.Number).First().Id);
            }
        });
        canvas.WorkItemDropped += (item, point) => Mutate(() =>
        {
            if (item.Kind == WorkTreeKind.Resource)
            {
                var selected = workTreeView.SelectedItem;
                var chapters = canvas.State.WorkTree.Where(x => x.Kind == WorkTreeKind.Chapter).ToList();
                var targetNode = canvas.State.Nodes.LastOrDefault(n => WorkflowCanvasControl.NodeRect(canvas.State, n).Contains(point));
                var targetChapterId = targetNode is null ? null : CanvasChapters.ResolveChapterId(canvas.State, targetNode);
                var chapter = chapters.FirstOrDefault(x => x.Id == targetChapterId)
                    ?? chapters.FirstOrDefault(x => x.Id == selected?.ChapterId || x.Id == selected?.Id)
                    ?? (chapters.Count == 1 ? chapters[0] : null);
                if (chapter is null) throw new InvalidOperationException("请先将资源拖到目标章节，再拖出场到画布。");
                item = UnifiedWorkTree.AddAppearance(canvas.State, item.ResourceId!.Value, chapter.Id);
            }
            UnifiedWorkTree.CreateNode(canvas.State, item, point.X, point.Y);
        });
        var pane = CreatePaneShell("工作树", workTreeView);
        RefreshWorkTreeView();
        return pane;
    }

    private void RefreshWorkTreeView()
    {
        if (workTreeView is null) return;
        UnifiedWorkTree.RefreshResources(canvas.State);
        var selectedItem = workTreeView.SelectedItem;
        workTreeView.SetItems(canvas.State.WorkTree, selectedItem);
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
        workTreeView?.Invalidate();
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
        submitButton.Text = "▶  提交任务"; submitButton.Tag = ButtonRole.Primary; submitButton.BackColor = Theme.PrimaryButton; submitButton.ForeColor = Theme.OnAccent;
        submitButton.FlatStyle = FlatStyle.Flat; submitButton.Dock = DockStyle.Fill; submitButton.Click += SubmitButton_Click; layout.Controls.Add(submitButton, 0, 16);

        layout.Controls.Add(SectionTitle("任务记录"), 0, 17);
        jobList.View = View.Details; jobList.FullRowSelect = true; jobList.GridLines = true; jobList.Dock = DockStyle.Fill; jobList.ShowItemToolTips = true;
        jobList.Columns.Add("状态", 90); jobList.Columns.Add("进度", 60); jobList.Columns.Add("任务", 80);
        jobList.DoubleClick += (_, _) => OpenJobDetailDialog(); layout.Controls.Add(jobList, 0, 18);
        return CreatePaneShell("任务与出图", CreateScrollHost(layout));
    }

    /// <summary>抽屉里的统一标题样式。</summary>
    private static Label SectionTitle(string text) =>
        new() { Text = text, AutoSize = true, Font = Theme.UiFontBold, ForeColor = Theme.Text, Padding = new Padding(0, 12, 0, 6) };

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Font = Theme.SmallFont, ForeColor = Theme.TextMuted, Padding = new Padding(0, 6, 0, 2) };

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
            row.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left, Font = Theme.SmallFont, ForeColor = Theme.TextMuted, Margin = new Padding(index == 0 ? 0 : 10, 8, 4, 0) }, index * 2, 0);
            row.Controls.Add(field.Box, index * 2 + 1, 0);
        }
        Place(0, left); Place(1, right);
        return row;
    }

    /// <summary>把内容放进可滚动的容器，抽屉高度不够时可以滚动。</summary>
    private static Panel CreateScrollHost(Control content)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.PanelBg, Padding = new Padding(2) };
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

        if (!EnsureCurrentCanvasWritable()) return;

        // 返工 V2/V3：导入会替换或合并进当前画布内容，与此前画布同等风险，先过统一守卫。
        if (!ConfirmPendingBeforeLeaving()) return;

        // 明确选择导入方式：是＝替换当前画布内容；否＝按 ID 合并进当前画布（同名不合并，重复导入不增量）。
        var choice = MessageBox.Show(
            "是：替换当前画布编辑内容（不删除任务历史）\n"
            + "否：合并进当前画布（按 ID 去重：已存在的对象跳过，同 ID 不同内容只报告不覆盖）\n"
            + "取消：放弃本次导入",
            "导入画布方式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        var mode = choice == DialogResult.Yes ? CanvasImportMode.Replace : CanvasImportMode.Merge;

        if (!CanvasImportService.TryImport(dialog.FileName, BuildCanvasState(), mode, out var outcome, out var error))
        {
            MessageBox.Show(error, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 返工 R16-2：导入会替换/合并成另一份内容，同样是「同一标签换文档」，先换身份并作废旧上下文。
        BeginNewDocumentOnCurrentTab();
        ApplyCanvasState(outcome.State, advanceRevision: mode == CanvasImportMode.Replace);
        SetCurrentCanvasPath(null);
        SaveRecentCanvas();
        RefreshCanvasLibrary();

        var detail = mode == CanvasImportMode.Replace
            ? $"已替换当前画布，导入 {outcome.ImportedAssets} 个资产。"
            : $"已合并进当前画布：\n{outcome.Merge!.ToText()}";
        if (outcome.MissingAssets > 0) detail += $"\n另有 {outcome.MissingAssets} 个资产在包内缺失。";
        detail += $"\n资产：新增 {outcome.ImportedAssets} 个（已存在的会跳过）。";
        if (outcome.Chapters.ChapterCount > 0 || outcome.Chapters.ChapterIssues > 0 || outcome.Chapters.SyncConflicts > 0)
            detail += "\n" + outcome.Chapters.Text;
        ReportCanvasHealth("导入画布", outcome.Migration, outcome.Validation, detail);
    }

    private sealed record CanvasResourceReplaceRequest(
        Guid RequestId,
        Guid RecordId,
        Guid EntityId,
        Guid VariantId,
        Guid? VariantVersionId);

    private sealed record ResourceReplaceOutcome(bool Ok, string Message, int? Revision);

    private bool CanCallCanvasWeb => !canvasWebAuthRejected &&
        !string.IsNullOrWhiteSpace(canvasWebToken) &&
        !canvasWebToken.Any(char.IsWhiteSpace) && !canvasWebToken.Any(char.IsControl);

    private async Task<HttpResponseMessage?> SendCanvasWebRequestAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        if (!CanCallCanvasWeb) return null;
        using var request = new HttpRequestMessage(method, $"{CanvasWebBaseUrl}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", canvasWebToken!);
        var response = await canvasPushClient.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
            canvasWebAuthRejected = true; // 配置或权限错误时停止轮询；不记录令牌或响应正文。
        return response;
    }

    private void PollResourceReplaceRequests()
    {
        if (!CanCallCanvasWeb || resourceReplacePollInFlight || IsDisposed) return;
        resourceReplacePollInFlight = true;
        _ = PollResourceReplaceRequestsAsync();
    }

    private async Task PollResourceReplaceRequestsAsync()
    {
        try
        {
            using var response = await SendCanvasWebRequestAsync(HttpMethod.Get, "/api/canvas/resource-replace/next");
            if (response is null || response.StatusCode == HttpStatusCode.NoContent) return;
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
        if (!CanCallCanvasWeb) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { requestId, ok, message, revision });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await SendCanvasWebRequestAsync(HttpMethod.Post, "/api/canvas/resource-replace/result", content);
        }
        catch { /* Web 服务未启动或不可达，下一次用户操作仍可重试。 */ }
    }

    private RecentCanvasState BuildCanvasState() =>
        new(canvasTitle, canvasRevision, promptBox.Text, negativePromptBox.Text, (int)widthBox.Value, (int)heightBox.Value, (int)stepsBox.Value, (double)cfgBox.Value, seedBox.Text, canvas.State)
        {
            FormatVersion = CanvasFormat.Current
        };

    /// <summary>把当前画布状态投影为 records 并推送给 Web 画布前端。火灾安全：失败只记日志。</summary>
    private async Task PushCanvasToWebAsync(string reason = "scene-update")
    {
        if (!CanCallCanvasWeb) return;
        try
        {
            var state = canvas.State;
            if (state is null) return;
            var records = NodeProjection.ProjectRecords(state.Nodes, state);
            var payload = JsonSerializer.Serialize(new { records, Revision = canvasRevision, Reason = reason });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await SendCanvasWebRequestAsync(HttpMethod.Post, "/api/canvas/scene", content);
        }
        catch { /* Web 服务未启动或不可达，静默忽略 */ }
    }

    private void ApplyCanvasState(RecentCanvasState state, bool advanceRevision)
    {
        // 高于当前支持的格式版本只做只读查看，保存入口据此拒绝覆盖（返工 R11）。
        currentCanvasUnsupportedFormat = state.FormatVersion > CanvasFormat.Current ? state.FormatVersion : 0;

        // 换画布就丢掉上一份同步会话，避免把别的画布的快照撤回来。
        chapterSyncSession?.Reset();
        chapterSyncSession = null;
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
        // 目标 6 / G6-1：项目级资源以**项目库为权威**——托管实体按库刷新，被引用而快照缺失的补进来；
        // 库里已不存在的托管实体如实记入缺失（交给引用与锁定版本的既有缺失提示）。
        var projectMerge = ProjectEntityScope.MergeInto(canvas.State);
        if (projectMerge.Changed || projectMerge.Missing.Count > 0)
            WriteAgentCommitDiagnostic(
                $"project-entities merged={projectMerge.Refreshed} injected={projectMerge.Injected} missing={projectMerge.Missing.Count}");
        AssetStore.Normalize(canvas.State);
        RefreshNodeInspector();
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
        referenceLabel.Text = "未引用设定"; referenceLabel.AutoSize = true; referenceLabel.ForeColor = Theme.TextMuted;
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

        var generate = new Button { Text = "AI 生成 / 继续", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Tag = ButtonRole.Primary, BackColor = Theme.PrimaryButton, ForeColor = Theme.OnAccent };
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
            MessageBox.Show("工作树资源为空。请先在工作树中创建角色、场景或道具。", "添加引用");
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
        // 锁定版本缺失的引用不能拿「变体当前内容」的参考图顶上（目标 4 / 4.2）：
        // 宁可这一条不给图，也不静默用错版本，问题由提示词里的 [阻断] 行与界面徽标暴露。
        return CanvasReferenceVersions.UsableContents(canvas.State, node)
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
            Height = 146,
            Padding = new Padding(12),
            Text = $"状态：{job.State}\n进度：{job.ProgressPercent}%\n"
                + $"工具：{(string.IsNullOrWhiteSpace(job.Tool) ? "-" : job.Tool)} · 能力：{job.Capability} · 通道：{(string.IsNullOrWhiteSpace(job.Channel) ? "-" : job.Channel)}\n"
                + $"尝试：{JobRetryPolicy.DescribeAttempt(job, JobRetryPolicy.DefaultMaxAttempts)}"
                + $" · 根任务：{(job.RootJobId == job.JobId ? "自身" : job.RootJobId.ToString()[..8])}"
                + $" · 重试自：{(job.RetryOfJobId is { } retryOf ? retryOf.ToString()[..8] : "-")}\n"
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
        var retry = new Button
        {
            Text = "重试任务",
            Width = 96,
            Height = 32,
            Enabled = execution.CanRetryJob(jobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out _)
        };
        retry.Click += async (_, _) => await RetryJobAsync(job);
        var resubmit = new Button { Text = "按相同参数重新发起", Width = 140, Height = 32, Enabled = !string.IsNullOrWhiteSpace(job.Tool) };
        resubmit.Click += async (_, _) => await ResubmitJobAsync(job);
        buttons.Controls.Add(openAsset); buttons.Controls.Add(retry); buttons.Controls.Add(resubmit); buttons.Controls.Add(cancelJob);
        dialog.Controls.Add(summary); dialog.Controls.Add(buttons); dialog.Controls.Add(inputList); dialog.Controls.Add(outputList);
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 重试失败或已取消的任务（目标 5）：沿用首次的调用信息与输入参数，用新的幂等键发起新一次尝试，
    /// 并受尝试次数上限约束（默认共 3 次）；超限后提示改用「按相同参数重新发起」。
    /// 判定按**整条尝试链**做（同根已成功、已有进行中的尝试、已用满上限都会在这里被拦下），
    /// 所以反复重试同一个历史源不会一直停在第 2 次。
    /// </summary>
    private async Task RetryJobAsync(Job job)
    {
        if (!execution.CanRetryJob(job.JobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out var reason))
        {
            MessageBox.Show(reason, "重试任务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var prompt = $"将重试任务 {job.JobId.ToString()[..8]}（{JobRetryPolicy.DescribeAttempt(job, JobRetryPolicy.DefaultMaxAttempts)}，工具 {job.Tool}）。是否继续？";
        if (MessageBox.Show(prompt, "重试任务", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            var result = await execution.RetryAsync(session, job.JobId, $"desktop-retry-{Guid.NewGuid():N}");
            SetCurrent(result);
            MessageBox.Show($"已发起第 {result.Attempt} 次尝试，新任务 {result.JobId.ToString()[..8]}（{result.State}）。", "重试任务");
        }
        catch (Exception error) when (error is ProtocolViolationException or InvalidOperationException)
        {
            MessageBox.Show(error.Message, "重试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
        var video = config.IsVideoConfigured ? $"视频：{config.VideoModel}" : "视频：未配置";
        providerLabel.Text = $"{text} · {image} · {video}";
    }

    private async Task GenerateSelectedImageAsync(bool quiet = false)
    {
        if (canvas.SelectedNode is not { } node) { if (!quiet) MessageBox.Show("请先选择一个节点。", "YeeYeeYee"); return; }
        if (RefuseWhenLocked(node, "生成参考图")) return;

        // 目标 6 / G6-T1：标记是"打开时"算出来的，库可能在之后被删除或漏拷——执行前必须**现场重新核验**，
        // 不能只信旧标记，否则缺库的引用会一路走到提供方那里（复核复现：探测计数 1）。
        var authorityMissing = ProjectEntityScope.RefreshAuthority(canvas.State);
        if (authorityMissing.Count > 0)
            WriteAgentCommitDiagnostic($"authority-recheck missing={authorityMissing.Count}");

        // 目标 6 / G6-S1：节点上存在不可用引用（锁定版本缺失，或项目级资源已不在项目库）时，
        // 必须在**发请求之前**明确拒绝——否则会拿旧快照或残缺引用去消耗额度，还让人以为用的是项目库那份。
        var referenceBlock = CanvasReferenceVersions.DescribeBlock(canvas.State, node);
        if (referenceBlock.Length > 0)
        {
            WriteAgentCommitDiagnostic(
                $"generation-blocked node={node.Id:N} references={node.References.Count}");
            if (!quiet)
                MessageBox.Show(
                    referenceBlock + "\n\n生成参考图已取消：没有发出任何请求。",
                    "引用不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (autoExpanding) { if (!quiet) MessageBox.Show("自动生成进行中，请先停止再试。", "YeeYeeYee"); return; }
        var prompt = ComposeNodePrompt(node);
        if (string.IsNullOrWhiteSpace(prompt)) { if (!quiet) MessageBox.Show("请先填写节点内容作为图像提示词。", "YeeYeeYee"); return; }

        var provider = ImageProviderFactory.Create(execution);
        if (!provider.IsConfigured)
        {
            if (!quiet)
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
                RefreshWorkTreeView();
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
                    "加入后属于资源变体的当前内容，需要在工作树资源中提交新版本才会固化，届时才能被引用锁定。",
                    "写入工作树资源", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
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
        lines.Add(config.IsVideoConfigured
            ? $"· 画视频接口：{config.VideoModel}｜地址 {(string.IsNullOrWhiteSpace(config.VideoEndpoint) ? "复用主接口地址" : config.VideoEndpoint)}｜参考帧上限 {config.VideoMaxReferenceImages}（0 表示不限）｜默认 {config.VideoDefaultSeconds} 秒（0 表示服务端默认）｜异步任务：提交后轮询取结果"
            : "· 画视频接口：未配置（视频技能只生成任务规格，不伪造视频结果）");
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
        using var dialog = new ScaledForm { Text = "AI Provider 设置", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(480, 840), Font = Font };
        var endpoint = new TextBox { Text = config.Endpoint, Dock = DockStyle.Fill, PlaceholderText = "https://api.example.com/v1" };
        var model = new TextBox { Text = config.Model, Dock = DockStyle.Fill, PlaceholderText = "模型名称" };
        var apiKey = new TextBox { Text = config.ApiKey, Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var imageModel = new TextBox { Text = config.ImageModel, Dock = DockStyle.Fill, PlaceholderText = "图像模型名称，留空表示不启用" };
        var imageEndpoint = new TextBox { Text = config.ImageEndpoint, Dock = DockStyle.Fill, PlaceholderText = "留空则复用上方接口地址" };
        var imageSize = new TextBox { Text = config.ImageSize, Dock = DockStyle.Fill, PlaceholderText = "1024x1024" };
        var maxReferencesLabel = new Label { Text = "图像接口最多能同时使用几张参考图（0 表示不限制；1 表示只支持单张底图）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var videoModel = new TextBox { Text = config.VideoModel, Dock = DockStyle.Fill, PlaceholderText = "视频模型名称，留空表示不启用画视频" };
        var videoEndpoint = new TextBox { Text = config.VideoEndpoint, Dock = DockStyle.Fill, PlaceholderText = "留空则复用上方接口地址" };
        var videoMaxRefs = new NumericUpDown { Minimum = 0, Maximum = 64, Value = Math.Clamp(config.VideoMaxReferenceImages, 0, 64), Dock = DockStyle.Fill };
        var videoSeconds = new NumericUpDown { Minimum = 0, Maximum = 600, Value = Math.Clamp(config.VideoDefaultSeconds, 0, 600), Dock = DockStyle.Fill };
        var comfyUrl = new TextBox { Text = config.ComfyUiBaseUrl, Dock = DockStyle.Fill, PlaceholderText = "http://127.0.0.1:8188" };
        var comfyCheckpoint = new TextBox { Text = config.ComfyUiCheckpoint, Dock = DockStyle.Fill, PlaceholderText = "checkpoint 文件名，填写后优先走 ComfyUI" };
        var assetDirectory = new TextBox { Text = config.AssetDirectory, Dock = DockStyle.Fill, PlaceholderText = $"留空使用默认的 {Path.Combine(AppPaths.Root, "assets")}" };
        var autoRounds = new NumericUpDown { Minimum = 0, Maximum = 20, Value = Math.Clamp(config.AutoGenerationRounds, 0, 20), Dock = DockStyle.Fill };
        var maxReferences = new NumericUpDown { Minimum = 0, Maximum = 64, Value = Math.Clamp(config.ImageMaxReferenceImages, 0, 64), Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 32, Padding = new Padding(16) };
        for (var row = 0; row < 32; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "接口地址（OpenAI 兼容 /chat/completions）", AutoSize = true }, 0, 0);
        layout.Controls.Add(endpoint, 0, 1);
        layout.Controls.Add(new Label { Text = "文本模型名称", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 2);
        layout.Controls.Add(model, 0, 3);
        layout.Controls.Add(new Label { Text = "API 密钥（仅保存在本机配置文件，落盘加密）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 4);
        layout.Controls.Add(apiKey, 0, 5);
        layout.Controls.Add(new Label { Text = "图像模型名称（用于 /images/generations）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 6);
        layout.Controls.Add(imageModel, 0, 7);
        layout.Controls.Add(new Label { Text = "图像接口地址（选填）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 8);
        layout.Controls.Add(imageEndpoint, 0, 9);
        layout.Controls.Add(new Label { Text = "图像尺寸（节点未单独设置时的默认尺寸）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 10);
        layout.Controls.Add(imageSize, 0, 11);
        layout.Controls.Add(maxReferencesLabel, 0, 12);
        layout.Controls.Add(maxReferences, 0, 13);
        layout.Controls.Add(new Label { Text = "视频模型名称（画视频接口，留空表示不启用）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 14);
        layout.Controls.Add(videoModel, 0, 15);
        layout.Controls.Add(new Label { Text = "画视频接口地址（选填，留空则复用上方接口地址）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 16);
        layout.Controls.Add(videoEndpoint, 0, 17);
        layout.Controls.Add(new Label { Text = "视频接口最多可用参考帧数（0 表示不限制）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 18);
        layout.Controls.Add(videoMaxRefs, 0, 19);
        layout.Controls.Add(new Label { Text = "视频默认时长（秒，0 表示由服务端默认）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 20);
        layout.Controls.Add(videoSeconds, 0, 21);
        layout.Controls.Add(new Label { Text = "ComfyUI 地址（填写后优先使用本地 ComfyUI 出图）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 22);
        layout.Controls.Add(comfyUrl, 0, 23);
        layout.Controls.Add(new Label { Text = "ComfyUI checkpoint", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 24);
        layout.Controls.Add(comfyCheckpoint, 0, 25);
        layout.Controls.Add(new Label { Text = "资产目录（图片与视频存放位置，留空使用默认）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 26);
        layout.Controls.Add(assetDirectory, 0, 27);
        layout.Controls.Add(new Label { Text = "AI 自动生成轮数（0 表示不限制）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 28);
        layout.Controls.Add(autoRounds, 0, 29);
        layout.Controls.Add(new Label { Text = $"配置文件：{AiProviderSettings.ConfigFilePath}", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 8, 0, 0) }, 0, 30);
        var save = new Button { Text = "保存", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) =>
        {
            // 就地修改已加载的配置，只覆盖本对话框负责的字段：
            // 之前这里 new 了一个新对象，主题、上下文窗口、Agent 工作目录、采样开关等未列出的项会被静默重置。
            config.Endpoint = endpoint.Text.Trim();
            config.Model = model.Text.Trim();
            config.ApiKey = apiKey.Text.Trim();
            config.ImageEndpoint = imageEndpoint.Text.Trim();
            config.ImageModel = imageModel.Text.Trim();
            config.ImageSize = string.IsNullOrWhiteSpace(imageSize.Text) ? "1024x1024" : imageSize.Text.Trim();
            config.VideoEndpoint = videoEndpoint.Text.Trim();
            config.VideoModel = videoModel.Text.Trim();
            config.VideoMaxReferenceImages = (int)videoMaxRefs.Value;
            config.VideoDefaultSeconds = (int)videoSeconds.Value;
            config.ComfyUiBaseUrl = comfyUrl.Text.Trim();
            config.ComfyUiCheckpoint = comfyCheckpoint.Text.Trim();
            config.AssetDirectory = assetDirectory.Text.Trim();
            config.AutoGenerationRounds = (int)autoRounds.Value;
            config.ImageMaxReferenceImages = (int)maxReferences.Value;
            config.ProviderChoiceMade = true;
            // 在这个对话框里填了地址与模型就说明要接真实模型；留空则明确是本地模拟。
            config.UseLocalProvider = string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model);

            if (!AiProviderSettings.Save(config))
            {
                MessageBox.Show("配置写入失败（配置文件可能不可写或磁盘只读），本次设置未生效。", "AI Provider 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

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
        // 智能导入：把 ComfyUI / 画图 / 画视频接口的信息从一段文本里识别出来并写入设置。
        var import = new Button { Text = "智能导入", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        import.Click += (_, _) =>
        {
            using var importer = new ProviderImportDialog(AiProviderSettings.Load(), onApplied: ApplyProviderChange);
            importer.ShowDialog(dialog);
        };
        // 接口向导：给一个接口说明网页就自动建出 api 生图 / api 生视频技能（含池子子技能），
        // 再让用户输密钥（加密落盘），最后问一次是否做最小测试并返图。
        var guide = new Button { Text = "接口向导", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat };
        guide.Click += (_, _) =>
        {
            using var wizard = new ApiImportWizardDialog(AiProviderSettings.Load(), onApplied: ApplyProviderChange);
            wizard.ShowDialog(dialog);
        };
        var settingsButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = Padding.Empty, Margin = Padding.Empty };
        settingsButtons.Controls.Add(save); settingsButtons.Controls.Add(probe); settingsButtons.Controls.Add(setup); settingsButtons.Controls.Add(import); settingsButtons.Controls.Add(guide);
        layout.Controls.Add(settingsButtons, 0, 31);
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
        // 返工 V2：切项目也走统一守卫（原先只看待处理清单，待恢复的批次会被绕过）。
        if (!ConfirmPendingBeforeLeaving()) return;

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
        // 返工 R16-1：标签持有的是**独立副本**，与接下来传给 LoadState 的那份状态分开。
        var tab = new CanvasTabState
        {
            Path = path,
            Title = string.IsNullOrWhiteSpace(state.Title) ? "未命名画布" : state.Title,
            Snapshot = CloneCanvasSnapshot(state)
        };
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
        // 返工 R16-1：快照必须是独立副本（切走之后这份状态还会被别的标签加载改写）。
        activeCanvasTab.Snapshot = BuildCanvasSnapshot();
        // 只读查看的画布不回写；备份失败会中止保存并把原因显示出来（返工 R7）。
        if (!string.IsNullOrWhiteSpace(currentCanvasPath) && !IsCurrentCanvasReadOnly())
        {
            try
            {
                CanvasLibrary.Save(activeCanvasTab.Snapshot, currentCanvasPath);
            }
            catch (CanvasSaveAbortedException aborted)
            {
                ShowSaveAborted(aborted.Message);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("切换画布时写回失败：" + error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

    /// <summary>
    /// 把当前画布写入“最近画布草稿”槽位，供启动时恢复未保存的工作状态。
    /// 这是滚动自动保存槽而不是画布库保存：不做备份（画布库文件才是权威副本），
    /// 但同样走原子写入，避免中途失败留下半截草稿；失败保持静默，不影响用户操作。
    /// </summary>
    private void SaveRecentCanvas()
    {
        try { CanvasFileWriter.Write(StorageMaintenance.DraftCanvasPath, BuildCanvasState()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void ActivateCanvasTab(CanvasTabState target)
    {
        if (switchingCanvasTab || target == activeCanvasTab) return;
        // 返工 V2：切标签也要走统一守卫（原先只看待处理清单，待恢复的批次会被绕过）。
        if (!ConfirmPendingBeforeLeaving()) return;
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

    /// <summary>
    /// 离开当前画布前的统一守卫（返工 U3/V2）：关闭标签、新建画布、切换标签、切换项目、
    /// 从画布库打开、关闭窗口**都走它**。
    /// 返回 false 表示必须中止——有未提交的 Agent 改动且保存失败/撤销未完成，或**还有任意一批在等待恢复**。
    ///
    /// 为什么不能只看待处理清单：提交成功的批次已经把清单清空，此时若撤销只做了一半（画布回退了、
    /// 文件或资产没恢复），清单是空的却仍有恢复记录挂着——只看清单就会放行离开，把恢复记录丢在半路。
    /// </summary>
    /// <param name="quiet">
    /// 自动化（回归/冒烟）用：只做判定、不弹模态框，也**不替用户做选择**——
    /// 有待提交改动或待恢复批次时一律返回 false。真实点击路径仍用默认值（会弹窗、会询问）。
    /// </param>
    private bool ConfirmPendingBeforeLeaving(bool quiet = false)
    {
        // G6-U1：项目库补偿记录只存于当前窗口，离开前必须先恢复，不能让关闭或切项目丢失记录。
        if (!EnsureNoProjectCompensation("离开当前画布或项目", quiet)) return false;

        // 返工 V2：不依赖任何清单，直接问账本「还有没有待恢复的批次」。
        if (agentBatchCommitter.Ledger.HasPendingRecovery(out var recovering))
        {
            if (!quiet)
                MessageBox.Show(
                    "上一批 Agent 改动的撤销只完成了一部分：请先重试撤销，把画布与文件恢复好，再离开当前画布。\n\n"
                    + $"（待恢复的批次：{ShortKey(recovering.ToString())}）",
                    "有未完成的恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (pendingChanges.IsEmpty) return true;
        if (quiet) return false;

        var choice = MessageBox.Show(
            "当前有尚未提交的 Agent 改动，是否先应用到画布并保存？\n\n是：应用并保存　否：撤销这批改动",
            "Agent 改动", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return false;
        return choice == DialogResult.Yes
            ? CommitPendingChanges() is not { Succeeded: false }
            : DiscardPendingChanges().Count == 0;
    }

    private void CloseCanvasTab(CanvasTabState tab)
    {
        if (canvasTabs.Count == 1) return;
        if (!ConfirmPendingBeforeLeaving()) return;
        var closingActive = ReferenceEquals(tab, activeCanvasTab);
        if (closingActive) SaveCurrentCanvasTab();
        var index = canvasTabs.IndexOf(tab);
        canvasTabs.Remove(tab);
        if (closingActive)
        {
            activeCanvasTab = canvasTabs[Math.Clamp(index - 1, 0, canvasTabs.Count - 1)];
            currentCanvasPath = activeCanvasTab?.Path;
            // 返工 R16-1：只有真的换成了另一个标签才装载快照。关闭**别的**标签时重装当前快照，
            // 会把用户在这之后的未保存编辑一起丢掉（而且旧实现里那份快照还是与活动状态同一对象）。
            if (activeCanvasTab is not null) ApplyCanvasState(activeCanvasTab.Snapshot, advanceRevision: false);
        }
        RebuildCanvasTabsUi();
        SetCurrentCanvasPath(currentCanvasPath);
    }

    private void NewCanvasButton_Click(object? sender, EventArgs e)
    {
        if (!ConfirmPendingBeforeLeaving()) return;
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
