using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 项目级资源库文件（目标 6 / G6-1）：一份项目共享的实体库，多份画布引用同一批实体。
/// 格式与画布内嵌的 <see cref="WorkflowEntity"/> 完全一致，因此迁移是零转换的搬运。
/// </summary>
public sealed class ProjectEntityFile
{
    /// <summary>库文件格式版本；只追加不重排。</summary>
    public int FormatVersion { get; set; } = ProjectLibrary.CurrentFormat;

    /// <summary>最近一次写入时间，便于人工核对。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<WorkflowEntity> Entities { get; set; } = new();
}

/// <summary>
/// 项目级资源库的读写（目标 6 / G6-1）。
///
/// 为什么需要它：实体原先内嵌在每份画布文件里，同一角色在两个画布上就是两份互不相干的副本，
/// 改一处不会影响另一处，删一处也不会提醒另一处。项目库把实体提到**项目**这一层：
/// 身份仍是 <see cref="WorkflowEntity.Id"/>（稳定 ID），内容只有一份，所有画布共享。
///
/// 三条硬语义：
/// · **原子写**：先写临时文件再替换，任何失败都不影响旧库文件；
/// · **写前备份**：每次覆盖前把旧库文件备份到 <c>project/backups</c>，失败可回滚；
/// · **幂等 upsert**：同 ID 覆盖、新 ID 追加，重复执行不会生成第二份。
/// </summary>
public static class ProjectLibrary
{
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>项目库目录（项目根下 project/）。</summary>
    public static string Directory => AppPaths.Combine("project");

    public static string FilePath => Path.Combine(Directory, "entities.json");

    /// <summary>库文件备份目录。</summary>
    public static string BackupDirectory => Path.Combine(Directory, "backups");

