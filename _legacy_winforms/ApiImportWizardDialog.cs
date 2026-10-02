using System.Net.Http;

namespace DreamForge.Desktop;

/// <summary>向导当前的阶段。</summary>
public enum ApiWizardStep { Document, Key, Test, Done }

/// <summary>
/// 接口导入向导（用户要求）：窗口打开后先给一个「接口说明网页」→ 自动按页面上的接口创建
/// api 生图 / api 生视频技能（并按文档里的模型与尺寸档位派生池子子技能）→ 再让用户输入密钥
/// （加密落盘）→ 最后询问是否做一次最小测试：是就真的调一次接口并把图返回来，否就保存完成。
///
/// 三条边界：
/// · **每一步都如实报出**：抓不到页面、没解析出接口、配置写不进去、测试失败，都显示真实原因，不显示成功；
/// · **不伪造产物**：最小测试真的调用接口；出视频链路尚未接入实现时明说，不拿别的文件冒充视频；
/// · **只写该写的**：密钥走 <see cref="SecretProtector"/> 加密落盘，界面绝不回显完整密钥。
/// </summary>
public sealed class ApiImportWizardDialog : ScaledForm
{
    private readonly AiProviderConfig config;
    private readonly Func<string, CancellationToken, Task<ApiDocFetchResult>> fetcher;
    private readonly IImageProvider? imageProvider;
    private readonly IVideoProvider? videoProvider;
    private readonly IAiJsonCompleter? jsonCompleter;
    private readonly Func<string, string, CancellationToken, Task<ApiAccountSnapshot>> accountProbe;
    private readonly Action? onApplied;

    private readonly Label stepLabel = new();
    private readonly Label stepHint = new();
    private readonly Label balanceLabel = new();
    private readonly TextBox urlBox = new();
    private readonly Button fetchButton = new();
    private readonly TextBox pasteBox = new();
    private readonly Button analyzeButton = new();
    private readonly Button repairButton = new();
    private readonly TextBox reportBox = new();
    private readonly Button createButton = new();
    private readonly TableLayoutPanel keyPanel = new();
    private readonly TextBox keyBox = new();
    private readonly Label keyHint = new();
    private readonly Button saveKeyButton = new();
    private readonly TableLayoutPanel testPanel = new();
    private readonly Label questionLabel = new();
    private readonly Button testYesButton = new();
    private readonly Button testNoButton = new();
    private readonly Button rebuildButton = new();
    private readonly TextBox statusBox = new();
    private readonly PictureBox previewBox = new();

    /// <summary>最近一次解析用的原始正文（大模型修正要用它）。</summary>
    private string lastContent = string.Empty;

    public ApiImportWizardDialog(
        AiProviderConfig? config = null,
        Func<string, CancellationToken, Task<ApiDocFetchResult>>? fetcher = null,
        IImageProvider? imageProvider = null,
        IVideoProvider? videoProvider = null,
        Action? onApplied = null,
        IAiJsonCompleter? jsonCompleter = null,
        Func<string, string, CancellationToken, Task<ApiAccountSnapshot>>? accountProbe = null)
    {
        this.config = config ?? AiProviderSettings.Load();
        this.fetcher = fetcher ?? ((url, token) => ApiDocFetcher.FetchAsync(url, null, token));
        this.imageProvider = imageProvider;
        this.videoProvider = videoProvider;
        this.onApplied = onApplied;
        this.jsonCompleter = jsonCompleter ?? AiProviderFactory.CreateJsonCompleter();
        this.accountProbe = accountProbe ?? ((url, key, token) => ApiAccountProbe.FetchAsync(url, key, null, token));

        Text = "接口导入向导（说明网页 → 技能 → 密钥 → 最小测试）";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 780);
        Font = Theme.UiFont;
        BackColor = Theme.PanelBg;

