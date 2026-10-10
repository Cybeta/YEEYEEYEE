namespace YEEYEEYEE.Desktop;

/// <summary>面向角色、道具、场景和分镜的生产动作，不绑定任何具体模型或 Provider。</summary>
public enum AssetGenerationIntent
{
    CharacterTurnaround,
    CharacterNineView,
    CharacterExplorationSheet,
    CharacterDetailSheet,
    PropTurnaround,
    PropExplorationSheet,
    PropMaterialDetail,
    SceneBaseKeyframe,
    SceneMultiView,
    SceneDistanceSheet,
    SceneMoodKeyframe,
    StoryboardFirstFrame,
    StoryboardVideo,
    StoryboardOneClick
}

/// <summary>角色九视图的固定九格合同，供提示词和请求元数据共同复用。</summary>
public static class CharacterNineViewTemplate
{
    public const string TemplateId = "character-nine-view-v1";

    public static IReadOnlyList<string> Slots { get; } =
    [
        "第1格：全身正面",
        "第2格：全身左侧面",
        "第3格：全身右侧面",
        "第4格：全身背面",
        "第5格：正面半身",
        "第6格：左侧半身",
        "第7格：右侧半身",
        "第8格：正脸表情、发型细节",
        "第9格：服装、鞋子、关键道具细节"
    ];

    public const string LockedDefaults = "固定3×3九格布局；同一角色身份、服装、发型、配色和关键道具锁定；每格独立、整齐分隔、无文字水印。";

    public static string BuildPromptPrefix() =>
        $"角色九视图固定模板（TemplateId={TemplateId}，模板已锁定）：{LockedDefaults}\n"
        + string.Join("；", Slots);

    public static string BuildNegativePrompt() =>
        "不要改变角色身份、服装、发型、配色或关键道具；不要交换格位；不要缺格、重复格、串格、视图粘连、不同格不同人、文字、水印、logo。";
}

/// <summary>执行方必须真实具备的生成能力。它描述接口/工作流能力，不描述业务动作。</summary>
public enum GenerationCapability
{
    TextToImage,
    ImageToImage,
    MultiReferenceImage,
    ImageSet,
    TextToVideo,
    ImageToVideo,
    FirstLastFrameToVideo,
    VideoReference,
    ControlNet,
    PoseControl,
    DepthControl,
    IpAdapter,
    BatchOutput
}

public static class GenerationCapabilityMatching
{
    public static bool SupportsAll(
        this IReadOnlySet<GenerationCapability> available,
        IEnumerable<GenerationCapability>? required)
    {
        if (required is null) return true;
        return required.All(available.Contains);
    }

    public static string MissingDescription(
        this IReadOnlySet<GenerationCapability> available,
        IEnumerable<GenerationCapability>? required)
    {
        if (required is null) return string.Empty;
        var missing = required.Where(capability => !available.Contains(capability)).ToArray();
        return missing.Length == 0
            ? string.Empty
            : $"当前执行方缺少能力：{string.Join("、", missing.Select(DisplayName))}";
    }

    public static string DisplayName(GenerationCapability capability) => capability switch
    {
        GenerationCapability.TextToImage => "文生图",
        GenerationCapability.ImageToImage => "图生图",
        GenerationCapability.MultiReferenceImage => "多参考图",
        GenerationCapability.ImageSet => "参考板/组图输出",
        GenerationCapability.TextToVideo => "文生视频",
        GenerationCapability.ImageToVideo => "图生视频",
        GenerationCapability.FirstLastFrameToVideo => "首尾帧生视频",
        GenerationCapability.VideoReference => "视频参考",
        GenerationCapability.ControlNet => "ControlNet",
        GenerationCapability.PoseControl => "姿态控制",
        GenerationCapability.DepthControl => "深度控制",
        GenerationCapability.IpAdapter => "IP-Adapter",
        GenerationCapability.BatchOutput => "批量输出",
        _ => capability.ToString()
    };
}
