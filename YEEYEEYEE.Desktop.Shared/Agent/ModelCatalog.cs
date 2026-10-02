using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 从已填的接口地址拉一份**模型清单**（OpenAI 兼容的 <c>GET {base}/models</c>，Anthropic 的 <c>GET {base}/v1/models</c>）。
///
/// 为什么值得做这个：模型名是这份配置里最容易写错、也最容易过期的一项——
/// 服务商会不断下线旧型号，用户照着半年前的教程手填一个名字，要等到真正对话时才会撞 404 / 400。
/// 能在填地址与密钥之后直接看到"这家现在到底有哪些型号"，比让人去翻官方文档靠谱。
///
/// 这里只做"拉取 + 解析 + 老实回报"，不做任何回退猜测：
/// 拉不到就说拉不到（网关没开这个路径、密钥无权限、网络不通），绝不编一份看起来很像的清单出来。
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// 模型清单的地址。与 <see cref="AiProviderConfig.RequestUrl"/> 同一套推导规则：
    /// 地址栏填的是**基础地址**时按接口格式补路径；开了「完整 URL」时把最后一段换成 <c>models</c>。
    /// </summary>
    public static string ModelsUrl(AiProviderConfig config)
    {
        var endpoint = (config.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0) return string.Empty;

        if (config.UseFullUrl)
        {
            // 完整 URL 模式下地址栏就是请求地址本身。把**请求路径**（可能两段，例如 /chat/completions）
            // 整段换成 /models：只砍最后一段的话，…/v1/chat/completions 会变成 …/v1/chat/models，那是个不存在的地址。
            // 认不出来时退回"砍最后一段"这个最接近的猜法；猜错时上层会如实报 404，不会假装成功。
            foreach (var suffix in new[] { "/chat/completions", "/completions", "/messages" })
                if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return $"{endpoint[..^suffix.Length]}/models";

            var cut = endpoint.LastIndexOf('/');
            return cut > "https://".Length ? $"{endpoint[..cut]}/models" : string.Empty;
        }

        // Anthropic 的请求路径带 /v1，基础地址里通常没有，所以这里要补上。
        return config.ApiFormat == AiApiFormat.AnthropicMessages
            ? $"{endpoint}/v1/models"
            : $"{endpoint}/models";
    }

    /// <summary>
    /// 拉取并解析模型清单。返回 <c>(清单, 说明)</c>：清单为空时说明里一定写了原因。
    /// 超时按 20 秒算——清单接口不该慢，慢就是不通，别把一个设置页按钮挂在那里转。
    /// </summary>
    public static async Task<(IReadOnlyList<string> Models, string Message)> FetchAsync(
        AiProviderConfig config,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        var url = ModelsUrl(config);
        if (url.Length == 0) return (Array.Empty<string>(), "请先填写接口地址。");
        if (string.IsNullOrWhiteSpace(config.ApiKey)) return (Array.Empty<string>(), "请先填写 API 密钥。");

        if (http is null)
        {
            using var owned = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            return await FetchAsync(config, owned, cancellationToken);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (config.ApiFormat == AiApiFormat.AnthropicMessages)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 404 是最常见的一种：地址推导得不对，或这家网关不暴露清单接口。如实说，并给出下一步。
                var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "（这家接口可能没有 /models 路径，或地址栏填的不是基础地址——请手填模型名）"
                    : string.Empty;
                return (Array.Empty<string>(),
                    $"拉取失败：GET {url} 返回 {(int)response.StatusCode}{hint}：{Trim(body, 300)}");
            }

            var models = Parse(body);
            return models.Count == 0
                ? (models, $"GET {url} 有响应，但里面没有解析出任何模型名：{Trim(body, 300)}")
                : (models, $"GET {url} 拉到 {models.Count} 个模型。");
        }
        catch (HttpRequestException error)
        {
            return (Array.Empty<string>(), $"拉取失败：请求 {url} 出错——{error.Message}");
        }
        catch (TaskCanceledException)
        {
            return (Array.Empty<string>(), $"拉取失败：请求 {url} 超时（20 秒）。");
        }
        catch (JsonException error)
        {
            return (Array.Empty<string>(), $"拉取失败：{url} 的返回不是合法 JSON——{error.Message}");
        }
    }

    /// <summary>
    /// 解析清单。两种常见形状都收：OpenAI 的 <c>{"data":[{"id":…}]}</c> 与直接给数组的 <c>[{"id":…}]</c>；
    /// 兼容 <c>name</c> 字段（有些自建网关只有 name）。解析不到就返回空，由调用方如实回报。
    /// </summary>
    public static IReadOnlyList<string> Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array
                        ? data
                        : default;
            if (array.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

            var names = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object
                        ? ReadString(item, "id") ?? ReadString(item, "name")
                        : null;
                // 去重但保持服务端给的顺序：有些网关会把同一个模型列两遍（不同 owner）。
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Trim(string text, int limit)
    {
        var collapsed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return collapsed.Length <= limit ? collapsed : collapsed[..limit] + "…";
    }
}
