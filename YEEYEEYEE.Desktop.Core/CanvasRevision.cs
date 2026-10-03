using System.Security.Cryptography;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 画布内容的修订号：整份字节的 SHA-256 取前 48 位。
///
/// **它是内容哈希，不是递增计数**，这一点决定了它能做什么、不能做什么：
/// · 能做：两端对同一份文件算出来必然一致，所以可以拿它做「是不是同一张画布 / 我这手里这份是不是最新」的比对；
/// · 不能做：它排不出大小，谁也不能拿它比「谁更新」（拿它比大小，约一半的判断会反过来）。
///
/// 服务端与桌面端**必须用同一份实现**，否则两边对同一张画布会算出不同的数，握手永远不成立。
/// 所以它住在这里，服务端的项目画布存储直接用这一个。
/// </summary>
public static class CanvasRevision
{
    public static long Of(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var digest = SHA256.HashData(bytes);
        return ((long)digest[0] << 40) | ((long)digest[1] << 32) | ((long)digest[2] << 24) |
               ((long)digest[3] << 16) | ((long)digest[4] << 8) | digest[5];
    }

    public static long OfFile(string path) => Of(File.ReadAllBytes(path));
}
