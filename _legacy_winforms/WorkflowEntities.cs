namespace DreamForge.Desktop;

using System.Text.Json;

/// <summary>项目级设定实体的种类。</summary>
public enum EntityKind { Character, Scene, Prop }

/// <summary>
/// 场景布局中元素所处的方位。同时提供「前后左右中」与「东南西北」两套参照系，
/// 因为古风场景常按方位命名（东厢、西跨院），现代场景常按相对位置描述（左侧、前方）。
/// </summary>
public enum SceneDirection
{
    Center,        // 中央
    Front,         // 前
    Back,          // 后
    Left,          // 左
    Right,         // 右
    East,          // 东
    South,         // 南
    West,          // 西
    North,         // 北
    Northeast,     // 东北
    Northwest,     // 西北
    Southeast,     // 东南
    Southwest,     // 西南
    Above,         // 上方
    Below,         // 下方
    Surrounding,   // 外围
    Underground,   // 地下
    Other          // 其他
}

/// <summary>
/// 场景布局中的一个元素：某个方位上有什么，以及该方位的细化说明。
/// 结构化的方位是出图时保证「茅草屋在宗门右侧」这类信息不丢失的关键。
/// </summary>
public sealed class SceneLayoutItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>元素所处方位。</summary>
    public SceneDirection Direction { get; set; } = SceneDirection.Center;

    /// <summary>方位的细化补充，例如「右侧偏后」「山门内东侧」。</summary>
    public string DirectionNote { get; set; } = string.Empty;

    /// <summary>该方位上的元素，例如「茅草屋」「藏经阁」。</summary>
    public string Element { get; set; } = string.Empty;

    /// <summary>补充说明，例如「距大殿约五十步」「石阶三十级」。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>若该元素本身是一个独立实体（例如主角住所），记录其 Id 便于后续建立引用。</summary>
    public Guid? EntityId { get; set; }

    public static SceneDirection[] AllDirections => Enum.GetValues<SceneDirection>();

    public static string DirectionName(SceneDirection direction) => direction switch
    {
        SceneDirection.Center => "中央",
        SceneDirection.Front => "前",
        SceneDirection.Back => "后",
        SceneDirection.Left => "左",
        SceneDirection.Right => "右",
        SceneDirection.East => "东",
        SceneDirection.South => "南",
        SceneDirection.West => "西",
        SceneDirection.North => "北",
        SceneDirection.Northeast => "东北",
        SceneDirection.Northwest => "西北",
        SceneDirection.Southeast => "东南",
        SceneDirection.Southwest => "西南",
        SceneDirection.Above => "上方",
        SceneDirection.Below => "下方",
        SceneDirection.Surrounding => "外围",
        SceneDirection.Underground => "地下",
        _ => "其他"
    };

    /// <summary>方位在提示词里的完整写法，例如「右侧（偏后）」。</summary>
    public string DescribeDirection() =>
        string.IsNullOrWhiteSpace(DirectionNote)
            ? DirectionName(Direction)
            : $"{DirectionName(Direction)}（{DirectionNote.Trim()}）";
}

/// <summary>
/// 场景变体的空间布局：一句整体概述加若干按方位排列的元素。
/// </summary>
public sealed class SceneLayout
{
    /// <summary>整体布局概述，例如「三进院落，背山面水，中轴对齐」。</summary>
    public string Overview { get; set; } = string.Empty;

    public List<SceneLayoutItem> Items { get; set; } = new();

    /// <summary>既没有概述也没有元素时视为空布局。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Overview) && Items.All(item => string.IsNullOrWhiteSpace(item.Element));

    /// <summary>
    /// 拼成可直接用于提示词的自然语言。概述在前，元素按方位逐条列出，
    /// 空元素的行会被跳过，避免提示词里出现「右侧：」这样的残缺描述。
    /// </summary>
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

/// <summary>
/// 项目级设定实体：角色 / 场景 / 道具。实体只承载跨章节不变的核心设定，
/// 具体表现放在变体里（角色的外观分支、场景的状态分支）。
/// </summary>
public sealed class WorkflowEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 是否由**项目级资源库**托管（目标 6）：托管实体的权威内容在 <c>project/entities.json</c>，
    /// 画布文件里只是快照；未迁移的旧数据保持 false，语义与从前一致（画布本地资源）。
    /// </summary>
    public bool ManagedByProject { get; set; }

