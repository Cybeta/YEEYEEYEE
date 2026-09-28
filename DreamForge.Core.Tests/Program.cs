using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using DreamForge.Core;
using DreamForge.Host;

var tests = new (string Name, Action Run)[]
{
    ("协议版本和方向校验", ProtocolValidation),
    ("能力位不能超出服务端声明", CapabilityFailClosed),
    ("TypedReference 循环引用拒绝", ReferenceCycle),
    ("TypedReference 缺失引用拒绝", MissingReference),
    ("Job 状态和进度", JobLifecycle),
    ("幂等结果和单人撤销", IdempotencyAndUndo),
    ("Canvas invoke 到 Job 回传", HostInvokeEndToEnd),
    ("Job 取消和失败终态", JobCancellationAndFailure),
    ("协议字段严格校验", StrictProtocolFields),
    ("资源版本替换协议", ResourceReplaceProtocol),
    ("资源版本替换状态", ResourceReplaceState),
    ("SQLite Job 持久化和恢复", SqliteJobPersistence),
    ("任务快照的调用信息与输入参数持久化", JobSnapshotPersistence),
    ("外部任务 ID 持久化", ExternalTaskIdPersistence),
    ("外部任务回调状态映射", ExternalTaskCallbackLifecycle),
    ("外部任务轮询器", ExternalTaskPolling),
    ("WebSocket 进度监听和释放", ExternalTaskProgressListening),
    ("ComfyUI 工作流、队列、取消和文件下载", ComfyUiWorkflowAndDownloadFlow),
("HTTP 回调签名防重放", SignedCallbackSecurity),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures.Add($"FAIL {test.Name}: {ex.Message}"); Console.WriteLine(failures[^1]); }
}

if (failures.Count > 0) Environment.ExitCode = 1;
else Console.WriteLine($"全部 {tests.Length} 项 Core 测试通过。");

static void ProtocolValidation()
{
    var valid = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"canvas/hello\",\"ts\":1,\"payload\":{\"canvasVersion\":\"0.1.0\",\"protocolVersion\":1,\"minHostProtocol\":1,\"features\":[]}}";
    using var doc = DreamForgeProtocol.Decode(valid, "canvasToHost");
    Expect(doc.RootElement.GetProperty("v").GetInt32() == 1, "有效协议未解码");
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(valid, "hostToCanvas"), "PROTOCOL_DIRECTION_MISMATCH");
    var badVersion = valid.Replace("\"v\":1", "\"v\":2");
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(badVersion, "canvasToHost"), "PROTOCOL_VERSION_MISMATCH");
}

static void CapabilityFailClosed()
{
    var json = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"host/capabilities\",\"ts\":1,\"payload\":{\"serverClaims\":[],\"canEditCanvas\":true,\"canInvokeSkill\":false,\"canCancelJob\":false,\"canUndo\":false}}";
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(json, "hostToCanvas"), "PROTOCOL_UNAUTHORIZED");
    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>() };
    Expect(!AccessPolicy.CanEditCanvas(session), "无声明会话不应可编辑");
}

static void ReferenceCycle()
{
    var a = Guid.NewGuid(); var b = Guid.NewGuid();
    var skills = new Dictionary<Guid, Skill>
    {
        [a] = new Skill { Id = a, References = [new DreamForge.Core.TypedReference { Kind = ReferenceKind.Skill, TargetId = b }] },
        [b] = new Skill { Id = b, References = [new DreamForge.Core.TypedReference { Kind = ReferenceKind.Skill, TargetId = a }] }
    };
    ExpectThrows<SkillReferenceException>(() => ReferenceGraph.Judge(skills[a], new Dictionary<Guid, Channel>(), new Dictionary<Guid, Tool>(), skills), "REFERENCE_CYCLE");
}

