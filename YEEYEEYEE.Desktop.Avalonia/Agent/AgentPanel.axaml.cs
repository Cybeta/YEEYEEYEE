using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// Agent 协作面板（Avalonia 版）。
///
/// 与旧 WinForms 面板的对应关系：对话流、输入区、授权模式、附件、**待审批虚影**都保留，
/// 差别只在一处**刻意的适配**：
/// 反问（ask）不再弹模态框，而是把提问卡片放进对话流里——沉浸式工作台没有 WinForms 那种
/// 独立对话框的位置，放流里也不会因为关掉弹窗而丢掉问题。
/// </summary>
public partial class AgentPanel : UserControl
{
    /// <summary>模型没按协议输出时，用它把要求再讲一遍（与旧端 AgentPane 的这段提示保持一致）。</summary>
    private const string ProtocolRepairInstruction =
        "（系统提示：你上一条回复里的 JSON 块无法解析，界面因此拿不到任何可执行的改动或可选的提问。）" +
        "请按协议重来一次：如果信息不足，只输出一个 ask 块（含 question 与 options）；" +
        "如果用户要求根据剧情自动建立节点，必须输出多个 create_node，并按需要输出 create_edge，不能只输出剧情正文；" +
        "如果需要改动画布，输出 actions 块。字符串内容里的引号必须用中文引号或转义（\\\"），换行写成 \\n，" +
        "不要在正文里重复问句。";

    private static readonly string[] ChangeVerbs =
    {
        "新建", "创建", "加一个", "加个", "添加", "删掉", "删除", "移除", "改一下", "改成", "修改",
        "更新", "连到", "连接", "连一条", "写入", "生成"
    };

    private readonly List<AiChatMessage> history = new();
    private readonly List<AgentAttachment> attachments = new();
    private readonly List<AgentAction> pendingActions = new();
    private readonly StringBuilder streamBuffer = new();
    private readonly DispatcherTimer flushTimer;

    private IAgentSessionHost? host;
    private AgentAuthMode authMode = AgentAuthMode.Ask;
    private CancellationTokenSource? streaming;
    private SelectableTextBlock? streamTarget;
    private Bubble? thinkingTarget;
    private TextBlock? emptyHint;
    private bool busy;
    private bool useContext = true;

    /// <summary>重建模型下拉时置真：程序设值和用户选择走的是同一个 SelectionChanged。</summary>
    private bool refreshingModels;

    /// <summary>本轮服务端回传的用量（没有回传时保持 Empty，界面上显示 —— 而不是 0）。</summary>
    private AiUsage lastUsage = AiUsage.Empty;

    /// <summary>本轮第一个字 / 最后一个字的时刻（ticks，0 表示还没收到）。解码速率只算这一段。</summary>
    private long firstTokenTicks;
    private long lastTokenTicks;

    public AgentPanel()
    {
        InitializeComponent();
        flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        flushTimer.Tick += (_, _) => FlushStream();
        InputBox.KeyDown += InputBox_OnKeyDown;
        AuthModeBox.SelectedIndex = 0;
        ShowEmptyState();
        RefreshHeader();
    }

    // ---------- 对外接口 ----------

    /// <summary>请求把右侧栏切回节点检查器（由工作台处理）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>绑定工作台宿主。可以在窗口构造后再调用。</summary>
    public void Attach(IAgentSessionHost sessionHost)
    {
        host = sessionHost;
        RefreshHeader();
        if (emptyHint is null && MessageList.Children.Count == 0) ShowEmptyState();
    }

    /// <summary>宿主状态变化（换画布、提交、撤销、改了模型配置）后刷新顶部与底部状态。</summary>
    public void SyncHostState() => RefreshHeader();

    public void FocusInput() => InputBox.Focus();

    /// <summary>
    /// 把一段指令填进输入框并聚焦，**不自动发送**：节点右键「Agent 协助」用它把算好的指令递过来。
    /// 不自动发送是有意的——按下发送就要花一次模型调用，先让人看一眼、改一改再发。
    /// </summary>
    public void Prefill(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();
    }

    // ---------- 顶部状态 ----------

