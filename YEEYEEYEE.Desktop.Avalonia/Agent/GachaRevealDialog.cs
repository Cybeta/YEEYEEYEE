using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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

/// <summary>
/// 揭晓里的一张卡：序号与画布上那排的编号一致，路径是已经落盘的那张图。
///
/// <see cref="Quality"/> 是这一格的档位。**null 不等于白档**：前者是「没判过」，
/// 界面上就是原来那副样子（四个光点、本色光晕）；后者是**判出来的最低档**，光点更少、光晕更暗。
/// 两者混起来等于凭空给了一张白卡。
/// </summary>
internal sealed record GachaCard(int Index, string Path, SlotQuality? Quality);

/// <summary>
/// 出图「开奖」的全屏揭晓。设置里那个开关打开后才会有这条路。
///
/// 流程：自有形象许愿 → 光点飞出 → 卡片依次翻面 → 挑一张收进节点。
///
/// **它是用户主动点开的一层，不是自己弹出来的**。这一点是它能全屏的前提：
/// 早先否决过「出图完自动弹出大图」，理由是选择权在用户看到之前就被替他做完了；
/// 而这里是他自己点的卡片，看多久、要不要挑、要不要先不看，都由他。
///
/// 五条刻意的设计取舍：
/// · **卡面是黑白的，颜色全在卡背后那团光里。** 卡片本身只有深浅灰：一层卡纸、一圈细描边、
///   中央一枚自有徽记。区分六张卡靠的是每张卡背后那团**缓缓流动**的光晕，它的颜色取这张图自己的主色相。
///   这么分是有道理的：卡面一旦上色（上一版就是这么干的：彩色描边、彩色色带、彩色角标），
///   它就和自己要展示的那张图抢眼，看着"花"而不是"好"；把颜色挪到卡背后之后，
///   卡片安静下来，而颜色反而成了更好用的线索（扫一眼光就知道哪张是哪张）。
/// · **形象是自有的**（那个 ◈ 标记 + 厂家缩写），不用任何厂家的 logo 或拟人形象——那些是各自的商标与
///   著作权作品，且拿了会让人以为有官方合作。厂家只体现为「两个字母 + 一个区分色」，与
///   <see cref="ProviderBadges"/> 是同一份数据。
/// · **光点数量暂时统一**，不按「金 / 紫 / 蓝」分档：质量判据还没定（TODO 第 8 条），
///   凭空分档等于在界面上宣布一个不存在的评级。等判据定了，只改 <see cref="SparkCount"/> 的来源。
/// · **许愿是仪式，不是因果**：图在这一层打开之前就已经全出好了。所以文案只陈述事实
///   （「这一批 N 张已出好」），不写「许愿会影响出图」这类不成立的话。
/// · **光晕的颜色是「这张卡偏什么色」，不是「这张卡更好」**，所以它不承担任何评级含义
///   （评级是待办第 8 条的事）。
/// </summary>
internal static class GachaRevealDialog
{
    /// <summary>
    /// **没判过档位**时用几个光点。判过的那几档各有各的数量（见 <see cref="QualityJudgement.SparksFor"/>），
    /// 白档比这个数还少——白是判出来的最低档，而这个是「不知道」，两者不该长得一样。
    /// </summary>
    private const int UngradedSparks = 4;

    /// <summary>向中心收拢的尘埃数量。它是**装饰**，不表示任何档位（带档位含义的是每张卡那一组光点）。</summary>
    private const int GatherCount = 14;

    /// <summary>尘埃从离中心多远的地方开始收（像素）。</summary>
    private const double GatherRadius = 340;

    /// <summary>尘埃往中心飞多久。</summary>
    private const int GatherFlyMs = 620;

    /// <summary>蓄势几拍之后爆发（毫秒）：光柱撑开 + 一圈白光 + 全屏一闪，许愿区也是在这一刻开始淡出。</summary>
    private const int BurstMs = 820;

    /// <summary>爆发之后多久，卡片从中心飞出来。</summary>
    private const int CardsFlyAtMs = 1250;

    /// <summary>一张卡飞到位要多久。</summary>
    private const int CardFlyMs = 520;

    /// <summary>相邻两张卡飞出的错开量。</summary>
    private const int CardFlyStaggerMs = 90;

    /// <summary>整手牌落位之后再等一拍，才开始翻面（留点时间让人看清这一手）。</summary>
    private const int SettleHoldMs = 200;

    /// <summary>相邻两张卡翻面的错开量：既有「一张张揭晓」的节奏，整排也不至于等太久。</summary>
    private const int FlipStaggerMs = 170;

    /// <summary>翻面的前半程（转到侧面对着你的那一瞬间）。</summary>
    private const int FlipHalfMs = 110;

    /// <summary>翻面的后半程（从侧面展开成正对）。</summary>
    private const int FlipBackMs = 170;

    /// <summary>光晕比卡片每边多出多少。少了看不出「卡背后有光」，多了会糊到邻居身上。</summary>
    private const double HaloBleed = 52;

