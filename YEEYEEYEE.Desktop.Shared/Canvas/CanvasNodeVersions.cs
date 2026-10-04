namespace YEEYEEYEE.Desktop;

/// <summary>
/// 一个节点**锁定了某一版设定**时，那一版相对它所在章节是什么状态。
///
/// 这一层回答的是「这一镜当初照的那版设定，现在还算数吗」——它是「第一集是剑、第二集变刀」
/// 这类漂移唯一的显式信号：设定改了、节点锁的还是旧的，光看图看不出来，只有这里会说「需要确认」。
/// </summary>
public enum VersionStatus
{
    /// <summary>锁的就是该章当前适用的那一版。</summary>
    Current,

    /// <summary>比该章当前适用的版本旧：它照的是上一版设定，要人工确认还用不用。</summary>
    NeedsConfirmation,

    /// <summary>比该章当前适用的版本新：那是后面章节才该出现的版本。</summary>
    Future,

    /// <summary>节点自己声明了「保留历史版本」——是刻意停在旧版，不算需要确认。</summary>
    KeptHistorical,

    /// <summary>节点自己声明了「采纳这一版」，且它确实就是当前适用版本。</summary>
    Adopted
}

/// <summary>一条「锁定版本」的引用在界面上要显示的状态。</summary>
public sealed record NodeVersionStatus(
    Guid EntityId,
    Guid VariantId,
    Guid VersionId,
    string EntityName,
    string VersionLabel,
    int VersionNumber,
    VersionStatus Status)
{
    public string Label => LabelOf(Status);

    /// <summary>旧界面那句「林晚 v2 · 当前适用」的意思，一字不改地留着。</summary>
    public string Describe() => $"{EntityName} {VersionLabel} · {Label}";

    /// <summary>要不要按「得看一眼」显示（旧界面用它决定徽标颜色）。</summary>
    public bool Warn => Status is VersionStatus.NeedsConfirmation or VersionStatus.Future;

    public static string LabelOf(VersionStatus status) => status switch
    {
        VersionStatus.KeptHistorical => "历史保留",
        VersionStatus.Adopted => "已采纳",
        VersionStatus.NeedsConfirmation => "需要确认",
        VersionStatus.Future => "未来版本",
        _ => "当前适用"
    };
}

/// <summary>
/// 一个节点的版本状态汇总：一句完整的话（检查器 / 状态栏用）+ 卡片上那枚短徽标。
/// </summary>
public sealed record NodeVersionSummary(IReadOnlyList<NodeVersionStatus> Statuses, string Text)
{
    /// <summary>没有锁定任何版本——**这是最常见的正常情况**，不要显示成警告。</summary>
    public static readonly NodeVersionSummary None = new(Array.Empty<NodeVersionStatus>(), "版本状态：未引用固定版本");

    public bool HasPinnedVersions => Statuses.Count > 0;

    /// <summary>卡片上那枚短徽标：`V2 · 当前适用`；多条不同的锁定再加 ` +1`。</summary>
    public string Badge
    {
        get
        {
            if (Statuses.Count == 0) return string.Empty;
            var distinct = Statuses.Select(item => (item.VersionNumber, item.Status)).Distinct().ToList();
            var label = $"V{distinct[0].VersionNumber} · {NodeVersionStatus.LabelOf(distinct[0].Status)}";
            return distinct.Count > 1 ? $"{label} +{distinct.Count - 1}" : label;
        }
    }

    /// <summary>徽标要不要按警告色画：只要有一条「需要确认」或「未来版本」就是。</summary>
    public bool Warn => Statuses.Any(item => item.Warn);
}

/// <summary>
/// 「这一版还算不算数」的判定：把节点锁定的版本，与它所在章节当前适用的版本比一比。
///
/// 这几档文案原先长在旧 WinForms 画布控件里（`GetNodeVersionStatusSummary` / `GetVersionStatus`），
/// 随那个控件一起删掉之后就没人算了——节点锁的是哪一版、那一版还对不对，界面上一个字都不说。
/// 现在搬回共享层：桌面端与网页端读的是同一份，也能被用例钉住。
///
/// **章节按稳定 ID 走，顺序取章节身份层那一份规范顺序的位次**（显式 Order → 章节名里的数字 → 出现顺序，
/// 见 <see cref="CanvasChapters.List"/>）。不自己再解析一次「第N章」：两处各解析一次，迟早会对不上，
/// 而那种错只会表现成「这一章的状态整体偏了一档」。
/// </summary>
public static class CanvasNodeVersions
{
    public static NodeVersionSummary Of(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        var statuses = new List<NodeVersionStatus>();
        var seen = new HashSet<(Guid, Guid, Guid)>();
        foreach (var reference in node.References)
        {
            var content = canvas.ResolveReferenceContent(reference);
            if (content?.Version is not { } version) continue;
            if (!seen.Add((content.Entity.Id, content.Variant.Id, version.Id))) continue;

            statuses.Add(new NodeVersionStatus(
                content.Entity.Id,
                content.Variant.Id,
                version.Id,
                content.Entity.Name,
                version.Label,
                version.Number,
                StatusOf(canvas, node, content, version)));
        }

        if (statuses.Count == 0) return NodeVersionSummary.None;
        return new NodeVersionSummary(statuses, "版本状态：" + string.Join("；", statuses.Select(item => item.Describe())));
    }

