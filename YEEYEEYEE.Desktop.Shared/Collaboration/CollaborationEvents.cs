using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>服务端推来的「画布变了」（载荷的读侧投影）。</summary>
public sealed record CanvasChangedNotice(long Revision, string? RecordId, string Actor, string Scope);

/// <summary>
/// 解析服务端推来的事件帧。
///
/// 与网页端那份（<c>serverEvents.ts</c>）同一条规矩：**认不出的类型直接忽略**，不抛错——
/// 服务端以后加新事件时，旧客户端不该整条流都断掉；而 <c>data</c> 里的 type 与事件名不一致时
/// 也不认（两边总有一个错了）。纯函数，所以能被单独钉住。
/// </summary>
public static class CollaborationEvents
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static bool IsCanvasChanged(string type) => string.Equals(type, "canvas.changed", StringComparison.Ordinal);

    /// <summary>解析一条 <c>canvas.changed</c>；认不出、类型不符、坏 JSON 都回 null。</summary>
    public static CanvasChangedNotice? ParseCanvasChanged(string type, string data)
    {
        if (!IsCanvasChanged(type)) return null;
        var frame = Parse(type, data);
        return frame is null ? null : new CanvasChangedNotice(frame.Revision, frame.RecordId, frame.Actor, frame.Scope);
    }

    /// <summary>这条帧是不是我们认得出来的 <c>edits.changed</c>（认不出就当没有）。</summary>
    public static bool IsEditsChangedFrame(string type, string data) =>
        string.Equals(type, "edits.changed", StringComparison.Ordinal) && Parse(type, data) is not null;

    /// <summary>这条帧是不是我们认得出来的 <c>presence.changed</c>（有人上线/下线）。
    /// 载荷只有原因与是谁，名单另外去 <c>GET /api/web/presence</c> 取——形状只有一处。</summary>
    public static bool IsPresenceChangedFrame(string type, string data) =>
        string.Equals(type, "presence.changed", StringComparison.Ordinal) && Parse(type, data) is not null;

    private static Frame? Parse(string expectedType, string data)
    {
        try
        {
            var frame = JsonSerializer.Deserialize<Frame>(data, Options);
            if (frame is null || !string.Equals(frame.Type, expectedType, StringComparison.Ordinal)) return null;
            return frame with
            {
                RecordId = string.IsNullOrWhiteSpace(frame.RecordId) ? null : frame.RecordId,
                Actor = string.IsNullOrWhiteSpace(frame.Actor) ? "有人" : frame.Actor,
                Scope = string.IsNullOrWhiteSpace(frame.Scope) ? "record" : frame.Scope
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Frame(string? Type, long Revision, string? RecordId, string? Actor, string? Scope);
}
