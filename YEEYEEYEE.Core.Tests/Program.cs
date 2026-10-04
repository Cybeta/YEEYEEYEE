using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

var tests = new (string Name, Action Run)[]
{
    ("统一出场：跨章隔离、双向编辑、版本锁定及保存重开", UnifiedAppearances),
    ("协议版本和方向校验", ProtocolValidation),
    ("能力位不能超出服务端声明", CapabilityFailClosed),
    ("TypedReference 循环引用拒绝", ReferenceCycle),
    ("TypedReference 缺失引用拒绝", MissingReference),
    ("Job 状态和进度", JobLifecycle),
    ("幂等结果和单人撤销", IdempotencyAndUndo),
    ("别人的编辑锁挡得住，自己不挡", RespectOthersLease),
    ("Job 取消和失败终态", JobCancellationAndFailure),
    ("任务重试与尝试链", JobRetryLifecycle),
    ("任务重试的权限与归属", JobRetryAuthorization),
    ("任务重试血缘的持久化与恢复", JobRetryPersistence),
    ("返工 R1：重试上限按整条尝试链判定，历史源与并发都绕不过", JobRetryCapCannotBeBypassed),
    ("协议字段严格校验", StrictProtocolFields),
    ("资源版本替换协议（照两端共读的固定样本验）", ResourceReplaceProtocol),
    ("协议表只有一份：C# 嵌的就是网页端那份 json（逐字节）", ProtocolTableIsSharedWithCanvas),
    ("资源版本替换状态", ResourceReplaceState),
    ("引用媒体草稿隔离与定向提交", ReferenceMediaDraftIsolation),
    ("媒体版本回滚仅影响草稿", MediaVersionRollbackDraftOnly),
    ("媒体提交当前及全部引用范围", MediaCommitReferenceScopes),
    ("子引用版本快照和隔离复制", NestedReferenceSnapshots),
    ("AI 导入自动建立角色技能道具引用", AgentCharacterNestedReferences),
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

/// <summary>
/// 钉住统一出场的跨章隔离、双向编辑、锁定版本与保存重开：本章编辑不泄漏到他章，节点改动能回写树，锁版与跟随最新语义不丢。
/// </summary>
static void UnifiedAppearances()
{
    var state = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Name = "角色" };
    var variant = entity.CreateVariant("默认");
    state.Entities.Add(entity);
    var a = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "一" };
    var b = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "二" };
    state.WorkTree.AddRange([a, b]);
    UnifiedWorkTree.RefreshResources(state);
    var first = UnifiedWorkTree.AddAppearance(state, entity.Id, a.Id);
    var second = UnifiedWorkTree.AddAppearance(state, entity.Id, b.Id);
    var node = UnifiedWorkTree.CreateNode(state, first, 10, 20);
    var other = UnifiedWorkTree.CreateNode(state, second, 30, 40);
    UnifiedWorkTree.Edit(state, first, "角色·受伤", "左手受伤");
    Expect(node.Content == "左手受伤" && other.Content == "" && entity.Core == "", "局部状态泄漏");
    node.Content = "已包扎";
    UnifiedWorkTree.FromNode(state, node);
    Expect(first.LocalState == "已包扎", "节点没有回写树");
    var version = variant.Commit("锁定");
    UnifiedWorkTree.SetVersion(state, first, version.Id);
    variant.Description = "新描述"; variant.Commit("后续版本");
    Expect(node.References.Single().VariantVersionId == version.Id && other.References.Single().VariantVersionId is null, "锁定或跟随最新失效");
    node.IsLocked = true;
    ExpectThrows<InvalidOperationException>(() => UnifiedWorkTree.Edit(state, first, "改名", "改动"));
    var reopened = JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(state))!;
    Expect(reopened.WorkTree.Single(x => x.Id == first.Id).LocalState == "已包扎", "本章状态丢失");
    Expect(reopened.Nodes[0].References[0].VariantVersionId == version.Id, "锁版丢失");
    var ai = new WorkflowCanvasState();
    var result = AgentActionExecutor.Apply(new[] {
        new AgentAction { Kind = "create_node", Title = "投影", WorkTreeTarget = "角色出场" },
        new AgentAction { Kind = "create_work_item", WorkTreeKind = "Appearance", Title = "角色出场", EntityTarget = "共享角色", ParentTarget = "章节", Content = "雨中" },
        new AgentAction { Kind = "create_work_item", WorkTreeKind = "Chapter", Title = "章节" },
        new AgentAction { Kind = "create_entity", Title = "共享角色" }
    }, ai, null);
    Expect(result.Errors.Count == 0 && ai.Nodes.Single().Content == "雨中", "AI 未先建资源与树再投影");
}

/// <summary>
/// 钉住协议版本与方向校验：方向反了或版本对不上都要按对应错误码拒绝。
/// </summary>
static void ProtocolValidation()
{
    var valid = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"canvas/hello\",\"ts\":1,\"payload\":{\"canvasVersion\":\"0.1.0\",\"protocolVersion\":1,\"minHostProtocol\":1,\"features\":[]}}";
    using var doc = YEEYEEYEEProtocol.Decode(valid, "canvasToHost");
    Expect(doc.RootElement.GetProperty("v").GetInt32() == 1, "有效协议未解码");
    ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(valid, "hostToCanvas"), "PROTOCOL_DIRECTION_MISMATCH");
    var badVersion = valid.Replace("\"v\":1", "\"v\":2");
    ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(badVersion, "canvasToHost"), "PROTOCOL_VERSION_MISMATCH");
}

