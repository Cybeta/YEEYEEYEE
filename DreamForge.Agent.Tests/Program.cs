using System.Net;
using System.Text;
using System.Text.Json;
using DreamForge.Desktop;

// Agent 操作层的离线校验：提议解析、操作执行、审批预检、画布虚影预览、附件加载与多模态报文。
//
// 为什么单独建一个测试工程：这些类型都在 DreamForge.Desktop（WinForms，net10.0-windows）里，
// 而 DreamForge.Core.Tests 是 net10.0，引用不到它。没有这个工程，Agent 操作层就没有任何回归保护。
// 测试全部离线，不访问网络、不需要模型——报文形状用桩 HttpClient 拦下来检查。
var tests = new (string Name, Action Run)[]
{
    ("提议解析：剥离操作块并读出 source", ParseReplyAndSource),
    ("同一批里建节点后用标题建立连线", CreateNodesThenConnectInOneBatch),
    ("拒绝重复连线与自环", RejectDuplicateAndSelfLoop),
    ("预检提示找不到的连线端点", PrecheckMissingEndpoint),
    ("整批预检能看见同批新建的节点", BatchPrecheckSeesEarlierNodes),
    ("delete_edge 只删指定方向", DeleteEdgeTargetsDirection),
    ("删除不存在的连线会失败而不是静默成功", DeleteMissingEdgeFails),
    ("删节点会连带删掉相连的边", DeleteNodeRemovesEdges),
    ("锁定节点拒改拒删且预检提前告知", LockedNodeIsProtected),
    ("版本创建同步工作树并锁定节点引用", EntityVersionCreatesWorkTreeAndNodeReference),
    ("更新节点可切换历史版本或跟随当前版本", UpdateNodeChangesVersionReference),
    ("版本决策序列化后仍保留", VersionDecisionRoundTrips),
    ("保留历史版本显示历史保留", KeepHistoricalVersionShowsStatus),
    ("采用有效版本显示已采纳", AdoptedVersionShowsStatus),
    ("新有效版本出现后重新需要确认", NewEffectiveVersionReopensConfirmation),
    ("画布预览不修改真实画布", PreviewDoesNotMutateCanvas),
    ("预览投影新增节点与新增连线", PreviewReportsAdditions),
    ("预览对无差异的操作不报假差异", PreviewIgnoresNoOp),
    ("预览报告将被删除的连线", PreviewReportsRemovedEdge),
    ("预览模式不写文件，提交时才落盘", FileWritePreviewVersusCommit),
    ("写文件路径越界被拒绝", FileWriteEscapeRejected),
    ("附件：图片转成 data URL 且能还原原始字节", AttachmentImageRoundTrip),
    ("附件：超过上限的图片被拒绝", AttachmentImageTooLarge),
    ("附件：文本文件读取与超长截断", AttachmentTextTruncation),
    ("附件：二进制文件被如实拒绝", AttachmentBinaryRejected),
    ("附件：文本拼进正文、图片单独收集", ComposeContentAndCollectImages),
    ("报文：OpenAI 格式携带图片块", OpenAiPayloadCarriesImage),
    ("报文：Anthropic 格式携带图片块且 system 分离", AnthropicPayloadCarriesImage),
    ("报文：system 消息绝不带图片", SystemMessageStaysTextOnly),
    ("报文：未开启图片输入时给出可读报错", ImageWithoutVisionFails),
    ("密钥保护：加密往返与脱敏描述", SecretProtectorRoundTrip),
    ("密钥落盘是密文，读回是明文", ConfigFileStoresCiphertext),
    ("损坏的密文降级为空密钥并标记原因", BrokenCiphertextIsReported),
    ("旧配置的明文密钥在读取时就地加密", PlaintextSecretMigratesOnLoad),
    ("流式：OpenAI 格式逐个增量回调，思考不混入正文", OpenAiStreamParsesDeltas),
    ("流式：Anthropic 格式逐个增量回调", AnthropicStreamParsesDeltas),
    ("流式：一行正文都没有时报错而不是空回复", EmptyStreamIsReportedAsError),
    ("流式：只收到思考没有正文也算失败", ThinkingOnlyIsStillAnError),
    ("流式显示：操作块与围栏不进对话区", ActionsBlockIsHiddenFromDisplay),
    ("反问：解析出问题与候选选项", AskIsParsedWithOptions),
    ("反问：与 actions 共存互不干扰", AskCoexistsWithActions),
    ("反问：只有 ask 没有 actions 也算有效回复", AskAloneIsValid),
    ("反问：ask 块同样不进对话区", AskBlockIsHiddenFromDisplay),
    ("协议块：内容里的裸引号仍能解析（真实样本）", BrokenQuotesSampleStillParses),
    ("协议块：格式损坏时不泄漏 JSON", BrokenProtocolIsHiddenFromDisplay),
    ("反问：未闭合 fenced JSON 不泄漏且流式隐藏", IncompleteAskProtocolIsHidden),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures.Add($"FAIL {test.Name}: {ex.Message}"); Console.WriteLine(failures[^1]); }
}

if (failures.Count > 0) Environment.ExitCode = 1;
else Console.WriteLine($"全部 {tests.Length} 项 Agent 测试通过。");

/// <summary>模型按协议给出的回复：自然语言 + 末尾的操作块。</summary>
static string SampleReply() => """
我建议这样组织第一章的流程：先放剧情节点，再挂分镜，然后把分镜连到剧情下面。

```json
{"actions":[
  {"kind":"create_node","title":"第一章 剧情","content":"少年入宗门","reason":"起点"},
  {"kind":"create_node","title":"第一章 分镜","content":"三个镜头","reason":"下游"},
  {"kind":"create_edge","source":"第一章 剧情","target":"第一章 分镜","reason":"分镜属于剧情"}
]}
```
""";

static void ParseReplyAndSource()
{
    var reply = AgentActionParser.Parse(SampleReply());
    Expect(reply.Actions.Count == 3, $"应解析出 3 条操作，实际 {reply.Actions.Count}");
    // 操作块必须从展示文本里剥离，否则用户会在对话区看到一大段 JSON。
    Expect(!reply.Text.Contains("create_node", StringComparison.Ordinal) && !reply.Text.Contains("actions", StringComparison.Ordinal),
        $"展示文本里残留了操作块：{reply.Text}");
    Expect(reply.Actions[2].Source == "第一章 剧情" && reply.Actions[2].Target == "第一章 分镜",
        "create_edge 的 source/target 没有被正确读出");
}

