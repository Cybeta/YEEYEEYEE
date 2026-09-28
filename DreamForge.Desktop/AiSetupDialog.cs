using System.Net.Http;

namespace DreamForge.Desktop;

/// <summary>接入引导的结果：要么保存了真实接入，要么显式选择了本地模拟。</summary>
public sealed record AiSetupOutcome(bool Saved, bool UseLocal);

/// <summary>
/// 接入大模型引导。首次启动必须在这里做一次明确选择——
/// 要么接入真实模型，要么显式选「本地模拟」，不允许静默降级后让用户以为在用大模型。
///
/// 添加模型的结构：预设服务商给一份**模型版本列表**（可再手填其他版本），
/// 自定义则自己选接口格式、填地址与模型 ID，高级配置里声明上下文窗口与最大输出。
/// </summary>
public sealed class AiSetupDialog : ScaledForm
{
    /// <summary>测试连接只探一次，用一个共享客户端即可，不必每次点都新建。</summary>
    private static readonly HttpClient probeHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AiProviderConfig config;
    private readonly ComboBox presets = new();
    private readonly ComboBox models = new();
    private readonly Label modelNote = new();
    private readonly TextBox endpoint = new();
    private readonly CheckBox fullUrl = new();
    private readonly Label urlPreview = new();
    private readonly TextBox apiKey = new();
    private readonly CheckBox advancedToggle = new();
    private readonly TableLayoutPanel advanced = new();
    private readonly ComboBox format = new();
    private readonly TextBox displayName = new();
    private readonly NumericUpDown contextWindow = new();
    private readonly NumericUpDown maxOutput = new();
    private readonly CheckBox sendSampling = new();
    private readonly CheckBox supportsVision = new();
    private readonly Label keyHint = new();
    private readonly Label hint = new();
    private readonly Label testResult = new();
    private readonly Button test = new();
    private readonly Button save = new();
    private readonly Button useLocal = new();
    private readonly TableLayoutPanel layout = new();

    private const int DialogWidth = 560;

    /// <summary>内容区可用宽度：560 减去左右各 18 的内边距。</summary>
    private const int ContentWidth = 524;

    /// <summary>
    /// 整行说明文字的公共配置：宽度钉成内容宽度、**高度自适应**。
    /// 之前这些标签是"宽度撑满 + 高度只够一行"（AutoSize=false），
    /// 文案一换行，第二行就被切掉——这也是用户看到"文字显示不全"的一类。
    /// </summary>
    private static void Wrap(Label label, Color color, int width = ContentWidth)
    {
        label.AutoSize = true;
        label.MinimumSize = new Size(width, 0);
        label.MaximumSize = new Size(width, 0);
        label.ForeColor = color;
    }

    public AiSetupOutcome? Outcome { get; private set; }

    public static AiSetupOutcome? Show(IWin32Window owner, bool firstRun)
    {
        using var dialog = new AiSetupDialog(AiProviderSettings.Load(), firstRun);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Outcome : null;
    }

    private AiSetupDialog(AiProviderConfig config, bool firstRun)
    {
        this.config = config;
        Text = firstRun ? "接入大模型（首次启动）" : "接入大模型";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = Theme.UiFont;

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(480, 0),   // 留出竖直滚动条的宽度，别让文字被滚动条压住
            ForeColor = Theme.Text,
            Text = firstRun
                ? "YeeYeeYee 需要接入一个 OpenAI 兼容的大模型才能使用 AI 功能。\n选择服务商 → 选模型版本（或手填模型 ID）→ 填 API 密钥 → 点「测试连接」确认可用 → 保存。\n也可以先选「本地模拟」试用界面，但那时 AI 生成只是关键词占位结果。"
                : "更换服务商只影响文本模型；图像与 ComfyUI 设置在「设置」里单独维护。模型 ID 可手填，服务商下线旧模型后不必等程序更新。"
        };

