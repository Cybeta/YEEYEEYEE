using System.Text;
using System.Text.Json.Nodes;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>体检发现的一处问题的种类。</summary>
public enum ComfyUiFindingKind
{
    /// <summary>
    /// **我们丢了**：那个必填输入的源头是个活着的后端节点，本该接得上，转换时却整项消失了。
    /// 这是唯一真正要处理的一类（缺的是必填输入，提交会被 ComfyUI 拒收）。
    /// </summary>
    DroppedByConversion,

    /// <summary>源头被静音（mode=2）或被绕过（mode=4）：官方语义就是删掉那一项，正常。</summary>
    MutedOrBypassed,

    /// <summary>原稿自己断线（没接线的 Reroute、没有同名 Set 的 Get）：工作流自身的问题，不是我们的。</summary>
    BrokenInSource,

    /// <summary>
    /// **引用的文件这台机器上没有**（示例素材、模型、依赖）：正文结构一点毛病都没有，所以前面几类
    /// 一个都查不出它来——提交时也**不会被挡下**，但这条链跑到那一步就会失败、产物为空。
    ///
    /// 判据：COMBO 型输入的候选清单**就是服务器当前的文件列表**（`/object_info` 给的），
    /// 值不在清单里就说明那个文件没了。实测这批 316 份里有 129 处、分布在 79 份。
    ///
    /// 真机实测（ComfyUI 0.37 / Linux），**会不会当场被挡下不看文件类型，看有没有产物依赖它**：
    ///   · 有产物依赖（在出片链上）→ 提交当场 400：`LoadImage` 这类报 `custom_validation_failed`
    ///     （点名 `Invalid image file: 33.jpg`，实测 B02）；模型那类只做清单校验，回 200 +
    ///     `node_errors` 里 `value_not_in_list`（实测 T12），那几支拿不到结果，同一份里别的产物照样出。
    ///   · 没有产物依赖它（链外）→ 200 收下（`node_errors` 甚至为空），跑起来也不影响出片
    ///     （实测 A05 的 LoadImage 与 T12 的 4 个缺模型节点）。
    /// 这正是 <see cref="ComfyUiImportFinding.OnExecutionChain"/> 那个事实的用处——
    /// 按同一句话说，用户要么白下载几十 GB，要么以为整条链都废了，两件都是假话。
    /// </summary>
    MissingOnServer,

    /// <summary>在原稿里找不到对应的节点/输入，判断不了（正常情况下应当是 0）。</summary>
    Unclassified
}

/// <summary>
/// <see cref="ComfyUiFindingKind.MissingOnServer"/> 那一处该怎么说。三种成因，说法必须不同：
/// 说成同一句，用户要么以为几十 GB 的模型没装（其实只是名字里的分隔符写法不同），
/// 要么以为我们会替他把模型补上（我们只换素材，不补模型）。
/// </summary>
public enum ComfyUiMissingFileFlavor
{
    /// <summary>不是「缺文件」那一类用的（其余种类都是这个）。</summary>
    None,

    /// <summary>示例素材：出片时我们会把该槽位换成用户的素材，给了就能跑。</summary>
    Sample,

    /// <summary>模型/依赖：不由我们替换，得先把文件补到服务器上。</summary>
    Resource,

    /// <summary>
    /// 文件其实在，只是分隔符写法不同（正文写 `krea\x.safetensors`，服务器上叫 `krea/x.safetensors`）。
    /// 到 ComfyUI 里把这个文件重挑一次就好，**别去重新下载**。
    /// </summary>
    SeparatorMismatch
}

/// <summary>一处发现。带上「哪一份、哪个节点、哪个输入」，因为用户要拿它去核对。</summary>
public sealed record ComfyUiImportFinding(
    ComfyUiFindingKind Kind,
    string WorkflowKey,
    string WorkflowTitle,
    string NodeId,
    string ClassType,
    string Input,
    string Detail)
{
    /// <summary>
    /// 这一处该怎么说话（只在 <see cref="ComfyUiFindingKind.MissingOnServer"/> 那一类上有意义）。
    /// 界面与探针据此分堆展示，**不要去比 Detail 的字符串**。
    /// </summary>
    public ComfyUiMissingFileFlavor MissingFileFlavor { get; init; }

    /// <summary>
    /// 这个节点**撑着几路产物**（提交后有多少个产物出口依赖它，见
    /// <see cref="ComfyUiImportAuditor.DependentOutputs"/>）。**0 就是没人读**，掉什么都无所谓。
    ///
    /// 为什么不是一个「在不在链上」的布尔：真机上出现过「在链上、可整条链照样出片」的情况——
    /// 那是它撑着的那几路产物自己过不了校验（实测 T12：4 个缺模型的节点撑着 4 路产物，
    /// 那 4 路没产出，同一份里别的产物照样出、history 还是 `success`）。说清「撑着几路」，
    /// 用户才知道缺这个文件到底影响什么，而不是被一句话吓到去下载几十 GB。
    /// </summary>
    public int DependentOutputs { get; init; }

    /// <summary>提交之后还会不会被读到（=<see cref="DependentOutputs"/> &gt; 0）。</summary>
    public bool OnExecutionChain => DependentOutputs > 0;

    /// <summary>缺的那个文件叫什么（只在缺文件那一类上有值）。出片前那道「没给满」的检查要用它报名字。</summary>
    public string MissingFileName { get; init; } = string.Empty;

    public string Label => $"{WorkflowTitle} · {ClassType}.{Input}（节点 {NodeId}）";

    /// <summary>
    /// 存进某一份工作流下面时的写法：不带标题（在那儿是重复的），但节点、输入与原因都要写清。
    /// </summary>
    public string ShortLabel => $"{ClassType}.{Input}（节点 {NodeId}）——{Detail}";
}

