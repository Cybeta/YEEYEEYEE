using System.Text.Json;
using System.Text.Json.Serialization;

namespace YEEYEEYEE.Desktop;

public enum EntityKind { Character, Scene, Prop }

public enum SceneDirection
{
    Center, Front, Back, Left, Right, East, South, West, North,
    Northeast, Northwest, Southeast, Southwest, Above, Below, Surrounding, Underground, Other
}

public sealed class SceneLayoutItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SceneDirection Direction { get; set; } = SceneDirection.Center;
    public string DirectionNote { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public static SceneDirection[] AllDirections => Enum.GetValues<SceneDirection>();
    public static string DirectionName(SceneDirection direction) => direction switch
    {
        SceneDirection.Center => "中央", SceneDirection.Front => "前", SceneDirection.Back => "后",
        SceneDirection.Left => "左", SceneDirection.Right => "右", SceneDirection.East => "东",
        SceneDirection.South => "南", SceneDirection.West => "西", SceneDirection.North => "北",
        SceneDirection.Northeast => "东北", SceneDirection.Northwest => "西北",
        SceneDirection.Southeast => "东南", SceneDirection.Southwest => "西南",
        SceneDirection.Above => "上方", SceneDirection.Below => "下方",
        SceneDirection.Surrounding => "外围", SceneDirection.Underground => "地下", _ => "其他"
    };
    public string DescribeDirection() => string.IsNullOrWhiteSpace(DirectionNote)
        ? DirectionName(Direction) : $"{DirectionName(Direction)}（{DirectionNote.Trim()}）";
}

public sealed class SceneLayout
{
    public string Overview { get; set; } = string.Empty;
    public List<SceneLayoutItem> Items { get; set; } = new();
    public bool IsEmpty => string.IsNullOrWhiteSpace(Overview) && Items.All(item => string.IsNullOrWhiteSpace(item.Element));
    public string ToPromptText()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Overview)) lines.Add($"整体布局：{Overview.Trim()}");
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Element)) continue;
            var text = $"{item.DescribeDirection()}：{item.Element.Trim()}";
            if (!string.IsNullOrWhiteSpace(item.Note)) text += $"，{item.Note.Trim()}";
            lines.Add("· " + text);
        }
        return string.Join("\n", lines);
    }
}

public sealed class WorkflowEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool ManagedByProject { get; set; }
    [JsonIgnore] public string? ProjectMissingReason { get; set; }
    [JsonIgnore] public bool IsProjectMissing => ProjectMissingReason is not null;
    public EntityKind Kind { get; set; } = EntityKind.Character;
    public string Name { get; set; } = "新设定";
    public string Aliases { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public List<WorkflowEntityVariant> Variants { get; set; } = new();
    public static string KindName(EntityKind kind) => kind switch
    { EntityKind.Character => "角色", EntityKind.Scene => "场景", _ => "道具" };
    /// <param name="description">用来在**提交初始版本之前**写好内容：Agent 建实体时是有内容的，
    /// 若先提交空描述再补写，会留下「有内容但 v1 快照是空的、还带着未提交改动」的怪状态。</param>
    public WorkflowEntityVariant CreateVariant(string name, string description = "")
    {
        var variant = new WorkflowEntityVariant
        {
            Name = name,
            Description = description ?? string.Empty,
            Layout = Kind == EntityKind.Scene ? new SceneLayout() : null
        };
        variant.EnsureInitialVersion(); Variants.Add(variant); return variant;
    }
    public WorkflowEntityVariant CreateIsolatedVariant(ReferenceContent content, string name, string description, IEnumerable<WorkflowAttachment> attachments)
    {
        var variant = new WorkflowEntityVariant
        {
            Name = name, Description = description, Layout = CloneLayout(content.Layout),
            Attachments = CloneAttachments(attachments), References = CloneReferences(content.References)
        };
        variant.EnsureInitialVersion(); Variants.Add(variant); return variant;
    }
    private static SceneLayout? CloneLayout(SceneLayout? value) => value is null ? null : JsonSerializer.Deserialize<SceneLayout>(JsonSerializer.Serialize(value));
    private static List<WorkflowAttachment> CloneAttachments(IEnumerable<WorkflowAttachment> value) => JsonSerializer.Deserialize<List<WorkflowAttachment>>(JsonSerializer.Serialize(value)) ?? new();
    private static List<NodeReference> CloneReferences(IEnumerable<NodeReference> value) => JsonSerializer.Deserialize<List<NodeReference>>(JsonSerializer.Serialize(value)) ?? new();
}

