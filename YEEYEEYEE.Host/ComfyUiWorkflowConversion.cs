using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YEEYEEYEE.Host;

/// <summary>一次转换的结果：可提交的 API 工作流，加上「哪些节点被跳过、为什么」的诚实说明。</summary>
public sealed record ComfyUiConversionResult(JsonObject ApiWorkflow, IReadOnlyList<string> Notes)
{
    /// <summary>跳过说明合并成一句，没有跳过时为空串。</summary>
    public string SkippedSummary => Notes.Count == 0 ? string.Empty : string.Join("；", Notes);

    public string ToJson() => ApiWorkflow.ToJsonString(JsonOptions);

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}

/// <summary>
/// 把 ComfyUI「网页格式」的工作流转成 <c>/prompt</c> 要的「API 格式」。
///
/// 这不是我们自己发明的规则，而是照搬官方前端 <c>src/utils/executionUtil.ts</c> 里
/// <c>graphToPrompt()</c> 的行为，落到「读 JSON」这一层（官方是在内存的图对象上跑）：
///
///   1. 被静音（mode=2）/ 被绕过（mode=4）的节点不进 API —— 官方那条 <c>mode === NEVER || BYPASS</c>；
///   2. 不是后端节点的（备注、rgthree 的 Label 之类）不进 API —— 官方靠 <c>node.comfyClass</c> 为空自然落空，
///      我们这里改成「<c>/object_info</c> 里查不到这个类型」来判定；
///   3. 控件值按**位置**对齐 <c>widgets_values</c>；声明了 <c>control_after_generate</c> 的控件后面
///      还跟着一个「生成后随机/固定」的值，它占位但**不进 API**（KSampler.seed 就是这种）；
///   4. 连线还原成 <c>[源节点id, 源槽]</c>，id 一律字符串（官方就是 <c>[origin_id, origin_slot]</c>）；
///   5. <c>class_type</c> 取节点类型；<c>_meta</c> 带标题与节点包身份；
///   6. 收尾：指向「已被跳过节点」的连线整条删掉。
///
/// 验收方式：用真机导出的 API 样本逐字对拍（见 Agent.Tests 的「ComfyUI 工作流转换」一组测试）。
/// </summary>
public static class ComfyUiWorkflowConversion
{
    /// <summary>
    /// 声明成这些类型的输入是「控件」（值直接写在节点上），其余字符串类型（MODEL/IMAGE/…）是连线槽位。
    /// 下拉框有两种写法都要认：老写法把候选数组放在第一位（<c>[[…], {…}]</c>），新写法写 <c>["COMBO", {options:[…]}]</c>。
    /// 注意 <c>socketless</c> 的预览类槽位（如 ResolutionSelector 的 RESOLUTION_PREVIEW）**不占** widgets_values 的位置，
    /// 所以它不在这里——样本实测三个控件值正好对三个 required 控件。
    /// </summary>
    private static readonly HashSet<string> WidgetTypeNames = new(StringComparer.Ordinal)
    {
        "INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"
    };

    private const int ModeNever = 2;   // 官方 LGraphEventMode.NEVER
    private const int ModeBypass = 4;  // 官方 LGraphEventMode.BYPASS

    public static ComfyUiConversionResult Convert(string uiWorkflowJson, string objectInfoJson)
    {
        var objectInfo = JsonNode.Parse(objectInfoJson) as JsonObject
            ?? throw new InvalidOperationException("object_info 不是 JSON 对象");
        return Convert(uiWorkflowJson, objectInfo);
    }

    public static ComfyUiConversionResult Convert(string uiWorkflowJson, JsonObject objectInfo)
    {
        var ui = JsonNode.Parse(uiWorkflowJson) as JsonObject
            ?? throw new InvalidOperationException("工作流不是 JSON 对象");

        var nodes = ui["nodes"] as JsonArray
            ?? throw new InvalidOperationException("工作流里没有 nodes");

        var links = ReadLinks(ui["links"] as JsonArray);

        var output = new JsonObject();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var unnamedNotes = new List<string>();

        foreach (var entry in nodes)
        {
            if (entry is not JsonObject node) continue;
            var type = AsText(node["type"]);
            if (string.IsNullOrEmpty(type)) continue;

            var mode = ReadInt(node["mode"]) ?? 0;
            if (mode is ModeNever or ModeBypass)
            {
                Bump(skipped, "被静音或绕过（mode=" + mode.ToString(CultureInfo.InvariantCulture) + "）");
                continue;
            }

            if (objectInfo[type] is not JsonObject def)
            {
                // 不是后端节点：备注、Label 这类纯界面物件。官方靠 comfyClass 为空落空，效果一致。
                Bump(skipped, "不是后端节点（" + type + "）");
                continue;
            }

            var id = IdText(node["id"]);
            if (id is null) continue;

            var (apiNode, unnamed) = BuildApiNode(node, def, type, links);
            output[id] = apiNode;
            if (unnamed > 0)
            {
                // 节点类自己加的控件（如 pysssss ShowText 的 text_0、VHS 的 videopreview）在节点
                // 定义里查不到，纯 JSON 变换无从判断该叫什么、该不该进 API——如实报出来，不猜。
                unnamedNotes.Add("跳过 " + unnamed.ToString(CultureInfo.InvariantCulture)
                    + " 个定义里没有的控件值：节点 " + id + "（" + type + "）——节点类自己加的控件"
                    + "（预览、文本显示之类）前端导出时也不进 API");
            }
        }

        PruneDanglingLinks(output);

        return new ComfyUiConversionResult(output, [.. DescribeSkips(skipped), .. unnamedNotes]);
    }

