using System.Text;

namespace DreamForge.Desktop;

/// <summary>Agent 的授权模式（权限预设）。注意最后一道关（保存画布）始终在用户手里。</summary>
internal enum AgentAuthMode
{
    /// <summary>每次都要人工勾选批准。</summary>
    Ask,

    /// <summary>Agent 直接把动作应用到当前画布，并自动保存。</summary>
    AutoStage,

    /// <summary>只提问，不许提改动。</summary>
    ReadOnly
}

/// <summary>Agent 面板的上下文快照：由主窗体提供，用来给模型交代当前在做什么。</summary>
public sealed record AgentContext(
    string NodeTitle,
    string NodeContent,
    string CanvasSummary,
    string LibrarySummary,
    string WorkspaceSummary,
    string PendingSummary,
    string WorkTreeSummary = "")
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(NodeTitle) && string.IsNullOrWhiteSpace(NodeContent)
        && string.IsNullOrWhiteSpace(CanvasSummary) && string.IsNullOrWhiteSpace(LibrarySummary);

    /// <summary>
    /// 上下文说明 + 操作协议。模型据此决定是否需要提议改动。
    /// <paramref name="maxCharacters"/> 是模型上下文窗口换算出的大致字符预算（0 表示未声明、不截断）：
    /// 只截画布上下文，**操作协议永远保留**——协议被截掉比上下文不全严重得多。
    /// </summary>
    public string Describe(int maxCharacters = 0)
    {
        var lines = new List<string>
        {
            "你在 YeeYeeYee 的画布助手里：既要和用户讨论创作，也要把**改动**作为提议交出来（协议见文末）。" +
            "用户要求改画布时，只写正文或只描述方案都不算完成——必须给出可批准的改动块。",
            "以下是当前的创作上下文，回答时请结合它："
        };
        if (!string.IsNullOrWhiteSpace(NodeTitle)) lines.Add($"- 选中节点：{NodeTitle}");
        if (!string.IsNullOrWhiteSpace(NodeContent)) lines.Add($"- 选中节点内容：\n{NodeContent}");
        if (!string.IsNullOrWhiteSpace(CanvasSummary)) lines.Add($"- 画布节点（target 可用短 id 或标题）：\n{CanvasSummary}");
        if (!string.IsNullOrWhiteSpace(LibrarySummary)) lines.Add($"- 设定库：{LibrarySummary}");
        if (!string.IsNullOrWhiteSpace(WorkTreeSummary)) lines.Add($"- 工作树（**叙事轴**：人物/能力/道具及剧情版本与来源章节）：\n{WorkTreeSummary}");
        if (!string.IsNullOrWhiteSpace(WorkspaceSummary)) lines.Add($"- 工作文件夹：{WorkspaceSummary}");
        lines.Add("- **剧情进入后的固定顺序：先树后节点，分两批提议**：\n" +
            "  1) 确认剧情：先把章节切分、出场人物、能力、关键道具、场景整理成一份清单，**这一步不要改画布、也不要改工作树**，用正文或 ask 请用户确认；用户没点头不进入第 2 步。\n" +
            "  2) 建工作树（叙事轴事实源）：项目根下按章节建 workTreeKind=Chapter（title=第10章）；角色用 workTreeKind=Character 挂在所属章节下；角色的能力用 workTreeKind=Ability 挂在角色下；能力的剧情版本用 workTreeKind=Version 挂在能力下，填 chapter 与 version。不得覆盖旧版本，只能新增版本条目。\n" +
            "  3) 建视觉骨架：为第 2 步的角色/场景/道具建或更新设定库实体（create_entity / update_entity），只写外观、服装、形态、空间布局；剧情里没有的信息先留空，不要编。\n" +
            "  4) 建节点（动作层，**只建主线**）：章节/剧情/分镜/成品各建一个节点，用 parentTarget 挂到上游节点，并**用 workTreeTarget 指向第 2 步建好的章节或能力条目**；content 只写「这一章/这一镜发生了什么」。**角色/场景/道具不建节点**，见下条。\n" +
            "  分批规则：**只有「按剧情搭项目结构」这类从零开始的请求**才按「先树后节点」分两次提议（先让用户在工作树里看清结构）。**用户明确说「生成节点 / 自动生成节点 / 铺开节点 / 建画布节点」时，不要停在树那一步——本次回复必须包含 create_node 批次**（需要的话把对应的 create_work_item 放进同一批一起给）；否则画布上一个节点都不会有，等于没做。同步方向是单向的：工作树变了才更新节点，节点的内容与附件不回写工作树。" +
            "\n- **两套载体不要互相抄**：工作树是叙事轴（这个人物/能力/道具在剧情里是什么、第几章有什么变化），只写剧情向内容；外观、服装、形态、空间布局与参考图属于视觉轴，一律写进设定库（create_entity / update_entity），不要写在工作树里。能力的素材（特效或形态参考图、演示视频）算叙事侧资料，可以挂在工作树条目上。" +
            "\n- **角色/场景/道具不进画布**：它们只存在于设定库（create_entity / update_entity），**不要为它们建 create_node**。用到它们的剧情/分镜节点用 entityTargets 引用，例如 \"entityTargets\":[\"林晚\",\"旧录音棚\",\"旧磁带\"]；画布上该卡片会显示 ◆ 引用标签与参考图缩略图，这就替代了原来的资源节点。只有需要锁定某个变体或版本时才改用单条的 entityTarget / variantTarget / variantVersion。不要把外观设定抄进节点 content。");
        if (!string.IsNullOrWhiteSpace(PendingSummary))
            lines.Add($"- **Agent 最近一批改动（已经显示在画布上，尚未保存到画布文件）**：\n{PendingSummary}\n这些改动已经作为当前最新版本提供给你；后续创建节点时可以用它们的标题作为 parentTarget、source 或 target，不要重复创建同名节点。");
        lines.Add("- **可调用的生成技能清单**（本程序提供的生成流程，与工作树里的「角色能力」是两回事）：\n" + BuiltInSkills.DescribeForAgent());
        lines.Add("- 调用**生成技能**时先说明：正在调用技能：技能名称。节点操作必须使用 actions；章节拆解按上面四步执行（默认先工作树条目再节点；用户明确要求生成节点时，节点批次必须在同一次回复里给出），节点批次内部按 章节→剧情→分镜→成品 的顺序输出（**角色/场景/道具不建节点，只写进设定库并在节点上用 entityTargets 引用**），每个节点写入 nodeCategory、完整 content、parentTarget、workTreeTarget，用到角色/场景/道具时写 entityTargets，并用 create_edge 建立关系；图片和视频必须说明参考图、画幅、时长与当前模型能力。");

        var head = string.Join("\n", lines);
        if (maxCharacters > 0 && head.Length > maxCharacters)
        {
            var omitted = head.Length - maxCharacters;
            head = head[..maxCharacters] + $"\n（以上内容按模型上下文窗口截断，省略了约 {omitted} 个字符；需要细节时请问我）";
        }

        return string.Join("\n", new[] { head, string.Empty, ActionProtocol });
    }

    /// <summary>
    /// 操作协议：模型在回复末尾给出结构化动作，宿主先把动作应用到画布；
    /// 自动模式直接保存，审批模式由用户在审批列表中选择保存或撤销。
    /// </summary>
    public const string ActionProtocol = """
    如果你需要改动画布或写入文件，**不要声称已经完成**，而是在回复的最后附上一个 JSON 代码块：
    ```json
    {"actions":[
      {"kind":"create_node","title":"节点标题","nodeCategory":"通用|分镜|成品","content":"节点内容","parentTarget":"父节点短id或标题","workTreeTarget":"要关联的工作树章节/能力条目名或短id","entityTargets":["这一镜出现的角色/场景/道具设定名，可多个"],"entityTarget":"单条引用（要指定变体或版本时才用）","variantTarget":"变体名或短id","variantVersion":"v2或版本短id，省略表示跟随当前","reason":"为什么建这个"},
      {"kind":"update_node","target":"节点短id或标题","title":"可选新标题","content":"新的内容","workTreeTarget":"可选：改挂到哪个工作树章节/能力条目","entityTargets":["可选：替换成这组引用"],"reason":"为什么改"},
      {"kind":"delete_node","target":"节点短id或标题","reason":"为什么删"},
      {"kind":"create_edge","source":"起点节点短id或标题","target":"终点节点短id或标题","reason":"为什么要连"},
      {"kind":"delete_edge","source":"起点节点短id或标题","target":"终点节点短id或标题","reason":"为什么断开"},
      {"kind":"create_entity","entityKind":"角色","title":"实体名","content":"核心设定","reason":"为什么建"},
      {"kind":"update_entity","target":"实体名","content":"新的核心设定","reason":"为什么改"},
      {"kind":"delete_entity","target":"实体名","reason":"为什么删"},
      {"kind":"create_work_item","title":"角色名或能力名称","workTreeKind":"Chapter|Character|Ability|Prop|Scene|Version|Project（Ability=角色能力，不是生成技能）","parentTarget":"父工作树条目名或短id，可空","chapter":"第10章","version":"0.2","content":"叙事设定（剧情向；外观与参考图请写进设定库）","reason":"为什么新增"},
      {"kind":"create_entity_version","entityTarget":"实体名或短id","variantTarget":"变体名或短id","chapter":"第3章","content":"新版本设定内容","versionNote":"本章升级说明","reason":"为什么创建新版本"},
      {"kind":"update_work_item","target":"工作树条目名或短id","title":"可选新名称","workTreeKind":"可选类型（Character|Ability|Prop|Scene|Version）","chapter":"可选来源章节","version":"可选版本","content":"可选新叙事设定（外观请写进设定库）","entityTarget":"可选实体名或短id","variantTarget":"可选变体名或短id","variantVersion":"可选版本","reason":"为什么更新"},
      {"kind":"delete_work_item","target":"工作树条目名或短id","reason":"为什么删除"},
      {"kind":"write_file","path":"相对工作文件夹的路径.md","content":"文件内容","reason":"为什么写"}
    ]}
    ```
    **JSON 必须合法**：content / title 这类字符串里如果要用引号，请用中文引号「」或转义成 \\"，
    不要直接写英文双引号——那会让整块 JSON 失效，界面解析不了，你的改动就全部作废（用户会说"没加上"）。
    换行请写成 \\n。
    **信息不足时就问，不要猜**：不知道加到哪个画布、节点类型、名称或内容时，
    不要凭空替用户决定，也不要把问题写成一大段正文让用户自己去回——用 ask 块，
    界面会弹窗让用户选择（弹窗里始终带一个"其他（在下面自己写）"）。
    ```json
    {"ask":{"question":"要加哪种节点？","options":["剧情节点","分镜节点","成品节点"]}}
    ```
    这条是硬要求：用户说「新建一个节点」这类**没给全信息**的请求时，
    只回一句文字问句（例如"你想建什么节点？"）**等于没做**——用户看不到弹窗、也没有可选项。
    必须输出上面的 ask 块，正文里不必再重复问题，界面会弹窗。
    options 可以留空数组（纯自由输入）；ask 可以和 actions 同时出现（例如先问清再改），
    但**没问清之前不要猜着改**。
    你的改动会先应用到当前画布。自动模式会直接保存；审批模式会在画布中显示改动，并在审批列表中等待用户选择保存或撤销。
    所以不要虚构执行结果；只说明动作内容，宿主会负责执行和展示审批状态。
    流程规则：默认**先建工作树、再建节点**，分两次提议；但**用户明确要求生成 / 自动生成 / 铺开画布节点时，
    必须在本次回复里就给出 create_node**，不能只给工作树条目。节点用 workTreeTarget 指向
    对应的章节或能力条目，这样它才会跟着项目树更新。
    连线规则：source 是上游、target 是下游，方向不能反；一次提议里**新建的节点**可以在后续
    动作里用它的标题作为 source/target（按顺序执行，前面建好的节点后面就能连上）。
    **重要**：用户要求改动时，不要用文字描述改动方案来代替这个块——只写文字等于什么都没做，
    用户看不到任何可批准的改动。必须输出上面的 JSON 块。
    例如用户说「给第一章加个分镜节点」：你应当输出一个 create_node 提议，
    并把分镜内容写进 content；如果用户选中了分镜节点并要求补上里面的场景，必须读取选中节点内容，
    把由分镜推导出的时间、地点、人物、动作和镜头连续性写进**设定库实体**（create_entity / update_entity），
    再在那个分镜节点的 entityTargets 里引用它——**不要为场景/角色/道具新建画布节点**。
    不要只在回复正文里描述这些内容。
    规则：只在确实需要改动时才输出这个块；没有改动就不要输出；自然语言的说明写在块之前。
    """;
}

