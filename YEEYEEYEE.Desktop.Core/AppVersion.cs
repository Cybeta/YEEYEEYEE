using System.Reflection;

namespace YEEYEEYEE.Desktop;

/// <summary>界面版本读取入口程序集，桌面 csproj 的 Version 是产品版本来源。</summary>
public static class AppVersion
{
    public static Version Current { get; } = ReadCurrent();
    public static string Display => "v" + Text(Current);

    /// <summary>修订号为零时显示三段，否则保留第四段。</summary>
    public static string Text(Version version) =>
        version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>接受两至四段数字以及 v 前缀，忽略预发布和构建后缀。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        var cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) trimmed = trimmed[..cut];
        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], out numbers[i])) return false;
        }
        version = parts.Length == 4
            ? new Version(numbers[0], numbers[1], numbers[2], numbers[3])
            : new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private static Version ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var fromInfo)) return fromInfo;
        var name = assembly.GetName().Version;
        if (name is not null && name.Major >= 0)
            return new Version(name.Major, name.Minor, Math.Max(name.Build, 0), Math.Max(name.Revision, 0));
        return new Version(0, 0, 0);
    }
}