static void MissingReference()
{
    var skill = new Skill { References = [new DreamForge.Core.TypedReference { Kind = ReferenceKind.Tool, TargetId = Guid.NewGuid() }] };
    ExpectThrows<SkillReferenceException>(() => ReferenceGraph.Judge(skill, new Dictionary<Guid, Channel>(), new Dictionary<Guid, Tool>(), new Dictionary<Guid, Skill>()), "REFERENCE_UNRESOLVED");
}

static void JobLifecycle()
{
    var job = new Job(Guid.NewGuid(), Guid.NewGuid(), "idem-1");
    job.Transition(JobState.Running); job.ReportProgress(40); job.Complete([]);
    Expect(job.State == JobState.Succeeded && job.ProgressPercent == 100, "Job 完成状态错误");
    ExpectThrows<JobStateException>(() => job.Transition(JobState.Cancelled));
}

static void IdempotencyAndUndo()
{
    var registry = new IdempotencyRegistry(); var user = Guid.NewGuid(); var result = new ExecutionResult { JobId = Guid.NewGuid(), IdempotencyKey = "k", State = JobState.Succeeded };
    registry.Store(user, "k", result); Expect(registry.TryGet(user, "k", out var found) && found.JobId == result.JobId, "幂等结果未复用");
    var undo = new LocalUndoStack(); var op = new OperationRecord(); undo.Push(op); Expect(undo.Pop()?.OperationId == op.OperationId && undo.Pop() is null, "本地撤销栈错误");
}

static void HostInvokeEndToEnd()
{
    var transport = new InMemoryCanvasTransport();
    var bridge = new HostBridge(transport);
    var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ClientType = ClientType.Desktop, Role = MemberRole.Member, ServerClaims = new HashSet<string>(["skill.invoke", "canvas.edit"]) };
    bridge.Initialize(session);
    var hello = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"canvas/hello\",\"ts\":1,\"payload\":{\"canvasVersion\":\"0.1.0\",\"protocolVersion\":1,\"minHostProtocol\":1,\"features\":[]}}";
    using (var helloDocument = JsonDocument.Parse(hello)) transport.Deliver(helloDocument.RootElement.Clone());
    var invocationId = Guid.NewGuid();
    var invoke = JsonSerializer.Serialize(new { v = 1, id = Guid.NewGuid(), type = "canvas/invoke.request", ts = 1L, payload = new { invocation = new { invocationId, tool = "local.test", capability = (int)Capability.TextToText, channel = "local", inputs = new { }, assets = Array.Empty<object>() }, idempotencyKey = "e2e-key" } });
    using (var invokeDocument = JsonDocument.Parse(invoke)) transport.Deliver(invokeDocument.RootElement.Clone());
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (DateTime.UtcNow < deadline && !transport.Sent.Any(x => x.GetProperty("type").GetString() == "host/job.update" && x.GetProperty("payload").GetProperty("state").GetString() == "Succeeded")) Thread.Sleep(10);
    var updates = transport.Sent.Where(x => x.GetProperty("type").GetString() == "host/job.update").ToArray();
    var success = updates.Single(x => x.GetProperty("payload").GetProperty("state").GetString() == "Succeeded");
    var firstJobId = success.GetProperty("payload").GetProperty("jobId").GetGuid();
    Expect(success.GetProperty("payload").GetProperty("progressPercent").GetInt32() == 100, "Job 未回传 100% 进度");
    using (var duplicateDocument = JsonDocument.Parse(invoke)) transport.Deliver(duplicateDocument.RootElement.Clone());
    Thread.Sleep(50);
    var duplicateSuccess = transport.Sent.Where(x => x.GetProperty("type").GetString() == "host/job.update" && x.GetProperty("payload").GetProperty("state").GetString() == "Succeeded").Last();
    Expect(duplicateSuccess.GetProperty("payload").GetProperty("jobId").GetGuid() == firstJobId, "重复幂等键创建了新的 Job");
}

