using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
// TransformOperations 在 Transformation 子命名空间、Setter 在 Styling 里：这两个是 animation 与
// 变换的属性名有编译期检查的前提，缺了它们就只能退化回字符串写法。
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>揭晓里的一张卡：序号与画布上那排的编号一致，路径是已经落盘的那张图。</summary>
internal sealed record GachaCard(int Index, string Path);

/// <summary>
/// 出图「开奖」的全屏揭晓。设置里那个开关打开后才会有这条路。
///
/// 流程：自有形象许愿 → 光点飞出 → 卡片依次翻面 → 挑一张收进节点。
///
/// **它是用户主动点开的一层，不是自己弹出来的**。这一点是它能全屏的前提：
/// 早先否决过「出图完自动弹出大图」，理由是选择权在用户看到之前就被替他做完了；
/// 而这里是他自己点的卡片，看多久、要不要挑、要不要先不看，都由他。
///
/// 三条刻意的设计取舍：
/// · **形象是自有的**（那个 ◈ 标记 + 厂家配色），不用任何厂家的 logo 或拟人形象——那些是各自的商标与
///   著作权作品，且拿了会让人以为有官方合作。厂家只体现为「两个字母 + 一个区分色」，与
///   <see cref="ProviderBadges"/> 是同一份数据。
/// · **光点数量暂时统一**，不按「金 / 紫 / 蓝」分档：质量判据还没定（TODO 第 8 条），
///   凭空分档等于在界面上宣布一个不存在的评级。等判据定了，只改 <see cref="SparkCount"/> 的来源。
/// · **许愿是仪式，不是因果**：图在这一层打开之前就已经全出好了。所以文案只陈述事实
///   （「这一批 N 张已出好」），不写「许愿会影响出图」这类不成立的话。
/// </summary>
internal static class GachaRevealDialog
{
    /// <summary>
    /// 光点数量。**暂时是常数**：全批一样多。
    ///
    /// 不先造一套「金 8 个 / 紫 5 个 / 蓝 2 个」的原因是：那会让用户以为系统judged了这张图更好，
    /// 而实际上没有任何判据。等第 8 条定下「谁判、阈值多少」之后，把这个常量换成一个按档位取值的函数即可，
    /// 布局、动画、光点渲染都不用动。
    /// </summary>
    private const int SparkCount = 4;

    /// <summary>许愿阶段停留多久（毫秒）。全屏、用户主动开，所以可以比格子内那种 0.3 秒宽松得多。</summary>
    private const int WishHoldMs = 820;

    /// <summary>卡片出现到开始翻面的间隔。</summary>
    private const int CardsAppearMs = 180;

    /// <summary>相邻两张卡翻面的错开量：既有「一张张揭晓」的节奏，整排也不至于等太久。</summary>
    private const int FlipStaggerMs = 170;

    /// <summary>翻面的前半程（转到侧面对着你的那一瞬间）。</summary>
    private const int FlipHalfMs = 110;

    /// <summary>翻面的后半程（从侧面展开成正对）。</summary>
    private const int FlipBackMs = 160;