/// <summary>
/// 钉住能力位不能超出服务端声明：解析越权报文要拒绝，没有声明的会话一律不可编辑。
/// </summary>
static void CapabilityFailClosed()
{
    var json = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"host/capabilities\",\"ts\":1,\"payload\":{\"serverClaims\":[],\"canEditCanvas\":true,\"canInvokeSkill\":false,\"canCancelJob\":false,\"canUndo\":false}}";
    ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(json, "hostToCanvas"), "PROTOCOL_UNAUTHORIZED");
    var session = new SessionContext { UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>() };
    Expect(!AccessPolicy.CanEditCanvas(session), "无声明会话不应可编辑");
}

/// <summary>
/// 钉住 TypedReference 的循环引用被拒绝，避免解析时无限递归。
/// </summary>
static void ReferenceCycle()
{
    var a = Guid.NewGuid(); var b = Guid.NewGuid();
    var skills = new Dictionary<Guid, Skill>
    {
        [a] = new Skill { Id = a, References = [new YEEYEEYEE.Core.TypedReference { Kind = ReferenceKind.Skill, TargetId = b }] },
        [b] = new Skill { Id = b, References = [new YEEYEEYEE.Core.TypedReference { Kind = ReferenceKind.Skill, TargetId = a }] }
    };
    ExpectThrows<SkillReferenceException>(() => ReferenceGraph.Judge(skills[a], new Dictionary<Guid, Channel>(), new Dictionary<Guid, Tool>(), skills), "REFERENCE_CYCLE");
}

/// <summary>
/// 钉住 TypedReference 指向不存在的目标时报未解析错误，而不是静默跳过。
/// </summary>
static void MissingReference()
{
    var skill = new Skill { References = [new YEEYEEYEE.Core.TypedReference { Kind = ReferenceKind.Tool, TargetId = Guid.NewGuid() }] };
    ExpectThrows<SkillReferenceException>(() => ReferenceGraph.Judge(skill, new Dictionary<Guid, Channel>(), new Dictionary<Guid, Tool>(), new Dictionary<Guid, Skill>()), "REFERENCE_UNRESOLVED");
}

/// <summary>
/// 钉住 Job 状态与进度的基本流转：完成后进度归 100，终态不能再被改。
/// </summary>
static void JobLifecycle()
{
    var job = new Job(Guid.NewGuid(), Guid.NewGuid(), "idem-1");
    job.Transition(JobState.Running); job.ReportProgress(40); job.Complete([]);
    Expect(job.State == JobState.Succeeded && job.ProgressPercent == 100, "Job 完成状态错误");
    ExpectThrows<JobStateException>(() => job.Transition(JobState.Cancelled));
}

/// <summary>
/// 钉住幂等结果可复用，以及本地撤销栈的后进先出与清空。
/// </summary>
static void IdempotencyAndUndo()
{
    var registry = new IdempotencyRegistry(); var user = Guid.NewGuid(); var result = new ExecutionResult { JobId = Guid.NewGuid(), IdempotencyKey = "k", State = JobState.Succeeded };
    registry.Store(user, "k", result); Expect(registry.TryGet(user, "k", out var found) && found.JobId == result.JobId, "幂等结果未复用");
    var undo = new LocalUndoStack(); var op = new OperationRecord(); undo.Push(op); Expect(undo.Pop()?.OperationId == op.OperationId && undo.Pop() is null, "本地撤销栈错误");
}

/// <summary>
/// 别人的节点锁挡得住，自己不挡（第 182 轮：桌面端开始**遵守**锁）。
///
/// 这条判断是「别把别人的改动盖掉」的最后一道依据——桌面端的检查器可编辑性、节点编辑入口、
/// 出图三条路都问它，所以它自己必须是对的：自己占的锁不算（否则自己都改不了自己锁着的节点），
/// 整树锁也不算（它的目标是画布，不是某个节点）。
/// </summary>
static void RespectOthersLease()
{
    var me = Guid.NewGuid();
    var other = Guid.NewGuid();
    var node = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var expires = now.AddMinutes(5);
    var theirs = new YEEYEEYEE.Desktop.CollaborationLease(Guid.NewGuid(), "node", node, other, "林晚", "web", now, expires);
    var mine = new YEEYEEYEE.Desktop.CollaborationLease(Guid.NewGuid(), "node", node, me, "我", "desktop", now, expires);
    var tree = new YEEYEEYEE.Desktop.CollaborationLease(Guid.NewGuid(), "tree", Guid.NewGuid(), other, "林晚", "web", now, expires);
    var elsewhere = new YEEYEEYEE.Desktop.CollaborationLease(Guid.NewGuid(), "node", Guid.NewGuid(), other, "林晚", "web", now, expires);

    Expect(YEEYEEYEE.Desktop.CollaborationSession.HeldByOthersOf([theirs], me, node) == theirs, "别人的节点锁没挡住");
    Expect(YEEYEEYEE.Desktop.CollaborationSession.HeldByOthersOf([mine], me, node) is null, "自己的锁把自己挡在门外了");
    Expect(YEEYEEYEE.Desktop.CollaborationSession.HeldByOthersOf([elsewhere], me, node) is null, "把别的节点的锁算到了这个节点头上");
    Expect(YEEYEEYEE.Desktop.CollaborationSession.HeldByOthersOf([tree], me, node) is null, "整树锁不该按节点算成只读");
    Expect(YEEYEEYEE.Desktop.CollaborationSession.HeldByOthersOf([mine, theirs], me, node) == theirs, "两条锁同时在时没认出别人的那条");
}

