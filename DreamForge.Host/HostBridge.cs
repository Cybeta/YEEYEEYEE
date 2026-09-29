using System.Text.Json;
using DreamForge.Core;

namespace DreamForge.Host;

public interface ICanvasTransport
{
    void Send(JsonElement message);
    void Subscribe(Action<JsonElement> handler);
}

public sealed record ResourceReplaceRequest(
    Guid RecordId,
    Guid EntityId,
    Guid VariantId,
    Guid? VariantVersionId
);

public sealed record HostBridgeOptions
{
    public string HostVersion { get; init; } = "0.1.0";
    public string Locale { get; init; } = "zh-CN";
    public string CanvasVersion { get; init; } = "0.1.0";
    public int SceneRevision { get; init; }
    public JsonElement Scene { get; init; }
}

public sealed class HostBridge
{
    private readonly ICanvasTransport transport;
    private readonly HostBridgeOptions options;
    private readonly SingleMachineExecutionService execution;
    private SessionContext? session;
    private bool ready;

    public HostBridge(ICanvasTransport transport, HostBridgeOptions? options = null, SingleMachineExecutionService? execution = null)
    {
        this.transport = transport;
        this.options = options ?? new HostBridgeOptions();
        this.execution = execution ?? new SingleMachineExecutionService();
        this.execution.Updated += SendJobUpdate;
        transport.Subscribe(Receive);
    }

    public bool IsReady => ready;

    public event Action<ResourceReplaceRequest>? ResourceReplaceRequested;