    private void RefreshHeader()
    {
        UndoLastButton.IsVisible = host?.CanUndoLastCommit == true;
        PanelStatusText.Text = host is null
            ? "正在初始化…"
            : (host.IsProjectEditable ? string.Empty : "当前项目只读：Agent 可以讨论，但改动写不进画布");
        RefreshModels();
        // 顶部状态刷完就通知一声：宿主靠它同步右下角那枚常驻入口的**厂家徽标**
        // （换模型走的是本控件的下拉，宿主那边不会有人告诉它）。
        HeaderRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>面板顶部状态刷新完成（模型可能刚换过）。宿主据此更新右下角常驻入口的徽标。</summary>
    public event EventHandler? HeaderRefreshed;

    /// <summary>
    /// 重建模型下拉。
    ///
    /// 末项固定是「管理模型…」：候选为空（一份都没启用）时，用户需要的不是一句「未接入」，
    /// 而是"从这儿进去填一份"。把它做成下拉的最后一项，比在别处再放一个入口更不容易被忽略。
    /// </summary>
    private void RefreshModels()
    {
        refreshingModels = true;
        try
        {
            ModelBox.Items.Clear();
            if (host is null)
            {
                ModelBox.IsEnabled = false;
                return;
            }

            var choices = host.ModelChoices();
            if (choices.Count == 0)
            {
                ModelBox.Items.Add("未接入：点右边「管理…」填一份配置");
                ModelBox.SelectedIndex = 0;
                ModelBox.IsEnabled = false;
                ToolTip.SetTip(ModelBox, "还没有启用任何模型配置");
                return;
            }

            ModelBox.IsEnabled = true;
            foreach (var choice in choices)
                ModelBox.Items.Add(choice.IsConfigured ? choice.Label : $"{choice.Label}（未填全）");
            ModelBox.Items.Add("管理模型…");

            var current = choices.ToList().FindIndex(choice => choice.IsCurrent);
            ModelBox.SelectedIndex = current >= 0 ? current : 0;
            ToolTip.SetTip(ModelBox, host.ProviderLabel);
        }
        finally
        {
            refreshingModels = false;
        }
    }

    private async void Model_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingModels || host is null) return;
        var index = ModelBox.SelectedIndex;
        var choices = host.ModelChoices();

        if (index == choices.Count)
        {
            // 选了「管理模型…」：这不是一次换模型的操作，得把显示摆回当前那一份，
            // 否则下拉会一直显示"管理模型…"，看起来像正在用一个叫这名字的模型。
            RefreshModels();
            await OpenModelSettingsAsync();
            return;
        }

        if (index < 0 || index >= choices.Count) return;
        var choice = choices[index];
        if (choice.IsCurrent) return;

        if (host.SelectModel(choice.Id) is { } failure)
        {
            AddSystemNote($"切换模型失败：{failure}", warning: true);
            RefreshModels();
            return;
        }

