using System.Text.Json;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 两端共读的界面文案（桌面端这一侧）。
///
/// **唯一的一份**是 <c>YEEYEEYEE.Canvas/src/shared/uiText.json</c>：网页端直接 import 它，
/// 桌面端在 csproj 里用 EmbeddedResource 把**同一个文件**嵌进来。所以这里没有第二份可抄，
/// 也就没有「改一边别忘改另一边」这类注释债——配色那两份就是这么欠下来的。
///
/// 读不到键就抛：界面文案缺一句会静默地少一截，而少掉的那截没人会发现。
/// 键写错是开发期错误，该当场炸（第一次跑到那一行就炸，比上线后少半句话好）。
/// </summary>
internal static class UiText
{
    private static readonly JsonElement Table = Load();

    /// <summary>两端的拼接分隔符——也在共享文件里：两端各写一个「 · 」，看着一样，改起来就不一样了。</summary>
    public static string Separator => Text("separator");

    public static string Text(string key)
    {
        if (Table.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
            return text;

        throw new InvalidOperationException(
            $"共享文案里没有这个键：{key}（YEEYEEYEE.Canvas/src/shared/uiText.json）");
    }

    /// <summary>
    /// 把若干个手势拼成一行提示——与网页端的 <c>gestureHint</c> 是同一个拼法（同一份措辞、同一个分隔符）。
    /// **列哪几个手势两端各定**：网页端没有 Delete 键这一条，所以它不列。
    /// </summary>
    public static string Gestures(params string[] keys) => string.Join(Separator, keys.Select(Text));

    /// <summary>
    /// 画布空闲时那一行。XAML 里用 <c>{x:Static}</c> 直接绑它——不在 XAML 里再抄一份字符串，
    /// 那份抄写正是「改一边忘一边」的老路。
    /// </summary>
    public static string CanvasIdleHint => Gestures(
        "gesture.pan", "gesture.zoom", "gesture.dragNode", "gesture.connect", "gesture.select",
        "gesture.delete", "gesture.cancel");

    private static JsonElement Load()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("UiText.json")
            ?? throw new InvalidOperationException(
                "嵌入式共享文案 UiText.json 不见了：检查 YEEYEEYEE.Desktop.Avalonia.csproj 里那条 "
                + "EmbeddedResource（它指向 YEEYEEYEE.Canvas/src/shared/uiText.json）。");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
