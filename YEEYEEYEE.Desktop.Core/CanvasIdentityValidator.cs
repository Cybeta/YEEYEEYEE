namespace YEEYEEYEE.Desktop;

/// <summary>
/// 校验问题的严重度。
/// Error：数据自相矛盾（同一作用域内 ID 重复、引用悬空、版本错绑），会让解析结果不确定。
/// Warning：可疑但可能合法（重复 ID 导致的归属歧义、旧数据里未参与查找的 ID、跨机器缺失的资产文件、可选的弱引用）。
/// </summary>
public enum CanvasIdentitySeverity
{
    Warning,
    Error
}

/// <summary>
/// 一条结构化校验问题：稳定错误代码 + 严重度 + 对象类型与 ID + 字段路径 + 目标标识 + 说明。
/// 只做描述，不携带修复动作，也不生成替代 ID。
/// </summary>
public sealed record CanvasIdentityIssue(
    string Code,
    CanvasIdentitySeverity Severity,
    string ObjectType,
    string ObjectId,
    string FieldPath,
    string TargetId,
    string Message)
{
    /// <summary>「类型(ID)」形式的对象标签，用于日志与报告。</summary>
    public string ObjectLabel => string.IsNullOrEmpty(ObjectId) ? ObjectType : $"{ObjectType}({ObjectId})";

    public override string ToString()
    {
        var target = string.IsNullOrEmpty(TargetId) ? string.Empty : $" → {TargetId}";
        return $"[{Severity}] {Code} {ObjectLabel} {FieldPath}{target}：{Message}";
    }
}

/// <summary>
/// 校验问题代码表。代码一经发布即视为稳定标识，只增不改，便于后续轮次按代码统计与回归。
/// 归属类问题刻意分成「确定错绑」（..._OWNER_MISMATCH）与「无法确定」（..._AMBIGUOUS）两类：
/// 只有当候选集合中确实没有一个属于所写上下文时才判错绑，绝不凭首条数据断定归属。
/// </summary>
public static class CanvasIdentityCodes
{
    /// <summary>ID 为空 GUID，但该 ID 会作为查找键使用。</summary>
    public const string IdEmpty = "ID_EMPTY";

    /// <summary>同一作用域内出现重复 ID，解析结果不确定。</summary>
    public const string IdDuplicate = "ID_DUPLICATE";

    /// <summary>边的起点或终点节点不存在（含空 GUID）。</summary>
    public const string EdgeEndpointMissing = "REF_EDGE_ENDPOINT_MISSING";

    /// <summary>边的起点或终点节点 ID 重复，无法确定连的是哪一个节点。</summary>
    public const string EdgeEndpointAmbiguous = "REF_EDGE_ENDPOINT_AMBIGUOUS";

    /// <summary>边的起点与终点是同一个节点。</summary>
    public const string EdgeSelfLoop = "REF_EDGE_SELF_LOOP";

    /// <summary>ParentNodeId 指向的节点不存在（含空 GUID）。</summary>
    public const string ParentMissing = "REF_PARENT_MISSING";

    /// <summary>节点的父节点是它自己。</summary>
    public const string ParentSelf = "REF_PARENT_SELF";

    /// <summary>ParentNodeId 链构成循环。</summary>
    public const string ParentCycle = "REF_PARENT_CYCLE";

    /// <summary>父节点 ID 重复，无法确定布局归属。</summary>
    public const string ParentAmbiguous = "REF_PARENT_AMBIGUOUS";

    /// <summary>节点的工作树锚 WorkTreeItemId 指向不存在的条目（含空 GUID）。</summary>
    public const string AnchorMissing = "REF_ANCHOR_MISSING";

    /// <summary>工作树锚 ID 重复，无法确定锚到哪一个条目。</summary>
    public const string AnchorAmbiguous = "REF_ANCHOR_AMBIGUOUS";

    /// <summary>工作树条目的父条目不存在（含空 GUID）。</summary>
    public const string WorkTreeParentMissing = "REF_WORKTREE_PARENT_MISSING";

    /// <summary>工作树条目的父条目是它自己。</summary>
    public const string WorkTreeParentSelf = "REF_WORKTREE_PARENT_SELF";

    /// <summary>工作树 ParentId 链构成循环。</summary>
    public const string WorkTreeParentCycle = "REF_WORKTREE_PARENT_CYCLE";

    /// <summary>工作树父条目 ID 重复，无法确定归属。</summary>
    public const string WorkTreeParentAmbiguous = "REF_WORKTREE_PARENT_AMBIGUOUS";

    /// <summary>节点引用既没有实体 ID 也没有变体 ID，无法解析。</summary>
    public const string NodeReferenceEmpty = "REF_NODE_REFERENCE_EMPTY";

    /// <summary>引用的设定实体不存在。</summary>
    public const string EntityMissing = "REF_ENTITY_MISSING";

    /// <summary>实体 ID 在画布内重复，无法确定引用指向哪一个实体。</summary>
    public const string EntityAmbiguous = "REF_ENTITY_AMBIGUOUS";

    /// <summary>引用的变体不存在。</summary>
    public const string VariantMissing = "REF_VARIANT_MISSING";

    /// <summary>变体 ID 重复（或实体上下文本身有歧义），无法确定引用指向哪一个变体。</summary>
    public const string VariantAmbiguous = "REF_VARIANT_AMBIGUOUS";

    /// <summary>变体的全部候选都不属于引用所写的实体（错绑）。</summary>
    public const string VariantOwnerMismatch = "REF_VARIANT_OWNER_MISMATCH";

    /// <summary>锁定的视觉版本不存在。</summary>
    public const string VersionMissing = "REF_VERSION_MISSING";

    /// <summary>引用缺少可唯一确定的变体，无法确认版本归属。</summary>
    public const string VersionAmbiguous = "REF_VERSION_AMBIGUOUS";

    /// <summary>锁定的视觉版本的全部候选都不属于引用所写的变体（错绑）。</summary>
    public const string VersionOwnerMismatch = "REF_VERSION_OWNER_MISMATCH";

    /// <summary>场景布局元素上的可选实体引用指向不存在的实体。</summary>
    public const string LayoutElementEntityMissing = "REF_LAYOUT_ELEMENT_ENTITY_MISSING";

    /// <summary>场景布局元素上的可选实体引用 ID 重复，无法反查实体。</summary>
    public const string LayoutElementEntityAmbiguous = "REF_LAYOUT_ELEMENT_ENTITY_AMBIGUOUS";

    /// <summary>工作树条目的来源实体不存在。</summary>
    public const string SourceEntityMissing = "REF_SOURCE_ENTITY_MISSING";

    /// <summary>工作树来源实体 ID 重复，无法确定来源。</summary>
    public const string SourceEntityAmbiguous = "REF_SOURCE_ENTITY_AMBIGUOUS";

