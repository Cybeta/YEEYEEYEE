using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DreamForge.Desktop;
using static DreamForge.Desktop.Avalonia.AgentDialogUi;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// 设置第一页：模型接入。
///
/// 页面分上下两段：
/// ①「已保存的配置」——多份接口各自成条，可以同时启用多份，其中一份是**当前**（Agent 真正发请求用的那一份）。
///    勾选框 = 要不要出现在 Agent 的模型选择器里；点一行 = 切换当前并载入下面的表单。
/// ②下面那张表单是**当前那一份**的编辑区，字段沿用原来的模型接入界面（服务商预设 / 接口 / 密钥 / 工作目录），
///    另外补了一个「从接口拉取模型列表」——模型名是这份配置里最容易写错、也最容易过期的一项。
///
/// 关于「一份都不启用」：启用多份是常态，但**当前必须有且只有一份**，否则发请求时不知道用谁。
/// 所以停用当前那一份之后由 <see cref="AiProviderSettings.ResolveSelected"/> 自动切到另一份启用中的，
/// 一份都不启用时如实告诉用户「Agent 会退回本地模拟」，绝不悄悄换一个模型替他回答。
/// </summary>
internal static class SettingsModelPage
{
    public static SettingsPageSection Build(AiProviderConfig config, SettingsPageContext context)
    {
        // 页内的写法保持原样：外壳的能力只在开头取一次别名，正文不必到处写 context.xxx。
        var owner = context.Owner;
        var report = context.Report;
        Func<bool> requestSave = context.SaveAll;
        // 页内回报一律走它：绝大多数是普通提示，写全两个参数只会让调用点变得很吵。
        void Say(string text, AgentNoteLevel level = AgentNoteLevel.Info) => report(text, level);

        // 一份都没有（全新安装、还没接入过）时先造一份空壳当"编辑目标"：
        // 表单总得有个落点，否则用户填完点保存，Save 里那条「顶层写回选中项」找不到任何一份可写。
        // 这里加的空壳不会写盘，只有用户真的点了保存才会落盘（那时也只落盘一次）。
        config.Profiles ??= new List<AiProviderProfile>();
        if (config.Profiles.Count == 0)
        {
            var first = new AiProviderProfile { DisplayName = "默认配置", Enabled = true };
            config.Profiles.Add(first);
            config.SelectedProfileId = first.Id;
        }

        // 密钥的"解不开"是**分份**的（换机器 / 换系统账户只影响那一份），所以这里记一份当前份的状态，
        // 而不是一直读顶层那个字段——切换配置后不改它，界面会对着新配置显示上一份的密钥状态。
        var keyUnreadable = config.ApiKeyUnreadable;
        // 表单回显期间不响应控件的 SelectionChanged：程序设值和用户选择走的是同一个事件。
        var loading = false;
        var presetModels = new List<ProviderModel>();
        var fetchedModels = new List<string>();

        // ---------- 表单零件 ----------

        var name = Field("这一份配置的名字（显示在 Agent 的模型选择器里；留空则显示模型名）", config.DisplayName);
        var endpoint = Field("接口地址（基础地址，程序会自动补 /chat/completions 或 /v1/messages）", config.Endpoint);
        var useFullUrl = new CheckBox { Content = "地址已是完整请求 URL（不再自动补路径）", IsChecked = config.UseFullUrl, FontSize = 11, Foreground = Brush("DfInk2") };
        var format = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        format.Items.Add("OpenAI 兼容：/chat/completions");
        format.Items.Add("Anthropic 兼容：/v1/messages");
        format.SelectedIndex = config.ApiFormat == AiApiFormat.AnthropicMessages ? 1 : 0;
        var model = Field("模型名（可手填；服务商下线的旧名请按官方文档改）", config.Model);

        var apiKey = new TextBox
        {
            Text = config.ApiKey,
            PasswordChar = '●',
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk"),
            Watermark = "sk-…（加密后写入配置文件，不回显完整值）"
        };
        var contextWindow = Field("上下文窗口（token；0 表示不声明，不按窗口截断上下文）",
            config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0");
        var maxOutput = Field("单次最大输出 token（0 表示由服务端默认）",
            config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0");
        var sendSampling = new CheckBox { Content = "发送 temperature 等采样参数（部分模型不接受，需关闭）", IsChecked = config.SendSamplingParameters, FontSize = 11, Foreground = Brush("DfInk2") };
        var supportImage = new CheckBox { Content = "模型支持图片输入（多模态，决定能否随对话发图）", IsChecked = config.SupportsImageInput, FontSize = 11, Foreground = Brush("DfInk2") };
        var workspace = Field("Agent 工作文件夹（只能写这个目录内的文件）", config.AgentWorkspace);

        var pickWorkspace = Secondary("选择目录");
        pickWorkspace.HorizontalAlignment = HorizontalAlignment.Left;
        pickWorkspace.Click += async (_, _) =>
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 Agent 工作文件夹",
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) workspace.Box.Text = path;
        };

        // ---------- 服务商预设 ----------

        var presetBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        foreach (var candidate in ProviderPreset.All) presetBox.Items.Add(candidate.Name);

        var presetHint = Note(string.Empty);
        presetHint.TextWrapping = TextWrapping.Wrap;
        presetHint.IsVisible = false;

        var modelPresetBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        var modelNote = Note(string.Empty);
        modelNote.IsVisible = false;

        // 高级设置区顶部的一行说明：厂家预设的这组值是按模型自动填好的，自定义则由用户自己填。
        var capability = Note(string.Empty);

        // 上下文窗口 / 单次输出 / 采样开关 / 图片输入：只挂在「得由用户自己定」的厂家下
        //（自定义 / 不预置型号的本地部署）；厂家预设的能力值由所选模型决定，界面上只给结论。
        var customBlock = new StackPanel { Spacing = 9, IsVisible = false };
        customBlock.Children.Add(Header("高级设置"));
        customBlock.Children.Add(Note("拿不准就留默认值：这几项只影响「按窗口截断上下文」「能否随对话发图」和采样参数，不会改坏接口地址。"));
        customBlock.Children.Add(contextWindow.Label);
        customBlock.Children.Add(contextWindow.Box);
        customBlock.Children.Add(maxOutput.Label);
        customBlock.Children.Add(maxOutput.Box);
        customBlock.Children.Add(sendSampling);
        customBlock.Children.Add(supportImage);

        // ---------- 模型清单（从接口拉） ----------

        var fetchModels = Secondary("从接口拉取模型列表");
        fetchModels.HorizontalAlignment = HorizontalAlignment.Left;
        var modelList = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk"),
            IsVisible = false
        };
        var fetchNote = Note(string.Empty);
        fetchNote.IsVisible = false;

