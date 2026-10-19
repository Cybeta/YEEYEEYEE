using System.Security.Cryptography;

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
/// 2. 出图之后新增的引用会使已记录依据的产物过期；空引用快照也显式记录。
/// 3. **锁定版本的引用不会因为变体内容变化而报警**：锁版本的意思就是「我就要那一版」，
///    指纹取的是版本快照的内容，变体再怎么改它都不变——这是对的，不是漏报。
///
/// **图片按字节内容入指纹**：新快照是 <see cref="ContentPrefix"/> 开头的 v2 指纹，在描述 / 布局 /
/// 附件引用 / 子引用 / 版本标签之外，还为每张图片记下 SHA-256。这样同一路径被另一份图片覆盖时，
/// 即使大小与修改时间都没变也能发现；内容没变、只改了时间则不会误报。
///
/// 检测时机（刻意不在界面渲染里算哈希）：哈希只在明确检查点计算——出图批次发起时、每张候选
/// 提交前复核、视频前置检查、以及用户主动打开的自检。画布节点卡是高频道渲染的，只比较指纹的
/// 结构部分（<see cref="Of"/> 默认 <c>verifyContent: false</c>），不会读图片字节。
///
/// **旧项目兼容**：v2 之前记下的指纹没有内容基线，不能拿它与 v2 直接比字符串（那会把每个旧产物
/// 都误判成已变化）。这类记录只按结构部分比较：结构没变就保持「未发现变化」，如实承认无法追溯
/// 判断历史替换；不因打开项目而改写引用、清空已有结果或自动生成。重出一次后自然带上 v2 依据。
/// </summary>
public static class ReferenceStaleness
{
    /// <summary>v2 指纹标记。后面依次是「结构部分」与「图片字节部分」，以控制字符分隔。</summary>
    private const string ContentPrefix = "v2\u0001";
    private const char ContentSeparator = '\u0001';

    /// <summary>图片读不到或读取失败时的内容标记：与任何真实哈希都不同，文件没了能如实变成「已变化」。</summary>
    private const string UnreadableMarker = "\u0000unreadable";