    /// <summary>工作树条目的来源变体不存在。</summary>
    public const string SourceVariantMissing = "REF_SOURCE_VARIANT_MISSING";

    /// <summary>工作树来源变体无法唯一确定。</summary>
    public const string SourceVariantAmbiguous = "REF_SOURCE_VARIANT_AMBIGUOUS";

    /// <summary>工作树条目的来源变体不属于来源实体（错绑）。</summary>
    public const string SourceVariantOwnerMismatch = "REF_SOURCE_VARIANT_OWNER_MISMATCH";

    /// <summary>工作树条目的来源版本不存在。</summary>
    public const string SourceVersionMissing = "REF_SOURCE_VERSION_MISSING";

    /// <summary>工作树来源版本归属无法唯一确定。</summary>
    public const string SourceVersionAmbiguous = "REF_SOURCE_VERSION_AMBIGUOUS";

    /// <summary>工作树条目的来源版本不属于来源变体（错绑）。</summary>
    public const string SourceVersionOwnerMismatch = "REF_SOURCE_VERSION_OWNER_MISMATCH";

    /// <summary>SupersedesVersionId 指向的上一版本不存在。</summary>
    public const string SupersedesMissing = "REF_SUPERSEDES_MISSING";

    /// <summary>SupersedesVersionId 的归属无法唯一确定。</summary>
    public const string SupersedesAmbiguous = "REF_SUPERSEDES_AMBIGUOUS";

    /// <summary>SupersedesVersionId 指向的版本不属于本变体（错绑）。</summary>
    public const string SupersedesOwnerMismatch = "REF_SUPERSEDES_OWNER_MISMATCH";

    /// <summary>SupersedesVersionId 指向版本自身。</summary>
    public const string SupersedesSelf = "REF_SUPERSEDES_SELF";

    /// <summary>附件没有资产标识（Reference 为空）。</summary>
    public const string AssetReferenceEmpty = "ASSET_REFERENCE_EMPTY";

    /// <summary>资产标识确实不可用（asset:// 后为空或只剩目录名、文件名含非法字符、路径含非法字符、疑似 URI 但无法解析）。</summary>
    public const string AssetReferenceMalformed = "ASSET_REFERENCE_MALFORMED";

    /// <summary>资产标识可用但不是规范写法（例如 asset:// 后带了目录，读取时只取文件名）；现有读取兼容行为，不按错误处理。</summary>
    public const string AssetReferenceNonCanonical = "ASSET_REFERENCE_NON_CANONICAL";

    /// <summary>资产标识格式正确，但当前机器上取不到对应文件。</summary>
    public const string AssetUnreachable = "ASSET_UNREACHABLE";
}

/// <summary>
/// 只读校验结果。一次调用收集全部问题，不在第一项失败后中断；
/// 重复 ID 会逐项报出，不会静默取第一条。
/// </summary>
public sealed class CanvasIdentityReport
{
    private readonly List<CanvasIdentityIssue> issues = new();

    public IReadOnlyList<CanvasIdentityIssue> Issues => issues;

    public int ErrorCount => issues.Count(issue => issue.Severity == CanvasIdentitySeverity.Error);

    public int WarningCount => issues.Count(issue => issue.Severity == CanvasIdentitySeverity.Warning);

    public bool HasErrors => ErrorCount > 0;

    /// <summary>没有任何问题。</summary>
    public bool IsClean => issues.Count == 0;

    /// <summary>按代码筛选问题，便于测试与复核逐项确认。</summary>
    public IReadOnlyList<CanvasIdentityIssue> WithCode(string code) =>
        issues.Where(issue => string.Equals(issue.Code, code, StringComparison.Ordinal)).ToList();

    /// <summary>按代码汇总数量。</summary>
    public IReadOnlyDictionary<string, int> CountByCode() =>
        issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>整份报告的可读文本。</summary>
    public string ToText()
    {
        var lines = new List<string> { $"校验问题 {issues.Count} 项（错误 {ErrorCount}，警告 {WarningCount}）。" };
        lines.AddRange(issues.Select(issue => "· " + issue));
        return string.Join(Environment.NewLine, lines);
    }

    internal void Add(
        string code,
        CanvasIdentitySeverity severity,
        string objectType,
        Guid objectId,
        string fieldPath,
        Guid? targetId,
        string message) =>
        issues.Add(new CanvasIdentityIssue(
            code,
            severity,
            objectType,
            objectId.ToString(),
            fieldPath,
            targetId?.ToString() ?? string.Empty,
            message));
}

/// <summary>
/// 画布 ID 与引用的只读校验器（目标 1）。
///
/// ID 作用域（与现有查找代码一致，改这里之前先改查找代码）：
/// · WorkflowNode.Id / WorkflowEdge.Id / WorkTreeItem.Id / WorkflowEntity.Id：画布级唯一。
/// · WorkflowEntityVariant.Id：画布级唯一（<see cref="WorkflowCanvasState.FindVariant"/> 跨实体查找）。
/// · EntityVariantVersion.Id：所属变体内唯一（<see cref="WorkflowEntityVariant.FindVersion"/>）。
/// · SceneLayoutItem.Id / WorkflowAttachment.Id / GenerationHistory.Id：仅作标识，当前不参与按 ID 查找。
/// · WorkflowAttachment.Reference 是路径或 URI，不是 GUID，按真实读取规则判定形状。
///
/// 归属判定只用候选集合：唯一候选才算确定；多个候选一律报歧义（Warning），
/// 明确「全部候选都不属于所写上下文」时才报错绑（Error），不按首条数据推断。
///
/// 校验只读：不改动传入对象、不写盘、不生成替代 ID、不按名称自动绑定、不删除引用。
/// </summary>
public static class CanvasIdentityValidator
{
    private const string NodeType = "WorkflowNode";
    private const string EdgeType = "WorkflowEdge";
    private const string WorkTreeType = "WorkTreeItem";
    private const string EntityType = "WorkflowEntity";
    private const string VariantType = "WorkflowEntityVariant";
    private const string VersionType = "EntityVariantVersion";
    private const string LayoutItemType = "SceneLayoutItem";
    private const string AttachmentType = "WorkflowAttachment";
    private const string HistoryType = "GenerationHistory";