    /// <summary>
    /// 权威资源缺失的原因（目标 6 / G6-R3，**运行时状态、不落盘**）：托管实体在项目库里已找不到
    /// （库文件被漏拷、被删除等）时由打开流程写入。此时画布里的旧快照**只供恢复参考**，
    /// 不能当有效资源参与解析、预检与出图——否则会拿旧版本照跑。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProjectMissingReason { get; set; }

    /// <summary>该实体当前是否属于「权威资源缺失」状态。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsProjectMissing => ProjectMissingReason is not null;

    public EntityKind Kind { get; set; } = EntityKind.Character;

    public string Name { get; set; } = "新设定";

    /// <summary>其他称呼，便于从章节正文里识别到同一个实体。</summary>
    public string Aliases { get; set; } = string.Empty;

    /// <summary>不变的核心设定：身份、性格、面相基准或地形骨架。</summary>
    public string Core { get; set; } = string.Empty;

    public List<WorkflowEntityVariant> Variants { get; set; } = new();

    public static string KindName(EntityKind kind) => kind switch
    {
        EntityKind.Character => "角色",
        EntityKind.Scene => "场景",
        _ => "道具"
    };

    /// <summary>
    /// 新建变体；场景实体默认带一个空的空间布局，并提交一个初始版本。
    ///
    /// <paramref name="description"/> 用来在**提交初始版本之前**写好内容：Agent 建实体时是有内容的，
    /// 若先提交空描述再补写，会留下「有内容但 v1 快照是空的、还带着未提交改动」的怪状态。
    /// </summary>
    public WorkflowEntityVariant CreateVariant(string name, string description = "")
    {
        var variant = new WorkflowEntityVariant
        {
            Name = name,
            Description = description ?? string.Empty,
            Layout = Kind == EntityKind.Scene ? new SceneLayout() : null
        };
        variant.EnsureInitialVersion();
        Variants.Add(variant);
        return variant;
    }

    /// <summary>从一条引用的实际生效内容创建隔离变体；不会改写原变体或其历史版本。</summary>
    public WorkflowEntityVariant CreateIsolatedVariant(
        ReferenceContent content,
        string name,
        string description,
        IEnumerable<WorkflowAttachment> attachments)
    {
        var variant = new WorkflowEntityVariant
        {
            Name = name,
            Description = description,
            Layout = CloneLayout(content.Layout),
            Attachments = CloneAttachments(attachments),
            References = CloneReferences(content.References)
        };
        variant.EnsureInitialVersion();
        Variants.Add(variant);
        return variant;
    }

    private static SceneLayout? CloneLayout(SceneLayout? layout) =>
        layout is null ? null : JsonSerializer.Deserialize<SceneLayout>(JsonSerializer.Serialize(layout));

    private static List<WorkflowAttachment> CloneAttachments(IEnumerable<WorkflowAttachment> attachments) =>
        JsonSerializer.Deserialize<List<WorkflowAttachment>>(JsonSerializer.Serialize(attachments)) ?? new();

    private static List<NodeReference> CloneReferences(IEnumerable<NodeReference> references) =>
        JsonSerializer.Deserialize<List<NodeReference>>(JsonSerializer.Serialize(references)) ?? new();
}

/// <summary>设定库相关的共用查询。</summary>
public static class EntityAssets
{
    /// <summary>
    /// 枚举设定库里全部附件：变体当前内容的参考图，以及所有历史版本的参考图。
    /// 历史版本的图同样不能丢，否则锁定到旧版本的引用会失去参考图。
    /// </summary>
    public static IEnumerable<WorkflowAttachment> AllAttachments(WorkflowCanvasState state) =>
        state.Entities
            .SelectMany(entity => entity.Variants)
            .SelectMany(variant => variant.Attachments
                .Concat(variant.Versions.SelectMany(version => version.Attachments)));
}

