namespace DreamForge.Desktop;

/// <summary>
/// 自绘容器里"填充块"的底色角色，见 <see cref="Theme.Surface{T}"/>。
/// </summary>
public enum SurfaceRole { Panel, Field, Rail, Editor }

/// <summary>
/// 全局主题：颜色与字体**集中在这里**，各处只引用，不再写死 Color.FromArgb。
///
/// 动手前先解决的是「零散」：原来颜色散落在十几个文件里，同一类控件在不同面板里深浅不一，
/// 这才是"丑"的根源——不是某个颜色不好看，而是没有统一语言。
/// </summary>
public enum ButtonRole { Secondary, Primary, Danger }

public static class Theme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;
    public static bool IsDark => Current == AppTheme.Dark;

    /// <summary>主题切换后触发：主窗体据此重新套用配色，画布据此重绘。</summary>
    public static event Action? Changed;

    public static void Set(AppTheme theme)
    {
        if (theme == Current) return;
        Current = theme;
        Changed?.Invoke();
    }

    public static AppTheme Toggle() => Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

    // ---------- 外壳（标题栏 / 活动栏 / 状态栏） ----------
    public static Color ChromeBg => IsDark ? FromHex("1B1B1F") : FromHex("EDEDF1");
    public static Color RailBg => IsDark ? FromHex("17171A") : FromHex("E4E4EA");

    // ---------- 面板与内容 ----------
    public static Color PanelBg => IsDark ? FromHex("202024") : FromHex("FFFFFF");
    public static Color EditorBg => IsDark ? FromHex("17171A") : FromHex("F7F7FA");
    public static Color FieldBg => IsDark ? FromHex("191920") : FromHex("FFFFFF");

    // ---------- 文字 ----------
    public static Color Text => IsDark ? FromHex("E4E4E9") : FromHex("23232A");
    public static Color TextMuted => IsDark ? FromHex("9C9CA8") : FromHex("6B6B76");
    public static Color TextDim => IsDark ? FromHex("6E6E7A") : FromHex("9A9AA6");

    // ---------- 线框与交互 ----------
    public static Color Border => IsDark ? FromHex("2E2E35") : FromHex("E1E1E8");
    public static Color Hover => IsDark ? FromHex("2A2A31") : FromHex("EFEFF4");
    public static Color Selected => IsDark ? FromHex("2C3350") : FromHex("E4E8FB");
    public static Color Accent => IsDark ? FromHex("5B7CFF") : FromHex("3B5BE0");
    public static Color AccentSoft => IsDark ? FromHex("3A3F63") : FromHex("DDE3FB");
    public static Color Warning => IsDark ? FromHex("D9A05B") : FromHex("B4762A");
    public static Color Danger => IsDark ? FromHex("E06C6C") : FromHex("C0504D");
    public static Color Success => IsDark ? FromHex("5FBF8F") : FromHex("2E8B62");
    public static Color PrimaryButton => IsDark ? FromHex("657FFF") : FromHex("4966D9");
    public static Color PrimaryButtonHover => IsDark ? FromHex("7890FF") : FromHex("5A75E6");
    public static Color DangerButton => IsDark ? FromHex("8F414D") : FromHex("C9575A");
    public static Color OnAccent => Color.White;

    // ---------- 画布（GDI+ 自绘用） ----------
    public static Color CanvasBg => IsDark ? FromHex("141417") : FromHex("F4F4F7");
    public static Color GridLine => IsDark ? FromHex("222228") : FromHex("E8E8EE");
    public static Color NodeBg => IsDark ? FromHex("22222A") : FromHex("FFFFFF");
    public static Color NodeBgSelected => IsDark ? FromHex("282833") : FromHex("F5F7FF");
    public static Color NodeBorder => IsDark ? FromHex("34343E") : FromHex("DCDCE4");
    public static Color NodeBody => IsDark ? FromHex("A8A8B6") : FromHex("5A5A66");
    public static Color Edge => IsDark ? FromHex("4A4A57") : FromHex("9A9AA6");
    public static Color Port => IsDark ? FromHex("5B7CFF") : FromHex("5B7CFF");
    public static Color PreviewOverlay => IsDark ? FromHex("2A2A34") : FromHex("F2F2F8");

    // ---------- 字体 ----------
    /// <summary>界面正文字体：中文优先用雅黑，避免 Segoe UI 回退时中英混排尺寸跳变。</summary>
    public static Font UiFont => new("Microsoft YaHei UI", 9.5F);
    public static Font UiFontBold => new("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
    public static Font TitleFont => new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static Font SmallFont => new("Microsoft YaHei UI", 8.75F);
    public static Font MonoFont => new("Consolas", 9.5F);
    public static Font IconFont => new("Segoe MDL2 Assets", 10F);

    /// <summary>从 "RRGGBB" 造色，避免到处写 Color.FromArgb(三参数) 的形式。</summary>
    private static Color FromHex(string hex) => Color.FromArgb(
        Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));

    /// <summary>
    /// 分隔线的标记。分隔线是「线」不是「面」：套色时必须还原成线色，
    /// 否则会被面板底色抹平——之前面板标题下的那条分隔线就是这么消失的。
    /// </summary>
    private static readonly object LineTag = new();

    /// <summary>造一条 1px 分隔线（颜色随主题）。</summary>
    public static Panel Line(DockStyle dock)
    {
        var line = new Panel { Dock = dock, BackColor = Border, Tag = LineTag };
        if (dock is DockStyle.Left or DockStyle.Right) line.Width = 1;
        else line.Height = 1;
        return line;
    }

    /// <summary>固定宽度的 1px 横线：流式布局里 Dock 不生效，得给死宽度。</summary>
    public static Panel Line(int width) =>
        new() { Size = new Size(width, 1), BackColor = Border, Tag = LineTag };

    /// <summary>
    /// 把一个容器标成"自绘容器里的填充块"，并声明它该跟哪一层的底色一致。
    ///
    /// 为什么需要它：**透明子控件会请父控件把背景画到自己身上**。
    /// 如果父控件是自绘的（画圆角、画边框），那段绘制会以父控件的坐标落在子控件的裁剪区里，
    /// 看起来就是"乱出的色块和马赛克"。所以自绘容器（圆角输入框这一类）里的每一层
    /// 都必须是不透明的、颜色一致的实心块——标上角色，套色时就不会被刷成面板底色。
    /// </summary>
    public static T Surface<T>(T panel, SurfaceRole role) where T : Panel
    {
        panel.Tag = role;
        panel.BackColor = ColorFor(role);
        return panel;
    }

    private static Color ColorFor(SurfaceRole role) => role switch
    {
        SurfaceRole.Field => FieldBg,
        SurfaceRole.Rail => RailBg,
        SurfaceRole.Editor => EditorBg,
        _ => PanelBg
    };

    /// <summary>
    /// 把当前配色递归套用到控件树上。
    /// 画布自己绘制（见 WorkflowCanvasControl），这里跳过它。
    /// 自定义绘制过的 ListView 只更新颜色，重新挂钩会重复绘制。
    /// </summary>
    public static void Apply(Control root)
    {
        foreach (Control child in root.Controls)
        {
            ApplyTo(child);
            if (child.HasChildren) Apply(child);
        }
    }

    private static readonly HashSet<Control> hooked = new();

    private static void ApplyTo(Control control)
    {
        switch (control)
        {
            case WorkflowCanvasControl:
                control.BackColor = CanvasBg;
                control.Invalidate();
                return;
            case ChatView chat:
                // 自绘控件：底色要跟着主题走，且必须重绘——它每一帧都从 Theme 取色，
                // 不像系统控件那样把颜色存在自己身上。
                chat.BackColor = FieldBg;
                chat.Invalidate();
                return;
            case Panel panel when panel.Tag is SurfaceRole role:
                panel.BackColor = ColorFor(role);
                return;
            case Panel panel when ReferenceEquals(panel.Tag, LineTag):
                panel.BackColor = Border;
                return;
            case RailNavButton or IconButton:
                // 自绘按钮必须保持透明底：套色后若被刷成实心底色，就会盖住底下那条栏。
                control.BackColor = Color.Transparent;
                control.ForeColor = Text;
                control.Font = UiFont;
                return;
            case TableLayoutPanel or FlowLayoutPanel or Panel:
                control.BackColor = control.BackColor == Color.Transparent ? Color.Transparent : PanelBg;
                return;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                var buttonRole = button.Tag is ButtonRole tagged ? tagged : InferButtonRole(button.Text);
                button.FlatAppearance.BorderSize = buttonRole is ButtonRole.Primary or ButtonRole.Danger ? 0 : 1;
                button.FlatAppearance.BorderColor = Border;
                button.BackColor = buttonRole switch
                {
                    ButtonRole.Primary => PrimaryButton,
                    ButtonRole.Danger => DangerButton,
                    _ => FieldBg
                };
                button.ForeColor = buttonRole is ButtonRole.Primary or ButtonRole.Danger ? OnAccent : Text;
                button.Font = buttonRole is ButtonRole.Primary or ButtonRole.Danger ? UiFontBold : UiFont;
                button.FlatAppearance.MouseOverBackColor = buttonRole switch
                {
                    ButtonRole.Primary => PrimaryButtonHover,
                    ButtonRole.Danger => Danger,
                    _ => Hover
                };
                button.FlatAppearance.MouseDownBackColor = buttonRole is ButtonRole.Primary ? Accent : Selected;
                return;
            case TextBox or RichTextBox or NumericUpDown or ComboBox:
                control.BackColor = FieldBg;
                control.ForeColor = Text;
                control.Font = UiFont;
                return;
            case TabControl tabs:
                tabs.BackColor = PanelBg;
                tabs.ForeColor = Text;
                tabs.Font = UiFont;
                return;
            case ContextMenuStrip menu:
                StyleContextMenu(menu);
                return;
            case ToolStrip strip:
                StyleToolStrip(strip);
                return;
            case ListView list:
                StyleListView(list);
                return;
            case LinkLabel or Label:
                control.ForeColor = MapFore(control.ForeColor);
                return;
            case CheckBox or RadioButton:
                control.BackColor = Color.Transparent;
                control.ForeColor = MapFore(control.ForeColor);
                control.Font = UiFont;
                return;
        }
    }

    private static void StyleToolStrip(ToolStrip strip)
    {
        strip.BackColor = PanelBg;
        strip.ForeColor = Text;
        strip.Font = UiFont;
        strip.RenderMode = ToolStripRenderMode.System;
    }

    private static void StyleContextMenu(ContextMenuStrip menu)
    {
        menu.BackColor = PanelBg;
        menu.ForeColor = Text;
        menu.Font = UiFont;
        menu.RenderMode = ToolStripRenderMode.System;
    }

    private static ButtonRole InferButtonRole(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Contains("删除", StringComparison.Ordinal) || value.Contains("移除", StringComparison.Ordinal)
            || value.Contains("清理", StringComparison.Ordinal) || value.Contains("撤销", StringComparison.Ordinal))
            return ButtonRole.Danger;
        if (value.Contains("保存", StringComparison.Ordinal) || value.Contains("应用", StringComparison.Ordinal)
            || value.Contains("创建", StringComparison.Ordinal) || value.Contains("进入", StringComparison.Ordinal)
            || value.Contains("生成", StringComparison.Ordinal) || value.Contains("提交", StringComparison.Ordinal)
            || value.Contains("导入并", StringComparison.Ordinal))
            return ButtonRole.Primary;
        return ButtonRole.Secondary;
    }

    /// <summary>
    /// 把历史遗留的字面色映射到主题角色。
    /// 这几种正是项目里真正用过的：近黑正文、灰提示、强调蓝、危险红、警告黄、成功绿。
    /// 没映射到的颜色原样保留——那多半是刻意的一次性配色，不该被主题抹掉。
    /// 映射是幂等的：已经是主题色的值不在表里，重复套用不会漂移。
    /// </summary>
    public static Color MapFore(Color color)
    {
        var value = color.ToArgb();
        if (value == Color.FromArgb(38, 38, 46).ToArgb() || value == Color.Black.ToArgb()) return Text;
        if (value == Color.Gray.ToArgb()
            || value == Color.FromArgb(120, 120, 130).ToArgb()
            || value == Color.FromArgb(132, 132, 142).ToArgb()
            || value == Color.FromArgb(150, 148, 170).ToArgb()) return TextMuted;
        if (value == Color.FromArgb(75, 63, 227).ToArgb()) return Accent;
        if (value == Color.FromArgb(190, 70, 70).ToArgb() || value == Color.FromArgb(200, 70, 70).ToArgb()) return Danger;
        if (value == Color.FromArgb(150, 110, 40).ToArgb() || value == Color.FromArgb(205, 150, 150).ToArgb()) return Warning;
        if (value == Color.FromArgb(40, 140, 90).ToArgb()) return Success;
        return color;
    }

    /// <summary>
    /// 列表样式。深色下必须自绘表头——系统绘制的表头永远是浅色的，
    /// 配深色面板会像一块补丁，这是"看起来没做完"的典型来源。
    /// </summary>
    public static void StyleListView(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = IsDark ? FieldBg : PanelBg;
        list.ForeColor = Text;
        list.Font = UiFont;
        list.OwnerDraw = true;
        if (!hooked.Add(list)) return;
        list.DrawColumnHeader += (_, e) =>
        {
            using var brush = new SolidBrush(IsDark ? FromHex("242430") : FromHex("F0F0F5"));
            e.Graphics.FillRectangle(brush, e.Bounds);
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using var text = new SolidBrush(TextMuted);
            var pad = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            e.Graphics.DrawString(e.Header!.Text, SmallFont, text, pad,
                new StringFormat { LineAlignment = StringAlignment.Center });
        };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
    }
}