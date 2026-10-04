using System.Security.Cryptography;

namespace YEEYEEYEE.Web.Auth;

/// <summary>
/// 首次启动时的那把一次性钥匙：没配部署令牌时，进程自己生成一串随机令牌，
/// 打在启动日志里、并写在账号库旁边；**第一个管理员建出来之后就作废**。
///
/// 为什么要有它：在这之前「没配 <c>YEEYEEYEE_SETUP_TOKEN</c>」等于「谁先打开页面谁就是管理员」。
/// 局域网自用很方便，但只要这个端口露到公网，抢注往往发生在你打开它之前。
/// 而要求每个部署者都先去设一个环境变量，又会毁掉「`docker compose up` 就能看」这条路——
/// 所以取中间那条：钥匙由程序生成，你只要读一眼日志（或那个文件）。
///
/// 三条刻意的取舍：
/// · **配了部署令牌时这套完全不动**：<c>YEEYEEYEE__SetupToken</c> 是部署者自己定的，优先于它。
/// · **钥匙写在账号库旁边**（容器里就是那个卷），所以重启不会换一把——
///   日志滚掉之后还能从文件里捞回来；`docker compose down -v` 清卷时一起没。
/// · **建出第一个管理员就删掉**：它不是长期凭据，留在盘上只会多一个秘密。
/// </summary>
internal static class BootstrapToken
{
    /// <summary>文件名。挨着账号库放一个点开头的文件，一眼看得出不是业务数据。</summary>
    public const string FileName = ".setup-token";

    private static string? current;
    private static string? path;

    /// <summary>当前这把钥匙；没有（已有账号 / 没生成 / 已用掉）就是 null。</summary>
    public static string? Current => current;

    /// <summary>
    /// 启动时调一次：还没有任何账号、又没有部署令牌时，确保有一把钥匙；
    /// 返回**要打给用户看的那几句话**（不需要就打空串）。
    /// 「还没有账号」由调用方数出来传进来——这个类只管钥匙，不去读账号库。
    /// </summary>
    public static string Ensure(string? userDatabasePath, int userCount, bool deploymentTokenConfigured)
    {
        current = null;
        path = string.IsNullOrWhiteSpace(userDatabasePath)
            ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(userDatabasePath))!, FileName);

        // 三种情况这套都不参与：部署者自己配了令牌、已经有账号了、或压根不知道账号库在哪。
        // 前两种时盘上若还留着一把旧的，顺手清掉——它已经不是钥匙了，留着只是多一个秘密。
        if (deploymentTokenConfigured || userCount > 0 || path is null)
        {
            TryDelete();
            return string.Empty;
        }

        // 已经有钥匙就用现成那把：**重启不该换钥匙**，否则用户刚抄下来的那串立刻作废，
        // 而他手上只有上一次的日志。
        if (TryRead(path, out var existing))
        {
            current = existing;
            return Banner(existing, first: false);
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        current = generated;
        // 写不进去也能用：日志里照样有。只是重启之后会换一把新的。
        TryWrite(path, generated);
        return Banner(generated, first: true);
    }

    /// <summary>钥匙用掉了：删文件、清内存。之后再问 <see cref="Current"/> 就是 null（界面据此不再要求填它）。</summary>
    public static void Consume()
    {
        current = null;
        TryDelete();
    }

    private static string Banner(string token, bool first) =>
        (first ? "还没有管理员账号：已生成一把一次性初始化令牌。" : "还没有管理员账号：沿用上次那把初始化令牌。")
        + Environment.NewLine
        + "    初始化令牌：" + token
        + Environment.NewLine
        + "    用它建第一个管理员（网页上「初始化令牌」那一栏，或请求头 X-Setup-Token）。"
        + "建完就作废，不会一直留着。"
        + Environment.NewLine
        + "    换个令牌：设环境变量 YEEYEEYEE_SETUP_TOKEN 后重启。"
        + (path is null ? string.Empty : $"（这串也在 {path} 里，重启不会换。）");

    private static void TryDelete()
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 删不掉也不该拦住建号：内存里那把已经清了，本次进程内它已经失效。
            Console.WriteLine($"初始化令牌的文件删不掉（{error.Message}），但它已经失效；顺手删掉 {path} 更干净。");
        }
    }

    private static bool TryRead(string file, out string token)
    {
        token = string.Empty;
        try
        {
            if (!File.Exists(file)) return false;
            var text = File.ReadAllText(file).Trim();
            if (text.Length == 0) return false;
            token = text;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryWrite(string file, string token)
    {
        try
        {
            File.WriteAllText(file, token);
            // 它是把钥匙，别让同机器的其他用户顺手读走。Windows 上没有这一档，失败就算。
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 写不进去没关系，见 Ensure 里的说明。
        }
    }
}