        AddSystemNote($"已切换到「{choice.Label}」，下一轮对话起用这个模型。");
    }

    private void AuthMode_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        authMode = AuthModeBox.SelectedIndex switch
        {
            1 => AgentAuthMode.AutoStage,
            2 => AgentAuthMode.ReadOnly,
            _ => AgentAuthMode.Ask
        };
        if (host is not null)
            PanelStatusText.Text = authMode switch
            {
                AgentAuthMode.AutoStage => "自动应用：模型提议会直接改画布并保存",
                AgentAuthMode.ReadOnly => "只读：模型只能讨论，提改动会被忽略",
                _ => "逐条审批：改动要先在下方确认才会写入画布"
            };
    }

    private void UseContext_OnChanged(object? sender, RoutedEventArgs e) =>
        useContext = UseContextBox.IsChecked == true;

    private void Close_OnClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void ModelSettings_OnClick(object? sender, RoutedEventArgs e) => await OpenModelSettingsAsync();

    private async Task OpenModelSettingsAsync()
    {
        if (host is null) return;
        // 取消（没保存）时不能报「已更新」，否则用户会以为配置生效了。
        if (!await host.OpenModelSettingsAsync()) return;
        RefreshHeader();
        AddSystemNote("模型配置已更新，下一轮对话生效。");
    }

    /// <summary>重新走接入引导。与首次启动是同一个窗口：随时换一家，不必去翻设置里的每个字段。</summary>
    private async void Onboarding_OnClick(object? sender, RoutedEventArgs e)
    {
        if (host is null) return;
        // 用户什么都没选（「稍后再说」）时保持沉默：引导本身已经把话说清楚了。
        if (!await host.RunOnboardingAsync()) return;
        RefreshHeader();
        AddSystemNote("接入已更新，下一轮对话生效。");
    }

    private void NewConversation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (busy) { AddSystemNote("正在生成中，先点「停止」再开新对话。", warning: true); return; }
        history.Clear();
        pendingActions.Clear();
        ApprovalCard.IsVisible = false;
        // 新对话时把上一批的虚影也收掉：否则画布上会留着没人认领的「待审批」提示。
        host?.ClearPendingPreview();
        MessageList.Children.Clear();
        emptyHint = null;
        ShowEmptyState();
        PanelStatusText.Text = "已开新对话（历史与画布改动都已从上下文移除）";
    }

    private async void UndoLast_OnClick(object? sender, RoutedEventArgs e)
    {
        if (host is null || busy) return;
        var failure = host.UndoLastCommit();
        if (failure is null)
        {
            AddSystemNote("已撤销上一批 Agent 改动，画布与文件都回滚到提交之前。");
            RefreshHeader();
        }
        else
        {
            AddSystemNote($"撤销未完成：{failure}", warning: true);
        }
        await Task.CompletedTask;
    }

    // ---------- 发送 ----------

    private async void Send_OnClick(object? sender, RoutedEventArgs e)
    {
        // 同一个按钮两种意思：正在跑的时候它是「停止」。
        if (busy) { StopGeneration(); return; }
        await SendAsync(null);
    }

    /// <summary>把发送按钮在「发送」与「停止」之间切换。跑着的时候按钮仍然可点——那正是停止。</summary>
    private void SetBusy(bool value)
    {
        busy = value;
        SendButton.Content = value ? "停止" : "发送";
        SendButton.IsEnabled = true;
        ToolTip.SetTip(SendButton, value ? "停下这一轮（半截回复不会写进对话历史）" : "发送（Enter；Shift+Enter 换行）");
    }

    private void StopGeneration()
    {
        if (streaming is null) return;
        PanelStatusText.Text = "正在取消…";
        streaming.Cancel();
    }

    private async void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        await SendAsync(null);
    }

    private async Task SendAsync(string? preset)
    {
        if (busy || host is null) return;
        var text = (preset ?? InputBox.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        if (host.CreateProvider() is not { } provider)
        {
            AddSystemNote("还没有接入大模型，无法对话。点右上角 ★ 走接入引导（选服务商 / 填密钥 / 测试连接），或点 ⚙ 细调 Endpoint、Model 与 ApiKey。", warning: true);
            return;
        }

        if (preset is null) InputBox.Text = string.Empty;
        ClearEmptyState();

        // 内置技能是代码里的静态表，用户停用后只能记在配置的名单里；这里必须把名单带上，
        // 否则设置页里的「停用」对运行时没有任何影响。
        var disabledSkills = AiProviderSettings.Load().DisabledBuiltInSkills;
        var skill = BuiltInSkills.Resolve(text, disabledSkills);
        if (skill is not null) AddSystemNote($"正在调用技能：{skill.Name}");
        AddBubble("你", Brush("DfPrimary"), isUser: true).Body.Text = text;

        // 文本附件拼进正文、图片随消息发送；对话区仍只显示你自己打的字。
        history.Add(new AiChatMessage
        {
            Role = "user",
            Content = AgentAttachmentLoader.ComposeContent(text, attachments),
            Images = AgentAttachmentLoader.CollectImages(attachments)
        });
        if (attachments.Count > 0)
        {
            AddSystemNote($"本轮随消息送出 {attachments.Count} 个附件。");
            attachments.Clear();
            RefreshAttachmentRow();
        }

        await RunTurnWithAsksAsync(provider, text);
    }

    /// <summary>
    /// 跑一轮；模型用 ask 反问时把提问卡片放进对话流并结束本轮，用户回答后再起一轮
    /// （最多让它追问 3 次，避免被反复追问拖住）。
    /// </summary>
    private async Task RunTurnWithAsksAsync(IAiChatProvider provider, string request)
    {
        var repaired = false;
        while (true)
        {
            var turn = await RunTurnAsync(provider);
            if (turn.Ask is not null)
            {
                AddAskCard(turn.Ask);
                return;
            }

            if (repaired) return;
            // 既没给改动块、也没提问，但用户明显在要求改动：多半是把该问的事写成了正文。
            // 自动请它按协议重来一次，而不是让用户自己再问一遍（只补一次）。
            if (!turn.ProtocolBroken && (turn.ProducedProposal || !LooksLikeChangeRequest(request))) return;
            repaired = true;
            AddSystemNote(turn.ProtocolBroken
                ? "模型这轮的改动块不是合法 JSON（多半是内容里的引号没转义），已自动请它按协议重来一次。"
                : "模型刚才没有按协议给出结构化结果（既没有改动块、也没有提问）。已自动请它按协议重来一次。",
                warning: true);
            history.Add(new AiChatMessage { Role = "user", Content = ProtocolRepairInstruction });
        }
    }

    private static bool LooksLikeChangeRequest(string text) =>
        ChangeVerbs.Any(verb => text.Contains(verb, StringComparison.Ordinal));

    // ---------- 一轮请求 ----------

    private readonly record struct TurnResult(AgentAsk? Ask, bool ProducedProposal, bool ProtocolBroken);

    private async Task<TurnResult> RunTurnAsync(IAiChatProvider provider)
    {
        SetBusy(true);
        // 用量是「这一轮」的：新一轮开始就把上一轮的数字清掉，免得新的一轮还挂着旧数据。
        lastUsage = AiUsage.Empty;
        firstTokenTicks = 0;
        lastTokenTicks = 0;
        UpdateUsageText();
        PanelStatusText.Text = "正在连接…（同一个按钮会变成「停止」）";
        var bubble = AddBubble("Agent", Brush("DfPrimary"), isUser: false);
        streamTarget = bubble.Body;
        thinkingTarget = bubble;
        streamBuffer.Clear();
        streaming = new CancellationTokenSource();
        var sink = BuildStreamSink();

        try
        {
            var reply = await provider.ChatStreamAsync(BuildRequestMessages(), sink, streaming.Token);
            FlushStream();
            var parsed = AgentActionParser.Parse(reply);
            // 兜底：本地兜底 Provider（以及任何「同步返回」的实现）会在同一线程里一次性把正文交出来，
            // 增量回调可能来不及落屏。正文已经拿到就必须显示出来，不能留一个空气泡。
            if (string.IsNullOrWhiteSpace(bubble.Body.Text))
            {
                var visible = VisibleBody(reply);
                if (!string.IsNullOrWhiteSpace(visible)) bubble.Body.Text = visible;
            }
            history.Add(new AiChatMessage { Role = "assistant", Content = parsed.Text });
            HandleProposedActions(parsed);
            return new TurnResult(parsed.Ask, parsed.Actions.Count > 0 || parsed.Ask is not null, parsed.ProtocolBroken);
        }
        catch (OperationCanceledException)
        {
            // 半截回复不进历史：它不是模型的完整回答，留着会污染后续上下文。
            AddSystemNote("已停止生成。以下内容不完整，未写入对话历史。", warning: true);
            return new TurnResult(null, true, false);
        }
        catch (Exception error)
        {
            AddErrorMessage(error);
            return new TurnResult(null, true, false);
        }
        finally
        {
            FlushStream();
            flushTimer.Stop();
            streaming?.Dispose();
            streaming = null;
            streamTarget = null;
            thinkingTarget = null;
            SetBusy(false);
            UpdateUsageText();
            PanelStatusText.Text = string.Empty;
            RefreshHeader();
            ScrollToEnd();
        }
    }

    /// <summary>正文里模型的操作块是给程序的，展示时一律截到协议块之前。</summary>
    private static string VisibleBody(string reply)
    {
        var blockStart = AgentActionParser.FindProtocolBlockStart(reply);
        return blockStart >= 0 ? reply[..blockStart] : reply;
    }

    /// <summary>
    /// 流式回调：正文只显示到协议块之前（操作块是给程序的，不该糊在用户脸上），
    /// 思考走独立块（折叠入口 + 展开读全文）且正文一旦开始就不再收。
    /// </summary>
    private AiStreamSink BuildStreamSink()
    {
        var raw = new StringBuilder();
        var displayed = 0;
        var hidden = false;
        var bodyStarted = false;
        var thinkingSealed = false;

        return new AiStreamSink(
            OnText: delta =>
            {
                // 本回调在后台线程：raw/displayed/hidden 只在这里读写，界面部分一律 Post 回 UI 线程，
                // 免得 streamBuffer 变成跨线程共享状态。
                // 记下第一个字与最后一个字的时刻：解码速率只算这一段（整轮墙钟会把排队与网络等待算进去）。
                var now = DateTimeOffset.UtcNow.Ticks;
                if (firstTokenTicks == 0) firstTokenTicks = now;
                lastTokenTicks = now;
                bodyStarted = true;
                thinkingSealed = true;
                raw.Append(delta);
                if (hidden) return;
                var all = raw.ToString();
                var blockStart = AgentActionParser.FindProtocolBlockStart(all);
                var visibleEnd = blockStart >= 0 ? blockStart : all.Length;
                if (visibleEnd <= displayed) return;
                var chunk = all[displayed..visibleEnd];
                displayed = visibleEnd;
                if (visibleEnd < all.Length) hidden = true;
                // 已经在 UI 线程（同步返回的 Provider）就直接入队，否则 Post 回来。
                // 必须这样分流：同步 Provider 的增量如果也走 Post，会在本轮 finally 之后才执行，
                // 文本就落在空气泡里、甚至串到下一轮。
                if (Dispatcher.UIThread.CheckAccess()) PushChunk(chunk);
                else Dispatcher.UIThread.Post(() => PushChunk(chunk));
            },
            OnThinking: delta =>
            {
                if (thinkingSealed || bodyStarted) return;
                // 思考只进折叠块：不拼正文、不进对话历史，界面上一行入口按字数更新。
                Dispatcher.UIThread.Post(() =>
                {
                    if (thinkingTarget is null) return;
                    thinkingTarget.AppendThinking(delta);
                    ScrollToEnd();
                });
            },
            // 用量可能出现在任意一条事件里（末尾那条通常是纯用量），每次都刷新那一行。
            OnUsage: usage => Dispatcher.UIThread.Post(() =>
            {
                lastUsage = usage;
                UpdateUsageText();
            }));
    }

    /// <summary>
    /// 把上一轮的用量写到输入框下面那一行。
    ///
    /// 三条口径都要写准，否则就是假数据：
    /// · **解码速率**用的是「第一个字到最后一个字」的时间，不是整轮墙钟——后者把排队与网络等待都算进去，
    ///   会低得离谱；标签写 tok/s 而实际算的是端到端速度，比不显示更误导。
    /// · **缓存命中率**取不到命中 / 未命中这两项的服务商显示 ——，不显示 0（0 会被读成"一次都没命中"）。
    /// · **上下文用量**用服务端回传的真实 prompt token，而不是按字符估算的那个数（估算那个继续用于发送前截断）。
    /// </summary>
    private void UpdateUsageText()
    {
        if (host is null) { UsageText.Text = string.Empty; return; }
        var usage = lastUsage;
        var parts = new List<string>();
        var details = new List<string>();

        var input = usage.InputTokens;
        if (input > 0 || usage.CompletionTokens > 0)
        {
            parts.Add($"↑{Short(input)} ↓{Short(usage.CompletionTokens)}");
            details.Add($"输入 {input:N0} token；其中缓存命中 {usage.CacheHitTokens:N0}、未命中 {usage.CacheMissTokens:N0}");
            details.Add($"输出 {usage.CompletionTokens:N0} token");
        }

        var cacheTotal = usage.CacheHitTokens + usage.CacheMissTokens;
        if (cacheTotal > 0)
        {
            var rate = (double)usage.CacheHitTokens / cacheTotal;
            parts.Add($"缓存 {rate:P0}");
            details.Add($"缓存命中率 {rate:P1}（命中的部分按服务商的规则计价，通常便宜得多）");
        }

        var window = host.ContextWindowTokens;
        if (window > 0 && input > 0)
        {
            parts.Add($"上下文 {Short(input)}/{Short(window)}");
            details.Add($"本次请求占上下文窗口 {input * 100.0 / window:0.#}%");
        }

        if (usage.CompletionTokens > 0 && firstTokenTicks > 0 && lastTokenTicks > firstTokenTicks)
        {
            var seconds = (lastTokenTicks - firstTokenTicks) / (double)TimeSpan.TicksPerSecond;
            if (seconds >= 0.05)
            {
                var rate = usage.CompletionTokens / seconds;
                parts.Add($"{rate:0} tok/s");
                details.Add($"解码速率 {rate:0.0} tok/s（只算第一个字到最后一个字之间的 {seconds:0.0} 秒，不含排队与网络等待）");
            }
        }

        UsageText.Text = parts.Count == 0
            ? "用量：—（这一家接口没有回传 token 用量）"
            : string.Join("  ·  ", parts);
        ToolTip.SetTip(UsageText, details.Count == 0
            ? "这一家接口没有回传 token 用量，所以看不到速率与命中率——不是 0，是没有。"
            : string.Join("\n", details));
    }

    /// <summary>大数字缩写着看：12.3K / 1.2M。</summary>
    private static string Short(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
        >= 1_000 => $"{value / 1_000.0:0.#}K",
        _ => value.ToString()
    };

    /// <summary>把一段正文增量排进显示缓冲，并启动节流刷新。</summary>
    private void PushChunk(string chunk)
    {
        // 本轮已结束：迟到的增量直接丢弃，避免串到下一轮的气泡里。
        if (streamTarget is null) return;
        streamBuffer.Append(chunk);
        if (!flushTimer.IsEnabled) flushTimer.Start();
    }

    private void FlushStream()
    {
        if (streamTarget is null)
        {
            flushTimer.Stop();
            return;
        }

        if (streamBuffer.Length > 0)
        {
            streamTarget.Text += streamBuffer.ToString();
            streamBuffer.Clear();
        }
        ScrollToEnd();
    }

    /// <summary>组装请求：可选的上下文放在最前，其后是历史轮次。协议块永远保留。</summary>
    private List<AiChatMessage> BuildRequestMessages()
    {
        var messages = new List<AiChatMessage>();
        var hasContextProtocol = false;
        if (useContext && host?.BuildContext() is { } context && !context.IsEmpty)
        {
            messages.Add(new AiChatMessage { Role = "system", Content = context.Describe(host.ContextCharacterBudget) });
            hasContextProtocol = true;
        }
        if (!hasContextProtocol)
            messages.Add(new AiChatMessage { Role = "system", Content = AgentContext.ActionProtocol });
        messages.AddRange(history);
        return messages;
    }

    // ---------- 提议落地 ----------

    private void HandleProposedActions(AgentReply parsed)
    {
        var proposed = parsed.Actions;
        if (proposed.Count == 0)
        {
            if (parsed.Ask is null)
                AddSystemNote(parsed.ProtocolBroken
                    ? "模型这轮的改动块不是合法 JSON，没能解析（改动没有生效）。"
                    : "本轮没有改动提议（模型只回答了文字）。要它改画布时，把要求说具体一点，或明确说「自动建立节点」。");
            return;
        }

        if (authMode == AgentAuthMode.ReadOnly)
        {
            AddSystemNote($"授权模式是「只读不提议」：已忽略模型提出的 {proposed.Count} 条改动。", warning: true);
            return;
        }

        if (authMode == AgentAuthMode.AutoStage && host is not null)
        {
            var report = host.Commit(proposed);
            if (report.Succeeded)
            {
                AddSystemNote($"已将 {report.Applied} 条改动应用到画布并保存{report.PartialNote}");
                host.RefreshCanvasSurface();
                RefreshHeader();
            }
            else
            {
                AddErrorMessage(new InvalidOperationException($"自动应用未完成：{report.Failure}"));
            }
            return;
        }

        ShowApproval(proposed);
    }

    /// <summary>逐条审批：把试算差异与后果提示摆出来，用户点保存才真正执行。</summary>
    private void ShowApproval(IReadOnlyList<AgentAction> actions)
    {
        pendingActions.Clear();
        pendingActions.AddRange(actions);

        ApprovalTitle.Text = $"待审批改动 · {actions.Count} 条";
        ApprovalLines.Children.Clear();

        // 待审批的改动**直接画到画布上**（半透明虚影 + 虚线框）：比在卡片里读数字直观得多，
        // 审批后虚影换成真实节点，丢弃则虚影消失。评审与舍弃都发生在真实画布上，不用脑补。
        host?.ShowPendingPreview(actions);

        if (host is not null)
        {
            var preview = host.Preview(actions);
            var parts = new List<string>();
            if (preview.AddedNodes.Count > 0) parts.Add($"新增节点 {preview.AddedNodes.Count}");
            if (preview.UpdatedNodeIds.Count > 0) parts.Add($"修改节点 {preview.UpdatedNodeIds.Count}");
            if (preview.RemovedNodeIds.Count > 0) parts.Add($"删除节点 {preview.RemovedNodeIds.Count}");
            if (preview.AddedEdges.Count > 0) parts.Add($"新增连线 {preview.AddedEdges.Count}");
            if (preview.RemovedEdgeIds.Count > 0) parts.Add($"删除连线 {preview.RemovedEdgeIds.Count}");
            ApprovalDiff.Text = parts.Count == 0
                ? "试算结果：这批动作不会改动画布节点（可能只写文件或只动设定库）。"
                : "试算结果：" + string.Join(" · ", parts) + "（画布上是虚影，尚未写入）";

            // 整批一起预检：同一批里前面建好的节点，后面的连线才能被正确识别。
            var hints = host.Precheck(actions);
            for (var index = 0; index < actions.Count && index < 10; index++)
            {
                var hint = index < hints.Count ? hints[index] : null;
                ApprovalLines.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(hint) ? "· " + actions[index].Describe() : $"⚠ {actions[index].Describe()}：{hint}",
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = string.IsNullOrWhiteSpace(hint) ? Brush("DfInk3") : Brush("DfWarning")
                });
            }
            if (actions.Count > 10)
                ApprovalLines.Children.Add(new TextBlock { Text = $"…另有 {actions.Count - 10} 条（保存时一并执行）", FontSize = 10, Foreground = Brush("DfInk3") });
        }

        ApprovalCard.IsVisible = true;
        AddSystemNote($"模型给出 {actions.Count} 条改动，画布上已用虚影标出。确认无误后点下方「保存并写入画布」。");
        ScrollToEnd();
    }

    private async void Approve_OnClick(object? sender, RoutedEventArgs e)
    {
        if (host is null || pendingActions.Count == 0) return;
        var count = pendingActions.Count;
        var report = host.Commit(pendingActions);
        if (report.Succeeded)
        {
            AddSystemNote($"已保存 {report.Applied} 条改动到画布{report.PartialNote}");
            pendingActions.Clear();
            ApprovalCard.IsVisible = false;
            // 虚影换成真实节点：先清虚影再重绘画布，避免两套东西同时出现。
            host.ClearPendingPreview();
            host.RefreshCanvasSurface();
            RefreshHeader();
        }
        else
        {
            AddErrorMessage(new InvalidOperationException($"保存未完成：{report.Failure}（这批 {count} 条改动没有写入画布）"));
        }
        await Task.CompletedTask;
    }

    private void Discard_OnClick(object? sender, RoutedEventArgs e)
    {
        var count = pendingActions.Count;
        pendingActions.Clear();
        ApprovalCard.IsVisible = false;
        // 丢弃 = 虚影消失，画布一个字节都不变。
        host?.ClearPendingPreview();
        AddSystemNote($"已放弃这 {count} 条改动，画布保持原样。");
    }

    // ---------- 附件 ----------

    private async void Attach_OnClick(object? sender, RoutedEventArgs e)
    {
        if (host is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要带给 Agent 的图片或文本文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif" } },
                new FilePickerFileType("文本") { Patterns = new[] { "*.txt", "*.md", "*.json", "*.csv", "*.log" } },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (attachments.Any(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                AddSystemNote($"{file.Name} 已经在附件里了，本次跳过。", warning: true);
                continue;
            }

            var result = AgentAttachmentLoader.Load(path);
            if (result.Attachment is null)
            {
                AddSystemNote($"附件未加入：{file.Name} —— {result.Error}", warning: true);
                continue;
            }
            // 图片要走模型的多模态通道，模型不支持时先拦住，别等接口报错。
            if (result.Attachment.Kind == AgentAttachmentKind.Image && !host.ImageInputEnabled)
            {
                AddSystemNote($"{file.Name} 是图片，但当前模型未开启图片输入，已跳过（可在 ⚙ 里打开）。", warning: true);
                continue;
            }
            attachments.Add(result.Attachment);
        }

        RefreshAttachmentRow();
    }

    private void ClearAttach_OnClick(object? sender, RoutedEventArgs e)
    {
        attachments.Clear();
        RefreshAttachmentRow();
    }

    private void RefreshAttachmentRow()
    {
        AttachmentChips.Children.Clear();
        foreach (var attachment in attachments)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1),
                Background = Brush("DfSurface3"),
                BorderBrush = Brush("DfLine"),
                Child = new TextBlock
                {
                    Text = $"{attachment.Name} · {attachment.Describe()}",
                    FontSize = 10,
                    Foreground = Brush("DfInk2")
                }
            };
            AttachmentChips.Children.Add(chip);
        }
        AttachmentChips.IsVisible = attachments.Count > 0;
        ClearAttachButton.IsVisible = attachments.Count > 0;
        if (attachments.Count > 0) ClearEmptyState();
    }

    // ---------- 对话区渲染 ----------

    /// <summary>
    /// 对话区里的一条消息。正文与思考分开：**思考折成一行入口**（「▸ 思考过程（N 字）」），
    /// 点开就地展开全文——思考不属于回答，默认不该占版面（与旧端 ChatView 的形态一致）。
    /// 折叠时额外露出**最新两行**，并随流式输出一直往后走：不用展开也知道它此刻在想什么。
    /// </summary>
    private sealed class Bubble
    {
        /// <summary>折叠态预览的兜底字数：思考常常是一整段没有换行，只能按字数截尾部。</summary>
        private const int PreviewFallbackChars = 64;

        private readonly StringBuilder thinking = new();

        public required Border Root { get; init; }
        public required SelectableTextBlock Body { get; init; }
        public required Button ThinkingToggle { get; init; }
        public required TextBlock ThinkingHead { get; init; }
        public required TextBlock ThinkingPreview { get; init; }
        public required SelectableTextBlock ThinkingText { get; init; }
        public required Border ThinkingBox { get; init; }

        private bool expanded;

        /// <summary>展开时读到全文；折叠时留字数 + 最新两行，让人知道「它想过、在想什么」。</summary>
        public void ToggleThinking()
        {
            expanded = !expanded;
            Apply();
        }

        /// <summary>思考增量：只追加到思考块，不混进正文。</summary>
        public void AppendThinking(string delta)
        {
            if (delta.Length == 0) return;
            thinking.Append(delta);
            Apply();
        }

        private void Apply()
        {
            var hasThinking = thinking.Length > 0;
            ThinkingToggle.IsVisible = hasThinking;
            if (!hasThinking)
            {
                ThinkingBox.IsVisible = false;
                ThinkingPreview.IsVisible = false;
                return;
            }

            ThinkingHead.Text = $"{(expanded ? "▾" : "▸")} 思考过程（{thinking.Length} 字）";
            ToolTip.SetTip(ThinkingToggle, expanded ? "收起思考过程" : "展开思考过程");
            ThinkingText.Text = thinking.ToString();
            ThinkingBox.IsVisible = expanded;
            // 折叠时才给「最新两行」：展开着就能读到全文，再挂一份预览是重复。
            ThinkingPreview.Text = expanded ? string.Empty : LatestLines(2);
            ThinkingPreview.IsVisible = !expanded;
        }

        /// <summary>
        /// 取思考末尾的两行。有换行就按换行取最后两行；思考常常是一整段没有换行，
        /// 那就按字数截尾部，交给 TextBlock 自己折行（并在两行处夹住）。
        /// </summary>
        private string LatestLines(int count)
        {
            var text = thinking.ToString();
            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= count) return string.Join('\n', parts[^count..]);

            var single = text.Trim();
            if (single.Length == 0) return string.Empty;
            return single.Length <= PreviewFallbackChars ? single : single[^PreviewFallbackChars..];
        }
    }

    private Bubble AddBubble(string author, IBrush accent, bool isUser)
    {
        var body = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 19,
            Foreground = Brush("DfInk")
        };

        // 思考区：一行入口 + 一个可展开的等宽小字盒子。入口默认不可见（没有思考就不显示）。
        var thinkingText = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            LineHeight = 17,
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            Foreground = Brush("DfInk3")
        };
        var thinkingBox = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("DfLine"),
            Background = Brush("DfSurface3"),
            Padding = new Thickness(9, 7),
            Child = thinkingText
        };
        var thinkingToggle = new Button
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        thinkingToggle.Classes.Add("miniButton");

        // 入口本身就是一次点击区：第一行是标题，折叠时下面再挂「最新两行」——
        // 两行会随流式输出一直往后走，所以不用展开也能跟着看它此刻在想什么。
        // 这里刻意用 TextBlock 而不是 SelectableTextBlock：可选文本会抢走指针事件，点入口就不灵了。
        var thinkingHead = new TextBlock { FontSize = 10 };
        var thinkingPreview = new TextBlock
        {
            IsVisible = false,
            FontSize = 10,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Brush("DfInk3")
        };
        var thinkingToggleContent = new StackPanel { Spacing = 0 };
        thinkingToggleContent.Children.Add(thinkingHead);
        thinkingToggleContent.Children.Add(thinkingPreview);
        thinkingToggle.Content = thinkingToggleContent;

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = author, FontSize = 10, Foreground = accent });
        content.Children.Add(thinkingToggle);
        content.Children.Add(thinkingBox);
        content.Children.Add(body);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 9),
            BorderThickness = new Thickness(1),
            Background = isUser ? Brush("DfPrimarySoft") : Brush("DfSurface2"),
            BorderBrush = isUser ? Brush("DfLineGlow") : Brush("DfLine"),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = content
        };
        MessageList.Children.Add(bubble);
        ScrollToEnd();

        var result = new Bubble
        {
            Root = bubble,
            Body = body,
            ThinkingToggle = thinkingToggle,
            ThinkingHead = thinkingHead,
            ThinkingPreview = thinkingPreview,
            ThinkingText = thinkingText,
            ThinkingBox = thinkingBox
        };
        thinkingToggle.Click += (_, _) =>
        {
            result.ToggleThinking();
            ScrollToEnd();
        };
        return result;
    }

    private void AddSystemNote(string text, bool warning = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = warning ? Brush("DfWarning") : Brush("DfInk3")
        };
        MessageList.Children.Add(block);
        ScrollToEnd();
    }

    private void AddErrorMessage(Exception error)
    {
        MessageList.Children.Add(new SelectableTextBlock
        {
            Text = "错误：" + error.Message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("DfError")
        });
        ScrollToEnd();
    }

    /// <summary>反问卡片：选项按钮 + 自由输入，答完就禁用，避免同一轮被回答两次。</summary>
    private void AddAskCard(AgentAsk ask)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "需要你补充信息",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("DfInk")
        });
        body.Children.Add(new SelectableTextBlock
        {
            Text = ask.Question,
            FontSize = 11,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("DfInk2")
        });

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11),
            BorderThickness = new Thickness(1),
            Background = Brush("DfPrimarySoft"),
            BorderBrush = Brush("DfLineGlow"),
            Child = body
        };

        if (ask.Options.Count > 0)
        {
            var options = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var option in ask.Options)
            {
                var button = new Button { Content = option, Margin = new Thickness(0, 0, 6, 4) };
                button.Classes.Add("miniButton");
                var answer = option;
                button.Click += async (_, _) =>
                {
                    card.IsEnabled = false;
                    card.Opacity = 0.6;
                    await SendAsync(answer);
                };
                options.Children.Add(button);
            }
            body.Children.Add(options);
        }

        var free = new TextBox
        {
            Watermark = "也可以在这里直接回答",
            FontSize = 11,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        var send = new Button { Content = "回答", Margin = new Thickness(6, 0, 0, 0) };
        send.Classes.Add("primary");
        send.Click += async (_, _) =>
        {
            var answer = (free.Text ?? string.Empty).Trim();
            if (answer.Length == 0) return;
            card.IsEnabled = false;
            card.Opacity = 0.6;
            await SendAsync(answer);
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(send, 1);
        row.Children.Add(free);
        row.Children.Add(send);
        body.Children.Add(row);

        MessageList.Children.Add(card);
        ScrollToEnd();
    }

    private void ShowEmptyState()
    {
        emptyHint = new TextBlock
        {
            Text = "和 Agent 讨论剧情，或直接让它改画布。\n例如：「按这段剧情自动建立第一章的节点」。",
            FontSize = 11,
            LineHeight = 19,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brush("DfInk3"),
            Margin = new Thickness(0, 24, 0, 0)
        };
        MessageList.Children.Add(emptyHint);
    }

    private void ClearEmptyState()
    {
        if (emptyHint is null) return;
        MessageList.Children.Remove(emptyHint);
        emptyHint = null;
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => ConversationScroll.ScrollToEnd());

    private static IBrush Brush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}