/// <summary>
/// 钉住取消与失败都能落到终态：取消进 Cancelled，执行异常进 Failed 并带错误码。
/// </summary>
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

/// <summary>目标 5 / 失败重试：终态任务可重试，尝试次数有上限，并记录完整的尝试链。</summary>
static void JobRetryLifecycle()
{
    var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
    var service = new SingleMachineExecutionService(new FlakyExecutor(failFirst: 2));
    var invocation = new Invocation
    {
        InvocationId = Guid.NewGuid(),
        Tool = "flaky",
        Capability = Capability.TextToImage,
        Channel = "local",
        Inputs = new Dictionary<string, JsonElement> { ["prompt"] = JsonSerializer.SerializeToElement("一只猫走进雨里") }
    };

    var first = service.StartAsync(session, invocation, "retry-key-1").GetAwaiter().GetResult();
    Expect(first.State == JobState.Failed, "第一次尝试应失败");
    Expect(first.Attempt == 1 && first.RetryOfJobId is null && first.RootJobId == first.JobId,
        "首次尝试应记为第 1 次、无重试来源、根任务是自己");

    var second = service.RetryAsync(session, first.JobId, "retry-key-2").GetAwaiter().GetResult();
    Expect(second.State == JobState.Failed, "第二次尝试在模拟器里仍应失败");
    Expect(second.Attempt == 2, "重试后尝试次数应为 2：" + second.Attempt);
    Expect(second.JobId != first.JobId, "重试应发起新的 Job，而不是复用失败的 Job");
    Expect(second.RetryOfJobId == first.JobId, "重试应记录来源 Job");
    Expect(second.RootJobId == first.JobId, "重试应沿用同一个根任务");
    Expect(second.Tool == invocation.Tool && second.Capability == Capability.TextToImage && second.Channel == "local",
        "重试应沿用工具、能力与通道");
    Expect(second.Inputs.TryGetValue("prompt", out var carried) && carried.GetString() == "一只猫走进雨里", "重试应沿用输入参数");

    var third = service.RetryAsync(session, second.JobId, "retry-key-3").GetAwaiter().GetResult();
    Expect(third.State == JobState.Succeeded, "第三次尝试应成功：" + third.State);
    Expect(third.Attempt == 3, "第三次尝试次数应为 3：" + third.Attempt);
    Expect(third.RetryOfJobId == second.JobId && third.RootJobId == first.JobId, "第三次尝试的血缘应正确");

    // 已达上限：显式给 1 次上限时，第 1 次尝试就不再允许重试。
    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(session, first.JobId, "retry-key-4", maxAttempts: 1).GetAwaiter().GetResult(),
        "JOB_NOT_RETRYABLE");
    // 成功的任务不能再重试。
    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(session, third.JobId, "retry-key-5").GetAwaiter().GetResult(),
        "JOB_NOT_RETRYABLE");
    // 缺少幂等键：拒绝。
    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(session, first.JobId, "  ").GetAwaiter().GetResult(),
        "PROTOCOL_MALFORMED");

    var chain = service.GetAttemptChain(third.JobId);
    Expect(chain.Count == 3, "尝试链应有 3 次尝试：" + chain.Count);
    Expect(chain.Select(job => job.Attempt).SequenceEqual([1, 2, 3]), "尝试链应按尝试次数排序");
    Expect(chain.All(job => job.RootJobId == first.JobId), "尝试链应共享同一个根任务");
    Expect(JobRetryPolicy.DescribeAttempt(chain[1], JobRetryPolicy.DefaultMaxAttempts).Contains("第 2 次尝试"), "尝试描述应可读");
    Expect(JobRetryPolicy.CanRetry(chain[2], JobRetryPolicy.DefaultMaxAttempts, out var reason) == false && reason.Contains("失败或已取消"),
        "成功的任务应被判为不可重试并给出原因：" + reason);
}

/// <summary>目标 5 / 权限：重试与取消都必须经过会话声明与归属校验。</summary>
static void JobRetryAuthorization()
{
    var owner = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
    var service = new SingleMachineExecutionService(new FailingExecutor());
    var failed = service.StartAsync(owner, new Invocation { InvocationId = Guid.NewGuid(), Tool = "fail" }, "auth-key-1").GetAwaiter().GetResult();
    Expect(failed.State == JobState.Failed, "准备用的任务应失败");

    var noInvoke = owner with { ServerClaims = new HashSet<string>(["job.cancel"]) };
    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(noInvoke, failed.JobId, "auth-key-2").GetAwaiter().GetResult(),
        "PROTOCOL_UNAUTHORIZED");

    var stranger = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(stranger, failed.JobId, "auth-key-3").GetAwaiter().GetResult(),
        "JOB_FORBIDDEN");

    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(owner, Guid.NewGuid(), "auth-key-4").GetAwaiter().GetResult(),
        "JOB_NOT_FOUND");

    // 取消同样受声明约束：没有 job.cancel 的会话不能取消别人（或自己）的任务。
    var blocking = new SingleMachineExecutionService(new BlockingExecutor());
    var running = blocking.StartAsync(owner, new Invocation { InvocationId = Guid.NewGuid(), Tool = "slow" }, "auth-key-5");
    SpinWait.SpinUntil(() => blocking.GetJobs(owner.UserId).Any(job => job.State == JobState.Running), 1000);
    var runningJobId = blocking.GetJobs(owner.UserId).Single().JobId;
    var noCancel = owner with { ServerClaims = new HashSet<string>(["skill.invoke"]) };
    ExpectThrows<ProtocolViolationException>(() => blocking.Cancel(noCancel, runningJobId), "PROTOCOL_UNAUTHORIZED");
    var strangerWithCancel = stranger with { ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
    ExpectThrows<ProtocolViolationException>(() => blocking.Cancel(strangerWithCancel, runningJobId), "JOB_FORBIDDEN");
    blocking.Cancel(owner, runningJobId);
    Expect(running.GetAwaiter().GetResult().State == JobState.Cancelled, "有权限的会话取消后应进入 Cancelled");
}