    private static (JsonObject Node, int UnnamedWidgets) BuildApiNode(JsonObject node, JsonObject def, string type, IReadOnlyDictionary<long, (string OriginId, int OriginSlot)> links)
    {
        var inputs = new JsonObject();

        // ── 控件值 ────────────────────────────────────────────────────────────
        // widgets_values 有两种写法，都要认：
        //   1) 数组（按位置对齐控件顺序）——老写法；
        //   2) 具名对象（键就是控件名）——新写法，直接按名字取，不必对位。
        var widgetSpecs = WidgetSpecsOf(node, def);
        var arrayValues = node["widgets_values"] as JsonArray;
        var namedValues = node["widgets_values"] as JsonObject;
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0;

        for (var index = 0; index < widgetSpecs.Count; index++)
        {
            var spec = widgetSpecs[index];
            JsonNode? raw;

            if (namedValues is not null)
            {
                if (namedValues.TryGetPropertyValue(spec.Name, out var direct))
                {
                    raw = direct;
                    consumed.Add(spec.Name);
                }
                else
                {
                    raw = spec.Default;
                }
            }
            else if (arrayValues is not null && cursor < arrayValues.Count)
            {
                raw = arrayValues[cursor];
                cursor++;

                // 「生成后随机/固定」这个控件占一个位置，但它不进 API。两种情形：
                //  1) 定义里声明了 control_after_generate（KSampler.seed 这种）；
                //  2) 名字像种子（seed / noise_seed / *_seed）**且值比控件多**——有些节点包自己
                //     加了这个控件却没写进定义（实测 llama_cpp_instruct_adv 的 seed）。
                // 加「值比控件多」这个前提，是为了在更旧的网页文件（值更少）上不会多吞一个位置。
                var surplus = arrayValues.Count - cursor - (widgetSpecs.Count - index - 1);
                if (spec.ControlAfterGenerate || (surplus > 0 && IsSeedLike(spec.Name))) cursor++;
            }
            else
            {
                // 值比控件清单短：这份文件比节点定义旧，尾部控件按前端行为取声明里的默认值。
                raw = spec.Default;
            }

            var value = Coerce(raw, spec.Type);
            if (value is null) continue;
            inputs[spec.Name] = value;
        }

        // 定义里没有的控件值（节点类自己加的预览、文本显示之类）前端导出时也不进 API，
        // 这里如实计数报出去，不编个名字塞进 inputs。
        var unnamed = namedValues is not null
            ? namedValues.Count(pair => !consumed.Contains(pair.Key))
            : arrayValues is null ? 0 : Math.Max(0, arrayValues.Count - cursor);

        // ── 连线：还原成 [源节点id, 源槽] ────────────────────────────────────
        if (node["inputs"] is JsonArray slots)
        {
            foreach (var slot in slots)
            {
                if (slot is not JsonObject input) continue;
                var name = AsText(input["name"]);
                if (string.IsNullOrEmpty(name)) continue;
                var linkId = ReadLong(input["link"]);
                if (linkId is not { } key || !links.TryGetValue(key, out var origin)) continue;
                // 官方是 resolveInput：连着线就以线为准（即使这个槽位本身是个控件）。
                inputs[name] = new JsonArray(JsonValue.Create(origin.OriginId), JsonValue.Create(origin.OriginSlot));
            }
        }

        var meta = new JsonObject();
        // 标题取节点自定义名，没有就用后端给的名字。**注意**：网页导出时这里是「前端翻译过」的
        // 展示名（SaveImage → 保存图像），那份翻译一半在前端打包的 locale 里、一半在服务器的
        // /api/i18n（只有自带翻译的节点包才有）。后端明确忽略 _meta，所以这里不做翻译复刻——
        // 对拍时也把这一项单独对待（见 Agent.Tests 的转换测试）。
        var title = AsText(node["title"]) ?? AsText(def["display_name"]) ?? type;
        if (!string.IsNullOrEmpty(title)) meta["title"] = title;

        // 节点包身份（cnr_id / aux_id / ver）**不发**：官方代码虽然写了这一段，但真机导出里
        // 一次都没出现过——四份样本约一百个节点，UI 侧明明带着 cnr_id（comfy-core、ComfyLiterals、
        // comfyui_layerstyle 都有），导出结果里却一个都没有。后端不读 _meta，所以照样本行为走。
        // 万一将来遇到带包身份的样本，对拍会立刻指出来。

        var apiNode = new JsonObject
        {
            ["inputs"] = inputs,
            ["class_type"] = type,
            ["_meta"] = meta
        };
        return (apiNode, unnamed);
    }

