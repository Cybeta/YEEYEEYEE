namespace DreamForge.Desktop;

/// <summary>引用所在的来源范围（目标 4 / 4.1、4.3）。</summary>
public enum ReferenceScopeKind
{
    /// <summary>当前打开的画布。</summary>
    Canvas,

    /// <summary>画布库里的其它已保存画布。</summary>
    Library,

    /// <summary>未保存到画布库的草稿画布（last-canvas.json）。</summary>
    Draft
}

/// <summary>一条引用命中：哪个来源的哪个节点引用了哪个实体/变体/版本。</summary>
public sealed record ReferenceHit(
    ReferenceScopeKind Scope,
    string SourceLabel,
    string? SourcePath,
    Guid NodeId,
    string NodeTitle,
    Guid EntityId,
    Guid VariantId,
    Guid? VariantVersionId,
    bool LockedVersionMissing)
{
    /// <summary>其它画布/草稿里的引用无法由本画布代为改写，删除时必须硬阻断。</summary>
    public bool IsForeign => Scope != ReferenceScopeKind.Canvas;

    public string VersionLabel =>
        VariantVersionId is null ? "跟随最新"
        : LockedVersionMissing ? "锁定版本缺失"
        : "锁定版本";
}

/// <summary>引用扫描报告。打不开的来源如实记入 <see cref="Skipped"/>，不静默跳过。</summary>
public sealed record ReferenceScanReport(
    IReadOnlyList<ReferenceHit> Hits,
    int ScannedCanvases,
    IReadOnlyList<string> Skipped)
{
    public static ReferenceScanReport Empty { get; } = new(Array.Empty<ReferenceHit>(), 0, Array.Empty<string>());
}

/// <summary>
/// 引用扫描（目标 4 / 4.1、4.3）：把「谁引用了这个设定」从当前画布扩展到画布库与草稿。
/// 只读：不修改任何画布文件，也不生成替代引用。
/// </summary>
public static class CanvasReferenceScanner
{
    /// <summary>
    /// 扫描当前画布 + 画布库里其它画布 + 草稿画布里的全部引用。
    /// <paramref name="includeLibrary"/> / <paramref name="includeDraft"/> 用于在只想看当前画布时关闭外圈扫描。
    /// </summary>
    public static ReferenceScanReport Scan(
        WorkflowCanvasState current,
        string? currentPath,
        bool includeLibrary = true,
        bool includeDraft = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        var hits = new List<ReferenceHit>();
        var skipped = new List<string>();
        var scanned = 0;

        Collect(current, ReferenceScopeKind.Canvas, "当前画布", currentPath, hits);
        scanned++;

        var currentFull = FullPath(currentPath);
        // 未打开项目时项目内路径不可解析：当作「没有草稿/没有画布库」继续扫描，而不是抛异常。
        string? draftPath = null;
        try { draftPath = FullPath(StorageMaintenance.DraftCanvasPath); }
        catch (InvalidOperationException) { }

        if (includeDraft && draftPath is not null && !string.Equals(draftPath, currentFull, StringComparison.OrdinalIgnoreCase))
        {
            // 草稿不存在是正常情况（还没生成草稿），不记为「打不开」；存在却读不了才算跳过。
            if (File.Exists(draftPath))
            {
                if (TryReadState(draftPath, out var draft, out var error))
                {
                    Collect(draft, ReferenceScopeKind.Draft, "草稿画布", draftPath, hits);
                    scanned++;
                }
                else
                {
                    skipped.Add($"草稿画布：{error}");
                }
            }
        }

        if (includeLibrary)
        {
            IReadOnlyList<CanvasSummary> summaries;
            try { summaries = CanvasLibrary.List(); }
            catch (InvalidOperationException) { summaries = Array.Empty<CanvasSummary>(); }

            foreach (var summary in summaries)
            {
                var path = FullPath(summary.Path);
                if (path is null || string.Equals(path, currentFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (draftPath is not null && string.Equals(path, draftPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryReadState(path, out var state, out var error))
                {
                    Collect(state, ReferenceScopeKind.Library, summary.Title, path, hits);
                    scanned++;
                }
                else if (error.Length > 0)
                {
                    skipped.Add($"{summary.Title}：{error}");
                }
            }
        }

        return new ReferenceScanReport(hits, scanned, skipped);
    }

    public static IReadOnlyList<ReferenceHit> ForEntity(ReferenceScanReport report, Guid entityId)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Hits.Where(hit => hit.EntityId == entityId).ToList();
    }

    public static IReadOnlyList<ReferenceHit> ForVariant(ReferenceScanReport report, Guid entityId, Guid variantId)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Hits.Where(hit => hit.EntityId == entityId && hit.VariantId == variantId).ToList();
    }

    /// <summary>把命中按范围分组，用于界面展示与删除前提示。</summary>
    public static IReadOnlyList<(ReferenceScopeKind Scope, IReadOnlyList<ReferenceHit> Hits)> GroupByScope(IReadOnlyList<ReferenceHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return hits
            .GroupBy(hit => hit.Scope)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, (IReadOnlyList<ReferenceHit>)group.ToList()))
            .ToList();
    }

    internal static void Collect(
        WorkflowCanvasState canvas,
        ReferenceScopeKind scope,
        string sourceLabel,
        string? sourcePath,
        List<ReferenceHit> hits)
    {
        foreach (var node in canvas.Nodes)
        {
            foreach (var reference in node.References)
            {
                var variant = canvas.FindVariant(reference.VariantId);
                var missing = reference.VariantVersionId is { } versionId
                    && (variant is null || variant.FindVersion(versionId) is null);
                hits.Add(new ReferenceHit(
                    scope,
                    sourceLabel,
                    sourcePath,
                    node.Id,
                    node.Title,
                    reference.EntityId,
                    reference.VariantId,
                    reference.VariantVersionId,
                    missing));
            }
        }
    }

    private static bool TryReadState(string path, out WorkflowCanvasState state, out string error)
    {
        state = new WorkflowCanvasState();
        error = string.Empty;
        if (!CanvasOpenService.TryRead(path, out var recent, out error)) return false;
        state = recent.Canvas ?? new WorkflowCanvasState();
        return true;
    }

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