        presets.Dock = DockStyle.Fill;
        presets.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var preset in ProviderPreset.All) presets.Items.Add(preset.Name);

        // 模型下拉允许自由输入：预设列表只是起点，服务商会不断下线旧模型。
        models.Dock = DockStyle.Fill;
        models.DropDownStyle = ComboBoxStyle.DropDown;

        Wrap(modelNote, Theme.TextMuted);

        endpoint.Dock = DockStyle.Fill;
        endpoint.PlaceholderText = "例如 https://api.deepseek.com/v1 或完整请求地址";
        fullUrl.Text = "完整 URL";
        fullUrl.AutoSize = true;
        fullUrl.Margin = new Padding(8, 6, 0, 0);

        Wrap(urlPreview, Theme.TextMuted);

        apiKey.Dock = DockStyle.Fill;
        apiKey.UseSystemPasswordChar = true;
        apiKey.PlaceholderText = "留空表示不修改已保存的密钥";
        var showKey = new CheckBox { Text = "显示密钥", AutoSize = true };
        showKey.CheckedChanged += (_, _) => apiKey.UseSystemPasswordChar = !showKey.Checked;
        apiKey.TextChanged += (_, _) => RefreshKeyHint();

        Wrap(keyHint, Theme.TextMuted);

        contextWindow.Maximum = 10_000_000;
        contextWindow.Increment = 1_000;
        contextWindow.Dock = DockStyle.Fill;
        maxOutput.Maximum = 1_000_000;
        maxOutput.Increment = 1_000;
        maxOutput.Dock = DockStyle.Fill;

        BuildAdvancedPanel();

        advancedToggle.Text = "高级配置（接口格式、模型展示名称、上下文窗口）";
        advancedToggle.AutoSize = true;
        advancedToggle.CheckedChanged += (_, _) => ToggleAdvanced();

        Wrap(hint, Theme.TextMuted);
        Wrap(testResult, Theme.TextMuted, ContentWidth - 120);
        testResult.TextAlign = ContentAlignment.MiddleLeft;

        test.Text = "测试连接";
        test.Width = 110;
        test.Height = 30;
        test.FlatStyle = FlatStyle.Flat;
        test.Click += async (_, _) => await TestAsync();

        save.Text = "保存并继续";
        save.Width = 120;
        save.Height = 32;
        save.FlatStyle = FlatStyle.Flat;
        save.Click += (_, _) => SaveAndClose();

        useLocal.Text = "先用本地模拟";
        useLocal.Width = 120;
        useLocal.Height = 32;
        useLocal.FlatStyle = FlatStyle.Flat;
        useLocal.Click += (_, _) => UseLocalAndClose();

        var keyRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        keyRow.Controls.Add(apiKey, 0, 0);
        keyRow.Controls.Add(showKey, 1, 0);

        var endpointRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        endpointRow.Controls.Add(endpoint, 0, 0);
        endpointRow.Controls.Add(fullUrl, 1, 0);

        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        testRow.Controls.Add(test, 0, 0);
        testRow.Controls.Add(testResult, 1, 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = Padding.Empty, Margin = Padding.Empty };
        footer.Controls.Add(save);
        footer.Controls.Add(useLocal);

        layout.ColumnCount = 1;
        layout.RowCount = 17;
        layout.Padding = new Padding(18);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 行高一律按内容算，不写死：写死高度加上固定窗口高度，
        // 一旦字体或文案变化，多出来的部分就把页脚按钮顶出可视区（用户点不到）。
        for (var row = 0; row < 17; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(FieldLabel("服务商"), 0, 1);
        layout.Controls.Add(presets, 0, 2);
        layout.Controls.Add(FieldLabel("模型（可从列表选择，也可以直接手填模型 ID）"), 0, 3);
        layout.Controls.Add(models, 0, 4);
        layout.Controls.Add(modelNote, 0, 5);
        layout.Controls.Add(FieldLabel("接口地址"), 0, 6);
        layout.Controls.Add(endpointRow, 0, 7);
        layout.Controls.Add(urlPreview, 0, 8);
        layout.Controls.Add(FieldLabel("API 密钥"), 0, 9);
        layout.Controls.Add(keyRow, 0, 10);
        layout.Controls.Add(keyHint, 0, 11);
        layout.Controls.Add(advancedToggle, 0, 12);
        layout.Controls.Add(advanced, 0, 13);
        layout.Controls.Add(hint, 0, 14);
        layout.Controls.Add(testRow, 0, 15);
        layout.Controls.Add(footer, 0, 16);
        layout.Dock = DockStyle.Top;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // 外面套一层可滚动的壳：内容再长，页脚按钮也永远够得着。
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = Padding.Empty, Margin = Padding.Empty };
        scroll.Controls.Add(layout);
        Controls.Add(scroll);

        AcceptButton = save;

        // 回显：先按已保存的地址反推服务商并套用预设，再用保存过的配置覆盖，最后才接上交互事件。
        // 没保存过地址时默认选中列表第一家，避免首次打开就停在一个空的「自定义」表单上。
        presets.SelectedIndex = IndexOf(string.IsNullOrWhiteSpace(config.Endpoint)
            ? ProviderPreset.DeepSeek
            : ProviderPreset.Match(config.Endpoint));
        ApplyPreset();
        RestoreFromConfig();
        PresetEvents();
        UpdateUrlPreview();
        // 高级配置默认收起。窗口尺寸不在这里定：这一步要等缩放完成（OnLoad）才算得准。
        advanced.Visible = false;
        ClientSize = new Size(DialogWidth, 620);
    }

    /// <summary>
    /// 按内容真实需要的高度定窗口尺寸，**全程用实际像素**。
    /// 只在缩放完成之后调用：缩放前 DeviceDpi 还不是真值，算出来的宽度会差一倍，
    /// 然后又被缩放乘一次。内容超过屏幕可用高度时靠外面那层滚动壳兜底。
    /// </summary>
    private void FitToContent()
    {
        var scale = DpiScale;
        var minHeight = (int)Math.Round(320 * scale);
        var width = Math.Max(320, (int)Math.Round(DialogWidth * scale));
        // 先定宽度再排版：宽度不定的话，内容会按控件默认的 300px 宽换行，高度算多。
        ClientSize = new Size(width, ClientSize.Height);
        layout.PerformLayout();
        var wanted = layout.PreferredSize.Height + 2;
        var available = Math.Max(minHeight, Screen.FromControl(this).WorkingArea.Height - 40);
        ClientSize = new Size(width, Math.Clamp(wanted, minHeight, available));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);   // 里面完成按 DPI 缩放
        FitToContent();
    }

    private void BuildAdvancedPanel()
    {
        // Dock=Top（不是 Fill）＋AutoSize：高度由内容决定，这样它在表格里的行高才是对的。
        advanced.Dock = DockStyle.Top;
        advanced.AutoSize = true;
        advanced.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        advanced.ColumnCount = 2;
        advanced.RowCount = 6;
        advanced.Padding = Padding.Empty;
        advanced.Margin = Padding.Empty;
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 6; row++) advanced.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        format.DropDownStyle = ComboBoxStyle.DropDownList;
        format.Dock = DockStyle.Fill;
        format.Items.Add("OpenAI Chat Completions");
        format.Items.Add("Anthropic Messages");
        displayName.Dock = DockStyle.Fill;
        displayName.PlaceholderText = "留空则显示模型 ID";
        sendSampling.Text = "发送 temperature（部分模型固定采样参数，传了会报错）";
        sendSampling.AutoSize = true;
        sendSampling.Margin = new Padding(0, 6, 0, 0);
        supportsVision.Text = "支持图片输入（只有多模态模型能收图，如 deepseek-flash / kimi-k3 / qwen3.8-max）";
        supportsVision.AutoSize = true;
        supportsVision.Margin = new Padding(0, 6, 0, 0);

        advanced.Controls.Add(FieldLabel("接口格式"), 0, 0);
        advanced.Controls.Add(format, 1, 0);
        advanced.Controls.Add(FieldLabel("模型展示名称"), 0, 1);
        advanced.Controls.Add(displayName, 1, 1);
        advanced.Controls.Add(FieldLabel("上下文窗口"), 0, 2);
        advanced.Controls.Add(WithUnit(contextWindow, "输入 token，0 = 未声明"), 1, 2);
        advanced.Controls.Add(FieldLabel("最大输出"), 0, 3);
        advanced.Controls.Add(WithUnit(maxOutput, "输出 token，0 = 用服务端默认"), 1, 3);
        advanced.Controls.Add(sendSampling, 1, 4);
        advanced.Controls.Add(supportsVision, 1, 5);
    }

    private static Control WithUnit(Control input, string unit)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(input, 0, 0);
        var label = new Label { Text = unit, AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(8, 6, 0, 0) };
        row.Controls.Add(label, 1, 0);
        return row;
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(0, 10, 0, 2) };

    private static int IndexOf(ProviderPreset preset)
    {
        for (var index = 0; index < ProviderPreset.All.Count; index++)
            if (ProviderPreset.All[index].Id == preset.Id) return index;
        return ProviderPreset.All.Count - 1;
    }

    private ProviderPreset SelectedPreset =>
        presets.SelectedIndex >= 0 ? ProviderPreset.All[presets.SelectedIndex] : ProviderPreset.Custom;

    private AiApiFormat SelectedFormat =>
        (AiApiFormat)Math.Clamp(format.SelectedIndex, 0, 1);

    /// <summary>切换服务商时套用预设：地址、接口格式、采样参数开关与模型列表都跟着换。</summary>
    private void ApplyPreset()
    {
        var preset = SelectedPreset;
        hint.Text = preset.Hint;
        models.Items.Clear();

        if (preset.IsLocal)
        {
            endpoint.Text = string.Empty;
            models.Text = string.Empty;
            modelNote.Text = string.Empty;
            SetEnabled(false);
            testResult.Text = "本地模拟不需要连接。";
            return;
        }

        SetEnabled(true);
        if (!string.IsNullOrWhiteSpace(preset.Endpoint)) endpoint.Text = preset.Endpoint;
        foreach (var item in preset.Models) models.Items.Add(item.Id);
        models.Text = preset.DefaultModel?.Id ?? string.Empty;
        format.SelectedIndex = (int)preset.Format;
        sendSampling.Checked = preset.SendsSamplingParameters;
        modelNote.Text = preset.NeedsManualModel
            ? "该服务商不预置模型 ID，请按文档手填。"
            : string.Empty;
        if (preset.DefaultModel is { } model) FillFromModel(model);
        testResult.Text = string.Empty;
    }

    /// <summary>用户选中某个预设模型版本时，把已核实的上下文、最大输出与图片输入能力一并填上。</summary>
    private void FillFromModel(ProviderModel model)
    {
        if (model.ContextWindow > 0) contextWindow.Value = Math.Clamp(model.ContextWindow, 0, (int)contextWindow.Maximum);
        if (model.MaxOutputTokens > 0) maxOutput.Value = Math.Clamp(model.MaxOutputTokens, 0, (int)maxOutput.Maximum);
        supportsVision.Checked = model.SupportsVision;
        modelNote.Text = string.IsNullOrWhiteSpace(model.Note) ? string.Empty : model.Note;
    }

    /// <summary>把保存过的配置回填到界面：预设只是默认值，用户改过的以配置为准。</summary>
    private void RestoreFromConfig()
    {
        if (!string.IsNullOrWhiteSpace(config.Endpoint)) endpoint.Text = config.Endpoint;
        if (!string.IsNullOrWhiteSpace(config.Model)) models.Text = config.Model;
        fullUrl.Checked = config.UseFullUrl;
        displayName.Text = config.DisplayName;
        contextWindow.Value = Math.Clamp(config.ContextWindow, 0, (int)contextWindow.Maximum);
        maxOutput.Value = Math.Clamp(config.MaxOutputTokens, 0, (int)maxOutput.Maximum);
        sendSampling.Checked = config.SendSamplingParameters;
        supportsVision.Checked = config.SupportsImageInput;
        // 密钥**不回显明文**（只写不回读）：只显示脱敏描述符，留空表示沿用已保存的那把。
        apiKey.Text = string.Empty;
        RefreshKeyHint();
        // 接口格式对预设是固定的，只有「自定义」才以保存值为准。
        var custom = SelectedPreset.Id == ProviderPreset.Custom.Id;
        format.SelectedIndex = Math.Clamp((int)(custom ? config.ApiFormat : SelectedPreset.Format), 0, 1);
    }

    private void PresetEvents()
    {
        presets.SelectedIndexChanged += (_, _) => { ApplyPreset(); UpdateUrlPreview(); RefreshFormatAvailability(); };
        models.SelectedIndexChanged += (_, _) =>
        {
            if (models.SelectedItem is string id && SelectedPreset.Models.FirstOrDefault(item => item.Id == id) is { } model)
                FillFromModel(model);
        };
        models.TextChanged += (_, _) =>
        {
            var text = models.Text.Trim();
            modelNote.Text = text.Length == 0
                ? string.Empty
                : SelectedPreset.Models.FirstOrDefault(item => item.Id == text)?.Note
                  ?? "手填的模型 ID：上下文窗口与最大输出按服务商文档填写，未知就留 0。";
            UpdateUrlPreview();
        };
        endpoint.TextChanged += (_, _) => UpdateUrlPreview();
        fullUrl.CheckedChanged += (_, _) => UpdateUrlPreview();
        format.SelectedIndexChanged += (_, _) => UpdateUrlPreview();
        RefreshFormatAvailability();
    }

    /// <summary>接口格式随服务商固定：预设已经决定了用哪种格式，只有「自定义」需要用户自己选。</summary>
    private void RefreshFormatAvailability()
    {
        var custom = SelectedPreset.Id == ProviderPreset.Custom.Id || SelectedPreset.IsLocal;
        format.Enabled = custom;
        if (!custom) format.SelectedIndex = (int)SelectedPreset.Format;
    }

    /// <summary>把将要实际请求的地址显示出来：路径是拼的还是用户自己写的，一看便知。</summary>
    private void UpdateUrlPreview()
    {
        if (SelectedPreset.IsLocal) { urlPreview.Text = string.Empty; return; }
        var probe = BuildProbe();
        urlPreview.Text = string.IsNullOrWhiteSpace(probe.Endpoint)
            ? "实际请求：—"
            : $"实际请求：POST {probe.RequestUrl}";
    }

    private void ToggleAdvanced()
    {
        // 收起的行靠"控件不可见"来塌掉（AutoSize 行对不可见控件算 0 高），
        // 不再手动改 RowStyle——那样窗口高度和行高会各自记一份，迟早对不上。
        advanced.Visible = advancedToggle.Checked;
        FitToContent();
    }

    private void SetEnabled(bool enabled)
    {
        endpoint.Enabled = enabled;
        fullUrl.Enabled = enabled;
        models.Enabled = enabled;
        apiKey.Enabled = enabled;
        test.Enabled = enabled;
        advancedToggle.Enabled = enabled;
        format.Enabled = enabled && SelectedPreset.Id == ProviderPreset.Custom.Id;
    }

    /// <summary>
    /// 密钥一行下面的说明：当前用的是哪把（脱敏）、存在哪里、怎么保护、留空是什么意思。
    /// 配置里的密钥解不开时用醒目颜色要求重填，而不是让用户以为「密钥没配」。
    /// </summary>
    private void RefreshKeyHint()
    {
        if (config.ApiKeyUnreadable)
        {
            keyHint.ForeColor = Color.FromArgb(190, 70, 70);
            keyHint.Text = "配置文件里的密钥无法解密（配置可能来自其它 Windows 账户或机器）。请重新填写密钥。";
            return;
        }

        keyHint.ForeColor = Theme.TextMuted;
        var typed = apiKey.Text.Trim();
        var current = typed.Length > 0
            ? $"将替换为新密钥 {SecretProtector.Describe(typed)}"
            : $"当前密钥：{SecretProtector.Describe(config.ApiKey)}";
        var protection = config.ApiKeyWasPlaintext
            ? "检测到原来的密钥是明文保存的，已自动加密。"
            : "以 Windows DPAPI 按当前账户加密后保存。";
        keyHint.Text = $"{current}　—— {protection}\n文件：{AiProviderSettings.ConfigFilePath}（拷到其它账户或机器无法解密；留空表示不修改）";
    }

    private AiProviderConfig BuildProbe() => new()
    {
        Endpoint = endpoint.Text.Trim(),
        Model = models.Text.Trim(),
        // 密钥框留空表示沿用已保存的那把，测试连接必须用同一把，否则会误报「鉴权失败」。
        ApiKey = apiKey.Text.Trim().Length > 0 ? apiKey.Text.Trim() : config.ApiKey,
        ApiFormat = SelectedPreset.Id == ProviderPreset.Custom.Id || SelectedPreset.IsLocal ? SelectedFormat : SelectedPreset.Format,
        UseFullUrl = fullUrl.Checked,
        Temperature = config.Temperature,
        MaxOutputTokens = (int)maxOutput.Value,
        SendSamplingParameters = sendSampling.Checked
    };

    private async Task TestAsync()
    {
        test.Enabled = false;
        save.Enabled = false;
        testResult.ForeColor = Theme.TextMuted;
        testResult.Text = "正在测试…";
        try
        {
            var probe = new OpenAiCompatibleProvider(BuildProbe(), probeHttp);
            var (ok, message) = await probe.TestAsync();
            testResult.ForeColor = ok ? Color.FromArgb(40, 140, 90) : Color.FromArgb(190, 70, 70);
            testResult.Text = message;
        }
        finally
        {
            test.Enabled = true;
            save.Enabled = true;
        }
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(endpoint.Text) || string.IsNullOrWhiteSpace(models.Text))
        {
            MessageBox.Show("接口地址与模型 ID 都不能为空。若暂时不接入模型，请点「先用本地模拟」。", "接入大模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (config.ApiKeyUnreadable && apiKey.Text.Trim().Length == 0)
        {
            MessageBox.Show("配置里的密钥无法解密，必须重新填写密钥后再保存。", "接入大模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 直接改已加载的配置对象，避免重建配置时丢掉图像、ComfyUI、目录等既有设置。
        var preset = SelectedPreset;
        config.Endpoint = endpoint.Text.Trim();
        config.Model = models.Text.Trim();
        // 留空＝沿用已保存的密钥，不把已存的那把抹掉（因为界面本来就不回显它）。
        if (apiKey.Text.Trim().Length > 0) config.ApiKey = apiKey.Text.Trim();
        config.ApiKeyUnreadable = false;
        config.ApiFormat = preset.Id == ProviderPreset.Custom.Id ? SelectedFormat : preset.Format;
        config.UseFullUrl = fullUrl.Checked;
        config.DisplayName = displayName.Text.Trim();
        config.ContextWindow = (int)contextWindow.Value;
        config.MaxOutputTokens = (int)maxOutput.Value;
        config.SendSamplingParameters = sendSampling.Checked;
        config.SupportsImageInput = supportsVision.Checked;
        config.ProviderChoiceMade = true;
        config.UseLocalProvider = false;
        WarnIfNotPersisted(AiProviderSettings.Save(config));
        Outcome = new AiSetupOutcome(Saved: true, UseLocal: false);
        DialogResult = DialogResult.OK;
    }

    private void UseLocalAndClose()
    {
        if (MessageBox.Show(
                "选择本地模拟后，AI 生成与对话都只是关键词占位的占位结果，不会真正调用大模型。\n\n确定继续吗？",
                "本地模拟", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        config.ProviderChoiceMade = true;
        config.UseLocalProvider = true;
        WarnIfNotPersisted(AiProviderSettings.Save(config));
        Outcome = new AiSetupOutcome(Saved: false, UseLocal: true);
        DialogResult = DialogResult.OK;
    }

    /// <summary>配置写不进去时明确告知，避免用户以为已经接好了。</summary>
    private static void WarnIfNotPersisted(bool saved)
    {
        if (saved) return;
        MessageBox.Show(
            $"配置无法写入：{AiProviderSettings.ConfigFilePath}\n\n本次选择只在当前会话生效，重启后会再次询问。",
            "接入大模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
