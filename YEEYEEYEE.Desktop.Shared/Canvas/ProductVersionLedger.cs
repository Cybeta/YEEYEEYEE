namespace YEEYEEYEE.Desktop;

/// <summary>成品的一版成片：成品节点上的一次拼接结果（就是它挂上来的那段视频）。</summary>
public sealed record ProductVersion(
    int Index,
    string Label,
    string FileName,
    string Reference,
    DateTimeOffset AddedAt,
    bool IsLatest,
    IReadOnlyList<string> Shots,
    Mp4FilmInfo Film)
{
    /// <summary>这一版接了哪几镜。没记就直说没记——不按当前分镜去猜（猜出来的「差在哪一镜」比不给更坏）。</summary>
    public string CompositionText => Shots.Count == 0 ? "没记下接了哪几镜" : string.Join("、", Shots);

    public string FactsText => Film.Describe();
}

/// <summary>
/// 「章节 → 成品」的版本对比：把一版成品**历次拼出来的成片**摆在一起，逐项对出差别。
///
/// 为什么要有它：成片每拼一次就会在成品节点上多挂一段视频（文件名是新的），
/// 而在此之前没有任何地方能回答「这一版和上一版差在哪」——只能一段段点开、自己看时长与画面。
///
/// 三条边界，都是刻意的：
/// · **版本只来自同一个成品节点上的成片附件**，不跨节点合并。不同成品节点是**不同的成品**，
///   不是同一版的先后；把它们当成一个序列会得出「第 3 版比第 1 版多两镜」这种自相矛盾的结论。
/// · **只认成片**（<see cref="WorkflowAttachment.SourceFilmJoin"/>），不把「出视频」出来的那几段算进来——
///   那是按提示词生成的一段画面，不是把分镜接起来的成片。
/// · 差别**只报量得出来的事实**（时长 / 帧数 / 分辨率 / 体积 / 接的镜数），不替用户下「哪一版更好」的结论。
/// </summary>
public sealed record ProductVersionLedger(
    string ProductTitle,
    string OwnerTitle,
    Guid? ChapterId,
    string ChapterName,
    IReadOnlyList<ProductVersion> Versions,
    ProductVideoPlan Plan)
{
    public bool HasVersions => Versions.Count > 0;

    /// <summary>最新一版（列表顺序就是挂上来的先后）。</summary>
    public ProductVersion? Latest => Versions.Count == 0 ? null : Versions[^1];

    /// <summary>上一版；只有一版时为 null。</summary>
    public ProductVersion? Previous => Versions.Count < 2 ? null : Versions[^2];

    /// <summary>最新一版与上一版的差别（只有一版时为空）。</summary>
    public IReadOnlyList<string> LatestChanges =>
        Latest is { } latest && Previous is { } previous
            ? ProductVersions.Compare(previous, latest)
            : Array.Empty<string>();

    /// <summary>一行概览，给时间轴那种一格放不下一段话的地方用。</summary>
    public string Headline()
    {
        if (Latest is not { } latest) return $"{ProductTitle} · 还没拼过成片";
        return $"{ProductTitle} · {Versions.Count} 版 · 最新 {latest.FactsText}";
    }
}

/// <summary>算「章节 → 成品」的版本对比。只读画布与文件，不改任何东西。</summary>
public static class ProductVersions
{
    /// <summary>一版成品（一个成品节点）历次拼出来的成片，旧的在前。</summary>
    public static ProductVersionLedger For(WorkflowCanvasState state, WorkflowNode product, Func<string, string?>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(product);
        var find = locate ?? AssetStore.Resolve;

        var films = product.Attachments
            .Where(attachment => attachment.Kind == AttachmentKind.Video
                && attachment.Source == WorkflowAttachment.SourceFilmJoin)
            .ToList();

        var versions = new List<ProductVersion>(films.Count);
        for (var index = 0; index < films.Count; index++)
        {
            var attachment = films[index];
            versions.Add(new ProductVersion(
                index + 1,
                $"第 {index + 1} 版",
                attachment.Name,
                attachment.Reference,
                attachment.AddedAt,
                index == films.Count - 1,
                Shots(attachment.ShotList),
                Probe(find, attachment.Reference)));
        }

        var owner = product.ParentNodeId is { } parentId
            ? state.Nodes.FirstOrDefault(node => node.Id == parentId)
            : null;
        var chapterId = CanvasChapters.ResolveChapterId(state, product);
        var chapter = chapterId is { } id
            ? CanvasChapters.ChapterItems(state).FirstOrDefault(item => item.Id == id)
            : null;

        return new ProductVersionLedger(
            product.Title,
            owner?.Title ?? string.Empty,
            chapterId,
            chapter?.Name ?? string.Empty,
            versions,
            ProductVideoAssembly.Plan(state, product, find));
    }

