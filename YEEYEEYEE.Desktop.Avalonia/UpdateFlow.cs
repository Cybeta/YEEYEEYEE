using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 更新这件事的流程编排：启动时报上一次结果 + 静默查一次；设置页里手动查；用户确认后下载、重启、替换。
///
/// 三层分工：<see cref="UpdateChecker"/> 只读数据，<see cref="UpdateInstaller"/> 只动文件与进程，
/// 这里只决定「什么时候问、问什么、拿到答复做什么」。界面细节都在 <see cref="UpdateDialog"/>。
/// </summary>
internal static class UpdateFlow
{
    /// <summary>启动时的处理只做一次：窗口重开（换项目）不该再弹一遍。</summary>
    private static bool startupHandled;

    /// <summary>
    /// 启动后做两件事：先把**上一次更新的结果**如实报出来（成功就列出更新内容，失败就说失败在哪），
    /// 再静默查一次有没有新版本。
    /// </summary>
    public static async Task HandleStartupAsync(Window owner)
    {
        if (startupHandled) return;
        startupHandled = true;

        await ReportLastAttemptAsync(owner);
        await SilentCheckAsync(owner);
    }

    /// <summary>
    /// 上一次替换的结果。判断依据是**跑起来的版本号**，不是脚本说自己成功了：
    /// 标记写着「更新到 0.2.0」而现在跑的还是 0.1.0，那就没生效，得如实说，不能报一句「更新完成」。
    /// </summary>
    private static async Task ReportLastAttemptAsync(Window owner)
    {
        var marker = UpdateInstaller.ReadMarker();
        var last = UpdateInstaller.ReadLastResult();
        var current = AppVersion.Text(AppVersion.Current);

        if (marker is not null)
        {
            UpdateInstaller.ClearMarker();
            if (string.Equals(current, marker.ToVersion, StringComparison.OrdinalIgnoreCase))
            {
                UpdateInstaller.ClearLastResult();
                await UpdateDialog.ShowCompletedAsync(owner, marker);
                return;
            }

            var reason = last?.Error is { Length: > 0 } text
                ? text
                : "替换脚本没有完成，或者替换后没有生效。";
            UpdateInstaller.ClearLastResult();
            await UpdateDialog.ShowNoticeAsync(
                owner,
                "更新没有生效",
                $"上次尝试更新到 {marker.ToVersion}，但程序现在仍然是 {current}。\n{reason}{LogHint(last?.LogPath)}",
                AgentNoteLevel.Error);
            return;
        }

        if (last is { Ok: false })
        {
            UpdateInstaller.ClearLastResult();
            await UpdateDialog.ShowNoticeAsync(
                owner,
                "上次更新失败",
                $"{last.Error}{LogHint(last.LogPath)}",
                AgentNoteLevel.Error);
            return;
        }

        if (last is { Ok: true }) UpdateInstaller.ClearLastResult();
    }

    /// <summary>
    /// 启动时的静默检查：**失败不打扰用户**（设置页里有手动入口，想看原因去那里看）。
    /// 只有真的发现新版才弹窗。
    /// </summary>
    private static async Task SilentCheckAsync(Window owner)
    {
        try
        {
            // 让启动页先落位：动效还没跑完就弹窗，观感上是「刚打开就被打断」。
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            var check = await UpdateService.CheckAsync();
            if (!check.HasUpdate) return;
            await PromptAndMaybeInstallAsync(owner, check);
        }
        catch
        {
            // 启动路径上的任何意外都不该冒泡出去。
        }
    }

    /// <summary>设置页里那个「检查更新」按钮：手动查，成功失败都要说清楚。</summary>
    public static async Task CheckManuallyAsync(Window owner)
    {
        var (check, error) = await UpdateDialog.RunWithProgressAsync(
            owner,
            "检查更新",
            "正在查询最新发行版",
            (_, token) => UpdateService.CheckAsync(token));

        if (error is not null)
        {
            await UpdateDialog.ShowNoticeAsync(owner, "检查更新失败", Describe(error), AgentNoteLevel.Warning);
            return;
        }

        if (check is null) return;

        if (check.State == UpdateCheckState.Failed)
        {
            await UpdateDialog.ShowNoticeAsync(owner, "检查更新失败", check.Message, AgentNoteLevel.Warning);
            return;
        }

        if (!check.HasUpdate)
        {
            await UpdateDialog.ShowNoticeAsync(owner, "检查更新", check.Message, AgentNoteLevel.Success);
            return;
        }

        await PromptAndMaybeInstallAsync(owner, check);
    }

    private static async Task PromptAndMaybeInstallAsync(Window owner, UpdateCheckResult check)
    {
        var blocker = UpdateService.DescribeSelfUpdateBlocker(check);
        var choice = await UpdateDialog.AskAsync(owner, check, blocker);

        switch (choice)
        {
            case UpdateChoice.OpenPage:
                OpenInBrowser(check.Release?.HtmlUrl);
                break;
            case UpdateChoice.Install:
                await InstallAsync(owner, check);
                break;
        }
    }

    private static async Task InstallAsync(Window owner, UpdateCheckResult check)
    {
        var asset = UpdateChecker.FindPackage(check.Release);
        if (asset is null || check.Latest is not { } latest) return;
        var version = AppVersion.Text(latest);

        var (package, error) = await UpdateDialog.RunWithProgressAsync(
            owner,
            "下载更新包",
            $"正在下载 {version}",
            (progress, token) => UpdateService.DownloadAsync(asset, version, progress, token));

        if (error is not null || package is null)
        {
            await UpdateDialog.ShowNoticeAsync(owner, "下载更新包失败", Describe(error), AgentNoteLevel.Error);
            return;
        }

        if (!await UpdateDialog.ConfirmRestartAsync(owner, package, package.Size)) return;

        // 写标记 → 拉起替换脚本 → 退出应用。顺序不能换：脚本等的是「这个进程消失」，
        // 而标记必须在退出前落盘，否则重启后就没有任何依据说清这次换了什么。
        UpdateInstaller.WriteMarker(new PendingUpdateInfo(
            AppVersion.Text(AppVersion.Current),
            version,
            check.Release?.Body ?? string.Empty,
            check.Release?.HtmlUrl ?? string.Empty));
        UpdateInstaller.ClearLastResult();

        try
        {
            UpdateInstaller.Launch(package);
        }
        catch (Exception failure)
        {
            UpdateInstaller.ClearMarker();
            await UpdateDialog.ShowNoticeAsync(owner, "没法启动替换脚本", Describe(failure), AgentNoteLevel.Error);
            return;
        }

        CloseAllWindows();
    }

    /// <summary>关掉所有窗口让进程真正退出——脚本就是靠「进程没了」才敢动文件的。</summary>
    private static void CloseAllWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows.ToList()) window.Close();
    }

    private static void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // 打不开浏览器不是致命问题：用户自己也能在发布页看到地址。
        }
    }

    private static string LogHint(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : $"\n脚本日志：{path}";

    private static string Describe(Exception? error) => error switch
    {
        null => "没有拿到具体原因。",
        HttpRequestException http => "网络请求失败：" + http.Message,
        TaskCanceledException or OperationCanceledException => "连接超时或被取消。",
        IOException io => "读写文件失败：" + io.Message,
        UnauthorizedAccessException => "没有权限写入目标目录。",
        _ => error.Message
    };
}
