using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 右下角那枚常驻入口的图标：一个**一直在吸积物质的黑洞**。
///
/// 画的是什么（从里到外）：纯黑的**事件视界**、贴着它的一圈**光子环**（最亮的那道边）、
/// 一圈**吸积盘**（厂家色，一侧更亮，整圈在转），还有几块**不断往里掉的东西**——
/// 从外缘旋进来、越转越快、越靠越暗，进了视界就没了，然后从外缘重新掉一块。
///
/// 为什么自己做控件而不是用一张 GIF：这里要跟着**厂家色**变（换模型时整枚图标跟着换色，
/// 与 <see cref="ProviderBadges"/> 一致），而图片没法换色；而且要能在不可见时停下来。
///
/// 开销：约 30 帧/秒，46×46 的方块里画几十条线。**看不见的时候定时器一定是停的**：
/// 「面板打开」「被启动页整个盖住」由调用方算进 <see cref="Running"/>（见 <c>UpdateAgentBadge</c>），
/// 「窗口最小化」由控件自己盯着。所以画布上放着不动的那枚徽标不会白烧 CPU。
/// </summary>
internal sealed class BlackHoleBadge : Control
{
    /// <summary>视界半径。中心那块纯黑就按它画。</summary>
    private const double HorizonRadius = 8.5;

    /// <summary>吸积盘从里到外的半径；外缘也是碎块掉进来的起点。</summary>
    private const double DiskInner = 9.6;
    private const double DiskOuter = 19.0;

    /// <summary>盘的倾角：y 方向压扁多少。0.42 大约是俯看 65 度——电影里那个经典角度。</summary>
    private const double Tilt = 0.42;

    private const int SegmentCount = 56;
    private const int MatterCount = 6;

    private readonly DispatcherTimer timer;
    private double seconds;

    /// <summary>调用方希望它转不转（面板收起时才是「要转」）。真正的开关还要看可见性，见 <see cref="SyncTimer"/>。</summary>
    private bool requested = true;

    /// <summary>所在的顶层窗口。窗口最小化时要停：最小化后画面没人看，但定时器照旧在烧。</summary>
    private TopLevel? topLevel;