static void JobCancellationAndFailure()
{
    var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
    var slow = new SingleMachineExecutionService(new BlockingExecutor());
    var invocation = new Invocation { InvocationId = Guid.NewGuid(), Tool = "slow" };
    var task = slow.StartAsync(session, invocation, "cancel-key");
    SpinWait.SpinUntil(() => slow.GetJobs(session.UserId).Any(job => job.State == JobState.Running), 500);
    var jobId = slow.GetJobs(session.UserId).Single().JobId;
    slow.Cancel(session, jobId);
    var cancelled = task.GetAwaiter().GetResult();
    Expect(cancelled.State == JobState.Cancelled, "Job 未进入 Cancelled 终态");

    var failed = new SingleMachineExecutionService(new FailingExecutor()).StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "fail" }, "fail-key").GetAwaiter().GetResult();
    Expect(failed.State == JobState.Failed && failed.ErrorCode == "JOB_EXECUTION_FAILED", "Job 失败终态错误");
}

static void StrictProtocolFields()
{
    var valid = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"canvas/hello\",\"ts\":1,\"payload\":{\"canvasVersion\":\"0.1.0\",\"protocolVersion\":1,\"minHostProtocol\":1,\"features\":[]}}";
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(valid.Replace("11111111-1111-4111-8111-111111111111", "bad"), "canvasToHost"), "PROTOCOL_MALFORMED");
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(valid.Replace("\"ts\":1", "\"ts\":1.5"), "canvasToHost"), "PROTOCOL_MALFORMED");
}

static void ResourceReplaceProtocol()
{
    var recordId = Guid.NewGuid();
    var entityId = Guid.NewGuid();
    var variantId = Guid.NewGuid();
    var valid = JsonSerializer.Serialize(new
    {
        v = 1,
        id = Guid.NewGuid(),
        type = "canvas/resource.replace.request",
        ts = 1L,
        payload = new
        {
            recordId,
            entityId,
            variantId,
            variantVersionId = (Guid?)null
        }
    });
    using var decoded = DreamForgeProtocol.Decode(valid, "canvasToHost");
    Expect(decoded.RootElement.GetProperty("type").GetString() == "canvas/resource.replace.request", "资源替换请求未通过协议校验");

    var invalid = valid.Replace(recordId.ToString(), "bad-record-id", StringComparison.Ordinal);
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(invalid, "canvasToHost"), "PROTOCOL_MALFORMED");

    var missingVersion = valid.Replace(",\"variantVersionId\":null", string.Empty, StringComparison.Ordinal);
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(missingVersion, "canvasToHost"), "PROTOCOL_MALFORMED");

    var result = JsonSerializer.Serialize(new
    {
        v = 1,
        id = Guid.NewGuid(),
        type = "host/resource.replace.result",
        ts = 1L,
        payload = new
        {
            requestId = Guid.NewGuid(),
            ok = true,
            message = "资源版本替换成功。",
            revision = 3
        }
    });
    using var resultDocument = DreamForgeProtocol.Decode(result, "hostToCanvas");
    Expect(resultDocument.RootElement.GetProperty("type").GetString() == "host/resource.replace.result", "资源替换结果未通过协议校验");
    ExpectThrows<ProtocolViolationException>(() => DreamForgeProtocol.Decode(result, "canvasToHost"), "PROTOCOL_DIRECTION_MISMATCH");
}

static void ResourceReplaceState()
{
    var entity = new DreamForge.Desktop.WorkflowEntity { Name = "主角" };
    var variant = entity.CreateVariant("战斗服");
    var version = variant.EnsureInitialVersion();
    var node = new DreamForge.Desktop.WorkflowNode();
    node.References.Add(new DreamForge.Desktop.NodeReference
    {
        EntityId = entity.Id,
        VariantId = variant.Id,
        VariantVersionId = version.Id
    });
    var state = new DreamForge.Desktop.WorkflowCanvasState
    {
        Nodes = [node],
        Entities = [entity]
    };

    Expect(state.ReplaceReferenceVersion(node.Id, entity.Id, variant.Id, null, out _), "解除版本锁定失败");
    Expect(node.References[0].VariantVersionId is null, "空版本未解除锁定");
    Expect(!state.ReplaceReferenceVersion(node.Id, entity.Id, variant.Id, Guid.NewGuid(), out var versionError)
        && versionError.Contains("不属于", StringComparison.Ordinal), "非法版本未拒绝");
    node.IsLocked = true;
    Expect(!state.ReplaceReferenceVersion(node.Id, entity.Id, variant.Id, version.Id, out var lockError)
        && lockError.Contains("锁定", StringComparison.Ordinal), "锁定节点仍可替换");
}