    /// <summary>
    /// 打开全屏揭晓。返回被选中的那一张的序号；用户没挑就关掉时返回 null
    /// （**不等于失败**：那一排卡还在画布上盖着，可以再点开）。
    /// </summary>
    /// <param name="avatar">
    /// 这一家的形象（你自己放进 `provider-art` 的那张图）。**null 就用自绘的 ◈ 徽记**——
    /// 形象是锦上添花，缺了它开奖照样完整。
    /// </param>
    public static async Task<int?> ShowAsync(
        Window owner,
        string nodeTitle,
        IReadOnlyList<GachaCard> cards,
        string badgeAbbreviation,
        string badgeColorHex,
        string sourceLine,
        Bitmap? avatar = null)
    {
        if (cards.Count == 0) return null;

        var accent = Color.Parse(badgeColorHex);

        // 先把这几张图都解出来：卡面的比例要按**这批图自己的**比例定，见 CardHeightRatioFor。
        var faceBitmaps = new Bitmap?[cards.Count];
        for (var i = 0; i < cards.Count; i++) faceBitmaps[i] = LoadFace(cards[i].Path);

        // 排列与卡片尺寸都走 GachaCardLayout：那份规则是纯计算、有回归用例钉着，这里只负责摆。
        var perRow = GachaCardLayout.PerRow(cards.Count);
        var rows = GachaCardLayout.Rows(cards.Count);
        var cardWidth = GachaCardLayout.Width(cards.Count);
        var cardHeight = GachaCardLayout.Height(cards.Count);
        var gap = GachaCardLayout.Gap;
        var rowWidth = GachaCardLayout.RowWidth(cards.Count);

        // 每张卡背后那团光的颜色：另解一张很小的缩略图，按色相分桶取最主要的那个色相。
        // 解 24 宽而不是从正面的 480 宽那张里抽：480 那张要全量拷进内存才读得到，
        // 而「这张图偏什么色」这件事，24×24 已经足够，且几乎不花时间。
        var tints = new Color[cards.Count];
        for (var i = 0; i < cards.Count; i++) tints[i] = ReadCardColor(cards[i].Path, accent);

        // 爆发那一下的光取这批图的主色相（见 BatchLight）。
        var burstLight = BatchLight(tints, accent);

        // 预兆：这一批里**最高的那一档**决定爆发那一下的强弱——它只说「这批里有最好的那一档」，
        // 不说是哪一张，所以不会提前泄露结果。二游就是这么干的，区别是这里的光**有依据**（档位是真判出来的）。
        // **裂纹卡不参与**：踩中负面提示词的图，分数再高也不该让这一批的入场变成最烈的那种。
        // 全是裂纹卡时按**白档**给（最弱的预兆），而不是当成「没判过」——一批明确坏掉的图
        // 与一批没判过的图，不该长得一样。
        // 一格都没判过时（None）用中性的强度与颜色，与以前完全一样。
        QualityTier? omen = null;
        var gradedAny = false;
        foreach (var card in cards)
        {
            if (card.Quality is not { Source: not QualitySource.None } graded) continue;
            gradedAny = true;
            if (graded.IsCracked) continue;
            if (omen is null || graded.Tier > omen.Value) omen = graded.Tier;
        }
        if (gradedAny && omen is null) omen = QualityTier.White;
        var omenSparks = omen is { } omenTier ? QualityJudgement.SparksFor(omenTier) : UngradedSparks;
        var omenPunch = omen switch
        {
            QualityTier.Gold => 1.3,
            QualityTier.Red => 1.15,
            QualityTier.Purple => 1.0,
            QualityTier.Blue => 0.88,
            QualityTier.White => 0.74,
            _ => 1.0
        };

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

        // ---- 舞台：许愿区、尘埃、光点、卡片区叠在同一块地方，靠不透明度交接 ----
        var wishStage = BuildWishStage(nodeTitle, cards.Count, badgeAbbreviation, accent, sourceLine, avatar,
            out var mark, out var markRing);
        // 爆发时的光点：从中心朝上扇开。数量由**预兆**决定（这一批最高那一档）。
        var sparkField = BuildSparkField(burstLight, omenSparks, out var sparks);
        // 蓄势时的尘埃：从四周往中心收。它是装饰，数量不带含义。
        var dustField = BuildDustField(burstLight, out var dust);
        // 爆发那一下：竖着的光柱 + 一圈白光。**预兆的强弱就落在这两团光的尺寸与亮度上。**
        var pillar = BuildEllipticalGlow(burstLight, 230 * omenPunch, cardHeight * 3.0 * omenPunch,
            peak: (byte)Math.Clamp(210 * omenPunch, 60, 255), mid: (byte)Math.Clamp(120 * omenPunch, 40, 255));
        pillar.RenderTransform = TransformOperations.Parse("scale(1,0.22)");
        var bloom = BuildEllipticalGlow(Colors.White, cardWidth * 3.6, cardWidth * 3.6,
            peak: (byte)Math.Clamp(200 * omenPunch, 60, 255), mid: 70);

        // 每行一个水平面板，行与行在竖直方向排开。末行张数少时也居中（5 张 = 上 3 下 2）。
        var cardGrid = new StackPanel
        {
            Spacing = GachaCardLayout.RowGap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rowOf = new List<StackPanel>();
        for (var r = 0; r < rows; r++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = gap,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rowOf.Add(row);
            cardGrid.Children.Add(row);
        }

        // 卡排下面一条舞台边线：不给台面一点交代的话，六张卡会像浮在空处。
        var floor = new Border
        {
            Width = rowWidth + 150,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(46, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            }
        };

        var cardColumn = new StackPanel
        {
            Spacing = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { cardGrid, floor }
        };

        // 一个格子 = 一团光晕 + 一张卡 + 一层「选中时加亮的光晕」。三者叠在一起，
        // 格子本身的尺寸就是卡片的尺寸，光晕超出格子向外溢（没有祖先裁剪它），
        // 所以六团光会在边缘互相渗一点——那正是想要的「流动」感。
        var cells = new List<Grid>();
        var flyers = new List<Grid>();
        var frames = new List<Border>();
        var cardSparkSets = new List<List<Border>>();
        var halos = new List<Border>();
        var haloBoosts = new List<Border>();
        var faces = new List<Control>();
        var inners = new List<Grid>();
        var flashes = new List<Border>();
        // 「这一批翻完了没有」。声明在卡片之前：点击与按钮状态都读它，
        // 而 C# 里被捕获的局部变量必须先声明后使用（lambda 是按书写位置编译的）。
        var flipsDone = false;
        for (var i = 0; i < cards.Count; i++)
        {
            var tint = tints[i];
            var quality = cards[i].Quality;
            var back = BuildCardBack(i + 1, badgeAbbreviation);
            var face = BuildCardFace(faceBitmaps[i], i + 1, quality, cardWidth, cardHeight);
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
                        new GradientStop(Color.FromArgb(190, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.45),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
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
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = CardEdge(selected: false),
                Background = new SolidColorBrush(Color.Parse("#0A0C10")),
                BoxShadow = CardShadows(selected: false),
                ClipToBounds = true,
                Child = inner,
                Cursor = new Cursor(StandardCursorType.Hand),
                // 翻面是绕中轴压扁再展开，所以变换原点必须在中心；选中时的抬升也用它。
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = TransformOperations.Parse("scale(1,1)")
            };
            frames.Add(frame);

            // 光晕的强度也按档位：金档更亮、白档更暗。它是「档位看得出来」的第一眼信号，
            // 而光点是第二眼（要等翻到它才看得到）。没判过档位时用中性强度。
            var glow = quality is { Source: not QualitySource.None } graded
                ? QualityJudgement.GlowFor(graded.Tier)
                : 1.0;
            var halo = BuildHalo(tint, cardWidth + HaloBleed * 2, cardHeight + HaloBleed * 2, strength: glow);
            halo.RenderTransformOrigin = RelativePoint.Center;
            halos.Add(halo);
            var boost = BuildHalo(tint, cardWidth + HaloBleed * 2, cardHeight + HaloBleed * 2,
                brighter: true, strength: glow);
            boost.Opacity = 0;
            haloBoosts.Add(boost);

            // 每张卡自己那组光点：翻到它的时候才飞出来，数量按它的档位。
            var cardSparkField = BuildCardSparkField(tint, quality, out var cardSparks);
            cardSparkSets.Add(cardSparks);

            // 两层壳，各管一件事（一层壳上挂两段动效会互相打架）：
            // · flyer —— 卡片从中心飞出来：只动它的 translate 与 opacity；
            // · dimmer —— 选中时把「卡 + 它背后那团光」一起压暗：只动它的 opacity。
            // 卡片自己是摆在 (col,row) 上的，所以「从中心飞出来」= 先把它挪到中心的相反方向
            // （-dx,-dy），再回到 0。
            // 位置与「从中心飞出来」的起点都由 GachaCardLayout 算——末行张数少时按它自己的张数居中。
            var rowIndex = GachaCardLayout.Slot(cards.Count, i).Row;
            var (offsetX, offsetY) = GachaCardLayout.OffsetFromCentre(cards.Count, i);

            var dimmer = new Grid { Width = cardWidth, Height = cardHeight };
            dimmer.Children.Add(halo);
            dimmer.Children.Add(boost);
            dimmer.Children.Add(frame);
            // 光点放在卡**之外**（dimmer 不裁剪，卡片自己有 ClipToBounds），
            // 否则飞出去的那一段会被卡片自己裁掉。
            dimmer.Children.Add(cardSparkField);
            cells.Add(dimmer);

            var flyer = new Grid
            {
                Width = cardWidth,
                Height = cardHeight,
                Opacity = 0,
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = TransformOperations.Parse($"translate({-offsetX:0.#}px,{-offsetY:0.#}px)")
            };
            flyer.Children.Add(dimmer);
            flyers.Add(flyer);

            var captured = i;
            frame.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (!flipsDone) return;     // 还没翻完先不接受选择：这时候看到的还不是图
                ApplySelection(captured);
            };
            // 鼠标扫过时这一格抬起来：一整排等亮的卡看着像「六张图」，
            // 扫过会动的那一排才像「可以挑的卡」。
            frame.PointerEntered += (_, _) =>
            {
                if (!flipsDone || selected == captured) return;
                frame.RenderTransform = TransformOperations.Parse("scale(1.045)");
                dimmer.Opacity = 0.8;
            };
            frame.PointerExited += (_, _) =>
            {
                if (!flipsDone || selected == captured) return;
                frame.RenderTransform = TransformOperations.Parse("scale(1,1)");
                dimmer.Opacity = selected is null ? 1 : 0.45;
            };
            rowOf[rowIndex].Children.Add(flyer);
        }

        var stage = new Grid { Children = { bloom, pillar, wishStage, dustField, sparkField, cardColumn } };

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        body.Children.Add(new Panel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { stage }
        });
        // 全部揭晓之后，画面上只剩一排卡和底部几个按钮，会显得「上面空着」。
        // 顶上补一行结果标题（节点名 + 这一批几张），与卡排一起浮出来。
        var resultHeader = BuildResultHeader(nodeTitle, cards.Count, omen);
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
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(11, 226, 236, 255), 0.44),
                    new GradientStop(Color.FromArgb(8, 190, 210, 245), 0.58),
                    new GradientStop(Color.FromArgb(0, 150, 180, 220), 1)
                }
            }
        };

        // 爆发那一瞬间的**全屏一闪**。二游基本都有这一下：屏幕白一下，把「抽到了」这件事砸实。
        // 峰值压在 0.34——再亮就刺眼了，而且全屏、无边框，看久了很难受。
        var whiteout = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0,
            Background = new SolidColorBrush(Colors.White)
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
        surface.Children.Add(whiteout);

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

        // 所有卡都停在同一条时间线上：翻面结束之后才允许挑。选中的那一格再抬起来、
        // 其余压暗——「挑一张」这件事在画面上必须有主次，六张等亮是看不出选没选的。
        //
        // 分拍（这是二游抽卡动画的骨架，但每一拍都用自绘的东西做）：
        // ① 蓄势：尘埃从四周往中心收，徽记在呼吸
        // ② 爆发：光柱撑开 + 一圈白光 + 全屏一闪，许愿区淡出
        // ③ 卡片从中心飞出来落位
        // ④ 依次翻面
        // ⑤ 全部揭晓，这时才能挑
        var cardsAt = CardsFlyAtMs;
        var flipsAt = cardsAt + (frames.Count - 1) * CardFlyStaggerMs + CardFlyMs + SettleHoldMs;
        // +40：把「全部揭晓」排在**最后一张的光点射出来之后**。两张定时器的到点时刻一样时，
        // 先建的先跑，而 FinishAll 会把 flipsDone 置真、后建的那组光点就被自己那道守卫拦掉了——
        // 于是最后一张永远不放光点。留 40 毫秒的缝就没有这个问题。
        var allDoneAt = flipsAt + (frames.Count - 1) * FlipStaggerMs + FlipHalfMs + FlipBackMs + 40;

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
                haloBoosts[i].Transitions = null;
                flyers[i].Transitions = null;
                flyers[i].Opacity = 1;
                flyers[i].RenderTransform = TransformOperations.Parse("translate(0px,0px)");
                cells[i].Opacity = selected is null ? 1 : (selected == i ? 1 : 0.45);
            }
            wishStage.Opacity = 0;
            dustField.Opacity = 0;
            sparkField.Opacity = 0;
            pillar.Opacity = 0;
            bloom.Opacity = 0;
            whiteout.Opacity = 0;
            ambient.Opacity = 1;
            resultHeader.Opacity = 1;
            cardColumn.Opacity = 1;
            skip.IsVisible = false;
            flipsDone = true;              // 先允许选择，再让 ApplySelection 把过渡接上
            ArmInteractions();
            footerHint.Text = "点一张挑走，再点「用这一张」";
        }

        /// <summary>把「选中要动的那几项」接上过渡：抬升、压暗、加亮的光晕都要有动画才不跳。</summary>
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
                    }
                };
                cells[i].Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(220),
                        Easing = new CubicEaseOut()
                    }
                };
                // 选中那层光晕：加亮靠它，所以它自己不能再被别的动画占着 Opacity，否则改不动。
                haloBoosts[i].Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(240),
                        Easing = new CubicEaseOut()
                    }
                };
            }
        }

        // ① 蓄势：徽记落位 + 呼吸；尘埃从四周往中心收。
        At(0, () =>
        {
            mark.Opacity = 1;
            mark.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            markRing.Opacity = 1;
            StartFloat(mark, seconds: 2.6);
        });
        At(60, () =>
        {
            dustField.Opacity = 1;
            for (var i = 0; i < dust.Count; i++)
            {
                var fleck = dust[i];
                var delay = i * 22;
                // 尘埃是「从外面被吸进来」的：起点已经在外面了（建的时候就摆好了），
                // 这里只负责接上过渡、把它送回中心。缓动用 EaseIn —— 越靠近中心越快，
                // 才有被吸过去的感觉；用 EaseOut 会变成「滑进来」。
                fleck.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(GatherFlyMs),
                        Delay = TimeSpan.FromMilliseconds(delay),
                        Easing = new CubicEaseIn()
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(260),
                        Delay = TimeSpan.FromMilliseconds(delay),
                        Easing = new CubicEaseOut()
                    }
                };
                fleck.Opacity = 1;
                fleck.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            }
        });

        // ② 爆发。
        At(BurstMs, () =>
        {
            wishStage.Opacity = 0;

            // 光柱：先「无过渡」地点亮、定住起点，再挂上过渡往终点走。
            // 反过来的话（先挂过渡再设起点）连起点都会被当成一段动画。
            dustField.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(200),
                    Easing = new CubicEaseIn()
                }
            };
            dustField.Opacity = 0;

            pillar.Opacity = 1;
            pillar.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(460),
                    Easing = new CubicEaseOut()
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(320),
                    Delay = TimeSpan.FromMilliseconds(240),
                    Easing = new CubicEaseIn()
                }
            };
            pillar.RenderTransform = TransformOperations.Parse("scale(1,1.5)");
            pillar.Opacity = 0;

            // 一圈白光：亮一下就化掉，顺带放大一圈。
            bloom.Opacity = 0.85;
            bloom.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(420),
                    Easing = new CubicEaseOut()
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(480),
                    Easing = new CubicEaseOut()
                }
            };
            bloom.RenderTransform = TransformOperations.Parse("scale(1.35)");
            bloom.Opacity = 0;

            // 全屏一闪。
            whiteout.Opacity = 0.34;
            whiteout.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(230),
                    Easing = new CubicEaseOut()
                }
            };
            whiteout.Opacity = 0;

            // 四个光点朝上散开。
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

        // ③ 卡片从中心飞出来落位。
        At(cardsAt, () =>
        {
            sparkField.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(260),
                    Easing = new CubicEaseOut()
                }
            };
            sparkField.Opacity = 0;
            ambient.Opacity = 1;
            resultHeader.Opacity = 1;
            cardColumn.Opacity = 1;
            for (var i = 0; i < flyers.Count; i++)
            {
                var flyer = flyers[i];
                var delay = i * CardFlyStaggerMs;
                flyer.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(CardFlyMs),
                        Delay = TimeSpan.FromMilliseconds(delay),
                        // 落位要有「砸到位」的感觉：Back 系缓动会先冲过头再收回来。
                        Easing = new BackEaseOut()
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(CardFlyMs / 2),
                        Delay = TimeSpan.FromMilliseconds(delay),
                        Easing = new CubicEaseOut()
                    }
                };
                flyer.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
                flyer.Opacity = 1;
            }
            // 光晕从这一刻开始缓缓流动：每张的周期不一样、起始方向也交替，
            // 所以六团光不会同步呼吸——同步就成了一整块背景，那样反而分不出哪团光属于哪张卡。
            for (var i = 0; i < halos.Count; i++)
                StartDrift(halos[i], seconds: 3.2 + i * 0.55, startHigh: i % 2 == 1);
        });

        // ④ 依次翻面；每张翻到位的那一刻，把它自己那组光点射出来（数量按它的档位）。
        At(flipsAt, () =>
        {
            for (var i = 0; i < frames.Count; i++)
            {
                FlipCard(frames[i], inners[i], flashes[i], faces[i], DelayMs: i * FlipStaggerMs);

                var set = cardSparkSets[i];
                if (set.Count == 0) continue;
                var landedAt = flipsAt + i * FlipStaggerMs + FlipHalfMs + FlipBackMs;
                ScheduleOnce(landedAt, () =>
                {
                    // 已经「跳过动画」了就别再冒出来——否则跳完之后还会零星炸几下。
                    if (flipsDone) return;
                    BurstSparks(set);
                }).Start();
            }
        });
        At(allDoneAt, FinishAll);

        // ---- 交互 ----
        /// <summary>
        /// 选中那一行说明。判过档位的把**分数、档位、谁判的、踩中了哪条负面词、以及模型自己那句话**
        /// 一起写出来——只说「金卡」等于把模型的判断当成事实；把原话与分数摆出来，
        /// 用户才能判断这个判断值不值得信。
        /// </summary>
        string DescribeSelection(int index)
        {
            var quality = cards[index].Quality;
            if (quality is not { Source: not QualitySource.None } graded)
                return $"已选中第 {index + 1} 张（共 {cards.Count} 张）";

            var who = graded.Source == QualitySource.Model ? "模型判定" : "本地判定";
            var grade = graded.ScoreLabel.Length > 0
                ? $"{QualityJudgement.Label(graded.Tier)}档 {graded.ScoreLabel}"
                : $"{QualityJudgement.Label(graded.Tier)}档";
            var header = $"第 {index + 1} 张 · {grade}（{who}）";

            // 裂纹卡把踩中的负面词**逐条列出来**（卡面那枚小牌只放得下几个字）：
            // 「踩中负面提示词」是用户自己下的判断标准，所以要说清是踩了哪一条。
            if (graded.IsCracked) header += $" · 裂纹，踩中负面词：{QualityJudgement.DescribeHits(graded.NegativeHits)}";
            return graded.Reason.Length > 0 ? header + "：" + graded.Reason : header;
        }

        // 选中状态只有这一处计算：点击与键盘都走它，免得两处各写一份、慢慢长出不一致。
        void ApplySelection(int index)
        {
            if (index < 0 || index >= frames.Count) return;
            selected = index;
            for (var k = 0; k < frames.Count; k++)
            {
                var on = k == index;
                frames[k].BorderThickness = new Thickness(on ? 2.5 : 1);
                frames[k].BorderBrush = CardEdge(on);
                frames[k].BoxShadow = CardShadows(on);
                frames[k].RenderTransform = TransformOperations.Parse(on ? "scale(1.06)" : "scale(1,1)");
                // 压暗压的是**整格**（卡 + 它背后那团光），不是只压卡：
                // 只压卡的话，没被选中的那几张背后的光还是满亮，画面照样是六团一样亮的光。
                cells[k].Opacity = on ? 1 : 0.45;
                haloBoosts[k].Opacity = on ? 0.85 : 0;
            }
            confirm.IsEnabled = true;
            footerHint.Text = DescribeSelection(index);
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
    /// 卡背：一层深灰卡纸 + 内衬细线 + 中央自有徽记 + 底下一行厂家缩写 + 左上角序号。
    ///
    /// **它是黑白的。** 卡背是整排盖着时唯一看得见的东西，如果它自己就有颜色，
    /// 那六张卡会先被自己的底色区分一次；而区分是背后那团光该干的事。
    /// </summary>
    private static Control BuildCardBack(int number, string badge)
    {
        var panel = new Panel { IsHitTestVisible = false };

        panel.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#171A20"), 0),
                    new GradientStop(Color.Parse("#0E1015"), 0.55),
                    new GradientStop(Color.Parse("#08090C"), 1)
                }
            }
        });

        // 内衬细线：外框里再收一层，卡面才不像一块糊上去的色块。
        panel.Children.Add(new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            IsHitTestVisible = false
        });

        // 中央：一枚自有的记号。不用任何厂家的 logo（商标与著作权，且会让人以为有官方合作），
        // 厂家只体现为底下那行两字母缩写，与右下角那枚徽标是同一份数据。
        var emblem = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var ring = new Border
        {
            Width = 84,
            Height = 84,
            CornerRadius = new CornerRadius(42),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromArgb(9, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ring.Child = new TextBlock
        {
            Text = "◈",
            FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(226, 233, 237, 244))
        };
        emblem.Children.Add(ring);
        if (badge.Length > 0)
        {
            emblem.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 150, 158, 170))
            });
        }
        panel.Children.Add(emblem);

        panel.Children.Add(BuildCornerTicks(inset: 14, size: 10, alpha: 40));
        panel.Children.Add(BuildIndexChip(number, top: true));
        return panel;
    }

    /// <summary>
    /// 卡面：满幅图，外加一圈黑白收边（底部压暗 + 内衬细线 + 角刻线 + 左下序号）。
    ///
    /// 收边一律是灰的，没有一道彩色——图的颜色归图，卡片不跟它抢。
    /// 图读不出来时如实说明，不拿一张空白冒充。
    /// **踩中负面提示词的那张会裂开**（见 <see cref="BuildCracks"/>），那是这张卡在这一排里最显眼的特征。
    /// </summary>
    private static Control BuildCardFace(Bitmap? bitmap, int number, SlotQuality? quality, double width, double height)
    {
        var panel = new Panel { IsHitTestVisible = false };

        if (bitmap is not null)
        {
            // 满幅：整张卡铺满。**不是 Uniform** ——竖卡里放一张方图，Uniform 会在上下留出两条，
            // 那两条无论拿什么填（试过同一张图放大模糊）都会在接缝处露馅：图上只要有地平线这类
            // 横向结构，就会看成「双地平线」，比裁掉还难看。
            //
            // 代价是方图会裁掉两侧（约三分之一宽）。可以接受，因为**六张裁法完全一样**，
            // 比的是同一把尺子；而收进节点的是没裁过的那份原图，所以裁掉的构图并没丢。
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

        // 底部压暗：不加的话，亮图的下缘会把序号吃掉。
        panel.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 4, 5, 8), 0),
                    new GradientStop(Color.FromArgb(0, 4, 5, 8), 0.6),
                    new GradientStop(Color.FromArgb(190, 4, 5, 8), 1)
                }
            }
        });

        // 裂纹卡：踩中负面提示词的那张在这里裂开——先压暗一层，再叠几道折线。
        // 它必须压在图的**上面**、收边与档位牌的**下面**：裂纹要看得见，但不能把序号和档位盖住。
        if (quality is { IsCracked: true })
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(58, 6, 8, 12)),
                IsHitTestVisible = false
            });
            panel.Children.Add(BuildCracks(width, height));
        }

        panel.Children.Add(new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            IsHitTestVisible = false
        });

        panel.Children.Add(BuildCornerTicks(inset: 14, size: 10, alpha: 90));
        panel.Children.Add(BuildIndexChip(number, top: false));

        // 档位小牌：卡面上**唯一带颜色**的东西。
        // 卡片本身保持黑白（颜色归背后那团光），但档位必须一眼看得见，所以这一枚破例；
        // 写的是「金 9/10」——分数一起写出来，因为档位就是分数切出来的带子，分数才是原始信息。
        // 踩中负面提示词的那张写「裂纹」并换成裂纹色，下面再补一行命中的原词——
        // 光看一个「裂纹」不知道是哪儿裂的，而「哪一条负面词被踩了」正是用户要的答案。
        if (quality is { Source: not QualitySource.None } graded)
        {
            var cracked = graded.IsCracked;
            var label = cracked
                ? (graded.ScoreLabel.Length > 0 ? $"裂纹 {graded.ScoreLabel}" : "裂纹")
                : QualityJudgement.Label(graded.Tier)
                  + (graded.ScoreLabel.Length > 0 ? $" {graded.ScoreLabel}" : string.Empty);
            var tint = cracked ? CrackColor : TierColor(graded.Tier);

            var lines = new StackPanel { Spacing = 1 };
            lines.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(tint)
            });
            if (cracked)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = "命中：" + Trim(QualityJudgement.DescribeHits(graded.NegativeHits), 14),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, tint.R, tint.G, tint.B))
                });
            }

            panel.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(7, 2, 7, 3),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(190, 8, 10, 14)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, tint.R, tint.G, tint.B)),
                Child = lines
            });
        }

        return panel;
    }

    /// <summary>截断，超了加省略号（小牌上放不下长句）。</summary>
    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 裂纹卡的颜色。与五档的颜色都不一样——它是**另一种状态**，不是「更低的一档」。
    /// 偏冷的灰蓝：看起来像碎掉的瓷，而不像金/红那种「奖励色」。
    /// </summary>
    private static readonly Color CrackColor = Color.Parse("#8FA6BD");

    /// <summary>
    /// 裂纹卡的那几道裂：三到四条折线，从卡边往中间走，越往中间越细、越淡。
    ///
    /// 全用代码画的折线，没有素材；**只有踩中负面提示词的卡才有**。
    /// 为什么折线而不是贴一张裂纹图：贴图要挑素材、要处理缩放，而「一条折线折几下」看起来已经足够像裂痕了。
    /// </summary>
    private static Control BuildCracks(double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };

        // (起笔粗细, 折点序列)。点用 0–1 的相对坐标写，乘上卡片的宽高才是实际位置。
        var strokes = new (double Thickness, byte Alpha, (double X, double Y)[] Points)[]
        {
            (1.6, 210, new[] { (0.10, -0.02), (0.21, 0.19), (0.12, 0.37), (0.25, 0.55), (0.19, 0.80), (0.26, 1.02) }),
            (1.3, 170, new[] { (1.02, 0.24), (0.80, 0.33), (0.66, 0.50), (0.47, 0.58), (0.34, 0.72) }),
            (1.1, 140, new[] { (0.58, 1.02), (0.63, 0.78), (0.50, 0.65), (0.42, 0.47) }),
            (0.9, 110, new[] { (1.02, 0.62), (0.84, 0.70), (0.72, 0.88) })
        };

        foreach (var stroke in strokes)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(stroke.Alpha, 233, 241, 250)),
                StrokeThickness = stroke.Thickness,
                StrokeJoin = PenLineJoin.Miter
            };
            foreach (var (x, y) in stroke.Points) line.Points.Add(new Point(x * width, y * height));
            canvas.Children.Add(line);
        }

        return canvas;
    }

    /// <summary>
    /// 档位的颜色。**只用在那一枚小牌上**——卡面本身是黑白的，这是唯一一处破例，
    /// 因为「这张是哪一档」必须一眼看得出来。
    /// </summary>
    private static Color TierColor(QualityTier tier) => tier switch
    {
        QualityTier.Gold => Color.Parse("#F3C556"),
        QualityTier.Red => Color.Parse("#E8756B"),
        QualityTier.Purple => Color.Parse("#B389E8"),
        QualityTier.Blue => Color.Parse("#7BA8E8"),
        _ => Color.Parse("#C9D2DE")
    };

    /// <summary>四角刻线：只画两条边的 L 形短线。有它卡面立刻「像一张卡」而不是一块图。</summary>
    private static Control BuildCornerTicks(double inset, double size, byte alpha)
    {
        var panel = new Panel { IsHitTestVisible = false };
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        var margin = new Thickness(inset);
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(1.2, 1.2, 0, 0), BorderBrush = brush,
            CornerRadius = new CornerRadius(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(0, 1.2, 1.2, 0), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(1.2, 0, 0, 1.2), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
        });
        panel.Children.Add(new Border
        {
            Width = size, Height = size, Margin = margin,
            BorderThickness = new Thickness(0, 0, 1.2, 1.2), BorderBrush = brush,
            CornerRadius = new CornerRadius(0, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        });
        return panel;
    }

    /// <summary>序号牌：整排盖着（或全亮着）的时候，要能对上是第几张。</summary>
    private static Control BuildIndexChip(int number, bool top)
    {
        return new Border
        {
            Margin = top ? new Thickness(14, 13, 0, 0) : new Thickness(14, 0, 0, 13),
            Padding = new Thickness(8, 1, 8, 2),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(165, 8, 10, 14)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(225, 222, 228, 238))
            }
        };
    }

    /// <summary>
    /// 卡片背后那团光晕。**它是这张卡唯一的颜色**——卡面是黑白的，六张靠这团光区分。
    ///
    /// 用 <see cref="RadialGradientBrush"/> 而不是 `BoxShadow`：阴影会把形状**内部**也填上颜色，
    /// 一大块圆角矩形叠在深底上就是一块灰疙瘩（上一版正是这么翻车的）。径向渐变才是真的
    /// 从中心往外淡出，没有任何硬边界。
    /// </summary>
    private static Border BuildHalo(Color tint, double width, double height, bool brighter = false, double strength = 1.0)
    {
        // 三个数都是试出来的：
        // · 0.85 处就降到全透明，剩下 15% 留成空白——最后一档如果正好落在边界上，
        //   六团光就会一起在同一个高度截断，连成一条横贯整排的直线（看着像浮出一个大色块）。
        // · 卡片只盖住半径的六成左右，所以真正看得见的是 0.6~0.85 那一段，
        //   亮度要给足，否则光全被卡片自己挡在后面、什么都看不见。
        // strength 是档位给的倍数（金更亮、白更暗）；它只改亮度，不改形状——形状变了就认不出是同一张卡了。
        var center = (brighter ? 215 : 150) * strength;
        var mid = (brighter ? 160 : 105) * strength;
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Math.Min(width, height) / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                // 半径是**相对**量（0.5 = 半个宽/高），且类型是 RelativeScalar 而不是 double。
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)Math.Clamp(center, 0, 255), tint.R, tint.G, tint.B), 0),
                    new GradientStop(Color.FromArgb((byte)Math.Clamp(mid, 0, 255), tint.R, tint.G, tint.B), 0.5),
                    new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0.85),
                    new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1)
                }
            }
        };
    }

    /// <summary>
    /// 让一团光晕缓缓上下漂、同时轻轻呼吸。周期由调用方给，每张卡不一样——**同步就白做了**：
    /// 六团光一起亮一起灭，看起来是一整块背景，反而分不出哪团光属于哪张卡。
    /// </summary>
    private static void StartDrift(Visual halo, double seconds, bool startHigh)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(seconds),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, startHigh ? 0.95 : 0.5),
                        new Setter(Visual.RenderTransformProperty,
                            TransformOperations.Parse(startHigh ? "translate(0px,-8px)" : "translate(0px,8px)"))
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, startHigh ? 0.5 : 0.95),
                        new Setter(Visual.RenderTransformProperty,
                            TransformOperations.Parse(startHigh ? "translate(0px,8px)" : "translate(0px,-8px)"))
                    }
                }
            }
        };
        _ = animation.RunAsync(halo);
    }

    /// <summary>卡片描边：左上亮、右下暗的斜向渐变，但**只有白与灰**。平涂一圈同色看着像塑料框。</summary>
    private static IBrush CardEdge(bool selected)
    {
        var light = selected ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(58, 255, 255, 255);
        var deep = selected ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(20, 255, 255, 255);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(light, 0),
                new GradientStop(Color.FromArgb(selected ? (byte)96 : (byte)38, 255, 255, 255), 0.5),
                new GradientStop(deep, 1)
            }
        };
    }

    /// <summary>
    /// 卡片的投影：一层往下的落地影 + 选中时一圈更亮的白边光。**没有颜色**——
    /// 颜色归卡背后那团光晕。
    ///
    /// 为什么不给整排卡加一块大柔光当台面：试过。`BoxShadow` 会把**形状内部**也填上颜色，
    /// 一大块圆角矩形叠在深底上就变成一块灰疙瘩，比不加还难看。
    /// </summary>
    private static BoxShadows CardShadows(bool selected) => selected
        ? new BoxShadows(
            new BoxShadow
            {
                OffsetX = 0, OffsetY = 18, Blur = 34, Spread = -10,
                Color = Color.FromArgb(190, 0, 0, 0)
            },
            new[]
            {
                new BoxShadow
                {
                    OffsetX = 0, OffsetY = 0, Blur = 30, Spread = -6,
                    Color = Color.FromArgb(46, 255, 255, 255)
                }
            })
        : new BoxShadows(new BoxShadow
        {
            OffsetX = 0, OffsetY = 12, Blur = 26, Spread = -12,
            Color = Color.FromArgb(165, 0, 0, 0)
        });

    /// <summary>结果标题：节点名 + 这一批几张；判过档位时再加一句「这是模型的判断」。</summary>
    private static Control BuildResultHeader(string nodeTitle, int count, QualityTier? omen)
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
        line.Children.Add(Rule(flip: false));
        line.Children.Add(new TextBlock
        {
            Text = $"共 {count} 张 · 点一张收进节点",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8FA6BD"))
        });
        line.Children.Add(Rule(flip: true));
        stack.Children.Add(line);

        // 档位是模型给的判断，这句话必须写在**画面上看得见的地方**，不能只躺在说明文档里：
        // 写成「金卡」而不说明是谁说的，用户就会把它当成客观结论。
        if (omen is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = QualityJudgement.ModelDisclaimer,
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 158, 148, 122))
            });
        }

        return stack;
    }

    /// <summary>标题两侧那两小段渐隐横线。</summary>
    private static Control Rule(bool flip)
    {
        var stops = new GradientStops
        {
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(90, 255, 255, 255), 1)
        };
        if (flip) stops = new GradientStops
        {
            new GradientStop(Color.FromArgb(90, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
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

    /// <summary>
    /// 许愿区：形象（你自己放的那张图，或者自绘的 ◈ 标记 + 一圈光）+ 厂家徽标 + 这一批的实情。
    ///
    /// `avatar` 是这一家的形象图（见 <see cref="ProviderAvatar"/>）；为 null 时退回自绘徽记。
    /// 有图时**环里换成图**、环本身留着当边框——同一个尺寸、同一圈光晕，所以换不换都不影响构图。
    /// </summary>
    private static Control BuildWishStage(
        string nodeTitle, int count, string badgeAbbreviation, Color accent, string sourceLine, Bitmap? avatar,
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
        // 环里放什么：放了形象图就用图，否则用「斜放的小方框 + ◈」——与卡背上的徽记是同一套语言。
        var ringBody = new Grid();
        if (avatar is not null)
        {
            // 圆环要真的裁掉图：ClipToBounds + CornerRadius 让这张图被剪成一个圆。
            markRing.ClipToBounds = true;
            ringBody.Children.Add(new Image
            {
                Source = avatar,
                Stretch = Stretch.UniformToFill
            });
        }
        else
        {
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
        }
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
    private static Control BuildSparkField(Color accent, int count, out List<Border> sparks)
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
        for (var i = 0; i < count; i++)
        {
            // 以上方为中心扇开（-90 度是正上方）：光点是「从形象这里散出去的」，方向要对得上。
            var spread = 26.0;
            var angle = -90 + (i - (count - 1) / 2.0) * spread;
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

    /// <summary>
    /// **一张卡自己那组光点**：翻到它的时候从卡片中间朝上扇开，数量按这张卡的档位。
    ///
    /// 为什么挪到每张卡上，而不是全批共用一组：档位是**每一格各自**的，共用一组就没法按档位给数量。
    /// 没判过档位时用中性数量（<see cref="UngradedSparks"/>）——它与白档不是一回事，白档给得更少。
    /// </summary>
    private static Control BuildCardSparkField(Color tint, SlotQuality? quality, out List<Border> sparks)
    {
        var tier = quality is { Source: not QualitySource.None } graded ? graded.Tier : (QualityTier?)null;
        var count = tier is { } value ? QualityJudgement.SparksFor(value) : UngradedSparks;

        // 画布比卡片大得多：光点要飞出卡外才看得见，而卡片自己有 ClipToBounds。
        const double size = 820;
        var field = new Canvas
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        sparks = new List<Border>();
        for (var i = 0; i < count; i++)
        {
            // 扇得比全批那组更开：卡片本身很宽，扇太窄的话光点会从卡片上下穿过去，看不出是「从这张卡里飞出来的」。
            var spread = 34.0;
            var angle = -90 + (i - (count - 1) / 2.0) * spread;
            var radius = 250 + (i % 3) * 46;
            var dx = Math.Cos(angle * Math.PI / 180) * radius;
            var dy = Math.Sin(angle * Math.PI / 180) * radius;

            var dot = tier == QualityTier.Gold ? 9.0 : 7.0;
            var spark = new Border
            {
                Width = dot,
                Height = dot,
                CornerRadius = new CornerRadius(dot / 2),
                Background = new SolidColorBrush(Color.FromArgb(238, tint.R, tint.G, tint.B)),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Blur = 22,
                    Spread = 3,
                    Color = Color.FromArgb(205, tint.R, tint.G, tint.B)
                }),
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransformOrigin = RelativePoint.Center,
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

    /// <summary>
    /// 把一组光点朝各自的方向射出去（起点在原点，方向与距离在各自的 <c>Tag</c> 里）。
    ///
    /// 与全批那组一样：先「无过渡」地点亮，再挂上过渡往 0 收 + 往外飞。
    /// 反过来的话连点亮那一下都会被当成一段动画，一闪的感觉就没了。
    /// </summary>
    private static void BurstSparks(IReadOnlyList<Border> sparks)
    {
        for (var i = 0; i < sparks.Count; i++)
        {
            var spark = sparks[i];
            var delay = i * 26;
            spark.Opacity = 1;
            spark.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(460),
                    Delay = TimeSpan.FromMilliseconds(delay),
                    Easing = new CubicEaseOut()
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(280),
                    Delay = TimeSpan.FromMilliseconds(delay + 220),
                    Easing = new CubicEaseIn()
                }
            };
            spark.RenderTransform = TransformOperations.Parse($"translate({spark.Tag as string})");
            spark.Opacity = 0;
        }
    }

    /// <summary>
    /// 蓄势用的尘埃：一圈小亮点，建的时候就已经摆在中心外面，等着被「吸」进去。
    ///
    /// 起点只在建的时候算一次（动画只要把它们送回原点就行）——起点算两次的话，
    /// 动画开始那一瞬间会先闪回原地再飞，看得出来。
    /// 数量是装饰，不带任何含义（带档位含义的是 <see cref="SparkCount"/>）。
    /// </summary>
    private static Control BuildDustField(Color light, out List<Border> dust)
    {
        const double size = (GatherRadius + 90) * 2;
        var field = new Canvas
        {
            Width = size,
            Height = size,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        dust = new List<Border>();
        for (var i = 0; i < GatherCount; i++)
        {
            // 撒一圈，但不落在一个正圆上：半径错开几档才像飘着的灰，不像一圈灯。
            var angle = i * (360.0 / GatherCount) + (i % 3) * 9;
            var radius = GatherRadius - (i % 4) * 34;
            var rad = angle * Math.PI / 180;
            var dx = Math.Cos(rad) * radius;
            // 竖直方向压扁一点：屏幕是横的，按正圆撒会顶到上下边。
            var dy = Math.Sin(rad) * radius * 0.7;

            var dot = i % 3 == 0 ? 5.0 : 3.0;
            var fleck = new Border
            {
                Width = dot,
                Height = dot,
                CornerRadius = new CornerRadius(dot / 2),
                Background = new SolidColorBrush(Color.FromArgb(220, light.R, light.G, light.B)),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Blur = 14,
                    Spread = 2,
                    Color = Color.FromArgb(150, light.R, light.G, light.B)
                }),
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = TransformOperations.Parse($"translate({dx:0.#}px,{dy:0.#}px)")
            };
            Canvas.SetLeft(fleck, size / 2 - dot / 2);
            Canvas.SetTop(fleck, size / 2 - dot / 2);
            field.Children.Add(fleck);
            dust.Add(fleck);
        }
        return field;
    }

    /// <summary>
    /// 一团椭圆形的柔光。爆发那一下的**光柱**与**白光**都是它——宽高不一样，一团是竖柱、一团是圆盘。
    ///
    /// 用径向渐变而不是 `BoxShadow`：阴影会把形状**内部**也填上颜色，出来是一块实心的圆角矩形（踩过）。
    /// 渐变在 0.9 处就归零、剩下留白，免得边界上出现一条硬边（这条在卡背后那团光上也踩过）。
    /// </summary>
    private static Border BuildEllipticalGlow(Color light, double width, double height, byte peak, byte mid)
    {
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Math.Min(width, height) / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("scale(1,1)"),
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(peak, light.R, light.G, light.B), 0),
                    new GradientStop(Color.FromArgb(mid, light.R, light.G, light.B), 0.45),
                    new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 0.9),
                    new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 1)
                }
            }
        };
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
            // 那 0 → 0.85 也会被当成一段动画，一闪的效果就没了。
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
    /// 这张卡背后那团光该用什么颜色：另解一张 24 宽的缩略图，按色相分 12 桶，取权重最大的那一桶的色相，
    /// 再按固定的饱和度与亮度画出来。
    ///
    /// 为什么不直接用平均色：照片的平均色几乎都往灰里掉，画成光晕就是一团脏灰，还不如没有。
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

    /// <summary>
    /// 爆发那一下的光用什么颜色：把这批图各自的主色相**在圆周上平均**成一个
    /// （色相是角度，直接算术平均会在 0/360 交界处出错，所以先化成单位向量再加）。
    ///
    /// 二游在爆发那一下常常用光的颜色暗示抽到了什么——也就是说那束光在**预告**结果。
    /// 这里的光不预告，它就是从结果推出来的：「你这批图偏这个色，所以光是这个色」。
    /// 所以它没法撒谎，也用不着撒谎——我们根本没有质量判据（待办第 8 条）。
    /// </summary>
    private static Color BatchLight(IReadOnlyList<Color> tints, Color fallback)
    {
        if (tints.Count == 0) return fallback;
        double x = 0, y = 0;
        foreach (var tint in tints)
        {
            var rad = HueOf(tint) * Math.PI / 180;
            x += Math.Cos(rad);
            y += Math.Sin(rad);
        }
        if (Math.Abs(x) < 1e-6 && Math.Abs(y) < 1e-6) return fallback;
        return FromHsv(Math.Atan2(y, x) * 180 / Math.PI, 0.58, 0.9);
    }

    /// <summary>一个颜色的色相（0–360）。</summary>
    private static double HueOf(Color color)
    {
        double r = color.R, g = color.G, b = color.B;
        return Hue(r, g, b, Math.Max(r, Math.Max(g, b)), Math.Min(r, Math.Min(g, b)));
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
