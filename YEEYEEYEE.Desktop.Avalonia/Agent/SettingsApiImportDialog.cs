using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 智能导入（设置 → 生图与生视频 页顶端那个按钮）。
///
/// 一条链路走完：给一个接口说明网页 → 抓正文 → 解析出接口与可用模型 → 建出 api 生图 / api 生视频技能
/// → 写密钥与地址模型（加密落盘）→ 问一次要不要做最小测试 → 顺手查一次账号余额与上线模型。
///
/// 与旧 WinForms 端 <c>ApiImportWizardDialog</c> 用的是**同一套内核**（<see cref="ApiDocFetcher"/> /
/// <see cref="ApiDocAnalyzer"/> / <see cref="ApiSkillFactory"/> / <see cref="ApiMinimalTest"/> /
/// <see cref="ApiAccountProbe"/> / <see cref="ApiDocRepair"/> 全部来自共享层），
/// 界面重写、结论口径一字不改——同一份文档在两端必须得到同一个结果。
///
/// 三条边界，与旧端一致：
/// · **每一步都如实报出**：抓不到页面、没解析出接口、写不进配置、测试失败，都显示真实原因，不显示成功；
/// · **不伪造产物**：最小测试真的调一次接口；出视频链路没配好时明说，不拿别的文件冒充视频；
/// · **只写该写的**：密钥加密落盘，界面绝不回显完整密钥。
///
/// 一处**刻意的改进**：密钥写进图像 / 视频各自的字段（<see cref="AiProviderConfig.ImageApiKey"/> /
/// <see cref="AiProviderConfig.VideoApiKey"/>），而不是像旧端那样一律写进顶层 <c>ApiKey</c>。
/// 旧端那个做法会让「导入一个出图接口」顺手把聊天模型的密钥覆盖掉——而出图与聊天常常不是同一个账号。
/// </summary>
internal sealed class SettingsApiImportDialog
{
    /// <summary>四步流程的阶段。与旧端同名同序，方便对照着读两份实现。</summary>
    private enum ApiWizardStep { Document, Key, Test, Done }

    private readonly AiProviderConfig config;
    private readonly IAiJsonCompleter? jsonCompleter;
    private readonly Func<string, CancellationToken, Task<ApiDocFetchResult>> fetcher;
    private readonly Func<string, string, CancellationToken, Task<ApiAccountSnapshot>> accountProbe;
    private readonly Func<IImageProvider> imageProviderFactory;
    private readonly Func<IVideoProvider> videoProviderFactory;

    private Window? window;

    /// <summary>正在按我们自己指定的结论关闭：用来区分「第一次关闭」与接管后那次真正的 Close。</summary>
    private bool closing;

    /// <summary>最近一次解析用的原始正文（大模型修正要用它）。</summary>
    private string lastContent = string.Empty;
    private ApiDocReport? lastReport;

    /// <summary>待登记的站点（跟着解析与探测一起长出来）；产物是它，不是一串技能文件。</summary>
    private SiteProfile? site;

    /// <summary>识别成 ComfyUI 时的结论（非空表示走的是 ComfyUI 那条分支）。</summary>
    private ProviderImportDraft? comfyDraft;

    /// <summary>刚刚登记下来的 ComfyUI 站点（结论里要报它拉到了多少份工作流）。</summary>
    private SiteProfile? installedComfySite;

    /// <summary>正在跑一件不能重入的慢事（拉清单 / 落站点）：期间把按钮按住，免得点两遍拉两次。</summary>
    private bool busy;
    private bool probing;
    private ApiAccountSnapshot? lastAccount;
    private long? lastTestCost;
    private bool wroteSite;
    private bool savedConfig;
    private int deletedWorkflowCount;
    private string returnedMediaPath = string.Empty;

    // ---------- 控件 ----------

    private readonly TextBlock stepLabel = new();
    private readonly TextBlock stepHint = new();
    private readonly TextBlock balanceText = new() { FontSize = 10, Foreground = Brush("DfInk2"), TextWrapping = TextWrapping.Wrap };
    private readonly Button balanceButton = Secondary(string.Empty);
    private readonly TextBox urlBox = new()
    {
        FontSize = 12,
        Background = Brush("DfSurface2"),
        Foreground = Brush("DfInk"),
        Watermark = "https://服务商文档地址……或者 http://127.0.0.1:8188 这样的 ComfyUI 地址"
    };
    private readonly Button fetchButton = Secondary("读取网页");
    private readonly TextBox pasteBox = new()
    {
        FontSize = 12,
        Background = Brush("DfSurface2"),
        Foreground = Brush("DfInk"),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 92,
        Watermark = "例如：\nPOST /v1/images/generations\nmodel: flux-1-dev\n可填 1k / 2k 两种画幅\nAuthorization: Bearer sk-xxxxxxxx"
    };
    private readonly Button analyzeButton = Secondary("分析这段文字");
    private readonly Button repairButton = Secondary("让大模型分析");
    private readonly Button createButton = Secondary("登记站点");

    /// <summary>
    /// 「这是 ComfyUI」：类型没判出来时的**人来定**的出口。
    ///
    /// 为什么要留它：识别器只看文字，而用户手上常常只有一个地址；探测也可能失败
    /// （地址暂时不通、在内网、有鉴权）。这时候识别器的提醒是「请手动选择类型」——
    /// 那句话得有地方能选，否则就是把人卡在死路上。
    /// </summary>
    private readonly Button forceComfyButton = Secondary("这是 ComfyUI");
    private readonly SelectableTextBlock reportText = CodeText();
    private readonly StackPanel keySection = new() { Spacing = 7, IsVisible = false };
    private readonly TextBox keyBox = new()
    {
        FontSize = 12,
        PasswordChar = '●',
        Background = Brush("DfSurface2"),
        Foreground = Brush("DfInk"),
        Watermark = "粘贴服务商给的 API Key；留空表示沿用已保存的密钥"
    };
    private readonly TextBlock keyHint = Note(string.Empty);
    private readonly Button saveKeyButton = Secondary("保存密钥");
    private readonly StackPanel testSection = new() { Spacing = 7, IsVisible = false };
    private readonly TextBlock questionLabel = Note(string.Empty);
    private readonly Button testYesButton = Secondary("是，做最小测试");
    private readonly Button testNoButton = Secondary("否，直接保存完成");
    private readonly Button rebuildButton = Secondary("重新探测池子");
    private readonly SelectableTextBlock statusText = CodeText();
    private readonly Image previewImage = new() { Stretch = Stretch.Uniform, MaxHeight = 190, IsVisible = false };
    private readonly Button openFileButton = Secondary("打开产出文件");

    /// <summary>
    /// 状态区那一格。留一个引用是为了**点亮它**：读取网页回来之后结论就写在下面那一格里，
    /// 而它在窗口下半截、要滚动才看得到，不给个视觉指引用户常常以为「点了没反应」。
    /// </summary>
    private Border? statusBox;

    /// <summary>「读取网页」的呼吸提示：地址填了、还没点过的时候一闪一闪。</summary>
    private AttentionPulse? fetchPulse;

    /// <summary>下一步那颗按钮（登记站点 / 拉取工作流并登记站点）的呼吸提示。</summary>
    private AttentionPulse? nextStepPulse;

    /// <summary>用户已经点过「读取网页」：同一个提示重复闪只会变成噪音，所以点了就不再闪。</summary>
    private bool fetchPrompted;

    private SettingsApiImportDialog(AiProviderConfig config)
    {
        this.config = config;
        jsonCompleter = AiProviderFactory.CreateJsonCompleter();
        fetcher = (url, token) => ApiDocFetcher.FetchAsync(url, null, token);
        accountProbe = (url, key, token) => ApiAccountProbe.FetchAsync(url, key, null, token);
        imageProviderFactory = () => new OpenAiCompatibleImageProvider(config);
        videoProviderFactory = () => VideoProviderFactory.Create(config);
    }

    /// <summary>
    /// 打开导入窗口并等它结束。返回给外壳回报的一行结论；返回 null 表示**什么都没写**（用户直接关掉了）。
    /// </summary>
    public static async Task<string?> ShowAsync(Window owner, AiProviderConfig config)
    {
        var dialog = new SettingsApiImportDialog(config);
        var window = DialogShell.Create("智能导入：接口说明网页，或 ComfyUI 地址", dialog.Build(), 900, 640);
        dialog.window = window;
        dialog.Wire(window);
        return await window.ShowDialog<string?>(owner);
    }

    // ---------- 组装 ----------

    private void Wire(Window window)
    {
        fetchButton.Click += async (_, _) =>
        {
            // 先记住「他照做了」再停止提示：同一个指引闪第二次就是噪音。
            fetchPrompted = true;
            SyncFetchPulse();
            await AnalyzeUrlAsync(urlBox.Text);
        };
        analyzeButton.Click += (_, _) =>
        {
            AnalyzeContent(pasteBox.Text, urlBox.Text);
            // 粘贴正文这条入口走完之后，结论同样要能被看见、下一步同样要指出来。
            MarkStatusImportant();
        };
        repairButton.Click += async (_, _) => await RepairWithModelAsync();
        forceComfyButton.Click += (_, _) =>
        {
            ForceComfyUi();
            MarkStatusImportant();
        };
        createButton.Click += async (_, _) =>
        {
            // 用户已经走向下一步：闪烁与点亮的使命结束，收回去。
            nextStepPulse?.Stop();
            HighlightStatus(false);
            // 同一个按钮两种意思：ComfyUI 那条分支拉工作流 + 登记站点，接口站那条分支登记站点。
            if (comfyDraft is not null) { await InstallComfyUiAsync(); return; }
            await CreateSite();
        };
        saveKeyButton.Click += async (_, _) =>
        {
            if (SaveKey()) await RefreshAccountAsync();
        };
        testYesButton.Click += async (_, _) => await TestMinimalAsync();
        testNoButton.Click += (_, _) => CompleteWithoutTest();
        rebuildButton.Click += (_, _) => ReprobeAsync();
        balanceButton.Click += async (_, _) => await RefreshAccountAsync();
        openFileButton.Click += (_, _) => OpenFile();
        window.Opened += (_, _) => urlBox.Focus();

        // 标题条的 ✕ 与 Esc 走的是另一条关闭路径（DialogShell 里直接就 Close 了）。
        // 不接管的话，这条路关掉窗口会把结论丢掉——调用方以为「什么都没干」，
        // 于是设置页不回显、也不刷新，用户随后一点保存就用旧值把刚导入的结果覆盖回去。
        window.Closing += (_, e) =>
        {
            if (closing) return;
            if (comfyImportCancellation is not null)
            {
                e.Cancel = true;
                closeAfterComfyImport = true;
                comfyImportCancellation.Cancel();
                comfySelectionWindow?.Close();
                return;
            }
            e.Cancel = true;
            closing = true;
            window.Close(BuildSummary());
        };
    }

