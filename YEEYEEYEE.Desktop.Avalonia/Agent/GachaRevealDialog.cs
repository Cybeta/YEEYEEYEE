using System.Runtime.InteropServices;
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
/// 四条刻意的设计取舍：
/// · **形象是自有的**（那个 ◈ 标记 + 厂家配色），不用任何厂家的 logo 或拟人形象——那些是各自的商标与
///   著作权作品，且拿了会让人以为有官方合作。厂家只体现为「两个字母 + 一个区分色」，与
///   <see cref="ProviderBadges"/> 是同一份数据。
/// · **光点数量暂时统一**，不按「金 / 紫 / 蓝」分档：质量判据还没定（TODO 第 8 条），
///   凭空分档等于在界面上宣布一个不存在的评级。等判据定了，只改 <see cref="SparkCount"/> 的来源。
/// · **许愿是仪式，不是因果**：图在这一层打开之前就已经全出好了。所以文案只陈述事实
///   （「这一批 N 张已出好」），不写「许愿会影响出图」这类不成立的话。
/// · **每张卡的颜色取它自己那张图的主色相**，不是六张共用一个厂家色。卡面顶部那道色带、选中时的
///   光晕、翻面时的闪光都用它——这是「这张卡的颜色」，不是「这张卡的质量」，
///   所以它不承担任何评级含义（评级是待到办第 8 条的事）。
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
    private const int CardsAppearMs = 200;

    /// <summary>相邻两张卡翻面的错开量：既有「一张张揭晓」的节奏，整排也不至于等太久。</summary>
    private const int FlipStaggerMs = 170;

    /// <summary>翻面的前半程（转到侧面对着你的那一瞬间）。</summary>
    private const int FlipHalfMs = 110;

    /// <summary>翻面的后半程（从侧面展开成正对）。</summary>
    private const int FlipBackMs = 170;

    /// <summary>兜底卡面比例（拿不到图时用）。二游卡面偏瘦长。</summary>
    private const double FallbackCardAspect = 1.42;

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

        // 先把这几张图都解出来：卡面的比例要按**这批图自己的**比例定，见 CardHeightRatioFor。
        var faceBitmaps = new Bitmap?[cards.Count];
        for (var i = 0; i < cards.Count; i++) faceBitmaps[i] = LoadFace(cards[i].Path);

        // 卡片按张数定尺寸：3 张时可以给大一点，6 张就得收窄，否则一行放不下。
        var cardWidth = cards.Count <= 3 ? 232 : 168;
        var cardHeight = cardWidth * CardHeightRatioFor(faceBitmaps);
        var gap = cards.Count <= 3 ? 22 : 15;
        var rowWidth = cards.Count * cardWidth + (cards.Count - 1) * gap;

        // 每张卡自己的主色：另解一张很小的缩略图，按色相分桶取最主要的那个色相。
        // 解 24 宽而不是从正面的 480 宽那张里抽：480 那张要全量拷进内存才读得到，
        // 而「这张图偏什么色」这件事，24×24 已经足够，且几乎不花时间。
        var tints = new Color[cards.Count];
        for (var i = 0; i < cards.Count; i++) tints[i] = ReadCardColor(cards[i].Path, accent);

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

        // ---- 舞台：许愿区、光点、卡片区叠在同一块地方，靠不透明度交接 ----
        var wishStage = BuildWishStage(nodeTitle, cards.Count, badgeAbbreviation, accent, sourceLine,
            out var mark, out var markRing);
        var sparkField = BuildSparkField(accent, out var sparks);

        var cardRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = gap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 卡排下面一条舞台边线、卡排后面一层柔光：六张卡直接浮在纯黑里会显得又空又平，
        // 这两样都不承担信息，纯粹让「这是一次揭晓」有个台面。
        var floor = new Border
        {
            Width = rowWidth + 150,
            Height = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(120, accent.R, accent.G, accent.B), 0.5),
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
                }
            }
        };

        var cardColumn = new StackPanel
        {
            Spacing = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { cardRow, floor }
        };

        var frames = new List<Border>();
        var faces = new List<Control>();
        var inners = new List<Grid>();
        var flashes = new List<Border>();
        // 「这一批翻完了没有」。声明在卡片之前：点击与按钮状态都读它，
        // 而 C# 里被捕获的局部变量必须先声明后使用（lambda 是按书写位置编译的）。
        var flipsDone = false;
        for (var i = 0; i < cards.Count; i++)
        {
            var tint = tints[i];
            var back = BuildCardBack(i + 1, tint);
            var face = BuildCardFace(faceBitmaps[i], i + 1, tint);
            faces.Add(face);

            // 卡面之外再套一层用于「翻面闪光」：它盖在卡面之上，翻完那一瞬间亮一下再化掉。
            var inner = new Grid();
            inner.Children.Add(back);
            var flash = new Border
            {
                Opacity = 0,
                IsHitTestVisible = false,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(200, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(150, tint.R, tint.G, tint.B), 0.5),
                        new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1)
                    }
                }
            };
            inner.Children.Add(flash);
            inners.Add(inner);
            flashes.Add(flash);

            var frame = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1.5),
                BorderBrush = CardBorder(tint, selected: false),
                Background = new SolidColorBrush(Color.Parse("#0B1119")),
                BoxShadow = CardShadows(tint, selected: false),
                ClipToBounds = true,
                Child = inner,
                Cursor = new Cursor(StandardCursorType.Hand),
                // 翻面是绕中轴压扁再展开，所以变换原点必须在中心；选中时的抬升也用它。
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = TransformOperations.Parse("scale(1,1)")
            };
            frames.Add(frame);
            var captured = i;
            frame.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (!flipsDone) return;     // 还没翻完先不接受选择：这时候看到的还不是图
                ApplySelection(captured);
            };
            // 鼠标扫过时这张抬一点、亮回来。一整排等亮的卡看着像「六张图」，
            // 扫过会动的那一排才像「可以挑的卡」。
            frame.PointerEntered += (_, _) =>
            {
                if (!flipsDone || selected == captured) return;
                frame.RenderTransform = TransformOperations.Parse("scale(1.035)");
                frame.Opacity = 1;
            };
            frame.PointerExited += (_, _) =>
            {
                if (!flipsDone || selected == captured) return;
                frame.RenderTransform = TransformOperations.Parse("scale(1,1)");
                frame.Opacity = selected is null ? 1 : 0.5;
            };
            cardRow.Children.Add(frame);
        }

        var stage = new Grid { Children = { wishStage, sparkField, cardColumn } };

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        body.Children.Add(new Panel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { stage }
        });
        // 全部揭晓之后，画面上只剩一排卡和底部几个按钮，会显得「上面空着」。
        // 顶上补一行结果标题（节点名 + 这一批几张），与卡排一起浮出来。
        var resultHeader = BuildResultHeader(nodeTitle, cards.Count, accent);
        resultHeader.VerticalAlignment = VerticalAlignment.Top;
        resultHeader.Margin = new Thickness(0, 72, 0, 0);
        resultHeader.Opacity = 0;
        body.Children.Add(resultHeader);
        Grid.SetRow(footer, 1);
        body.Children.Add(footer);

        // 卡片出现时把背景托一层光：**横贯整屏、上下都渐隐**的一道光带。
        // 它没有左右边界，所以不会像一块色块；只把卡排所在的那条水平带照亮一点。
        var ambient = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 118, 156, 255), 0),
                    new GradientStop(Color.FromArgb(18, 118, 156, 255), 0.44),
                    new GradientStop(Color.FromArgb(12, 92, 132, 224), 0.58),
                    new GradientStop(Color.FromArgb(0, 70, 110, 200), 1)
                }
            }
        };

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
        surface.Children.Add(ambient);
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

        // 所有卡都停在同一条时间线上：翻面结束之后才允许挑。选中的那张再抬起来、
        // 其余压暗——「挑一张」这件事在画面上必须有主次，六张等亮是看不出选没选的。
        var cardsAt = WishHoldMs + 470;
        var flipsAt = cardsAt + CardsAppearMs;
        var allDoneAt = flipsAt + (frames.Count - 1) * FlipStaggerMs + FlipHalfMs + FlipBackMs;

        void FinishAll()
        {
            // 跳过：把动画直接推到终点。停掉剩下的定时器，否则它们随后还会再改一次状态。
            foreach (var timer in timers) timer.Stop();
            for (var i = 0; i < frames.Count; i++)
            {
                frames[i].RenderTransform = TransformOperations.Parse("scale(1,1)");
                frames[i].Transitions = null;
                if (!ReferenceEquals(inners[i].Children[0], faces[i])) inners[i].Children[0] = faces[i];
                flashes[i].Opacity = 0;
            }
            wishStage.Opacity = 0;
            sparkField.Opacity = 0;
            ambient.Opacity = 1;
            resultHeader.Opacity = 1;
            cardColumn.Opacity = 1;
            skip.IsVisible = false;
            flipsDone = true;              // 先允许选择，再让 ApplySelection 把过渡接上
            ArmInteractions();
            footerHint.Text = "点一张挑走，再点「用这一张」";
        }

        /// <summary>把「选中要动的那几项」接上过渡：抬升、压暗、描边都要有动画才不跳。</summary>
        void ArmInteractions()
        {
            for (var i = 0; i < frames.Count; i++)
            {
                frames[i].Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(180),
                        Easing = new CubicEaseOut()
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(180),
                        Easing = new CubicEaseOut()
                    }
                };
            }
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
        At(cardsAt, () =>
        {
            wishStage.Opacity = 0;
            sparkField.Opacity = 0;
            ambient.Opacity = 1;
            resultHeader.Opacity = 1;
            cardColumn.Opacity = 1;
        });
        At(flipsAt, () =>
        {
            for (var i = 0; i < frames.Count; i++)
                FlipCard(frames[i], inners[i], flashes[i], faces[i], DelayMs: i * FlipStaggerMs);
        });
        At(allDoneAt, FinishAll);

        // ---- 交互 ----
        // 选中状态只有这一处计算：点击与键盘都走它，免得两处各写一份、慢慢长出不一致。
        void ApplySelection(int index)
        {
            if (index < 0 || index >= frames.Count) return;
            selected = index;
            for (var k = 0; k < frames.Count; k++)
            {
                var on = k == index;
                var tint = tints[k];
                frames[k].BorderThickness = new Thickness(on ? 3 : 1.5);
                frames[k].BorderBrush = CardBorder(tint, on);
                frames[k].BoxShadow = CardShadows(tint, on);
                frames[k].RenderTransform = TransformOperations.Parse(on ? "scale(1.06)" : "scale(1,1)");
                frames[k].Opacity = on ? 1 : 0.5;
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

    // ==================== 卡面 ====================

    /// <summary>
    /// 卡背：双框 + 斜向掠光 + 中央徽记 + 四角刻线。
    ///
    /// 二游的卡背之所以好看，靠的不是图案复杂，而是**层次**：底色有渐变、外框里再收一层内框、
    /// 光斜着扫过去、中间有个能认的记号。所以这里就按这四层叠，不用任何位图素材。
    /// </summary>
    private static Control BuildCardBack(int number, Color tint)
    {
        var panel = new Panel { IsHitTestVisible = false };

        // 第一层：斜向的深蓝紫渐变（纯色会把卡背压成一块死色）。
        panel.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#243052"), 0),
                    new GradientStop(Color.Parse("#141C2E"), 0.5),
                    new GradientStop(Color.Parse("#080C14"), 1)
                }
            }
        });

        // 第二层：斜向掠光。宽度给足，靠外层 ClipToBounds 裁掉，这样旋转后两头不会出现硬边。
        panel.Children.Add(new Border
        {
            Width = 420,
            Height = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("rotate(22deg)"),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.46),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.62),
                    new GradientStop(Color.FromArgb(16, tint.R, tint.G, tint.B), 0.8),
                    new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1)
                }
            }
        });

        // 第三层：内框。外框里再收一层 1px 的浅线，卡面才不像一块糊上去的色块。
        panel.Children.Add(new Border
        {
            Margin = new Thickness(7),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            IsHitTestVisible = false
        });

        // 第四层：中央徽记。光环 + 斜放的方框 + ◈，三层同心。
        var emblem = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        emblem.Children.Add(new Border
        {
            Width = 92,
            Height = 92,
            CornerRadius = new CornerRadius(46),
            Background = new SolidColorBrush(Color.FromArgb(26, tint.R, tint.G, tint.B)),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 0,
                Blur = 34,
                Spread = 1,
                Color = Color.FromArgb(120, tint.R, tint.G, tint.B)
            })
        });
        emblem.Children.Add(new Border
        {
            Width = 62,
            Height = 62,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, tint.R, tint.G, tint.B)),
            Background = new SolidColorBrush(Color.FromArgb(18, tint.R, tint.G, tint.B)),
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("rotate(45deg)")
        });
        emblem.Children.Add(new TextBlock
        {
            Text = "◈",
            FontSize = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(240, 232, 240, 255))
        });
        panel.Children.Add(emblem);

        panel.Children.Add(BuildCornerTicks(tint, inset: 13, size: 11, alpha: 130));

        // 序号放左上角：整排盖着的时候要能对上是第几张。
        var index = new Border
        {
            Margin = new Thickness(13, 12, 0, 0),
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(150, 8, 12, 20)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, tint.R, tint.G, tint.B)),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 214, 228, 250))
            }
        };
        panel.Children.Add(index);

        return panel;
    }

    /// <summary>
    /// 卡面：满幅图 + 底部压暗 + 顶部色带 + 编号牌 + 四角刻线。
    ///
    /// 顶部那道色带是这张图自己的颜色（<see cref="ReadCardColor"/>）——它说的只是「这张卡偏什么色」，
    /// 不表示这张卡更好或更差。图读不出来时如实说明，不拿一张空白冒充。
    /// </summary>
    private static Control BuildCardFace(Bitmap? bitmap, int number, Color tint)
    {
        var panel = new Panel { IsHitTestVisible = false };

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
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#E5A3A3"))
            });
        }

        // 底部压暗：不加的话，亮图的下缘会把编号牌和「第几张」吃掉。
        panel.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 4, 7, 12), 0),
                    new GradientStop(Color.FromArgb(0, 4, 7, 12), 0.52),
                    new GradientStop(Color.FromArgb(210, 4, 7, 12), 1)
                }
            }
        });

        // 顶部色带：这张卡自己的颜色。
        panel.Children.Add(new Border
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(235, tint.R, tint.G, tint.B), 0),
                    new GradientStop(Color.FromArgb(150, tint.R, tint.G, tint.B), 0.55),
                    new GradientStop(Color.FromArgb(40, tint.R, tint.G, tint.B), 1)
                }
            }
        });

        // 内衬细线：与卡背同一套「外框里再收一层」，翻过来时不至于像换了一张卡。
        panel.Children.Add(new Border
        {
            Margin = new Thickness(7),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            IsHitTestVisible = false
        });

        panel.Children.Add(BuildCornerTicks(tint, inset: 13, size: 11, alpha: 200));

        var plate = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(11, 0, 0, 11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        plate.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(235, tint.R, tint.G, tint.B)),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#080C14"))
            }
        });
        plate.Children.Add(new TextBlock
        {
            Text = "候选",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(210, 226, 236, 250))
        });
        panel.Children.Add(plate);

        return panel;
    }

    /// <summary>四角刻线：只画两条边的 L 形短线。有它卡面立刻「像一张卡」而不是一块图。</summary>
    private static Control BuildCornerTicks(Color tint, double inset, double size, byte alpha)
    {
        var panel = new Panel { IsHitTestVisible = false };
        var brush = new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
        var margin = new Thickness(inset);
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(1.4, 1.4, 0, 0), BorderBrush = brush,
            CornerRadius = new CornerRadius(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(0, 1.4, 1.4, 0), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(1.4, 0, 0, 1.4), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(0, 0, 1.4, 1.4), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        });
        return panel;
    }

    /// <summary>
    /// 卡片的投影。两层：一层往下的落地影（让卡片「坐在台面上」而不是浮在色块里），
    /// 一层本色微光；选中时把本色光换成更强的一圈。
    ///
    /// 为什么不给整排卡加一块大柔光当台面：试过。`BoxShadow` 会把**形状内部**也填上颜色，
    /// 一大块圆角矩形叠在深底上就变成一块灰疙瘩，比不加还难看。要让卡浮起来只能一张一张地投影。
    /// </summary>
    private static BoxShadows CardShadows(Color tint, bool selected) => selected
        ? new BoxShadows(
            new BoxShadow
            {
                OffsetX = 0, OffsetY = 18, Blur = 34, Spread = -10,
                Color = Color.FromArgb(180, 0, 0, 0)
            },
            new[]
            {
                new BoxShadow
                {
                    OffsetX = 0, OffsetY = 0, Blur = 46, Spread = 1,
                    Color = Color.FromArgb(205, tint.R, tint.G, tint.B)
                }
            })
        : new BoxShadows(
            new BoxShadow
            {
                OffsetX = 0, OffsetY = 12, Blur = 26, Spread = -12,
                Color = Color.FromArgb(160, 0, 0, 0)
            },
            new[]
            {
                new BoxShadow
                {
                    OffsetX = 0, OffsetY = 0, Blur = 20, Spread = -7,
                    Color = Color.FromArgb(80, tint.R, tint.G, tint.B)
                }
            });

    /// <summary>
    /// 卡片描边：**左上亮、右下暗**的斜向渐变。平涂一圈同色看起来像个塑料框，
    /// 而斜向的明暗一变，边框立刻有金属边的意思——二游卡框基本上都是这个做法。
    /// </summary>
    private static IBrush CardBorder(Color tint, bool selected)
    {
        var light = Mix(tint, Colors.White, selected ? 0.62 : 0.3);
        var deep = selected ? tint : Mix(tint, Color.Parse("#0A1120"), 0.45);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(light, 0),
                new GradientStop(tint, 0.42),
                new GradientStop(deep, 1)
            }
        };
    }

    /// <summary>把两个颜色按 t 混合（0 = a，1 = b）。</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>结果标题：节点名 + 这一批几张。卡片出现时一起浮出来。</summary>
    private static Control BuildResultHeader(string nodeTitle, int count, Color accent)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        stack.Children.Add(new TextBlock
        {
            Text = nodeTitle,
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E8EFFA"))
        });

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        line.Children.Add(Rule(accent, flip: false));
        line.Children.Add(new TextBlock
        {
            Text = $"共 {count} 张 · 点一张收进节点",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD"))
        });
        line.Children.Add(Rule(accent, flip: true));
        stack.Children.Add(line);
        return stack;
    }

    /// <summary>标题两侧那两小段渐隐横线。</summary>
    private static Control Rule(Color accent, bool flip)
    {
        var stops = new GradientStops
        {
            new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(170, accent.R, accent.G, accent.B), 1)
        };
        if (flip) stops = new GradientStops
        {
            new GradientStop(Color.FromArgb(170, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
        };
        return new Border
        {
            Width = 54,
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = stops
            }
        };
    }

    // ==================== 许愿与光点 ====================

    /// <summary>许愿区：自有形象（那枚 ◈ 标记 + 一圈光）+ 厂家徽标 + 这一批的实情。</summary>
    private static Control BuildWishStage(
        string nodeTitle, int count, string badgeAbbreviation, Color accent, string sourceLine,
        out Border mark, out Border markRing)
    {
        markRing = new Border
        {
            Width = 112,
            Height = 112,
            CornerRadius = new CornerRadius(56),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 0,
                Blur = 52,
                Spread = 4,
                Color = Color.FromArgb(150, accent.R, accent.G, accent.B)
            }),
            IsHitTestVisible = false
        };
        // 环里再套一个斜放的小方框，与卡背上的徽记是同一套语言。
        var ringBody = new Grid();
        ringBody.Children.Add(new Border
        {
            Width = 62,
            Height = 62,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("rotate(45deg)")
        });
        ringBody.Children.Add(new TextBlock
        {
            Text = "◈",
            FontSize = 42,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(238, 230, 238, 255))
        });
        markRing.Child = ringBody;

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
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(12, 4),
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
            Margin = new Thickness(0, 4, 0, 22),
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
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Blur = 18,
                    Spread = 3,
                    Color = Color.FromArgb(200, accent.R, accent.G, accent.B)
                }),
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

    // ==================== 动画零件 ====================

    /// <summary>
    /// 翻一张卡：先横向压扁（背面转到侧面对你的那一瞬间），压到最扁时把内容换成正面的图，再展开回来，
    /// 翻完让那张卡的闪光层亮一下再化掉。
    ///
    /// 分两步是因为过渡只负责插值，中间插不进一个「换内容」的回调。压到 0.08 而不是 0：
    /// 完全压成 0 会在那一帧看不见任何东西，反而像闪了一下；留一点宽度，翻转的连续感就出来了。
    /// </summary>
    private static void FlipCard(Border card, Grid inner, Border flash, Control face, int DelayMs)
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
            inner.Children[0] = face;
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

            // 闪光：**先无过渡地点亮，再挂上过渡往 0 收**。反过来的话（先挂过渡再点亮）
            // 那 0 → 0.9 也会被当成一段动画，一闪的效果就没了。
            flash.Opacity = 0.85;
            flash.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(520),
                    Easing = new CubicEaseOut()
                }
            };
            flash.Opacity = 0;
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

    // ==================== 读图 ====================

    /// <summary>
    /// 这一批卡的卡面比例（**高 / 宽**）：取这批图自己的高宽比（取中位数），再收进一个上下限。
    ///
    /// 为什么要按图定、而不是写死一个「像卡」的竖比例：出图多是方的或横的，硬套竖卡
    /// 就会把两侧裁掉一大半，而挑图的时候恰恰要看全——裁掉的正好是边上的构图。
    /// 让卡跟着图走，就不必裁，卡也仍然是「一张卡」的样子。
    /// 上限 1.5 保瘦长、下限 0.62 让横图也能成卡（那时它就是一张横的缩略图，这是对的）。
    ///
    /// 注意方向：这里是**高 / 宽**，所以横图得到的是小于 1 的值。搞反了会让 16:9 的图
    /// 被套进一张竖卡里，然后被裁掉一大半——比写死比例还糟。
    /// </summary>
    private static double CardHeightRatioFor(IReadOnlyList<Bitmap?> faces)
    {
        var ratios = new List<double>();
        foreach (var face in faces)
        {
            if (face is null || face.PixelSize.Width <= 0 || face.PixelSize.Height <= 0) continue;
            ratios.Add((double)face.PixelSize.Height / face.PixelSize.Width);
        }
        if (ratios.Count == 0) return FallbackCardAspect;
        ratios.Sort();
        return Math.Clamp(ratios[ratios.Count / 2], 0.62, 1.5);
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

    /// <summary>
    /// 这张卡自己的颜色：另解一张 24 宽的缩略图，按色相分 12 桶，取权重最大的那一桶的色相，
    /// 再按固定的饱和度与亮度画出来。
    ///
    /// 为什么不直接用平均色：照片的平均色几乎都往灰里掉，画成色带就是一条脏灰色，还不如没有。
    /// 所以要的是「主色相」而不是「平均色」。为什么不用 480 那张正面图：读它的像素要整张拷进内存
    /// （4K 图就是几十 MB），而「这张图偏什么色」24×24 已经够用。
    ///
    /// 读不出来（解码失败、格式不认识）时退回厂家色，不显示一个假颜色。
    /// </summary>
    private static Color ReadCardColor(string path, Color fallback)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var tiny = Bitmap.DecodeToWidth(stream, 24);
            var size = tiny.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return fallback;

            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                tiny.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // 解码出来是 Bgra8888，所以字节序是 B,G,R,A。
            var weight = new double[12];
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                double b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max < 40) continue;                 // 近黑不参与
                var saturation = (max - min) / max;
                if (saturation < 0.15) continue;        // 灰阶不参与：它们投向哪一桶都是噪声
                var bucket = (int)(Hue(r, g, b, max, min) / 30) % 12;
                // 越饱和的像素越能代表这张卡的颜色，所以按饱和度的平方加权。
                weight[bucket] += saturation * saturation;
            }

            var best = -1;
            var bestWeight = 0.0;
            for (var i = 0; i < 12; i++)
            {
                if (weight[i] > bestWeight)
                {
                    bestWeight = weight[i];
                    best = i;
                }
            }
            if (best < 0) return fallback;

            // 取桶中心的色相，饱和度与亮度固定：这样每张卡都拿得到一个「像样」的颜色，
            // 而不是原图那口发暗发灰的平均色。
            return FromHsv(best * 30.0 + 15.0, 0.62, 0.88);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or OverflowException)
        {
            return fallback;
        }
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        var delta = max - min;
        if (delta <= 0) return 0;
        double hue;
        if (max == r) hue = ((g - b) / delta) % 6;
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        double r, g, b;
        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
