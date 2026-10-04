using System.Text.Json.Serialization;

namespace YEEYEEYEE.Desktop;

public enum ContentSource { User, Ai, Api }
public enum VersionDecision { None, KeepHistorical, Adopted }
public enum NodeCategory
{
    General, Character, Scene, Storyboard, Prop, Product,
    StoryPlan,
    StoryOutline,
    Chapter
}
public enum NodeExecutionStatus { Draft, WaitingForUser, Generating, Completed, Failed, NeedsReview }
public enum AttachmentKind { Image, Video, Audio, Other }

public sealed class WorkflowAttachment
{
    public const string SourceComposition = "合成底图";

    /// <summary>「成片」这个产物的来源标记：由本机的无损拼接（<c>ProductVideoAssembly</c>）产生。
    /// 单独给它一个常量，是因为版本对比要能**只认出成片**——成品节点上同样可能挂着「出视频」出来的那几段，
    /// 把两者混在一起当「同一版成片的历次结果」是错的。</summary>
    public const string SourceFilmJoin = "成片 · 无损拼接（不重新编码）";

    public Guid Id { get; set; } = Guid.NewGuid();
    public AttachmentKind Kind { get; set; } = AttachmentKind.Image;
    public string Reference { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 生成这张图（或这段视频）时**实际发出去的提示词**。留着它，下次打开预览就是上次那一份，
    /// 不用重敲——早先提示词是每次现拼的，用户改过的措辞一出图就丢了。
    /// 手放的素材没有提示词，读出来是空字符串（调用方落回按设定现拼）。
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>与 <see cref="Prompt"/> 配套发出去的负面提示词。</summary>
    public string NegativePrompt { get; set; } = string.Empty;

    /// <summary>
    /// 这一版成片**接了哪几镜**（按分镜顺序的标题，原样留着）。
    ///
    /// 只在成片这类产物上有值，别的产物是空。之所以要在拼的时候就记下来：事后从文件里**反推不出**镜头清单
    /// （容器里只有时长与帧数），而版本对比要回答的正是「这一版比上一版多了哪一镜」。
    /// 空有两种含义——老产物没记，或者这一版压根不是拼出来的——两种都如实说「没记」，不按当前分镜去猜。
    /// </summary>
    public string ShotList { get; set; } = string.Empty;

    /// <summary>
    /// 这张产物是**照着哪一版设定**做出来的：引用键（<c>实体Id/变体Id</c>）→ 当时的设定指纹。
    ///
    /// 与 <see cref="Prompt"/> 是两类不同的凭据：那个记「我发出去的是什么话」，
    /// 这个记「我照着哪一版设定画的」。有了它才能回答「我引用的设定后来改了图，这张是不是该重出」——
    /// 没有它，下游节点对被引用内容的变化是完全无感的。
    ///
    /// 空的含义是**没有记录**（老产物、手放的素材），**不是「没有依据」**：
    /// 判定过期时对空的一律不报（宁可漏报也不误报，见 <c>ReferenceStaleness</c>）。
    /// </summary>
    public Dictionary<string, string> SourceFingerprints { get; set; } = new();

    public static AttachmentKind KindOf(string reference)
    {
        var extension = Path.GetExtension(reference).ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" => AttachmentKind.Image,
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => AttachmentKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => AttachmentKind.Audio,
            _ => AttachmentKind.Other
        };
    }

    public static string DisplayName(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => "图片",
        AttachmentKind.Video => "视频",
        AttachmentKind.Audio => "音频",
        _ => "文件"
    };
}

public sealed class GenerationHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Input { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Accepted { get; set; }
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public NodeExecutionStatus Status { get; set; }
    public List<AiNodeProposal> Proposals { get; set; } = new();
}