    private Control Build()
    {
        createButton.IsEnabled = false;
        createButton.IsVisible = false;
        // 「这是 ComfyUI」平时不出现：只在类型没判出来、手上又有个地址时才亮出来，
        // 平时摆着会让人以为「要先点它才算 ComfyUI」。
        forceComfyButton.IsVisible = false;
        if (jsonCompleter is null)
        {
            repairButton.IsEnabled = false;
            ToolTip.SetTip(repairButton, "还没有接入文本模型：先在「设置 → 模型接入」里配好");
        }

        var urlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        urlRow.Children.Add(urlBox);
        Grid.SetColumn(fetchButton, 1);
        urlRow.Children.Add(fetchButton);

        var documentSection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "接口说明网页地址，或 ComfyUI 地址", FontSize = 11, Foreground = Brush("DfInk2") },
                urlRow,
                new TextBlock
                {
                    Text = "贴 ComfyUI 地址也可以（例如 https://主机:端口 或 http://127.0.0.1:8188）："
                        + "那会先读取服务器上的工作流清单，由你勾选所需工作流，仅导入所选项，再登记成 ComfyUI 站点并保存设置。"
                        + "\n文档站抓不到正文时（页面由前端脚本渲染），在浏览器里打开该文档，把接口段落粘到下面。",
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("DfInk3")
                },
                pasteBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { analyzeButton, repairButton, forceComfyButton, createButton }
                }
            }
        };

        var reportBox = CodeBox(reportText, 186);

        // ---------- 密钥 ----------
        keyHint.TextWrapping = TextWrapping.Wrap;
        keySection.Children.Add(Header("接口密钥"));
        keySection.Children.Add(Header2("密钥只保存在本机，写入时按当前平台的方案加密；界面与日志都不会回显完整密钥。"));
        keySection.Children.Add(keyBox);
        keySection.Children.Add(keyHint);
        keySection.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { saveKeyButton } });

        // ---------- 最小测试 ----------
        questionLabel.TextWrapping = TextWrapping.Wrap;
        rebuildButton.IsVisible = false;
        testSection.Children.Add(Header("最小测试"));
        testSection.Children.Add(questionLabel);
        testSection.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { testYesButton, testNoButton, rebuildButton }
        });

        // ---------- 顶部：步骤 + 余额 ----------
        stepLabel.FontSize = 12;
        stepLabel.FontWeight = FontWeight.SemiBold;
        stepLabel.Foreground = Brush("DfInk");
        stepHint.FontSize = 10;
        stepHint.TextWrapping = TextWrapping.Wrap;
        stepHint.Foreground = Brush("DfInk3");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(new StackPanel { Spacing = 3, Children = { stepLabel, stepHint } });
        balanceButton.Content = balanceText;
        balanceButton.VerticalAlignment = VerticalAlignment.Top;
        balanceButton.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(balanceButton, "点一下重新查余额与上线模型");
        Grid.SetColumn(balanceButton, 1);
        header.Children.Add(balanceButton);

        // ---------- 底部：状态 + 预览 ----------
        openFileButton.IsVisible = false;
        var previewPanel = new StackPanel
        {
            Spacing = 6,
            Children = { previewImage, openFileButton }
        };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,232"), ColumnSpacing = 8 };
        statusBox = CodeBox(statusText, 128);
        bottom.Children.Add(statusBox);
        Grid.SetColumn(previewPanel, 1);
        bottom.Children.Add(previewPanel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(documentSection);
        body.Children.Add(reportBox);
        body.Children.Add(keySection);
        body.Children.Add(testSection);
        body.Children.Add(bottom);
        body.Children.Add(new TextBlock
        {
            Text = $"技能目录：{SkillLibrary.Directory}",
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("DfInk3")
        });

        // 内容很长、又是一页到底，所以自己包一层滚动：AgentDialogUi.Layout 会把 footer 一起卷进去，
        // 而「关闭」要一直看得见。
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(scroll);

        var close = Primary("关闭");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => CloseWithSummary();
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footerRow.Children.Add(Note("导入过程与结论都留在上面的状态区；没写入任何东西时直接关掉即可。"));
        Grid.SetColumn(close, 1);
        footerRow.Children.Add(close);
        var footer = Footer(footerRow);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        EnterStep(ApiWizardStep.Document);
        SetBalance("余额：未查询\n填密钥后自动查");

        // 两处引导：地址填好之后「读取网页」闪起来；读取回来之后「下一步」闪起来。
        // 建在这里而不建在字段初始化里，是因为要等到按钮已经进了可视树——动画挂在控件本身上。
        fetchPulse = new AttentionPulse(fetchButton);
        nextStepPulse = new AttentionPulse(createButton);
        urlBox.TextChanged += (_, _) => SyncFetchPulse();
        return root;
    }

    // ---------- 引导：呼吸提示与点亮 ----------

    /// <summary>地址非空、按钮还能点、而且他还没点过 → 让「读取网页」一闪一闪，等于说「下一步点这里」。</summary>
    private void SyncFetchPulse()
    {
        if (fetchPulse is null) return;
        var ready = !fetchPrompted && (urlBox.Text?.Trim().Length ?? 0) > 0 && fetchButton.IsEnabled;
        if (ready) fetchPulse.Start();
        else fetchPulse.Stop();
    }

    /// <summary>
    /// 读取网页回来了：把结论那一格点亮，并让「下一步」那颗按钮闪起来。
    ///
    /// 为什么值得做这两下：这一页从上到下很长（地址、正文、报告、密钥、测试、状态区），
    /// 用户点完「读取网页」之后视线还停在按钮上，而结论与下一步都在**他看不见的下方**——
    /// 不指一下，很多人会以为没反应，然后再点一次。
    /// </summary>
    private void MarkStatusImportant()
    {
        HighlightStatus(true);
        SyncNextStepPulse();
    }

    /// <summary>下一步那颗按钮「可以点了」才闪；正在忙或还没露面就收回去。</summary>
    private void SyncNextStepPulse()
    {
        if (nextStepPulse is null) return;
        if (createButton.IsVisible && createButton.IsEnabled && !busy) nextStepPulse.Start();
        else nextStepPulse.Stop();
    }

    /// <summary>
    /// 点亮 / 收回状态区。动的是边框与底色，**不动字色**：那一格是 SelectableTextBlock，
    /// 里面贴的常常是服务端原样的报错，把前景色一起换掉会让报错本身更难读。
    /// </summary>
    private void HighlightStatus(bool on)
    {
        if (statusBox is null) return;
        statusBox.BorderBrush = Brush(on ? "DfPrimary" : "DfLine");
        statusBox.BorderThickness = new Thickness(on ? 2 : 1);
        statusBox.Background = Brush(on ? "DfPrimarySoft" : "DfBg");
    }

    /// <summary>
    /// 一闪一闪：来回摆不透明度，用来指「这里可以点」。
    ///
    /// 停下时**必须把不透明度还原成 1**：半透明的按钮看着像被禁用了，比不做提示更误导。
    /// 动画是无限循环的，所以要用一个 token 才停得下来（RunAsync 一直不返回）。
    /// </summary>
    private sealed class AttentionPulse
    {
        private static readonly Animation Blink = new()
        {
            Duration = TimeSpan.FromMilliseconds(820),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 0.5d) } }
            }
        };

        private readonly Visual target;
        private CancellationTokenSource? cancellation;

        public AttentionPulse(Visual target) => this.target = target;

        public void Start()
        {
            if (cancellation is not null) return;
            var source = new CancellationTokenSource();
            cancellation = source;
            _ = RunAsync(source.Token);
        }

        public void Stop()
        {
            if (cancellation is null) return;
            var source = cancellation;
            cancellation = null;
            source.Cancel();
            source.Dispose();
            target.Opacity = 1;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try { await Blink.RunAsync(target, cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 把这一次的结果**当面**说一遍：说清了什么、成在哪、以后去哪儿看。
    ///
    /// 为什么要一个弹窗而不只是写进状态区：拉工作流要等好一会儿，用户点完常常已经去干别的了，
    /// 回来只看到窗口还在——状态区那一格在下半截、还要滚，等于结论没人读。弹窗是他回来必然撞到的东西。
    /// </summary>
    private async Task ShowCompletionDialogAsync(string title, IReadOnlyList<string> lines)
    {
        if (window is null) return;

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        foreach (var line in lines)
        {
            body.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                // 「·」开头的是细节、其余是主句：用字色把主次分开，扫一眼先看到「成了什么」。
                Foreground = Brush(line.StartsWith('·') ? "DfInk2" : "DfInk")
            });
        }

        var ok = Primary("知道了");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok);
        var dialog = DialogShell.Create(title, Layout(body, Footer(row)), 540, 440);
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(window);
    }

    private void CloseWithSummary()
    {
        closing = true;
        window?.Close(BuildSummary());
    }

    /// <summary>给调用方的一行结论。**什么都没写成时返回 null**，让外壳知道不必刷新、也不必报「导入成功」。</summary>
    private string? BuildSummary()
    {
        if (!wroteSite && !savedConfig && deletedWorkflowCount == 0) return null;

        var parts = new List<string>();
        if (installedComfySite is { } comfy)
        {
            // ComfyUI 这条：站点本身就是产物，把份数说出来——用户看不到的话，
            // 「导入成功了什么」只能靠他自己去技能管理页翻。
            parts.Add($"ComfyUI 站点「{comfy.Label}」已登记："
                + $"图像 {comfy.ImageWorkflows.Count} 份、视频 {comfy.VideoWorkflows.Count} 份工作流");
        }
        else if (wroteSite)
        {
            parts.Add($"站点已登记（{SiteCatalog.Directory}）");
        }

        if (deletedWorkflowCount > 0) parts.Add($"已删除 {deletedWorkflowCount} 份本地工作流");
        if (savedConfig) parts.Add("接口地址 / 模型 / 密钥已保存");
        if (returnedMediaPath.Length > 0) parts.Add($"最小测试产出 {Path.GetFileName(returnedMediaPath)}");
        if (lastTestCost is { } cost && cost != 0) parts.Add($"本次测试消耗 {cost} 积分");
        return string.Join("；", parts) + "。";
    }

    private void SetBalance(string text) => balanceText.Text = text;

    // ---------- 流程 ----------

    private async Task<bool> AnalyzeUrlAsync(string? url)
    {
        var target = (url ?? string.Empty).Trim();
        urlBox.Text = target;
        SetStatus($"正在读取 {target} …");
        fetchButton.IsEnabled = false;
        SyncFetchPulse();      // 正在读：按钮按住的同时把呼吸提示收掉，免得「看起来还能点」
        try
        {
            var fetched = await fetcher(target, CancellationToken.None);
            if (!fetched.Ok)
            {
                // 抓不到正文**不代表这个地址没用**：ComfyUI 的首页本来就不是「文档页」，
                // 抓不到是正常的（早先这里直接 return，于是贴 ComfyUI 地址的人
                // 只看到一句抓取失败，后面那条分支永远走不到）。
                // 所以照样往下走一遍类型判断——判不出来时，那边会去问地址一句。
                SetStatus(fetched.Error);
                AppendStatus("（这个地址可能不是文档页。先按「手上只有一个地址」继续判断类型。）");
                AnalyzeContent(string.Empty, target);
                return false;
            }
            lastContent = fetched.Content;
            AnalyzeContent(fetched.Content, target);
            // 必须**在分析之后**追加：解析那一步会把状态区整段换掉，先写就被冲掉了。
            // 正文不是从页面本身读到的（例如从它按路由名匹配到的脚本里恢复出来的）时说清出处——
            // 用户接下来要核对的正是这份正文，不讲清它是哪来的，等于让他对一段不知出处的文字下判断。
            if (fetched.Note.Length > 0) AppendStatus(fetched.Note);
            return true;
        }
        finally
        {
            fetchButton.IsEnabled = true;
            SyncFetchPulse();
            // 结论已经落在状态区里了：点亮它，并把「下一步可以点了」指出来。
            MarkStatusImportant();
        }
    }

    private ApiDocReport AnalyzeContent(string? content, string? sourceUrl)
    {
        lastContent = content ?? string.Empty;

        // 先问一句「这是不是 ComfyUI」。
        //
        // ComfyUI **不是「一家有接口站」**：它跑的是工作流模板，用哪个模型、走几步、
        // 要不要参考图全由模板决定，没有「模型 × 档位」可挑。所以它走另一条分支（登记成站点 + 拉工作流）。
        //
        // 这个识别器本来就写好了（`ProviderImporter.Inspect`：`/object_info`、`/prompt`、`:8188`
        // 之类的判据都能认），但它**只认文字**：用户手上往往只有一个地址，而那个地址里
        // 一个能判断用途的词都没有（不是 8188、域名里也没有 comfy），于是只能判成「未能判断」——
        // 明明有一台 ComfyUI，界面上却什么都出不来。所以判不出来时**去问一句地址**。
        var draft = ProviderImporter.Inspect(lastContent);
        // 正文可能一个字都没有（地址是一台 ComfyUI 的首页，它不是「文档页」，抓不到是正常的）。
        // 那时只有地址可用——地址一样能判类型，判不出来还能去问它一句。
        if (draft.BaseUrl.Length == 0 && urlBox.Text.Trim().Length > 0)
            draft = ProviderImporter.Inspect(urlBox.Text.Trim());
        if (draft.Kind != ProviderKind.ComfyUi && draft.BaseUrl.Length > 0)
        {
            SetStatus($"这段文字里没有能判断类型的词，正在问一下 {draft.BaseUrl} 是不是 ComfyUI…");
            var looksLikeComfy = ComfyUiLibrary.LooksLikeComfyUiAsync(draft.BaseUrl).GetAwaiter().GetResult();
            AppendStatus(looksLikeComfy
                ? "那个地址答话了：它是 ComfyUI（/system_stats 回了 ComfyUI 才有的结构）。"
                : "那个地址不像 ComfyUI；如果有别的地址或更完整的文档，换一个再试。");
            draft = ProviderImporter.AsComfyUi(draft, fromProbe: looksLikeComfy);
        }

        if (draft.Kind == ProviderKind.ComfyUi)
        {
            ShowComfyUi(draft);
            // 这条分支不产出接口报告（没有要解析的接口清单）；返回一份空报告，调用方只看成功与否。
            return ApiDocAnalyzer.Analyze(null, null);
        }

        comfyDraft = null;
        createButton.Content = "登记站点";
        rebuildButton.IsVisible = true;
        // 判不出类型但手上有个地址时，留一个**人来定**的出口：探测失败（地址暂时不通、在内网、
        // 有鉴权）时不该就此卡死，而「请手动选择类型」那句话也得真有个地方能选。
        // 只在**确实没判出来**时出现——已经认出是画图接口了还摆着一个「这是 ComfyUI」，
        // 会让人以为非点它不可。
        forceComfyButton.IsVisible = draft.Kind == ProviderKind.Unknown && draft.BaseUrl.Length > 0;

        var report = ApiDocAnalyzer.Analyze(content, sourceUrl ?? urlBox.Text.Trim());
        Analyze(report);
        return report;
    }

    /// <summary>用户点「这是 ComfyUI」时：按他说的走，不再猜。</summary>
    private void ForceComfyUi()
    {
        var draft = ProviderImporter.Inspect(lastContent);
        // 同样地：正文可能是空的（地址不是文档页），那时地址栏里那个才是线索。
        if (draft.BaseUrl.Length == 0) draft = ProviderImporter.Inspect(urlBox.Text);
        draft = ProviderImporter.AsComfyUi(draft, fromProbe: false);
        if (draft.BaseUrl.Length == 0)
        {
            SetStatus("按「这是 ComfyUI」走需要先有一个地址：把 ComfyUI 的首页地址（例如 http://127.0.0.1:8188）贴上。");
            return;
        }
        comfyDraft = null;
        SetStatus("按你的判断当成 ComfyUI：下面是这条分支的结论。");
        ShowComfyUi(draft);
    }

    /// <summary>
    /// ComfyUI 分支的结论页。**不探测模型池**（ComfyUI 没有「模型 × 档位」），
    /// 改成把服务器上 <c>workflows/</c> 里的工作流整份拉下来。
    /// </summary>
    private void ShowComfyUi(ProviderImportDraft draft)
    {
        comfyDraft = draft;
        lastReport = null;
        site = null;
        installedComfySite = null;

        var lines = new List<string>
        {
            "—— 识别结果：ComfyUI ——",
            $"· 地址：{(draft.BaseUrl.Length > 0 ? draft.BaseUrl : "（这段文字里没有地址）")}",
            $"· checkpoint：{(draft.Checkpoint.Length > 0 ? draft.Checkpoint : "（没提到；留空即用工作流模板自己声明的那一个）")}"
        };
        if (draft.Signals.Count > 0) lines.Add($"· 判据：{string.Join("、", draft.Signals.Take(6))}");
        lines.Add(string.Empty);
        lines.Add("ComfyUI 不是「一家有接口站」：它跑的是工作流模板，用哪个模型、走几步、吃几张参考图全由模板决定，");
        lines.Add("所以这里没有「模型 × 档位」可挑，也不会探测模型清单——**它的子项就是工作流**。");
        lines.Add(string.Empty);
        lines.Add("点下面的按钮会做两件事：");
        lines.Add("· 把地址与 checkpoint 写进设置（并登记成一个 ComfyUI 站点，地址以站点文件为准）；");
        lines.Add("· 先读取服务器 workflows 清单，勾选少量工作流后，仅拉取所选项并转成 API 格式存好——");
        lines.Add("  这等价于在浏览器里一份一份右键「导出（API）」，只是不用你点。");
        lines.Add("  拉取过程只读：只问清单、正文与节点定义，不发任何生成请求。");
        reportText.Text = string.Join(Environment.NewLine, lines);

        createButton.Content = "拉取工作流并登记站点";
        createButton.IsEnabled = draft.BaseUrl.Length > 0;
        createButton.IsVisible = true;
        rebuildButton.IsVisible = false;
        forceComfyButton.IsVisible = false;
        SetStatus(draft.BaseUrl.Length > 0
            ? "识别为 ComfyUI：点「拉取工作流并登记站点」把工作流拉下来存好（只读，不跑任何生成）。"
            : "识别为 ComfyUI，但这段文字里没有能用的地址：把控制台首页地址、或含 http://…:8188 的那一行一起贴进来。");
        EnterStep(ApiWizardStep.Document);
    }

    private CancellationTokenSource? comfyImportCancellation;
    private bool closeAfterComfyImport;
    private Window? comfySelectionWindow;
    private SelectableTextBlock? comfySelectionStatus;
    private TextBlock? comfyTimingText;
    private ProgressBar? comfyProgressBar;
    private Stopwatch? comfyWatch;
    private Stopwatch? comfyFetchWatch;
    private int comfySelectedCount;
    private int comfyCompleted;
    private double? comfySecondsPerItem;
    private bool comfyFetchActive;
    private string comfyPhase = string.Empty;

    private static string FormatComfyDuration(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private void UpdateComfyTiming()
    {
        if (comfyTimingText is null || comfyWatch is null) return;
        var remaining = comfyFetchActive
            ? comfySecondsPerItem is { } seconds
                ? $"预计拉取剩余 {FormatComfyDuration(TimeSpan.FromSeconds(Math.Max(0, comfySelectedCount - comfyCompleted) * seconds))}（按实际完成速率；审核与保存另计）"
                : "预计剩余：估算中（初始化／尚无完成样本）"
            : comfyPhase == "完成" ? "预计剩余 00:00:00" : comfyPhase;
        comfyTimingText.Text = $"已用 {FormatComfyDuration(comfyWatch.Elapsed)}｜{remaining}"
            + (comfyImportCancellation?.IsCancellationRequested == true && comfyWatch.IsRunning
                ? "｜已请求取消，等待当前操作安全结束" : string.Empty);
    }

    /// <summary>
    /// ComfyUI 分支的落地动作：读清单 → 选择工作流 → 拉取并审核所选项 → 安装站点 → 保存设置。
    ///
    /// 为什么在这一步就把工作流拉下来，而不是等出图时再说：转换要用服务器的
    /// <c>object_info</c>，而实测那有 **21.8 MB**；每次提交前去拉一次既慢又不稳。
    /// 拉一次存起来，之后不管选哪一份都只是读一个几十 KB 的本地文件。
    ///
    /// 站点文件与设置**同源**：地址与 checkpoint 先写配置、再写进站点，最后按站点回写配置，
    /// 保证「设置里显示的」和「站点里记着的」是同一台机器，不会出现改了这头忘那头。
    /// </summary>
    private async Task InstallComfyUiAsync()
    {
        if (comfyDraft is null || lastReport is not null || busy) return;
        if (comfyDraft.BaseUrl.Length == 0)
        {
            SetStatus("这段文字里没有能用的 ComfyUI 地址：把控制台首页地址、或含 http://…:8188 的那一行一起贴进来。");
            return;
        }

        busy = true;
        createButton.IsEnabled = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromHours(2));
        comfyImportCancellation = budget;
        try
        {
            SetStatus("正在读取工作流清单…");
            var manifest = await ComfyUiLibrary.ReadManifestAsync(comfyDraft.BaseUrl, cancellationToken: budget.Token);
            await SelectComfyUiWorkflowsAsync(manifest, budget);
        }
        catch (OperationCanceledException)
        {
            SetStatus("工作流导入已取消或达到总时限。");
        }
        catch (Exception error)
        {
            SetStatus("读取工作流清单失败：" + error.Message);
        }
        finally
        {
            comfyImportCancellation = null;
            busy = false;
            createButton.IsEnabled = true;
            if (closeAfterComfyImport)
            {
                closing = true;
                window?.Close(BuildSummary());
            }
        }
    }

    private async Task ImportSelectedComfyUiAsync(IReadOnlyCollection<string> selectedPaths, CancellationToken token)
    {
        try
        {
            // Progress<T> 在界面线程上构造，回调就回到界面线程——直接改 TextBlock 是安全的。
            var progress = new Progress<ComfyUiLibraryProgress>(item =>
            {
                if (!comfyFetchActive) return;
                if (item.Done > comfyCompleted && comfyFetchWatch is { } watch)
                {
                    comfyCompleted = item.Done;
                    comfySecondsPerItem = watch.Elapsed.TotalSeconds / comfyCompleted;
                }
                if (comfyProgressBar is { } bar)
                {
                    bar.IsIndeterminate = item.Done == 0;
                    bar.Maximum = Math.Max(1, item.Total);
                    bar.Value = comfyCompleted;
                }
                SetStatus(item.Describe());
                UpdateComfyTiming();
            });
            ComfyUiLibraryResult fetched;
            try
            {
                SetStatus($"正在拉取所选的 {selectedPaths.Count} 份工作流…");
                var webViewService = (global::Avalonia.Application.Current as YEEYEEYEE.Desktop.Avalonia.App)?.ComfyUiWebViewService
                    ?? throw new InvalidOperationException("Avalonia NativeWebView 官方前端宿主尚未初始化。");
                fetched = await ComfyUiLibrary.FetchAsync(
                    comfyDraft!.BaseUrl, null, progress, token, webViewService.Factory, selectedPaths);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                SetStatus("工作流导入已取消或达到总时限，未登记站点。");
                return;
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
            {
                SetStatus("拉取工作流失败：" + error.Message + Environment.NewLine
                    + "· 确认地址指向 ComfyUI 本身（控制台首页），并且那台机器上的 ComfyUI 正在运行；"
                    + "地址后面带 /object_info、/prompt 这类路径的，请去掉再试。" + Environment.NewLine
                    + "· 本次**没有登记站点**，也没有改动出图 / 出视频的设置。");
                return;
            }
            finally
            {
                comfyFetchActive = false;
            }
            comfyPhase = "审核中（剩余时间估算中）";
            if (comfyProgressBar is { } auditBar) auditBar.IsIndeterminate = true;
            UpdateComfyTiming();

            // 优先按地址找到同一站点（包括端口撞名后带后缀的站点），部分导入保留未选工作流。
            var existingSites = SiteCatalog.Load().Sites;
            var previous = existingSites.FirstOrDefault(item => item.IsComfyUi &&
                string.Equals(item.BaseUrl, fetched.BaseUrl, StringComparison.OrdinalIgnoreCase))
                ?? existingSites.FirstOrDefault(item =>
                    string.Equals(item.Id, fetched.SiteId, StringComparison.OrdinalIgnoreCase));

            // 上次这台机器上学到的「前端节点规则」接着用——同一台机器不该每导一次就再认一遍。
            if (!fetched.UsesOfficialFrontend && previous is { VirtualNodeRules.Count: > 0 })
            {
                SetStatus($"正在按上次学到的 {previous.VirtualNodeRules.Count} 条前端节点规则重转…");
                fetched = ComfyUiLibrary.Reconvert(fetched, previous.VirtualNodeRules);
            }

            // ---------- 体检：拉到就查，查完当场说 ----------
            // 体检要的三样（原稿、转换结果、这台的节点定义）在拉取时都拿到了，判断全是本地读 JSON。
            // 有「我们丢了的输入」才值得打扰用户；那几类正常的（静音/绕过、原稿自己断线）只报不催。
            //
            // 另外：模型那一步的结论要**留到最后的报告里**。它是用户花钱做的决定，只闪在状态区一下
            // 就被后面的落盘报告冲掉，等于花完钱没留凭据（实测演示里就是这样）。
            var repairSummary = string.Empty;

            var audit = fetched.Audit;
            if (fetched.UsesOfficialFrontend && audit is { NeedsAttention: true })
                SetStatus("官方前端导出后的体检发现问题（保留审计，不应用 C# 学习重转）："
                    + Environment.NewLine + audit.Describe());
            if (!fetched.UsesOfficialFrontend && audit is { NeedsAttention: true })
            {
                SetStatus("体检发现问题：" + Environment.NewLine + audit.Describe());
                switch (await AskRepairAsync(audit))
                {
                    case ComfyUiRepairChoice.Cancel:
                        comfyImportCancellation?.Cancel();
                        SetStatus("已取消这次导入：**没有登记站点**，也没有改动出图 / 出视频的设置。"
                            + Environment.NewLine + audit.Describe());
                        return;

                    case ComfyUiRepairChoice.Repair:
                        var known = previous?.VirtualNodeRules ?? new List<ComfyUiVirtualNodeRule>();
                        var learning = await ComfyUiVirtualNodeLearner.LearnAsync(
                            fetched,
                            jsonCompleter!,
                            known,
                            message => SetStatus(message), token);
                        // 只采用**验证过**的规则（照它重转之后「我们丢了」真的变少、且没多出判断不了的）。
                        fetched = ComfyUiLibrary.Reconvert(fetched, learning.Accepted);
                        repairSummary = learning.Describe();
                        SetStatus(learning.Describe() + Environment.NewLine + Environment.NewLine
                            + (fetched.Audit?.Describe() ?? string.Empty));
                        break;

                    default:
                        AppendStatus("按你的选择先这样导入：上面那些「我们丢了」的输入保持缺失状态，"
                            + "用到那份工作流时可能缺东西。要治的话，选一份带源视频/底图入口的工作流，或重新导入时选「让大模型认一认」。");
                        break;
                }
            }

            token.ThrowIfCancellationRequested();
            comfyPhase = "保存中（安装站点与保存配置，剩余时间估算中）";
            UpdateComfyTiming();
            var previousKeys = previous?.Workflows.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            var updatedCount = selectedPaths.Count(previousKeys.Contains);
            // Install 不支持中途取消；一旦开始写入，等待站点和配置保存安全结束。
            var (installed, failure) = await Task.Run(() => ComfyUiLibrary.Install(
                fetched,
                previous?.DisplayName ?? string.Empty,
                comfyDraft!.Checkpoint,
                previous));
            if (installed is null)
            {
                SetStatus("工作流拉回来了，但站点没能落盘：" + failure + Environment.NewLine
                    + $"· 站点目录：{SiteCatalog.Directory}" + Environment.NewLine
                    + "· 本次没有改动出图 / 出视频的设置。");
                return;
            }

            installedComfySite = installed;
            wroteSite = true;
            // 站点文件为准：按刚落盘的站点回写配置，两边不会各说一套。
            config.ComfyUiBaseUrl = installed.BaseUrl;
            if (installed.Checkpoint.Length > 0) config.ComfyUiCheckpoint = installed.Checkpoint;
            if (!await Task.Run(() => AiProviderSettings.Save(config)))
            {
                SetStatus("所选工作流已安装，但配置保存失败，请检查配置文件权限后重试。");
                return;
            }
            savedConfig = true;

            var picks = installed.UsableWorkflows.Count(workflow => workflow.Recommended);
            var lines = new List<string>
            {
                $"站点「{installed.Label}」已登记：图像 {installed.ImageWorkflows.Count} 份、视频 {installed.VideoWorkflows.Count} 份工作流。",
                $"· 地址：{installed.BaseUrl}"
                    + (installed.Checkpoint.Length > 0 ? $"｜checkpoint {installed.Checkpoint}" : "｜没填 checkpoint"),
                $"· 本次选择 {selectedPaths.Count} 份（新增 {selectedPaths.Count - updatedCount}／更新 {updatedCount}），转成 {fetched.Converted} 份；站点现有 {installed.Workflows.Count} 份（保留未选的已有工作流）",
                $"· 每个家族（服务器上的顶层文件夹）各推了一份默认：共 {picks} 份",
                $"· 正文按份落盘：{SiteCatalog.PayloadDirectory(installed.Id)}（站点文件本身不带正文，选择器不必读大文件）"
            };
            if (fetched.Failed > 0)
                lines.Add($"· **{fetched.Failed} 份没能转换**：原因逐份记在站点文件里，不影响其它工作流");
            foreach (var note in fetched.Notes) lines.Add("· " + note);
            // 模型那一步说了什么、有没有被采用——留在这里，用户关掉之后还查得到。
            if (repairSummary.Length > 0)
            {
                lines.Add("· 让大模型认过之后：");
                foreach (var line in repairSummary.Split(Environment.NewLine))
                    lines.Add("    " + line);
            }
            // 体检结论留在这一步的结论里：用户点「完成」之后把结论收走，之后想回看就只能重新导入一次。
            if (fetched.Audit is { } finalAudit)
            {
                lines.Add("· 导入前体检：");
                foreach (var line in finalAudit.Describe().Split(Environment.NewLine))
                    lines.Add("    " + line);
            }
            lines.Add("· 以后要换工作流 / 看这一台有哪些：**设置 → 技能管理 → 站点与池子**");
            lines.Add("· 出图 / 出视频时会先让你在这台服务器的工作流里选一份（默认选中的是推荐的那份）");
            SetStatus(string.Join(Environment.NewLine, lines));
            EnterStep(ApiWizardStep.Done);

            comfyPhase = "完成";
            if (comfyProgressBar is { } completedBar)
            {
                completedBar.IsIndeterminate = false;
                completedBar.Value = completedBar.Maximum;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("工作流导入已取消或达到总时限。"
                + (wroteSite ? "站点已写入，请检查配置保存结果。" : "本次未登记站点或保存配置。"));
        }
        catch (Exception error)
        {
            SetStatus("导入失败：" + error.Message
                + (wroteSite ? "\n站点已写入，请检查配置保存结果。" : "\n本次未保存配置；若安装已开始，请检查站点目录。"));
        }
    }

    private async Task SelectComfyUiWorkflowsAsync(IReadOnlyList<string> paths, CancellationTokenSource cancellation)
    {
        if (window is null) return;
        SetStatus("正在后台读取本地工作流状态…");
        var local = await Task.Run(() =>
        {
            // 只按完整地址匹配，不能把同主机不同端口的另一站点当成本地记录。
            var existing = SiteCatalog.Load().Sites.FirstOrDefault(item => item.IsComfyUi &&
                string.Equals(item.BaseUrl.TrimEnd('/'), comfyDraft!.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            var statuses = existing is null ? Array.Empty<SiteWorkflowStatus>()
                : SiteCatalog.ReadWorkflowStatuses(existing).ToArray();
            return (Site: existing, Statuses: statuses);
        }, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var statuses = local.Statuses.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var remotePaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        var remoteKeys = remotePaths.ToHashSet(StringComparer.Ordinal);
        var choices = new StackPanel { Spacing = 4 };
        var boxes = new List<(string Path, CheckBox Box, Grid Row, bool Remote)>();
        var groups = new List<(CheckBox Box, Expander View, List<CheckBox> Items)>();
        var count = Note(string.Empty);
        var confirm = Primary("导入所选工作流");
        var cancel = Secondary("取消");
        var all = Secondary("全选可见项");
        var none = Secondary("取消全选");
        var unimported = Secondary("选择未导入");
        var filter = new ComboBox { MinWidth = 100, FontSize = 12 };
        filter.ItemsSource = new[] { "全部", "未导入", "已导入" };
        filter.SelectedIndex = 0;
        var deletionStatus = Note(string.Empty);
        var deleting = false;
        bool IsVisible(string path) => filter.SelectedIndex switch
        {
            1 => !statuses.ContainsKey(path),
            2 => statuses.ContainsKey(path),
            _ => true
        };
        string DescribeStatus(string path)
        {
            if (!statuses.TryGetValue(path, out var status)) return "未导入";
            var text = status.IsUsable ? "已导入" : "需修复";
            text += "｜ImportedAt：" + (status.ImportedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "未知（旧记录）");
            if (status.LastImportError.Length > 0)
                text += (status.IsUsable ? "｜更新失败，旧版可用：" : "｜上次导入失败：") + status.LastImportError;
            if (!status.Enabled) text += "｜已停用";
            if (!status.Converted) text += "｜转换未成功";
            if (!status.PayloadAvailable) text += "｜本地正文缺失或不可读";
            return text;
        }
        var syncing = false;
        var running = false;
        var started = false;
        var closeRequested = false;
        void SyncSelection()
        {
            if (syncing) return;
            syncing = true;
            try
            {
                foreach (var item in boxes) item.Row.IsVisible = IsVisible(item.Path);
                var visibleBoxes = boxes.Where(item => item.Row.IsVisible).Select(item => item.Box).ToHashSet();
                foreach (var group in groups)
                {
                    var visible = group.Items.Where(visibleBoxes.Contains).ToList();
                    var selectable = visible.Where(box => box.IsEnabled).ToList();
                    var n = selectable.Count(box => box.IsChecked == true);
                    group.Box.IsChecked = n == 0 ? false : n == selectable.Count ? true : null;
                    group.Box.IsEnabled = selectable.Count > 0;
                    group.View.IsVisible = visible.Count > 0;
                }
                var selected = boxes.Where(item => item.Remote && item.Box.IsChecked == true).ToList();
                var selectedCount = selected.Count;
                var updates = selected.Count(item => statuses.ContainsKey(item.Path));
                count.Text = $"远端清单 {remotePaths.Length} 份，仅本地已有 {boxes.Count(item => !item.Remote)} 份；可见 {boxes.Count(item => item.Row.IsVisible)} 份。已选 {selectedCount} 份（新增 {selectedCount - updates}／更新 {updates}）。"
                    + (selectedCount == 0
                        ? " 请选择工作流后查看预计耗时。"
                        : $" 初始粗估：约 {FormatComfyDuration(TimeSpan.FromSeconds(60 + selectedCount * 15))}～{FormatComfyDuration(TimeSpan.FromSeconds(120 + selectedCount * 30))}（含首次前端加载预留 1～2 分钟及每份 15～30 秒；仅供参考，运行后按实际速率更新；审核与保存另计）。");
                confirm.IsEnabled = !started && !deleting && selectedCount > 0;
            }
            finally { syncing = false; }
        }
        void SelectAll(bool value)
        {
            syncing = true;
            try
            {
                foreach (var item in boxes.Where(item => item.Remote && (!value || item.Row.IsVisible)))
                    item.Box.IsChecked = value;
            }
            finally { syncing = false; }
            SyncSelection();
        }
        // 使用完整父目录；a/x 与 b/x、a 与 a/sub 分别成组，保留原路径提交给 Fetch。
        foreach (var folder in remotePaths.Concat(statuses.Keys.Where(key => !remoteKeys.Contains(key)))
            .GroupBy(path =>
            {
                var normalized = path.Replace('\\', '/');
                var separator = normalized.LastIndexOf('/');
                return (LocalOnly: !remoteKeys.Contains(path), Folder: separator < 0 ? string.Empty : normalized[..separator]);
            }).OrderBy(group => group.Key.LocalOnly).ThenBy(group => group.Key.Folder, StringComparer.Ordinal))
        {
            var groupName = $"{(folder.Key.LocalOnly ? "仅本地已有 · " : string.Empty)}{(folder.Key.Folder.Length == 0 ? "（根目录）" : folder.Key.Folder)}";
            var groupTitle = new TextBlock { Text = $"{groupName}（{folder.Count()} 份）", TextWrapping = TextWrapping.Wrap };
            var groupBox = new CheckBox
            {
                IsThreeState = true, IsChecked = false, Content = groupTitle
            };
            var children = new StackPanel { Spacing = 3, Margin = new Thickness(20, 0, 0, 0) };
            var items = new List<CheckBox>();
            foreach (var path in folder.OrderBy(path => path, StringComparer.Ordinal))
            {
                var box = new CheckBox
                {
                    IsChecked = false, IsEnabled = !folder.Key.LocalOnly,
                    Content = new TextBlock { Text = path.Replace('\\', '/').Split('/')[^1], TextWrapping = TextWrapping.Wrap },
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                ToolTip.SetTip(box, path);
                var detail = Note(DescribeStatus(path));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new StackPanel { Spacing = 2, Children = { box, detail } });
                var remove = Secondary("删除本地");
                remove.IsVisible = statuses.ContainsKey(path);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                boxes.Add((path, box, row, !folder.Key.LocalOnly));
                items.Add(box);
                box.IsCheckedChanged += (_, _) => SyncSelection();
                remove.Click += async (_, _) =>
                {
                    if (started || deleting || local.Site is null || comfySelectionWindow is null) return;
                    deleting = true;
                    confirm.IsEnabled = false;
                    cancel.IsEnabled = false;
                    try
                    {
                        if (!await ConfirmAsync(comfySelectionWindow, "删除本地工作流",
                            $"确定删除「{path}」的本地记录与不再被引用的正文吗？\n远端工作流与已经生成的图像、视频不受影响。\n"
                            + "旧画布对工作流的引用无法完全检测；删除后，依赖它的旧画布可能无法再次生成。\n"
                            + "对应的图像、视频选择偏好也会清理。", "删除本地")) return;
                        if (cancellation.IsCancellationRequested || closeRequested) return;
                        var deleted = await Task.Run(() =>
                        {
                            var success = SiteCatalog.TryDeleteWorkflow(local.Site, path, out var error);
                            return (Success: success, Error: error);
                        });
                        if (!deleted.Success)
                        {
                            deletionStatus.Text = "删除失败：" + deleted.Error;
                            return;
                        }
                        deletedWorkflowCount++;
                        var cleanup = await ClearDeletedWorkflowPreferencesAsync(local.Site.Id, path);
                        statuses.Remove(path);
                        box.IsChecked = false;
                        remove.IsVisible = false;
                        detail.Text = DescribeStatus(path);
                        if (folder.Key.LocalOnly)
                        {
                            boxes.RemoveAll(item => item.Box == box);
                            items.Remove(box);
                            children.Children.Remove(row);
                            groupTitle.Text = $"{groupName}（{items.Count} 份）";
                        }
                        deletionStatus.Text = $"已删除本地工作流「{path}」。" + cleanup;
                    }
                    catch (Exception error) { deletionStatus.Text = "删除或偏好清理失败：" + error.Message; }
                    finally
                    {
                        deleting = false;
                        cancel.IsEnabled = true;
                        SyncSelection();
                        if (closeRequested) comfySelectionWindow?.Close();
                    }
                };
                children.Children.Add(row);
            }
            groupBox.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                // 三态只用于显示汇总；用户把选中态点到 null 时视为取消全组。
                var value = groupBox.IsChecked == true;
                syncing = true;
                try
                {
                    foreach (var box in items.Where(box => box.IsEnabled && boxes.Any(item => item.Box == box && item.Row.IsVisible)))
                        box.IsChecked = value;
                }
                finally { syncing = false; }
                SyncSelection();
            };
            var view = new Expander
            {
                Header = groupBox, Content = children, IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            groups.Add((groupBox, view, items));
            choices.Children.Add(view);
        }
        all.Click += (_, _) => SelectAll(true);
        none.Click += (_, _) => SelectAll(false);
        unimported.Click += (_, _) =>
        {
            syncing = true;
            try
            {
                foreach (var item in boxes) item.Box.IsChecked = item.Remote && !statuses.ContainsKey(item.Path);
            }
            finally { syncing = false; }
            SyncSelection();
        };
        filter.SelectionChanged += (_, _) => SyncSelection();
        SyncSelection();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { filter, unimported, all, none } };
        var selection = new StackPanel
        {
            Spacing = 8, Children = { count, toolbar,
                Note("已导入筛选包含需修复项。筛选保留已勾选项；全选与分组选择仅作用于可见的远端项。选择未导入会替换当前选择。\n仅本地已有项只能删除，不能导入。可用状态只表示本地正文可读，不保证远端执行成功。"),
                deletionStatus, choices }
        };
        var result = CodeText();
        result.TextWrapping = TextWrapping.Wrap;
        var timing = Note("尚未开始；确认后在此窗口显示进度与结果。");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10 };
        var scroll = new ScrollViewer { Content = selection, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(scroll);
        Grid.SetRow(bar, 1);
        body.Children.Add(bar);
        Grid.SetRow(timing, 2);
        body.Children.Add(timing);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(body);
        var footer = Footer(buttons);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        var dialog = DialogShell.Create("选择并导入 ComfyUI 工作流", root, 820, 600);
        comfySelectionWindow = dialog;
        comfySelectionStatus = result;
        comfyTimingText = timing;
        comfyProgressBar = bar;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateComfyTiming();
        void CancelRunning()
        {
            cancellation.Cancel();
            cancel.IsEnabled = false;
            cancel.Content = "正在取消…";
            timing.Text = "已请求取消，正在等待当前操作安全结束（保存已开始时会完成保存）。";
        }
        dialog.Closing += (_, e) =>
        {
            if (!running && !deleting) return;
            e.Cancel = true;
            closeRequested = true;
            if (running) CancelRunning();
        };
        cancel.Click += (_, _) => { if (running) CancelRunning(); else dialog.Close(); };
        confirm.Click += async (_, _) =>
        {
            if (started || deleting) return;
            var selected = boxes.Where(item => item.Remote && item.Box.IsChecked == true).Select(item => item.Path).ToArray();
            if (selected.Length == 0) return;
            started = running = true;
            confirm.IsEnabled = false;
            selection.IsEnabled = false;
            scroll.Content = result;
            bar.IsVisible = true;
            bar.IsIndeterminate = true;
            comfySelectedCount = selected.Length;
            comfyCompleted = 0;
            comfySecondsPerItem = null;
            comfyPhase = "拉取中";
            comfyFetchActive = true;
            comfyWatch = Stopwatch.StartNew();
            comfyFetchWatch = Stopwatch.StartNew();
            timer.Start();
            UpdateComfyTiming();
            try { await ImportSelectedComfyUiAsync(selected, cancellation.Token); }
            finally
            {
                running = false;
                comfyFetchActive = false;
                timer.Stop();
                comfyWatch.Stop();
                comfyFetchWatch.Stop();
                bar.IsIndeterminate = false;
                if (comfyPhase != "完成") comfyPhase = cancellation.IsCancellationRequested ? "已取消／操作已安全结束" : "操作结束，请查看结果";
                UpdateComfyTiming();
                cancel.Content = "关闭";
                cancel.IsEnabled = true;
                confirm.IsVisible = false;
                if (closeRequested) dialog.Close();
            }
        };
        using var registration = cancellation.Token.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (running) CancelRunning();
            else if (!started) dialog.Close();
        }));
        try { await dialog.ShowDialog(window); }
        finally
        {
            timer.Stop();
            comfySelectionWindow = null;
            comfySelectionStatus = null;
            comfyTimingText = null;
            comfyProgressBar = null;
            comfyWatch = null;
            comfyFetchWatch = null;
        }
    }

    // 使用现有配置接口清理偏好；内存工作副本也同步清空，避免关闭设置页时重新保存旧引用。
    private async Task<string> ClearDeletedWorkflowPreferencesAsync(string siteId, string key)
    {
        bool MatchesImage(AiProviderConfig settings) =>
            string.Equals(settings.LastImageWorkflowSiteId, siteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(settings.LastImageWorkflowKey, key, StringComparison.Ordinal);
        if (MatchesImage(config))
        {
            config.LastImageWorkflowSiteId = string.Empty;
            config.LastImageWorkflowKey = string.Empty;
        }
        return await Task.Run(() =>
        {
            var warnings = new List<string>();
            try
            {
                var stored = AiProviderSettings.Load();
                if (MatchesImage(stored))
                {
                    stored.LastImageWorkflowSiteId = string.Empty;
                    stored.LastImageWorkflowKey = string.Empty;
                    if (!AiProviderSettings.Save(stored)) warnings.Add("图像偏好保存失败，请重新保存设置");
                }
            }
            catch (Exception error) { warnings.Add("图像偏好清理失败：" + error.Message); }
            try
            {
                var preference = VideoRoutePreferenceStore.Load();
                var routeKey = $"flow|{siteId}|{key}";
                if (string.Equals(preference.Key, routeKey, StringComparison.Ordinal))
                {
                    // Remember 失去目标后回到每次询问；Auto 仍保持自动选择。
                    if (preference.ModeKind == VideoRouteMode.Remember) preference.Mode = "ask";
                    preference.Key = string.Empty;
                    preference.Label = string.Empty;
                    VideoRoutePreferenceStore.Save(preference);
                    // Save 无返回值且会吞掉 IO 错误，重新读取确认不能假报成功。
                    if (VideoRoutePreferenceStore.Load().Key == routeKey)
                        warnings.Add("视频偏好保存失败，请检查配置文件权限");
                }
            }
            catch (Exception error) { warnings.Add("视频偏好清理失败：" + error.Message); }
            return warnings.Count == 0 ? "相关选择偏好已清理。" : "正文已删除，但" + string.Join("；", warnings) + "。";
        });
    }

    /// <summary>体检发现问题时用户的选择。</summary>
    private enum ComfyUiRepairChoice
    {
        /// <summary>让大模型认一认那些不认识的前端节点，然后重转。</summary>
        Repair,

        /// <summary>先这样导入（问题留着，用到那几份工作流时可能缺东西）。</summary>
        ImportAsIs,

        /// <summary>取消这次导入（什么都不写）。</summary>
        Cancel
    }

    /// <summary>
    /// **当场问**：体检发现「我们丢了 N 处」时把这个窗口摆出来。
    ///
    /// 为什么必须问、不能自己决定：修它要花用户的钱（调大模型），改的还是**别人写的工作流**；
    /// 而且「不修也能用」是事实——判断不了的输入多半只在少数几份工作流上，用户可能根本不用它们。
    /// 所以三件事都说清：问题是什么、修是怎么修的（照模型给的规则重转**并核对**）、不修会怎样。
    /// 没接入大模型时那个按钮置灰并说明去哪儿接——**不给一个点下去必然失败的按钮**。
    /// </summary>
    private async Task<ComfyUiRepairChoice> AskRepairAsync(ComfyUiImportAuditReport audit)
    {
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(Header("导入前的体检：发现问题"));
        var account = CodeText();
        account.Text = audit.Describe();
        account.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(CodeBox(account, 190));
        body.Children.Add(Note(audit.UnknownTypes.Count > 0
            ? "「让大模型认一认」做的是：把这些我不担保的类型在原稿里的形状发给模型，"
              + "它判断「是直通 / 自带值 / 纯界面件」，我们再**照它的答案把那台机器的全部正文重转一遍并核对**——"
              + "只有当有输入真的**变确定了**（「我们丢了」或「判断不了」少了一处）、而且没有一样变坏，才采用它。"
              + (jsonCompleter is null
                  ? "\n当前**没有接入大模型**：到「设置 → 模型接入」配好文本模型与密钥之后，这条路才能用；"
                    + "现在可以先「先这样导入」。"
                  : string.Empty)
            : "这些问题不在我们认识的范围内，稍后可以再导入一次看看。",
            audit.UnknownTypes.Count > 0 && jsonCompleter is not null
                ? AgentNoteLevel.Info
                : AgentNoteLevel.Warning));

        var repair = Primary("让大模型认一认");
        var asIs = Secondary("先这样导入");
        var cancel = Secondary("取消这次导入");
        repair.IsEnabled = jsonCompleter is not null && audit.UnknownTypes.Count > 0;
        if (!repair.IsEnabled && jsonCompleter is null) repair.Content = "让大模型认一认（未接入模型）";

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { repair, asIs, cancel }
        };

        var choice = ComfyUiRepairChoice.Cancel;
        var question = DialogShell.Create("导入前的体检", Layout(body, Footer(buttons)), 700, 470);
        repair.Click += (_, _) => { choice = ComfyUiRepairChoice.Repair; question.Close(); };
        asIs.Click += (_, _) => { choice = ComfyUiRepairChoice.ImportAsIs; question.Close(); };
        cancel.Click += (_, _) => { choice = ComfyUiRepairChoice.Cancel; question.Close(); };
        var owner = comfySelectionWindow ?? window;
        using var registration = comfyImportCancellation?.Token.Register(() =>
            Dispatcher.UIThread.Post(() => question.Close()));
        if (owner is not null) await question.ShowDialog(owner);
        return choice;
    }

    private void Analyze(ApiDocReport report)
    {
        lastReport = report;
        site = SiteFor(report, Array.Empty<SitePool>(), string.Empty);
        RenderReport();
        createButton.IsEnabled = report.HasAnything;
        createButton.IsVisible = true;
        SetStatus(report.HasAnything
            ? $"解析完成：出图 {report.ImageOps.Count} 条、出视频 {report.VideoOps.Count} 条。"
            : "没有解析出可用的出图或出视频接口：请确认地址指向接口说明页，或改贴文档里的接口段落。");
        EnterStep(ApiWizardStep.Document);

        // 池子清单要联网探一次（站点自己那份「有哪些模型、各自什么档位」）。先渲染文档那一版，
        // 探到之后再刷新报告——不让用户先看到一份空池子以为这家没东西。
        if (report.HasAnything) ProbeAsync();
    }

    private void RenderReport() =>
        reportText.Text = lastReport is null
            ? string.Empty
            : ApiImportSummary.RenderReport(lastReport, plan: null, site);

    /// <summary>
    /// 探测站点的池子清单。**全程只读**：只问「有哪些模型、各自什么档位」，不发任何生成请求
    /// （生成要花钱、出视频还要轮询，一个「看看有什么」的动作不该顺手起任务）。
    /// </summary>
    private async void ProbeAsync()
    {
        if (site is null || probing) return;
        probing = true;
        rebuildButton.IsEnabled = false;
        var previous = "正在探测站点的池子清单…";
        if (statusText.Text.Length == 0) SetStatus(previous);
        else AppendStatus(previous);

        try
        {
            var baseUrl = site.BaseUrl.Length > 0 ? site.BaseUrl : site.SourceUrl;
            var pools = new List<SitePool>();
            var sources = new List<string>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var candidate in SitePoolProbe.CandidateUrls(baseUrl))
            {
                var body = await GetTextAsync(candidate, budget.Token);
                if (body is null) continue;
                var found = SitePoolProbe.Parse(body);
                if (found.Count == 0) continue;
                sources.Add($"{candidate}（{found.Count} 个）");
                pools.AddRange(found);
            }

            // 一个来源都没认出池子时退回文档里的「可用模型」表：总比空着强，但要如实说明来源。
            if (pools.Count == 0) pools.AddRange(PoolsFromDoc(lastReport!));
            site = SiteFor(lastReport!, Dedupe(pools), string.Join("；", sources));
            RenderReport();
            AppendStatus(sources.Count > 0
                ? $"池子清单探到 {site.Pools.Count} 个（生图 {site.ImagePools.Count}、视频 {site.VideoPools.Count}），"
                  + $"来源：{string.Join("、", sources)}"
                : "没能从站点自己的清单接口探到池子，已退回文档里的「可用模型」表——这份通常更少也更旧，"
                  + "建议核对后再登记。");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            AppendStatus($"探测池子清单时出错（{error.Message}）；已退回文档里的「可用模型」表。");
        }
        finally
        {
            probing = false;
            rebuildButton.IsEnabled = true;
            EnterStep(ApiWizardStep.Document);
        }
    }

    private static async Task<string?> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>由文档解析结果拼出一个站点骨架（接口路径与基础地址来自文档）。</summary>
    private static SiteProfile SiteFor(ApiDocReport report, IReadOnlyList<SitePool> pools, string listSource)
    {
        var anchor = report.BaseUrl.Length > 0 ? report.BaseUrl : report.SourceUrl;
        return new SiteProfile
        {
            Id = SiteCatalog.IdFor(anchor),
            DisplayName = SiteCatalog.DefaultDisplayNameFor(anchor),
            BaseUrl = report.BaseUrl,
            SourceUrl = report.SourceUrl,
            ListSource = listSource,
            // 接口路径只认文档里**已经判定用途**的那几条；判不出用途的一律不写（写了就是猜）。
            ImagePath = report.ImageOps.FirstOrDefault(op => op.Capability == Capability.TextToImage)?.Path
                        ?? report.ImageOps.FirstOrDefault()?.Path ?? string.Empty,
            ImageEditPath = report.ImageOps.FirstOrDefault(op => op.Capability == Capability.ImageToImage)?.Path ?? string.Empty,
            VideoPath = report.VideoOps.FirstOrDefault()?.Path ?? string.Empty,
            Method = "POST",
            AuthStyle = "bearer",
            Pools = pools.ToList()
        };
    }

    /// <summary>文档里那份「可用模型」表 → 池子（探测不到站点清单时的退路）。</summary>
    private static IReadOnlyList<SitePool> PoolsFromDoc(ApiDocReport report) => report.ModelTable
        .SelectMany(entry =>
        {
            var isVideo = entry.IsVideo;
            var tiers = entry.Sizes.Count > 0 ? entry.Sizes : new[] { string.Empty }.ToList();
            return tiers.Select(tier =>
            {
                var (width, height) = isVideo ? (0, 0) : ApiDocAnalyzer.ParseSize(tier) ?? (0, 0);
                return new SitePool
                {
                    Model = entry.Name,
                    Kind = isVideo ? "video" : "image",
                    Tier = isVideo ? tier : tier.ToLowerInvariant(),
                    Width = width,
                    Height = height,
                    // 文档的「可用模型」表只说尺寸，从不说吃不吃参考图：这里如实留成未知，
                    // 让图生图去试、由服务端裁决，而不是先替它声称支持。
                    SupportsReference = null
                };
            });
        })
        .ToList();

    private static IReadOnlyList<SitePool> Dedupe(IEnumerable<SitePool> pools)
    {
        var unique = new List<SitePool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in pools)
            if (seen.Add($"{pool.Kind}|{pool.Key}")) unique.Add(pool);
        return unique;
    }

    private async Task<ApiDocRepairResult> RepairWithModelAsync()
    {
        var content = string.IsNullOrWhiteSpace(lastContent) ? pasteBox.Text : lastContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            SetStatus("没有可分析的正文：请先填网页地址点「读取网页」，或把接口说明粘到上面的输入框。");
            return ApiDocRepairResult.Failed("没有可分析的正文。");
        }
        if (jsonCompleter is null)
        {
            SetStatus("当前没有接入大模型，无法做这一步分析：请在「设置 → 模型接入」里配好文本模型与密钥后重试。");
            return ApiDocRepairResult.Failed("没有接入大模型。");
        }

        var source = lastReport?.SourceUrl ?? urlBox.Text.Trim();
        SetStatus("正在请大模型整理这份文档（只做整理，不会替你编内容）…");
        repairButton.IsEnabled = false;
        try
        {
            var result = await ApiDocRepair.RepairAsync(content, source, jsonCompleter);
            if (!result.Ok || result.Report is null)
            {
                SetStatus("大模型分析没有成功：" + result.Error
                    + (result.Raw.Length == 0
                        ? string.Empty
                        : Environment.NewLine + "模型原文（前 400 字）：" + Trim(result.Raw, 400)));
                return result;
            }

            Analyze(result.Report);
            AppendStatus("上面这些接口与模型来自大模型整理（不是本地规则解析的结果）：请核对无误后再点「创建技能」。");
            return result;
        }
        finally
        {
            repairButton.IsEnabled = true;
        }
    }

    private async Task<ApiAccountSnapshot> RefreshAccountAsync()
    {
        var baseUrl = lastReport?.BaseUrl ?? string.Empty;
        var apiKey = string.IsNullOrWhiteSpace(keyBox.Text) ? CurrentKeyForProbe() : keyBox.Text.Trim();
        if (baseUrl.Length == 0)
        {
            SetBalance("余额：还没解析出接口地址\n解析成功后自动查");
            return ApiAccountSnapshot.Failed("还没有接口地址。");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetBalance("余额：还没有密钥\n填好密钥后自动查");
            return ApiAccountSnapshot.Failed("还没有密钥。");
        }

        balanceButton.IsEnabled = false;
        SetBalance("余额：查询中…");
        try
        {
            var snapshot = await accountProbe(baseUrl, apiKey, CancellationToken.None);
            lastAccount = snapshot;

            if (snapshot.Ok && snapshot.Balance is { } balance)
            {
                var lines = new List<string> { balance.Describe() };
                if (snapshot.Models.Count > 0) lines.Add($"上线模型 {snapshot.Models.Count} 个");
                var temporary = balance.TemporaryNote();
                if (temporary.Length > 0) lines.Add(temporary);
                SetBalance(string.Join("\n", lines));
            }
            else
            {
                SetBalance("余额：查询失败\n" + Trim(snapshot.Error, 42));
            }

            // 上线模型列表顺带当一次交叉核对：文档里的池子如果在接口上已经下线，要如实说出来。
            if (lastReport is not null && site is not null && snapshot.Models.Count > 0)
            {
                var documented = site.Pools.Select(pool => pool.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var missing = snapshot.MissingFrom(documented);
                if (missing.Count > 0)
                    AppendStatus($"注意：登记的池子里有 {missing.Count} 个模型接口当前没上线，用它们调用会 404："
                        + string.Join("、", missing.Take(6))
                        + (missing.Count > 6 ? " 等" : string.Empty)
                        + "。可点「重新探测池子」按接口实际有的模型刷新一遍。");
            }
            return snapshot;
        }
        finally
        {
            balanceButton.IsEnabled = true;
        }
    }

    /// <summary>重新探一遍池子清单（首次解析时已经探过；这里是用户手动刷新）。</summary>
    private void ReprobeAsync()
    {
        if (lastReport is null || site is null)
        {
            SetStatus("请先解析文档。");
            return;
        }
        ProbeAsync();
    }

    /// <summary>
    /// 登记站点：把「这家有哪些接口、哪些池子」写成一个站点文件。
    ///
    /// 与旧端最大的差别在这里：**产物不是一串技能文件**。池子是站点下的选项，不是技能——
    /// 一家站 20 个模型 × 3 档就是 60 个池子，逐个建文件只会把技能目录刷满，
    /// 而且用户真正要回答的问题是「这家有哪些能用的」，那是一个站点的问题。
    /// </summary>
    private async Task CreateSite()
    {
        if (site is null || lastReport is null)
        {
            SetStatus("请先读取或粘贴接口说明并完成解析。");
            return;
        }
        if (site.Pools.Count == 0)
        {
            SetStatus("这个站点还没有可用的池子（探测失败且文档里也没有「可用模型」表）："
                + "先确认地址与文档对不对，或到「设置 → 模型接入」里手工填一份配置。");
            return;
        }

        // 识别完**就地**要密钥。上一轮用户的反馈正是「key 放哪里，设置里没看到」——
        // 因为唯一入口藏在跟站点看不出关系的字段里。这里问一次，并明确告诉他以后去哪儿改。
        var typed = await PromptAsync(window, "给这一家填密钥",
            $"已识别站点「{site.Label}」：生图 {site.ImagePools.Count} 个池子、视频 {site.VideoPools.Count} 个。"
            + Environment.NewLine + "粘贴这一家的 API Key（加密落盘，界面不回显）。"
            + Environment.NewLine + "留空也行 —— 之后随时可以在「设置 → 技能管理 → 站点与池子」里补。",
            string.Empty, secret: true);

        if (typed is not null && typed.Trim().Length > 0)
        {
            site.ApiKey = typed.Trim();
            site.ApiKeyUnreadable = false;
        }

        if (!SiteCatalog.Save(site, out var failure))
        {
            SetStatus($"站点没能写入（技能目录可能不可写）：{failure}" + Environment.NewLine
                + $"目标目录：{SiteCatalog.Directory}");
            return;
        }
        wroteSite = true;

        // 交给 SaveKey 走同一套写入：它写配置（地址 / 模型 / 密钥）、把站点一并落盘、然后进最小测试。
        // 密钥只需要在这一个地方输一次，不是「弹窗问一遍、下一页的输入框再问一遍」。
        keyBox.Text = site.ApiKey;
        PrepareKeyStep();
        EnterStep(ApiWizardStep.Key);
        if (!SaveKey()) return;

        AppendStatus($"站点「{site.Label}」已登记：生图 {site.ImagePools.Count} 个池子、视频 {site.VideoPools.Count} 个池子。"
            + Environment.NewLine + $"· 密钥：{(site.HasApiKey ? "已加密保存 " + SecretProtector.Describe(site.ApiKey) : "没填（调用时会退回设置里的图像接口密钥）")}"
            + Environment.NewLine + "· 以后要改密钥 / 看池子 / 删站点：**设置 → 技能管理 → 站点与池子**"
            + Environment.NewLine + "· 出图：画布节点上右键「运行技能…」，会先让你在这些池子里选一个");
    }

    private bool SaveKey()
    {
        if (lastReport is null || site is null)
        {
            SetStatus("请先读取接口说明并登记站点。");
            return false;
        }

        var applied = ApplyConfiguredProviders(keyBox.Text.Trim());
        if (applied.Count == 0)
        {
            // 没变化也是成功：用户可能只是点了一次确认。
            savedConfig = true;
            SetStatus("密钥与接口地址和现有配置一致，没有需要写入的改动。");
            PrepareTestStep();
            EnterStep(ApiWizardStep.Test);
            return true;
        }

        if (!AiProviderSettings.Save(config))
        {
            // 保存失败必须如实报出，不能显示成功。
            SetStatus("配置写入失败（配置文件可能不可写或磁盘只读），本次密钥未生效："
                + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
                + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}");
            return false;
        }

        // 站点文件跟着一起落盘：配置写成了、站点文件没写成的话，「密钥已生效」这句话就是假的。
        if (site is not null && !SiteCatalog.Save(site, out var siteFailure))
        {
            SetStatus($"配置已写入，但站点文件没能写入（{siteFailure}）：这一家的密钥没有生效。"
                + Environment.NewLine + $"站点目录：{SiteCatalog.Directory}");
            return false;
        }

        savedConfig = true;
        SetStatus("已加密保存 " + applied.Count + " 项："
            + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
            + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}（密钥为加密存储）");
        PrepareTestStep();
        EnterStep(ApiWizardStep.Test);
        return true;
    }

    private void CompleteWithoutTest()
    {
        SetStatus("已按你的选择跳过最小测试：技能与密钥都已保存完成。"
            + Environment.NewLine + "到画布上选中一个实体或节点，运行技能即可使用这些新技能。");
        EnterStep(ApiWizardStep.Done);
    }

    private async Task TestMinimalAsync()
    {
        if (lastReport is null)
        {
            SetStatus("请先读取接口说明并创建技能。");
            return;
        }

        var plan = ApiMinimalTest.Plan(lastReport);
        if (!plan.CanRun)
        {
            // 文档里只有图生图 / 图生视频：凭空测不了，如实说明而不是硬跑一次注定失败的调用。
            SetStatus(plan.Reason + Environment.NewLine + "技能与密钥都已保存完成，可直接在画布上运行。"
                + (plan.Warning.Length > 0 ? Environment.NewLine + plan.Warning : string.Empty));
            EnterStep(ApiWizardStep.Done);
            return;
        }

        var op = plan.Op!;
        var balanceBefore = lastAccount?.Balance?.Balance;
        SetStatus($"正在做最小测试：{plan.Reason}");
        testYesButton.IsEnabled = false;
        try
        {
            if (plan.Kind == ApiMinimalTestKind.Image) await RunImageTestAsync(plan, op);
            else await RunVideoTestAsync(plan, op);
        }
        finally
        {
            testYesButton.IsEnabled = true;
        }

        // 跑完再查一次余额：既刷新右上角的显示，也能算出这一次到底花了多少。
        var snapshot = await RefreshAccountAsync();
        var balanceAfter = snapshot.Balance?.Balance;
        lastTestCost = balanceBefore is not null && balanceAfter is not null ? balanceBefore - balanceAfter : null;
        if (lastTestCost is { } cost && cost != 0)
            AppendStatus($"本次最小测试消耗 {cost} 积分" + (balanceAfter is null ? "。" : $"，当前余额 {balanceAfter}。"));

        EnterStep(ApiWizardStep.Done);
    }

    private async Task RunImageTestAsync(ApiMinimalTestPlan plan, ApiOpCandidate op)
    {
        var provider = imageProviderFactory();
        if (!provider.IsConfigured)
        {
            SetStatus("最小测试未执行：尚未配置图像模型。"
                + Environment.NewLine + "请在本页填好图像接口地址与模型后重试；技能与密钥都已保存完成。");
            return;
        }

        var (width, height) = ApiMinimalTest.SmallestSize(op);
        // 模型名与执行配置由 ApiMinimalTest 给出：接口自己声明的优先，执行配置与正式运行共用一份，
        // 所以最小测试打的确实是这条导入接口的地址、路径、方法与鉴权。
        var model = plan.Model;
        var prompt = "最小测试图：一张浅灰色背景上的白色圆形，用于确认接口可用。";
        ImageGenerationResult result;
        try
        {
            result = await provider.GenerateAsync(new ImageGenerationRequest
            {
                Prompt = prompt,
                Model = model,
                BaseUrl = plan.Endpoint?.BaseUrl ?? string.Empty,
                EndpointPath = plan.Endpoint?.Path ?? string.Empty,
                Method = plan.Endpoint?.Method ?? string.Empty,
                AuthStyle = plan.Endpoint?.AuthStyle ?? string.Empty,
                Width = width,
                Height = height
            });
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            result = new ImageGenerationResult { Status = ImageGenerationStatus.Failed, Provider = provider.Name, Model = model, Error = error.Message };
        }

        if (result.Status != ImageGenerationStatus.Succeeded)
        {
            SetStatus("最小测试失败（技能与密钥都已保存完成，不影响后续使用）："
                + Environment.NewLine + "· " + result.Error
                + (model.Length == 0 ? string.Empty : Environment.NewLine + "· 使用的模型：" + model)
                + (plan.Warning.Length > 0 ? Environment.NewLine + "· " + plan.Warning : string.Empty));
            return;
        }

        returnedMediaPath = result.FilePath;
        ShowPreview(result.FilePath, isVideo: false);
        SetStatus("最小测试成功，接口可用，已返回图片："
            + Environment.NewLine + "· 文件：" + result.FilePath
            + Environment.NewLine + "· 提供方：" + result.Provider + "｜模型：" + result.Model + $"｜画幅 {width}x{height}"
            + Environment.NewLine + "· 提示词：" + prompt);
    }

    private async Task RunVideoTestAsync(ApiMinimalTestPlan plan, ApiOpCandidate op)
    {
        var provider = videoProviderFactory();
        if (!provider.IsConfigured)
        {
            // 视频链路没配好：如实说明，不拿别的文件冒充视频。
            SetStatus("最小测试未执行：出视频链路当前不可用。"
                + Environment.NewLine + "· " + plan.Reason
                + Environment.NewLine + "· 技能与密钥都已保存完成；在「设置 → 生图与生视频 → 视频接口」里填上地址与模型后，这些技能就能直接运行。"
                + (plan.Warning.Length > 0 ? Environment.NewLine + "· " + plan.Warning : string.Empty));
            return;
        }

        var (width, height) = ApiMinimalTest.SmallestSize(op);
        var result = await provider.GenerateAsync(new VideoGenerationRequest
        {
            Prompt = "最小测试：一段静态画面，用于确认出视频接口可用。",
            Model = plan.Model,
            Width = width,
            Height = height,
            Seconds = config.VideoDefaultSeconds
        });

        if (result.Status != VideoGenerationStatus.Succeeded)
        {
            SetStatus("最小测试失败（技能与密钥都已保存完成）：" + Environment.NewLine + "· " + result.Error);
            return;
        }

        returnedMediaPath = result.FilePath;
        ShowPreview(result.FilePath, isVideo: true);
        SetStatus("最小测试成功，出视频接口可用，已返回视频："
            + Environment.NewLine + "· 文件：" + result.FilePath
            + Environment.NewLine + "· 提供方：" + result.Provider + "｜模型：" + result.Model);
    }

    private void ShowPreview(string path, bool isVideo)
    {
        // 上一次的位图要显式放掉：Bitmap 持有解码后的像素缓冲，反复测几次不释放会一直涨着。
        if (previewImage.Source is IDisposable previous) previous.Dispose();
        previewImage.Source = null;
        previewImage.IsVisible = false;
        openFileButton.IsVisible = false;
        if (!File.Exists(path)) return;

        // 视频不做内嵌预览（要另一套解码依赖），给一个「打开产出文件」交给系统播放器。
        openFileButton.IsVisible = true;
        if (isVideo) return;

        try
        {
            // 读成内存流再解：直接按路径构造 Bitmap 在某些后端上会一直占着文件句柄，
            // 用户想删/想改名这张测试图时会被拒。
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            previewImage.Source = new Bitmap(stream);
            previewImage.IsVisible = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 预览失败不影响结论：文件路径已经如实报出。
            AppendStatus("（预览这张图失败：" + error.Message + "；文件本身已生成，路径见上。）");
        }
    }

    private void OpenFile()
    {
        if (returnedMediaPath.Length == 0 || !File.Exists(returnedMediaPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(returnedMediaPath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppendStatus("打不开文件（" + error.Message + "）。路径：" + returnedMediaPath);
        }
    }

    // ---------- 写配置 ----------

    /// <summary>
    /// 按识别到的类型写入地址、模型与密钥。
    ///
    /// 密钥的去处与旧端**不同**：出图 / 出视频各自写在 <c>ImageApiKey</c> / <c>VideoApiKey</c> 上，
    /// 只有文档里根本没有出图出视频接口（退化成文本接口）时才写顶层 <c>ApiKey</c>。
    /// 旧端一律写顶层，于是「导入一个出图接口」会顺手把聊天模型的密钥覆盖掉——而这两者常常不是同一个账号。
    /// </summary>
    private IReadOnlyList<string> ApplyConfiguredProviders(string apiKey)
    {
        var applied = new List<string>();
        var baseUrl = lastReport?.BaseUrl ?? string.Empty;
        var imageModel = PreferredModel(lastReport, isVideo: false);
        var videoModel = PreferredModel(lastReport, isVideo: true);
        var hasImage = lastReport is not null && (lastReport.ImageOps.Count > 0 || lastReport.ImageModels.Count > 0);
        var hasVideo = lastReport is not null && (lastReport.VideoOps.Count > 0 || lastReport.VideoModels.Count > 0);
        var key = apiKey.Trim();

        if (hasImage)
        {
            // 交给 ProviderImporter 时密钥传空：它会把密钥写进顶层 ApiKey，而这里要写到出图那一份上。
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.ImageApi, baseUrl, string.Empty, imageModel, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
            if (key.Length > 0 && !string.Equals(config.ImageApiKey, key, StringComparison.Ordinal))
            {
                config.ImageApiKey = key;
                applied.Add($"图像接口密钥 = {SecretProtector.Describe(key)}");
            }
        }

        if (hasVideo)
        {
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.VideoApi, baseUrl, string.Empty, videoModel, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
            if (key.Length > 0 && !string.Equals(config.VideoApiKey, key, StringComparison.Ordinal))
            {
                config.VideoApiKey = key;
                applied.Add($"视频接口密钥 = {SecretProtector.Describe(key)}");
            }
        }

        // 密钥同时挂到**站点**上：一家站一把账号，而站点才是它以后被调用的地方。
        // 只写全局的 ImageApiKey 的话，导入第二家站会把第一家的密钥覆盖掉，第一家那批池子从此全部 401。
        // 这里只改内存，落盘在 SaveKey 里一并做——站点文件的写入不该藏在一段「写配置」的方法里。
        if (site is not null && key.Length > 0 && !string.Equals(site.ApiKey, key, StringComparison.Ordinal))
        {
            site.ApiKey = key;
            site.ApiKeyUnreadable = false;
            applied.Add($"站点「{site.Label}」的密钥 = {SecretProtector.Describe(key)}");
        }

        if (!hasImage && !hasVideo)
        {
            // 文档里既没有出图也没有出视频接口：退化成「这一页其实想配的是文本接口」。
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.TextApi, baseUrl, key, string.Empty, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
        }

        return applied;
    }

    /// <summary>
    /// 该写进设置里的模型名：先用接口自己声明的，没有就用文档「可用模型」表里同类模型的第一个。
    /// 表里第一个通常就是文档主推的模型。
    /// </summary>
    private static string PreferredModel(ApiDocReport? report, bool isVideo)
    {
        if (report is null) return string.Empty;
        var ops = isVideo ? report.VideoOps : report.ImageOps;
        if (ops.SelectMany(op => op.Models).FirstOrDefault() is { Length: > 0 } declared) return declared;
        var models = isVideo ? report.VideoModels : report.ImageModels;
        return models.FirstOrDefault()?.Name ?? string.Empty;
    }

    /// <summary>查余额时用哪把密钥：先看这一页配置的密钥，再退到已保存的聊天模型密钥。</summary>
    private string CurrentKeyForProbe()
    {
        if (config.ImageApiKey.Length > 0) return config.ImageApiKey;
        if (config.VideoApiKey.Length > 0) return config.VideoApiKey;
        return config.ApiKey;
    }

    private void PrepareKeyStep()
    {
        keyBox.Text = string.Empty;
        var baseUrl = lastReport?.BaseUrl ?? string.Empty;
        var imageModel = PreferredModel(lastReport, isVideo: false);
        var videoModel = PreferredModel(lastReport, isVideo: true);
        var parts = new List<string>();
        if (baseUrl.Length > 0) parts.Add($"接口地址 {baseUrl}");
        if (imageModel.Length > 0) parts.Add($"图像模型 {imageModel}");
        if (videoModel.Length > 0) parts.Add($"视频模型 {videoModel}");
        keyHint.Text = parts.Count == 0
            ? "文档里没有解析到接口地址与模型名：密钥会保存到本机配置，地址与模型请在本页手动补。"
            : "将写入：" + string.Join("｜", parts) + "（要改地址与模型就在这一页改，改完点窗口的「保存」）";
    }

    private void PrepareTestStep()
    {
        questionLabel.Text = BuildQuestion();
        testYesButton.IsEnabled = lastReport is not null;
    }

    private string BuildQuestion()
    {
        if (lastReport is null) return "要不要现在做一次最小测试？";
        var plan = ApiMinimalTest.Plan(lastReport);
        var target = plan.CanRun
            ? $"{plan.Capability}（{plan.Op!.Method} {plan.Op.Path}）"
            : "（这份文档里的接口都需要参考图，无法凭空测）";
        return $"技能已创建、密钥已加密保存。要不要现在做一次最小测试？测的是 {target}。"
            + "选「是」会真的调一次接口，成功就把图返回给你；选「否」就保存完成。";
    }

    private void EnterStep(ApiWizardStep step)
    {
        keySection.IsVisible = step is ApiWizardStep.Key or ApiWizardStep.Test or ApiWizardStep.Done;
        testSection.IsVisible = step is ApiWizardStep.Test or ApiWizardStep.Done;
        testYesButton.IsVisible = step == ApiWizardStep.Test;
        testNoButton.IsVisible = step == ApiWizardStep.Test;
        // 解析出接口之后就一直摆着：站点会加模型、也会下线模型，随时可以重探一遍。
        rebuildButton.IsVisible = lastReport is not null;

        (stepLabel.Text, stepHint.Text) = step switch
        {
            ApiWizardStep.Document => ("第 1 步 / 共 4 步：给一个接口说明网页",
                "填文档地址点「读取网页」；页面由脚本渲染而抓不到内容时，把文档里的接口段落粘到下面再点「分析这段文字」。"
                + "本地规则解析不出来时，可以点「让大模型分析」请已接入的文本模型把正文整理成结构化 JSON（只做整理，不替你编内容）。"),
            // 进到这一步就直接把光标放进密钥框：这一屏用户唯一要做的事就是贴密钥，
            // 顺带也让滚动区自动滚到这儿（否则刚出现的区域可能在窗口下沿之外）。
            ApiWizardStep.Key => ("第 2 步 / 共 4 步：输入接口密钥",
                "密钥只保存在本机，写入时按当前平台的方案加密；界面与日志都不会回显完整密钥。"),
            ApiWizardStep.Test => ("第 3 步 / 共 4 步：是否做一次最小测试",
                "选「是」会真的调一次接口，成功就把图返回来；选「否」直接保存完成。"),
            _ => comfyDraft is not null
                ? ("第 2 步 / 共 2 步：完成",
                    "工作流已按份存好，站点也登记了。要换哪一份、或看这一台有哪些，" +
                    "去「设置 → 技能管理 → 站点与池子」；出图时在选择器里挑。")
                : ("第 4 步 / 共 4 步：完成",
                    "技能已建好、密钥已加密保存。要改地址或模型可以随时在本页改，或回「设置」改。")
        };

        if (step == ApiWizardStep.Key) keyBox.Focus();
    }

    // ---------- 小零件 ----------

    /// <summary>
    /// 状态区写一行。
    ///
    /// **必须能跨线程调用**：库那边的进度回调不保证落在 UI 线程上——`ComfyUiVirtualNodeLearner.LearnAsync`
    /// 内部的 await 带 `ConfigureAwait(false)`，于是它在工作线程上调回调。直接写 TextBlock 会抛
    /// 「Call from invalid thread」，**把整个应用带走**（实测：点「让大模型认一认」必崩；而无头测试
    /// 看不见这个，因为探针那条路的回调写的是 Console）。所以这里统一兜一层：不在 UI 线程就 Post 回去写。
    /// </summary>
    private void SetStatus(string text) => OnUiThread(() =>
    {
        statusText.Text = text;
        if (comfySelectionStatus is { } target) target.Text = text;
    });

    /// <summary>往状态区追加一行（保留已有内容，便于看到完整过程）。</summary>
    private void AppendStatus(string line) => OnUiThread(() =>
    {
        statusText.Text = string.IsNullOrEmpty(statusText.Text) ? line : statusText.Text + Environment.NewLine + line;
        if (comfySelectionStatus is { } target) target.Text = statusText.Text;
    });

    /// <summary>在 UI 线程上执行；已经在上面就直接跑，免得每次进度更新都被推迟一拍。</summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";

    /// <summary>二级说明行：比 <see cref="AgentDialogUi.Header"/> 轻，用来紧跟在小标题下面讲一句。</summary>
    private static TextBlock Header2(string text) => new()
    {
        Text = text,
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("DfInk3")
    };

    private static SelectableTextBlock CodeText() => new()
    {
        FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
        FontSize = 11,
        Foreground = Brush("DfInk2"),
        TextWrapping = TextWrapping.NoWrap
    };

    private static Border CodeBox(SelectableTextBlock text, double height) => new()
    {
        Height = height,
        Background = Brush("DfBg"),
        BorderBrush = Brush("DfLine"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10),
        Child = new ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        }
    };
}