    /// <summary>读取项目库；文件不存在、项目未打开或不可读时返回空库（不抛异常，打开旧项目照样能用）。</summary>
    public static ProjectEntityFile Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ProjectEntityFile();
            var parsed = JsonSerializer.Deserialize<ProjectEntityFile>(File.ReadAllText(FilePath), Options);
            return parsed ?? new ProjectEntityFile();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new ProjectEntityFile();
        }
    }

    public static IReadOnlyList<WorkflowEntity> Entities() => Load().Entities;

    public static WorkflowEntity? Find(Guid id) => Load().Entities.FirstOrDefault(entity => entity.Id == id);

    public static bool Contains(Guid id) => Find(id) is not null;

    /// <summary>
    /// 写入项目库（原子写 + 写前备份）。失败时返回 false 并给出原因，**旧库文件保持原样**。
    /// </summary>
    public static bool TrySave(ProjectEntityFile file, out string error)
    {
        ArgumentNullException.ThrowIfNull(file);
        error = string.Empty;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(FilePath) && !TryBackup(out var backupError))
            {
                error = "备份现有项目库失败，已中止写入：" + backupError;
                return false;
            }

            file.FormatVersion = CurrentFormat;
            file.UpdatedAt = DateTimeOffset.Now;
            var temp = Path.Combine(Directory, $".entities.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                // 失败必须清理临时文件：否则项目目录里会留下一堆半成品 .tmp（与画布写入保持同一做法）。
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            error = exception is InvalidOperationException ? "还没有打开项目，项目级资源库暂不可用。" : exception.Message;
            return false;
        }
    }

    /// <summary>把现有库文件备份到 <c>project/backups</c>；库文件不存在时视为成功（无需备份）。</summary>
    public static bool TryBackup(out string error) => TryBackup(out _, out error);

    /// <summary>返回本次创建的确切备份路径；迁移回滚不能按文件名排序猜测恢复源。</summary>
    public static bool TryBackup(out string? backupPath, out string error)
    {
        backupPath = null;
        error = string.Empty;
        if (!File.Exists(FilePath)) return true;
        try
        {
            System.IO.Directory.CreateDirectory(BackupDirectory);
            var target = Path.Combine(BackupDirectory, $"entities-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40] + ".json");
            File.Copy(FilePath, target, overwrite: false);
            backupPath = target;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = exception is InvalidOperationException ? "还没有打开项目。" : exception.Message;
            return false;
        }
    }

    /// <summary>列出库文件备份（新的在前）。</summary>
    public static IReadOnlyList<string> Backups()
    {
        try
        {
            return System.IO.Directory.Exists(BackupDirectory)
                ? System.IO.Directory.GetFiles(BackupDirectory, "entities-*.json").OrderByDescending(path => path).ToList()
                : Array.Empty<string>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>用备份文件恢复项目库（回滚用）。失败返回原因。</summary>
    public static string? RestoreFromBackup(string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return $"备份文件不存在：{backupPath}";
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(backupPath, FilePath, overwrite: true);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return error is InvalidOperationException ? "还没有打开项目。" : error.Message;
        }
    }

    /// <summary>删除项目库文件：回滚「首次迁移」时用，回到本来就没有库的状态。</summary>
    public static bool TryDeleteFile(out string error)
    {
        error = string.Empty;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = exception is InvalidOperationException ? "还没有打开项目。" : exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 幂等写入：同 ID 覆盖内容、新 ID 追加。返回新增/更新条数；不改变传入对象的集合。
    /// </summary>
    public static ProjectEntityFile Upsert(IEnumerable<WorkflowEntity> entities, out int added, out int updated)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var file = Load();
        added = 0;
        updated = 0;
        foreach (var entity in entities)
        {
            var index = file.Entities.FindIndex(item => item.Id == entity.Id);
            var copy = ProjectEntityScope.CloneEntity(entity);
            copy.ManagedByProject = true;
            if (index < 0)
            {
                file.Entities.Add(copy);
                added++;
            }
            else
            {
                file.Entities[index] = copy;
                updated++;
            }
        }

        return file;
    }

    /// <summary>从库中移除一个实体（调用方负责先做引用保护与备份）。</summary>
    public static bool TryRemove(Guid entityId, out string error)
    {
        error = string.Empty;
        var file = Load();
        var removed = file.Entities.RemoveAll(entity => entity.Id == entityId);
        if (removed == 0) { error = "项目库里没有这个实体。"; return false; }
        return TrySave(file, out error);
    }
}

/// <summary>把画布与项目库对接的结果，用于给人看的说明（不静默改数据）。</summary>
public sealed record ProjectMergeReport(
    int Refreshed,
    int Injected,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Notes)
{
    public static ProjectMergeReport Empty { get; } = new(0, 0, Array.Empty<string>(), Array.Empty<string>());

    public bool Changed => Refreshed > 0 || Injected > 0;
}

/// <summary>
/// 画布与项目库之间的“同一份资源”语义（目标 6 / G6-1、G6-3）。
///
/// · 打开画布时 <see cref="MergeInto"/>：托管实体以**库为权威**刷新；画布引用了但快照里没有的库实体补进来；
///   库中已不存在的托管实体如实列进 <c>Missing</c>（交给引用与锁定版本的既有缺失提示）。
/// · 保存画布时 <see cref="RefreshSnapshots"/>：托管实体在画布文件里只是**快照**，写盘前从库刷新，
///   于是「改库 → 所有画布看到同一份内容」，同时旧版程序打开画布仍能读到一份完整快照。
/// </summary>
public static class ProjectEntityScope
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>深拷贝一个实体（避免画布与库共享同一个对象实例）。</summary>
    public static WorkflowEntity CloneEntity(WorkflowEntity entity) =>
        JsonSerializer.Deserialize<WorkflowEntity>(JsonSerializer.Serialize(entity, Options), Options)
        ?? new WorkflowEntity { Id = entity.Id, Kind = entity.Kind, Name = entity.Name };

    public static ProjectMergeReport MergeInto(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var library = ProjectLibrary.Load();
        if (library.Entities.Count == 0 && !canvas.Entities.Any(entity => entity.ManagedByProject))
            return ProjectMergeReport.Empty;

        var byId = library.Entities.ToDictionary(entity => entity.Id);
        var refreshed = 0;
        var injected = 0;
        var missing = new List<string>();
        var notes = new List<string>();

        for (var index = 0; index < canvas.Entities.Count; index++)
        {
            var local = canvas.Entities[index];
            if (!local.ManagedByProject) continue;
            if (byId.TryGetValue(local.Id, out var authoritative))
            {
                var refreshedCopy = CloneEntity(authoritative);
                refreshedCopy.ProjectMissingReason = null;
                canvas.Entities[index] = refreshedCopy;
                refreshed++;
                continue;
            }

            // 目标 6 / G6-R3：权威资源不见了（库文件漏拷或被删）——旧快照只留作恢复参考，
            // 打上运行时标记，解析/预检/出图一律按阻断处理，不许拿旧内容照跑。
            var reason = $"{WorkflowEntity.KindName(local.Kind)}「{local.Name}」已不在项目库中（可能被删除，或库文件未随项目一起拷贝）";
            local.ProjectMissingReason = reason;
            canvas.Entities[index] = local;
            missing.Add(reason);
        }

        var referenced = canvas.Nodes
            .SelectMany(node => node.References)
            .Select(reference => reference.EntityId)
            .ToHashSet();
        foreach (var id in referenced)
        {
            if (canvas.Entities.Any(entity => entity.Id == id)) continue;
            if (!byId.TryGetValue(id, out var authoritative)) continue;
            var copy = CloneEntity(authoritative);
            copy.ManagedByProject = true;
            copy.ProjectMissingReason = null;
            canvas.Entities.Add(copy);
            injected++;
        }

        if (refreshed > 0) notes.Add($"已按项目库刷新 {refreshed} 个共享资源。");
        if (injected > 0) notes.Add($"已从项目库补入 {injected} 个被引用但本画布没有的资源。");
        if (missing.Count > 0) notes.Add($"有 {missing.Count} 个共享资源在项目库里找不到，引用会按缺失处理。");
        return new ProjectMergeReport(refreshed, injected, missing, notes);
    }

    /// <summary>把托管实体的快照刷新为库中最新（只应对克隆体调用，不要拿它改内存中的活动画布）。</summary>
    public static int RefreshSnapshots(WorkflowCanvasState canvas) => RefreshSnapshots(canvas, ProjectLibrary.Load());

    public static int RefreshSnapshots(WorkflowCanvasState canvas, ProjectEntityFile library)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(library);
        if (library.Entities.Count == 0) return 0;
        var byId = library.Entities.ToDictionary(entity => entity.Id);
        var refreshed = 0;
        for (var index = 0; index < canvas.Entities.Count; index++)
        {
            var local = canvas.Entities[index];
            if (!local.ManagedByProject) continue;
            if (!byId.TryGetValue(local.Id, out var authoritative)) continue;
            var copy = CloneEntity(authoritative);
            copy.ManagedByProject = true;
            copy.ProjectMissingReason = null;
            canvas.Entities[index] = copy;
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>
    /// 一次发布的真实结果（目标 6 / G6-T2）：只有**真的落进项目库**才叫已持久化。
    /// 本地（未迁移）实体不写库，所以 <see cref="Persisted"/> 为 false——界面不得提示"已保存"。
    /// </summary>
    public readonly record struct PublishOutcome(bool Succeeded, bool Persisted, string Error);

    /// <summary>
    /// 只重新核验「项目级资源是否还在库里」（目标 6 / G6-T1）。
    ///
    /// 打开时算出的缺失标记会过期：库可能在画布打开之后被删除或漏拷，此时旧标记还是"正常"，
    /// 出图就会一路走到提供方。所以执行前要现场对照项目库重新判定；这里只更新标记、
    /// **不碰实体内容**，因此可以随时调用，不会覆盖用户未保存的编辑。
    /// </summary>
    public static IReadOnlyList<string> RefreshAuthority(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var ids = ProjectLibrary.Load().Entities.Select(entity => entity.Id).ToHashSet();
        var missing = new List<string>();
        foreach (var entity in canvas.Entities)
        {
            if (!entity.ManagedByProject) continue;
            if (ids.Contains(entity.Id))
            {
                entity.ProjectMissingReason = null;
                continue;
            }

            entity.ProjectMissingReason ??=
                $"{WorkflowEntity.KindName(entity.Kind)}「{entity.Name}」已不在项目库中（可能被删除，或库文件未随项目一起拷贝）";
            missing.Add(entity.ProjectMissingReason);
        }

        return missing;
    }

    /// <summary>
    /// 发布一次改动并如实说明是否真的落盘（目标 6 / G6-T2）：托管实体写库成功才算已持久化；
    /// 本地实体（未迁移）直接返回"成功但未持久化"——它的内容要等画布保存才落盘，调用方不得说"已保存"。
    /// </summary>
    public static PublishOutcome Publish(WorkflowEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.ManagedByProject) return new PublishOutcome(true, false, string.Empty);
        return TryPublish(entity, out var error)
            ? new PublishOutcome(true, true, string.Empty)
            : new PublishOutcome(false, false, error);
    }

    /// <summary>
    /// 把托管实体的当前内容**发布到项目库**（目标 6 / G6-R1）。
    ///
    /// 项目库是权威来源，所以对共享资源的编辑必须落在库上：否则保存画布时
    /// <see cref="RefreshSnapshots"/> 会拿库里的旧内容把这次编辑覆盖掉（界面看似改了、磁盘没有）。
    /// 本地资源（未托管）直接返回 true，不写库。写入失败如实返回原因，调用方负责把内存内容还原。
    /// </summary>
    public static bool TryPublish(WorkflowEntity entity, out string error)
    {
        ArgumentNullException.ThrowIfNull(entity);
        error = string.Empty;
        if (!entity.ManagedByProject) return true;

        var file = ProjectLibrary.Upsert(new[] { entity }, out _, out _);
        if (!ProjectLibrary.TrySave(file, out var saveError))
        {
            error = saveError;
            return false;
        }

        entity.ProjectMissingReason = null;
        return true;
    }

    /// <summary>把 <paramref name="source"/> 的内容整体写回 <paramref name="target"/>（发布失败时还原内存内容用）。</summary>
    public static void RestoreInto(WorkflowEntity target, WorkflowEntity source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Kind = source.Kind;
        target.Name = source.Name;
        target.Aliases = source.Aliases;
        target.Core = source.Core;
        target.Variants = new List<WorkflowEntityVariant>(source.Variants);
        target.ManagedByProject = source.ManagedByProject;
    }

    /// <summary>把画布上的实体标记为项目级（迁移落地时调用）。</summary>
    public static int MarkManaged(WorkflowCanvasState canvas, IEnumerable<Guid> entityIds)
    {
        var ids = entityIds.ToHashSet();
        var count = 0;
        foreach (var entity in canvas.Entities)
        {
            if (!ids.Contains(entity.Id)) continue;
            entity.ManagedByProject = true;
            count++;
        }

        foreach (var entity in WorkflowEntitiesOf(canvas))
        {
            if (!ids.Contains(entity.Id)) continue;
            entity.ManagedByProject = true;
        }

        return count;
    }

    private static IEnumerable<WorkflowEntity> WorkflowEntitiesOf(WorkflowCanvasState canvas) => canvas.Entities;
}