/// <summary>
/// 自动模式落地结果（返工 S1）：动作清单 + 失败说明。
/// <see cref="Failure"/> 为 null 才代表**真的已应用并保存**；有值时必须如实显示，不能报成功。
/// </summary>
public sealed record AgentAutoStageResult(IReadOnlyList<AgentAction> Actions, string? Failure);

/// <summary>
/// Agent 面板与外部的交互契约，由主窗体实现。
/// 动作先暂存为预览；保存入口统一执行并保存，自动模式立即提交，审批模式等待用户确认。
/// </summary>
public sealed record AgentPaneHost(
    Func<IAiChatProvider?> Provider,
    Func<string> ProviderLabel,
    Func<AgentContext> Context,
    Func<string?> WorkspacePath,
    Action RequestWorkspaceSelection,
    Func<IReadOnlyList<AgentAction>, IReadOnlyList<string?>> PrecheckActions,
    Action<IReadOnlyList<AgentAction>> ApplyActions,
    Action<IReadOnlyList<AgentAction>> PreviewActions,
    Func<IReadOnlyList<AgentAction>, bool, IReadOnlyList<AgentAction>> PrepareActions,
    Func<IReadOnlyList<AgentAction>> PendingActions,
    Action<AgentAction> RemovePending,
    Func<string?> SaveApplied,
    Func<string?> UndoApplied,
    Action<AgentAction> FocusAction,
    Func<int> ContextCharacterBudget,
    Func<bool> ImageInputEnabled,
    Action RequestModelSettings,
    Action<string> ApplyModel,
    Action<string>? AcceptToNode)
{
    public AgentAutoStageResult AutoStage(IReadOnlyList<AgentAction> actions)
    {
        var prepared = PrepareActions(actions, true);
        // 保存入口负责执行待处理批次；这里仅暂存，避免执行两次。
        PreviewActions(prepared);
        // 返工 S1：保存结果必须传出去。旧实现丢弃返回值，上层于是无条件报「已应用并保存」。
        return new AgentAutoStageResult(prepared, SaveApplied());
    }
}

/// <summary>操作列表当前展示的是哪个阶段。</summary>
internal enum PaneMode { Approve, Pending }

/// <summary>
/// Agent 对话面板：内嵌在主窗口右侧、与画布并排，不是独立窗口。
/// 它对画布的所有改动都以「提议 → 用户审批 → 执行」的方式进行，模型不能直接改数据。
/// </summary>
public sealed class AgentPane : Panel
{
    private readonly AgentPaneHost host;
    private readonly ChatView chat = new();
    private readonly TextBox input = new();
    private readonly IconButton sendIcon;
    private readonly Button accept = new();
    private readonly Button clear = new();
    private readonly CheckBox useContext = new();
    private readonly ContextMenuStrip composerMenu = new();
    private readonly Label status = new();
    private readonly Label workspaceLabel = new();
    private readonly Button workspaceButton = new();
    private readonly Panel actionsPanel = new();
    private readonly Label actionsSummary = new();
    private readonly ListView actionList = new();
    private bool actionsExpanded;
    private readonly Label actionsHint = new();
    private readonly TableLayoutPanel layout = new();
    private readonly List<AiChatMessage> history = new();
    private readonly Button primary = new();
    private readonly Button secondary = new();
    private readonly Button removeSelected = new();
    private readonly FlowLayoutPanel actionButtons = new();
    private readonly ListView attachList = new();
    private readonly Panel attachPanel = new();
    private readonly Label attachHint = new();
    private readonly Button removeAttachment = new();
    private readonly IconButton stop;
    private readonly Button regenerate = new();
    private readonly System.Windows.Forms.Timer streamFlush = new() { Interval = 60 };
    private readonly StringBuilder pendingDisplay = new();
    private readonly StringBuilder pendingThinking = new();
    private readonly object pendingLock = new();
    private CancellationTokenSource? streaming;
    private bool streamTimerRunning;

    /// <summary>正在流式接收的那一条。正文与思考都追加到它身上，收尾时清空。</summary>
    private ChatEntry? streamingEntry;

    /// <summary>上一轮有没有产出结构化结果（改动块或 ask 块）。用来判断"该问却没问"。</summary>
    private bool lastTurnProducedProposal;

    /// <summary>上一轮的协议块出现了但写坏了（例如内容里的引号没转义）。</summary>
    private bool lastTurnBroken;

    /// <summary>上一条提问的原文（未拼附件），用于「编辑重跑」时回填输入框。</summary>
    private string lastUserText = string.Empty;

    private readonly List<AgentAttachment> attachments = new();

    // 输入区底部：统一附件入口 · 授权模式 · 模型选择 · 上下文杯
    private readonly IconButton attachButton;
    private readonly IconButton authButton;
    private readonly IconButton modelButton;
    private readonly IconButton skillButton;
    private readonly ContextMenuStrip skillMenu = new();
    private readonly CupGauge contextCup = new();
    private readonly CacheBox cacheBox = new();
    private readonly Label statsLabel = new();
    private TableLayoutPanel? toolsRow;
    private Panel? statsRow;
    private Panel? statusRow;
    private ComposerPanel? composer;
    private TableLayoutPanel? composerLayout;
    private Panel? inputHost;
    private Label? placeholder;
    private readonly ContextMenuStrip attachMenu = new();
    private readonly ContextMenuStrip authMenu = new();
    private readonly ContextMenuStrip modelMenu = new();
    private readonly ToolTip agentTip = new();
    private AgentAuthMode authMode = AgentAuthMode.Ask;
    private int turnCount;
    private AiUsage lastUsage = AiUsage.Empty;
    private double lastTokensPerSecond;
    private int lastContextCharacters;
    private PaneMode mode = PaneMode.Approve;
    private bool busy;