    /// <summary>
    /// 校验一份画布状态并返回结构化问题报告。
    /// <paramref name="assetAccessible"/> 为空时只检查资产标识的形状（不访问磁盘）；
    /// 传入 <c>AssetStore.Exists</c> 这类委托时，额外报告本机取不到的资产。
    /// </summary>
    public static CanvasIdentityReport Validate(WorkflowCanvasState state, Func<string, bool>? assetAccessible = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var report = new CanvasIdentityReport();
        var lookup = new Lookup();

        IndexNodes(state, report, lookup);
        IndexEdges(state, report);
        IndexWorkTree(state, report, lookup);
        IndexEntities(state, report, lookup);

        CheckNodes(state, report, lookup, assetAccessible);
        CheckEdges(state, report, lookup);
        CheckWorkTree(state, report, lookup, assetAccessible);
        CheckEntities(state, report, lookup, assetAccessible);

        return report;
    }

    // ── 索引：收集候选查找表，并报告空 ID 与重复 ID ─────────────────────────

    private static void IndexNodes(WorkflowCanvasState state, CanvasIdentityReport report, Lookup lookup)
    {
        var ids = new List<(Guid Id, string Path)>(state.Nodes.Count);
        for (var index = 0; index < state.Nodes.Count; index++)
        {
            var node = state.Nodes[index];
            var path = $"Nodes[{index}].Id";
            CheckKeyId(report, NodeType, node.Id, path);
            ids.Add((node.Id, path));
            AddCandidate(lookup.Nodes, node.Id, node);
        }

        ReportDuplicates(report, NodeType, "画布节点集合 Nodes", ids, CanvasIdentitySeverity.Error);
    }

    private static void IndexEdges(WorkflowCanvasState state, CanvasIdentityReport report)
    {
        var ids = new List<(Guid Id, string Path)>(state.Edges.Count);
        for (var index = 0; index < state.Edges.Count; index++)
        {
            var edge = state.Edges[index];
            var path = $"Edges[{index}].Id";
            CheckKeyId(report, EdgeType, edge.Id, path);
            ids.Add((edge.Id, path));
        }

        ReportDuplicates(report, EdgeType, "连线集合 Edges", ids, CanvasIdentitySeverity.Error);
    }

    private static void IndexWorkTree(WorkflowCanvasState state, CanvasIdentityReport report, Lookup lookup)
    {
        var ids = new List<(Guid Id, string Path)>(state.WorkTree.Count);
        for (var index = 0; index < state.WorkTree.Count; index++)
        {
            var item = state.WorkTree[index];
            var path = $"WorkTree[{index}].Id";
            CheckKeyId(report, WorkTreeType, item.Id, path);
            ids.Add((item.Id, path));
            AddCandidate(lookup.WorkTreeItems, item.Id, item);
        }

        ReportDuplicates(report, WorkTreeType, "工作树集合 WorkTree", ids, CanvasIdentitySeverity.Error);
    }

    private static void IndexEntities(WorkflowCanvasState state, CanvasIdentityReport report, Lookup lookup)
    {
        var entityIds = new List<(Guid Id, string Path)>(state.Entities.Count);
        var variantIds = new List<(Guid Id, string Path)>();

        for (var entityIndex = 0; entityIndex < state.Entities.Count; entityIndex++)
        {
            var entity = state.Entities[entityIndex];
            var entityPath = $"Entities[{entityIndex}]";
            CheckKeyId(report, EntityType, entity.Id, entityPath + ".Id");
            entityIds.Add((entity.Id, entityPath + ".Id"));
            AddCandidate(lookup.Entities, entity.Id, entity);

            for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
            {
                var variant = entity.Variants[variantIndex];
                var variantPath = $"{entityPath}.Variants[{variantIndex}]";
                CheckKeyId(report, VariantType, variant.Id, variantPath + ".Id");
                variantIds.Add((variant.Id, variantPath + ".Id"));
                AddCandidate(lookup.Variants, variant.Id, new VariantSlot(variant, entity));

                // 版本 ID 的作用域是所属变体，因此重复判定按变体分组。
                var versionIds = new List<(Guid Id, string Path)>(variant.Versions.Count);
                for (var versionIndex = 0; versionIndex < variant.Versions.Count; versionIndex++)
                {
                    var version = variant.Versions[versionIndex];
                    var versionPath = $"{variantPath}.Versions[{versionIndex}]";
                    CheckKeyId(report, VersionType, version.Id, versionPath + ".Id");
                    versionIds.Add((version.Id, versionPath + ".Id"));
                    AddCandidate(lookup.Versions, version.Id, new VersionSlot(version, variant));
                }

                ReportDuplicates(report, VersionType, "所属变体内的版本", versionIds, CanvasIdentitySeverity.Error);
            }
        }

        ReportDuplicates(report, EntityType, "实体集合 Entities", entityIds, CanvasIdentitySeverity.Error);
        ReportDuplicates(report, VariantType, "画布内的全部变体（跨实体查找）", variantIds, CanvasIdentitySeverity.Error);
    }

    // ── 节点 ───────────────────────────────────────────────────────────────

    private static void CheckNodes(
        WorkflowCanvasState state,
        CanvasIdentityReport report,
        Lookup lookup,
        Func<string, bool>? assetAccessible)
    {
        for (var index = 0; index < state.Nodes.Count; index++)
        {
            var node = state.Nodes[index];
            var path = $"Nodes[{index}]";

            if (node.ParentNodeId is { } parentId)
            {
                if (parentId == Guid.Empty)
                {
                    report.Add(CanvasIdentityCodes.ParentMissing, CanvasIdentitySeverity.Error, NodeType, node.Id, path + ".ParentNodeId", parentId,
                        "父节点 ID 是空 GUID；应为 null 或不指向自身的有效节点 ID。");
                }
                else if (parentId == node.Id)
                {
                    report.Add(CanvasIdentityCodes.ParentSelf, CanvasIdentitySeverity.Error, NodeType, node.Id, path + ".ParentNodeId", parentId,
                        "父节点是节点自己，布局父子关系无意义。");
                }
                else
                {
                    var candidates = Candidates(lookup.Nodes, parentId);
                    var (parent, ambiguous) = Single(candidates);
                    if (ambiguous)
                        report.Add(CanvasIdentityCodes.ParentAmbiguous, CanvasIdentitySeverity.Warning, NodeType, node.Id, path + ".ParentNodeId", parentId,
                            $"父节点 ID 在 Nodes 中出现 {candidates.Count} 次，无法确定布局归属；重复 ID 已单独报告，这里不按首条判断。");
                    else if (parent is null)
                        report.Add(CanvasIdentityCodes.ParentMissing, CanvasIdentitySeverity.Error, NodeType, node.Id, path + ".ParentNodeId", parentId,
                            "父节点在 Nodes 中不存在，节点会失去布局归属。");
                }
            }

            if (node.WorkTreeItemId is { } anchorId)
            {
                if (anchorId == Guid.Empty)
                {
                    report.Add(CanvasIdentityCodes.AnchorMissing, CanvasIdentitySeverity.Error, NodeType, node.Id, path + ".WorkTreeItemId", anchorId,
                        "工作树锚是空 GUID；应为 null 或有效的工作树条目 ID。");
                }
                else
                {
                    var candidates = Candidates(lookup.WorkTreeItems, anchorId);
                    var (anchor, ambiguous) = Single(candidates);
                    if (ambiguous)
                        report.Add(CanvasIdentityCodes.AnchorAmbiguous, CanvasIdentitySeverity.Warning, NodeType, node.Id, path + ".WorkTreeItemId", anchorId,
                            $"工作树锚 ID 在 WorkTree 中出现 {candidates.Count} 次，无法确定跟随哪一个条目；重复 ID 已单独报告。");
                    else if (anchor is null)
                        report.Add(CanvasIdentityCodes.AnchorMissing, CanvasIdentitySeverity.Error, NodeType, node.Id, path + ".WorkTreeItemId", anchorId,
                            "工作树锚指向的条目不存在，节点无法跟随章节/能力更新。");
                }
            }

            CheckNodeReferences(report, node, path, lookup);
            CheckAttachments(report, node.Attachments, path, assetAccessible);
            CheckGenerationHistory(report, node.GenerationHistory, path);
        }

        ReportParentCycles(report, state.Nodes, lookup);
    }

