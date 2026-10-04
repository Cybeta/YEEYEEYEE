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

/// <summary>
/// 一处「这一次最多能喂几张参考图」的声明。<c>Max</c> 为 <c>null</c> 表示**这一处不设限**——
/// 注意它与 0 不是一回事，见 <see cref="ReferenceCapResolver"/>。
/// </summary>
public sealed record ReferenceCapDeclaration(string Label, int? Max);

/// <summary>算出来的上限，以及**是被哪一处卡住的**（0 或超限时要能说出是谁定的）。</summary>
public sealed record ReferenceCapResult(int Cap, string BindingLabel);

/// <summary>
/// 把各处声明的上限取小（规格里的规则二）。
///
/// 为什么不能只看用户设置那一项：会犯两种相反的错——
///   · 设置 3、这一家只吃 1：多出来的两张被服务端悄悄忽略，用户以为带了设定，其实没带；
///   · 设置 1、这份工作流有 4 个底图入口：白白浪费掉工作流本身的能力（刚刚才把多槽绑定做通）。
///
/// **0 与 null 必须分清**：用户设置里的 0 是「不带参考图」，是一个真实的约束；
/// 而池子没声明张数时是「不知道」，那是 <c>null</c>——它绝不该反过来把用户的上限压成 0。
/// </summary>
public static class ReferenceCapResolver
{
    /// <summary>
    /// 取所有声明的**最小值**；并列时取先出现的那个（顺序本身就是权威顺序：设置 → 池子 → 工作流）。
    /// 传进来的声明都是 <c>null</c>（谁都不设限）时返回 0——没有任何依据就别声称能带参考图。
    /// </summary>
    public static ReferenceCapResult Resolve(IReadOnlyList<ReferenceCapDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var cap = int.MaxValue;
        var binding = string.Empty;
        foreach (var declaration in declarations)
        {
            if (declaration.Max is not { } max) continue;
            if (max < 0) max = 0;
            if (max >= cap) continue;
            cap = max;
            binding = declaration.Label;
        }

        return cap == int.MaxValue ? new ReferenceCapResult(0, string.Empty) : new ReferenceCapResult(cap, binding);
    }
}

/// <summary>
/// 一镜**能**喂的一张候选参考图（还没做上限裁剪）。给界面用：手动指定「这一次用哪几张」之前，
/// 得先让人看得见有哪些候选、按什么顺序。
/// </summary>
public sealed record ReferenceCandidate(string Key, string Label, string Path)
{
    /// <summary>
    /// 引用的稳定标识（实体 + 变体）。手动指定按它点名，而不是按下标——
    /// 下标会随「引用的先后」「某个变体带了几张图」变，点在错的图上不会有任何报错。
    /// </summary>
    public static string KeyOf(NodeReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.EntityId.ToString("N") + ":" + reference.VariantId.ToString("N");
    }
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
    /// 上限由调用方算好传进来（见 <see cref="ReferenceCapResolver"/>：用户声明的、池子声明的、
    /// 工作流槽位数三者取小），这里不碰配置——免得同一个上限有两处真值来源。
    /// <paramref name="capSource"/> 是那个上限**来自哪一处**，只说给人听：上限是 0 或者超限时，
    /// 必须说得出是谁定的，否则会把用户指去改错的地方（明明是这家池子只吃 1 张，却让人去改设置）。
    ///
    /// <paramref name="locatePath"/> 负责把附件解成磁盘上的文件；解不出、或文件不在，
    /// 这张就当作没有（不拿一张不存在的图去骗模型）。
    /// </summary>
    public static ReferenceImagePlan Plan(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        int cap,
        Func<WorkflowAttachment, string?> locatePath,
        string capSource = "",
        IReadOnlyCollection<string>? onlyKeys = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(locatePath);

        var limit = Math.Max(0, cap);
        var ranked = Rank(canvas, node, locatePath);

        // 手动指定：只留点过名的那几张。**先筛再裁**——反过来（先裁再筛）的话，
        // 用户排除掉的那张会白占一个上限名额，本该留下的那张反而被丢掉。
        var handPicked = onlyKeys is not null;
        if (onlyKeys is not null) ranked = ranked.Where(item => onlyKeys.Contains(item.Key)).ToList();

        var kept = ranked.Take(limit).ToList();
        var dropped = ranked.Skip(limit).Select(item => item.Label).ToList();
        return new ReferenceImagePlan(
            kept.Select(item => item.Path).ToList(),
            dropped,
            limit,
            Describe(limit, capSource, kept.Select(item => item.Label).ToList(), dropped, handPicked));
    }

    /// <summary>
    /// 这一镜**能**喂哪几张（按固定顺序排好，不做上限裁剪）。给界面用：手动指定之前先让人看得见候选。
    /// </summary>
    public static IReadOnlyList<ReferenceCandidate> Candidates(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        Func<WorkflowAttachment, string?> locatePath)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(locatePath);
        return Rank(canvas, node, locatePath)
            .Select(item => new ReferenceCandidate(item.Key, item.Label, item.Path))
            .ToList();
    }

    /// <summary>把引用翻成「排好序的候选」：顺序固定 角色 → 道具 → 场景，同类内按引用顺序。</summary>
    private static List<RankedCandidate> Rank(
        WorkflowCanvasState canvas,
        WorkflowNode node,
        Func<WorkflowAttachment, string?> locatePath)
    {
        var ranked = new List<RankedCandidate>();
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

            ranked.Add(new RankedCandidate(rank, ReferenceCandidate.KeyOf(reference), content.Label, path));
        }
        return ranked.OrderBy(item => item.Rank).ToList();
    }

    private sealed record RankedCandidate(int Rank, string Key, string Label, string Path);

    private static string Describe(
        int limit, string capSource, IReadOnlyList<string> kept, IReadOnlyList<string> dropped, bool handPicked)
    {
        // 「卡在谁身上」只在**上限真的起了作用**时才说：三张都带上了还说「来自设置」是废话。
        var because = capSource.Length > 0 ? "（来自" + capSource + "）" : string.Empty;
        var byHand = handPicked ? "按你勾选的那几张：" : string.Empty;

        if (limit == 0)
            return "这一镜的参考图上限是 0" + because + "，所以没有喂参考图："
                + "角色 / 道具 / 场景都只能靠提示词描述，跨镜一致性会掉。";

        if (kept.Count == 0)
            return handPicked
                ? "这一次一张参考图都没勾，按文生图出——想把设定带上就回上一步把需要的勾回来。"
                : "这一镜引用的设定里还没有图，按文生图出——给引用的变体补一张设定图，下一张就能带上。";

        var text = byHand + $"带了 {kept.Count} 张参考图：{string.Join("、", kept)}。";
        if (dropped.Count > 0)
            text += $"上限是 {limit} 张" + because + $"，丢掉了：{string.Join("、", dropped)}——它们这一镜只能靠提示词描述。";
        return text;
    }
}
