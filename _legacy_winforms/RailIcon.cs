using System.Drawing.Drawing2D;

namespace DreamForge.Desktop;

/// <summary>左侧活动栏与 Agent 面板用到的图标种类。</summary>
public enum RailIcon { Library, Entity, Node, Task, Chat, Plugin, Settings, Attach, Cup, Box, Send, Brush, Stop, Model, Shield, Skill }

/// <summary>
/// 用 GDI+ 手绘活动栏图标。
///
/// 为什么不用图标字体：Segoe MDL2 Assets 的字形码点无法离线核实，写错就是一个空方块，
/// 比原来的汉字更糟。手绘的图形是确定的，且只用到圆角矩形/直线/圆弧这几种原语。
/// </summary>
public static class RailIconPainter
{
    public static void Draw(Graphics g, RailIcon icon, RectangleF box, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(box.Width, box.Height);
        var half = size / 2 - 1;
        using var pen = new Pen(color, Math.Max(1.35f, size * 0.075f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        var center = new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);

        switch (icon)
        {
            case RailIcon.Library:   // 四个小方块：一份「库」的样子
            {
                var cell = half * 0.78f;
                var gap = half * 0.22f;
                foreach (var (dx, dy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
                {
                    var rect = new RectangleF(
                        center.X + dx * (cell / 2 + gap / 2) - cell / 2,
                        center.Y + dy * (cell / 2 + gap / 2) - cell / 2, cell, cell);
                    using var cellPath = RoundedRect(rect, Math.Max(1.5f, cell * 0.16f));
                    g.DrawPath(pen, cellPath);
                }
                break;
            }
            case RailIcon.Entity:    // 头 + 肩：角色 / 设定
            {
                var head = half * 0.62f;
                g.DrawEllipse(pen, center.X - head / 2, center.Y - half * 0.9f, head, head);
                g.DrawArc(pen, center.X - half * 0.85f, center.Y + head / 2 - half * 0.35f,
                    half * 1.7f, half * 1.5f, 200f, 140f);
                break;
            }
            case RailIcon.Node:      // 卡片 + 两行内容：节点属性
            {
                var rect = new RectangleF(center.X - half * 0.9f, center.Y - half * 0.7f, half * 1.8f, half * 1.4f);
                using var nodePath = RoundedRect(rect, Math.Max(2f, half * 0.16f));
                g.DrawPath(pen, nodePath);
                g.DrawLine(pen, rect.X + 3, rect.Y + rect.Height * 0.38f, rect.Right - 3, rect.Y + rect.Height * 0.38f);
                g.DrawLine(pen, rect.X + 3, rect.Y + rect.Height * 0.68f, rect.X + rect.Width * 0.6f, rect.Y + rect.Height * 0.68f);
                break;
            }
            case RailIcon.Task:      // 三条带点的事项：任务与出图
            {
                for (var row = -1; row <= 1; row++)
                {
                    var y = center.Y + row * half * 0.62f;
                    g.FillEllipse(brush, center.X - half * 0.92f, y - 1.4f, 2.8f, 2.8f);
                    g.DrawLine(pen, center.X - half * 0.4f, y, center.X + half * 0.92f, y);
                }
                break;
            }
            case RailIcon.Chat:      // 对话气泡 + 尾巴
            {
                var rect = new RectangleF(center.X - half * 0.95f, center.Y - half * 0.85f, half * 1.9f, half * 1.35f);
                using var path = RoundedRect(rect, 3f);
                g.DrawPath(pen, path);
                g.DrawLines(pen, new[]
                {
                    new PointF(center.X - half * 0.3f, rect.Bottom - 0.5f),
                    new PointF(center.X - half * 0.45f, rect.Bottom + half * 0.55f),
                    new PointF(center.X + half * 0.15f, rect.Bottom - 0.5f)
                });
                break;
            }
            case RailIcon.Plugin:    // 拼图块：左上圆角缺口的方块组合
            {
                var rect = new RectangleF(center.X - half * 0.85f, center.Y - half * 0.85f, half * 1.7f, half * 1.7f);
                using var path = RoundedRect(rect, 3f);
                g.DrawPath(pen, path);
                g.DrawLine(pen, rect.X + rect.Width * 0.5f, rect.Y, rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.3f);
                g.DrawLine(pen, rect.X + rect.Width * 0.5f, rect.Bottom - rect.Height * 0.3f, rect.X + rect.Width * 0.5f, rect.Bottom);
                break;
            }
            case RailIcon.Settings:  // 三条滑杆：比齿轮更好画也更清晰
            {
                for (var row = -1; row <= 1; row++)
                {
                    var y = center.Y + row * half * 0.62f;
                    g.DrawLine(pen, center.X - half * 0.9f, y, center.X + half * 0.9f, y);
                    var knob = center.X + (row == 0 ? -1 : 1) * half * 0.35f;
                    using var fill = new SolidBrush(color);
                    g.FillEllipse(fill, knob - 2.2f, y - 2.2f, 4.4f, 4.4f);
                }
                break;
            }
            case RailIcon.Attach:    // 回形针：统一附件入口
            {
                g.DrawArc(pen, center.X - half * 0.55f, center.Y - half * 0.9f, half * 1.1f, half * 1.2f, 180, 180);
                g.DrawLine(pen, center.X - half * 0.55f, center.Y - half * 0.3f, center.X - half * 0.55f, center.Y + half * 0.55f);
                g.DrawLine(pen, center.X + half * 0.55f, center.Y - half * 0.3f, center.X + half * 0.55f, center.Y + half * 0.55f);
                g.DrawArc(pen, center.X - half * 0.55f, center.Y + half * 0.1f, half * 1.1f, half * 1.0f, 0, 180);
                g.DrawLine(pen, center.X - half * 0.2f, center.Y - half * 0.9f, center.X - half * 0.2f, center.Y + half * 0.4f);
                break;
            }
            case RailIcon.Cup:       // 竖着的杯子：里面的液面就是上下文占用
            {
                var body = new RectangleF(center.X - half * 0.45f, center.Y - half * 0.95f, half * 0.9f, half * 1.75f);
                using var path = RoundedRect(body, 1.5f);
                g.DrawPath(pen, path);
                g.DrawLine(pen, body.X + body.Width, center.Y - half * 0.35f, center.X + half * 0.75f, center.Y - half * 0.35f);
                g.DrawLine(pen, body.X + body.Width, center.Y + half * 0.35f, center.X + half * 0.75f, center.Y + half * 0.35f);
                break;
            }
            case RailIcon.Box:       // 箱子：命中 / 未命中按比例填色
            {
                var rect = new RectangleF(center.X - half * 0.9f, center.Y - half * 0.7f, half * 1.8f, half * 1.4f);
                using var boxPath = RoundedRect(rect, Math.Max(2f, half * 0.16f));
                g.DrawPath(pen, boxPath);
                g.DrawLine(pen, rect.X, rect.Y + rect.Height * 0.3f, rect.Right, rect.Y + rect.Height * 0.3f);
                g.DrawLine(pen, rect.X + rect.Width * 0.5f, rect.Y, rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.3f);
                break;
            }
            case RailIcon.Send:      // 纸飞机
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(center.X - half * 0.9f, center.Y - half * 0.6f),
                    new PointF(center.X + half * 0.9f, center.Y),
                    new PointF(center.X - half * 0.9f, center.Y + half * 0.6f),
                    new PointF(center.X - half * 0.45f, center.Y)
                });
                g.DrawLine(pen, center.X - half * 0.45f, center.Y, center.X + half * 0.9f, center.Y);
                break;
            }
            case RailIcon.Brush:     // 画笔：斜笔杆 + 笔头，作为"发送/生成"的主动作图标
            {
                g.DrawLine(pen, center.X + half * 0.2f, center.Y - half * 0.8f, center.X + half * 0.8f, center.Y - half * 0.2f);
                using var tip = new GraphicsPath();
                tip.AddPolygon(new[]
                {
                    new PointF(center.X + half * 0.05f, center.Y - half * 0.35f),
                    new PointF(center.X + half * 0.45f, center.Y + half * 0.05f),
                    new PointF(center.X - half * 0.55f, center.Y + half * 0.9f),
                    new PointF(center.X - half * 0.9f, center.Y + half * 0.55f)
                });
                g.DrawPath(pen, tip);
                using var bristle = new SolidBrush(color);
                g.FillPolygon(bristle, new[]
                {
                    new PointF(center.X + half * 0.05f, center.Y - half * 0.35f),
                    new PointF(center.X + half * 0.45f, center.Y + half * 0.05f),
                    new PointF(center.X - half * 0.25f, center.Y + half * 0.6f),
                    new PointF(center.X - half * 0.6f, center.Y + half * 0.25f)
                });
                break;
            }
            case RailIcon.Stop:      // 方块：停止生成
            {
                var rect = new RectangleF(center.X - half * 0.62f, center.Y - half * 0.62f, half * 1.24f, half * 1.24f);
                using var path = RoundedRect(rect, 2f);
                g.DrawPath(pen, path);
                break;
            }
            case RailIcon.Model:     // 芯片：模型选择
            {
                var rect = new RectangleF(center.X - half * 0.62f, center.Y - half * 0.62f, half * 1.24f, half * 1.24f);
                using var path = RoundedRect(rect, 2f);
                g.DrawPath(pen, path);
                for (var i = -1; i <= 1; i++)
                {
                    var offset = i * half * 0.45f;
                    g.DrawLine(pen, center.X + offset, rect.Y - 3f, center.X + offset, rect.Y);
                    g.DrawLine(pen, center.X + offset, rect.Bottom, center.X + offset, rect.Bottom + 3f);
                    g.DrawLine(pen, rect.X - 3f, center.Y + offset, rect.X, center.Y + offset);
                    g.DrawLine(pen, rect.Right, center.Y + offset, rect.Right + 3f, center.Y + offset);
                }
                break;
            }
            case RailIcon.Skill:     // 三个节点：技能注册与调用
            {
                g.DrawEllipse(pen, center.X - half * 0.85f, center.Y - half * 0.15f, half * 0.42f, half * 0.42f);
                g.DrawEllipse(pen, center.X + half * 0.42f, center.Y - half * 0.75f, half * 0.42f, half * 0.42f);
                g.DrawEllipse(pen, center.X + half * 0.42f, center.Y + half * 0.45f, half * 0.42f, half * 0.42f);
                g.DrawLine(pen, center.X - half * 0.4f, center.Y, center.X + half * 0.42f, center.Y - half * 0.5f);
                g.DrawLine(pen, center.X - half * 0.4f, center.Y, center.X + half * 0.42f, center.Y + half * 0.7f);
                break;
            }
            case RailIcon.Shield:    // 盾牌：授权模式
            {
                using var path = new GraphicsPath();
                path.AddPolygon(new[]
                {
                    new PointF(center.X, center.Y - half * 0.95f),
                    new PointF(center.X + half * 0.8f, center.Y - half * 0.55f),
                    new PointF(center.X + half * 0.8f, center.Y + half * 0.15f),
                    new PointF(center.X, center.Y + half * 0.95f),
                    new PointF(center.X - half * 0.8f, center.Y + half * 0.15f),
                    new PointF(center.X - half * 0.8f, center.Y - half * 0.55f)
                });
                g.DrawPath(pen, path);
                break;
            }
        }
    }

