using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 接口账号的一次快照：余额 + 当前上线的模型列表。
/// 两件事一起取是因为它们来自同一个接口账号，界面上也总是一起看（余额够不够、要用的模型在不在）。
/// </summary>
public sealed record ApiAccountSnapshot(
    bool Ok,
    ApiBalance? Balance,
    IReadOnlyList<string> Models,
    string Error)
{
    public static ApiAccountSnapshot Failed(string error) => new(false, null, Array.Empty<string>(), error);

    /// <summary>文档里写了、但接口当前没有上线的模型（调用会 404）。</summary>
    public IReadOnlyList<string> MissingFrom(IReadOnlyList<string> documented) =>
        Models.Count == 0
            ? Array.Empty<string>()
            : documented.Where(name => !Models.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
}

/// <summary>
/// 账号余额。口径取自接口 <c>GET {base}/user/balance</c>：
/// <c>{"balance":620,"used":3005,"total":3625,"permanent_credits":620,"temporary_credits":0,"temporary_expires_at":"…"}</c>。
/// </summary>
public sealed record ApiBalance(
    long Balance,
    long Used,
    long Total,
    long Permanent,
    long Temporary,
    string TemporaryExpiresAt)
{
    /// <summary>给界面看的余额摘要。</summary>
    public string Describe() => $"余额 {Balance} 积分（已用 {Used} / 累计 {Total}）";

    /// <summary>临时额度（会过期的那种）非 0 时额外说明，避免用户以为余额都是永久的。</summary>
    public string TemporaryNote() => Temporary <= 0
        ? string.Empty
        : $"其中临时额度 {Temporary}{(TemporaryExpiresAt.Length == 0 ? string.Empty : $"，{TemporaryExpiresAt} 到期")}";
}

/// <summary>
/// 查接口账号的余额与上线模型。**只读**：一次 GET 余额 + 一次 GET 模型列表，
/// 任何一步失败都如实返回原因，不猜数字、不用 0 冒充「余额」。
/// </summary>
public static class ApiAccountProbe
{
    private static readonly HttpClient Default = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static async Task<ApiAccountSnapshot> FetchAsync(
        string? baseUrl,
        string? apiKey,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (root.Length == 0) return ApiAccountSnapshot.Failed("还没有接口地址，查不到余额。");
        if (string.IsNullOrWhiteSpace(apiKey)) return ApiAccountSnapshot.Failed("还没有填密钥，查不到余额。");

        var client = http ?? Default;
        var balance = await GetAsync(client, $"{root}/user/balance", apiKey, cancellationToken).ConfigureAwait(false);
        if (!balance.Ok) return ApiAccountSnapshot.Failed(balance.Error);

        var parsed = ParseBalance(balance.Body);
        if (parsed is null) return ApiAccountSnapshot.Failed($"余额响应看不懂（{Trim(balance.Body, 160)}）。");

        // 模型列表失败不影响余额展示，但要如实带出原因。
        var models = await GetAsync(client, $"{root}/models", apiKey, cancellationToken).ConfigureAwait(false);
        var ids = models.Ok ? ModelIds(models.Body) : Array.Empty<string>();
        return new ApiAccountSnapshot(true, parsed, ids, models.Ok ? string.Empty : "上线模型列表没取到：" + models.Error);
    }

    /// <summary>解析余额响应；识别不了返回 null（不猜）。</summary>
    public static ApiBalance? ParseBalance(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 余额字段各站叫法不一：balance 为主，其次 credits。
            var balance = ReadLong(root, "balance") ?? ReadLong(root, "credits");
            if (balance is null) return null;
            return new ApiBalance(
                balance.Value,
                ReadLong(root, "used") ?? 0,
                ReadLong(root, "total") ?? 0,
                ReadLong(root, "permanent_credits") ?? 0,
                ReadLong(root, "temporary_credits") ?? 0,
                ReadString(root, "temporary_expires_at"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>解析模型列表里的 id；兼容 data[] 与 models[] 两种外壳。</summary>
    public static IReadOnlyList<string> ModelIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return IdsOf(root);
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return IdsOf(data);
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array) return IdsOf(models);
            return Array.Empty<string>();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> IdsOf(JsonElement array)
    {
        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            string? id = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out var value) => value.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
        }
        return ids;
    }

    private static async Task<(bool Ok, int Status, string Body, string Error)> GetAsync(
        HttpClient client, string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, (int)response.StatusCode, body, string.Empty);
            var detail = ExtractDetail(body);
            return (false, (int)response.StatusCode, body,
                $"接口返回 {(int)response.StatusCode}{(detail.Length == 0 ? string.Empty : "：" + detail)}");
        }
        catch (HttpRequestException error) { return (false, 0, string.Empty, "请求失败：" + error.Message); }
        catch (TaskCanceledException) { return (false, 0, string.Empty, "请求超时。"); }
    }

    /// <summary>把 {"detail":"missing api key"} 这类错误说明抽出来，比只报状态码有用。</summary>
    private static string ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? string.Empty;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        return Trim(body, 120);
    }

    private static long? ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number => (long)Math.Round(value.GetDouble()),
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
