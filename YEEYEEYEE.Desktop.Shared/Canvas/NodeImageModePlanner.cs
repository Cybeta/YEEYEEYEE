namespace YEEYEEYEE.Desktop;

/// <summary>节点出图走哪条路：纯文生图，还是拿一张现成的图当底图（图生图）。</summary>
public enum NodeImageApproach { TextToImage, ImageToImage }

/// <summary>
/// 一次出图的取图方式决定。
///
/// **为什么必须显式决定、不能「顺手把引用图都传上」**：这条链路里「传参考图」就等于切图生图——
/// OpenAI 兼容接口会从 <c>/images/generations</c> 切到 <c>/images/edits</c>，
/// ComfyUI 那条会把 Tool 从 text-to-image 换成 image-to-image 并把 denoise 交给执行方。
/// **没有「只做一致性参考、不改构图」的第三个通道**。所以要么拿它当底图，要么干脆不传。
/// </summary>
/// <param name="Approach">文生图 / 图生图。</param>
/// <param name="BaseImageReference">底图的资产引用（<c>asset://…</c>，调用方自己 Resolve 成绝对路径）。</param>
/// <param name="BaseImageLabel">这张底图是什么（给人看的，例如「场景「旧书铺」的基准图」）。</param>
/// <param name="Denoise">图生图的重绘幅度；文生图为 0。</param>
/// <param name="Reason">为什么这么定——这句话会原样显示给用户，所以要能看懂、能据此反驳。</param>
public sealed record NodeImageDecision(
    NodeImageApproach Approach,
    string BaseImageReference,
    string BaseImageLabel,
    double Denoise,
    string Reason)
{
    /// <summary>真的会拿底图去出图（图生图且底图非空）。</summary>
    public bool UsesBaseImage => Approach == NodeImageApproach.ImageToImage && BaseImageReference.Length > 0;

    public string ApproachLabel => Approach == NodeImageApproach.ImageToImage ? "图生图" : "文生图";
}

/// <summary>手上能当底图的一张图：给人看的标签 + 资产引用。</summary>
public sealed record ImageBaseCandidate(string Label, string Reference);

/// <summary>
/// 「这一镜该文生图还是图生图」的判定。
///
/// **判定依据是剧情**：同一场景的连续镜头值得用场景基准图锁住空间；要换空间 / 换机位 / 大动作的镜头
/// 则必须放开构图，否则底图会把人物与机位一起锁在原画幅里。节点自己上一版的成图只在「这一版是改稿」
/// 时才当底图——只是「再出一版」不该悄悄变成描摹上一版。
///
/// **词表是启发式，不是理解**：中文分镜的写法千变万化，关键词必然漏判。所以这里的产出是
/// 「一个有理由的默认值」，界面上必须让人看见理由并**能覆盖**它——不做成不可见的自动行为。
/// </summary>
public static class NodeImageModePlanner
{
    /// <summary>合成底图：分步合成留下的接口，摆在那儿就是给人接着画的，重绘幅度可以大一些。</summary>
    public const double CompositionDenoise = 0.55;

    /// <summary>改这一版：只动要动的地方，别把整张重画。</summary>
    public const double RevisionDenoise = 0.5;

    /// <summary>同场景连续镜头：锁住空间关系，但人物动作与光线仍该能变。</summary>
    public const double SceneDenoise = 0.45;

    /// <summary>「这一版是改稿」的说法。</summary>
    private static readonly string[] RevisionSignals =
    {
        "改成", "改为", "换成", "换一", "换个", "替换", "调整", "修改", "删掉", "去掉", "不要", "加上", "补上",
        "保留", "变成", "重画", "改一改", "细微", "另一个版本"
    };

    /// <summary>「这一镜要换空间 / 换机位 / 大动作」的说法——这些一律放开构图。</summary>
    private static readonly string[] NewCompositionSignals =
    {
        "进入", "来到", "走出", "推开", "冲向", "冲出", "转身", "奔跑", "跑向", "打斗", "跳", "站起", "起身",
        "换场", "另一个", "新的空间", "全景", "远景", "航拍", "鸟瞰", "俯拍", "俯视", "仰拍", "仰视"
    };