/// <summary>
/// 节点引用的解析结果：实体、变体，以及这次引用实际生效的设定内容。
/// 锁定到版本时内容来自版本快照，未锁定时来自变体的当前内容。
/// </summary>
public sealed record ReferenceContent(
    WorkflowEntity Entity,
    WorkflowEntityVariant Variant,
    EntityVariantVersion? Version,
    string Description,
    SceneLayout? Layout,
    IReadOnlyList<WorkflowAttachment> Attachments)
{
    /// <summary>当前生效内容包含的子引用，例如角色的衣服、道具和配饰。</summary>
    public IReadOnlyList<NodeReference> References { get; init; } = Array.Empty<NodeReference>();

    /// <summary>当前内容是否包含可继续展开的子引用。</summary>
    public bool HasReferences => References.Count > 0;

    /// <summary>是否锁定在某个已提交版本上。</summary>
    public bool IsLocked => Version is not null;

    /// <summary>
    /// 引用了版本但该版本已不存在（目标 4 / 4.2）。此时 <see cref="Version"/> 为 null、内容退回变体当前内容，
    /// 这**只是为了让画面还能显示**，绝不能当成「正常跟随最新」——调用方必须按阻断处理，
    /// 判定请用 <see cref="CanvasReferenceVersions.IsNodeBlocked"/>。
    /// </summary>
    public bool VersionMissing { get; init; }

    /// <summary>版本标签，例如「v3」或「最新」；版本缺失时明确标出。</summary>
    public string VersionLabel => VersionMissing ? "版本缺失" : Version is null ? "最新" : Version.Label;

    /// <summary>整体的引用标签，例如「小明 · 少年黑衣 · v3」。</summary>
    public string Label => $"{Entity.Name} · {Variant.Name} · {VersionLabel}";
}

/// <summary>
/// 节点对设定的一条引用：实体 + 变体 + 可选版本。
/// 一个节点可以有多条引用，对应一个镜头里同时出现多个角色或场景。
/// </summary>
public sealed class NodeReference
{
    public Guid EntityId { get; set; }
    public Guid VariantId { get; set; }

    /// <summary>引用卡片在资源二级画布中的持久化位置；旧数据缺省为自动布局。</summary>
    public float X { get; set; } = -1;
    public float Y { get; set; } = -1;

    /// <summary>留空表示跟随变体当前内容，非空表示锁定到该版本快照。</summary>
    public Guid? VariantVersionId { get; set; }
}

/// <summary>版本之间的一处差异。<see cref="Before"/> 为空表示新增，<see cref="After"/> 为空表示移除。</summary>
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

/// <summary>
/// 版本差异比较：把一个已提交版本与变体的当前内容逐项对比，
/// 用于升级引用或回滚前的确认，避免在不知情的情况下换掉设定。
/// </summary>
public static class VersionDiff
{
    public static List<VersionDifference> CompareToCurrent(EntityVariantVersion version, WorkflowEntityVariant current)
    {
        var differences = new List<VersionDifference>();
        Add(differences, "差异描述", version.Description, current.Description);
        Add(differences, "布局概述", version.Layout?.Overview, current.Layout?.Overview);

        var before = VisibleItems(version.Layout);
        var after = VisibleItems(current.Layout);
        foreach (var item in after)
            if (!before.Any(candidate => SameKey(candidate, item)))
                differences.Add(new VersionDifference($"布局元素（{item.DescribeDirection()}）", null, Describe(item)));
        foreach (var item in before)
            if (!after.Any(candidate => SameKey(candidate, item)))
                differences.Add(new VersionDifference($"布局元素（{item.DescribeDirection()}）", Describe(item), null));
        foreach (var item in after)
        {
            var match = before.FirstOrDefault(candidate => SameKey(candidate, item));
            if (match is null) continue;
            Add(differences, $"元素说明（{item.DescribeDirection()}）", DescribeNote(match), DescribeNote(item));
        }

        var beforeReferences = version.References.Select(ReferenceKey).ToHashSet(StringComparer.Ordinal);
        var afterReferences = current.References.Select(ReferenceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in afterReferences.Where(reference => !beforeReferences.Contains(reference)))
            differences.Add(new VersionDifference("子引用", null, reference));
        foreach (var reference in beforeReferences.Where(reference => !afterReferences.Contains(reference)))
            differences.Add(new VersionDifference("子引用", reference, null));

        var beforeImages = ImageNames(version.Attachments);
        var afterImages = ImageNames(current.Attachments);
        foreach (var name in afterImages.Where(name => !beforeImages.Contains(name)))
            differences.Add(new VersionDifference("参考图", null, name));
        foreach (var name in beforeImages.Where(name => !afterImages.Contains(name)))
            differences.Add(new VersionDifference("参考图", name, null));

        return differences;
    }