/// <summary>
/// 导入时的体检账。**只报事实与归因**，不替用户判断该不该修：
/// 「我们丢了」才需要处理；静音/绕过是正常的；原稿自己断线是那份工作流的问题（换一份就行）。
/// </summary>
public sealed record ComfyUiImportAuditReport(
    int ScannedWorkflows,
    int PayloadNodes,
    IReadOnlyList<ComfyUiImportFinding> Findings,
    IReadOnlyList<ComfyUiUnknownType> UnknownTypes,
    int UnreadableFileSlots = 0)
{
    public int Count(ComfyUiFindingKind kind) => Findings.Count(item => item.Kind == kind);

    public bool NeedsAttention => Count(ComfyUiFindingKind.DroppedByConversion) > 0;

    public int AffectedWorkflows => Findings
        .Where(item => item.Kind == ComfyUiFindingKind.DroppedByConversion)
        .Select(item => item.WorkflowKey)
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>给人看的总账。**先把「不用管的」说清**，免得用户对着 100 多条正常项发愁。</summary>
    public string Describe()
    {
        var dropped = Count(ComfyUiFindingKind.DroppedByConversion);
        var lines = new List<string>
        {
            $"扫了 {ScannedWorkflows} 份工作流（正文合计 {PayloadNodes} 个节点）。"
        };

        lines.Add(dropped > 0
            ? $"\u26a0 有 **{dropped} 处**可能是我们转换时丢的（分布在 {AffectedWorkflows} 份里）："
              + "源头是个活着的后端节点，本该接得上。缺的是**必填**输入，所以提交时**会被 ComfyUI 拒收**"
              + "（HTTP 400，它会点名缺哪一处）——那几份现在用不了。"
            : "\u2713 没有发现「我们转换时丢掉的输入」——这几百份的转换是干净的。");

        var muted = Count(ComfyUiFindingKind.MutedOrBypassed);
        if (muted > 0)
            lines.Add($"· 另有 {muted} 处源头被**静音或绕过**：官方语义就是删掉那一项，不用管。");

        var broken = Count(ComfyUiFindingKind.BrokenInSource);
        if (broken > 0)
            lines.Add($"· 还有 {broken} 处是**那份工作流自己断线**（没接线的 Reroute、没有同名 Set 的 Get）："
                + "不是转换的问题，但同样是必填输入缺着，**提交时会被 ComfyUI 拒收**（实测 HTTP 400，点名缺哪一处）"
                + "——要去 ComfyUI 里把线接上，或者换一份。");

        var unknown = Count(ComfyUiFindingKind.Unclassified);
        if (unknown > 0)
            lines.Add($"· 另有 {unknown} 处判断不了（在原稿里找不到对应节点），已如实记下。");

        var missing = Count(ComfyUiFindingKind.MissingOnServer);
        if (missing > 0)
        {
            int Count(ComfyUiMissingFileFlavor flavor) => Findings.Count(item => item.Kind == ComfyUiFindingKind.MissingOnServer
                && item.MissingFileFlavor == flavor);
            var parts = new List<string>();
            if (Count(ComfyUiMissingFileFlavor.Sample) is > 0 and var samples)
                parts.Add(samples + " 处是**示例素材**——出片时我们会把该给的那个槽位换成你的素材，"
                    + "给了就能跑；**没给满**的槽位会留着那个失效的示例");
            if (Count(ComfyUiMissingFileFlavor.SeparatorMismatch) is > 0 and var renamed)
                parts.Add(renamed + " 处**文件其实在、只是名字里的分隔符写法不同**"
                    + "（正文写 `krea\\x`、这台机器上叫 `krea/x`；到 ComfyUI 里重挑一次就好，别去重新下载）");
            if (Count(ComfyUiMissingFileFlavor.Resource) is > 0 and var resources)
                parts.Add(resources + " 处是模型/依赖（不由我们替换——在出片链上的那几处得先把文件补上）");

            var offChain = Findings.Count(item => item.Kind == ComfyUiFindingKind.MissingOnServer
                && !item.OnExecutionChain);
            lines.Add($"· 另有 {missing} 处**引用的文件这台机器上没有**（正文结构一点毛病都没有，所以上面几类"
                + "一个都查不出它来；可只要有产物依赖它，提交时就会被 ComfyUI 挡下）：其中 "
                + string.Join("；其中 ", parts) + "。"
                + (offChain > 0
                    ? $"（其中 {offChain} 处**没有任何产物靠它**，眼下不影响出片。）"
                    : string.Empty));
        }

        if (UnreadableFileSlots > 0)
            lines.Add($"· 另有 {UnreadableFileSlots} 处**固定选项的形状我没读懂**（候选不在我认的那几个位置——"
                + "这台服务器的写法我没见过）：这几处里如果有引用的文件，**我查不了它在不在**。"
                + "别看上面「引用的文件这台机器上没有」是 0 就以为文件都在——那一条在这里是查不了的。");

        if (UnknownTypes.Count > 0)
            lines.Add("· 丢掉的输入，链子上经过这些**我们不认识的前端节点**："
                + string.Join("、", UnknownTypes.Select(item => $"{item.Type}×{item.Count}")));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 把账落到每一份工作流上（导入与重转各调一次）。
    ///
    /// 为什么要落下来、而不是只留在报告里：报告是一次性的，而「这份工作流有 18 处断线」这件事
    /// 在**选择器里**、在**点下去出片之前**都还要看得到——它决定用户要不要换一份。
    /// 先清零再写：这次没有的结论不能留着上一次的数（重转之后问题可能已经没了）。
    /// </summary>
    public void ApplyTo(IEnumerable<SiteWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        // 除了数个数，把**哪几处**也带上：用户要拿着这份清单去核对（重新导入时让大模型认，
        // 或去 ComfyUI 里把那几根线接上）。只说「18 处」，等于把人找活儿全推回去。
        var relevant = Findings
            .Where(item => item.Kind is ComfyUiFindingKind.DroppedByConversion
                or ComfyUiFindingKind.BrokenInSource
                or ComfyUiFindingKind.MissingOnServer)
            .GroupBy(item => item.WorkflowKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var workflow in workflows)
        {
            // 先清零再写：这次没有的结论不能留着上一次的数（重转之后问题可能已经没了）。
            workflow.DroppedInputDetails = new List<string>();
            workflow.BrokenInputDetails = new List<string>();
            workflow.MissingFileDetails = new List<string>();
            workflow.MissingMedia = new List<ComfyUiMissingMedia>();
            if (relevant.TryGetValue(workflow.Key, out var found))
            {
                workflow.DroppedInputDetails = found
                    .Where(item => item.Kind == ComfyUiFindingKind.DroppedByConversion)
                    .Select(item => item.ShortLabel)
                    .ToList();
                workflow.BrokenInputDetails = found
                    .Where(item => item.Kind == ComfyUiFindingKind.BrokenInSource)
                    .Select(item => item.ShortLabel)
                    .ToList();
                workflow.MissingFileDetails = found
                    .Where(item => item.Kind == ComfyUiFindingKind.MissingOnServer)
                    .Select(item => item.ShortLabel)
                    .ToList();
                // 结构化的那一份：只留「我们会替换的素材槽位」且**有产物靠它**的——
                // 出片前那道检查要用它判断「这个槽位我们没给满、它留着的示例又已经不在服务器上了」。
                workflow.MissingMedia = found
                    .Where(item => item.Kind == ComfyUiFindingKind.MissingOnServer
                        && item.MissingFileFlavor == ComfyUiMissingFileFlavor.Sample
                        && item.DependentOutputs > 0)
                    .Select(item => new ComfyUiMissingMedia(item.NodeId, item.ClassType, item.Input, item.MissingFileName, item.DependentOutputs))
                    .ToList();
            }

            // 计数以清单为准：两处写同一个数，就不会出现「说有 3 处、只列了 2 条」。
            workflow.DroppedInputs = workflow.DroppedInputDetails.Count;
            workflow.BrokenInputs = workflow.BrokenInputDetails.Count;
            workflow.MissingFiles = workflow.MissingFileDetails.Count;
        }
    }
}

/// <summary>
/// 一个「我们会替换、可这次没给满」的素材槽位：它自己留着的示例早就不在服务器上了，而它有产物靠它。
/// 出片前拿它把提交挡住——不挡的话就是一次 400（实测 B02 的 `Invalid image file: 33.jpg`）。
/// </summary>
public sealed record ComfyUiMissingMedia(string NodeId, string ClassType, string Input, string FileName, int DependentOutputs)
{
    /// <summary>这是哪一路素材（图 / 视频 / 音频）。按类名认，认不出的当图（`LoadImage` 那一类）。</summary>
    public string Kind => ClassType.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "音频"
        : ClassType.Contains("video", StringComparison.OrdinalIgnoreCase) ? "视频"
        : "图";
}

/// <summary>
/// 一个我们不认识的前端节点类型（<c>/object_info</c> 里没有、内置表里也没有）。
/// 这既是「为什么丢了输入」的解释，也是**可以让大模型认一认**的清单。
/// </summary>
public sealed record ComfyUiUnknownType(string Type, int Count, string SampleWorkflowKey, string SampleWorkflowTitle);

/// <summary>
/// 导入时的体检：拿**这台机器自己的**节点定义（<c>object_info</c>）核每一份正文，
/// 把「必填输入缺了」逐处回原稿定性。
///
/// 为什么必须在导入那一刻做：这时候手上同时有**原稿**（网页格式）、**转换结果**与**节点定义**三样，
/// 判断「这一项是我们丢的，还是原稿本来就没有」只需本地读 JSON，**不发任何请求**。
/// 事后再查就得重新把原稿拉一遍。
///
/// 归因三条（与离线体检同一套判据）：
///   · 源头是活着的后端节点 → <see cref="ComfyUiFindingKind.DroppedByConversion"/>（要处理）；
///   · 源头静音/绕过 → <see cref="ComfyUiFindingKind.MutedOrBypassed"/>（正常）；
///   · 链子在原稿里就断着 → <see cref="ComfyUiFindingKind.BrokenInSource"/>（工作流自身）。
/// </summary>
public static class ComfyUiImportAuditor
{
    /// <summary>缺的是**示例素材**（我们出片时会替换那个槽位——给了就能跑）。</summary>
    internal const string MissingSamplePrefix = "这台机器上没有它引用的素材";

    /// <summary>缺的是**模型/依赖**（不由我们替换——得先把文件补到服务器上）。</summary>
    internal const string MissingResourcePrefix = "这台机器上没有它引用的文件";

    public static ComfyUiImportAuditReport Inspect(
        IReadOnlyDictionary<string, string> rawDrafts,
        IReadOnlyDictionary<string, string> payloads,
        JsonObject objectInfo)
    {
        ArgumentNullException.ThrowIfNull(rawDrafts);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(objectInfo);

        var findings = new List<ComfyUiImportFinding>();
        var unknown = new Dictionary<string, (int Count, string Key, string Title)>(StringComparer.Ordinal);
        var scanned = 0;
        var nodeTotal = 0;
        var unreadable = 0;

        foreach (var pair in rawDrafts)
        {
            var title = Path.GetFileNameWithoutExtension(pair.Key);
            if (!payloads.TryGetValue(pair.Key, out var payload) || string.IsNullOrWhiteSpace(payload)) continue;

            JsonObject raw;
            JsonObject api;
            try
            {
                raw = JsonNode.Parse(pair.Value) as JsonObject ?? new JsonObject();
                api = JsonNode.Parse(payload) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            scanned++;
            nodeTotal += api.Count;

            // 「这个节点撑着几路产物」——整份正文算一次，逐条发现都带上这个事实。
            // 没人读（0 路）的地方缺什么，对出片毫无影响；报给用户就等于让他白忙。
            var dependents = DependentOutputs(api, objectInfo);
            int Depends(string nodeId) => dependents.TryGetValue(nodeId, out var count) ? count : 0;

            var shape = new RawShape(raw, objectInfo);
            foreach (var node in api)
            {
                if (node.Value is not JsonObject body) continue;
                var classType = body["class_type"]?.GetValue<string>() ?? string.Empty;
                if (classType.Length == 0) continue;
                if (objectInfo[classType] is not JsonObject definition) continue;
                if (definition["input"]?["required"] is not JsonObject required) continue;

                var present = new HashSet<string>(StringComparer.Ordinal);
                if (body["inputs"] is JsonObject inputs)
                    foreach (var input in inputs) present.Add(input.Key);

                foreach (var requiredInput in required)
                {
                    if (present.Contains(requiredInput.Key)) continue;
                    // 动态输入（`values.a` 这种）按前缀算「有」——与体检同一套口径。
                    if (present.Any(name => name.StartsWith(requiredInput.Key + ".", StringComparison.Ordinal))) continue;

                    var where = shape.Locate(node.Key, requiredInput.Key);
                    if (where is null)
                    {
                        findings.Add(new ComfyUiImportFinding(ComfyUiFindingKind.Unclassified, pair.Key, title,
                            node.Key, classType, requiredInput.Key, "在原稿里找不到这个节点或这个输入，判断不了")
                        { DependentOutputs = Depends(node.Key) });
                        continue;
                    }

                    var finding = where.Origin is null
                        ? new ComfyUiImportFinding(ComfyUiFindingKind.BrokenInSource, pair.Key, title,
                            node.Key, classType, requiredInput.Key, "原稿里这个输入本来就没接线")
                        : shape.Classify(where.Origin.Value.NodeKey, where.Origin.Value.Slot) with
                        {
                            WorkflowKey = pair.Key,
                            WorkflowTitle = title,
                            NodeId = node.Key,
                            ClassType = classType,
                            Input = requiredInput.Key
                        };
                    finding = finding with { DependentOutputs = Depends(node.Key) };
                    findings.Add(finding);

                    // 链子上的「不认识的前端节点」就是让大模型认的清单。
                    if (finding.Kind == ComfyUiFindingKind.DroppedByConversion && where.Origin is { } origin)
                        foreach (var type in shape.UnknownTypesOnPath(origin.NodeKey))
                            unknown[type] = unknown.TryGetValue(type, out var seen)
                                ? (seen.Count + 1, seen.Key, seen.Title)
                                : (1, pair.Key, title);
                }
            }

            // 正文结构查完了，再查一件正文结构查不出的事：**它引用的文件这台机器上还有没有**。
            // 正文里写着一个不存在的文件名，照样「必填输入都在」——只有拿服务器当前的候选清单去对才看得出。
            var media = MediaInputs(api);
            unreadable += CountUnreadableSlots(api, objectInfo);
            foreach (var node in api)
            {
                if (node.Value is not JsonObject body || body["inputs"] is not JsonObject inputs) continue;
                var classType = body["class_type"]?.GetValue<string>() ?? string.Empty;
                if (classType.Length == 0 || objectInfo[classType] is not JsonObject definition) continue;

                foreach (var field in inputs)
                {
                    if (field.Value is not JsonValue value || !value.TryGetValue<string>(out var current)) continue;
                    if (FileOptions(definition, field.Key) is not { } options) continue;
                    if (options.Contains(current, StringComparer.Ordinal)) continue;

                    var isMedia = media.Contains($"{node.Key}.{field.Key}");
                    if (!isMedia && !SameKindFile(options, current)) continue;

                    var sibling = SeparatorSibling(options, current);
                    var flavor = sibling is not null
                        ? ComfyUiMissingFileFlavor.SeparatorMismatch
                        : isMedia ? ComfyUiMissingFileFlavor.Sample : ComfyUiMissingFileFlavor.Resource;
                    var viaOutputs = Depends(node.Key);
                    var detail = sibling is not null
                        // 「文件其实在，只是分隔符写法不同」这一档要单独说：不这么说，用户会以为自己没装这个模型，
                        // 去下载一个本来就在的几十 GB 文件。实测 T12 的 `krea\krea2_turbo_fp8_scaled.safetensors`
                        // 在服务器上写作 `krea2/krea2_turbo_fp8_scaled.safetensors`（那台是 Linux，反斜杠不当分隔符）。
                        ? $"这台机器上**其实有这个文件**（服务器上写作 {sibling}），只是正文里写的是分隔符不同的"
                          + $" {current}——"
                          + (viaOutputs > 0
                              ? $"提交时 ComfyUI 会把它算成「不在清单里」列进 `node_errors`（`value_not_in_list`），"
                                + $"靠它的 {viaOutputs} 路产物拿不到结果"
                              : "没有任何产物靠它，现在不影响出片")
                          + "；到 ComfyUI 里把这个文件重挑一次就好"
                        : isMedia
                            ? $"{MissingSamplePrefix}（{current}）——出片时我们会把**该给的那个槽位**换成你的素材，"
                              + "所以给了就能跑；"
                              + (viaOutputs > 0
                                  // 实测 B02：链上那个 LoadImage 的文件没了，提交回 400
                                  // `custom_validation_failed`（点名 `Invalid image file: 33.jpg`），连队列都进不去。
                                  ? "**没给满**的槽位会留着这个失效的示例，提交时 ComfyUI 会当场挡下"
                                    + "（实测回 400 `custom_validation_failed`、点名这个文件名）"
                                  : "而这个节点没有任何产物靠它，留着这个失效的示例也不影响出片")
                            : $"{MissingResourcePrefix}（{current}）——这类不由我们替换（模型、依赖一类）："
                              + (viaOutputs > 0
                                  // 实测 T12：这类值只做清单校验，不会当场挡下——回 200 + `node_errors`。
                                  // 靠它的那几路产物拿不到结果，同一份里别的产物照样出（history 里是 `success`）。
                                  ? $"提交时会被列进 `node_errors`（`value_not_in_list`），靠它的 {viaOutputs} 路产物"
                                    + "拿不到结果（同一份里别的产物照样出）；要真跑得先把文件补上"
                                  : "这个节点没有任何产物靠它（实测这类节点没被跑到时整条链照样出片），"
                                    + "现在不影响出片，先别急着补文件");

                    findings.Add(new ComfyUiImportFinding(ComfyUiFindingKind.MissingOnServer, pair.Key, title,
                        node.Key, classType, field.Key, detail)
                    { MissingFileFlavor = flavor, DependentOutputs = viaOutputs, MissingFileName = current });
                }
            }
        }

        var types = unknown
            .Select(item => new ComfyUiUnknownType(item.Key, item.Value.Count, item.Value.Key, item.Value.Title))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Type, StringComparer.Ordinal)
            .ToList();

        return new ComfyUiImportAuditReport(scanned, nodeTotal, findings, types, unreadable);
    }

    /// <summary>原稿里那一处输入连到哪儿了。</summary>
    private readonly record struct RawSlot(string NodeKey, int Slot);

    /// <summary>
    /// 这份正文里，**声明了是固定选项（COMBO）可我们读不懂它候选形状**的输入有几处。
    ///
    /// 为什么非要说出来：读不懂就一处都查不出来，而「一处都没报」会被读成「文件都在」——
    /// 静默失效比报错更坏。实测这台服务器上就有别的写法（候选不在我们认的那两个位置、
    /// 元素还是对象），`FileOptions` 会直接返回 null，于是 `MissingOnServer` 一处不报。
    /// 换机器/换 ComfyUI 版本时这条最该先说话：它让「查不了」和「没问题」分得开。
    /// </summary>
    private static int CountUnreadableSlots(JsonObject api, JsonObject objectInfo)
    {
        var count = 0;
        foreach (var node in api)
        {
            if (node.Value is not JsonObject body) continue;
            var type = body["class_type"]?.GetValue<string>() ?? string.Empty;
            if (type.Length == 0 || objectInfo[type] is not JsonObject definition) continue;
            foreach (var group in new[] { "required", "optional" })
            {
                if (definition["input"]?[group] is not JsonObject spec) continue;
                foreach (var field in spec)
                {
                    if (field.Value is not JsonArray slot || slot.Count == 0) continue;
                    if (ComboOptions(definition, field.Key, out var fixedOptions) is not null) continue;
                    // 只数「本来就是固定选项、可我读不出候选」的那些；枚举读得出来，只是不是文件，不算。
                    if (fixedOptions) count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 把一个固定选项（COMBO）输入的候选清单读成字符串；**读不出来就返回 null**。
    /// <paramref name="fixedOptions"/> 说明「这本来就是一条固定选项输入」——
    /// 与「读懂了、但它不是文件」分开，后者不算「读不懂」（不然会把 sampler_name 那种枚举全算进来）。
    /// </summary>
    private static List<string>? ComboOptions(JsonObject definition, string name, out bool fixedOptions)
    {
        fixedOptions = false;
        foreach (var group in new[] { "required", "optional" })
        {
            if (definition["input"]?[group] is not JsonObject spec) continue;
            if (spec[name] is not JsonArray slot || slot.Count == 0) continue;

            JsonArray? options = null;
            if (slot[0] is JsonValue declared)
            {
                // 用 `IndexOf` 而不是 `StartsWith`：这台服务器上见过 `COMFY_DYNAMICCOMBO_V3` 这种写法，
                // 它也是固定选项，只是候选不在我们认的那个位置（按 `StartsWith` 会整条跳过、一声不吭）。
                fixedOptions = declared.ToString().IndexOf("COMBO", StringComparison.Ordinal) >= 0;
                if (fixedOptions) options = slot.Count > 1 ? slot[1]?["options"] as JsonArray : null;
            }
            else if (slot[0] is JsonArray literal)
            {
                fixedOptions = true;   // 老写法：候选直接写在第一位
                options = literal;
            }

            if (options is null || options.Count == 0) return null;

            var names = new List<string>(options.Count);
            foreach (var item in options)
            {
                if (item is JsonValue value)
                {
                    // 字符串、数字都算读得懂（数字型的候选项肯定不是文件，`FileOptions` 那道「像不像文件名」会滤掉）。
                    names.Add(value.ToString());
                    continue;
                }
                // 新写法：候选项是**对象**、真正的值在 `key` 上（`COMFY_DYNAMICCOMBO_V3` 就是这种，
                // 实测这台服务器上有 283 处）。读得出 `key` 就算读懂了——不放进「读不懂」那一堆，
                // 否则那 276 处会盖住真正读不懂的那几处（多数是 `resize_type` 这种模式选择，不是文件）。
                if (item is JsonObject shape
                    && shape["key"] is JsonValue field
                    && field.TryGetValue<string>(out var keyName)
                    && keyName.Length > 0)
                {
                    names.Add(keyName);
                    continue;
                }
                return null;
            }
            return names;
        }
        return null;
    }

    /// <summary>
    /// 这个输入是不是「从服务器文件里挑一个」的那种：COMBO，且候选清单本身就是一堆带扩展名的文件名。
    /// 是就返回候选清单，不是就返回 null。
    ///
    /// 只看「像不像文件名」是有意的：模型（ckpt / vae / lora / gguf）、图片、视频、音频都算得进来，
    /// 而 enable / disable 那种枚举型选项不算——把枚举也算进来，「值不在清单里」立刻变成噪音。
    /// </summary>
    private static List<string>? FileOptions(JsonObject definition, string name)
    {
        if (ComboOptions(definition, name, out _) is not { Count: > 0 } options) return null;

        var sample = options.Take(50).ToList();
        var fileish = sample.Count(item => Extension(item).Length > 0);
        if (fileish < sample.Count * 3 / 4) return null;

        return options;
    }

    /// <summary>
    /// 值自称是「**这一类的文件**」：扩展名在这份候选清单里出现过。
    ///
    /// 护栏是实测算出来的，少了它误报会很显眼：`local_model = randomize`（哨兵值，没有点）、
    /// `prompt_optimizer_provider = 阿里云/qwen3.7-plus`（provider 名，点后面带 `-`）都会被算成「文件没了」——
    /// 它们的候选清单里确实有文件，但值不是文件。
    ///
    /// 故意要求「清单里出现过这种扩展名」，而不是只查「点后面像不像扩展名」：
    /// 后者挡不住 `openai/gpt-4o` 这种带点又全字母数字的值，而前者问的是更要紧的那句
    /// 「你这台机器上的这一类文件，还有没有这个」。
    /// 代价是一个已知的盲点：**这台机器上某类文件一个都没有**时（比如一个 .wav 都没有，
    /// 而原稿引用的 .wav 已经删了），这一处查不出来——它会退化成出片时在这一步失败，仍然发现得了。
    ///
    /// 素材槽位（<paramref name="media"/>）**不走这道护栏**：出片时我们会把该槽位换成用户的素材，
    /// 而 API 格式里这类输入只有「文件名」一种写法，值按定义就是文件——再查它像不像文件只会漏。
    /// 实测漏过两个：值结尾被前端缀了标注（`xxx.png [input]`），点后面是 `png [input]`，
    /// 怎么看都不像扩展名，可它确实是一个（已经不在服务器上的）素材。
    /// </summary>
    private static bool SameKindFile(IReadOnlyList<string> options, string current)
    {
        var extension = Extension(current);
        return extension.Length > 0
            && options.Any(item => string.Equals(Extension(item), extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 每一路产物出口各靠哪些节点：<c>节点 id → 有几路产物依赖它</c>（0 就是没人读）。
    ///
    /// 出口必须是节点定义里标了 <c>output_node</c> 的那些，**不能拿「没有下游」凑**：
    /// 悬空的死节点也没有下游，会被当成出口，连着它上游一起算活——那这个判据就永远报
    /// 「全是活的」（实测踩过）。
    ///
    /// 数出来、而不是只给一个「在不在链上」的布尔：真机上出现过「在链上、可整条链照样出片」
    /// 的情况（T12 那 4 个缺模型的节点撑着 4 路产物，而那 4 路自己过不了校验、别的产物照样出）。
    /// 说清「撑着几路」，用户才知道缺这个文件到底影响什么。
    ///
    /// 探针与产品共用这一份（早先探针自己写了一份，两套判据各说各话吃过亏）。
    /// </summary>
    public static Dictionary<string, int> DependentOutputs(JsonObject payload, JsonObject objectInfo)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(objectInfo);

        var incoming = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in payload)
        {
            if (pair.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (var slot in inputs)
            {
                if (slot.Value is not JsonArray edge || edge.Count == 0) continue;
                var source = edge[0]?.ToString();
                if (string.IsNullOrEmpty(source)) continue;
                if (!incoming.TryGetValue(pair.Key, out var list)) incoming[pair.Key] = list = new List<string>();
                list.Add(source!);
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in payload)
        {
            if (pair.Value is not JsonObject node) continue;
            var type = node["class_type"]?.GetValue<string>() ?? string.Empty;
            if (objectInfo[type]?["output_node"]?.GetValue<bool>() != true) continue;

            // 这一路产物往上游走一遍，走到的每个节点都记一笔（每一路只记一次）。
            var seen = new HashSet<string>(StringComparer.Ordinal) { pair.Key };
            var queue = new Queue<string>();
            queue.Enqueue(pair.Key);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                counts[current] = counts.TryGetValue(current, out var already) ? already + 1 : 1;
                if (!incoming.TryGetValue(current, out var sources)) continue;
                foreach (var source in sources)
                    if (seen.Add(source)) queue.Enqueue(source);
            }
        }
        return counts;
    }

    /// <summary>提交之后还会被读到的节点（<see cref="DependentOutputs"/> 里计数大于 0 的那些）。</summary>
    public static HashSet<string> ExecutedNodes(JsonObject payload, JsonObject objectInfo) =>
        DependentOutputs(payload, objectInfo)
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 候选清单里有没有「**同一个文件、只是分隔符写法不同**」的那一个：正文写 `krea\x.safetensors`、
    /// 服务器上叫 `krea/x.safetensors`（Windows 上取的工作流丢到 Linux 服务器上跑就是这种）。
    /// 有就把服务器上那个写法返回去——这一档要说「文件其实在，重挑一次就好」，不能让人去重新下载。
    /// </summary>
    private static string? SeparatorSibling(IReadOnlyList<string> options, string current)
    {
        var normalized = current.Replace('\\', '/');
        foreach (var item in options)
            if (string.Equals(item.Replace('\\', '/'), normalized, StringComparison.Ordinal))
                return item;
        return null;
    }

    /// <summary>
    /// 取扩展名（不含点）。2~12 个字符且**全是字母数字**才算：
    /// `safetensors`/`gguf`/`png`/`WAV` 都算，`qwen3.7-plus`（点后面带 `-`）、`randomize`（没有点）不算。
    /// 长度下限 2 是为了不把 `qwen3.8` 这种带点的版本号尾巴（`8`）当成扩展名。
    /// </summary>
    private static string Extension(string text)
    {
        var dot = text.LastIndexOf('.');
        if (dot <= 0) return string.Empty;
        var extension = text[(dot + 1)..];
        if (extension.Length is < 2 or > 12) return string.Empty;
        foreach (var character in extension)
            if (!char.IsLetterOrDigit(character)) return string.Empty;
        return extension;
    }

    /// <summary>
    /// 这份正文里「我们出片时会喂素材」的那些输入（写成「节点.输入」）。用来把两类问题分开说：
    /// 落在这些输入上的是**示例素材**（给了就能跑），落在别处的是**模型/依赖**（得先补文件）。
    /// </summary>
    private static HashSet<string> MediaInputs(JsonObject api)
    {
        var slots = ComfyUiWorkflowBinder.Detect(api.ToJsonString());
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in slots.ImageNodeIds) set.Add(id + "." + slots.ImageInput);
        foreach (var pair in slots.ImageListInputs) set.Add(pair.Key + "." + pair.Value);
        for (var index = 0; index < slots.VideoNodeIds.Count && index < slots.VideoInputs.Count; index++)
            set.Add(slots.VideoNodeIds[index] + "." + slots.VideoInputs[index]);
        for (var index = 0; index < slots.AudioNodeIds.Count && index < slots.AudioInputs.Count; index++)
            set.Add(slots.AudioNodeIds[index] + "." + slots.AudioInputs[index]);
        return set;
    }

    /// <summary>
    /// 把「这个我们不认识的节点长什么样」写成给模型看的说明：它自己、它每一路输入接到的源头
    /// （并标明源头是不是后端节点）、以及它的输出接到了谁。**只给事实，不给结论**——
    /// 判断「它是直通、自带值、还是纯界面件」正是要模型做的事。
    /// </summary>
    public static string DescribeForModel(string rawDraft, string type, JsonObject objectInfo, int maxNodes = 2)
    {
        var shape = new RawShape(JsonNode.Parse(rawDraft) as JsonObject ?? new JsonObject(), objectInfo);
        return shape.Describe(type, objectInfo, maxNodes);
    }

    private sealed record RawPlacement(string NodeId, string Input, RawSlot? Origin, string ClassType);

    /// <summary>
    /// 把网页格式的原稿摊平成「节点表 + 连线表」，id 规则与转换器一致（子图容器展开成 <c>容器id:内部id</c>）。
    ///
    /// 为什么要摊平：转换结果是摊平的，要拿它去原稿里对位，就得用同一套 id；子图里的节点在顶层查不到。
    /// </summary>
    private sealed class RawShape
    {
        private const string ModeNever = "2";
        private const string ModeBypass = "4";

        private readonly Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Origin, int Slot)> links = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> subgraphs = new(StringComparer.Ordinal);

        /// <summary>
        /// 这台机器的节点定义（<c>/object_info</c>）。用来回答「源头那个节点的类型这台机器上到底有没有」——
        /// 没有的话，链子是从那儿断的，与我们转换得对不对无关。拿不到定义时为 null（那就只说事实、不下结论）。
        /// </summary>
        private readonly JsonObject? objectInfo;

        public RawShape(JsonObject raw, JsonObject? objectInfo = null)
        {
            this.objectInfo = objectInfo;
            if (raw["definitions"]?["subgraphs"] is JsonArray definitions)
                foreach (var item in definitions)
                    if (item is JsonObject definition && definition["id"]?.ToString() is { Length: > 0 } id)
                        subgraphs[id] = definition;

            AddLinks(raw["links"], string.Empty);
            AddNodes(raw["nodes"] as JsonArray, string.Empty);
        }

        private void AddLinks(JsonNode? source, string prefix)
        {
            if (source is JsonArray rows)
            {
                // 顶层是**数组行**：[id, 起点, 槽, 终点, 槽, 类型]
                foreach (var row in rows)
                    if (row is JsonArray line && line.Count >= 5 && line[0]?.ToString() is { } linkId)
                        links[prefix + linkId] = (line[1]!.ToString(), Slot(line[2]));
            }
            else if (source is JsonObject objects)
            {
                // 子图定义里是**对象**：{id, origin_id, origin_slot, …}
                foreach (var pair in objects)
                    if (pair.Value is JsonObject row)
                    {
                        var origin = row["origin_id"]?.ToString() ?? "-20";
                        links[prefix + pair.Key] = (origin is "-10" or "-20" ? origin : prefix + origin, Slot(row["origin_slot"]));
                    }
            }
        }

        private void AddNodes(JsonArray? source, string prefix)
        {
            foreach (var item in source ?? new JsonArray())
            {
                if (item is not JsonObject node) continue;
                if (node["id"]?.ToString() is not { Length: > 0 } id) continue;
                nodes[prefix + id] = node;
                var type = node["type"]?.ToString() ?? string.Empty;
                if (!subgraphs.TryGetValue(type, out var definition)) continue;
                var inner = prefix + id + ":";
                AddLinks(definition["links"], inner);
                AddNodes(definition["nodes"] as JsonArray, inner);
            }
        }

        private static int Slot(JsonNode? value)
            => value is not null && int.TryParse(value.ToString(), out var slot) ? slot : 0;

        private static string ParentPrefix(string key)
        {
            var cut = key.LastIndexOf(':');
            return cut < 0 ? string.Empty : key[..(cut + 1)];
        }

        /// <summary>转换结果里那个节点/输入，在原稿里对应哪一条线。</summary>
        public RawPlacement? Locate(string nodeKey, string input)
        {
            if (!nodes.TryGetValue(nodeKey, out var node)) return null;
            var classType = node["type"]?.ToString() ?? string.Empty;
            if (node["inputs"] is not JsonArray slots) return new RawPlacement(nodeKey, input, null, classType);

            foreach (var item in slots)
            {
                if (item is not JsonObject slot) continue;
                if (slot["name"]?.ToString() != input) continue;
                var link = slot["link"]?.ToString();
                if (string.IsNullOrEmpty(link) || link == "null") return new RawPlacement(nodeKey, input, null, classType);
                if (!links.TryGetValue(ParentPrefix(nodeKey) + link, out var origin))
                    return new RawPlacement(nodeKey, input, null, classType);
                return new RawPlacement(nodeKey, input, new RawSlot(origin.Origin, origin.Slot), classType);
            }
            return new RawPlacement(nodeKey, input, null, classType);
        }

        /// <summary>
        /// 给这一处定性（与离线体检同一套判据）。返回 null 表示它不算「缺输入」（走完了链子、确实有源头）。
        /// </summary>
        public ComfyUiImportFinding Classify(string originKey, int slot)
        {
            var verdict = Walk(originKey, 0);
            return verdict switch
            {
                "live" => new ComfyUiImportFinding(ComfyUiFindingKind.DroppedByConversion, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, LiveCause(originKey)),
                "muted" => new ComfyUiImportFinding(ComfyUiFindingKind.MutedOrBypassed, string.Empty, string.Empty,
                    originKey, string.Empty, string.Empty, "源头被静音或绕过"),
                _ => new ComfyUiImportFinding(ComfyUiFindingKind.BrokenInSource, string.Empty, string.Empty,
                    originKey, string.Empty, string.Empty, "链子在这份原稿里就断着")
            };
        }

        /// <summary>
        /// 链子走到尽头那个节点，到底为什么接不过去。**不能说成一句「源头是个活着的后端节点」就完事**：
        /// 那个类型这台机器上根本没有时（自定义节点没装、或它本来就是纯前端件），链子是从那儿断的，
        /// 与转换得对不对无关——用户该做的是换一份/装那个节点，不是去重导一遍。
        /// </summary>
        private string LiveCause(string originKey)
        {
            var source = LiveSourceKey(originKey);
            var type = source.Length > 0 && nodes.TryGetValue(source, out var node)
                ? node["type"]?.ToString() ?? string.Empty
                : string.Empty;
            if (type.Length > 0 && objectInfo is { } definitions && !definitions.ContainsKey(type))
                return $"源头是个活着的节点，可它的类型（{type}）**这台服务器上没有**——"
                    + "链子是从那儿断的，跟转换得对不对无关（要让它跑得先给这台机器装上/认出这个节点）";
            return "源头是个活着的后端节点";
        }

        /// <summary>顺着「只转发」的节点往上，第一个「不是转发」的节点就是链子真正的源头。</summary>
        private string LiveSourceKey(string nodeKey)
        {
            for (var guard = 0; guard < 64; guard++)
            {
                if (!nodes.TryGetValue(nodeKey, out var node)) return string.Empty;
                var type = node["type"]?.ToString() ?? string.Empty;
                if (!IsForwardingType(type)) return nodeKey;
                var next = NextUpstream(nodeKey, node);
                if (next is null || next.Value.NodeKey == "-10") return string.Empty;
                nodeKey = next.Value.NodeKey;
            }
            return string.Empty;
        }

        /// <summary>链子上经过的、我们不认识的前端节点类型（给大模型认的清单）。</summary>
        public IReadOnlyList<string> UnknownTypesOnPath(string originKey)
        {
            var found = new List<string>();
            var current = originKey;
            for (var guard = 0; guard < 64; guard++)
            {
                if (!nodes.TryGetValue(current, out var node)) break;
                var type = node["type"]?.ToString() ?? string.Empty;
                if (!IsKnownFrontendType(type) && !IsForwardingType(type)) found.Add(type);
                if (!IsForwardingType(type)) break;
                var next = NextUpstream(current, node);
                if (next is null) break;
                current = next.Value.NodeKey;
            }
            return found;
        }

        /// <summary>顺「只转发」的节点往上找真正的源头：live / muted / dead。</summary>
        private string Walk(string nodeKey, int guard)
        {
            if (guard > 64) return "dead";
            if (!nodes.TryGetValue(nodeKey, out var node)) return "dead";

            var mode = node["mode"]?.ToString() ?? "0";
            if (mode == ModeNever || mode == ModeBypass) return "muted";
            var type = node["type"]?.ToString() ?? string.Empty;
            if (!IsForwardingType(type)) return "live";

            var next = NextUpstream(nodeKey, node);
            if (next is null) return "dead";
            if (next.Value.NodeKey == "-10")
                return WalkThroughContainer(nodeKey, next.Value.Slot, guard + 1);
            return Walk(next.Value.NodeKey, guard + 1);
        }

        /// <summary>「容器的输入」：顺着外面喂给容器那个输入的那条线继续往上走。</summary>
        private string WalkThroughContainer(string innerKey, int slot, int guard)
        {
            var containerKey = ParentPrefix(innerKey).TrimEnd(':');
            var outer = ParentPrefix(containerKey);
            if (!nodes.TryGetValue(containerKey, out var container)) return "dead";
            if ((container["inputs"] as JsonArray) is not { } slots || slot >= slots.Count) return "dead";
            if (slots[slot] is not JsonObject input || input["link"] is null || input["link"]!.ToString() == "null")
                return "dead";
            if (!links.TryGetValue(outer + input["link"]!.ToString(), out var parent)) return "dead";
            if (parent.Origin == "-10") return WalkThroughContainer(containerKey, parent.Slot, guard + 1);
            return Walk(parent.Origin, guard + 1);
        }

        /// <summary>这个转发节点把哪一路往上接。</summary>
        private RawSlot? NextUpstream(string nodeKey, JsonObject node)
        {
            var prefix = ParentPrefix(nodeKey);
            var type = node["type"]?.ToString() ?? string.Empty;

            // Set/Get 配对：Get 的值在同名的 Set 上（名字写在第一个控件上）。
            if (IsGetter(type))
            {
                var name = (node["widgets_values"] as JsonArray)?[0]?.ToString() ?? string.Empty;
                if (name.Length == 0) return null;
                var setter = nodes.FirstOrDefault(item => IsSetter(item.Value["type"]?.ToString() ?? string.Empty)
                    && ((item.Value["widgets_values"] as JsonArray)?[0]?.ToString() ?? string.Empty) == name);
                if (setter.Value is null) return null;
                prefix = ParentPrefix(setter.Key);
                node = setter.Value;
            }

            if (node["inputs"] is not JsonArray slots) return null;
            foreach (var item in slots)
            {
                if (item is not JsonObject slot) continue;
                var link = slot["link"]?.ToString();
                if (string.IsNullOrEmpty(link) || link == "null") continue;
                if (!links.TryGetValue(prefix + link, out var origin)) return null;
                return new RawSlot(origin.Origin, origin.Slot);
            }
            return null;
        }

        private static bool IsForwardingType(string type)
            => type.StartsWith("Reroute", StringComparison.Ordinal) || IsSetter(type) || IsGetter(type);

        private static bool IsSetter(string type) => Flat(type) is "setnode" or "nodeset" or "easysetnode";

        private static bool IsGetter(string type) => Flat(type) is "getnode" or "nodeget" or "easygetnode";

        private static bool IsKnownFrontendType(string type)
            => IsForwardingType(type) || Flat(type) == "primitivenode"
            || Flat(type).StartsWith("fastbypasser", StringComparison.Ordinal)
            || Flat(type).StartsWith("fastgroupsbypasser", StringComparison.Ordinal);

        private static string Flat(string type)
            => new(type.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>这个类型的节点长什么样（给模型看的说明，只讲事实）。</summary>
        public string Describe(string type, JsonObject objectInfo, int maxNodes)
        {
            var matches = nodes
                .Where(pair => (pair.Value["type"]?.ToString() ?? string.Empty) == type)
                .Take(Math.Max(1, maxNodes))
                .ToList();
            if (matches.Count == 0) return $"（这份原稿里没有类型为 {type} 的节点）";

            var builder = new StringBuilder();
            foreach (var pair in matches)
            {
                var node = pair.Value;
                builder.AppendLine($"节点 {pair.Key}（{type}）：" + Compact(node.ToJsonString()));

                builder.AppendLine("  它每一路输入接到的源头：");
                if (node["inputs"] is JsonArray slots)
                    for (var index = 0; index < slots.Count; index++)
                    {
                        if (slots[index] is not JsonObject slot) continue;
                        var name = slot["name"]?.ToString() ?? string.Empty;
                        var slotType = slot["type"]?.ToString() ?? string.Empty;
                        var link = slot["link"]?.ToString();
                        if (string.IsNullOrEmpty(link) || link == "null")
                        {
                            builder.AppendLine($"    · 输入[{index}]「{name}」（{slotType}）：没接线");
                            continue;
                        }
                        if (!links.TryGetValue(ParentPrefix(pair.Key) + link, out var origin))
                        {
                            builder.AppendLine($"    · 输入[{index}]「{name}」（{slotType}）：连线的源头找不到");
                            continue;
                        }
                        builder.AppendLine($"    · 输入[{index}]「{name}」（{slotType}）← 节点 {origin.Origin}"
                            + $"（{TypeOf(origin.Origin, objectInfo)}，后端节点：{(IsBackend(origin.Origin, objectInfo) ? "是" : "否")}）"
                            + $"第 {origin.Slot} 个输出");
                    }

                var consumers = new List<string>();
                foreach (var other in nodes)
                {
                    if (other.Value["inputs"] is not JsonArray others) continue;
                    foreach (var item in others)
                    {
                        if (item is not JsonObject slot) continue;
                        var link = slot["link"]?.ToString();
                        if (string.IsNullOrEmpty(link) || link == "null") continue;
                        if (!links.TryGetValue(ParentPrefix(other.Key) + link, out var origin)) continue;
                        if (origin.Origin != pair.Key) continue;
                        consumers.Add($"节点 {other.Key}（{TypeOf(other.Key, objectInfo)}）的输入「{slot["name"]}」"
                            + $"（{slot["type"]}）← 它的第 {origin.Slot} 个输出");
                    }
                }
                builder.AppendLine(consumers.Count == 0
                    ? "  它的输出没接到任何地方。"
                    : "  它的输出接到了：" + string.Join("；", consumers));
            }
            return builder.ToString();
        }

        private string TypeOf(string nodeKey, JsonObject objectInfo)
            => nodes.TryGetValue(nodeKey, out var node) ? node["type"]?.ToString() ?? "?" : "?";

        private bool IsBackend(string nodeKey, JsonObject objectInfo)
        {
            var type = TypeOf(nodeKey, objectInfo);
            return type != "?" && objectInfo[type] is not null;
        }

        /// <summary>给模型看的说明要短：原稿里一个节点的 JSON 可能上千字符，截到能读清形状就够了。</summary>
        private static string Compact(string text) => text.Length <= 900 ? text : text[..900] + "…（截断）";
    }
}

/// <summary>
/// 一份工作流的「体检结论」写成给人看的话——选择器里、出片之前都要用到，所以只写一处。
///
/// 为什么非说不可：这几类问题都让那份工作流**出不了片**，只是拦下的时机不同——
/// 缺必填输入是**提交就被挡住**（HTTP 400，实测）；引用的文件不在机器上则是**跑到那一步才失败**
/// （实测拿不到产物）。不说的话，用户只会觉得「这模型不行」，而真正该做的是换一份工作流、
/// 补一个文件，或者给上素材。
/// </summary>
public static class ComfyUiWorkflowHealth
{
    /// <summary>
    /// 按「站点 id + 工作流键」把这一份找出来（找不到、或站点文件读不到都返回 null）。
    /// 出图与出视频两条链都要用它，所以放在这里一份——两处各写一遍迟早会有一处漏掉新的判断。
    /// </summary>
    public static SiteWorkflow? Find(string siteId, string workflowKey)
    {
        if (siteId.Length == 0 || workflowKey.Length == 0) return null;
        try
        {
            return SiteCatalog.Load().Sites
                .FirstOrDefault(item => item.Id == siteId)?
                .Workflows.FirstOrDefault(item => item.Key == workflowKey);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _ = error;
            return null;
        }
    }

    /// <summary>没有问题时返回空串（不占地方、也不制造假警报）。</summary>
    public static string Describe(SiteWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var lines = new List<string>();
        if (workflow.DroppedInputs > 0)
            lines.Add($"⚠ 这份工作流有 **{workflow.DroppedInputs} 处输入是我们转换时丢掉的**"
                + "（导入体检查出来的）：缺的是必填输入，提交会被 ComfyUI 拒收（HTTP 400）——先换一份，"
                + "或者重新导入时选「让大模型认一认」。"
                + ListDetails(workflow.DroppedInputDetails));
        if (workflow.BrokenInputs > 0)
            lines.Add($"⚠ 这份工作流**自己**有 {workflow.BrokenInputs} 处断线"
                + "（没接线的 Reroute、没有同名 Set 的 Get——不是转换的问题）：同样是必填输入缺着，"
                + "提交会被 ComfyUI 拒收（实测 HTTP 400，它会点名缺哪一处）——"
                + "要去 ComfyUI 里把那几根线接上，或者换一份。"
                + ListDetails(workflow.BrokenInputDetails));
        if (workflow.MissingFiles > 0)
            lines.Add($"⚠ 这份工作流引用的文件里，有 {workflow.MissingFiles} 处**这台机器上没有**"
                + "（拿服务器当前的候选清单核出来的，换台机器结论会变）：**有产物依赖它**的那几处，"
                + "提交时会被 ComfyUI 当场挡下（实测 400）或者这一支拿不到结果；"
                + "**在出片链上没人读**的那几处不影响出片，逐条都写着是哪一种。"
                + "是**示例素材**的话，给上你的素材就能跑；是**模型/依赖**就得先把文件补到服务器上。"
                + ListDetails(workflow.MissingFileDetails));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 出片之前的一道检查：**这次没给满的那些素材槽位，它自己留着的示例是不是早就不在服务器上了**。
    ///
    /// 为什么要挡：绑定时「给不满就只写前几个，多出来的入口保持它原来的示例图」是有意的
    /// （不拿同一张图去凑数，那样等于谎报输入）；可那些示例来自别人的机器——实测 B02 那份
    /// 「单双三图」，用户给一张、另外两个入口留着自己的 `33.jpg`，而那张图服务器上早就没了：
    /// 提交回 400 `custom_validation_failed`（点名 `Invalid image file: 33.jpg`），连队列都进不去。
    /// 与其让用户对着这条 400 猜，不如在这儿说清「还差哪个入口、差的是什么」。
    ///
    /// <paramref name="filledSlots"/> 是调用方**这次会写满的**槽位（写法「节点 id.输入名」）。
    /// 没给满、而留着的示例又已经不在服务器上的，返回一条说明；给满了（或本来就没事）返回 null。
    /// </summary>
    public static string? DescribeUnfilledStaleMedia(SiteWorkflow workflow, IReadOnlyCollection<string> filledSlots)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.MissingMedia.Count == 0) return null;

        var filled = new HashSet<string>(filledSlots ?? Array.Empty<string>(), StringComparer.Ordinal);
        var stale = workflow.MissingMedia
            .Where(item => !filled.Contains(item.NodeId + "." + item.Input))
            .ToList();
        if (stale.Count == 0) return null;

        var detail = string.Join("；", stale.Select(item =>
            $"{item.Kind}槽位（节点 {item.NodeId}.{item.Input}）还留着它的示例 `{item.FileName}`，"
            + $"而那份文件已经不在服务器上了，有 {item.DependentOutputs} 路产物靠它"));
        return $"这次没给满，而空着的槽位留着**已经失效的示例**：{detail}。"
            + "提交会被 ComfyUI 当场挡下（实测 400 `custom_validation_failed`、点名那个文件名），"
            + "所以先不提交：把缺的素材给上，或者换一份工作流。";
    }

    /// <summary>
    /// 这次会**写满**哪些素材槽位（写法「节点 id.输入名」）——给
    /// <see cref="DescribeUnfilledStaleMedia"/> 判「哪些槽位还空着」用。
    ///
    /// 规矩与 <c>ComfyUiWorkflowBinder.Bind</c> 一模一样：**按入口顺序对号入座、给不满就只写前几个**
    /// （图片是「每组各取前几张」；清单式入口只要给了图就会写）。这份是它的影子——两处必须同步，
    /// 所以有一条用例把两者对起来钉住（`ComfyUiFilledMediaSlotsMatchTheBinder`）：
    /// 判据各说各话吃过一次亏，这次不让它重演。
    /// </summary>
    public static List<string> FilledMediaSlots(ComfyUiWorkflowSlots slots, int images, int videos, int audios)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var filled = new List<string>();

        void Take(IReadOnlyList<string> nodeIds, string input, int count)
        {
            for (var index = 0; index < nodeIds.Count && index < count; index++)
                filled.Add(nodeIds[index] + "." + input);
        }

        var imageInput = slots.ImageInput.Length > 0 ? slots.ImageInput : "image";
        if (slots.ImageGroups.Count > 1)
        {
            foreach (var group in slots.ImageGroups)
                Take(group, imageInput, images);
        }
        else
        {
            var imageSlots = slots.ImageNodeIds.Count > 0
                ? (IReadOnlyList<string>)slots.ImageNodeIds
                : slots.ImageNodeId.Length > 0 ? new[] { slots.ImageNodeId } : Array.Empty<string>();
            Take(imageSlots, imageInput, images);
        }

        // 清单式入口（多行文本框）只要给了图就会写它，那个字段整体算「给上了」。
        if (images > 0)
            foreach (var (listNodeId, listInput) in slots.ImageListInputs)
                filled.Add(listNodeId + "." + listInput);

        for (var index = 0; index < slots.VideoNodeIds.Count && index < videos; index++)
            filled.Add(slots.VideoNodeIds[index] + "." + slots.VideoInputs[index]);
        for (var index = 0; index < slots.AudioNodeIds.Count && index < audios; index++)
            filled.Add(slots.AudioNodeIds[index] + "." + slots.AudioInputs[index]);

        return filled;
    }

    /// <summary>
    /// 把「到底是哪几处」列出来——只报个数等于把找人的活儿推回给用户。
    /// 最多列 6 条，多的收成一行，免得一份 18 处的工作流把说明撑满。
    /// </summary>
    private static string ListDetails(IReadOnlyList<string> details)
    {
        if (details.Count == 0) return string.Empty;
        var shown = details.Take(6).Select(item => Environment.NewLine + "    · " + item);
        var tail = details.Count > 6
            ? $"{Environment.NewLine}    · …还有 {details.Count - 6} 处（重新导入会重算这份清单）"
            : string.Empty;
        return string.Join(string.Empty, shown) + tail;
    }

    /// <summary>选择器那一行挂的记号（列表里一眼能看出哪几份有问题）。没有问题就是空串。</summary>
    public static string ShortMark(SiteWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.DroppedInputs > 0) return "｜⚠ 转换丢过输入";
        if (workflow.BrokenInputs > 0) return "｜⚠ 自己有断线";
        return workflow.MissingFiles > 0 ? $"｜⚠ 引用文件缺 {workflow.MissingFiles} 处" : string.Empty;
    }
}