    public AgentPane(AgentPaneHost host)
    {
        this.host = host;

        Dock = DockStyle.Fill;
        BackColor = Theme.PanelBg;

        BuildWorkspaceRow();

        chat.Dock = DockStyle.Fill;

        BuildActionsPanel();
        BuildAttachmentPanel();

        input.BorderStyle = BorderStyle.None;
        input.Multiline = true;
        input.Dock = DockStyle.Fill;
        input.BackColor = Theme.FieldBg;
        input.ForeColor = Theme.Text;
        input.Margin = Padding.Empty;
        input.TextChanged += (_, _) => { ResizeComposer(); RefreshPlaceholder(); };
        // 焦点在内时外框换成强调色，像真的输入框在等你打字。
        input.GotFocus += (_, _) => { composer?.Invalidate(); RefreshPlaceholder(); };
        input.LostFocus += (_, _) => { composer?.Invalidate(); RefreshPlaceholder(); };
        // 第一次拿到句柄时才知道真实宽度，这时才算一次高度；面板变宽变窄也要重算折行。
        HandleCreated += (_, _) => ResizeComposer();
        Resize += (_, _) => ResizeComposer();
        // 滚轮在输入框上时也要能滚对话区：WinForms 只把滚轮发给有焦点的控件，
        // 输入框不会消费它，事件会冒泡到这里，按光标位置决定去处。
        MouseWheel += OnPaneMouseWheel;

        stop = new IconButton(RailIcon.Stop, null, "停止生成", agentTip)
        {
            Dock = DockStyle.Fill,
            Visible = false,
            Enabled = false
        };
        stop.Click += (_, _) => CancelStream();

        // 流式显示按固定间隔刷新，而不是每来一个字就刷一次界面：
        // 模型吐字很快，逐字投递会把 UI 线程打满。
        streamFlush.Tick += (_, _) =>
        {
            FlushPendingDisplay();
            if (!busy) { streamFlush.Stop(); streamTimerRunning = false; }
        };

        accept.Text = "采纳到节点";
        accept.Width = 104;
        accept.Height = 28;
        accept.FlatStyle = FlatStyle.Flat;
        accept.Enabled = false;
        accept.Margin = new Padding(0, 2, 6, 2);
        accept.Click += (_, _) => AcceptToNode();

        clear.Text = "清空";
        clear.Width = 64;
        clear.Height = 28;
        clear.FlatStyle = FlatStyle.Flat;
        clear.Margin = new Padding(0, 2, 0, 2);
        clear.Click += (_, _) => ClearConversation();

        useContext.Text = "带上画布上下文";
        useContext.AutoSize = true;
        useContext.Checked = true;
        useContext.Margin = new Padding(0, 7, 10, 0);

        // 附件与用途都收进一个图标菜单，不再排一排文字按钮。
        // 三个按钮都 Dock=Fill：它们的宽度由下面那行表格决定，不再各自按文字长度撑宽。
        attachButton = new IconButton(RailIcon.Attach, null, "附件：图片 / 文件", agentTip) { Dock = DockStyle.Fill };
        attachButton.Click += (_, _) => attachMenu.Show(attachButton, new Point(0, attachButton.Height));
        authButton = new IconButton(RailIcon.Shield, AuthCaption(), "授权模式（权限预设）", agentTip) { Dock = DockStyle.Fill };
        authButton.Click += (_, _) => authMenu.Show(authButton, new Point(0, authButton.Height));
        modelButton = new IconButton(RailIcon.Model, ModelCaption(), "选择或设置模型", agentTip) { Dock = DockStyle.Fill };
        modelButton.Click += (_, _) => { BuildModelMenu(); modelMenu.Show(modelButton, new Point(0, modelButton.Height)); };
        skillButton = new IconButton(RailIcon.Skill, "技能", "查看内置技能并填入调用模板", agentTip) { Dock = DockStyle.Fill };
        skillButton.Click += (_, _) => { BuildSkillMenu(); skillMenu.Show(skillButton, new Point(0, skillButton.Height)); };
        // 两个自绘指示器尺寸固定，居中放在各自的单元格里。
        contextCup.Anchor = AnchorStyles.None;
        cacheBox.Anchor = AnchorStyles.None;
        BuildAttachmentMenu();
        BuildAuthMenu();
        BuildModelMenu();
        BuildSkillMenu();

        removeAttachment.Text = "移除附件";
        removeAttachment.Width = 84;
        removeAttachment.Height = 26;
        removeAttachment.FlatStyle = FlatStyle.Flat;
        removeAttachment.Margin = new Padding(0, 3, 0, 3);
        removeAttachment.Click += (_, _) => RemoveSelectedAttachments();

        // 取消之后才出现：取消的语义是「保留半截 + 可重来」，这里做成
        // 「把上一条提问放回输入框，改完再发」——重跑前参数可编辑。
        regenerate.Text = "编辑重跑";
        regenerate.Width = 84;
        regenerate.Height = 26;
        regenerate.FlatStyle = FlatStyle.Flat;
        regenerate.Margin = new Padding(0, 3, 0, 3);
        regenerate.Visible = false;
        regenerate.Click += (_, _) => RegenerateToInput();

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty, AutoScroll = true };
        toolbar.Controls.Add(useContext);
        toolbar.Controls.Add(accept);
        toolbar.Controls.Add(clear);
        toolbar.Controls.Add(removeAttachment);
        toolbar.Controls.Add(regenerate);

        // 发送改成画笔图标并做成实心强调色按钮（用户要求：像一个按钮、突出显示）。
        sendIcon = new IconButton(RailIcon.Brush, null, "发送 (Ctrl+Enter)", agentTip)
        {
            Dock = DockStyle.Fill,
            Highlight = true
        };
        sendIcon.Click += async (_, _) => await SendAsync();

