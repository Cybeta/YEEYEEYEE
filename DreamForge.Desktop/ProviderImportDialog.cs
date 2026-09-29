using System.Net.Http;
using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>
/// 智能导入窗口（用户要求）：把 ComfyUI 或「画图 / 画视频」接口的地址、模型、密钥
/// 从一段任意文本（文档片段、curl、JSON 配置、env 键值对）里识别出来，核对后一键写入设置。
///
/// 设计边界：
/// · 识别与写入分开——「智能识别」只填字段、不动配置；「导入并保存」才写配置与落盘（密钥走 DPAPI 加密）。
/// · 识别不到的字段一律留空并提示手填，绝不猜测、不用其它值顶替。
/// · 保存失败（配置文件不可写等）必须如实报出，不能显示成功。
/// · 只改本次识别/手填涉及的字段，其余配置项原样保留。
/// </summary>
public sealed class ProviderImportDialog : Form
{
    private static readonly HttpClient probeHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AiProviderConfig config;
    private readonly Action? onApplied;

    private readonly TextBox pasteBox = new();
    private readonly ComboBox kindBox = new();
    private readonly TextBox baseUrl = new();
    private readonly TextBox model = new();
    private readonly TextBox checkpoint = new();
    private readonly TextBox apiKey = new();
    private readonly Label conclusion = new();
    private readonly TextBox resultBox = new();
    private readonly Button importButton = new();

    /// <summary>最近一次识别结论；未识别时为 null。</summary>
    public ProviderImportDraft? Draft { get; private set; }

    /// <summary>最近一次导入实际写入的字段描述。</summary>
    public IReadOnlyList<string> LastApplied { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次保存是否成功（未点过导入时为 null）。</summary>
    public bool? LastSaveSucceeded { get; private set; }

    /// <summary>结果区文本，便于回归与冒烟检查。</summary>
    public string LastSummary => resultBox.Text;

    public ProviderImportDialog(AiProviderConfig config, Action? onApplied = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.onApplied = onApplied;

        Text = "智能导入接口（ComfyUI / 画图 / 画视频）";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 720);
        Font = Theme.UiFont;
        BackColor = Theme.PanelBg;

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = Theme.TextMuted,
            Padding = new Padding(12, 12, 12, 0),
            Text = "把服务商文档里的接口地址、模型名、密钥直接粘到下面（一段话、一条 curl、一份 JSON 或 env 键值对都行），"
                + "点「智能识别」自动填表；核对无误后点「导入并保存」。识别不到的字段请手动填写。"
        };

        pasteBox.Multiline = true;
        pasteBox.ScrollBars = ScrollBars.Vertical;
        pasteBox.Dock = DockStyle.Fill;
        pasteBox.PlaceholderText = "例如：\nhttps://api.example.com/v1/images/generations\nAuthorization: Bearer sk-xxxxxxxx\nmodel: dall-e-3";