        ProviderPreset CurrentPreset() =>
            presetBox.SelectedIndex >= 0 ? ProviderPreset.All[presetBox.SelectedIndex] : ProviderPreset.Custom;

        AiApiFormat SelectedFormat() =>
            format.SelectedIndex == 1 ? AiApiFormat.AnthropicMessages : AiApiFormat.OpenAiChat;

        /// <summary>这家是不是「要不要用户自己填能力值」：规则与接入引导共用一份（见 ProviderPresetValues）。</summary>
        bool NeedsManualCapabilities(ProviderPreset preset) => ProviderPresetValues.NeedsManualCapabilities(preset);

        void RefreshCapabilityNote(ProviderPreset preset, ProviderModel? candidate)
        {
            if (preset.IsLocal)
            {
                capability.Text = "本地模拟：不调用大模型，这些能力值（上下文窗口 / 图片输入）用不上，只影响本地兜底的占位结果。";
                capability.Foreground = Brush("DfInk3");
                return;
            }

            if (preset.Id == ProviderPreset.Custom.Id)
            {
                capability.Text = "自定义：下面这组值由你自己填，0 表示不声明（上下文窗口为 0 时不按窗口截断上下文）。";
                capability.Foreground = Brush("DfInk3");
                return;
            }

            var values = ProviderPresetValues.Resolve(preset, candidate);
            if (preset.Models.Count == 0)
            {
                capability.Text = "这家没有预置型号：模型 ID 与下面的能力值都要你自己填（照官方文档填即可）。";
                capability.Foreground = Brush("DfInk3");
                return;
            }

            capability.Text = $"已按所选模型自动启用：{values.DescribeCapabilities()}（来自预设表，不必手填）。";
            capability.Foreground = Brush("DfSuccess");
        }

