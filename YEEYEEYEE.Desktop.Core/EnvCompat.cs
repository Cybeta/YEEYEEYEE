namespace YEEYEEYEE.Desktop;

/// <summary>
/// 环境变量读取：**优先新名 <c>YEEYEEYEE_*</c>，回退旧名 <c>DREAMFORGE_*</c>**。
///
/// 为什么留着旧名：这些覆盖项常被写进启动脚本、CI 变量或系统环境里。项目从 DreamForge 改名成
/// YEEYEEYEE 时，如果直接只认新名，这些已经配好的覆盖会**静默失效**——表现是「设置里明明填了，
/// 程序却按默认值跑」，很难查。所以读的时候两个名字都看，写和文档只推新名。
///
/// 配置目录名与项目目录名的兼容不在这里，见 <see cref="AppPaths"/>（那两个是磁盘上的实体，
/// 要靠「旧的还在就用旧的」来保住用户已有的密钥与项目）。
/// </summary>
public static class EnvCompat
{
    private const string Prefix = "YEEYEEYEE_";
    private const string LegacyPrefix = "DREAMFORGE_";

    /// <summary>取环境变量：新名为空（或空串）时回退旧名。</summary>
    public static string? Get(string suffix)
    {
        var value = Environment.GetEnvironmentVariable(Prefix + suffix);
        return string.IsNullOrEmpty(value)
            ? Environment.GetEnvironmentVariable(LegacyPrefix + suffix)
            : value;
    }
}
