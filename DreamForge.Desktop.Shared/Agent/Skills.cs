namespace DreamForge.Desktop;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DreamForge.Core;

/// <summary>
/// 技能的一个步骤：一次模型调用。
/// 技能只能用大模型（含图像 / 视频模型）获取新资源，因此步骤里只有提示词、
/// 参考图来源与输出位置，没有可执行代码。
/// </summary>
public sealed class SkillStep
{
    public string Id { get; set; } = "step";

    /// <summary>步骤名，用于进度提示与失败信息。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>要调用的能力，取值来自 Capability 枚举，例如 TextToImage / ImageToImage / TextToVideo。</summary>
    public string Capability { get; set; } = "TextToImage";

    /// <summary>
    /// 本步使用的模型名；留空表示用设置里的默认模型。
    /// 由接口文档生成的池子技能会给每一步指定自己的模型（例如 flux-1-dev / veo-3）。
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>提示词模板，支持 {kind} {name} {variant} {core} {description} {layout} 占位符。</summary>
    public string Prompt { get; set; } = string.Empty;

    public string NegativePrompt { get; set; } = string.Empty;

    /// <summary>
    /// 参考图来源，可用逗号组合多个：
    /// 留空表示文生图；variant 用变体当前参考图；ref:N 用目标上下文的第 N 张参考图；
    /// 其余按前置步骤 id 取该步骤产出。用几张由执行方决定，这里不设上限。
    /// </summary>
    public string ReferenceFrom { get; set; } = string.Empty;

    /// <summary>图生图重绘强度，留空使用服务默认。</summary>
    public double? Denoise { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>产出名称，用于附件命名，例如「正面」。</summary>
    public string OutputName { get; set; } = string.Empty;

    /// <summary>本步的接口执行配置（来源路径 / 方法 / 鉴权）；由接口文档导入时写入（返工 R4）。</summary>
    public SkillEndpoint? Endpoint { get; set; }
}

/// <summary>
/// 一个步骤的接口执行配置（返工 R4）。导入时把**来源、路径、方法、鉴权**一起记进技能，
/// 执行时按它发请求，而不是一律去打 <c>/images/generations</c> 加 Bearer——
/// 否则导入的自定义路径 / PUT / x-api-key 接口永远跑不通，最小测试也证明不了「导入的这个接口」可用。
/// 字段留空表示按能力走默认（默认路径 + 配置里的地址与 Bearer 鉴权）。
/// </summary>
public sealed class SkillEndpoint
{
    /// <summary>接口根地址（含版本前缀，例如 https://host/v1）；留空用设置里的地址。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>请求路径，例如 /v1/images/generations；留空按能力取默认路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>HTTP 方法，默认 POST。</summary>
    public string Method { get; set; } = "POST";

    /// <summary>鉴权方式：bearer（默认）/ x-api-key / query。</summary>
    public string AuthStyle { get; set; } = "bearer";

    public bool IsEmpty =>
        BaseUrl.Length == 0 && Path.Length == 0
        && string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase)
        && (AuthStyle.Length == 0 || string.Equals(AuthStyle, "bearer", StringComparison.OrdinalIgnoreCase));

    /// <summary>给界面看的一行摘要。</summary>
    public string Describe() =>
        $"{Method.ToUpperInvariant()} {(BaseUrl.Length == 0 ? string.Empty : BaseUrl)}{(Path.Length == 0 ? "（默认路径）" : Path)}"
        + $"｜鉴权 {AuthStyle}";
}