    /// <summary>
    /// 解析附件文件路径；默认按可移植引用解析到资产目录。
    /// 没有打开项目（或引用解析失败）时如实当作「读不到」，不能把异常带进过期判定。
    /// </summary>
    private static string? LocateDefault(WorkflowAttachment attachment)
    {
        try { return AssetStore.Resolve(attachment.Reference); }
        catch (Exception error) when (error is InvalidOperationException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>引用在指纹表里的键。与 <see cref="NodeReference"/> 的实体 / 变体一一对应。</summary>
    public static string KeyOf(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}";

    /// <summary>
    /// 一段引用内容的结构指纹：描述 + 布局 + 附件引用 + 子引用 + 版本标签。
    /// 与 <see cref="WorkflowEntityVariant.Fingerprint"/> 同一路数（那个记「变体自己变没变」，
    /// 这个记「这次引用生效的内容变没变」——锁定版本时取的是版本快照，两者不是一回事）。
    ///
    /// 这个**不带图片字节**：它是 v2 之前的老格式，也是新格式里可单独比较的结构部分。
    /// 要能发现同路径换图，用带文件定位的那个重载（或 <see cref="Snapshot"/>）。
    /// </summary>
    public static string FingerprintOf(ReferenceContent content) => string.Join("|",
        content.Description.Trim(),
        content.Layout?.ToPromptText() ?? string.Empty,
        string.Join(",", content.Attachments.Select(item => item.Reference)),
        string.Join(",", content.References.Select(KeyOf)),
        content.VersionLabel);

    /// <summary>
    /// 带图片字节内容的新指纹（v2）。结构部分与 <see cref="FingerprintOf(ReferenceContent)"/> 相同，
    /// 另附每张图片的 SHA-256；<paramref name="locatePath"/> 为空时按资产引用解析到本机文件。
    /// </summary>
    public static string FingerprintOf(ReferenceContent content, Func<WorkflowAttachment, string?>? locatePath) =>
        ContentPrefix + FingerprintOf(content) + ContentSeparator + ContentFingerprintOf(content, locatePath);

    /// <summary>
    /// 记录下来的指纹与当前引用内容是否一致。
    ///
    /// <paramref name="verifyContent"/> 为真时才核对图片字节；旧格式记录没有内容基线，这时只比较结构部分
    /// （不把「查不了」说成「变了」）。结构不同则无论新旧格式都算已变化。
    /// </summary>
    public static bool Matches(string? recorded, ReferenceContent content,
        Func<WorkflowAttachment, string?>? locatePath = null, bool verifyContent = true)
    {
        if (string.IsNullOrEmpty(recorded)) return false;
        var structural = FingerprintOf(content);
        if (!TryReadRecorded(recorded, out var recordedStructural, out var recordedContent))
            return string.Equals(recorded, structural, StringComparison.Ordinal);
        if (!string.Equals(recordedStructural, structural, StringComparison.Ordinal)) return false;
        if (!verifyContent) return true;
        return string.Equals(recordedContent, ContentFingerprintOf(content, locatePath), StringComparison.Ordinal);
    }

    private static bool TryReadRecorded(string recorded, out string structural, out string content)
    {
        structural = recorded;
        content = string.Empty;
        if (!recorded.StartsWith(ContentPrefix, StringComparison.Ordinal)) return false;
        var rest = recorded[ContentPrefix.Length..];
        var separator = rest.LastIndexOf(ContentSeparator);
        if (separator < 0) return false;
        structural = rest[..separator];
        content = rest[(separator + 1)..];
        return true;
    }

    /// <summary>每张图片一项字节哈希；非图片附件不参与（本功能说的是参考图）。</summary>
    private static string ContentFingerprintOf(ReferenceContent content,
        Func<WorkflowAttachment, string?>? locatePath)
    {
        var locate = locatePath ?? LocateDefault;
        return string.Join(",", content.Attachments.Select(attachment =>
        {
            if (attachment.Kind != AttachmentKind.Image) return "-";
            // 网页端上传的媒体可能以 data URL 存在：它在内存里本来就有完整字节，
            // 按文件路径解析不到；直接对解码后的字节取哈希，记录与检查才用同一把尺。
            if (TryHashDataUrl(attachment.Reference) is { } inline) return inline;
            string? path;
            try { path = locate(attachment); }
            catch (Exception error) when (error is InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException) { path = null; }
            return AssetStore.ContentHashOfFile(path) ?? UnreadableMarker;
        }));
    }

    private static string? TryHashDataUrl(string reference)
    {
        if (!reference.StartsWith("data:image/", StringComparison.Ordinal)) return null;
        var separator = reference.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 0) return null;
        try { return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(reference[(separator + 8)..]))); }
        catch (FormatException) { return UnreadableMarker; }
    }

    /// <summary>引用解析不出来时的指纹标记。用不可能与真指纹相同的写法，免得被当成「没变」。</summary>
    private const string BrokenMarker = "\u0000broken";
    private const string BaselineMarker = "\u0000recorded";

    /// <summary>
    /// 两份快照是否仍然一致（用于「提交时的依据 vs 现在的依据」这类复核）。
    /// 与逐字符串比较的区别：旧格式记录没有内容基线，只按结构部分比较，
    /// 不会因为升级到 v2 本身就把在途任务误判成「资产变了」。
    /// </summary>
    public static bool SnapshotMatches(WorkflowCanvasState canvas, WorkflowNode node,
        IReadOnlyDictionary<string, string> recorded,
        Func<WorkflowAttachment, string?>? locatePath = null, bool verifyContent = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(recorded);
        if (recorded.Count == 0) return false;

        var expected = new HashSet<string>(node.References.Select(KeyOf), StringComparer.Ordinal);
        if (recorded.Keys.Any(key => key != BaselineMarker && !expected.Contains(key))) return false;

        foreach (var reference in node.References)
        {
            if (!recorded.TryGetValue(KeyOf(reference), out var was)) return false;
            var content = canvas.ResolveReferenceContent(reference);
            if (content is null)
            {
                if (was != BrokenMarker) return false;
                continue;
            }
            if (!Matches(was, content, locatePath, verifyContent)) return false;
        }
        return true;
    }

    /// <param name="verifyContent">为真时核对图片字节；默认只比结构，供高频路径调用。</param>
    public static IReadOnlyList<StaleReference> OfAttachment(
        WorkflowCanvasState canvas, WorkflowNode node, WorkflowAttachment attachment,
        bool verifyContent = true, Func<WorkflowAttachment, string?>? locatePath = null)
    {
        if (attachment.SourceFingerprints.Count == 0) return Array.Empty<StaleReference>();
        return node.References.Where(reference =>
        {
            if (!attachment.SourceFingerprints.TryGetValue(KeyOf(reference), out var recorded)) return true;
            var content = canvas.ResolveReferenceContent(reference);
            return content is null
                ? recorded != BrokenMarker
                : !Matches(recorded, content, locatePath, verifyContent);
        }).Select(reference =>
        {
            var content = canvas.ResolveReferenceContent(reference);
            return new StaleReference(reference.EntityId, reference.VariantId,
                content?.Label ?? "（引用已失效）", content is null);
        }).ToList();
    }

    /// <summary>
    /// 为这个节点现在的引用拍一份指纹快照——**出图时记在产物上**，以后靠它判断过期。
    /// 新快照带图片字节内容（v2）；<paramref name="locatePath"/> 只在测试或特殊路径校验时传入。
    /// 这是明确检查点，不是界面渲染路径。
    /// </summary>
    public static Dictionary<string, string> Snapshot(WorkflowCanvasState canvas, WorkflowNode node,
        Func<WorkflowAttachment, string?>? locatePath = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);

        var result = new Dictionary<string, string>(StringComparer.Ordinal) { [BaselineMarker] = "1" };
        foreach (var reference in node.References)
        {
            var content = canvas.ResolveReferenceContent(reference);
            result[KeyOf(reference)] = content is null ? BrokenMarker : FingerprintOf(content, locatePath);
        }
        return result;
    }

    /// <summary>
    /// 这个节点上有哪些产物是照着**已经变了**的设定做出来的（按引用去重，顺序跟节点的引用顺序一致）。
    ///
    /// <paramref name="verifyContent"/> 默认假：画布卡片会跟着生成进度高频重画，不能在这里同步读图片。
    /// 自检、生成提交前检查等明确检查点传真，才会把同路径换图也认出来。
    /// </summary>
    public static IReadOnlyList<StaleReference> Of(WorkflowCanvasState canvas, WorkflowNode node,
        bool verifyContent = false, Func<WorkflowAttachment, string?>? locatePath = null)
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

            foreach (var attachment in recorded)
            {
                // 新引用不在生成时的快照中，同样需要重新生成。
                if (attachment.SourceFingerprints.TryGetValue(key, out var was)
                    && (content is null
                        ? was == BrokenMarker
                        : Matches(was, content, locatePath, verifyContent))) continue;
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
