using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>「发现新版本」那个弹窗里用户选了什么。</summary>
internal enum UpdateChoice
{
    /// <summary>稍后再说。</summary>
    Later,

    /// <summary>去浏览器看发布页（自己下载）。</summary>
    OpenPage,

    /// <summary>应用内下载并升级。</summary>
    Install
}

/// <summary>
/// 更新相关的几个弹窗：发现新版、升级已就绪、更新完成、以及各种如实报的失败。
///
/// 外观统一走 <see cref="DialogShell"/>，按钮统一走 <see cref="AgentDialogUi"/>，与其它弹窗同源。
/// 这里只负责「把事实说清楚」：版本号、发布时间、这一版改了什么、以及**为什么这次装不了**。
/// </summary>
internal static class UpdateDialog
{
    /// <summary>正文区高度：够看完一条发行说明，又不至于把弹窗撑成一整屏。</summary>
    private const double NotesHeight = 190;

    private const double DialogWidth = 580;
    private const double DialogHeight = 470;

    /// <summary>发现新版本：让用户选「稍后 / 去看发布页 / 下载并升级」。</summary>
    public static async Task<UpdateChoice> AskAsync(Window owner, UpdateCheckResult result, string? blockedReason)
    {
        var release = result.Release;
        var latest = result.Latest is { } version ? AppVersion.Text(version) : "未知";

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(new TextBlock
        {
            Text = $"发现新版本 {latest}",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = AgentDialogUi.Brush("DfInk")
        });

        var published = release?.PublishedAt is { } at ? $" · {at.ToLocalTime():yyyy-MM-dd} 发布" : string.Empty;
        body.Children.Add(AgentDialogUi.Note($"当前版本 {AppVersion.Text(result.Current)}{published}"));

        if (!string.IsNullOrWhiteSpace(release?.Name))
            body.Children.Add(AgentDialogUi.Header(release!.Name));

        body.Children.Add(AgentDialogUi.Header("这一版改了什么"));
        body.Children.Add(BuildNotes(release?.Body));

        if (blockedReason is { Length: > 0 })
            body.Children.Add(AgentDialogUi.Note(blockedReason, AgentNoteLevel.Warning));

        var later = AgentDialogUi.Secondary("稍后再说");
        var openPage = AgentDialogUi.Secondary("去发布页");
        var install = AgentDialogUi.Primary("下载并升级");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(later);
        buttons.Children.Add(openPage);
        if (blockedReason is null) buttons.Children.Add(install);

        var window = DialogShell.Create("检查更新", AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), DialogWidth, DialogHeight);
        var choice = UpdateChoice.Later;
        later.Click += (_, _) => window.Close();
        openPage.Click += (_, _) => { choice = UpdateChoice.OpenPage; window.Close(); };
        install.Click += (_, _) => { choice = UpdateChoice.Install; window.Close(); };

        await window.ShowDialog(owner);
        return choice;
    }

    /// <summary>升级包已经下载并解压好：确认现在重启（替换发生在应用退出之后）。</summary>
    public static async Task<bool> ConfirmRestartAsync(Window owner, UpdatePackage package, long bytes)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(new TextBlock
        {
            Text = $"更新包已就绪：{package.Version}",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = AgentDialogUi.Brush("DfInk")
        });
        body.Children.Add(AgentDialogUi.Note($"已下载 {FormatSize(bytes)} 并解压完成。"));
        body.Children.Add(AgentDialogUi.Header("接下来会发生什么"));
        body.Children.Add(AgentDialogUi.Note(
            "点「立即重启」后应用会先退出，再由一个替换脚本把程序目录换成新版，然后自动把应用启动回来。"
            + "替换期间请不要手动打开这个程序。更新内容会在重启后弹出来。"));
        body.Children.Add(AgentDialogUi.Note(
            "注意：这一步会改动程序所在目录，所以只对**便携部署**（程序目录可写）有效；"
            + "如果程序装在 Program Files 这类受保护位置，请改用「去发布页」自己下载安装。",
            AgentNoteLevel.Warning));

        var later = AgentDialogUi.Secondary("稍后重启");
        var now = AgentDialogUi.Primary("立即重启");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(later);
        buttons.Children.Add(now);

        var window = DialogShell.Create("准备重启", AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), DialogWidth, 400);
        var restart = false;
        later.Click += (_, _) => window.Close();
        now.Click += (_, _) => { restart = true; window.Close(); };

        await window.ShowDialog(owner);
        return restart;
    }

    /// <summary>重启之后：把这次更新到底换成了什么版本、改了什么说清楚。</summary>
    public static async Task ShowCompletedAsync(Window owner, PendingUpdateInfo info)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(new TextBlock
        {
            Text = $"已更新到 {info.ToVersion}",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = AgentDialogUi.Brush("DfInk")
        });
        body.Children.Add(AgentDialogUi.Note($"上一版是 {info.FromVersion}，这次替换已完成。", AgentNoteLevel.Success));
        body.Children.Add(AgentDialogUi.Header("更新内容"));
        body.Children.Add(BuildNotes(info.Notes));

        var ok = AgentDialogUi.Primary("知道了");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);

        var window = DialogShell.Create("更新完成", AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), DialogWidth, DialogHeight);
        ok.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }

    /// <summary>用完就丢的提示框：只报一件事，一个确定按钮。</summary>
    public static async Task ShowNoticeAsync(Window owner, string title, string message, AgentNoteLevel level = AgentNoteLevel.Info)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 16) };
        body.Children.Add(AgentDialogUi.Note(message, level));

        var ok = AgentDialogUi.Primary("知道了");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);

        var window = DialogShell.Create(title, AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), 480);
        ok.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }

    /// <summary>
    /// 跑一件有进度的事（下载 + 解压）并把它显示出来：一个进度条加一行状态，做完自己关。
    /// 失败不在这里弹框，而是把异常交回调用方——那一步要连「日志在哪」一起说，只有调用方知道。
    /// </summary>
    public static async Task<(T? Result, Exception? Error)> RunWithProgressAsync<T>(
        Window owner,
        string title,
        string message,
        Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        var status = new TextBlock
        {
            Text = message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AgentDialogUi.Brush("DfInk2")
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6 };

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20, 20, 20, 20) };
        body.Children.Add(status);
        body.Children.Add(bar);

        var window = DialogShell.Create(title, body, 470, 120);
        T? result = default;
        Exception? error = null;

        window.Opened += async (_, _) =>
        {
            var progress = new Progress<double>(value => Dispatcher.UIThread.Post(() =>
            {
                bar.Value = value;
                status.Text = $"{message}  {(value * 100):0}%";
            }));
            try
            {
                result = await work(progress, CancellationToken.None);
            }
            catch (Exception failure)
            {
                error = failure;
            }
            window.Close();
        };

        await window.ShowDialog(owner);
        return (result, error);
    }

    /// <summary>
    /// 发行说明是一段 Markdown 原文。这里**只读、可选中、可滚动**地铺出来，不做渲染——
    /// 渲染要引一套 Markdown 库，而这里真正要传达的是「这一版改了什么」这件事本身。
    /// </summary>
    private static Control BuildNotes(string? notes)
    {
        var text = string.IsNullOrWhiteSpace(notes) ? "这一版没有写发行说明。" : notes.Trim();
        return new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = NotesHeight,
            FontSize = 11,
            Background = AgentDialogUi.Brush("DfSurface2"),
            Foreground = AgentDialogUi.Brush("DfInk2"),
            VerticalContentAlignment = VerticalAlignment.Top
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };
}
