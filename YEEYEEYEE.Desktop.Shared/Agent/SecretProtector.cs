using System.Security.Cryptography;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 密钥的落盘保护：**按平台挑一档能用的方案**，配置文件里不出现明文密钥。
///
/// 四档方案（写盘时带前缀，读回时按前缀分派，所以换平台后旧文件能被正确识别为「解不开」而不是被当成明文）：
/// - <c>dpapi:</c>    Windows DPAPI（当前用户作用域）。与旧版本完全一致，旧的 <c>ai-config.json</c> 原样可读。
/// - <c>keychain:</c> macOS 钥匙串：主密钥进钥匙串，配置文件里只放 AES-GCM 密文。
/// - <c>aesgcm:</c>   本机密钥文件 + AES-256-GCM（没有系统密钥库时的回退，密钥文件在 Unix 上收 0600）。
/// - <c>plain:</c>    未加密（最后一档兜底，界面会如实提示）。
///
/// **边界必须说清楚**：这一层挡的是「把配置文件拷走」——备份、云同步、打包发人、误提交仓库；
/// 它**挡不住**以同一个账户运行的进程（DPAPI 与钥匙串同理：解密能力本来就在同一信任边界内）。
/// 也正因绑定了账户与设备，配置换账户或换机器就解不开，只能重新填——这是有意的：
/// 宁可让用户重填一次，也不要把密钥变成能随配置文件一起搬走的明文。
/// </summary>
public static class SecretProtector
{
    /// <summary>DPAPI 方案前缀。保留旧名字：旧配置与既有调用方都按 <c>dpapi:</c> 判断是否已有密文。</summary>
    public const string Prefix = DpapiSecretCipher.SchemeName + ":";

    /// <summary>当前平台的落盘方案是否真的加密。false 表示只能明文保存，界面必须提示。</summary>
    public static bool ProtectsAtRest => Preferred.ProtectsAtRest;

    /// <summary>当前落盘方案的说明，给设置界面显示。</summary>
    public static string StorageDescription => Preferred.Description;

    /// <summary>
    /// 加密。空值原样返回；已按某种方案落盘的不重复加密。
    /// 首选方案不可用时自动退到下一档（钥匙串上锁、配置目录不可写等），最后才退到 <c>plain:</c>——
    /// 无论退到哪一档，前缀都会如实写明用的是哪一种。
    /// </summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain ?? string.Empty;
        if (IsProtected(plain)) return plain;

        foreach (var cipher in Candidates())
        {
            try
            {
                return cipher.Scheme + ":" + cipher.Protect(plain);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
                or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                // 这一档在本机不可用：换下一档，不把「存不下去」这种实现细节抛给用户。
            }
        }

