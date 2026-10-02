using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;              // ExtendClientAreaChromeHints
using Avalonia.VisualTree;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 所有弹窗共用的外壳：**无边框 + 玻璃底 + 自绘标题条**。
///
/// 为什么要有这一层：主窗口与启动页是自绘的无边框窗口（`SystemDecorations="None"` + 透明背景 + 顶部命令条），
/// 而弹窗此前都是朴素的 `new Window`，在 Windows 上顶着系统标题栏和系统配色出现——
/// 同一个应用里两套外观，一眼就能看出「这是两个东西拼起来的」。
/// 现在弹窗统一走这里：标题条、拖动、关闭按钮、Esc 关闭、圆角与顶部冷蓝光都只有一份实现。
///
/// `Create` 里的 `contentHeight` 指的是**内容区**高度（标题条另算）：
/// 传 null 表示按内容自适应高度（`SizeToContent`），传数字表示内容区固定这么高。
/// 这样调用方写的数字仍然是「内容多高」，不用关心标题条占了多少。
/// </summary>
internal static class DialogShell
{
    /// <summary>标题条高度。与主窗口顶部命令条保持同一套视觉密度。</summary>
    public const double TitleBarHeight = 44;

    /// <summary>对话框默认宽度（少数窗口会覆盖）。</summary>
    public const double DefaultWidth = 520;

    public static Window Create(
        string title,
        Control content,
        double width = DefaultWidth,
        double? contentHeight = null,
        bool resizable = false)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            CanResize = resizable,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // 与主窗口同一套无边框策略：系统不画标题栏，标题条与窗口按钮都由我们自己画。
            SystemDecorations = SystemDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome,
            ExtendClientAreaTitleBarHeightHint = 0,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Background = Brushes.Transparent
        };

        if (contentHeight is { } bodyHeight)
        {
            window.Height = bodyHeight + TitleBarHeight;
            window.SizeToContent = SizeToContent.Manual;
        }
        else
        {
            window.SizeToContent = SizeToContent.Height;
        }

        if (!resizable)
        {
            // 不可缩放的弹窗：把上下限钉死，免得某些平台仍能从边缘拖出奇怪的尺寸。
            window.MinWidth = width;
            window.MaxWidth = width;
            if (contentHeight is { } lockedHeight)
            {
                window.MinHeight = lockedHeight + TitleBarHeight;
                window.MaxHeight = lockedHeight + TitleBarHeight;
            }
        }

        window.Content = BuildSurface(window, title, content);

        // Esc 关窗：自绘标题条去掉了系统的关闭路径，键盘上得留一条退路。
        // 关闭不带返回值，`ShowDialog<T>` 会拿到 default(T)，正好等同于「取消」。
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            window.Close();
            e.Handled = true;
        };

        return window;
    }

    private static Control BuildSurface(Window window, string title, Control content)
    {
        var titleBar = BuildTitleBar(window, title);

        var rows = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight},*") };
        rows.Children.Add(titleBar);
        Grid.SetRow(content, 1);
        rows.Children.Add(content);

        var surface = new Panel();
        // 顶部冷蓝光场：与主窗口同一份资源，弹窗不会是「纯黑一块」。
        surface.Children.Add(new Border
        {
            Background = AgentDialogUi.Brush("DfAuroraSoft"),
            IsHitTestVisible = false
        });
        surface.Children.Add(rows);

        return new Border
        {
            Background = AgentDialogUi.Brush("DfBg"),
            BorderBrush = AgentDialogUi.Brush("DfLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = surface
        };
    }

    private static Control BuildTitleBar(Window window, string title)
    {
        var mark = new Border
        {
            Width = 20,
            Height = 20,
            Background = AgentDialogUi.Brush("DfSurface3"),
            BorderBrush = AgentDialogUi.Brush("DfLineGlow"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = "◈",
                FontSize = 11,
                Foreground = AgentDialogUi.Brush("DfPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var label = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = AgentDialogUi.Brush("DfInk"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var close = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("windowButton");
        close.Classes.Add("closeButton");
        close.Click += (_, _) => window.Close();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 0) };
        row.Children.Add(mark);
        Grid.SetColumn(label, 1);
        label.Margin = new Thickness(10, 0, 10, 0);
        row.Children.Add(label);
        Grid.SetColumn(close, 2);
        row.Children.Add(close);

        var bar = new Border
        {
            BorderBrush = AgentDialogUi.Brush("DfLine"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Panel { Children = { MakeEdgeLight(), row } }
        };

        bar.PointerPressed += (_, e) => StartDrag(window, bar, e);
        return bar;
    }

    /// <summary>标题条上的拖动手势：按在按钮上时不拖（否则点关闭会变成拖窗口）。</summary>
    private static void StartDrag(Window window, Control titleBar, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button) return;
            if (ReferenceEquals(visual, titleBar)) break;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private static Border MakeEdgeLight()
    {
        var edge = new Border { Classes = { "edgeTop" } };
        return edge;
    }
}