/// <summary>目标 5 / 任务状态落盘：尝试次数与重试血缘必须能跨重启读回。</summary>
static void JobRetryPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"yeeeyee-retry-{Guid.NewGuid():N}.db");
    var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke"]) };
    try
    {
        Guid rootJobId;
        Guid secondJobId;
        using (var store = new SqliteJobStore(path))
        {
            var service = new SingleMachineExecutionService(new FlakyExecutor(failFirst: 1), store);
            var first = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "flaky" }, "persist-key-1").GetAwaiter().GetResult();
            var second = service.RetryAsync(session, first.JobId, "persist-key-2").GetAwaiter().GetResult();
            Expect(second.State == JobState.Succeeded && second.Attempt == 2, "重试应成功并记为第 2 次尝试");
            rootJobId = first.JobId;
            secondJobId = second.JobId;
        }

        using var reopened = new SqliteJobStore(path);
        var restoredService = new SingleMachineExecutionService(new FlakyExecutor(failFirst: 0), reopened);
        Expect(restoredService.TryGet(secondJobId, out var reloaded) && reloaded is not null, "重启后应能读回任务");
        Expect(reloaded!.Attempt == 2, "重启后尝试次数应保留：" + reloaded.Attempt);
        Expect(reloaded.RetryOfJobId == rootJobId, "重启后重试来源应保留");
        Expect(reloaded.RootJobId == rootJobId, "重启后根任务应保留");
        Expect(reloaded.IdempotencyKey == "persist-key-2", "重启后幂等键应保留");
        Expect(restoredService.GetAttemptChain(secondJobId).Count == 2, "重启后尝试链应保留");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// 目标 5 返工 R1：重试上限必须按**整条尝试链**判定。返工前的绕过路径是