/// <summary>技能清单：一个可复用的内容生产流程，由若干模型调用步骤组成。</summary>
public sealed class SkillDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";

    /// <summary>适用的实体种类：Character / Scene / Prop / Any。</summary>
    public string TargetKind { get; set; } = "Any";

    /// <summary>产出写回哪里：variant 写入变体当前内容；node 写入触发节点附件；node-base 写入节点并标记为出图底图。</summary>
    public string OutputTarget { get; set; } = "variant";

    public List<SkillStep> Steps { get; set; } = new();

    /// <summary>
    /// 来源命名空间（返工 R5）：同一个接口文档来源导入的技能共用它，文件名与技能 Id 都带这段前缀，
    /// 于是 A、B 两个来源的同名池子不会互相覆盖，重导时也只需更新自己写过的文件。
    /// 手工创建的技能这里是空串（视为「手写技能」，导入流程一律不碰）。
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>来源文档地址，便于回看这条技能的出处。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>
    /// 完整规范化来源身份（返工 U5），例如 <c>https://api.example.com:8443/v1</c>。
    /// 与 <see cref="SourceId"/> 一起用于覆盖前的归属核对：Id 是给人看的短名，身份是判定依据。
    /// </summary>
    public string SourceIdentity { get; set; } = string.Empty;

    /// <summary>
    /// 本技能历次导入写出的文件清单（更新清单）。减掉池子时只删这份清单里、本次不再产出的文件，
    /// 不动其它来源与手工技能的文件。
    /// </summary>
    public List<string> OwnedFiles { get; set; } = new();

    /// <summary>是否为某个来源导入出来的技能（手写技能不参与导入的覆盖与清理）。</summary>
    public bool IsImported => SourceId.Length > 0;

    /// <summary>
    /// 是否启用。停用的技能**保留配置与文件**，只是不再被调用——技能管理页上的那个开关就是它。
    /// 会写进技能 JSON（只写这一个键，见 <see cref="SkillLibrary.TrySetEnabled"/>），所以重启后记得住。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>这个技能是从哪个文件读出来的（不写进 JSON，装载时补上）。删除与管理都要用它。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 规划态（返工 R4）：技能已按接口文档建好，但当前执行方还不能真跑（例如异步视频链路尚未接入）。
    /// 界面要显示「不可执行」，运行入口要直接拒绝并说明原因——不能让它失败得莫名其妙，更不能标成可用。
    /// </summary>
    public bool IsPlannedOnly { get; set; }

    /// <summary>规划态的原因（为什么现在不能跑）。</summary>
    public string PlannedReason { get; set; } = string.Empty;

    /// <summary>步骤里是否含出图能力：含则运行前必须先有可用的图像链路。</summary>
    public bool NeedsImageProvider =>
        Steps.Any(step => CapabilityOf(step) is Capability.TextToImage or Capability.ImageToImage);

    /// <summary>步骤里是否含出视频能力。</summary>
    public bool NeedsVideoProvider =>
        Steps.Any(step => CapabilityOf(step) is Capability.TextToVideo or Capability.ImageToVideo);

    /// <summary>技能里是否存在执行器不支持的能力（能力名拼错，或用了还没接入的能力）。</summary>
    public bool HasUnsupportedCapability =>
        Steps.Any(step => CapabilityOf(step) is not (Capability.TextToImage or Capability.ImageToImage or Capability.TextToVideo or Capability.ImageToVideo));

    private static Capability? CapabilityOf(SkillStep step) =>
        Enum.TryParse<Capability>(step.Capability, ignoreCase: true, out var capability) ? capability : null;

    /// <summary>运行前的可用性说明；返回 null 表示可以运行。</summary>
    public string? Validate(WorkflowEntity entity)
    {
        if (Steps.Count == 0) return "技能没有定义任何步骤。";
        if (!string.Equals(TargetKind, "Any", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(TargetKind, WorkflowEntity.KindName(entity.Kind), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(TargetKind, entity.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
            return $"该技能只适用于{TargetKind}，当前实体是{WorkflowEntity.KindName(entity.Kind)}。";
        return null;
    }
}

/// <summary>
/// 技能装载：从本机技能目录读取 JSON 清单。装载失败只影响单个技能，
/// 会把错误收集起来交给界面展示，不打断其它技能。
/// </summary>
public static class SkillLibrary
{
    /// <summary>技能目录：默认在项目根文件夹，可用 DREAMFORGE_SKILL_DIR 覆盖。</summary>
    public static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DREAMFORGE_SKILL_DIR");
            return string.IsNullOrWhiteSpace(configured)
                ? AppPaths.Combine("skills")
                : Path.GetFullPath(configured);
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>内置示例技能：首次运行时写入技能目录，让三视图开箱可用。</summary>
    private const string DefaultThreeViewSkill = """
    {
      "id": "character-three-view",
      "name": "人物一键三视图",
      "description": "生成正面、侧面、背面三张全身立绘。后两张以正面图为参考图，保证是同一个人。",
      "version": "1.0.0",
      "targetKind": "Character",
      "outputTarget": "variant",
      "steps": [
        {
          "id": "front",
          "name": "正面立绘",
          "capability": "TextToImage",
          "prompt": "{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}\n正面全身立绘，标准站姿，白底，不要背景元素",
          "negativePrompt": "多人，裁切，模糊，文字水印，多余肢体",
          "width": 832,
          "height": 1216,
          "outputName": "正面"
        },
        {
          "id": "side",
          "name": "侧面立绘",
          "capability": "ImageToImage",
          "referenceFrom": "front",
          "denoise": 0.55,
          "prompt": "同一角色的纯侧面全身立绘，保持脸型、发型与服装完全一致，白底，不要背景元素",
          "negativePrompt": "多人，裁切，模糊，文字水印，换装，换发型",
          "width": 832,
          "height": 1216,
          "outputName": "侧面"
        },
        {
          "id": "back",
          "name": "背面立绘",
          "capability": "ImageToImage",
          "referenceFrom": "front",
          "denoise": 0.55,
          "prompt": "同一角色的纯背面全身立绘，保持发型与服装完全一致，白底，不要背景元素",
          "negativePrompt": "多人，裁切，模糊，文字水印，换装，换发型",
          "width": 832,
          "height": 1216,
          "outputName": "背面"
        }
      ]
    }
    """;

    /// <summary>
    /// 内置示例技能二：把前两张参考图合成到同一画面，产出标记为该镜头的出图底图。
    /// 它演示 ref:N 引用来源与 node-base 产出目标，是「分步合成」的最小示例。
    /// </summary>
    private const string DefaultReferenceMergeSkill = """
    {
      "id": "reference-merge-two",
      "name": "参考图两两合成",
      "description": "把本镜头的前两张参考图合成到同一画面，产出作为该镜头的出图底图。",
      "version": "1.0.0",
      "targetKind": "Any",
      "outputTarget": "node-base",
      "steps": [
        {
          "id": "merge",
          "name": "合成前两张参考图",
          "capability": "ImageToImage",
          "referenceFrom": "ref:0,ref:1",
          "denoise": 0.55,
          "prompt": "把两张参考图里的主体合成到同一画面：保持各自的外形、颜色与比例不变，构图自然、无遮挡冲突，不要添加图中没有的元素。",
          "negativePrompt": "多余肢体，重复主体，模糊，文字水印",
          "outputName": "合成"
        }
      ]
    }
    """;

    /// <summary>技能目录为空时写入内置示例，不覆盖用户已有的任何文件。</summary>
    public static void EnsureDefaultSkills()
    {
        try
        {
            var directory = EnsureDirectory();
            var hasAny = System.IO.Directory.EnumerateFiles(directory, "*.json").Any();
            if (!hasAny)
            {
                File.WriteAllText(Path.Combine(directory, "character-three-view.json"), DefaultThreeViewSkill);
                File.WriteAllText(Path.Combine(directory, "reference-merge-two.json"), DefaultReferenceMergeSkill);
                return;
            }
            // 已有技能时只补缺失的内置示例，不动用户文件。
            var mergePath = Path.Combine(directory, "reference-merge-two.json");
            if (!File.Exists(mergePath)) File.WriteAllText(mergePath, DefaultReferenceMergeSkill);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 技能目录不可写时不影响主流程，只是没有可用技能。
        }
    }

    /// <summary>读取技能目录下全部清单；单个文件出错只记录错误并跳过。</summary>
    public static (List<SkillDefinition> Skills, List<string> Errors) Load()
    {
        var skills = new List<SkillDefinition>();
        var errors = new List<string>();
        if (!System.IO.Directory.Exists(Directory)) return (skills, errors);

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var skill = JsonSerializer.Deserialize<SkillDefinition>(File.ReadAllText(path), Options);
                if (skill is null || string.IsNullOrWhiteSpace(skill.Id))
                {
                    errors.Add($"{Path.GetFileName(path)}：缺少 id");
                    continue;
                }
                if (skill.Steps.Count == 0)
                {
                    errors.Add($"{Path.GetFileName(path)}：没有定义步骤");
                    continue;
                }
                skill.FilePath = path;   // 管理与删除都要知道它从哪个文件来
                skills.Add(skill);
            }
            catch (JsonException error)
            {
                errors.Add($"{Path.GetFileName(path)}：{error.Message}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}：{error.Message}");
            }
        }
        return (skills, errors);
    }

    /// <summary>
    /// 切换某个技能的启用状态并落盘。
    ///
    /// 只改 JSON 里的 <c>Enabled</c> 这一个键（用 <see cref="JsonNode"/> 而不是整体反序列化再写回）：
    /// 技能文件可能带有我们这一版不认识的字段，整体重写会把它们抹掉——那是静悄悄的数据损失。
    /// </summary>
    public static bool TrySetEnabled(SkillDefinition skill, bool enabled, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(skill);
        if (string.IsNullOrWhiteSpace(skill.FilePath) || !File.Exists(skill.FilePath))
        {
            error = "找不到这个技能的文件（可能已被移动或删除）。";
            return false;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(skill.FilePath)) is not JsonObject node)
            {
                error = "技能文件不是一个 JSON 对象，无法改写。";
                return false;
            }
            node["Enabled"] = enabled;
            // Encoder 必须显式放宽：默认编码器会把中文全部转成 \uXXXX，技能名字、提示词、描述全变码点，
            // 文件从此没法读也没法 diff——技能 JSON 本来就是给人改的。
            File.WriteAllText(skill.FilePath, node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            }));
            skill.Enabled = enabled;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// 删除一个技能文件。**只删这一个文件**：导入来源写出的其它文件（<see cref="SkillDefinition.OwnedFiles"/>）
    /// 可能被同来源的其它技能共用，顺手删掉会把它们一起弄坏。
    /// </summary>
    public static bool TryDelete(SkillDefinition skill, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(skill);
        if (string.IsNullOrWhiteSpace(skill.FilePath) || !File.Exists(skill.FilePath))
        {
            error = "找不到这个技能的文件（可能已被移动或删除）。";
            return false;
        }

        try
        {
            File.Delete(skill.FilePath);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = failure.Message;
            return false;
        }
    }
}

