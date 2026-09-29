using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>导入方式：替换当前画布，或按 ID 合并进当前画布。</summary>
public enum CanvasImportMode { Replace, Merge }

/// <summary>导入冲突：同一个 ID 在目标与来源里内容不同，或 ID 已被别的对象占用。</summary>
public sealed record ImportConflict(string ObjectType, string ObjectId, string Path, string Reason)
{
    public override string ToString() => $"[冲突] {ObjectType}({ObjectId}) {Path}：{Reason}";
}

/// <summary>合并导入的报告：追加了多少、跳过多少（已导入）、哪些冲突与提示。</summary>
public sealed record ImportMergeReport(
    int AddedNodes,
    int AddedEdges,
    int AddedEntities,
    int AddedWorkTreeItems,
    int SkippedNodes,
    int SkippedEdges,
    int SkippedEntities,
    int SkippedWorkTreeItems,
    IReadOnlyList<ImportConflict> Conflicts,
    IReadOnlyList<string> Notes)
{
    /// <summary>是否真的往目标画布里加了东西；重复导入同一份数据时为 false。</summary>
    public bool Changed => AddedNodes + AddedEdges + AddedEntities + AddedWorkTreeItems > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    public string ToText()
    {
        var lines = new List<string>
        {
            $"合并结果：新增 节点 {AddedNodes} / 连线 {AddedEdges} / 实体 {AddedEntities} / 工作树 {AddedWorkTreeItems}；"
            + $"已存在而跳过 节点 {SkippedNodes} / 连线 {SkippedEdges} / 实体 {SkippedEntities} / 工作树 {SkippedWorkTreeItems}；"
            + $"冲突 {Conflicts.Count} 项。"
        };
        lines.AddRange(Conflicts.Select(conflict => conflict.ToString()));
        lines.AddRange(Notes.Select(note => "· " + note));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>导入后章节/工作树的状况摘要（大目标 B：章节元数据与同步冲突随包一起检查）。</summary>
public sealed record CanvasChapterImportSummary(int ChapterCount, int ChapterIssues, int SyncConflicts, string Text);

/// <summary>导入结果：最终画布状态、合并报告、迁移与校验报告、资产统计。</summary>
public sealed record CanvasImportOutcome(
    RecentCanvasState State,
    ImportMergeReport? Merge,
    MigrationReport Migration,
    CanvasIdentityReport Validation,
    int ImportedAssets,
    int MissingAssets,
    string SourcePath)
{
    /// <summary>导入后的章节与工作树状况；章节顺序等元数据随画布文件一起进出，无需单独迁移。</summary>
    public CanvasChapterImportSummary Chapters { get; init; } = new(0, 0, 0, string.Empty);
}

/// <summary>
/// 重复导入去重（目标 1.4）。判定身份只按 ID 与作用域，绝不按名称：
/// · 目标里已有同 ID 且内容一致 → 视为已导入，跳过（重复导入不增量）；
/// · 同 ID 但内容不同 → 记为冲突，保留目标内容，不静默覆盖；
/// · 新 ID → 追加；同名不同 ID → 各自保留，并在报告中提示未按名称去重；
/// · 变体 ID 已被别的实体占用时整条实体不合并，避免把设定挂到错误实体下。
/// </summary>
public static class CanvasImportMerge
{
    private static readonly JsonSerializerOptions Options = new();

    public static ImportMergeReport Merge(WorkflowCanvasState target, WorkflowCanvasState incoming)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(incoming);

        var conflicts = new List<ImportConflict>();
        var notes = new List<string>();

        var existingVariantIds = target.Entities.SelectMany(entity => entity.Variants).Select(variant => variant.Id).ToHashSet();
        var incomingVersionIds = incoming.Entities.SelectMany(entity => entity.Variants).SelectMany(variant => variant.Versions).Select(version => version.Id).ToHashSet();
        var existingVersionIds = target.Entities.SelectMany(entity => entity.Variants).SelectMany(variant => variant.Versions).Select(version => version.Id).ToHashSet();
        var existingNames = target.Entities.Select(entity => entity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedEntities = 0;
        var skippedEntities = 0;
        foreach (var entity in incoming.Entities)
        {
            var path = $"Entities[{incoming.Entities.IndexOf(entity)}]";
            var same = target.Entities.FirstOrDefault(candidate => candidate.Id == entity.Id);
            if (same is not null)
            {
                if (Signature(same) == Signature(entity)) { skippedEntities++; continue; }
                conflicts.Add(new ImportConflict("WorkflowEntity", entity.Id.ToString(), path,
                    $"同 ID 的实体在目标画布中已存在但内容不同（目标「{same.Name}」/ 来源「{entity.Name}」），保留目标内容，未覆盖。"));
                skippedEntities++;
                continue;
            }

            var variantClash = entity.Variants.FirstOrDefault(variant => existingVariantIds.Contains(variant.Id));
            if (variantClash is not null)
            {
                conflicts.Add(new ImportConflict("WorkflowEntity", entity.Id.ToString(), path,
                    $"来源实体的变体 {variantClash.Id} 已被目标画布中的其它实体占用，整条实体未合并，避免设定挂到错误实体下。"));
                skippedEntities++;
                continue;
            }

            target.Entities.Add(entity);
            addedEntities++;
            foreach (var variant in entity.Variants) existingVariantIds.Add(variant.Id);

            if (existingNames.Contains(entity.Name))
                notes.Add($"实体「{entity.Name}」与目标画布中的同名实体 ID 不同，已各自保留（不按名称合并）。");
        }

        if (incomingVersionIds.Overlaps(existingVersionIds))
            notes.Add("来源与目标存在相同的版本 ID（版本作用域是所属变体）；相关锁定的版本会按所属变体解析，校验器会提示歧义。");

        var addedNodes = 0;
        var skippedNodes = 0;
        var entityIds = target.Entities.Select(item => item.Id).ToHashSet();
        foreach (var node in incoming.Nodes)
        {
            var path = $"Nodes[{incoming.Nodes.IndexOf(node)}]";
            var same = target.Nodes.FirstOrDefault(candidate => candidate.Id == node.Id);
            if (same is not null)
            {
                if (Signature(same) == Signature(node)) { skippedNodes++; continue; }
                conflicts.Add(new ImportConflict("WorkflowNode", node.Id.ToString(), path,
                    $"同 ID 的节点在目标画布中已存在但内容不同（目标「{same.Title}」/ 来源「{node.Title}」），保留目标内容。"));
                skippedNodes++;
                continue;
            }

            target.Nodes.Add(node);
            addedNodes++;
            foreach (var reference in node.References)
            {
                if (reference.EntityId != Guid.Empty && !entityIds.Contains(reference.EntityId))
                    notes.Add($"节点「{node.Title}」引用的实体 {reference.EntityId} 不在合并后的画布中，引用按原样保留，等待人工处理。");
            }
        }

        var addedEdges = 0;
        var skippedEdges = 0;
        var nodeIds = target.Nodes.Select(node => node.Id).ToHashSet();
        foreach (var edge in incoming.Edges)
        {
            var path = $"Edges[{incoming.Edges.IndexOf(edge)}]";
            var same = target.Edges.FirstOrDefault(candidate => candidate.Id == edge.Id);
            if (same is not null)
            {
                if (Signature(same) == Signature(edge)) { skippedEdges++; continue; }
                conflicts.Add(new ImportConflict("WorkflowEdge", edge.Id.ToString(), path, "同 ID 的连线已存在但两端不同，保留目标内容。"));
                skippedEdges++;
                continue;
            }

            target.Edges.Add(edge);
            addedEdges++;
            if (!nodeIds.Contains(edge.SourceNodeId) || !nodeIds.Contains(edge.TargetNodeId))
                notes.Add($"连线 {edge.Id} 的端点不在合并后的画布中，保留但需要人工处理。");
        }

        var addedWorkTree = 0;
        var skippedWorkTree = 0;
        foreach (var item in incoming.WorkTree)
        {
            var path = $"WorkTree[{incoming.WorkTree.IndexOf(item)}]";
            var same = target.WorkTree.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (same is not null)
            {
                if (Signature(same) == Signature(item)) { skippedWorkTree++; continue; }
                conflicts.Add(new ImportConflict("WorkTreeItem", item.Id.ToString(), path,
                    $"同 ID 的工作树条目已存在但内容不同（目标「{same.Name}」/ 来源「{item.Name}」），保留目标内容。"));
                skippedWorkTree++;
                continue;
            }

            target.WorkTree.Add(item);
            addedWorkTree++;
        }

        return new ImportMergeReport(
            addedNodes, addedEdges, addedEntities, addedWorkTree,
            skippedNodes, skippedEdges, skippedEntities, skippedWorkTree,
            conflicts, notes);
    }

    /// <summary>
    /// 内容指纹：同一个对象序列化后的字符串，用来区分「已导入」与「同 ID 不同内容」。
    /// 只有参与身份与引用的字段算进指纹：附件、布局元素、生成历史的 ID 不参与查找，
    /// 而迁移会为空 ID 生成随机新 ID，若把它们算进指纹，同一份数据二次导入会变成假冲突。
    /// </summary>
    private static string Signature<T>(T value)
    {
        var clone = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options);
        if (clone is null) return string.Empty;
        ResetLooseIds(clone);
        return JsonSerializer.Serialize(clone, Options);
    }

    private static void ResetLooseIds(object? value)
    {
        switch (value)
        {
            case WorkflowNode node:
                foreach (var attachment in node.Attachments) ResetAttachment(attachment);
                foreach (var record in node.GenerationHistory) record.Id = Guid.Empty;
                break;
            case WorkTreeItem item:
                foreach (var attachment in item.Attachments) ResetAttachment(attachment);
                break;
            case WorkflowEntity entity:
                foreach (var variant in entity.Variants)
                {
                    foreach (var attachment in variant.Attachments) ResetAttachment(attachment);
                    ResetLooseIds(variant.Layout);
                    foreach (var version in variant.Versions)
                    {
                        foreach (var attachment in version.Attachments) ResetAttachment(attachment);
                        ResetLooseIds(version.Layout);
                    }
                }

                break;
            case SceneLayout layout:
                foreach (var item in layout.Items) item.Id = Guid.Empty;
                break;
        }
    }

    /// <summary>
    /// 附件的 ID 与添加时间都不参与身份判定：ID 不参与查找，时间戳由迁移时的机器时钟决定。
    /// 若把它们算进指纹，同一份来源连续迁移会因随机 ID / 时间戳不同而被误判成「同 ID 不同内容」。
    /// </summary>
    private static void ResetAttachment(WorkflowAttachment attachment)
    {
        attachment.Id = Guid.Empty;
        attachment.AddedAt = default;
    }
}

/// <summary>
/// 导入画布包的真实入口（目标 1.2 / 1.4）：读取文件 → 补齐资产 → 迁移来源画布 →
/// 按选择替换或合并 → 校验合并结果。合并只作用于目标画布的副本，调用方持有的对象不被就地改写。
/// </summary>
public static class CanvasImportService
{
    public static bool TryImport(
        string canvasFilePath,
        RecentCanvasState target,
        CanvasImportMode mode,
        out CanvasImportOutcome outcome,
        out string error)
    {
        outcome = null!;
        error = string.Empty;

        try
        {
            // 两种方式都要把包内资产补进本机资产目录（已存在的会跳过，重复导入不增量）。
            // ImportDetailed 会在包内容副本上先迁移再收集资产，旧字段里的资产同样会被复制（返工 R9）。
            var read = CanvasPackage.ImportDetailed(canvasFilePath);

            if (read.State.FormatVersion > CanvasFormat.Current)
            {
                error = $"来源画布的格式版本是 {read.State.FormatVersion}，高于当前支持的 {CanvasFormat.Current}；已拒绝导入，避免误读或覆盖未知数据。";
                return false;
            }

            if (target.FormatVersion > CanvasFormat.Current)
            {
                error = $"当前画布的格式版本是 {target.FormatVersion}，高于当前支持的 {CanvasFormat.Current}；已拒绝导入，避免覆盖未知数据。";
                return false;
            }

            if (mode == CanvasImportMode.Replace)
            {
                var replaced = read.State with { FormatVersion = CanvasFormat.Current };
                var replacedValidation = CanvasIdentityValidator.Validate(replaced.Canvas ?? new WorkflowCanvasState(), AssetStore.Exists);
                outcome = new CanvasImportOutcome(replaced, null, read.Migration, replacedValidation, read.Imported, read.Missing, canvasFilePath)
                {
                    Chapters = SummarizeChapters(replaced.Canvas ?? new WorkflowCanvasState())
                };
                return true;
            }

            var canvas = CanvasCloner.Clone(target.Canvas ?? new WorkflowCanvasState());
            var report = CanvasImportMerge.Merge(canvas, read.State.Canvas ?? new WorkflowCanvasState());
            var merged = target with { Canvas = canvas, FormatVersion = CanvasFormat.Current };
            var validation = CanvasIdentityValidator.Validate(merged.Canvas, AssetStore.Exists);

            outcome = new CanvasImportOutcome(merged, report, read.Migration, validation, read.Imported, read.Missing, canvasFilePath)
            {
                Chapters = SummarizeChapters(merged.Canvas)
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 导入后立刻检查章节与工作树：章节数、章节诊断问题与双向同步冲突。
    /// 章节顺序等元数据随画布文件进出，不需要额外迁移；这里保证导入后冲突可见。
    /// </summary>
    private static CanvasChapterImportSummary SummarizeChapters(WorkflowCanvasState canvas)
    {
        var chapters = CanvasChapters.List(canvas);
        var issues = CanvasChapters.Diagnose(canvas);
        var plan = CanvasWorkTreeSync.Plan(canvas, SyncDirection.Both);
        var lines = new List<string>
        {
            $"章节：{chapters.Count} 个，章节诊断问题 {issues.Count} 项，同步冲突 {plan.Conflicts.Count} 项"
            + (plan.HasBlockingConflicts ? "（含阻断项，需人工处理）" : string.Empty)
        };
        lines.AddRange(issues.Take(5).Select(issue => "· " + issue));
        lines.AddRange(plan.Conflicts.Take(5).Select(conflict => "· " + conflict));
        return new CanvasChapterImportSummary(chapters.Count, issues.Count, plan.Conflicts.Count, string.Join(Environment.NewLine, lines));
    }
}
