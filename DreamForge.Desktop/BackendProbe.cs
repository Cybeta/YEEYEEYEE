namespace DreamForge.Desktop;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>一条探测结论：某个能力在目标后端是否存在，以及它的输入要求。</summary>
public sealed record ProbeFinding(string Category, string Name, bool Available, string Detail);

/// <summary>
/// 后端能力探测结果。写工作流模板之前必须先拿到这个结果，
/// 因为 ComfyUI 的多图参考取决于装了哪些自定义节点，而节点输入字段在不同版本间会变。
/// </summary>
public sealed record CapabilityProbeResult(
    string Backend,
    bool Reachable,
    string Summary,
    IReadOnlyList<ProbeFinding> Findings,
    IReadOnlyList<string> Notes)
{
    public static CapabilityProbeResult Unreachable(string backend, string reason) =>
        new(backend, false, $"无法连接：{reason}", Array.Empty<ProbeFinding>(),
            new[] { "后端不可达时无法确定提交方式，请先确认服务已启动、地址与密钥正确。" });

    /// <summary>按能力分组输出，便于直接对照写模板。</summary>
    public IEnumerable<IGrouping<string, ProbeFinding>> ByCategory() =>
        Findings.GroupBy(finding => finding.Category);
}

/// <summary>
/// ComfyUI 探测：调用 /object_info 拿到这台机器上真实可用的节点清单。
/// 这是「ComfyUI 支持接受什么提交方式」的唯一权威来源——工作流模板必须按这里的节点与输入名来写。
/// </summary>
public static class ComfyUiProbe
{
    /// <summary>待探测的节点分组。候选名覆盖常见的多图参考与视频链路。</summary>
    private static readonly (string Category, string[] Candidates)[] Groups =
    {
        ("基础出图", new[] { "KSampler", "CheckpointLoaderSimple", "EmptyLatentImage", "CLIPTextEncode", "VAEDecode", "SaveImage" }),
        ("单图底图", new[] { "LoadImage", "VAEEncode" }),
        ("多图参考·IPAdapter", new[] { "IPAdapterUnifiedLoader", "IPAdapterApply", "IPAdapterAdvanced", "IPAdapterBatch", "IPAdapterEncoder", "IPAdapter" }),
        ("多图参考·ControlNet", new[] { "ControlNetLoader", "ControlNetApplyAdvanced", "ControlNetApply" }),
        ("多图参考·图像合并", new[] { "ImageBatch", "ImageStitch", "ImageConcanate", "ImageConcatMulti" }),
        ("视频·编码合成", new[] { "VHS_VideoCombine", "CreateVideo", "SaveWEBM", "SaveAnimatedWEBP", "SaveVideo" }),
        ("视频·生成模型", new[] { "SVD_img2vid_Conditioning", "WanVideoSampler", "WanImageToVideo", "WanVideoModelLoader", "CogVideoXSampler", "AnimateDiffLoaderWithContext" })
    };

    public static async Task<CapabilityProbeResult> RunAsync(string baseUrl, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return CapabilityProbeResult.Unreachable("ComfyUI", "未填写 ComfyUI 地址");

        var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var url = $"{baseUrl.TrimEnd('/')}/object_info";
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return CapabilityProbeResult.Unreachable("ComfyUI", $"GET {url} 返回 {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            return Analyze(body);
        }
        catch (HttpRequestException error) { return CapabilityProbeResult.Unreachable("ComfyUI", error.Message); }
        catch (TaskCanceledException) { return CapabilityProbeResult.Unreachable("ComfyUI", "请求超时"); }
        catch (JsonException error) { return CapabilityProbeResult.Unreachable("ComfyUI", $"返回不是合法 JSON：{error.Message}"); }
    }

    private static CapabilityProbeResult Analyze(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return CapabilityProbeResult.Unreachable("ComfyUI", "/object_info 返回的顶层不是对象");

        var available = document.RootElement;
        var findings = new List<ProbeFinding>();
        foreach (var (category, candidates) in Groups)
            foreach (var candidate in candidates)
            {
                var found = available.TryGetProperty(candidate, out var node);
                findings.Add(new ProbeFinding(category, candidate, found, found ? DescribeInputs(node) : "未安装"));
            }

        var notes = new List<string>
        {
            $"本机共 {available.EnumerateObject().Count()} 个节点可用。",
            "多图参考不是一种固定协议：能同时吃几张取决于所选节点（IPAdapter 每个应用节点吃一张，需要串联或使用批量节点）。",
            "视频链路在 ComfyUI 里同样是「提交工作流 + 轮询结果」，与出图共用一套异步机制。"
        };
        var ipadapter = findings.Any(finding => finding.Available && finding.Category.Contains("IPAdapter", StringComparison.Ordinal));
        var controlnet = findings.Any(finding => finding.Available && finding.Category.Contains("ControlNet", StringComparison.Ordinal));
        var video = findings.Any(finding => finding.Available && finding.Category.StartsWith("视频", StringComparison.Ordinal));
        notes.Add(ipadapter || controlnet
            ? $"已具备多图参考节点：{(ipadapter ? "IPAdapter " : string.Empty)}{(controlnet ? "ControlNet" : string.Empty)}。可按其输入名编写多图模板。"
            : "未检测到多图参考节点，当前只能做单张底图的 img2img；要启用分步合成需先安装 IPAdapter 或 ControlNet 相关自定义节点。");
        notes.Add(video ? "检测到视频节点，可据此编写视频模板。" : "未检测到视频生成节点，视频链路需要先安装对应自定义节点。");

        return new CapabilityProbeResult("ComfyUI", true, $"已连接，共 {available.EnumerateObject().Count()} 个节点。", findings, notes);
    }