static void SqliteJobPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"dreamforge-{Guid.NewGuid():N}.db");
    try
    {
        var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
        Guid jobId;
        using (var store = new SqliteJobStore(path))
        {
            var service = new SingleMachineExecutionService(store: store);
            var result = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "persist" }, "persist-key").GetAwaiter().GetResult();
            jobId = result.JobId;
            Expect(result.State == JobState.Succeeded, "持久化前 Job 未成功");
        }
        using (var restoredStore = new SqliteJobStore(path))
        {
            var restored = new SingleMachineExecutionService(store: restoredStore);
            Expect(restored.TryGet(jobId, out var job) && job?.State == JobState.Succeeded, "成功 Job 未从 SQLite 恢复");
            var duplicate = restored.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "different" }, "persist-key").GetAwaiter().GetResult();
            Expect(duplicate.JobId == jobId, "恢复后的幂等结果未复用");
            restoredStore.Save(new ExecutionResult { UserId = session.UserId, JobId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), IdempotencyKey = "stale-key", State = JobState.Running, ProgressPercent = 25 });
        }
        using (var recoveryStore = new SqliteJobStore(path))
        {
            var recovery = new SingleMachineExecutionService(store: recoveryStore);
            var stale = recovery.GetJobs(session.UserId).Single(job => job.IdempotencyKey == "stale-key");
            Expect(stale.State == JobState.Failed && stale.ToResult().ErrorCode == "HOST_RESTARTED", "未完成 Job 未标记宿主重启失败");
        }
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void JobSnapshotPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"dreamforge-{Guid.NewGuid():N}.db");
    try
    {
        var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
        var invocation = new Invocation
        {
            InvocationId = Guid.NewGuid(),
            Tool = "text-to-image",
            Capability = Capability.TextToImage,
            Channel = "comfyui",
            Inputs = new Dictionary<string, JsonElement>
            {
                ["prompt"] = JsonSerializer.SerializeToElement("一只猫"),
                ["steps"] = JsonSerializer.SerializeToElement(28)
            }
        };
        Guid jobId;
        using (var store = new SqliteJobStore(path))
        {
            var service = new SingleMachineExecutionService(store: store);
            jobId = service.StartAsync(session, invocation, "snapshot-key").GetAwaiter().GetResult().JobId;
        }

        using (var store = new SqliteJobStore(path))
        {
            var service = new SingleMachineExecutionService(store: store);
            Expect(service.TryGet(jobId, out var job) && job is not null, "未恢复任务记录");
            Expect(job!.Tool == "text-to-image", "工具未持久化");
            Expect(job.Capability == Capability.TextToImage, "能力未持久化");
            Expect(job.Channel == "comfyui", "通道未持久化");
            Expect(job.Inputs.Count == 2, "输入参数数量未持久化");
            Expect(job.Inputs["prompt"].GetString() == "一只猫", "输入参数内容未持久化");
            Expect(job.Inputs["steps"].GetInt32() == 28, "输入参数数值未持久化");
        }
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void ExternalTaskIdPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"dreamforge-external-{Guid.NewGuid():N}.db");
    try
    {
        var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
        Guid jobId;
        using (var store = new SqliteJobStore(path))
        {
            var service = new SingleMachineExecutionService(new ExternalTaskExecutor(), store);
            var result = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "comfyui" }, "external-key").GetAwaiter().GetResult();
            jobId = result.JobId;
            Expect(result.ExternalTaskId == "comfyui-queue-42", "执行结果未返回外部任务 ID");
        }
        using (var store = new SqliteJobStore(path))
        {
            var restored = new SingleMachineExecutionService(store: store);
            Expect(restored.TryGet(jobId, out var job) && job?.ToResult().ExternalTaskId == "comfyui-queue-42", "外部任务 ID 未从 SQLite 恢复");
        }
        var jobModel = new Job(Guid.NewGuid(), session.UserId, "attach-key");
        jobModel.Transition(JobState.Running);
        jobModel.AttachExternalTask("first");
        ExpectThrows<InvalidOperationException>(() => jobModel.AttachExternalTask("second"));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void ExternalTaskCallbackLifecycle()
{
    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    var service = new SingleMachineExecutionService(new ExternalTaskExecutor());
    var result = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "external" }, "callback-key").GetAwaiter().GetResult();
    var receiver = new ExternalTaskCallbackReceiver(service);
    Expect(receiver.Receive(session.UserId, new ExternalTaskUpdate { ExternalTaskId = result.ExternalTaskId!, State = ExternalTaskState.Running, ProgressPercent = 60 }), "运行状态回调未接收");
    Expect(service.TryGet(result.JobId, out var running) && running!.State == JobState.Running && running.ProgressPercent == 60, "运行状态映射或进度单调性错误");
    Expect(receiver.Receive(session.UserId, new ExternalTaskUpdate { ExternalTaskId = result.ExternalTaskId!, State = ExternalTaskState.Succeeded, ProgressPercent = 100, Outputs = [new AssetRef { Role = "output", Ref = "external://done" }] }), "成功回调未接收");
    Expect(service.TryGet(result.JobId, out var completed) && completed!.State == JobState.Succeeded && completed.Outputs.Count == 1, "成功状态映射错误");
    Expect(receiver.Receive(session.UserId, new ExternalTaskUpdate { ExternalTaskId = result.ExternalTaskId!, State = ExternalTaskState.Succeeded, ProgressPercent = 80 }), "重复成功回调不应失败");
    Expect(!receiver.Receive(Guid.NewGuid(), new ExternalTaskUpdate { ExternalTaskId = result.ExternalTaskId!, State = ExternalTaskState.Failed, ProgressPercent = 100 }), "其他用户不应匹配外部任务");
}

