using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamForge.Desktop;

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
    public string ImageSize { get; set; } = "1024x1024";
    public string ComfyUiBaseUrl { get; set; } = string.Empty;
    public string ComfyUiCheckpoint { get; set; } = string.Empty;
    public string ComfyUiClientId { get; set; } = "dreamforge-desktop";
    public string AssetDirectory { get; set; } = string.Empty;

    /// <summary>AI 自动展开下游节点的轮数；0 表示不限制。</summary>
    public int AutoGenerationRounds { get; set; } = 2;

    /// <summary>图像参数默认值：节点未单独设置时使用。</summary>
    public string DefaultNegativePrompt { get; set; } = string.Empty;
    public string DefaultImageSteps { get; set; } = string.Empty;
    public string DefaultImageCfg { get; set; } = string.Empty;

    /// <summary>界面主题（"Dark" / "Light"）。默认深色。</summary>
    public string Theme { get; set; } = nameof(AppTheme.Dark);

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
    /// 配置文件里的密钥无法解密（通常是把配置拷到了别的 Windows 账户或机器）。
    /// 此时 <see cref="ApiKey"/> 为空，界面必须提示重新填写，而不是让用户以为「密钥没配」。
    /// 不写进配置文件——它描述的是读取结果，不是配置项。
    /// </summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

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
}

public static class AiProviderSettings
{
    /// <summary>
    /// 全局配置文件路径：所有项目共用一份模型配置。
    /// 可用 DREAMFORGE_CONFIG 覆盖，便于测试和便携部署。
    /// </summary>
    private static string ConfigPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DREAMFORGE_CONFIG");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

            return AppPaths.CombineProgram("ai-config.json");
        }
    }

    /// <summary>旧配置里的明文密钥只尝试加密保存一次，避免配置目录不可写时每次读取都重复尝试落盘。</summary>
    private static bool migrationAttempted;

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
        }

        config.Endpoint = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_AI_ENDPOINT"), config.Endpoint);
        config.Model = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_AI_MODEL"), config.Model);
        config.ApiKey = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_AI_KEY"), config.ApiKey);
        config.ImageEndpoint = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_IMAGE_ENDPOINT"), config.ImageEndpoint);
        config.ImageModel = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_IMAGE_MODEL"), config.ImageModel);
        config.ComfyUiBaseUrl = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_COMFYUI_BASEURL"), config.ComfyUiBaseUrl);
        config.ComfyUiCheckpoint = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_COMFYUI_CHECKPOINT"), config.ComfyUiCheckpoint);
        config.AssetDirectory = FirstNonEmpty(Environment.GetEnvironmentVariable("DREAMFORGE_ASSET_DIR"), config.AssetDirectory);
        if (int.TryParse(Environment.GetEnvironmentVariable("DREAMFORGE_AUTO_ROUNDS"), out var rounds) && rounds >= 0)
            config.AutoGenerationRounds = rounds;
        if (int.TryParse(Environment.GetEnvironmentVariable("DREAMFORGE_IMAGE_MAX_REFS"), out var maxRefs) && maxRefs >= 0)
            config.ImageMaxReferenceImages = maxRefs;

        MigratePlaintextSecret(config);
        return config;
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
    /// </summary>
    public static bool Save(AiProviderConfig config)
    {
        var plaintext = config.ApiKey;
        try
        {
            config.ApiKey = SecretProtector.Protect(plaintext);
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException)
        {
            return false;
        }
        finally
        {
            // 无论成功失败都还原内存里的明文，避免后续请求拿到密文。
            config.ApiKey = plaintext;
        }
    }

    public static string ConfigFilePath => ConfigPath;

    private static string FirstNonEmpty(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
