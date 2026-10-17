namespace YEEYEEYEE.Web;

using System.Text.Json;

/// <summary>聊天技能参数的类型。声明、服务端校验与前端编辑器共用同一套口径。</summary>
public enum ChatParameterKind
{
    String,
    Number,
    Boolean,
    Enum,
    Array,
    Object
}

/// <summary>
/// 一个聊天技能参数的声明。
///
/// 为什么必须**先声明再收**：技能能收哪些参数不能由调用方（模型或浏览器）说了算。
/// 收下不认识的东西再丢掉就是静默丢参数——用户以为设置了，实际没生效，而且不会有任何提示。
/// 这份声明同时供三处使用：服务端校验（错误码见下）、模型提示词（告诉它有哪些参数）、前端编辑器（渲染控件）。
/// </summary>
/// <param name="Name">参数名（与 invoke 请求体里的键一致）。</param>
/// <param name="Kind">类型：字符串 / 数字 / 布尔 / 枚举 / 数组 / 对象。</param>
/// <param name="Required">是否必填；必填项缺失返回 <c>SKILL_PARAM_REQUIRED</c>。</param>
/// <param name="Description">给模型与界面看的一句话说明。</param>
/// <param name="Options">枚举类型的可选值；其它类型为空。</param>
/// <param name="Editable">聊天内是否允许用户编辑（模型自主决定的不列进编辑器）。</param>
/// <param name="Minimum">数字下限（含）。</param>
/// <param name="Maximum">数字上限（含）；字符串类型表示最大长度。</param>
public sealed record ChatParameterSpec(
    string Name,
    ChatParameterKind Kind,
    bool Required,
    string Description,
    IReadOnlyList<string> Options,
    bool Editable,
    double? Minimum = null,
    double? Maximum = null);

/// <summary>
/// 聊天技能参数的声明与校验。
///
/// 错误码是稳定的，前端与用户提示都按它走：
/// <list type="bullet">
/// <item><c>SKILL_PARAM_UNKNOWN</c> 参数名不在该技能的声明里。</item>
/// <item><c>SKILL_PARAM_TYPE</c> 值类型（或数字范围）不符。</item>
/// <item><c>SKILL_PARAM_INVALID</c> 类型对但取值不被接受（枚举越界、数组项不合法、对象字段过深）。</item>
/// <item><c>SKILL_PARAM_REQUIRED</c> 必填参数缺失。</item>
/// <item><c>SKILL_INPUT_UNSUPPORTED</c> 参数名是本协议认识的，但**这个技能**不支持（不静默忽略）。</item>
/// </list>
/// </summary>
public static class WebChatSkillParameters
{
    /// <summary>字符串参数的最大长度。</summary>
    public const int MaxStringLength = 4000;

    /// <summary>数组参数的最大项数。</summary>
    public const int MaxArrayItems = 32;

    /// <summary>对象参数的最大字段数。</summary>
    public const int MaxObjectProperties = 32;

    /// <summary>数组每一项的最大长度。</summary>
    public const int MaxArrayItemLength = 512;

    internal const string WebImageSkillId = "web.text-to-image";
    internal const string LocalImageSkillId = "comfyui.text-to-image";
    internal const string VideoSkillId = "web.text-to-video";

    private static readonly string[] ImageRatios = { "1:1", "16:9", "9:16", "4:3", "3:4" };

    private static ChatParameterSpec Text(string name, bool required, string description, bool editable = false, int maximum = MaxStringLength) =>
        new(name, ChatParameterKind.String, required, description, Array.Empty<string>(), editable, null, maximum);

    private static ChatParameterSpec Number(string name, string description, double minimum, double maximum, bool editable = false) =>
        new(name, ChatParameterKind.Number, false, description, Array.Empty<string>(), editable, minimum, maximum);

    private static ChatParameterSpec Flag(string name, string description, bool editable = false) =>
        new(name, ChatParameterKind.Boolean, false, description, Array.Empty<string>(), editable);

    private static ChatParameterSpec Choice(string name, bool required, string description, IReadOnlyList<string> options, bool editable) =>
        new(name, ChatParameterKind.Enum, required, description, options, editable);

    private static ChatParameterSpec List(string name, string description, bool editable = false) =>
        new(name, ChatParameterKind.Array, false, description, Array.Empty<string>(), editable);

    private static readonly ChatParameterSpec[] PromptOnly =
    {
        Text("prompt", true, "完整提示词")
    };