/// 「每次都拿最初那个失败任务去重试、每次换新幂等键」，因为旧实现取的是 source.Attempt + 1，
/// 于是永远只产生第 2 次尝试，上限形同虚设。这里逐条覆盖：历史源连续重试、同根已有成功尝试、
/// 同根已有进行中的尝试、以及宿主重启后规则是否仍然成立。
/// </summary>
static void JobRetryCapCannotBeBypassed()
{
    var session = new SessionContext
    {
        SessionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"])
    };
    const int max = JobRetryPolicy.DefaultMaxAttempts;

    // 1) 反复重试同一个失败的历史源（每次换新幂等键）：尝试号要一路涨到上限，然后被拒
    var service = new SingleMachineExecutionService(new FailingExecutor());
    var first = service.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "fail" }, "cap-1").GetAwaiter().GetResult();
    Expect(first.State == JobState.Failed && first.Attempt == 1, "准备用任务应失败且是第 1 次尝试");

    var second = service.RetryAsync(session, first.JobId, "cap-2").GetAwaiter().GetResult();
    Expect(second.Attempt == 2, "第一次重试应给第 2 次尝试：" + second.Attempt);
    var third = service.RetryAsync(session, first.JobId, "cap-3").GetAwaiter().GetResult();
    Expect(third.Attempt == 3, "从历史源再重试应递增到第 3 次（返工前会停在第 2 次）：" + third.Attempt);
    Expect(third.RetryOfJobId == first.JobId && third.RootJobId == first.JobId, "血缘应指向被重试的历史源与同一根任务");

    ExpectThrows<ProtocolViolationException>(
        () => service.RetryAsync(session, first.JobId, "cap-4").GetAwaiter().GetResult(),
        "JOB_NOT_RETRYABLE");
    var chain = service.GetAttemptChain(first.JobId);
    Expect(chain.Count == max, $"尝试链应停在上限 {max} 次：" + chain.Count);
    Expect(chain.Select(job => job.Attempt).Distinct().Count() == max, "尝试号不得重复");
    Expect(!service.CanRetryJob(first.JobId, session.UserId, max, out var capped) && capped.Contains("上限"),
        "界面预检也应给出上限原因：" + capped);

    // 2) 同根已有成功尝试：重试等于重复执行同一次输入，直接拒绝
    var success = new SingleMachineExecutionService(new FlakyExecutor(failFirst: 1));
    var successFirst = success.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "flaky" }, "suc-1").GetAwaiter().GetResult();
    var successSecond = success.RetryAsync(session, successFirst.JobId, "suc-2").GetAwaiter().GetResult();
    Expect(successSecond.State == JobState.Succeeded, "第二次尝试应成功");
    ExpectThrows<ProtocolViolationException>(
        () => success.RetryAsync(session, successFirst.JobId, "suc-3").GetAwaiter().GetResult(),
        "JOB_NOT_RETRYABLE");
    Expect(success.GetAttemptChain(successFirst.JobId).Count == 2, "被拒后不得新增尝试");
    Expect(!success.CanRetryJob(successFirst.JobId, session.UserId, max, out var succeeded) && succeeded.Contains("成功"),
        "应说明同根已有成功尝试：" + succeeded);

    // 3) 同根已有进行中的尝试：并发重试会让同一次输入同时跑两份，必须拒绝
    var gate = new GateExecutor();
    var concurrent = new SingleMachineExecutionService(gate);
    var failed = concurrent.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "gate" }, "con-1").GetAwaiter().GetResult();
    Expect(failed.State == JobState.Failed, "首次应失败（模拟器设定）");
    var retryTask = concurrent.RetryAsync(session, failed.JobId, "con-2");
    SpinWait.SpinUntil(() => concurrent.GetJobs(session.UserId).Any(job => job.State == JobState.Running), 3000);
    Expect(concurrent.GetJobs(session.UserId).Any(job => job.State == JobState.Running), "第 2 次尝试应处于进行中");
    ExpectThrows<ProtocolViolationException>(
        () => concurrent.RetryAsync(session, failed.JobId, "con-3").GetAwaiter().GetResult(),
        "JOB_NOT_RETRYABLE");
    Expect(!concurrent.CanRetryJob(failed.JobId, session.UserId, max, out var running) && running.Contains("进行中"),
        "应说明已有进行中的尝试：" + running);
    gate.Release();
    Expect(retryTask.GetAwaiter().GetResult().State == JobState.Succeeded, "放行后第 2 次尝试应成功");
    Expect(concurrent.GetAttemptChain(failed.JobId).Count == 2, "并发重试被拒后只应有 2 次尝试");

    // 4) 重启后规则不变：尝试号与血缘从 SQLite 读回后仍能拦住第 4 次
    var path = Path.Combine(Path.GetTempPath(), $"yeeeyee-retry-cap-{Guid.NewGuid():N}.db");
    try
    {
        Guid rootId;
        using (var store = new SqliteJobStore(path))
        {
            var hosted = new SingleMachineExecutionService(new FailingExecutor(), store);
            var start = hosted.StartAsync(session, new Invocation { InvocationId = Guid.NewGuid(), Tool = "fail" }, "db-1").GetAwaiter().GetResult();
            hosted.RetryAsync(session, start.JobId, "db-2").GetAwaiter().GetResult();
            hosted.RetryAsync(session, start.JobId, "db-3").GetAwaiter().GetResult();
            rootId = start.JobId;
            ExpectThrows<ProtocolViolationException>(
                () => hosted.RetryAsync(session, start.JobId, "db-4").GetAwaiter().GetResult(),
                "JOB_NOT_RETRYABLE");
        }

        using var reopened = new SqliteJobStore(path);
        var restored = new SingleMachineExecutionService(new FailingExecutor(), reopened);
        Expect(restored.GetAttemptChain(rootId).Count == max, "重启后尝试链应保留：" + restored.GetAttemptChain(rootId).Count);
        Expect(restored.GetAttemptChain(rootId).Max(job => job.Attempt) == max, "重启后最大尝试号应保留");
        ExpectThrows<ProtocolViolationException>(
            () => restored.RetryAsync(session, rootId, "db-5").GetAwaiter().GetResult(),
            "JOB_NOT_RETRYABLE");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// 钉住协议字段严格校验：非法 ID 与非整数时间戳都要按格式错误拒绝。
/// </summary>
static void StrictProtocolFields()
{
    var valid = "{\"v\":1,\"id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"canvas/hello\",\"ts\":1,\"payload\":{\"canvasVersion\":\"0.1.0\",\"protocolVersion\":1,\"minHostProtocol\":1,\"features\":[]}}";
    ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(valid.Replace("11111111-1111-4111-8111-111111111111", "bad"), "canvasToHost"), "PROTOCOL_MALFORMED");
    ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(valid.Replace("\"ts\":1", "\"ts\":1.5"), "canvasToHost"), "PROTOCOL_MALFORMED");
}

/// <summary>
/// 资源版本替换协议：**照固定样本验**，不照现场手写的 JSON 验。
///
/// 样本住在 <c>YEEYEEYEE.Canvas/src/shared/protocolFixtures.json</c>——两端共读同一份。
/// 用手写内联 JSON 的时候，「什么算合法」只存在于这一个测试里，另一端的实现没有可照的东西；
/// 而这类协议最容易出的错就是两边各自理解了一遍「必填」。
/// </summary>
static void ResourceReplaceProtocol()
{
    var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "protocolFixtures.json");
    Expect(File.Exists(fixturePath), "找不到协议样本：" + fixturePath);

    using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
    var root = document.RootElement;
    var checkedCases = 0;
    foreach (var section in new[] { "request", "result" })
    {
        var block = root.GetProperty(section);
        var type = block.GetProperty("type").GetString()!;
        var direction = block.GetProperty("direction").GetString()!;
        Expect(YEEYEEYEEProtocol.DirectionOf(type) == direction, $"{type} 的方向与协议表里不一致");

        foreach (var item in block.GetProperty("cases").EnumerateArray())
        {
            checkedCases++;
            var name = item.GetProperty("name").GetString();
            // 样本可以自己改方向（用来验「方向反了就拒」那条）。
            var useDirection = item.TryGetProperty("direction", out var overridden)
                ? overridden.GetString()!
                : direction;
            var message = JsonSerializer.Serialize(new
            {
                v = YEEYEEYEEProtocol.Version,
                id = Guid.NewGuid(),
                type,
                ts = 1L,
                payload = item.GetProperty("payload")
            });

            if (item.GetProperty("ok").GetBoolean())
            {
                using var decoded = YEEYEEYEEProtocol.Decode(message, useDirection);
                Expect(decoded.RootElement.GetProperty("type").GetString() == type, $"样本「{name}」应当验得过");
                continue;
            }

            var code = item.GetProperty("code").GetString()!;
            try { ExpectThrows<ProtocolViolationException>(() => YEEYEEYEEProtocol.Decode(message, useDirection), code); }
            catch (InvalidOperationException error) { throw new InvalidOperationException($"样本「{name}」：" + error.Message); }
        }
    }

    // 扫到 0 条说明样本文件没被读到，这条对拍就成了摆设。
    Expect(checkedCases >= 8, "样本太少了，这条对拍等于没验：" + checkedCases);
}