public static class EntityAssets
{
    /// <summary>
    /// 这张设定卡拿哪一张图当预览：**第一张图片附件**（视频、音频不参与）。
    ///
    /// 只此一份：节点投影的 <c>thumbnailRef</c> 与网页端取缩略图那条接口都问它——
    /// 两处各写一条「第一张图片附件」，迟早会给出两张不同的图，而这种偏差没人会去查。
    /// </summary>
    public static WorkflowAttachment? PreviewImage(ReferenceContent content) =>
        content.Attachments.FirstOrDefault(attachment => attachment.Kind == AttachmentKind.Image);

    public static IEnumerable<WorkflowAttachment> AllAttachments(WorkflowCanvasState state) => state.Entities
        .SelectMany(entity => entity.Variants)
        .SelectMany(variant => variant.Attachments.Concat(variant.Versions.SelectMany(version => version.Attachments)));
}

public sealed record ReferenceContent(
    WorkflowEntity Entity, WorkflowEntityVariant Variant, EntityVariantVersion? Version,
    string Description, SceneLayout? Layout, IReadOnlyList<WorkflowAttachment> Attachments)
{
    public IReadOnlyList<NodeReference> References { get; init; } = Array.Empty<NodeReference>();
    public bool HasReferences => References.Count > 0;
    public bool IsLocked => Version is not null;
    public bool VersionMissing { get; init; }
    public string VersionLabel => VersionMissing ? "版本缺失" : Version is null ? "最新" : Version.Label;
    public string Label => $"{Entity.Name} · {Variant.Name} · {VersionLabel}";
}

public sealed class NodeReference
{
    public Guid EntityId { get; set; }
    public Guid VariantId { get; set; }
    public float X { get; set; } = -1;
    public float Y { get; set; } = -1;
    public Guid? VariantVersionId { get; set; }
}

public sealed record VersionDifference(string Field, string? Before, string? After)
{
    public string Describe() => (Before, After) switch
    {
        (null, { } after) => $"{Field}：新增「{Trim(after)}」",
        ({ } before, null) => $"{Field}：移除「{Trim(before)}」",
        ({ } before, { } after) => $"{Field}：「{Trim(before)}」→「{Trim(after)}」",
        _ => Field
    };
    private static string Trim(string value) => value.Length <= 80 ? value : value[..80] + "…";
}