static void CreateNodesThenConnectInOneBatch()
{
    // 这是连线可用性的关键：执行器按顺序作用在同一份画布上，
    // 所以同批新建的节点能立刻用标题被后面的连线引用，不必让模型等下一轮拿短 id。
    var canvas = new WorkflowCanvasState();
    var result = AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);
    Expect(result.Applied == 3 && result.Errors.Count == 0, $"应全部成功，实际 {result.Applied} 条成功：{string.Join("；", result.Errors)}");
    Expect(canvas.Edges.Count == 1, $"应产生 1 条连线，实际 {canvas.Edges.Count}");
    Expect(canvas.Nodes.First(node => node.Title == "第一章 剧情").Id == canvas.Edges[0].SourceNodeId
        && canvas.Nodes.First(node => node.Title == "第一章 分镜").Id == canvas.Edges[0].TargetNodeId,
        "连线方向与 source/target 不一致");
}

static void RejectDuplicateAndSelfLoop()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);

    var duplicate = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "create_edge", Source = "第一章 剧情", Target = "第一章 分镜" } }, canvas, null);
    Expect(duplicate.Applied == 0 && canvas.Edges.Count == 1, "重复连线没有被拒绝");

    var selfLoop = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "create_edge", Source = "第一章 剧情", Target = "第一章 剧情" } }, canvas, null);
    Expect(selfLoop.Applied == 0 && canvas.Edges.Count == 1, "起点与终点相同的连线没有被拒绝");
}

static void PrecheckMissingEndpoint()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章 剧情" });
    var hint = AgentActionExecutor.Precheck(
        new AgentAction { Kind = "create_edge", Source = "不存在的节点", Target = "第一章 剧情" }, canvas, null);
    Expect(hint == "找不到起点节点", $"预检提示不符：{hint}");
}

static void BatchPrecheckSeesEarlierNodes()
{
    // 这条测试来自一次真实模型的端到端实测：模型一次提议「建 3 个分镜 + 连 3 条线」，
    // 逐条预检把其中 3 条连线全报成「找不到端点」，而实际提交 6 条全部成功。
    // 用户看到那串 ⚠ 会以为坏了，把正确的操作取消勾选。
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章 剧情" });
    var actions = new[]
    {
        new AgentAction { Kind = "create_node", Title = "分镜 A" },
        new AgentAction { Kind = "create_edge", Source = "第一章 剧情", Target = "分镜 A" }
    };

    var single = AgentActionExecutor.Precheck(actions[1], canvas, null);
    Expect(single == "找不到终点节点", $"逐条预检本来就该误报（记录旧行为），实际：{single}");

    var hints = AgentActionExecutor.PrecheckBatch(actions, canvas, null);
    Expect(hints.Count == 2 && hints.All(hint => hint is null),
        $"整批预检不应误报，实际：{string.Join(" / ", hints.Select(hint => hint ?? "无"))}");
    Expect(canvas.Nodes.Count == 1, $"整批预检改动了真实画布：{canvas.Nodes.Count} 个节点");

    // 真实警告不能被整批预检「洗掉」。
    var locked = new WorkflowCanvasState();
    locked.Nodes.Add(new WorkflowNode { Title = "定稿章节", IsLocked = true });
    var lockedHints = AgentActionExecutor.PrecheckBatch(
        new[] { new AgentAction { Kind = "update_node", Target = "定稿章节" } }, locked, null);
    Expect(lockedHints[0] == "节点已锁定，应用会被拒绝", $"整批预检漏掉了真实警告：{lockedHints[0]}");
}

static void DeleteEdgeTargetsDirection()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "第一章 剧情" };
    var second = new WorkflowNode { Title = "第一章 分镜" };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);

    AgentActionExecutor.Apply(new[] { new AgentAction { Kind = "create_edge", Source = first.Title, Target = second.Title } }, canvas, null);
    AgentActionExecutor.Apply(new[] { new AgentAction { Kind = "create_edge", Source = second.Title, Target = first.Title } }, canvas, null);
    Expect(canvas.Edges.Count == 2, $"反向连线也应被允许（不做拓扑校验），实际 {canvas.Edges.Count}");

    var removed = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_edge", Source = second.Title, Target = first.Title } }, canvas, null);
    Expect(removed.Applied == 1 && canvas.Edges.Count == 1, $"应只删掉一条，实际剩 {canvas.Edges.Count}");
    Expect(canvas.Edges[0].SourceNodeId == first.Id && canvas.Edges[0].TargetNodeId == second.Id,
        "删错了方向：应该保留 剧情→分镜");
}

static void DeleteMissingEdgeFails()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "A" });
    canvas.Nodes.Add(new WorkflowNode { Title = "B" });
    var result = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_edge", Source = "A", Target = "B" } }, canvas, null);
    // 静默成功最危险：用户会以为断开了，其实什么都没发生。
    Expect(result.Applied == 0 && result.Errors.Count == 1, "删除不存在的连线应报错");
}

static void DeleteNodeRemovesEdges()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);
    var result = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_node", Target = "第一章 剧情" } }, canvas, null);
    Expect(result.Applied == 1 && canvas.Nodes.Count == 1 && canvas.Edges.Count == 0,
        $"删节点应连带删边，实际剩 {canvas.Nodes.Count} 个节点 / {canvas.Edges.Count} 条边");
}

static void LockedNodeIsProtected()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "定稿章节", IsLocked = true });

    var result = AgentActionExecutor.Apply(new[]
    {
        new AgentAction { Kind = "update_node", Target = "定稿章节", Content = "偷偷改" },
        new AgentAction { Kind = "delete_node", Target = "定稿章节" }
    }, canvas, null);

    // 锁定是数据层强制，不是界面提示——Agent 不能绕过审批之外的保护。
    Expect(result.Applied == 0 && canvas.Nodes.Count == 1 && canvas.Nodes[0].Content.Length == 0,
        $"锁定节点被改动了：{string.Join("；", result.Errors)}");
    Expect(AgentActionExecutor.Precheck(new AgentAction { Kind = "update_node", Target = "定稿章节" }, canvas, null) == "节点已锁定，应用会被拒绝",
        "预检没有提前告知锁定会被拒绝");
}

