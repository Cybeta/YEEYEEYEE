namespace YEEYEEYEE.Desktop;

/// <summary>一条「下游产物是照着旧设定做的」记录。</summary>
/// <param name="EntityId">那条引用的实体。</param>
/// <param name="VariantId">那条引用的变体。</param>
/// <param name="Label">给人看的名字（例：「林晚 · 少年黑衣 · 最新」）。</param>
/// <param name="Broken">引用现在解析不出来了（设定被删 / 版本缺失）。</param>
public sealed record StaleReference(Guid EntityId, Guid VariantId, string Label, bool Broken);

/// <summary>
/// 「我引用的设定后来改了，手上这张图是不是该重出」——这件事的判定。
///
/// 为什么要有：节点上的产物只记得「我出了什么」，不记得「我照着哪一版设定出的」。
/// 于是角色换了装、场景换了图之后，引用它的分镜毫无感觉，用户得自己想起来去重出。
///
/// 判定的口径是**指纹比对**：出图时把每条引用的设定指纹记在那张产物上
/// （<see cref="WorkflowAttachment.SourceFingerprints"/>），之后拿现在的指纹与它比，
/// 不一样就说「这条引用的内容变了」。
///
/// 为什么用「记下来再比对」而不是「改设定时去通知下游」：通知那条路要覆盖**所有**能改设定的入口
/// （引用画廊出图、版本提交、Agent 改设定、以后还可能加别的），漏掉一个就是静默不报——
/// 而静默不报正是这个功能要解决的问题。指纹是自己带在产物上的凭据，比对时现算，
/// 无论内容是被哪条路改的，它都会发现。
///
/// 三条刻意的取舍：
/// 1. **没有记录的产物不报**（老产物、手放的素材）。没有可比的东西，硬报「过期」等于让用户
///    去重出一张可能是对的图。宁可漏报，不误报。
/// 2. **出图之后才新加的引用不报**：那不是「你照着的那一版变了」，而是「这一镜后来多引了一张设定」。
/// 3. **锁定版本的引用不会因为变体内容变化而报警**：锁版本的意思就是「我就要那一版」，
///    指纹取的是版本快照的内容，变体再怎么改它都不变——这是对的，不是漏报。
/// </summary>
public static class ReferenceStaleness
{
    /// <summary>引用在指纹表里的键。与 <see cref="NodeReference"/> 的实体 / 变体一一对应。</summary>
    public static string KeyOf(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}";

    /// <summary>
    /// 一段引用内容的指纹：描述 + 布局 + 附件 + 子引用 + 版本标签。
    /// 与 <see cref="WorkflowEntityVariant.Fingerprint"/> 同一路数（那个记「变体自己变没变」，
    /// 这个记「这次引用生效的内容变没变」——锁定版本时取的是版本快照，两者不是一回事）。
    /// </summary>
    public static string FingerprintOf(ReferenceContent content) => string.Join("|",
        content.Description.Trim(),
        content.Layout?.ToPromptText() ?? string.Empty,
        string.Join(",", content.Attachments.Select(item => item.Reference)),
        string.Join(",", content.References.Select(KeyOf)),
        content.VersionLabel);

    /// <summary>引用解析不出来时的指纹标记。用不可能与真指纹相同的写法，免得被当成「没变」。</summary>
    private const string BrokenMarker = "\u0000broken";

    /// <summary>
    /// 为这个节点现在的引用拍一份指纹快照——**出图时记在产物上**，以后靠它判断过期。
    /// </summary>
    public static Dictionary<string, string> Snapshot(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in node.References)
        {
            var content = canvas.ResolveReferenceContent(reference);
            result[KeyOf(reference)] = content is null ? BrokenMarker : FingerprintOf(content);
        }
        return result;
    }

    /// <summary>
    /// 这个节点上有哪些产物是照着**已经变了**的设定做出来的（按引用去重，顺序跟节点的引用顺序一致）。
    /// </summary>
    public static IReadOnlyList<StaleReference> Of(WorkflowCanvasState canvas, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        // 只有记过依据的产物才参与：没记过的没有可比的东西（见类型注释里的第 1 条取舍）。
        var recorded = node.Attachments.Where(item => item.SourceFingerprints.Count > 0).ToList();
        if (recorded.Count == 0 || node.References.Count == 0) return Array.Empty<StaleReference>();

        var result = new List<StaleReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in node.References)
        {
            var key = KeyOf(reference);
            var content = canvas.ResolveReferenceContent(reference);
            var current = content is null ? BrokenMarker : FingerprintOf(content);

            foreach (var attachment in recorded)
            {
                // 记录里没有这一条引用：说明它是出图**之后**才加的，不算「更新」（第 2 条取舍）。
                if (!attachment.SourceFingerprints.TryGetValue(key, out var was)) continue;
                if (string.Equals(was, current, StringComparison.Ordinal)) continue;
                // 同一条引用被多张产物记着，只报一次。
                if (!seen.Add(key)) break;

                result.Add(new StaleReference(
                    reference.EntityId,
                    reference.VariantId,
                    content?.Label ?? "（引用已失效）",
                    content is null));
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 这个节点有引用、也有产物，但那几件产物**一条依据都没记过**——
    /// 也就是本次更新之前出的图（或手放的素材）。
    ///
    /// 界面据此说明「为什么现在还看不到过期提示」：不说的话，用户会以为这个功能没生效。
    /// 不用「补记」当前指纹来填平它：那等于宣称「它当时用的就是现在这一版」，
    /// 万一设定其实早就变过了，补记之后就永远不报——那是伪造凭据，不是修好。
    /// 重出一次就会自然带上依据。
    /// </summary>
    public static bool LacksBaseline(WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.References.Count == 0) return false;
        var products = node.Attachments
            .Where(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video)
            .ToList();
        return products.Count > 0 && products.All(item => item.SourceFingerprints.Count == 0);
    }

    /// <summary>一句话说清这件事，卡片与菜单共用（避免两处各写一套措辞）。</summary>
    public static string Describe(IReadOnlyList<StaleReference> stale)
    {
        if (stale.Count == 0) return string.Empty;
        var names = string.Join(" · ", stale.Take(2).Select(item => item.Label));
        var more = stale.Count > 2 ? $" 等 {stale.Count} 项" : string.Empty;
        var broken = stale.Any(item => item.Broken);
        return broken
            ? $"引用的设定有变化或已失效：{names}{more}；这一镜建议重出"
            : $"引用的设定已更新：{names}{more}；这一镜建议重出";
    }

    /// <summary>
    /// 把每一条都列出来（给状态栏那种**只有一行**的地方用，所以不带换行）。
    /// 与 <see cref="Describe"/> 的分工：那个是卡片上的一行提示，这个是点了之后要看的明细。
    /// </summary>
    public static string DescribeAll(IReadOnlyList<StaleReference> stale) =>
        stale.Count == 0
            ? string.Empty
            : string.Join("、", stale.Select(item => item.Label + (item.Broken ? "（解析不出来）" : string.Empty)));
}