        return PlaintextSecretCipher.SchemeName + ":" + plain;
    }

    /// <summary>
    /// 解密。失败时返回 null（密文来自别的账户 / 机器 / 平台，或密文损坏），由调用方决定怎么提示。
    /// **不抛异常也不静默返回空串**——静默返回空串会让用户以为「密钥丢了」却不知道为什么。
    /// 没有任何方案前缀的值按旧配置的明文处理，原样返回，由保存流程负责迁移。
    /// </summary>
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!TrySplitKnownScheme(value, out var scheme, out var payload)) return value;

        // 方案认识但本机用不了（例如把 macOS 上的配置拷到 Windows）也返回 null：
        // 这时**必须**让上层提示重新填写，绝不能让密文被当成密钥去请求。
        return Find(scheme)?.Unprotect(payload);
    }

    /// <summary>
    /// 这串值是否带**本机认识的**方案前缀。注意它不等于「已加密」：
    /// <c>plain:</c> 也算带前缀，判断是否真的加密请用 <see cref="IsEncryptedAtRest"/>。
    /// </summary>
    public static bool IsProtected(string? value) =>
        value is not null && TrySplitKnownScheme(value, out var scheme, out _) && Find(scheme) is not null;

    /// <summary>这串值是否**真的**以密文落盘（dpapi / keychain / aesgcm 为真，plain 与裸明文为假）。</summary>
    public static bool IsEncryptedAtRest(string? value) =>
        value is not null && TrySplitKnownScheme(value, out var scheme, out _)
        && Find(scheme) is { ProtectsAtRest: true };

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

    /// <summary>
    /// 输入框里那串字是不是「已存密钥的脱敏显示」——是的话，保存时必须按**没改过**处理。
    ///
    /// 为什么需要它：密钥框不再用密码字符把整串盖住，而是显示成 <c>sk-c…e7ec</c>，
    /// 让人一眼认得出填的是哪一把。这串字于是**长得像密钥、却不能当密钥用**；
    /// 不认它，就等于把「sk-c…e7ec」当成新密钥写回配置文件，下一次请求必然 401。
    ///
    /// 两边都卡住：必须与 <see cref="Describe"/> 逐字相同，而且**不等于真值本身**——
    /// 后者是防「有人真的把密钥取成这个形状」，那时它就该被当成真密钥。
    /// </summary>
    public static bool IsRedactedDisplayOf(string? text, string? stored) =>
        !string.IsNullOrEmpty(stored)
        && text is not null
        && !string.Equals(text, stored, StringComparison.Ordinal)
        && string.Equals(text, Describe(stored), StringComparison.Ordinal);

    /// <summary>
    /// 密钥框提交时到底该用哪个值——「没改」与「改成空」在这里分开。
    ///
    /// 四种情形，只有第三种是换新密钥：
    /// · 框里是**脱敏显示**（<c>sk-c…e7ec</c>）→ 盘上那份原样保留（照抄会把脱敏串当密钥存进去，下一次必然 401）；
    /// · 框里空着、而盘上那份**解不开** → 保留（解不开是"这个账户读不出"，不是"用户想删"）；
    /// · 框里是新敲的 → 用它；
    /// · 框里空着、盘上那份可读 → 清掉（留空就是"我不要了"，这条语义不能悄悄改掉）。
    ///
    /// 抽成函数而不是写在设置页里，是因为这一段**出过一次真事故**（刚敲的密钥被后一次保存擦成空），
    /// 摆在共享层才测得动。
    /// </summary>
    public static string ResolveTypedKey(string? typedText, string storedKey, bool storedUnreadable)
    {
        var typed = typedText?.Trim() ?? string.Empty;
        if (IsRedactedDisplayOf(typed, storedKey)) return storedKey;
        if (typed.Length > 0) return typed;
        return storedUnreadable ? storedKey : string.Empty;
    }

    /// <summary>
    /// 当前平台的首选方案。可用 <c>YEEYEEYEE_SECRET_SCHEME</c>（dpapi / keychain / aesgcm / plain）
    /// 显式指定，供自动化测试与「有系统密钥库但用户更想用本机密钥文件」这类选择使用。
    /// 每次现算而不是缓存：环境变量在同一次进程内可能被测试切换。
    /// </summary>
    private static ISecretCipher Preferred
    {
        get
        {
            var forced = EnvCompat.Get("SECRET_SCHEME");
            if (!string.IsNullOrWhiteSpace(forced) && Find(forced.Trim().ToLowerInvariant()) is { } chosen) return chosen;

            if (DpapiSecretCipher.IsAvailable) return DpapiSecretCipher.Instance;
            if (KeychainSecretCipher.IsAvailable) return KeychainSecretCipher.Instance;
            return AesGcmSecretCipher.Instance;
        }
    }

    /// <summary>落盘时的尝试顺序：首选方案，然后本机密钥文件，最后（在 Protect 里）才是明文。</summary>
    private static IEnumerable<ISecretCipher> Candidates()
    {
        var preferred = Preferred;
        yield return preferred;
        if (!ReferenceEquals(preferred, AesGcmSecretCipher.Instance)) yield return AesGcmSecretCipher.Instance;
    }

    private static ISecretCipher? Find(string scheme) => scheme switch
    {
        DpapiSecretCipher.SchemeName when DpapiSecretCipher.IsAvailable => DpapiSecretCipher.Instance,
        KeychainSecretCipher.SchemeName when KeychainSecretCipher.IsAvailable => KeychainSecretCipher.Instance,
        AesGcmSecretCipher.SchemeName => AesGcmSecretCipher.Instance,
        PlaintextSecretCipher.SchemeName => PlaintextSecretCipher.Instance,
        _ => null
    };

    /// <summary>
    /// 拆出方案前缀。**只认已知方案**：不认识的前缀一律按旧配置的明文处理，
    /// 这样用户填的密钥里万一带冒号（<c>sk-proj:xxx</c>）也不会被误判成密文而读不出来。
    /// </summary>
    private static bool TrySplitKnownScheme(string value, out string scheme, out string payload)
    {
        scheme = string.Empty;
        payload = string.Empty;

        var separator = value.IndexOf(':');
        if (separator <= 0) return false;

        var candidate = value[..separator];
        if (candidate is not (DpapiSecretCipher.SchemeName or KeychainSecretCipher.SchemeName
            or AesGcmSecretCipher.SchemeName or PlaintextSecretCipher.SchemeName)) return false;

        scheme = candidate;
        payload = value[(separator + 1)..];
        return true;
    }
}