/// <summary>删除前的引用检查结果。</summary>
public sealed record ReferenceGuardResult(
    bool CanDelete,
    bool HardBlocked,
    string Message,
    IReadOnlyList<ReferenceHit> Hits)
{
    public bool HasReferences => Hits.Count > 0;

    /// <summary>本画布内的引用数量：这些可以由用户选择「解除引用并删除」。</summary>
    public int LocalCount => Hits.Count(hit => !hit.IsForeign);

    /// <summary>其它画布/草稿里的引用数量：必须先到那边处理，本画布不能代为改写。</summary>
    public int ForeignCount => Hits.Count(hit => hit.IsForeign);
}

/// <summary>
/// 删除保护（目标 4 / 4.3）：存在引用时阻止破坏性删除。
/// 规则：本画布内的引用可以显式「解除引用并删除」；其它画布/草稿里的引用硬阻断。
/// </summary>
public static class CanvasDeletionGuard
{
    public static ReferenceGuardResult CheckEntity(ReferenceScanReport report, WorkflowEntity entity)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(entity);
        return Build(CanvasReferenceScanner.ForEntity(report, entity.Id), $"实体「{entity.Name}」");
    }

    public static ReferenceGuardResult CheckVariant(ReferenceScanReport report, WorkflowEntity entity, WorkflowEntityVariant variant)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(variant);
        return Build(CanvasReferenceScanner.ForVariant(report, entity.Id, variant.Id), $"变体「{entity.Name} · {variant.Name}」");
    }

    private static ReferenceGuardResult Build(IReadOnlyList<ReferenceHit> hits, string subject)
    {
        var foreign = hits.Where(hit => hit.IsForeign).ToList();
        if (foreign.Count > 0)
        {
            var sources = string.Join("、", foreign.Select(hit => hit.SourceLabel).Distinct(StringComparer.Ordinal));
            return new ReferenceGuardResult(
                CanDelete: false,
                HardBlocked: true,
                $"不能删除{subject}：其它画布/草稿仍在引用它（{foreign.Count} 处，来自 {sources}）。"
                + "请先在那些画布中解除引用，再回到这里删除。",
                hits);
        }

        if (hits.Count > 0)
        {
            return new ReferenceGuardResult(
                CanDelete: true,
                HardBlocked: false,
                $"{subject}仍被本画布 {hits.Count} 个节点引用。继续删除会同时解除这些引用（节点本身与文本保留）。",
                hits);
        }

        return new ReferenceGuardResult(true, false, $"{subject}没有被任何画布引用。", hits);
    }

    /// <summary>
    /// 在给定画布上解除对某实体（或某变体）的全部引用，返回解除条数。
    /// 只作用于传入的画布，绝不改写其它画布文件。
    /// </summary>
    public static int RemoveReferencesIn(WorkflowCanvasState canvas, Guid entityId, Guid? variantId = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var removed = 0;
        foreach (var node in canvas.Nodes)
        {
            removed += node.References.RemoveAll(reference =>
                reference.EntityId == entityId && (variantId is null || reference.VariantId == variantId));
        }

        return removed;
    }

    /// <summary>
    /// 带保护的删除实体（目标 4 / 4.3、4.4）：**先把实体快照写进回收站，写盘成功之后**才解除引用并从画布移除。
    /// 任一步失败都保持画布原样并返回 false，绝不出现「快照没落盘但数据已删」的不可恢复状态。
    /// </summary>
    public static bool TryDeleteEntity(
        WorkflowCanvasState canvas,
        WorkflowEntity entity,
        IReadOnlyList<ReferenceHit> references,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(entity);
        if (!CanvasRecycleBin.TryStashEntity(entity, references, out _, out error)) return false;
        RemoveReferencesIn(canvas, entity.Id);
        canvas.Entities.Remove(entity);
        return true;
    }

    /// <summary>带保护的删除变体：先写回收站快照，成功后才解除引用并移除变体。</summary>
    public static bool TryDeleteVariant(
        WorkflowCanvasState canvas,
        WorkflowEntity entity,
        WorkflowEntityVariant variant,
        IReadOnlyList<ReferenceHit> references,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(variant);
        if (entity.Variants.Count <= 1)
        {
            error = "每个实体至少保留一个变体。";
            return false;
        }

        if (!CanvasRecycleBin.TryStashVariant(entity, variant, references, out _, out error)) return false;
        RemoveReferencesIn(canvas, entity.Id, variant.Id);
        entity.Variants.Remove(variant);
        return true;
    }
}

