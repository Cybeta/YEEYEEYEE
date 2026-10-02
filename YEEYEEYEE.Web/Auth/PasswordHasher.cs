using System.Security.Cryptography;
using System.Text;

namespace YEEYEEYEE.Web.Auth;

/// <summary>
/// 口令的哈希与校验。**明文口令不落盘、不进日志、不出现在任何响应里。**
///
/// 用 PBKDF2-SHA256，迭代 21 万次（OWASP 对 PBKDF2-SHA256 的现行建议值），每份口令一个随机盐。
/// 存的形式是自带参数的字符串：
/// <code>pbkdf2-sha256$210000$&lt;盐 base64&gt;$&lt;哈希 base64&gt;</code>
/// 参数跟着哈希一起存，是为了以后能提高迭代次数而不用把老用户的口令清空重设——
/// 校验时用**存下来的**参数算，而不是用当前的常量算。
///
/// 没有引入 BCrypt / Argon2 的第三方包：PBKDF2 在 .NET 基础库里就有，
/// 多引一个包就等于多一处供应链风险，而这个规模的应用用 PBKDF2 是站得住的。
/// </summary>
internal static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>口令的最短长度。存在这里是为了让「设置页的提示」与「服务端的校验」用同一个数。</summary>
    public const int MinLength = 8;
    public const int MaxLength = 200;

    /// <summary>口令不合规时返回原因，合规返回 null。服务端与界面都用它，所以写成纯函数。</summary>
    public static string? Check(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "口令不能为空";
        if (password.Length < MinLength) return $"口令至少 {MinLength} 位";
        if (password.Length > MaxLength) return $"口令最多 {MaxLength} 位";
        if (password.All(char.IsWhiteSpace)) return "口令不能只有空白字符";
        return null;
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// 校验。**任何解析失败都返回 false，不抛异常**——一个坏掉的哈希行不该让登录接口 500，
    /// 那会把「数据损坏」变成「服务不可用」，而且异常信息可能带上哈希内容。
    /// </summary>
    public static bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1000 || iterations > 2_000_000) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>会话令牌的哈希：令牌本身只发给浏览器，库里只留哈希。</summary>
    public static string HashToken(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>生成会话令牌（32 字节随机 → URL 安全的 base64）。</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