    public void Initialize(SessionContext context)
    {
        session = context ?? throw new ArgumentNullException(nameof(context));
        var scene = options.Scene.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { records = Array.Empty<object>() })
            : options.Scene;
        var payload = new
        {
            protocolVersion = DreamForgeProtocol.Version,
            hostVersion = options.HostVersion,
            session = new
            {
                sessionId = context.SessionId,
                userId = context.UserId,
                clientType = context.ClientType.ToString(),
                role = context.Role.ToString(),
                serverClaims = context.ServerClaims.ToArray()
            },
            capabilities = BuildCapabilities(context),
            scene = new { revision = options.SceneRevision, snapshot = scene },
            locale = options.Locale
        };
        Send("host/init", payload);
    }

    public void SendSceneReset(int revision, string reason = "new-canvas")
    {
        EnsureSession();
        Send("host/scene.reset", new
        {
            revision,
            reason,
            scene = new { snapshot = new { records = Array.Empty<object>() } }
        });
    }

    /// <summary>
    /// 把完整画布 records 推送给前端。调用方负责用 NodeProjection 投影节点，
    /// 这里只做传输。用于初始化或章节切换。
    /// </summary>
    public void SendScene(List<object> records, int revision, string reason = "scene-update")
    {
        EnsureSession();
        Send("host/scene.reset", new
        {
            revision,
            reason,
            scene = new { snapshot = new { records } }
        });
    }

    /// <summary>
    /// 增量推送节点变更。调用方负责投影，这里只做传输。
    /// </summary>
    public void SendNodeUpdates(List<object> records, int revision)
    {
        EnsureSession();
        Send("host/op.batch", new
        {
            batchId = Guid.NewGuid().ToString(),
            revision,
            origin = "host",
            actorSessionId = session!.SessionId,
            ops = records
        });
    }

    /// <summary>
    /// 把任务状态推给 Web。除状态与进度外，还带上尝试次数与重试血缘（目标 5 / 桌面与 Web 状态一致）：
    /// 这样浏览器与桌面看到的是同一条尝试链，能显示「第 N 次尝试 / 重试自哪个任务」。
    /// </summary>
    public void SendJobUpdate(ExecutionResult result)
    {
        EnsureSession();
        Send("host/job.update", new
        {
            jobId = result.JobId,
            invocationId = result.InvocationId,
            state = result.State.ToString(),
            progressPercent = result.ProgressPercent,
            errorCode = result.ErrorCode,
            errorMessage = result.ErrorMessage,
            externalTaskId = result.ExternalTaskId,
            outputs = result.Outputs,
            attempt = result.Attempt < 1 ? 1 : result.Attempt,
            retryOfJobId = result.RetryOfJobId,
            rootJobId = result.RootJobId == Guid.Empty ? result.JobId : result.RootJobId
        });
    }

    public void SendError(string code, string message, string severity = "warning", string? relatedType = null)
        => Send("host/error", new { code, message, severity, relatedType });

    public void SendResourceReplaceResult(Guid requestId, bool ok, string message, int? revision = null)
        => Send("host/resource.replace.result", new { requestId, ok, message, revision });

    private void Receive(JsonElement message)
    {
        try
        {
            using var decoded = DreamForgeProtocol.Decode(message.GetRawText(), "canvasToHost");
            var type = decoded.RootElement.GetProperty("type").GetString();
            var payload = decoded.RootElement.GetProperty("payload");
            if (type == "canvas/hello") ready = true;
            else if (type == "canvas/invoke.request") _ = HandleInvocationAsync(payload);
            else if (type == "canvas/resource.replace.request") HandleResourceReplace(payload);
            else if (type == "canvas/job.cancel.request") HandleCancel(payload);
        }
        catch (ProtocolViolationException error)
        {
            ready = false;
            SendError(error.Code, error.Message, error.Severity);
        }
    }

    private void HandleResourceReplace(JsonElement payload)
    {
        try
        {
            EnsureReady();
            var request = new ResourceReplaceRequest(
                payload.GetProperty("recordId").GetGuid(),
                payload.GetProperty("entityId").GetGuid(),
                payload.GetProperty("variantId").GetGuid(),
                payload.GetProperty("variantVersionId").ValueKind == JsonValueKind.Null
                    ? null
                    : payload.GetProperty("variantVersionId").GetGuid());

            if (ResourceReplaceRequested is null)
                throw new ProtocolViolationException("RESOURCE_REPLACE_UNAVAILABLE", "当前宿主未接入画布资源替换处理器", "warning");

            ResourceReplaceRequested.Invoke(request);
        }
        catch (ProtocolViolationException error) { SendError(error.Code, error.Message, error.Severity, "canvas/resource.replace.request"); }
        catch (Exception error) { SendError("RESOURCE_REPLACE_FAILED", error.Message, "warning", "canvas/resource.replace.request"); }
    }

    private async Task HandleInvocationAsync(JsonElement payload)
    {
        try
        {
            EnsureReady();
            var (invocation, key) = InvocationRequestParser.Parse(payload);
            await execution.StartAsync(session!, invocation, key);
        }
        catch (ProtocolViolationException error) { SendError(error.Code, error.Message, error.Severity, "canvas/invoke.request"); }
        catch (Exception error) { SendError("JOB_EXECUTION_FAILED", error.Message, "warning", "canvas/invoke.request"); }
    }

    private void HandleCancel(JsonElement payload)
    {
        try
        {
            EnsureReady();
            if (!payload.TryGetProperty("jobId", out var jobIdElement) || !Guid.TryParse(jobIdElement.GetString(), out var jobId)) throw new ProtocolViolationException("PROTOCOL_MALFORMED", "jobId 无效");
            execution.Cancel(session!, jobId);
        }
        catch (ProtocolViolationException error) { SendError(error.Code, error.Message, error.Severity, "canvas/job.cancel.request"); }
    }

    private void EnsureReady()
    {
        EnsureSession();
        if (!ready) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", "Canvas 尚未完成握手", "warning");
    }

    private object BuildCapabilities(SessionContext context) => new
    {
        serverClaims = context.ServerClaims.ToArray(),
        canEditCanvas = context.ServerClaims.Contains("canvas.edit"),
        canInvokeSkill = context.ServerClaims.Contains("skill.invoke"),
        canCancelJob = context.ServerClaims.Contains("job.cancel"),
        canUndo = context.ServerClaims.Contains("canvas.undo")
    };

    private void Send(string type, object payload)
    {
        var envelope = new { v = DreamForgeProtocol.Version, id = Guid.NewGuid(), type, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        transport.Send(document.RootElement.Clone());
    }

    private void EnsureSession()
    {
        if (session is null)
        {
            throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", "宿主会话尚未初始化", "warning");
        }
    }
}