/// <summary>项目库里的一个实体被哪些来源引用（跨画布索引，目标 6 / G6-1、G6-3）。</summary>
public sealed record ProjectEntityUsage(
    WorkflowEntity Entity,
    IReadOnlyList<ReferenceHit> Hits)
{
    public bool HasForeignReference => Hits.Any(hit => hit.IsForeign);

    public string Summary =>
        Hits.Count == 0
            ? "暂无画布引用"
            : string.Join("、", Hits.Select(hit => $"{hit.SourceLabel}（{hit.NodeTitle}）").Distinct().Take(4))
                + (Hits.Select(hit => hit.SourceLabel).Distinct().Count() > 4 ? " 等" : string.Empty);
}

/// <summary>
/// 跨画布索引（目标 6 / G6-1、G6-3）：项目库实体 → 引用它的画布/草稿位置。
/// 复用 <see cref="CanvasReferenceScanner"/> 的只读扫描，不另造一套遍历，避免两处口径不一致。
/// </summary>
public static class ProjectEntityIndex
{
    public static ReferenceScanReport Scan(WorkflowCanvasState current, string? currentPath) =>
        CanvasReferenceScanner.Scan(current, currentPath);

    /// <summary>为一批库实体建立引用索引；返回顺序与传入实体一致。</summary>
    public static IReadOnlyList<ProjectEntityUsage> ForLibrary(
        IReadOnlyList<WorkflowEntity> entities,
        ReferenceScanReport report)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(report);
        return entities
            .Select(entity => new ProjectEntityUsage(
                entity,
                report.Hits.Where(hit => hit.EntityId == entity.Id).ToList()))
            .ToList();
    }

    /// <summary>把索引压成一句人话（面板与迁移预览共用）。</summary>
    public static string Describe(ReferenceScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var canvases = report.Hits.Select(hit => hit.SourceLabel).Distinct().Count();
        var text = canvases == 0
            ? "引用扫描：没有任何画布引用这些资源。"
            : $"引用扫描：已扫描 {report.ScannedCanvases} 个来源，{canvases} 个来源命中 {report.Hits.Count} 条引用。";
        if (report.Skipped.Count > 0) text += $"（{report.Skipped.Count} 个来源打不开，已如实跳过）";
        return text;
    }
}

