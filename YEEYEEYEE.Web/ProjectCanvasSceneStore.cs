using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>Web adapter for the desktop canvas file; never persists a projected scene.</summary>
internal sealed class ProjectCanvasSceneStore
{
    private readonly string path;
    private readonly string entitiesPath;
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
    public (long Serial, long Revision) LastCommit { get; private set; }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public ProjectCanvasSceneStore(string canvasPath, string? configuredEntitiesPath) =>
        (path, entitiesPath) = ResolvePaths(canvasPath, configuredEntitiesPath);

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
        // A single byte snapshot supplies both the response revision and the projected state.
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
            // A local entity keeps its own authority; all others require a library entry.
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

                var entity = canvas.FindEntity(entityId);
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
    public IResult Assist(string recordId)
    {
        if (!Guid.TryParse(recordId, out var nodeId))
            return Error(400, "CANVAS_RECORD_INVALID", "记录 ID 不是合法的 GUID");
        try
        {
            var current = Load();
            CheckProjectAuthority(current.State);
            var node = current.State.Canvas.Nodes.FirstOrDefault(item => item.Id == nodeId);
            if (node is null) return Error(404, "CANVAS_RECORD_NOT_FOUND", "这条记录不在这张画布里");

            var plan = NodeAssistPlanner.BuildPlan(current.State.Canvas, node);
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

            if (AgentAttachmentLoader.SplitDataUrl(attachment.Reference) is { } inline)
            {
                try { return Results.Bytes(Convert.FromBase64String(inline.Data), inline.MediaType); }
                catch (FormatException) { return Error(500, "ASSET_INLINE_INVALID", "内嵌图片的 base64 解不开"); }
            }

            var file = AssetStore.Resolve(attachment.Reference);
            if (file is null)
                return Error(404, "ASSET_FILE_MISSING",
                    "图片文件不在这个部署里：容器要把共享素材目录（ASSET_DIR）或项目目录挂进来");
            if (!InsideAllowedRoots(file))
                return Error(403, "ASSET_OUTSIDE_PROJECT",
                    "这张图不在允许的目录里（共享素材目录或项目目录），拒绝服务");
            return Results.File(file, AgentAttachmentLoader.ImageMediaType(file) ?? "application/octet-stream");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, "PROJECT_CANVAS_READ_FAILED", "项目画布或项目资源无法安全读取：" + ex.Message);
        }
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
        Func<RecentCanvasState, byte[], object> respond)
    {
        var committed = false;
        try
        {
            var current = Load();
            if (current.Revision != baseRevision) return Error(409, "SCENE_REVISION_CONFLICT", "项目画布已被修改，请重新加载");
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
                // The save service checks expected bytes and revalidates the project library
                // under the cross-process canvas lease before backup and atomic replacement.
                saved = CanvasSaveService.Save(next, path, current.Bytes, () => CheckProjectAuthority(next));
            }
            catch (CanvasSaveAbortedException ex) when (ex.Message.Contains("其他进程", StringComparison.Ordinal))
            { return Error(409, "SCENE_REVISION_CONFLICT", ex.Message); }
            committed = true;
            // 只在这里记：**落盘之后**才知道这次提交算数。调用方拿序号前后比一次就知道写没写；
            // 修订号取的是刚落盘那份字节的哈希，与 respond 回给客户端的 `revision` 是同一个数——
            // 客户端就是拿它和自己手上的比「谁更新」的，两边必须是同一把尺子。
            LastCommit = (LastCommit.Serial + 1, Revision(saved.WrittenBytes));
            // Do not re-open the file after commit: another writer may already have replaced it.
            // Returning a failure after successful replacement would invite an unsafe retry.
            return Results.Json(respond(next, saved.WrittenBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, committed ? "PROJECT_CANVAS_COMMITTED" : "PROJECT_CANVAS_WRITE_FAILED",
                committed ? "项目画布已保存，但无法确认提交后的读取：" + ex.Message : "项目画布未安全保存：" + ex.Message);
        }
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
        if (category == NodeCategory.Chapter)
            return Error(400, "CANVAS_CHAPTER_NOT_SUPPORTED",
                "这一版还不能新建章节：章节是工作树条目（不是画布节点），得先在桌面端建好");

        var created = Guid.Empty;
        return Write(baseRevision, current =>
            {
                var canvas = current.Canvas;
                if (canvas is null) return (null, Error(409, "CANVAS_EMPTY", "这张画布没有节点集合"));

                // 锚点必须真的存在：写一个不存在的章节 ID，等于往画布里塞一个悬空引用。
                if (chapterId is { } anchor && CanvasChapters.List(canvas).All(chapter => chapter.Id != anchor))
                    return (null, Error(400, "CANVAS_UNKNOWN_CHAPTER", "指定的章节不在这张画布里"));

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
