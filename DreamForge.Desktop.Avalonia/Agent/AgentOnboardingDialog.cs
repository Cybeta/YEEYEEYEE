using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DreamForge.Desktop;
using static DreamForge.Desktop.Avalonia.AgentDialogUi;

namespace DreamForge.Desktop.Avalonia;

/// <summary>接入引导的结果。null（没返回值）表示用户什么都没选，下次启动还会问。</summary>
internal sealed record AgentOnboardingOutcome(bool Saved, bool UseLocal);

/// <summary>
/// 首次启动的接入引导（服务商 → 密钥 → 测试连接）。
///
/// 为什么必须有这一步：新主端如果一直没接入模型，AI 生成与对话只会给本地模拟的占位结果。
/// 旧端靠这一步保证「不允许静默降级后让用户以为在用大模型」，新主端此前缺的就是它。
///
/// 三条出口都是有意的：
/// - 完成接入：写配置并置 ProviderChoiceMade，之后不再自动弹；
/// - 先用本地模拟：要求二次确认（说明只会得到占位结果）后才置 ProviderChoiceMade；
/// - 稍后再说：**不写** ProviderChoiceMade，下次启动照样问——但也绝不偷偷改成「已选择」。
///
/// 与模型设置的分工：引导只走「选一家 + 密钥 + 测试」这条最短路径，
/// 自定义接口格式、上下文窗口、Agent 工作目录这些留给设置窗口。
/// </summary>
internal static class AgentOnboardingDialog
{
    /// <summary>测试连接只探一次，共用一个客户端即可，不必每次点都新建。</summary>
    private static readonly HttpClient probeHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<AgentOnboardingOutcome?> ShowAsync(Window owner, bool firstRun)
    {
        var config = AiProviderSettings.Load();

        var presetBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        foreach (var candidate in ProviderPreset.All) presetBox.Items.Add(candidate.Name);

        var presetHint = Note(string.Empty);
        var modelBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        var modelNote = Note(string.Empty);
        var modelField = Field("模型 ID（可手填，服务商下线旧模型后不必等程序更新）", config.Model);
        var endpointField = Field("接口地址（预设会填好；自定义时填基础地址即可）", config.Endpoint);

        // 厂家预设：能力由所选模型决定，这里只把结论**显示出来**，不让用户去填。
        var autoCapabilities = Note(string.Empty, AgentNoteLevel.Success);
        autoCapabilities.IsVisible = false;

        // 自定义 API：显示名与高级设置（参考旧端接入引导的「高级配置」），只在选了「自定义」时出现。
        var displayNameField = Field("模型显示名称（留空就显示模型 ID）", config.DisplayName);
        var formatBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        formatBox.Items.Add("OpenAI 兼容：/chat/completions");
        formatBox.Items.Add("Anthropic 兼容：/v1/messages");
        formatBox.SelectedIndex = config.ApiFormat == AiApiFormat.AnthropicMessages ? 1 : 0;
        var contextField = Field("上下文窗口（输入 token；0 = 未声明，不按窗口截断上下文）",
            config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0");
        var maxOutputField = Field("最大输出（输出 token；0 = 用服务端默认）",
            config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0");
        var sendSamplingBox = new CheckBox
        {
            Content = "发送 temperature 等采样参数（部分模型固定采样，传了会报错）",
            IsChecked = config.SendSamplingParameters,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };
        var supportImageBox = new CheckBox
        {
            Content = "支持图片输入（多模态模型才能收图）",
            IsChecked = config.SupportsImageInput,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };

        var customBlock = new StackPanel { Spacing = 9, IsVisible = false };
        customBlock.Children.Add(Header("自定义 API 设置"));
        customBlock.Children.Add(displayNameField.Label);
        customBlock.Children.Add(displayNameField.Box);
        customBlock.Children.Add(Note("下面是高级设置：拿不准就留默认值，接入后也能在模型设置里改。"));
        customBlock.Children.Add(Note("接口格式（预设固定；自定义时自己选）"));
        customBlock.Children.Add(formatBox);
        customBlock.Children.Add(contextField.Label);
        customBlock.Children.Add(contextField.Box);
        customBlock.Children.Add(maxOutputField.Label);
        customBlock.Children.Add(maxOutputField.Box);
        customBlock.Children.Add(sendSamplingBox);
        customBlock.Children.Add(supportImageBox);
        var apiKey = new TextBox
        {
            Text = config.ApiKeyUnreadable ? string.Empty : config.ApiKey,
            PasswordChar = '●',
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk"),
            Watermark = "粘贴 API 密钥（留空表示不修改已保存的那把）"
        };
        var keyHint = Note(string.Empty);
        var test = Secondary("测试连接");
        var testResult = Note("填好密钥后点「测试连接」，能出结果再接入。");
        testResult.FontSize = 11;

        var presetModels = new List<ProviderModel>();

        ProviderPreset CurrentPreset() =>
            presetBox.SelectedIndex >= 0 ? ProviderPreset.All[presetBox.SelectedIndex] : ProviderPreset.Custom;

        /// <summary>
        /// 当前这家 + 界面上真正填着的型号 → 配置字段。下拉里换了型号（默认款换成便宜款）能力要跟着换；
        /// 填的型号不在预设表里（服务商新上线或已下线），退回按这家默认型号算，不假装知道它的能力。
        /// </summary>
        ProviderPresetValues ResolveCurrentModel(ProviderPreset preset)
        {
            var typed = modelField.Box.Text?.Trim() ?? string.Empty;
            var matched = presetModels.FirstOrDefault(candidate => string.Equals(candidate.Id, typed, StringComparison.Ordinal));
            return ProviderPresetValues.Resolve(preset, matched);
        }

        /// <summary>
        /// 这家是不是「能力值得由用户自己填」：规则与模型设置共用一份（见 ProviderPresetValues）。
        /// 这样的厂家要沿用他已保存的那份值，而不是按预设里的 0 覆盖掉——引导页不该顺手把老配置擦掉。
        /// </summary>
        bool NeedsManualCapabilities(ProviderPreset preset) => ProviderPresetValues.NeedsManualCapabilities(preset);

        void ApplyModel(ProviderModel candidate)
        {
            var values = ProviderPresetValues.Resolve(CurrentPreset(), candidate);
            modelField.Box.Text = values.ModelId;
            modelNote.Text = candidate.Note;
            modelNote.IsVisible = !string.IsNullOrWhiteSpace(candidate.Note);
            ShowAutoCapabilities(values);
        }

        /// <summary>把「这台预设的模型自动开了什么能力」摊给用户看，而不是悄悄生效。</summary>
        void ShowAutoCapabilities(ProviderPresetValues values)
        {
            var preset = CurrentPreset();
            if (preset.Id == ProviderPreset.Custom.Id)
            {
                autoCapabilities.IsVisible = false;      // 自定义的能力由上面的高级设置决定
                return;
            }

            if (preset.Models.Count == 0)
            {
                // 本地 Ollama 这类不预置型号：能力无从判断，如实说明去哪儿设，不猜。
                autoCapabilities.Text = "这家没有预置型号：填入你本地已有的模型 ID 后，上下文窗口与图片输入请在「模型设置」里声明。";
                autoCapabilities.Foreground = Brush("DfInk3");
                autoCapabilities.IsVisible = true;
                return;
            }

            autoCapabilities.Text = $"已按预设自动启用：{values.DescribeCapabilities()}";
            autoCapabilities.Foreground = Brush("DfSuccess");
            autoCapabilities.IsVisible = true;
        }

        void ApplyPreset(ProviderPreset preset, bool fillFields)
        {
            var isCustom = preset.Id == ProviderPreset.Custom.Id;

            presetHint.Text = preset.Hint;
            presetHint.IsVisible = !string.IsNullOrWhiteSpace(preset.Hint);

            // 显示名与高级设置只在「自定义」出现；厂家预设的能力来自模型，界面上只给结论不给输入框。
            customBlock.IsVisible = isCustom;
            modelBox.IsVisible = preset.Models.Count > 0 && !isCustom;
            autoCapabilities.IsVisible = false;

            presetModels.Clear();
            presetModels.AddRange(preset.Models);
            modelBox.Items.Clear();
            foreach (var candidate in preset.Models)
                modelBox.Items.Add(string.IsNullOrWhiteSpace(candidate.Note) ? candidate.Id : $"{candidate.Id} — {candidate.Note}");
            modelBox.SelectedIndex = -1;
            modelNote.IsVisible = false;

            if (!fillFields) return;

            if (isCustom)
            {
                // 自定义：地址与模型都由用户自己填；显示名与能力值沿用他原来保存的那份，不清空。
                endpointField.Box.Text = string.Empty;
                modelField.Box.Text = config.Model;
                displayNameField.Box.Text = config.DisplayName;
                formatBox.SelectedIndex = config.ApiFormat == AiApiFormat.AnthropicMessages ? 1 : 0;
                contextField.Box.Text = config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0";
                maxOutputField.Box.Text = config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0";
                sendSamplingBox.IsChecked = config.SendSamplingParameters;
                supportImageBox.IsChecked = config.SupportsImageInput;
                ShowAutoCapabilities(ProviderPresetValues.Resolve(preset, null));
                RefreshKeyHint();
                return;
            }

            // 与模型设置共用同一份换算：选了 Kimi 就不发采样参数、选了 GLM 就填 glm-5.3，两边一致。
            var values = ProviderPresetValues.Resolve(preset, null);
            endpointField.Box.Text = values.Endpoint;
            if (preset.DefaultModel is { } first) ApplyModel(first);
            else
            {
                modelField.Box.Text = string.Empty;
                modelNote.IsVisible = false;
                ShowAutoCapabilities(values);
            }
            RefreshKeyHint();
        }

        presetBox.SelectionChanged += (_, _) =>
        {
            if (presetBox.SelectedIndex < 0) return;
            ApplyPreset(ProviderPreset.All[presetBox.SelectedIndex], fillFields: true);
        };

        modelBox.SelectionChanged += (_, _) =>
        {
            var index = modelBox.SelectedIndex;
            if (index >= 0 && index < presetModels.Count) ApplyModel(presetModels[index]);
        };

        // 首次启动（或本来就什么都没配）预选第一家有完整预设的服务商并把字段填好，用户只需要补密钥——
        // 从面板 ★ 进来时若还是空表单，用户得自己想「该选哪家」，这一步就白引导了。
        // 已经配过（或显式选了本地模拟）则按当前地址反推是哪一家回显，且不覆盖用户改过的值。
        var nothingConfigured = string.IsNullOrWhiteSpace(config.Endpoint) && string.IsNullOrWhiteSpace(config.Model);
        if (!config.UseLocalProvider && (firstRun || nothingConfigured))
        {
            presetBox.SelectedIndex = 0;
            ApplyPreset(ProviderPreset.All[0], fillFields: true);
        }
        else
        {
            var current = config.UseLocalProvider ? ProviderPreset.Local : ProviderPreset.Match(config.Endpoint);
            presetBox.SelectedIndex = ProviderPreset.All.ToList().FindIndex(candidate => candidate.Id == current.Id);
            ApplyPreset(current, fillFields: false);
            var savedModel = presetModels.FindIndex(candidate => string.Equals(candidate.Id, config.Model, StringComparison.Ordinal));
            if (savedModel >= 0) modelBox.SelectedIndex = savedModel;
        }

        // 失败原因就地显示在按钮旁边（声明在这里，供下面的密钥提示与各按钮共用）。
        var status = Note(string.Empty, AgentNoteLevel.Error);
        status.IsVisible = false;

        void RefreshKeyHint()
        {
            var typed = apiKey.Text?.Trim() ?? string.Empty;
            var current = typed.Length > 0
                ? $"将替换为新密钥 {SecretProtector.Describe(typed)}"
                : config.ApiKeyUnreadable
                    ? "已保存的密钥在当前账户 / 机器 / 系统上解不开，必须重新填写"
                    : config.ApiKey.Length > 0
                        ? $"已保存密钥 {SecretProtector.Describe(config.ApiKey)}，留空即沿用"
                        : "还没有密钥";
            var protection = SecretProtector.ProtectsAtRest
                ? $"密钥按 {SecretProtector.StorageDescription} 加密落盘"
                : $"注意：当前平台没有可用的系统密钥库，密钥会以明文保存（{SecretProtector.StorageDescription}）";
            keyHint.Text = $"{current}；{protection}。";
        }
        RefreshKeyHint();
        // 用户一动手就撤掉上一次的报错：留着「还没有填密钥」而输入框里已经有密钥，只会让人以为还没生效。
        apiKey.TextChanged += (_, _) => { RefreshKeyHint(); Hide(status); };

        // 测试连接用的配置从**当前界面**取值：密钥留空时沿用已保存的那把，否则会误报鉴权失败。
        AiProviderConfig BuildProbe()
        {
            var preset = CurrentPreset();
            var values = ResolveCurrentModel(preset);
            // 自定义 / 没预置型号：界面上的能力值（可能沿用已保存的那份）比预设里的 0 更可信。
            var manual = NeedsManualCapabilities(preset);
            return new AiProviderConfig
            {
                Endpoint = endpointField.Box.Text?.Trim() ?? string.Empty,
                Model = modelField.Box.Text?.Trim() ?? string.Empty,
                ApiKey = apiKey.Text?.Trim() is { Length: > 0 } typed ? typed : config.ApiKey,
                ApiFormat = manual ? SelectedFormat() : values.Format,
                UseFullUrl = false,
                Temperature = config.Temperature,
                ContextWindow = manual ? Parse(contextField.Box.Text) : values.ContextWindow,
                MaxOutputTokens = manual ? Parse(maxOutputField.Box.Text) : values.MaxOutputTokens,
                SendSamplingParameters = manual ? sendSamplingBox.IsChecked == true : values.SendsSamplingParameters,
                SupportsImageInput = manual ? supportImageBox.IsChecked == true : values.SupportsVision
            };
        }

        AiApiFormat SelectedFormat() =>
            formatBox.SelectedIndex == 1 ? AiApiFormat.AnthropicMessages : AiApiFormat.OpenAiChat;

        test.Click += async (_, _) =>
        {
            Hide(status);
            test.IsEnabled = false;
            testResult.Text = "正在测试…";
            testResult.Foreground = Brush("DfInk3");
            try
            {
                var provider = new OpenAiCompatibleProvider(BuildProbe(), probeHttp);
                var (ok, message) = await provider.TestAsync();
                testResult.Text = message;
                testResult.Foreground = Brush(ok ? "DfSuccess" : "DfError");
            }
            finally
            {
                test.IsEnabled = true;
            }
        };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
        body.Children.Add(Note(
            "三步接入：① 选一家服务商（地址、接口格式、推荐模型会自动填好）② 贴上 API 密钥 ③ 点「测试连接」确认能出结果。\n"
            + "需要自定义接口格式、上下文窗口或 Agent 工作目录，走 Agent 面板右上角的模型设置。"));
        body.Children.Add(Header("服务商预设"));
        body.Children.Add(presetBox);
        body.Children.Add(presetHint);
        body.Children.Add(Header("模型与接口"));
        body.Children.Add(modelBox);
        body.Children.Add(modelNote);
        body.Children.Add(modelField.Label);
        body.Children.Add(modelField.Box);
        body.Children.Add(endpointField.Label);
        body.Children.Add(endpointField.Box);
        body.Children.Add(autoCapabilities);      // 厂家预设：自动启用了什么，摊开显示
        body.Children.Add(customBlock);           // 自定义：显示名 + 高级设置
        body.Children.Add(Header("API 密钥"));
        body.Children.Add(apiKey);
        body.Children.Add(keyHint);
        body.Children.Add(test);
        body.Children.Add(testResult);

        var finish = Primary("完成接入");
        var useLocal = Secondary("先用本地模拟");
        var later = Secondary("稍后再说");

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { later, useLocal, finish }
        };

        // 提示区在左、按钮在右：失败信息要贴着按钮出现，用户不用去翻上面找原因。
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footerRow.Children.Add(status);
        Grid.SetColumn(footerButtons, 1);
        footerRow.Children.Add(footerButtons);

        var dialog = DialogShell.Create(
            firstRun ? "接入大模型（首次启动）" : "接入大模型",
            Layout(body, Footer(footerRow)),
            660,
            700);

        // 完成接入：把界面上这几项落到配置上（只覆盖引导负责的字段，不动图像 / ComfyUI / 工作目录等既有设置）。
        finish.Click += (_, _) =>
        {
            var preset = CurrentPreset();
            var values = ResolveCurrentModel(preset);
            var endpoint = endpointField.Box.Text?.Trim() ?? string.Empty;
            var model = modelField.Box.Text?.Trim() ?? string.Empty;
            var typed = apiKey.Text?.Trim() ?? string.Empty;

            if (endpoint.Length == 0 || model.Length == 0)
            {
                Show(status, "接口地址与模型 ID 都不能为空。暂时不接入模型请点「先用本地模拟」。");
                return;
            }
            if (config.ApiKeyUnreadable && typed.Length == 0)
            {
                Show(status, "已保存的密钥在当前账户 / 机器 / 系统上解不开，必须重新填写密钥后再接入。");
                return;
            }

            // 首次接入时密钥不能留空：否则配置看起来「已接入」，一对话就 401，
            // 而用户会以为是程序坏了。本地部署（Ollama 之类）确实不需要密钥，按回环地址放行。
            var hasSavedKey = config.ApiKey.Length > 0 && !config.ApiKeyUnreadable;
            if (typed.Length == 0 && !hasSavedKey && !IsLoopbackEndpoint(endpoint))
            {
                Show(status, "还没有填密钥。填上密钥再接入；如果这家确实不需要密钥（本地部署），请把地址指向 127.0.0.1 或 localhost。");
                return;
            }

            config.Endpoint = endpoint;
            config.Model = model;
            // 厂家预设：接口格式与能力都由预设模型决定（界面上只显示了结论）；
            // 自定义（以及没预置型号的本地部署）：以用户已有的 / 填的为准，别把老配置擦成 0。
            var manual = NeedsManualCapabilities(preset);
            config.ApiFormat = manual ? SelectedFormat() : values.Format;
            config.UseFullUrl = false;
            config.SendSamplingParameters = manual ? sendSamplingBox.IsChecked == true : values.SendsSamplingParameters;
            config.ContextWindow = manual ? Parse(contextField.Box.Text) : values.ContextWindow;
            config.MaxOutputTokens = manual ? Parse(maxOutputField.Box.Text) : values.MaxOutputTokens;
            config.SupportsImageInput = manual ? supportImageBox.IsChecked == true : values.SupportsVision;
            // 显示名只属于自定义接口：厂家预设的模型名本身可读，留着上一家填的显示名会挂个对不上的名字。
            config.DisplayName = preset.Id == ProviderPreset.Custom.Id
                ? displayNameField.Box.Text?.Trim() ?? string.Empty
                : string.Empty;
            if (typed.Length > 0) config.ApiKey = typed;
            config.ApiKeyUnreadable = false;
            config.UseLocalProvider = false;
            config.ProviderChoiceMade = true;

            if (!AiProviderSettings.Save(config))
            {
                Show(status, $"配置写不进去（{AiProviderSettings.ConfigFilePath}），本次接入没有生效。请检查目录权限后重试。");
                return;
            }

            dialog.Close(new AgentOnboardingOutcome(Saved: true, UseLocal: false));
        };

        useLocal.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync(
                dialog,
                "改用本地模拟",
                "选择本地模拟后，AI 生成与对话都只是关键词占位的占位结果，不会真正调用大模型。\n\n确定继续吗？",
                "确认使用本地模拟");
            if (!confirmed) return;

            config.ProviderChoiceMade = true;
            config.UseLocalProvider = true;
            if (!AiProviderSettings.Save(config))
            {
                Show(status, $"配置写不进去（{AiProviderSettings.ConfigFilePath}），本次选择没有生效。请检查目录权限后重试。");
                return;
            }

            dialog.Close(new AgentOnboardingOutcome(Saved: false, UseLocal: true));
        };

        later.Click += (_, _) => dialog.Close(null);

        return await dialog.ShowDialog<AgentOnboardingOutcome?>(owner);
    }

    /// <summary>就地显示失败原因，并把提示区露出来。</summary>
    private static void Show(TextBlock status, string message)
    {
        status.Text = message;
        status.IsVisible = true;
    }

    /// <summary>撤掉上一次的报错（用户已经动手改了，旧结论不再作数）。</summary>
    private static void Hide(TextBlock status) => status.IsVisible = false;

    /// <summary>把输入框里的数字读出来；空或非法一律当 0（0 在这几个字段里就是「不声明」）。</summary>
    private static int Parse(string? text) => int.TryParse(text?.Trim(), out var value) && value > 0 ? value : 0;

    /// <summary>是不是指向本机的地址（本地 Ollama、内网网关这类不需要密钥的部署）。</summary>
    private static bool IsLoopbackEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