/// <summary>删除项目库资源前的结果：其它来源还在用就硬阻断。</summary>
public sealed record ProjectEntityDeletionCheck(
    bool Blocked,
    string Message,
    IReadOnlyList<ReferenceHit> Foreign,
    IReadOnlyList<ReferenceHit> Local)
{
    public bool HasReferences => Foreign.Count > 0 || Local.Count > 0;
}

/// <summary>项目库资源的删除保护与回收（目标 6 / G6-3）。</summary>
public static class ProjectEntityDeletion
{
    /// <summary>
    /// 判断能不能删：**其它画布/草稿还在引用时硬阻断**（本画布可以顺带解除引用，交给既有删除流程）。
    /// </summary>
    public static ProjectEntityDeletionCheck Check(ReferenceScanReport report, Guid entityId)
    {
        ArgumentNullException.ThrowIfNull(report);
        var hits = report.Hits.Where(hit => hit.EntityId == entityId).ToList();
        var foreign = hits.Where(hit => hit.IsForeign).ToList();
        var local = hits.Where(hit => !hit.IsForeign).ToList();
        if (foreign.Count > 0)
        {
            var detail = string.Join("、", foreign.Select(hit => $"{hit.SourceLabel}（{hit.NodeTitle}）").Distinct().Take(4));
            return new ProjectEntityDeletionCheck(true,
                $"项目库资源仍被其它来源引用，已阻断删除：{detail}"
                + (foreign.Count > 4 ? " 等" : string.Empty)
                + "。请先在那些画布里解除引用。",
                foreign, local);
        }

        var message = local.Count == 0
            ? "本画布没有引用该资源，可以直接从项目库移除（会进回收站，可还原）。"
            : $"本画布有 {local.Count} 条引用，删除时会一并解除并放进回收站。";
        return new ProjectEntityDeletionCheck(false, message, foreign, local);
    }

