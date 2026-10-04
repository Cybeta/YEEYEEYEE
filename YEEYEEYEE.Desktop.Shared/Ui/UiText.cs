using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 两端共读的界面文案。
///
/// **唯一的一份**是 <c>YEEYEEYEE.Canvas/src/shared/uiText.json</c>：网页端直接 import 它，
/// 这一侧用 EmbeddedResource 把**同一个文件**嵌进来（见 <c>YEEYEEYEE.Desktop.Shared.csproj</c>）。
/// 所以这里没有第二份可抄，也就没有「改一边别忘改另一边」这类注释债——配色那两份就是这么欠下来的。
///
/// **为什么住在 Desktop.Shared**：它要同时给桌面端、协作服务端（<c>YEEYEEYEE.Web</c>）与
/// Agent 侧的代码用。只有一条是说给未来的自己听的：一旦有别的 C# 项目也要嵌这份文案，
/// Docker 那条链上必须把 <c>YEEYEEYEE.Canvas/src/shared/uiText.json</c> 拷进构建上下文
/// （现在 Dockerfile 的②段为此专门有一行 COPY），否则本机照过、镜像构建才炸。
///
/// 读不到键就抛：界面文案缺一句会静默地少一截，而少掉的那截没人会发现。
/// 键写错是开发期错误，该当场炸（第一次跑到那一行就炸，比上线后少半句话好）。
/// </summary>
public static class UiText
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
    /// 填一句带占位符的文案：占位符写成 <c>{名字}</c>，两端都只做**字面替换**
    /// （不做格式化、不转义）。填完还剩 <c>{</c> 就抛——那说明模板或调用方有一个写错了，
    /// 而「少半句话」正是最不容易被发现的那种错。
    /// </summary>
    public static string Fill(string key, params (string Name, string Value)[] values)
    {
        var text = Text(key);
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value);
        if (text.Contains('{'))
            throw new InvalidOperationException($"共享文案 {key} 里还有没填上的占位符：{text}");
        return text;
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

    /// <summary>
    /// 面板与视图切换的标签（中央区三个视图 + 左栏两个工作区视角）。
    /// 网页端是 <c>WorkbenchShell.tsx</c> 里那两条数组，这一侧是 <c>MainWindow.axaml</c> 里那几个 Content——
    /// 两处过去各写一份，一端改了名字另一端会悄悄留着旧名字。XAML 用 <c>{x:Static}</c> 直接绑它们。
    /// </summary>
    public static string PanelCanvas => Text("panel.canvas");

    public static string PanelTimeline => Text("panel.timeline");

    public static string PanelScript => Text("panel.script");

    public static string PanelProjectTree => Text("panel.projectTree");

    public static string PanelStoryCanvas => Text("panel.storyCanvas");

    /// <summary>
    /// 检查器的两句提示：一句是**改完怎么落盘**（含 Ctrl+Enter 这个快捷键，网页端那一句逐字相同），
    /// 一句是**还没选节点时的初值**——它是前一句前面加了「选中节点后可改；」。
    /// 两句话之所以都在这里，是因为它们同时出现在三个地方（XAML 的初值、.cs 的状态行、网页端），
    /// 而键位一改（比如以后换成别的组合）必须三处一起改。
    /// </summary>
    public static string InspectorApplyHint => Text("inspector.applyHint");

    public static string InspectorIdleHint => Text("inspector.idleHint");

    /// <summary>
    /// 节点右键菜单里那两条节点操作。第 174 轮网页端也做了节点右键菜单，这两个标签从那时起
    /// **两端都有**——同一样东西在两个菜单里叫两个名字，是这类界面最容易走散的地方。
    /// </summary>
    public static string NodeMenuEdit => Text("nodeMenu.edit");

    public static string NodeMenuDelete => Text("nodeMenu.delete");

    /// <summary>
    /// 右侧栏那两个页签（第 179 轮网页端也做了同一组页签，两个词从那时起两端都有）。
    /// 桌面端在 <c>MainWindow.axaml</c> 与 <c>AgentPanel.axaml</c> 里各有一处。
    /// </summary>
    public static string DockInspector => Text("dock.inspector");

    public static string DockAgent => Text("dock.agent");

    /// <summary>
    /// 编辑锁的来源端说法（wire 值只有 <c>web</c> / <c>desktop</c>）。**认不出的按「网页端」**——
    /// 这条规则过去在服务端 <c>EditClient.Label</c>、桌面端 <c>CollaborationSession.ClientLabel</c>
    /// 与网页端 <c>locks.ts</c> 各写了一遍，每处的注释都指着另一处。现在只有这一份。
    /// </summary>
    public static string ClientLabel(string? client) =>
        Text(client == "desktop" ? "lease.client.desktop" : "lease.client.web");

    /// <summary>「林晚（桌面端）」这种可读的持有者描述——三处过去各拼一遍，现在只有这一份。</summary>
    public static string Who(string displayName, string? client) =>
        Fill("lease.who", ("name", displayName), ("client", ClientLabel(client)));

    /// <summary>「N 人在线」。网页端命令条与桌面端状态条共用这一句（**在线 ≠ 拥有锁**）。</summary>
    public static string OnlineCount(int count) =>
        Fill("presence.count", ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// 「N 人在线：甲、乙」。名字用**共享分隔符**拼——两端各写一个「、」看着一样，改起来就不一样了。
    /// 一个名字都没有时退回只有人数的说法。
    /// </summary>
    public static string OnlineDetail(int count, IEnumerable<string> names)
    {
        var joined = string.Join(Separator, names);
        return joined.Length == 0
            ? OnlineCount(count)
            : Fill("presence.detail", ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("names", joined));
    }

    private static JsonElement Load()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("UiText.json")
            ?? throw new InvalidOperationException(
                "嵌入式共享文案 UiText.json 不见了：检查 YEEYEEYEE.Desktop.Shared.csproj 里那条 "
                + "EmbeddedResource（它指向 YEEYEEYEE.Canvas/src/shared/uiText.json），"
                + "以及 Dockerfile 的②段有没有把那个 json 拷进构建上下文。");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