    /// <summary>
    /// 一章之下的全部成品，各自的历次成片（「章节 → 成品」）。
    /// 顺序跟泳道布局同一份规则：先按所属分镜的阅读顺序，再按坐标与标题——
    /// 时间轴上看到的次序，与画布上从左到右的次序是同一个。
    /// </summary>
    public static IReadOnlyList<ProductVersionLedger> ForChapter(
        WorkflowCanvasState state, Guid chapterId, Func<string, string?>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var find = locate ?? AssetStore.Resolve;

        var products = state.Nodes
            .Where(node => node.Category == NodeCategory.Product)
            .Where(node => CanvasChapters.ResolveChapterId(state, node) == chapterId)
            .ToList();

        var owners = products
            .Select(node => node.ParentNodeId is { } parentId ? state.Nodes.FirstOrDefault(item => item.Id == parentId) : null)
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList();
        var rank = CanvasSwimlaneLayout.OrderStoryboards(state, owners)
            .Select((node, index) => (node.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);

        return products
            .OrderBy(node => node.ParentNodeId is { } parent && rank.TryGetValue(parent, out var value) ? value : int.MaxValue)
            .ThenBy(node => node.X)
            .ThenBy(node => node.Title, StringComparer.Ordinal)
            .Select(node => For(state, node, find))
            .ToList();
    }

    /// <summary>
    /// 两版之间的差别。**基线在前、被比的在后**，句子读起来是「相对基线变了什么」。
    /// 读不出来的那一版如实点名，其余能比的照比——不因为一版坏掉就整段作废。
    /// </summary>
    public static IReadOnlyList<string> Compare(ProductVersion baseline, ProductVersion candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var lines = new List<string>();
        if (!baseline.Film.Readable) lines.Add($"{baseline.Label}读不出来：{baseline.Film.Problem}");
        if (!candidate.Film.Readable) lines.Add($"{candidate.Label}读不出来：{candidate.Film.Problem}");

        if (baseline.Film.Readable && candidate.Film.Readable)
        {
            lines.Add(DescribeDouble("时长", baseline.Film.Seconds, candidate.Film.Seconds, "秒", 2));
            lines.Add(DescribeDouble("帧数", baseline.Film.Frames, candidate.Film.Frames, "帧", 0));
            lines.Add(baseline.Film.Width == candidate.Film.Width && baseline.Film.Height == candidate.Film.Height
                ? $"分辨率没变 {baseline.Film.Width}×{baseline.Film.Height}"
                : $"分辨率 {baseline.Film.Width}×{baseline.Film.Height} → {candidate.Film.Width}×{candidate.Film.Height}");
        }

        if (baseline.Film.Bytes > 0 || candidate.Film.Bytes > 0)
            lines.Add(baseline.Film.Bytes == candidate.Film.Bytes
                ? $"体积没变 {Mp4FilmInfo.Size(candidate.Film.Bytes)}"
                : $"体积 {Mp4FilmInfo.Size(baseline.Film.Bytes)} → {Mp4FilmInfo.Size(candidate.Film.Bytes)}");

        lines.Add(DescribeShots(baseline, candidate));
        lines.Add($"时间：{baseline.Label} {baseline.AddedAt.ToLocalTime():MM-dd HH:mm} → "
            + $"{candidate.Label} {candidate.AddedAt.ToLocalTime():MM-dd HH:mm}");
        return lines;
    }

    private static string DescribeDouble(string what, double baseline, double candidate, string unit, int decimals)
    {
        var difference = candidate - baseline;
        var format = "0." + new string('0', decimals);
        if (Math.Abs(difference) < (decimals == 0 ? 0.5 : 0.005))
            return $"{what}没变（{baseline.ToString(format)} {unit}）";
        var sign = difference > 0 ? "+" : "-";
        return $"{what} {baseline.ToString(format)} {unit} → {candidate.ToString(format)} {unit}"
            + $"（{sign}{Math.Abs(difference).ToString(format)}）";
    }

    private static string DescribeShots(ProductVersion baseline, ProductVersion candidate)
    {
        if (baseline.Shots.Count == 0 || candidate.Shots.Count == 0)
        {
            var missing = baseline.Shots.Count == 0 ? baseline.Label : candidate.Label;
            return $"接的镜数：{missing}没记（拼的时候还没开始记镜头清单），比不了";
        }

        var added = candidate.Shots.Where(shot => !baseline.Shots.Contains(shot)).ToList();
        var removed = baseline.Shots.Where(shot => !candidate.Shots.Contains(shot)).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return $"接的镜头没变（{candidate.Shots.Count} 镜）";

        var detail = new List<string>();
        if (added.Count > 0) detail.Add("新增 " + string.Join("、", added));
        if (removed.Count > 0) detail.Add("去掉 " + string.Join("、", removed));
        return $"接的镜数 {baseline.Shots.Count} → {candidate.Shots.Count}；{string.Join("；", detail)}";
    }

    private static Mp4FilmInfo Probe(Func<string, string?> locate, string reference)
    {
        var path = locate(reference);
        return string.IsNullOrEmpty(path)
            ? Mp4FilmInfo.Unreadable("文件不在资产目录里（可能被删或搬走了）")
            : Mp4Concatenator.Probe(path);
    }

    /// <summary>拼的时候记下来的镜头清单（「、」分隔）。空字符串 = 没记。</summary>
    private static IReadOnlyList<string> Shots(string shotList) =>
        string.IsNullOrWhiteSpace(shotList)
            ? Array.Empty<string>()
            : shotList.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