    private static void CheckNodeReferences(CanvasIdentityReport report, WorkflowNode node, string nodePath, Lookup lookup)
    {
        for (var index = 0; index < node.References.Count; index++)
        {
            var reference = node.References[index];
            var path = $"{nodePath}.References[{index}]";

            if (reference.EntityId == Guid.Empty && reference.VariantId == Guid.Empty)
            {
                report.Add(CanvasIdentityCodes.NodeReferenceEmpty, CanvasIdentitySeverity.Error, NodeType, node.Id, path, null,
                    "引用既没有实体 ID 也没有变体 ID，无法解析到任何设定。");
                continue;
            }

            var variant = ResolveTarget(
                report, lookup, NodeType, node.Id, path, ".EntityId", ".VariantId",
                reference.EntityId, reference.VariantId, TargetIssueCodes.Reference, out _);

            if (reference.VariantVersionId is not { } versionId) continue; // null：跟随变体当前内容，合法

            CheckVersion(
                report, lookup, VersionIssueCodes.Reference, NodeType, node.Id, path + ".VariantVersionId",
                versionId, variant, "锁定版本");
        }
    }

    /// <summary>
    /// 解析「实体 + 变体」引用，返回可唯一确定的变体；无法唯一确定时返回 null 并报告原因。
    /// 变体优先按引用所写的实体上下文解析，避免重复变体 ID 时凭首条数据断定错绑。
    /// </summary>
    private static WorkflowEntityVariant? ResolveTarget(
        CanvasIdentityReport report,
        Lookup lookup,
        string objectType,
        Guid objectId,
        string path,
        string entityField,
        string variantField,
        Guid entityId,
        Guid variantId,
        TargetIssueCodes codes,
        out WorkflowEntity? entity,
        bool useDefaultVariant = true)
    {
        entity = null;
        var entityAmbiguous = false;

        if (entityId != Guid.Empty)
        {
            var candidates = Candidates(lookup.Entities, entityId);
            var (resolved, ambiguous) = Single(candidates);
            entity = resolved;
            entityAmbiguous = ambiguous;
            if (ambiguous)
                report.Add(codes.EntityAmbiguous, CanvasIdentitySeverity.Warning, objectType, objectId, path + entityField, entityId,
                    $"实体 ID 在 Entities 中出现 {candidates.Count} 次，无法确定引用指向哪一个实体；重复 ID 已单独报告，这里不按首条判断归属。");
            else if (resolved is null)
                report.Add(codes.EntityMissing, CanvasIdentitySeverity.Error, objectType, objectId, path + entityField, entityId,
                    "引用的设定实体在 Entities 中不存在。");
        }

        if (variantId != Guid.Empty)
        {
            var slots = Candidates(lookup.Variants, variantId);
            if (slots.Count == 0)
            {
                report.Add(codes.VariantMissing, CanvasIdentitySeverity.Error, objectType, objectId, path + variantField, variantId,
                    "引用的变体在任何实体下都不存在。");
                return null;
            }

            if (entity is not null)
            {
                var contextEntity = entity;
                var matching = slots.Where(slot => ReferenceEquals(slot.Owner, contextEntity)).ToList();
                var (resolved, ambiguous) = Single(matching);
                if (ambiguous)
                {
                    report.Add(codes.VariantAmbiguous, CanvasIdentitySeverity.Warning, objectType, objectId, path + variantField, variantId,
                        $"同一实体下有 {matching.Count} 个变体使用该 ID，无法确定引用指向哪一个变体。");
                    return null;
                }

                if (resolved is null)
                {
                    report.Add(codes.VariantOwnerMismatch, CanvasIdentitySeverity.Error, objectType, objectId, path + variantField, variantId,
                        $"该变体的全部候选都属于其它实体（{DescribeOwners(slots)}），与引用写的实体 {entity.Id}（{entity.Name}）不一致；不按名称重新绑定。");
                    return null;
                }

                return resolved.Variant;
            }

            if (entityAmbiguous)
            {
                report.Add(codes.VariantAmbiguous, CanvasIdentitySeverity.Warning, objectType, objectId, path + variantField, variantId,
                    $"引用的实体 ID 本身存在歧义，无法确认变体归属（候选变体 {slots.Count} 个）。");
                return null;
            }

            var (unique, multiple) = Single(slots);
            if (multiple)
            {
                report.Add(codes.VariantAmbiguous, CanvasIdentitySeverity.Warning, objectType, objectId, path + variantField, variantId,
                    $"变体 ID 在画布内出现 {slots.Count} 次，且没有可确定的实体上下文，无法确定引用指向哪一个变体。");
                return null;
            }

            if (entityId != Guid.Empty)
            {
                // 实体缺失时不再推断归属：变体确实存在，但不属于（也不存在于）所写实体。
                report.Add(codes.VariantOwnerMismatch, CanvasIdentitySeverity.Error, objectType, objectId, path + variantField, variantId,
                    $"所写实体 {entityId} 不存在，该变体属于 {DescribeOwners(slots)}，无法归属到该实体。");
                return null;
            }

            return unique!.Variant;
        }

        // 变体 ID 留空表示跟随实体的默认变体：这是节点引用的既有解析语义，属合法值。
        // 工作树来源字段没有这层语义（MainForm 只按 SourceVariantId 解析），因此不套用回退。
        if (useDefaultVariant && entity is not null)
        {
            var fallback = entity.Variants.FirstOrDefault();
            if (fallback is null)
                report.Add(codes.VariantMissing, CanvasIdentitySeverity.Error, objectType, objectId, path + variantField, null,
                    "引用没有写变体 ID，而所属实体也没有任何变体，引用无法解析。");
            return fallback;
        }

        return null;
    }

