using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>
/// 画布数据格式版本。<see cref="RecentCanvasState.FormatVersion"/> 缺省为 0，代表未标注格式的旧文件。
/// 版本号只增不改；新增字段一律可选，旧读取器忽略未知字段即可继续打开。
/// </summary>
public static class CanvasFormat
{
    /// <summary>未标注格式的旧文件：可能含旧单引用字段、旧图片路径，以及没有值的 GUID。</summary>
    public const int Legacy = 0;

    /// <summary>当前格式：引用列表 + 附件 + 实体/工作树，标识完整并在保存时标注。</summary>
    public const int Current = 1;
}

/// <summary>迁移做过的确定性改动。</summary>
public enum MigrationChangeKind
{
    /// <summary>为空 GUID 的对象补上了新 ID（按来源文档与字段路径确定的稳定 ID）。</summary>
    AssignedId,

    /// <summary>旧单引用字段搬进了引用列表。</summary>
    MovedLegacyReference,

    /// <summary>旧图片路径搬进了附件列表。</summary>
    MovedLegacyAttachments,

    /// <summary>旧字段已有等价的新数据，按规则清空旧字段。</summary>
    DiscardedLegacyField,

    /// <summary>空资产标识的旧路径被跳过。</summary>
    SkippedLegacyAssetPath,

    /// <summary>为章节条目补齐/归一化显式顺序（新增成员只追加）。</summary>
    NormalizedChapterOrder
}

/// <summary>一次迁移改动的记录：对象类型、对象 ID、字段路径与说明。</summary>
public sealed record MigrationChange(
    MigrationChangeKind Kind,
    string ObjectType,
    string ObjectId,
    string FieldPath,
    string Detail)
{
    public override string ToString() => $"[{Kind}] {ObjectType}({ObjectId}) {FieldPath}：{Detail}";
}

/// <summary>
/// 迁移无法确定、必须由用户决定的一项。迁移不猜绑定，只把问题与可选处理方式列出来。
/// </summary>
public sealed record MigrationAmbiguity(
    string ObjectType,
    string ObjectId,
    string FieldPath,
    string Reason,
    string Handling)
{
    public override string ToString() => $"{ObjectType}({ObjectId}) {FieldPath}：{Reason}（处理：{Handling}）";
}