static void ExternalTaskPolling()
{
    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    var service = new SingleMachineExecutionService(new ExternalTaskExecutor());
    var result = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "poll" }, "poll-key").GetAwaiter().GetResult();
    var provider = new TestExternalProvider(result.ExternalTaskId!);
    var poller = new ExternalTaskPoller(service, provider, TimeSpan.FromMilliseconds(10));
    poller.Start();
    SpinWait.SpinUntil(() => service.TryGet(result.JobId, out var job) && job!.State == JobState.Succeeded, TimeSpan.FromSeconds(2));
    Expect(service.TryGet(result.JobId, out var completed) && completed!.State == JobState.Succeeded, "轮询器未完成外部 Job");
    poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void ExternalTaskProgressListening()
{
    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    var service = new SingleMachineExecutionService(new ExternalTaskExecutor());
    var result = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "progress" }, "progress-key").GetAwaiter().GetResult();
    var provider = new TestProgressProvider(result.ExternalTaskId!);
    var poller = new ExternalTaskPoller(service, provider, TimeSpan.FromMilliseconds(10));
    poller.Start();
    Expect(SpinWait.SpinUntil(() => service.TryGet(result.JobId, out var job) && job!.ProgressPercent == 42, TimeSpan.FromSeconds(2)), "WebSocket 进度未写入 Job");
    poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Expect(provider.Stopped.Task.Wait(TimeSpan.FromSeconds(1)), "WebSocket 监听任务未在轮询器停止时释放");
}

