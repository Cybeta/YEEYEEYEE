using System.Text;
using System.Text.Json;

namespace YEEYEEYEE.Web;

/// <summary>
/// ComfyUI 连通性自检。
///
/// **为什么值得单独探一次**：云上那条隧道最常见的故障不是「连不上」，而是「端口还通着、
/// 后面已经不是 ComfyUI 了」——TCP 连得上，HTTP 回的却是一张机房 FAQ 页（404）。
/// 这种情况下服务照样起得来，用户点「调用技能」才失败，报出来的是 ComfyUI 的 404，
/// 指向的是「地址」而不是「那台机器已经不在了」。开机把话说清楚，比让用户去猜省事得多。
///
/// 自检**只报告、不阻拦**：画布、账号、协作这些功能跟 ComfyUI 没关系，
/// 不能因为它没开就把整个服务端拦下来。
/// </summary>
internal static class ComfyUiSelfCheck
{
    public sealed record Result(bool Reachable, string Detail, string? Version, string? Device);

    /// <summary>探一次 <c>system_stats</c>。除成功外的每一种情况都给出**人能看懂的一句**。</summary>
    public static async Task<Result> ProbeAsync(
        HttpClient http,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync("system_stats", linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Result(
                    false,
                    $"HTTP {(int)response.StatusCode}{await DescribeBodyAsync(response.Content, linked.Token).ConfigureAwait(false)}",
                    null,
                    null);

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var version = root.TryGetProperty("system", out var system)
                    && system.TryGetProperty("comfyui_version", out var value)
                        ? value.GetString()
                        : null;
                string? device = null;
                if (root.TryGetProperty("devices", out var devices)
                    && devices.ValueKind == JsonValueKind.Array
                    && devices.GetArrayLength() > 0
                    && devices[0].TryGetProperty("name", out var name))
                    device = name.GetString();

                if (version is null && device is null)
                    return new Result(false, "返回的 JSON 里没有 system_stats 该有的字段，这个地址后面大概不是 ComfyUI", null, null);

                return new Result(true, "已连接", version, device);
            }
            catch (JsonException)
            {
                return new Result(false, "返回的不是 JSON——这个地址后面大概不是 ComfyUI", null, null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(false, $"探测超时（{timeout.TotalSeconds:0} 秒内没有回应）", null, null);
        }
        catch (System.Net.Http.HttpRequestException error)
        {
            return new Result(false, FirstLine(error.Message), null, null);
        }
    }

    /// <summary>
    /// 把正文开头那一小段摘回来。**只读前 512 字节**：失败时对面可能回一整页 HTML，
    /// 为了写一句日志把整页读进内存不值当；而「这是一张网页」这个事实，开头几个字节就说清楚了。
    /// </summary>
    private static async Task<string> DescribeBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) return "（正文为空）";
            var text = Encoding.UTF8.GetString(buffer, 0, read).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 0 ? $"：{text}" : "（正文为空）";
        }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string FirstLine(string message)
    {
        var index = message.IndexOfAny(new[] { '\r', '\n' });
        return (index < 0 ? message : message[..index]).Trim();
    }
}
