using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace YEEYEEYEE.Desktop;

public sealed class AiProviderConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;

    /// <summary>文本接口的协议格式：OpenAI 兼容的 chat/completions，或 Anthropic 兼容的 /v1/messages。</summary>
    public AiApiFormat ApiFormat { get; set; } = AiApiFormat.OpenAiChat;

    /// <summary><see cref="Endpoint"/> 是否已经是完整请求地址；为 false 时按 <see cref="ApiFormat"/> 自动补路径。</summary>
    public bool UseFullUrl { get; set; }

    /// <summary>界面与状态栏展示用的名字；留空时直接展示模型 ID。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>模型单次请求可接收的最大 token 数；0 表示未声明，此时不按窗口截断上下文。</summary>
    public int ContextWindow { get; set; }

    /// <summary>模型单次响应可生成的最大 token 数；0 表示不发送 max_tokens，由服务端用默认值。</summary>
    public int MaxOutputTokens { get; set; }

    /// <summary>
    /// 是否发送 temperature 这类采样参数。部分模型（如 Kimi 系列）把采样参数固定在服务端，
    /// 显式传入会直接报错，因此必须能关掉。
    /// </summary>
    public bool SendSamplingParameters { get; set; } = true;

    /// <summary>
    /// 模型是否支持图片输入（多模态）。不支持的模型传图会被接口直接拒绝（DeepSeek 返回 400），
    /// 所以在发送前先拦住并告诉用户怎么改，而不是把服务端的报错原样抛出来。
    /// </summary>
    public bool SupportsImageInput { get; set; }

    public string ImageEndpoint { get; set; } = string.Empty;
    public string ImageModel { get; set; } = string.Empty;

    /// <summary>
    /// 图像接口自己的密钥。**留空时沿用当前选中模型的密钥**。
    /// 出图这条链路跟"聊天用哪家模型"没有关系（ComfyUI 是本地地址、视频常常是另一家云），
    /// 所以它必须能独立配置，否则「聊天用 A 家、出图用 B 家」根本做不到。
    /// </summary>
    public string ImageApiKey { get; set; } = string.Empty;

    public string ImageSize { get; set; } = "1024x1024";
    public string ComfyUiBaseUrl { get; set; } = string.Empty;
    public string ComfyUiCheckpoint { get; set; } = string.Empty;
    public string ComfyUiClientId { get; set; } = "yeeeyee-desktop";
    public string AssetDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 画视频接口地址（选填）。留空则复用 <see cref="Endpoint"/>，与图像接口的写法一致。
    /// 视频接口通常是异步任务：提交后轮询取结果，因此未配置时只生成任务规格，不伪造视频结果。
    /// </summary>
    public string VideoEndpoint { get; set; } = string.Empty;

    /// <summary>视频模型名称；留空表示未启用视频链路。</summary>
    public string VideoModel { get; set; } = string.Empty;

    /// <summary>视频接口自己的密钥；留空时沿用当前选中模型的密钥（与 <see cref="ImageApiKey"/> 同理）。</summary>
    public string VideoApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 上一次出图用的那个池子（站点 Id + 模型 + 档位）。
    ///
    /// 为什么记在配置里而不是只在会话内：多数人的出图是**同一家、同一个模型**反复用，
    /// 每次都要在下拉里重挑一遍纯属浪费。记的是「印记」而不是价格或接口地址——
    /// 池子的真实参数（路径、单价、吃不吃参考图）始终以站点文件为准，这里只是用来找回它。
    /// </summary>
    public string LastImagePoolSiteId { get; set; } = string.Empty;

    public string LastImagePoolModel { get; set; } = string.Empty;

    public string LastImagePoolTier { get; set; } = string.Empty;

    /// <summary>
    /// 上一次出图用的那份 **ComfyUI 工作流**（站点 Id + 服务器上的相对路径）。
    ///
    /// 与池子那份印记分开存：两种来源是并列的两条路，共用一组字段的话，
    /// 「上次用池子」会把它上一次用工作流的记录冲掉，反之也一样——于是预选永远是错的。
    /// 同样只存印记，工作流的真实内容始终以站点里那份正文为准。
    /// </summary>
    public string LastImageWorkflowSiteId { get; set; } = string.Empty;

    public string LastImageWorkflowKey { get; set; } = string.Empty;

    /// <summary>视频接口一次最多能同时使用几张参考帧（0 表示不限制）。</summary>
    public int VideoMaxReferenceImages { get; set; } = 1;

    /// <summary>视频默认时长（秒）；0 表示由服务端默认。</summary>
    public int VideoDefaultSeconds { get; set; }

    /// <summary>AI 自动展开下游节点的轮数；0 表示不限制。</summary>
    public int AutoGenerationRounds { get; set; } = 2;

    /// <summary>图像参数默认值：节点未单独设置时使用。</summary>
    public string DefaultNegativePrompt { get; set; } = string.Empty;
    public string DefaultImageSteps { get; set; } = string.Empty;
    public string DefaultImageCfg { get; set; } = string.Empty;

    /// <summary>界面主题（"Dark" / "Light"）。默认深色。</summary>
    public string Theme { get; set; } = nameof(AppTheme.Dark);

    /// <summary>
    /// 协作服务器（YEEYEEYEE.Web）的基地址，形如 <c>http://127.0.0.1:5000</c>。
    ///
    /// 与 <see cref="Theme"/> 同级：这是这台机器接不接协作，不属于某一份接口配置，
    /// 所以切换「当前选中的那一份」时不会被换掉。留空 = 不接协作，桌面端照旧独立工作——
    /// **没有服务器不是错误状态，是默认状态**。
    /// </summary>
    public string CollaborationServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// 上一次登录用的账号名。**只记名字，不记密码**：密码不落盘、会话只在进程内，
    /// 所以重启要重新登录。真要「开机就是在编辑」，得先把会话安全地存起来，那是另一件事。
    /// </summary>
    public string CollaborationAccount { get; set; } = string.Empty;

    /// <summary>
    /// 「改完自动同步」：改动静默一会儿就自动推给服务端（不需要点「保存修订」）。
    ///
    /// **默认关**。桌面端一直是「要不要落盘由『保存修订』决定」，那是刻意的设计；
    /// 自动同步把落盘时机拿走了，所以只能由用户显式打开，不该悄悄改掉默认行为。
    /// </summary>
    public bool CollaborationAutoSync { get; set; }

    /// <summary>
    /// 出图「开奖」：一批图出完后不直接把缩略图铺在画布上，而是留一排**背面朝上**的卡，
    /// 由用户点开，再走一段全屏揭晓（自有形象许愿 → 光点飞入 → 卡片依次翻面），挑一张收进节点。
    ///
    /// 它存在文档顶层、与 <see cref="Theme"/> 同级：这是**界面观感偏好**，不属于某一份接口配置，
    /// 所以切换「当前选中的那一份」时不会被换掉（主题就在这个位置，行为保持一致）。
    ///
    /// 为什么默认关：它改的是出图后的主要交互路径，而老用户升级上来时预期是「和原来一样」。
    /// 想用的人自己去设置里打开，看到的就是他选的。
    /// </summary>
    public bool GachaReveal { get; set; }

    /// <summary>
    /// 出图之后**让模型看一眼、给这一批排个名次**，名次换成档位（金 / 红 / 紫 / 蓝 / 白），
    /// 开奖时按档位出不同的效果。
    ///
    /// **默认关，因为它要额外花一次模型调用**（一批 6 张图要连图带问题发一次，费用与耗时都由用户承担）。
    /// 只在开奖开关也打开、且这个模型开了「支持图片输入」时才有意义——三者缺一，界面上会说明缺哪一条。
    ///
    /// 档位是**模型在这一批里排的名次**，不是绝对质量：第 1 名金、最后一名白。见 <see cref="QualityJudgement"/>。
    /// </summary>
    public bool JudgeImageQuality { get; set; }

    /// <summary>
    /// 出图后判出**裂纹卡**（踩中负面提示词的那几张）时，自动按同一套参数把这几张重出一遍，
    /// 只在状态栏说一声，**不需要用户操作**。
    ///
    /// 为什么单独一个开关：它是在用户已经点过「出图」之后**再花一次钱**，不是零成本的自动化。
    /// 默认开（这是产品要的行为），但它**只有判档开着时才可能生效**——判档默认关，
    /// 所以没开判档的人不会被它多花一分钱。重出只针对裂纹的那几张，不会把整批推倒重来；
    /// 轮数上限见 <see cref="NodeImageBatch.MaxCrackedRedrawRounds"/>，避免一直命中一直重出。
    /// </summary>
    public bool AutoRedrawCrackedCards { get; set; } = true;

    /// <summary>
    /// 图像接口一次最多能同时使用几张参考图。1 表示只支持单张底图，0 表示不限制。
    /// 这是接口（模型）的能力，不是画布的限制；ComfyUI 链路的上限由工作流模板声明，不使用该值。
    /// </summary>
    public int ImageMaxReferenceImages { get; set; } = 1;

    /// <summary>用户是否已在接入引导里做过选择（含显式选择本地模拟）。未做过时启动会弹出接入引导。</summary>
    public bool ProviderChoiceMade { get; set; }

    /// <summary>用户显式选择了本地模拟：即使填了地址也不走真实模型，避免误以为在用大模型。</summary>
    public bool UseLocalProvider { get; set; }

    /// <summary>
    /// 配置文件里的密钥无法解密（通常是换过系统账户 / 换过机器，或把 Windows 上的配置拷到了 macOS）。
    /// 此时 <see cref="ApiKey"/> 为空，界面必须提示重新填写，而不是让用户以为「密钥没配」。
    /// 不写进配置文件——它描述的是读取结果，不是配置项。
    /// </summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

    /// <summary>
    /// 读到的密钥**是否以明文落盘**（含只加了前缀、其实没加密的兜底情形）。
    /// 当前平台没有可用的系统密钥库、且本机密钥文件也写不出来时为 true；
    /// 界面必须如实提示，不能反过来让用户以为密钥已经被加密保护。
    /// 不写进配置文件——它描述的同样是读取结果。
    /// </summary>
    [JsonIgnore]
    public bool ApiKeyStoredUnencrypted { get; set; }

    /// <summary>
    /// 读取配置文件时，里面的密钥还是明文（旧版本留下的），已在内存里解密为明文，
    /// 并且**已就地自动加密落盘**。界面据此告诉用户「已自动加密」，不写进配置文件。
    /// 注意这是「读取时的事实」，不随保存变化。
    /// </summary>
    [JsonIgnore]
    public bool ApiKeyWasPlaintext { get; set; }

    /// <summary>
    /// Agent 的工作文件夹：Agent 只能把文件写在这个目录内（路径越界会被拒绝），
    /// 目录内的文件清单会作为上下文提供给模型。
    /// </summary>
    public string AgentWorkspace { get; set; } = string.Empty;

    /// <summary>
    /// 保存下来的**多份**接入配置。可以同时启用多份，Agent 面板在启用中的那些里选一份来用；
    /// 顶层那一组字段继续表示「当前选中的那一份」——旧端、旧配置文件、既有代码路径读的都是它，
    /// 所以加多份配置不会把别人的配置读坏（详见 <see cref="AiProviderSettings.ApplyProfile"/>）。
    /// </summary>
    public List<AiProviderProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Agent 当前选中的那一份配置的 Id（必须是一份**已启用**的）。
    /// 指向了已停用 / 已删除的配置时，<see cref="AiProviderSettings.ResolveSelected"/> 会自动改到另一个启用中的，
    /// 一份都没启用时置空并回落本地模拟。
    /// </summary>
    public string SelectedProfileId { get; set; } = string.Empty;

    /// <summary>
    /// 被停用的**内置**技能 Id（见 <see cref="BuiltInSkills"/>）。
    /// 内置技能是代码里的静态表，只能在配置里记一份"停用名单"，而不是去改代码。
    /// </summary>
    public List<string> DisabledBuiltInSkills { get; set; } = new();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// 实际请求地址。地址栏填的是基础地址时，按接口格式补路径；
    /// 打开「完整 URL」时则完全按用户填的地址请求（有些网关的路径与官方不一致）。
    /// </summary>
    public string RequestUrl
    {
        get
        {
            var endpoint = Endpoint.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
            if (UseFullUrl) return endpoint;
            return ApiFormat switch
            {
                AiApiFormat.AnthropicMessages => $"{endpoint}/v1/messages",
                _ => $"{endpoint}/chat/completions"
            };
        }
    }

    /// <summary>
    /// 按上下文窗口估算出来的上下文字符预算；0 表示未声明窗口、不截断。
    /// 换算按中文「1 token ≈ 1.5 字符」的保守比例，并且**只把窗口的四分之一留给画布上下文**，
    /// 其余留给用户输入、历史轮次与模型回复——宁可少给一点，也不要因为超窗被服务端拒绝。
    /// </summary>
    public int ContextCharacterBudget =>
        ContextWindow <= 0 ? 0 : (int)Math.Min(ContextWindow * 1.5 / 4, 300_000);

    public bool IsImageConfigured =>
        !string.IsNullOrWhiteSpace(ImageModel) && !string.IsNullOrWhiteSpace(EffectiveImageEndpoint);

    public bool IsComfyUiConfigured =>
        !string.IsNullOrWhiteSpace(ComfyUiBaseUrl) && !string.IsNullOrWhiteSpace(ComfyUiCheckpoint);

    public string EffectiveImageEndpoint =>
        string.IsNullOrWhiteSpace(ImageEndpoint) ? Endpoint : ImageEndpoint;

    /// <summary>画视频接口是否已配置：要有模型名，并且能解析出地址（自填的视频地址或复用的文本地址）。</summary>
    public bool IsVideoConfigured =>
        !string.IsNullOrWhiteSpace(VideoModel) && !string.IsNullOrWhiteSpace(EffectiveVideoEndpoint);

    public string EffectiveVideoEndpoint =>
        string.IsNullOrWhiteSpace(VideoEndpoint) ? Endpoint : VideoEndpoint;
}

