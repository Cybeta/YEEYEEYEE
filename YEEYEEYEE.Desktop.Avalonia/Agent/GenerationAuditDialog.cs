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
    /// </summary>
    public static async Task<GenerationAuditOutcome> ShowAsync(
        Window owner,
        GenerationAuditReport report,
        bool videoAvailable,
        ImageSourceChoice? initialImageSource,
        ImageSourceChoice? initialVideoSource,
        int initialSeconds)
    {
        Guid? locate = null;
        OneClickRunRequest? run = null;

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(Header($"{report.RootKind}「{report.RootTitle}」"));
        body.Children.Add(Note(report.Describe(), report.IsClean ? AgentNoteLevel.Success : AgentNoteLevel.Warning));

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
        var pickVideoPool = Secondary("选视频池子…");
        var imagePoolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "生图池子", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center }, pickImagePool, poolNote }
        };
        var videoPoolNote = Note(string.Empty);
        var videoPoolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "视频池子", FontSize = 11, Foreground = Brush("DfInk2"), VerticalAlignment = VerticalAlignment.Center }, pickVideoPool, videoPoolNote }
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
            return new OneClickRunRequest(intent, fill, images, videos, imagePool, videoPool, seconds);
        }

        void SyncTiers()
        {
            // 档位下拉只列视频池子清单里写明的那些秒数（没写就是空表，用户自己填）。
            // 走 ComfyUI 工作流时没有「档位」这回事（时长由工作流自己的帧数决定），也不该装作有。
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
                videoAvailable ? "没选：会用设置里的视频接口。" : "还没配视频链路：出视频这一档现在跑不了。");

            var check = VideoDurationPolicy.Check(videoPool, seconds);
            durationNote.Text = check.Note;
            durationNote.Foreground = Brush(check.Mismatch ? "DfWarning" : "DfInk3");

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
            var picked = await SitePoolPicker.ShowAsync(owner, SiteCatalog.Load().Sites, report.RootTitle, video: true);
            if (picked is null) return;
            videoPool = picked;
            // 换了池子就把时长对齐到它登记的档位：用户多半是「照这家的档位来」，
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

        return new Border
        {
            Background = Brush("DfSurface2"),
            BorderBrush = Brush("DfLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = content
        };
    }

    /// <summary>一个缺口：勾选框（预勾 = 这次会生成它）+ 说明 + 定位按钮。取消勾选就不会花这份钱。</summary>
    private static Control BuildItem(
        GenerationAuditItem item,
        bool preset,
        Action<Guid> locate,
        HashSet<GenerationAuditItem> checkedItems,
        Action onChanged)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

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
        row.Children.Add(check);

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
