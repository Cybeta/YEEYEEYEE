using System.Text.Json;

namespace DreamForge.Core;

public static class DreamForgeProtocol
{
    public const int Version = 1;
    private static readonly IReadOnlyDictionary<string, string> Directions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["canvas/hello"] = "canvasToHost", ["canvas/op.batch"] = "canvasToHost", ["canvas/undo.request"] = "canvasToHost", ["canvas/redo.request"] = "canvasToHost", ["canvas/invoke.request"] = "canvasToHost", ["canvas/job.cancel.request"] = "canvasToHost", ["canvas/selection.changed"] = "canvasToHost", ["canvas/diagnostic"] = "canvasToHost",
        ["host/init"] = "hostToCanvas", ["host/op.batch"] = "hostToCanvas", ["host/scene.reset"] = "hostToCanvas", ["host/undo.result"] = "hostToCanvas", ["host/job.update"] = "hostToCanvas", ["host/capabilities"] = "hostToCanvas", ["host/error"] = "hostToCanvas"
    };

    public static JsonDocument Decode(string json, string direction)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new ProtocolViolationException("PROTOCOL_MALFORMED", ex.Message); }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || !root.TryGetProperty("id", out var id) || !root.TryGetProperty("type", out var type) || !root.TryGetProperty("ts", out var ts) || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || v.ValueKind != JsonValueKind.Number || id.ValueKind != JsonValueKind.String || type.ValueKind != JsonValueKind.String || ts.ValueKind != JsonValueKind.Number || !Guid.TryParse(id.GetString(), out _) || !long.TryParse(ts.GetRawText(), out _))
        { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_MALFORMED", "信封字段无效"); }
        if (v.GetInt32() != Version) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_VERSION_MISMATCH", "协议版本不匹配"); }
        var typeValue = type.GetString()!;
        if (!Directions.TryGetValue(typeValue, out var expectedDirection)) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_UNKNOWN_TYPE", $"未知消息类型 {typeValue}"); }
        if (!StringComparer.Ordinal.Equals(expectedDirection, direction)) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_DIRECTION_MISMATCH", $"消息方向错误 {typeValue}"); }
        ValidatePayload(typeValue, payload);
        return doc;
    }

    private static void ValidatePayload(string type, JsonElement payload)
    {
        foreach (var field in RequiredFields(type)) if (!payload.TryGetProperty(field, out _)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", $"{type} 缺少字段 {field}");
        if ((type is "canvas/undo.request" or "canvas/redo.request") && (!payload.TryGetProperty("localOnly", out var local) || local.ValueKind != JsonValueKind.True)) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", "只能执行单人本地撤销", "warning");
        if (type == "host/undo.result" && payload.TryGetProperty("localOnly", out var resultLocal) && resultLocal.ValueKind != JsonValueKind.True) throw new ProtocolViolationException("PROTOCOL_FAIL_CLOSED", "禁止协作快照撤销");
        if (type == "canvas/invoke.request" && (!payload.TryGetProperty("idempotencyKey", out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "执行请求缺少幂等键");
        if (type == "host/init") ValidateHostInit(payload);
        if (type == "host/capabilities") ValidateCapabilities(payload);
        if (type == "host/job.update") ValidateJobUpdate(payload);
    }

    private static void ValidateHostInit(JsonElement payload)
    {
        if (!payload.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != Version) throw new ProtocolViolationException("PROTOCOL_VERSION_MISMATCH", "宿主协议版本不匹配");
        var capabilities = payload.GetProperty("capabilities");
        ValidateCapabilities(capabilities);
        var sessionClaims = payload.GetProperty("session").GetProperty("serverClaims");
        var declared = capabilities.GetProperty("serverClaims");
        var set = declared.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        if (sessionClaims.EnumerateArray().Any(x => !set.Contains(x.GetString()))) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", "会话声明超出服务端声明集");
    }

    private static void ValidateJobUpdate(JsonElement payload)
    {
        if (!Guid.TryParse(payload.GetProperty("jobId").GetString(), out _) || !Guid.TryParse(payload.GetProperty("invocationId").GetString(), out _)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "Job 标识无效");
        var progress = payload.GetProperty("progressPercent");
        if (progress.ValueKind != JsonValueKind.Number || !progress.TryGetInt32(out var value) || value is < 0 or > 100) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "Job 进度无效");
        var state = payload.GetProperty("state").GetString();
        if (!Enum.TryParse<JobState>(state, out _)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "Job 状态无效");
        if (payload.TryGetProperty("externalTaskId", out var external) && external.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "外部任务 ID 无效");
    }

    private static void ValidateCapabilities(JsonElement payload)
    {
        var claims = payload.GetProperty("serverClaims").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, string> { ["canEditCanvas"] = "canvas.edit", ["canInvokeSkill"] = "skill.invoke", ["canCancelJob"] = "job.cancel", ["canUndo"] = "canvas.undo" };
        foreach (var item in map) if (payload.TryGetProperty(item.Key, out var enabled) && enabled.ValueKind == JsonValueKind.True && !claims.Contains(item.Value)) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", $"能力位 {item.Key} 超出声明集");
    }

    private static IEnumerable<string> RequiredFields(string type) => type switch
    {
        "canvas/hello" => ["canvasVersion", "protocolVersion", "minHostProtocol", "features"],
        "canvas/op.batch" => ["batchId", "baseRevision", "source", "ops"],
        "canvas/undo.request" or "canvas/redo.request" => ["localOnly"],
        "canvas/invoke.request" => ["invocation", "idempotencyKey"],
        "canvas/job.cancel.request" => ["jobId"],
        "host/init" => ["protocolVersion", "hostVersion", "session", "capabilities", "scene", "locale"],
        "host/op.batch" => ["batchId", "revision", "origin", "actorSessionId", "ops"],
        "host/scene.reset" => ["revision", "reason", "scene"],
        "host/undo.result" => ["ok", "localOnly", "revision", "reason"],
        "host/job.update" => ["jobId", "invocationId", "state", "progressPercent", "outputs"],
        "host/capabilities" => ["serverClaims", "canEditCanvas", "canInvokeSkill", "canCancelJob", "canUndo"],
        "host/error" => ["code", "message", "severity"],
        _ => Array.Empty<string>()
    };
}