/// <summary>
/// 一份保存下来的模型接入配置。
///
/// 与顶层那一组同名字段的关系：**顶层 =「当前选中的那一份」的投影**。
/// 这样旧端（只认顶层字段）与既有代码路径（<c>AiProviderFactory.Create()</c> 读 <see cref="AiProviderSettings.Load"/>）
/// 一行都不用改，而新界面能同时存多份、同时启用多份。
/// </summary>
public sealed class AiProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>界面与对话里显示的名字；留空时退到模型 ID。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;
    public bool UseFullUrl { get; set; }
    public AiApiFormat ApiFormat { get; set; } = AiApiFormat.OpenAiChat;
    public string Model { get; set; } = string.Empty;

    /// <summary>内存里是明文、盘上是密文（与顶层 <see cref="AiProviderConfig.ApiKey"/> 同一套处理）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.7;
    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }
    public bool SendSamplingParameters { get; set; } = true;
    public bool SupportsImageInput { get; set; }

    /// <summary>是否在 Agent 面板的模型选择器里出现。**可以同时启用多份**，停用的只是不进候选。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>这一份的密钥解不开（换过机器 / 换过系统账户）。只在内存里，不入盘。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

    /// <summary>界面上怎么称呼它。</summary>
    [JsonIgnore]
    public string Label => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : !string.IsNullOrWhiteSpace(Model) ? Model : "（未命名）";

    /// <summary>配置是否可用（地址与模型都填了）。</summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);
}