    /// <summary>圆角矩形路径。对话区的卡片、输入框外框也都用它，所以是 internal 而不是 private。</summary>
    internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 面板里的小图标按钮：可带一行短文字（例如当前模型名）。
/// 与活动栏按钮的区别是不需要选中指示条，尺寸更小。
/// </summary>
public sealed class IconButton : Button
{
    private readonly RailIcon icon;
    private string? caption;

    /// <summary>按钮上的短文字（例如当前授权模式 / 模型名）；改它会同步重算宽度。</summary>
    [System.ComponentModel.DefaultValue(null)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Caption
    {
        get => caption;
        set
        {
            if (string.Equals(caption, value, StringComparison.Ordinal)) return;
            caption = value;
            // 宽度由外层布局决定时（Dock 不是 None）不要自己改宽度，否则会和布局打架。
            if (Dock == DockStyle.None) Width = caption is null ? 30 : Math.Max(30, 34 + caption.Length * 12);
            Invalidate();
        }
    }

    /// <summary>
    /// 强调态：画成实心强调色按钮。给"主要动作"用（例如发送），
    /// 一排描边按钮里要有一个一眼能认出来的。
    /// </summary>
    [System.ComponentModel.DefaultValue(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Highlight { get; set; }

    public IconButton(RailIcon icon, string? caption, string tip, ToolTip? toolTip = null)
    {
        this.icon = icon;
        this.caption = caption;
        Height = 26;
        Width = caption is null ? 30 : Math.Max(30, 34 + caption.Length * 12);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Theme.Hover;
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.SmallFont;
        Cursor = Cursors.Hand;
        TabStop = false;
        Text = string.Empty;
        if (toolTip is not null) toolTip.SetToolTip(this, tip);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        RailNavButton.FillBack(graphics, this);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        var color = Highlight ? Color.White
            : Enabled ? (hovered ? Theme.Text : Theme.TextMuted)
            : Theme.TextDim;

        // 每个按钮都画一个圆角框：一排图标加文字挤在一起时，不画框就看不出哪里能点。
        // 强调态画成实心强调色，作为"主要动作"。
        using (var path = RailIconPainter.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Dpi.Scale(this, 6)))
        {
            var fillColor = Highlight ? Theme.Accent : hovered ? Theme.Hover : Theme.FieldBg;
            using var fill = new SolidBrush(fillColor);
            graphics.FillPath(fill, path);
            using var border = new Pen(Highlight ? Theme.Accent : hovered ? Theme.TextDim : Theme.Border);
            graphics.DrawPath(border, path);
        }

        // 图标与文字作为**一整组**居中，而不是各自贴左——之前文字偏在一边，看起来没对齐。
        var iconSize = Dpi.Scale(this, 16);
        var gap = Dpi.Scale(this, 6);
        var textWidth = caption is null ? 0 : TextRenderer.MeasureText(caption, Theme.SmallFont, new Size(int.MaxValue, int.MaxValue), Flags).Width;
        var contentWidth = iconSize + (caption is null ? 0 : gap + textWidth);
        var left = Math.Max(Dpi.Scale(this, 5), (Width - contentWidth) / 2);

        RailIconPainter.Draw(graphics, icon, new RectangleF(left, Height / 2f - iconSize / 2f, iconSize, iconSize), color);
        if (caption is null) return;

        var textLeft = left + iconSize + gap;
        TextRenderer.DrawText(graphics, caption, Theme.SmallFont,
            new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - Dpi.Scale(this, 5)), Height), color,
            Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

/// <summary>
/// 上下文占用杯：竖着的杯子，液面高度就是占用比例；满了就是上下文满了。
/// 鼠标悬停显示具体数字，不把数字摊在界面上。
/// </summary>
public sealed class CupGauge : Control
{
    private double ratio;

    public CupGauge()
    {
        // 尺寸放大约一倍：之前 26px 在 150% 缩放下只剩一点点，用户反馈"图标太小了"。
        Width = 34;
        Height = 30;
        // 必须先声明"支持透明底色"，否则下面设 Transparent 会直接抛异常（普通 Control 默认不支持）。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    /// <summary>占用比例 0–1；超过 1 按满处理。</summary>
    public void SetRatio(double value)
    {
        var clamped = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
        if (Math.Abs(clamped - ratio) < 0.001) return;
        ratio = clamped;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        RailNavButton.FillBack(graphics, this);

        // 图标按控件尺寸等比绘制，不写死像素——写死的话控件放大了图标还是那么大。
        var size = Math.Max(8f, Math.Min(Width, Height) - Dpi.Scale(this, 4));
        var box = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
        RailIconPainter.Draw(graphics, RailIcon.Cup, box, Theme.TextMuted);
        if (ratio <= 0) return;

        // 液面按杯体几何算：杯身在图标里居中，底端在 center.Y + 0.8h。
        var half = size / 2f - 1;
        var centerX = box.X + box.Width / 2f;
        var centerY = box.Y + box.Height / 2f;
        var bodyLeft = centerX - half * 0.45f;
        var bodyWidth = Math.Max(2f, half * 0.9f);
        var bodyBottom = centerY + half * 0.8f;
        var liquid = half * 1.75f * (float)ratio;
        var color = ratio > 0.9 ? Theme.Danger : ratio > 0.7 ? Theme.Warning : Theme.Accent;
        using var fill = new SolidBrush(color);
        graphics.FillRectangle(fill, bodyLeft + 1, bodyBottom - liquid, Math.Max(1f, bodyWidth - 2), liquid);
    }
}

/// <summary>
/// 缓存命中箱：箱体按命中比例分成蓝色（命中）与黄色（未命中）两段。
/// 用来直观表示 Token 命中视图——一眼看出这次请求有多少是缓存命中。
/// </summary>
public sealed class CacheBox : Control
{
    private long hit;
    private long miss;

    public CacheBox()
    {
        Width = 26;
        Height = 26;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void SetUsage(long hitTokens, long missTokens)
    {
        if (hit == hitTokens && miss == missTokens) return;
        hit = hitTokens;
        miss = missTokens;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        RailNavButton.FillBack(graphics, this);

        // 按控件尺寸等比绘制，不写死像素。
        var inset = Math.Max(3f, Math.Min(Width, Height) * 0.16f);
        var box = new RectangleF(inset, inset + Dpi.Scale(this, 2), Width - inset * 2, Math.Max(4f, Height - inset * 2 - Dpi.Scale(this, 2)));
        using var border = new Pen(Theme.TextMuted);
        graphics.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);

        var total = hit + miss;
        if (total > 0)
        {
            var hitWidth = box.Width * hit / total;
            using var hitBrush = new SolidBrush(Theme.Accent);
            graphics.FillRectangle(hitBrush, box.X, box.Y, (float)hitWidth, box.Height);
            if (miss > 0)
            {
                using var missBrush = new SolidBrush(Theme.Warning);
                graphics.FillRectangle(missBrush, box.X + (float)hitWidth, box.Y, (float)(box.Width - hitWidth), box.Height);
            }
        }
        // 箱盖，让它是"箱子"而不是普通方块。
        var lidGap = Dpi.Scale(this, 3);
        graphics.DrawLine(border, box.X, box.Y - lidGap, box.Right, box.Y - lidGap);
        graphics.DrawLine(border, box.X + box.Width / 2f, box.Y - lidGap, box.X + box.Width / 2f, box.Y);
    }
}

/// <summary>
/// 活动栏按钮：图标 + 一行小字标签，垂直居中排在左侧。
/// 选中时左侧一条强调色指示条、底色换成强调软色，
/// 这样每一项都能被认出来，不再是一整条长得一样的图标。
/// </summary>
public sealed class RailNavButton : Button
{
    private readonly RailIcon? icon;
    private readonly string? glyph;
    private readonly string label;

    /// <summary>选中态：由主窗体在切换面板时设置，不是设计器属性。</summary>
    [System.ComponentModel.DefaultValue(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    /// <summary>手绘图标的项。</summary>
    public RailNavButton(RailIcon icon, string label, ToolTip tip) : this(label, tip) => this.icon = icon;

    /// <summary>插件自带的字形项：没有手绘图标可用，就用它自己的字。</summary>
    public RailNavButton(string glyph, string label, ToolTip tip) : this(label, tip) => this.glyph = glyph;

    private RailNavButton(string label, ToolTip tip)
    {
        this.label = label;
        Size = new Size(128, 34);
        Margin = new Padding(0, 1, 0, 1);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
        // 标签由本类自绘，基类 Text 不参与布局（否则按钮会按文字重算尺寸）。
        Text = string.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        tip.SetToolTip(this, label);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        // 自绘控件必须自己铺底：依赖"透明背景由父控件代画"时，
        // 重绘不会先清掉旧内容，旧字留在下面就是"字叠字/马赛克"。
        FillBack(graphics, this);
        var hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (hovered || Active)
        {
            using var background = new SolidBrush(Active ? Theme.AccentSoft : Theme.Hover);
            graphics.FillRectangle(background, ClientRectangle);
        }
        if (Active)
        {
            using var bar = new SolidBrush(Theme.Accent);
            graphics.FillRectangle(bar, 0, 6, 2, Height - 12);
        }

        var color = Active ? Theme.Text : Theme.TextMuted;
        if (icon is { } drawn)
        {
            RailIconPainter.Draw(graphics, drawn, new RectangleF(12, Height / 2f - 9, 18, 18), color);
        }
        else if (!string.IsNullOrEmpty(glyph))
        {
            using var glyphBrush = new SolidBrush(color);
            graphics.DrawString(glyph, Theme.UiFont, glyphBrush, new RectangleF(12, 0, 18, Height),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }

        using var labelBrush = new SolidBrush(color);
        graphics.DrawString(label, Theme.SmallFont, labelBrush, new RectangleF(38, 0, Width - 44, Height),
            new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
    }

    /// <summary>用父容器的底色铺满自己，等价于"不透明版的透明底"。</summary>
    internal static void FillBack(Graphics graphics, Control control)
    {
        var back = control.Parent?.BackColor ?? Theme.PanelBg;
        if (back.A == 0) back = Theme.PanelBg;
        using var brush = new SolidBrush(back);
        graphics.FillRectangle(brush, control.ClientRectangle);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

/// <summary>
/// 左侧活动栏的滑出条：收起时只留一条窄边并在中间画三道短横（提示这里能滑出选项），
/// 鼠标靠过来就展开成完整的一列「图标 + 小字标签」。
/// 展开靠主窗体的计时器逐帧改宽度实现，这里只负责按当前宽度决定画什么。
/// </summary>
public sealed class RailStrip : Panel
{
    private bool collapsed = true;

    public RailStrip()
    {
        BackColor = Theme.RailBg;
        // ResizeRedraw：滑出动画每帧都在改宽度，不重绘整块就会留下上一帧的内容（叠字/残影）。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>收起态：三道短横就是"这里还有东西"的全部提示。</summary>
    [System.ComponentModel.DefaultValue(true)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => collapsed;
        set
        {
            if (collapsed == value) return;
            collapsed = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        // 条自己铺底：它是下面那些控件的背景来源，必须是不透明的实心块。
        using (var back = new SolidBrush(Theme.RailBg)) graphics.FillRectangle(back, ClientRectangle);
        if (!collapsed) return;
        using var brush = new SolidBrush(Theme.TextDim);
        var center = Height / 2f;
        for (var row = -1; row <= 1; row++)
            e.Graphics.FillRectangle(brush, Width / 2f - 3, center + row * 8 - 1, 6, 2);
    }
}
