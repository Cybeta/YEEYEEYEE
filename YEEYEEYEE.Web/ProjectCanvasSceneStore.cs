using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>桌面画布文件的 Web 适配器；不会将投影视图持久化。</summary>
internal sealed class ProjectCanvasSceneStore
{
    private readonly string defaultPath;
    private readonly string defaultEntitiesPath;
    private string path => WebProjectApi.Current?.CanvasPath ?? defaultPath;
    private string entitiesPath => WebProjectApi.Current?.EntitiesPath ?? defaultEntitiesPath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 最近一次**成功提交**的（提交序号，对外修订号）。失败时两个都不动。
    ///
    /// 为什么要两个数：调用方得在请求前后各读一次才知道「这次到底写没写」——状态码不行，
    /// 「本来就无需改动」这类请求也是 200，而它不该广播说画布变了。
    /// 但**对外那个修订号不能拿来比大小**：项目模式下它是画布内容的哈希，
    /// 新内容的哈希不保证比旧的大，拿它比「变大了没有」等于掷硬币。
    /// 所以另给一个单调递增的序号当判据，修订号只当推送载荷用（它就是接口回报给客户端的那个数）。
    /// </summary>
    private readonly AsyncLocal<CommitState?> commit = new();
    private sealed class CommitState { public (long Serial, long Revision) Value; }
    public (long Serial, long Revision) LastCommit
    {
        get { commit.Value ??= new CommitState(); return commit.Value.Value; }
        private set { commit.Value ??= new CommitState(); commit.Value.Value = value; }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public ProjectCanvasSceneStore(string canvasPath, string? configuredEntitiesPath) =>
        (defaultPath, defaultEntitiesPath) = ResolvePaths(canvasPath, configuredEntitiesPath);

    /// <summary>
    /// 校验并规范化画布与资源路径。**构造函数与「这张实例在看哪张画布」的解析共用这一处**，
    /// 免得两套规则各自演化出差异（编辑锁的落点靠它决定）。
    /// </summary>
    internal static (string CanvasPath, string EntitiesPath) ResolvePaths(string canvasPath, string? configuredEntitiesPath)
    {
        if (!Path.IsPathFullyQualified(canvasPath)) throw new ArgumentException("画布路径必须是绝对路径");
        var resolved = Path.GetFullPath(canvasPath);
        var directory = Path.GetDirectoryName(resolved)!;
        var root = Directory.GetParent(directory)?.FullName ?? throw new ArgumentException("缺少项目根目录");
        if (!string.Equals(Path.GetFileName(directory), "canvases", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, "project.json")))
            throw new ArgumentException("画布必须位于含 project.json 的项目 canvases 目录");
        var entities = Path.Combine(root, "project", "entities.json");
        if (string.IsNullOrWhiteSpace(configuredEntitiesPath) || !Path.IsPathFullyQualified(configuredEntitiesPath) ||
            !string.Equals(Path.GetFullPath(configuredEntitiesPath), entities, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("项目资源路径必须与画布属于同一个项目");
        if (ProjectContext.Open(root) is null) throw new ArgumentException("项目描述文件无效");
        return (resolved, entities);
    }


    /// <summary>修订号的定义在 <see cref="CanvasRevision.Of"/>：桌面端也要算同一个数，所以实现只有一份。</summary>
    private static long Revision(byte[] bytes) => CanvasRevision.Of(bytes);

    /// <summary>磁盘上这份画布现在的修订号；读不到（文件不在、没权限）就是 null。</summary>
    public long? CurrentRevision()
    {
        try { return Revision(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private (RecentCanvasState State, byte[] Bytes, long Revision, CanvasOpenOutcome Open) Load()
    {
        // 同一份字节快照同时提供响应修订号和投影状态。
        var bytes = File.ReadAllBytes(path);
        if (!CanvasOpenService.TryOpenBytes(bytes, path, out var opened, out var reason))
            throw new InvalidDataException(reason);
        if (opened.State.Canvas is null) throw new InvalidDataException("画布节点集合不存在");
        return (opened.State, bytes, Revision(bytes), opened);
    }

    private ProjectEntityFile? CheckProjectAuthority(RecentCanvasState state)
    {
        if (!File.Exists(entitiesPath))
        {
            if (state.Canvas.Entities.Any(entity => entity.ManagedByProject) ||
                state.Canvas.Nodes.SelectMany(node => node.References).Any(reference =>
                    !state.Canvas.Entities.Any(entity => entity.Id == reference.EntityId && !entity.ManagedByProject)) ||
                state.Canvas.WorkTree.Any(item => item.SourceEntityId is { } id &&
                    !state.Canvas.Entities.Any(entity => entity.Id == id && !entity.ManagedByProject)))
                throw new InvalidDataException("项目资源库缺失，无法验证引用");
            return null;
        }
        var library = JsonSerializer.Deserialize<ProjectEntityFile>(File.ReadAllText(entitiesPath), JsonOptions)
            ?? throw new InvalidDataException("项目资源库为空");
        if (library.FormatVersion > ProjectLibrary.CurrentFormat || library.Entities is null ||
            library.Entities.Any(entity => entity is null || entity.Id == Guid.Empty || entity.Variants is null ||
                entity.Variants.Any(variant => variant is null || variant.Id == Guid.Empty || variant.Versions is null)) ||
            library.Entities.GroupBy(entity => entity.Id).Any(group => group.Count() != 1) ||
            library.Entities.SelectMany(entity => entity.Variants).GroupBy(variant => variant.Id).Any(group => group.Count() != 1))
            throw new InvalidDataException("项目资源库格式不支持或实体/变体 ID 重复");
        var byId = library.Entities.ToDictionary(entity => entity.Id);
        if (state.Canvas.Entities.Any(entity => entity.ManagedByProject && !byId.ContainsKey(entity.Id)))
            throw new InvalidDataException("画布引用的项目实体在权威库中缺失");
        if (state.Canvas.Entities.Any(entity => entity.ManagedByProject &&
            entity.Variants.Any(variant => !byId[entity.Id].Variants.Any(item => item.Id == variant.Id))))
            throw new InvalidDataException("画布托管实体的变体在权威库中缺失");

        void CheckReference(Guid? entityId, Guid? variantId, Guid? versionId)
        {
            if (entityId is not { } id) return;
            // 本地实体以自身为准；其它实体必须存在于项目库。
            if (state.Canvas.Entities.Any(local => local.Id == id && !local.ManagedByProject)) return;
            if (!byId.TryGetValue(id, out var entity))
                throw new InvalidDataException("项目库中缺少画布引用的实体");
            var variant = variantId is { } vid && vid != Guid.Empty
                ? entity.Variants.FirstOrDefault(item => item.Id == vid)
                : entity.Variants.FirstOrDefault();
            if (variant is null || versionId is { } ver && !variant.Versions.Any(item => item.Id == ver))
                throw new InvalidDataException("项目库中缺少画布引用的变体或版本");
        }
        foreach (var node in state.Canvas.Nodes)
            foreach (var reference in node.References)
                CheckReference(reference.EntityId, reference.VariantId, reference.VariantVersionId);
        foreach (var item in state.Canvas.WorkTree)
            if (item.SourceEntityId is not null)
                CheckReference(item.SourceEntityId, item.SourceVariantId, item.SourceVersionId);
        return library;
    }

    private static object Project(string canvasPath, RecentCanvasState state, long revision, CanvasOpenOutcome opened)
    {
        // 顶部面包屑与画布标签要显示的是**项目名与画布名**，不是文件名。两个名字都在手边：
        // 项目名在 project.json 里（ResolvePaths 已经确认过这个画布确实属于某个项目根），
        // 画布名就是画布文件自己的 Title。缺了它们网页端只能显示占位文字，那比没有更糟。
        var root = Directory.GetParent(Path.GetDirectoryName(canvasPath)!)!.FullName;
        return new
        {
            revision,
            records = NodeProjection.ProjectRecords(state.Canvas.Nodes, state.Canvas),
            // 连线也要投影：桌面端的画布上一直画着它们，网页端却完全看不到——那是白丢一层信息。
            // 只投影两头都还在的那些：悬空连线画不出来，也不该被画出来。
            edges = ProjectEdges(state.Canvas),
            readOnly = opened.UnsupportedFormat || opened.Validation.HasErrors || opened.Migration.Ambiguities.Count > 0,
            formatVersion = state.FormatVersion,
            projectName = ProjectContext.Open(root)?.Descriptor.Name ?? Path.GetFileName(root),
            canvasTitle = state.Title,
            migration = opened.Migration,
            validation = opened.Validation
        };
    }

    /// <summary>
    /// 连线投影：<c>edgeId / sourceId / targetId</c>，与记录的 <c>recordId</c> 用同一套标识（都是节点 GUID）。
    /// 两端任一不在画布上的连线直接跳过——画不出来，也不该让前端去猜它连到哪。
    /// </summary>
    private static List<object> ProjectEdges(WorkflowCanvasState canvas)
    {
        var known = canvas.Nodes.Select(node => node.Id).ToHashSet();
        return canvas.Edges
            .Where(edge => known.Contains(edge.SourceNodeId) && known.Contains(edge.TargetNodeId))
            .Select(edge => (object)new
            {
                edgeId = edge.Id.ToString(),
                sourceId = edge.SourceNodeId.ToString(),
                targetId = edge.TargetNodeId.ToString()
            })
            .ToList();
    }

    internal (WorkflowCanvasState Canvas, WorkflowNode Node, long Revision) GenerationTarget(Guid nodeId)
    {
        var current = Load();
        var authority = CheckProjectAuthority(current.State);
        ProjectEntityScope.RefreshSnapshots(current.State.Canvas, authority ?? new ProjectEntityFile());
        var node = current.State.Canvas.Nodes.SingleOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidDataException("目标节点不存在");
        return (current.State.Canvas, node, current.Revision);
    }

    internal static (Guid Entity, Guid Variant)? OutputTarget(WorkflowCanvasState canvas, WorkflowNode node, string entityTarget, string variantTarget)
    {
        var explicitTarget = !string.IsNullOrEmpty(entityTarget) || !string.IsNullOrEmpty(variantTarget);
        var kind = node.Category switch { NodeCategory.Character => EntityKind.Character, NodeCategory.Prop => EntityKind.Prop, NodeCategory.Scene => EntityKind.Scene, _ => (EntityKind?)null };
        if (!explicitTarget && kind is null) return null;
        if (explicitTarget && (!Guid.TryParse(entityTarget, out _) || !Guid.TryParse(variantTarget, out _)))
            throw new InvalidDataException("设定图输出目标须提供完整 entityTarget 和 variantTarget ID");
        var candidates = node.References.Select(r => (Reference: r, Entity: canvas.FindEntity(r.EntityId)))
            .Where(x => x.Entity is not null && (explicitTarget || x.Entity.Kind == kind))
            .Select(x => (Entity: x.Entity!, Variant: x.Entity!.Variants.FirstOrDefault(v => v.Id == x.Reference.VariantId), Reference: x.Reference))
            .Where(x => x.Variant is not null && (!explicitTarget || x.Entity.Id == Guid.Parse(entityTarget) && x.Variant.Id == Guid.Parse(variantTarget)))
            .ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("设定图输出目标未绑定当前节点或存在歧义，请明确绑定实体与变体");
        if (candidates[0].Reference.VariantVersionId is not null) throw new InvalidDataException("历史版本引用不能作为设定图输出目标，请绑定当前变体");
        return (candidates[0].Entity.Id, candidates[0].Variant!.Id);
    }

    public (long Revision, string Context) AgentContextSnapshot(string? chapterId, string? nodeId, bool requireWritable = false)
    {
        var current = Load();
        if (requireWritable && (current.Open.UnsupportedFormat || current.Open.Validation.HasErrors || current.Open.Migration.Ambiguities.Count > 0))
            throw new InvalidDataException("画布只读或有未解决校验问题");
        ProjectEntityScope.RefreshSnapshots(current.State.Canvas, CheckProjectAuthority(current.State) ?? new ProjectEntityFile());
        var canvas = current.State.Canvas;
        var selected = canvas.Nodes.FirstOrDefault(n => n.Id.ToString() == nodeId);
        if (!string.IsNullOrEmpty(chapterId) && !Guid.TryParse(chapterId, out _)) throw new InvalidDataException("章节ID无效");
        Guid? chapter = Guid.TryParse(chapterId, out var id) ? id : null;
        if (chapter is not null && CanvasChapters.List(canvas).All(c => c.Id != chapter)) throw new InvalidDataException("当前章节不存在");
        if (!string.IsNullOrEmpty(nodeId) && selected is null) throw new InvalidDataException("选中节点不存在");
        var nodes = canvas.Nodes.Where(n => chapter is null || CanvasChapters.ResolveChapterId(canvas, n) == chapter).Take(100).ToList();
        // 目标节点可能被章节筛选排除、或落在 100 条的截断之外——它是这次请求的操作对象，
        // 必须始终出现在清单里：清单之外只留一个标题时，同名节点会让模型挑错人。
        if (selected is not null && nodes.All(n => n.Id != selected.Id)) nodes.Insert(0, selected);
        var context = new AgentContext(selected?.Title ?? "", selected?.Content ?? "",
            string.Join("\n", nodes.Select(n => $"{n.Id} | {n.Category} | {n.Title} | {n.Content} | assetRequirements={JsonSerializer.Serialize(n.AssetRequirements)} | references={JsonSerializer.Serialize(n.References)}")),
            string.Join("\n", canvas.Entities.Select(e => $"{e.Id} | {e.Kind} | {e.Name} | variants=" + string.Join(",", e.Variants.Select(v => $"{v.Id}:v{v.CurrentVersion}:imageAvailable={v.Attachments.Any(a => a.Kind == AttachmentKind.Image && WebGenerationReferences.Locate(a) is not null)}")))),
            "", "", JsonSerializer.Serialize(canvas.WorkTree), TargetIdentity(canvas, selected));
        return (current.Revision, $"当前章节ID：{chapterId ?? "全部"}；基准修订：{current.Revision}\n" + context.Describe(24000, includeDesktopSkills: false) + "\nWeb能力边界：生成技能使用Web聊天提供的invoke_skill协议与服务端预授权；模型不能声称调用了桌面技能或任务已完成。write_file不支持。审批模式只预览，用户提交后才写盘。实体修改受项目资源权威校验约束。");
    }

    /// <summary>
    /// 目标节点的稳定身份：**id 在前**，跟着类别、章节与标题。
    ///
    /// 为什么 id 要写全：模型靠这一段认人，而标题可能重名、还可能被清单截断；
    /// 用完整 Guid（外加 8 位短 id 便于和清单里的短写法对上）才不会挑错节点。
    /// 类别与章节用共享层的同一套读法（NodeAssistPlanner / CanvasChapters），不另立一份。
    /// 没有目标节点（普通聊天）时返回空串，`Describe` 就不会摆出这一段。
    /// </summary>
    private static string TargetIdentity(WorkflowCanvasState canvas, WorkflowNode? node)
    {
        if (node is null) return string.Empty;
        var id = node.Id.ToString("N");
        var chapter = "未归档";
        if (CanvasChapters.ResolveChapterId(canvas, node) is { } chapterId)
            chapter = $"{chapterId:N}（{CanvasChapters.List(canvas).FirstOrDefault(c => c.Id == chapterId)?.Name ?? "未命名"}）";
        return $"{id}（短 {id[..8]}）· {NodeAssistPlanner.KindLabelOf(node.Category)} · 章节 {chapter} · 标题「{node.Title}」";
    }

    public (long Revision, byte[] Before, byte[] After, string?[] Hints, string[] Errors, object Diff) PreviewActions(IReadOnlyList<AgentAction> actions, Func<AgentAction, YEEYEEYEE.Core.ExecutionResult>? attachmentJob = null)
    {
        var current = Load();
        ProjectEntityScope.RefreshSnapshots(current.State.Canvas, CheckProjectAuthority(current.State) ?? new ProjectEntityFile());
        if (current.Open.UnsupportedFormat || current.Open.Validation.HasErrors || current.Open.Migration.Ambiguities.Count > 0)
            throw new InvalidDataException("画布只读或有未解决校验问题");
        if (actions.Count is < 1 or > 100) throw new InvalidDataException("动作数量必须为1至100");
        if (actions.Any(a => a.Kind == "write_file")) throw new InvalidDataException("Web事务不支持write_file，请改为画布动作");
        var canvas = CanvasPreviewBuilder.Clone(current.State.Canvas);
        var ordinary = actions.Where(a => a.Kind != "attach_asset").ToArray();
        var hints = AgentActionExecutor.PrecheckBatch(ordinary, canvas, null, path).ToList();
        var result = AgentActionExecutor.Apply(ordinary, canvas, null, FileWriteMode.Skip, canvasPath: path);
        var errors = result.Errors.ToList();
        foreach (var action in actions.Where(a => a.Kind == "attach_asset"))
        {
            try
            {
                if (!Guid.TryParse(action.Target, out var target) || canvas.Nodes.SingleOrDefault(n => n.Id == target) is not { } node)
                    throw new InvalidDataException("附件目标节点不存在");
                if (node.IsLocked) throw new InvalidDataException("附件目标节点已锁定");
                var job = attachmentJob?.Invoke(action) ?? throw new InvalidDataException("附件缺少可信任务来源");
                if (!job.Inputs.TryGetValue("targetNodeId", out var originalTarget) || originalTarget.GetGuid() != target)
                    throw new InvalidDataException("附件目标与原任务不一致");
                var fingerprints = WebGenerationReferences.Fingerprints(job.Inputs);
                if (!action.Content.StartsWith("asset://", StringComparison.Ordinal) || action.Content != "asset://" + Path.GetFileName(action.Content[8..]))
                    throw new InvalidDataException("附件必须是规范的项目生成资产引用");
                var file = AssetStore.Resolve(action.Content);
                if (file is null || !File.Exists(file) || !InsideAllowedRoots(file)) throw new InvalidDataException("附件资产不可安全读取");
                for (var part = Path.GetFullPath(file); part is not null; part = Path.GetDirectoryName(part))
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("附件路径不能经过符号链接");
                var kind = WorkflowAttachment.KindOf(file);
                if (kind is not (AttachmentKind.Image or AttachmentKind.Video)) throw new InvalidDataException("附件仅支持图片或视频");
                var id = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"web-chat:{action.Source}:{action.Content}"))[..16]);
                if (!node.Attachments.Any(a => a.Id == id))
                    node.Attachments.Add(new WorkflowAttachment { Id = id, Kind = kind, Reference = action.Content, Name = action.Title, Source = "web-chat", SourceFingerprints = new(fingerprints, StringComparer.Ordinal) });
                if (!string.IsNullOrEmpty(action.EntityTarget) || !string.IsNullOrEmpty(action.VariantTarget))
                {
                    if (kind != AttachmentKind.Image) throw new InvalidDataException("实体图槽仅接受图片");
                    var output = OutputTarget(canvas, node, action.EntityTarget, action.VariantTarget)!.Value;
                    var variant = canvas.FindEntity(output.Entity)!.Variants.Single(v => v.Id == output.Variant);
                    variant.EnsureInitialVersion();
                    if (!variant.Attachments.Any(a => a.Id == id))
                    {
                        variant.Attachments.Insert(0, new WorkflowAttachment { Id = id, Kind = kind, Reference = action.Content, Name = action.Title, Source = "web-chat", SourceFingerprints = new(fingerprints, StringComparer.Ordinal) });
                        variant.Commit("Web Agent 生成设定图");
                    }
                    hints.Add("将图片写入实体变体并提交新版本");
                }
                node.ExecutionStatus = NodeExecutionStatus.Completed;
                hints.Add("将生成媒体回挂到节点：" + node.Title);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            { errors.Add(ex.Message); hints.Add(ex.Message); }
        }
        var next = current.State with { Canvas = canvas };
        if (CanvasIdentityValidator.Validate(canvas).HasErrors) errors.Add("预览画布引用或身份校验失败");
        try { CheckProjectAuthority(next); }
        catch (InvalidDataException) { errors.Add("动作违反项目资源库权威约束"); }
        var comparison = CanvasPreviewBuilder.Compare(current.State.Canvas, canvas);
        static List<object> Nodes(WorkflowCanvasState state, IEnumerable<Guid> ids)
        {
            var selected = ids.Select(id => id.ToString()).ToHashSet();
            return NodeProjection.ProjectRecords(state.Nodes, state)
                .Where(record => selected.Contains(JsonSerializer.SerializeToElement(record).GetProperty("recordId").GetString()!)).ToList();
        }
        static List<object> Edges(WorkflowCanvasState state, IEnumerable<Guid> ids)
        {
            var selected = ids.Select(id => id.ToString()).ToHashSet();
            return ProjectEdges(state)
                .Where(edge => selected.Contains(JsonSerializer.SerializeToElement(edge).GetProperty("edgeId").GetString()!)).ToList();
        }
        var diff = new
        {
            addedNodes = Nodes(canvas, comparison.AddedNodes.Select(node => node.Id)),
            updatedNodes = Nodes(canvas, comparison.UpdatedNodeIds),
            removedNodes = Nodes(current.State.Canvas, comparison.RemovedNodeIds),
            addedEdges = Edges(canvas, comparison.AddedEdges.Select(edge => edge.Id)),
            removedEdges = Edges(current.State.Canvas, comparison.RemovedEdgeIds)
        };
        return (current.Revision, JsonSerializer.SerializeToUtf8Bytes(current.State), JsonSerializer.SerializeToUtf8Bytes(next), hints.ToArray(), errors.ToArray(), diff);
    }

    public IResult Read()
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            return Results.Json(Project(path, current.State, current.Revision, current.Open));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
    }

    public IResult RegisterAttachment(long baseRevision, Guid nodeId, Guid attachmentId, Guid entityId, Guid variantId) =>
        Write(baseRevision, current =>
        {
            var canvas = current.Canvas;
            var node = canvas.Nodes.SingleOrDefault(n => n.Id == nodeId);
            if (node is null) return (null, Error(404, "RECORD_NOT_FOUND", "节点不存在"));
            if (node.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));
            var entity = canvas.FindEntity(entityId);
            if (entity is null || entity.ManagedByProject)
                return (null, Error(409, "CANVAS_LOCAL_ENTITY_REQUIRED", "请选择当前画布的内嵌实体"));
            var variant = entity.Variants.SingleOrDefault(v => v.Id == variantId);
            if (variant is null) return (null, Error(404, "CANVAS_VARIANT_NOT_FOUND", "变体不存在"));
            if (!node.References.Any(r => r.EntityId == entityId && r.VariantId == variantId && r.VariantVersionId is null))
                return (null, Error(409, "OUTPUT_TARGET_UNBOUND", "请先把实体的当前变体明确绑定到节点"));
            var attachment = node.Attachments.SingleOrDefault(a => a.Id == attachmentId);
            if (attachment is null || attachment.Kind != AttachmentKind.Image)
                return (null, Error(404, "ASSET_ATTACHMENT_NOT_FOUND", "节点图片附件不存在"));
            var file = WebGenerationReferences.Locate(attachment);
            if (file is null) return (null, Error(409, "ASSET_UNAVAILABLE", "图片资产不可安全读取"));
            using (var stream = File.OpenRead(file))
            {
                var signature = new byte[Math.Min(32L, stream.Length)];
                stream.ReadExactly(signature);
                var type = AgentAttachmentLoader.ImageMediaType(file);
                if (type == "image/jpeg" && signature.Length >= 4)
                {
                    stream.Seek(-2, SeekOrigin.End);
                    stream.ReadExactly(signature.AsSpan(signature.Length - 2));
                }
                if (type is null || !WebEntityApi.ValidMedia(signature, type))
                    return (null, Error(415, "ASSET_SIGNATURE_INVALID", "图片内容与声明格式不符"));
            }
            if (variant.Attachments.Any(a => a.Id == attachmentId || a.Reference == attachment.Reference))
                return (null, Results.Json(new { revision = baseRevision, registered = false, version = variant.CurrentVersion }));
            variant.EnsureInitialVersion();
            variant.Attachments.Insert(0, JsonSerializer.Deserialize<WorkflowAttachment>(JsonSerializer.Serialize(attachment))!);
            variant.Commit("Web 登记节点图片附件");
            return (canvas, null);
        }, (next, bytes) => new { revision = Revision(bytes), registered = true,
            version = next.Canvas.FindEntity(entityId)!.Variants.Single(v => v.Id == variantId).CurrentVersion });

    public IResult RegistrationTargets(Guid nodeId)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var node = current.State.Canvas.Nodes.SingleOrDefault(n => n.Id == nodeId);
            if (node is null) return Error(404, "RECORD_NOT_FOUND", "节点不存在");
            var targets = current.State.Canvas.Entities.Where(e => !e.ManagedByProject)
                .SelectMany(e => e.Variants.Select(v => new { entityId = e.Id, variantId = v.Id,
                    name = e.Name, variantName = v.Name, version = v.CurrentVersion,
                    bound = node.References.Any(r => r.EntityId == e.Id && r.VariantId == v.Id && r.VariantVersionId is null) }));
            return Results.Json(new { revision = current.Revision, targets });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException)
        { return Error(503, "PROJECT_CANVAS_READ_FAILED", "画布无法安全读取"); }
    }

    /// <summary>
    /// 给一个节点**挂上一条引用**（网页端从项目库里挑一条设定挂过去；桌面端对应的是节点编辑器里的「加引用」）。
    ///
    /// 与「改标题内容」同一档：**记录级**。引用是节点内容的一部分，不动画布结构——按结构级对待的话，
    /// 挂一条设定就得先占住整棵树锁，那与「挪一下卡片挡住别人的结构改动」是同一个毛病。
    ///
    /// 服务端把关三件：
    /// ① 实体 / 变体 / 版本**必须真的在这张画布里**——写一条对不上的引用等于塞一个悬空引用
    ///    （画布校验随后会判错，但那时文件已经写坏了，所以要在写之前拦）；
    /// ② 同一条设定不许挂两遍——那只会变成两枚一模一样的徽标；
    /// ③ 节点没被锁定。
    /// **没给 variantId 就用这个实体的第一个变体；没给 versionId 就是「跟着当前版走」**（null 的含义），
    /// 这两条与共享的 <see cref="WorkflowCanvasState.ResolveReferenceContent"/> 是同一套规矩，不另立一套。
    /// </summary>
    public IResult AddReference(long baseRevision, Guid nodeId, Guid entityId, Guid? variantId, Guid? versionId)
    {
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                var node = canvas.Nodes.SingleOrDefault(item => item.Id == nodeId);
                if (node is null) return (null, Error(404, "RECORD_NOT_FOUND", "该记录不是可编辑节点"));
                if (node.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));

                // 库里有、这张画布还没有的设定，**按引用把它补进画布**——与桌面端
                // ProjectEntityScope.MergeInto 的注入规则是同一条（那边注入的是「被节点引用到的」库实体）。
                //
                // 为什么这里必须认库：网页端「项目资源库」面板列的就是项目库（/api/web/assets 直接读
                // entities.json），选中即写。若只认画布里的快照，就会出现「列表里能选到、一选必然
                // 404 CANVAS_ENTITY_NOT_FOUND」——资源新建完就再也挂不上去，用户以为功能坏了。
                var entity = canvas.FindEntity(entityId);
                if (entity is null)
                {
                    var library = CheckProjectAuthority(current) ?? throw new InvalidDataException("资源库缺失");
                    if (library.Entities.FirstOrDefault(candidate => candidate.Id == entityId) is { } authoritative)
                    {
                        var snapshot = ProjectEntityScope.CloneEntity(authoritative);
                        // 画布里只放**托管快照**：权威内容始终在项目库，画布这份供渲染与解析用。
                        snapshot.ManagedByProject = true;
                        snapshot.ProjectMissingReason = null;
                        canvas.Entities.Add(snapshot);
                        entity = snapshot;
                    }
                }
                if (entity is null) return (null, Error(404, "CANVAS_ENTITY_NOT_FOUND", "这条设定不在这张画布里"));
                var variant = variantId is { } wanted
                    ? entity.Variants.FirstOrDefault(item => item.Id == wanted)
                    : entity.Variants.FirstOrDefault();
                if (variant is null)
                    return (null, Error(404, "CANVAS_VARIANT_NOT_FOUND",
                        variantId is null ? "这条设定还没有变体" : "这个变体不在这条设定里"));
                if (versionId is { } version && variant.FindVersion(version) is null)
                    return (null, Error(404, "CANVAS_VERSION_NOT_FOUND", "这个版本不在这个变体里"));
                if (node.References.Any(reference => reference.EntityId == entityId &&
                        reference.VariantId == variant.Id && reference.VariantVersionId == versionId))
                    return (null, Error(409, "REFERENCE_DUPLICATE", "这个节点已经引用过这一条设定了"));

                // X / Y 留默认的 -1：那是「还没摆过」，位置交给共享的引用布局去算，不在这里猜。
                node.References.Add(new NodeReference
                {
                    EntityId = entityId,
                    VariantId = variant.Id,
                    VariantVersionId = versionId
                });
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), record = ProjectRecord(next.Canvas, nodeId.ToString()) });
    }

    /// <summary>
    /// 只读：一个节点的「协助计划」——网页端节点右键那份菜单的内容，与桌面端右键菜单**同一份**来源。
    ///
    /// 建议**不是写死的**：由共享的 <see cref="NodeAssistPlanner"/> 沿入边收集上游设定、再按节点类型
    /// 算出来。这里只做投影，所以不要锁、不写盘、任何人（含只读账号）都能取——「这个节点能做什么」
    /// 是看一眼就知道的事，不该因为别人正占着编辑锁就问不出来。
    /// </summary>
    public IResult Assist(string recordId, bool videoAvailable = false)
    {
        if (!Guid.TryParse(recordId, out var nodeId))
            return Error(400, "CANVAS_RECORD_INVALID", "记录 ID 不是合法的 GUID");
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var node = current.State.Canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
            if (node is null) return Error(404, "CANVAS_RECORD_NOT_FOUND", "这条记录不在这张画布里");

            // 「有没有素材」由共享的计划算；「这一端有没有出视频的链路」必须由这一端告诉它——
            // 桌面端传的是它自己的视频 provider 是否配好，网页端传的是网页视频接口/ComfyUI 是否配好。
            // 不传（一直是 false）的话，分镜那条「出视频」永远会带着「还没有可用的出视频链路」被挡掉。
            var plan = NodeAssistPlanner.BuildPlan(current.State.Canvas, node, videoAvailable: videoAvailable);
            return Results.Json(new
            {
                recordId = nodeId.ToString(),
                summary = plan.ContextSummary,
                hasUpstream = plan.HasUpstream,
                suggestions = plan.Suggestions.Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    // 动作的**种类**由服务端说（两端共用同一个枚举），至于「这一端能不能跑」由客户端定：
                    // 网页端现在只有一条出图技能，Agent / 视频两条执行方还没接。
                    kind = item.Kind.ToString(),
                    skillId = item.SkillId,
                    prompt = item.Prompt,
                    negativePrompt = item.NegativePrompt,
                    blocked = item.Blocked,
                    canRun = item.CanRun
                })
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
    }

    /// <summary>
    /// 只读：一条设定的**预览图字节**（网页端画布上那些引用徽标要显示它）。
    ///
    /// 取哪一张图不由这里定：问 <see cref="EntityAssets.PreviewImage"/>——与节点投影里的 <c>thumbnailRef</c>
    /// 是同一个方法，所以「网页端显示的缩略图」与「服务端说是哪张图」不会各说各话。
    ///
    /// 引用的形式有两种，都要认：项目内的**文件**（走 <see cref="AssetStore.Resolve"/>）与内嵌的 **data URL**
    /// （出图链路有时把图直接写进画布）。两种都从这条接口出去，客户端不必分情况。
    /// </summary>
    public IResult EntityThumbnail(Guid entityId, string? variantId, string? versionId)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var canvas = current.State.Canvas;

            var entity = canvas.FindEntity(entityId);
            if (entity is null) return Error(404, "CANVAS_ENTITY_NOT_FOUND", "这条设定不在这张画布里");
            var variant = (Guid.TryParse(variantId, out var parsedVariant)
                    ? entity.Variants.FirstOrDefault(item => item.Id == parsedVariant)
                    : null)
                ?? entity.Variants.FirstOrDefault();
            if (variant is null) return Error(404, "CANVAS_VARIANT_NOT_FOUND", "这个变体不在这条设定里");

            // 「拿哪一版」的规矩交给共享的解析器（没给版本号就是当前版），不在这里再判一遍。
            var content = canvas.ResolveReferenceContent(new NodeReference
            {
                EntityId = entityId,
                VariantId = variant.Id,
                VariantVersionId = Guid.TryParse(versionId, out var parsedVersion) ? parsedVersion : null
            });
            if (content is null)
                return Error(404, "CANVAS_REFERENCE_UNRESOLVED", "这条引用解不开：设定 / 变体 / 版本对不上");

            var attachment = EntityAssets.PreviewImage(content);
            if (attachment is null) return Error(404, "ASSET_NO_IMAGE", "这条设定还没有图片");
            return AttachmentBytes(attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
    }

    /// <summary>
    /// 只读：一条记录上**某个产物**的字节（检查器里那些产物图）。
    ///
    /// 与设定缩略图走**同一条**出字节的路（<see cref="AttachmentBytes"/>）：内嵌 data URL 与项目内文件
    /// 两种形式、以及那道「只服务项目内文件」的闸，只写一遍——两处各写一份，安全闸迟早会走散。
    /// </summary>
    public IResult AttachmentFile(Guid recordId, Guid attachmentId)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var node = current.State.Canvas.Nodes.FirstOrDefault(item => item.Id == recordId);
            if (node is null) return Error(404, "RECORD_NOT_FOUND", "这条记录不在这张画布里");
            var attachment = node.Attachments.FirstOrDefault(item => item.Id == attachmentId);
            if (attachment is null) return Error(404, "ASSET_ATTACHMENT_NOT_FOUND", "这个产物不在这条记录上");
            if (attachment.Kind is not (AttachmentKind.Image or AttachmentKind.Video or AttachmentKind.Audio))
                return Error(409, "ASSET_NOT_MEDIA", "这个产物不是可预览的图片、视频或音频");
            return AttachmentBytes(attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
    }

    /// <summary>
    /// 把一个附件引用变成字节：**两种形式在这里收口**——内嵌的 data URL，与项目内的文件。
    /// 文件那一路带着「只服务允许目录」的闸，见 <see cref="InsideAllowedRoots"/>。
    /// </summary>
    public IResult EntityAttachmentFile(Guid entityId, Guid variantId, Guid attachmentId, Guid? versionId)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var library = JsonSerializer.Deserialize<ProjectEntityFile>(File.ReadAllBytes(entitiesPath), JsonOptions);
            var variant = library?.Entities.FirstOrDefault(e => e.Id == entityId)?.Variants.FirstOrDefault(v => v.Id == variantId);
            var attachments = versionId is { } id ? variant?.FindVersion(id)?.Attachments : variant?.Attachments;
            var attachment = attachments?.FirstOrDefault(a => a.Id == attachmentId);
            return attachment is null ? Error(404, "ASSET_ATTACHMENT_NOT_FOUND", "媒体附件不存在") : AttachmentBytes(attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Error(503, "LIBRARY_UNAVAILABLE", "资源库无法安全读取"); }
    }

    /// <summary>
    /// 只读：**待审批预览**里某个产物的字节。
    ///
    /// 待提交的产物还没写进正式节点，<see cref="AttachmentFile"/> 那条按正式画布找的接口自然读不到它。
    /// 这里不另建一套试算：调用方给的这批动作走的就是 <see cref="PreviewActions"/>（同一条预检、
    /// 同一个附件任务校验、同一次 FileWriteMode.Skip 的试算），再从**试算结果**里取那一件产物——
    /// 于是「这个 ID 属不属于这批预览」由服务端自己说了算，调用方猜不出来。
    /// 出字节仍然只有 <see cref="AttachmentBytes"/> 一条路（内嵌 data URL 与允许目录两道闸都在那里），
    /// 没有为预览放宽任何校验。
    /// </summary>
    public IResult PreviewAttachmentFile(IReadOnlyList<AgentAction> actions, Func<AgentAction, YEEYEEYEE.Core.ExecutionResult>? attachmentJob, Guid recordId, Guid attachmentId)
    {
        try
        {
            if (actions.Count == 0) return Error(409, "ACTION_PREVIEW_REQUIRED", "这批动作已经不在预览状态");
            var preview = PreviewActions(actions, attachmentJob);
            if (preview.Errors.Length > 0) return Error(409, "ACTION_PRECHECK_FAILED", string.Join("；", preview.Errors));
            if (!CanvasOpenService.TryOpenBytes(preview.After, path, out var opened, out var reason) || opened.State.Canvas is null)
                return Error(503, "PREVIEW_CANVAS_INVALID", "预览画布无法读取：" + reason);
            var node = opened.State.Canvas.Nodes.FirstOrDefault(item => item.Id == recordId);
            var attachment = node?.Attachments.FirstOrDefault(item => item.Id == attachmentId);
            if (attachment is null) return Error(404, "ASSET_ATTACHMENT_NOT_FOUND", "这个产物不属于本次待审批的改动");
            if (attachment.Kind is not (AttachmentKind.Image or AttachmentKind.Video or AttachmentKind.Audio))
                return Error(409, "ASSET_NOT_MEDIA", "这个产物不是可预览的图片、视频或音频");
            return AttachmentBytes(attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
    }

    private IResult AttachmentBytes(WorkflowAttachment attachment)
    {
        if (AgentAttachmentLoader.SplitDataUrl(attachment.Reference) is { } inline)
        {
            try
            {
                var bytes = Convert.FromBase64String(inline.Data);
                return WebEntityApi.ValidMedia(bytes, inline.MediaType) ? Results.Bytes(bytes, inline.MediaType)
                    : Error(415, "ASSET_SIGNATURE_INVALID", "媒体内容与声明格式不符");
            }
            catch (FormatException) { return Error(500, "ASSET_INLINE_INVALID", "内嵌图片的 base64 解不开"); }
        }

        var file = AssetStore.Resolve(attachment.Reference);
        if (file is null)
            return Error(404, "ASSET_FILE_MISSING",
                "图片文件不在这个部署里：容器要把共享素材目录（ASSET_DIR）或项目目录挂进来");
        if (!InsideAllowedRoots(file))
            return Error(403, "ASSET_OUTSIDE_PROJECT",
                "这张图不在允许的目录里（共享素材目录或项目目录），拒绝服务");
        var mediaType = AgentAttachmentLoader.ImageMediaType(file) ?? Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
        using (var stream = File.OpenRead(file))
        {
            var signature = new byte[Math.Min(32L, stream.Length)];
            stream.ReadExactly(signature);
            if (mediaType == "image/jpeg" && signature.Length >= 4)
            {
                stream.Seek(-2, SeekOrigin.End);
                stream.ReadExactly(signature.AsSpan(signature.Length - 2));
            }
            if (!WebEntityApi.ValidMedia(signature, mediaType))
                return Error(415, "ASSET_SIGNATURE_INVALID", "媒体内容与声明格式不符");
        }
        return Results.File(file, mediaType, enableRangeProcessing: true);
    }

    /// <summary>
    /// 只服务**项目内**的文件。设定里的附件引用可以是绝对路径（老数据就是这么写的），
    /// 不加这道闸，一个被诱导出来的引用就能把机器上任何一张图片读出去。
    /// 允许两个根：共享素材目录（<see cref="AssetStore.Directory"/>，容器里由 ASSET_DIR 指到挂载点）
    /// 与**画布所在的项目目录**。
    /// </summary>
    private bool InsideAllowedRoots(string file)
    {
        var canvasDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var roots = new[]
        {
            AssetStore.Directory,
            Directory.GetParent(canvasDirectory)?.FullName ?? canvasDirectory
        };
        var full = Path.GetFullPath(file);
        return roots.Any(root => !string.IsNullOrEmpty(root) && full.StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 所有写入的唯一通道：修订校验 → 只读与校验闸门 → 项目库权威校验 → 变更 → 再校验 →
    /// 原子落盘（带备份与跨进程租约）。「改标题内容」与「整理布局」共用它——
    /// 两套写入规则各自演化出差异，是这类接口最典型的坏法。
    ///
    /// <paramref name="mutate"/> 返回 <c>(null, 结果)</c> 就立刻把那个结果回给调用方：
    /// 错误与「本来就无需改动」都走这条路，所以它是「结果」而不是「拒绝」。
    /// </summary>
    private IResult Write(
        long baseRevision,
        Func<RecentCanvasState, (WorkflowCanvasState? Canvas, IResult? Result)> mutate,
        Func<RecentCanvasState, byte[], object> respond,
        Action? onCommitted = null,
        bool requireBaseRevision = true)
    {
        var committed = false;
        try
        {
            var current = Load();
            if (requireBaseRevision && current.Revision != baseRevision) return Error(409, "SCENE_REVISION_CONFLICT", "项目画布已被修改，请重新加载");
            if (current.Open.UnsupportedFormat) return Error(409, "CANVAS_READ_ONLY", "高版本画布只读，拒绝覆盖");
            if (current.Open.Validation.HasErrors || current.Open.Migration.Ambiguities.Count > 0)
                return Error(409, "CANVAS_VALIDATION_FAILED", "画布引用或迁移存在未解决问题，拒绝保存");
            if (current.State.Revision == int.MaxValue) return Error(409, "SCENE_REVISION_CONFLICT", "画布修订已达上限");
            CheckProjectAuthority(current.State);

            var (canvas, result) = mutate(current.State);
            if (result is not null) return result;
            if (canvas is null) return Error(500, "CANVAS_INVALID", "变更没有产出可写入的画布");

            var next = current.State with { Revision = current.State.Revision + 1, Canvas = canvas };
            var validation = CanvasIdentityValidator.Validate(next.Canvas);
            if (validation.HasErrors) return Error(409, "CANVAS_VALIDATION_FAILED", "修改后画布校验失败");
            CanvasSaveOutcome saved;
            try
            {
                // 保存服务会在跨进程画布租约下校验期望字节并重新验证项目资源库，
                // 然后执行备份和原子替换。
                saved = CanvasSaveService.Save(next, path, current.Bytes, () => CheckProjectAuthority(next));
            }
            catch (CanvasSaveAbortedException ex) when (ex.Message.Contains("其他进程", StringComparison.Ordinal))
            { return Error(409, "SCENE_REVISION_CONFLICT", ex.Message); }
            committed = true;
            onCommitted?.Invoke();
            // 只在这里记：**落盘之后**才知道这次提交算数。调用方拿序号前后比一次就知道写没写；
            // 修订号取的是刚落盘那份字节的哈希，与 respond 回给客户端的 `revision` 是同一个数——
            // 客户端就是拿它和自己手上的比「谁更新」的，两边必须是同一把尺子。
            LastCommit = (LastCommit.Serial + 1, Revision(saved.WrittenBytes));
            // 提交后不再重新打开文件：其它写入者可能已替换它。
            // 成功替换后返回失败会诱发不安全的重复提交。
            return Results.Json(respond(next, saved.WrittenBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, committed ? "PROJECT_CANVAS_COMMITTED" : "PROJECT_CANVAS_WRITE_FAILED",
                committed ? "项目画布已保存，但无法确认提交后的读取：" + ex.Message : "项目画布未安全保存：" + ex.Message);
        }
    }

    private string? LocateMedia(string reference)
    {
        var file = AssetStore.Resolve(reference);
        return file is not null && InsideAllowedRoots(file) ? file : null;
    }

    /// <summary>
    /// 与 <see cref="Write"/> 同一套闸门、同一次原子落盘，只去掉「修订必须等于 baseRevision」这一条。
    ///
    /// 用在哪：**生成产物回挂**。任务提交时记下的 revision 与回挂时的当前版本几乎必然不同——
    /// 同一批里第一个任务回挂成功就改了它，于是第二个任务按旧修订写必然冲突。
    /// 安全由两层兜住：一是这里读的是**刚读到的当前快照**（不是提交时那份），
    /// 二是落盘时 <c>CanvasSaveService.Save</c> 会校验期望字节——读与写之间有人动过这整笔就作废
    /// （调用方拿到 409，下一轮重新读、重新追加）。所以这不是「不校验」，
    /// 而是把判据从「修订相等」换成「我改的正是我刚读的那一份」——后者才真正回答并发问题。
    /// </summary>
    private IResult AppendToCurrent(
        Func<RecentCanvasState, (WorkflowCanvasState? Canvas, IResult? Result)> mutate,
        Func<RecentCanvasState, byte[], object> respond) => Write(0, mutate, respond, null, requireBaseRevision: false);

    public IResult FilmPlan(Guid nodeId)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var product = current.State.Canvas.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Category == NodeCategory.Product);
            if (product is null) return Error(404, "PRODUCT_NOT_FOUND", "成品节点不存在");
            var plan = ProductVideoAssembly.Plan(current.State.Canvas, product, LocateMedia);
            var ledger = ProductVersions.For(current.State.Canvas, product, LocateMedia);
            var films = product.Attachments.Where(a => a.Source == WorkflowAttachment.SourceFilmJoin && a.Kind == AttachmentKind.Video).ToList();
            return Results.Json(new { revision = current.Revision, plan.CanConcat, plan.Note, plan.MissingTitles,
                shots = plan.Shots.Select(s => new { s.Index, s.NodeId, s.Title }),
                versions = ledger.Versions.Select((v, i) => new { v.Index, v.Label, v.FileName, v.AddedAt, v.IsLatest, v.Shots, v.FactsText,
                    url = $"/api/web/records/{nodeId}/attachments/{films[i].Id}/file" }), ledger.LatestChanges });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Error(503, "FILM_READ_FAILED", "成片计划无法读取"); }
    }

    public IResult ConcatFilm(long baseRevision, Guid nodeId, IReadOnlyList<Guid> selectedShots)
    {
        string? output = null;
        var committed = false;
        try
        {
        return Write(baseRevision, current =>
        {
            var canvas = current.Canvas;
            var product = canvas.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Category == NodeCategory.Product);
            if (product is null) return (null, Error(404, "PRODUCT_NOT_FOUND", "成品节点不存在"));
            if (product.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));
            var plan = ProductVideoAssembly.Plan(canvas, product, LocateMedia);
            if (selectedShots.Count < 2 || selectedShots.Distinct().Count() != selectedShots.Count ||
                selectedShots.Any(id => plan.Shots.All(s => s.NodeId != id)))
                return (null, Error(400, "FILM_SELECTION_INVALID", "请选择至少两镜已有视频的分镜"));
            var shots = plan.Shots.Where(s => selectedShots.Contains(s.NodeId)).ToList();
            output = Path.Combine(AssetStore.EnsureDirectory(), $"film-{Guid.NewGuid():N}.mp4");
            var result = Mp4Concatenator.Concat(shots.Select(s => s.Path).ToList(), output);
            if (!result.Ok) return (null, Error(409, "FILM_CONCAT_FAILED", result.Error));
            product.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Video, Reference = AssetStore.ToReference(result.FilePath),
                Name = product.Title + "-成片.mp4", Source = WorkflowAttachment.SourceFilmJoin,
                ShotList = string.Join("、", shots.Select(s => s.Title)), ShotRefs = string.Join("、", shots.Select(s => s.NodeId)) });
            product.ExecutionStatus = NodeExecutionStatus.Completed;
            return (canvas, null);
        }, (next, bytes) => new { revision = Revision(bytes), record = ProjectRecord(next.Canvas, nodeId.ToString()) }, () => committed = true);
        }
        finally
        {
            if (!committed && output is not null)
            {
                try { File.Delete(output); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public IResult AttachJobOutputs(YEEYEEYEE.Core.ExecutionResult job, Guid nodeId)
    {
        if (!job.Inputs.TryGetValue("targetNodeId", out var originalTarget) || originalTarget.GetGuid() != nodeId)
            return Error(409, "TARGET_MISMATCH", "回挂目标与原任务不一致");
        var fingerprints = WebGenerationReferences.Fingerprints(job.Inputs);
        // 提交时那一版修订仍要记着（它是任务依据的一部分，也用来判断任务是不是同一批），
        // 但**不再要求它与回挂时的当前版本相等**：多个任务从同一版提交时，
        // 前一个的回挂必然把修订改掉（见 AppendToCurrent）。并发安全改由字节级 CAS 兜住。
        if (!job.Inputs.TryGetValue("targetBaseRevision", out var baseline) || !baseline.TryGetInt64(out _))
            return Error(409, "TARGET_REVISION_REQUIRED", "任务缺少提交时修订，产物保留在任务中");
        var snapshot = Load();
        var existingNode = snapshot.State.Canvas.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (existingNode is not null && job.Outputs.Count > 0 && job.Outputs.Select((_, index) =>
                new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"web-job:{job.JobId}:{index}"))[..16]))
            .All(id => existingNode.Attachments.Any(a => a.Id == id)))
            return Results.Json(new { attached = true });
        return AppendToCurrent(current =>
        {
            var node = current.Canvas.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null) return (null, Error(404, "TARGET_NOT_FOUND", "任务目标节点已删除"));
            if (node.IsLocked) return (null, Error(409, "TARGET_LOCKED", "任务目标节点已锁定"));
            var added = false;
            for (var index = 0; index < job.Outputs.Count; index++)
            {
                var output = job.Outputs[index];
                var attachmentId = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"web-job:{job.JobId}:{index}"))[..16]);
                if (node.Attachments.Any(a => a.Id == attachmentId)) continue;
                var file = AssetStore.Resolve(output.Ref);
                if (file is null || !InsideAllowedRoots(file)) return (null, Error(409, "OUTPUT_UNAVAILABLE", "成功产物文件不可安全读取"));
                var kind = WorkflowAttachment.KindOf(file);
                if (kind is not (AttachmentKind.Image or AttachmentKind.Video)) return (null, Error(409, "OUTPUT_UNSUPPORTED", "产物不是支持的图片或视频"));
                node.Attachments.Add(new WorkflowAttachment
                {
                    Id = attachmentId, Kind = kind, Reference = output.Ref,
                    Name = $"{WorkflowAttachment.DisplayName(kind)} · {job.JobId.ToString()[..8]} · {index + 1}",
                    Source = job.Tool, Prompt = job.Inputs.TryGetValue("prompt", out var prompt) ? prompt.GetString() ?? "" : "",
                    SourceFingerprints = new(fingerprints, StringComparer.Ordinal)
                });
                added = true;
            }
            if (!added) return (null, Results.Json(new { attached = true }));
            node.ExecutionStatus = NodeExecutionStatus.Completed;
            return (current.Canvas, null);
        }, (_, bytes) => new { attached = true, revision = Revision(bytes) });
    }

    /// <summary>
    /// 采用一件候选产物：把**这一件**（按任务产物自己的下标）安全追加到原目标节点。
    ///
    /// 为什么按「原始下标」而不是整批一起挂：用户采用的是画布上那**一张**（Desktop 的 SavePickedSlot
    /// 同样只收一张）。而附件 ID 是服务端按 `web-job:{jobId}:{index}` 算出来的——下标必须还是任务里
    /// 那个原始下标，换个下标就等于换了一件产物，幂等与「重复采用不重复添加」都会失效。
    ///
    /// 与回挂同一条写入通道（<see cref="AppendToCurrent"/>）：读的是当前快照、落盘仍是字节级 CAS，
    /// 所以别的画布改动不会让它必然失败，也不会覆盖别人刚写进去的附件；目标不存在/锁定、产物读不到
    /// 或不是图片时整笔拒绝，画布一个字节不动。
    /// </summary>
    public IResult AdoptJobOutput(YEEYEEYEE.Core.ExecutionResult job, Guid nodeId, int outputIndex)
    {
        if (!job.Inputs.TryGetValue("targetNodeId", out var originalTarget) || originalTarget.GetGuid() != nodeId)
            return Error(409, "TARGET_MISMATCH", "采用目标与原任务不一致");
        if (outputIndex < 0 || outputIndex >= job.Outputs.Count)
            return Error(404, "OUTPUT_NOT_FOUND", "任务产物不存在");
        var fingerprints = WebGenerationReferences.Fingerprints(job.Inputs);
        var attachmentId = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"web-job:{job.JobId}:{outputIndex}"))[..16]);
        var snapshot = Load();
        var existing = snapshot.State.Canvas.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (existing is not null && existing.Attachments.Any(a => a.Id == attachmentId))
            return Results.Json(new { attached = true, alreadyAttached = true, attachmentId });
        return AppendToCurrent(current =>
        {
            var node = current.Canvas.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null) return (null, Error(404, "TARGET_NOT_FOUND", "任务目标节点已删除"));
            if (node.IsLocked) return (null, Error(409, "TARGET_LOCKED", "任务目标节点已锁定"));
            // 采用是幂等的：这一件已经在节点上就什么都不做，也不重复写盘。
            if (node.Attachments.Any(a => a.Id == attachmentId))
                return (null, Results.Json(new { attached = true, alreadyAttached = true, attachmentId }));
            var output = job.Outputs[outputIndex];
            var file = AssetStore.Resolve(output.Ref);
            if (file is null || !InsideAllowedRoots(file))
                return (null, Error(409, "OUTPUT_UNAVAILABLE", "产物文件不可安全读取"));
            var kind = WorkflowAttachment.KindOf(file);
            if (kind is not AttachmentKind.Image)
                return (null, Error(409, "OUTPUT_UNSUPPORTED", "候选产物不是图片"));
            node.Attachments.Add(new WorkflowAttachment
            {
                Id = attachmentId, Kind = kind, Reference = output.Ref,
                Name = $"{WorkflowAttachment.DisplayName(kind)} · {job.JobId.ToString()[..8]} · {outputIndex + 1}",
                Source = job.Tool, Prompt = job.Inputs.TryGetValue("prompt", out var prompt) ? prompt.GetString() ?? "" : "",
                SourceFingerprints = new(fingerprints, StringComparer.Ordinal)
            });
            node.ExecutionStatus = NodeExecutionStatus.Completed;
            return (current.Canvas, null);
        }, (_, bytes) => new { attached = true, alreadyAttached = false, attachmentId, revision = Revision(bytes) });
    }

    public async Task<IResult> Update(string recordId, HttpRequest request)
    {
        JsonElement input;
        try { input = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: request.HttpContext.RequestAborted); }
        catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("baseRevision", out var rev) || !rev.TryGetInt64(out var baseRevision) ||
            !input.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            !input.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            return Error(400, "INVALID_REQUEST", "需要 baseRevision、title、content");

        return Write(baseRevision, current =>
        {
            var canvas = current.Canvas;
            var projected = NodeProjection.ProjectRecords(canvas.Nodes, canvas);
            if (!Guid.TryParse(recordId, out var id) ||
                !projected.Any(row => JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString() == recordId))
                return (null, Error(404, "RECORD_NOT_FOUND", "可编辑节点不存在"));
            var node = canvas.Nodes.SingleOrDefault(item => item.Id == id);
            if (node is null) return (null, Error(404, "RECORD_NOT_FOUND", "该记录不是可编辑节点"));
            if (node.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));
            node.Title = title.GetString()!;
            node.Content = content.GetString()!;
            return (canvas, null);
        }, (next, bytes) => new { revision = Revision(bytes), record = ProjectRecord(next.Canvas, recordId) });
    }

    /// <summary>
    /// 投影一条记录——写入响应里回给客户端的那一份。取不到就抛：调用方刚刚才确认过它存在，
    /// 到这儿还取不到说明投影规则和存在性判断用的不是同一套，那正是要立刻炸出来的事。
    /// </summary>
    private static object ProjectRecord(WorkflowCanvasState canvas, string recordId) =>
        NodeProjection.ProjectRecords(canvas.Nodes, canvas)
            .First(row => JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString() == recordId);

    /// <summary>
    /// 移动一个节点（网页端在画布上把它拖到别处）。
    ///
    /// 这是**记录级**写入，不是结构级：它不动画布结构（不增删任何节点 / 连线、不改父子关系与章节归属），
    /// 画布校验也从不看坐标。要是按结构级对待，一个人挪一下卡片就得先占住整棵树锁，
    /// 别人的结构改动会被一次无关紧要的拖动挡在门外。
    ///
    /// 落点由**客户端**给——这是拖动，坐标本来就是人摆的（与「新建节点」那条相反：那条不让客户端送坐标，
    /// 因为落位该由服务端一个人说了算）。服务端只把关三件事：坐标得是有限数、
    /// 节点得真的在（且没被锁），以及给节点标上 <see cref="WorkflowNode.ManualPosition"/>。
    ///
    /// 那个标记就是「这是人摆的」：整理布局默认绕开它（除非显式确认覆盖），这与桌面端拖动节点的收尾一致。
    /// 反过来，整理布局真的移动了它时会把标记清掉——那是整理布局那一侧的事。
    ///
    /// **「位置没变」不在这里挡**：客户端已经先挡了一道（拖出去又拖回来时它根本不发请求），
    /// 而这里再写一次也无害——内容一样，只是文件里那个修订计数器 +1。
    /// 这与「改标题内容」那条路是同一个态度（那条也从不判断「其实没改」），
    /// 只有整理布局会在服务端判「无需改动」，因为那一条要跑完整张画布的引擎。
    /// </summary>
    public IResult MoveNode(long baseRevision, Guid nodeId, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return Error(400, "CANVAS_POSITION_INVALID", "坐标必须是有限数值");

        var recordId = nodeId.ToString();
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                var node = canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
                if (node is null) return (null, Error(404, "RECORD_NOT_FOUND", "可编辑节点不存在"));
                if (node.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));

                node.X = x;
                node.Y = y;
                node.ManualPosition = true;
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), record = ProjectRecord(next.Canvas, recordId) });
    }

    /// <summary>
    /// 改一个节点的类别（网页端的「节点类别」下拉）。
    ///
    /// 与移动位置同一档：**记录级**。它只改一个字段，不增删节点 / 连线，也不动父子关系与章节归属；
    /// 类别影响的是「整理布局时它落到哪条泳道」，那是布局引擎读它，不是结构变了
    /// （成品没有父分镜时引擎会把它排到泳道最后一行，不会坏）。所以同样不要整棵树锁。
    ///
    /// **两侧都拒章节**：把普通节点改成章节会造出一个没有工作树条目撑着的章节；
    /// 把章节改成别的，会让它那一整章（工作树条目与分章关系）失去落点。这两件事都该在桌面端做（那边有撤销）。
    /// </summary>
    public IResult SetCategory(long baseRevision, Guid nodeId, NodeCategory category)
    {
        if (category == NodeCategory.Chapter)
            return Error(400, "CANVAS_CHAPTER_NOT_SUPPORTED",
                "这一版不能把节点改成章节：章节挂着工作树与分章关系，请到桌面端建（那边有撤销）");

        var recordId = nodeId.ToString();
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                var node = canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
                if (node is null) return (null, Error(404, "RECORD_NOT_FOUND", "可编辑节点不存在"));
                if (node.Category == NodeCategory.Chapter)
                    return (null, Error(400, "CANVAS_CHAPTER_NOT_SUPPORTED",
                        "不能改章节节点的类别：它挂着工作树与分章关系，请到桌面端改（那边有撤销）"));
                if (node.IsLocked) return (null, Error(409, "NODE_LOCKED", "节点已锁定"));

                node.Category = category;
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), record = ProjectRecord(next.Canvas, recordId) });
    }

    /// <summary>
    /// 整张画布的写入：桌面端把画布字节交回来，由服务端校验、推进修订、原子落盘。
    ///
    /// 为什么要有这条路：桌面端编辑的是**本地文件**，它自己保存时既不经锁仲裁、别人也收不到通知。
    /// 把保存交给服务端之后，「谁在编辑」这条锁才真正管得住它，改动也顺便推给所有订阅者。
    ///
    /// 送来的字节被当作**画布文件的内容重新解析与校验**（不是拿去覆盖文件），
    /// 所以只读闸门、迁移歧义、项目库权威校验、备份与原子落盘一个都没绕开——
    /// 与「改一个节点」和「整理布局」走的是同一条 <see cref="Write"/> 通道，修订也由这里推进。
    /// </summary>
    public IResult SplitChapters(long baseRevision, Guid sourceId) =>
        Write(baseRevision, current =>
        {
            var canvas = current.Canvas;
            var source = canvas.Nodes.FirstOrDefault(n => n.Id == sourceId);
            if (source is null) return (null, Error(404, "RECORD_NOT_FOUND", "拆章源节点不存在"));
            if (source.IsLocked) return (null, Error(409, "NODE_LOCKED", "源节点已锁定"));
            var split = ChapterSplitPlanner.Split(source.Content);
            if (split.IsEmpty) return (null, Error(400, "CHAPTER_SOURCE_EMPTY", split.Note));
            var row = 0;
            var order = canvas.WorkTree.Where(i => i.Kind == WorkTreeKind.Chapter).Select(i => i.Order).DefaultIfEmpty(0).Max();
            foreach (var draft in split.Chapters)
            {
                var item = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = draft.Title, Chapter = draft.Title, Order = checked(order + draft.Order), Prompt = draft.Content };
                canvas.WorkTree.Add(item);
                var chapter = UnifiedWorkTree.CreateNode(canvas, item, Math.Max(0, source.X) + 420, Math.Max(0, source.Y) + row++ * 320);
                canvas.Edges.Add(new WorkflowEdge { SourceNodeId = source.Id, TargetNodeId = chapter.Id });
                var slot = 0;
                foreach (var entity in ChapterSplitPlanner.MatchEntities(canvas, draft.Content))
                {
                    // Project entities are authoritative: never create a canvas-only variant.
                    var variant = entity.Variants.FirstOrDefault();
                    if (variant is null) continue;
                    var appearance = UnifiedWorkTree.AddAppearance(canvas, entity.Id, item.Id, variant.Id);
                    var node = UnifiedWorkTree.CreateNode(canvas, appearance, chapter.X + 420, chapter.Y + slot++ * 160);
                    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = chapter.Id, TargetNodeId = node.Id });
                }
            }
            return (canvas, null);
        }, (next, bytes) => new { revision = Revision(bytes), chapters = next.Canvas.WorkTree.Count(i => i.Kind == WorkTreeKind.Chapter) });

    public IResult WorkTreePlan(SyncDirection direction)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            return Results.Json(new { revision = current.Revision, plan = CanvasWorkTreeSync.Plan(current.State.Canvas, direction) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Error(503, "WORKTREE_READ_FAILED", "工作树无法安全读取：" + ex.Message); }
    }

    public IResult ApplyWorkTree(long baseRevision, SyncDirection direction, string baselineHash, bool confirmed) =>
        Write(baseRevision, current =>
        {
            var plan = CanvasWorkTreeSync.Plan(current.Canvas, direction);
            if (!string.Equals(plan.BaselineHash, baselineHash, StringComparison.Ordinal))
                return (null, Error(409, "SYNC_PLAN_STALE", "同步计划已过期，请重新预览"));
            var applied = CanvasWorkTreeSync.Apply(current.Canvas, plan, confirmed);
            if (applied.Refused)
                return (null, Results.Json(new { code = "SYNC_CONFLICT", message = applied.ToText(), conflicts = applied.Conflicts }, statusCode: 409));
            if (!applied.Changed)
                return (null, Results.Json(new { revision = baseRevision, applied = 0, skipped = applied.Skipped.Count }));
            return (applied.Canvas, null);
        }, (next, bytes) => new { revision = Revision(bytes) });

    internal byte[] EntityLibraryBytes() => File.ReadAllBytes(entitiesPath);

    internal IResult ReplaceChatCanvas(long baseRevision, byte[] canvasBytes, IReadOnlyCollection<Guid> entityIds, byte[] expectedLibrary, out byte[]? committedLibrary)
    {
        committedLibrary = null;
        if (entityIds.Count == 0) return ReplaceCanvas(baseRevision, canvasBytes);
        byte[]? writtenLibrary = null;
        try
        {
            var current = Load();
            if (current.Revision != baseRevision) return Error(409, "SCENE_REVISION_CONFLICT", "画布已变化");
            if (current.Open.UnsupportedFormat || current.Open.Validation.HasErrors || current.Open.Migration.Ambiguities.Count > 0)
                return Error(409, "CANVAS_READ_ONLY", "画布不可安全写入");
            var next = JsonSerializer.Deserialize<RecentCanvasState>(canvasBytes) ?? throw new InvalidDataException("画布为空");
            next = next with { Revision = checked(current.State.Revision + 1) };
            if (CanvasIdentityValidator.Validate(next.Canvas).HasErrors) throw new InvalidDataException("画布校验失败");
            var saved = CanvasSaveService.Save(next, path, current.Bytes, () =>
            {
                if (!expectedLibrary.AsSpan().SequenceEqual(File.ReadAllBytes(entitiesPath)))
                    throw new InvalidDataException("项目实体已变化，请重新生成提议；撤销不能覆盖后续实体编辑");
                var library = CheckProjectAuthority(current.State) ?? throw new InvalidDataException("资源库缺失");
                foreach (var id in entityIds)
                {
                    var incoming = next.Canvas.FindEntity(id) ?? throw new InvalidDataException("实体不存在");
                    if (!incoming.ManagedByProject) continue;
                    var index = library.Entities.FindIndex(e => e.Id == id);
                    if (index < 0) throw new InvalidDataException("权威实体不存在");
                    var retainedVersions = incoming.Variants.SelectMany(v => v.Versions).Select(v => v.Id).ToHashSet();
                    var removedVersions = library.Entities[index].Variants.SelectMany(v => v.Versions).Select(v => v.Id).Where(id => !retainedVersions.Contains(id)).ToHashSet();
                    if (removedVersions.Count > 0)
                    {
                        bool Matches(NodeReference reference) => reference.EntityId == id && reference.VariantVersionId is { } version && removedVersions.Contains(version);
                        var directory = Path.GetDirectoryName(path)!;
                        var sources = Directory.EnumerateFiles(directory, "*.json").Where(p => !p.EndsWith(".edits.json", StringComparison.OrdinalIgnoreCase)).ToList();
                        var draft = Path.Combine(Path.GetDirectoryName(directory)!, "last-canvas.json");
                        if (File.Exists(draft)) sources.Add(draft);
                        foreach (var source in sources)
                        {
                            WorkflowCanvasState canvas;
                            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) canvas = next.Canvas;
                            else
                            {
                                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || !CanvasOpenService.TryRead(source, out var state, out _) || state.Canvas is null)
                                    throw new InvalidDataException("有画布无法安全扫描，已阻止撤销实体版本");
                                canvas = state.Canvas;
                            }
                            if (CanvasReferenceScanner.Scan(canvas, source, false, false).Hits.Any(h => h.EntityId == id && h.VariantVersionId is { } version && removedVersions.Contains(version)) ||
                                canvas.Entities.Where(e => !e.ManagedByProject).SelectMany(e => e.Variants).Any(v => v.References.Concat(v.Versions.SelectMany(ver => ver.References)).Any(Matches)))
                                throw new InvalidDataException("其它画布仍引用生成版本，不能撤销");
                        }
                        if (library.Entities.Where(e => e.Id != id).Append(incoming).SelectMany(e => e.Variants).Any(v => v.References.Concat(v.Versions.SelectMany(ver => ver.References)).Any(Matches)))
                            throw new InvalidDataException("实体历史仍引用生成版本，不能撤销");
                    }
                    library.Entities[index] = incoming;
                }
                if (!ProjectLibrary.TrySave(library, entitiesPath, expectedLibrary, out var error)) throw new IOException(error);
                writtenLibrary = File.ReadAllBytes(entitiesPath);
                return CheckProjectAuthority(next);
            }, () =>
            {
                if (writtenLibrary is null) return;
                var original = JsonSerializer.Deserialize<ProjectEntityFile>(expectedLibrary, JsonOptions)!;
                if (!ProjectLibrary.TrySave(original, entitiesPath, writtenLibrary, out var error)) throw new IOException("资源库回滚失败：" + error);
            });
            committedLibrary = writtenLibrary;
            LastCommit = (LastCommit.Serial + 1, Revision(saved.WrittenBytes));
            return Results.Json(new { revision = LastCommit.Revision, nodes = next.Canvas.Nodes.Count });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return Error(409, "ACTION_FAILED", ex.Message); }
    }

    public IResult ReplaceCanvas(long baseRevision, byte[] canvasBytes)
    {
        if (!CanvasOpenService.TryOpenBytes(canvasBytes, path, out var opened, out var reason))
            return Error(400, "CANVAS_INVALID", "送来的画布无法解析：" + reason);
        if (opened.State.Canvas is not { } incoming)
            return Error(400, "CANVAS_INVALID", "送来的画布没有节点集合");
        if (opened.UnsupportedFormat) return Error(409, "CANVAS_READ_ONLY", "高版本画布只读，拒绝覆盖");
        if (opened.Validation.HasErrors || opened.Migration.Ambiguities.Count > 0)
            return Error(409, "CANVAS_VALIDATION_FAILED", "画布引用或迁移存在未解决问题，拒绝保存");

        return Write(baseRevision, _ => (incoming, null),
            (next, bytes) => new { revision = Revision(bytes), nodes = next.Canvas.Nodes.Count });
    }

    /// <summary>
    /// 新建一个节点（网页端的「新建」走这里）。
    ///
    /// 位置由**服务端**算：放在同一章节里最右那个节点的右边一列（没有同章节的节点就落在原点）。
    /// 不让客户端送坐标——排版只该有一份规则，客户端再送一份就是第二个真相；
    /// 真正「排好看」交给整理布局（它按同一份引擎重排，连 ManualPosition 也会被它覆盖）。
    /// 节点的形态（Category / WaitingForUser / ManualPosition）与**桌面端新建节点时一致**。
    /// </summary>
    public IResult CreateNode(long baseRevision, NodeCategory category, string? title, Guid? chapterId)
    {
        if (title is { Length: > 256 })
            return Error(400, "INVALID_REQUEST", "标题不能超过256个字符");
        if (category == NodeCategory.Chapter && chapterId is not null)
            return Error(400, "INVALID_REQUEST", "章节应建在工作树根级，不能嵌入另一个章节");

        var created = Guid.Empty;
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                // 锚点必须真的存在：写一个不存在的章节 ID，等于往画布里塞一个悬空引用。
                if (chapterId is { } anchor && CanvasChapters.List(canvas).All(chapter => chapter.Id != anchor))
                    return (null, Error(400, "CANVAS_UNKNOWN_CHAPTER", "指定的章节不在这张画布里"));

                if (category == NodeCategory.Chapter)
                {
                    var chapters = canvas.WorkTree.Where(item => item.Kind == WorkTreeKind.Chapter).ToList();
                    var item = new WorkTreeItem
                    {
                        Kind = WorkTreeKind.Chapter,
                        Name = string.IsNullOrWhiteSpace(title) ? $"章节{chapters.Count + 1}" : title.Trim(),
                        Order = chapters.Count == 0 ? 0 : chapters.Max(chapter => chapter.Order) + 1
                    };
                    canvas.WorkTree.Add(item);
                    var chapterNode = UnifiedWorkTree.CreateNode(canvas, item,
                        canvas.Nodes.Count == 0 ? 0 : canvas.Nodes.Max(node => node.X) + PlaceholderSpacing, 0);
                    chapterNode.ExecutionStatus = NodeExecutionStatus.WaitingForUser;
                    created = chapterNode.Id;
                    return (canvas, null);
                }

                // 落点只是「别叠在一起」，不是排版结果——同章节的兄弟姐妹决定它往哪边放。
                var siblings = canvas.Nodes
                    .Where(node => CanvasChapters.ResolveChapterId(canvas, node) == chapterId)
                    .ToList();
                var node = new WorkflowNode
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "新节点" : title.Trim(),
                    Category = category,
                    ExecutionStatus = NodeExecutionStatus.WaitingForUser,
                    X = siblings.Count == 0 ? 0f : siblings.Max(item => item.X) + PlaceholderSpacing,
                    Y = siblings.Count == 0 ? 0f : siblings.Max(item => item.Y),
                    ManualPosition = true
                };
                // 章节锚点就是 WorkTreeItemId：章节在画布里是**工作树条目**，不是普通节点字段。
                if (chapterId is { } target) node.WorkTreeItemId = target;
                canvas.Nodes.Add(node);
                created = node.Id;
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), nodeId = created, nodes = next.Canvas.Nodes.Count });
    }

    /// <summary>
    /// 删掉一个节点，**连带删掉它的连线**。
    ///
    /// 与桌面端的删除是同一条语义（那边也是摘掉节点 + 把两头连着它的连线一起删）：
    /// 留着悬空连线，画布校验会立刻拦下这次写入，而用户看到的会是「删了却保存不了」。
    /// 引用完整性同样由校验把守（别人还引用着它时会如实拒绝，而不是留一份坏画布）。
    /// </summary>
    public IResult DeleteNode(long baseRevision, Guid nodeId)
    {
        var title = string.Empty;
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                var node = canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
                if (node is null) return (null, Error(404, "CANVAS_NODE_NOT_FOUND", "这个节点不在这张画布里"));
                if (node.Category == NodeCategory.Chapter)
                    return (null, Error(400, "CANVAS_CHAPTER_NOT_SUPPORTED",
                        "这一版不能删章节：它挂着工作树与分章关系，请到桌面端删（那边有撤销）"));

                title = node.Title;
                canvas.Nodes.Remove(node);
                canvas.Edges.RemoveAll(edge => edge.SourceNodeId == nodeId || edge.TargetNodeId == nodeId);
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), deleted = title, nodes = next.Canvas.Nodes.Count });
    }

    /// <summary>
    /// 新建一根连线（网页端的「连接」走这里，与新建节点同一条结构级通道）。
    ///
    /// 「许不许连」问的是共享的 <see cref="CanvasEdgeRules"/>——与桌面端拖拽连接**同一条规则**；
    /// 端点存不存在则在这里先说清楚，否则画布校验会在写入时拦下，使用者看到的是「连上了却保存不了」。
    /// 端口留默认值，与桌面端新建连线时一致（界面上一个出口只有一根线）。
    /// </summary>
    public IResult CreateEdge(long baseRevision, Guid sourceId, Guid targetId)
    {
        var created = Guid.Empty;
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                if (!canvas.Nodes.Any(node => node.Id == sourceId))
                    return (null, Error(404, "CANVAS_NODE_NOT_FOUND", "起点节点不在这张画布里"));
                if (!canvas.Nodes.Any(node => node.Id == targetId))
                    return (null, Error(404, "CANVAS_NODE_NOT_FOUND", "终点节点不在这张画布里"));

                if (CanvasEdgeRules.Refusal(canvas, sourceId, targetId) is { } refusal)
                    return (null, RefusalResult(refusal));

                var edge = new WorkflowEdge { SourceNodeId = sourceId, TargetNodeId = targetId };
                canvas.Edges.Add(edge);
                created = edge.Id;
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), edgeId = created, edges = next.Canvas.Edges.Count });
    }

    /// <summary>
    /// 把共享层的拒绝映射成 HTTP 响应。共享层不带 HTTP 词汇（它要同时服务桌面端与网页端），
    /// 所以映射只此一处——散开写会出现「同一个原因在两个端点有两种错误码」。
    /// </summary>
    private static IResult RefusalResult(EdgeRefusal refusal) => refusal.Kind switch
    {
        EdgeRefusalKind.SelfLoop => Error(400, "CANVAS_EDGE_SELF_LOOP", refusal.Message),
        _ => Error(409, "CANVAS_EDGE_EXISTS", refusal.Message)
    };

    /// <summary>
    /// 断开一根连线。连线没有引用完整性可言，所以只需要「它真的在这张画布里」。
    ///
    /// 与桌面端选中连线后删除是同一条语义（那边是 DeleteSelection 里「连线优先」那一支）。
    /// 注意方向：删节点会**连带**删掉它的连线（见 <see cref="DeleteNode"/>），反过来不成立。
    /// </summary>
    public IResult DeleteEdge(long baseRevision, Guid edgeId)
    {
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                var edge = canvas.Edges.FirstOrDefault(item => item.Id == edgeId);
                if (edge is null) return (null, Error(404, "CANVAS_EDGE_NOT_FOUND", "这条连线不在这张画布里"));

                canvas.Edges.Remove(edge);
                return (canvas, null);
            },
            (next, bytes) => new { revision = Revision(bytes), edges = next.Canvas.Edges.Count });
    }

    /// <summary>新节点的临时落点间距：它只是「别叠在一起」，摆好看是整理布局的事。</summary>
    private const float PlaceholderSpacing = 320f;

    /// <summary>
    /// 整理布局的预览：**只算不写**，返回泳道划分与逐节点改动。
    ///
    /// 用的是桌面端同一份 <see cref="CanvasSwimlaneLayout"/>（Desktop.Shared 里的那个引擎，
    /// 两边都是 net10.0 且它不含 UI 依赖）。所以这里算出来的排布与桌面端点「整理」的结果一致；
    /// 要是在 TypeScript 里另写一份，两份实现在同一张画布上迟早会排出不同的样子。
    /// </summary>
    public IResult PlanLayout(string? scope, bool overrideManual)
    {
        var preview = BuildPreview(scope, overrideManual);
        if (preview.Error is not null) return preview.Error;
        return Results.Json(Wire(preview.Plan!, preview.Canvas!));
    }

    /// <summary>
    /// 应用前的**干跑**：把「该不该写、写几项」先算清楚，一个字节都不动。
    ///
    /// 接口层靠它决定值不值得去拿整棵树锁——否则一次注定失败的整理（修订陈旧、布局被阻断）
    /// 也会占住整棵树，把别人的结构改动挡在门外，直到锁自己过期。
    /// </summary>
    internal IResult? PreflightApply(long baseRevision, string? scope, bool overrideManual,
        out bool willWrite, out long revision, out int moves, out string summary)
    {
        willWrite = false;
        revision = 0;
        moves = 0;
        summary = string.Empty;
        var preview = BuildPreview(scope, overrideManual);
        if (preview.Error is not null) return preview.Error;
        if (preview.Revision != baseRevision)
            return Error(409, "SCENE_REVISION_CONFLICT", "项目画布已被修改，请重新加载");
        var plan = preview.Plan!;
        if (Refuse(plan, overrideManual, preview.Canvas!) is { } refusal) return refusal;
        willWrite = plan.Changed;
        revision = preview.Revision;
        moves = plan.Changes.Count;
        summary = plan.Changed ? plan.ToText(preview.Canvas!) : NoChangeSummary;
        return null;
    }

    /// <summary>
    /// 整理布局的写入。计划**在服务端按同一份引擎重算**，不接受客户端送来的坐标——
    /// 「同一份画布 + 同一份引擎」的结果是确定的，所以重算一遍比信任请求体安全得多。
    /// 调用方要先把整棵树锁拿到手（整理结构属于整棵树级的操作）。
    /// </summary>
    public IResult ApplyLayout(long baseRevision, string? scope, bool overrideManual)
    {
        // 被真正移动的节点：响应里只回这些记录的投影，客户端不必为一次整理重拉整张画布。
        var movedIds = new HashSet<Guid>();
        var summary = string.Empty;
        return Write(baseRevision, current =>
        {
            var canvas = current.Canvas;
            var plan = BuildPlan(canvas, scope, overrideManual);
            if (plan is null) return (null, Error(400, "INVALID_REQUEST", "scope 必须是 all 或一个章节 ID"));
            if (Refuse(plan, overrideManual, canvas) is { } refusal) return (null, refusal);
            if (!plan.Changed)
                // 干跑已经拦过这一种，这里是写入前的兜底：位置本来就排好了，不该写盘、也不该推进修订。
                return (null, Results.Json(new
                {
                    revision = current.Revision,
                    moved = 0,
                    summary = NoChangeSummary,
                    records = Array.Empty<object>()
                }));
            foreach (var change in plan.Changes) movedIds.Add(change.NodeId);
            summary = plan.ToText(canvas);
            return (CanvasSwimlaneLayout.Apply(canvas, plan), null);
        }, (next, bytes) =>
        {
            return new
            {
                revision = Revision(bytes),
                moved = movedIds.Count,
                summary,
                records = NodeProjection.ProjectRecords(next.Canvas.Nodes, next.Canvas)
                    .Where(row => movedIds.Contains(Guid.Parse(JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString()!)))
                    .ToArray()
            };
        });
    }

    internal const string NoChangeSummary = "位置已符合泳道布局，无需改动。";

    /// <summary>加载并算出布局计划，不写盘、也不看修订。预览与干跑共用它。</summary>
    private (CanvasLayoutPlan? Plan, WorkflowCanvasState? Canvas, long Revision, IResult? Error) BuildPreview(string? scope, bool overrideManual)
    {
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var plan = BuildPlan(current.State.Canvas, scope, overrideManual);
            if (plan is null) return (null, null, 0, Error(400, "INVALID_REQUEST", "scope 必须是 all 或一个章节 ID"));
            return (plan, current.State.Canvas, current.Revision, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return (null, null, 0, Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message));
        }
    }

    /// <summary>拒绝写入的那几条规矩。预览之后的每一种入口都走它，免得同一个条件出现两种文案。</summary>
    private static IResult? Refuse(CanvasLayoutPlan plan, bool overrideManual, WorkflowCanvasState canvas)
    {
        // 阻断冲突即使确认覆盖也不写：覆盖只解决「保护手动坐标」，不解决冲突。
        if (plan.HasBlockingConflicts)
            return Error(409, "LAYOUT_BLOCKED", "布局被阻断，画布未改动。" + Environment.NewLine + plan.ToText(canvas));
        if (plan.RequiresConfirmation && !overrideManual)
            return Error(409, "LAYOUT_NEEDS_CONFIRMATION",
                $"有 {plan.ProtectedNodeIds.Count} 个手动摆放的节点会被移动，需要显式确认「自动布局覆盖」");
        return null;
    }

    private static CanvasLayoutPlan? BuildPlan(WorkflowCanvasState canvas, string? scope, bool overrideManual)
    {
        var options = new CanvasLayoutOptions(OverrideManual: overrideManual);
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, AllScope, StringComparison.OrdinalIgnoreCase))
            return CanvasSwimlaneLayout.PlanAll(canvas, options);
        return Guid.TryParse(scope, out var chapterId) ? CanvasSwimlaneLayout.PlanChapter(canvas, chapterId, options) : null;
    }

    /// <summary>整画布范围的名字。章节范围就直接传章节 ID。</summary>
    public const string AllScope = "all";

    private static object Wire(CanvasLayoutPlan plan, WorkflowCanvasState canvas) => new
    {
        wholeCanvas = plan.WholeCanvas,
        changed = plan.Changed,
        blocking = plan.HasBlockingConflicts,
        requiresConfirmation = plan.RequiresConfirmation,
        protectedRecordIds = plan.ProtectedNodeIds.Select(id => id.ToString()).ToArray(),
        lanes = plan.Lanes.Select(lane => new
        {
            kind = lane.Kind.ToString(),
            chapterId = lane.ChapterId,
            title = lane.Title,
            order = lane.Order,
            recordIds = lane.NodeIds.Select(id => id.ToString()).ToArray()
        }).ToArray(),
        changes = plan.Changes.Select(change => new
        {
            recordId = change.NodeId.ToString(),
            title = change.Title,
            fromX = change.FromX,
            fromY = change.FromY,
            toX = change.ToX,
            toY = change.ToY
        }).ToArray(),
        conflicts = plan.Conflicts.Select(conflict => new
        {
            recordId = conflict.NodeId,
            code = conflict.Code,
            message = conflict.Message,
            blocking = conflict.Blocking
        }).ToArray(),
        summary = plan.ToText(canvas)
    };
}
