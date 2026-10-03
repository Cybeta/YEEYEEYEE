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


    private static long Revision(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return ((long)digest[0] << 40) | ((long)digest[1] << 32) | ((long)digest[2] << 24) |
               ((long)digest[3] << 16) | ((long)digest[4] << 8) | digest[5];
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
            readOnly = opened.UnsupportedFormat || opened.Validation.HasErrors || opened.Migration.Ambiguities.Count > 0,
            formatVersion = state.FormatVersion,
            projectName = ProjectContext.Open(root)?.Descriptor.Name ?? Path.GetFileName(root),
            canvasTitle = state.Title,
            migration = opened.Migration,
            validation = opened.Validation
        };
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
        }, (next, bytes) =>
        {
            var record = NodeProjection.ProjectRecords(next.Canvas.Nodes, next.Canvas)
                .First(row => JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString() == recordId);
            return new { revision = Revision(bytes), record };
        });
    }

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
