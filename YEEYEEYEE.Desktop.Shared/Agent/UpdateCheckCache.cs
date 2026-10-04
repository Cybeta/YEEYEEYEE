using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 上一次查更新的结果：查到的时间、当时读到的发行版信息、以及（被限流时）什么时候才能再问。
///
/// 为什么要有它：`/releases/latest` 是**未认证**接口，每 IP 每小时只有 60 次。反复启动程序
/// （开发、重启、切项目）就会把额度烧光，之后每次启动都只能看到一句「次数用完了」——
/// 明明只是没查成，读起来却像「这软件不更新了」。
///
/// 缓存的**不是**结论而是事实（当时的 <see cref="ReleaseInfo"/>）：结论是相对「当前版本」算出来的，
/// 换了一版程序之后同一份事实依旧算得出正确结果（见 <see cref="UpdateChecker.Evaluate"/>）。
/// <see cref="CheckedAt"/> 为默认值表示**从没成功问到过**，这时只有限流窗口可用。
/// </summary>
public sealed record UpdateCheckCache(DateTimeOffset CheckedAt, ReleaseInfo? Release, DateTimeOffset? RateLimitedUntil)
{
    /// <summary>多久之内算「刚查过」。一小时 60 次的额度，六小时问一次怎么都够。</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);

    /// <summary>成功问到过（哪怕答案是「还没有发行版」）。</summary>
    public bool HasAnswer => CheckedAt != default;

    public bool IsFresh(DateTimeOffset now) => HasAnswer && now - CheckedAt < FreshFor;

    public bool IsRateLimited(DateTimeOffset now) => RateLimitedUntil is { } until && now < until;
}

/// <summary>这一次到底要不要真的去问 GitHub。</summary>
public enum UpdateCheckDecisionKind
{
    AskGitHub,
    UseCache,
    WaitForRateLimit
}

/// <summary>决定 + 一句给用户看的说明（用缓存或要等时，必须说清为什么）。</summary>
public sealed record UpdateCheckDecision(UpdateCheckDecisionKind Kind, string Note);

/// <summary>
/// 「这一次要不要真的问 GitHub」——**纯函数，不碰网络**，所以三种情形都能被用例逐条钉住。
/// 手动点了按钮（<c>force</c>）就照做：那是用户明确要求，不该被缓存挡住。
/// </summary>
public static class UpdateCheckPolicy
{
    public static UpdateCheckDecision Decide(UpdateCheckCache? cache, bool force, DateTimeOffset now)
    {
        if (cache is null) return new UpdateCheckDecision(UpdateCheckDecisionKind.AskGitHub, string.Empty);

        // 已知还在限流窗口里：**连问都不问**——反正会被拒，不如直接说清等到什么时候。
        if (!force && cache.IsRateLimited(now))
            return new UpdateCheckDecision(UpdateCheckDecisionKind.WaitForRateLimit,
                $"上次查更新被 GitHub 限流了，到 {Local(cache.RateLimitedUntil!.Value)} 之后才会自动再查"
                + "（手动点「检查更新」可以不等）。");

        if (!force && cache.IsFresh(now))
            return new UpdateCheckDecision(UpdateCheckDecisionKind.UseCache,
                $"这是 {Local(cache.CheckedAt)} 查到的结果，六小时内不重复问 GitHub。");

        return new UpdateCheckDecision(UpdateCheckDecisionKind.AskGitHub, string.Empty);
    }

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("MM-dd HH:mm");
}

/// <summary>
/// 缓存的落盘：用户配置目录里一个小 JSON。
/// 读写失败一律当作「没有缓存」——更新检查不该因为一个缓存文件坏了或写不进去而拦住启动。
/// </summary>
public static class UpdateCheckStore
{
    private const string FileName = "update-check.json";

    /// <summary>缓存文件的位置。<paramref name="directory"/> 只给用例传临时目录用。</summary>
    public static string FileIn(string? directory = null) =>
        System.IO.Path.Combine(directory ?? AppPaths.UserConfigDirectory, FileName);

    public static UpdateCheckCache? Load(string? directory = null)
    {
        try
        {
            var path = FileIn(directory);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    public static void Save(UpdateCheckCache cache, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        try
        {
            var path = FileIn(directory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // 写不进去不是什么大事：下次再问一次而已，绝不能因此拦住启动。
        }
    }
}