    private sealed record WidgetSpec(string Name, string Type, bool ControlAfterGenerate, JsonNode? Default);

    /// <summary>
    /// 取这个节点的控件清单，按「声明顺序」排列，来自两处的并集：
    ///
    /// 1) <b>节点自己列出的控件槽位</b>（<c>inputs[]</c> 里带 <c>widget</c> 标记的那些）。这一份是权威的，
    ///    因为它包含<b>节点包动态加的控件</b>——VHS_VideoCombine 的 <c>pix_fmt / crf / save_metadata /
    ///    trim_to_audio</c> 就是按 <c>format</c> 下拉框的取值动态长出来的，节点定义里查不到；同时它正好
    ///    排除了纯界面控件（VHS 的 <c>videopreview</c>、pysssss ShowText 的 <c>text_0</c> 都不在其中）。
    /// 2) <b>定义里有、节点没列的控件</b>（文件比节点定义旧时，前端加载会把它补上并取默认值——
    ///    实测 ModelSamplingAuraFlow 的 optional <c>sampling</c>）。
    ///
    /// 连线槽位（MODEL/IMAGE/…）不占 widgets_values 的位置；下拉框算控件。
    /// <c>forceInput</c> 声明成「只能连线、不给控件」的，即使类型是 STRING/FLOAT 也要跳过。
    /// </summary>
    private static List<WidgetSpec> WidgetSpecsOf(JsonObject node, JsonObject def)
    {
        var fromDef = DefWidgetSpecs(def);
        var known = fromDef.ToDictionary(spec => spec.Name, StringComparer.Ordinal);

        var specs = new List<WidgetSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (node["inputs"] is JsonArray slots)
        {
            foreach (var slot in slots)
            {
                if (slot is not JsonObject input || input["widget"] is null) continue;
                var name = AsText(input["name"]);
                if (string.IsNullOrEmpty(name)) continue;

                // 只要「真正的取值控件」：类型得是下拉框或 INT/FLOAT/STRING/BOOLEAN。
                // 传别的（如 LoadImage 由 image_upload 生出来的按钮，类型写作 IMAGEUPLOAD）都是纯界面件，
                // 前端导出时也不序列化。注意这一步要在记 seen 之前——否则会连带把定义里同名控件也挡掉。
                if (!IsWidgetType(input["type"])) continue;
                if (!seen.Add(name)) continue;

                // 定义里声明过的，用定义那一份（类型、默认值、控制位都以定义为准）；
                // 定义里没有的才是节点包动态加的（如 VHS 的 pix_fmt/crf），只能用节点自己写的类型。
                specs.Add(known.TryGetValue(name, out var declaredSpec)
                    ? declaredSpec
                    : new WidgetSpec(name, DeclaredTypeOf(input["type"]), false, null));
            }
        }

        foreach (var spec in fromDef)
        {
            if (seen.Add(spec.Name)) specs.Add(spec);
        }
        return specs;
    }

    /// <summary>节点定义里声明的控件（含默认值，以及「生成后随机/固定」占位标记）。</summary>
    private static List<WidgetSpec> DefWidgetSpecs(JsonObject def)
    {
        var list = new List<WidgetSpec>();
        if (def["input"] is not JsonObject input) return list;

        foreach (var group in new[] { "required", "optional" })
        {
            if (input[group] is not JsonObject section) continue;
            foreach (var pair in section)
            {
                if (pair.Value is not JsonArray spec || spec.Count == 0) continue;
                var first = spec[0];
                string declared;
                if (first is JsonArray) declared = "COMBO";
                else if (AsText(first) is { } name && WidgetTypeNames.Contains(name)) declared = name;
                else continue; // 连线槽位

                var options = spec.Count > 1 ? spec[1] as JsonObject : null;

                // forceInput：声明成「只能连线、不给控件」。类型虽然写在 primitives 里也要跳过，
                // 否则会凭空多出一个值（实测 ZealmanLLM_Generate 的 video_seconds / audio_context）。
                if (options?["forceInput"] is JsonValue forced && forced.TryGetValue<bool>(out var mustConnect) && mustConnect) continue;

                var control = options?["control_after_generate"] is JsonValue flag
                              && flag.TryGetValue<bool>(out var on) && on;

                JsonNode? fallback = options?["default"]?.DeepClone();
                if (fallback is null && first is JsonArray choices && choices.Count > 0) fallback = choices[0]?.DeepClone();

                list.Add(new WidgetSpec(pair.Key, declared, control, fallback));
            }
        }
        return list;
    }