    /// <summary>
    /// 从项目库移除一个实体：先备份库文件，再把实体放进回收站（可还原），最后从库中删除。
    /// 任一步失败都不改动库文件。
    /// </summary>
    public static bool TryDeleteFromLibrary(
        WorkflowEntity entity,
        IReadOnlyList<ReferenceHit> references,
        out string error,
        out Guid recycleId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        error = string.Empty;
        recycleId = Guid.Empty;
        if (!ProjectLibrary.TryBackup(out var backupError))
        {
            error = "备份项目库失败，已中止删除：" + backupError;
            return false;
        }

        if (!CanvasRecycleBin.TryStashEntity(entity, references, out var entry, out var stashError))
        {
            error = "写入回收站失败，已中止删除：" + stashError;
            return false;
        }

        recycleId = entry?.Id ?? Guid.Empty;
        if (ProjectLibrary.TryRemove(entity.Id, out var removeError)) return true;
        error = removeError;
        return false;
    }

    /// <summary>把回收站里的实体还原回**项目库**（与画布级还原分开，避免共享资源被还原成某一份画布的本地资源）。</summary>
    public static bool TryRestoreToLibrary(Guid entryId, out string message)
    {
        var entry = CanvasRecycleBin.List().FirstOrDefault(item => item.Id == entryId);
        if (entry is null) { message = "回收站里没有这条记录。"; return false; }
        if (entry.Kind != RecycleEntryKind.Entity) { message = "这条记录不是实体快照，不能用项目库还原。"; return false; }

        WorkflowEntity? entity;
        try
        {
            entity = JsonSerializer.Deserialize<WorkflowEntity>(entry.PayloadJson, RecycleOptions);
        }
        catch (JsonException error) { message = "回收站里的实体快照解析失败：" + error.Message; return false; }
        if (entity is null) { message = "回收站里的实体快照为空。"; return false; }

        entity.ManagedByProject = true;
        var file = ProjectLibrary.Upsert(new[] { entity }, out _, out _);
        if (!ProjectLibrary.TrySave(file, out var saveError)) { message = "写回项目库失败：" + saveError; return false; }
        if (!CanvasRecycleBin.TryPurge(entryId, out var purgeError))
        {
            message = $"「{entity.Name}」已写回项目库，但回收站记录未能清除：{purgeError}";
            return false;
        }

        message = $"已把「{entity.Name}」还原到项目库，原引用会按稳定 ID 重新生效。";
        return true;
    }

