namespace YEEYEEYEE.Desktop;

/// <summary>
/// 更新这条链路的入口：共用两个 HttpClient（查与下载的超时不是一回事）+ 两个动作。
///
/// 为什么查与下载分开：检查更新发生在启动流程里，必须几秒内出结果，不然就是在拖慢启动；
/// 而下载可能是几十上百 MB，用同一个 15 秒超时会让大包永远下不完。
/// </summary>
public static class UpdateService
{
    /// <summary>查发行版信息：短超时，失败就让调用方如实报。</summary>
    private static readonly HttpClient CheckClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>下载更新包：给足时间，真正的取消交给令牌。</summary>
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        UpdateChecker.CheckAsync(CheckClient, AppVersion.Current, UpdateChecker.DefaultRepository, cancellationToken);

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