/// <summary>
/// 协议表只有一份：C# 嵌进去的必须**逐字节等于**源码树里那个 json，
/// 而不是另抄的一张小表。抄一份的话，「某条消息莫名被判为未知类型」这类症状
/// 没人会想到去查协议表——两端各存一份表，先对不上的那次就是这样表现。
/// </summary>
static void ProtocolTableIsSharedWithCanvas()
{
    var tablePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "protocol.json");
    Expect(File.Exists(tablePath), "找不到共享协议表：" + tablePath);
    var onDisk = File.ReadAllBytes(tablePath);

    using var embedded = typeof(YEEYEEYEEProtocol).Assembly.GetManifestResourceStream("protocol.json");
    Expect(embedded is not null, "嵌入式协议表不见了：检查 YEEYEEYEE.Core.csproj 里那条 EmbeddedResource");
    using var buffer = new MemoryStream();
    embedded!.CopyTo(buffer);
    Expect(buffer.ToArray().SequenceEqual(onDisk), "嵌进去的协议表与源码树里那份不一致：是不是有人另抄了一份");

    Expect(YEEYEEYEEProtocol.MessageTypes.Count >= 10, "表里的消息类型少得不像话：" + YEEYEEYEEProtocol.MessageTypes.Count);
    var directions = YEEYEEYEEProtocol.MessageTypes.Select(YEEYEEYEEProtocol.DirectionOf).Distinct().ToList();
    Expect(directions.All(item => item is "canvasToHost" or "hostToCanvas"),
        "方向只该有那两个，实际 " + string.Join("、", directions));
    Expect(directions.Count == 2, "两个方向都该用到");
    Expect(YEEYEEYEEProtocol.RequiredFieldsOf("canvas/resource.replace.request").Count == 4,
        "必填字段读的是表里的那一份");
    Expect(YEEYEEYEEProtocol.DirectionOf("不存在的类型") is null, "表里没有的类型就该是 null");
    Expect(YEEYEEYEEProtocol.RequiredFieldsOf("不存在的类型").Count == 0, "表里没有的类型没有必填字段");
}