public sealed class WorkflowNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新节点";
    public NodeCategory Category { get; set; } = NodeCategory.General;
    public ContentSource ContentSource { get; set; } = ContentSource.User;
    public NodeExecutionStatus ExecutionStatus { get; set; } = NodeExecutionStatus.Draft;
    public string Content { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public Guid? ParentNodeId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WorkTreeItemId { get; set; }
    public Guid? GenerationId { get; set; }
    public bool IsCollapsed { get; set; }
    public List<NodeReference> References { get; set; } = new();
    [JsonPropertyName("EntityId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyEntityId { get; set; }
    [JsonPropertyName("VariantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyVariantId { get; set; }
    [JsonPropertyName("VariantVersionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyVariantVersionId { get; set; }
    public bool IsLocked { get; set; }
    public VersionDecision VersionDecision { get; set; } = VersionDecision.None;
    public List<WorkflowAttachment> Attachments { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
    public List<GenerationHistory> GenerationHistory { get; set; } = new();
    public float X { get; set; } = 80;
    public float Y { get; set; } = 80;
    public int InputCount { get; set; } = 1;
    public int OutputCount { get; set; } = 1;
    public bool ManualPosition { get; set; }
    [JsonPropertyName("AssetPaths")]
    public List<string> LegacyAssetPaths { get; set; } = new();
}

public sealed class WorkflowEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceNodeId { get; set; }
    public int SourcePort { get; set; }
    public Guid TargetNodeId { get; set; }
    public int TargetPort { get; set; }
}

public sealed class WorkflowCanvasState
{
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowEdge> Edges { get; set; } = new();
    public List<WorkflowEntity> Entities { get; set; } = new();
    public List<WorkTreeItem> WorkTree { get; set; } = new();

    public WorkflowEntity? FindEntity(Guid id) => Entities.FirstOrDefault(entity => entity.Id == id);
    public WorkflowEntityVariant? FindVariant(Guid id) =>
        Entities.SelectMany(entity => entity.Variants).FirstOrDefault(variant => variant.Id == id);

    public bool ReplaceReferenceVersion(Guid recordId, Guid entityId, Guid variantId, Guid? variantVersionId, out string error)
    {
        error = string.Empty;
        var node = Nodes.FirstOrDefault(item => item.Id == recordId);
        if (node is null) { error = "目标画布节点不存在。"; return false; }
        if (node.IsLocked) { error = "目标节点已锁定，不能替换资源版本。"; return false; }
        var reference = node.References.FirstOrDefault(item => item.EntityId == entityId && item.VariantId == variantId);
        if (reference is null) { error = "目标节点未引用该资源变体。"; return false; }
        var entity = FindEntity(entityId);
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == variantId);
        if (entity is null || variant is null) { error = "资源实体或变体不存在。"; return false; }
        if (variantVersionId is { } versionId && variant.FindVersion(versionId) is null)
        { error = "资源版本不属于指定变体。"; return false; }
        reference.VariantVersionId = variantVersionId;
        return true;
    }

    public List<ReferenceContent> ResolveReferences(WorkflowNode node) =>
        node.References.Select(ResolveReference).Where(item => item is not null).Cast<ReferenceContent>().ToList();

    public List<(NodeReference Reference, ReferenceContent? Content)> ResolveReferencePairs(WorkflowNode node) =>
        node.References.Select(reference => (reference, ResolveReference(reference))).ToList();

    public ReferenceContent? ResolveReferenceContent(NodeReference reference) => ResolveReference(reference);

    private ReferenceContent? ResolveReference(NodeReference reference)
    {
        var entity = reference.EntityId != Guid.Empty ? FindEntity(reference.EntityId) : null;
        if (entity is null && reference.VariantId != Guid.Empty)
            entity = Entities.FirstOrDefault(candidate => candidate.Variants.Any(variant => variant.Id == reference.VariantId));
        if (entity is null || entity.IsProjectMissing) return null;
        var variant = reference.VariantId != Guid.Empty
            ? entity.Variants.FirstOrDefault(candidate => candidate.Id == reference.VariantId)
            : null;
        variant ??= entity.Variants.FirstOrDefault();
        if (variant is null) return null;
        var version = reference.VariantVersionId is { } versionId ? variant.FindVersion(versionId) : null;
        return version is null
            ? new ReferenceContent(entity, variant, null, DescriptionOf(entity, variant), variant.Layout, variant.Attachments)
            {
                VersionMissing = reference.VariantVersionId is not null,
                References = variant.References
            }
            : new ReferenceContent(entity, variant, version, version.Description, version.Layout, version.Attachments)
            { References = version.References };
    }

    /// <summary>
    /// 变体当前内容的**兜底读取**：优先变体描述，变体为空时退回实体核心设定。
    ///
    /// 为什么需要兜底：设定内容有两个落点——<c>WorkflowEntity.Core</c>（实体级核心设定）与
    /// <c>WorkflowEntityVariant.Description</c>（变体级表现）。老数据、以及只写了 Core 的生成动作，
    /// 会让变体描述是空的；只认变体描述的话，画布上的引用卡、左栏、Agent 协作会显示成
    /// 「有这个设定但什么都没写」——用户看到的就是「AI 生成的节点没有内容」。
    /// 优先变体（它才是这个镜头真正用的那一版），空则退回 Core。
    /// </summary>
    private static string DescriptionOf(WorkflowEntity entity, WorkflowEntityVariant variant) =>
        string.IsNullOrWhiteSpace(variant.Description) ? entity.Core : variant.Description;

    public string DescribeReferenceForPrompt(WorkflowNode node)
    {
        var references = ResolveReferences(node);
        if (references.Count == 0) return string.Empty;
        var lines = new List<string>();
        foreach (var reference in references)
        {
            if (reference.VersionMissing)
                lines.Add($"[阻断] 「{reference.Entity.Name}」引用的版本已缺失，当前内容不是锁定的那一版，请先改回跟随最新或重新锁定版本。");
            lines.Add($"{WorkflowEntity.KindName(reference.Entity.Kind)}「{reference.Label}」");
            if (!string.IsNullOrWhiteSpace(reference.Entity.Core)) lines.Add($"核心设定：{reference.Entity.Core.Trim()}");
            // 变体描述为空时会兜底回退到核心设定，此时两行是同一段文字，重复写一遍只是白占上下文。
            if (!string.IsNullOrWhiteSpace(reference.Description)
                && !string.Equals(reference.Description.Trim(), reference.Entity.Core?.Trim(), StringComparison.Ordinal))
                lines.Add($"当前表现：{reference.Description.Trim()}");
            if (reference.Layout is { IsEmpty: false } layout) lines.Add(layout.ToPromptText());
        }
        return string.Join("\n", lines);
    }
}

public sealed record CanvasReferenceActivation(WorkflowNode Node, NodeReference Reference, ReferenceContent? Content);