/// <summary>技能运行的目标：工作树资源的一条实体 + 变体，以及可选的触发节点。</summary>
public sealed class SkillTarget
{
    /// <summary>画布状态：节点级技能（例如参考图合成）需要它来解析节点的引用。</summary>
    public WorkflowCanvasState? Canvas { get; init; }

    public WorkflowEntity? Entity { get; init; }
    public WorkflowEntityVariant? Variant { get; init; }
    public WorkflowNode? Node { get; init; }

    /// <summary>变体作用域下的实体名，供提示词占位符使用。</summary>
    public string EntityName => Entity?.Name ?? string.Empty;

    public string VariantName => Variant?.Name ?? string.Empty;

    public string Core => Entity?.Core ?? string.Empty;

    public string Description => Variant?.Description ?? string.Empty;

    public SceneLayout? Layout => Variant?.Layout;
}

public sealed class SkillRunResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<WorkflowAttachment> Produced { get; init; } = new();
}

/// <summary>
/// 技能执行器：按步骤顺序调用出图 / 出视频能力，把每一步的产出作为下一步可能的参考图。
/// 任何一步失败都不会写回任何产出，避免半成品混进变体参考图。
/// </summary>
public static class SkillRunner
{
    public static async Task<SkillRunResult> RunAsync(
        SkillDefinition skill,
        SkillTarget target,
        IImageProvider provider,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        IVideoProvider? videoProvider = null)
    {
        // 规划态技能（例如异步视频链路还没接执行方）：直接拒绝并说明原因，
        // 不让它跑到一半报一个莫名其妙的错误，也不让它看起来可用（返工 R4）。
        if (skill.IsPlannedOnly)
            return new SkillRunResult
            {
                Succeeded = false,
                Message = $"技能「{skill.Name}」目前不可执行："
                    + (skill.PlannedReason.Length == 0 ? "该接口的执行方尚未接入。" : skill.PlannedReason)
            };

        // 只有含出图步骤的技能才要求图像链路：纯出视频技能不该因为没配出图模型就跑不起来。
        if (skill.NeedsImageProvider && !provider.IsConfigured)
            return new SkillRunResult { Succeeded = false, Message = "尚未配置图像模型，无法运行技能。" };

        var scope = (skill.OutputTarget ?? "variant").Trim();
        var toNode = scope.StartsWith("node", StringComparison.OrdinalIgnoreCase);
        var markAsBaseImage = string.Equals(scope, "node-base", StringComparison.OrdinalIgnoreCase);
        if (toNode && target.Node is null)
            return new SkillRunResult { Succeeded = false, Message = "该技能需要作用在画布节点上，但当前没有指定节点。" };
        if (!toNode && target.Variant is null)
            return new SkillRunResult { Succeeded = false, Message = "该技能需要作用在工作树资源的变体上，但当前没有指定变体。" };

        var produced = new Dictionary<string, WorkflowAttachment>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < skill.Steps.Count; index++)
        {
            var step = skill.Steps[index];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"[{index + 1}/{skill.Steps.Count}] {step.Name}");

