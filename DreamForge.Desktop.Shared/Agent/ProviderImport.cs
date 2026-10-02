using System.Text.RegularExpressions;

namespace DreamForge.Desktop;

/// <summary>智能导入识别出的接口类型。</summary>
public enum ProviderKind
{
    /// <summary>没能判断出类型：只填地址不猜能力，由用户在界面上确认。</summary>
    Unknown,

    /// <summary>本地或远程 ComfyUI（异步工作流提交）。</summary>
    ComfyUi,

    /// <summary>画图接口（OpenAI 兼容 /images/generations 这一类的同步或异步出图）。</summary>
    ImageApi,

    /// <summary>画视频接口（异步任务：提交后轮询取结果）。</summary>
    VideoApi,

    /// <summary>文本接口（OpenAI 兼容 /chat/completions 或 Anthropic /v1/messages）。</summary>
    TextApi
}

/// <summary>
/// 智能导入的识别结果。**只声明识别到了什么**：识别不到的字段一律留空，
/// 由界面显示为「未识别，请手填」，绝不猜测或用其它值顶替。
/// </summary>
public sealed record ProviderImportDraft(
    ProviderKind Kind,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Checkpoint,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> Warnings)
{
    public bool HasAnything =>
        Kind != ProviderKind.Unknown
        || BaseUrl.Length > 0
        || ApiKey.Length > 0
        || Model.Length > 0
        || Checkpoint.Length > 0;

    public static ProviderImportDraft Empty { get; } = new(
        ProviderKind.Unknown, string.Empty, string.Empty, string.Empty, string.Empty,
        Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// ComfyUI / 画图 / 画视频接口的智能导入（用户要求：智能导入窗口与模块）。
///
/// 输入可以是任意一段从服务商文档、控制台或同事那里复制来的文本：一个地址、一段 curl、
/// 一份 JSON 配置，或 env 形式的键值对。输出是「识别结论 + 将要写入哪些字段」，
/// 写入由 <see cref="Apply"/> 在用户确认后执行——识别与写入分开，识别阶段零副作用。
///
/// 判定原则：
/// · 命中判据才算识别到，并把命中的判据原文列出来给用户核对（<see cref="ProviderImportDraft.Signals"/>）；
/// · 判不出类型时返回 <see cref="ProviderKind.Unknown"/> 并说明原因，不猜是画图还是画视频；
/// · 地址会归一化成「基础地址」（去掉 /object_info、/images/generations 这类具体路径），
///   与设置里「地址栏填基础地址、程序按协议补路径」的既有约定保持一致。
/// </summary>
public static class ProviderImporter
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>\)\]]+", RegexOptions.IgnoreCase);

    private static readonly Regex SkKeyPattern = new(@"\bsk-[A-Za-z0-9_\-]{8,}", RegexOptions.None);

    private static readonly Regex BearerPattern =
        new(@"(?:authorization\s*[:=]\s*[""']?bearer\s+|bearer\s+)([A-Za-z0-9_\-\.]{12,})", RegexOptions.IgnoreCase);

    private static readonly Regex AssignmentKeyPattern =
        new(@"(?:api[_-]?key|apikey|access[_-]?token|secret[_-]?key|密钥)\s*[:=]\s*[""']?([A-Za-z0-9_\-\.]{12,})", RegexOptions.IgnoreCase);

    private static readonly Regex ModelPattern =
        new(@"[""']?(?:model|model_name|modelname|model_id|模型)[""']?\s*[:=]\s*[""']?([A-Za-z0-9_\-\./:]{3,})", RegexOptions.IgnoreCase);

    private static readonly Regex CheckpointPattern =
        new(@"([A-Za-z0-9_\-\.]+\.[A-Za-z0-9_\-\.]+\.(?:safetensors|ckpt|pt|pth|gguf)|[A-Za-z0-9_\-\.]+\.(?:safetensors|ckpt|gguf))", RegexOptions.IgnoreCase);

    /// <summary>ComfyUI 的判据：接口路径、WebSocket、端口或产品名。</summary>
    private static readonly string[] ComfySignals =
    {
        "/object_info", "/prompt", "/queue", "/history", "/interrupt", "/system_stats", "/ws?clientid", "clientid=",
        "comfyui", "comfy ui", ":8188"
    };

    /// <summary>画图接口的判据。</summary>
    private static readonly string[] ImageSignals =
    {
        "images/generations", "images/edits", "images/variations", "image2image", "img2img",
        "dall-e", "dalle", "gpt-image", "flux", "stable-diffusion", "stable diffusion", "sd3", "sd-xl", "sdxl",
        "seedream", "qwen-image", "midjourney", "文生图", "图生图", "画图", "出图"
    };

    /// <summary>画视频接口的判据。</summary>
    private static readonly string[] VideoSignals =
    {
        "video/generations", "videos", "text2video", "image2video", "img2video", "i2v", "t2v",
        "veo", "kling", "runway", "sora", "wan2", "wanvideo", "hunyuan", "animatediff", "svd",
        "文生视频", "图生视频", "画视频", "生成视频", "让图片动起来"
    };

    /// <summary>文本接口的判据。</summary>
    private static readonly string[] TextSignals =
    {
        "chat/completions", "/completions", "v1/messages", "anthropic", "claude", "gpt-4", "gpt-5",
        "deepseek", "qwen", "moonshot", "kimi", "glm", "文本模型", "大模型"
    };

    /// <summary>可被剥离的接口路径尾巴，按长度从长到短匹配，避免只剥掉一半。</summary>
    private static readonly string[] KnownPathTails =
    {
        "images/generations", "images/edits", "images/variations", "video/generations", "chat/completions",
        "system_stats", "object_info", "completions", "generations", "interrupt", "messages", "videos", "models",
        "prompt", "queue", "history", "view", "ws", "edits"
    };

    public static string KindName(ProviderKind kind) => kind switch
    {
        ProviderKind.ComfyUi => "ComfyUI",
        ProviderKind.ImageApi => "画图接口",
        ProviderKind.VideoApi => "画视频接口",
        ProviderKind.TextApi => "文本接口",
        _ => "未能判断"
    };

    /// <summary>识别一段文本。空输入返回空结论，不抛异常。</summary>
    public static ProviderImportDraft Inspect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ProviderImportDraft.Empty;
        var blob = text.Trim();
        var lower = blob.ToLowerInvariant();

        var signals = new List<string>();
        var warnings = new List<string>();

        var rawUrl = UrlPattern.Match(blob) is { Success: true } match ? TrimUrl(match.Value) : string.Empty;
        var baseUrl = rawUrl.Length > 0 ? NormalizeBaseUrl(rawUrl) : string.Empty;
        if (rawUrl.Length > 0 && !string.Equals(rawUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            signals.Add($"地址归一化：{rawUrl} → {baseUrl}");

        var apiKey = ExtractApiKey(blob);
        var checkpoint = CheckpointPattern.Match(blob) is { Success: true } checkpointMatch ? checkpointMatch.Groups[1].Value : string.Empty;
        var model = ExtractModel(blob);

        var comfyHits = Hit(lower, ComfySignals);
        var imageHits = Hit(lower, ImageSignals);
        var videoHits = Hit(lower, VideoSignals);
        var textHits = Hit(lower, TextSignals);

        ProviderKind kind;
        if (comfyHits.Count > 0 || (checkpoint.Length > 0 && rawUrl.Length > 0 && IsLocalHost(rawUrl)))
        {
            kind = ProviderKind.ComfyUi;
        }
        else if (videoHits.Count > 0 && imageHits.Count == 0)
        {
            kind = ProviderKind.VideoApi;
        }
        else if (imageHits.Count > 0 && videoHits.Count == 0)
        {
            kind = ProviderKind.ImageApi;
        }
        else if (videoHits.Count > 0 && imageHits.Count > 0)
        {
            // 两个都命中：常见于「一个网关既出图又出视频」的文档。不替用户选，如实说明。
            kind = ProviderKind.Unknown;
            warnings.Add("同时出现画图与画视频的关键词，无法确定是哪一种：请在下拉里手动选择类型。");
        }
        else if (textHits.Count > 0 && rawUrl.Length > 0)
        {
            kind = ProviderKind.TextApi;
        }
        else
        {
            kind = ProviderKind.Unknown;
            warnings.Add(rawUrl.Length > 0
                ? "只识别到地址，没有能判断用途的关键词：请手动选择类型（ComfyUI / 画图 / 画视频 / 文本），或粘贴更完整的文档片段。"
                : "没有识别到可用地址：请粘贴接口地址（或在文档里复制一段包含地址的内容）。");
        }

        foreach (var hit in comfyHits) signals.Add($"ComfyUI 判据：{hit}");
        foreach (var hit in videoHits) signals.Add($"画视频判据：{hit}");
        foreach (var hit in imageHits) signals.Add($"画图判据：{hit}");
        foreach (var hit in textHits) signals.Add($"文本判据：{hit}");
        if (checkpoint.Length > 0) signals.Add($"checkpoint：{checkpoint}");
        if (model.Length > 0) signals.Add($"模型：{model}");

        if (kind == ProviderKind.ComfyUi)
        {
            if (baseUrl.Length == 0) warnings.Add("识别为 ComfyUI 但没有找到地址：请填写形如 http://127.0.0.1:8188 的地址。");
            if (checkpoint.Length == 0) warnings.Add("没有找到 checkpoint 文件名：ComfyUI 出图需要它，请手填（例如 sd_xl_base_1.0.safetensors）。");
        }
        else if (kind is ProviderKind.ImageApi or ProviderKind.VideoApi)
        {
            if (model.Length == 0) warnings.Add($"没有找到 {KindName(kind)}的模型名：请手填（例如 dall-e-3 / veo-3 / kling-v1）。");
            if (apiKey.Length == 0) warnings.Add("没有找到 API 密钥：如果该接口需要鉴权，请手填密钥。");
        }
        else if (kind == ProviderKind.TextApi)
        {
            if (model.Length == 0) warnings.Add("没有找到文本模型名：请手填（例如 deepseek-chat）。");
        }

        if (apiKey.Length > 0) signals.Add($"密钥：{SecretProtector.Describe(apiKey)}（只显示首尾，不回显完整密钥）");

        return new ProviderImportDraft(kind, baseUrl, apiKey, model, checkpoint, signals, warnings);
    }

    /// <summary>
    /// 计算「导入后会写入/改动哪些字段」，用于导入前预览。只报告真正会变化的项。
    /// 密钥用脱敏形式描述（<see cref="SecretProtector.Describe"/>），不会回显完整值。
    /// </summary>
    public static IReadOnlyList<string> DescribeChanges(AiProviderConfig config, ProviderImportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(draft);
        var changes = new List<string>();

        void Consider(string label, string target, string value, bool changed)
        {
            if (!changed) return;
            changes.Add($"{label} → {(value.Length == 0 ? "（清空）" : value)}");
        }

        void ConsiderSecret(bool changed)
        {
            if (changed) changes.Add($"API 密钥 → {SecretProtector.Describe(draft.ApiKey)}");
        }

        switch (draft.Kind)
        {
            case ProviderKind.ComfyUi:
                Consider("ComfyUI 地址", config.ComfyUiBaseUrl, draft.BaseUrl, draft.BaseUrl.Length > 0 && !Same(config.ComfyUiBaseUrl, draft.BaseUrl));
                Consider("ComfyUI checkpoint", config.ComfyUiCheckpoint, draft.Checkpoint, draft.Checkpoint.Length > 0 && !Same(config.ComfyUiCheckpoint, draft.Checkpoint));
                break;
            case ProviderKind.ImageApi:
                if (draft.BaseUrl.Length > 0)
                {
                    // 与文本地址相同 → 写入的是「留空表示复用」，预览必须与 Apply 的实际动作一致。
                    var target = Same(config.Endpoint, draft.BaseUrl) ? string.Empty : draft.BaseUrl;
                    Consider("图像接口地址", config.ImageEndpoint, target, !Same(config.ImageEndpoint, target));
                }

                Consider("图像模型", config.ImageModel, draft.Model, draft.Model.Length > 0 && !Same(config.ImageModel, draft.Model));
                break;
            case ProviderKind.VideoApi:
                if (draft.BaseUrl.Length > 0)
                {
                    var target = Same(config.Endpoint, draft.BaseUrl) ? string.Empty : draft.BaseUrl;
                    Consider("画视频接口地址", config.VideoEndpoint, target, !Same(config.VideoEndpoint, target));
                }

                Consider("视频模型", config.VideoModel, draft.Model, draft.Model.Length > 0 && !Same(config.VideoModel, draft.Model));
                break;
            case ProviderKind.TextApi:
                Consider("接口地址", config.Endpoint, draft.BaseUrl, draft.BaseUrl.Length > 0 && !Same(config.Endpoint, draft.BaseUrl));
                Consider("文本模型", config.Model, draft.Model, draft.Model.Length > 0 && !Same(config.Model, draft.Model));
                break;
            default:
                break;
        }

        ConsiderSecret(draft.ApiKey.Length > 0 && !Same(config.ApiKey, draft.ApiKey));
        return changes;
    }

    /// <summary>
    /// 按识别结果写入配置（用户确认后调用）。返回实际改动的字段描述；密钥只写入 <see cref="AiProviderConfig.ApiKey"/>，
    /// 落盘加密由 <see cref="AiProviderSettings.Save"/> 负责。未识别到的字段一律不写，避免把现有配置清空。
    /// </summary>
    public static IReadOnlyList<string> Apply(AiProviderConfig config, ProviderImportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(draft);
        var applied = new List<string>();

        switch (draft.Kind)
        {
            case ProviderKind.ComfyUi:
                if (draft.BaseUrl.Length > 0 && !Same(config.ComfyUiBaseUrl, draft.BaseUrl))
                {
                    config.ComfyUiBaseUrl = draft.BaseUrl;
                    applied.Add($"ComfyUI 地址 = {draft.BaseUrl}");
                }

                if (draft.Checkpoint.Length > 0 && !Same(config.ComfyUiCheckpoint, draft.Checkpoint))
                {
                    config.ComfyUiCheckpoint = draft.Checkpoint;
                    applied.Add($"ComfyUI checkpoint = {draft.Checkpoint}");
                }

                break;
            case ProviderKind.ImageApi:
                if (draft.BaseUrl.Length > 0)
                {
                    // 与文本地址相同就留空表示复用，保持配置里只有一个地址。
                    var target = Same(config.Endpoint, draft.BaseUrl) ? string.Empty : draft.BaseUrl;
                    if (!Same(config.ImageEndpoint, target))
                    {
                        config.ImageEndpoint = target;
                        applied.Add(target.Length == 0 ? "图像接口地址 = （复用主接口地址）" : $"图像接口地址 = {target}");
                    }
                }

                if (draft.Model.Length > 0 && !Same(config.ImageModel, draft.Model))
                {
                    config.ImageModel = draft.Model;
                    applied.Add($"图像模型 = {draft.Model}");
                }

                break;
            case ProviderKind.VideoApi:
                if (draft.BaseUrl.Length > 0)
                {
                    var target = Same(config.Endpoint, draft.BaseUrl) ? string.Empty : draft.BaseUrl;
                    if (!Same(config.VideoEndpoint, target))
                    {
                        config.VideoEndpoint = target;
                        applied.Add(target.Length == 0 ? "画视频接口地址 = （复用主接口地址）" : $"画视频接口地址 = {target}");
                    }
                }

                if (draft.Model.Length > 0 && !Same(config.VideoModel, draft.Model))
                {
                    config.VideoModel = draft.Model;
                    applied.Add($"视频模型 = {draft.Model}");
                }

                break;
            case ProviderKind.TextApi:
                if (draft.BaseUrl.Length > 0 && !Same(config.Endpoint, draft.BaseUrl))
                {
                    config.Endpoint = draft.BaseUrl;
                    applied.Add($"接口地址 = {draft.BaseUrl}");
                }

                if (draft.Model.Length > 0 && !Same(config.Model, draft.Model))
                {
                    config.Model = draft.Model;
                    applied.Add($"文本模型 = {draft.Model}");
                }

                break;
            default:
                break;
        }

        if (draft.ApiKey.Length > 0 && !Same(config.ApiKey, draft.ApiKey))
        {
            config.ApiKey = draft.ApiKey;
            applied.Add($"API 密钥 = {SecretProtector.Describe(draft.ApiKey)}");
        }

        return applied;
    }

    /// <summary>
    /// 归一化为基础地址：剥掉 /object_info、/images/generations 这类具体接口路径，
    /// 但保留 /v1 这类版本前缀——与设置里「填基础地址、程序按协议补路径」的约定一致。
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;

        var path = uri.AbsolutePath.TrimEnd('/');
        for (var pass = 0; pass < 3; pass++)
        {
            var stripped = false;
            foreach (var tail in KnownPathTails)
            {
                var suffix = "/" + tail;
                if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                path = path[..^suffix.Length];
                stripped = true;
                break;
            }

            if (!stripped) break;
        }

        var builder = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string TrimUrl(string url) =>
        url.TrimEnd('.', ',', ';', ')', ']', '、', '。', '；', '，', '"', '\'');

    private static bool IsLocalHost(string url) =>
        url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || url.Contains("://[::1]", StringComparison.OrdinalIgnoreCase);

    private static List<string> Hit(string lower, string[] candidates)
    {
        var hits = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!lower.Contains(candidate, StringComparison.Ordinal)) continue;
            hits.Add(candidate);
        }

        return hits;
    }

    private static string ExtractApiKey(string blob)
    {
        var sk = SkKeyPattern.Match(blob);
        if (sk.Success) return sk.Value;
        var bearer = BearerPattern.Match(blob);
        if (bearer.Success) return bearer.Groups[1].Value;
        var assigned = AssignmentKeyPattern.Match(blob);
        return assigned.Success ? assigned.Groups[1].Value : string.Empty;
    }

    private static string ExtractModel(string blob)
    {
        var match = ModelPattern.Match(blob);
        if (!match.Success) return string.Empty;
        var value = match.Groups[1].Value.Trim().Trim('"', '\'', ',');
        // 地址不当模型名；识别不到就返回空，由用户手填。
        return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    private static bool Same(string left, string right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
