using System.Text;
using System.Text.Json.Nodes;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>体检发现的一处问题的种类。</summary>
public enum ComfyUiFindingKind
{
    /// <summary>
    /// **我们丢了**：那个必填输入的源头是个活着的后端节点，本该接得上，转换时却整项消失了。
    /// 这是唯一真正要处理的一类（服务端往往还回 success，只是产出为空）。
    /// </summary>
    DroppedByConversion,

    /// <summary>源头被静音（mode=2）或被绕过（mode=4）：官方语义就是删掉那一项，正常。</summary>
    MutedOrBypassed,

    /// <summary>原稿自己断线（没接线的 Reroute、没有同名 Set 的 Get）：工作流自身的问题，不是我们的。</summary>
    BrokenInSource,

    /// <summary>在原稿里找不到对应的节点/输入，判断不了（正常情况下应当是 0）。</summary>
    Unclassified
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
    public string Label => $"{WorkflowTitle} · {ClassType}.{Input}（节点 {NodeId}）";
}

/// <summary>
/// 导入时的体检账。**只报事实与归因**，不替用户判断该不该修：
/// 「我们丢了」才需要处理；静音/绕过是正常的；原稿自己断线是那份工作流的问题（换一份就行）。
/// </summary>
public sealed record ComfyUiImportAuditReport(
    int ScannedWorkflows,
    int PayloadNodes,
    IReadOnlyList<ComfyUiImportFinding> Findings,
    IReadOnlyList<ComfyUiUnknownType> UnknownTypes)
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
              + "源头是个活着的后端节点，本该接得上。**服务端往往不报错**，只是那一步不产出。"
            : "\u2713 没有发现「我们转换时丢掉的输入」——这几百份的转换是干净的。");

        var muted = Count(ComfyUiFindingKind.MutedOrBypassed);
        if (muted > 0)
            lines.Add($"· 另有 {muted} 处源头被**静音或绕过**：官方语义就是删掉那一项，不用管。");

        var broken = Count(ComfyUiFindingKind.BrokenInSource);
        if (broken > 0)
            lines.Add($"· 还有 {broken} 处是**那份工作流自己断线**（没接线的 Reroute、没有同名 Set 的 Get）："
                + "不是转换的问题，用它会缺东西——建议换一份，或去 ComfyUI 里把线接上。");

        var unknown = Count(ComfyUiFindingKind.Unclassified);
        if (unknown > 0)
            lines.Add($"· 另有 {unknown} 处判断不了（在原稿里找不到对应节点），已如实记下。");

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

        var counts = Findings
            .Where(item => item.Kind is ComfyUiFindingKind.DroppedByConversion or ComfyUiFindingKind.BrokenInSource)
            .GroupBy(item => item.WorkflowKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (
                    Dropped: group.Count(item => item.Kind == ComfyUiFindingKind.DroppedByConversion),
                    Broken: group.Count(item => item.Kind == ComfyUiFindingKind.BrokenInSource)),
                StringComparer.Ordinal);

        foreach (var workflow in workflows)
        {
            workflow.DroppedInputs = 0;
            workflow.BrokenInputs = 0;
            if (!counts.TryGetValue(workflow.Key, out var found)) continue;
            workflow.DroppedInputs = found.Dropped;
            workflow.BrokenInputs = found.Broken;
        }
    }
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

            var shape = new RawShape(raw);
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
                            node.Key, classType, requiredInput.Key, "在原稿里找不到这个节点或这个输入，判断不了"));
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
                    findings.Add(finding);

                    // 链子上的「不认识的前端节点」就是让大模型认的清单。
                    if (finding.Kind == ComfyUiFindingKind.DroppedByConversion && where.Origin is { } origin)
                        foreach (var type in shape.UnknownTypesOnPath(origin.NodeKey))
                            unknown[type] = unknown.TryGetValue(type, out var seen)
                                ? (seen.Count + 1, seen.Key, seen.Title)
                                : (1, pair.Key, title);
                }
            }
        }

        var types = unknown
            .Select(item => new ComfyUiUnknownType(item.Key, item.Value.Count, item.Value.Key, item.Value.Title))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Type, StringComparer.Ordinal)
            .ToList();

        return new ComfyUiImportAuditReport(scanned, nodeTotal, findings, types);
    }

    /// <summary>原稿里那一处输入连到哪儿了。</summary>
    private readonly record struct RawSlot(string NodeKey, int Slot);

    /// <summary>
    /// 把「这个我们不认识的节点长什么样」写成给模型看的说明：它自己、它每一路输入接到的源头
    /// （并标明源头是不是后端节点）、以及它的输出接到了谁。**只给事实，不给结论**——
    /// 判断「它是直通、自带值、还是纯界面件」正是要模型做的事。
    /// </summary>
    public static string DescribeForModel(string rawDraft, string type, JsonObject objectInfo, int maxNodes = 2)
    {
        var shape = new RawShape(JsonNode.Parse(rawDraft) as JsonObject ?? new JsonObject());
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

        public RawShape(JsonObject raw)
        {
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
                    string.Empty, string.Empty, string.Empty, "源头是个活着的后端节点"),
                "muted" => new ComfyUiImportFinding(ComfyUiFindingKind.MutedOrBypassed, string.Empty, string.Empty,
                    originKey, string.Empty, string.Empty, "源头被静音或绕过"),
                _ => new ComfyUiImportFinding(ComfyUiFindingKind.BrokenInSource, string.Empty, string.Empty,
                    originKey, string.Empty, string.Empty, "链子在这份原稿里就断着")
            };
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
/// 为什么非说不可：这两种问题都让「用它出片」缺东西，而服务端往往还回 **success**、只是产出为空。
/// 不说的话，用户只会觉得「这模型不行」，而真正该做的是换一份工作流。
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
                + "（导入体检查出来的）：用它出片可能缺东西，甚至跑完什么都不产出——先换一份，"
                + "或者重新导入时选「让大模型认一认」。");
        if (workflow.BrokenInputs > 0)
            lines.Add($"⚠ 这份工作流**自己**有 {workflow.BrokenInputs} 处断线"
                + "（没接线的 Reroute、没有同名 Set 的 Get——不是转换的问题）：用它出片会缺东西，"
                + "建议换一份，或去 ComfyUI 里把那几处线接上。");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>选择器那一行挂的记号（列表里一眼能看出哪几份有问题）。没有问题就是空串。</summary>
    public static string ShortMark(SiteWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.DroppedInputs > 0) return "｜⚠ 转换丢过输入";
        return workflow.BrokenInputs > 0 ? "｜⚠ 自己有断线" : string.Empty;
    }
}