    /// <summary>Web 图片接口这条路能真正落到请求里的参数（反向提示词由执行器拼进 prompt）。</summary>
    private static readonly ChatParameterSpec[] ImageApi =
    {
        Text("prompt", true, "完整提示词"),
        // 比例在聊天里由用户点选、在直连接口里由 IMAGE_RATIO_REQUIRED 单独把关，
        // 所以这里不标必填：标了会让「不带比例的直连调用」由比例校验变成缺参校验，报错位置就偏了。
        Choice("aspectRatio", false, "图片比例", ImageRatios, true),
        Text("negativePrompt", false, "反向提示词", true),
        Flag("candidateBatch", "节点候选批次（聊天内由宿主决定）"),
        // 参考图可改：模型选错时用户要能自己指定；服务端仍逐张校验它确属本条会话的图片。
        List("referenceAttachmentIds", "参考图附件 ID（本条对话已上传的图片）", editable: true)
    };

    /// <summary>本机 ComfyUI 文生图：执行器会读 negativePrompt 与 seed。</summary>
    private static readonly ChatParameterSpec[] LocalImage =
    {
        Text("prompt", true, "完整提示词"),
        Choice("aspectRatio", false, "图片比例", ImageRatios, true),
        Text("negativePrompt", false, "反向提示词", true),
        // 种子是这条路**真正会读**的参数（ComfyUiWorkflowFactory 把它写进模板），所以可改；
        // Web 图片接口那条路不读它，那边就没有这个控件——两边各按自己真实支持的能力声明。
        Number("seed", "随机种子", 0, 9007199254740991, editable: true),
        Flag("candidateBatch", "节点候选批次（聊天内由宿主决定）"),
        List("referenceAttachmentIds", "参考图附件 ID（本条对话已上传的图片）", editable: true)
    };

    private static readonly ChatParameterSpec[] Video =
    {
        Text("prompt", true, "完整提示词"),
        // 反向提示词只有 ComfyUI 那条视频路认；接口站那条明确拒收它（SKILL_INPUT_UNSUPPORTED），
        // 所以这里不摆控件——一个换后端就会被拒的输入框比没有更糟。
        Text("negativePrompt", false, "反向提示词"),
        Text("model", false, "模型名", false, 256),
        // 秒数是**两套视频执行器都认**的量纲（ComfyUI 那条由聊天侧折成帧数，见 WebChatApi），
        // 所以用户改的是秒，不是帧。
        Number("seconds", "时长（秒）", 1, 600, editable: true),
        Number("width", "宽（像素）", 64, 2048, editable: true),
        Number("height", "高（像素）", 64, 2048, editable: true),
        Number("videoFrames", "帧数", 1, 4096),
        // 种子只有 ComfyUI 那条视频路认（接口站那条明确拒收），所以不在界面上摆一个
        // 换后端就会被拒的控件。
        Number("seed", "随机种子", 0, 9007199254740991),
        // 参考图在视频这两条路上差别很大（接口站只收一张首帧、ComfyUI 那条模板不接受首帧），
        // 摆成可改的控件只会让用户撞上「服务端拒收」或「任务失败」——这条仍由模型与宿主决定。
        List("referenceAttachmentIds", "首帧参考图附件 ID（本条对话已上传的图片）")
    };

    /// <summary>本协议认识的全部参数名。用来区分「协议不认识」与「这个技能不支持」。</summary>
    private static readonly HashSet<string> KnownNames = new(StringComparer.Ordinal)
    {
        "prompt", "aspectRatio", "negativePrompt", "candidateBatch", "referenceAttachmentIds",
        "seed", "model", "seconds", "width", "height", "videoFrames"
    };

    /// <summary>某个技能声明的参数清单。未单独声明的（含管理员工作流）只收提示词。</summary>
    public static IReadOnlyList<ChatParameterSpec> Declare(string skillId) => skillId switch
    {
        WebImageSkillId => ImageApi,
        LocalImageSkillId => LocalImage,
        VideoSkillId => Video,
        _ => PromptOnly
    };

    /// <summary>
    /// 视频秒数折成 ComfyUI 视频模板认的**帧数**。
    ///
    /// LTX 的 `video_length` 在节点 schema 里写着 `min = 1, step = 8`，只吃 1、9、17、25…；
    /// 发一个 24 过去不报错，但会被节点**悄悄折到 17**（用户要 1 秒，拿到 0.71 秒）。
    /// 所以这里向上取到下一个 1+8k：宁可略长，也不要短给——折法与网页端节点菜单同一套
    /// （见 `videoParams.ts` 的 `videoFramesForSeconds`），两处结果必须一致。
    /// </summary>
    public static int VideoFramesForSeconds(int seconds)
    {
        var target = Math.Max(1, Math.Min(seconds, 600)) * 24.0;
        return 1 + (int)Math.Ceiling((target - 1) / 8.0) * 8;
    }