    /// <summary>把节点的必填输入名列出来，这就是该节点的「提交字段」。</summary>
    private static string DescribeInputs(JsonElement node)
    {
        if (!node.TryGetProperty("input", out var input) || !input.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Object)
            return "已安装";
        var names = required.EnumerateObject().Select(property => property.Name).Take(12);
        return "必填输入：" + string.Join(", ", names);
    }
}

/// <summary>
/// OpenAI 兼容接口探测：列出可用模型并回显当前声明的图像提交方式。
/// 接口能吃几张参考图无法自动探测（取决于模型），只能靠这里的声明 + 实测确认。
/// </summary>
public static class OpenAiCompatibleProbe
{
    private static readonly string[] ImageKeywords = { "image", "dall", "flux", "sd", "stable", "midjourney", "seedream", "qwen-image" };
    private static readonly string[] VideoKeywords = { "video", "veo", "kling", "runway", "sora", "wan", "hunyuan" };

    public static async Task<CapabilityProbeResult> RunAsync(AiProviderConfig config, HttpClient? http = null)
    {
        var endpoint = config.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return CapabilityProbeResult.Unreachable("OpenAI 兼容接口", "未填写接口地址");

        var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var url = $"{endpoint.TrimEnd('/')}/models";
        var findings = new List<ProbeFinding>();
        var notes = new List<string>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return CapabilityProbeResult.Unreachable("OpenAI 兼容接口", $"GET {url} 返回 {(int)response.StatusCode}：{Trim(body, 200)}");

            var models = ReadModels(body);
            findings.Add(new ProbeFinding("接口连通性", "GET /models", true, $"返回 {models.Count} 个模型"));
            foreach (var model in models.Take(40))
            {
                var isImage = ImageKeywords.Any(keyword => model.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                var isVideo = VideoKeywords.Any(keyword => model.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (!isImage && !isVideo) continue;
                findings.Add(new ProbeFinding(isVideo ? "视频模型（疑似）" : "图像模型（疑似）", model, true,
                    isVideo ? "视频接口通常为异步任务：提交后轮询取结果" : "图像接口通常同步返回"));
            }
            notes.Add("参考图字段名随实现而异：OpenAI 官方 /images/edits 单图用 image，gpt-image 系列多图用 image[]，部分三方实现用 images。");
            notes.Add("能吃几张参考图取决于模型，无法探测；请在设置里声明上限，超限时宿主会提示分步合成或截断。");
            if (findings.All(finding => !finding.Category.StartsWith("图像模型", StringComparison.Ordinal) && !finding.Category.StartsWith("视频模型", StringComparison.Ordinal)))
                notes.Add("模型列表里没有识别出图像或视频模型，可能接口不暴露 /models，或需要手工填写模型名。");
        }
        catch (HttpRequestException error) { return CapabilityProbeResult.Unreachable("OpenAI 兼容接口", error.Message); }
        catch (TaskCanceledException) { return CapabilityProbeResult.Unreachable("OpenAI 兼容接口", "请求超时"); }
        catch (JsonException error) { return CapabilityProbeResult.Unreachable("OpenAI 兼容接口", $"返回不是合法 JSON：{error.Message}"); }

        findings.Add(new ProbeFinding("文本接口", $"{config.Model}（/chat/completions）", config.IsConfigured,
            config.IsConfigured ? "已配置，可用于规划合成方案" : "未配置，规划会退回本地兜底"));
        findings.Add(new ProbeFinding("图像接口", string.IsNullOrWhiteSpace(config.ImageModel) ? "（未配置）" : config.ImageModel, config.IsImageConfigured,
            config.IsImageConfigured
                ? $"声明上限 {config.ImageMaxReferenceImages} 张参考图（0 表示不限）"
                : "未配置图像模型"));

        return new CapabilityProbeResult("OpenAI 兼容接口", true, "已连接。", findings, notes);
    }

    private static List<string> ReadModels(string body)
    {
        using var document = JsonDocument.Parse(body);
        var models = new List<string>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return models;
        foreach (var item in data.EnumerateArray())
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                models.Add(id.GetString()!);
        return models;
    }

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