    public BlackHoleBadge()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            // 按**固定步长**推进，不用墙上时间：掉帧时宁可慢一点，也不要碎块跳一格。
            seconds += 0.033;
            InvalidateVisual();
        };
        ClipToBounds = false;
    }

    /// <summary>厂家区分色。整枚图标（光子环、盘、碎块、外晕）都用它。</summary>
    public Color Accent { get; set; } = Color.Parse("#8FA6BD");

    /// <summary>中心那两个字（厂家的缩写）。黑洞也是「当前在用哪一家」的入口，这两个字不能丢。</summary>
    public string Label { get; set; } = "AI";

    /// <summary>该不该转。由界面在显隐变化时设置；这里只记「要不要」，真正启停在 <see cref="SyncTimer"/>。</summary>
    public bool Running
    {
        get => requested;
        set
        {
            if (value == requested) return;
            requested = value;
            SyncTimer();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null) topLevel.PropertyChanged += TopLevel_OnPropertyChanged;
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (topLevel is not null) topLevel.PropertyChanged -= TopLevel_OnPropertyChanged;
        topLevel = null;
        SyncTimer();
        base.OnDetachedFromVisualTree(e);
    }

    private void TopLevel_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) SyncTimer();
    }

    /// <summary>
    /// 唯一决定定时器启停的地方：**要转**、而且**窗口没被最小化**。
    ///
    /// 「窗口最小化」为什么要单独看：最小化以后没人在看这块画面，可 <see cref="DispatcherTimer"/>
    /// 照旧按 30 帧/秒唤醒 UI 线程——这枚徽标是常驻的，一直转就是一整晚的白烧。
    /// 「被启动页盖住 / 面板打开」这两件事由调用方算进 <see cref="Running"/>（见 <c>UpdateAgentBadge</c>），
    /// 因为那种「祖先被隐藏」的状态在这里看不到。
    /// </summary>
    private void SyncTimer()
    {
        // topLevel 为空（还没进可视树 / 已摘下来）时不拦着：那种情况下有没有在画由调用方说了算。
        var shouldRun = requested
                        && (topLevel is not Window window || window.WindowState != WindowState.Minimized);
        if (shouldRun == timer.IsEnabled) return;
        if (shouldRun) timer.Start();
        else timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 2 || height <= 2) return;

        var center = new Point(width / 2, height / 2);
        var accent = Accent;

        // ① 外晕：几层由大到小、由淡到略实的同色椭圆叠出雾感（替代原来那层 BoxShadow）。
        //    不用 RadialGradientBrush：它的 Radius 已被标记过时，而这里只有 46px，
        //    叠四层已经足够柔和，还省掉一次渐变采样。
        for (var layer = 3; layer >= 0; layer--)
        {
            var spread = 0.62 + layer * 0.13;
            var glow = new SolidColorBrush(Color.FromArgb((byte)(9 + layer * 7), accent.R, accent.G, accent.B));
            context.DrawEllipse(glow, null, center, width / 2 * spread, height / 2 * spread);
        }

        // ② 吸积盘：一圈短线段，透明度沿角度变化（一侧亮、一侧暗），整圈随时间转。
        //    这样即使没有扫描渐变，看着也是「在转的一圈物质」而不是一条静止的环。
        var rotation = seconds * 0.9;
        var pen = new Pen(new SolidColorBrush(accent), 2.0);
        for (var index = 0; index < SegmentCount; index++)
        {
            var a0 = index * Math.Tau / SegmentCount + rotation;
            var a1 = (index + 1.4) * Math.Tau / SegmentCount + rotation;
            // 亮度按角度做一圈渐变：转过最亮那侧时最实，背面只剩一点余痕。
            var brightness = 0.20 + 0.80 * Math.Pow((Math.Sin((index * Math.Tau / SegmentCount) - rotation) + 1) / 2, 2.2);
            pen.Brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(brightness * 235, 0, 255), accent.R, accent.G, accent.B));
            pen.Thickness = 1.4 + 1.1 * brightness;
            const double radius = (DiskInner + DiskOuter) / 2;
            context.DrawLine(pen, OnDisk(center, radius, a0), OnDisk(center, radius, a1));
        }

        // ③ 往里掉的碎块：从外缘旋到视界，越靠越近、越转越快、越暗越小，进了视界就没了。
        for (var index = 0; index < MatterCount; index++)
        {
            // 每块错开一个相位，于是「一直在吸」而不是一块一块地来。
            var fall = (seconds * 0.30 + index / (double)MatterCount) % 1.0;
            var radius = DiskOuter - (DiskOuter - HorizonRadius + 1.4) * Math.Pow(fall, 0.65);
            var angle = index * Math.Tau / MatterCount + fall * fall * 4.6 + seconds * 0.25;
            var dot = OnDisk(center, radius, angle);
            var size = 2.4 - 1.3 * fall;
            var alpha = (byte)Math.Clamp(235 * (1 - fall * 0.92), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            context.DrawEllipse(brush, null, dot, size / 2, size / 2 * 0.8);
        }

        // ④ 事件视界：纯黑一块，压在盘与碎块之上（掉进去就是看不见）。
        context.DrawEllipse(Brushes.Black, null, center, HorizonRadius, HorizonRadius);

        // ⑤ 光子环：贴视界那一圈最亮的边。它比盘亮，黑洞才有「边」。
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(
            246, Math.Min((byte)255, (byte)(accent.R + 40)), Math.Min((byte)255, (byte)(accent.G + 40)),
            Math.Min((byte)255, (byte)(accent.B + 40)))), 1.3);
        context.DrawEllipse(null, rim, center, HorizonRadius + 0.7, HorizonRadius + 0.7);

        // ⑥ 中心那两个字：黑洞也是「现在用的是哪一家」的入口，这两个字不能丢。
        //    字号按 0.22 倍宽度取——两个大写在视界（直径 17px）里还要留一圈余量，
        //    再大就会贴到光子环上，看着像字压着边。
        if (Label.Length > 0)
        {
            var text = new FormattedText(
                Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                Math.Max(8, width * 0.22),
                new SolidColorBrush(Color.FromArgb(232, accent.R, accent.G, accent.B)));
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }

    /// <summary>盘平面上的一个点：x 是半径、y 按倾角压扁，看着就像俯看一个斜的盘。</summary>
    private static Point OnDisk(Point center, double radius, double angle) =>
        new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius * Tilt);
}