    /// <summary>
    /// 打开全屏揭晓。返回被选中的那一张的序号；用户没挑就关掉时返回 null
    /// （**不等于失败**：那一排卡还在画布上盖着，可以再点开）。
    /// </summary>
    public static async Task<int?> ShowAsync(
        Window owner,
        string nodeTitle,
        IReadOnlyList<GachaCard> cards,
        string badgeAbbreviation,
        string badgeColorHex,
        string sourceLine)
    {
        if (cards.Count == 0) return null;

        var accent = Color.Parse(badgeColorHex);
        // 卡片按张数定尺寸：3 张时可以给大一点，6 张就得收窄，否则一行放不下。
        var cardWidth = cards.Count <= 3 ? 210 : 152;
        var cardHeight = cardWidth * 1.42;

        int? selected = null;
        int? chosen = null;

        // ---- 底部按钮 ----
        var skip = new Button { Content = "跳过动画", IsVisible = true };
        skip.Classes.Add("miniButton");
        var confirm = new Button { Content = "用这一张", IsEnabled = false };
        confirm.Classes.Add("primary");
        var cancel = new Button { Content = "先不开" };
        cancel.Classes.Add("miniButton");

        var footerHint = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#7C8CA0")),
            Text = "正在揭晓…"
        };

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { skip, cancel, confirm }
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(34, 0, 34, 26)
        };
        footer.Children.Add(footerHint);
        Grid.SetColumn(footerButtons, 1);
        footer.Children.Add(footerButtons);

        // ---- 舞台：许愿区与卡片区叠在同一块地方，靠不透明度交接 ----
        var wishStage = BuildWishStage(nodeTitle, cards.Count, badgeAbbreviation, accent, sourceLine, out var mark, out var markRing);
        var sparkField = BuildSparkField(accent, out var sparks);
        var cardRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = cards.Count <= 3 ? 18 : 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0
        };

        var frames = new List<Border>();
        var faces = new List<Control>();
        // 「这一批翻完了没有」。声明在卡片之前：点击与按钮状态都读它，
        // 而 C# 里被捕获的局部变量必须先声明后使用（lambda 是按书写位置编译的）。
        var flipsDone = false;
        for (var i = 0; i < cards.Count; i++)
        {
            var frame = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(190, 118, 144, 194)),
                Background = new SolidColorBrush(Color.Parse("#0E141C")),
                ClipToBounds = true,
                Child = BuildCardBack(i + 1),
                // 翻面是绕中轴压扁再展开，所以变换原点必须在中心。
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = TransformOperations.Parse("scale(1,1)")
            };
            frames.Add(frame);
            faces.Add(BuildCardFace(cards[i], i + 1, accent));
            var captured = i;
            frame.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (!flipsDone) return;     // 还没翻完先不接受选择：这时候看到的还不是图
                ApplySelection(captured);
            };
            cardRow.Children.Add(frame);
        }

        var stage = new Panel { Children = { wishStage, sparkField, cardRow } };

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        body.Children.Add(new Panel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { stage }
        });
        Grid.SetRow(footer, 1);
        body.Children.Add(footer);

        // ---- 整块底：不透明的深底 + 顶部一层冷蓝光，与主窗口同一套观感 ----
        var surface = new Panel();
        surface.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#0B1220"), 0),
                    new GradientStop(Color.Parse("#06090E"), 0.62),
                    new GradientStop(Color.Parse("#04060A"), 1)
                }
            }
        });
        surface.Children.Add(new Border
        {
            Background = AgentDialogUi.Brush("DfAuroraSoft"),
            IsHitTestVisible = false
        });
        surface.Children.Add(body);

        var window = new Window
        {
            Title = "出图开奖",
            SystemDecorations = SystemDecorations.None,
            WindowState = WindowState.FullScreen,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#06090E")),
            Content = surface
        };

        // ---- 时序 ----
        var timers = new List<DispatcherTimer>();
        void At(int ms, Action action) => timers.Add(ScheduleOnce(ms, action));

        void FinishAll()
        {
            // 跳过：把动画直接推到终点。停掉剩下的定时器，否则它们随后还会再改一次状态。
            foreach (var timer in timers) timer.Stop();
            foreach (var frame in frames)
            {
                frame.RenderTransform = TransformOperations.Parse("scale(1,1)");
                frame.Transitions = null;
            }
            for (var i = 0; i < frames.Count; i++)
                if (!ReferenceEquals(frames[i].Child, faces[i])) frames[i].Child = faces[i];
            wishStage.Opacity = 0;
            sparkField.Opacity = 0;
            cardRow.Opacity = 1;
            skip.IsVisible = false;
            flipsDone = true;
            if (!confirm.IsEnabled) footerHint.Text = "点一张选中，再点「用这一张」";
        }

        // 许愿：形象落位 + 呼吸，光点随后飞出。
        At(0, () =>
        {
            mark.Opacity = 1;
            mark.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            markRing.Opacity = 1;
            StartFloat(mark, seconds: 2.6);
        });
        At(WishHoldMs, () =>
        {
            sparkField.Opacity = 1;
            for (var i = 0; i < sparks.Count; i++)
            {
                var spark = sparks[i];
                var delay = i * 55;
                spark.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(360),
                        Delay = TimeSpan.FromMilliseconds(delay),
                        Easing = new CubicEaseOut()
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(180),
                        Delay = TimeSpan.FromMilliseconds(delay + 240),
                        Easing = new CubicEaseIn()
                    }
                };
                spark.Opacity = 0;
                spark.RenderTransform = TransformOperations.Parse(
                    $"translate({spark.Tag as string})");
            }
        });
        At(WishHoldMs + 430, () =>
        {
            wishStage.Opacity = 0;
            sparkField.Opacity = 0;
            cardRow.Opacity = 1;
        });
        At(WishHoldMs + 430 + CardsAppearMs, () =>
        {
            for (var i = 0; i < frames.Count; i++)
                FlipCard(frames[i], faces[i], DelayMs: i * FlipStaggerMs);
        });
        At(WishHoldMs + 430 + CardsAppearMs + (frames.Count - 1) * FlipStaggerMs + FlipHalfMs + FlipBackMs, FinishAll);

        // ---- 交互 ----
        // 选中状态只有这一处计算：点击与键盘都走它，免得两处各写一份、慢慢长出不一致。
        void ApplySelection(int index)
        {
            if (index < 0 || index >= frames.Count) return;
            selected = index;
            for (var k = 0; k < frames.Count; k++)
            {
                var on = k == index;
                frames[k].BorderThickness = new Thickness(on ? 2 : 1);
                frames[k].BorderBrush = new SolidColorBrush(on
                    ? accent
                    : Color.FromArgb(190, 118, 144, 194));
                frames[k].BoxShadow = on
                    ? BoxShadows.Parse($"0 0 26 -2 #{accent.R:X2}{accent.G:X2}{accent.B:X2}")
                    : default(BoxShadows);
            }
            confirm.IsEnabled = true;
            footerHint.Text = $"已选中第 {index + 1} 张（共 {cards.Count} 张）";
        }

        skip.Click += (_, _) => FinishAll();
        cancel.Click += (_, _) => window.Close();
        confirm.Click += (_, _) =>
        {
            chosen = selected;          // 点了「用这一张」才算数：点开又关掉不留痕迹
            window.Close();
        };
        window.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    window.Close();
                    e.Handled = true;
                    break;
                // 键盘上留一条挑卡的路：左右改选中、回车确认。全屏没有鼠标时也不至于只能关掉。
                case Key.Left when flipsDone:
                    ApplySelection(selected is { } left ? Math.Max(0, left - 1) : frames.Count - 1);
                    e.Handled = true;
                    break;
                case Key.Right when flipsDone:
                    ApplySelection(selected is { } right ? Math.Min(frames.Count - 1, right + 1) : 0);
                    e.Handled = true;
                    break;
                case Key.Enter when confirm.IsEnabled:
                    chosen = selected;
                    window.Close();
                    e.Handled = true;
                    break;
            }
        };

        // 窗口起来之后才开始跑：还没显示就设动画终点，用户看到的第一帧就是终态。
        window.Opened += (_, _) =>
        {
            flipsDone = false;
            foreach (var timer in timers) timer.Start();
        };
        window.Closed += (_, _) => { foreach (var timer in timers) timer.Stop(); };

        await window.ShowDialog(owner);
        return chosen;
    }

    /// <summary>许愿区：自有形象（那枚 ◈ 标记 + 一圈光）+ 厂家徽标 + 这一批的实情。</summary>
    private static Control BuildWishStage(
        string nodeTitle, int count, string badgeAbbreviation, Color accent, string sourceLine,
        out Border mark, out Border markRing)
    {
        markRing = new Border
        {
            Width = 108,
            Height = 108,
            CornerRadius = new CornerRadius(54),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)),
            BoxShadow = BoxShadows.Parse($"0 0 46 4 #{accent.R:X2}{accent.G:X2}{accent.B:X2}"),
            IsHitTestVisible = false
        };
        markRing.Child = new TextBlock
        {
            Text = "◈",
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(235, 226, 236, 255))
        };

        mark = new Border
        {
            // 起来时先往上浮一段再落位：与启动页那几处入场动效同一个做法。
            RenderTransform = TransformOperations.Parse("translate(0px,16px)"),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = markRing
        };

        var badge = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(11, 4),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = badgeAbbreviation,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(accent)
            }
        };

        var stack = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = nodeTitle,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E6EDF7"))
        });
        stack.Children.Add(new TextBlock
        {
            // 只陈述事实：图确实已经出好了。许愿是仪式，不假装它在影响结果。
            Text = $"这一批 {count} 张已经出好",
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#7C8CA0"))
        });
        stack.Children.Add(mark);
        stack.Children.Add(badge);
        stack.Children.Add(new TextBlock
        {
            Text = $"本次出图：{sourceLine}",
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD"))
        });
        stack.Children.Add(new TextBlock
        {
            Text = "许个愿…",
            FontSize = 12,
            Margin = new Thickness(0, 22, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#A7C7EA"))
        });
        return stack;
    }

    /// <summary>
    /// 光点：从形象那里朝上扇开。
    ///
    /// 每条光点把「飞多远多大角度」先算好塞进 <c>Tag</c>（形如 "12px,-180px"），
    /// 动画阶段直接取用——省得在动画回调里再算一次三角函数。
    /// </summary>
    private static Control BuildSparkField(Color accent, out List<Border> sparks)
    {
        const double size = 460;
        var field = new Canvas
        {
            Width = size,
            Height = size,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        sparks = new List<Border>();
        for (var i = 0; i < SparkCount; i++)
        {
            // 以上方为中心扇开（-90 度是正上方）：光点是「从形象这里散出去的」，方向要对得上。
            var spread = 26.0;
            var angle = -90 + (i - (SparkCount - 1) / 2.0) * spread;
            var radius = 168 + (i % 2) * 34;
            var dx = Math.Cos(angle * Math.PI / 180) * radius;
            var dy = Math.Sin(angle * Math.PI / 180) * radius;

            const double dot = 8;
            var spark = new Border
            {
                Width = dot,
                Height = dot,
                CornerRadius = new CornerRadius(dot / 2),
                Background = new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B)),
                BoxShadow = BoxShadows.Parse($"0 0 16 3 #{accent.R:X2}{accent.G:X2}{accent.B:X2}"),
                RenderTransform = TransformOperations.Parse("translate(0px,0px)"),
                Tag = $"{dx:0.#}px,{dy:0.#}px"
            };
            Canvas.SetLeft(spark, size / 2 - dot / 2);
            Canvas.SetTop(spark, size / 2 - dot / 2);
            field.Children.Add(spark);
            sparks.Add(spark);
        }

        return field;
    }

    /// <summary>卡背：只有标记与编号——「还没翻」这件事本身要一眼可见。</summary>
    private static Control BuildCardBack(int number)
    {
        var stack = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = "◈",
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(150, 150, 178, 255))
        });
        stack.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#5C6A7C"))
        });
        return stack;
    }

    /// <summary>卡正面：图 + 底部编号。图读不出来时如实说明，不拿一张空白冒充。</summary>
    private static Control BuildCardFace(GachaCard card, int number, Color accent)
    {
        var panel = new Panel();
        var bitmap = LoadFace(card.Path);
        if (bitmap is not null)
        {
            panel.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "这张图读不出来",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#E5A3A3"))
            });
        }

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 10, 15, 22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B))
            }
        });
        return panel;
    }

    /// <summary>
    /// 翻一张卡：先横向压扁（背面转到侧面对着你的那一瞬间），压到最扁时把内容换成正面的图，再展开回来。
    ///
    /// 分两步是因为过渡只负责插值，中间插不进一个「换内容」的回调。压到 0.08 而不是 0：
    /// 完全压成 0 会在那一帧看不见任何东西，反而像闪了一下；留一点宽度，翻转的连续感就出来了。
    /// </summary>
    private static void FlipCard(Border card, Control face, int DelayMs)
    {
        card.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(FlipHalfMs),
                Delay = TimeSpan.FromMilliseconds(DelayMs),
                Easing = new CubicEaseIn()
            }
        };
        card.RenderTransform = TransformOperations.Parse("scale(0.08,1)");

        // 定时器不自己管生命周期：它只跑一次就停，窗口关掉时剩下的也会被统一停掉。
        ScheduleOnce(DelayMs + FlipHalfMs, () =>
        {
            card.Child = face;
            card.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(FlipBackMs),
                    Easing = new CubicEaseOut()
                }
            };
            card.RenderTransform = TransformOperations.Parse("scale(1,1)");
        }).Start();
    }

    /// <summary>一次性的定时器。集中在这里建，便于统一设定间隔语义（到点执行，然后自停）。</summary>
    private static DispatcherTimer ScheduleOnce(int delayMs, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    /// <summary>让形象缓缓上下浮动：正弦缓动来回播，避免机械的线性跳动（与启动页那处同一个做法）。</summary>
    private static void StartFloat(Visual target, double seconds)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(seconds),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0.82) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1.0) } }
            }
        };
        _ = animation.RunAsync(target);
    }

    /// <summary>解一张正面要用的图。按 480 宽解码而不是整图：出图可能是 4K，整图解出来只会让翻面卡顿。</summary>
    private static Bitmap? LoadFace(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 480);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
