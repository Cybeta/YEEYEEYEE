using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 一种「密钥落盘」方案：把明文变成可写进配置文件的字符串，并能反过来还原。
///
/// 前缀由 <see cref="SecretProtector"/> 统一加，实现里只处理载荷本身——
/// 这样「以前缀分派、按方案读回」的逻辑只有一份，各平台实现互不知道对方存在。
/// </summary>
internal interface ISecretCipher
{
    /// <summary>方案名，落盘时作为前缀（例如 <c>dpapi</c>）。</summary>
    string Scheme { get; }

    /// <summary>是否真的以密文落盘。false 表示只是加了前缀的明文，界面必须如实提示。</summary>
    bool ProtectsAtRest { get; }

    /// <summary>给人看的说明，例如「Windows DPAPI（当前 Windows 账户）」。</summary>
    string Description { get; }

    /// <summary>加密（不含方案前缀）。</summary>
    string Protect(string plain);

    /// <summary>解密。解不开返回 null，不抛异常也不静默返回空串。</summary>
    string? Unprotect(string payload);
}

/// <summary>
/// AES-256-GCM 载荷编解码：<c>base64(nonce ‖ tag ‖ 密文)</c>。
///
/// 抽出来是因为有两档方案共用它：macOS 把主密钥放进钥匙串，没有系统密钥库时把主密钥放进本机密钥文件；
/// 变的只是「主密钥放哪」，密文格式与代码只有一份。
/// </summary>
internal static class AesGcmPayload
{
    public const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static string Encrypt(byte[] key, string plain)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes) throw new CryptographicException($"AES-256 需要 {KeyBytes} 字节密钥，实际 {key.Length} 字节。");

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceBytes + TagBytes + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, payload, NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, payload, NonceBytes + TagBytes, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public static string? Decrypt(byte[] key, string payload)
    {
        if (key.Length != KeyBytes) return null;

        byte[] raw;
        try { raw = Convert.FromBase64String(payload); }
        catch (FormatException) { return null; }
        if (raw.Length < NonceBytes + TagBytes) return null;

        try
        {
            var plain = new byte[raw.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(
                raw.AsSpan(0, NonceBytes),
                raw.AsSpan(NonceBytes + TagBytes),
                raw.AsSpan(NonceBytes, TagBytes),
                plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // 换机器、换了密钥文件、密文被改过都走这里——如实返回「解不开」。
            return null;
        }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>
/// Windows DPAPI（当前用户作用域）。
///
/// 附加熵、作用域与旧版本用 <c>ProtectedData</c> 写下的密文**完全一致**，所以升级后旧
/// <c>ai-config.json</c> 里的 <c>dpapi:</c> 密文原样可读，用户不需要重填密钥。
/// 用 NuGet 形式的 <c>System.Security.Cryptography.ProtectedData</c> 而不是桌面框架里的同名类型：
/// 后者要求整个应用额外依赖「Windows 桌面运行时」，而我们只用它加解密一个字符串。
/// </summary>
internal sealed class DpapiSecretCipher : ISecretCipher
{
    public const string SchemeName = "dpapi";

    /// <summary>附加熵：与旧版本同一个常量。**改了它等于把所有人的旧密文作废**。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YEEYEEYEE.ApiKey.v1");

    public static DpapiSecretCipher Instance { get; } = new();

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public string Scheme => SchemeName;
    public bool ProtectsAtRest => true;
    public string Description => "Windows DPAPI（当前 Windows 账户）";

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plain)));

    public string? Unprotect(string payload)
    {
        byte[] cipher;
        try { cipher = Convert.FromBase64String(payload); }
        catch (FormatException) { return null; }
        return UnprotectBytes(cipher);
    }

    // 平台判断放在真正调用 DPAPI 的这一层：分析器据此消除 CA1416，运行期也不会在非 Windows 上误调。
    private static byte[] ProtectBytes(byte[] plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用。");
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    private static string? UnprotectBytes(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// macOS 钥匙串（Security.framework 的 SecItem* 接口）。
///
/// 密钥本体**不进配置文件**：主密钥存在钥匙串（服务名 <c>YEEYEEYEE</c>），配置文件里只有 AES-GCM 密文。
/// 这一档的强度与 Windows 的 DPAPI 同级：解密能力绑定当前登录用户与设备。
///
/// 说明两点事实：
/// 1）这里全部用运行时判断（<see cref="IsAvailable"/>）而不是条件编译，因此这段代码在任何一个平台都会
///    参与编译、被类型检查，只有真正调用时才需要 macOS。
/// 2）本机没有 macOS 可跑，这一档只做到「编译通过 + 失败可回退」：钥匙串调用一旦失败会抛异常，
///    由 <see cref="SecretProtector"/> 退到本机密钥文件方案，不会把用户卡在「存不下去」。
/// </summary>
internal sealed class KeychainSecretCipher : ISecretCipher
{
    public const string SchemeName = "keychain";

    private const string ServiceName = "YEEYEEYEE";
    private const string MasterKeyAccount = "ai-master-key";

    /// <summary>CFStringEncodingUTF8：CoreFoundation 的字符串编码常量。</summary>
    private const uint CfStringEncodingUtf8 = 0x08000100;

    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";

    public static KeychainSecretCipher Instance { get; } = new();

    public static bool IsAvailable => OperatingSystem.IsMacOS();

    public string Scheme => SchemeName;
    public bool ProtectsAtRest => true;
    public string Description => "macOS 钥匙串（主密钥在钥匙串，配置文件里只有密文）";

    public string Protect(string plain) => AesGcmPayload.Encrypt(LoadOrCreateMasterKey(), plain);

    public string? Unprotect(string payload)
    {
        var key = TryReadValue(MasterKeyAccount);
        return key is { Length: AesGcmPayload.KeyBytes } ? AesGcmPayload.Decrypt(key, payload) : null;
    }

    private static byte[] LoadOrCreateMasterKey()
    {
        if (TryReadValue(MasterKeyAccount) is { Length: AesGcmPayload.KeyBytes } existing) return existing;

        var key = RandomNumberGenerator.GetBytes(AesGcmPayload.KeyBytes);
        WriteValue(MasterKeyAccount, key);
        return key;
    }

    // ---------- SecItem 封装 ----------

    private static byte[]? TryReadValue(string account)
    {
        using var scope = new CoreFoundationScope();
        var keys = new[]
        {
            scope.Constant("kSecClass"),
            scope.String("kSecAttrService"),
            scope.String("kSecAttrAccount"),
            scope.Constant("kSecReturnData"),
            scope.Constant("kSecMatchLimit")
        };
        var values = new[]
        {
            scope.Constant("kSecClassGenericPassword"),
            scope.String(ServiceName),
            scope.String(account),
            scope.Constant("kCFBooleanTrue"),
            scope.Constant("kSecMatchLimitOne")
        };
        var query = scope.Dictionary(keys, values);

        var status = SecItemCopyMatching(query, out var result);
        if (status != 0 || result == IntPtr.Zero) return null;

        try
        {
            var length = (int)CFDataGetLength(result);
            if (length <= 0) return null;
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            return bytes;
        }
        finally
        {
            CFRelease(result);
        }
    }

    private static void WriteValue(string account, byte[] value)
    {
        // 同一个键已存在时 SecItemAdd 会返回 errSecDuplicateItem，先删掉再写。
        DeleteValue(account);

        using var scope = new CoreFoundationScope();
        var keys = new[]
        {
            scope.Constant("kSecClass"),
            scope.String("kSecAttrService"),
            scope.String("kSecAttrAccount"),
            scope.String("kSecValueData"),
            scope.Constant("kSecAttrAccessible")
        };
        var values = new[]
        {
            scope.Constant("kSecClassGenericPassword"),
            scope.String(ServiceName),
            scope.String(account),
            scope.Data(value),
            scope.Constant("kSecAttrAccessibleWhenUnlocked")
        };
        var attributes = scope.Dictionary(keys, values);

        var status = SecItemAdd(attributes, IntPtr.Zero);
        if (status != 0) throw new CryptographicException($"写入 macOS 钥匙串失败（OSStatus {status}）。");
    }

    private static void DeleteValue(string account)
    {
        using var scope = new CoreFoundationScope();
        var keys = new[] { scope.Constant("kSecClass"), scope.String("kSecAttrService"), scope.String("kSecAttrAccount") };
        var values = new[] { scope.Constant("kSecClassGenericPassword"), scope.String(ServiceName), scope.String(account) };
        SecItemDelete(scope.Dictionary(keys, values));
    }

    /// <summary>
    /// 一次调用里创建的 CoreFoundation 对象集合：统一释放，避免在异常路径上漏掉。
    /// 框架自带的常量（kSecClass 之类）不属于我们，不能释放。
    /// </summary>
    private sealed class CoreFoundationScope : IDisposable
    {
        private readonly List<IntPtr> created = new();

        public IntPtr String(string value) => Track(CFStringCreateWithCString(IntPtr.Zero, value, CfStringEncodingUtf8));

        public IntPtr Data(byte[] bytes) => Track(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

        /// <summary>取框架导出的常量。导出符号里存的是指针，需要再解一层引用。</summary>
        public IntPtr Constant(string symbol)
        {
            var handle = symbol == "kCFBooleanTrue" ? CoreFoundationHandle : SecurityHandle;
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, symbol));
        }

        /// <summary>
        /// 建字典。键值回调传 NULL：字典不持有这些对象，生存期由本 scope 负责——
        /// 这些对象从创建到调用结束一直活着，比让 CoreFoundation 参与引用计数更不容易出错。
        /// </summary>
        public IntPtr Dictionary(IntPtr[] keys, IntPtr[] values) =>
            Track(CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, IntPtr.Zero, IntPtr.Zero));

        public void Dispose()
        {
            for (var index = created.Count - 1; index >= 0; index--) CFRelease(created[index]);
            created.Clear();
        }

        private IntPtr Track(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new CryptographicException("CoreFoundation 对象创建失败。");
            created.Add(handle);
            return handle;
        }
    }

    // 懒加载：静态字段初始化必须能在非 macOS 上安全执行，所以这里只建 Lazy 对象，不碰原生库。
    private static readonly Lazy<IntPtr> coreFoundation = new(() => NativeLibrary.Load(CoreFoundationPath));
    private static readonly Lazy<IntPtr> security = new(() => NativeLibrary.Load(SecurityPath));

    private static IntPtr CoreFoundationHandle => coreFoundation.Value;
    private static IntPtr SecurityHandle => security.Value;

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(SecurityPath)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemDelete(IntPtr query);
}

/// <summary>
/// 本机密钥文件 + AES-256-GCM：没有可用系统密钥库时的回退（Linux，或钥匙串调用失败时的 macOS）。
///
/// 主密钥是随机生成的 32 字节，落在用户配置目录的 <c>ai-key.bin</c>，在 Unix 上收到 0600。
/// **强度要说清楚**：它挡的是「只把配置文件拷走」（备份、云同步、误提交仓库）；
/// 攻击者若能读到整个配置目录就能同时拿到密钥与密文，这一档弱于 DPAPI / 钥匙串。
/// 因此界面会如实显示当前用的是哪一档（见 <see cref="SecretProtector.StorageDescription"/>）。
/// </summary>
internal sealed class AesGcmSecretCipher : ISecretCipher
{
    public const string SchemeName = "aesgcm";
    private const string KeyFileName = "ai-key.bin";

    public static AesGcmSecretCipher Instance { get; } = new();

    public string Scheme => SchemeName;
    public bool ProtectsAtRest => true;
    public string Description => "本机密钥文件 + AES-256-GCM（挡拷走配置文件，弱于系统密钥库）";

    /// <summary>
    /// 密钥文件位置：默认在用户配置目录，与配置文件同处一地。
    /// 便携部署或自动化测试可用 <c>YEEYEEYEE_SECRET_KEYFILE</c> 指定到别处（与 YEEYEEYEE_CONFIG 对称）。
    /// </summary>
    public string KeyFilePath =>
        EnvCompat.Get("SECRET_KEYFILE") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : AppPaths.ResolveAppFile(KeyFileName);

    public string Protect(string plain) => AesGcmPayload.Encrypt(LoadOrCreateKey(), plain);

    public string? Unprotect(string payload) =>
        TryLoadKey() is { } key ? AesGcmPayload.Decrypt(key, payload) : null;

    private byte[] LoadOrCreateKey()
    {
        if (TryLoadKey() is { } existing) return existing;

        var path = KeyFilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var key = RandomNumberGenerator.GetBytes(AesGcmPayload.KeyBytes);
        // 先写临时文件再改名：中途失败不会留下半截密钥——半截密钥会把已存下的密文全部作废。
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, key);
        RestrictToOwner(temporary);
        try
        {
            File.Move(temporary, path);
        }
        catch (IOException)
        {
            // 并发创建：已经有别的实例写好了就用它的，别覆盖（覆盖会让对方刚加密的密文解不开）。
            if (!File.Exists(path)) throw;
            File.Delete(temporary);
            return TryLoadKey() ?? key;
        }
        RestrictToOwner(path);
        return key;
    }

    private byte[]? TryLoadKey()
    {
        try
        {
            var path = KeyFilePath;
            if (!File.Exists(path)) return null;
            var key = File.ReadAllBytes(path);
            return key.Length == AesGcmPayload.KeyBytes ? key : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Unix 上把密钥文件收紧到「只有属主可读写」；Windows 的访问控制由 ACL 决定，这里不动。</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限收紧失败不阻断保存：文件仍写出去了，只是没有额外收紧。
        }
    }
}

/// <summary>
/// 明文保存：连密钥文件都写不出来时的最后一档（只加 <c>plain:</c> 前缀，不做任何加密）。
/// 目的是**不把功能卡死**，同时让配置文件自描述「这一份没有加密」，界面据此如实告警。
/// </summary>
internal sealed class PlaintextSecretCipher : ISecretCipher
{
    public const string SchemeName = "plain";

    public static PlaintextSecretCipher Instance { get; } = new();

    public string Scheme => SchemeName;
    public bool ProtectsAtRest => false;
    public string Description => "未加密（明文保存在配置文件里）";

    public string Protect(string plain) => plain;

    public string? Unprotect(string payload) => payload;
}