        var pasteButton = new Button { Text = "智能识别", Width = 96, Height = 30 };
        pasteButton.Click += (_, _) => Inspect();
        var clearButton = new Button { Text = "清空", Width = 72, Height = 30 };
        clearButton.Click += (_, _) =>
        {
            pasteBox.Clear();
            Draft = null;
            conclusion.Text = string.Empty;
            resultBox.Clear();
        };
        var pasteButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 4, 10, 4) };
        pasteButtons.Controls.Add(pasteButton);
        pasteButtons.Controls.Add(clearButton);

        kindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var kind in new[] { ProviderKind.Unknown, ProviderKind.ComfyUi, ProviderKind.ImageApi, ProviderKind.VideoApi, ProviderKind.TextApi })
            kindBox.Items.Add(ProviderImporter.KindName(kind));
        kindBox.SelectedIndex = 0;
        kindBox.Dock = DockStyle.Fill;
        kindBox.SelectedIndexChanged += (_, _) => RefreshFieldLabels();

        baseUrl.Dock = DockStyle.Fill;
        baseUrl.PlaceholderText = "基础地址，例如 https://api.example.com/v1 或 http://127.0.0.1:8188";
        model.Dock = DockStyle.Fill;
        model.PlaceholderText = "模型名，例如 dall-e-3 / veo-3 / kling-v1 / deepseek-chat";
        checkpoint.Dock = DockStyle.Fill;
        checkpoint.PlaceholderText = "ComfyUI checkpoint 文件名，例如 sd_xl_base_1.0.safetensors";
        apiKey.Dock = DockStyle.Fill;
        apiKey.UseSystemPasswordChar = true;
        apiKey.PlaceholderText = "API 密钥；留空表示沿用已保存的密钥";

        conclusion.AutoSize = true;
        conclusion.MaximumSize = new Size(660, 0);
        conclusion.ForeColor = Theme.Text;

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 0, 12, 0) };
        for (var row = 0; row < 10; row++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = "类型", AutoSize = true }, 0, 0);
        fields.Controls.Add(kindBox, 0, 1);
        fields.Controls.Add(new Label { Text = "基础地址", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 2);
        fields.Controls.Add(baseUrl, 0, 3);
        fields.Controls.Add(new Label { Text = "模型名", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 4);
        fields.Controls.Add(model, 0, 5);
        fields.Controls.Add(new Label { Text = "ComfyUI checkpoint（仅 ComfyUI 使用）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 6);
        fields.Controls.Add(checkpoint, 0, 7);
        fields.Controls.Add(new Label { Text = "API 密钥（仅保存在本机，写入时加密）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 8);
        fields.Controls.Add(apiKey, 0, 9);

        resultBox.Multiline = true;
        resultBox.ReadOnly = true;
        resultBox.ScrollBars = ScrollBars.Both;
        resultBox.WordWrap = false;
        resultBox.Dock = DockStyle.Fill;
        resultBox.Height = 170;
        resultBox.BackColor = Color.White;
        resultBox.Font = new Font("Consolas", 9f);

        var probeButton = new Button { Text = "测试连接", Width = 96, Height = 32 };
        probeButton.Click += async (_, _) => await ProbeAsync();
        importButton.Text = "导入并保存";
        importButton.Width = 110;
        importButton.Height = 32;
        importButton.Click += (_, _) => Import();
        var closeButton = new Button { Text = "关闭", Width = 80, Height = 32 };
        closeButton.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(importButton);
        buttons.Controls.Add(probeButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(pasteBox, 0, 1);
        layout.Controls.Add(pasteButtons, 0, 2);
        layout.Controls.Add(fields, 0, 3);
        layout.Controls.Add(resultBox, 0, 4);

        Controls.Add(layout);
        Controls.Add(conclusion);
        Controls.Add(buttons);
        conclusion.Dock = DockStyle.Top;
        conclusion.Padding = new Padding(12, 0, 12, 6);
        buttons.BringToFront();

        RefreshFieldLabels();
        RefreshImportAvailability();
    }

    private void RefreshFieldLabels()
    {
        var kind = SelectedKind();
        checkpoint.Enabled = kind == ProviderKind.ComfyUi;
        RefreshImportAvailability();
    }

    private void RefreshImportAvailability()
    {
        var kind = SelectedKind();
        var hasAddress = !string.IsNullOrWhiteSpace(baseUrl.Text);
        importButton.Enabled = kind != ProviderKind.Unknown || hasAddress;
    }

    private ProviderKind SelectedKind()
    {
        var order = new[] { ProviderKind.Unknown, ProviderKind.ComfyUi, ProviderKind.ImageApi, ProviderKind.VideoApi, ProviderKind.TextApi };
        var index = kindBox.SelectedIndex;
        return index >= 0 && index < order.Length ? order[index] : ProviderKind.Unknown;
    }

    private void SelectKind(ProviderKind kind)
    {
        var order = new[] { ProviderKind.Unknown, ProviderKind.ComfyUi, ProviderKind.ImageApi, ProviderKind.VideoApi, ProviderKind.TextApi };
        var index = Array.IndexOf(order, kind);
        kindBox.SelectedIndex = index < 0 ? 0 : index;
    }

    private void Inspect()
    {
        var draft = ProviderImporter.Inspect(pasteBox.Text);
        Draft = draft;

        SelectKind(draft.Kind);
        baseUrl.Text = draft.BaseUrl;
        model.Text = draft.Model;
        checkpoint.Text = draft.Checkpoint;
        apiKey.Text = draft.ApiKey;

        var lines = new List<string> { $"识别结论：{ProviderImporter.KindName(draft.Kind)}" };
        if (draft.Signals.Count > 0)
        {
            lines.Add("命中的判据：");
            lines.AddRange(draft.Signals.Select(signal => "· " + signal));
        }

        if (draft.Warnings.Count > 0)
        {
            lines.Add("需要你确认的地方：");
            lines.AddRange(draft.Warnings.Select(warning => "· " + warning));
        }

        var changes = ProviderImporter.DescribeChanges(config, draft);
        lines.Add(changes.Count == 0 ? "导入后不会改动任何字段（与当前配置一致）。" : "导入后将写入：");
        if (changes.Count > 0) lines.AddRange(changes.Select(change => "· " + change));

        conclusion.ForeColor = draft.Warnings.Count > 0 ? Theme.Warning : Theme.Text;
        conclusion.Text = draft.Warnings.Count > 0
            ? $"已识别为「{ProviderImporter.KindName(draft.Kind)}」，但有 {draft.Warnings.Count} 处需要确认。"
            : $"已识别为「{ProviderImporter.KindName(draft.Kind)}」。";
        resultBox.Text = string.Join(Environment.NewLine, lines);
        RefreshFieldLabels();
    }

    private ProviderImportDraft CurrentDraft()
    {
        var kind = SelectedKind();
        return new ProviderImportDraft(
            kind,
            baseUrl.Text.Trim(),
            apiKey.Text.Trim(),
            model.Text.Trim(),
            checkpoint.Text.Trim(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    private async Task ProbeAsync()
    {
        var draft = CurrentDraft();
        if (string.IsNullOrWhiteSpace(draft.BaseUrl))
        {
            resultBox.Text = "请先填写基础地址再测试连接。";
            return;
        }

        resultBox.Text = $"正在测试 {ProviderImporter.KindName(draft.Kind)}：{draft.BaseUrl} …";
        CapabilityProbeResult probe;
        try
        {
            if (draft.Kind == ProviderKind.ComfyUi)
            {
                probe = await ComfyUiProbe.RunAsync(draft.BaseUrl, probeHttp);
            }
            else
            {
                var probeConfig = new AiProviderConfig
                {
                    Endpoint = draft.BaseUrl,
                    Model = draft.Model,
                    ApiKey = string.IsNullOrWhiteSpace(draft.ApiKey) ? config.ApiKey : draft.ApiKey
                };
                probe = await OpenAiCompatibleProbe.RunAsync(probeConfig, probeHttp);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            resultBox.Text = $"连接测试失败：{error.Message}";
            return;
        }

        var lines = new List<string> { probe.Summary };
        lines.AddRange(probe.Notes.Select(note => "· " + note));
        lines.AddRange(probe.Findings
            .Where(finding => finding.Available)
            .Take(20)
            .Select(finding => $"· {finding.Category}｜{finding.Name}：{finding.Detail}"));
        resultBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void Import()
    {
        var draft = CurrentDraft();
        if (draft.Kind == ProviderKind.Unknown && string.IsNullOrWhiteSpace(draft.BaseUrl))
        {
            resultBox.Text = "没有可导入的内容：请先粘贴接口信息并点「智能识别」，或手动选择类型并填写地址。";
            LastSaveSucceeded = false;
            return;
        }

        var applied = ProviderImporter.Apply(config, draft);
        LastApplied = applied;
        if (applied.Count == 0)
        {
            LastSaveSucceeded = true;
            resultBox.Text = "识别到的内容与现有配置一致，没有需要写入的改动。";
            return;
        }

        if (!AiProviderSettings.Save(config))
        {
            // 保存失败必须如实报出，不能显示成功。
            LastSaveSucceeded = false;
            resultBox.Text = "配置写入失败（配置文件可能不可写或磁盘只读），本次导入未生效："
                + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
                + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}";
            conclusion.ForeColor = Theme.Warning;
            conclusion.Text = "导入失败：配置未写入。";
            return;
        }

        LastSaveSucceeded = true;
        resultBox.Text = "已写入 " + applied.Count + " 项："
            + Environment.NewLine + string.Join(Environment.NewLine, applied.Select(item => "· " + item))
            + Environment.NewLine + $"配置文件：{AiProviderSettings.ConfigFilePath}（密钥为加密存储）";
        conclusion.ForeColor = Theme.Text;
        conclusion.Text = $"导入成功：{ProviderImporter.KindName(draft.Kind)} 已写入 {applied.Count} 项。";
        onApplied?.Invoke();
    }
}
