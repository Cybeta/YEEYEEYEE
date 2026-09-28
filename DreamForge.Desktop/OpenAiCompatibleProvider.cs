using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DreamForge.Desktop;

public static class AiProviderFactory
{
    public static IAiProvider Create()
    {
        var config = AiProviderSettings.Load();
        // 显式选择本地模拟时不再尝试真实接口，避免用户以为正在使用大模型。
        if (config.UseLocalProvider || !config.IsConfigured) return new LocalAiProvider();
        return new OpenAiCompatibleProvider(config);
    }
}

/// <summary>
/// 通过 OpenAI 兼容的 /chat/completions 接口生成节点内容与下游节点建议。
/// 仅在用户显式配置 endpoint 与 model 后启用，密钥只从本地配置文件或环境变量读取。
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider, ICompositionPlanner, IAiChatProvider
{
    private const string SystemPrompt =
        "你是 AI 创作工作流助手，服务于小说与短剧的分镜生产流程。只输出 JSON，不要输出解释、Markdown 代码块或多余文字。";

    /// <summary>
    /// 对话模式的系统提示：不要求 JSON，允许自然语言回答。
    ///
    /// 这里**必须**说明「要改画布就走协议块」：早先版本写的是「不要输出 JSON」，
    /// 那是 Agent 协议出现之前的遗留——它与上下文里的操作协议**直接矛盾**，
    /// 叠加画布里的剧情正文与历史里的写作模式后，模型就会顺着写故事而不提议改动。
    /// </summary>
    private const string ChatSystemPrompt =
        "你是 YeeYeeYee 的创作助手，帮助用户打磨小说与短剧的企划、章节、分镜、角色与场景设定。" +
        "用简体中文回答。讨论创作时直接给出可用的内容或建议，不要说明你的输出格式。\n" +
        "当用户要求**改动画布或写入文件**时，必须按上下文中给出的操作协议输出改动块（JSON）。" +
        "尤其是用户要求根据剧情自动建立节点时，必须输出多个 create_node，并按需要输出 create_edge，不能只写剧情正文。" +
        "这类要求绝不能只用文字描述或正文内容代替——只写文字等于什么都没做，用户看不到可批准的改动。";

    private readonly HttpClient http;
    private readonly AiProviderConfig config;

    public OpenAiCompatibleProvider(AiProviderConfig config, HttpClient? http = null)
    {
        this.config = config;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var text = await SendAsync(SystemPrompt,
            new[] { new AiChatMessage { Role = "user", Content = BuildPrompt(request) } }, cancellationToken);
        return ParseResult(text);
    }

    /// <summary>
    /// 多轮对话：把消息转发给文本接口，使用对话用系统提示（不要求 JSON）。
    /// </summary>
    public Task<string> ChatAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken cancellationToken = default) =>
        SendAsync(ChatSystemPrompt, messages, cancellationToken);

    /// <summary>
    /// 一条请求走两种协议格式：OpenAI 兼容的 /chat/completions 与 Anthropic 兼容的 /v1/messages。
    /// 报文、鉴权头与响应解析都在这里分支，上层（生成 / 对话 / 规划 / 连接测试）不用各自判断。
    /// </summary>
    private async Task<string> SendAsync(string systemPrompt, IReadOnlyList<AiChatMessage> messages, CancellationToken cancellationToken)
    {
        var url = config.RequestUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("还没有配置接口地址。");
        EnsureImageInputAllowed(messages);

        var anthropic = config.ApiFormat == AiApiFormat.AnthropicMessages;
        var payload = anthropic ? BuildAnthropicPayload(systemPrompt, messages) : BuildOpenAiPayload(systemPrompt, messages);

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        ApplyAuthHeaders(request, anthropic);

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 接口返回 {(int)response.StatusCode}：{Trim(body, 300)}");

        return ExtractText(body);
    }

    /// <summary>
    /// 流式对话：两种协议都用 SSE，逐行读 <c>data:</c> 负载，把增量回调出去。
    /// 用 <see cref="HttpCompletionOption.ResponseHeadersRead"/> 是为了**边收边显示**——
    /// 否则 HttpClient 会等整个响应读完才返回，流式就白做了。
    /// </summary>
    public async Task<string> ChatStreamAsync(
        IReadOnlyList<AiChatMessage> messages, AiStreamSink sink, CancellationToken cancellationToken = default)
    {
        var url = config.RequestUrl;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("还没有配置接口地址。");
        EnsureImageInputAllowed(messages);

        var anthropic = config.ApiFormat == AiApiFormat.AnthropicMessages;
        var payload = anthropic ? BuildAnthropicPayload(ChatSystemPrompt, messages) : BuildOpenAiPayload(ChatSystemPrompt, messages);
        payload["stream"] = true;
        // 让服务端在事件流末尾补一条 usage（OpenAI 兼容协议的做法）；
        // Anthropic 本来就在 message_start / message_delta 里带用量，不需要开关。
        if (!anthropic)
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        ApplyAuthHeaders(request, anthropic);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 出错时服务端返回的是普通 JSON 而不是事件流，照常读出来报给用户。
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"AI 接口返回 {(int)response.StatusCode}：{Trim(error, 300)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var builder = new StringBuilder();
        var usage = AiUsage.Empty;
        var usageSeen = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // 注释行、event: 行都跳过
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            // 用量可能出现在任意一条事件里（末尾那条通常是纯用量），逐条尝试读取。
            if (TryReadUsage(data, anthropic, usage) is { } merged) { usage = merged; usageSeen = true; sink.OnUsage?.Invoke(usage); }

            var (text, thinking) = anthropic ? ReadAnthropicDelta(data) : ReadOpenAiDelta(data);
            // 思考与正文是两条流：思考只用于展示，既不拼进正文，也不进对话历史。
            if (thinking.Length > 0) sink.OnThinking?.Invoke(thinking);
            if (text.Length == 0) continue;
            builder.Append(text);
            sink.OnText(text);
        }

        var reply = builder.ToString();
        // 收到的事件流里一行正文都没有，说明格式与预期不符，不能假装成功返回空回复。
        if (reply.Length == 0) throw new InvalidOperationException("流式响应里没有收到任何文本，可能是接口不支持 stream。");
        if (usageSeen) sink.OnUsage?.Invoke(usage);
        return reply;
    }

    /// <summary>
    /// 从一条事件里读用量。取不到就返回 null（保持上一次累积的值不变）。
    /// OpenAI 兼容：末尾块带 usage.prompt_tokens / completion_tokens，DeepSeek 另给命中与未命中；
    /// Anthropic：message_start 给 input_tokens，message_delta 给 output_tokens。两边字段名不同，所以分开处理。
    /// </summary>
    private static AiUsage? TryReadUsage(string data, bool anthropic, AiUsage accumulated)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            var container = root;
            if (anthropic && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                container = message;
            if (!container.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            var prompt = accumulated.PromptTokens;
            var completion = accumulated.CompletionTokens;
            var hit = accumulated.CacheHitTokens;
            var miss = accumulated.CacheMissTokens;

            if (ReadLong(usage, "prompt_tokens") is { } p) prompt = p;
            if (ReadLong(usage, "input_tokens") is { } i) prompt = i;
            if (ReadLong(usage, "completion_tokens") is { } c) completion = c;
            if (ReadLong(usage, "output_tokens") is { } o) completion = o;
            if (ReadLong(usage, "prompt_cache_hit_tokens") is { } h) hit = h;
            if (ReadLong(usage, "cache_read_input_tokens") is { } ch) hit = ch;
            if (ReadLong(usage, "prompt_cache_miss_tokens") is { } m) miss = m;

            var merged = new AiUsage(prompt, completion, hit, miss);
            return merged == accumulated ? null : merged;
        }
        catch (JsonException) { return null; }
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    /// <summary>OpenAI 兼容格式：增量在 choices[0].delta.content，思考在 delta.reasoning_content。</summary>
    private static (string Text, string Thinking) ReadOpenAiDelta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return (string.Empty, string.Empty);
            if (!choices[0].TryGetProperty("delta", out var delta)) return (string.Empty, string.Empty);

            var text = delta.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty;
            var thinking = delta.TryGetProperty("reasoning_content", out var reasoning) ? reasoning.GetString() ?? string.Empty : string.Empty;
            // 有的实现在同一块里既给思考又给正文，那时只认正文，避免思考混入。
            return text.Length > 0 ? (text, string.Empty) : (string.Empty, thinking);
        }
        catch (JsonException) { return (string.Empty, string.Empty); }
    }

    /// <summary>Anthropic 格式：增量在 content_block_delta，文本块是 text_delta，思考块是 thinking_delta。</summary>
    private static (string Text, string Thinking) ReadAnthropicDelta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("delta", out var delta)) return (string.Empty, string.Empty);
            var type = delta.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            return type switch
            {
                "text_delta" => (ReadString(delta, "text"), string.Empty),
                "thinking_delta" => (string.Empty, ReadString(delta, "thinking")),
                _ => (string.Empty, string.Empty)
            };
        }
        catch (JsonException) { return (string.Empty, string.Empty); }
    }

    /// <summary>不支持的模型传图会被接口拒绝（DeepSeek 直接 400），在这里先拦住并说清怎么改。</summary>
    private void EnsureImageInputAllowed(IReadOnlyList<AiChatMessage> messages)
    {
        if (messages.Sum(item => item.Images.Count) > 0 && !config.SupportsImageInput)
            throw new InvalidOperationException(
                "当前模型没有开启图片输入。请在接入设置的高级配置里勾选「支持图片输入」，" +
                "或改用支持图像理解的模型（例如 deepseek-flash、kimi-k3、qwen3.8-max）。");
    }

    private void ApplyAuthHeaders(HttpRequestMessage request, bool anthropic)
    {
        if (anthropic)
        {
            // Anthropic 用 x-api-key 而不是 Bearer，并且要求带版本头。
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    private Dictionary<string, object?> BuildOpenAiPayload(string systemPrompt, IReadOnlyList<AiChatMessage> messages)
    {
        var list = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var item in messages)
        {
            // 没有图片时 content 保持字符串：兼容性最好，也让报文更好读。
            if (item.Images.Count == 0)
            {
                list.Add(new { role = item.Role, content = item.Content });
                continue;
            }

            var parts = new List<object> { new { type = "text", text = item.Content } };
            foreach (var image in item.Images)
                parts.Add(new { type = "image_url", image_url = new { url = image } });
            list.Add(new { role = item.Role, content = parts });
        }

        var payload = new Dictionary<string, object?> { ["model"] = config.Model, ["messages"] = list };
        if (config.SendSamplingParameters) payload["temperature"] = config.Temperature;
        if (config.MaxOutputTokens > 0) payload["max_tokens"] = config.MaxOutputTokens;
        return payload;
    }

    private Dictionary<string, object?> BuildAnthropicPayload(string systemPrompt, IReadOnlyList<AiChatMessage> messages)
    {
        // Anthropic 的 messages 不接受 system 角色，system 必须单独放在顶层字段。
        // 也因此图片天然不会被塞进 system 消息——DeepSeek 对 system 里的图片会直接报 400。
        var list = new List<object>();
        foreach (var item in messages)
        {
            var role = string.Equals(item.Role, "assistant", StringComparison.Ordinal) ? "assistant" : "user";
            if (item.Images.Count == 0)
            {
                list.Add(new { role, content = item.Content });
                continue;
            }

            var parts = new List<object> { new { type = "text", text = item.Content } };
            foreach (var image in item.Images) parts.Add(AnthropicImageBlock(image));
            list.Add(new { role, content = parts });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            // max_tokens 在 Anthropic 协议里是必填项：没配置时给一个保守值，不赌服务端默认。
            ["max_tokens"] = config.MaxOutputTokens > 0 ? config.MaxOutputTokens : 4096,
            ["system"] = systemPrompt,
            ["messages"] = list
        };
        if (config.SendSamplingParameters) payload["temperature"] = config.Temperature;
        return payload;
    }

    /// <summary>
    /// Anthropic 的图片块：data URL 要拆成 media_type + base64 两部分；
    /// 外链图片则用 url 来源。两种写法不能混。
    /// </summary>
    private static object AnthropicImageBlock(string image) =>
        AgentAttachmentLoader.SplitDataUrl(image) is { } data
            ? new { type = "image", source = new { type = "base64", media_type = data.MediaType, data = data.Data } }
            : new { type = "image", source = new { type = "url", url = image } };

    /// <summary>
    /// 用一次极小的请求验证地址、格式、密钥与模型是否可用。接入引导的「测试连接」用它，
    /// 避免用户保存了一份填错的配置却以为接好了。
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint)) return (false, "请先填写接口地址。");
        if (string.IsNullOrWhiteSpace(config.Model)) return (false, "请先填写模型 ID。");
        try
        {
            var reply = await SendAsync("你是连接测试助手，只需回复你收到的内容。",
                new[] { new AiChatMessage { Role = "user", Content = "ping" } }, cancellationToken);
            return (true, $"连接成功：POST {config.RequestUrl}，模型可用（返回 {reply.Length} 字符）。");
        }
        catch (HttpRequestException error) { return (false, $"请求失败：{error.Message}"); }
        catch (TaskCanceledException) { return (false, "请求超时，请检查地址与网络。"); }
        catch (JsonException error) { return (false, $"返回内容不是合法 JSON：{error.Message}"); }
        catch (InvalidOperationException error) { return (false, error.Message); }
    }

    private static string BuildPrompt(AiGenerationRequest request)
    {
        var node = request.Node;
        var builder = new StringBuilder();
        builder.AppendLine("当前节点信息：");
        builder.AppendLine($"- 节点标题：{node.Title}");
        builder.AppendLine($"- 节点文本内容：{(string.IsNullOrWhiteSpace(node.Content) ? "（空）" : node.Content)}");
        if (!string.IsNullOrWhiteSpace(request.NodeTreeContext))
        {
            builder.AppendLine("- 当前节点所在的完整节点树：");
            builder.AppendLine(request.NodeTreeContext);
        }
        var attachments = WorkflowCanvasControl.AttachmentSummary(node);
        if (!string.IsNullOrEmpty(attachments)) builder.AppendLine($"- 附件：{attachments}");
        if (!string.IsNullOrWhiteSpace(request.Reference))
        {
            builder.AppendLine("- 该节点引用的设定（必须遵守，不要改动其中的设定细节）：");
            foreach (var line in request.Reference.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) builder.AppendLine($"  {line.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(request.Instruction)) builder.AppendLine($"- 用户指令：{request.Instruction}");
        if (!string.IsNullOrWhiteSpace(request.Answer)) builder.AppendLine($"- 用户对上一次提问的回答：{request.Answer}");
        builder.AppendLine();
        builder.AppendLine("任务要求：");
        builder.AppendLine("结合完整节点树上下文和当前节点分类，使用简体中文生成内容，保持人物、事件、场景及时间线连续一致。");
        builder.AppendLine("如关键信息不足，请在 question 字段提出一个最关键的问题，并把 output 留空。");
        builder.AppendLine("信息充足时，output 填写生成结果，question 留空。");
        builder.AppendLine("proposals 填写 2 到 6 个可供用户勾选的下游节点候选，每项包含具体 title 与可直接使用的 content；结合完整节点树避免重复，不要修改当前节点。没有合理候选时返回空数组。");
        builder.AppendLine();
        builder.AppendLine("输出 JSON 格式：");
        builder.AppendLine("{\"output\":\"生成结果\",\"question\":\"需要用户补充的问题\",\"proposals\":[{\"title\":\"分镜\",\"content\":\"该节点的初始内容\"}]}");
        return builder.ToString();
    }

    /// <summary>
    /// 从响应里取文本：OpenAI 兼容格式在 choices[0].message.content，
    /// Anthropic 兼容格式在 content 块数组里的 text 块。
    /// </summary>
    private static string ExtractText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty;
        }

        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                // 只取文本块：思考块（thinking）等不属于要展示的内容。
                if (block.TryGetProperty("type", out var type) && !string.Equals(type.GetString(), "text", StringComparison.Ordinal)) continue;
                if (block.TryGetProperty("text", out var text)) builder.Append(text.GetString());
            }
            if (builder.Length > 0) return builder.ToString();
        }

        throw new InvalidOperationException("AI 接口返回内容里没有可用的文本。");
    }

    private AiGenerationResult ParseResult(string text)
    {
        var json = StripCodeFence(text);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var output = ReadString(root, "output");
            var question = ReadString(root, "question");
            var proposals = new List<AiNodeProposal>();
            if (root.TryGetProperty("proposals", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var title = ReadString(item, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    proposals.Add(new AiNodeProposal { Title = title, Content = ReadString(item, "content") });
                }
            }
            if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(question)) output = text;
            return new AiGenerationResult
            {
                Provider = "OpenAiCompatible",
                Model = config.Model,
                Output = output,
                Question = question,
                Proposals = proposals
            };
        }
        catch (JsonException)
        {
            // 模型没有按约定返回 JSON 时，把原文作为内容返回，不猜测结构。
            return new AiGenerationResult { Provider = "OpenAiCompatible", Model = config.Model, Output = text };
        }
    }

    /// <summary>
    /// 让大模型规划参考图合成方案。规划只产出编排（步骤、提示词、备选策略），
    /// 不产出图片内容；模型返回不可用时退回本地兜底规划，避免卡住整个流程。
    /// </summary>
    public async Task<CompositionPlan> PlanCompositionAsync(CompositionPlanningRequest request, CancellationToken cancellationToken = default)
    {
        var text = await SendAsync(SystemPrompt,
            new[] { new AiChatMessage { Role = "user", Content = BuildCompositionPrompt(request) } }, cancellationToken);
        return ParseCompositionPlan(text, request);
    }

    private static string BuildCompositionPrompt(CompositionPlanningRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("任务：为一个镜头规划「参考图分步合成」方案。");
        builder.AppendLine();
        builder.AppendLine($"镜头标题：{request.NodeTitle}");
        builder.AppendLine($"镜头内容：{(string.IsNullOrWhiteSpace(request.NodeContent) ? "（空）" : request.NodeContent)}");
        builder.AppendLine();
        builder.AppendLine("本镜头引用的设定与对应参考图：");
        foreach (var reference in request.References) builder.AppendLine("- " + reference.Describe());
        builder.AppendLine();
        builder.AppendLine($"硬性约束：生图链路一次最多只能同时使用 {request.MaxImagesPerStep} 张参考图，" +
                           "所以要用多步合成把参考图逐步并入，每一步的 inputs 数量都不得超过这个上限。");
        builder.AppendLine("只有标注了「有参考图」的条目才能出现在 inputs 里。");
        builder.AppendLine("每一步只表达一个明确的并入关系（例如「把武器换成图中的剑」「在背后加上背包」），提示词要写明保持哪些部分不变。");
        builder.AppendLine("最后一步的产出会作为该镜头的出图底图，因此最后一步应该是画面最完整的那一步。");
        if (!string.IsNullOrWhiteSpace(request.Instruction))
        {
            builder.AppendLine();
            builder.AppendLine("用户的额外要求（优先满足）：");
            builder.AppendLine(request.Instruction);
        }
        builder.AppendLine();
        builder.AppendLine("输出 JSON 格式：");
        builder.AppendLine("{\"summary\":\"为什么这样排的简短说明\",\"question\":\"需要用户补充的问题，没有则留空\",\"steps\":[{\"id\":\"s1\",\"name\":\"步骤名\",\"inputs\":[\"ref:0\",\"ref:1\"],\"prompt\":\"本步提示词\",\"denoise\":0.55}],\"alternatives\":[\"备选策略的简短描述\"]}");
        builder.AppendLine("inputs 用 ref:N 表示第 N 条引用的参考图（从 0 开始），用前置步骤的 id 表示该步产出。");
        return builder.ToString();
    }

    private CompositionPlan ParseCompositionPlan(string text, CompositionPlanningRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(text));
            var root = document.RootElement;
            var steps = new List<CompositionStepPlan>();
            if (root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
            {
                var sequence = 0;
                foreach (var item in stepsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var inputs = new List<string>();
                    if (item.TryGetProperty("inputs", out var inputsElement) && inputsElement.ValueKind == JsonValueKind.Array)
                        foreach (var input in inputsElement.EnumerateArray())
                            if (input.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(input.GetString()))
                                inputs.Add(input.GetString()!);
                    var id = ReadString(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) id = $"s{++sequence}";
                    steps.Add(new CompositionStepPlan
                    {
                        Id = id,
                        Name = ReadString(item, "name"),
                        Inputs = inputs,
                        Prompt = ReadString(item, "prompt"),
                        Denoise = ReadDouble(item, "denoise", 0.55)
                    });
                }
            }

            var alternatives = new List<string>();
            if (root.TryGetProperty("alternatives", out var alternativesElement) && alternativesElement.ValueKind == JsonValueKind.Array)
                foreach (var item in alternativesElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        alternatives.Add(item.GetString()!);

            var plan = new CompositionPlan
            {
                Summary = ReadString(root, "summary"),
                Question = ReadString(root, "question"),
                Steps = steps,
                Alternatives = alternatives
            };
            if (plan.IsUsable) return plan;
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }

        var fallback = LocalCompositionPlanner.Plan(request);
        return new CompositionPlan
        {
            Summary = $"模型没有返回可用的步骤，已改用本地兜底规划。{fallback.Summary}",
            Steps = fallback.Steps,
            Alternatives = fallback.Alternatives
        };
    }

    private static double ReadDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? Math.Clamp(value.GetDouble(), 0.05, 1.0)
            : fallback;

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? trimmed[(start + 1)..end].Trim() : trimmed;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
