using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>自检窗口的产出：要定位哪个节点（可空）+ 用户点了「开始」时的那份跑法（可空）。</summary>
internal sealed record GenerationAuditOutcome(Guid? LocateNodeId, OneClickRunRequest? Run);

/// <summary>
/// 生成链自检 + **一键开跑**的窗口：先看清楚缺什么、缺多少、要花多少钱，再决定动不动手。
///
/// 它以前只出报告（「按勾选生成」是灰的），现在把后半截接上了，但**规矩没变**：
/// 清单先摆出来、钱先算清楚，用户点头才动手。所以「一键」指的是「不用一个一个节点去右键」，
/// 不是「不打招呼就花钱」。
///
/// 一键有两档（用户在这里选）：
/// · **一键出分镜图**：把这一章缺的设定图与分镜图补齐；
/// · **一键出章节视频**：先按需补齐图，再逐镜出视频——每一镜的视频都要一个首帧，
///   缺图直接出视频等于让模型凭空编，所以默认勾着「缺的图一并补齐」。
/// </summary>
internal static class GenerationAuditDialog
{
    /// <summary>
    /// 展示报告并收集这次要怎么跑。
    /// 返回用户点「定位」时要跳过去的节点 ID，以及点「开始」时那份跑法。
    ///
    /// <paramref name="resolveVideoRoute"/> 是给「自动抉择 / 记住一条」用的：出视频的来源有三种口径，
    /// 而自动抉择要读这一章的情况（有没有首帧、想要几秒、这一章是讲什么的），那些东西在这个窗口里没有——
    /// 所以由宿主（见 <c>MainWindow.ResolveVideoRouteAsync</c>）去算，算完把结论与那句人话交回来。
    /// 传 null 时这一档就退化成「只能手动选」（例如将来的网页端）。
    /// </summary>
    public static async Task<GenerationAuditOutcome> ShowAsync(
        Window owner,
        GenerationAuditReport report,
        bool videoAvailable,
        ImageSourceChoice? initialImageSource,
        ImageSourceChoice? initialVideoSource,
        int initialSeconds,
        Func<VideoRouteMode, Task<(ImageSourceChoice? Source, string Note)>>? resolveVideoRoute = null)
    {
        Guid? locate = null;
        OneClickRunRequest? run = null;

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(Header($"{report.RootKind}「{report.RootTitle}」"));
        // 过期也算「有事要办」，所以它不该显示成一句绿色的「一切都好」。
        body.Children.Add(Note(report.Describe(),
            report.IsClean && report.StaleCount == 0 ? AgentNoteLevel.Success : AgentNoteLevel.Warning));

        if (report.Note.Length > 0)
        {
            body.Children.Add(Note("自检范围算不出来时不出清单：一份「看起来什么都没缺」的报告，"
                + "比直接说算不出来更危险。", AgentNoteLevel.Warning));
            var onlyClose = Primary("知道了");
            var onlyRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6,
                Children = { onlyClose }
            };
            var onlyDialog = DialogShell.Create($"自检 · {report.RootTitle}", Layout(body, Footer(onlyRow)), 560, 300);
            onlyClose.Click += (_, _) => onlyDialog.Close();
            await onlyDialog.ShowDialog(owner);
            return new GenerationAuditOutcome(null, null);
        }

        body.Children.Add(Note("依赖链：设定图 → 分镜图 → 分镜视频 → 成品视频。上一层没有产物时，"
            + "下一层也能出来，但那是模型现编的——所以缺哪一层都要先在这里看到。", AgentNoteLevel.Info));

        var current = report;
        var checkedItems = new HashSet<GenerationAuditItem>();