        BuildDocumentPanel(out var documentPanel);
        BuildReportBox();
        BuildKeyPanel();
        BuildTestPanel();

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(0) };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.Controls.Add(HeaderPanel(), 0, 0);
        main.Controls.Add(documentPanel, 0, 1);
        main.Controls.Add(reportBox, 0, 2);
        main.Controls.Add(keyPanel, 0, 3);
        main.Controls.Add(testPanel, 0, 4);
        main.Controls.Add(StatusRow(), 0, 5);

        var closeButton = new Button { Text = "关闭", Width = 84, Height = 32 };
        closeButton.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        buttons.Controls.Add(closeButton);

        Controls.Add(main);
        Controls.Add(buttons);
        buttons.BringToFront();

        EnterStep(ApiWizardStep.Document);
    }

    /// <summary>最近一次解析报告。</summary>
    public ApiDocReport? LastReport { get; private set; }

    /// <summary>根据报告算出的技能清单（创建之前就能给用户看）。</summary>
    public ApiSkillPlan? LastPlan { get; private set; }

    /// <summary>最近一次创建技能实际写入的文件。</summary>
    public IReadOnlyList<string> LastWrittenFiles { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次创建技能失败的文件与原因。</summary>
    public IReadOnlyList<string> LastWriteErrors { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次保存密钥写入的字段描述。</summary>
    public IReadOnlyList<string> LastApplied { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次保存密钥是否成功（未点过时为 null）。</summary>
    public bool? LastKeySaveSucceeded { get; private set; }

    /// <summary>最近一次最小测试的结果（没测过为 null，与「测了但失败」区分开）。</summary>
    public bool? LastTestSucceeded { get; private set; }

    /// <summary>最小测试返回的图片或视频的本机路径；没有时为 empty。</summary>
    public string LastReturnedMediaPath { get; private set; } = string.Empty;

    /// <summary>问过用户的那句话（是否做最小测试）。</summary>
    public string LastQuestion { get; private set; } = string.Empty;

    /// <summary>最近一次账号查询结果（余额 + 上线模型）；没查过时为 null。</summary>
    public ApiAccountSnapshot? LastAccount { get; private set; }

    /// <summary>最近一次查到的余额；查不到时为 null（不拿 0 冒充）。</summary>
    public ApiBalance? LastBalance => LastAccount?.Balance;

    /// <summary>文档里写过、但接口当前没有上线的模型（调用会 404）；没有差异时为空。</summary>
    public IReadOnlyList<string> LastModelMismatch { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次最小测试实际消耗的积分；余额对不上时为 null。</summary>
    public long? LastTestCost { get; private set; }

    /// <summary>最近一次「让大模型分析」的结果。</summary>
    public ApiDocRepairResult? LastRepair { get; private set; }

    public ApiWizardStep CurrentStep { get; private set; } = ApiWizardStep.Document;

    /// <summary>状态区文本，便于回归与冒烟检查。</summary>
    public string LastSummary => statusBox.Text;

    /// <summary>报告区文本（识别到的接口与将要创建的技能清单），便于回归与冒烟检查。</summary>
    public string ReportPreview() => reportBox.Text;

    /// <summary>识别到的接口说明网页地址。</summary>
    public string SourceUrl => LastReport?.SourceUrl ?? urlBox.Text.Trim();

    // ── 对外可调用的流程（按钮与测试都走这里） ───────────────────────────────

    /// <summary>抓取网页并解析。</summary>
    public async Task<bool> AnalyzeUrlAsync(string? url)
    {
        var target = (url ?? string.Empty).Trim();
        urlBox.Text = target;
        statusBox.Text = $"正在读取 {target} …";
        var fetched = await fetcher(target, CancellationToken.None);
        if (!fetched.Ok)
        {
            statusBox.Text = fetched.Error;
            return false;
        }

        lastContent = fetched.Content;
        AnalyzeContent(fetched.Content, target);
        return true;
    }

    /// <summary>解析一段文档正文（网页抓不到内容时由用户粘贴）。</summary>
    public ApiDocReport AnalyzeContent(string? content, string? sourceUrl)
    {
        lastContent = content ?? string.Empty;
        var report = ApiDocAnalyzer.Analyze(content, sourceUrl ?? urlBox.Text.Trim());
        Analyze(report);
        return report;
    }

    /// <summary>用一份解析报告刷新界面（便于测试直接注入报告）。</summary>
    public void Analyze(ApiDocReport report)
    {
        LastReport = report;
        LastPlan = ApiSkillFactory.Build(report);
        reportBox.Text = ApiImportSummary.RenderReport(report, LastPlan);
        createButton.Enabled = LastPlan.HasAnything;
        statusBox.Text = report.HasAnything
            ? $"解析完成：出图 {report.ImageOps.Count} 条、出视频 {report.VideoOps.Count} 条；可创建 {LastPlan.Skills.Count} 条技能。"
            : "没有解析出可用的出图或出视频接口：请确认地址指向接口说明页，或改贴文档里的接口段落。";
        UpdateModelMismatch();
        EnterStep(ApiWizardStep.Document);
    }

    /// <summary>
    /// 请大模型把正文整理成结构化 JSON（用户要求：智能导入失败时允许调用大模型分析修正）。
    /// 这是**兜底**：本地规则解析优先，只有规则解析不出东西时才用；结果会明确标注来源并要求核对。
    /// </summary>
    public async Task<ApiDocRepairResult> RepairWithModelAsync()
    {
        var content = string.IsNullOrWhiteSpace(lastContent) ? pasteBox.Text : lastContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            statusBox.Text = "没有可分析的正文：请先填网页地址点「读取网页」，或把接口说明粘到上面的输入框。";
            return ApiDocRepairResult.Failed("没有可分析的正文。");
        }
        if (jsonCompleter is null)
        {
            statusBox.Text = "当前没有接入大模型，无法做这一步分析：请在「设置」里配好文本模型与密钥后重试。";
            return ApiDocRepairResult.Failed("没有接入大模型。");
        }

        var source = LastReport?.SourceUrl ?? urlBox.Text.Trim();
        statusBox.Text = "正在请大模型整理这份文档（只做整理，不会替你编内容）…";
        repairButton.Enabled = false;
        try
        {
            var result = await ApiDocRepair.RepairAsync(content, source, jsonCompleter);
            LastRepair = result;
            if (!result.Ok || result.Report is null)
            {
                statusBox.Text = "大模型分析没有成功：" + result.Error
                    + (result.Raw.Length == 0
                        ? string.Empty
                        : Environment.NewLine + "模型原文（前 400 字）：" + Trim(result.Raw, 400));
                return result;
            }

            Analyze(result.Report);
            AppendStatus("上面这些接口与模型来自大模型整理（不是本地规则解析的结果）：请核对无误后再点「创建技能」。");
            return result;
        }
        finally
        {
            repairButton.Enabled = jsonCompleter is not null;
        }
    }

    /// <summary>
    /// 查一次余额与上线模型（用户要求：在窗口上显示余额）。填完密钥会自动查，点余额那一行也能手动重查。
    /// 密钥优先用输入框里的（用户可能还没点保存），其次用已保存的配置。
    /// </summary>
    public async Task<ApiAccountSnapshot> RefreshAccountAsync()
    {
        var baseUrl = LastReport?.BaseUrl ?? string.Empty;
        var apiKey = string.IsNullOrWhiteSpace(keyBox.Text) ? config.ApiKey : keyBox.Text.Trim();
        if (baseUrl.Length == 0)
        {
            balanceLabel.Text = "余额：先解析出接口地址\r\n才能查余额";
            return ApiAccountSnapshot.Failed("还没有接口地址。");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            balanceLabel.Text = "余额：还没有密钥\r\n填好密钥后自动查";
            return ApiAccountSnapshot.Failed("还没有密钥。");
        }

        balanceLabel.Text = "余额：查询中…";
        var snapshot = await accountProbe(baseUrl, apiKey, CancellationToken.None);
        LastAccount = snapshot;

        if (snapshot.Ok && snapshot.Balance is { } balance)
        {
            var lines = new List<string> { balance.Describe() };
            if (snapshot.Models.Count > 0) lines.Add($"上线模型 {snapshot.Models.Count} 个");
            var temporary = balance.TemporaryNote();
            if (temporary.Length > 0) lines.Add(temporary);
            balanceLabel.Text = string.Join("\r\n", lines);
        }
        else
        {
            balanceLabel.Text = "余额：查询失败\r\n" + Trim(snapshot.Error, 42);
        }

        UpdateModelMismatch();
        if (LastModelMismatch.Count > 0)
            AppendStatus($"注意：文档里写的 {LastModelMismatch.Count} 个模型接口当前没有上线，用它们调用会 404："
                + string.Join("、", LastModelMismatch.Take(6))
                + (LastModelMismatch.Count > 6 ? " 等" : string.Empty)
                + "。可点「按接口实际模型重建技能」只保留能用的模型。");
        return snapshot;
    }

    /// <summary>按接口实际返回的模型列表重建技能：文档里写过、但线上没有的模型会被去掉。</summary>
    public IReadOnlyList<string> RebuildSkillsFromLiveModels()
    {
        if (LastReport is null || LastPlan is null)
        {
            statusBox.Text = "请先解析文档。";
            return Array.Empty<string>();
        }
        if (LastAccount is not { Ok: true } account || account.Models.Count == 0)
        {
            statusBox.Text = "还没有拿到接口的上线模型列表（填好密钥后会自动查），无法重建。";
            return Array.Empty<string>();
        }

        var before = LastPlan.Skills.Count(skill => skill.IsPool);
        // 模型表按线上列表收敛；池子若来自接口段落（文档没有模型表），Build 里的过滤同样生效。
        var kept = LastReport.ModelTable
            .Where(entry => account.Models.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var filtered = ApiSkillFactory.Build(LastReport with { ModelTable = kept }, account.Models);
        LastPlan = filtered;
        reportBox.Text = ApiImportSummary.RenderReport(LastReport, filtered);
        createButton.Enabled = filtered.HasAnything;
        UpdateModelMismatch();

        var after = filtered.Skills.Count(skill => skill.IsPool);
        if (after == before)
        {
            statusBox.Text = "文档里的模型都在接口的上线列表里，不需要重建。";
            return Array.Empty<string>();
        }

        var written = CreateSkills();
        AppendStatus($"已按接口实际模型重建：池子从 {before} 条收敛到 {after} 条，写入 {written.Count} 个技能文件；"
            + "没上线的模型没有写进技能（会退回设置里的默认模型）。");
        return written;
    }

    /// <summary>把清单里的技能写入技能目录。</summary>
    public IReadOnlyList<string> CreateSkills()
    {
        if (LastPlan is null)
        {
            statusBox.Text = "请先读取或粘贴接口说明并完成解析。";
            return Array.Empty<string>();
        }

        var result = ApiSkillFactory.Write(LastPlan);
        LastWrittenFiles = result.Written;
        LastWriteErrors = result.Errors;
        var lines = new List<string>();
        if (result.Written.Count > 0)
            lines.Add($"已写入 {result.Written.Count} 条技能到 {SkillLibrary.Directory}："
                + Environment.NewLine + string.Join(Environment.NewLine, result.Written.Select(path => "· " + Path.GetFileName(path))));
        if (result.Errors.Count > 0)
        {
            lines.Add("以下技能没有写入成功：" + Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Select(error => "· " + error)));
            lines.Add("（技能目录可能不可写：请检查目录权限后重试。）");
        }

        statusBox.Text = string.Join(Environment.NewLine, lines);
        if (result.Written.Count == 0) return result.Written;

        // 技能建好后立刻进入输入密钥，符合「创建好后弹窗口让用户输入 key」的顺序。
        PrepareKeyStep();
        EnterStep(ApiWizardStep.Key);
        return result.Written;
    }

    /// <summary>保存密钥（加密落盘），并写入识别到的接口地址与模型。</summary>
    public bool SaveKey()
    {
        if (LastReport is null || LastPlan is null)
        {
            statusBox.Text = "请先读取接口说明并创建技能。";
            return false;
        }

        var draftKey = keyBox.Text.Trim();
        var applied = ApplyConfiguredProviders(draftKey);
        LastApplied = applied;
        if (applied.Count == 0)
        {
            // 没变化也是成功：用户可能只是点了一次确认。
            LastKeySaveSucceeded = true;
            statusBox.Text = "密钥与接口地址和现有配置一致，没有需要写入的改动。"
                + Environment.NewLine + $"（当前密钥：{SecretProtector.Describe(config.ApiKey)}）";
            PrepareTestStep();
            EnterStep(ApiWizardStep.Test);
            return true;
        }

        if (!AiProviderSettings.Save(config))
        {
            // 保存失败必须如实报出，不能显示成功。
            LastKeySaveSucceeded = false;
            statusBox.Text = "配置写入失败（配置文件可能不可写或磁盘只读），本次密钥未生效："
                + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
                + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}";
            return false;
        }

        LastKeySaveSucceeded = true;
        statusBox.Text = "已加密保存 " + applied.Count + " 项："
            + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
            + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}（密钥为加密存储）";
        onApplied?.Invoke();
        PrepareTestStep();
        EnterStep(ApiWizardStep.Test);
        return true;
    }

    /// <summary>用户选择「否」：保存完成，不做测试。</summary>
    public void CompleteWithoutTest()
    {
        LastQuestion = BuildQuestion();
        LastTestSucceeded = null;
        LastReturnedMediaPath = string.Empty;
        statusBox.Text = "已按你的选择跳过最小测试：技能与密钥都已保存完成。"
            + Environment.NewLine
            + $"技能目录：{SkillLibrary.Directory}"
            + Environment.NewLine
            + "到画布上选中一个实体或节点，运行「运行技能」即可使用这些新技能。";
        EnterStep(ApiWizardStep.Done);
    }

    /// <summary>用户选择「是」：做一次最小测试，成功就把图返回来。</summary>
    public async Task TestMinimalAsync()
    {
        LastQuestion = BuildQuestion();
        if (LastReport is null)
        {
            statusBox.Text = "请先读取接口说明并创建技能。";
            return;
        }

        var plan = ApiMinimalTest.Plan(LastReport);
        if (!plan.CanRun)
        {
            LastTestSucceeded = null;
            statusBox.Text = plan.Reason + Environment.NewLine + "技能与密钥都已保存完成，可直接在画布上运行。"
                + (plan.Warning.Length > 0 ? Environment.NewLine + plan.Warning : string.Empty);
            EnterStep(ApiWizardStep.Done);
            return;
        }

        var op = plan.Op!;
        var balanceBefore = LastAccount?.Balance?.Balance;
        statusBox.Text = $"正在做最小测试：{plan.Reason}";
        if (plan.Kind == ApiMinimalTestKind.Image)
        {
            await RunImageTestAsync(plan, op);
        }
        else
        {
            await RunVideoTestAsync(plan, op);
        }

        // 跑完再查一次余额：既刷新右上角的显示，也能算出这一次到底花了多少。
        var snapshot = await RefreshAccountAsync();
        var balanceAfter = snapshot.Balance?.Balance;
        LastTestCost = balanceBefore is not null && balanceAfter is not null ? balanceBefore - balanceAfter : null;
        if (LastTestCost is { } cost && cost != 0)
            AppendStatus($"本次最小测试消耗 {cost} 积分" + (balanceAfter is null ? "。" : $"，当前余额 {balanceAfter}。"));

        EnterStep(ApiWizardStep.Done);
    }

    private async Task RunImageTestAsync(ApiMinimalTestPlan plan, ApiOpCandidate op)
    {
        var provider = imageProvider ?? new OpenAiCompatibleImageProvider(config);
        if (!provider.IsConfigured)
        {
            LastTestSucceeded = false;
            statusBox.Text = "最小测试未执行：尚未配置图像模型。"
                + Environment.NewLine + "请在「设置」里填好图像接口地址与模型后重试；技能与密钥都已保存完成。";
            return;
        }
        var (width, height) = ApiMinimalTest.SmallestSize(op);
        // 模型名与执行配置由 ApiMinimalTest 给出：接口自己声明的优先，执行配置与正式运行共用一份，
        // 所以最小测试打的确实是这条导入接口的地址、路径、方法与鉴权（返工 R4）。
        var model = plan.Model;
        var prompt = "最小测试图：一张浅灰色背景上的白色圆形，用于确认接口可用。";
        var request = new ImageGenerationRequest
        {
            Prompt = prompt,
            Model = model,
            BaseUrl = plan.Endpoint?.BaseUrl ?? string.Empty,
            EndpointPath = plan.Endpoint?.Path ?? string.Empty,
            Method = plan.Endpoint?.Method ?? string.Empty,
            AuthStyle = plan.Endpoint?.AuthStyle ?? string.Empty,
            Width = width,
            Height = height
        };
        ImageGenerationResult result;
        try
        {
            result = await provider.GenerateAsync(request);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            result = new ImageGenerationResult { Status = ImageGenerationStatus.Failed, Provider = provider.Name, Model = model, Error = error.Message };
        }

        if (result.Status != ImageGenerationStatus.Succeeded)
        {
            LastTestSucceeded = false;
            statusBox.Text = "最小测试失败（技能与密钥都已保存完成，不影响后续使用）："
                + Environment.NewLine + "· " + result.Error
                + (model.Length == 0 ? string.Empty : Environment.NewLine + "· 使用的模型：" + model)
                + (plan.Warning.Length > 0 ? Environment.NewLine + "· " + plan.Warning : string.Empty);
            return;
        }

        LastTestSucceeded = true;
        LastReturnedMediaPath = result.FilePath;
        ShowPreview(result.FilePath, isVideo: false);
        statusBox.Text = "最小测试成功，接口可用，已返回图片："
            + Environment.NewLine + "· 文件：" + result.FilePath
            + Environment.NewLine + "· 提供方：" + result.Provider + "｜模型：" + result.Model + $"｜画幅 {width}x{height}"
            + Environment.NewLine + "· 提示词：" + prompt;
    }

    private async Task RunVideoTestAsync(ApiMinimalTestPlan plan, ApiOpCandidate op)
    {
        var provider = videoProvider ?? VideoProviderFactory.Create(config);
        if (!provider.IsConfigured)
        {
            // 出视频执行方还没接入：如实说明，不拿别的文件冒充视频。
            LastTestSucceeded = false;
            statusBox.Text = "最小测试未执行：出视频链路当前不可用。"
                + Environment.NewLine + "· " + plan.Reason
                + Environment.NewLine + "· 技能与密钥都已保存完成；等接入出视频执行方后可直接运行这些技能。"
                + (plan.Warning.Length > 0 ? Environment.NewLine + "· " + plan.Warning : string.Empty);
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
            LastTestSucceeded = false;
            statusBox.Text = "最小测试失败（技能与密钥都已保存完成）：" + Environment.NewLine + "· " + result.Error;
            return;
        }

        LastTestSucceeded = true;
        LastReturnedMediaPath = result.FilePath;
        ShowPreview(result.FilePath, isVideo: true);
        statusBox.Text = "最小测试成功，出视频接口可用，已返回视频："
            + Environment.NewLine + "· 文件：" + result.FilePath
            + Environment.NewLine + "· 提供方：" + result.Provider + "｜模型：" + result.Model;
    }

    private string BuildQuestion()
    {
        if (LastReport is null) return "要不要现在做一次最小测试？";
        var plan = ApiMinimalTest.Plan(LastReport);
        var target = plan.CanRun
            ? $"{plan.Capability}（{plan.Op!.Method} {plan.Op.Path}）"
            : "（这份文档里的接口都需要参考图，无法凭空测）";
        return $"技能已创建、密钥已加密保存。要不要现在做一次最小测试？测的是 {target}。"
            + "选「是」会真的调一次接口，成功就把图返回给你；选「否」就保存完成。";
    }

    private void ShowPreview(string path, bool isVideo)
    {
        previewBox.Visible = false;
        previewBox.Image = null;
        if (isVideo || !File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            previewBox.Image = Image.FromStream(stream);
            previewBox.Visible = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 预览失败不影响结论：文件路径已经如实报出。
        }
    }

    /// <summary>按识别到的类型写入地址、模型与密钥；出图与出视频各写一次。</summary>
    private IReadOnlyList<string> ApplyConfiguredProviders(string apiKey)
    {
        var applied = new List<string>();
        var baseUrl = LastReport?.BaseUrl ?? string.Empty;
        var imageModel = PreferredModel(LastReport, isVideo: false);
        var videoModel = PreferredModel(LastReport, isVideo: true);
        var hasImage = LastReport is not null && (LastReport.ImageOps.Count > 0 || LastReport.ImageModels.Count > 0);
        var hasVideo = LastReport is not null && (LastReport.VideoOps.Count > 0 || LastReport.VideoModels.Count > 0);

        if (hasImage)
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.ImageApi, baseUrl, apiKey, imageModel, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
        if (hasVideo)
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.VideoApi, baseUrl, apiKey, videoModel, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
        if (!hasImage && !hasVideo)
            applied.AddRange(ProviderImporter.Apply(config, new ProviderImportDraft(
                ProviderKind.TextApi, baseUrl, apiKey, string.Empty, string.Empty,
                Array.Empty<string>(), Array.Empty<string>())));
        return applied;
    }

    /// <summary>
    /// 该写进设置里的模型名：先用接口自己声明的，没有就用文档「可用模型」表里同类模型的第一个。
    /// 表里第一个通常就是文档主推的模型（真实文档上就是「别名优先」列出的那批）。
    /// </summary>
    private static string PreferredModel(ApiDocReport? report, bool isVideo)
    {
        if (report is null) return string.Empty;
        var ops = isVideo ? report.VideoOps : report.ImageOps;
        if (ops.SelectMany(op => op.Models).FirstOrDefault() is { Length: > 0 } declared) return declared;
        var models = isVideo ? report.VideoModels : report.ImageModels;
        return models.FirstOrDefault()?.Name ?? string.Empty;
    }

    private void PrepareKeyStep()
    {
        keyBox.Text = string.Empty;
        var baseUrl = LastReport?.BaseUrl ?? string.Empty;
        var imageModel = PreferredModel(LastReport, isVideo: false);
        var videoModel = PreferredModel(LastReport, isVideo: true);
        var parts = new List<string>();
        if (baseUrl.Length > 0) parts.Add($"接口地址 {baseUrl}");
        if (imageModel.Length > 0) parts.Add($"图像模型 {imageModel}");
        if (videoModel.Length > 0) parts.Add($"视频模型 {videoModel}");
        keyHint.Text = parts.Count == 0
            ? "文档里没有解析到接口地址与模型名：密钥会保存到本机配置，地址与模型请在「设置」里手动补。"
            : "将写入：" + string.Join("｜", parts) + "（如需修改请到「设置」里改）";
    }

    private void PrepareTestStep()
    {
        var question = BuildQuestion();
        LastQuestion = question;
        questionLabel.Text = question;
        testYesButton.Enabled = LastReport is not null;
    }

    private void EnterStep(ApiWizardStep step)
    {
        CurrentStep = step;
        keyPanel.Visible = step is ApiWizardStep.Key or ApiWizardStep.Test or ApiWizardStep.Done;
        testPanel.Visible = step is ApiWizardStep.Test or ApiWizardStep.Done;
        testYesButton.Visible = step == ApiWizardStep.Test;
        testNoButton.Visible = step == ApiWizardStep.Test;
        rebuildButton.Visible = LastModelMismatch.Count > 0;

        (stepLabel.Text, stepHint.Text) = step switch
        {
            ApiWizardStep.Document => ("第 1 步 / 共 4 步：给一个接口说明网页",
                "填文档地址点「读取网页」；页面由脚本渲染而抓不到内容时，把文档里的接口段落粘到下面再点「分析这段文字」。"),
            ApiWizardStep.Key => ("第 2 步 / 共 4 步：输入接口密钥",
                "密钥只保存在本机，写入时用 Windows DPAPI 加密；界面与日志都不会回显完整密钥。"),
            ApiWizardStep.Test => ("第 3 步 / 共 4 步：是否做一次最小测试",
                "选「是」会真的调一次接口，成功就把图返回来；选「否」直接保存完成。"),
            _ => ("第 4 步 / 共 4 步：完成",
                "技能已建好、密钥已加密保存。需要改地址或模型时可以随时打开「设置」。")
        };
    }

    private Panel HeaderPanel()
    {
        stepLabel.AutoSize = true;
        stepLabel.ForeColor = Theme.Text;
        stepHint.AutoSize = true;
        stepHint.MaximumSize = new Size(500, 0);
        stepHint.ForeColor = Theme.TextMuted;

        // 余额放在右上角：填完密钥就会自动查一次，之后每次调用完再查一次（能看到这次花了多少）。
        balanceLabel.Dock = DockStyle.Fill;
        balanceLabel.TextAlign = ContentAlignment.MiddleRight;
        balanceLabel.ForeColor = Theme.Text;
        balanceLabel.Cursor = Cursors.Hand;
        balanceLabel.Text = "余额：未查询\r\n（填密钥后自动查）";
        balanceLabel.Click += async (_, _) => await RefreshAccountAsync();

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(stepLabel, 0, 0);
        panel.Controls.Add(stepHint, 0, 1);
        panel.Controls.Add(balanceLabel, 1, 0);
        panel.SetRowSpan(balanceLabel, 2);
        return panel;
    }

    private void BuildDocumentPanel(out TableLayoutPanel panel)
    {
        panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 0, 12, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label { Text = "接口说明网页地址", AutoSize = true, ForeColor = Theme.Text }, 0, 0);
        urlBox.Dock = DockStyle.Fill;
        urlBox.PlaceholderText = "https://服务商文档地址/v1/images/generations 所在的说明页";
        fetchButton.Text = "读取网页";
        fetchButton.Width = 96;
        fetchButton.Height = 30;
        fetchButton.Click += async (_, _) => await AnalyzeUrlAsync(urlBox.Text);
        var urlRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        urlRow.Controls.Add(urlBox, 0, 0);
        urlRow.Controls.Add(fetchButton, 1, 0);
        panel.Controls.Add(urlRow, 0, 1);

        panel.Controls.Add(new Label
        {
            Text = "或把文档里的接口说明粘到这里（页面由前端脚本渲染时抓不到正文）",
            AutoSize = true,
            ForeColor = Theme.TextMuted
        }, 0, 2);

        pasteBox.Multiline = true;
        pasteBox.ScrollBars = ScrollBars.Vertical;
        pasteBox.Dock = DockStyle.Fill;
        pasteBox.PlaceholderText = "例如：\nPOST /v1/images/generations\nmodel: flux-1-dev\n可填 1k / 2k 两种画幅\nAuthorization: Bearer sk-xxxxxxxx";
        panel.Controls.Add(pasteBox, 0, 3);

        analyzeButton.Text = "分析这段文字";
        analyzeButton.Width = 120;
        analyzeButton.Height = 30;
        analyzeButton.Click += (_, _) => AnalyzeContent(pasteBox.Text, urlBox.Text);
        // 本地规则解析不出接口时的兜底：请已接入的大模型把正文整理成结构化 JSON（只做「整理」，不做「补全」）。
        repairButton.Text = jsonCompleter is null ? "让大模型分析（未接入）" : "让大模型分析";
        repairButton.Width = 158;
        repairButton.Height = 30;
        repairButton.Enabled = jsonCompleter is not null;
        repairButton.Click += async (_, _) => await RepairWithModelAsync();
        // 解析完才允许建技能：没识别到接口就点它只会得到一句说明，干脆先禁用。
        createButton.Text = "创建技能";
        createButton.Width = 96;
        createButton.Height = 30;
        createButton.Enabled = false;
        createButton.Click += (_, _) => CreateSkills();
        var pasteRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        pasteRow.Controls.Add(analyzeButton);
        pasteRow.Controls.Add(repairButton);
        pasteRow.Controls.Add(createButton);
        panel.Controls.Add(pasteRow, 0, 4);
    }

    private void BuildReportBox()
    {
        reportBox.Multiline = true;
        reportBox.ReadOnly = true;
        reportBox.ScrollBars = ScrollBars.Both;
        reportBox.WordWrap = false;
        reportBox.Dock = DockStyle.Fill;
        reportBox.BackColor = Color.White;
        reportBox.Font = new Font("Consolas", 9f);
        reportBox.Margin = new Padding(12, 4, 12, 4);
    }

    private void BuildKeyPanel()
    {
        keyPanel.Dock = DockStyle.Fill;
        keyPanel.ColumnCount = 1;
        keyPanel.RowCount = 4;
        keyPanel.Padding = new Padding(12, 0, 12, 0);
        keyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        keyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        keyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        keyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        keyPanel.Controls.Add(new Label { Text = "API 密钥（仅保存在本机，写入时加密）", AutoSize = true, ForeColor = Theme.Text }, 0, 0);
        keyBox.Dock = DockStyle.Fill;
        keyBox.UseSystemPasswordChar = true;
        keyBox.PlaceholderText = "粘贴服务商给的 API Key；留空表示沿用已保存的密钥";
        keyPanel.Controls.Add(keyBox, 0, 1);
        keyHint.AutoSize = true;
        keyHint.MaximumSize = new Size(720, 28);
        keyHint.ForeColor = Theme.TextMuted;
        keyPanel.Controls.Add(keyHint, 0, 2);

        saveKeyButton.Text = "保存密钥";
        saveKeyButton.Width = 110;
        saveKeyButton.Height = 30;
        // 保存成功后顺手查一次余额：用户最关心「这 key 还有多少钱」，不该还得再去别处看。
        saveKeyButton.Click += async (_, _) =>
        {
            if (SaveKey()) await RefreshAccountAsync();
        };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        row.Controls.Add(saveKeyButton);
        keyPanel.Controls.Add(row, 0, 3);
    }

    private void BuildTestPanel()
    {
        testPanel.Dock = DockStyle.Fill;
        testPanel.ColumnCount = 1;
        testPanel.RowCount = 2;
        testPanel.Padding = new Padding(12, 0, 12, 0);
        testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        testPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        questionLabel.AutoSize = false;
        questionLabel.Dock = DockStyle.Fill;
        questionLabel.ForeColor = Theme.Text;
        testPanel.Controls.Add(questionLabel, 0, 0);

        testYesButton.Text = "是，做最小测试";
        testYesButton.Width = 130;
        testYesButton.Height = 30;
        testYesButton.Click += async (_, _) => await TestMinimalAsync();
        testNoButton.Text = "否，直接保存完成";
        testNoButton.Width = 140;
        testNoButton.Height = 30;
        testNoButton.Click += (_, _) => CompleteWithoutTest();
        // 只有「文档里写了、但接口当前没上线」的模型存在时才出现：一键按线上模型重建池子技能。
        rebuildButton.Text = "按接口实际模型重建技能";
        rebuildButton.Width = 178;
        rebuildButton.Height = 30;
        rebuildButton.Visible = false;
        rebuildButton.Click += (_, _) => RebuildSkillsFromLiveModels();
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        row.Controls.Add(testYesButton);
        row.Controls.Add(testNoButton);
        row.Controls.Add(rebuildButton);
        testPanel.Controls.Add(row, 0, 1);
    }

    private TableLayoutPanel StatusRow()
    {
        statusBox.Multiline = true;
        statusBox.ReadOnly = true;
        statusBox.ScrollBars = ScrollBars.Vertical;
        statusBox.Dock = DockStyle.Fill;
        statusBox.BackColor = Color.FromArgb(250, 250, 252);
        statusBox.Font = new Font("Consolas", 9f);

        previewBox.SizeMode = PictureBoxSizeMode.Zoom;
        previewBox.Dock = DockStyle.Fill;
        previewBox.BorderStyle = BorderStyle.FixedSingle;
        previewBox.Visible = false;

        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 0, 12, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
        row.Controls.Add(statusBox, 0, 0);
        row.Controls.Add(previewBox, 1, 0);
        return row;
    }

    /// <summary>
    /// 把「文档写了、但接口没上线」的模型差异算出来（没有账号信息时视为无差异）。
    /// 取的是**技能里实际会用到的模型名**（池子与父技能步骤）+ 模型表，前者才是真正会 404 的那批。
    /// </summary>
    private void UpdateModelMismatch()
    {
        var documented = new List<string>();
        if (LastPlan is not null)
            documented.AddRange(LastPlan.Skills
                .SelectMany(skill => skill.Definition.Steps)
                .Select(step => step.Model)
                .Where(model => model.Length > 0));
        if (LastReport is not null) documented.AddRange(LastReport.ModelTable.Select(entry => entry.Name));

        LastModelMismatch = LastAccount?.MissingFrom(documented.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            ?? Array.Empty<string>();
        rebuildButton.Visible = LastModelMismatch.Count > 0;
    }

    /// <summary>往状态区追加一行（保留已有内容，便于看到完整过程）。</summary>
    private void AppendStatus(string line)
    {
        statusBox.Text = statusBox.Text.Length == 0
            ? line
            : statusBox.Text + Environment.NewLine + line;
    }

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";

}
