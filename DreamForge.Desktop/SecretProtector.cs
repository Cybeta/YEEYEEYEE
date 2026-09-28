using System.Security.Cryptography;
using System.Text;

namespace DreamForge.Desktop;

/// <summary>
/// 密钥的落盘保护：用 Windows DPAPI（CurrentUser 作用域）加密后再写进配置文件。
///
/// **边界必须说清楚**：DPAPI 挡的是「把配置文件拷走」——备份、云同步、打包发人、误提交仓库；
/// 它**挡不住**以同一个 Windows 账户运行的进程（密钥与解密能力本来就在同一信任边界内，
/// 加密数据库方案同理：社区工具能扫进程内存拿到库密钥）。
/// 也正因绑定了账户，配置**换账户或换机器就解不开**，只能重新填。
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀。没有这个前缀的值一律按明文处理，用于兼容旧配置。</summary>
    public const string Prefix = "dpapi:";

    /// <summary>附加熵：让密文只对 DreamForge 自己的配置有意义，防止别处解出来的密文被拿来顶替。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DreamForge.ApiKey.v1");

    public static bool IsProtected(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>加密。空值原样返回；已加密的不重复加密。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain ?? string.Empty;
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// 解密。失败时返回 null（配置来自其它账户或机器、或密文损坏），由调用方决定怎么提示。
    /// **不抛异常也不静默返回空串**——静默返回空串会让用户以为「密钥丢了」却不知道为什么。
    /// </summary>
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!IsProtected(value)) return value;   // 旧配置的明文，原样返回，由保存流程负责迁移
        try
        {
            var cipher = Convert.FromBase64String(value[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 给界面看的脱敏描述符，例如 <c>sk-c…e7ec</c>。
    /// 只露出首尾各 4 个字符用于辨认是哪一把，**绝不回显完整密钥**（只写不回读）。
    /// </summary>
    public static string Describe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "（未填写）";
        const int visible = 4;
        return value.Length <= visible * 2
            ? new string('•', value.Length)
            : $"{value[..visible]}…{value[^visible..]}";
    }
}