        void ApplyModel(ProviderModel candidate)
        {
            model.Box.Text = candidate.Id;
            contextWindow.Box.Text = candidate.ContextWindow > 0 ? candidate.ContextWindow.ToString() : "0";
            maxOutput.Box.Text = candidate.MaxOutputTokens > 0 ? candidate.MaxOutputTokens.ToString() : "0";
            supportImage.IsChecked = candidate.SupportsVision;
            modelNote.Text = candidate.Note;
            modelNote.IsVisible = !string.IsNullOrWhiteSpace(candidate.Note);
            RefreshCapabilityNote(CurrentPreset(), candidate);
        }

        /// <summary>只回显「预设区」（提示语 / 该家的型号表 / 能力结论），**不动字段值**——回显字段由 LoadForm 负责。</summary>
        void ShowPreset(ProviderPreset preset)
        {
            presetHint.Text = preset.Hint;
            presetHint.IsVisible = !string.IsNullOrWhiteSpace(preset.Hint);

            presetModels.Clear();
            presetModels.AddRange(preset.Models);
            modelPresetBox.Items.Clear();
            foreach (var candidate in preset.Models)
                modelPresetBox.Items.Add(string.IsNullOrWhiteSpace(candidate.Note) ? candidate.Id : $"{candidate.Id} — {candidate.Note}");
            modelPresetBox.SelectedIndex = -1;
            modelPresetBox.IsVisible = preset.Models.Count > 0;
            modelNote.IsVisible = false;
        }

        void ApplyPreset(ProviderPreset preset, bool fillFields)
        {
            ShowPreset(preset);
            // 接口格式由预设决定：只有「自定义」才允许用户自己选，否则会把地址与格式配成互相矛盾的组合。
            format.IsEnabled = preset.Id == ProviderPreset.Custom.Id;
            customBlock.IsVisible = NeedsManualCapabilities(preset);
            RefreshCapabilityNote(preset, null);
            if (!fillFields) return;

            if (preset.Id == ProviderPreset.Custom.Id)
            {
                // 切回「自定义」时沿用已保存的那份，不凭空清空。
                endpoint.Box.Text = config.Endpoint;
                useFullUrl.IsChecked = config.UseFullUrl;
                format.SelectedIndex = config.ApiFormat == AiApiFormat.AnthropicMessages ? 1 : 0;
                model.Box.Text = config.Model;
                contextWindow.Box.Text = config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0";
                maxOutput.Box.Text = config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0";
                sendSampling.IsChecked = config.SendSamplingParameters;
                supportImage.IsChecked = config.SupportsImageInput;
                return;
            }

            // 预设 → 字段的换算与接入引导共用一份（ProviderPresetValues），两边不会各填一套。
            var values = ProviderPresetValues.Resolve(preset, null);
            endpoint.Box.Text = values.Endpoint;
            useFullUrl.IsChecked = values.UseFullUrl;
            format.SelectedIndex = values.Format == AiApiFormat.AnthropicMessages ? 1 : 0;
            sendSampling.IsChecked = values.SendsSamplingParameters;
            if (preset.DefaultModel is { } first) ApplyModel(first);
            else
            {
                model.Box.Text = string.Empty;
                contextWindow.Box.Text = config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0";
                maxOutput.Box.Text = config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0";
                supportImage.IsChecked = config.SupportsImageInput;
            }
        }

