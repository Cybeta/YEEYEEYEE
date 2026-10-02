using Avalonia.Media.Imaging;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 每个厂家一张「形象」：开奖的许愿那一拍用它，放不进去就用回自绘的 ◈ 徽记。
///
/// **图片来源是你自己的文件，程序不联网、也不内置任何图。** 把图放进下面任一处、
/// 文件名用厂家的 id（见 <see cref="ProviderBadges"/> / <see cref="ProviderPreset"/>），
/// 扩展名 png / jpg / jpeg / webp / gif 都认：
///
/// · 用户配置目录下的 `provider-art\`（Windows 是 `%LOCALAPPDATA%\YEEYEEYEE\provider-art\`）
/// · 程序目录下的 `provider-art\`
///
/// 前一处优先（换版本、重新发布都不会把它覆盖掉）。
///
/// 为什么仓库里不带图：网上流传的那些模型拟人形象，著作权属于各自的画师与厂商，
/// 塞进一个公开发布的仓库既涉及著作权，也容易被人读成「和官方有合作」。
/// 所以这里只提供「把图放哪儿」这个接口，用什么素材、从哪来，由你自己决定并负责。
/// </summary>
internal static class ProviderAvatar
{
    /// <summary>放形象的文件夹名（用户配置目录与程序目录下各一个）。</summary>
    public const string FolderName = "provider-art";

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    // 开奖可能被反复点开，而 Bitmap 从磁盘读一次就够；按厂家 id 缓存。
    // 记 null 也要缓存：否则每次开奖都会把不存在的路径再遍历一遍。
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取这一家的形象；没有就返回 null（调用方用自绘徽记）。</summary>
    public static Bitmap? Load(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        if (Cache.TryGetValue(providerId, out var cached)) return cached;

        Bitmap? bitmap = null;
        if (Locate(providerId) is { } path)
        {
            try
            {
                bitmap = new Bitmap(path);
            }
            catch
            {
                // 文件坏了 / 不是图片：当成没有。不能因为一张形象图让开奖整场打不开。
                bitmap = null;
            }
        }

        Cache[providerId] = bitmap;
        return bitmap;
    }

    /// <summary>这一家的形象是从哪个文件读的（界面上如实交代来源）；没有就是 null。</summary>
    public static string? Locate(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        foreach (var root in Roots())
        {
            foreach (var extension in Extensions)
            {
                var path = Path.Combine(root, providerId + extension);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>两个可以放形象的目录（给设置页与文档显示用）。</summary>
    public static IEnumerable<string> Roots()
    {
        yield return Path.Combine(AppPaths.UserConfigDirectory, FolderName);
        yield return Path.Combine(AppPaths.ProgramRoot, FolderName);
    }
}
