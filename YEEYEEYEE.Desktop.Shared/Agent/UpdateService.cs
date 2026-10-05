using System.Diagnostics;
using System.Net;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 更新这条链路的入口：共用两个 HttpClient（查与下载的超时不是一回事）+ 两个动作。
///
/// 为什么查与下载分开：检查更新发生在启动流程里，必须几秒内出结果，不然就是在拖慢启动；
/// 而下载可能是几十上百 MB，用同一个 15 秒超时会让大包永远下不完。
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// 查发行版信息：短超时，失败就让调用方如实报。
    /// </summary>
    private static readonly HttpClient CheckClient = CreateClient(TimeSpan.FromSeconds(15));

    /// <summary>下载更新包：给足时间，真正的取消交给令牌。</summary>
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(30));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (ResolveProxy() is { } proxy) handler.Proxy = proxy;
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// 更新这条链路要用的代理。**为什么单给它一份**：查发行版与下包都落在 github.com 上，
    /// 而国内拉 GitHub 基本都要走代理；很多机器上代理**只配在 git 里**（`https.proxy`），
    /// 应用默认只认系统设置——结果就是「git 能推代码、应用下不了更新包」。
    /// 实测卡住过：报「网络请求失败」，一路查到 git 配置里那个 `127.0.0.1:10809`。
    ///
    /// 依次看：`YEEYEEYEE_PROXY`（自己人排查用）→ `HTTPS_PROXY` / `HTTP_PROXY`（通用约定）
    /// → git 配置里的 `https.proxy` / `http.proxy`（开发者机器上通常就配在那儿）→ 都没有就用系统默认。
    /// </summary>
    internal static IWebProxy? ResolveProxy()
    {
        // 我们自己的那个变量放最前面：排查时想临时指到别处，不该被机器上的 HTTPS_PROXY 挡回去。
        if (EnvCompat.Get("PROXY") is { Length: > 0 } custom && Uri.TryCreate(custom, UriKind.Absolute, out var fromCustom))
            return new WebProxy(fromCustom);

        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var fromEnvironment))
                return new WebProxy(fromEnvironment);
        }

        foreach (var key in new[] { "https.proxy", "http.proxy" })
            if (GitConfig(key) is { Length: > 0 } value && Uri.TryCreate(value, UriKind.Absolute, out var fromGit))
                return new WebProxy(fromGit);

        return null;
    }

    /// <summary>读 git 配置里的一个键（读不到、没装 git、超时都返回 null）。只读，不写任何东西。</summary>
    private static string? GitConfig(string key)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("config");
            start.ArgumentList.Add("--get");
            start.ArgumentList.Add(key);
            using var process = Process.Start(start);
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return text.Length > 0 ? text : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(
        bool force = false,
        CancellationToken cancellationToken = default,
        string? cacheDirectory = null)
    {
        var now = DateTimeOffset.UtcNow;
        var cache = UpdateCheckStore.Load(cacheDirectory);
        var decision = UpdateCheckPolicy.Decide(cache, force, now);

        // 已知还在限流窗口里：连问都不问，直接如实说等到什么时候（不去撞那一下下线）。
        if (decision.Kind == UpdateCheckDecisionKind.WaitForRateLimit)
            return new UpdateCheckResult(
                UpdateCheckState.Failed, AppVersion.Current, null, null, decision.Note, cache?.RateLimitedUntil);

        // 六小时内查过：拿上次读到的事实重算一遍结论，并把「这是缓存」说清楚——
        // 不说的话，用户会以为这就是刚刚的结果。
        if (decision.Kind == UpdateCheckDecisionKind.UseCache && cache is { } remembered)
        {
            var reused = UpdateChecker.Evaluate(AppVersion.Current, remembered.Release);
            return reused with { Message = $"{reused.Message}（{decision.Note}）" };
        }

        var result = await UpdateChecker.CheckAsync(
            CheckClient, AppVersion.Current, UpdateChecker.DefaultRepository, cancellationToken);

        // 只把「问到过」与「被限流」记下来：
        // · 问到过（含「还没有发行版」）→ 事实留着，六小时内不再问；
        // · 被限流 → 记住重置时间，这段时间连问都不问；**上一次的事实继续留着**（它只是旧了几小时）；
        // · 网络不通那种**不缓存**：那是本机或线路的一时问题，下次启动该再试一次。
        if (result.RateLimitedUntil is { } until)
            UpdateCheckStore.Save(new UpdateCheckCache(cache?.CheckedAt ?? default, cache?.Release, until), cacheDirectory);
        else if (result.State != UpdateCheckState.Failed)
            UpdateCheckStore.Save(new UpdateCheckCache(now, result.Release, null), cacheDirectory);

        return result;
    }

    public static Task<UpdatePackage> DownloadAsync(
        ReleaseAsset asset,
        string version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        UpdateInstaller.DownloadAsync(DownloadClient, asset, version, progress, cancellationToken);

    /// <summary>
    /// 能不能在应用内自己换掉自己。两个条件缺一不可：
    /// 程序目录可写（便携部署），以及这次替换确实有包可下。
    /// 不满足时返回一句**给用户看的原因**，界面据此只留「去发布页」。
    /// </summary>
    public static string? DescribeSelfUpdateBlocker(UpdateCheckResult result)
    {
        if (UpdateChecker.FindPackage(result.Release) is null)
            return "这个发行版没有附带 zip 更新包，只能在发布页手动下载。";

        if (!AppPaths.CanWriteProgramRoot())
            return $"程序目录不可写（{AppPaths.ProgramRoot}），无法在应用内替换；请从发布页下载后手动安装。";

        return null;
    }
}