public static class AiProviderSettings
{
    /// <summary>
    /// 全局配置文件路径：所有项目共用一份模型配置。
    /// 可用 YEEYEEYEE_CONFIG 覆盖，便于测试和便携部署。
    /// </summary>
    private static string ConfigPath
    {
        get
        {
            var configured = EnvCompat.Get("CONFIG");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

            // 应用级配置文件写在用户配置目录（旧版本写在程序旁边，会在首次读取时自动搬过去）；
            // 便携部署或自动化测试用 YEEYEEYEE_CONFIG 明确指定一份文件。
            return AppPaths.ResolveAppFile("ai-config.json");
        }
    }

    /// <summary>
    /// 配置文件**路径**（不是内容）。给网页端那条只读的设置接口用：
    /// 它必须**绕开 <see cref="Load"/>**——那个会去解密钥，而解密钥要 DPAPI，只在 Windows 上有。
    /// </summary>
    public static string FilePath => ConfigPath;

    /// <summary>旧配置里的明文密钥只尝试加密保存一次，避免配置目录不可写时每次读取都重复尝试落盘。</summary>
    private static bool migrationAttempted;

    /// <summary>
    /// 写盘格式。
    /// Encoder 必须显式放宽：默认编码器会把中文全部转成 \uXXXX——配置里现在有「配置名字」这类中文，
    /// 转义之后整份文件变成一串码点，用户想自己看一眼、排查一下就没法读了。
    /// </summary>
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static AiProviderConfig Load()
    {
        var config = new AiProviderConfig();
        var onDiskKey = string.Empty;
        try
        {
            if (File.Exists(ConfigPath))
            {
                config = JsonSerializer.Deserialize<AiProviderConfig>(File.ReadAllText(ConfigPath)) ?? config;
                onDiskKey = config.ApiKey;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        // 配置文件里的密钥是加密的，解出来才是可用值；解不开时必须让上层知道原因。
        var decrypted = SecretProtector.Unprotect(onDiskKey);
        if (decrypted is null)
        {
            config.ApiKeyUnreadable = true;
            config.ApiKey = string.Empty;
        }
        else
        {
            config.ApiKey = decrypted;
            config.ApiKeyWasPlaintext = onDiskKey.Length > 0 && !SecretProtector.IsProtected(onDiskKey);
            config.ApiKeyStoredUnencrypted = onDiskKey.Length > 0 && !SecretProtector.IsEncryptedAtRest(onDiskKey);
        }

        // 多份配置：先把每一份的密钥解开，再把「当前选中的那一份」投影到顶层。
        // 顺序不能反——环境变量要能压过一切，所以放在投影**之后**（见 ApplyEnvironmentOverrides）。
        DecryptProfiles(config);
        EnsureProfiles(config);
        var selected = ResolveSelected(config);
        if (selected is not null) ApplyProfile(config, selected);
        ApplyEnvironmentOverrides(config, selected);

        MigratePlaintextSecret(config);
        return config;
    }

    /// <summary>把每一份的密文密钥解开；解不开的只标在这一份上，不影响其它份。</summary>
    private static void DecryptProfiles(AiProviderConfig config)
    {
        foreach (var profile in config.Profiles)
        {
            var decrypted = SecretProtector.Unprotect(profile.ApiKey);
            if (decrypted is null)
            {
                profile.ApiKeyUnreadable = true;
                profile.ApiKey = string.Empty;
            }
            else
            {
                profile.ApiKey = decrypted;
            }
        }
    }

    /// <summary>
    /// 旧配置（只有顶层字段，或被旧端写过的那种）迁移成「一份启用且选中」的配置。
    /// 顶层全空时不造空壳——那表示"还没接入"，不是"有一份空配置"。
    /// </summary>
    private static void EnsureProfiles(AiProviderConfig config)
    {
        config.Profiles ??= new List<AiProviderProfile>();   // 手写的 JSON 里给了 null 也不该炸
        foreach (var profile in config.Profiles.Where(item => string.IsNullOrWhiteSpace(item.Id)))
            profile.Id = Guid.NewGuid().ToString("N");

        if (config.Profiles.Count > 0) return;
        if (string.IsNullOrWhiteSpace(config.Endpoint) && string.IsNullOrWhiteSpace(config.Model)
            && string.IsNullOrWhiteSpace(config.ApiKey)) return;

        var migrated = FromConfig(config);
        migrated.Enabled = true;
        config.Profiles.Add(migrated);
        config.SelectedProfileId = migrated.Id;
    }

    /// <summary>
    /// 环境变量覆盖（便携部署与自动化测试用）。它们压过配置文件，所以顶层与选中项要**一起改**：
    /// 只改顶层的话，界面上显示的"正在用哪一份"与实际发出去的请求会对不上。
    /// </summary>
    private static void ApplyEnvironmentOverrides(AiProviderConfig config, AiProviderProfile? selected)
    {
        config.Endpoint = FirstNonEmpty(EnvCompat.Get("AI_ENDPOINT"), config.Endpoint);
        config.Model = FirstNonEmpty(EnvCompat.Get("AI_MODEL"), config.Model);
        config.ApiKey = FirstNonEmpty(EnvCompat.Get("AI_KEY"), config.ApiKey);
        if (selected is not null)
        {
            selected.Endpoint = config.Endpoint;
            selected.Model = config.Model;
            selected.ApiKey = config.ApiKey;
        }

        config.ImageEndpoint = FirstNonEmpty(EnvCompat.Get("IMAGE_ENDPOINT"), config.ImageEndpoint);
        config.ImageModel = FirstNonEmpty(EnvCompat.Get("IMAGE_MODEL"), config.ImageModel);
        config.VideoEndpoint = FirstNonEmpty(EnvCompat.Get("VIDEO_ENDPOINT"), config.VideoEndpoint);
        config.VideoModel = FirstNonEmpty(EnvCompat.Get("VIDEO_MODEL"), config.VideoModel);
        config.ComfyUiBaseUrl = FirstNonEmpty(EnvCompat.Get("COMFYUI_BASEURL"), config.ComfyUiBaseUrl);
        config.ComfyUiCheckpoint = FirstNonEmpty(EnvCompat.Get("COMFYUI_CHECKPOINT"), config.ComfyUiCheckpoint);
        config.AssetDirectory = FirstNonEmpty(EnvCompat.Get("ASSET_DIR"), config.AssetDirectory);
        if (int.TryParse(EnvCompat.Get("AUTO_ROUNDS"), out var rounds) && rounds >= 0)
            config.AutoGenerationRounds = rounds;
        if (int.TryParse(EnvCompat.Get("IMAGE_MAX_REFS"), out var maxRefs) && maxRefs >= 0)
            config.ImageMaxReferenceImages = maxRefs;
        if (int.TryParse(EnvCompat.Get("VIDEO_MAX_REFS"), out var maxVideoRefs) && maxVideoRefs >= 0)
            config.VideoMaxReferenceImages = maxVideoRefs;
        if (int.TryParse(EnvCompat.Get("VIDEO_SECONDS"), out var videoSeconds) && videoSeconds >= 0)
            config.VideoDefaultSeconds = videoSeconds;
    }

    /// <summary>
    /// Agent 当前该用哪一份：选中的那份**还启用着**就用它；否则自动改到另一个启用中的，并把选中项改过去；
    /// 一份都没启用时返回 null（调用方回落本地模拟，并如实告诉用户，绝不静悄悄换一个模型替他回答）。
    ///
    /// 这里会**顺手修正** <see cref="AiProviderConfig.SelectedProfileId"/>：界面上要显示"实际在用哪一份"，
    /// 让选中项和实际使用的配置长期不一致比自动切换更糟。
    /// </summary>
    public static AiProviderProfile? ResolveSelected(AiProviderConfig config)
    {
        if (config.Profiles is null || config.Profiles.Count == 0) return null;
        var picked = config.Profiles.FirstOrDefault(item => item.Id == config.SelectedProfileId);
        if (picked is { Enabled: true }) return picked;

        var fallback = config.Profiles.FirstOrDefault(item => item.Enabled);
        config.SelectedProfileId = fallback?.Id ?? string.Empty;
        return fallback;
    }

    /// <summary>Agent 面板的候选：所有**已启用**的配置（停用的不进候选，但配置与密钥都留着）。</summary>
    public static IReadOnlyList<AiProviderProfile> EnabledProfiles(AiProviderConfig config) =>
        config.Profiles is null ? Array.Empty<AiProviderProfile>() : config.Profiles.Where(item => item.Enabled).ToList();

    /// <summary>把某一份的内容写成「当前生效」的顶层字段。</summary>
    public static void ApplyProfile(AiProviderConfig config, AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        config.SelectedProfileId = profile.Id;
        config.DisplayName = profile.DisplayName;
        config.Endpoint = profile.Endpoint;
        config.UseFullUrl = profile.UseFullUrl;
        config.ApiFormat = profile.ApiFormat;
        config.Model = profile.Model;
        config.ApiKey = profile.ApiKey;
        config.Temperature = profile.Temperature;
        config.ContextWindow = profile.ContextWindow;
        config.MaxOutputTokens = profile.MaxOutputTokens;
        config.SendSamplingParameters = profile.SendSamplingParameters;
        config.SupportsImageInput = profile.SupportsImageInput;
    }

    /// <summary>
    /// 反向：把顶层字段写回某一份。**旧端只认顶层字段**，它在设置里改的东西要落回"当前选中的那一份"上，
    /// 不然老端一保存就把这次编辑丢了（这也是 <see cref="Save"/> 里那条同步规则的作用）。
    /// </summary>
    public static void UpdateProfileFromConfig(AiProviderConfig config, AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.DisplayName = config.DisplayName;
        profile.Endpoint = config.Endpoint;
        profile.UseFullUrl = config.UseFullUrl;
        profile.ApiFormat = config.ApiFormat;
        profile.Model = config.Model;
        profile.ApiKey = config.ApiKey;
        profile.Temperature = config.Temperature;
        profile.ContextWindow = config.ContextWindow;
        profile.MaxOutputTokens = config.MaxOutputTokens;
        profile.SendSamplingParameters = config.SendSamplingParameters;
        profile.SupportsImageInput = config.SupportsImageInput;
    }

    /// <summary>用顶层字段造一份配置（迁移与「复制一份」都用它）。</summary>
    public static AiProviderProfile FromConfig(AiProviderConfig config) => new()
    {
        DisplayName = config.DisplayName,
        Endpoint = config.Endpoint,
        UseFullUrl = config.UseFullUrl,
        ApiFormat = config.ApiFormat,
        Model = config.Model,
        ApiKey = config.ApiKey,
        Temperature = config.Temperature,
        ContextWindow = config.ContextWindow,
        MaxOutputTokens = config.MaxOutputTokens,
        SendSamplingParameters = config.SendSamplingParameters,
        SupportsImageInput = config.SupportsImageInput
    };

    /// <summary>
    /// 复制一份配置（含密钥）。
    /// 设置页的「复制」用它、而不是自己逐字段抄一遍：配置将来多了字段时，
    /// 漏抄的后果是"复制出来的那份悄悄少了个设置"，而那种 bug 很难被看出来。
    /// </summary>
    public static AiProviderProfile Duplicate(AiProviderProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AiProviderProfile
        {
            DisplayName = source.DisplayName,
            Endpoint = source.Endpoint,
            UseFullUrl = source.UseFullUrl,
            ApiFormat = source.ApiFormat,
            Model = source.Model,
            ApiKey = source.ApiKey,
            Temperature = source.Temperature,
            ContextWindow = source.ContextWindow,
            MaxOutputTokens = source.MaxOutputTokens,
            SendSamplingParameters = source.SendSamplingParameters,
            SupportsImageInput = source.SupportsImageInput,
            Enabled = source.Enabled
        };
    }

    /// <summary>把一份配置写回列表（按 Id 替换，没有就追加）。界面上编辑的是工作副本，保存时才落回列表。</summary>
    public static AiProviderProfile Upsert(AiProviderConfig config, AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(config);
        config.Profiles ??= new List<AiProviderProfile>();
        var index = config.Profiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0) config.Profiles[index] = profile;
        else config.Profiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// 把旧版本留下的明文密钥立刻加密落盘，缩小明文暴露窗口（不必等用户下次打开设置）。
    /// 这是本类唯一的「读取时写入」，所以只做一次：配置目录不可写时不会反复尝试。
    /// </summary>
    private static void MigratePlaintextSecret(AiProviderConfig config)
    {
        if (!config.ApiKeyWasPlaintext || migrationAttempted) return;
        migrationAttempted = true;
        Save(config);
    }

    /// <summary>
    /// 保存配置。写入失败（目录不可写、磁盘只读等）时返回 false 而不是抛异常，
    /// 由调用方决定是否提示用户——配置丢失不该让整个设置流程崩掉。
    ///
    /// 落盘前把密钥换成 DPAPI 密文：内存里始终是明文（请求要带它），**文件里始终是密文**。
    /// 多份配置的密钥、以及图像 / 视频各自的密钥，都走同一套处理。
    /// </summary>
    public static bool Save(AiProviderConfig config)
    {
        // 同步之后会**再记一次**（见下面那段说明）。这里先取一份，是为了万一同步自己抛异常，
        // finally 也有明文可还原、不至于把密文留在内存里被后续请求带走。
        var topLevelPlaintext = config.ApiKey;
        var imagePlaintext = config.ImageApiKey;
        var videoPlaintext = config.VideoApiKey;
        var profileSecrets = new Dictionary<AiProviderProfile, string>();
        try
        {
            // 顶层与「当前选中」的同步，顺序**不能反**：
            // ①先把顶层写回它代表的那一份（按 Id 认，**不看启用状态**）——旧端只改顶层字段，
            //   不这么做它这次的编辑就丢了；②再解析实际生效的那一份并把顶层对齐过去（被停用时会自动切换）。
            // 反过来先解析的话，切换后的那份会被顶层的值覆盖掉——测试里就是这么抓到的：
            // 停用第二份之后，第一份的地址与密钥被第二份的值冲掉了。
            var owner = config.Profiles?.FirstOrDefault(item => item.Id == config.SelectedProfileId)
                // 选中项被删掉了：退一步，拿顶层字段与哪一份完全对得上的那份当"它代表的那一份"。
                ?? config.Profiles?.FirstOrDefault(item =>
                    item.Endpoint == config.Endpoint && item.Model == config.Model && item.ApiKey == config.ApiKey);
            if (owner is not null) UpdateProfileFromConfig(config, owner);
            if (ResolveSelected(config) is { } effective) ApplyProfile(config, effective);

            // **同步之后**才记「这次真正要落盘的明文」。
            //
            // 原先是在方法入口就记的，那是错的：设置页保存时，用户刚敲的密钥只进了**顶层字段**，
            // 各份配置要等上面那句 UpdateProfileFromConfig 才拿到新值——入口时它们还是**空**。
            // 于是 finally 会把刚写好的那一份**还原成空**；紧接着界面 Reload 又按「那一份」把顶层
            // 也盖成空、密钥栏跟着清空，用户再点一次保存（或关窗口时的自动保存）就把空值写进文件。
            // 密钥就是这么丢的：**第一次保存其实写对了，第二次把它擦掉**——用户看到的是
            // 「我填了、也保存了，可它就是不生效」。
            topLevelPlaintext = config.ApiKey;
            foreach (var profile in config.Profiles) profileSecrets[profile] = profile.ApiKey;

            // 解不开的密钥**不许被清掉**——这是这条链上唯一会造成永久损失的一步。
            //
            // 为什么非挡不可：Load() 解不开密文时会把内存里的密钥置空、只标一个 ApiKeyUnreadable，
            // 而**任何一次「Load → 改点别的 → Save」**（设置页保存、记住上次用的池子 / 工作流、
            // 切换 ComfyUI 当前那一台……）都会把这个空值当成「用户把密钥清掉了」写进文件。
            // 于是密文永久消失——可它本来只是「**这个账户**解不开」（换回原账户、或换回原机器，
            // 本来还解得开）。"读不出来"绝不该变成"没有了"。
            //
            // 判据要**两个条件同时成立**才算「该保留盘上那份」：解不开、**而且这次也没给新值**。
            // 只看前者的话，用户重填一把新密钥会被这条守卫挡在门外——那等于把人锁死。
            var keepTopLevelCipher = config.ApiKeyUnreadable && string.IsNullOrEmpty(topLevelPlaintext);
            var profilesToKeep = (config.Profiles ?? new List<AiProviderProfile>())
                .Where(profile => profile.ApiKeyUnreadable && string.IsNullOrEmpty(profile.ApiKey))
                .Select(profile => profile.Id)
                .ToHashSet(StringComparer.Ordinal);
            var onDisk = keepTopLevelCipher || profilesToKeep.Count > 0 ? ReadOnDiskSecrets() : null;

            config.ApiKey = keepTopLevelCipher && onDisk?.ApiKey is { Length: > 0 } keptKey
                ? keptKey
                : SecretProtector.Protect(topLevelPlaintext);
            config.ImageApiKey = ProtectOptional(imagePlaintext);
            config.VideoApiKey = ProtectOptional(videoPlaintext);
            foreach (var profile in config.Profiles)
            {
                var kept = profilesToKeep.Contains(profile.Id)
                    ? onDisk?.Profiles?.FirstOrDefault(item => item.Id == profile.Id)?.ApiKey
                    : null;
                profile.ApiKey = kept is { Length: > 0 } ? kept : ProtectOptional(profile.ApiKey);
            }

            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, SaveOptions));

            // 这一份的密钥已经是新写进去的值了，「解不开」这个标记描述的就不再是它——清掉。
            // 不清的话，界面在**保存成功之后**仍会挂着「密钥解不开」的警告、密钥栏还会被清空
            // （ReloadForm 按这个标记决定回不回显），用户看到的是「我填了、保存了，可它还是说解不开」
            // ——于是又填一遍，或者干脆以为存上了。这一步必须和写盘同时发生。
            if (!keepTopLevelCipher) config.ApiKeyUnreadable = false;
            foreach (var profile in config.Profiles)
            {
                if (!profilesToKeep.Contains(profile.Id)) profile.ApiKeyUnreadable = false;
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException)
        {
            return false;
        }
        finally
        {
            // 无论成功失败都还原内存里的明文，避免后续请求拿到密文。
            // 还原的是**同步之后**记下的那一份（见上面的说明）——用入口时的那份会把用户刚填的值擦掉。
            config.ApiKey = topLevelPlaintext;
            config.ImageApiKey = imagePlaintext;
            config.VideoApiKey = videoPlaintext;
            foreach (var pair in profileSecrets) pair.Key.ApiKey = pair.Value;
        }
    }

    /// <summary>空密钥不加密——空值加密后反而会变成一段"看起来配了密钥"的密文，界面会误报已配置。</summary>
    private static string ProtectOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : SecretProtector.Protect(value);

    /// <summary>
    /// 读盘上那份配置的密钥字段（**不解密**，只取原文）。
    /// 专门给「解不开的密钥不许被清掉」用：把盘上那份原样留回去，而不是写一个空。
    /// 读不到（文件不在、坏了、读不了）返回 null——那时也没什么可保护的。
    /// </summary>
    private static AiProviderConfig? ReadOnDiskSecrets()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            return JsonSerializer.Deserialize<AiProviderConfig>(File.ReadAllText(ConfigPath));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string ConfigFilePath => ConfigPath;

    private static string FirstNonEmpty(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
