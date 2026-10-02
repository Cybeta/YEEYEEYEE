using System.Reflection;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 程序版本号的**唯一来源**。
///
/// 为什么要有这一层：界面标题栏、左栏底部、设置页以前是三处写死的字符串，而程序集里另有一个
/// <c>&lt;Version&gt;</c>——四处各说各话，改一处不会带动别处，升级时必然漏。现在界面全部读这里，
/// 而这里只读程序集的版本属性，于是「版本号」只有一个地方需要改（csproj 的 <c>&lt;Version&gt;</c>）。
///
/// 读的是**入口程序集**（真正在跑的那个 exe），而不是本类所在的库：库的版本与产品的版本不是一回事。
/// </summary>
public static class AppVersion
{
    /// <summary>当前运行的程序版本；读不到时退回 0.0.0（不抛异常——版本读不到不该让程序起不来）。</summary>
    public static Version Current { get; } = ReadCurrent();

    /// <summary>界面上显示的写法，例如 <c>v0.1.0</c>。</summary>
    public static string Display => "v" + Text(Current);

    /// <summary>
    /// 把版本号写成 <c>0.1.0</c> 这种三段式；修订号为 0 时不写第四段。
    /// 不用 <see cref="Version.ToString()"/> 是因为它会输出 <c>0.1.0.0</c>，与标签里的写法对不上。
    /// </summary>
    public static string Text(Version version) =>
        version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>
    /// 解析发行版标签或版本字符串：接受 <c>v0.1.0</c>、<c>0.1.0</c>、<c>1.2</c>，
    /// 以及带预发布后缀的 <c>v0.2.0-beta.1</c>（后缀被丢掉，只比数字部分）。
    /// 解析不出来时返回 false——调用方据此如实报「版本号看不懂」，不要当成「没有新版本」。
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];

        // 预发布/构建元数据不参与大小比较：0.2.0-beta 与 0.2.0 对「有没有新版」是同一档。
        var cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) trimmed = trimmed[..cut];
        if (trimmed.Length == 0) return false;

        // 只接受 2~3 段纯数字（"1.2" 补成 1.2.0；"1" 或 "1.2.3.4" 算看不懂）。
        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        foreach (var part in parts)
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) return false;

        version = new Version(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            parts.Length > 2 ? int.Parse(parts[2]) : 0);
        return true;
    }

    private static Version ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        // InformationalVersion 最贴近「产品版本」，但可能带 +构建号（SourceLink 会加），要截掉。
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var cut = informational.IndexOf('+');
            if (cut >= 0) informational = informational[..cut];
            if (TryParse(informational, out var fromInfo)) return fromInfo;
        }

        var name = assembly.GetName().Version;
        if (name is not null && name.Major >= 0) return new Version(name.Major, name.Minor, Math.Max(name.Build, 0), Math.Max(name.Revision, 0));

        return new Version(0, 0, 0);
    }
}