/// <summary>迁移报告：从哪个版本迁到哪个版本、改了什么、有哪些歧义留待处理。</summary>
public sealed record MigrationReport(
    int FromVersion,
    int ToVersion,
    IReadOnlyList<MigrationChange> Changes,
    IReadOnlyList<MigrationAmbiguity> Ambiguities)
{
    /// <summary>
    /// 来源格式版本高于当前支持的版本：不做任何迁移，也不得标注为当前版本或覆盖保存。
    /// 打开时只能只读查看并给出可读诊断。
    /// </summary>
    public bool UnsupportedFormat { get; init; }

    /// <summary>本次迁移是否产生了确定性改动（重复迁移应为 false）。</summary>
    public bool Changed => Changes.Count > 0;

    /// <summary>没有迁移动作时的空报告（例如复制节点后只需要看校验结果）。</summary>
    public static MigrationReport NotMigrated { get; } = new(
        CanvasFormat.Current,
        CanvasFormat.Current,
        Array.Empty<MigrationChange>(),
        Array.Empty<MigrationAmbiguity>());

    /// <summary>不支持更高格式版本时的报告模板。</summary>
    public static MigrationReport Unsupported(int fromVersion) => new(
        fromVersion,
        fromVersion,
        Array.Empty<MigrationChange>(),
        Array.Empty<MigrationAmbiguity>())
    {
        UnsupportedFormat = true
    };

    public string ToText()
    {
        if (UnsupportedFormat)
            return $"格式版本 {FromVersion} 高于当前支持的 {CanvasFormat.Current}：本轮不做迁移，也不会标注为当前版本；"
                + "只读查看可以继续，但覆盖保存与导入会被拒绝，以免丢失未知数据。";

        var lines = new List<string>
        {
            $"格式版本 {FromVersion} → {ToVersion}：确定改动 {Changes.Count} 项，待处理歧义 {Ambiguities.Count} 项。"
        };
        lines.AddRange(Changes.Select(change => "· " + change));
        lines.AddRange(Ambiguities.Select(item => "! " + item));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>迁移结果：迁移后的画布状态与报告。</summary>
public sealed record CanvasMigrationResult(RecentCanvasState State, MigrationReport Report);

/// <summary>显式修复操作的结果。</summary>
public sealed record CanvasRepairOutcome(int Affected, string Summary)
{
    public bool Changed => Affected > 0;
}

/// <summary>
/// 旧画布迁移（目标 1.3 / 返工 R5、R11）。只做能确定的补全：
/// · 旧单引用字段 / 旧图片路径按既定语义搬到 <c>References</c> 与 <c>Attachments</c>；
/// · 空 GUID 的对象补新 ID，ID 由「来源文档指纹 + 对象类型 + 字段路径」确定生成，
///   因此同一份未修改的来源连续迁移会得到同一批 ID，导入身份稳定、不会随迁移漂移；
/// · 指向不存在对象、或空值语义不唯一的引用一律留成歧义项，不猜绑定、不改写含义；
/// · 来源格式版本高于当前支持版本时不做任何迁移，也不标注为当前版本。
///
/// 迁移与只读校验分开：校验器只报告，迁移才改动。迁移幂等：第二次运行不再产生确定改动。
/// </summary>
public static class CanvasMigration
{
    private static readonly JsonSerializerOptions KeyOptions = new();

    /// <summary>迁移整份画布状态（含格式版本标注）。调用方传入的对象会被就地迁移。</summary>
    public static CanvasMigrationResult Migrate(RecentCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FormatVersion > CanvasFormat.Current)
            return new CanvasMigrationResult(state, MigrationReport.Unsupported(state.FormatVersion));

        var canvas = state.Canvas ?? new WorkflowCanvasState();
        var report = MigrateCanvas(canvas, state.FormatVersion);
        var migrated = state with { Canvas = canvas, FormatVersion = CanvasFormat.Current };
        return new CanvasMigrationResult(migrated, report);
    }

    /// <summary>
    /// 就地迁移画布内容。<paramref name="fromVersion"/> 低于当前格式时才搬旧字段，
    /// 空 ID 补全与歧义收集每次都做，因此重复调用没有额外改动。
    /// </summary>
    public static MigrationReport MigrateCanvas(WorkflowCanvasState canvas, int fromVersion)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (fromVersion > CanvasFormat.Current) return MigrationReport.Unsupported(fromVersion);

        var changes = new List<MigrationChange>();
        var ambiguities = new List<MigrationAmbiguity>();

        // 文档指纹取迁移前的原始内容：同一份未修改的来源每次都会得到相同的补全 ID。
        var seed = new IdSeed(DocumentKey(canvas));

        if (fromVersion < CanvasFormat.Current)
            MigrateLegacyFields(canvas, seed, changes, ambiguities);

        BackfillEmptyIds(canvas, seed, changes);
        BackfillChapterOrder(canvas, changes);
        CollectAmbiguities(canvas, ambiguities);

        return new MigrationReport(fromVersion, CanvasFormat.Current, changes, ambiguities);
    }

    /// <summary>
    /// 为章节条目补齐显式顺序（大目标 B）。规则确定且幂等：同一父章节下先按已有顺序，
    /// 再按「第N章」数字、最后按条目出现顺序，按固定步长赋值；第二次运行不再产生改动。
    /// </summary>
    private static void BackfillChapterOrder(WorkflowCanvasState canvas, List<MigrationChange> changes)
    {
        var before = CanvasChapters.ChapterItems(canvas).ToDictionary(item => item.Id, item => item.Order);
        if (!CanvasChapters.EnsureExplicitOrder(canvas)) return;

        foreach (var item in CanvasChapters.ChapterItems(canvas))
        {
            if (!before.TryGetValue(item.Id, out var previous) || previous == item.Order) continue;
            changes.Add(new MigrationChange(MigrationChangeKind.NormalizedChapterOrder, "WorkTreeItem", item.Id.ToString(),
                "WorkTree[].Order", $"章节「{item.Name}」补齐显式顺序：{(previous == 0 ? "（未指定）" : previous.ToString())} → {item.Order}。"));
        }
    }

    /// <summary>迁移前的文档指纹，用于生成稳定的补全 ID。</summary>
    public static string DocumentKey(WorkflowCanvasState canvas) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canvas, KeyOptions))));

    /// <summary>由「文档指纹 + 对象类型 + 字段路径」确定生成的 ID。</summary>
    private static Guid DeterministicId(string documentKey, string objectType, string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentKey}|{objectType}|{path}"));
        var created = new Guid(digest.AsSpan(0, 16));
        return created == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : created;
    }

    private readonly record struct IdSeed(string DocumentKey)
    {
        public Guid For(string objectType, string path) => DeterministicId(DocumentKey, objectType, path);
    }

    // ── 旧字段搬运 ─────────────────────────────────────────────────────────

    private static void MigrateLegacyFields(
        WorkflowCanvasState canvas,
        IdSeed seed,
        List<MigrationChange> changes,
        List<MigrationAmbiguity> ambiguities)
    {
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";

            if (node.LegacyEntityId is not null || node.LegacyVariantId is not null)
            {
                var entityId = node.LegacyEntityId ?? Guid.Empty;
                var variantId = node.LegacyVariantId ?? Guid.Empty;
                var versionId = node.LegacyVariantVersionId;

                if (node.References.Count == 0)
                {
                    node.References.Add(new NodeReference
                    {
                        EntityId = entityId,
                        VariantId = variantId,
                        VariantVersionId = versionId
                    });
                    changes.Add(new MigrationChange(MigrationChangeKind.MovedLegacyReference, "WorkflowNode", node.Id.ToString(),
                        path + ".References[0]", $"旧单引用（实体 {Describe(entityId)}、变体 {Describe(variantId)}）已转为引用列表。"));

                    if (!Resolves(canvas, entityId, variantId))
                        ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), path + ".References[0]",
                            $"旧引用指向的实体/变体在画布中不存在（实体 {Describe(entityId)}、变体 {Describe(variantId)}）",
                            "保留该引用等待人工处理，或使用「丢弃无法解析的旧引用」"));
                }
                else
                {
                    changes.Add(new MigrationChange(MigrationChangeKind.DiscardedLegacyField, "WorkflowNode", node.Id.ToString(),
                        path + ".LegacyEntityId", "节点已有引用列表，旧单引用字段按规则清空，未覆盖现有引用。"));
                    ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), path + ".LegacyEntityId",
                        $"旧单引用（实体 {Describe(entityId)}）与现有引用列表并存，无法判断两者关系",
                        "核对引用列表后手工补充；迁移不合并、不覆盖"));
                }

                node.LegacyEntityId = null;
                node.LegacyVariantId = null;
                node.LegacyVariantVersionId = null;
            }

            if (node.LegacyAssetPaths.Count == 0) continue;

            var movedReferences = 0;
            foreach (var reference in node.LegacyAssetPaths)
            {
                if (string.IsNullOrWhiteSpace(reference))
                {
                    changes.Add(new MigrationChange(MigrationChangeKind.SkippedLegacyAssetPath, "WorkflowNode", node.Id.ToString(),
                        path + ".AssetPaths", "空的旧图片路径已跳过。"));
                    continue;
                }

                var attachmentPath = $"{path}.Attachments[{node.Attachments.Count}]";
                node.Attachments.Add(new WorkflowAttachment
                {
                    Id = seed.For("WorkflowAttachment", attachmentPath),
                    Kind = WorkflowAttachment.KindOf(reference),
                    Reference = reference,
                    Name = Path.GetFileName(reference),
                    Source = "旧画布迁移"
                });
                movedReferences++;
            }

            changes.Add(new MigrationChange(MigrationChangeKind.MovedLegacyAttachments, "WorkflowNode", node.Id.ToString(),
                path + ".Attachments", $"旧图片路径转成附件：{movedReferences} 个。"));
            node.LegacyAssetPaths.Clear();
        }
    }

    private static bool Resolves(WorkflowCanvasState canvas, Guid entityId, Guid variantId)
    {
        if (entityId != Guid.Empty) return canvas.Entities.Any(entity => entity.Id == entityId);
        if (variantId != Guid.Empty) return canvas.Entities.Any(entity => entity.Variants.Any(variant => variant.Id == variantId));
        return false;
    }

    // ── 空 ID 补全 ─────────────────────────────────────────────────────────

    private static void BackfillEmptyIds(WorkflowCanvasState canvas, IdSeed seed, List<MigrationChange> changes)
    {
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";
            node.Id = EnsureId(node.Id, "WorkflowNode", path, seed, changes);

            for (var attachmentIndex = 0; attachmentIndex < node.Attachments.Count; attachmentIndex++)
                BackfillAttachment(node.Attachments[attachmentIndex], "WorkflowAttachment", $"{path}.Attachments[{attachmentIndex}]", seed, changes);

            for (var historyIndex = 0; historyIndex < node.GenerationHistory.Count; historyIndex++)
            {
                var record = node.GenerationHistory[historyIndex];
                record.Id = EnsureId(record.Id, "GenerationHistory", $"{path}.GenerationHistory[{historyIndex}]", seed, changes);
            }
        }

        for (var index = 0; index < canvas.Edges.Count; index++)
        {
            var edge = canvas.Edges[index];
            edge.Id = EnsureId(edge.Id, "WorkflowEdge", $"Edges[{index}]", seed, changes);
        }

        for (var index = 0; index < canvas.WorkTree.Count; index++)
        {
            var item = canvas.WorkTree[index];
            var path = $"WorkTree[{index}]";
            item.Id = EnsureId(item.Id, "WorkTreeItem", path, seed, changes);
            for (var attachmentIndex = 0; attachmentIndex < item.Attachments.Count; attachmentIndex++)
                BackfillAttachment(item.Attachments[attachmentIndex], "WorkflowAttachment", $"{path}.Attachments[{attachmentIndex}]", seed, changes);
        }

        for (var entityIndex = 0; entityIndex < canvas.Entities.Count; entityIndex++)
        {
            var entity = canvas.Entities[entityIndex];
            var entityPath = $"Entities[{entityIndex}]";
            entity.Id = EnsureId(entity.Id, "WorkflowEntity", entityPath, seed, changes);

            for (var variantIndex = 0; variantIndex < entity.Variants.Count; variantIndex++)
            {
                var variant = entity.Variants[variantIndex];
                var variantPath = $"{entityPath}.Variants[{variantIndex}]";
                variant.Id = EnsureId(variant.Id, "WorkflowEntityVariant", variantPath, seed, changes);

                for (var attachmentIndex = 0; attachmentIndex < variant.Attachments.Count; attachmentIndex++)
                    BackfillAttachment(variant.Attachments[attachmentIndex], "WorkflowAttachment", $"{variantPath}.Attachments[{attachmentIndex}]", seed, changes);
                BackfillLayout(variant.Layout, $"{variantPath}.Layout", seed, changes);

                for (var versionIndex = 0; versionIndex < variant.Versions.Count; versionIndex++)
                {
                    var version = variant.Versions[versionIndex];
                    var versionPath = $"{variantPath}.Versions[{versionIndex}]";
                    version.Id = EnsureId(version.Id, "EntityVariantVersion", versionPath, seed, changes);

                    for (var attachmentIndex = 0; attachmentIndex < version.Attachments.Count; attachmentIndex++)
                        BackfillAttachment(version.Attachments[attachmentIndex], "WorkflowAttachment", $"{versionPath}.Attachments[{attachmentIndex}]", seed, changes);
                    BackfillLayout(version.Layout, $"{versionPath}.Layout", seed, changes);
                }
            }
        }
    }

    private static void BackfillAttachment(WorkflowAttachment attachment, string objectType, string path, IdSeed seed, List<MigrationChange> changes) =>
        attachment.Id = EnsureId(attachment.Id, objectType, path, seed, changes);

    private static void BackfillLayout(SceneLayout? layout, string path, IdSeed seed, List<MigrationChange> changes)
    {
        if (layout is null) return;
        for (var index = 0; index < layout.Items.Count; index++)
        {
            var item = layout.Items[index];
            item.Id = EnsureId(item.Id, "SceneLayoutItem", $"{path}.Items[{index}]", seed, changes);
        }
    }

    private static Guid EnsureId(Guid id, string objectType, string path, IdSeed seed, List<MigrationChange> changes)
    {
        if (id != Guid.Empty) return id;
        var created = seed.For(objectType, path + ".Id");
        changes.Add(new MigrationChange(MigrationChangeKind.AssignedId, objectType, created.ToString(), path + ".Id",
            $"空 ID 补为稳定 ID（原值 {Describe(Guid.Empty)}；同一份来源重复迁移得到同一 ID）。"));
        return created;
    }

    // ── 歧义收集 ───────────────────────────────────────────────────────────

    private static void CollectAmbiguities(WorkflowCanvasState canvas, List<MigrationAmbiguity> ambiguities)
    {
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";

            for (var referenceIndex = 0; referenceIndex < node.References.Count; referenceIndex++)
            {
                var reference = node.References[referenceIndex];
                var referencePath = $"{path}.References[{referenceIndex}]";

                if (reference.EntityId == Guid.Empty && reference.VariantId == Guid.Empty)
                {
                    ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), referencePath,
                        "引用既没有实体 ID 也没有变体 ID，无法确定指向哪一个设定",
                        "使用「丢弃无法解析的旧引用」，或手工补上实体/变体 ID"));
                    continue;
                }

                if (reference.VariantVersionId == Guid.Empty)
                    ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), referencePath + ".VariantVersionId",
                        "锁定版本是空 GUID，无法判断是「跟随当前」还是指向某个具体版本",
                        "使用「把空锁定版本改为跟随当前」，或手工指定版本"));
            }

            if (node.ParentNodeId == Guid.Empty)
                ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), path + ".ParentNodeId",
                    "父节点 ID 是空 GUID，无法确定布局归属", "手工指定父节点或清空该字段"));

            if (node.WorkTreeItemId == Guid.Empty)
                ambiguities.Add(new MigrationAmbiguity("WorkflowNode", node.Id.ToString(), path + ".WorkTreeItemId",
                    "工作树锚是空 GUID，无法确定跟随哪一个条目", "手工指定锚点或清空该字段"));
        }

        for (var index = 0; index < canvas.Edges.Count; index++)
        {
            var edge = canvas.Edges[index];
            if (edge.SourceNodeId != Guid.Empty && edge.TargetNodeId != Guid.Empty) continue;
            ambiguities.Add(new MigrationAmbiguity("WorkflowEdge", edge.Id.ToString(), $"Edges[{index}]",
                "连线端点是空 GUID，无法确定连的是哪一个节点", "手工指定端点或删除该连线"));
        }

        for (var index = 0; index < canvas.WorkTree.Count; index++)
        {
            var item = canvas.WorkTree[index];
            var path = $"WorkTree[{index}]";
            if (item.ParentId == Guid.Empty)
                ambiguities.Add(new MigrationAmbiguity("WorkTreeItem", item.Id.ToString(), path + ".ParentId",
                    "父条目是空 GUID，无法确定归属", "手工指定父条目或清空该字段"));
            if (item.SourceVersionId == Guid.Empty)
                ambiguities.Add(new MigrationAmbiguity("WorkTreeItem", item.Id.ToString(), path + ".SourceVersionId",
                    "来源版本是空 GUID，无法判断指向哪个版本", "手工指定版本或清空该字段"));
            if (item.SupersedesVersionId == Guid.Empty)
                ambiguities.Add(new MigrationAmbiguity("WorkTreeItem", item.Id.ToString(), path + ".SupersedesVersionId",
                    "上一版本是空 GUID，无法判断版本链", "手工指定版本或清空该字段"));
        }
    }

    // ── 显式处理入口（只在用户明确操作时调用） ─────────────────────────────

    /// <summary>丢弃无法解析的节点引用：空引用，或实体与变体都找不到的引用。返回被丢弃的条数。</summary>
    public static CanvasRepairOutcome DropUnresolvableReferences(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var dropped = 0;
        foreach (var node in canvas.Nodes)
        {
            dropped += node.References.RemoveAll(reference =>
                (reference.EntityId == Guid.Empty && reference.VariantId == Guid.Empty)
                || !Resolves(canvas, reference.EntityId, reference.VariantId));
        }

        return new CanvasRepairOutcome(dropped, dropped == 0 ? "没有需要丢弃的引用。" : $"已丢弃 {dropped} 条无法解析的引用。");
    }

    /// <summary>把空 GUID 的锁定版本改成 null（跟随当前版本）。返回改动条数。</summary>
    public static CanvasRepairOutcome TreatEmptyLockedVersionAsFollowCurrent(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var changed = 0;
        foreach (var node in canvas.Nodes)
        foreach (var reference in node.References)
        {
            if (reference.VariantVersionId != Guid.Empty) continue;
            reference.VariantVersionId = null;
            changed++;
        }

        return new CanvasRepairOutcome(changed, changed == 0 ? "没有空 GUID 的锁定版本。" : $"已把 {changed} 条空锁定版本改为跟随当前。");
    }

    private static string Describe(Guid id) => id == Guid.Empty ? "空 GUID" : id.ToString();
}