        // 输入框底部一行：附件 · 授权模式 · 模型 · 上下文杯 · 停止 · 发送。
        // 缓存命中箱在下面的统计行里、且在"缓存 x%"文字**之后**（用户要求）。
        //
        // 用表格布局而不是流式布局：流式布局在 400px 宽的面板里放不下这几项时**直接裁掉尾部**，
        // 杯子和箱子会整个看不见；表格布局会让模型那一列自己去挤，挤不下就省略号。
        // 这一行现在和输入框同处一个圆角外框里，所以它必须是**不透明的实心块**，
        // 且颜色与外框的填充色一致（SurfaceRole.Field）——不能用透明底：
        // 透明子控件会请自绘的外框把背景画到自己身上，圆角和边框会以错误的坐标落进来，
        // 看起来就是"黑色的马赛克块"。
        toolsRow = Theme.Surface(new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty }, SurfaceRole.Field);
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));   // 附件
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));  // 授权模式
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 模型
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));   // 技能
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));   // 上下文杯
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));   // 停止
        toolsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));   // 发送
        toolsRow.Controls.Add(attachButton, 0, 0);
        toolsRow.Controls.Add(authButton, 1, 0);
        toolsRow.Controls.Add(modelButton, 2, 0);
        toolsRow.Controls.Add(skillButton, 3, 0);
        toolsRow.Controls.Add(contextCup, 4, 0);
        toolsRow.Controls.Add(stop, 5, 0);
        toolsRow.Controls.Add(sendIcon, 6, 0);

        // 输入区：输入框上面盖一个只读提示标签。
        // 为什么不用系统 PlaceholderText：它的灰是给浅色底设计的，深色底上等于看不见；
        // 为什么不用外框自绘：输入框是不透明的，盖住了外框，画在外框上的提示永远看不到。
        inputHost = Theme.Surface(new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty }, SurfaceRole.Field);
        placeholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(3, 0, 0, 0),
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont,
            Cursor = Cursors.IBeam,
            Text = "说说你想聊什么，Ctrl+Enter 发送"
        };
        // 点提示就等于点输入框。
        placeholder.MouseDown += (_, _) => input.Focus();
        inputHost.Controls.Add(input);
        inputHost.Controls.Add(placeholder);
        placeholder.BringToFront();

        composerLayout = Theme.Surface(new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty }, SurfaceRole.Field);
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        composerLayout.Controls.Add(inputHost, 0, 0);
        composerLayout.Controls.Add(Theme.Line(DockStyle.Bottom), 0, 1);
        composerLayout.Controls.Add(toolsRow, 0, 2);
        // 外框留出内边距：里面这层是不透明的，不留边距就会把圆角与描边整块盖掉
        // （上一版就是这个原因，"输入框没有用线条圈起来"）。
        composer = new ComposerPanel(input) { Dock = DockStyle.Fill, Padding = new Padding(12, 9, 12, 9) };
        composer.Controls.Add(composerLayout);

        statsLabel.AutoSize = false;
        statsLabel.Dock = DockStyle.Fill;
        statsLabel.AutoEllipsis = true;
        statsLabel.ForeColor = Theme.TextDim;
        statsLabel.Font = Theme.SmallFont;
        statsLabel.TextAlign = ContentAlignment.MiddleLeft;
        statsLabel.Padding = new Padding(2, 0, 0, 0);

        // 用量统计行：**文字在前、箱子图标在"缓存 x%"之后**（用户要求）。
        // 状态提示另占一行——面板只有 400px 宽，两个长句子挤一行必然会截掉一个。
        var statsLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        statsLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statsLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        statsLine.Controls.Add(statsLabel, 0, 0);
        statsLine.Controls.Add(cacheBox, 1, 0);
        statsRow = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty };
        statsRow.Controls.Add(statsLine);

        statusRow = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty };
        statusRow.Controls.Add(status);
        status.Dock = DockStyle.Fill;
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.ForeColor = Theme.TextMuted;
        status.TextAlign = ContentAlignment.MiddleLeft;

        layout.Dock = DockStyle.Fill;
        layout.ColumnCount = 1;
        layout.RowCount = 8;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // 工作目录
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 对话区
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // 待审批列表
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));   // 语义按钮行
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // 附件区
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));  // 输入框（含工具行）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 用量统计（含缓存箱图标）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));   // 状态提示
        layout.Controls.Add(BuildWorkspaceRow(), 0, 0);
        layout.Controls.Add(chat, 0, 1);
        layout.Controls.Add(actionsPanel, 0, 2);
        layout.Controls.Add(toolbar, 0, 3);
        layout.Controls.Add(attachPanel, 0, 4);
        layout.Controls.Add(composer, 0, 5);
        layout.Controls.Add(statsRow, 0, 6);
        layout.Controls.Add(statusRow, 0, 7);
        Controls.Add(layout);

        input.KeyDown += async (_, e) =>
        {
            if ((e.Control || e.Shift) && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };

        RefreshWorkspaceLabel();
        RefreshStatus();
        RefreshToolsRow();
        ResizeComposer();
        RefreshPlaceholder();
        Append("系统", BuildStatusText(), Color.FromArgb(120, 120, 130));
    }

    /// <summary>面板展开时调用：刷新显示并把焦点交给输入框。</summary>
    public void FocusInput()
    {
        RefreshState();
        input.Focus();
    }

    /// <summary>外部状态（工作文件夹、Provider、待提交变更集）变化后刷新显示。</summary>
    public void RefreshState()
    {
        RefreshWorkspaceLabel();
        RefreshStatus();
        // 重新接入模型后「支持图片输入」可能变了，附件区要跟着更新。
        RefreshAttachments();
        // 授权模式 / 模型名 / 上下文占用 / 用量统计都可能因外部状态变化而过期。
        RefreshToolsRow();
        // 只在「待提交」视图下重绘列表，避免覆盖用户正在勾选的待审批列表。
        if (mode == PaneMode.Pending) SyncPending();
    }

    private Control BuildWorkspaceRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        workspaceLabel.Dock = DockStyle.Fill;
        workspaceLabel.AutoSize = false;
        workspaceLabel.TextAlign = ContentAlignment.MiddleLeft;
        workspaceLabel.ForeColor = Theme.TextMuted;
        workspaceLabel.Cursor = Cursors.Hand;
        workspaceLabel.Click += (_, _) => host.RequestWorkspaceSelection();
        workspaceButton.Text = "选择";
        workspaceButton.Dock = DockStyle.Fill;
        workspaceButton.Height = 24;
        workspaceButton.FlatStyle = FlatStyle.Flat;
        workspaceButton.Click += (_, _) => host.RequestWorkspaceSelection();
        row.Controls.Add(workspaceLabel, 0, 0);
        row.Controls.Add(workspaceButton, 1, 0);
        return row;
    }

    private void BuildActionsPanel()
    {
        actionsPanel.Dock = DockStyle.Fill;
        actionsPanel.BackColor = Theme.Hover;
        actionsPanel.Visible = false;

        actionsSummary.Dock = DockStyle.Top;
        actionsSummary.Height = 30;
        actionsSummary.AutoEllipsis = true;
        actionsSummary.Padding = new Padding(8, 0, 4, 0);
        actionsSummary.TextAlign = ContentAlignment.MiddleLeft;
        actionsSummary.ForeColor = Theme.Text;
        actionsSummary.Cursor = Cursors.Hand;
        actionsSummary.Click += (_, _) =>
        {
            actionsExpanded = !actionsExpanded;
            UpdateActionsPanelLayout();
        };

        actionsHint.Dock = DockStyle.Top;
        actionsHint.Height = 24;
        actionsHint.TextAlign = ContentAlignment.MiddleLeft;
        actionsHint.ForeColor = Color.FromArgb(150, 110, 40);
        actionsHint.Padding = new Padding(6, 0, 0, 0);

        actionList.Dock = DockStyle.Fill;
        actionList.View = View.Details;
        actionList.CheckBoxes = true;
        actionList.FullRowSelect = true;
        actionList.GridLines = false;
        actionList.MultiSelect = true;
        actionList.BorderStyle = BorderStyle.FixedSingle;
        actionList.HeaderStyle = ColumnHeaderStyle.None;
        actionList.BackColor = Theme.EditorBg;
        actionList.ForeColor = Theme.Text;
        actionList.OwnerDraw = true;
        actionList.DrawColumnHeader += (_, e) => e.DrawDefault = false;
        actionList.DrawItem += (_, e) =>
        {
            e.DrawDefault = false;
            if (e.Item is not { } item) return;
            var bounds = Rectangle.Inflate(e.Bounds, -4, -3);
            using var background = new SolidBrush(item.Selected ? Theme.Selected : Theme.PanelBg);
            e.Graphics.FillRectangle(background, bounds);
            using var accent = new SolidBrush(item.Checked ? Theme.Accent : Theme.TextMuted);
            e.Graphics.FillRectangle(accent, new Rectangle(bounds.Left, bounds.Top, 3, bounds.Height));
        };
        actionList.DrawSubItem += (_, e) =>
        {
            if (e.Item is not { } item || e.SubItem is not { } subItem) return;
            var color = e.ColumnIndex == 0 ? Theme.Text : Theme.TextMuted;
            var bounds = Rectangle.Inflate(e.Bounds, -10, 0);
            TextRenderer.DrawText(e.Graphics, subItem.Text, Theme.UiFont, bounds, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        };
        actionList.SelectedIndexChanged += (_, _) =>
        {
            if (actionList.SelectedItems.Count > 0 && actionList.SelectedItems[0].Tag is AgentAction action)
                host.FocusAction(action);
        };
        actionList.Columns.Add("操作", 170);
        actionList.Columns.Add("理由", 190);

        primary.Width = 92; primary.Height = 26; primary.FlatStyle = FlatStyle.Flat; primary.Margin = new Padding(4, 2, 4, 0);
        primary.Click += (_, _) => OnPrimary();
        secondary.Width = 64; secondary.Height = 26; secondary.FlatStyle = FlatStyle.Flat; secondary.Margin = new Padding(0, 2, 4, 0);
        secondary.Click += (_, _) => OnSecondary();
        removeSelected.Text = "移除选中"; removeSelected.Width = 80; removeSelected.Height = 26; removeSelected.FlatStyle = FlatStyle.Flat; removeSelected.Margin = new Padding(0, 2, 4, 0);
        removeSelected.Click += (_, _) => RemoveSelectedPending();

        actionButtons.Dock = DockStyle.Bottom;
        actionButtons.Height = 32;
        actionButtons.FlowDirection = FlowDirection.LeftToRight;
        actionButtons.WrapContents = false;
        actionButtons.Padding = Padding.Empty;
        actionButtons.Margin = Padding.Empty;
        actionButtons.Controls.Add(primary);
        actionButtons.Controls.Add(secondary);
        actionButtons.Controls.Add(removeSelected);

        actionsPanel.Controls.Add(actionList);
        actionsPanel.Controls.Add(actionButtons);
        actionsPanel.Controls.Add(actionsHint);
        actionsPanel.Controls.Add(actionsSummary);
    }

    /// <summary>
    /// 附件区：图片走模型的多模态通道，文本文件读成文本拼进消息正文。
    /// 没有附件时整行收起，不占对话区空间。
    /// </summary>
    private void BuildAttachmentPanel()
    {
        attachList.Dock = DockStyle.Fill;
        attachList.View = View.Details;
        attachList.FullRowSelect = true;
        attachList.MultiSelect = true;
        attachList.GridLines = true;
        attachList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        // 列宽之和必须小于面板宽度（Agent 面板默认 400px）：三列加起来超过宽度时，
        // 最后一列会被直接切掉，"来源"这一列等于不存在。
        attachList.Columns.Add("附件", 120);
        attachList.Columns.Add("类型", 120);
        attachList.Columns.Add("来源", 150);

        attachHint.Dock = DockStyle.Bottom;
        attachHint.Height = 20;
        attachHint.AutoSize = false;
        // 面板只有 400px 宽，说明文字必须能收尾，不能一个字一个字被切掉。
        attachHint.AutoEllipsis = true;
        attachHint.ForeColor = Color.Gray;
        attachPanel.BackColor = Theme.PanelBg;

        attachPanel.Dock = DockStyle.Fill;
        attachPanel.Visible = false;
        attachPanel.Controls.Add(attachList);
        attachPanel.Controls.Add(attachHint);
    }

    /// <summary>
    /// 统一附件入口的菜单。视频**置灰**：我们的两种协议（OpenAI 兼容 / Anthropic）都没核实视频输入的报文格式，
    /// 与其做一个点了报错的入口，不如明说暂不支持。
    /// </summary>
    private void BuildAttachmentMenu()
    {
        attachMenu.Items.Clear();
        attachMenu.ShowItemToolTips = true;
        attachMenu.Items.Add(new ToolStripMenuItem("图片…", null, (_, _) => AddAttachments(imagesOnly: true)));
        attachMenu.Items.Add(new ToolStripMenuItem("文件…", null, (_, _) => AddAttachments(imagesOnly: false)));
        attachMenu.Items.Add(new ToolStripMenuItem("视频（接口格式未核实，暂不支持）") { Enabled = false });
        RefreshAttachmentCapability();
    }

    /// <summary>模型没开图片输入时，别让用户选了图才发现发不出去。</summary>
    private void RefreshAttachmentCapability()
    {
        if (attachMenu.Items.Count > 0 && attachMenu.Items[0] is ToolStripMenuItem imageItem)
            imageItem.Enabled = host.ImageInputEnabled();
    }

    /// <summary>授权模式：权限预设。最后一道关（保存画布）不在这里，始终由用户把握。</summary>
    private void BuildAuthMenu()
    {
        authMenu.Items.Clear();
        authMenu.ShowItemToolTips = true;
        foreach (var (mode, label, hint) in new[]
        {
            (AgentAuthMode.Ask, "每次询问", "模型提议后逐条勾选批准（默认）"),
            (AgentAuthMode.AutoStage, "自动应用并保存", "提议直接写入当前画布并自动保存"),
            (AgentAuthMode.ReadOnly, "只读不提议", "只回答问题，不提出任何画布改动")
        })
        {
            var item = new ToolStripMenuItem(label) { Checked = authMode == mode, ToolTipText = hint };
            item.Click += (_, _) =>
            {
                authMode = mode;
                BuildAuthMenu();
                RefreshToolsRow();
                Append("系统", $"授权模式已切到「{label}」：{hint}。", Color.FromArgb(120, 120, 130));
            };
            authMenu.Items.Add(item);
        }
    }

    /// <summary>模型选择：当前服务商的预置模型列表，外加入口去完整设置。</summary>
    private void BuildModelMenu()
    {
        modelMenu.Items.Clear();
        modelMenu.ShowItemToolTips = true;
        var config = AiProviderSettings.Load();
        var preset = ProviderPreset.Match(config.Endpoint);
        foreach (var model in preset.Models)
        {
            var item = new ToolStripMenuItem(model.Id)
            {
                Checked = string.Equals(model.Id, config.Model, StringComparison.OrdinalIgnoreCase),
                ToolTipText = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note
            };
            item.Click += (_, _) => { host.ApplyModel(model.Id); RefreshToolsRow(); };
            modelMenu.Items.Add(item);
        }
        if (preset.Models.Count == 0)
            modelMenu.Items.Add(new ToolStripMenuItem($"（{preset.Name} 没有预置模型，请在设置里手填）") { Enabled = false });
        modelMenu.Items.Add(new ToolStripSeparator());
        modelMenu.Items.Add(new ToolStripMenuItem("打开模型设置…", null, (_, _) => host.RequestModelSettings()));
    }

    private void BuildSkillMenu()
    {
        skillMenu.Items.Clear();
        skillMenu.ShowItemToolTips = true;

        var header = new ToolStripMenuItem("内置技能注册表") { Enabled = false };
        skillMenu.Items.Add(header);
        skillMenu.Items.Add(new ToolStripSeparator());

        foreach (var skill in BuiltInSkills.All)
        {
            var item = new ToolStripMenuItem(skill.Name)
            {
                ToolTipText = $"{skill.Description}\n输出：{skill.OutputFormat}"
            };
            item.Click += (_, _) => SelectSkill(skill);
            skillMenu.Items.Add(item);
        }

        skillMenu.Items.Add(new ToolStripSeparator());
        skillMenu.Items.Add(new ToolStripMenuItem("查看技能协议说明", null, (_, _) => ShowSkillRegistry()));
    }

    private void SelectSkill(BuiltInSkill skill)
    {
        var prefix = $"请调用「{skill.Name}」技能（{skill.Id}）：";
        input.Text = input.Text.Length == 0
            ? prefix
            : prefix + input.Text.TrimStart();
        input.SelectionStart = input.TextLength;
        input.Focus();
        Append("技能", $"已选择技能：{skill.Name}。请补充具体目标后发送。", Theme.Accent, ChatRole.System, badge: "SKILL");
    }

    private void ShowSkillRegistry()
    {
        var lines = BuiltInSkills.All.Select(skill =>
            $"{skill.Name}（{skill.Id}）\n{skill.Description}\n输出：{skill.OutputFormat}");
        MessageBox.Show(string.Join("\n\n", lines), "内置技能注册表", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private string AuthCaption() => authMode switch
    {
        AgentAuthMode.AutoStage => "自动应用并保存",
        AgentAuthMode.ReadOnly => "只读不提议",
        _ => "每次询问"
    };

    private string ModelCaption()
    {
        // 上限 10 个字符：这一列在 400px 宽的面板里只剩 155px，
        // 太长会被省略号吃掉，反而看不出是哪个模型。
        var label = host.ProviderLabel();
        return label.Length <= 10 ? label : label[..10] + "…";
    }

    /// <summary>刷新输入区底部三块：授权/模型文字、上下文杯、命中箱与一行统计。</summary>
    private void RefreshToolsRow()
    {
        if (authButton is null || modelButton is null) return;
        authButton.Caption = AuthCaption();
        modelButton.Caption = ModelCaption();

        var config = AiProviderSettings.Load();
        var estimated = EstimateContextTokens();
        var ratio = config.ContextWindow > 0 ? (double)estimated / config.ContextWindow : 0;
        contextCup.SetRatio(ratio);
        agentTip.SetToolTip(contextCup, config.ContextWindow > 0
            ? $"上下文占用约 {estimated:N0} / {config.ContextWindow:N0} tokens（{ratio:P0}，按字符粗估）"
            : "模型未声明上下文窗口，无法估算占用");

        cacheBox.SetUsage(lastUsage.CacheHitTokens, lastUsage.CacheMissTokens);
        var hitTotal = lastUsage.CacheHitTokens + lastUsage.CacheMissTokens;
        var hitRate = hitTotal == 0 ? 0 : (double)lastUsage.CacheHitTokens / hitTotal;
        agentTip.SetToolTip(cacheBox, hitTotal == 0
            ? "服务商没有返回缓存命中数据"
            : $"缓存命中 {lastUsage.CacheHitTokens:N0} / 未命中 {lastUsage.CacheMissTokens:N0}（命中率 {hitRate:P0}）");

        statsLabel.Text = turnCount == 0
            ? "还没有对话"
            : $"第 {turnCount} 轮 · {lastTokensPerSecond:F0} tok/s · 输入 {lastUsage.InputTokens:N0} · 输出 {lastUsage.CompletionTokens:N0} · 缓存 {hitRate:P0}";

        // 按钮宽度现在由表格列决定，设完文字要让表格重新排一次，别停在旧的宽度上。
        toolsRow?.PerformLayout();
    }

    /// <summary>
    /// 上下文占用估算：没有分词器，按中文「1 token ≈ 1.5 字符」粗估，只用于看占用趋势，
    /// **不是计费依据**——真实用量以服务端返回的 usage 为准（就在旁边的命中箱里）。
    /// </summary>
    private long EstimateContextTokens()
    {
        var context = host.Context();
        var characters = (context.IsEmpty ? 0 : context.Describe(host.ContextCharacterBudget()).Length)
                         + history.Sum(message => message.Content.Length);
        lastContextCharacters = characters;
        return (long)(characters / 1.5);
    }

    private void RefreshAttachments()
    {
        attachList.Items.Clear();
        foreach (var item in attachments)
            attachList.Items.Add(new ListViewItem(new[] { item.Name, item.Describe(), item.SourcePath }) { Tag = item });

        var vision = host.ImageInputEnabled();
        RefreshAttachmentCapability();
        attachPanel.Visible = attachments.Count > 0;
        // 附件区的行高在建表的最后才追加，构造期间走到这里时它还不存在。
        if (layout.RowStyles.Count > 4)
        {
            // 运行时才算的行高不会跟着窗体的 DPI 缩放走，得自己折算。
            var rowHeight = attachPanel.Visible ? Dpi.Scale(this, 108) : 0;
            layout.RowStyles[4] = new RowStyle(SizeType.Absolute, rowHeight);
        }

        var images = attachments.Count(item => item.Kind == AgentAttachmentKind.Image);
        // 说明要短到能在 400px 一行里读完，细节交给悬停提示。
        attachHint.Text = vision
            ? $"图片 {images} · 文本 {attachments.Count - images}　—— 图片随消息发送，文本拼进正文"
            : $"图片 {images} · 文本 {attachments.Count - images}　—— 当前模型未开启图片输入";
        agentTip.SetToolTip(attachHint, vision
            ? "图片随消息一起发送，文本内容拼进正文；发送后仍保留，可手动移除"
            : "当前模型未开启图片输入，图片发不出去。可在「设置 → 高级配置」里勾选「支持图片输入」，或改用 deepseek-flash / kimi-k3 / qwen3.8-max");
    }

    private void AddAttachments(bool imagesOnly)
    {
        using var dialog = new OpenFileDialog
        {
            Title = imagesOnly ? "选择要发给模型的图片" : "选择要发给模型的文件",
            Filter = imagesOnly
                ? AgentAttachmentLoader.ImageFilter
                : $"{AgentAttachmentLoader.TextFilter}|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var errors = new List<string>();
        var added = 0;
        foreach (var path in dialog.FileNames)
        {
            // 同一路径不重复添加：否则模型会看到两张一模一样的图，白花 token。
            if (attachments.Any(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var (attachment, error) = AgentAttachmentLoader.Load(path);
            if (attachment is null) { errors.Add(error ?? "未知错误"); continue; }
            attachments.Add(attachment);
            added++;
        }

        RefreshAttachments();
        if (errors.Count > 0)
            MessageBox.Show(string.Join("\n\n", errors), "附件未能添加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (added > 0) Append("系统", $"已添加 {added} 个附件。", Color.FromArgb(120, 120, 130));
    }

    private void RemoveSelectedAttachments()
    {
        var selected = attachList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag).OfType<AgentAttachment>().ToList();
        if (selected.Count == 0) { MessageBox.Show("请先在列表中选择要移除的附件。", "移除附件"); return; }
        foreach (var item in selected) attachments.Remove(item);
        RefreshAttachments();
    }

    private void RefreshWorkspaceLabel()
    {
        var path = host.WorkspacePath();
        workspaceLabel.Text = string.IsNullOrWhiteSpace(path) ? "工作文件夹：未设置（点击选择）" : $"工作文件夹：{path}";
    }

    /// <summary>状态行上的一次性提示（例如「本轮没有改动提议」），下一轮开始时清掉。</summary>
    private string statusNote = string.Empty;

    private void RefreshStatus() =>
        status.Text = statusNote.Length > 0
            ? statusNote
            : host.Provider() is null
                ? "未接入大模型，无法对话"
                : $"就绪 · {host.ProviderLabel()}";

    /// <summary>
    /// 面板开场白的诚实性要求：本地模拟**永远不会产出改动提议**（它只会回占位文本），
    /// 所以不能说「我可以新建/修改/删除画布节点」——那会让用户以为 Agent 坏了。
    /// </summary>
    private string BuildStatusText()
    {
        if (host.Provider() is not { } provider) return "当前 Provider 不支持对话。";
        if (provider is LocalAiProvider)
            return "当前是本地模拟：我只能回占位示例文本，**不会提出任何画布改动**，所以改不了节点与设定。" +
                   "要让我改画布，请点顶栏的模型状态接入一个大模型（本地模拟不需要密钥，但也没有改动能力）。";
        return $"当前模型：{host.ProviderLabel()}。我可以在你批准后新建/修改/删除画布节点与设定，也可以把内容写进工作文件夹。";
    }

    /// <summary>只写发言者标题行：流式回复的正文随后逐段追加，标题必须先落地。</summary>
    private ChatEntry OpenStreamingEntry()
    {
        var entry = new ChatEntry
        {
            Speaker = host.ProviderLabel(),
            Accent = Color.FromArgb(40, 140, 90),
            Role = ChatRole.Assistant,
            Streaming = true
        };
        chat.Add(entry);
        return entry;
    }

    /// <summary>加一条对话条目。所有写进对话区的内容都走这里，颜色由 ChatView 按当前主题取。</summary>
    private ChatEntry Append(string speaker, string text, Color accent, ChatRole role = ChatRole.System, string note = "", string badge = "")
    {
        var entry = new ChatEntry
        {
            Speaker = speaker,
            Accent = Theme.MapFore(accent),
            Role = role,
            Body = text,
            Note = note,
            Badge = badge
        };
        chat.Add(entry);
        return entry;
    }

    private async Task SendAsync()
    {
        if (busy) return;
        var text = input.Text.Trim();
        if (text.Length == 0) return;
        if (host.Provider() is not { } provider)
        {
            Append("系统", "还没有接入大模型，无法对话。请点顶栏的模型状态或左侧「设」完成接入。", Color.FromArgb(190, 70, 70), ChatRole.Error);
            return;
        }

        input.Clear();
        var skill = BuiltInSkills.Resolve(text);
        if (skill is not null)
            Append("技能", $"正在调用技能：{skill.Name}", Color.FromArgb(75, 63, 227), ChatRole.System, badge: "SKILL");
        Append("你", text, Color.FromArgb(75, 63, 227), ChatRole.User);
        lastUserText = text;   // 记原文，供「编辑重跑」回填
        turnCount++;
        // 文本附件拼进正文、图片随消息发送；对话区仍只显示用户自己打的字。
        history.Add(new AiChatMessage
        {
            Role = "user",
            Content = AgentAttachmentLoader.ComposeContent(text, attachments),
            Images = AgentAttachmentLoader.CollectImages(attachments)
        });

        await RunTurnWithAsksAsync(provider, text);
    }

    /// <summary>
    /// 跑一轮；模型用 ask 反问时弹窗让用户选或填，拿到答案后自动继续下一轮。
    /// 最多连续问 3 次——模型反复追问会把用户拖住，那不是帮助。
    ///
    /// 额外补一层：如果模型既没给改动块、也没给 ask 块，而用户明显是在要求改动，
    /// 那多半是它把该问的事写成了正文（"你想建什么节点？"）。这时**自动请它按协议重来一次**，
    /// 而不是让用户自己再问一遍。只补一次，且会在对话区里明说补了什么。
    /// </summary>
    private async Task RunTurnWithAsksAsync(IAiChatProvider provider, string request)
    {
        const int maxRounds = 3;
        var repaired = false;
        var askedSomething = false;
        for (var round = 0; round < maxRounds; round++)
        {
            var ask = await RunTurnAsync(provider);
            if (ask is not null)
            {
                askedSomething = true;
                var answer = AgentAskDialog.Ask(FindForm(), ask);
                if (answer is null)
                {
                    Append("系统", "已跳过这个问题。需要继续时，把要求说清楚一点再说一次。", Color.FromArgb(150, 110, 40));
                    return;
                }
                Append("你", answer, Color.FromArgb(75, 63, 227), ChatRole.User);
                lastUserText = answer;
                history.Add(new AiChatMessage { Role = "user", Content = answer });
                continue;
            }

            if (!repaired && (lastTurnBroken || (!lastTurnProducedProposal && LooksLikeChangeRequest(request))))
            {
                repaired = true;
                Append("系统", lastTurnBroken
                    ? "模型这轮的改动块不是合法 JSON（多半是内容里的引号没转义），已自动请它按协议重来一次。"
                    : "模型刚才没有按协议给出结构化结果（既没有改动块、也没有提问）。已自动请它按协议重来一次。",
                    Color.FromArgb(150, 110, 40));
                history.Add(new AiChatMessage { Role = "user", Content = ProtocolRepairInstruction });
                continue;
            }
            return;
        }
        if (askedSomething)
            Append("系统", "模型连续追问了多次，先停一下——把要求一次说清可能更快。", Color.FromArgb(150, 110, 40));
    }

    /// <summary>用户这句话像是在要求改动画布（用来判断"该问却没问"）。</summary>
    private void WriteAgentDiagnostic(string rawReply, AgentReply parsed)
    {
        try
        {
            var path = AppPaths.CombineProgram("agent-diagnostics.log");
            var lines = new[]
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] provider={host.ProviderLabel()} authMode={authMode}",
                $"actions={parsed.Actions.Count} ask={(parsed.Ask is not null)} protocolBroken={parsed.ProtocolBroken} rawLength={rawReply.Length}",
                $"raw={rawReply.Replace(Environment.NewLine, "\\n")}",
                $"parsed={parsed.Text.Replace(Environment.NewLine, "\\n")}",
                string.Empty
            };
            File.AppendAllLines(path, lines, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 诊断日志不能影响 Agent 正常对话。
        }
    }

    private static bool LooksLikeChangeRequest(string text) =>
        ChangeVerbs.Any(verb => text.Contains(verb, StringComparison.Ordinal));

    private static readonly string[] ChangeVerbs =
    {
        "新建", "创建", "加一个", "加个", "添加", "删掉", "删除", "移除", "改一下", "改成", "修改",
        "更新", "连到", "连接", "连一条", "写入", "生成"
    };

    /// <summary>模型没按协议输出时，用它把要求再讲一遍。</summary>
    private const string ProtocolRepairInstruction =
        "（系统提示：你上一条回复里的 JSON 块无法解析，界面因此拿不到任何可执行的改动或可选的提问。）" +
        "请按协议重来一次：如果信息不足，只输出一个 ask 块（含 question 与 options）；" +
        "如果用户要求根据剧情自动建立节点，必须输出多个 create_node，并按需要输出 create_edge，不能只输出剧情正文；" +
        "如果需要改动画布，输出 actions 块。字符串内容里的引号必须用中文引号或转义（\\\"），换行写成 \\n，" +
        "不要在正文里重复问句。";

    /// <summary>
    /// 取消之后的「重新生成」：把上一条提问**放回输入框让你改**，而不是立刻原样重跑。
    ///
    /// 为什么要先移除历史里那条提问：取消时没有写入 assistant 回复，历史末尾就是那条 user；
    /// 若留着它再发一次，同一句话会在上下文里出现两遍。移除后你改完点发送，就是**替代**那一轮，
    /// 而不是追加新的一轮。附件不需要回填——它们本来就没被清掉，可以顺手增删；
    /// 「带上画布上下文」开关也保持原样，等于重跑前这些参数都可改。
    /// </summary>
    private void RegenerateToInput()
    {
        if (busy) return;
        regenerate.Visible = false;

        if (history.Count > 0 && string.Equals(history[^1].Role, "user", StringComparison.Ordinal))
            history.RemoveAt(history.Count - 1);

        if (!string.IsNullOrEmpty(lastUserText)) input.Text = lastUserText;
        input.SelectionStart = input.TextLength;
        input.Focus();
        status.Text = "已把上一条提问放回输入框（原那轮已从历史移除），改完点发送即可";
    }

    /// <summary>跑一轮请求：流式接收 → 解析提议与反问 → 更新界面。返回值是需要弹窗询问用户的问题（没有则 null）。</summary>
    private async Task<AgentAsk?> RunTurnAsync(IAiChatProvider provider)
    {
        busy = true;
        sendIcon.Enabled = false;
        stop.Visible = true;
        stop.Enabled = true;
        regenerate.Visible = false;   // 新一轮开始，「重新生成」这个补救入口就该收起来
        statusNote = string.Empty;    // 上一轮的一次性提示不该留到这一轮
        status.Text = "正在连接…（可点「停止」取消）";
        streamTimerRunning = false;
        streamingEntry = null;
        lock (pendingLock) { pendingDisplay.Clear(); pendingThinking.Clear(); }

        streaming = new CancellationTokenSource();
        var sink = BuildStreamSink();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await provider.ChatStreamAsync(BuildRequestMessages(), sink, streaming.Token);
            clock.Stop();
            // 速率用服务端报的输出 token 算；服务商不给用量时退回字符数估算，并如实标注。
            var outputTokens = lastUsage.CompletionTokens > 0 ? lastUsage.CompletionTokens : (long)(reply.Length / 1.5);
            lastTokensPerSecond = clock.Elapsed.TotalSeconds > 0.1 ? outputTokens / clock.Elapsed.TotalSeconds : outputTokens;
            FlushPendingDisplay();
            CloseStreamBlock();

            var parsed = AgentActionParser.Parse(reply);
            lastTurnProducedProposal = parsed.Actions.Count > 0 || parsed.Ask is not null;
            lastTurnBroken = parsed.ProtocolBroken;
            WriteAgentDiagnostic(reply, parsed);
            history.Add(new AiChatMessage { Role = "assistant", Content = parsed.Text });
            accept.Enabled = host.AcceptToNode is not null && !string.IsNullOrWhiteSpace(parsed.Text);

            // 授权模式决定「提议」怎么落地——但**最后一道关始终是保存画布**，这一层不越过。
            var proposedActions = parsed.Actions;
            var actionsToShow = proposedActions;
            if (authMode == AgentAuthMode.ReadOnly && proposedActions.Count > 0)
            {
                Append("系统", $"授权模式是「只读不提议」：已忽略模型提出的 {proposedActions.Count} 条改动。", Color.FromArgb(150, 110, 40));
                actionsToShow = Array.Empty<AgentAction>();
            }
            else if (proposedActions.Count > 0 && authMode == AgentAuthMode.AutoStage)
            {
                var staged = host.AutoStage(proposedActions);
                if (string.IsNullOrEmpty(staged.Failure))
                    Append("系统", $"已将 {staged.Actions.Count} 条改动直接应用到画布并保存。", Color.FromArgb(75, 63, 227));
                else
                    Append("自动应用未完成", $"自动模式没能完成这批改动：{staged.Failure}", Color.FromArgb(176, 66, 66));
                actionsToShow = Array.Empty<AgentAction>();
            }

            // 操作块的 JSON 在流式过程中就被挡掉了（见 BuildStreamSink），
            // 这里只告诉用户"有东西要审"，细节在下方审批列表里看。
            if (proposedActions.Count > 0 && authMode == AgentAuthMode.Ask)
                Append("系统", $"已将 {proposedActions.Count} 条改动直接应用到画布。请在下方审批列表中选择保存或撤销。",
                    Color.FromArgb(75, 63, 227));
            else if (proposedActions.Count == 0 && parsed.Ask is null)
                // 只回复文字、没有任何提议时，必须说清楚——否则用户会以为"Agent 加不了节点"。
                statusNote = parsed.ProtocolBroken
                    ? "模型这轮的改动块不是合法 JSON，没能解析（改动没有生效），已自动请它按协议重来。"
                    : "本轮没有改动提议（模型只回答了文字）。请确认已开启「带上画布上下文」，并明确说“自动建立节点”。";
            if (parsed.Ask is not null)
                Append("系统", "模型需要补充信息，正在弹窗询问——也可以直接在窗口里输入。", Color.FromArgb(75, 63, 227));
            if (authMode == AgentAuthMode.Ask && proposedActions.Count > 0)
            {
                var applied = host.PrepareActions(proposedActions, false);
                host.PreviewActions(applied);
                ShowApproval(applied);
            }
            else if (proposedActions.Count > 0)
                SyncPending();
            return parsed.Ask;
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            FlushPendingDisplay();
            CloseStreamBlock();
            // 半截回复不进历史：它不是模型的完整回答，留着会污染后续上下文。
            // 但它留在屏幕上，并给一个重新生成的入口（取消后保留半截的语义）。
            Append("系统", "已停止生成。以下内容不完整，未写入对话历史；可点「编辑重跑」把这条提问放回输入框改完再发。", Color.FromArgb(150, 110, 40));
            regenerate.Visible = true;
            return null;
        }
        catch (Exception error)
        {
            FlushPendingDisplay();
            CloseStreamBlock();
            Append("错误", error.Message, Color.FromArgb(190, 70, 70), ChatRole.Error);
            // 失败同样允许改完再来一次（失败态也带重试入口），但不把失败说成"已停止"。
            regenerate.Visible = true;
            return null;
        }
        finally
        {
            streamFlush.Stop();
            streamTimerRunning = false;
            streaming?.Dispose();
            streaming = null;
            stop.Enabled = false;
            stop.Visible = false;
            busy = false;
            sendIcon.Enabled = true;
            RefreshStatus();
            // 本轮的 tok/s、用量与命中率都变了，统计行要跟着走。
            RefreshToolsRow();
            input.Focus();
        }
    }

    /// <summary>
    /// 组装流式回调。三件事在这里决定：
    /// 1）正文显示**只取操作块之前的部分**——模型的操作块是给程序的，不该糊在用户脸上；
    ///    定位复用解析器自己的逻辑，避免显示与解析判断不一致。
    /// 2）思考走**独立队列**，落到条目的思考字段上（界面上是可点开的折叠入口）。
    /// 3）进入操作块后一律不显示，也不回头补显。
    /// </summary>
    private AiStreamSink BuildStreamSink()
    {
        var raw = new StringBuilder();
        var displayed = 0;
        var hidden = false;
        // 这两个状态只在这一轮流式里有效，所以放局部变量，不再挂在面板的字段上。
        var bodyStarted = false;
        var thinkingSealed = false;

        return new AiStreamSink(
            OnText: delta =>
            {
                // 正文一开始，思考阶段就结束了：后面的思考增量不再收。
                bodyStarted = true;
                thinkingSealed = true;
                raw.Append(delta);
                if (hidden) return;
                var all = raw.ToString();
                var blockStart = AgentActionParser.FindProtocolBlockStart(all);
                var visibleEnd = blockStart >= 0 ? blockStart : all.Length;
                if (visibleEnd <= displayed) return;
                QueueDisplay(all[displayed..visibleEnd]);
                displayed = visibleEnd;
                if (visibleEnd < all.Length) hidden = true;
            },
            OnThinking: delta =>
            {
                if (thinkingSealed || bodyStarted) return;
                lock (pendingLock) pendingThinking.Append(delta);
                RequestFlush();
            },
            OnUsage: usage => lastUsage = usage);
    }

    private void CancelStream()
    {
        if (streaming is null) return;
        stop.Enabled = false;
        status.Text = "正在取消…";
        streaming.Cancel();
    }

    /// <summary>把正文增量排进显示队列（在后台线程调用）。</summary>
    private void QueueDisplay(string text)
    {
        if (text.Length > 0) lock (pendingLock) pendingDisplay.Append(text);
        RequestFlush();
    }

    /// <summary>确保刷新定时器已启动：思考与正文都要唤醒它，否则思考期间界面不动。</summary>
    private void RequestFlush()
    {
        if (streamTimerRunning) return;
        streamTimerRunning = true;
        BeginInvoke(() => { if (!IsDisposed && busy) streamFlush.Start(); });
    }

    /// <summary>把队列里的内容真正写进对话区（UI 线程）：先思考，再正文。</summary>
    private void FlushPendingDisplay()
    {
        FlushThinking();

        string text;
        lock (pendingLock)
        {
            if (pendingDisplay.Length == 0) return;
            text = pendingDisplay.ToString();
            pendingDisplay.Clear();
        }
        streamingEntry ??= OpenStreamingEntry();
        chat.AppendBody(streamingEntry, text);
    }

    /// <summary>
    /// 思考区：追加到当前条目的思考字段上，界面上是一条可点开的折叠入口。
    ///
    /// 这里不再有"限长显示"：折叠状态下它只占一行，展开是用户主动要的，
    /// 所以没必要截断——把完整推理留着，比省几行更有价值。
    /// </summary>
    private void FlushThinking()
    {
        string text;
        lock (pendingLock)
        {
            text = pendingThinking.ToString();
            pendingThinking.Clear();
        }
        if (text.Length == 0) return;
        // 还没有正文条目时也要把思考挂上去：思考可能先于任何正文到达。
        streamingEntry ??= OpenStreamingEntry();
        chat.AppendThinking(streamingEntry, text);
    }

    /// <summary>流式结束后收尾：收住思考块，并补一个空行，让下一段内容不贴着上一段。</summary>
    private void CloseStreamBlock()
    {
        if (streamingEntry is not { } entry) return;
        entry.Streaming = false;
        chat.Relayout();
        streamingEntry = null;
    }

    /// <summary>组装请求：可选的上下文放在最前，其后是历史轮次。</summary>
    private List<AiChatMessage> BuildRequestMessages()
    {
        var messages = new List<AiChatMessage>();
        var hasContextProtocol = false;
        if (useContext.Checked && host.Context() is { } context && !context.IsEmpty)
        {
            messages.Add(new AiChatMessage { Role = "system", Content = context.Describe(host.ContextCharacterBudget()) });
            hasContextProtocol = true;
        }
        // 即使用户关闭了画布上下文，或当前画布为空，也必须保留动作协议；否则模型只能写正文。
        if (!hasContextProtocol)
            messages.Insert(0, new AiChatMessage { Role = "system", Content = AgentContext.ActionProtocol });
        messages.AddRange(history);
        return messages;
    }

    /// <summary>模型动作已显示为画布虚影，此处审批是否保存或撤销。</summary>
    private void ShowApproval(IReadOnlyList<AgentAction> actions)
    {
        if (actions.Count == 0) { SyncPending(); return; }
        mode = PaneMode.Approve;
        actionList.Items.Clear();
        // 整批一起预检：同一批里前面建好的节点，后面的连线才能被正确识别。
        var hints = host.PrecheckActions(actions);
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var item = new ListViewItem(action.Describe()) { Checked = true, Tag = action };
            // 把预检后果并进理由列，让用户批准前就知道会发生什么（覆盖、锁定、连带删除等）。
            var hint = index < hints.Count ? hints[index] : null;
            item.SubItems.Add(string.IsNullOrWhiteSpace(hint)
                ? action.Reason
                : (string.IsNullOrWhiteSpace(action.Reason) ? $"⚠ {hint}" : $"{action.Reason}　⚠ {hint}"));
            actionList.Items.Add(item);
        }
        actionList.CheckBoxes = true;
        primary.Text = "保存画布";
        secondary.Text = "撤销本批";
        removeSelected.Visible = false;
        actionsHint.Text = $"虚影预览 {actions.Count} 条改动——保存画布后写入，或撤销本批";
        ShowActionsPanel(actions.Count);
    }

    /// <summary>展示当前已应用但尚未保存的 Agent 批次。</summary>
    public void SyncPending()
    {
        var pending = host.PendingActions();
        if (pending.Count == 0) { HideActionsPanel(); return; }
        mode = PaneMode.Pending;
        actionList.Items.Clear();
        foreach (var action in pending)
        {
            var item = new ListViewItem(action.Describe()) { Tag = action };
            item.SubItems.Add(string.IsNullOrWhiteSpace(action.Reason) ? "—" : action.Reason);
            actionList.Items.Add(item);
        }
        actionList.CheckBoxes = false;
        primary.Text = "保存画布";
        secondary.Text = "撤销本批";
        removeSelected.Visible = false;
        actionsHint.Text = "点击具体改动可定位画布节点；保存后写入画布，或撤销这一批改动";
        actionsSummary.Text = $"您有 {pending.Count} 个 Agent 虚影预览改动 　⌃";
        // 新动作进入待提交区时直接展开，用户能立即看到节点预览和提交按钮。
        actionsExpanded = true;
        ShowActionsPanel(pending.Count);
    }

    private void ShowActionsPanel(int count)
    {
        actionsPanel.Visible = true;
        UpdateActionsPanelLayout(count);
    }

    private void UpdateActionsPanelLayout(int? count = null)
    {
        var actualCount = count ?? actionList.Items.Count;
        actionList.Visible = actionsExpanded;
        actionsHint.Visible = actionsExpanded;
        actionButtons.Visible = actionsExpanded;
        var height = actionsExpanded ? Math.Min(260, 54 + actualCount * 24) : 34;
        layout.RowStyles[2] = new RowStyle(SizeType.Absolute, height);
    }

    private void HideActionsPanel()
    {
        actionList.Items.Clear();
        actionsPanel.Visible = false;
        layout.RowStyles[2] = new RowStyle(SizeType.Absolute, 0);
    }

    private void OnPrimary()
    {
        if (host.PendingActions().Count == 0) return;
        // 保存可能失败（配置文件/画布不可写）：必须照实显示，不能一律报「已保存」（返工 R3）。
        var failure = host.SaveApplied();
        if (string.IsNullOrEmpty(failure))
            Append("已保存", "Agent 改动已写入画布文件。", Color.FromArgb(75, 63, 227));
        else
            Append("保存未完成", failure, Color.FromArgb(176, 66, 66));
        SyncPending();
    }

    private void OnSecondary()
    {
        var count = host.PendingActions().Count;
        if (count == 0) return;
        if (MessageBox.Show($"确定撤销这 {count} 条已应用的 Agent 改动？\n\n画布将恢复到本批动作执行前的状态。",
                "撤销 Agent 改动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var failure = host.UndoApplied();
        if (string.IsNullOrEmpty(failure))
            Append("已撤销", $"{count} 条 Agent 改动已撤销。", Color.FromArgb(150, 110, 40));
        else
            Append("撤销未完成", failure, Color.FromArgb(176, 66, 66));
        SyncPending();
    }

    private void RemoveSelectedPending()
    {
        if (mode != PaneMode.Pending) return;
        var selected = actionList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag).OfType<AgentAction>().ToList();
        if (selected.Count == 0) { MessageBox.Show("请先在列表中选择要移除的改动。", "移除待提交"); return; }
        foreach (var action in selected) host.RemovePending(action);
        Append("已移除", $"已移除 {selected.Count} 条待提交改动。", Color.FromArgb(150, 110, 40));
        SyncPending();
    }

    private List<AgentAction> CheckedActions() =>
        actionList.Items.Cast<ListViewItem>()
            .Where(item => item.Checked)
            .Select(item => item.Tag).OfType<AgentAction>().ToList();

    private void AcceptToNode()
    {
        if (host.AcceptToNode is null) return;
        var last = history.LastOrDefault(message => string.Equals(message.Role, "assistant", StringComparison.Ordinal));
        if (last is null || string.IsNullOrWhiteSpace(last.Content)) return;
        host.AcceptToNode(last.Content);
    }

    private void ClearConversation()
    {
        history.Clear();
        chat.Clear();
        accept.Enabled = false;
        // 附件属于这次对话，一并清掉；待提交改动属于画布状态，不随对话清空。
        attachments.Clear();
        regenerate.Visible = false;
        lastUserText = string.Empty;
        RefreshAttachments();
        SyncPending();
        Append("系统", BuildStatusText(), Color.FromArgb(120, 120, 130));
    }

    /// <summary>没输入时显示提示文字。有字就藏起来，否则会和正文叠在一起。</summary>
    private void RefreshPlaceholder()
    {
        if (placeholder is null) return;
        placeholder.Visible = input.TextLength == 0;
    }

    /// <summary>
    /// 输入框随内容长高（1～5 行），同时把外框整体高度一起改掉。
    /// 行高与折行都是运行时算的，所以必须按当前 DPI 折算——否则高分屏下输入框永远只有一行高。
    /// </summary>
    private void ResizeComposer()
    {
        if (composerLayout is null || layout.RowStyles.Count <= 5) return;
        var font = input.Font;
        var textFlags = TextFormatFlags.NoPrefix;
        var lineHeight = Math.Max(1, TextRenderer.MeasureText("测", font, new Size(int.MaxValue, int.MaxValue), textFlags).Height);
        // 宽度可能是 0（还没布局），这时按一行算；拿到句柄后会再算一次。
        var width = input.ClientSize.Width - Dpi.Scale(this, 6);
        var text = input.TextLength == 0 ? " " : input.Text;
        var height = width <= 0
            ? lineHeight
            : TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | textFlags).Height;
        var lines = Math.Clamp((int)Math.Ceiling(height / (double)lineHeight), 1, 5);

        var inputHeight = lineHeight * lines + Dpi.Scale(this, 10);
        composerLayout.RowStyles[0] = new RowStyle(SizeType.Absolute, inputHeight);
        // 输入区 + 分界线 + 工具行 + 外框上下内边距 = 整个圆角框的高度。
        layout.RowStyles[5] = new RowStyle(SizeType.Absolute,
            inputHeight + Dpi.Scale(this, 1) + Dpi.Scale(this, 36) + Dpi.Scale(this, 18));
    }

    /// <summary>滚轮按光标位置决定去处：在对话区上就滚对话区，否则交回默认行为。</summary>
    private void OnPaneMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!chat.RectangleToScreen(chat.ClientRectangle).Contains(Cursor.Position)) return;
        chat.ScrollByLines(-Math.Sign(e.Delta) * 3);
    }
}

