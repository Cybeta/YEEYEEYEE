namespace YEEYEEYEE.Desktop;

/// <summary>一轮对话消息。</summary>
public sealed class AiChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 随消息发送的图片，每项是 data URL 或 http(s) 地址。
    /// 只有支持图像理解的模型才允许非空——不支持的模型传图会被接口拒绝，
    /// 所以发送前由 Provider 依据配置判断并给出可读的报错。
    /// </summary>
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 一次请求的 token 用量。命中 / 未命中是缓存视角（DeepSeek 会明确给出），
/// 服务商不给这两项时按 0 处理——**如实为空，不猜**。
/// </summary>
public sealed record AiUsage(long PromptTokens, long CompletionTokens, long CacheHitTokens, long CacheMissTokens)
{
    public static readonly AiUsage Empty = new(0, 0, 0, 0);

    /// <summary>输入侧总量：优先用服务商报的 prompt_tokens，没有就用命中+未命中。</summary>
    public long InputTokens => PromptTokens > 0 ? PromptTokens : CacheHitTokens + CacheMissTokens;

    public bool IsEmpty => InputTokens == 0 && CompletionTokens == 0;
}

/// <summary>
/// 流式回调：正文与思考**分成两条流**（把 model replies 与 reasoning 分开记）。
///
/// 为什么思考要单独给：思考型模型（deepseek-v4-flash 默认开思考）会先想一会儿再吐正文，
/// 这期间界面若什么都不显示，用户会以为卡死了；而思考本身**不属于回答**，
/// 不能混进正文，也**不进对话历史**——DeepSeek 文档明确：不带 tools 时
/// <c>reasoning_content</c> 不需要回传，传了也会被忽略。所以它只用于展示。
/// </summary>
public sealed record AiStreamSink(Action<string> OnText, Action<string>? OnThinking = null, Action<AiUsage>? OnUsage = null);

/// <summary>
/// 结构化补全：按调用方给定的系统提示要一份 JSON 输出。
///
/// 与 <see cref="IAiChatProvider"/> 分开是有原因的：对话模式**故意不要求 JSON**（聊创作时 JSON 会碍事），
/// 而这里的场景（把一份人写的接口文档整理成结构化 JSON）**必须**约束输出格式。
/// 混用那个接口会让模型的系统提示与本次要求互相矛盾。
/// </summary>
public interface IAiJsonCompleter
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// 多轮对话能力。与 <see cref="IAiProvider.GenerateAsync"/> 的区别：
/// 对话**不强制 JSON 输出**，允许模型用自然语言回答，用于「和助手讨论企划 / 剧情 / 设定」这类场景；
/// 结构化生成仍然走 GenerateAsync。
/// </summary>
public interface IAiChatProvider
{
    Task<string> ChatAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式对话：每收到一段增量就回调一次，返回值是**正文**（不含思考）。
    /// 默认实现退回非流式（一次性把全文回调出去），所以不支持流式的实现不必额外写代码——
    /// 代价是没有「逐字出现」的体感，但行为与语义完全一致。
    /// </summary>
    async Task<string> ChatStreamAsync(
        IReadOnlyList<AiChatMessage> messages, AiStreamSink sink, CancellationToken cancellationToken = default)
    {
        var text = await ChatAsync(messages, cancellationToken);
        sink.OnText(text);
        return text;
    }
}

/// <summary>
/// 文本接口的协议格式。这里**只列真正实现了的两种**，不提供「选了但不生效」的选项：
/// 选了 Anthropic 格式就会按 /v1/messages 的报文与鉴权头发请求。
/// </summary>
public enum AiApiFormat
{
    /// <summary>OpenAI 兼容的 /chat/completions，绝大多数服务商都支持。</summary>
    OpenAiChat,

    /// <summary>Anthropic 兼容的 /v1/messages，Claude 系列与部分服务商提供。</summary>
    AnthropicMessages
}