    /// <summary>「实体 + 变体」引用的错误代码组合：节点引用与工作树来源各用一套。</summary>
    private readonly record struct TargetIssueCodes(
        string EntityMissing,
        string EntityAmbiguous,
        string VariantMissing,
        string VariantAmbiguous,
        string VariantOwnerMismatch)
    {
        public static TargetIssueCodes Reference => new(
            CanvasIdentityCodes.EntityMissing,
            CanvasIdentityCodes.EntityAmbiguous,
            CanvasIdentityCodes.VariantMissing,
            CanvasIdentityCodes.VariantAmbiguous,
            CanvasIdentityCodes.VariantOwnerMismatch);

        public static TargetIssueCodes Source => new(
            CanvasIdentityCodes.SourceEntityMissing,
            CanvasIdentityCodes.SourceEntityAmbiguous,
            CanvasIdentityCodes.SourceVariantMissing,
            CanvasIdentityCodes.SourceVariantAmbiguous,
            CanvasIdentityCodes.SourceVariantOwnerMismatch);
    }

    private readonly record struct VersionIssueCodes(string Missing, string Ambiguous, string OwnerMismatch)
    {
        public static VersionIssueCodes Reference => new(
            CanvasIdentityCodes.VersionMissing,
            CanvasIdentityCodes.VersionAmbiguous,
            CanvasIdentityCodes.VersionOwnerMismatch);

        public static VersionIssueCodes Source => new(
            CanvasIdentityCodes.SourceVersionMissing,
            CanvasIdentityCodes.SourceVersionAmbiguous,
            CanvasIdentityCodes.SourceVersionOwnerMismatch);

        public static VersionIssueCodes Supersedes => new(
            CanvasIdentityCodes.SupersedesMissing,
            CanvasIdentityCodes.SupersedesAmbiguous,
            CanvasIdentityCodes.SupersedesOwnerMismatch);
    }

    /// <summary>
    /// 校验一个版本引用。先看它是否属于已确定的变体；变体内找不到时再看全局候选，
    /// 只有候选全部落在别的变体上才判错绑，否则报归属无法确定。
    /// </summary>
    private static void CheckVersion(
        CanvasIdentityReport report,
        Lookup lookup,
        VersionIssueCodes codes,
        string objectType,
        Guid objectId,
        string path,
        Guid versionId,
        WorkflowEntityVariant? variant,
        string subject)
    {
        if (versionId == Guid.Empty)
        {
            report.Add(codes.Missing, CanvasIdentitySeverity.Error, objectType, objectId, path, versionId,
                $"{subject}是空 GUID；只有 null 才表示跟随当前版本，空 GUID 不是有效版本。");
            return;
        }

        var slots = Candidates(lookup.Versions, versionId);

        if (variant is not null)
        {
            if (variant.FindVersion(versionId) is not null) return;

            if (slots.Count == 0)
            {
                report.Add(codes.Missing, CanvasIdentitySeverity.Error, objectType, objectId, path, versionId,
                    $"{subject}在该变体及任何其他变体下都不存在。");
                return;
            }

            report.Add(codes.OwnerMismatch, CanvasIdentitySeverity.Error, objectType, objectId, path, versionId,
                $"{subject}的全部候选都属于其它变体（{DescribeVersionOwners(slots)}），与所写变体 {variant.Id} 不一致（错绑）。");
            return;
        }

        if (slots.Count > 0)
            report.Add(codes.Ambiguous, CanvasIdentitySeverity.Warning, objectType, objectId, path, versionId,
                $"{subject}缺少可唯一确定的变体，无法确认归属；版本只在所属变体内校验，不跨变体猜测。");
        else
            report.Add(codes.Missing, CanvasIdentitySeverity.Error, objectType, objectId, path, versionId,
                $"{subject}在任何变体下都不存在。");
    }

    private static string DescribeOwners(IReadOnlyList<VariantSlot> slots) =>
        string.Join("、", slots.Select(slot => $"{slot.Owner.Id}（{slot.Owner.Name}）").Distinct(StringComparer.Ordinal));

    private static string DescribeVersionOwners(IReadOnlyList<VersionSlot> slots) =>
        string.Join("、", slots.Select(slot => $"{slot.Variant.Id}").Distinct(StringComparer.Ordinal));

    private static void ReportParentCycles(CanvasIdentityReport report, IReadOnlyList<WorkflowNode> nodes, Lookup lookup)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            var start = nodes[index];
            var path = new List<WorkflowNode>();
            var positions = new Dictionary<Guid, int>();
            var current = start;

