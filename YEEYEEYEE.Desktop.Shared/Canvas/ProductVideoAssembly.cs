namespace YEEYEEYEE.Desktop;

/// <summary>成片里的一个镜头：第几镜（按分镜顺序）、标题、视频文件。</summary>
public sealed record ProductShot(int Index, string Title, string Path);

/// <summary>这一版成片的镜头清单：按分镜顺序，谁有视频、谁还没有。</summary>
public sealed record ProductVideoPlan(
    string ProductTitle,
    IReadOnlyList<ProductShot> Shots,
    IReadOnlyList<string> MissingTitles,
    string Note)
{
    /// <summary>能不能拼：至少两段、且没有缺的镜头。</summary>
    public bool CanConcat => Shots.Count >= 2 && MissingTitles.Count == 0;
}

/// <summary>
/// 把一版成片的每一镜串起来。
///
/// 这一步**不生成画面**：它只接已经出好的那些镜头视频（<see cref="Mp4Concatenator"/> 做无损拼接）。
/// 之所以要把这句话写在这里，是因为「成片」这个词最容易被理解成「再生成一段」——
/// 那样用户拿到的是一段凭空生成的镜头，而不是他那一版。缺哪一镜就如实说缺哪一镜。
///
/// 范围与顺序都不自己发明：范围用自检的 <see cref="GenerationAudit.Scope"/>（自检说齐了，拼出来就得刚好是那几段），
/// 顺序用泳道布局的 <see cref="CanvasSwimlaneLayout.OrderStoryboards"/>（画布上第 2 镜，拼进成片还在第 2 位）。
/// </summary>
public static class ProductVideoAssembly
{
    public static ProductVideoPlan Plan(WorkflowCanvasState canvas, WorkflowNode product, Func<string, string?>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(product);
        var find = locate ?? AssetStore.Resolve;

        var scope = GenerationAudit.Scope(canvas, product, out _);
        var storyboards = CanvasSwimlaneLayout.OrderStoryboards(
            canvas, scope.Where(node => node.Category == NodeCategory.Storyboard));

        var shots = new List<ProductShot>();
        var missing = new List<string>();
        for (var index = 0; index < storyboards.Count; index++)
        {
            var storyboard = storyboards[index];
            var path = LatestVideo(storyboard, find);
            // 序号按**分镜顺序**算，不按「有视频的第几个」算：缺了第 2 镜时，
            // 第 3 镜仍然是第 3 镜——报出来的位置要和画布上看到的一致。
            if (path.Length == 0) missing.Add($"{index + 1}. {storyboard.Title}");
            else shots.Add(new ProductShot(index + 1, storyboard.Title, path));
        }

        var note = Describe(product, storyboards.Count, shots, missing);
        return new ProductVideoPlan(product.Title, shots, missing, note);
    }

    /// <summary>按分镜顺序串起来。缺镜头、只有一段、认不出的形状都如实拒绝，绝不拿别的东西冒充成片。</summary>
    public static Mp4ConcatResult Concat(
        WorkflowCanvasState canvas, WorkflowNode product, string outputPath, Func<string, string?>? locate = null)
    {
        var plan = Plan(canvas, product, locate);
        if (plan.MissingTitles.Count > 0)
            return new Mp4ConcatResult(false, string.Empty,
                $"还有 {plan.MissingTitles.Count} 镜没出视频（{string.Join("、", plan.MissingTitles.Take(5))}），拼不了。"
                + "串片只接已经出好的段，不会替你生成画面——先把缺的那几镜出了再来。", string.Empty);
        if (plan.Shots.Count == 0)
            return new Mp4ConcatResult(false, string.Empty, "这一版一个镜头视频都没有，拼不了。", string.Empty);
        if (plan.Shots.Count == 1)
            return new Mp4ConcatResult(false, string.Empty, "只有一镜有视频，一段不需要拼。", string.Empty);

        return Mp4Concatenator.Concat(plan.Shots.Select(shot => shot.Path).ToList(), outputPath);
    }

    private static string Describe(
        WorkflowNode product,
        int storyboardCount,
        IReadOnlyList<ProductShot> shots,
        IReadOnlyList<string> missing)
    {
        if (storyboardCount == 0)
            return $"「{product.Title}」这一版还没有挂分镜：先建分镜节点，再把它们连到这一版成品上。";

        var list = string.Join("、", shots.Select(shot => $"第 {shot.Index} 镜「{shot.Title}」"));
        if (missing.Count > 0 && shots.Count == 0)
            return $"「{product.Title}」这一版的 {storyboardCount} 镜都还没出视频，拼不了："
                + $"缺 {string.Join("、", missing.Take(5))}。先在各个分镜节点出视频。";

        var tail = missing.Count == 0
            ? "画面与音频原样搬运，不重新编码。"
            : $"还有 {missing.Count} 镜没出视频（{string.Join("、", missing.Take(5))}），缺的不接、也不替你生成。";

        return $"「{product.Title}」会接 {shots.Count} 段（按分镜顺序：{list}）。{tail}";
    }

    /// <summary>这个节点上最新的那段视频。取「最新」的理由与出图侧一致：节点上可能挂过好几版。</summary>
    private static string LatestVideo(WorkflowNode node, Func<string, string?> locate)
    {
        for (var index = node.Attachments.Count - 1; index >= 0; index--)
        {
            var attachment = node.Attachments[index];
            if (attachment.Kind != AttachmentKind.Video) continue;
            if (locate(attachment.Reference) is { } path) return path;
        }
        return string.Empty;
    }
}