static void ComfyUiWorkflowAndDownloadFlow()
{
    var workflow = new ComfyUiWorkflowFactory("model.safetensors").Create(new Invocation
    {
        Tool = "txt2img",
        Inputs = new Dictionary<string, JsonElement>
        {
            ["prompt"] = JsonDocument.Parse("\"a castle\"").RootElement.Clone(),
            ["width"] = JsonDocument.Parse("4096").RootElement.Clone(),
            ["steps"] = JsonDocument.Parse("200").RootElement.Clone()
        }
    });
    Expect(workflow.GetProperty("6").GetProperty("inputs").GetProperty("text").GetString() == "a castle", "动态工作流未映射 prompt");
    Expect(workflow.GetProperty("5").GetProperty("inputs").GetProperty("width").GetInt32() == 2048, "工作流尺寸边界未生效");
    Expect(workflow.GetProperty("3").GetProperty("inputs").GetProperty("steps").GetInt32() == 150, "工作流 steps 边界未生效");

    var directory = Path.Combine(Path.GetTempPath(), "dreamforge-assets-" + Guid.NewGuid().ToString("N"));
    var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal)
        ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(new { prompt_id = "42" }) }
        : request.RequestUri!.AbsolutePath.EndsWith("/history/42", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"42\":{\"status\":{\"status_str\":\"success\"},\"outputs\":{\"3\":{\"images\":[{\"filename\":\"out.png\",\"subfolder\":\"x\",\"type\":\"output\"}]}}}}") }
            : request.RequestUri!.AbsolutePath.EndsWith("/history/missing", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }
            : request.RequestUri!.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"queue_running\":[],\"queue_pending\":[[0,\"42\",{}]]}") }
                : request.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal)
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }
                    : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    try
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://comfy.local/") };
        var executor = new ComfyUiExecutor(http, _ => JsonDocument.Parse("{}").RootElement.Clone());
        var session = new SessionContext { UserId = Guid.NewGuid() };
        var job = new Job(Guid.NewGuid(), session.UserId, "comfy-key");
        var output = executor.ExecuteAsync(session, new Invocation { Tool = "txt2img" }, job, CancellationToken.None).GetAwaiter().GetResult();
        Expect(output.ExternalTaskId == "42" && output.AwaitExternalCompletion, "ComfyUI prompt_id 提交解析失败");
        var provider = new ComfyUiProvider(http, directory);
        var update = provider.GetStatusAsync("42", CancellationToken.None).GetAwaiter().GetResult();
        var asset = update.Outputs.Single().Ref;
        Expect(update.State == ExternalTaskState.Succeeded && asset.StartsWith("asset://", StringComparison.Ordinal), "ComfyUI 输出未转换为本地 Asset");
        Expect(File.Exists(Path.Combine(directory, asset[8..])), "ComfyUI 输出文件未下载");

        var queued = provider.GetStatusAsync("missing", CancellationToken.None).GetAwaiter().GetResult();
        Expect(queued.State == ExternalTaskState.Queued, "ComfyUI pending 队列状态映射失败");
        provider.CancelAsync("42", CancellationToken.None).GetAwaiter().GetResult();
        Expect(handler.Requests.Any(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal)), "ComfyUI interrupt 未调用");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void SignedCallbackSecurity()
{
    var secret = Encoding.UTF8.GetBytes("0123456789abcdef-secret");
    var verifier = new HmacCallbackVerifier(new CallbackSignatureOptions { Secret = secret, AllowedClockSkew = TimeSpan.FromMinutes(5) });
    var body = Encoding.UTF8.GetBytes("{\"externalTaskId\":\"42\",\"state\":\"Succeeded\",\"progressPercent\":100}");
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    var nonce = Guid.NewGuid().ToString("N");
    var canonical = Encoding.UTF8.GetBytes($"{timestamp}.{Convert.ToBase64String(body)}");
    var signature = "sha256=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(secret, canonical));
    verifier.Verify(timestamp, nonce, signature, body);
    ExpectThrows<CallbackReplayException>(() => verifier.Verify(timestamp, nonce, signature, body));
    ExpectThrows<System.Security.Cryptography.CryptographicException>(() => verifier.Verify(timestamp, Guid.NewGuid().ToString("N"), "sha256=bad", body));
    ExpectThrows<CallbackReplayException>(() => verifier.Verify((DateTimeOffset.UtcNow - TimeSpan.FromHours(1)).ToUnixTimeSeconds().ToString(), Guid.NewGuid().ToString("N"), signature, body));

    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    var service = new SingleMachineExecutionService(new ExternalTaskExecutor());
    var result = service.StartAsync(session, new Invocation { Tool = "callback" }, "signed-callback-key").GetAwaiter().GetResult();
    var callbackBody = Encoding.UTF8.GetBytes($"{{\"externalTaskId\":\"{result.ExternalTaskId}\",\"state\":\"Succeeded\",\"progressPercent\":100}}");
    var callbackTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    var callbackNonce = Guid.NewGuid().ToString("N");
    var callbackCanonical = Encoding.UTF8.GetBytes($"{callbackTimestamp}.{Convert.ToBase64String(callbackBody)}");
    var callbackSignature = "sha256=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(secret, callbackCanonical));
    var callbackHandler = new SignedExternalCallbackHandler(new HmacCallbackVerifier(new CallbackSignatureOptions { Secret = secret }), new ExternalTaskCallbackReceiver(service));
    Expect(callbackHandler.Handle(session.UserId, callbackTimestamp, callbackNonce, callbackSignature, callbackBody), "签名回调未进入外部任务接收器");
    Expect(service.TryGet(result.JobId, out var completed) && completed!.State == JobState.Succeeded, "签名回调未完成 Job");
}