    private static void Add(List<VersionDifference> differences, string field, string? before, string? after)
    {
        if (string.Equals(before?.Trim() ?? string.Empty, after?.Trim() ?? string.Empty, StringComparison.Ordinal)) return;
        differences.Add(new VersionDifference(field, before, after));
    }

    private static List<SceneLayoutItem> VisibleItems(SceneLayout? layout) =>
        layout?.Items.Where(item => !string.IsNullOrWhiteSpace(item.Element)).ToList() ?? new List<SceneLayoutItem>();

    private static bool SameKey(SceneLayoutItem left, SceneLayoutItem right) =>
        left.Direction == right.Direction
        && string.Equals(left.Element.Trim(), right.Element.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Describe(SceneLayoutItem item) =>
        string.IsNullOrWhiteSpace(item.Note) ? item.Element.Trim() : $"{item.Element.Trim()}（{item.Note.Trim()}）";

    private static string DescribeNote(SceneLayoutItem item) =>
        string.IsNullOrWhiteSpace(item.Note) ? string.Empty : item.Note.Trim();

    private static string ReferenceKey(NodeReference reference) =>
        $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";

    private static HashSet<string> ImageNames(IEnumerable<WorkflowAttachment> attachments) =>
        attachments
            .Where(attachment => attachment.Kind == AttachmentKind.Image)
            .Select(attachment => string.IsNullOrWhiteSpace(attachment.Name) ? attachment.Reference : attachment.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 实体变体的一个不可变版本。内容一旦提交就不再改动，供引用锁定到确定的设定状态，
/// 这样已完成章节不会因为后续修改而被动变化。
/// </summary>
public sealed class EntityVariantVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>版本号，从 1 起递增。</summary>
    public int Number { get; set; } = 1;

    /// <summary>提交说明，记录这一版改了什么，便于在版本列表里辨认。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>该版本直接继承并替代的上一版本。</summary>
    public Guid? SupersedesVersionId { get; set; }

    public string Description { get; set; } = string.Empty;

    public SceneLayout? Layout { get; set; }

    /// <summary>该版本自己的参考图，不会被后续版本覆盖。</summary>
    public List<WorkflowAttachment> Attachments { get; set; } = new();

    /// <summary>该版本包含的子引用，例如角色对应的衣服、道具或配饰。</summary>
    public List<NodeReference> References { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>版本标签，例如「v3」。</summary>
    public string Label => $"v{Number}";
}

/// <summary>
/// 实体的一个变体：角色的一种外观、场景的一种状态、道具的一种形制。
/// 引用时指向「实体 + 变体」，可以进一步锁定到某个已提交版本。
/// </summary>
public sealed class WorkflowEntityVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "默认";

    /// <summary>当前可编辑的内容，相对实体核心的差异描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>场景变体专用的空间布局；非场景实体为 null。</summary>
    public SceneLayout? Layout { get; set; }

    /// <summary>当前内容的参考图。</summary>
    public List<WorkflowAttachment> Attachments { get; set; } = new();

    /// <summary>当前变体包含的子引用，例如角色的服装、道具和配饰。</summary>
    public List<NodeReference> References { get; set; } = new();

    /// <summary>当前内容对应的版本号；有未提交改动时它仍是最后一次提交的版本号。</summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>已提交的历史版本，只读快照，按版本号升序。</summary>
    public List<EntityVariantVersion> Versions { get; set; } = new();

    /// <summary>最后一次提交时的内容指纹，用于判断是否存在未提交改动。</summary>
    public string CommittedFingerprint { get; set; } = string.Empty;

    /// <summary>当前内容的指纹。</summary>
    public string Fingerprint() => string.Join("|",
        Description.Trim(),
        Layout?.ToPromptText() ?? string.Empty,
        string.Join(",", Attachments.Select(attachment => attachment.Reference)),
        string.Join(",", References.Select(ReferenceKey)));

    /// <summary>是否存在尚未提交的改动。</summary>
    public bool HasUncommittedChanges => !string.Equals(Fingerprint(), CommittedFingerprint, StringComparison.Ordinal);

    /// <summary>当前版本标签，例如「v3」。</summary>
    public string CurrentVersionLabel => $"v{CurrentVersion}";

    /// <summary>
    /// 把当前内容提交为一个新版本，返回新版本号。快照做深拷贝，
    /// 之后继续编辑当前内容不会影响已提交的版本。
    /// </summary>
    public EntityVariantVersion Commit(string note)
    {
        var version = new EntityVariantVersion
        {
            Number = CurrentVersion + 1,
            Note = note.Trim(),
            SupersedesVersionId = Versions.OrderByDescending(item => item.Number).FirstOrDefault()?.Id,
            Description = Description,
            Layout = Clone(Layout),
            Attachments = Clone(Attachments),
            References = Clone(References)
        };
        CurrentVersion = version.Number;
        Versions.Add(version);
        CommittedFingerprint = Fingerprint();
        return version;
    }

    /// <summary>
    /// 保证至少存在一个已提交版本：全新变体或旧画布数据（没有版本历史）首次被引用前调用，
    /// 让「锁定到版本」总有落点。
    /// </summary>
    public EntityVariantVersion EnsureInitialVersion()
    {
        if (Versions.Count > 0) return Versions[0];
        var version = new EntityVariantVersion
        {
            Number = CurrentVersion,
            Note = "初始版本",
            Description = Description,
            Layout = Clone(Layout),
            Attachments = Clone(Attachments),
            References = Clone(References)
        };
        Versions.Add(version);
        CommittedFingerprint = Fingerprint();
        return version;
    }

    public EntityVariantVersion? FindVersion(Guid versionId) =>
        Versions.FirstOrDefault(version => version.Id == versionId);

    /// <summary>创建一份与当前变体内容隔离的草稿变体，用于单条引用的局部编辑。</summary>
    public WorkflowEntityVariant CloneAsVariant(string name)
    {
        var clone = new WorkflowEntityVariant
        {
            Name = name,
            Description = Description,
            Layout = Clone(Layout),
            Attachments = Clone(Attachments),
            References = Clone(References),
            CurrentVersion = CurrentVersion,
            CommittedFingerprint = CommittedFingerprint,
            Versions = Clone(Versions)
        };
        return clone;
    }

    /// <summary>
    /// 把当前内容回滚为指定版本的快照（深拷贝）。回滚不改动版本历史，
    /// 回滚后当前内容会显示为「有未提交改动」，需要时再提交为新版本。
    /// </summary>
    public void RollbackTo(EntityVariantVersion version)
    {
        Description = version.Description;
        Layout = Clone(version.Layout);
            Attachments = Clone(version.Attachments);
            References = Clone(version.References);
    }

    /// <summary>提示词里的完整写法：差异描述 + 场景布局。</summary>
    public string ToPromptText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Description)) parts.Add(Description.Trim());
        if (Layout is { IsEmpty: false }) parts.Add(Layout.ToPromptText());
        return string.Join("\n", parts);
    }

    private static string ReferenceKey(NodeReference reference) =>
        $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";

    private static T? Clone<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));

    private static List<T> Clone<T>(List<T> value) =>
        JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(value)) ?? new List<T>();
}