/// <summary>
/// 服务商预设的一个模型版本。
/// 上下文与最大输出**只填已核实的官方数值**，拿不到就留 0 表示未声明——
/// 宁可让用户按文档自己填，也不要写一个看起来权威的错数字。
/// </summary>
public sealed record ProviderModel(
    string Id,
    string Note = "",
    int ContextWindow = 0,
    int MaxOutputTokens = 0,
    bool SupportsVision = false);

/// <summary>
/// 服务商预设：接入引导里选一个，地址、接口格式与推荐模型自动填好，用户只需补密钥。
/// 模型列表只是**起点**：服务商会不断下线旧模型，所以模型 ID 始终允许手填。
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string Endpoint,
    string Hint,
    AiApiFormat Format,
    IReadOnlyList<ProviderModel> Models,
    bool SendsSamplingParameters = true)
{
    /// <summary>没有预设模型时，模型 ID 由用户自己填。</summary>
    public ProviderModel? DefaultModel => Models.Count > 0 ? Models[0] : null;

    /// <summary>不接入模型：用关键词启发式生成草稿，联网功能不可用。</summary>
    public static readonly ProviderPreset Local = new(
        "local", "本地模拟（不接入模型）", string.Empty,
        "不联网。AI 生成与对话都是本地启发式的占位结果，只适合先试用界面。",
        AiApiFormat.OpenAiChat, Array.Empty<ProviderModel>());

    public static readonly ProviderPreset DeepSeek = new(
        "deepseek", "DeepSeek", "https://api.deepseek.com/v1",
        "旧的 deepseek-chat / deepseek-reasoner 已于 2026-07-24 停用，现在的模型名是 deepseek-flash 与 deepseek-v4-pro。" +
        "只有 deepseek-flash 支持图像理解（能上传图片），deepseek-v4-pro 是纯文本。" +
        "Anthropic 格式的地址是 https://api.deepseek.com/anthropic（选「自定义」后切换接口格式）。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("deepseek-flash", "V4.1-Flash · 1M 上下文 / 384K 输出 · 支持图像理解", 1_000_000, 384_000, SupportsVision: true),
            new ProviderModel("deepseek-v4-pro", "V4-Pro · 1M 上下文 / 384K 输出 · 纯文本", 1_000_000, 384_000),
            new ProviderModel("deepseek-v4-flash", "旧名，当前路由到 V4.1-Flash · 支持图像理解", 1_000_000, 384_000, SupportsVision: true)
        });

    public static readonly ProviderPreset Moonshot = new(
        "moonshot", "月之暗面 Kimi", "https://api.moonshot.cn/v1",
        "moonshot-v1 系列与 kimi-k2.5 已下线。Kimi 的 temperature / top_p 由服务端固定，显式传入会报错，" +
        "所以这里不发送采样参数（高级配置里可改）。列出的模型都支持图片与视频输入。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("kimi-k3", "旗舰 · 1M 上下文 · 支持视觉理解", 1_048_576, SupportsVision: true),
            new ProviderModel("kimi-k2.7-code", "编码模型 · 256K 上下文 · 支持图片输入", 262_144, SupportsVision: true),
            new ProviderModel("kimi-k2.7-code-highspeed", "编码高速版 · 256K 上下文 · 支持图片输入", 262_144, SupportsVision: true),
            new ProviderModel("kimi-k2.6", "通用 · 256K 上下文 · 支持图片输入", 262_144, SupportsVision: true)
        },
        SendsSamplingParameters: false);

    public static readonly ProviderPreset Qwen = new(
        "qwen", "阿里通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1",
        "百炼的 API Key 按地域绑定，跨地域调用会直接鉴权失败。地址仍可用 dashscope.aliyuncs.com；" +
        "如需更高稳定性，可在控制台改用业务空间专属域名。main 系列支持图片输入。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("qwen3.8-max", "旗舰多模态 · 1M 上下文 · 支持图片与视频", 1_000_000, SupportsVision: true),
            new ProviderModel("qwen3.7-plus", "均衡多模态 · 1M 上下文 · 支持图片", 1_000_000, SupportsVision: true),
            new ProviderModel("qwen3.8-flash", "高速经济型 · 支持图片", 1_000_000, SupportsVision: true),
            new ProviderModel("qwen3-coder-plus", "编码模型（未核实图像支持）")
        });

    public static readonly ProviderPreset Zhipu = new(
        "zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4",
        "GLM-5.3 目前**只处理文本**，且固定开启思考（reasoning_effort 支持 low / high / max）。" +
        "Anthropic 格式的地址是 https://open.bigmodel.cn/api/anthropic。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("glm-5.3", "旗舰 · 1M 上下文 / 128K 输出 · 纯文本", 1_000_000, 128_000),
            new ProviderModel("glm-5.2"),
            new ProviderModel("glm-4.7"),
            new ProviderModel("glm-4.6"),
            new ProviderModel("glm-4.5-flash", "低成本")
        });

    public static readonly ProviderPreset SiliconFlow = new(
        "siliconflow", "硅基流动", "https://api.siliconflow.cn/v1",
        "模型 ID 是「组织/模型」形式，在硅基流动模型库复制即可。这里只预置了已核实的条目，" +
        "其余（DeepSeek、Qwen、Kimi 等）请直接手填模型 ID。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("zai-org/GLM-5.3", "旗舰 · 1M 上下文 / 128K 输出 · 纯文本", 1_000_000, 128_000)
        });

    public static readonly ProviderPreset OpenAi = new(
        "openai", "OpenAI", "https://api.openai.com/v1",
        "模型 ID 以 OpenAI 控制台为准；国内网络通常需要自备代理，这些型号仅供参考，可直接手填。",
        AiApiFormat.OpenAiChat,
        new[]
        {
            new ProviderModel("gpt-5.6", "支持图片输入", SupportsVision: true),
            new ProviderModel("gpt-5.4", "支持图片输入", SupportsVision: true),
            new ProviderModel("gpt-5.2", "支持图片输入", SupportsVision: true)
        });

    public static readonly ProviderPreset Ollama = new(
        "ollama", "本地 Ollama", "http://127.0.0.1:11434/v1",
        "需要本机已安装并启动 Ollama；密钥留空，模型名填你已 pull 的名字，例如 qwen3:8b。" +
        "Ollama 的模型完全取决于你本地拉过什么，所以这里不预置。",
        AiApiFormat.OpenAiChat, Array.Empty<ProviderModel>());

    public static readonly ProviderPreset Custom = new(
        "custom", "自定义（自选接口格式）", string.Empty,
        "先选接口格式，再填地址：关掉「完整 URL」时按格式自动补 /chat/completions 或 /v1/messages，" +
        "打开则完全按你填的地址请求。",
        AiApiFormat.OpenAiChat, Array.Empty<ProviderModel>());

    public static IReadOnlyList<ProviderPreset> All { get; } = new[]
    {
        DeepSeek, Moonshot, Qwen, Zhipu, SiliconFlow, OpenAi, Ollama, Custom, Local
    };

    /// <summary>本地模拟走的是占位实现，没有地址与模型可填。</summary>
    public bool IsLocal => Id == "local";

    /// <summary>模型列表为空时只能手填模型 ID。</summary>
    public bool NeedsManualModel => Models.Count == 0 && !IsLocal;

    /// <summary>按已保存的地址反推预设，用于重新打开引导时回显当前用的是哪一家。</summary>
    public static ProviderPreset Match(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return Custom;
        return All.FirstOrDefault(preset =>
            !string.IsNullOrWhiteSpace(preset.Endpoint)
            && string.Equals(preset.Endpoint.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ?? Custom;
    }
}