static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void ExpectThrows<T>(Action action, string? code = null) where T : Exception
{
    try { action(); throw new InvalidOperationException($"预期抛出 {typeof(T).Name}"); }
    catch (T ex) { if (code is not null && ex is ProtocolViolationException protocol && protocol.Code != code || code is not null && ex is SkillReferenceException reference && reference.Code != code) throw new InvalidOperationException($"错误码不匹配，实际为 {ex.Message}"); }
}

sealed class BlockingExecutor : IInvocationExecutor
{
    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken) { await Task.Delay(5000, cancellationToken); return new ExecutionOutput(); }
}

sealed class FailingExecutor : IInvocationExecutor
{
    public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken) => throw new InvalidOperationException("模拟执行器失败");
}

sealed class ExternalTaskExecutor : IInvocationExecutor
{
    public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken) => Task.FromResult(new ExecutionOutput { ExternalTaskId = "comfyui-queue-42", AwaitExternalCompletion = true, Outputs = [] });
}

sealed class TestExternalProvider : IExternalTaskProvider
{
    private readonly string id;
    public TestExternalProvider(string id) => this.id = id;
    public Task<ExternalTaskUpdate> GetStatusAsync(string externalTaskId, CancellationToken cancellationToken) => Task.FromResult(new ExternalTaskUpdate { ExternalTaskId = id, State = ExternalTaskState.Succeeded, ProgressPercent = 100 });
}

sealed class TestProgressProvider : IExternalTaskProvider, IExternalTaskProgressSource
{
    private readonly string id;
    public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestProgressProvider(string id) => this.id = id;

    public Task<ExternalTaskUpdate> GetStatusAsync(string externalTaskId, CancellationToken cancellationToken)
        => Task.FromResult(new ExternalTaskUpdate { ExternalTaskId = id, State = ExternalTaskState.Queued, ProgressPercent = 0 });

    public async Task ListenForProgressAsync(string externalTaskId, Action<ExternalTaskUpdate> onUpdate, CancellationToken cancellationToken)
    {
        onUpdate(new ExternalTaskUpdate { ExternalTaskId = id, State = ExternalTaskState.Running, ProgressPercent = 42 });
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Stopped.TrySetResult(true);
        }
    }
}

sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;
    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        this.handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(handler(request));
    }
}