static void EntityVersionCreatesWorkTreeAndNodeReference()
{
    var canvas = new WorkflowCanvasState();
    var created = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "create_entity",
            EntityKind = "角色",
            Title = "沈砚",
            Content = "第一版角色设定"
        }
    }, canvas, null);
    Expect(created.Applied == 1 && created.Errors.Count == 0, $"实体创建失败：{string.Join("；", created.Errors)}");

    var versioned = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "create_entity_version",
            EntityTarget = "沈砚",
            VariantTarget = "默认",
            Chapter = "第二章",
            Content = "第二版角色设定",
            VersionNote = "第二章进入听雨铃事件后的新设定"
        },
        new AgentAction
        {
            Kind = "create_node",
            Title = "第二章角色",
            Content = "沈砚在雨夜追查来信",
            EntityTarget = "沈砚",
            VariantTarget = "默认",
            VariantVersion = "v2"
        }
    }, canvas, null);

    Expect(versioned.Applied == 2 && versioned.Errors.Count == 0,
        $"版本与节点应全部成功：{string.Join("；", versioned.Errors)}");
    var entity = canvas.Entities.Single(entity => entity.Name == "沈砚");
    var variant = entity.Variants.Single(variant => variant.Name == "默认");
    var version = variant.Versions.Single(version => version.Number == 2);
    var initialVersion = variant.Versions.Single(version => version.Number == 1);
    var workItem = canvas.WorkTree.Single(item => item.SourceVersionId == version.Id);
    var node = canvas.Nodes.Single(node => node.Title == "第二章角色");

    Expect(version.SupersedesVersionId == initialVersion.Id,
        "V2 没有记录它继承的 V1 版本");
    Expect(workItem.Chapter == "第二章" && workItem.SourceEntityId == entity.Id
        && workItem.SourceVariantId == variant.Id && workItem.SourceVersionId == version.Id
        && workItem.SupersedesVersionId == initialVersion.Id && workItem.Version == "v2",
        "工作树版本条目的来源章节、升级关系或实体关联不完整");
    Expect(node.References.Count == 1 && node.References[0].EntityId == entity.Id
        && node.References[0].VariantId == variant.Id
        && node.References[0].VariantVersionId == version.Id,
        "节点没有锁定到新建的 v2 快照");
}

static void UpdateNodeChangesVersionReference()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(new[]
    {
        new AgentAction { Kind = "create_entity", EntityKind = "道具", Title = "听雨铃", Content = "初始设定" },
        new AgentAction
        {
            Kind = "create_entity_version",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            Chapter = "第三章",
            Content = "回声版本设定",
            VersionNote = "第三章获得回声能力"
        },
        new AgentAction
        {
            Kind = "create_node",
            Title = "旧版道具节点",
            Content = "仍按旧版描述",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            VariantVersion = "v1"
        }
    }, canvas, null);

    var updated = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "update_node",
            Target = "旧版道具节点",
            Content = "切换到当前版本描述",
            EntityTarget = "听雨铃",
            VariantTarget = "默认"
        }
    }, canvas, null);

    Expect(updated.Applied == 1 && updated.Errors.Count == 0,
        $"更新节点失败：{string.Join("；", updated.Errors)}");
    var node = canvas.Nodes.Single(node => node.Title == "旧版道具节点");
    Expect(node.References.Count == 1 && node.References[0].VariantVersionId is null,
        "省略版本号时节点应改为跟随变体当前内容");

    var locked = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "update_node",
            Target = "旧版道具节点",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            VariantVersion = "v1"
        }
    }, canvas, null);
    var initialVersion = canvas.Entities.Single().Variants.Single().Versions.Single(version => version.Number == 1);
    Expect(locked.Applied == 1 && canvas.Nodes.Single().References[0].VariantVersionId == initialVersion.Id,
        "明确指定历史版本后节点应锁定到 v1 快照");
}

static void VersionDecisionRoundTrips()
{
    var state = new WorkflowCanvasState();
    state.Nodes.Add(new WorkflowNode
    {
        Title = "第二章角色",
        Chapter = "第二章",
        VersionDecision = VersionDecision.KeepHistorical
    });

    var restored = JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(state));
    Expect(restored is not null && restored.Nodes.Single().VersionDecision == VersionDecision.KeepHistorical,
        "版本决策经过 JSON 保存和加载后没有保留");
}

static void KeepHistoricalVersionShowsStatus()
{
    var (canvas, control, node, _, _) = BuildVersionScenario();
    node.VersionDecision = VersionDecision.KeepHistorical;

    var summary = control.GetNodeVersionStatusSummary(node);
    Expect(summary.Contains("历史保留", StringComparison.Ordinal), $"保留历史后状态不正确：{summary}");
    control.Dispose();
}

static void AdoptedVersionShowsStatus()
{
    var (canvas, control, node, _, currentVersion) = BuildVersionScenario();
    node.References[0].VariantVersionId = currentVersion.Id;
    node.VersionDecision = VersionDecision.Adopted;

    var summary = control.GetNodeVersionStatusSummary(node);
    Expect(summary.Contains("已采纳", StringComparison.Ordinal), $"采用有效版本后状态不正确：{summary}");
    control.Dispose();
}

static void NewEffectiveVersionReopensConfirmation()
{
    var (canvas, control, node, variant, currentVersion) = BuildVersionScenario();
    node.References[0].VariantVersionId = currentVersion.Id;
    node.VersionDecision = VersionDecision.Adopted;

    variant.Description = "第三章新设定";
    var v3 = variant.Commit("第三章新设定");
    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = "默认 " + v3.Label,
        Chapter = "第三章",
        Version = v3.Label,
        SourceEntityId = canvas.Entities[0].Id,
        SourceVariantId = variant.Id,
        SourceVersionId = v3.Id,
        SupersedesVersionId = currentVersion.Id
    });
    node.Chapter = "第三章";

    var summary = control.GetNodeVersionStatusSummary(node);
    Expect(summary.Contains("需要确认", StringComparison.Ordinal),
        $"新有效版本出现后应重新提示确认，实际：{summary}");
    control.Dispose();
}

static (WorkflowCanvasState Canvas, WorkflowCanvasControl Control, WorkflowNode Node,
    WorkflowEntityVariant Variant, EntityVariantVersion CurrentVersion) BuildVersionScenario()
{
    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    variant.Description = "第二章设定";
    var v2 = variant.Commit("第二章更新");
    canvas.Entities.Add(entity);
    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = "默认 " + v2.Label,
        Chapter = "第二章",
        Version = v2.Label,
        SourceEntityId = entity.Id,
        SourceVariantId = variant.Id,
        SourceVersionId = v2.Id,
        SupersedesVersionId = v2.SupersedesVersionId
    });

    var node = new WorkflowNode
    {
        Title = "第二章角色",
        Chapter = "第二章",
        References = new List<NodeReference>
        {
            new() { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = v2.Id }
        }
    };
    canvas.Nodes.Add(node);
    var control = new WorkflowCanvasControl();
    control.LoadState(canvas);
    return (control.State, control, node, variant, v2);
}