    /// <summary>
    /// 这个参数在聊天卡片上有没有可编辑的控件。
    ///
    /// 用来区分两种失败：有控件（用户改一下就能过）与没控件（模型自己编的，改什么都没用）。
    /// 前者应当把卡片留在可重试状态，后者只能整条挡住——否则界面会挂着一个「怎么点都失败」的按钮。
    /// </summary>
    public static bool IsEditable(string skillId, string? name) =>
        name is not null && Declare(skillId).Any(spec => spec.Name == name && spec.Editable);

    /// <summary>给界面与模型看的声明投影。</summary>
    public static object[] Describe(string skillId) => Declare(skillId).Select(spec => (object)new
    {
        name = spec.Name,
        kind = spec.Kind.ToString().ToLowerInvariant(),
        required = spec.Required,
        description = spec.Description,
        options = spec.Options,
        editable = spec.Editable,
        minimum = spec.Minimum,
        maximum = spec.Maximum
    }).ToArray();

    /// <summary>
    /// 校验一批参数。
    /// </summary>
    /// <param name="skillId">目标技能。</param>
    /// <param name="supplied">调用方提交的参数（只校验这里出现的键）。</param>
    /// <param name="satisfied">由既有字段已经满足的必填项（例如 prompt 来自 content）。</param>
    /// <param name="accepted">校验通过的参数（原样保留，交给执行层）。</param>
    /// <param name="errorCode">失败时的稳定错误码。</param>
    /// <param name="errorMessage">失败时的用户可读提示。</param>
    /// <param name="errorParameter">出错的那个参数名（没有就是 null）。调用方据此判断「用户能不能在界面上改好」。</param>
    /// <returns>全部通过返回 true。</returns>
    public static bool TryValidate(
        string skillId,
        IReadOnlyDictionary<string, JsonElement> supplied,
        IReadOnlyCollection<string> satisfied,
        out Dictionary<string, JsonElement> accepted,
        out string? errorCode,
        out string? errorMessage,
        out string? errorParameter) =>
        TryValidate(Declare(skillId), supplied, satisfied, out accepted, out errorCode, out errorMessage, out errorParameter);

    /// <summary>
    /// 按一份给定的声明校验参数。
    ///
    /// 与上面那个重载是**同一份实现**：技能 id 只是「取哪份声明的」这一层便利，
    /// 校验规则本身不因调用方式而分叉（分成两份实现迟早会把错误码或边界对不上）。
    /// </summary>
    public static bool TryValidate(
        IReadOnlyList<ChatParameterSpec> specs,
        IReadOnlyDictionary<string, JsonElement> supplied,
        IReadOnlyCollection<string> satisfied,
        out Dictionary<string, JsonElement> accepted,
        out string? errorCode,
        out string? errorMessage,
        out string? errorParameter)
    {
        accepted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        errorCode = null;
        errorMessage = null;
        errorParameter = null;
        var byName = specs.ToDictionary(spec => spec.Name, StringComparer.Ordinal);
        foreach (var pair in supplied)
        {
            if (!byName.TryGetValue(pair.Key, out var spec))
            {
                errorCode = KnownNames.Contains(pair.Key) ? "SKILL_INPUT_UNSUPPORTED" : "SKILL_PARAM_UNKNOWN";
                errorMessage = errorCode == "SKILL_INPUT_UNSUPPORTED"
                    ? $"该技能不支持参数 {pair.Key}"
                    : $"未知参数 {pair.Key}";
                errorParameter = pair.Key;
                return false;
            }
            if (!TryRead(spec, pair.Value, out errorCode, out errorMessage)) { errorParameter = pair.Key; return false; }
            accepted[pair.Key] = pair.Value.Clone();
        }
        foreach (var spec in specs)
        {
            if (!spec.Required || supplied.ContainsKey(spec.Name) || satisfied.Contains(spec.Name)) continue;
            errorCode = "SKILL_PARAM_REQUIRED";
            errorMessage = $"缺少必填参数 {spec.Name}（{spec.Description}）";
            errorParameter = spec.Name;
            return false;
        }
        return true;
    }

