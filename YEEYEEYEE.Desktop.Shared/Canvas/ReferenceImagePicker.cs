namespace YEEYEEYEE.Desktop;

/// <summary>
/// 一镜该喂哪几张参考图——按什么顺序、被丢掉的是谁、上限有没有生效。
///
/// 规则与理由写在 `docs/spec-参考图与设定锁定.md`：参考图取自**变体**（不是节点产物）、
/// 顺序固定、超上限要在动手前说出来。
/// </summary>
public sealed record ReferenceImagePlan(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Dropped,
    int Cap,
    string Note)
{
    public bool UsesReferences => Paths.Count > 0;
}

/// <summary>把「这一镜引用了哪些设定」翻成「该喂哪几张图」。</summary>
public static class ReferenceImagePicker
{
    /// <summary>
    /// 顺序固定为 角色 → 道具 → 场景，同类内按引用顺序。
    ///
    /// 为什么固定：顺序会影响出图结果（模型按顺序理解「这是谁、这是哪儿」），
    /// 所以不能取决于用户加引用的先后——那样同一镜换个加引用次序就换个结果。
    /// </summary>
    private static readonly EntityKind[] KindOrder = { EntityKind.Character, EntityKind.Prop, EntityKind.Scene };

    /// <summary>
    /// 上限由调用方算好传进来（用户声明的与池子声明的取小），这里不碰配置——
    /// 免得同一个上限有两处真值来源。
    ///
    /// <paramref name="locatePath"/> 负责把附件解成磁盘上的文件；解不出、或文件不在，
    /// 这张就当作没有（不拿一张不存在的图去骗模型）。
    /// </summary>
    public static ReferenceImagePlan Plan(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        int cap,
        Func<WorkflowAttachment, string?> locatePath)
    {
        var limit = Math.Max(0, cap);
        var ranked = new List<(int Rank, string Label, string Path)>();

        foreach (var reference in node.References)
        {
            if (canvas.ResolveReferenceContent(reference) is not { } content) continue;
            var rank = Array.IndexOf(KindOrder, content.Entity.Kind);
            if (rank < 0) rank = KindOrder.Length;

            // 变体带多张图时只取**第一张**：一个「角色三视图」否则会一口吃掉三个名额。
            var path = content.Attachments
                .Where(attachment => attachment.Kind == AttachmentKind.Image)
                .Select(locatePath)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (path is null) continue;

            ranked.Add((rank, content.Label, path));
        }

        var kept = ranked.OrderBy(item => item.Rank).Take(limit).ToList();
        var dropped = ranked.OrderBy(item => item.Rank).Skip(limit).Select(item => item.Label).ToList();
        return new ReferenceImagePlan(
            kept.Select(item => item.Path).ToList(),
            dropped,
            limit,
            Describe(limit, kept.Select(item => item.Label).ToList(), dropped));
    }

    private static string Describe(int limit, IReadOnlyList<string> kept, IReadOnlyList<string> dropped)
    {
        if (limit == 0)
            return "这一镜的参考图上限是 0（设置 → 生图生视频 →「图像参考图上限」），所以没有喂参考图："
                + "角色 / 道具 / 场景都只能靠提示词描述，跨镜一致性会掉。";

        if (kept.Count == 0)
            return "这一镜引用的设定里还没有图，按文生图出——给引用的变体补一张设定图，下一张就能带上。";

        var text = $"带了 {kept.Count} 张参考图：{string.Join("、", kept)}。";
        if (dropped.Count > 0)
            text += $"上限是 {limit} 张，丢掉了：{string.Join("、", dropped)}——它们这一镜只能靠提示词描述。";
        return text;
    }
}
