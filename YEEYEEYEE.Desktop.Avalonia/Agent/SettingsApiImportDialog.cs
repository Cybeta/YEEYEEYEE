using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
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
/// · **不伪造产物**：最小测试真的调一次接口；出视频链路尚未接入实现时明说，不拿别的文件冒充视频；
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

    /// <summary>识别成 ComfyUI 时的结论（非空表示走的是 ComfyUI 那条分支，不登记站点）。</summary>
    private ProviderImportDraft? comfyDraft;
    private bool probing;
    private ApiAccountSnapshot? lastAccount;
    private long? lastTestCost;
    private bool wroteSite;
    private bool savedConfig;
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
        Watermark = "https://服务商文档地址——接口说明所在的那一页"
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
        var window = DialogShell.Create("智能导入：从一个接口说明网页建出技能与配置", dialog.Build(), 900, 640);
        dialog.window = window;
        dialog.Wire(window);
        return await window.ShowDialog<string?>(owner);
    }

    // ---------- 组装 ----------

    private void Wire(Window window)
    {
        fetchButton.Click += async (_, _) => await AnalyzeUrlAsync(urlBox.Text);
        analyzeButton.Click += (_, _) => AnalyzeContent(pasteBox.Text, urlBox.Text);
        repairButton.Click += async (_, _) => await RepairWithModelAsync();
        createButton.Click += async (_, _) =>
        {
            // 同一个按钮两种意思：ComfyUI 那条分支写设置，接口站那条分支登记站点。
            if (comfyDraft is not null) { WriteComfyUi(); return; }
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
            e.Cancel = true;
            closing = true;
            window.Close(BuildSummary());
        };
    }

    private Control Build()
    {
        createButton.IsEnabled = false;
        createButton.IsVisible = false;
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
                new TextBlock { Text = "接口说明网页地址", FontSize = 11, Foreground = Brush("DfInk2") },
                urlRow,
                new TextBlock
                {
                    Text = "抓不到正文时（文档站由前端脚本渲染）不用换地方：在浏览器里打开该文档，把接口段落粘到下面。",
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("DfInk3")
                },
                pasteBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { analyzeButton, repairButton, createButton }
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
        bottom.Children.Add(CodeBox(statusText, 128));
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
        return root;
    }

    private void CloseWithSummary()
    {
        closing = true;
        window?.Close(BuildSummary());
    }

    /// <summary>给调用方的一行结论。**什么都没写成时返回 null**，让外壳知道不必刷新、也不必报「导入成功」。</summary>
    private string? BuildSummary()
    {
        if (!wroteSite && !savedConfig) return null;

        var parts = new List<string>();
        if (wroteSite) parts.Add($"站点已登记（{SiteCatalog.Directory}）");
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
        try
        {
            var fetched = await fetcher(target, CancellationToken.None);
            if (!fetched.Ok)
            {
                SetStatus(fetched.Error);
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
        }
    }

    private ApiDocReport AnalyzeContent(string? content, string? sourceUrl)
    {
        lastContent = content ?? string.Empty;

        // 先问一句「这是不是 ComfyUI」。
        //
        // ComfyUI **不是「一家有接口站」**：它跑的是本机 / 远程的工作流模板，用哪个模型、走几步、
        // 要不要参考图全由模板决定，没有「模型 × 档位」可挑。所以它走另一条分支（只写设置、不登记站点）。
        //
        // 这个识别器本来就写好了（`ProviderImporter.Inspect`：`/object_info`、`/prompt`、`/queue`、
        // `/system_stats`、`clientid=`、`:8188`、产品名都能认），只是这个窗口一直没用它——
        // 只用了它的「写入」那一半（`Apply`），于是贴一段 ComfyUI 的说明进来会被当成画图接口。
        var draft = ProviderImporter.Inspect(lastContent);
        if (draft.Kind == ProviderKind.ComfyUi)
        {
            ShowComfyUi(draft);
            // 这条分支不产出接口报告（没有要解析的接口清单）；返回一份空报告，调用方只看成功与否。
            return ApiDocAnalyzer.Analyze(null, null);
        }
        comfyDraft = null;
        createButton.Content = "登记站点";
        rebuildButton.IsVisible = true;

        var report = ApiDocAnalyzer.Analyze(content, sourceUrl ?? urlBox.Text.Trim());
        Analyze(report);
        return report;
    }

    /// <summary>
    /// ComfyUI 分支的结论页。**不建站点、不探测清单**，只把地址与 checkpoint 写进设置。
    /// </summary>
    private void ShowComfyUi(ProviderImportDraft draft)
    {
        comfyDraft = draft;
        lastReport = null;
        site = null;

        var lines = new List<string>
        {
            "—— 识别结果：ComfyUI ——",
            $"· 地址：{(draft.BaseUrl.Length > 0 ? draft.BaseUrl : "（这段文字里没有地址）")}",
            $"· checkpoint：{(draft.Checkpoint.Length > 0 ? draft.Checkpoint : "（没提到；留空即用工作流模板自己声明的那一个）")}"
        };
        if (draft.Signals.Count > 0) lines.Add($"· 判据：{string.Join("、", draft.Signals.Take(6))}");
        lines.Add(string.Empty);
        lines.Add("ComfyUI 不是「一家有接口站」：它跑的是本机 / 远程的工作流模板，");
        lines.Add("用哪个模型、走几步、吃几张参考图，全由模板决定，没有「模型 × 档位」能在这里挑。");
        lines.Add("所以这条**不登记站点、也不探测模型清单**，只把地址与 checkpoint 写进设置；工作流模板在那一页选。");
        reportText.Text = string.Join(Environment.NewLine, lines);

        createButton.Content = "写入 ComfyUI 设置";
        createButton.IsEnabled = draft.BaseUrl.Length > 0;
        createButton.IsVisible = true;
        rebuildButton.IsVisible = false;
        SetStatus(draft.BaseUrl.Length > 0
            ? "识别为 ComfyUI：点「写入 ComfyUI 设置」把地址与 checkpoint 写进设置（不登记站点）。"
            : "识别为 ComfyUI，但这段文字里没有能用的地址：把控制台首页地址、或含 http://…:8188 的那一行一起贴进来。");
        EnterStep(ApiWizardStep.Document);
    }

    private void WriteComfyUi()
    {
        if (comfyDraft is null || lastReport is not null) return;
        var applied = ProviderImporter.Apply(config, comfyDraft);
        if (applied.Count == 0)
        {
            SetStatus("ComfyUI 地址与 checkpoint 和现有配置一致，没有需要写入的改动。");
            EnterStep(ApiWizardStep.Done);
            return;
        }
        if (!AiProviderSettings.Save(config))
        {
            SetStatus("配置写入失败（配置文件可能不可写或磁盘只读），本次没有生效："
                + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item)));
            return;
        }
        savedConfig = true;
        SetStatus("已写入 ComfyUI 设置：" + Environment.NewLine
            + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
            + Environment.NewLine + "以后要改：设置 → 生图与生视频 → ComfyUI。工作流模板也在那一页选。");
        EnterStep(ApiWizardStep.Done);
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
            // 出视频执行方还没接入：如实说明，不拿别的文件冒充视频。
            SetStatus("最小测试未执行：出视频链路当前不可用。"
                + Environment.NewLine + "· " + plan.Reason
                + Environment.NewLine + "· 技能与密钥都已保存完成；等接入出视频执行方后可直接运行这些技能。"
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
            _ => ("第 4 步 / 共 4 步：完成",
                "技能已建好、密钥已加密保存。要改地址或模型可以随时在本页改，或回「设置」改。")
        };

        if (step == ApiWizardStep.Key) keyBox.Focus();
    }

    // ---------- 小零件 ----------

    private void SetStatus(string text) => statusText.Text = text;

    /// <summary>往状态区追加一行（保留已有内容，便于看到完整过程）。</summary>
    private void AppendStatus(string line)
    {
        statusText.Text = statusText.Text.Length == 0
            ? line
            : statusText.Text + Environment.NewLine + line;
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
