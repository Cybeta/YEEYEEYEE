namespace DreamForge.Desktop;

/// <summary>
/// 项目里所有窗体的基类，只做一件事：**按系统 DPI 缩放整套像素布局**。
///
/// 为什么必须有它：本项目的界面全部用代码构建，尺寸写的是字面像素（Width=128、行高 22…）。
/// 高分屏下（例如 150% 缩放）点是物理单位，字体会自动放大 1.5 倍，而那些字面像素不会——
/// 于是每个容器都比文字需要的尺寸小 1.5 倍，字体越大切得越狠，
/// 表现为"几乎每个面板的文字都显示不全"，而不只是某个按钮。
///
/// 为什么不用 AutoScaleMode：实测它在这个纯代码构建的界面里只缩放窗体自身，
/// 子控件和表格行高一个都不动（探针实测数据见 PROGRESS 第六十八轮），等于没修。
/// </summary>
public class ScaledForm : Form
{
    private bool scaled;

    public ScaledForm()
    {
        // 不靠 AutoScaleMode 兜底：实测（见 PROGRESS 第六十八轮）它在这个纯代码构建的界面里只缩放了窗体自身，子控件与表格行高一个都不动。真正的缩放走 ScaleToDpi。
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        Theme.Apply(this);
    }

    /// <summary>当前窗体的 DPI 放大倍数（100% = 1.0）。</summary>
    protected float DpiScale => DeviceDpi / 96f;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ScaleToDpi();
    }

    /// <summary>
    /// 按当前 DPI 显式缩放整棵控件树（含表格的行高列宽）。
    /// 必须在句柄创建之后、窗口显示之前调用——DeviceDpi 这时才是真值。
    /// </summary>
    internal void ScaleToDpi()
    {
        var scale = DpiScale;
        if (scaled || scale <= 1.001f) return;
        scaled = true;
        Scale(new SizeF(scale, scale));
        // WinForms 不缩放 ListView 的列宽（实测：其他都缩放了，列宽仍是原值），
        // 列宽不缩放就会把单元格里的文字截断，这里补上。
        ScaleListColumns(this, scale);
    }

    private static void ScaleListColumns(Control root, float scale)
    {
        foreach (Control child in root.Controls)
        {
            if (child is ListView list)
                foreach (ColumnHeader column in list.Columns)
                    column.Width = Math.Max(20, (int)Math.Round(column.Width * scale));
            if (child.HasChildren) ScaleListColumns(child, scale);
        }
    }
}

/// <summary>
/// 设计时像素值 → 当前 DPI 实际像素。
///
/// 窗体构造时写死的尺寸会被 <see cref="ScaledForm.ScaleToDpi"/> 统一放大，
/// 但**运行时才算出来的尺寸不会**（例如鼠标悬停时活动栏要展开多宽、附件区要占几行高）。
/// 这类值必须经过这里，否则它们会停在高分屏下的"半尺寸"上。
/// </summary>
internal static class Dpi
{
    public static int Scale(Control control, int value) =>
        value == 0 ? 0 : (int)Math.Round(value * control.DeviceDpi / 96.0);
}
