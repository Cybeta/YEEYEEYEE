namespace YEEYEEYEE.Desktop;

/// <summary>
/// 复制时的 ID 映射（旧 ID → 新 ID），按对象种类分开保存。
/// 只有「在源画布中唯一可解析」的旧 ID 才进入映射：出现重复的旧 ID 不会被登记，
/// 相关引用保留原值并在报告里列为歧义，绝不取首条猜测归属。
/// 版本按「所属变体 + 版本 ID」记录，因为版本 ID 的作用域是所属变体（返工 R6）。
/// </summary>
public sealed class CanvasIdMap
{
    public Dictionary<Guid, Guid> Nodes { get; } = new();
    public Dictionary<Guid, Guid> Edges { get; } = new();
    public Dictionary<Guid, Guid> WorkTreeItems { get; } = new();
    public Dictionary<Guid, Guid> Entities { get; } = new();
    public Dictionary<Guid, Guid> Variants { get; } = new();

    /// <summary>版本映射：键是「所属变体的旧 ID + 版本旧 ID」，同一 GUID 出现在不同变体时互不干扰。</summary>
    public Dictionary<(Guid VariantId, Guid VersionId), Guid> Versions { get; } = new();

    public Dictionary<Guid, Guid> LayoutItems { get; } = new();
    public Dictionary<Guid, Guid> Attachments { get; } = new();
    public Dictionary<Guid, Guid> History { get; } = new();

    private readonly List<string> shared = new();
    private readonly List<string> ambiguities = new();

    /// <summary>集合外引用：目标不在复制范围内，按共享语义保留原 ID。</summary>
    public IReadOnlyList<string> SharedReferences => shared;

    /// <summary>无法唯一重映射的引用：旧 ID 重复或与上下文不符，保留原 ID 等待人工处理。</summary>
    public IReadOnlyList<string> Ambiguities => ambiguities;

    internal void NoteShared(string description) => shared.Add(description);

    internal void NoteAmbiguity(string description) => ambiguities.Add(description);

    /// <summary>本次复制一共换了多少个 ID。</summary>
    public int Count =>
        Nodes.Count + Edges.Count + WorkTreeItems.Count + Entities.Count + Variants.Count
        + Versions.Count + LayoutItems.Count + Attachments.Count + History.Count;
}

