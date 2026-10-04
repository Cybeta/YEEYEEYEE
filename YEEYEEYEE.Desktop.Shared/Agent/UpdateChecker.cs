using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>发行版里可下载的一个文件。</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>从 GitHub 发行版接口读到的一份信息。</summary>
public sealed record ReleaseInfo(
    string TagName,
    string Name,
    string Body,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

public enum UpdateCheckState
{
    /// <summary>已经是最新（或本地比线上还新）。</summary>
    UpToDate,

    /// <summary>线上有更新的版本。</summary>
    UpdateAvailable,

    /// <summary>没查成：网络不通、被限流、返回看不懂……</summary>
    Failed
}

/// <summary>一次检查的结果。**失败与「没有新版」是两件事**，界面必须分开说。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckState State,
    Version Current,
    Version? Latest,
    ReleaseInfo? Release,
    string Message,
    DateTimeOffset? RateLimitedUntil = null)
{
    public bool HasUpdate => State == UpdateCheckState.UpdateAvailable;
}

/// <summary>
/// 检查有没有新版本：读 GitHub 上这个仓库的**最新发行版**，与当前程序版本比大小。
///
/// 四条刻意的取舍：
/// 1. **只读 <c>/releases/latest</c>**。这个接口本身就不返回草稿与预发布版，所以不必自己再过滤一遍；
///    预发布版不该推给普通用户。
/// 2. **失败就是失败**。超时、被限流、JSON 变了、标签看不懂——一律返回 Failed 并带上原因，
///    绝不降级成「已是最新」。把查不到当成没有新版，是最容易让人以为「这软件不更新了」的写法。
/// 3. **网络异常不抛出**。调用方是启动流程，检查更新失败不该拦住程序启动。
/// 4. **被限流时把「什么时候能再问」带回去**（<see cref="UpdateCheckResult.RateLimitedUntil"/>）：
///    这个额度是每 IP 每小时 60 次，攒着不用才是对的（见 <see cref="UpdateCheckPolicy"/>）。
/// </summary>
public static class UpdateChecker
{
    /// <summary>本项目的仓库。发行版接口按 owner/repo 拼地址。</summary>
    public const string DefaultRepository = "Cybeta/YEEYEEYEE";

    private const string LatestReleaseTemplate = "https://api.github.com/repos/{0}/releases/latest";

    public static async Task<UpdateCheckResult> CheckAsync(
        HttpClient http,
        Version current,
        string repository = DefaultRepository,
        CancellationToken cancellationToken = default)
    {
        var url = string.Format(LatestReleaseTemplate, repository);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub 的 API 强制要求 User-Agent，缺了直接 403。
            request.Headers.TryAddWithoutValidation("User-Agent", "YEEYEEYEE-Desktop");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateCheckState.UpToDate, current, null, null, "这个仓库还没有发布任何发行版。");

            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                var until = RetryAfterFrom(response.Headers);
                return new UpdateCheckResult(UpdateCheckState.Failed, current, null, null,
                    until is { } reset
                        ? $"GitHub 拒绝了这次请求（多半是接口调用次数用完了）。到 {reset.ToLocalTime():MM-dd HH:mm} 之后会自动再试。"
                        : "GitHub 拒绝了这个请求（多半是接口调用次数用完了）。过一会儿再试。",
                    until);
            }

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckState.Failed, current, null, null,
                    $"发行版接口返回 {(int)response.StatusCode} {response.ReasonPhrase}。");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Compare(current, json);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return new UpdateCheckResult(UpdateCheckState.Failed, current, null, null, DescribeNetworkFailure(error));
        }
    }

    /// <summary>
    /// 被限流时 GitHub 会告诉我们什么时候可以再问：优先 `Retry-After`（秒或日期），
    /// 没有就看 `X-RateLimit-Reset`（Unix 秒）。两个都没有就返回 null——**不编一个时间**：
    /// 编出来的时间会让用户白等，而真正能再问的时刻只有服务端知道。
    /// </summary>
    public static DateTimeOffset? RetryAfterFrom(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero) return DateTimeOffset.UtcNow + delta;
            if (retryAfter.Date is { } date && date > DateTimeOffset.UtcNow) return date;
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix)
            && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix);

        return null;
    }

    /// <summary>把发行版 JSON 与当前版本比一比。单独拆出来是为了能离线喂样本测。</summary>
    public static UpdateCheckResult Compare(Version current, string? json)
    {
        ReleaseInfo release;
        try
        {
            release = ParseRelease(json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckState.Failed, current, null, null, "发行版接口返回的内容看不懂：" + error.Message);
        }

        return Evaluate(current, release);
    }

    /// <summary>
    /// 从「已经读到手的发行版信息」算出结论。与 <see cref="Compare"/> 只差一层解析，
    /// 所以**用缓存复用时也走这一条**——结论只有这一处算法，缓存那条路不会说出一句不一样的话。
    /// </summary>
    public static UpdateCheckResult Evaluate(Version current, ReleaseInfo? release)
    {
        if (release is null)
            return new UpdateCheckResult(UpdateCheckState.UpToDate, current, null, null, "这个仓库还没有发布任何发行版。");

        if (!AppVersion.TryParse(release.TagName, out var latest))
            return new UpdateCheckResult(UpdateCheckState.Failed, current, null, release, $"最新发行版的标签是「{release.TagName}」，这不是一个能比较的版本号。");

        return latest > current
            ? new UpdateCheckResult(UpdateCheckState.UpdateAvailable, current, latest, release, $"发现新版本 {AppVersion.Text(latest)}（当前 {AppVersion.Text(current)}）。")
            : new UpdateCheckResult(UpdateCheckState.UpToDate, current, latest, release, $"已经是最新版本（{AppVersion.Text(current)}）。");
    }

    /// <summary>从发行版里挑出可下载的更新包：优先 <c>.zip</c>，没有就返回 null（让界面如实说没有包）。</summary>
    public static ReleaseAsset? FindPackage(ReleaseInfo? release)
    {
        if (release is null) return null;
        return release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseInfo ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("内容为空");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("顶层不是对象");

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Text(item, "name");
                var url = Text(item, "browser_download_url");
                if (name.Length == 0 || url.Length == 0) continue;
                assets.Add(new ReleaseAsset(name, url, Number(item, "size")));
            }
        }

        DateTimeOffset? published = null;
        var publishedText = Text(root, "published_at");
        if (DateTimeOffset.TryParse(publishedText, out var parsed)) published = parsed;

        return new ReleaseInfo(
            Text(root, "tag_name"),
            Text(root, "name"),
            Text(root, "body"),
            Text(root, "html_url"),
            published,
            assets);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string DescribeNetworkFailure(Exception error) => error switch
    {
        TaskCanceledException or OperationCanceledException => "连接超时或被取消，没能问到发行版信息。",
        _ => "连不上 GitHub：" + error.Message
    };
}
