using Microsoft.Extensions.Configuration;

namespace YEEYEEYEE.Web;

/// <summary>
/// 配置读取：**优先新键 <c>YEEYEEYEE:*</c>，回退旧键 <c>DreamForge:*</c>**。
///
/// 与 <c>EnvCompat</c> 同一个理由：项目从 DreamForge 改名成 YEEYEEYEE 之后，旧键可能还留在
/// 自行维护的 appsettings、环境变量（<c>DreamForge__WebToken</c> 这种双下划线形式）或命令行参数里。
/// 只认新键会让这些覆盖项静默失效，表现成「明明配了令牌，接口却报未配置」。
/// appsettings.json 里随仓库走的那一份已经统一改成新键，回退只为照顾外部覆盖。
/// </summary>
public static class LegacyConfig
{
    /// <summary>取字符串值（新键优先）；两处都没有时返回 null。</summary>
    public static string? Text(IConfiguration configuration, string name)
        => configuration["YEEYEEYEE:" + name] ?? configuration["DreamForge:" + name];

    /// <summary>取布尔值（新键优先）；两处都没有或解析不出时返回 null——调用方自己决定默认值。</summary>
    public static bool? Flag(IConfiguration configuration, string name)
        => bool.TryParse(Text(configuration, name), out var value) ? value : null;
}
