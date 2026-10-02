using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>启动页里的一个最近项目条目。</summary>
internal sealed record RecentProjectItem(string Name, string Path, string Note, bool Exists);

/// <summary>
/// 启动页：窗口起来后先让用户选项目（新建 / 打开 / 最近），**选择之前不展示画布、工作树与设置**。
///
/// 版式是「上半屏品牌、下半屏动作」：主视觉是 LOGO 与品牌语，选择项目与最近项目沉在窗口下方，
/// 视线从上往下走一遍就能做完选择。动效一共四处，都写在本文件里（一处就能看完全部动效，不用去 XAML 里翻）：
/// 光晕呼吸、光环旋转、标语里那个 YES 的呼吸，以及主视觉与下方动作区的先后入场。
///
/// 这里只负责**收集选择**：真正建项目 / 开项目、写最近列表、加载画布都由 MainWindow 执行，
/// 与左侧栏那两个按钮走同一条路径，免得同一件事有两份实现。
/// </summary>
public partial class StartPageView : UserControl
{
    public event EventHandler<string>? CreateRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler<string>? RecentRequested;

    /// <summary>列表项与路径按下标对齐：比从界面文本里回查路径可靠（路径本身可能被省略号截断）。</summary>
    private readonly List<string> recentPaths = new();

    private bool animationsStarted;

    public StartPageView()
    {
        InitializeComponent();
        // Loaded 可能触发多次（切回启动页时会重新挂载），动效只放一次，免得越叠越快。
        Loaded += (_, _) => PlayStartupAnimations();
    }

    /// <summary>铺最近项目列表；列表为空时显示空态文案。</summary>
    internal void SetRecent(IReadOnlyList<RecentProjectItem> items)
    {
        RecentList.Items.Clear();
        recentPaths.Clear();
        foreach (var item in items)
        {
            var title = new TextBlock
            {
                Text = item.Name,
                FontSize = 13,
                Foreground = Brush(item.Exists ? "DfInk" : "DfInk3"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var note = new TextBlock
            {
                Text = item.Note,
                FontSize = 10,
                Foreground = Brush("DfInk3"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var path = new TextBlock
            {
                Text = item.Path,
                FontSize = 10,
                Foreground = Brush(item.Exists ? "DfInk3" : "DfError"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(title);
            text.Children.Add(note);
            text.Children.Add(path);

            var entry = new ListBoxItem { Content = text };
            entry.Classes.Add("recentCard");                       // 卡片外观与悬停过渡都在 XAML 的样式里
            ToolTip.SetTip(entry, item.Path);
            recentPaths.Add(item.Path);
            RecentList.Items.Add(entry);
        }

        RecentEmptyText.IsVisible = items.Count == 0;
    }

    /// <summary>就地提示失败原因（例如所选文件夹不是有效项目）。</summary>
    internal void ShowStatus(string message, bool warning)
    {
        StatusLine.Text = message;
        StatusLine.Foreground = Brush(warning ? "DfError" : "DfInk3");
        StatusLine.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    // ==================== 动效 ====================

    /// <summary>
    /// 启动动效：主视觉先淡入上浮，下方动作区稍晚落位（视线自然往下走），随后两处呼吸循环起来。
    /// 光环的旋转写在 XAML 的样式里（那里能直接改 RenderTransform 的角度），其余都在这里：
    /// 属性名有编译期检查，改错了当场报错，而不是运行时静默不动。
    /// </summary>
    private void PlayStartupAnimations()
    {
        if (animationsStarted) return;
        animationsStarted = true;

        RiseIntoPlace(HeroArea, offsetY: 18, delayMs: 0);
        RiseIntoPlace(ActionPanel, offsetY: 28, delayMs: 140);
        StartBreathing(LogoGlow, from: 0.35, to: 0.90, seconds: 3.2);
        StartBreathing(BrandYes, from: 0.55, to: 1.00, seconds: 2.2);
    }

    /// <summary>淡入 + 上浮：先摆到偏移位置，挂上过渡，再设成最终值，剩下的交给过渡自己跑。</summary>
    private static void RiseIntoPlace(Visual target, double offsetY, int delayMs)
    {
        target.RenderTransform = TransformOperations.Parse($"translate(0px,{offsetY}px)");
        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(460),
                Delay = TimeSpan.FromMilliseconds(delayMs),
                Easing = new CubicEaseOut()
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(540),
                Delay = TimeSpan.FromMilliseconds(delayMs),
                Easing = new CubicEaseOut()
            }
        };

        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
    }

    /// <summary>呼吸：来回改不透明度，用正弦缓动，避免机械的线性跳动。</summary>
    private static void StartBreathing(Visual target, double from, double to, double seconds)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(seconds),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, to) } }
            }
        };

        _ = animation.RunAsync(target);
    }

    // ==================== 交互 ====================

    private void Create_OnClick(object? sender, RoutedEventArgs e) =>
        CreateRequested?.Invoke(this, NameBox.Text?.Trim() ?? string.Empty);

    private void Open_OnClick(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void Recent_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = RecentList.SelectedIndex;
        if (index < 0 || index >= recentPaths.Count) return;

        var path = recentPaths[index];
        RecentList.SelectedIndex = -1;    // 先清掉选中：交出去之后用户还能再点同一条
        RecentRequested?.Invoke(this, path);
    }

    /// <summary>本控件所在的窗口（UserControl 没有 Window 成员，要走 TopLevel）。</summary>
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Host is not { } window) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 点在窗口按钮上时不要拖窗口。
        for (Visual? visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button) return;
            if (visual == StartTitleBar) break;
        }

        if (e.ClickCount >= 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (window.WindowState != WindowState.Maximized) window.BeginMoveDrag(e);
    }

    private void Minimize_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window) window.WindowState = WindowState.Minimized;
    }

    private void Maximize_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Host is not { } window) return;
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Host?.Close();

    /// <summary>主题取不到时退到透明而不是抛异常：起不来比颜色不对严重得多。</summary>
    private static IBrush Brush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}