    private static bool TryRead(ChatParameterSpec spec, JsonElement value, out string? errorCode, out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;
        switch (spec.Kind)
        {
            case ChatParameterKind.String:
                if (value.ValueKind != JsonValueKind.String) return TypeError(spec, out errorCode, out errorMessage);
                if (spec.Maximum is { } length && value.GetString()!.Length > length)
                {
                    errorCode = "SKILL_PARAM_INVALID";
                    errorMessage = $"参数 {spec.Name} 最多 {length} 字";
                    return false;
                }
                return true;
            case ChatParameterKind.Number:
                // 数字参数当前都是整数语义（秒 / 宽 / 高 / 帧数 / 种子）：小数按类型不符拒绝，
                // 不做静默取整——把 3.7 悄悄变成 3 就是替用户改了他给的值。
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
                    || number < (spec.Minimum ?? double.MinValue) || number > (spec.Maximum ?? double.MaxValue))
                    return TypeError(spec, out errorCode, out errorMessage);
                return true;
            case ChatParameterKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return TypeError(spec, out errorCode, out errorMessage);
                return true;
            case ChatParameterKind.Enum:
                if (value.ValueKind != JsonValueKind.String) return TypeError(spec, out errorCode, out errorMessage);
                if (!spec.Options.Contains(value.GetString()!, StringComparer.Ordinal))
                {
                    errorCode = "SKILL_PARAM_INVALID";
                    errorMessage = $"参数 {spec.Name} 只能是 {string.Join("、", spec.Options)}";
                    return false;
                }
                return true;
            case ChatParameterKind.Array:
                if (value.ValueKind != JsonValueKind.Array) return TypeError(spec, out errorCode, out errorMessage);
                if (value.GetArrayLength() > MaxArrayItems)
                {
                    errorCode = "SKILL_PARAM_INVALID";
                    errorMessage = $"参数 {spec.Name} 最多 {MaxArrayItems} 项";
                    return false;
                }
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                        || item.GetString()!.Length > MaxArrayItemLength)
                    {
                        errorCode = "SKILL_PARAM_INVALID";
                        errorMessage = $"参数 {spec.Name} 的每一项都必须是 1-{MaxArrayItemLength} 字的字符串";
                        return false;
                    }
                }
                return true;
            default:
                if (value.ValueKind != JsonValueKind.Object) return TypeError(spec, out errorCode, out errorMessage);
                var count = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++count > MaxObjectProperties)
                    {
                        errorCode = "SKILL_PARAM_INVALID";
                        errorMessage = $"参数 {spec.Name} 最多 {MaxObjectProperties} 个字段";
                        return false;
                    }
                    // 只收一层：嵌套结构无法逐项校验，收下去等于把校验责任推给执行层。
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
                        or JsonValueKind.True or JsonValueKind.False))
                    {
                        errorCode = "SKILL_PARAM_INVALID";
                        errorMessage = $"参数 {spec.Name} 的字段 {property.Name} 只能是字符串、数字或布尔值";
                        return false;
                    }
                }
                return true;
        }
    }

    private static bool TypeError(ChatParameterSpec spec, out string? errorCode, out string? errorMessage)
    {
        errorCode = "SKILL_PARAM_TYPE";
        errorMessage = spec.Kind switch
        {
            ChatParameterKind.Number => $"参数 {spec.Name} 必须是数字，且在 {spec.Minimum} 到 {spec.Maximum} 之间",
            ChatParameterKind.Boolean => $"参数 {spec.Name} 必须是布尔值",
            ChatParameterKind.Enum => $"参数 {spec.Name} 必须是字符串",
            ChatParameterKind.Array => $"参数 {spec.Name} 必须是字符串数组",
            ChatParameterKind.Object => $"参数 {spec.Name} 必须是对象",
            _ => $"参数 {spec.Name} 必须是字符串"
        };
        return false;
    }

    /// <summary>把声明摊成给模型看的参数清单。</summary>
    internal static string DescribeForModel(string skillId)
    {
        var specs = Declare(skillId);
        if (specs.Count == 0) return string.Empty;
        return string.Join("；", specs.Select(spec =>
        {
            var type = spec.Kind switch
            {
                ChatParameterKind.Enum => "枚举：" + string.Join("/", spec.Options),
                ChatParameterKind.Array => "字符串数组",
                ChatParameterKind.Object => "对象（字段只能是字符串/数字/布尔）",
                ChatParameterKind.Number => $"数字（{spec.Minimum}~{spec.Maximum}）",
                ChatParameterKind.Boolean => "布尔",
                _ => "字符串"
            };
            return $"{spec.Name}({type}{(spec.Required ? "，必填" : string.Empty)}，{spec.Description})";
        }));
    }
}