/// <summary>
/// 钉住资源版本替换的状态集：可解除锁定、非法版本拒绝、锁定节点不可替换。
/// </summary>
static void ResourceReplaceState()
{
    var entity = new YEEYEEYEE.Desktop.WorkflowEntity { Name = "主角" };
    var variant = entity.CreateVariant("战斗服");
    var version = variant.EnsureInitialVersion();
    var node = new YEEYEEYEE.Desktop.WorkflowNode();
    node.References.Add(new YEEYEEYEE.Desktop.NodeReference
    {
        EntityId = entity.Id,
        VariantId = variant.Id,
        VariantVersionId = version.Id
    });
    var state = new YEEYEEYEE.Desktop.WorkflowCanvasState
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

/// <summary>
/// 钉住媒体版本回滚只作用于草稿：草稿恢复到所选快照，源变体不被改动。
/// </summary>
static void MediaVersionRollbackDraftOnly()
{
    var variant = new WorkflowEntityVariant { Description = "当前", Attachments = [new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://current.png", Name = "current.png" }] };
    var previous = variant.EnsureInitialVersion();
    variant.Description = "修改后";
    variant.Attachments[0].Name = "changed.png";
    variant.Commit("第二版");
    var draft = variant.CloneAsVariant("草稿");
    draft.RollbackTo(previous);
    Expect(draft.Description == "当前" && draft.Attachments.Single().Name == "current.png", "回滚没有恢复所选版本快照");
    Expect(variant.Description == "修改后" && variant.Attachments.Single().Name == "changed.png", "回滚草稿意外改动源变体");
    Expect(draft.HasUncommittedChanges, "回滚草稿应保留未提交状态");
}

/// <summary>
/// 钉住媒体提交的范围语义：仅当前镜头只改这一条引用，全部引用则覆盖该实体变体的所有引用且不误伤非本实体的引用。
/// </summary>
static void MediaCommitReferenceScopes()
{
    var entity = new WorkflowEntity { Name = "主角" };
    var variant = entity.CreateVariant("默认");
    var old = variant.Versions.Single();
    var current = new WorkflowNode();
    var other = new WorkflowNode();
    var unrelated = new WorkflowNode();
    current.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = old.Id });
    other.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    unrelated.References.Add(new NodeReference { EntityId = Guid.NewGuid(), VariantId = variant.Id });
    var draft = variant.CloneAsVariant("草稿");
    draft.Description = "新版本";
    variant.Description = draft.Description;
    var created = variant.Commit("测试");
    current.References[0].VariantVersionId = created.Id;
    Expect(current.References[0].VariantVersionId == created.Id, "当前引用没有切换到新版本");
    Expect(other.References[0].VariantVersionId is null, "仅当前镜头模式修改了其它引用");

    foreach (var reference in new[] { current.References[0], other.References[0], unrelated.References[0] })
        if (reference.EntityId == entity.Id && reference.VariantId == variant.Id) reference.VariantVersionId = created.Id;
    Expect(current.References[0].VariantVersionId == created.Id && other.References[0].VariantVersionId == created.Id, "全部引用模式遗漏匹配引用");
    Expect(unrelated.References[0].VariantVersionId is null, "全部引用模式依赖名称或错误匹配非同 ID 引用");
}

/// <summary>
/// 钉住引用媒体的草稿隔离与定向提交：编辑草稿不污染正式变体与已锁版本，定向提交生成独立变体且不共享附件对象。
/// </summary>
static void ReferenceMediaDraftIsolation()
{
    var entity = new WorkflowEntity { Name = "主角", Core = "核心设定" };
    var variant = entity.CreateVariant("默认");
    variant.Description = "共享表现";
    variant.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://shared.png", Name = "shared.png" });
    variant.Commit("共享初始");

    var followNode = new WorkflowNode { Title = "跟随镜头" };
    followNode.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    var lockedVersion = variant.Versions.OrderByDescending(version => version.Number).First();
    var lockedNode = new WorkflowNode { Title = "锁定镜头" };
    lockedNode.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = lockedVersion.Id });
    var state = new WorkflowCanvasState { Entities = [entity], Nodes = [followNode, lockedNode] };

    var effective = state.ResolveReferences(followNode).Single();
    var draft = variant.CloneAsVariant("草稿");
    draft.Description = "只对当前镜头的新表现";
    draft.Attachments[0].Name = "draft.png";
    Expect(variant.Description == "共享表现" && variant.Attachments[0].Name == "shared.png", "编辑草稿不应污染正式变体");
    Expect(lockedVersion.Description == "共享表现" && lockedVersion.Attachments[0].Name == "shared.png", "编辑草稿不应污染已锁定版本");

    var isolated = entity.CreateIsolatedVariant(effective, "当前镜头变体", draft.Description, draft.Attachments);
    var isolatedVersion = isolated.Versions.Single();
    followNode.References[0].VariantId = isolated.Id;
    followNode.References[0].VariantVersionId = isolatedVersion.Id;

    Expect(state.ResolveReferences(followNode).Single().Description == "只对当前镜头的新表现", "当前镜头应读取新变体");
    Expect(state.ResolveReferences(lockedNode).Single().Description == "共享表现", "其他锁定引用不应改变");
    Expect(state.ResolveReferences(lockedNode).Single().Attachments.Single().Name == "shared.png", "其他锁定引用媒体不应改变");
    Expect(state.ResolveReferences(followNode).Single().Variant.Id != variant.Id, "定向提交应创建独立变体");
    draft.Attachments[0].Name = "edited-after-submit.png";
    Expect(isolated.Attachments[0].Name == "draft.png", "提交后的变体不应与草稿共享附件对象");
}

/// <summary>
/// 钉住子引用的版本快照与隔离复制：初始版本保存子引用，新版本不继承已移除的，回滚能恢复且快照与克隆不共享对象。
/// </summary>
static void NestedReferenceSnapshots()
{
    var costume = new WorkflowEntity { Name = "战斗服", Kind = EntityKind.Prop };
    var costumeVariant = costume.CreateVariant("黑色");
    var character = new WorkflowEntity { Name = "主角" };
    var characterVariant = new WorkflowEntityVariant { Name = "战斗状态" };
    character.Variants.Add(characterVariant);
    characterVariant.References.Add(new NodeReference
    {
        EntityId = costume.Id,
        VariantId = costumeVariant.Id,
        VariantVersionId = costumeVariant.Versions.Single().Id
    });
    var first = characterVariant.EnsureInitialVersion();
    characterVariant.References.Clear();
    var second = characterVariant.Commit("移除服装引用");
    var clone = characterVariant.CloneAsVariant("镜头隔离");
    clone.RollbackTo(first);

    Expect(first.References.Count == 1, "初始版本未保存子引用");
    Expect(second.References.Count == 0, "新版本错误继承已移除的子引用");
    Expect(clone.References.Count == 1, "回滚未恢复历史子引用");
    clone.References[0].VariantId = Guid.NewGuid();
    Expect(first.References[0].VariantId == costumeVariant.Id, "版本快照与克隆仍共享子引用对象");

    var content = new ReferenceContent(character, characterVariant, first, first.Description, first.Layout, first.Attachments)
    {
        References = first.References
    };
    var isolated = character.CreateIsolatedVariant(content, "单镜头", "隔离", Array.Empty<WorkflowAttachment>());
    Expect(isolated.References.Count == 1, "隔离变体未复制子引用");
}