    /// <summary>
    /// 单条判定的五档，顺序与原实现一致：
    /// 先看节点自己的声明（保留历史 / 采纳），再比章节——比不出章节就按「当前适用」。
    /// </summary>
    public static VersionStatus StatusOf(
        WorkflowCanvasState canvas, WorkflowNode node, ReferenceContent content, EntityVariantVersion version)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(version);

        if (node.VersionDecision == VersionDecision.KeepHistorical) return VersionStatus.KeptHistorical;

        var chapter = ChapterIndex(canvas, node);
        var effective = chapter is { } index ? EffectiveVersion(canvas, content, index) : null;
        if (node.VersionDecision == VersionDecision.Adopted && version.Id == effective?.Id) return VersionStatus.Adopted;

        // 这个节点不在任何章节里：没有「这一章当前适用哪一版」这回事，只能算当前适用。
        if (chapter is null) return VersionStatus.Current;
        if (effective is null || version.Number == effective.Number) return VersionStatus.Current;

        return version.Number < effective.Number ? VersionStatus.NeedsConfirmation : VersionStatus.Future;
    }

    /// <summary>
    /// 某个变体在某一章「当前适用」的那一版：**来源章节不晚于这一章**的版本里，号最大的那一个。
    /// 版本来自哪一章写在它自己的工作树条目上（`Kind == Version` 且三个来源 ID 都对得上），
    /// 所以这一条也走稳定 ID，不按版本号或名字猜。
    /// </summary>
    public static EntityVariantVersion? EffectiveVersion(
        WorkflowCanvasState canvas, ReferenceContent content, int chapterIndex)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(content);

        return content.Variant.Versions
            .Where(version => VersionChapterIndex(canvas, content.Entity, content.Variant, version) is not { } source
                || source <= chapterIndex)
            .OrderByDescending(version => version.Number)
            .FirstOrDefault();
    }

    /// <summary>节点所在章节在规范顺序里的位次；不在任何章节里是 null。</summary>
    public static int? ChapterIndex(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        return CanvasChapters.ResolveChapterId(canvas, node) is { } id ? IndexOfChapter(canvas, id) : null;
    }

    /// <summary>这一版设定是在哪一章立项的（位次）；查不到是 null。</summary>
    public static int? VersionChapterIndex(
        WorkflowCanvasState canvas, WorkflowEntity entity, WorkflowEntityVariant variant, EntityVariantVersion version)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var item = canvas.WorkTree.FirstOrDefault(candidate =>
            candidate.Kind == WorkTreeKind.Version
            && candidate.SourceEntityId == entity.Id
            && candidate.SourceVariantId == variant.Id
            && candidate.SourceVersionId == version.Id);
        if (item is null) return null;

        // 条目挂在哪个章下：先沿父链找章节条目（稳定 ID），找不到才退回条目上写的章节名——
        // **只在唯一同名时认**，同名歧义就当作不知道，不猜。
        if (CanvasChapters.ChapterOfAnchor(canvas, item.Id) is { } chapterId) return IndexOfChapter(canvas, chapterId);
        if (string.IsNullOrWhiteSpace(item.Chapter)) return null;

        var matches = CanvasChapters.List(canvas)
            .Where(chapter => string.Equals(chapter.Name.Trim(), item.Chapter.Trim(), StringComparison.Ordinal))
            .ToList();
        return matches.Count == 1 ? IndexOfChapter(canvas, matches[0].Id) : null;
    }

    private static int? IndexOfChapter(WorkflowCanvasState canvas, Guid chapterId)
    {
        var chapters = CanvasChapters.List(canvas);
        for (var index = 0; index < chapters.Count; index++)
            if (chapters[index].Id == chapterId) return index;
        return null;
    }
}
