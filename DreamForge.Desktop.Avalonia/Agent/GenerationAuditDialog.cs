using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DreamForge.Desktop;
using static DreamForge.Desktop.Avalonia.AgentDialogUi;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// 生成链自检的报告窗口：**先看清楚缺什么、缺多少、要花多少钱，再决定动不动手**。
///
/// 为什么先只出报告、不顺手生成：「按节点把缺的全补上」是一件花钱、要等、还会覆盖现有产物的事。
/// 一个「看看有什么」的动作不该顺手起几十次调用——这一条在出图虚影那一轮已经吃过一次亏，
/// 所以这里把清单摆出来、把花费算清楚，自动生成留到下一步接。
///
/// 勾选框**现在就画出来**（并按「会挡路的那些」预勾）：让用户在花这笔钱之前就能核对
/// 「下一步准备替我生成哪几件」，而不是等生成了才发现勾错了一整章。
/// </summary>
internal static class GenerationAuditDialog
{
    /// <summary>
    /// 展示报告。返回用户点「定位」时要跳过去的节点 ID（没有就返回 null）。
    /// 定位只做一件事：把那个节点选中并挪进视野——用户自己决定怎么出图。
    /// </summary>
    public static async Task<Guid?> ShowAsync(Window owner, GenerationAuditReport report, double? unitPrice)
    {
        Guid? locate = null;
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

        body.Children.Add(Header($"{report.RootKind}「{report.RootTitle}」"));
        body.Children.Add(Note(report.Describe(), report.IsClean ? AgentNoteLevel.Success : AgentNoteLevel.Warning));

        var cost = Note(report.EstimateCost(unitPrice), AgentNoteLevel.Info);
        cost.FontSize = 11;
        body.Children.Add(cost);

        if (report.Note.Length > 0) return await ShowNoteOnlyAsync(owner, report, body);

        body.Children.Add(Note("依赖链：设定图 → 分镜图 → 分镜视频 → 成品视频。上一层没有产物时，"
            + "下一层也能出来，但那是模型现编的——所以缺哪一层都要先在这里看到。", AgentNoteLevel.Info));

        // 意图在这里由用户定，而不是替他猜：「这次要出图」与「这次要出视频」要补的东西不一样——
        // 只补图时，分镜视频那一层缺多少并不挡事；要出视频时，从设定图到分镜视频三层缺一件都会出事。
        var intentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        intentRow.Children.Add(new TextBlock
        {
            Text = "这次要做什么：",
            FontSize = 11,
            Foreground = Brush("DfInk2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var imagesIntent = new RadioButton { Content = "出图", GroupName = "auditIntent", IsChecked = report.Intent == GenerationIntent.Images };
        var videoIntent = new RadioButton { Content = "出视频", GroupName = "auditIntent", IsChecked = report.Intent == GenerationIntent.Video };
        intentRow.Children.Add(imagesIntent);
        intentRow.Children.Add(videoIntent);
        body.Children.Add(intentRow);

        var layersHost = new StackPanel { Spacing = 10 };
        body.Children.Add(layersHost);

        var current = report;
        void RefreshLayers()
        {
            layersHost.Children.Clear();
            var checkedSet = current.DefaultChecked.ToHashSet();
            foreach (var layer in current.Layers)
                layersHost.Children.Add(BuildLayer(layer, checkedSet, nodeId => locate = nodeId));
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
        RefreshLayers();

        var close = Primary("关闭");
        var copy = Secondary("复制清单");
        var generate = Secondary("按勾选生成（下一步接入）");
        generate.IsEnabled = false;
        ToolTip.SetTip(generate,
            "这一步先只出报告：清单与花费确认无误后，再接「按勾选自动生成」。"
            + "出图那条链路（设定图 / 分镜图）随时可以在节点右键里手动出。");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { copy, generate, close }
        };

        var dialog = DialogShell.Create($"自检 · {report.RootKind}「{report.RootTitle}」", Layout(body, Footer(buttons)), 660, 560);

        close.Click += (_, _) => dialog.Close();
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(current.ToText(unitPrice));
        };

        await dialog.ShowDialog(owner);
        return locate;
    }

    /// <summary>章节锚点没建好这种「算不出范围」的情况：只说明原因，不摆一张全是空的清单。</summary>
    private static async Task<Guid?> ShowNoteOnlyAsync(Window owner, GenerationAuditReport report, StackPanel body)
    {
        body.Children.Add(Note("自检范围算不出来时不出清单：一份「看起来什么都没缺」的报告，"
            + "比直接说算不出来更危险。", AgentNoteLevel.Warning));

        var close = Primary("知道了");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { close }
        };

        var dialog = DialogShell.Create($"自检 · {report.RootTitle}", Layout(body, Footer(buttons)), 560, 300);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return null;
    }

    /// <summary>
    /// 一层一张卡：标题行给「缺 N / 共 M」，下面列出每一个缺口。
    /// 目标数是必须的——缺 1 / 共 2 和缺 1 / 共 40 是完全不同的两件事。
    /// </summary>
    private static Border BuildLayer(
        GenerationAuditLayer layer,
        HashSet<GenerationAuditItem> checkedSet,
        Action<Guid> locate)
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
                content.Children.Add(BuildItem(item, checkedSet.Contains(item), locate));
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

    /// <summary>一个缺口：勾选框（预勾 = 下一步会生成它）+ 说明 + 定位按钮。</summary>
    private static Control BuildItem(GenerationAuditItem item, bool preset, Action<Guid> locate)
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