/// <summary>
/// 版本锁定策略（目标 4 / 4.2）：锁定的版本必须真实存在；缺失时只能判定为「阻断」，
/// 不允许静默按变体当前内容继续使用。
/// </summary>
public static class CanvasReferenceVersions
{
    /// <summary>该引用锁定的版本是否已缺失（锁定了一个不存在的版本）。</summary>
    public static bool IsLockedVersionMissing(WorkflowCanvasState canvas, NodeReference reference)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.VariantVersionId is not { } versionId) return false;
        var variant = canvas.FindVariant(reference.VariantId);
        return variant is null || variant.FindVersion(versionId) is null;
    }

    /// <summary>该引用指向的**项目级资源**是否已从项目库中消失（目标 6 / G6-R3）。</summary>
    public static bool IsAuthoritativeMissing(WorkflowCanvasState canvas, NodeReference reference)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(reference);
        var entity = reference.EntityId != Guid.Empty ? canvas.FindEntity(reference.EntityId) : null;
        entity ??= reference.VariantId != Guid.Empty
            ? canvas.Entities.FirstOrDefault(candidate => candidate.Variants.Any(variant => variant.Id == reference.VariantId))
            : null;
        return entity?.IsProjectMissing == true;
    }

    /// <summary>
    /// 该引用是否处于阻断状态：锁定版本缺失，**或**它指向的项目级资源已不在项目库里。
    /// 后者即使画布里还留着旧快照，也不允许当有效资源使用。
    /// </summary>
    public static bool IsBlocked(WorkflowCanvasState canvas, NodeReference reference) =>
        IsLockedVersionMissing(canvas, reference) || IsAuthoritativeMissing(canvas, reference);

    /// <summary>该节点上所有处于阻断状态的引用（锁定版本缺失 / 项目级资源缺失）。</summary>
    public static IReadOnlyList<NodeReference> BlockedReferences(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        return node.References.Where(reference => IsBlocked(canvas, reference)).ToList();
    }

    /// <summary>节点是否处于版本缺失的阻断状态。</summary>
    public static bool IsNodeBlocked(WorkflowCanvasState canvas, WorkflowNode node) =>
        BlockedReferences(canvas, node).Count > 0;

    /// <summary>
    /// 可安全使用的引用内容：排除「锁定版本缺失」的引用。
    /// 出图等消耗引用的地方用它取内容，就不会拿变体当前内容顶替锁定的那一版（目标 4 / 4.2）。
    /// </summary>
    public static IReadOnlyList<ReferenceContent> UsableContents(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        return canvas.ResolveReferences(node).Where(content => !content.VersionMissing).ToList();
    }

    /// <summary>阻断说明；节点未被阻断时返回空串。</summary>
    public static string DescribeBlock(WorkflowCanvasState canvas, WorkflowNode node)
    {
        var blocked = BlockedReferences(canvas, node);
        if (blocked.Count == 0) return string.Empty;

        string Label(NodeReference reference)
        {
            var entity = canvas.FindEntity(reference.EntityId);
            var variant = canvas.FindVariant(reference.VariantId);
            return $"{entity?.Name ?? "（已删除的设定）"} · {variant?.Name ?? "（已删除的变体）"}";
        }

        var locked = blocked.Where(reference => IsLockedVersionMissing(canvas, reference)).ToList();
        var authoritative = blocked.Where(reference => IsAuthoritativeMissing(canvas, reference)).ToList();
        var reasons = new List<string>();
        if (locked.Count > 0)
            reasons.Add($"{locked.Count} 条引用锁定的版本已缺失（{string.Join("、", locked.Select(Label))}），"
                + "不能静默按当前内容使用：请改回「跟随最新」或重新锁定一个存在的版本");
        if (authoritative.Count > 0)
            reasons.Add($"{authoritative.Count} 条引用指向的项目级资源已不在项目库中（{string.Join("、", authoritative.Select(Label))}），"
                + "画布里剩下的旧快照只供恢复参考：请把项目库文件放回项目，或改为引用其它资源");

        return $"节点「{node.Title}」有 {blocked.Count} 条引用不可用：" + string.Join("；", reasons) + "。";
    }

    /// <summary>
    /// 设置引用的版本策略：<paramref name="versionId"/> 为空表示跟随最新，非空表示锁定到该版本。
    /// 版本不存在或不属于该变体时拒绝，不做任何改写。
    /// </summary>
    public static bool TrySetVersion(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        Guid entityId,
        Guid variantId,
        Guid? versionId,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        error = string.Empty;

        var reference = node.References.FirstOrDefault(item => item.EntityId == entityId && item.VariantId == variantId);
        if (reference is null) { error = "该节点没有引用这条设定。"; return false; }
        if (node.IsLocked) { error = "节点已锁定（定稿保护），请先在界面上解锁。"; return false; }

        if (versionId is { } wanted)
        {
            var variant = canvas.FindVariant(variantId);
            if (variant is null) { error = "变体不存在。"; return false; }
            if (variant.FindVersion(wanted) is null) { error = "要锁定的版本不存在，已拒绝写入（不做静默降级）。"; return false; }
        }

        reference.VariantVersionId = versionId;
        return true;
    }
}