static void PreviewDoesNotMutateCanvas()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    CanvasPreviewBuilder.Build(AgentActionParser.Parse(SampleReply()).Actions, canvas);
    // 预览是「试算到副本上」，真实画布在用户保存前必须一点都不能动。
    Expect(canvas.Nodes.Count == 1 && canvas.Edges.Count == 0,
        $"预览改动了真实画布：{canvas.Nodes.Count} 个节点 / {canvas.Edges.Count} 条边");
}

static void PreviewReportsAdditions()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    var preview = CanvasPreviewBuilder.Build(new[]
    {
        new AgentAction { Kind = "create_node", Title = "分镜 A", Content = "镜头" },
        new AgentAction { Kind = "create_edge", Source = "第一章", Target = "分镜 A" }
    }, canvas);
    Expect(preview.AddedNodes.Count == 1 && preview.AddedEdges.Count == 1,
        $"预览应报 1 个新节点 + 1 条新连线，实际 {preview.AddedNodes.Count} / {preview.AddedEdges.Count}");
}

static void PreviewIgnoresNoOp()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    canvas.Nodes.Add(new WorkflowNode { Title = "第二章" });
    // 删一条不存在的连线什么也不会变，预览不应凭空画出差异。
    var preview = CanvasPreviewBuilder.Build(
        new[] { new AgentAction { Kind = "delete_edge", Source = "第一章", Target = "第二章" } }, canvas);
    Expect(preview.IsEmpty, "无效果的操作不应产生预览差异");
}

static void PreviewReportsRemovedEdge()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "A" };
    var second = new WorkflowNode { Title = "B" };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = first.Id, TargetNodeId = second.Id });

    var preview = CanvasPreviewBuilder.Build(
        new[] { new AgentAction { Kind = "delete_edge", Source = "A", Target = "B" } }, canvas);
    Expect(preview.RemovedEdgeIds.Count == 1 && preview.AddedEdges.Count == 0,
        $"预览应报 1 条将被删除的连线，实际 {preview.RemovedEdgeIds.Count}");
    Expect(canvas.Edges.Count == 1, "预览不应真的删掉连线");
}