            if (!Enum.TryParse<Capability>(step.Capability, ignoreCase: true, out var capability))
                return new SkillRunResult { Succeeded = false, Message = $"步骤「{step.Name}」的能力名 {step.Capability} 无法识别。" };
            if (capability is not (Capability.TextToImage or Capability.ImageToImage or Capability.TextToVideo or Capability.ImageToVideo))
                return new SkillRunResult { Succeeded = false, Message = $"步骤「{step.Name}」使用了不支持的能力 {capability}。技能目前只能调用出图与出视频能力。" };

            var isVideo = capability is Capability.TextToVideo or Capability.ImageToVideo;
            var needsReference = capability is Capability.ImageToImage or Capability.ImageToVideo;
            if (isVideo && (videoProvider is null || !videoProvider.IsConfigured))
                return new SkillRunResult
                {
                    Succeeded = false,
                    Message = $"步骤「{step.Name}」需要出视频链路，但当前没有可用的出视频实现：技能已保存，接入出视频执行方后即可直接运行。"
                };

            var referencePaths = ResolveReferences(step, target, produced);
            if (needsReference && referencePaths.Count == 0)
                return new SkillRunResult { Succeeded = false, Message = $"步骤「{step.Name}」需要参考图，但没有找到可用的参考图。" };

            var config = AiProviderSettings.Load();
            var (defaultWidth, defaultHeight) = ParseSize(config.ImageSize);
            var prompt = SkillTemplates.Render(step.Prompt, target);
            var negativePrompt = SkillTemplates.Render(step.NegativePrompt, target);
            string filePath;
            if (isVideo)
            {
                var videoRequest = new VideoGenerationRequest
                {
                    Prompt = prompt,
                    NegativePrompt = negativePrompt,
                    Model = step.Model,
                    Width = step.Width is > 0 ? step.Width.Value : 0,
                    Height = step.Height is > 0 ? step.Height.Value : 0,
                    Seconds = config.VideoDefaultSeconds,
                    ReferenceImages = referencePaths
                };
                var videoResult = await videoProvider!.GenerateAsync(videoRequest, cancellationToken).ConfigureAwait(false);
                if (videoResult.Status != VideoGenerationStatus.Succeeded)
                    return new SkillRunResult { Succeeded = false, Message = $"步骤「{step.Name}」失败：{videoResult.Error}" };
                filePath = videoResult.FilePath;
            }
            else
            {
                var request = new ImageGenerationRequest
                {
                    Prompt = prompt,
                    NegativePrompt = negativePrompt,
                    Model = step.Model,
                    // 技能自带的执行配置（返工 R4）：来源、路径、方法、鉴权逐步骤生效。
                    BaseUrl = step.Endpoint?.BaseUrl ?? string.Empty,
                    EndpointPath = step.Endpoint?.Path ?? string.Empty,
                    Method = step.Endpoint?.Method ?? string.Empty,
                    AuthStyle = step.Endpoint?.AuthStyle ?? string.Empty,
                    Width = step.Width is > 0 ? step.Width.Value : defaultWidth,
                    Height = step.Height is > 0 ? step.Height.Value : defaultHeight,
                    ReferenceImages = needsReference ? referencePaths : Array.Empty<string>(),
                    Denoise = step.Denoise
                };
                var result = await provider.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.Status != ImageGenerationStatus.Succeeded)
                    return new SkillRunResult { Succeeded = false, Message = $"步骤「{step.Name}」失败：{result.Error}" };
                filePath = result.FilePath;
            }

            var name = string.IsNullOrWhiteSpace(step.OutputName) ? step.Name : step.OutputName;
            var attachment = new WorkflowAttachment
            {
                Kind = isVideo ? AttachmentKind.Video : AttachmentKind.Image,
                Reference = AssetStore.ToReference(filePath),
                Name = $"{skill.Name}-{name}{Path.GetExtension(filePath)}",
                Source = $"技能 {skill.Name}",
                // 技能这条路（Agent 驱动）也把实际发出去的提示词存下来：
                // 不然过一阵想知道这张图当初怎么来的，只能翻对话记录。
                Prompt = prompt,
                NegativePrompt = negativePrompt
            };
            produced[string.IsNullOrWhiteSpace(step.Id) ? step.Name : step.Id] = attachment;
        }

        // 全部步骤成功后才写回，任一步失败都不会留下半成品。
        var list = produced.Values.ToList();
        if (markAsBaseImage && list.Count > 0)
        {
            // 最后一步的产出就是合成结果：打上标记，出图时优先作为底图。
            list[^1].Source = WorkflowAttachment.SourceComposition;
            list[^1].Name = $"{skill.Name}·{target.Node?.Title ?? string.Empty}{Path.GetExtension(list[^1].Reference)}";
        }
        var sink = toNode ? target.Node!.Attachments : target.Variant!.Attachments;
        foreach (var attachment in list) sink.Add(attachment);
        return new SkillRunResult
        {
            Succeeded = true,
            Produced = list,
            Message = $"技能「{skill.Name}」完成，生成 {DescribeProduced(list)}。"
        };
    }

    /// <summary>按产出类型报数：出图技能说「张图」，出视频技能说「个视频」，混合技能两样都报。</summary>
    private static string DescribeProduced(IReadOnlyList<WorkflowAttachment> produced)
    {
        var images = produced.Count(attachment => attachment.Kind == AttachmentKind.Image);
        var videos = produced.Count(attachment => attachment.Kind == AttachmentKind.Video);
        var parts = new List<string>();
        if (images > 0) parts.Add($"{images} 张图");
        if (videos > 0) parts.Add($"{videos} 个视频");
        return parts.Count == 0 ? $"{produced.Count} 个产物" : string.Join("、", parts);
    }

    /// <summary>
    /// 解析步骤的参考图路径，支持三类来源（可用逗号组合）：
    /// variant 取变体当前参考图；ref:N 取目标上下文的第 N 张参考图；
    /// 其余按前置步骤 id 取该步骤产出。这里不做数量截断，用几张由执行方决定。
    /// </summary>
    private static List<string> ResolveReferences(SkillStep step, SkillTarget target, IReadOnlyDictionary<string, WorkflowAttachment> produced)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(step.ReferenceFrom)) return paths;
        var ordered = TargetReferenceImages(target);
        foreach (var source in step.ReferenceFrom.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(source, "variant", StringComparison.OrdinalIgnoreCase))
            {
                paths.AddRange(VariantImages(target));
                continue;
            }
            if (source.StartsWith("ref:", StringComparison.OrdinalIgnoreCase) && int.TryParse(source[4..], out var referenceIndex))
            {
                if (referenceIndex >= 0 && referenceIndex < ordered.Count) paths.Add(ordered[referenceIndex]);
                continue;
            }
            if (produced.TryGetValue(source, out var producedAttachment))
            {
                var resolved = AssetStore.Resolve(producedAttachment.Reference);
                if (resolved is not null) paths.Add(resolved);
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 目标上下文里按顺序排列的参考图：节点目标按引用顺序取每条引用的参考图，
    /// 变体目标取变体自身的参考图。ref:N 的下标就是这个顺序。
    /// </summary>
    private static List<string> TargetReferenceImages(SkillTarget target)
    {
        if (target.Node is { } node && target.Canvas is { } canvas)
            return canvas.ResolveReferences(node)
                .SelectMany(reference => reference.Attachments)
                .Where(attachment => attachment.Kind == AttachmentKind.Image)
                .Select(attachment => AssetStore.Resolve(attachment.Reference))
                .Where(path => path is not null)
                .Select(path => path!)
                .ToList();
        return VariantImages(target);
    }

    private static List<string> VariantImages(SkillTarget target)
    {
        var attachments = (target.Variant?.Attachments ?? new List<WorkflowAttachment>())
            .Concat(target.Node?.Attachments ?? new List<WorkflowAttachment>())
            .Where(attachment => attachment.Kind == AttachmentKind.Image);
        return attachments
            .Select(attachment => AssetStore.Resolve(attachment.Reference))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();
    }

    private static (int Width, int Height) ParseSize(string? size)
    {
        if (!string.IsNullOrWhiteSpace(size))
        {
            var parts = size.Split('x', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
                return (Math.Clamp(width, 64, 2048), Math.Clamp(height, 64, 2048));
        }
        return (1024, 1024);
    }
}

/// <summary>技能提示词模板的占位符替换。</summary>
public static class SkillTemplates
{
    public static string Render(string? template, SkillTarget target)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        var text = template
            .Replace("{kind}", target.Entity is { } entity ? WorkflowEntity.KindName(entity.Kind) : string.Empty)
            .Replace("{name}", target.EntityName)
            .Replace("{variant}", target.VariantName)
            .Replace("{core}", target.Core)
            .Replace("{description}", target.Description)
            .Replace("{layout}", target.Layout?.ToPromptText() ?? string.Empty);
        // 占位符为空时会留下空行，这里折掉，避免提示词里出现空白段落。
        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0));
    }
}
