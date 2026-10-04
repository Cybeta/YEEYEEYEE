using System.Text.Json;

namespace YEEYEEYEE.Core;

public static class YEEYEEYEEProtocol
{
    /// <summary>
    /// 消息表：类型 → 方向 + 必填字段 + 协议版本。**只有一份**，住在
    /// <c>YEEYEEYEE.Canvas/src/shared/protocol.json</c>，这一侧用 EmbeddedResource 把
    /// **同一个文件**嵌进来（见 <c>YEEYEEYEE.Core.csproj</c>）。
    ///
    /// 为什么不留一份 C# 里的副本：两端各存一份表的时候，先对不上的那次只会表现成
    /// 「某条消息莫名被判为未知类型」——没有人会想到去查协议表。版本号同理，只有 json 里那一个。
    /// </summary>
    private static readonly ProtocolTable Table = ProtocolTable.Load();

    /// <summary>信封版本。</summary>
    public static int Version => Table.Version;

    /// <summary>表里认得的全部消息类型（顺序即表里的顺序）。</summary>
    public static IReadOnlyList<string> MessageTypes => Table.Types;

    /// <summary>某个类型的方向；表里没有就是 null。</summary>
    public static string? DirectionOf(string type) =>
        Table.Directions.TryGetValue(type, out var direction) ? direction : null;

    /// <summary>某个类型的必填字段；表里没有就是空。</summary>
    public static IReadOnlyList<string> RequiredFieldsOf(string type) =>
        Table.Required.TryGetValue(type, out var fields) ? fields : Array.Empty<string>();

    public static JsonDocument Decode(string json, string direction)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new ProtocolViolationException("PROTOCOL_MALFORMED", ex.Message); }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || !root.TryGetProperty("id", out var id) || !root.TryGetProperty("type", out var type) || !root.TryGetProperty("ts", out var ts) || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || v.ValueKind != JsonValueKind.Number || id.ValueKind != JsonValueKind.String || type.ValueKind != JsonValueKind.String || ts.ValueKind != JsonValueKind.Number || !Guid.TryParse(id.GetString(), out _) || !long.TryParse(ts.GetRawText(), out _))
        { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_MALFORMED", "信封字段无效"); }
        if (v.GetInt32() != Version) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_VERSION_MISMATCH", "协议版本不匹配"); }
        var typeValue = type.GetString()!;
        if (!Table.Directions.TryGetValue(typeValue, out var expectedDirection)) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_UNKNOWN_TYPE", $"未知消息类型 {typeValue}"); }
        if (!StringComparer.Ordinal.Equals(expectedDirection, direction)) { doc.Dispose(); throw new ProtocolViolationException("PROTOCOL_DIRECTION_MISMATCH", $"消息方向错误 {typeValue}"); }
        ValidatePayload(typeValue, payload);
        return doc;
    }

    private static void ValidatePayload(string type, JsonElement payload)
    {
        foreach (var field in RequiredFieldsOf(type)) if (!payload.TryGetProperty(field, out _)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", $"{type} 缺少字段 {field}");
        if ((type is "canvas/undo.request" or "canvas/redo.request") && (!payload.TryGetProperty("localOnly", out var local) || local.ValueKind != JsonValueKind.True)) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", "只能执行单人本地撤销", "warning");
        if (type == "host/undo.result" && payload.TryGetProperty("localOnly", out var resultLocal) && resultLocal.ValueKind != JsonValueKind.True) throw new ProtocolViolationException("PROTOCOL_FAIL_CLOSED", "禁止协作快照撤销");
        if (type == "canvas/invoke.request" && (!payload.TryGetProperty("idempotencyKey", out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "执行请求缺少幂等键");
        if (type == "canvas/resource.replace.request") ValidateResourceReplace(payload);
        if (type == "host/init") ValidateHostInit(payload);
        if (type == "host/capabilities") ValidateCapabilities(payload);
        if (type == "host/job.update") ValidateJobUpdate(payload);
    }

    private static void ValidateResourceReplace(JsonElement payload)
    {
        foreach (var field in new[] { "recordId", "entityId", "variantId" })
            if (!Guid.TryParse(payload.GetProperty(field).GetString(), out _))
                throw new ProtocolViolationException("PROTOCOL_MALFORMED", $"资源替换请求的 {field} 无效");

        if (!payload.TryGetProperty("variantVersionId", out var version)) return;
        if (version.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ProtocolViolationException("PROTOCOL_MALFORMED", "资源替换请求的 variantVersionId 无效");

        if (version.ValueKind == JsonValueKind.String
            && !Guid.TryParse(version.GetString(), out _))
            throw new ProtocolViolationException("PROTOCOL_MALFORMED", "资源替换请求的 variantVersionId 无效");
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
}

/// <summary>
/// 协议表本体，从嵌进来的 <c>protocol.json</c> 读。
///
/// **加载时就把表验一遍**：真出问题要在这一处炸开并说清是哪一个类型，而不是等到某条消息
/// 被判成未知类型——那种症状离原因太远（一个「未知消息类型」可以是表坏了、也可以真的是对方发错了）。
/// </summary>
internal sealed class ProtocolTable
{
    private ProtocolTable(
        int version,
        IReadOnlyList<string> types,
        IReadOnlyDictionary<string, string> directions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> required)
    {
        Version = version;
        Types = types;
        Directions = directions;
        Required = required;
    }

    public int Version { get; }
    public IReadOnlyList<string> Types { get; }
    public IReadOnlyDictionary<string, string> Directions { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Required { get; }

    public static ProtocolTable Load()
    {
        using var stream = typeof(YEEYEEYEEProtocol).Assembly.GetManifestResourceStream("protocol.json")
            ?? throw new InvalidOperationException(
                "嵌入式协议表 protocol.json 不见了：检查 YEEYEEYEE.Core.csproj 里那条 EmbeddedResource"
                + "（它指向 YEEYEEYEE.Canvas/src/shared/protocol.json），"
                + "以及 Dockerfile 有没有把那个 json 拷进构建上下文。");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        var allowed = root.GetProperty("directions").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

        var types = new List<string>();
        var directions = new Dictionary<string, string>(StringComparer.Ordinal);
        var required = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("messages").EnumerateArray())
        {
            var type = item.GetProperty("type").GetString() ?? string.Empty;
            var direction = item.GetProperty("direction").GetString() ?? string.Empty;
            if (type.Length == 0) throw new InvalidOperationException("协议表里有条目没写 type。");
            if (!allowed.Contains(direction, StringComparer.Ordinal))
                throw new InvalidOperationException($"协议表里 {type} 的方向「{direction}」不在允许的两个里。");
            if (!directions.TryAdd(type, direction))
                throw new InvalidOperationException($"协议表里 {type} 写了两遍。");
            types.Add(type);
            required[type] = item.GetProperty("required").EnumerateArray()
                .Select(field => field.GetString() ?? string.Empty).ToList();
        }

        if (types.Count == 0) throw new InvalidOperationException("协议表里一条消息都没有。");
        return new ProtocolTable(version, types, directions, required);
    }
}