/// <summary>
/// 钉住 AI 导入时自动建立角色到技能与道具的子引用，缺来源实体的条目不生成无效引用。
/// </summary>
static void AgentCharacterNestedReferences()
{
    var canvas = new WorkflowCanvasState();
    var character = new WorkflowEntity { Name = "林晚", Kind = EntityKind.Character };
    var skill = new WorkflowEntity { Name = "听声辨位", Kind = EntityKind.Prop };
    var prop = new WorkflowEntity { Name = "旧录音笔", Kind = EntityKind.Prop };
    character.CreateVariant("默认");
    skill.CreateVariant("默认");
    prop.CreateVariant("默认");
    canvas.Entities.AddRange([character, skill, prop]);

    var characterAction = new AgentAction
    {
        Kind = "create_work_item",
        Title = "林晚",
        WorkTreeKind = "Character",
        EntityTarget = "林晚"
    };
    var skillAction = new AgentAction
    {
        Kind = "create_work_item",
        Title = "听声辨位",
        WorkTreeKind = "Ability",
        ParentTarget = "林晚",
        EntityTarget = "听声辨位"
    };
    var propAction = new AgentAction
    {
        Kind = "create_work_item",
        Title = "旧录音笔",
        WorkTreeKind = "Prop",
        ParentTarget = "林晚",
        EntityTarget = "旧录音笔"
    };

    var result = AgentActionExecutor.Apply(
        [characterAction, skillAction, propAction, propAction],
        canvas,
        workspaceDirectory: null,
        fileMode: FileWriteMode.Skip);

    Expect(result.Applied == 4, "AI 工作树动作未全部应用");
    var characterItem = canvas.WorkTree.Single(item => item.Kind == WorkTreeKind.Character);
    var characterVariant = character.Variants.Single();
    Expect(characterItem.SourceEntityId == character.Id, "角色来源实体未建立");
    Expect(characterVariant.References.Count == 2, "角色未自动获得技能和道具子引用");
    Expect(characterVariant.References.Count(reference => reference.EntityId == skill.Id) == 1, "技能引用重复或缺失");
    Expect(characterVariant.References.Count(reference => reference.EntityId == prop.Id) == 1, "道具引用重复或缺失");

    var missingSource = new AgentAction
    {
        Kind = "create_work_item",
        Title = "未绑定能力",
        WorkTreeKind = "Ability",
        ParentTarget = "林晚"
    };
    AgentActionExecutor.Apply([missingSource], canvas, workspaceDirectory: null, fileMode: FileWriteMode.Skip);
    Expect(characterVariant.References.Count == 2, "缺少来源实体的工作树项不应生成无效引用");
}

/// <summary>
/// 钉住 Job 能落盘并在重启后恢复，未完成的 Job 在宿主重启后标记为失败。
/// </summary>
static void SqliteJobPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"yeeeyee-{Guid.NewGuid():N}.db");
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

/// <summary>
/// 钉住任务快照里的工具、能力、通道与输入参数都能持久化并读回。
/// </summary>
static void JobSnapshotPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"yeeeyee-{Guid.NewGuid():N}.db");
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

/// <summary>
/// 钉住外部任务 ID 能落盘恢复，且一个任务只能挂一次外部 ID。
/// </summary>
static void ExternalTaskIdPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), $"yeeeyee-external-{Guid.NewGuid():N}.db");
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

/// <summary>
/// 钉住外部回调的状态映射：进度单调、成功落终态、重复回调不报错、别的用户匹配不上。
/// </summary>
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

/// <summary>
/// 钉住轮询器能把外部任务推进到成功终态，并在结束后不泄漏资源。
/// </summary>
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

/// <summary>
/// 钉住 WebSocket 进度能写进 Job，且轮询器停止时监听任务被释放。
/// </summary>
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

/// <summary>
/// 钉住 ComfyUI 的工作流映射、队列状态、取消与文件下载整条链路。
/// </summary>
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

    var directory = Path.Combine(Path.GetTempPath(), "yeeeyee-assets-" + Guid.NewGuid().ToString("N"));
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

/// <summary>
/// 钉住 HTTP 回调的签名校验与防重放：重放、坏签名与过期时间戳都被拒绝，合法签名能完成 Job。
/// </summary>
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

/// <summary>
/// 断言为真，否则抛出带消息的异常；测试里统一走它，省去各处手写判断。
/// </summary>
static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
/// <summary>
/// 断言这段调用会抛出指定异常，并可选地核对协议或引用类异常的错误码。
/// </summary>
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

/// <summary>前 N 次尝试失败、之后成功的执行器，用于验证重试链路。</summary>
sealed class FlakyExecutor : IInvocationExecutor
{
    private readonly int failFirst;
    private int attempts;

    public FlakyExecutor(int failFirst) => this.failFirst = failFirst;

    public int Attempts => Volatile.Read(ref attempts);

    public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref attempts);
        if (current <= failFirst) throw new InvalidOperationException($"模拟第 {current} 次尝试失败");
        return Task.FromResult(new ExecutionOutput
        {
            ExternalTaskId = $"local-{job.JobId:N}",
            Outputs = [new AssetRef { Role = "output", Ref = $"local://jobs/{job.JobId:N}/{invocation.Tool}" }]
        });
    }
}

/// <summary>
/// 第一次调用直接失败、之后挂住直到放行的执行器：用来制造「同根任务已有进行中的尝试」这一状态，
/// 验证并发重试会被拒（返工 R1）。挂住而不是睡死，是为了让测试能在放行后立刻收敛。
/// </summary>
sealed class GateExecutor : IInvocationExecutor
{
    private readonly TaskCompletionSource<object?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;

    public int Calls => Volatile.Read(ref calls);

    /// <summary>放行被挂住的调用。</summary>
    public void Release() => gate.TrySetResult(null);

    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref calls);
        if (current == 1) throw new InvalidOperationException("模拟首次尝试失败");
        await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutionOutput
        {
            ExternalTaskId = $"gate-{job.JobId:N}",
            Outputs = [new AssetRef { Role = "output", Ref = $"local://jobs/{job.JobId:N}/{invocation.Tool}" }]
        };
    }
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