    /// <summary>这个槽位类型是不是「真正的取值控件」：下拉框（候选数组）或 INT/FLOAT/STRING/BOOLEAN。</summary>
    private static bool IsWidgetType(JsonNode? type)
        => type is JsonArray || (AsText(type) is { } name && WidgetTypeNames.Contains(name));

    /// <summary>节点自己写的槽位类型：数组形式的下拉框一律按 COMBO 处理。</summary>
    private static string DeclaredTypeOf(JsonNode? type)
        => type is JsonArray ? "COMBO" : AsText(type) ?? "COMBO";

    /// <summary>
    /// 按定义里声明的类型归一化取值。网页格式里数字控件常被存成字符串（"720"），
    /// 而 API 格式里应该是数字——官方由控件自己的取值逻辑完成这一步，我们在这里补上。
    /// </summary>
    private static JsonNode? Coerce(JsonNode? raw, string declared)
    {
        if (raw is null) return null;
        if (raw is JsonArray) return new JsonObject { ["__value__"] = raw.DeepClone() }; // 裸数组会被误认成连线
        if (raw is not JsonValue value) return raw.DeepClone();

        var text = AsText(value);
        switch (declared)
        {
            case "INT":
                if (value.TryGetValue<long>(out var number)) return JsonValue.Create(number);
                if (text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return JsonValue.Create(parsed);
                break;
            case "FLOAT":
                if (value.TryGetValue<double>(out var fraction)) return JsonValue.Create(fraction);
                if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFraction))
                    return JsonValue.Create(parsedFraction);
                break;
            case "BOOLEAN":
                if (value.TryGetValue<bool>(out var flag)) return JsonValue.Create(flag);
                if (text is "true") return JsonValue.Create(true);
                if (text is "false") return JsonValue.Create(false);
                break;
            case "STRING":
                if (text is not null) return JsonValue.Create(text);
                break;
        }
        return raw.DeepClone();
    }

    /// <summary>名字像种子的控件：前端会在这类控件后面再挂一个「生成后随机/固定」控件。</summary>
    private static bool IsSeedLike(string name)
        => name is "seed" or "noise_seed" || name.EndsWith("_seed", StringComparison.Ordinal);

    /// <summary>取字符串值；不是字符串（数字/布尔）时返回 null，绝不抛。</summary>
    private static string? AsText(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Dictionary<long, (string OriginId, int OriginSlot)> ReadLinks(JsonArray? links)
    {
        var map = new Dictionary<long, (string, int)>();
        if (links is null) return map;
        foreach (var entry in links)
        {
            if (entry is not JsonArray row || row.Count < 3) continue;
            if (ReadLong(row[0]) is not { } id) continue;
            var originId = IdText(row[1]);
            var originSlot = (int)(ReadLong(row[2]) ?? 0);
            if (originId is null) continue;
            map[id] = (originId, originSlot);
        }
        return map;
    }

    /// <summary>官方最后一步：连到「已不进 API 的节点」的输入整条删掉。</summary>
    private static void PruneDanglingLinks(JsonObject output)
    {
        foreach (var pair in output.ToList())
        {
            if (pair.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (var input in inputs.ToList())
            {
                if (input.Value is not JsonArray link || link.Count != 2) continue;
                var target = (link[0] as JsonValue)?.GetValue<string>();
                if (target is not null && output[target] is not null) continue;
                inputs.Remove(input.Key);
            }
        }
    }

    private static string? IdText(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static int? ReadInt(JsonNode? node) => (int?)ReadLong(node);

    private static void Bump(Dictionary<string, int> counts, string reason)
        => counts[reason] = counts.TryGetValue(reason, out var n) ? n + 1 : 1;

    private static List<string> DescribeSkips(Dictionary<string, int> counts)
        => counts.Select(pair => "跳过 " + pair.Value.ToString(CultureInfo.InvariantCulture) + " 个节点：" + pair.Key).ToList();
}