    private static readonly JsonSerializerOptions RecycleOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>迁移预览里的一条：哪个资源、会怎么处理、为什么。</summary>
public sealed record ProjectMigrationItem(
    Guid EntityId,
    string KindName,
    string Name,
    string Action,
    string Note);

/// <summary>迁移预览：先看清楚再落盘（目标 6 / G6-2）。</summary>
public sealed record ProjectMigrationPreview(
    IReadOnlyList<ProjectMigrationItem> Items,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Notes,
    int ScannedCanvases,
    int Migrated,
    int AlreadyShared)
{
    public static ProjectMigrationPreview Empty { get; } = new(
        Array.Empty<ProjectMigrationItem>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0, 0);

    public bool HasWork => Migrated > 0;

    public string ToText()
    {
        var lines = new List<string> { $"迁移预览：扫描 {ScannedCanvases} 个来源，将迁移 {Migrated} 个资源（已共享 {AlreadyShared} 个）。" };
        lines.AddRange(Items.Take(12).Select(item => $"· [{item.KindName}] {item.Name} — {item.Action}（{item.Note}）"));
        if (Items.Count > 12) lines.Add($"…… 另有 {Items.Count - 12} 个资源。");
        lines.AddRange(Conflicts.Select(conflict => "同名冲突：" + conflict));
        lines.AddRange(Notes);
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>迁移结果：写了哪些画布、备份在哪、失败原因，以及回滚是否真的完成。</summary>
public sealed record ProjectMigrationOutcome(
    bool Succeeded,
    int Migrated,
    IReadOnlyList<string> WrittenCanvases,
    IReadOnlyList<string> Errors,
    string? LibraryBackup,
    string Message,
    bool RollbackComplete = true);

/// <summary>
/// 旧画布资源迁移（目标 6 / G6-2）：把内嵌在画布里的实体搬进项目库，并让画布改为引用共享资源。
///
/// 硬性要求：
/// · **先预览**：列出每个资源的处置与同名冲突，用户确认后才落盘；
/// · **同名不同实体不合并**：名称相同但 ID 不同的实体各自保留，只在预览里提示，绝不按名字并成一个；
/// · **备份 + 回滚**：写库前备份库文件，改画布前逐个走 <see cref="CanvasBackup"/>；任一步失败整批回滚；
/// · **幂等**：已经共享的实体直接跳过，重复执行不会生成第二份。
/// </summary>
public static class ProjectEntityMigration
{
    /// <summary>收集需要迁移的实体：当前画布 + 画布库画布 + 草稿里所有未托管实体。</summary>
    public static ProjectMigrationPreview Preview(WorkflowCanvasState current, string? currentPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        var items = new List<ProjectMigrationItem>();
        var notes = new List<string>();
        var conflicts = new List<string>();
        var scanned = 0;
        var alreadyShared = 0;
        var seen = new HashSet<Guid>();

        void Collect(WorkflowCanvasState canvas, string sourceLabel)
        {
            scanned++;
            foreach (var entity in canvas.Entities)
            {
                if (entity.ManagedByProject)
                {
                    alreadyShared++;
                    continue;
                }

                if (!seen.Add(entity.Id)) continue;
                items.Add(new ProjectMigrationItem(
                    entity.Id,
                    WorkflowEntity.KindName(entity.Kind),
                    entity.Name,
                    "迁移进项目库",
                    $"{sourceLabel} 的本地资源；迁移后该画布改为引用共享资源"));
            }
        }

        Collect(current, "当前画布");
        foreach (var summary in SafeLibraryList())
        {
            if (!CanvasLibrary.TryLoad(summary.Path, out var state) || state?.Canvas is null) continue;
            if (PathEquals(summary.Path, currentPath)) continue;
            Collect(state.Canvas, summary.Title);
        }

        if (TryLoadDraft(out var draft, out var draftPath) && !PathEquals(draftPath, currentPath))
            Collect(draft, "草稿画布");

        // 同名不同实体：绝不合并，只提示
        foreach (var group in items.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            conflicts.Add($"「{group.Key}」有 {group.Count()} 个不同实体（ID 不同），各自独立迁移、不会合并。");

        if (items.Count == 0) notes.Add("没有需要迁移的本地资源：所有实体都已在项目库里。");
        if (alreadyShared > 0) notes.Add($"另有 {alreadyShared} 个实体已经是项目级共享资源，本次跳过（幂等）。");
        return new ProjectMigrationPreview(items, conflicts, notes, scanned, items.Count, alreadyShared);
    }

    /// <summary>
    /// 执行迁移：备份 → 写库 → 逐个画布标记并保存；失败整批回滚（库文件与已写入的画布都恢复原状）。
    /// </summary>
    public static ProjectMigrationOutcome Apply(WorkflowCanvasState current, string? currentPath) =>
        Apply(current, currentPath, null);

    // 仅供隔离回归在真实 Apply 写入边界注入文件系统故障；生产入口不传回调。
    internal static ProjectMigrationOutcome Apply(WorkflowCanvasState current, string? currentPath, Action<string, int>? afterCanvasWritten)
    {
        ArgumentNullException.ThrowIfNull(current);
        var preview = Preview(current, currentPath);
        if (!preview.HasWork)
            return new ProjectMigrationOutcome(true, 0, Array.Empty<string>(), Array.Empty<string>(),
                null, preview.ToText());

        var errors = new List<string>();
        var written = new List<string>();
        var originalBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 复核 G6-R2：回滚必须能恢复「库原先是否存在」与「内存里的托管标记」，否则首次迁移失败会留下半成品。
        var hadLibrary = File.Exists(ProjectLibrary.FilePath);
        var managedBefore = current.Entities.ToDictionary(entity => entity.Id, entity => entity.ManagedByProject);

        // 1) 备份现有库文件
        string? backupBefore = null;
        if (hadLibrary)
        {
            if (!ProjectLibrary.TryBackup(out backupBefore, out var backupError) || backupBefore is null)
            {
                errors.Add("备份项目库失败：" + (backupBefore is null && string.IsNullOrEmpty(backupError) ? "未获得本轮备份路径" : backupError));
                return new ProjectMigrationOutcome(false, 0, written, errors, null, "迁移未开始：项目库备份失败。");
            }
        }

        // 2) 收集实体并幂等写库（同一实体可能出现在多份画布里：按 ID 去重后写库，标记则逐份画布做）
        var toMigrate = new List<(Guid Id, WorkflowEntity Entity, WorkflowCanvasState Canvas, string Path)>();
        CollectTargets(current, currentPath, toMigrate);
        var distinctEntities = toMigrate.GroupBy(item => item.Id).Select(group => group.First().Entity).ToList();
        var file = ProjectLibrary.Upsert(distinctEntities, out var added, out var updated);
        if (!ProjectLibrary.TrySave(file, out var saveError))
        {
            errors.Add("写入项目库失败（" + saveError + "），开始回滚。");
            return FailedOutcome(errors, backupBefore, hadLibrary, written, originalBackups, current, managedBefore);
        }

        // 3) 标记并保存每一份受影响的画布（逐份备份，失败即整批回滚）
        foreach (var group in toMigrate.GroupBy(item => item.Path))
        {
            var path = group.Key;
            var ids = group.Select(item => item.Id).ToList();
            ProjectEntityScope.MarkManaged(current, ids);
            if (string.IsNullOrWhiteSpace(path)) continue;   // 当前画布未落盘：等用户保存，不代写

            if (!CanvasLibrary.TryLoad(path, out var state) || state?.Canvas is null)
            {
                errors.Add($"{Path.GetFileName(path)}：画布读取失败，开始回滚。");
                return FailedOutcome(errors, backupBefore, hadLibrary, written, originalBackups, current, managedBefore);
            }

            ProjectEntityScope.MarkManaged(state.Canvas, ids);
            var originalBackup = CanvasBackup.TryBackup(path, out var canvasBackupError);
            if (canvasBackupError is not null)
            {
                errors.Add($"{Path.GetFileName(path)}：备份失败（{canvasBackupError}），开始回滚。");
                return FailedOutcome(errors, backupBefore, hadLibrary, written, originalBackups, current, managedBefore);
            }
            if (originalBackup is null)
            {
                errors.Add($"{Path.GetFileName(path)}：找不到本轮备份，开始回滚。");
                return FailedOutcome(errors, backupBefore, hadLibrary, written, originalBackups, current, managedBefore);
            }
            originalBackups[path] = originalBackup;

            try
            {
                CanvasSaveService.Save(state, path);
                written.Add(path);
                afterCanvasWritten?.Invoke(path, written.Count);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CanvasSaveAbortedException)
            {
                errors.Add($"{Path.GetFileName(path)}：写入失败（{error.Message}），开始回滚。");
                return FailedOutcome(errors, backupBefore, hadLibrary, written, originalBackups, current, managedBefore);
            }
        }

        var message = $"迁移完成：新入库 {added} 个、更新 {updated} 个资源；"
            + (written.Count == 0 ? "当前画布尚未落盘，保存后即以共享资源方式写入。" : $"已更新 {written.Count} 份画布文件。");
        return new ProjectMigrationOutcome(true, added + updated, written, errors, backupBefore, message);
    }

    private static void CollectTargets(
        WorkflowCanvasState current,
        string? currentPath,
        List<(Guid Id, WorkflowEntity Entity, WorkflowCanvasState Canvas, string Path)> targets)
    {
        // 同一实体可能在多份画布里各有一份本地副本：**每份都要标记**，否则迁移完另一份还会被再迁一次
        // （看起来像"没有幂等"）。预览按资源去重，这里按「资源 × 画布」去重。
        var seen = new HashSet<(Guid EntityId, string Path)>();
        void Add(WorkflowCanvasState canvas, string? path)
        {
            var key = path ?? string.Empty;
            foreach (var entity in canvas.Entities)
            {
                if (entity.ManagedByProject) continue;
                if (!seen.Add((entity.Id, key))) continue;
                targets.Add((entity.Id, entity, canvas, key));
            }
        }

        Add(current, currentPath);
        foreach (var summary in SafeLibraryList())
        {
            if (PathEquals(summary.Path, currentPath)) continue;
            if (!CanvasLibrary.TryLoad(summary.Path, out var state) || state?.Canvas is null) continue;
            Add(state.Canvas, summary.Path);
        }

        if (TryLoadDraft(out var draft, out var draftPath) && !PathEquals(draftPath, currentPath))
            Add(draft, draftPath);
    }

    /// <summary>
    /// 回滚并如实汇报（复核补证）：回滚自身没做完时，明确说"未完全成功，请人工检查"，
    /// 绝不把恢复失败报告成"已回滚"。
    /// </summary>
    private static ProjectMigrationOutcome FailedOutcome(
        List<string> errors,
        string? libraryBackup,
        bool hadLibrary,
        IReadOnlyList<string> written,
        IReadOnlyDictionary<string, string> originalBackups,
        WorkflowCanvasState current,
        IReadOnlyDictionary<Guid, bool> managedBefore)
    {
        var rollbackFailures = Rollback(hadLibrary, libraryBackup, written, originalBackups, current, managedBefore);
        errors.AddRange(rollbackFailures);
        var message = rollbackFailures.Count == 0
            ? "迁移失败：已回滚到迁移前状态（含项目库与内存标记）。"
            : "迁移失败，但回滚未完全成功，请人工检查项目库与画布文件：" + string.Join("；", rollbackFailures);
        return new ProjectMigrationOutcome(
            false, 0, rollbackFailures.Count == 0 ? Array.Empty<string>() : written.ToArray(),
            errors, libraryBackup, message, rollbackFailures.Count == 0);
    }

    /// <summary>
    /// 整批回滚（复核 G6-R2 与补证）：项目库、已写入的画布文件、内存里的托管标记三样都要回到迁移前。
    /// 返回**回滚自身的失败项**——回滚失败绝不能被当成"已回滚"上报（复核明确要求如实反馈）。
    /// </summary>
    private static List<string> Rollback(
        bool hadLibrary,
        string? libraryBackup,
        IReadOnlyList<string> writtenCanvases,
        IReadOnlyDictionary<string, string> originalBackups,
        WorkflowCanvasState current,
        IReadOnlyDictionary<Guid, bool> managedBefore)
    {
        var failures = new List<string>();
        if (hadLibrary)
        {
            if (libraryBackup is null)
                failures.Add("项目库原本存在，但没有找到可用备份，库文件可能停在迁移后的内容。");
            else if (ProjectLibrary.RestoreFromBackup(libraryBackup) is { } restoreError)
                failures.Add("恢复项目库失败：" + restoreError);
        }
        else if (!ProjectLibrary.TryDeleteFile(out var deleteError))
        {
            failures.Add("删除本次新建的项目库失败：" + deleteError);
        }

        foreach (var path in writtenCanvases)
            if (!originalBackups.TryGetValue(path, out var backup))
                failures.Add($"{Path.GetFileName(path)}：找不到本轮原始备份，无法恢复。");
            else if (RestoreOriginalBackup(path, backup) is { } canvasError)
                failures.Add(canvasError);

        foreach (var entity in current.Entities)
            if (managedBefore.TryGetValue(entity.Id, out var wasManaged)) entity.ManagedByProject = wasManaged;

        return failures;
    }

    private static string? RestoreOriginalBackup(string canvasPath, string backup)
    {
        try
        {
            CanvasBackup.Restore(backup, canvasPath);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(canvasPath)}：恢复失败（{error.Message}）。";
        }
    }

    private static IReadOnlyList<CanvasSummary> SafeLibraryList()
    {
        try { return CanvasLibrary.List(); }
        catch (InvalidOperationException) { return Array.Empty<CanvasSummary>(); }
    }

    private static bool TryLoadDraft(out WorkflowCanvasState canvas, out string? path)
    {
        canvas = null!;
        path = null;
        try
        {
            path = Path.GetFullPath(CoreStoragePaths.DraftCanvasPath);
            if (!CanvasLibrary.TryLoad(path, out var state) || state?.Canvas is null) return false;
            canvas = state.Canvas;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