/// <summary>
/// 输入框的圆角外框：自绘圆角底 + 描边（聚焦时换强调色），里面放无边框的 TextBox。
///
/// 关键点：**外框必须留出内边距**。里面那层是不透明的，Dock=Fill 会把圆角与描边整块盖掉
/// ——上一版就是这样，所以"输入框没有被线条圈起来"。提示文字与分界线也**不能画在这里**：
/// 它们要么被不透明的输入框盖住，要么被内层盖住，只能做成真正的子控件。
///
/// 为什么还留着 TextBox：中文输入法、选区、粘贴、Ctrl+Enter 这些是系统文本框几十年磨出来的行为，
/// 自己实现一个编辑器只会更差。所以做法是"把系统控件的皮换掉"。
/// </summary>
internal sealed class ComposerPanel : Panel
{
    private readonly Control focusTarget;

    public ComposerPanel(Control focusTarget)
    {
        this.focusTarget = focusTarget;
        BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var radius = Dpi.Scale(this, 10);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = RailIconPainter.RoundedRect(rect, radius);
        using (var fill = new SolidBrush(Theme.FieldBg)) graphics.FillPath(fill, path);
        // 描边不能太暗：用"线框色"在深色主题里几乎看不见，用户会找不到输入框在哪。
        using var pen = new Pen(focusTarget.Focused ? Theme.Accent : Theme.TextDim);
        graphics.DrawPath(pen, path);
    }
}