            while (true)
            {
                if (positions.TryGetValue(current.Id, out var head))
                {
                    var members = path.Skip(head).ToList();
                    var signature = string.Join(",", members.Select(item => item.Id.ToString()).OrderBy(text => text, StringComparer.Ordinal));
                    if (reported.Add(signature))
                    {
                        var chain = string.Join(" → ", members.Select(item => item.Id.ToString())) + " → " + current.Id;
                        report.Add(CanvasIdentityCodes.ParentCycle, CanvasIdentitySeverity.Error, NodeType, start.Id,
                            $"Nodes[{index}].ParentNodeId", current.Id,
                            $"父链构成循环：{chain}，循环内每个节点都无法解析到根节点。");
                    }

                    break;
                }

                positions[current.Id] = path.Count;
                path.Add(current);

                if (current.ParentNodeId is not { } parentId || parentId == Guid.Empty) break;
                if (parentId == current.Id) break; // 自引用已单独报告
                var (parent, _) = Single(Candidates(lookup.Nodes, parentId));
                if (parent is null) break; // 悬空或 ID 重复（无法确定父链），已单独报告
                current = parent;
            }
        }
    }

    private static void CheckGenerationHistory(CanvasIdentityReport report, IReadOnlyList<GenerationHistory> history, string nodePath)
    {
        var ids = new List<(Guid Id, string Path)>(history.Count);
        for (var index = 0; index < history.Count; index++)
        {
            var record = history[index];
            var path = $"{nodePath}.GenerationHistory[{index}]";
            // 生成历史 ID 目前不参与按 ID 查找，只做标识完整性提醒。
            // Output 既可能是资产引用也可能是文本正文，因此不按资产标识校验。
            CheckLooseId(report, HistoryType, record.Id, path + ".Id", "生成历史记录");
            ids.Add((record.Id, path + ".Id"));
        }

        ReportDuplicates(report, HistoryType, "同一节点的生成历史", ids, CanvasIdentitySeverity.Warning);
    }

    // ── 连线 ───────────────────────────────────────────────────────────────

    private static void CheckEdges(WorkflowCanvasState state, CanvasIdentityReport report, Lookup lookup)
    {
        for (var index = 0; index < state.Edges.Count; index++)
        {
            var edge = state.Edges[index];
            var path = $"Edges[{index}]";

            var sourceKnown = CheckEndpoint(report, edge, edge.SourceNodeId, path + ".SourceNodeId", "起点", lookup);
            var targetKnown = CheckEndpoint(report, edge, edge.TargetNodeId, path + ".TargetNodeId", "终点", lookup);

            if (sourceKnown && targetKnown && edge.SourceNodeId == edge.TargetNodeId)
                report.Add(CanvasIdentityCodes.EdgeSelfLoop, CanvasIdentitySeverity.Error, EdgeType, edge.Id, path + ".TargetNodeId", edge.TargetNodeId,
                    "起点与终点是同一个节点；创建连线的入口本身会拒绝这种自环。");
        }
    }

    /// <summary>校验一个连线端点；返回是否可唯一确定（歧义时不再判断自环）。</summary>
    private static bool CheckEndpoint(
        CanvasIdentityReport report,
        WorkflowEdge edge,
        Guid nodeId,
        string path,
        string label,
        Lookup lookup)
    {
        if (nodeId == Guid.Empty)
        {
            report.Add(CanvasIdentityCodes.EdgeEndpointMissing, CanvasIdentitySeverity.Error, EdgeType, edge.Id, path, nodeId,
                $"连线的{label}节点 ID 是空 GUID。");
            return false;
        }

        var candidates = Candidates(lookup.Nodes, nodeId);
        var (node, ambiguous) = Single(candidates);
        if (ambiguous)
        {
            report.Add(CanvasIdentityCodes.EdgeEndpointAmbiguous, CanvasIdentitySeverity.Warning, EdgeType, edge.Id, path, nodeId,
                $"连线的{label}节点 ID 在 Nodes 中出现 {candidates.Count} 次，无法确定连的是哪一个节点。");
            return false;
        }

        if (node is null)
        {
            report.Add(CanvasIdentityCodes.EdgeEndpointMissing, CanvasIdentitySeverity.Error, EdgeType, edge.Id, path, nodeId,
                $"连线的{label}节点在 Nodes 中不存在。");
            return false;
        }

        return true;
    }

    // ── 工作树 ─────────────────────────────────────────────────────────────

    private static void CheckWorkTree(
        WorkflowCanvasState state,
        CanvasIdentityReport report,
        Lookup lookup,
        Func<string, bool>? assetAccessible)
    {
        for (var index = 0; index < state.WorkTree.Count; index++)
        {
            var item = state.WorkTree[index];
            var path = $"WorkTree[{index}]";

            if (item.ParentId is { } parentId)
            {
                if (parentId == Guid.Empty)
                {
                    report.Add(CanvasIdentityCodes.WorkTreeParentMissing, CanvasIdentitySeverity.Error, WorkTreeType, item.Id, path + ".ParentId", parentId,
                        "父条目 ID 是空 GUID；应为 null 或不指向自身的有效条目 ID。");
                }
                else if (parentId == item.Id)
                {
                    report.Add(CanvasIdentityCodes.WorkTreeParentSelf, CanvasIdentitySeverity.Error, WorkTreeType, item.Id, path + ".ParentId", parentId,
                        "父条目是条目自己。");
                }
                else
                {
                    var candidates = Candidates(lookup.WorkTreeItems, parentId);
                    var (parent, ambiguous) = Single(candidates);
                    if (ambiguous)
                        report.Add(CanvasIdentityCodes.WorkTreeParentAmbiguous, CanvasIdentitySeverity.Warning, WorkTreeType, item.Id, path + ".ParentId", parentId,
                            $"父条目 ID 在 WorkTree 中出现 {candidates.Count} 次，无法确定归属；重复 ID 已单独报告。");
                    else if (parent is null)
                        report.Add(CanvasIdentityCodes.WorkTreeParentMissing, CanvasIdentitySeverity.Error, WorkTreeType, item.Id, path + ".ParentId", parentId,
                            "父条目在 WorkTree 中不存在，条目会失去归属。");
                }
            }

            CheckWorkTreeSources(report, item, path, lookup);
            CheckAttachments(report, item.Attachments, path, assetAccessible);
        }

        ReportWorkTreeParentCycles(report, state.WorkTree, lookup);
    }

    private static void CheckWorkTreeSources(CanvasIdentityReport report, WorkTreeItem item, string path, Lookup lookup)
    {
        var variant = ResolveTarget(
            report, lookup, WorkTreeType, item.Id, path, ".SourceEntityId", ".SourceVariantId",
            item.SourceEntityId ?? Guid.Empty, item.SourceVariantId ?? Guid.Empty, TargetIssueCodes.Source, out _, useDefaultVariant: false);

        if (item.SourceVersionId is { } sourceVersionId)
            CheckVersion(report, lookup, VersionIssueCodes.Source, WorkTreeType, item.Id, path + ".SourceVersionId",
                sourceVersionId, variant, "来源版本");

        if (item.SupersedesVersionId is not { } previousId) return;

        // 归属先按已确定的来源变体判断，再判缺失或错绑。
        CheckVersion(report, lookup, VersionIssueCodes.Supersedes, WorkTreeType, item.Id, path + ".SupersedesVersionId",
            previousId, variant, "上一版本");

        if (previousId != Guid.Empty && previousId == item.SourceVersionId)
            report.Add(CanvasIdentityCodes.SupersedesSelf, CanvasIdentitySeverity.Error, WorkTreeType, item.Id, path + ".SupersedesVersionId", previousId,
                "条目同时把自己当作上一版本，版本链自引用。");
    }

    private static void ReportWorkTreeParentCycles(CanvasIdentityReport report, IReadOnlyList<WorkTreeItem> items, Lookup lookup)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var start = items[index];
            var path = new List<WorkTreeItem>();
            var positions = new Dictionary<Guid, int>();
            var current = start;

            while (true)
            {
                if (positions.TryGetValue(current.Id, out var head))
                {
                    var members = path.Skip(head).ToList();
                    var signature = string.Join(",", members.Select(item => item.Id.ToString()).OrderBy(text => text, StringComparer.Ordinal));
                    if (reported.Add(signature))
                    {
                        var chain = string.Join(" → ", members.Select(item => item.Id.ToString())) + " → " + current.Id;
                        report.Add(CanvasIdentityCodes.WorkTreeParentCycle, CanvasIdentitySeverity.Error, WorkTreeType, start.Id,
                            $"WorkTree[{index}].ParentId", current.Id,
                            $"父链构成循环：{chain}，循环内每个条目都无法解析到根条目。");
                    }

                    break;
                }

                positions[current.Id] = path.Count;
                path.Add(current);

                if (current.ParentId is not { } parentId || parentId == Guid.Empty) break;
                if (parentId == current.Id) break;
                var (parent, _) = Single(Candidates(lookup.WorkTreeItems, parentId));
                if (parent is null) break;
                current = parent;
            }
        }
    }

    // ── 设定库：实体 / 变体 / 版本 ─────────────────────────────────────────

    private static void CheckEntities(
        WorkflowCanvasState state,
        CanvasIdentityReport report,
        Lookup lookup,
        Func<string, bool>? assetAccessible)
    {
        for (var entityIndex = 0; entityIndex < state.Entities.Count; entityIndex++)
        {
            var entity = state.Entities[entityIndex];
            var entityPath = $"Entities[{entityIndex}]";

            for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
            {
                var variant = entity.Variants[variantIndex];
                var variantPath = $"{entityPath}.Variants[{variantIndex}]";

                CheckLayout(report, variant.Layout, VariantType, variant.Id, variantPath + ".Layout", lookup);
                CheckAttachments(report, variant.Attachments, variantPath, assetAccessible);

                for (var versionIndex = 0; versionIndex < variant.Versions.Count; versionIndex++)
                {
                    var version = variant.Versions[versionIndex];
                    var versionPath = $"{variantPath}.Versions[{versionIndex}]";

                    CheckSupersedes(report, version, variant, versionPath, lookup);
                    CheckLayout(report, version.Layout, VersionType, version.Id, versionPath + ".Layout", lookup);
                    CheckAttachments(report, version.Attachments, versionPath, assetAccessible);
                }
            }
        }
    }

    private static void CheckSupersedes(
        CanvasIdentityReport report,
        EntityVariantVersion version,
        WorkflowEntityVariant variant,
        string versionPath,
        Lookup lookup)
    {
        if (version.SupersedesVersionId is not { } previousId) return;

        CheckVersion(report, lookup, VersionIssueCodes.Supersedes, VersionType, version.Id,
            versionPath + ".SupersedesVersionId", previousId, variant, "上一版本");

        if (previousId != Guid.Empty && previousId == version.Id)
            report.Add(CanvasIdentityCodes.SupersedesSelf, CanvasIdentitySeverity.Error, VersionType, version.Id,
                versionPath + ".SupersedesVersionId", previousId, "版本把自己当作上一版本，版本链自引用。");
    }

    private static void CheckLayout(
        CanvasIdentityReport report,
        SceneLayout? layout,
        string ownerType,
        Guid ownerId,
        string ownerPath,
        Lookup lookup)
    {
        if (layout is null) return;

        var ids = new List<(Guid Id, string Path)>(layout.Items.Count);
        for (var index = 0; index < layout.Items.Count; index++)
        {
            var item = layout.Items[index];
            var path = $"{ownerPath}.Items[{index}]";
            // 布局元素 ID 目前不参与按 ID 查找，只做标识完整性提醒。
            CheckLooseId(report, LayoutItemType, item.Id, path + ".Id", "布局元素");
            ids.Add((item.Id, path + ".Id"));

            if (item.EntityId is not { } entityId) continue;

            if (entityId == Guid.Empty)
            {
                report.Add(CanvasIdentityCodes.LayoutElementEntityMissing, CanvasIdentitySeverity.Warning, LayoutItemType, item.Id, path + ".EntityId", entityId,
                    "可选实体引用是空 GUID；应为 null 或有效实体 ID。元素文本仍在，只是无法反查实体。");
                continue;
            }

            var candidates = Candidates(lookup.Entities, entityId);
            var (entity, ambiguous) = Single(candidates);
            if (ambiguous)
                report.Add(CanvasIdentityCodes.LayoutElementEntityAmbiguous, CanvasIdentitySeverity.Warning, LayoutItemType, item.Id, path + ".EntityId", entityId,
                    $"可选实体引用 ID 在 Entities 中出现 {candidates.Count} 次，无法反查实体；元素文本仍在。");
            else if (entity is null)
                report.Add(CanvasIdentityCodes.LayoutElementEntityMissing, CanvasIdentitySeverity.Warning, LayoutItemType, item.Id, path + ".EntityId", entityId,
                    "可选实体引用指向不存在的实体；元素文本仍在，只是无法反查实体。");
        }

        ReportDuplicates(report, LayoutItemType, "同一布局内的元素", ids, CanvasIdentitySeverity.Warning);
    }

    // ── 附件与资产标识 ─────────────────────────────────────────────────────

    private static void CheckAttachments(
        CanvasIdentityReport report,
        IReadOnlyList<WorkflowAttachment> attachments,
        string ownerPath,
        Func<string, bool>? assetAccessible)
    {
        var ids = new List<(Guid Id, string Path)>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            var path = $"{ownerPath}.Attachments[{index}]";
            // 附件 ID 目前不参与按 ID 查找，只做标识完整性提醒。
            CheckLooseId(report, AttachmentType, attachment.Id, path + ".Id", "附件");
            ids.Add((attachment.Id, path + ".Id"));

            var reference = attachment.Reference;
            if (string.IsNullOrWhiteSpace(reference))
            {
                report.Add(CanvasIdentityCodes.AssetReferenceEmpty, CanvasIdentitySeverity.Error, AttachmentType, attachment.Id, path + ".Reference", null,
                    "附件没有资产标识，无法定位参考图或媒体文件。");
                continue;
            }

            var shape = ClassifyAssetReference(reference, out var reason);
            if (shape == AssetReferenceShape.Malformed)
            {
                report.Add(CanvasIdentityCodes.AssetReferenceMalformed, CanvasIdentitySeverity.Error, AttachmentType, attachment.Id, path + ".Reference", null,
                    $"资产标识不可用：{reason}。资产标识按路径/URI 校验，不按 GUID 校验。");
                continue;
            }

            if (shape == AssetReferenceShape.NonCanonical)
                report.Add(CanvasIdentityCodes.AssetReferenceNonCanonical, CanvasIdentitySeverity.Warning, AttachmentType, attachment.Id, path + ".Reference", null,
                    $"资产标识可用但不是规范写法：{reason}；当前读取会按文件名解析，属既有兼容行为，不作为错误。");

            if (assetAccessible is not null && !assetAccessible(reference))
                report.Add(CanvasIdentityCodes.AssetUnreachable, CanvasIdentitySeverity.Warning, AttachmentType, attachment.Id, path + ".Reference", null,
                    "资产标识格式正确，但当前机器上取不到该文件；画布资产包可能尚未导入本机。");
        }

        ReportDuplicates(report, AttachmentType, "同一对象下的附件", ids, CanvasIdentitySeverity.Warning);
    }

    private enum AssetReferenceShape { Canonical, NonCanonical, Malformed }

    /// <summary>
    /// 判断资产标识的形状，不访问磁盘。
    /// 依据 <see cref="AssetStore.Resolve"/> 的真实读取行为：
    /// · asset:// 引用只取文件名解析，所以带目录的写法可用但非规范（警告），
    ///   而只剩目录名或文件名含非法字符的写法确实取不到文件（错误）；
    /// · 本地路径按 Windows 盘符/UNC 优先识别，逐段检查文件名非法字符（含 * 与 ?）；
    /// · 带方案的 URI 交给 Uri 解析，data: 之类同样按 URI 接受。
    /// </summary>
    private static AssetReferenceShape ClassifyAssetReference(string reference, out string reason)
    {
        reason = string.Empty;
        var value = reference.Trim();
        const string scheme = "asset://";

        if (value.StartsWith(scheme, StringComparison.Ordinal))
        {
            var name = value[scheme.Length..];
            if (string.IsNullOrWhiteSpace(name)) { reason = "asset:// 之后没有文件名"; return AssetReferenceShape.Malformed; }

            var fileName = Path.GetFileName(name);
            if (fileName.Length == 0 || fileName == "." || fileName == "..")
            {
                reason = $"解析后得到的是目录名（「{fileName}」）而不是文件";
                return AssetReferenceShape.Malformed;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                reason = "文件名包含 Windows 不允许的字符（如 * ? \" < > | :）";
                return AssetReferenceShape.Malformed;
            }

            if (!string.Equals(name, fileName, StringComparison.Ordinal))
            {
                reason = "asset:// 之后带了目录，读取时只会取文件名";
                return AssetReferenceShape.NonCanonical;
            }

            return AssetReferenceShape.Canonical;
        }

        // Windows 盘符与 UNC 优先按本地路径处理，避免被 Uri 解析成 file 方案后放过非法字符。
        if (IsWindowsDrivePath(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            return ClassifyLocalPath(value, out reason);

        if (LooksLikeUri(value))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
                return AssetReferenceShape.Canonical;
            reason = "疑似 URI 但无法解析";
            return AssetReferenceShape.Malformed;
        }

        return ClassifyLocalPath(value, out reason);
    }

    private static AssetReferenceShape ClassifyLocalPath(string value, out string reason)
    {
        reason = string.Empty;
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            reason = "路径包含非法字符";
            return AssetReferenceShape.Malformed;
        }

        var trimmed = IsWindowsDrivePath(value) ? value[2..] : value;
        foreach (var segment in trimmed.Split('/', '\\'))
        {
            if (segment.Length == 0) continue;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) continue;
            reason = $"路径片段「{segment}」包含 Windows 不允许的字符（如 * ? \" < > |）";
            return AssetReferenceShape.Malformed;
        }

        return AssetReferenceShape.Canonical;
    }

    private static bool IsWindowsDrivePath(string value) =>
        value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'
        && (value.Length == 2 || value[2] == '/' || value[2] == '\\');

    /// <summary>是否像带方案的 URI（单字母方案当作盘符，不在此判定）。</summary>
    private static bool LooksLikeUri(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 1) return false;
        for (var index = 0; index < colon; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character) && character != '+' && character != '-' && character != '.') return false;
        }

        return true;
    }

    // ── 通用工具 ───────────────────────────────────────────────────────────

    private static void AddCandidate<T>(Dictionary<Guid, List<T>> map, Guid id, T value)
    {
        if (id == Guid.Empty) return;
        if (!map.TryGetValue(id, out var list)) map[id] = list = new List<T>();
        list.Add(value);
    }

    private static IReadOnlyList<T> Candidates<T>(Dictionary<Guid, List<T>> map, Guid id) =>
        id != Guid.Empty && map.TryGetValue(id, out var list) ? list : Array.Empty<T>();

    /// <summary>唯一的候选才算确定；多个候选一律视为有歧义，不取第一条。</summary>
    private static (T? Value, bool Ambiguous) Single<T>(IReadOnlyList<T> candidates) where T : class =>
        candidates.Count == 1 ? (candidates[0], false) : (null, candidates.Count > 1);

    /// <summary>会作为查找键使用的 ID：空值按错误报告。</summary>
    private static void CheckKeyId(CanvasIdentityReport report, string objectType, Guid id, string path)
    {
        if (id != Guid.Empty) return;
        report.Add(CanvasIdentityCodes.IdEmpty, CanvasIdentitySeverity.Error, objectType, id, path, null,
            "ID 是空 GUID；该 ID 会被当作查找键使用，空值无法定位对象。此轮只报告，不生成替代 ID。");
    }

    /// <summary>不参与查找的标识：空值按警告报告。</summary>
    private static void CheckLooseId(CanvasIdentityReport report, string objectType, Guid id, string path, string label)
    {
        if (id != Guid.Empty) return;
        report.Add(CanvasIdentityCodes.IdEmpty, CanvasIdentitySeverity.Warning, objectType, id, path, null,
            $"{label}的 ID 是空 GUID；它当前不参与按 ID 查找，但空值会让去重与定位失效。");
    }

    private static void ReportDuplicates(
        CanvasIdentityReport report,
        string objectType,
        string scope,
        IReadOnlyList<(Guid Id, string Path)> entries,
        CanvasIdentitySeverity severity)
    {
        var groups = new Dictionary<Guid, List<string>>();
        foreach (var (id, path) in entries)
        {
            if (id == Guid.Empty) continue;
            if (!groups.TryGetValue(id, out var paths)) groups[id] = paths = new List<string>();
            paths.Add(path);
        }

        foreach (var group in groups)
        {
            if (group.Value.Count < 2) continue;
            var positions = string.Join("、", group.Value);
            foreach (var path in group.Value)
                report.Add(CanvasIdentityCodes.IdDuplicate, severity, objectType, group.Key, path, group.Key,
                    $"该 ID 在{scope}中出现 {group.Value.Count} 次（{positions}）；此处不静默取第一条，需要按上下文决定归属。");
        }
    }

    /// <summary>查找表：只在本次校验内构建，不写回画布；同一 ID 的多个对象都保留为候选。</summary>
    private sealed class Lookup
    {
        public Dictionary<Guid, List<WorkflowNode>> Nodes { get; } = new();
        public Dictionary<Guid, List<WorkTreeItem>> WorkTreeItems { get; } = new();
        public Dictionary<Guid, List<WorkflowEntity>> Entities { get; } = new();
        public Dictionary<Guid, List<VariantSlot>> Variants { get; } = new();
        public Dictionary<Guid, List<VersionSlot>> Versions { get; } = new();
    }

    private sealed record VariantSlot(WorkflowEntityVariant Variant, WorkflowEntity Owner);

    private sealed record VersionSlot(EntityVariantVersion Version, WorkflowEntityVariant Variant);
}