    /// <summary>
    /// 按剧情与手上现有的图决定怎么出。四步依次判定，先命中先赢：
    /// ①合成底图 ②改这一版 ③同场景连续性 ④放开构图 / 兜底。
    /// </summary>
    public static NodeImageDecision Decide(WorkflowCanvasState canvas, WorkflowNode node, string? instruction = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        // ① 合成底图优先：它是「分步合成」与「正式出图」之间的接口，摆在那儿就是给人接着画的。
        var composition = node.Attachments.LastOrDefault(item =>
            item.Source == WorkflowAttachment.SourceComposition && IsUsableImage(item));
        if (composition is not null)
            return new NodeImageDecision(NodeImageApproach.ImageToImage, composition.Reference, "本节点的合成底图",
                CompositionDenoise, "这个节点上挂着「合成底图」：它是分步合成留的接口，按它继续画。");

        var text = ((instruction ?? string.Empty) + "\n" + node.Content).Trim();
        var wantsNewComposition = ContainsAny(text, NewCompositionSignals);

        // ② 剧情是「改这一版」：节点已有自己的成图，且这次的说法是修改。
        var revision = ContainsAny(text, RevisionSignals);
        var latest = node.Attachments.LastOrDefault(IsUsableImage);
        if (revision && latest is not null && !wantsNewComposition)
            return new NodeImageDecision(NodeImageApproach.ImageToImage, latest.Reference,
                $"本节点上一版镜头图（{latest.Name}）", RevisionDenoise,
                "这次的剧情是改这一版（出现「改成 / 换成」这类说法）：拿上一版当底图，只改要改的地方。");

        // ③ 引用里有场景图，且这个场景此前已经出过镜头图 —— 同场景连续性，值得锁空间。
        var scene = SceneWithImage(canvas, node);
        if (scene is not null && !wantsNewComposition)
        {
            if (SceneHasRenderedShot(canvas, node, scene.Value.EntityId))
                return new NodeImageDecision(NodeImageApproach.ImageToImage, scene.Value.Reference,
                    $"场景「{scene.Value.Name}」的基准图", SceneDenoise,
                    $"引用里的场景「{scene.Value.Name}」此前已经出过镜头图：同一场景的连续镜头拿它的基准图当底图锁住空间"
                    + "（墙、窗、柜台的相对位置），人物动作与光线仍然可以变。");
            return new NodeImageDecision(NodeImageApproach.TextToImage, string.Empty, string.Empty, 0,
                $"引用里的场景「{scene.Value.Name}」有基准图，但这个场景还没有出过镜头图：第一镜按文字从头画，"
                + "别让场景基准图的构图先把这一镜锁死。");
        }

        // ④ 兜底：要么剧情明确要换空间 / 换机位，要么手上的图不适合当底图。
        if (wantsNewComposition)
            return new NodeImageDecision(NodeImageApproach.TextToImage, string.Empty, string.Empty, 0,
                "这一镜的剧情要新空间 / 新机位（出现「进入 / 全景 / 俯拍」这类说法）：走文生图，免得被底图锁住构图。");
        if (HasReferenceImage(canvas, node))
            return new NodeImageDecision(NodeImageApproach.TextToImage, string.Empty, string.Empty, 0,
                "引用里有图，但没有适合当底图的那一张（角色转面图会把它的版面带进画面，场景图要先有同场景的镜头图）："
                + "走文生图，设定一致性靠提示词里的外观锚点。");
        return new NodeImageDecision(NodeImageApproach.TextToImage, string.Empty, string.Empty, 0,
            "节点与它的引用里都没有现成的图：走文生图。");
    }

    private static bool IsUsableImage(WorkflowAttachment attachment) =>
        attachment.Kind == AttachmentKind.Image && attachment.Reference.Length > 0;

    /// <summary>
    /// 手上能当底图的图（按「更适合手动当底图」排序）：**本节点上一版镜头图**在前，
    /// 然后是**引用里的场景基准图**。给界面「手动指定图生图」用——判定是启发式的，
    /// 人得能推翻它，而不是只能接受。
    /// </summary>
    public static IReadOnlyList<ImageBaseCandidate> BaseCandidates(WorkflowCanvasState? canvas, WorkflowNode node)
    {
        var list = new List<ImageBaseCandidate>();
        var own = node.Attachments.LastOrDefault(IsUsableImage);
        if (own is not null) list.Add(new ImageBaseCandidate($"本节点上一版镜头图（{own.Name}）", own.Reference));
        if (SceneWithImage(canvas, node) is { } scene)
            list.Add(new ImageBaseCandidate($"场景「{scene.Name}」的基准图", scene.Reference));
        return list;
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals) =>
        text.Length > 0 && signals.Any(signal => text.Contains(signal, StringComparison.Ordinal));

    /// <summary>引用里的**场景**设定，且它有能用的图；返回它、图的引用与实体 Id。</summary>
    private static (Guid EntityId, string Name, string Reference)? SceneWithImage(WorkflowCanvasState? canvas, WorkflowNode node)
    {
        if (canvas is null) return null;
        foreach (var reference in node.References)
        {
            var entity = canvas.FindEntity(reference.EntityId);
            if (entity is null || entity.Kind != EntityKind.Scene) continue;
            var variant = entity.Variants.FirstOrDefault(item => item.Id == reference.VariantId)
                ?? entity.Variants.FirstOrDefault();
            var image = variant?.Attachments.LastOrDefault(IsUsableImage);
            if (image is not null) return (entity.Id, entity.Name, image.Reference);
        }
        return null;
    }

    /// <summary>节点与引用里到底有没有图（用来区分「没有图」与「有图但不适合当底图」两种兜底理由）。</summary>
    private static bool HasReferenceImage(WorkflowCanvasState? canvas, WorkflowNode node)
    {
        if (node.Attachments.Any(IsUsableImage)) return true;
        if (canvas is null) return false;
        foreach (var reference in node.References)
        {
            var entity = canvas.FindEntity(reference.EntityId);
            if (entity is null) continue;
            var variant = entity.Variants.FirstOrDefault(item => item.Id == reference.VariantId)
                ?? entity.Variants.FirstOrDefault();
            if (variant?.Attachments.Any(IsUsableImage) == true) return true;
        }
        return false;
    }

    /// <summary>这个场景此前有没有出过图（别的节点引用了同一个场景、并且身上有图）。</summary>
    private static bool SceneHasRenderedShot(WorkflowCanvasState? canvas, WorkflowNode node, Guid sceneEntityId) =>
        canvas is not null && canvas.Nodes.Any(other => other.Id != node.Id
            && other.References.Any(reference => reference.EntityId == sceneEntityId)
            && other.Attachments.Any(IsUsableImage));
}