static void FileWritePreviewVersusCommit()
{
    var workspace = NewWorkspace();
    try
    {
        var file = Path.Combine(workspace, "note.md");
        var action = new[] { new AgentAction { Kind = "write_file", Path = "note.md", Content = "内容" } };

        var preview = AgentActionExecutor.Apply(action, new WorkflowCanvasState(), workspace, FileWriteMode.Skip);
        Expect(preview.Applied == 1 && !File.Exists(file), "预览模式不应落盘");

        var snapshots = new List<FileSnapshot>();
        var commit = AgentActionExecutor.Apply(action, new WorkflowCanvasState(), workspace, FileWriteMode.Apply, snapshots);
        Expect(commit.Applied == 1 && File.Exists(file), "提交时应落盘");
        // 文件原本不存在 → 快照记 null，撤销时据此删除这个新文件。
        Expect(snapshots.Count == 1 && snapshots[0].OriginalContent is null,
            $"应记录 1 条文件快照，实际 {snapshots.Count}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void FileWriteEscapeRejected()
{
    var workspace = NewWorkspace();
    try
    {
        var escaped = Path.Combine(Path.GetDirectoryName(workspace)!, "escape.md");
        var result = AgentActionExecutor.Apply(
            new[] { new AgentAction { Kind = "write_file", Path = "../escape.md", Content = "内容" } },
            new WorkflowCanvasState(), workspace, FileWriteMode.Apply);
        Expect(result.Applied == 0, "越界路径应被拒绝");
        Expect(!File.Exists(escaped), "越界路径写出了文件");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentImageRoundTrip()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "角色.png");
        var original = Convert.FromBase64String(Sample.TinyPngBase64);
        File.WriteAllBytes(path, original);

        var (attachment, error) = AgentAttachmentLoader.Load(path);
        Expect(attachment is not null, $"应成功加载图片：{error}");
        Expect(attachment!.Kind == AgentAttachmentKind.Image, "应识别为图片附件");
        Expect(attachment.Payload.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
            $"data URL 前缀不对：{attachment.Payload[..Math.Min(40, attachment.Payload.Length)]}");

        // 关键：发出去的必须是**原始字节**，不是被重新编码过的图。
        var split = AgentAttachmentLoader.SplitDataUrl(attachment.Payload);
        Expect(split is not null && split.Value.MediaType == "image/png", "应能拆出 media type");
        Expect(Convert.FromBase64String(split!.Value.Data).SequenceEqual(original), "还原出来的字节与原始文件不一致");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentImageTooLarge()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "巨图.png");
        File.WriteAllBytes(path, new byte[AgentAttachmentLoader.MaxImageBytes + 1]);
        var (attachment, error) = AgentAttachmentLoader.Load(path);
        // 拒绝要给出可执行的原因，不能只说"失败"。
        Expect(attachment is null && error is not null && error.Contains("超过", StringComparison.Ordinal),
            $"超限图片应被拒绝并说明原因，实际：{error}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentTextTruncation()
{
    var workspace = NewWorkspace();
    try
    {
        var shortPath = Path.Combine(workspace, "设定.md");
        File.WriteAllText(shortPath, "# 主角设定\n少年剑客");
        var (small, _) = AgentAttachmentLoader.Load(shortPath);
        Expect(small is not null && small.Kind == AgentAttachmentKind.Text && !small.Truncated, "普通文本文件应完整读取");
        Expect(small!.Payload.Contains("少年剑客", StringComparison.Ordinal), "文本内容丢失");

        var longPath = Path.Combine(workspace, "长文.md");
        File.WriteAllText(longPath, new string('字', AgentAttachmentLoader.MaxTextCharacters + 5000));
        var (big, _) = AgentAttachmentLoader.Load(longPath);
        Expect(big is not null && big.Truncated, "超长文本应被标记为已截断");
        Expect(big!.Payload.Length == AgentAttachmentLoader.MaxTextCharacters, $"截断长度不对：{big.Payload.Length}");

        // 截断必须让模型知道，否则它会以为读到了全文。
        var composed = AgentAttachmentLoader.ComposeContent("看看这份设定", new[] { big });
        Expect(composed.Contains("已截断", StringComparison.Ordinal), "拼进正文时应标注截断");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentBinaryRejected()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "分镜.pdf");
        File.WriteAllBytes(path, new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var (attachment, error) = AgentAttachmentLoader.Load(path);
        Expect(attachment is null, "PDF 不应被当作可发送附件");
        Expect(error is not null && error.Contains("转成文本", StringComparison.Ordinal),
            $"拒绝理由应说明怎么解决，实际：{error}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void ComposeContentAndCollectImages()
{
    var image = new AgentAttachment { Name = "角色.png", Kind = AgentAttachmentKind.Image, Payload = "data:image/png;base64,AAAA" };
    var text = new AgentAttachment { Name = "设定.md", Kind = AgentAttachmentKind.Text, Payload = "少年剑客" };
    var composed = AgentAttachmentLoader.ComposeContent("这张图有问题吗", new[] { image, text });

    Expect(composed.StartsWith("这张图有问题吗", StringComparison.Ordinal), "用户的问题应保留在最前");
    Expect(composed.Contains("【附件：设定.md】", StringComparison.Ordinal) && composed.Contains("少年剑客", StringComparison.Ordinal),
        "文本附件的内容应拼进正文");
    Expect(!composed.Contains("data:image/png", StringComparison.Ordinal), "图片不应被塞进正文（它走图片块）");

    var images = AgentAttachmentLoader.CollectImages(new[] { image, text });
    Expect(images.Count == 1 && images[0] == image.Payload, "只应收集图片附件");
}

static void OpenAiPayloadCarriesImage()
{
    var handler = SendOneTurn(AiApiFormat.OpenAiChat, vision: true, content: "这张图怎样", images: new[] { Sample.DataUrl });
    var messages = JsonDocument.Parse(handler.Body!).RootElement.GetProperty("messages");

    Expect(messages[0].GetProperty("role").GetString() == "system", "第一条应是 system 消息");
    var user = messages[1];
    var parts = user.GetProperty("content");
    Expect(parts.ValueKind == JsonValueKind.Array, "带图片时 content 应是内容块数组");
    Expect(parts[0].GetProperty("type").GetString() == "text" && parts[0].GetProperty("text").GetString() == "这张图怎样",
        "第一个块应是文本块");
    Expect(parts[1].GetProperty("type").GetString() == "image_url"
        && parts[1].GetProperty("image_url").GetProperty("url").GetString() == Sample.DataUrl,
        "第二个块应是 image_url，且 url 就是 data URL");
}

static void AnthropicPayloadCarriesImage()
{
    var handler = SendOneTurn(AiApiFormat.AnthropicMessages, vision: true, content: "这张图怎样", images: new[] { Sample.DataUrl });
    var root = JsonDocument.Parse(handler.Body!).RootElement;

    // Anthropic 的 system 必须放在顶层，不能出现在 messages 里。
    Expect(root.GetProperty("system").ValueKind == JsonValueKind.String, "system 应是顶层字符串");
    Expect(root.GetProperty("max_tokens").GetInt32() > 0, "Anthropic 的 max_tokens 是必填项");
    foreach (var message in root.GetProperty("messages").EnumerateArray())
        Expect(message.GetProperty("role").GetString() != "system", "messages 里不应出现 system 角色");

    var parts = root.GetProperty("messages")[0].GetProperty("content");
    var image = parts[1];
    Expect(image.GetProperty("type").GetString() == "image", "第二个块应是 image");
    var source = image.GetProperty("source");
    // data URL 必须拆成 media_type + data 两部分，直接塞 data URL 会被拒。
    Expect(source.GetProperty("type").GetString() == "base64", "data URL 应转成 base64 来源");
    Expect(source.GetProperty("media_type").GetString() == "image/png", "media_type 应从 data URL 里拆出来");
    Expect(source.GetProperty("data").GetString() == Sample.DataUrl.Split(',')[1], "base64 数据应原样传递");
}

static void SystemMessageStaysTextOnly()
{
    // DeepSeek 对 system 消息里的图片直接返回 400，所以系统提示必须始终是纯文本。
    var openai = JsonDocument.Parse(
        SendOneTurn(AiApiFormat.OpenAiChat, vision: true, content: "问题", images: new[] { Sample.DataUrl }).Body!)
        .RootElement.GetProperty("messages")[0];
    Expect(openai.GetProperty("content").ValueKind == JsonValueKind.String, "OpenAI 格式的 system 内容应是纯文本");
}

static void ImageWithoutVisionFails()
{
    string? message = null;
    try { SendOneTurn(AiApiFormat.OpenAiChat, vision: false, content: "这张图怎样", images: new[] { Sample.DataUrl }); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(message is not null, "未开启图片输入时应直接拦住，而不是把图发给接口");
    // 报错要能指导用户怎么改，不能把服务端的 400 原样抛出来。
    Expect(message!.Contains("图片输入", StringComparison.Ordinal) && message.Contains("deepseek-flash", StringComparison.Ordinal),
        $"报错信息不够可读：{message}");
}

/// <summary>发一轮对话，返回被桩 HttpClient 拦下来的请求体。</summary>
static CapturingHandler SendOneTurn(AiApiFormat format, bool vision, string content, IReadOnlyList<string> images)
{
    var handler = new CapturingHandler();
    var config = ConfigFor(format);
    config.SupportsImageInput = vision;
    var provider = new OpenAiCompatibleProvider(config, new HttpClient(handler));
    provider.ChatAsync(new[] { new AiChatMessage { Role = "user", Content = content, Images = images } }).GetAwaiter().GetResult();
    return handler;
}

static void SecretProtectorRoundTrip()
{
    const string key = "sk-test-placeholder-0123456789abcdef";
    var cipher = SecretProtector.Protect(key);
    Expect(SecretProtector.IsProtected(cipher), "加密结果应带 dpapi: 前缀");
    Expect(!cipher.Contains(key, StringComparison.Ordinal), "密文里不应出现明文");
    Expect(SecretProtector.Unprotect(cipher) == key, "解密应还原原值");
    Expect(SecretProtector.Protect(cipher) == cipher, "已加密的值不应被二次加密（否则解不回来）");

    // 旧版本留下的明文要原样返回，由保存流程负责迁移，不能直接判为"坏数据"。
    Expect(SecretProtector.Unprotect(key) == key, "明文应原样返回以兼容旧配置");
    // 密文损坏时返回 null 而不是抛异常，让上层能给出可读的提示。
    Expect(SecretProtector.Unprotect("dpapi:!!!not-base64!!!") is null, "非法 base64 应返回 null");

    var described = SecretProtector.Describe(key);
    Expect(!described.Contains(key, StringComparison.Ordinal), $"脱敏描述泄露了完整密钥：{described}");
    Expect(described.Contains('…', StringComparison.Ordinal), $"脱敏描述应带省略号：{described}");
    Expect(SecretProtector.Describe(string.Empty) == "（未填写）", "空密钥的描述不符");
}

static void ConfigFileStoresCiphertext()
{
    const string key = "sk-roundtrip-check-0123456789";
    using var environment = new ConfigEnvironment();
    try
    {
        var config = AiProviderSettings.Load();
        config.Endpoint = "https://api.example.com/v1";
        config.Model = "test-model";
        config.ApiKey = key;
        Expect(AiProviderSettings.Save(config), "保存应该成功");

        // 这是本轮要保证的核心性质：文件里永远是密文，内存里永远是明文。
        var onDisk = File.ReadAllText(environment.ConfigPath);
        Expect(!onDisk.Contains(key, StringComparison.Ordinal), "配置文件里出现了明文密钥");
        Expect(onDisk.Contains(SecretProtector.Prefix, StringComparison.Ordinal), "配置文件里没有 DPAPI 密文");
        Expect(config.ApiKey == key, "保存后内存里的密钥应仍是明文（请求要带它）");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKey == key, $"读回应还原明文，实际：{loaded.ApiKey}");
        Expect(!loaded.ApiKeyUnreadable && !loaded.ApiKeyWasPlaintext, "正常读回不该带任何警告标记");
    }
    finally { environment.Restore(); }
}

static void BrokenCiphertextIsReported()
{
    using var environment = new ConfigEnvironment();
    try
    {
        // 密文合法但解不开（等价于配置被拷到别的 Windows 账户或机器）。
        File.WriteAllText(environment.ConfigPath,
            "{\"Endpoint\":\"https://api.example.com/v1\",\"Model\":\"m\",\"ApiKey\":\"dpapi:bm90LWEtcmVhbC1jaXBoZXJ0ZXh0\"}");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKeyUnreadable, "解不开的密文应被标记，供界面提示重新填写");
        Expect(loaded.ApiKey.Length == 0, "解不开时密钥应为空，绝不能拿密文去当密钥请求");
        // 一处解不开不该把整个配置废掉。
        Expect(loaded.Endpoint == "https://api.example.com/v1" && loaded.Model == "m", "其它配置项不应受影响");
    }
    finally { environment.Restore(); }
}

static void PlaintextSecretMigratesOnLoad()
{
    const string key = "sk-legacy-plaintext-9876543210";
    using var environment = new ConfigEnvironment();
    try
    {
        // 模拟旧版本留下的明文配置。
        File.WriteAllText(environment.ConfigPath,
            $"{{\"Endpoint\":\"https://api.example.com/v1\",\"Model\":\"m\",\"ApiKey\":\"{key}\",\"ProviderChoiceMade\":true}}");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKey == key, $"旧明文配置应仍然可用，实际：{loaded.ApiKey}");
        Expect(loaded.ApiKeyWasPlaintext, "应记录「原来是明文」这一事实，供界面提示已自动加密");
        Expect(!loaded.ApiKeyUnreadable, "明文不是坏数据，不该被标记为无法解密");

        // 关键：读取时就地把明文升级为密文，不等用户下次打开设置——缩小明文暴露窗口。
        var onDisk = File.ReadAllText(environment.ConfigPath);
        Expect(!onDisk.Contains(key, StringComparison.Ordinal), "明文密钥没有被迁移掉");
        Expect(onDisk.Contains(SecretProtector.Prefix, StringComparison.Ordinal), "迁移后应是 DPAPI 密文");
        Expect(AiProviderSettings.Load().ApiKey == key, "迁移后应仍能读回原密钥");
    }
    finally { environment.Restore(); }
}

static void OpenAiStreamParsesDeltas()
{
    // 思考型模型的 SSE：先来 reasoning_content，再来正文。思考绝不能混进正文。
    const string sse = """
        data: {"choices":[{"delta":{"reasoning_content":"让我想想"}}]}

        data: {"choices":[{"delta":{"content":"第一段"}}]}

        data: {"choices":[{"delta":{"content":"第二段"}}]}

        data: [DONE]

        """;
    var handler = new CapturingHandler { ResponseBody = sse, ContentType = "text/event-stream" };
    var deltas = new List<string>();
    var thinking = new List<string>();
    var reply = StreamOnce(AiApiFormat.OpenAiChat, handler, deltas, thinking.Add);

    Expect(reply == "第一段第二段", $"拼接结果不对：{reply}");
    Expect(deltas.SequenceEqual(new[] { "第一段", "第二段" }), $"增量应分次回调：{string.Join("/", deltas)}");
    // 思考作为独立的一条流给出来（把 reasoning 与 reply 分开记）。
    Expect(thinking.Count == 1 && thinking[0] == "让我想想", $"思考增量不正确：{string.Join("/", thinking)}");
    Expect(!reply.Contains("让我想想", StringComparison.Ordinal), "思考内容混进了正文");
    Expect(handler.Body!.Contains("\"stream\":true", StringComparison.Ordinal), "请求体没有带 stream:true");
}

static void AnthropicStreamParsesDeltas()
{
    // Anthropic 的事件流：增量在 content_block_delta，文本是 text_delta，思考是 thinking_delta。
    const string sse = """
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"嗯"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"甲"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"乙"}}

        data: [DONE]

        """;
    var handler = new CapturingHandler { ResponseBody = sse, ContentType = "text/event-stream" };
    var deltas = new List<string>();
    var thinking = new List<string>();
    var reply = StreamOnce(AiApiFormat.AnthropicMessages, handler, deltas, thinking.Add);

    Expect(reply == "甲乙", $"拼接结果不对：{reply}");
    Expect(thinking.Count == 1 && thinking[0] == "嗯", $"thinking_delta 应作为思考增量给出：{string.Join("/", thinking)}");
    Expect(handler.Body!.Contains("\"stream\":true", StringComparison.Ordinal), "请求体没有带 stream:true");
    Expect(handler.Body!.Contains("max_tokens", StringComparison.Ordinal), "Anthropic 报文仍应带必填的 max_tokens");
}

static void EmptyStreamIsReportedAsError()
{
    // 网关返回 200 但一行正文都没有（例如不支持 stream）——必须报错，不能静默返回空回复。
    var handler = new CapturingHandler { ResponseBody = "data: [DONE]\n\n", ContentType = "text/event-stream" };
    string? message = null;
    try { StreamOnce(AiApiFormat.OpenAiChat, handler, new List<string>(), _ => { }); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(message is not null && message.Contains("没有收到任何文本", StringComparison.Ordinal),
        $"应明确报错，实际：{message ?? "（没有抛异常）"}");
}

static void ThinkingOnlyIsStillAnError()
{
    // 只收到思考、没有正文：这是不完整的一轮，不能当作成功（否则界面会显示一个空回答）。
    var handler = new CapturingHandler
    {
        ResponseBody = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想了半天\"}}]}\n\ndata: [DONE]\n\n",
        ContentType = "text/event-stream"
    };
    var thinking = new List<string>();
    string? message = null;
    try { StreamOnce(AiApiFormat.OpenAiChat, handler, new List<string>(), thinking.Add); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(thinking.Count == 1, "思考增量仍应被回调出来");
    Expect(message is not null, "只有思考没有正文时应报错");
}

static void ActionsBlockIsHiddenFromDisplay()
{
    // 流式显示要在协议块之前截断，而且不能把```json 围栏留在对话区。
    const string reply = "建议这样改：\n\n```json\n{\"actions\":[{\"kind\":\"create_node\",\"title\":\"A\"}]}\n```\n";
    var cut = AgentActionParser.FindProtocolBlockStart(reply);
    Expect(cut >= 0, "应能定位协议块");
    var shown = reply[..cut].Trim();
    Expect(shown == "建议这样改：", $"显示部分不符：{shown}");
    Expect(!shown.Contains("```", StringComparison.Ordinal), "围栏不该出现在显示部分");

    // 没有协议块的普通回复不该被截断。
    Expect(AgentActionParser.FindProtocolBlockStart("普通回复，没有操作块。") < 0, "无操作块时不应截断");
    Expect(AgentActionParser.FindProtocolBlockStart(string.Empty) < 0, "空文本不应截断");
}

static void AskIsParsedWithOptions()
{
    // 模型信息不足时应当「问」，界面据此弹窗——而不是把问题写成一大段正文让用户自己组织语言。
    const string reply = """
        我需要确认两件事再动手。

        ```json
        {"ask":{"question":"要加哪种节点？","options":["角色节点","场景节点","分镜节点"]}}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null, "应解析出反问");
    Expect(parsed.Ask!.Question == "要加哪种节点？", $"问题文本不符：{parsed.Ask.Question}");
    Expect(parsed.Ask.Options.SequenceEqual(new[] { "角色节点", "场景节点", "分镜节点" }),
        $"选项不符：{string.Join("/", parsed.Ask.Options)}");
    Expect(parsed.Actions.Count == 0, "这条回复没有改动提议");
    Expect(!parsed.Text.Contains("ask", StringComparison.Ordinal), $"展示文本里残留了协议块：{parsed.Text}");
    Expect(parsed.Text.Contains("我需要确认", StringComparison.Ordinal), "自然语言部分应保留");
}

static void AskCoexistsWithActions()
{
    // 允许「先问清、再改」同批出现；两者必须互不干扰。
    const string reply = """
        ```json
        {"ask":{"question":"加到哪个画布？","options":["当前画布"]},
         "actions":[{"kind":"create_node","title":"新角色","content":"待补充","reason":"先占位"}]}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null && parsed.Ask.Question == "加到哪个画布？", "反问应被解析");
    Expect(parsed.Actions.Count == 1 && parsed.Actions[0].Title == "新角色", "操作应被解析");
}

static void AskAloneIsValid()
{
    // 只有 ask、没有 actions：过去这种回复会因为找不到 "actions" 标记而被当成纯文本，
    // 整块 JSON 直接糊在对话区里——这正是要避免的。
    const string reply = "先确认一下。\n\n{\"ask\":{\"question\":\"内容写什么？\",\"options\":[]}}\n";
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null && parsed.Ask.Question == "内容写什么？", "应解析出反问");
    Expect(parsed.Ask!.Options.Count == 0, "允许不给选项（纯自由输入）");
    Expect(!parsed.Text.Contains("\"ask\"", StringComparison.Ordinal), "协议块应从展示文本里剥离");
    Expect(parsed.Text.Trim() == "先确认一下。", $"展示文本不符：{parsed.Text}");
}

static void AskBlockIsHiddenFromDisplay()
{
    // 流式显示时 ask 块也要被挡住（它和 actions 走同一套定位）。
    const string reply = "我先问一下：\n\n```json\n{\"ask\":{\"question\":\"哪种节点？\"}}\n```\n";
    var cut = AgentActionParser.FindProtocolBlockStart(reply);
    Expect(cut >= 0, "ask 块也应能被定位");
    var shown = reply[..cut].Trim();
    Expect(!shown.Contains("```", StringComparison.Ordinal) && !shown.Contains("\"ask\"", StringComparison.Ordinal),
        $"显示部分不该包含围栏或 ask 块：{shown}");
}

/// <summary>
/// 真实样本：用户让 Agent「当前画布加个新节点」，模型最后给出的 create_entity 块里，
/// content 的正文用了**英文引号**（"读" / "有用"）却没转义，整块 JSON 因此非法。
/// 修复前：定位不到块 → 整块 JSON 原样糊在对话区里、改动全部作废（用户的"没有加上"）。
/// 修复后：内容里的裸引号被自动转义，块照常解析。
/// </summary>
static void BrokenProtocolIsHiddenFromDisplay()
{
    const string reply = """
        我先说明处理方式。

        ```json
        {"actions":[{"kind":"update_node","content":"未闭合"}]
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.ProtocolBroken, "格式损坏的协议块应标记为失败");
    Expect(parsed.Text.Trim() == "我先说明处理方式。", $"应只保留自然语言，实际：{parsed.Text}");
    Expect(!parsed.Text.Contains("update_node", StringComparison.Ordinal), "损坏的操作协议不应出现在展示文本");
}

static void IncompleteAskProtocolIsHidden()
{
    const string reply = """
        你还没说清要加什么节点，我先问一下，避免猜错建错。

        ```json
        {"ask":{"question":"这两个节点要建成什么类型、什么内容？","options":["角色节点（我做设定）","场景节点","分镜节点","普通草稿节点（先占位，内容我来给）"]
        """;
    var parsed = AgentActionParser.Parse(reply);
    var streamCut = AgentActionParser.FindProtocolBlockStart(reply);

    Expect(parsed.ProtocolBroken, "未闭合 ask 协议应标记为格式错误");
    Expect(parsed.Text.Trim() == "你还没说清要加什么节点，我先问一下，避免猜错建错。", $"应只保留自然语言，实际：{parsed.Text}");
    Expect(!parsed.Text.Contains("options", StringComparison.Ordinal), "损坏的 ask JSON 不应泄漏");
    Expect(streamCut >= 0 && !reply[..streamCut].Contains("options", StringComparison.Ordinal), "流式展示不应显示 fenced JSON");

    const string truncated = "你还没说是什么节点，先确认类型，我再把它建到画布上。\n\n```json\n{\"ask";
    var partialCut = AgentActionParser.FindProtocolBlockStart(truncated);
    var partialParsed = AgentActionParser.Parse(truncated);
    Expect(partialCut >= 0 && !truncated[..partialCut].Contains("ask", StringComparison.Ordinal), "半截 ask 标记必须在流式阶段隐藏");
    Expect(partialParsed.ProtocolBroken && !partialParsed.Text.Contains("ask", StringComparison.Ordinal), "半截 ask 标记不能泄漏到最终回复");

    var chunks = new[] { "正文。\n\n```", "json", "\n", "{", "\\\"ask" };
    var accumulated = string.Empty;
    var cuts = new List<int>();
    foreach (var chunk in chunks)
    {
        accumulated += chunk;
        cuts.Add(AgentActionParser.FindProtocolBlockStart(accumulated));
    }
    Expect(cuts.All(cut => cut == "正文。\n\n".Length), $"各流式分片都应从起始围栏处隐藏，实际：{string.Join(",", cuts)}");
}

static void BrokenQuotesSampleStillParses()
{
    const string reply = """
        我按"低起点、内在有缺口"这个骨架拟了一个主角，设定放进改动块里，等你批准。

        ```json
        {"actions":[{"kind":"create_entity","entityKind":"角色","title":"沈砚（主角）","content":"【定位】主角\n【秘密】他是唯一能"读"封印档案的人；这能力不是天赋，是十年前留下的疤。\n【内在需求】真正的成长是学会不靠"有用"来换取留下。","reason":"用户要求新建一个角色节点，名称与设定由我拟定"}]}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(!parsed.ProtocolBroken, "内容里的裸引号不该被判成坏块");
    Expect(parsed.Actions.Count == 1, $"应解析出 1 条改动，实际 {parsed.Actions.Count}");
    var action = parsed.Actions[0];
    Expect(action.Kind == "create_entity" && action.EntityKind == "角色", $"改动种类不符：{action.Kind}/{action.EntityKind}");
    Expect(action.Title == "沈砚（主角）", $"标题不符：{action.Title}");
    Expect(action.Content.Contains("\"读\"", StringComparison.Ordinal), "内容里的引号应原样保留");
    Expect(action.Content.Contains("不靠\"有用\"来换取", StringComparison.Ordinal) || action.Content.Contains("有用", StringComparison.Ordinal),
        "内容应完整保留");
    Expect(!parsed.Text.Contains("\"actions\"", StringComparison.Ordinal), "协议块应从展示文本里剥离");
}

/// <summary>跑一次流式对话，返回正文；正文增量与思考增量通过参数带出。</summary>
static string StreamOnce(AiApiFormat format, CapturingHandler handler, List<string> deltas, Action<string> onThinking)
{
    var provider = new OpenAiCompatibleProvider(ConfigFor(format), new HttpClient(handler));
    return provider.ChatStreamAsync(
        new[] { new AiChatMessage { Role = "user", Content = "hi" } },
        new AiStreamSink(deltas.Add, onThinking)).GetAwaiter().GetResult();
}

static AiProviderConfig ConfigFor(AiApiFormat format) => new()
{
    Endpoint = "https://example.invalid/v1",
    Model = "test-model",
    ApiKey = "test-key",
    ApiFormat = format,
    SupportsImageInput = true,
    SendSamplingParameters = true
};

static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static string NewWorkspace()
{
    var path = Path.Combine(Path.GetTempPath(), "df-agent-tests-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(path);
    return path;
}

/// <summary>测试用的样本数据。</summary>
static class Sample
{
    /// <summary>1x1 透明 PNG：用真实图片字节，而不是随便凑一段数据。</summary>
    public const string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    public const string DataUrl = "data:image/png;base64," + TinyPngBase64;
}

/// <summary>
/// 把配置文件路径隔离到临时目录，并清掉会覆盖它的环境变量——
/// 否则测试会碰到用户真实的配置与密钥（环境变量优先级高于配置文件）。
/// </summary>
sealed class ConfigEnvironment : IDisposable
{
    private readonly string? previousConfig = Environment.GetEnvironmentVariable("DREAMFORGE_CONFIG");
    private readonly string? previousKey = Environment.GetEnvironmentVariable("DREAMFORGE_AI_KEY");
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "df-config-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>本次测试使用的配置文件路径。</summary>
    public string ConfigPath { get; }

    public ConfigEnvironment()
    {
        Directory.CreateDirectory(workspace);
        ConfigPath = Path.Combine(workspace, "ai-config.json");
        Environment.SetEnvironmentVariable("DREAMFORGE_CONFIG", ConfigPath);
        Environment.SetEnvironmentVariable("DREAMFORGE_AI_KEY", null);
    }

    public void Restore()
    {
        Environment.SetEnvironmentVariable("DREAMFORGE_CONFIG", previousConfig);
        Environment.SetEnvironmentVariable("DREAMFORGE_AI_KEY", previousKey);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
    }

    public void Dispose() { }
}

/// <summary>桩 HttpClient：不发真实请求，只把请求体留给我们检查；响应内容可指定（含 SSE）。</summary>
sealed class CapturingHandler : HttpMessageHandler
{
    public string? Body { get; private set; }

    /// <summary>返回给调用方的响应体，默认是一个最小的非流式回答。</summary>
    public string ResponseBody { get; set; } = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";

    public string ContentType { get; set; } = "application/json";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, ContentType)
        };
    }
}
