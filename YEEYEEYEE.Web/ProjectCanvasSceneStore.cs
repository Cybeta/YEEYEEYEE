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
        var committed = false;
        try
        {
            var current = Load();
            if (current.Revision != baseRevision) return Error(409, "SCENE_REVISION_CONFLICT", "项目画布已被修改，请重新加载");
            if (current.Open.UnsupportedFormat) return Error(409, "CANVAS_READ_ONLY", "高版本画布只读，拒绝覆盖");
            if (current.Open.Validation.HasErrors || current.Open.Migration.Ambiguities.Count > 0)
                return Error(409, "CANVAS_VALIDATION_FAILED", "画布引用或迁移存在未解决问题，拒绝保存");
            CheckProjectAuthority(current.State);
            var projected = NodeProjection.ProjectRecords(current.State.Canvas.Nodes, current.State.Canvas);
            if (!Guid.TryParse(recordId, out var id) ||
                !projected.Any(row => JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString() == recordId))
                return Error(404, "RECORD_NOT_FOUND", "可编辑节点不存在");
            var node = current.State.Canvas.Nodes.SingleOrDefault(node => node.Id == id);
            if (node is null) return Error(404, "RECORD_NOT_FOUND", "该记录不是可编辑节点");
            if (node.IsLocked) return Error(409, "NODE_LOCKED", "节点已锁定");
            if (current.State.Revision == int.MaxValue) return Error(409, "SCENE_REVISION_CONFLICT", "画布修订已达上限");
            node.Title = title.GetString()!;
            node.Content = content.GetString()!;
            var next = current.State with { Revision = current.State.Revision + 1 };
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
            // Do not re-open the file after commit: another writer may already have replaced it.
            // Returning a failure after successful replacement would invite an unsafe retry.
            var record = NodeProjection.ProjectRecords(next.Canvas.Nodes, next.Canvas)
                .First(row => JsonSerializer.SerializeToElement(row).GetProperty("recordId").GetString() == recordId);
            return Results.Json(new { revision = Revision(saved.WrittenBytes), record });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Error(503, committed ? "PROJECT_CANVAS_COMMITTED" : "PROJECT_CANVAS_WRITE_FAILED",
                committed ? "项目画布已保存，但无法确认提交后的读取：" + ex.Message : "项目画布未安全保存：" + ex.Message);
        }
    }
}
