using Avalonia.Media;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 把共享的 <see cref="NodeKindPalette"/>（#RRGGBB）包成画刷。
/// 配色规则本身在共享层、有测试盯着；这里只负责「上色」这一层界面活儿。
/// </summary>
internal static class NodeKindBrushes
{
    public static Color ColorOf(NodeCategory category) => Color.Parse(NodeKindPalette.HexOf(category));

    public static IBrush BrushOf(NodeCategory category) => new SolidColorBrush(ColorOf(category));

    /// <summary>种类色带的底色：主色压到低透明度，铺在卡片底色上。</summary>
    public static IBrush SoftFillOf(NodeCategory category, byte alpha = 52)
    {
        var color = ColorOf(category);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>种类色带的描边：比底色实一点，边界才看得出来。</summary>
    public static IBrush SoftStrokeOf(NodeCategory category, byte alpha = 120)
    {
        var color = ColorOf(category);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
