using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 模型深拷贝（只做内存往返，从不落盘）。
///
/// 一律用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：默认编码器会转义 base64 里
/// 的 '+' / '/' / '='，碰到内嵌参考图那种几 MB 的长字符串时会为它租整块 char / byte 缓冲，
/// 复制几个实体会留下几百 MB 死对象。放宽转义只影响中间 JSON 的写法，反序列化回来的对象完全一致。
/// </summary>
internal static class ModelCloner
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static T? Clone<T>(T? value) where T : class => value is null
        ? null
        : JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options);

    internal static List<T> CloneList<T>(IEnumerable<T> value) =>
        JsonSerializer.Deserialize<List<T>>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options) ?? new();
}

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
    private static SceneLayout? CloneLayout(SceneLayout? value) => ModelCloner.Clone(value);
    private static List<WorkflowAttachment> CloneAttachments(IEnumerable<WorkflowAttachment> value) => ModelCloner.CloneList(value);
    private static List<NodeReference> CloneReferences(IEnumerable<NodeReference> value) => ModelCloner.CloneList(value);
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

    /// <summary>
    /// 这条引用**这一次**实际要喂的那张参考图（必须是该实体 / 变体下的图片附件）。
    /// 为空表示沿用旧行为：取这一版内容里的第一张可用图片。
    /// 明确选了却找不到或不是图片时，生成会被阻断，不会静默退回别的图。
    /// </summary>
    public Guid? AttachmentId { get; set; }
}

public sealed class EntityVariantVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; } = 1;
    public string Note { get; set; } = string.Empty;
    public Guid? SupersedesVersionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public SceneLayout? Layout { get; set; }
    public VoiceProfile? Voice { get; set; }
    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public List<NodeReference> References { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Label => $"v{Number}";
}

/// <summary>
/// 角色的**声音档案**：跨镜头配音时必须逐字不变的那几个字段。
///
/// 为什么不放在节点上：声音属于角色（更准确地说属于角色的这个变体），
/// 跟外观一样要能版本化。哪一镜换了音色，责任在实体而不在镜头，查起来才有一条线。
/// 空档案（全部字段为空）表示「还没定音色」，句子里出现该角色时要阻断配音，而不是临时挑一个。
/// </summary>
public sealed class VoiceProfile
{
    /// <summary>服务商标识（例如 anyaiapi / openai）；只用于记录，执行时以服务端配置为准。</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>音色 ID（供应商侧的 voice 名或克隆音色 ID）。</summary>
    public string VoiceId { get; set; } = string.Empty;
    /// <summary>配音模型名（例如 tts-1）。</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>语速倍率，1 为原速。</summary>
    public double Speed { get; set; } = 1;
    /// <summary>音高偏移（半音），仅支持该参数的供应商使用。</summary>
    public double? Pitch { get; set; }
    /// <summary>固定种子；供应商支持时用来复现同一条音色表现。</summary>
    public long? Seed { get; set; }
    /// <summary>人工备注：为什么选这个音色、参考样本在哪。</summary>
    public string Note { get; set; } = string.Empty;

    [JsonIgnore] public bool IsEmpty =>
        string.IsNullOrWhiteSpace(VoiceId) && string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Provider);

    /// <summary>跨镜一致性比对键：这四个字段任意一个变了就是换了声音。</summary>
    public string Key => string.Join("|",
        (Provider ?? string.Empty).Trim().ToLowerInvariant(),
        (Model ?? string.Empty).Trim().ToLowerInvariant(),
        (VoiceId ?? string.Empty).Trim(),
        Speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>配音前的最小完备条件：没有音色 ID 或模型，就不允许提交。</summary>
    [JsonIgnore] public bool IsComplete => !string.IsNullOrWhiteSpace(VoiceId) && !string.IsNullOrWhiteSpace(Model);

    public string Describe() => IsEmpty ? "未设置"
        : $"{VoiceId} · {Model}" + (Math.Abs(Speed - 1) > 0.001 ? $" · {Speed:0.##}x" : string.Empty);
}

public sealed class WorkflowEntityVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "默认";
    public string Description { get; set; } = string.Empty;
    public SceneLayout? Layout { get; set; }
    public VoiceProfile? Voice { get; set; }
    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public List<NodeReference> References { get; set; } = new();
    public int CurrentVersion { get; set; } = 1;
    public List<EntityVariantVersion> Versions { get; set; } = new();
    public string CommittedFingerprint { get; set; } = string.Empty;
    public string Fingerprint() => string.Join("|", Description.Trim(), Layout?.ToPromptText() ?? string.Empty, Voice?.Key ?? string.Empty, string.Join(",", Attachments.Select(item => item.Reference)), string.Join(",", References.Select(ReferenceKey)));
    public bool HasUncommittedChanges => !string.Equals(Fingerprint(), CommittedFingerprint, StringComparison.Ordinal);
    public string CurrentVersionLabel => $"v{CurrentVersion}";
    public EntityVariantVersion Commit(string note)
    {
        var version = new EntityVariantVersion
        {
            Number = CurrentVersion + 1, Note = note.Trim(), SupersedesVersionId = Versions.OrderByDescending(item => item.Number).FirstOrDefault()?.Id,
            Description = Description, Layout = Clone(Layout), Voice = Clone(Voice), Attachments = Clone(Attachments), References = Clone(References)
        };
        CurrentVersion = version.Number; Versions.Add(version); CommittedFingerprint = Fingerprint(); return version;
    }
    public EntityVariantVersion EnsureInitialVersion()
    {
        if (Versions.Count > 0) return Versions[0];
        var version = new EntityVariantVersion { Number = CurrentVersion, Note = "初始版本", Description = Description, Layout = Clone(Layout), Voice = Clone(Voice), Attachments = Clone(Attachments), References = Clone(References) };
        Versions.Add(version); CommittedFingerprint = Fingerprint(); return version;
    }
    public EntityVariantVersion? FindVersion(Guid versionId) => Versions.FirstOrDefault(version => version.Id == versionId);
    public WorkflowEntityVariant CloneAsVariant(string name) => new()
    {
        Name = name, Description = Description, Layout = Clone(Layout), Voice = Clone(Voice), Attachments = Clone(Attachments), References = Clone(References),
        CurrentVersion = CurrentVersion, CommittedFingerprint = CommittedFingerprint, Versions = Clone(Versions)
    };
    public void RollbackTo(EntityVariantVersion version)
    { Description = version.Description; Layout = Clone(version.Layout); Voice = Clone(version.Voice); Attachments = Clone(version.Attachments); References = Clone(version.References); }
    public string ToPromptText() => string.Join("\n", new[] { Description.Trim(), Layout is { IsEmpty: false } layout ? layout.ToPromptText() : string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string ReferenceKey(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";
    private static T? Clone<T>(T? value) where T : class => ModelCloner.Clone(value);
    private static List<T> Clone<T>(List<T> value) => ModelCloner.CloneList(value);
}