/// <summary>复制结果说明：数量、共享引用、歧义与提示。</summary>
public sealed record DuplicationReport(
    int Nodes,
    int Edges,
    int Entities,
    int Variants,
    int Versions,
    int WorkTreeItems,
    int RenamedEmptyIds,
    IReadOnlyList<string> SharedReferences,
    IReadOnlyList<string> Ambiguities,
    IReadOnlyList<string> Notes)
{
    /// <summary>是否存在需要人工处理的复制歧义。</summary>
    public bool NeedsAttention => Ambiguities.Count > 0;

    public string ToText()
    {
        var lines = new List<string>
        {
            $"复制：节点 {Nodes}、连线 {Edges}、实体 {Entities}、变体 {Variants}、版本 {Versions}、工作树条目 {WorkTreeItems}；"
            + $"补齐空 ID {RenamedEmptyIds} 个。"
        };
        lines.AddRange(SharedReferences.Select(item => "· 共享保留：" + item));
        lines.AddRange(Ambiguities.Select(item => "! 需人工处理：" + item));
        lines.AddRange(Notes.Select(item => "· " + item));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>复制整份画布的结果。</summary>
public sealed record CanvasCopyResult(WorkflowCanvasState Canvas, CanvasIdMap Map, DuplicationReport Report);

/// <summary>复制一批节点的结果：新对象与报告（不修改入参，也不自动加入画布）。</summary>
public sealed record NodeCopyResult(IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowEdge> Edges, CanvasIdMap Map, DuplicationReport Report);

/// <summary>
/// 复制与 ID 生命周期（目标 1.2 / 返工 R6、R10）。规则：
/// · 复制出来的每个对象都拿到新 ID，空 ID 也会被补齐；
/// · 集合内部引用按「源画布中的唯一候选 + 明确的所属上下文」重映射，版本一律按所属变体解析；
/// · 旧 ID 重复或与所写上下文不符时不猜归属：保留原引用并在报告里列为歧义；
/// · 集合外引用按共享语义保留原 ID，不做深拷贝、不按名称重新绑定；
/// · 版本锁定的语义原样保留：null 仍是跟随当前，指定版本仍是锁定快照。
///
/// 复制只读入参：先深拷贝再改，因此原对象与磁盘数据不受影响。
/// </summary>
public static class CanvasDuplication
{
    /// <summary>复制整份画布：所有对象换新 ID，集合内关系按上下文全部重映射。</summary>
    public static CanvasCopyResult DuplicateCanvas(WorkflowCanvasState source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var canvas = CanvasCloner.Clone(source);
        var map = new CanvasIdMap();
        var notes = new List<string>();
        var plan = new CopyPlan(canvas, map);

        // 第一步：按源画布结构与顺序分配新 ID。
        plan.AssignIds();

        // 第二步：改写引用，全部按上下文解析。
        var rewriter = new ReferenceRewriter(source, plan, map);
        for (var index_ = 0; index_ < canvas.Nodes.Count; index_++)
        {
            var node = canvas.Nodes[index_];
            var subject = $"节点「{node.Title}」";
            if (node.ParentNodeId is { } parentId) node.ParentNodeId = rewriter.MapNodeValue(parentId, subject + "的父节点");
            node.WorkTreeItemId = rewriter.MapWorkTreeValue(node.WorkTreeItemId, subject + "的工作树锚");
            RewriteReferences(rewriter, node.References, subject);
        }

        foreach (var edge in canvas.Edges)
        {
            edge.SourceNodeId = rewriter.MapNodeValue(edge.SourceNodeId, "连线起点节点");
            edge.TargetNodeId = rewriter.MapNodeValue(edge.TargetNodeId, "连线终点节点");
        }

        foreach (var item in canvas.WorkTree)
        {
            var subject = "工作树条目";
            item.ParentId = rewriter.MapWorkTreeValue(item.ParentId, subject + "的父条目");
            item.ChapterId = rewriter.MapWorkTreeValue(item.ChapterId, subject + "的章节");
            if (item.ResourceId is { } resourceId)
                item.ResourceId = rewriter.MapEntityValue(resourceId, resourceId, subject + "的资源");

            // 上下文必须先用源 ID 解析，再改写字段。
            var writtenEntityId = item.SourceEntityId ?? Guid.Empty;
            var writtenVariantId = item.SourceVariantId ?? Guid.Empty;
            var context = rewriter.ResolveVariantContext(writtenVariantId, writtenEntityId);
            if (item.SourceEntityId is not null)
                item.SourceEntityId = rewriter.MapEntityValue(writtenEntityId, writtenEntityId, subject + "的来源实体");
            if (item.SourceVariantId is not null)
                item.SourceVariantId = rewriter.MapVariantValue(writtenVariantId, writtenEntityId, subject + "的来源变体");
            item.SourceVersionId = rewriter.MapVersionValue(item.SourceVersionId, context, subject + "的来源版本");
            item.SupersedesVersionId = rewriter.MapVersionValue(item.SupersedesVersionId, context, subject + "的上一版本");
        }

        for (var entityIndex = 0; entityIndex < canvas.Entities.Count; entityIndex++)
        {
            var entity = canvas.Entities[entityIndex];
            for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
            {
                var variant = entity.Variants[variantIndex];
                RewriteLayout(rewriter, variant.Layout, $"实体「{entity.Name}」变体「{variant.Name}」");

                for (var versionIndex = 0; versionIndex < variant.Versions.Count; versionIndex++)
                {
                    var version = variant.Versions[versionIndex];
                    var context = (entityIndex, variantIndex);
                    version.SupersedesVersionId = rewriter.MapVersionValue(
                        version.SupersedesVersionId, context, "版本的上一版本");
                    RewriteLayout(rewriter, version.Layout, $"实体「{entity.Name}」版本「{version.Label}」");
                }
            }
        }

        var report = plan.BuildReport(map, notes);
        return new CanvasCopyResult(canvas, map, report);
    }

    /// <summary>
    /// 复制指定节点（含附件与生成历史）。无法唯一确定要复制哪一个节点时拒绝该 ID 并报告，
    /// 不会取首条、也不会复制出多份。父节点与连线只在复制集合内部重映射。
    /// </summary>
    public static NodeCopyResult DuplicateNodes(
        WorkflowCanvasState source,
        IReadOnlyList<Guid> nodeIds,
        bool includeEdges = true,
        float offsetX = 40f,
        float offsetY = 40f)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nodeIds);

        var map = new CanvasIdMap();
        var notes = new List<string>();
        var clones = CanvasCloner.Clone(source);
        var sourceById = new Dictionary<Guid, List<int>>();
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            var id = source.Nodes[index].Id;
            if (id == Guid.Empty) continue;
            if (!sourceById.TryGetValue(id, out var list)) sourceById[id] = list = new List<int>();
            list.Add(index);
        }

        // 选择要复制的节点：只有唯一命中的 ID 才接受，重复 ID 一律拒绝。
        var selected = new SortedSet<int>();
        foreach (var id in nodeIds.Distinct())
        {
            if (id == Guid.Empty)
            {
                notes.Add("请求里含空的节点 ID，无法定位，已跳过。");
                continue;
            }

            if (!sourceById.TryGetValue(id, out var slots) || slots.Count == 0)
            {
                notes.Add($"节点 {id} 在源画布中不存在，已跳过。");
                continue;
            }

            if (slots.Count > 1)
            {
                map.NoteAmbiguity($"节点 ID {id} 在源画布中出现 {slots.Count} 次，无法确定要复制哪一个，已拒绝该 ID（未复制任何副本）。");
                continue;
            }

            selected.Add(slots[0]);
        }

        var newIds = new Dictionary<int, Guid>();
        var emptyIds = 0;
        foreach (var index in selected)
        {
            var node = clones.Nodes[index];
            var created = NextId(node.Id, ref emptyIds);
            newIds[index] = created;
            node.Id = created;
            foreach (var attachment in node.Attachments)
            {
                var attachmentId = attachment.Id;
                attachment.Id = NextId(attachmentId, ref emptyIds);
                map.Attachments.TryAdd(attachmentId, attachment.Id);
            }

            foreach (var record in node.GenerationHistory)
            {
                var historyId = record.Id;
                record.Id = NextId(historyId, ref emptyIds);
                map.History.TryAdd(historyId, record.Id);
            }
        }

        foreach (var index in selected)
        {
            var node = clones.Nodes[index];
            node.X += offsetX;
            node.Y += offsetY;

            if (node.ParentNodeId is { } parentId)
            {
                var parentSlot = newIds.Keys.FirstOrDefault(candidate => source.Nodes[candidate].Id == parentId, -1);
                if (parentSlot >= 0) node.ParentNodeId = newIds[parentSlot];
                else notes.Add($"节点「{node.Title}」的父节点不在复制范围内，按原样保留（集合外引用）。");
            }
        }

        var copiedNodes = selected.Select(index => clones.Nodes[index]).ToList();
        foreach (var index in selected) map.Nodes.TryAdd(source.Nodes[index].Id, newIds[index]);

        var copiedEdges = new List<WorkflowEdge>();
        if (includeEdges)
        {
            foreach (var edge in clones.Edges)
            {
                var sourceSlot = selected.FirstOrDefault(candidate => source.Nodes[candidate].Id == edge.SourceNodeId, -1);
                var targetSlot = selected.FirstOrDefault(candidate => source.Nodes[candidate].Id == edge.TargetNodeId, -1);
                if (sourceSlot < 0 || targetSlot < 0) continue;
                var copy = new WorkflowEdge
                {
                    SourceNodeId = newIds[sourceSlot],
                    TargetNodeId = newIds[targetSlot],
                    SourcePort = edge.SourcePort,
                    TargetPort = edge.TargetPort
                };
                map.Edges.TryAdd(edge.Id, copy.Id);
                copiedEdges.Add(copy);
            }
        }

        if (copiedNodes.Any(node => node.References.Count > 0))
            notes.Add("节点引用的是画布级设定库，复制后仍指向同一实体/变体/版本（共享语义，不复制设定正文）。");

        var report = new DuplicationReport(
            copiedNodes.Count, copiedEdges.Count, 0, 0, 0, 0,
            emptyIds, map.SharedReferences, map.Ambiguities, notes);
        return new NodeCopyResult(copiedNodes, copiedEdges, map, report);
    }

    /// <summary>把复制出来的节点与连线追加到画布（同 ID 冲突时跳过，避免破坏现有对象）。</summary>
    public static int Append(WorkflowCanvasState target, IEnumerable<WorkflowNode> nodes, IEnumerable<WorkflowEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var added = 0;
        var nodeIds = target.Nodes.Select(node => node.Id).ToHashSet();
        foreach (var node in nodes)
        {
            if (!nodeIds.Add(node.Id)) continue;
            target.Nodes.Add(node);
            added++;
        }

        var edgeIds = target.Edges.Select(edge => edge.Id).ToHashSet();
        foreach (var edge in edges)
        {
            if (!edgeIds.Add(edge.Id)) continue;
            target.Edges.Add(edge);
        }

        return added;
    }

    // ── 内部实现 ───────────────────────────────────────────────────────────

    /// <summary>分配新 ID，并统计被补齐的空 ID 数量。</summary>
    private static Guid NextId(Guid oldId, ref int emptyIds)
    {
        if (oldId == Guid.Empty) emptyIds++;
        return Guid.NewGuid();
    }

    private static void RewriteReferences(ReferenceRewriter rewriter, IReadOnlyList<NodeReference> references, string subject)
    {
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            var path = $"{subject}的第 {index + 1} 条引用";

            // 上下文与映射都必须用源 ID 计算，不能用刚改写过的新 ID。
            var writtenEntityId = reference.EntityId;
            var writtenVariantId = reference.VariantId;
            var context = rewriter.ResolveVariantContext(writtenVariantId, writtenEntityId);
            reference.EntityId = rewriter.MapEntityValue(writtenEntityId, writtenEntityId, path + "的实体");
            reference.VariantId = rewriter.MapVariantValue(writtenVariantId, writtenEntityId, path + "的变体");
            reference.VariantVersionId = rewriter.MapVersionValue(reference.VariantVersionId, context, path + "的锁定版本");
        }
    }

    private static void RewriteLayout(ReferenceRewriter rewriter, SceneLayout? layout, string subject)
    {
        if (layout is null) return;
        foreach (var item in layout.Items)
        {
            if (item.EntityId is not { } entityId || entityId == Guid.Empty) continue;
            item.EntityId = rewriter.MapEntityValue(entityId, entityId, $"{subject}的布局元素「{item.Element}」");
        }
    }

    /// <summary>按源画布结构与顺序分配新 ID，并登记「唯一可解析」的旧 → 新映射。</summary>
    private sealed class CopyPlan
    {
        private int emptyIds;
        private readonly WorkflowCanvasState canvas;
        private readonly CanvasIdMap map;
        private readonly HashSet<string> reportedDuplicates = new(StringComparer.Ordinal);

        public List<Guid> NodeIds { get; } = new();
        public List<Guid> EdgeIds { get; } = new();
        public List<Guid> WorkTreeIds { get; } = new();
        public List<Guid> EntityIds { get; } = new();
        public List<List<Guid>> VariantIds { get; } = new();
        public List<List<List<Guid>>> VersionIds { get; } = new();
        public int EmptyIds => emptyIds;

        public CopyPlan(WorkflowCanvasState canvas, CanvasIdMap map)
        {
            this.canvas = canvas;
            this.map = map;
        }

        public void AssignIds()
        {
            var nodeCounts = Count(canvas.Nodes.Select(node => node.Id));
            var edgeCounts = Count(canvas.Edges.Select(edge => edge.Id));
            var workTreeCounts = Count(canvas.WorkTree.Select(item => item.Id));
            var entityCounts = Count(canvas.Entities.Select(entity => entity.Id));

            for (var entityIndex = 0; entityIndex < canvas.Entities.Count; entityIndex++)
            {
                var entity = canvas.Entities[entityIndex];
                var entityOldId = entity.Id;
                var entityId = NextId(entityOldId);
                Register(map.Entities, "WorkflowEntity", entityOldId, entityId, entityCounts);
                entity.Id = entityId;
                EntityIds.Add(entityId);

                var variants = new List<Guid>();
                var versionsPerVariant = new List<List<Guid>>();
                var variantCounts = Count(entity.Variants.Select(variant => variant.Id));
                for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
                {
                    var variant = entity.Variants[variantIndex];
                    var variantOldId = variant.Id;
                    var variantId = NextId(variantOldId);
                    Register(map.Variants, "WorkflowEntityVariant", variantOldId, variantId, variantCounts);
                    variant.Id = variantId;
                    variants.Add(variantId);

                    foreach (var attachment in variant.Attachments)
                    {
                        var old = attachment.Id;
                        attachment.Id = NextId(old);
                        // 附件 ID 不参与查找，映射只作统计参考。
                        map.Attachments.TryAdd(old, attachment.Id);
                    }

                    RenameLayout(variant.Layout);

                    var versions = new List<Guid>();
                    var versionCounts = Count(variant.Versions.Select(version => version.Id));
                    for (var versionIndex = 0; versionIndex < variant.Versions.Count; versionIndex++)
                    {
                        var version = variant.Versions[versionIndex];
                        var versionOldId = version.Id;
                        var versionId = NextId(versionOldId);
                        // 版本按所属变体登记：同一 GUID 出现在不同变体是合法的，互不影响。
                        if (versionOldId != Guid.Empty && versionCounts[versionOldId] == 1)
                            map.Versions.TryAdd((variantOldId, versionOldId), versionId);
                        else if (versionOldId != Guid.Empty)
                            NoteDuplicate("EntityVariantVersion", versionOldId, versionCounts[versionOldId]);

                        version.Id = versionId;
                        versions.Add(versionId);

                        foreach (var attachment in version.Attachments)
                        {
                            var old = attachment.Id;
                            attachment.Id = NextId(old);
                            map.Attachments.TryAdd(old, attachment.Id);
                        }

                        RenameLayout(version.Layout);
                    }

                    versionsPerVariant.Add(versions);
                }

                VariantIds.Add(variants);
                VersionIds.Add(versionsPerVariant);
            }

            for (var index = 0; index < canvas.Nodes.Count; index++)
            {
                var node = canvas.Nodes[index];
                var nodeOldId = node.Id;
                var nodeId = NextId(nodeOldId);
                Register(map.Nodes, "WorkflowNode", nodeOldId, nodeId, nodeCounts);
                node.Id = nodeId;
                NodeIds.Add(nodeId);

                foreach (var attachment in node.Attachments)
                {
                    var old = attachment.Id;
                    attachment.Id = NextId(old);
                    map.Attachments.TryAdd(old, attachment.Id);
                }

                foreach (var record in node.GenerationHistory)
                {
                    var old = record.Id;
                    record.Id = NextId(old);
                    map.History.TryAdd(old, record.Id);
                }
            }

            for (var index = 0; index < canvas.Edges.Count; index++)
            {
                var edge = canvas.Edges[index];
                var edgeOldId = edge.Id;
                var edgeId = NextId(edgeOldId);
                Register(map.Edges, "WorkflowEdge", edgeOldId, edgeId, edgeCounts);
                edge.Id = edgeId;
                EdgeIds.Add(edgeId);
            }

            for (var index = 0; index < canvas.WorkTree.Count; index++)
            {
                var item = canvas.WorkTree[index];
                var itemOldId = item.Id;
                var itemId = NextId(itemOldId);
                Register(map.WorkTreeItems, "WorkTreeItem", itemOldId, itemId, workTreeCounts);
                item.Id = itemId;
                WorkTreeIds.Add(itemId);

                foreach (var attachment in item.Attachments)
                {
                    var old = attachment.Id;
                    attachment.Id = NextId(old);
                    map.Attachments.TryAdd(old, attachment.Id);
                }
            }
        }

        /// <summary>只登记唯一 ID：空 ID 与重复 ID 一律不进映射，避免被当成「第一处」使用。</summary>
        private void Register(Dictionary<Guid, Guid> bucket, string objectType, Guid oldId, Guid newId, IReadOnlyDictionary<Guid, int> counts)
        {
            if (oldId == Guid.Empty) return;
            if (counts[oldId] == 1)
            {
                bucket.TryAdd(oldId, newId);
                return;
            }

            NoteDuplicate(objectType, oldId, counts[oldId]);
        }

        private void NoteDuplicate(string objectType, Guid id, int count)
        {
            if (!reportedDuplicates.Add($"{objectType}|{id}")) return;
            map.NoteAmbiguity($"{objectType} ID {id} 在源画布出现 {count} 次，无法唯一重映射，未登记映射；相关引用保留原 ID 待人工处理。");
        }

        private static Dictionary<Guid, int> Count(IEnumerable<Guid> ids)
        {
            var counts = new Dictionary<Guid, int>();
            foreach (var id in ids)
            {
                if (id == Guid.Empty) continue;
                counts[id] = counts.TryGetValue(id, out var existing) ? existing + 1 : 1;
            }

            return counts;
        }

        private void RenameLayout(SceneLayout? layout)
        {
            if (layout is null) return;
            var counts = Count(layout.Items.Select(item => item.Id));
            foreach (var item in layout.Items)
            {
                var old = item.Id;
                item.Id = NextId(old);
                if (old == Guid.Empty) continue;
                if (counts[old] == 1) map.LayoutItems.TryAdd(old, item.Id);
                else NoteDuplicate("SceneLayoutItem", old, counts[old]);
            }
        }

        private Guid NextId(Guid oldId) => CanvasDuplication.NextId(oldId, ref emptyIds);

        public DuplicationReport BuildReport(CanvasIdMap idMap, IReadOnlyList<string> notes) =>
            new(canvas.Nodes.Count, canvas.Edges.Count, canvas.Entities.Count,
                idMap.Variants.Count, idMap.Versions.Count, canvas.WorkTree.Count,
                EmptyIds, idMap.SharedReferences, idMap.Ambiguities, notes);
    }

    /// <summary>源画布的候选索引：每个旧 ID 保留全部候选，用于「唯一才映射」的判定。</summary>
    private sealed class SourceIndex
    {
        public Dictionary<Guid, List<int>> NodeSlots { get; } = new();
        public Dictionary<Guid, List<int>> WorkTreeSlots { get; } = new();
        public Dictionary<Guid, List<int>> EntitySlots { get; } = new();
        public Dictionary<Guid, List<(int Entity, int Variant)>> VariantSlots { get; } = new();

        public SourceIndex(WorkflowCanvasState source)
        {
            for (var index = 0; index < source.Nodes.Count; index++) Add(NodeSlots, source.Nodes[index].Id, index);
            for (var index = 0; index < source.WorkTree.Count; index++) Add(WorkTreeSlots, source.WorkTree[index].Id, index);
            for (var entityIndex = 0; entityIndex < source.Entities.Count; entityIndex++)
            {
                var entity = source.Entities[entityIndex];
                Add(EntitySlots, entity.Id, entityIndex);
                for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
                    Add(VariantSlots, entity.Variants[variantIndex].Id, (entityIndex, variantIndex));
            }
        }

        private static void Add<T>(Dictionary<Guid, List<T>> map, Guid id, T slot)
        {
            if (id == Guid.Empty) return;
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<T>();
            list.Add(slot);
        }

        public IReadOnlyList<T> Slots<T>(Dictionary<Guid, List<T>> map, Guid id) =>
            map.TryGetValue(id, out var list) ? list : Array.Empty<T>();
    }

    /// <summary>引用改写器：唯一候选才映射；重复候选或与上下文不符时保留原值并记录歧义。</summary>
    private sealed class ReferenceRewriter
    {
        private readonly WorkflowCanvasState source;
        private readonly SourceIndex index;
        private readonly CopyPlan plan;
        private readonly CanvasIdMap map;

        public ReferenceRewriter(WorkflowCanvasState source, CopyPlan plan, CanvasIdMap map)
        {
            this.source = source;
            index = new SourceIndex(source);
            this.plan = plan;
            this.map = map;
        }

        public Guid MapNodeValue(Guid nodeId, string subject)
        {
            if (nodeId == Guid.Empty) return nodeId;
            var slots = index.Slots(index.NodeSlots, nodeId);
            if (slots.Count == 1) return plan.NodeIds[slots[0]];
            if (slots.Count == 0)
            {
                map.NoteShared($"{subject} {nodeId} 不在复制范围内或不存在，保留原 ID。");
                return nodeId;
            }

            map.NoteAmbiguity($"{subject}：节点 ID {nodeId} 在源画布出现 {slots.Count} 次，无法唯一重映射，保留原 ID 待人工处理。");
            return nodeId;
        }

        /// <summary>nullable 的工作树引用：null 必须保持 null，不能变成空 GUID。</summary>
        public Guid? MapWorkTreeValue(Guid? itemId, string subject)
        {
            if (itemId is not { } id) return null;
            if (id == Guid.Empty) return Guid.Empty;
            var slots = index.Slots(index.WorkTreeSlots, id);
            if (slots.Count == 1) return plan.WorkTreeIds[slots[0]];
            if (slots.Count == 0)
            {
                map.NoteShared($"{subject} {id} 不在复制范围内或不存在，保留原 ID。");
                return id;
            }

            map.NoteAmbiguity($"{subject}：工作树条目 ID {id} 在源画布出现 {slots.Count} 次，无法唯一重映射，保留原 ID 待人工处理。");
            return id;
        }

        public Guid MapEntityValue(Guid entityId, Guid writtenEntityId, string subject)
        {
            if (entityId == Guid.Empty) return entityId;
            var slots = index.Slots(index.EntitySlots, entityId);
            if (slots.Count == 1) return plan.EntityIds[slots[0]];
            if (slots.Count == 0)
            {
                map.NoteShared($"{subject} {entityId} 不在复制范围内或不存在，保留原 ID。");
                return entityId;
            }

            map.NoteAmbiguity($"{subject}：实体 ID {entityId}（写入值 {writtenEntityId}）在源画布出现 {slots.Count} 次，无法唯一重映射，保留原 ID 待人工处理。");
            return entityId;
        }

        public Guid MapVariantValue(Guid variantId, Guid writtenEntityId, string subject)
        {
            if (variantId == Guid.Empty) return variantId;
            var slots = index.Slots(index.VariantSlots, variantId);
            if (slots.Count == 0)
            {
                map.NoteShared($"{subject} {variantId} 不在复制范围内或不存在，保留原 ID。");
                return variantId;
            }

            var entitySlot = writtenEntityId == Guid.Empty ? (int?)null : ResolveEntitySlot(writtenEntityId);
            if (entitySlot is { } entity)
            {
                var matching = slots.Where(slot => slot.Entity == entity).ToList();
                if (matching.Count == 1) return plan.VariantIds[matching[0].Entity][matching[0].Variant];
                if (matching.Count > 1)
                {
                    map.NoteAmbiguity($"{subject}：同一实体内有 {matching.Count} 个变体使用 ID {variantId}，无法唯一重映射，保留原 ID 待人工处理。");
                    return variantId;
                }

                map.NoteAmbiguity($"{subject}：变体 {variantId} 不属于所写实体 {writtenEntityId}，无法按上下文重映射，保留原 ID 待人工处理。");
                return variantId;
            }

            if (slots.Count == 1) return plan.VariantIds[slots[0].Entity][slots[0].Variant];
            map.NoteAmbiguity($"{subject}：变体 ID {variantId} 出现在 {slots.Count} 个实体下且没有可确定的实体上下文，保留原 ID 待人工处理。");
            return variantId;
        }

        public Guid? MapVersionValue(Guid? versionId, (int Entity, int Variant)? context, string subject)
        {
            if (versionId is not { } id) return null; // null：跟随当前版本，语义保留
            if (id == Guid.Empty) return id;

            if (context is { } scope)
            {
                var versions = source.Entities[scope.Entity].Variants[scope.Variant].Versions;
                var matches = CountMatches(versions, id);
                if (matches == 1) return plan.VersionIds[scope.Entity][scope.Variant][IndexOfVersion(versions, id)];
                if (matches > 1)
                {
                    map.NoteAmbiguity($"{subject}：同一变体内有 {matches} 个版本使用 ID {id}，无法唯一重映射，保留原 ID 待人工处理。");
                    return id;
                }

                map.NoteAmbiguity($"{subject}：版本 {id} 不在所写变体内，无法按上下文重映射，保留原 ID 待人工处理。");
                return id;
            }

            var slots = VersionSlots(id);
            if (slots.Count == 1) return plan.VersionIds[slots[0].Entity][slots[0].Variant][slots[0].Version];
            if (slots.Count == 0)
            {
                map.NoteShared($"{subject} {id} 不在复制范围内或不存在，保留原 ID。");
                return id;
            }

            map.NoteAmbiguity($"{subject}：版本 ID {id} 出现在 {slots.Count} 个变体中且没有可确定的变体上下文，保留原 ID 待人工处理。");
            return id;
        }

        /// <summary>解析变体上下文（所属实体索引 + 变体索引）：唯一才返回，否则 null。</summary>
        public (int Entity, int Variant)? ResolveVariantContext(Guid variantId, Guid writtenEntityId)
        {
            if (variantId == Guid.Empty) return null;
            var slots = index.Slots(index.VariantSlots, variantId);
            if (slots.Count == 0) return null;

            if (writtenEntityId != Guid.Empty)
            {
                var entitySlot = ResolveEntitySlot(writtenEntityId);
                if (entitySlot is not { } entity) return null;
                var matching = slots.Where(slot => slot.Entity == entity).ToList();
                return matching.Count == 1 ? (matching[0].Entity, matching[0].Variant) : null;
            }

            return slots.Count == 1 ? (slots[0].Entity, slots[0].Variant) : null;
        }

        private int? ResolveEntitySlot(Guid entityId)
        {
            var slots = index.Slots(index.EntitySlots, entityId);
            return slots.Count == 1 ? slots[0] : null;
        }

        private List<(int Entity, int Variant, int Version)> VersionSlots(Guid versionId)
        {
            var result = new List<(int Entity, int Variant, int Version)>();
            for (var entityIndex = 0; entityIndex < source.Entities.Count; entityIndex++)
            {
                var entity = source.Entities[entityIndex];
                for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
                {
                    var versions = entity.Variants[variantIndex].Versions;
                    for (var versionIndex = 0; versionIndex < versions.Count; versionIndex++)
                    {
                        if (versions[versionIndex].Id == versionId) result.Add((entityIndex, variantIndex, versionIndex));
                    }
                }
            }

            return result;
        }

        private static int CountMatches(IReadOnlyList<EntityVariantVersion> versions, Guid versionId)
        {
            var matches = 0;
            for (var index = 0; index < versions.Count; index++)
            {
                if (versions[index].Id == versionId) matches++;
            }

            return matches;
        }

        private static int IndexOfVersion(IReadOnlyList<EntityVariantVersion> versions, Guid versionId)
        {
            for (var index = 0; index < versions.Count; index++)
            {
                if (versions[index].Id == versionId) return index;
            }

            return -1;
        }
    }
}