        presetBox.SelectionChanged += (_, _) =>
        {
            if (loading || presetBox.SelectedIndex < 0) return;
            ApplyPreset(ProviderPreset.All[presetBox.SelectedIndex], fillFields: true);
        };

        modelPresetBox.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            var index = modelPresetBox.SelectedIndex;
            if (index >= 0 && index < presetModels.Count) ApplyModel(presetModels[index]);
        };

        modelList.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            var index = modelList.SelectedIndex;
            if (index >= 0 && index < fetchedModels.Count) model.Box.Text = fetchedModels[index];
        };

        fetchModels.Click += async (_, _) =>
        {
            fetchModels.IsEnabled = false;
            fetchNote.Text = "正在拉取…";
            fetchNote.Foreground = Brush("DfInk3");
            fetchNote.IsVisible = true;
            try
            {
                // 探测用的是**界面上**的值（地址 / 格式 / 密钥 / 完整 URL），而不是磁盘上那份：
                // 用户刚改完地址就点测试，那一刻磁盘上还没有新地址，用旧的就等于告诉他"失败"。
                var probe = new AiProviderConfig
                {
                    Endpoint = endpoint.Box.Text?.Trim() ?? string.Empty,
                    UseFullUrl = useFullUrl.IsChecked == true,
                    ApiFormat = SelectedFormat(),
                    ApiKey = apiKey.Text is { Length: > 0 } typed ? typed : config.ApiKey,
                    Model = model.Box.Text?.Trim() ?? string.Empty
                };
                var (models, message) = await ModelCatalog.FetchAsync(probe);

                fetchedModels.Clear();
                fetchedModels.AddRange(models);
                modelList.Items.Clear();
                foreach (var item in fetchedModels) modelList.Items.Add(item);
                modelList.IsVisible = fetchedModels.Count > 0;
                modelList.SelectedIndex = fetchedModels.FindIndex(item =>
                    string.Equals(item, probe.Model, StringComparison.Ordinal));

                fetchNote.Text = message;
                fetchNote.Foreground = Brush(fetchedModels.Count > 0 ? "DfInk3" : "DfWarning");
            }
            finally
            {
                fetchModels.IsEnabled = true;
            }
        };

        // ---------- 密钥说明 ----------

        var keyNote = Note(string.Empty);
        keyNote.TextWrapping = TextWrapping.Wrap;

        void RefreshKeyNote()
        {
            var typed = apiKey.Text?.Trim() ?? string.Empty;
            if (keyUnreadable)
            {
                keyNote.Text = "这一份的密钥在当前账户 / 机器 / 系统上解不开（换过系统账户或机器，或把 Windows 上的配置拷到了 macOS）。请重新填写后保存。";
                keyNote.Foreground = Brush("DfWarning");
                return;
            }

            if (config.ApiKeyStoredUnencrypted)
            {
                keyNote.Text = $"注意：当前平台没有可用的系统密钥库，密钥以明文保存在配置文件里（{SecretProtector.StorageDescription}）。请不要把配置文件分享出去或提交到仓库。";
                keyNote.Foreground = Brush("DfError");
                return;
            }

            keyNote.Foreground = Brush("DfInk3");
            keyNote.Text = typed.Length > 0
                ? $"将保存 {SecretProtector.Describe(typed)}；保存后按 {SecretProtector.StorageDescription} 加密落盘。"
                : config.ApiKeyWasPlaintext
                    ? "检测到旧版明文密钥，已按当前平台的方案自动加密写回配置文件。"
                    : $"密钥以密文落盘（{SecretProtector.StorageDescription}），界面只显示首尾各 4 个字符。";
        }
        apiKey.TextChanged += (_, _) => RefreshKeyNote();

        // ---------- 回显 / 写回 ----------

        /// <summary>把「当前那一份」的字段刷到界面上。切换配置、保存后自动切换，都走这里。</summary>
        void LoadForm()
        {
            loading = true;
            try
            {
                var preset = config.UseLocalProvider ? ProviderPreset.Local : ProviderPreset.Match(config.Endpoint);
                presetBox.SelectedIndex = ProviderPreset.All.ToList().FindIndex(candidate => candidate.Id == preset.Id);
                ShowPreset(preset);
                format.IsEnabled = preset.Id == ProviderPreset.Custom.Id;
                customBlock.IsVisible = NeedsManualCapabilities(preset);

                name.Box.Text = config.DisplayName;
                endpoint.Box.Text = config.Endpoint;
                useFullUrl.IsChecked = config.UseFullUrl;
                format.SelectedIndex = config.ApiFormat == AiApiFormat.AnthropicMessages ? 1 : 0;
                model.Box.Text = config.Model;
                apiKey.Text = keyUnreadable ? string.Empty : config.ApiKey;
                contextWindow.Box.Text = config.ContextWindow > 0 ? config.ContextWindow.ToString() : "0";
                maxOutput.Box.Text = config.MaxOutputTokens > 0 ? config.MaxOutputTokens.ToString() : "0";
                sendSampling.IsChecked = config.SendSamplingParameters;
                supportImage.IsChecked = config.SupportsImageInput;
                workspace.Box.Text = config.AgentWorkspace;

                var matched = presetModels.FindIndex(candidate => string.Equals(candidate.Id, config.Model, StringComparison.Ordinal));
                modelPresetBox.SelectedIndex = matched;
                modelNote.Text = matched >= 0
                    ? presetModels[matched].Note
                    : string.IsNullOrWhiteSpace(config.Model)
                        ? string.Empty
                        : $"当前模型「{config.Model}」不在预设列表里，按你手填的值保留（预设只是起点）。";
                modelNote.IsVisible = !string.IsNullOrWhiteSpace(modelNote.Text);
                RefreshCapabilityNote(preset, matched >= 0 ? presetModels[matched] : null);

                // 上一次拉到的清单属于上一份配置（地址可能完全不同），切换后必须清掉。
                fetchedModels.Clear();
                modelList.Items.Clear();
                modelList.SelectedIndex = -1;
                modelList.IsVisible = false;
                fetchNote.IsVisible = false;

                RefreshKeyNote();
            }
            finally
            {
                loading = false;
            }
        }

        /// <summary>把表单写回**顶层字段**（= 当前那一份的投影）。落回列表与写盘由外壳统一做。</summary>
        void CommitForm()
        {
            var preset = CurrentPreset();
            var typedModel = model.Box.Text?.Trim() ?? string.Empty;
            var matched = presetModels.FirstOrDefault(candidate => string.Equals(candidate.Id, typedModel, StringComparison.Ordinal));
            var values = ProviderPresetValues.Resolve(preset, matched);
            var manual = NeedsManualCapabilities(preset);

            // 名字现在代表"这一份配置"（选择器上显示的就是它），所以总是按用户填的走，
            // 不再像原先那样只在自定义下保留——多了几份配置之后，"这份叫什么"必须由用户说了算。
            config.DisplayName = name.Box.Text?.Trim() ?? string.Empty;
            config.Endpoint = endpoint.Box.Text?.Trim() ?? string.Empty;
            config.UseFullUrl = useFullUrl.IsChecked == true;
            config.ApiFormat = manual ? SelectedFormat() : values.Format;
            config.Model = typedModel;
            config.ApiKey = apiKey.Text ?? string.Empty;
            // 「本地模拟」不是一家真的服务商：它不定义任何能力值，所以这两项保持原样——
            // 否则用户拿本地模拟比一下再切回自己的接口，会发现上下文窗口被悄悄清零了。
            if (!preset.IsLocal)
            {
                config.ContextWindow = manual ? Parse(contextWindow.Box.Text) : values.ContextWindow;
                config.MaxOutputTokens = manual ? Parse(maxOutput.Box.Text) : values.MaxOutputTokens;
                config.SendSamplingParameters = manual ? sendSampling.IsChecked == true : values.SendsSamplingParameters;
                config.SupportsImageInput = manual ? supportImage.IsChecked == true : values.SupportsVision;
            }
            config.AgentWorkspace = workspace.Box.Text?.Trim() ?? string.Empty;
            // 选「本地模拟」要真的走本地兜底，否则用户会以为填了地址就在用大模型。
            config.UseLocalProvider = preset.IsLocal;
            // 显式走过一次设置，就不再弹「还没选择服务商」的引导。
            config.ProviderChoiceMade = true;
        }

        // ---------- 已保存的配置列表 ----------

        var listHost = new StackPanel { Spacing = 6 };

        void SelectProfile(AiProviderProfile profile)
        {
            if (profile.Id == config.SelectedProfileId)
            {
                Say($"「{profile.Label}」已经是当前使用的那一份。");
                return;
            }

            // 先把表单落回"它现在那一份"再切：不这么做，用户刚敲进表单的内容会被写进新选中的那一份。
            requestSave();

            var activated = false;
            if (!profile.Enabled)
            {
                // 不启用的话 Agent 的选择器里根本看不到它，切过去也是白切——顺手启用，并把这件事说清楚。
                profile.Enabled = true;
                activated = true;
            }

            AiProviderSettings.ApplyProfile(config, profile);
            keyUnreadable = profile.ApiKeyUnreadable;
            LoadForm();
            RefreshList();
            requestSave();
            Say(activated
                ? $"已把「{profile.Label}」设为当前并启用（不启用的话 Agent 选不到它）。"
                : $"已切换到「{profile.Label}」。");
        }

        void ToggleEnabled(AiProviderProfile profile, bool enabled)
        {
            profile.Enabled = enabled;
            if (!requestSave())
                Say("改动没能写盘，磁盘上仍是旧的那份配置。", AgentNoteLevel.Warning);

            // 停用当前那一份时，ResolveSelected 会把选中项改到另一份启用中的；
            // 界面上的「当前」与表单必须跟着变，否则显示的和真正发请求用的不是同一个。
            if (AiProviderSettings.ResolveSelected(config) is { } effective)
            {
                AiProviderSettings.ApplyProfile(config, effective);
                keyUnreadable = effective.ApiKeyUnreadable;
                LoadForm();
            }
            else
            {
                Say("所有配置都停用了：Agent 会退回「本地模拟」（不调用大模型）。想继续用就把其中一份重新勾上。",
                    AgentNoteLevel.Warning);
            }

            RefreshList();
            Say(enabled
                ? $"已启用「{profile.Label}」，它会出现在 Agent 的模型选择器里。"
                : $"已停用「{profile.Label}」：配置与密钥都留着，只是不再进 Agent 的候选。");
        }

        void DuplicateProfile(AiProviderProfile source)
        {
            var clone = AiProviderSettings.Duplicate(source);
            clone.DisplayName = $"{source.Label} 副本";
            // 复制出来就是要用的，直接启用：不然用户还得再找到它、再点一下勾选框。
            clone.Enabled = true;
            config.Profiles.Add(clone);

            requestSave();          // 先把当前表单落回它自己的那一份
            AiProviderSettings.ApplyProfile(config, clone);
            keyUnreadable = clone.ApiKeyUnreadable;
            LoadForm();
            RefreshList();
            Say($"已复制为「{clone.Label}」并切过去编辑（已启用）。改完点「保存」写盘。");
        }

        async void DeleteProfileAsync(AiProviderProfile profile)
        {
            if (!await ConfirmAsync(owner, "删除这份配置",
                    $"要删除「{profile.Label}」吗？它的接口地址、模型与密钥会一并从配置文件里删掉，密钥没法从界面上找回，只能重新贴一次。\n" +
                    "如果只是暂时不想用它，点它的「启用」勾选框停用即可——配置会留着。",
                    "删除"))
                return;

            var wasCurrent = profile.Id == config.SelectedProfileId;
            config.Profiles.Remove(profile);
            if (wasCurrent) config.SelectedProfileId = string.Empty;

            var effective = AiProviderSettings.ResolveSelected(config);
            if (effective is not null)
            {
                AiProviderSettings.ApplyProfile(config, effective);
                keyUnreadable = effective.ApiKeyUnreadable;
                LoadForm();
            }
            requestSave();
            RefreshList();
            Say(wasCurrent
                ? $"已删除「{profile.Label}」，当前切换到了「{effective?.Label ?? "（没有可用的配置）"}」。"
                : $"已删除「{profile.Label}」。");
        }

        void RefreshList()
        {
            listHost.Children.Clear();
            var profiles = config.Profiles;
            var currentId = config.SelectedProfileId;

            foreach (var item in profiles)
            {
                var profile = item;
                var isCurrent = profile.Id == currentId;

                var enabled = new CheckBox
                {
                    IsChecked = profile.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                enabled.IsCheckedChanged += (_, _) => ToggleEnabled(profile, enabled.IsChecked == true);

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                nameRow.Children.Add(new TextBlock
                {
                    Text = profile.Label,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush("DfInk"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (isCurrent) nameRow.Children.Add(Chip("当前", "DfPrimary"));
                if (!profile.Enabled) nameRow.Children.Add(Chip("已停用", "DfInk3"));

                var info = new StackPanel
                {
                    Spacing = 3,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        nameRow,
                        new TextBlock
                        {
                            Text = Describe(profile),
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush("DfInk3")
                        }
                    }
                };

                var copy = Secondary("复制");
                copy.FontSize = 10;
                copy.VerticalAlignment = VerticalAlignment.Center;
                copy.Click += (_, _) => DuplicateProfile(profile);

                var remove = Secondary("删除");
                remove.FontSize = 10;
                remove.VerticalAlignment = VerticalAlignment.Center;
                // 至少留一份：一份都没有的话，表单就没有"落点"，保存时也没有哪一份可写。
                // 不需要它时该做的是「停用」，而不是删光。
                remove.IsEnabled = profiles.Count > 1;
                if (!remove.IsEnabled) ToolTip.SetTip(remove, "至少要保留一份配置；不需要它时请停用（配置与密钥都会留着）");
                remove.Click += (_, _) => DeleteProfileAsync(profile);

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { copy, remove }
                };

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                grid.Children.Add(enabled);
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);
                Grid.SetColumn(actions, 2);
                grid.Children.Add(actions);

                var row = new Border
                {
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush(isCurrent ? "DfLineGlow" : "DfLine"),
                    Background = Brush(isCurrent ? "DfPrimarySoft" : "DfSurface2"),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = grid
                };
                row.PointerReleased += (_, e) =>
                {
                    // 点在勾选框或「复制 / 删除」上时不切换：那些控件有自己的意思，
                    // 顺手切一下当前配置会让"我只是想删掉它"变成"顺便换了模型"。
                    if (!IsFromInteractive(e.Source, row)) SelectProfile(profile);
                };
                listHost.Children.Add(row);
            }
        }

        // ---------- 组装 ----------

        var root = new StackPanel { Spacing = 8, Margin = new Thickness(20) };

        root.Children.Add(Header("已保存的配置"));
        root.Children.Add(Note(
            "可以同时保存并启用多份（例如一家用于长文、一家便宜用于日常），Agent 面板的模型选择器里挑一份用。" +
            "「当前」那一份就是现在真正发请求用的；点一行即可切换，并会让它进入编辑区。"));
        root.Children.Add(listHost);

        root.Children.Add(Header("服务商预设"));
        root.Children.Add(Note("选一家：地址、接口格式、采样开关与推荐模型会自动填好，你只需补密钥。选「自定义」可自己配接口格式。"));
        root.Children.Add(presetBox);
        root.Children.Add(presetHint);

        root.Children.Add(Header("模型与接口"));
        root.Children.Add(name.Label);
        root.Children.Add(name.Box);
        root.Children.Add(endpoint.Label);
        root.Children.Add(endpoint.Box);
        root.Children.Add(useFullUrl);
        root.Children.Add(format);
        modelPresetBox.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(modelPresetBox);
        root.Children.Add(modelNote);
        root.Children.Add(model.Label);
        root.Children.Add(model.Box);
        root.Children.Add(fetchModels);
        root.Children.Add(modelList);
        root.Children.Add(fetchNote);
        // 能力这行对两种厂家都出现：预设是说「替你开了什么」，自定义是说「这组值归你填」。
        // 自动生效的东西如果界面上看不见，用户撞上问题只能凭猜。
        root.Children.Add(capability);
        root.Children.Add(customBlock);

        root.Children.Add(Header("密钥"));
        root.Children.Add(apiKey);
        root.Children.Add(keyNote);

        root.Children.Add(Header("工作目录"));
        root.Children.Add(workspace.Label);
        root.Children.Add(workspace.Box);
        root.Children.Add(pickWorkspace);

        root.Children.Add(Header("配置文件位置"));
        root.Children.Add(Note(AiProviderSettings.ConfigFilePath));

        LoadForm();
        RefreshList();

        return new SettingsPageSection
        {
            Title = "模型接入",
            Glyph = "◈",
            Summary = "聊天模型 / 密钥 / 工作目录",
            Root = root,
            Commit = CommitForm,
            Reload = () =>
            {
                // 保存之后重来一遍：停用当前那一份时可能会自动切到另一份，界面要跟上。
                if (AiProviderSettings.ResolveSelected(config) is { } effective)
                {
                    AiProviderSettings.ApplyProfile(config, effective);
                    keyUnreadable = effective.ApiKeyUnreadable;
                }
                LoadForm();
                RefreshList();
            }
        };
    }

    private static int Parse(string? text) => int.TryParse(text?.Trim(), out var value) && value > 0 ? value : 0;

    /// <summary>这条配置长什么样，一行说清；密钥状态也写进去，因为"没有密钥"是这份配置最常见的坏掉方式。</summary>
    private static string Describe(AiProviderProfile profile)
    {
        var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint) ? "（未填接口地址）" : profile.Endpoint;
        var model = string.IsNullOrWhiteSpace(profile.Model) ? "（未填模型）" : profile.Model;
        var key = profile.ApiKeyUnreadable
            ? "密钥解不开"
            : string.IsNullOrEmpty(profile.ApiKey) ? "没有密钥" : "密钥已存";
        return $"{endpoint} · {model} · {key}";
    }

    private static Border Chip(string text, string foreground) => new()
    {
        Padding = new Thickness(6, 0),
        CornerRadius = new CornerRadius(6),
        Background = Brush("DfSurface3"),
        BorderBrush = Brush("DfLine"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, Foreground = Brush(foreground) }
    };

    /// <summary>
    /// 这次点击是不是落在"有自己意思的控件"上（勾选框 / 按钮）。
    /// 用来把「点整行切换配置」与「点勾选框启停、点按钮复制删除」区分开——
    /// 两者共用一个 Border，不区分的话点删除会顺手把当前配置也换掉。
    /// </summary>
    private static bool IsFromInteractive(object? source, Visual row)
    {
        for (Visual? visual = source as Visual; visual is not null && !ReferenceEquals(visual, row); visual = visual.GetVisualParent())
        {
            if (visual is Button || visual is ToggleButton || visual is TextBox) return true;
        }
        return false;
    }
}
