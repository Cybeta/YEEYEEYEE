using System.Text.Json;
using DreamForge.Core;
using DreamForge.Desktop;
using DreamForge.Host;

var checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception($"断言 #{checks}: {message}"); }
var root = Path.Combine(Path.GetTempPath(), "dreamforge-g6v1-" + Guid.NewGuid().ToString("N"));
var prior = new[] { "DREAMFORGE_CANVAS_DIR", "DREAMFORGE_ASSET_DIR", "DREAMFORGE_CONFIG" }
    .ToDictionary(key => key, Environment.GetEnvironmentVariable);
try
{
    Directory.CreateDirectory(root);
    var project = ProjectContext.Create(root, "isolated");
    AppPaths.UseProject(project);
    var canvases = Path.Combine(project.RootPath, "canvases");
    var assets = Path.Combine(project.RootPath, "assets");
    Directory.CreateDirectory(canvases);
    Directory.CreateDirectory(assets);
    Environment.SetEnvironmentVariable("DREAMFORGE_CANVAS_DIR", canvases);
    Environment.SetEnvironmentVariable("DREAMFORGE_ASSET_DIR", assets);
    Environment.SetEnvironmentVariable("DREAMFORGE_CONFIG", Path.Combine(project.RootPath, "empty-config.json"));
    var first = new WorkflowCanvasState();
    var applied = AgentActionExecutor.Apply(new[] {
        new AgentAction { Kind = "create_entity", Title = "同名角色", EntityKind = "角色", Content = "A-核心" },
        new AgentAction { Kind = "create_node", Title = "跟随镜头", Content = "拍摄", NodeCategory = "分镜", EntityTargets = new[] { "同名角色" } },
        new AgentAction { Kind = "create_node", Title = "锁定镜头", Content = "拍摄", NodeCategory = "分镜", EntityTarget = "同名角色", VariantVersion = "v1" }
    }, first, null);
    Check(applied.Applied == 3 && applied.Errors.Count == 0, "真实 Agent 动作入口应创建实体及两条引用");
    var a = first.Entities.Single();
    var variant = a.Variants.Single();
    var v1 = variant.Versions.Single().Id;
    variant.Description = "A-旧表现";
    var v2 = variant.Commit("旧表现").Id;
    var follow = first.Nodes.Single(node => node.Title == "跟随镜头");
    var locked = first.Nodes.Single(node => node.Title == "锁定镜头");
    Check(follow.References.Single().EntityId == a.Id && follow.References.Single().VariantId == variant.Id && follow.References.Single().VariantVersionId is null, "跟随引用稳定 ID");
    Check(locked.References.Single().EntityId == a.Id && locked.References.Single().VariantVersionId == v1, "锁定引用版本 ID");
    var other = new WorkflowCanvasState();
    var b = new WorkflowEntity { Name = a.Name, Core = "B-核心" };
    var bVariant = b.CreateVariant("默认");
    other.Entities.Add(b);
    var foreign = new WorkflowNode { Title = "异名画布镜头" };
    foreign.References.Add(new NodeReference { EntityId = a.Id, VariantId = variant.Id });
    other.Nodes.Add(foreign);
    var otherPath = Path.Combine(canvases, "other.json");
    var firstPath = Path.Combine(canvases, "first.json");
    RecentCanvasState Wrap(string name, WorkflowCanvasState canvas) => new(name, 0, "", "", 512, 512, 20, 7, "", canvas);
    CanvasSaveService.Save(Wrap("other", other), otherPath);
    CanvasSaveService.Save(Wrap("first", first), firstPath);

    var session = new SessionContext { SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"]) };
    var executor = new CountingExecutor(assets);
    var db = Path.Combine(project.RootPath, "jobs.db");
    Guid historyId;
    string oldPrompt;
    using (var store = new SqliteJobStore(db))
    {
        var execution = new SingleMachineExecutionService(executor, store);
        var provider = new ComfyUiImageProvider(execution, new AiProviderConfig { ComfyUiBaseUrl = "http://127.0.0.1:1", ComfyUiCheckpoint = "offline-only" }, session);
        oldPrompt = first.DescribeReferenceForPrompt(follow);
        Check(oldPrompt.Contains("A-核心") && oldPrompt.Contains("A-旧表现"), "出图入口采用实际引用提示词");
        var result = await provider.GenerateAsync(new ImageGenerationRequest { Prompt = oldPrompt });
        Check(result.Status == ImageGenerationStatus.Failed && executor.Calls == 1, "真实 ComfyUI 提交入口经过离线执行桩且第一次失败");
        historyId = execution.GetJobs(session.UserId).Single().JobId;
        Check(store.LoadAll().Single(job => job.JobId == historyId).State == JobState.Failed, "失败历史任务真实持久化");
    }
    var preview = ProjectEntityMigration.Preview(first, firstPath);
    Check(preview.Migrated == 2 && preview.Conflicts.Count > 0, "迁移预览同名不同 ID 分开报告");
    var migration = ProjectEntityMigration.Apply(first, firstPath);
    Check(migration.Succeeded && migration.Migrated == 2, "真实迁移 Apply 成功：" + migration.Message);
    Check(ProjectLibrary.Load().Entities.Select(e => e.Id).ToHashSet().SetEquals(new[] { a.Id, b.Id }), "项目库同名异 ID 不合并");
    Check(ProjectEntityMigration.Preview(first, firstPath).Migrated == 0, "迁移幂等");
    Check(ProjectLibrary.Find(a.Id)!.Variants.Single().FindVersion(v1) is not null, "原锁定版本仍在项目库");
    var published = ProjectLibrary.Find(a.Id)!;
    published.Core = "A-共享更新";
    published.Variants.Single().Description = "A-新表现";
    var v3 = published.Variants.Single().Commit("共享版本").Id;
    Check(ProjectEntityScope.Publish(published).Persisted, "共享实体和新版本发布到项目库");
    CanvasSaveService.Save(Wrap("first", first), firstPath);
    Check(CanvasOpenService.TryOpen(firstPath, out var reopened, out var openError), "真实画布打开：" + openError);
    var live = reopened.State.Canvas;
    ProjectEntityScope.MergeInto(live);
    var liveFollow = live.Nodes.Single(n => n.Id == follow.Id);
    var liveLocked = live.Nodes.Single(n => n.Id == locked.Id);
    Check(live.FindEntity(a.Id)!.Core == "A-共享更新" && live.FindVariant(variant.Id)!.FindVersion(v3) is not null, "保存重开刷新共享权威内容及版本");
    Check(liveFollow.References.Single().EntityId == a.Id && liveLocked.References.Single().VariantVersionId == v1, "保存重开稳定 ID 和锁定版本不漂移");
    Check(live.ResolveReferences(liveFollow).Single().Description == "A-新表现", "跟随读取最新内容");
    Check(live.ResolveReferences(liveLocked).Single().Description != "A-新表现", "锁定读取旧版快照");
    Check(live.DescribeReferenceForPrompt(liveFollow).Contains("A-共享更新") && !live.DescribeReferenceForPrompt(liveFollow).Contains("B-核心"), "同名异 ID 不误绑定");
    var scan = ProjectEntityIndex.Scan(live, firstPath);
    Check(scan.Hits.Count(hit => hit.EntityId == a.Id) == 3 && scan.Hits.All(hit => hit.EntityId != b.Id), "跨画布扫描按 ID 命中三个引用");
    Check(ProjectEntityDeletion.Check(scan, a.Id).Blocked && !ProjectEntityDeletion.Check(scan, b.Id).Blocked, "异画布引用硬阻断 A 删除、不误阻断 B");
    Check(CanvasDeletionGuard.CheckEntity(scan, live.FindEntity(a.Id)!).HardBlocked, "画布删除保护阻断跨来源引用");
    Check(CanvasOpenService.TryOpen(otherPath, out var foreignOpen, out var foreignError), "跨画布重开：" + foreignError);
    ProjectEntityScope.MergeInto(foreignOpen.State.Canvas);
    Check(foreignOpen.State.Canvas.FindEntity(a.Id)?.Core == "A-共享更新" &&
        foreignOpen.State.Canvas.FindEntity(b.Id)?.Core == "B-核心", "另一画布从共享库注入 A，且同名 B 仍保持独立");
    using (var reopenedStore = new SqliteJobStore(db))
    {
        var restored = new SingleMachineExecutionService(executor, reopenedStore);
        Check(restored.TryGet(historyId, out var historical) && historical!.State == JobState.Failed, "重启执行宿主恢复历史失败任务");
        Check(restored.CanRetryJob(historyId, session.UserId, 3, out _), "历史任务允许重试");
        var retried = await restored.RetryAsync(session, historyId, "g6-v1-retry-" + Guid.NewGuid().ToString("N"));
        Check(retried.State == JobState.Succeeded && executor.Calls == 2, "历史任务真实重试到离线提供方且成功");
        Check(retried.Attempt == 2 && retried.RetryOfJobId == historyId && retried.RootJobId == historyId, "重试尝试号与血缘");
        Check(restored.GetAttemptChain(historyId).Count == 2 && reopenedStore.LoadAll().Count == 2, "尝试链持久化两个任务");
        Check(executor.Prompts[1] == oldPrompt && !executor.Prompts[1].Contains("A-共享更新"), "历史重试沿用原任务输入，不伪称重新解析最新共享实体");
        Check(!restored.CanRetryJob(historyId, session.UserId, 3, out _), "已有成功尝试阻止重复历史重试");
    }
    var countBefore = executor.Calls;
    var authoritative = ProjectLibrary.Load();
    authoritative.Entities.RemoveAll(e => e.Id == a.Id);
    Check(ProjectLibrary.TrySave(authoritative, out var libraryError), "在隔离项目模拟权威资源缺失：" + libraryError);
    Check(ProjectEntityScope.RefreshAuthority(live).Count == 1, "打开后缺失权威现场复核");
    Check(CanvasReferenceVersions.IsNodeBlocked(live, liveFollow) && CanvasReferenceVersions.DescribeBlock(live, liveFollow).Contains("项目库"), "缺失项目资源阻断引用");
    Check(live.ResolveReferences(liveFollow).Count == 0 && executor.Calls == countBefore, "缺失资源不解析、不触及离线提供方");

    // G6-W3：只保留真实生成历史/任务输入快照时，不把 prompt、名称或历史文本反推成稳定资源引用。
    // 另建隔离项目，避免上方画布库的三条真实节点引用污染此边界测试。
    var snapshotProject = ProjectContext.Create(root, "snapshot-only");
    AppPaths.UseProject(snapshotProject);
    var snapshotCanvases = Path.Combine(snapshotProject.RootPath, "canvases");
    Environment.SetEnvironmentVariable("DREAMFORGE_CANVAS_DIR", snapshotCanvases);
    var snapshot = new WorkflowCanvasState();
    var snapshotEntity = new WorkflowEntity { Name = "同名角色" };
    var snapshotVariant = snapshotEntity.CreateVariant("默认");
    snapshot.Entities.Add(snapshotEntity);
    var snapshotNode = new WorkflowNode { Title = "只留历史" };
    snapshotNode.GenerationHistory.Add(new GenerationHistory { Input = oldPrompt + snapshotEntity.Id + snapshotVariant.Id + snapshotVariant.Versions[0].Id,
        Instruction = "旧输入", Output = "独立输出", Status = NodeExecutionStatus.Completed });
    snapshot.Nodes.Add(snapshotNode);
    var snapshotPath = Path.Combine(snapshotCanvases, "snapshot.json");
    CanvasSaveService.Save(Wrap("仅历史", snapshot), snapshotPath);
    Check(CanvasOpenService.TryOpen(snapshotPath, out var snapshotOpen, out var snapshotError), "快照画布落盘重开：" + snapshotError);
    Check(snapshotOpen.State.Canvas.Nodes.Single().GenerationHistory.Single().Input.Contains(snapshotEntity.Id.ToString()), "生成历史文本确实保留稳定 ID 字符串但没有引用字段");
    using (var snapshotStore = new SqliteJobStore(Path.Combine(snapshotProject.RootPath, "jobs.db")))
    {
        snapshotStore.Initialize();
        snapshotStore.Save(new ExecutionResult { JobId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), UserId = session.UserId,
            IdempotencyKey = "snapshot-only", State = JobState.Failed, Tool = "text-to-image", Channel = "comfyui",
            Inputs = new Dictionary<string, JsonElement> { ["prompt"] = JsonSerializer.SerializeToElement(snapshotNode.GenerationHistory.Single().Input) } });
        var stored = snapshotStore.LoadAll().Single();
        Check(stored.Inputs["prompt"].GetString() == snapshotNode.GenerationHistory.Single().Input, "SQLite 输入快照读回原始 prompt");
        var snapshotScan = ProjectEntityIndex.Scan(snapshotOpen.State.Canvas, snapshotPath);
        Check(snapshotScan.ScannedCanvases == 1 && snapshotScan.Hits.Count == 0 && snapshotScan.Skipped.Count == 0,
            "仅历史/任务输入快照不产生节点结构化引用命中");
        Check(!ProjectEntityDeletion.Check(snapshotScan, snapshotEntity.Id).Blocked &&
            !CanvasDeletionGuard.CheckVariant(snapshotScan, snapshotEntity, snapshotVariant).HardBlocked,
            "快照文本不误阻断实体或变体删除");
        using var jobJson = JsonDocument.Parse(JsonSerializer.Serialize(stored));
        using var historyJson = JsonDocument.Parse(JsonSerializer.Serialize(snapshotOpen.State.Canvas.Nodes.Single().GenerationHistory.Single()));
        Check(!jobJson.RootElement.TryGetProperty("EntityId", out _) && !jobJson.RootElement.TryGetProperty("VariantId", out _) &&
            !jobJson.RootElement.TryGetProperty("VariantVersionId", out _) &&
            !historyJson.RootElement.TryGetProperty("EntityId", out _) && !historyJson.RootElement.TryGetProperty("VariantId", out _) &&
            !historyJson.RootElement.TryGetProperty("VariantVersionId", out _),
            "任务与生成历史实际序列化形状没有实体/变体/版本引用字段");
    }
    Console.WriteLine($"PASS G6-V1 checks={checks} providerCalls={executor.Calls} jobs=2 hits=3; snapshotOnlyHits=0 isolated=true");
    Console.WriteLine("CHAIN AgentActionExecutor.Apply -> ComfyUiImageProvider.GenerateAsync -> SingleMachineExecutionService.StartAsync -> SQLite(failed) -> ProjectEntityMigration.Apply -> ProjectEntityScope.Publish -> CanvasSaveService.Save -> CanvasOpenService.TryOpen -> MergeInto -> SQLite restore -> RetryAsync -> CountingExecutor; missing authority -> RefreshAuthority -> DescribeBlock (no provider call); snapshot Save/Open + SQLite Save/LoadAll -> ProjectEntityIndex.Scan -> deletion checks");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL checks={checks}: {error}");
    Environment.ExitCode = 1;
}
finally
{
    foreach (var (key, value) in prior) Environment.SetEnvironmentVariable(key, value);
    try { Directory.Delete(root, true); } catch (IOException) { }
}

sealed class CountingExecutor(string directory) : IInvocationExecutor
{
    public int Calls { get; private set; }
    public List<string> Prompts { get; } = new();
    public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken token)
    {
        Calls++;
        Prompts.Add(invocation.Inputs["prompt"].GetString() ?? string.Empty);
        if (Calls == 1) throw new IOException("offline first attempt failed");
        var path = Path.Combine(directory, $"{job.JobId:N}.png");
        File.WriteAllBytes(path, [137, 80, 78, 71]);
        return Task.FromResult(new ExecutionOutput { Outputs = [new AssetRef { Role = "output", Ref = AssetStore.ToReference(path) }] });
    }
}