        // ---------- 这次要做什么 ----------
        var intentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        intentRow.Children.Add(new TextBlock
        {
            Text = "这次要做什么：",
            FontSize = 11,
            Foreground = Brush("DfInk2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var imagesIntent = new RadioButton { Content = "一键出分镜图", GroupName = "auditIntent", IsChecked = report.Intent == GenerationIntent.Images };
        var videoIntent = new RadioButton { Content = "一键出章节视频", GroupName = "auditIntent", IsChecked = report.Intent == GenerationIntent.Video };
        intentRow.Children.Add(imagesIntent);
        intentRow.Children.Add(videoIntent);
        body.Children.Add(intentRow);

        var layersHost = new StackPanel { Spacing = 10 };
        body.Children.Add(layersHost);

        // 过期那一段**不跟着意图变**（它就是一批旧产物，与「这次要补什么」无关），
        // 所以只建一次，不进 RefreshLayers。
        if (report.StaleCount > 0 || report.UnrecordedBaselineNodes > 0)
            body.Children.Add(BuildStaleCard(report, nodeId => locate = nodeId));

        // ---------- 补齐 / 池子 / 时长 ----------
        var fillImages = new CheckBox
        {
            Content = "缺的图一并补齐（先把设定图与分镜图补上，再出视频）",
            IsChecked = true,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };
        body.Children.Add(fillImages);

        var imagePool = initialImageSource;
        var videoPool = initialVideoSource;
        var seconds = Math.Max(0, initialSeconds);

        var poolNote = Note(string.Empty);
        var pickImagePool = Secondary("选生图池子…");
        // 出视频这一路现在**也列 ComfyUI 的工作流**，所以按钮不再写「池子」——
        // 写「池子」会让想用工作流的人以为这里没得选。
        var pickVideoPool = Secondary("选视频来源…");
        var imagePoolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "生图池子", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center }, pickImagePool, poolNote }
        };
        var videoPoolNote = Note(string.Empty);
        // 自动抉择 / 记住一条那两条路的结果写在这儿：SyncCost 每次都会重写 videoPoolNote，
        // 所以那句人话要单独存着再拼进去，不能被清掉。
        var routeNote = string.Empty;
        var videoPoolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "视频来源", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center }, pickVideoPool, videoPoolNote }
        };
        body.Children.Add(imagePoolRow);
        body.Children.Add(videoPoolRow);

        // 时长：**从池子的档位里选，也可以自己填**。池子的档位来自清单（SitePool.Seconds），
        // 自己填的那个会被 VideoDurationPolicy 拿去和清单对一遍，对不上就提醒但不拦。
        var durationBox = new TextBox { Text = seconds > 0 ? seconds.ToString() : string.Empty, Width = 72, FontSize = 12 };
        var tierBox = new ComboBox { Width = 132, FontSize = 12, Classes = { "panelCombo" } };
        var durationNote = Note(string.Empty);
        var durationRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "每镜时长（秒）", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center }, tierBox, durationBox, durationNote }
        };
        body.Children.Add(durationRow);

        // 比例 / 目标像素：与时长并列的三个出视频选项。**只有走 ComfyUI 工作流时它们才可能生效**——
        // 接口站那条路只发时长，这两项不适用，所以那一路会把它们置灰并说明为什么（不假装生效）。
        var aspectBox = new ComboBox { Width = 108, FontSize = 12, Classes = { "panelCombo" } };
        foreach (var item in new[] { "跟随首帧", "9:16", "16:9", "1:1" }) aspectBox.Items.Add(item);
        aspectBox.SelectedIndex = 0;
        var megapixelsBox = new ComboBox { Width = 120, FontSize = 12, Classes = { "panelCombo" } };
        foreach (var item in new[] { "0.5 MP", "1.0 MP", "2.0 MP" }) megapixelsBox.Items.Add(item);
        megapixelsBox.SelectedIndex = 1;
        var shapeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "比例", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center },
                aspectBox,
                new TextBlock { Text = "目标像素", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center },
                megapixelsBox
            }
        };
        var shapeNote = Note(string.Empty);
        shapeNote.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(shapeRow);
        body.Children.Add(shapeNote);
        // 选项值直接取自两个下拉当前选中的那一项（空串 = 跟随首帧；目标像素默认 1.0MP）。
        string CurrentAspect() => aspectBox.SelectedIndex switch
        {
            1 => "9:16",
            2 => "16:9",
            3 => "1:1",
            _ => string.Empty
        };
        double CurrentMegapixels() => megapixelsBox.SelectedIndex switch { 0 => 0.5, 2 => 2.0, _ => 1.0 };

        var costNote = Note(string.Empty);
        costNote.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(costNote);

        var copy = Secondary("复制清单");
        var start = Primary("开始");
        var close = Secondary("关闭");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { copy, start, close }
        };

        // ---------- 同步 ----------
        OneClickRunRequest CurrentRun()
        {
            var intent = videoIntent.IsChecked == true ? GenerationIntent.Video : GenerationIntent.Images;
            var images = checkedItems.Where(item => item.Stage is GenerationStage.SettingImage or GenerationStage.StoryboardImage).ToList();
            var videos = checkedItems.Where(item => item.Stage is GenerationStage.StoryboardVideo or GenerationStage.ProductVideo).ToList();
            var fill = intent == GenerationIntent.Images || fillImages.IsChecked == true;
            // 比例 / 目标像素只有走 ComfyUI 工作流时才会被真正写进去（见 RunVideoAsync 与 ComfyUiVideoProvider）；
            // 走池子时这两个值传下去也不会发出去，界面那一边已经把话说明白了。
            return new OneClickRunRequest(
                intent, fill, images, videos, imagePool, videoPool, seconds, CurrentAspect(), CurrentMegapixels());
        }

        void SyncTiers()
        {
            // 档位下拉只列视频池子清单里写明的那些秒数（没写就是空表，用户自己填）。
            // 走 ComfyUI 工作流时没有「池子登记的档位」这回事（时长由那份工作流的帧数入口按秒换算），
            // 也不该装作有。
            var tiers = (videoPool?.PoolItem?.Seconds ?? 0) > 0
                ? new List<string> { $"{videoPool!.PoolItem!.Seconds}s（清单登记）" }
                : new List<string>();
            var anyPool = SiteCatalog.Load().Sites.SelectMany(site => site.VideoPools)
                .Where(pool => pool.Seconds > 0).Select(pool => $"{pool.Seconds}s").Distinct().OrderBy(text => text.Length).ToList();
            foreach (var tier in anyPool)
                if (!tiers.Contains(tier) && !tiers.Any(item => item.StartsWith(tier))) tiers.Add(tier);
            tierBox.ItemsSource = tiers;
            tierBox.IsVisible = tiers.Count > 0;
        }

        /// <summary>一行说明：走池子时报池子的细节，走 ComfyUI 工作流时报那份工作流的名字。</summary>
        string SourceNote(ImageSourceChoice? source, string none)
        {
            if (source is null) return none;
            return source.IsWorkflow
                ? $"{source.Label}（ComfyUI 工作流：底模与步数由它自己决定，烧本机显卡）"
                : $"{source.Label}（{source.PoolItem?.Describe()}）";
        }

        void SyncCost()
        {
            poolNote.Text = SourceNote(imagePool,
                "没选：补图会用设置里的默认图像模型（价格看设置，这里算不出来）。");
            videoPoolNote.Text = SourceNote(videoPool,
                videoAvailable ? "没选：会用设置里的视频接口。" : "还没配视频链路：出视频这一档现在跑不了。")
                + (routeNote.Length > 0 ? "　" + routeNote : string.Empty);

            var check = VideoDurationPolicy.Check(videoPool, seconds);
            durationNote.Text = check.Note;
            durationNote.Foreground = Brush(check.Mismatch ? "DfWarning" : "DfInk3");

            // 比例 / 目标像素 / 时长改不改得动：走工作流的答案只在它自己的正文里（Detect 出来的槽位），
            // 走池子则这两项不适用。两条都**照实说**，界面不另判一套。
            var chosenWorkflow = videoPool?.Workflow;
            aspectBox.IsEnabled = chosenWorkflow is not null;
            megapixelsBox.IsEnabled = chosenWorkflow is not null;
            if (chosenWorkflow is not null)
            {
                var (slots, error) = ComfyUiWorkflowInspector.Inspect(chosenWorkflow.Site, chosenWorkflow.Workflow);
                if (slots is null)
                {
                    shapeNote.Text = $"这份工作流的正文读不到，改不改得动这三样判断不了：{error}";
                    shapeNote.Foreground = Brush("DfWarning");
                    durationNote.Text = "时长：读不到工作流正文，判断不了。";
                    durationNote.Foreground = Brush("DfWarning");
                }
                else
                {
                    shapeNote.Text = VideoShapeNotice.DescribeWorkflow(slots, CurrentAspect(), CurrentMegapixels(), seconds);
                    shapeNote.Foreground = Brush("DfInk3");
                    // 时长可改的工作流上，VideoDurationPolicy 那句「不会发出去」是错的：
                    // 秒数现在真的会按它的帧率换算成帧写进去，所以这一行留给上面的说明去讲。
                    durationNote.Text = string.Empty;
                }
            }
            else
            {
                shapeNote.Text = VideoShapeNotice.DescribePool(seconds);
                shapeNote.Foreground = Brush("DfInk3");
            }

            var lines = OneClickCost.Describe(CurrentRun());
            costNote.Text = string.Join("\n", lines);
        }

        void RefreshLayers()
        {
            layersHost.Children.Clear();
            checkedItems.Clear();
            foreach (var item in current.DefaultChecked) checkedItems.Add(item);
            foreach (var layer in current.Layers)
                layersHost.Children.Add(BuildLayer(layer, checkedItems, nodeId => locate = nodeId, SyncCost));
            SyncTiers();
            SyncCost();
        }

        imagesIntent.IsCheckedChanged += (_, _) =>
        {
            if (imagesIntent.IsChecked != true) return;
            current = current.WithIntent(GenerationIntent.Images);
            RefreshLayers();
        };
        videoIntent.IsCheckedChanged += (_, _) =>
        {
            if (videoIntent.IsChecked != true) return;
            current = current.WithIntent(GenerationIntent.Video);
            RefreshLayers();
        };
        fillImages.IsCheckedChanged += (_, _) => SyncCost();
        // 换了比例 / 目标像素，事前那句说明要跟着重算（它算的是「这样选会写成什么」）。
        aspectBox.SelectionChanged += (_, _) => SyncCost();
        megapixelsBox.SelectionChanged += (_, _) => SyncCost();
        durationBox.TextChanged += (_, _) =>
        {
            seconds = int.TryParse(durationBox.Text, out var parsed) && parsed > 0 ? parsed : 0;
            SyncCost();
        };
        tierBox.SelectionChanged += (_, _) =>
        {
            if (tierBox.SelectedItem is not string text) return;
            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0) durationBox.Text = digits;   // 赋值会触发 TextChanged，由它去同步
        };
        pickImagePool.Click += async (_, _) =>
        {
            var picked = await SitePoolPicker.ShowAsync(owner, SiteCatalog.Load().Sites, report.RootTitle, video: false);
            if (picked is null) return;
            imagePool = picked;
            SyncCost();
        };
        pickVideoPool.Click += async (_, _) =>
        {
            var sites = SiteCatalog.Load().Sites;
            var pick = await SitePoolPicker.ShowVideoAsync(
                owner,
                sites,
                report.RootTitle,
                preset: videoPool?.Pool,
                presetWorkflow: videoPool?.Workflow,
                presetMode: VideoRoutePreferenceStore.Load().ModeKind);
            if (pick is null) return;   // 取消：什么都不改

            // 先把「以后这事儿怎么定」存下来。它与「这一次选了哪条」是两件事：
            // 选了「记住」就该记住，选了「自动」就该每次自动，选了「每次都问」也该能退回去。
            VideoRoutePreferenceStore.Save(new VideoRoutePreference
            {
                Mode = VideoRoutePreference.ModeValue(pick.Mode),
                Key = pick.Choice is null ? string.Empty : VideoRoute.FromChoice(pick.Choice)?.Key ?? string.Empty,
                Label = pick.Choice?.Label ?? string.Empty
            });

            if (pick.Mode == VideoRouteMode.Auto)
            {
                // 「交给 AI 自动抉择」：现在就挑一次（由宿主算，它知道这一章的情况），
                // 并把「是谁挑的、为什么」写在窗口上——不说的话，用户只知道「系统选的」。
                // **只挑一次**：这个回调里可能有一次模型调用，调两次就是花两次钱。
                if (resolveVideoRoute is null)
                {
                    routeNote = "自动抉择这一档需要宿主支持，这里只能手动选。";
                }
                else
                {
                    var resolved = await resolveVideoRoute(VideoRouteMode.Auto);
                    videoPool = resolved.Source;
                    routeNote = resolved.Note;
                }
                SyncTiers();
                SyncCost();
                return;
            }

            if (pick.Choice is null) return;
            routeNote = pick.Mode == VideoRouteMode.Remember
                ? "以后不再问，直接用这一条（要改回「每次都问」就再点一次这个按钮）。"
                : string.Empty;
            videoPool = pick.Choice;
            // 换了来源就把时长对齐到它登记的档位：用户多半是「照这家的档位来」，
            // 而不是「我非要 15 秒」——真非要的话他自己改回去，那时会看到一句提醒。
            // 走 ComfyUI 工作流时没有可对齐的档位（时长由工作流自己的帧数决定），所以不动它。
            if (videoPool.PoolItem?.Seconds is > 0 and var tierSeconds)
                durationBox.Text = tierSeconds.ToString();
            SyncTiers();
            SyncCost();
        };
        RefreshLayers();

        var dialog = DialogShell.Create($"自检 · {report.RootKind}「{report.RootTitle}」", Layout(body, Footer(buttons)), 700, 620);
        close.Click += (_, _) => dialog.Close();
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(current.ToText(imagePool?.UnitPrice) + "\n\n" + string.Join("\n", OneClickCost.Describe(CurrentRun())));
        };
        start.Click += (_, _) =>
        {
            var request = CurrentRun();
            if (!request.WantsImages && !request.WantsVideos)
            {
                costNote.Text = "这一档没有要生成的东西：把上面清单里要补的勾上，或换一档。";
                return;
            }
            run = request;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return new GenerationAuditOutcome(locate, run);
    }

    /// <summary>
    /// 一层一张卡：标题行给「缺 N / 共 M」，下面列出每一个缺口。
    /// 目标数是必须的——缺 1 / 共 2 和缺 1 / 共 40 是完全不同的两件事。
    /// </summary>
    private static Border BuildLayer(
        GenerationAuditLayer layer,
        HashSet<GenerationAuditItem> checkedItems,
        Action<Guid> locate,
        Action onChanged)
    {
        var content = new StackPanel { Spacing = 6 };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(new TextBlock
        {
            Text = $"{layer.Title}　{layer.What}",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("DfInk")
        });
        var counter = new TextBlock
        {
            Text = layer.IsClean ? $"齐了（{layer.TargetCount}）" : $"缺 {layer.MissingCount} / 共 {layer.TargetCount}",
            FontSize = 11,
            Foreground = layer.IsClean ? Brush("DfSuccess") : Brush("DfWarning")
        };
        Grid.SetColumn(counter, 1);
        titleRow.Children.Add(counter);
        content.Children.Add(titleRow);

        if (!layer.Executable)
            content.Children.Add(Note(layer.ExecutableNote, AgentNoteLevel.Warning));

        if (layer.IsClean)
        {
            content.Children.Add(Note(layer.TargetCount == 0
                ? "这个范围内没有这一类节点。"
                : "这一层是齐的，不用补。", AgentNoteLevel.Success));
        }
        else
        {
            foreach (var item in layer.Missing)
                content.Children.Add(BuildItem(item, checkedItems.Contains(item), locate, checkedItems, onChanged));
        }

        return Card(content);
    }

    /// <summary>
    /// 「产物过期」那张卡：**没有勾选框**。
    ///
    /// 为什么不给勾：重出是另一件事、另一笔钱，与「把缺的补齐」不是同一个问题。把它混进一键清单，
    /// 用户会以为自己点的是「补上没出的那几张」，结果连旧的也一起重出了一遍。所以这里只把
    /// 「哪几个节点、照着的哪条设定变了」摆清楚、给一个「定位」，要不要重出由他自己在那个节点上定。
    /// </summary>
    private static Border BuildStaleCard(GenerationAuditReport report, Action<Guid> locate)
    {
        var content = new StackPanel { Spacing = 6 };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(new TextBlock
        {
            Text = "产物过期　照着的设定后来变了",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("DfInk")
        });
        var counter = new TextBlock
        {
            Text = $"{report.StaleCount} 处建议重出",
            FontSize = 11,
            Foreground = Brush("DfWarning")
        };
        Grid.SetColumn(counter, 1);
        titleRow.Children.Add(counter);
        content.Children.Add(titleRow);

        content.Children.Add(Note("过期**不是缺**：这些产物都在，只是照着的那一版设定已经变了（换了图或改了描述）。"
            + "所以它们不进上面的清单、也不算进「要补几张」——要重出哪几件由你定。", AgentNoteLevel.Warning));

        foreach (var item in report.Stale)
            content.Children.Add(BuildStaleItem(item, locate));

        if (report.UnrecordedBaselineNodes > 0)
            content.Children.Add(Note($"另有 {report.UnrecordedBaselineNodes} 个节点的产物没记过依据"
                + "（这是本次更新之前出的，判不出它当时照的是哪一版设定），过期与否判断不了；"
                + "重出一次就会带上依据。"));

        return Card(content);
    }

    /// <summary>卡片外观只有一份：四层各一张、过期一张，长得不一样会让人以为是两类东西。</summary>
    private static Border Card(Control content) => new()
    {
        Background = Brush("DfSurface2"),
        BorderBrush = Brush("DfLine"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10),
        Child = content
    };

    /// <summary>一个缺口：勾选框（预勾 = 这次会生成它）+ 说明 + 定位按钮。取消勾选就不会花这份钱。</summary>
    private static Control BuildItem(
        GenerationAuditItem item,
        bool preset,
        Action<Guid> locate,
        HashSet<GenerationAuditItem> checkedItems,
        Action onChanged)
    {
        var check = new CheckBox
        {
            IsChecked = preset && item.Actionable,
            // 不能直接生成的（例如引用已失效、设定只在引用画廊里）不给勾：
            // 勾了却生成不出来，用户会以为是自己点的姿势不对。
            IsEnabled = item.Actionable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        if (!item.Actionable) ToolTip.SetTip(check, "这一项不能在画布节点上直接生成，先按说明处理。");
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true) checkedItems.Add(item);
            else checkedItems.Remove(item);
            onChanged();
        };
        return BuildRow(item, check, locate);
    }

    /// <summary>过期那一项：同一副面孔，但**没有勾选框**（理由见 <see cref="BuildStaleCard"/>）。</summary>
    private static Control BuildStaleItem(GenerationAuditItem item, Action<Guid> locate) =>
        BuildRow(item, check: null, locate);

    /// <summary>一项的正文：可选的勾选框 + 标题与理由 + 定位按钮。缺口与过期共用，免得两处措辞走样。</summary>
    private static Control BuildRow(GenerationAuditItem item, CheckBox? check, Action<Guid> locate)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        if (check is not null)
        {
            row.Children.Add(check);
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = 12,
                Foreground = Brush("DfWarning"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0)
            });
        }

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = item.Display,
            FontSize = 11,
            Foreground = Brush("DfInk"),
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(Note(item.Reason));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (item.ActionNodeId != Guid.Empty)
        {
            var go = Secondary("定位");
            go.VerticalAlignment = VerticalAlignment.Center;
            go.Click += (_, _) => locate(item.ActionNodeId);
            Grid.SetColumn(go, 2);
            row.Children.Add(go);
        }
        else
        {
            var hint = Note("引用画廊", AgentNoteLevel.Warning);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint, 2);
            row.Children.Add(hint);
        }

        return row;
    }
}