public static class VersionDiff
{
    public static List<VersionDifference> CompareToCurrent(EntityVariantVersion version, WorkflowEntityVariant current)
    {
        var result = new List<VersionDifference>();
        Add(result, "差异描述", version.Description, current.Description);
        Add(result, "布局概述", version.Layout?.Overview, current.Layout?.Overview);
        var before = VisibleItems(version.Layout); var after = VisibleItems(current.Layout);
        foreach (var item in after.Where(item => !before.Any(candidate => SameKey(candidate, item)))) result.Add(new VersionDifference($"布局元素（{item.DescribeDirection()}）", null, Describe(item)));
        foreach (var item in before.Where(item => !after.Any(candidate => SameKey(candidate, item)))) result.Add(new VersionDifference($"布局元素（{item.DescribeDirection()}）", Describe(item), null));
        foreach (var item in after)
        {
            var match = before.FirstOrDefault(candidate => SameKey(candidate, item));
            if (match is not null) Add(result, $"元素说明（{item.DescribeDirection()}）", DescribeNote(match), DescribeNote(item));
        }
        var beforeReferences = version.References.Select(ReferenceKey).ToHashSet(StringComparer.Ordinal);
        var afterReferences = current.References.Select(ReferenceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var value in afterReferences.Where(value => !beforeReferences.Contains(value))) result.Add(new VersionDifference("子引用", null, value));
        foreach (var value in beforeReferences.Where(value => !afterReferences.Contains(value))) result.Add(new VersionDifference("子引用", value, null));
        var beforeImages = ImageNames(version.Attachments); var afterImages = ImageNames(current.Attachments);
        foreach (var name in afterImages.Where(name => !beforeImages.Contains(name))) result.Add(new VersionDifference("参考图", null, name));
        foreach (var name in beforeImages.Where(name => !afterImages.Contains(name))) result.Add(new VersionDifference("参考图", name, null));
        return result;
    }
    private static void Add(List<VersionDifference> result, string field, string? before, string? after)
    { if (!string.Equals(before?.Trim() ?? string.Empty, after?.Trim() ?? string.Empty, StringComparison.Ordinal)) result.Add(new VersionDifference(field, before, after)); }
    private static List<SceneLayoutItem> VisibleItems(SceneLayout? layout) => layout?.Items.Where(item => !string.IsNullOrWhiteSpace(item.Element)).ToList() ?? new();
    private static bool SameKey(SceneLayoutItem left, SceneLayoutItem right) => left.Direction == right.Direction && string.Equals(left.Element.Trim(), right.Element.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Describe(SceneLayoutItem item) => string.IsNullOrWhiteSpace(item.Note) ? item.Element.Trim() : $"{item.Element.Trim()}（{item.Note.Trim()}）";
    private static string DescribeNote(SceneLayoutItem item) => string.IsNullOrWhiteSpace(item.Note) ? string.Empty : item.Note.Trim();
    private static string ReferenceKey(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";
    private static HashSet<string> ImageNames(IEnumerable<WorkflowAttachment> attachments) => attachments.Where(item => item.Kind == AttachmentKind.Image).Select(item => string.IsNullOrWhiteSpace(item.Name) ? item.Reference : item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class EntityVariantVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; } = 1;
    public string Note { get; set; } = string.Empty;
    public Guid? SupersedesVersionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public SceneLayout? Layout { get; set; }
    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public List<NodeReference> References { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Label => $"v{Number}";
}

public sealed class WorkflowEntityVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "默认";
    public string Description { get; set; } = string.Empty;
    public SceneLayout? Layout { get; set; }
    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public List<NodeReference> References { get; set; } = new();
    public int CurrentVersion { get; set; } = 1;
    public List<EntityVariantVersion> Versions { get; set; } = new();
    public string CommittedFingerprint { get; set; } = string.Empty;
    public string Fingerprint() => string.Join("|", Description.Trim(), Layout?.ToPromptText() ?? string.Empty, string.Join(",", Attachments.Select(item => item.Reference)), string.Join(",", References.Select(ReferenceKey)));
    public bool HasUncommittedChanges => !string.Equals(Fingerprint(), CommittedFingerprint, StringComparison.Ordinal);
    public string CurrentVersionLabel => $"v{CurrentVersion}";
    public EntityVariantVersion Commit(string note)
    {
        var version = new EntityVariantVersion
        {
            Number = CurrentVersion + 1, Note = note.Trim(), SupersedesVersionId = Versions.OrderByDescending(item => item.Number).FirstOrDefault()?.Id,
            Description = Description, Layout = Clone(Layout), Attachments = Clone(Attachments), References = Clone(References)
        };
        CurrentVersion = version.Number; Versions.Add(version); CommittedFingerprint = Fingerprint(); return version;
    }
    public EntityVariantVersion EnsureInitialVersion()
    {
        if (Versions.Count > 0) return Versions[0];
        var version = new EntityVariantVersion { Number = CurrentVersion, Note = "初始版本", Description = Description, Layout = Clone(Layout), Attachments = Clone(Attachments), References = Clone(References) };
        Versions.Add(version); CommittedFingerprint = Fingerprint(); return version;
    }
    public EntityVariantVersion? FindVersion(Guid versionId) => Versions.FirstOrDefault(version => version.Id == versionId);
    public WorkflowEntityVariant CloneAsVariant(string name) => new()
    {
        Name = name, Description = Description, Layout = Clone(Layout), Attachments = Clone(Attachments), References = Clone(References),
        CurrentVersion = CurrentVersion, CommittedFingerprint = CommittedFingerprint, Versions = Clone(Versions)
    };
    public void RollbackTo(EntityVariantVersion version)
    { Description = version.Description; Layout = Clone(version.Layout); Attachments = Clone(version.Attachments); References = Clone(version.References); }
    public string ToPromptText() => string.Join("\n", new[] { Description.Trim(), Layout is { IsEmpty: false } layout ? layout.ToPromptText() : string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string ReferenceKey(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";
    private static T? Clone<T>(T? value) where T : class => value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    private static List<T> Clone<T>(List<T> value) => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(value)) ?? new();
}
