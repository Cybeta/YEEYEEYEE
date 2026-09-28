namespace DreamForge.Desktop;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Agent 提议的一次操作。**它只是提议**：必须先经用户审批，再由
/// <see cref="AgentActionExecutor"/> 应用到画布，模型不能直接改动任何数据。
/// </summary>
public sealed class AgentAction
{
    /// <summary>
    /// 操作种类：create_node / update_node / delete_node / create_edge / delete_edge /
    /// create_entity / update_entity / delete_entity / create_entity_version / write_file /
    /// create_work_item / update_work_item / delete_work_item。
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>目标：节点或实体的短 id（Guid 前 8 位）或标题；修改与删除必填。连线的终点节点也用它。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>连线的起点节点（短 id 或标题）。只有 create_edge / delete_edge 使用。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>标题（新建节点/实体时使用，修改时可选）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>节点分类：通用、角色、场景、分镜、道具或成品。</summary>
    public string NodeCategory { get; set; } = string.Empty;

    /// <summary>新建节点时的父节点短 id 或标题，由 Agent 上下文补全。</summary>
    public string ParentTarget { get; set; } = string.Empty;

    /// <summary>新建节点时要关联的工作树条目（章节或能力）名或短 id；决定节点跟随哪个章节簇更新。</summary>
    public string WorkTreeTarget { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>实体种类：角色 / 场景 / 道具。</summary>
    public string EntityKind { get; set; } = string.Empty;

    /// <summary>写文件时的相对路径（相对工作文件夹）。</summary>
    public string Path { get; set; } = string.Empty;

    public string WorkTreeKind { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string EntityTarget { get; set; } = string.Empty;
    public string VariantTarget { get; set; } = string.Empty;
    public string VariantVersion { get; set; } = string.Empty;

    /// <summary>
    /// 要引用的一组设定库实体名（这一镜出现谁、在哪、用什么）。
    /// 角色/场景/道具只存在设定库里，靠这个字段挂到剧情/分镜节点上，不再各占一个画布节点。
    /// </summary>
    public IReadOnlyList<string> EntityTargets { get; set; } = Array.Empty<string>();
    public string VersionNote { get; set; } = string.Empty;
    public bool MarkVersionAdopted { get; set; }

    /// <summary>提议理由，展示给用户判断要不要批准。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>给审批列表看的一行摘要。</summary>
    public string Describe()
    {
        var target = string.IsNullOrWhiteSpace(Target) ? string.Empty : $"「{Target}」";
        var link = $"「{Source}」→「{Target}」";
        return Kind switch
        {
            "create_node" => $"新建节点{Quote(Title)}",
            "update_node" => $"修改节点{target}",
            "delete_node" => $"删除节点{target}",
            "create_edge" => $"连线{link}",
            "delete_edge" => $"删除连线{link}",
            "create_entity" => $"新建{NormalizeEntityKind()}「{Title}」",
            "update_entity" => $"修改实体{target}",
            "delete_entity" => $"删除实体{target}",
            "create_entity_version" => $"创建{EntityTarget}的新版本{Version}",
            "write_file" => $"写入文件「{Path}」",
            "create_work_item" => $"工作树新增{WorkTreeItem.KindName(WorkTreeItem.ParseKind(WorkTreeKind))}「{Title}」",
            "update_work_item" => $"工作树更新「{Target}」",
            "delete_work_item" => $"工作树删除「{Target}」",
            _ => $"未知操作 {Kind}"
        };
    }

    private string Quote(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"「{value}」";

    private string NormalizeEntityKind() => string.IsNullOrWhiteSpace(EntityKind) ? "实体" : EntityKind;
}

/// <summary>
/// 模型的反问：信息不足时用来**问用户**，由界面弹窗让用户选或自己填。
///
/// 为什么需要它：没有这个通道时，模型只能把「你要加哪个画布？节点类型是？内容写什么？」
/// 写成一大段正文，让用户自己去读、自己组织语言再回一遍——那是聊天，不是工具该有的样子。
/// </summary>
public sealed record AgentAsk(string Question, IReadOnlyList<string> Options);

/// <summary>Agent 的一次回复：自然语言文本 + 可选的操作提议 + 可选的反问。</summary>
public sealed class AgentReply
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<AgentAction> Actions { get; init; } = Array.Empty<AgentAction>();

    /// <summary>模型在等用户补充信息时给出的反问；没有就是 null。</summary>
    public AgentAsk? Ask { get; init; }

    /// <summary>
    /// 回复里**出现了协议标记但整块不可解析**（多半是内容里的引号没转义）。
    /// 与"压根没写协议块"不同：那是遵约问题，这是格式问题，两者都该自动请它重来，
    /// 但要在对话区里说清是哪一种。
    /// </summary>
    public bool ProtocolBroken { get; init; }
}

/// <summary>
/// 从模型回复末尾的协议块里解析出操作提议与反问。
/// 块形如 <c>{"ask":{...},"actions":[...]}</c>（两者都可缺省，但不能都没有）；
/// 解析成功后把该块从展示文本中剥离；协议标记存在但格式损坏时只保留协议块前的自然语言。
/// </summary>
public static class AgentActionParser
{
    /// <summary>协议块的识别标记：块里至少要有其中之一。</summary>
    private static readonly string[] Markers = { "\"actions\"", "\"ask\"" };

    public static AgentReply Parse(string? reply)
    {
        var text = reply ?? string.Empty;
        var start = FindBlockStart(text);
        // 有协议标记却定位不到可解析的块 → 是"块写坏了"，不是"没写块"。
        if (start < 0)
        {
            var marker = FindMarker(text);
            return marker < 0
                ? new AgentReply { Text = text.Trim() }
                : new AgentReply { Text = HideBrokenProtocol(text, marker), ProtocolBroken = true };
        }

        var end = FindMatchingBrace(text, start);
        if (end < 0) return new AgentReply { Text = HideBrokenProtocol(text, start), ProtocolBroken = true };

        var json = text[start..(end + 1)];
        var parsed = new List<AgentAction>();
        AgentAsk? ask = null;
        var read = false;
        // 先按原样解析；失败就修一次"内容里的裸引号"再解析——
        // 模型写中文文案时很爱直接敲英文引号，那不是它能自己发现的错，得我们兜住。
        foreach (var candidate in new[] { json, EscapeInnerQuotes(json) })
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                ReadActions(root, parsed);
                ask = ReadAsk(root);
                read = true;
                break;
            }
            catch (JsonException) { }
        }
        if (!read) return new AgentReply { Text = HideBrokenProtocol(text, start), ProtocolBroken = true };
        if (parsed.Count == 0 && ask is null) return new AgentReply { Text = HideBrokenProtocol(text, start), ProtocolBroken = true };

        // 把协议块前后多余的空行去掉，避免对话区出现大段空白。
        var cleaned = (text[..start] + text[(end + 1)..]).Trim();
        return new AgentReply { Text = cleaned, Actions = parsed, Ask = ask };
    }

    private static void ReadActions(JsonElement root, List<AgentAction> parsed)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return;
        foreach (var item in actions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var kind = Read(item, "kind");
            if (string.IsNullOrWhiteSpace(kind)) continue;
            parsed.Add(new AgentAction
            {
                Kind = kind.Trim(),
                Target = Read(item, "target"),
                Source = Read(item, "source"),
                Title = Read(item, "title"),
                NodeCategory = Read(item, "nodeCategory"),
                ParentTarget = Read(item, "parentTarget"),
                WorkTreeTarget = Read(item, "workTreeTarget"),
                Content = Read(item, "content"),
                EntityKind = Read(item, "entityKind"),
                Path = Read(item, "path"),
                WorkTreeKind = Read(item, "workTreeKind"),
                Chapter = Read(item, "chapter"),
                Version = Read(item, "version"),
                EntityTarget = Read(item, "entityTarget"),
                VariantTarget = Read(item, "variantTarget"),
                VariantVersion = Read(item, "variantVersion"),
                EntityTargets = ReadStringArray(item, "entityTargets"),
                VersionNote = Read(item, "versionNote"),
                MarkVersionAdopted = item.TryGetProperty("markVersionAdopted", out var adopted)
                    && adopted.ValueKind == JsonValueKind.True,
                Reason = Read(item, "reason")
            });
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var values = new List<string>();
        foreach (var entry in element.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                values.Add(entry.GetString()!.Trim());
        return values;
    }

    /// <summary>反问单独解析：有 question 才算，光有 ask 键不算。</summary>
    private static AgentAsk? ReadAsk(JsonElement root)
    {
        if (!root.TryGetProperty("ask", out var askElement) || askElement.ValueKind != JsonValueKind.Object) return null;
        var question = Read(askElement, "question");
        if (string.IsNullOrWhiteSpace(question)) return null;

        var options = new List<string>();
        if (askElement.TryGetProperty("options", out var optionElement) && optionElement.ValueKind == JsonValueKind.Array)
            foreach (var option in optionElement.EnumerateArray())
                if (option.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(option.GetString()))
                    options.Add(option.GetString()!.Trim());
        return new AgentAsk(question.Trim(), options);
    }

    /// <summary>
    /// 修一个模型常犯的错：在 JSON 字符串**内容**里直接写英文引号（中文文案里很自然），
    /// 于是整个块不再是合法 JSON，解析失败后整块 JSON 会原样糊到对话区里、改动也全部作废。
    ///
    /// 判据：在字符串内部遇到的引号，只有当它后面第一个非空白字符是 , } ] : 或到了结尾时，
    /// 才有资格当"结束引号"；否则它只能是内容里的引号，转义掉。
    /// </summary>
    private static string EscapeInnerQuotes(string json)
    {
        var builder = new StringBuilder(json.Length + 16);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];
            // 已转义的字符整对搬过去，不要在里面误判引号
            if (inString && ch == '\\' && index + 1 < json.Length)
            {
                builder.Append(ch).Append(json[++index]);
                continue;
            }
            if (ch != '"')
            {
                builder.Append(ch);
                continue;
            }
            if (!inString)
            {
                inString = true;
                builder.Append(ch);
                continue;
            }

            var next = index + 1;
            while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
            var isCloser = next >= json.Length || json[next] is ',' or '}' or ']' or ':';
            if (isCloser) inString = false;
            builder.Append(isCloser ? "\"" : "\\\"");
        }
        return builder.ToString();
    }

    /// <summary>
    /// 协议块在文本里的**可见起点**：模型通常会在块前加 ```json 围栏，
    /// 流式显示时要从围栏就开始隐藏，否则用户会先看到半截围栏再看着它消失。
    /// 找不到时返回 -1。与解析共用同一套定位逻辑，避免显示与解析两边判断不一致。
    /// </summary>
    public static int FindProtocolBlockStart(string? text)
    {
        var source = text ?? string.Empty;
        var brace = FindBlockStart(source);
        if (brace >= 0)
        {
            var fence = source.LastIndexOf("```", brace, StringComparison.Ordinal);
            return fence >= 0 ? fence : brace;
        }

        var marker = FindMarker(source);
        if (marker >= 0)
        {
            var markedFence = source.LastIndexOf("```", marker, StringComparison.Ordinal);
            if (markedFence >= 0) return markedFence;
            var partialBrace = source.LastIndexOf('{', marker);
            return partialBrace >= 0 ? partialBrace : marker;
        }

        var openFence = source.LastIndexOf("```", StringComparison.Ordinal);
        if (openFence < 0 || source.IndexOf("```", openFence + 3, StringComparison.Ordinal) >= 0) return -1;
        var afterFence = source[(openFence + 3)..].TrimStart();
        var language = "json";
        if (afterFence.Length == 0 || language.StartsWith(afterFence, StringComparison.OrdinalIgnoreCase)) return openFence;
        if (afterFence.StartsWith(language, StringComparison.OrdinalIgnoreCase))
        {
            afterFence = afterFence[language.Length..].TrimStart();
            return afterFence.Length == 0 || afterFence.StartsWith('{') ? openFence : -1;
        }
        return afterFence.StartsWith('{') ? openFence : -1;
    }

    /// <summary>
    /// 找协议块的起始花括号。
    ///
    /// 不能简单地「从标记往回找最近的花括号」——{@"ask"} 写在 {@"actions"} 前面时，
    /// 往回找到的是 ask 对象自己的花括号，整个块就被切错了。
    /// 所以逐个试候选：取第一个**配对成功、且确实含 actions/ask 键**的对象。
    /// 这样正文里偶然出现的花括号也不会被误判。
    /// </summary>
    private static int FindBlockStart(string text)
    {
        var marker = FindMarker(text);
        if (marker < 0) return -1;

        for (var index = text.IndexOf('{'); index >= 0 && index < marker; index = text.IndexOf('{', index + 1))
        {
            var end = FindMatchingBrace(text, index);
            if (end < marker) continue;   // 这个对象没把标记包进去，不是它
            if (IsProtocolBlock(text[index..(end + 1)])) return index;
        }
        return -1;
    }

    /// <summary>最后一个协议标记的位置（actions 或 ask 之中更靠后的那个）。</summary>
    private static string HideBrokenProtocol(string text, int hint)
    {
        var marker = FindMarker(text);
        var fence = text.LastIndexOf("```", marker >= 0 ? marker : Math.Clamp(hint, 0, text.Length), StringComparison.Ordinal);
        var start = fence >= 0 ? fence : text.LastIndexOf('{', Math.Clamp(hint, 0, text.Length));
        return (start >= 0 ? text[..start] : text[..Math.Clamp(hint, 0, text.Length)]).Trim();
    }

    private static int FindMarker(string text)
    {
        var marker = -1;
        foreach (var candidate in Markers)
        {
            var index = text.LastIndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (index > marker) marker = index;

            for (var length = candidate.Length - 1; length >= 4; length--)
            {
                var partial = candidate[..length];
                if (text.EndsWith(partial, StringComparison.OrdinalIgnoreCase))
                    marker = Math.Max(marker, text.Length - length);
            }
        }
        return marker;
    }

    private static bool IsProtocolBlock(string json)
    {
        // 与 Parse 用同一套容错：内容里有裸引号的块也要认得出来，
        // 否则"块写坏了"会被误判成"没写块"，连重试都触发不了。
        foreach (var candidate in new[] { json, EscapeInnerQuotes(json) })
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("actions", out _) || root.TryGetProperty("ask", out _)) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    /// <summary>从起始花括号做括号配对，返回匹配的结束花括号位置。</summary>
    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') inString = false;
                continue;
            }
            if (character == '"') { inString = true; continue; }
            if (character == '{') depth++;
            else if (character == '}')
            {
                depth--;
                if (depth == 0) return index;
            }
        }
        return -1;
    }

    private static string Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>应用 Agent 操作的结果。</summary>
public sealed record AgentApplyResult(int Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> RemovedReferences);

/// <summary>文件类操作的执行方式：预览试算时必须跳过，落盘只在真正提交时发生。</summary>
public enum FileWriteMode
{
    /// <summary>真正写入文件（提交时）。</summary>
    Apply,

    /// <summary>跳过写入，只试算画布部分（预览时，保证预览无副作用）。</summary>
    Skip
}

/// <summary>
/// 把审批通过的 Agent 操作应用到画布。所有操作只改数据，界面刷新与资产清理由调用方处理。
/// </summary>
public static class AgentActionExecutor
{
    public static AgentApplyResult Apply(
        IReadOnlyList<AgentAction> actions,
        WorkflowCanvasState canvas,
        string? workspaceDirectory,
        FileWriteMode fileMode = FileWriteMode.Apply,
        List<FileSnapshot>? fileSnapshots = null)
    {
        var applied = 0;
        var errors = new List<string>();
        var removed = new List<string>();

        foreach (var action in actions)
        {
            try
            {
                var message = ApplyOne(action, canvas, workspaceDirectory, removed, fileMode, fileSnapshots);
                if (message is null) applied++;
                else errors.Add($"{action.Describe()}：{message}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{action.Describe()}：{error.Message}");
            }
        }
        return new AgentApplyResult(applied, errors, removed);
    }

    /// <summary>返回 null 表示成功，否则返回失败原因。</summary>
    private static string? ApplyOne(
        AgentAction action,
        WorkflowCanvasState canvas,
        string? workspaceDirectory,
        List<string> removed,
        FileWriteMode fileMode,
        List<FileSnapshot>? fileSnapshots)
    {
        switch (action.Kind.Trim().ToLowerInvariant())
        {
            case "create_node":
            {
                if (string.IsNullOrWhiteSpace(action.Title) && string.IsNullOrWhiteSpace(action.Content))
                    return "标题与内容都为空";
                var node = new WorkflowNode
                {
                    Title = string.IsNullOrWhiteSpace(action.Title) ? "新节点" : action.Title.Trim(),
                    Content = action.Content ?? string.Empty,
                    Chapter = action.Chapter?.Trim() ?? string.Empty,
                    Category = ParseNodeCategory(action.NodeCategory),
                    ContentSource = ContentSource.Ai
                };
                if (ApplyReferences(canvas, action, node, replace: false) is { } referenceError) return referenceError;
                if (string.IsNullOrWhiteSpace(node.Chapter))
                    node.Chapter = FindInheritedChapter(canvas, action.ParentTarget);
                if (AttachWorkTreeAnchor(canvas, action.WorkTreeTarget, node) is { } anchorError) return anchorError;
                PlaceNewNode(canvas, node, action.ParentTarget);
                return null;
            }
            case "update_node":
            {
                if (FindNode(canvas, action.Target) is not { } node) return "找不到目标节点";
                // 锁定表示已定稿：必须先在界面上解锁，Agent 不能绕过这道保护。
                if (node.IsLocked) return "节点已锁定（定稿保护），请先解锁";
                if (!string.IsNullOrWhiteSpace(action.Title)) node.Title = action.Title.Trim();
                if (!string.IsNullOrWhiteSpace(action.NodeCategory)) node.Category = ParseNodeCategory(action.NodeCategory);
                if (!string.IsNullOrWhiteSpace(action.Content)) node.Content = action.Content;
                if (!string.IsNullOrWhiteSpace(action.Chapter)) node.Chapter = action.Chapter.Trim();
                if (ApplyReferences(canvas, action, node, replace: true) is { } referenceError) return referenceError;
                if (!string.IsNullOrWhiteSpace(action.EntityTarget))
                    node.VersionDecision = action.MarkVersionAdopted
                        ? VersionDecision.Adopted
                        : VersionDecision.None;
                if (AttachWorkTreeAnchor(canvas, action.WorkTreeTarget, node) is { } anchorError) return anchorError;
                node.ContentSource = ContentSource.Ai;
                node.GenerationHistory.Add(new GenerationHistory
                {
                    Input = node.Title,
                    Instruction = "Agent 操作",
                    Output = action.Content ?? string.Empty,
                    Provider = "Agent",
                    Accepted = true,
                    Status = node.ExecutionStatus
                });
                return null;
            }
            case "delete_node":
            {
                if (FindNode(canvas, action.Target) is not { } node) return "找不到目标节点";
                if (node.IsLocked) return "节点已锁定（定稿保护），请先解锁";
                removed.AddRange(node.Attachments.Select(attachment => attachment.Reference));
                canvas.Nodes.Remove(node);
                canvas.Edges.RemoveAll(edge => edge.SourceNodeId == node.Id || edge.TargetNodeId == node.Id);
                return null;
            }
            case "create_work_item":
            {
                if (string.IsNullOrWhiteSpace(action.Title)) return "工作树条目名称不能为空";
                var item = new WorkTreeItem
                {
                    Kind = ParseWorkTreeKind(action.WorkTreeKind),
                    Name = action.Title.Trim(),
                    Chapter = action.Chapter,
                    Version = action.Version,
                    Prompt = action.Content
                };
                if (!string.IsNullOrWhiteSpace(action.EntityTarget))
                {
                    var entity = FindEntity(canvas, action.EntityTarget);
                    if (entity is null) return "找不到工作树关联实体";
                    item.SourceEntityId = entity.Id;
                    if (!string.IsNullOrWhiteSpace(action.VariantTarget))
                    {
                        var variant = FindVariant(entity, action.VariantTarget);
                        if (variant is null) return "找不到工作树关联变体";
                        item.SourceVariantId = variant.Id;
                        if (!string.IsNullOrWhiteSpace(action.VariantVersion))
                        {
                            var version = FindVersion(variant, action.VariantVersion);
                            if (version is null) return "找不到工作树关联版本";
                            item.SourceVersionId = version.Id;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(action.ParentTarget))
                {
                    var parent = canvas.WorkTree.FirstOrDefault(candidate =>
                        candidate.Id.ToString("N").StartsWith(action.ParentTarget, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.Name, action.ParentTarget, StringComparison.OrdinalIgnoreCase));
                    if (parent is null) return "找不到工作树父条目";
                    item.ParentId = parent.Id;
                }
                canvas.WorkTree.Add(item);
                return null;
            }
            case "update_work_item":
            {
                var item = canvas.WorkTree.FirstOrDefault(candidate =>
                    candidate.Id.ToString("N").StartsWith(action.Target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, action.Target, StringComparison.OrdinalIgnoreCase));
                if (item is null) return "找不到工作树条目";
                if (!string.IsNullOrWhiteSpace(action.Title)) item.Name = action.Title.Trim();
                if (!string.IsNullOrWhiteSpace(action.Content)) item.Prompt = action.Content;
                if (!string.IsNullOrWhiteSpace(action.Chapter)) item.Chapter = action.Chapter;
                if (!string.IsNullOrWhiteSpace(action.Version)) item.Version = action.Version;
                if (!string.IsNullOrWhiteSpace(action.WorkTreeKind)) item.Kind = ParseWorkTreeKind(action.WorkTreeKind);
                if (!string.IsNullOrWhiteSpace(action.EntityTarget))
                {
                    var entity = FindEntity(canvas, action.EntityTarget);
                    if (entity is null) return "找不到工作树关联实体";
                    item.SourceEntityId = entity.Id;
                    if (!string.IsNullOrWhiteSpace(action.VariantTarget))
                    {
                        var variant = FindVariant(entity, action.VariantTarget);
                        if (variant is null) return "找不到工作树关联变体";
                        item.SourceVariantId = variant.Id;
                        if (!string.IsNullOrWhiteSpace(action.VariantVersion))
                        {
                            var version = FindVersion(variant, action.VariantVersion);
                            if (version is null) return "找不到工作树关联版本";
                            item.SourceVersionId = version.Id;
                        }
                    }
                }
                return null;
            }
            case "delete_work_item":
            {
                var item = canvas.WorkTree.FirstOrDefault(candidate =>
                    candidate.Id.ToString("N").StartsWith(action.Target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, action.Target, StringComparison.OrdinalIgnoreCase));
                if (item is null) return "找不到工作树条目";
                var removedIds = new HashSet<Guid> { item.Id };
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var child in canvas.WorkTree.Where(candidate => candidate.ParentId is Guid parentId && removedIds.Contains(parentId)))
                        changed |= removedIds.Add(child.Id);
                }
                canvas.WorkTree.RemoveAll(candidate => removedIds.Contains(candidate.Id));
                return null;
            }
            case "create_edge":
            {
                if (FindNode(canvas, action.Source) is not { } source) return "找不到起点节点";
                if (FindNode(canvas, action.Target) is not { } target) return "找不到终点节点";
                if (source.Id == target.Id) return "起点与终点是同一个节点";
                // 与界面上拖拽连线保持一致：只加边，不维护 ParentNodeId（界面拖拽也不维护）。
                if (canvas.Edges.Any(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id))
                    return "这两个节点之间已经有连线";
                canvas.Edges.Add(new WorkflowEdge { SourceNodeId = source.Id, TargetNodeId = target.Id });
                return null;
            }
            case "delete_edge":
            {
                if (FindNode(canvas, action.Source) is not { } source) return "找不到起点节点";
                if (FindNode(canvas, action.Target) is not { } target) return "找不到终点节点";
                var removedEdges = canvas.Edges.RemoveAll(edge =>
                    edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id);
                return removedEdges == 0 ? "这两个节点之间没有连线" : null;
            }
            case "create_entity":
            {
                if (string.IsNullOrWhiteSpace(action.Title)) return "实体名不能为空";
                var entityName = action.Title.Trim();
                if (canvas.Entities.Any(entity => string.Equals(entity.Name, entityName, StringComparison.OrdinalIgnoreCase)))
                    return "已存在同名实体";
                var entity = new WorkflowEntity
                {
                    Kind = ParseEntityKind(action.EntityKind),
                    Name = action.Title.Trim(),
                    Core = action.Content ?? string.Empty
                };
                entity.CreateVariant("默认");
                canvas.Entities.Add(entity);
                return null;
            }
            case "update_entity":
            {
                if (FindEntity(canvas, action.Target) is not { } entity) return "找不到目标实体";
                if (!string.IsNullOrWhiteSpace(action.Title)) entity.Name = action.Title.Trim();
                if (!string.IsNullOrWhiteSpace(action.Content)) entity.Core = action.Content;
                return null;
            }
            case "create_entity_version":
            {
                var entity = FindEntity(canvas, action.EntityTarget);
                if (entity is null) return "找不到版本所属实体";
                var variant = FindVariant(entity, action.VariantTarget);
                if (variant is null) return "找不到版本所属变体";
                if (!string.IsNullOrWhiteSpace(action.Content)) variant.Description = action.Content;
                var version = variant.Commit(string.IsNullOrWhiteSpace(action.VersionNote) ? action.Content : action.VersionNote);
                if (!string.IsNullOrWhiteSpace(action.Chapter))
                    canvas.WorkTree.Add(new WorkTreeItem
                    {
                        Kind = WorkTreeKind.Version,
                        Name = version.Label,
                        Chapter = action.Chapter.Trim(),
                        Version = version.Label,
                        Prompt = version.Note,
                        SourceEntityId = entity.Id,
            SourceVariantId = variant.Id,
            SourceVersionId = version.Id,
            SupersedesVersionId = version.SupersedesVersionId
        });
                return null;
            }
            case "delete_entity":
            {
                if (FindEntity(canvas, action.Target) is not { } entity) return "找不到目标实体";
                removed.AddRange(entity.Variants.SelectMany(variant =>
                    variant.Attachments.Concat(variant.Versions.SelectMany(version => version.Attachments)))
                    .Select(attachment => attachment.Reference));
                canvas.Entities.Remove(entity);
                return null;
            }
            case "write_file":
            {
                var path = ResolveWorkspacePath(workspaceDirectory, action.Path);
                if (path is null) return "工作文件夹未设置，或路径超出工作文件夹范围";
                // 预览阶段不能有副作用：只校验路径，不落盘。
                if (fileMode == FileWriteMode.Skip) return null;
                // 覆盖前先记快照，撤销时才能把原内容写回去。
                fileSnapshots?.Add(new FileSnapshot(path, File.Exists(path) ? File.ReadAllText(path) : null));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, action.Content ?? string.Empty);
                return null;
            }
            default:
                return $"不支持的操作 {action.Kind}";
        }
    }

    /// <summary>
    /// 审批前的只读预检：返回需要提醒用户的后果（文件会被覆盖、节点已锁定、找不到目标等）。
    /// 不做任何修改；没有可提醒的问题时返回 null。
    /// </summary>
    public static string? Precheck(AgentAction action, WorkflowCanvasState canvas, string? workspaceDirectory)
    {
        switch (action.Kind.Trim().ToLowerInvariant())
        {
            case "create_node":
                return MissingWorkTreeHint(canvas, action);
            case "update_node":
            {
                if (FindNode(canvas, action.Target) is not { } node) return "找不到目标节点";
                if (node.IsLocked) return "节点已锁定，应用会被拒绝";
                return MissingWorkTreeHint(canvas, action);
            }
            case "delete_node":
            {
                if (FindNode(canvas, action.Target) is not { } node) return "找不到目标节点";
                if (node.IsLocked) return "节点已锁定，应用会被拒绝";
                return node.Attachments.Count > 0
                    ? $"删除「{node.Title}」会同时移除 {node.Attachments.Count} 个附件"
                    : null;
            }
            case "create_edge":
            {
                if (FindNode(canvas, action.Source) is not { } source) return "找不到起点节点";
                if (FindNode(canvas, action.Target) is not { } target) return "找不到终点节点";
                if (source.Id == target.Id) return "起点与终点是同一个节点";
                return canvas.Edges.Any(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id)
                    ? "这两个节点之间已经有连线"
                    : null;
            }
            case "delete_edge":
            {
                if (FindNode(canvas, action.Source) is not { } source) return "找不到起点节点";
                if (FindNode(canvas, action.Target) is not { } target) return "找不到终点节点";
                return canvas.Edges.Any(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id)
                    ? null
                    : "这两个节点之间没有连线";
            }
            case "create_entity":
            {
                if (string.IsNullOrWhiteSpace(action.Title)) return "实体名不能为空";
                return canvas.Entities.Any(entity => string.Equals(entity.Name, action.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                    ? "已存在同名实体"
                    : null;
            }
            case "create_work_item":
            {
                if (string.IsNullOrWhiteSpace(action.Title)) return "工作树条目名称不能为空";
                if (!string.IsNullOrWhiteSpace(action.ParentTarget) && FindWorkItem(canvas, action.ParentTarget) is null)
                    return "找不到工作树父条目";
                return canvas.WorkTree.Any(item => item.ParentId == FindWorkItemId(canvas, action.ParentTarget)
                    && string.Equals(item.Name, action.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                    ? "该父条目下已存在同名工作树条目" : null;
            }
            case "update_work_item":
            case "delete_work_item":
                return FindWorkItem(canvas, action.Target) is null ? "找不到工作树条目" : null;
            case "update_entity":
                return FindEntity(canvas, action.Target) is null ? "找不到目标实体" : null;
            case "delete_entity":
            {
                if (FindEntity(canvas, action.Target) is not { } entity) return "找不到目标实体";
                var images = entity.Variants.Sum(variant =>
                    variant.Attachments.Count + variant.Versions.Sum(version => version.Attachments.Count));
                return images > 0 ? $"删除「{entity.Name}」会同时移除 {images} 张参考图" : null;
            }
            case "write_file":
            {
                var path = ResolveWorkspacePath(workspaceDirectory, action.Path);
                if (path is null) return "工作文件夹未设置，或路径超出工作文件夹范围";
                return File.Exists(path) ? "该文件已存在，应用后会覆盖原内容" : null;
            }
            default:
                return string.IsNullOrWhiteSpace(action.Kind) ? "缺少 kind 字段" : null;
        }
    }

    /// <summary>
    /// 批量预检：在画布副本上**按顺序试算**，让后面的操作能看到前面操作的效果。
    /// 否则同一批里「先建节点、再连线」的连线会被误报成「找不到端点」——
    /// 真跑起来明明是成功的，用户却会因此把正确的操作取消勾选。
    /// 试算只作用于副本，且跳过文件写入，所以预检始终是只读的。
    /// </summary>
    public static IReadOnlyList<string?> PrecheckBatch(
        IReadOnlyList<AgentAction> actions, WorkflowCanvasState canvas, string? workspaceDirectory)
    {
        var working = CanvasPreviewBuilder.Clone(canvas);
        var hints = new List<string?>(actions.Count);
        foreach (var action in actions)
        {
            hints.Add(Precheck(action, working, workspaceDirectory));
            Apply(new[] { action }, working, workspaceDirectory, FileWriteMode.Skip);
        }
        return hints;
    }

    /// <summary>
    /// 节点要关联的工作树条目还没建时给出提醒：工作树是上游，应该先建树再建节点。
    /// 只是提醒，不阻断应用——预检列表里由用户决定要不要取消勾选。
    /// </summary>
    private static string? MissingWorkTreeHint(WorkflowCanvasState canvas, AgentAction action) =>
        string.IsNullOrWhiteSpace(action.WorkTreeTarget) || FindWorkItem(canvas, action.WorkTreeTarget) is not null
            ? null
            : "要关联的工作树条目还不存在；先把章节/能力写进工作树，再建节点";

    /// <summary>
    /// 把动作里的 workTreeTarget 解析成节点的工作树锚。工作树是叙事轴的上游，
    /// 节点靠这个 id 跟随章节簇更新；章节文本只在缺失时从条目补齐，不用作文本匹配。
    /// </summary>
    private static string? AttachWorkTreeAnchor(WorkflowCanvasState canvas, string target, WorkflowNode node)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var item = FindWorkItem(canvas, target);
        if (item is null) return "找不到要关联的工作树条目";
        node.WorkTreeItemId = item.Id;
        if (string.IsNullOrWhiteSpace(node.Chapter))
            node.Chapter = item.Kind == WorkTreeKind.Chapter ? item.Name : item.Chapter;
        return null;
    }

    private static string FindInheritedChapter(WorkflowCanvasState canvas, string parentTarget)
    {
        var current = FindNode(canvas, parentTarget);
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Chapter)) return current.Chapter;
            if (IsChapterNode(current)) return current.Title;
            current = current.ParentNodeId is { } parentId
                ? canvas.Nodes.FirstOrDefault(node => node.Id == parentId)
                : null;
        }
        return string.Empty;
    }

    private static bool IsChapterNode(WorkflowNode node) =>
        node.Title.Contains("章", StringComparison.Ordinal) ||
        Regex.IsMatch(node.Title, @"第\\s*\\d+\\s*章", RegexOptions.CultureInvariant);

    /// <summary>
    /// 新节点落位：按章节分块。节点归到它所属章节的区块里，区块存在就把整块重排成网格
    /// （新节点排在末尾）；章节还没有区块时，在所有内容右侧另起一块。
    /// 这样节点一多也是「每章一个方块」，而不是一条来回折的蛇。
    /// </summary>
    private static void PlaceNewNode(WorkflowCanvasState canvas, WorkflowNode node, string parentTarget)
    {
        var parent = FindNode(canvas, parentTarget);
        if (parent is not null) node.ParentNodeId = parent.Id;

        var key = WorkflowCanvasControl.ChapterKeyOf(canvas, node);
        var blocks = WorkflowCanvasControl.ChapterBounds(canvas, canvas.Nodes);

        float blockX, blockY;
        if (key.Length > 0 && blocks.TryGetValue(key, out var block))
        {
            blockX = block.X;
            blockY = block.Y;
        }
        else
        {
            // 新章节（或未分章）：排到所有已有内容的右边；纵向对齐已有区块的顶边。
            var occupied = canvas.Nodes.Select(existing => WorkflowCanvasControl.NodeRect(canvas, existing)).ToList();
            blockX = occupied.Count == 0 ? 80f : occupied.Max(rect => rect.Right) + WorkflowCanvasControl.ChapterBlockGap;
            blockY = blocks.Count > 0
                ? blocks.Values.Min(rect => rect.Top)
                : occupied.Count == 0
                    ? 80f
                    : occupied.Min(rect => rect.Top) - WorkflowCanvasControl.ChapterBlockPadding - WorkflowCanvasControl.ChapterBlockTitleHeight;
        }

        // 临时位置放在同章已有节点的下方，重排时就排在末尾。
        var sameChapter = canvas.Nodes
            .Where(existing => WorkflowCanvasControl.ChapterKeyOf(canvas, existing) == key)
            .ToList();
        node.X = blockX + WorkflowCanvasControl.ChapterBlockPadding;
        node.Y = sameChapter.Count == 0
            ? blockY + WorkflowCanvasControl.ChapterBlockPadding + WorkflowCanvasControl.ChapterBlockTitleHeight
            : sameChapter.Max(existing => existing.Y + WorkflowCanvasControl.NodeHeightFor(canvas, existing))
                + WorkflowCanvasControl.ChapterRowGap;

        canvas.Nodes.Add(node);
        WorkflowCanvasControl.ArrangeChapter(canvas, key, blockX, blockY);
    }

    /// <summary>按短 id（Guid 前 8 位）或标题精确匹配节点；同名时视为歧义并拒绝。</summary>
    private static WorkflowNode? FindNode(WorkflowCanvasState canvas, string target)
    {
        var match = MatchByShortId(canvas.Nodes, target, node => node.Id);
        if (match is not null) return match;
        var wanted = (target ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;
        var byTitle = canvas.Nodes.Where(node => string.Equals(node.Title, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    private static WorkTreeItem? FindWorkItem(WorkflowCanvasState canvas, string target)
    {
        var match = MatchByShortId(canvas.WorkTree, target, item => item.Id);
        if (match is not null) return match;
        var wanted = (target ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;
        var matches = canvas.WorkTree.Where(item => string.Equals(item.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Guid? FindWorkItemId(WorkflowCanvasState canvas, string target) =>
        string.IsNullOrWhiteSpace(target) ? null : FindWorkItem(canvas, target)?.Id;

    /// <summary>
    /// 解析工作树种类。旧协议里的 "Skill"（角色能力）继续接受，映射到
    /// <see cref="WorkTreeKind.Ability"/>；本程序的生成技能不在工作树里。
    /// </summary>
    private static WorkTreeKind ParseWorkTreeKind(string? value) => WorkTreeItem.ParseKind(value);

    /// <summary>
    /// 把动作里的引用写进节点。<c>entityTarget</c> 是单条、可指定变体与版本；
    /// <c>entityTargets</c> 是一组名称，各自跟随实体的第一个变体与当前版本——
    /// 用来表达「这一镜出现谁、在哪、用什么」，角色/场景/道具因此不必再各占一个画布节点。
    /// </summary>
    private static string? ApplyReferences(WorkflowCanvasState canvas, AgentAction action, WorkflowNode node, bool replace)
    {
        var many = action.EntityTargets.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        var hasSingle = !string.IsNullOrWhiteSpace(action.EntityTarget);
        if (!hasSingle && many.Count == 0) return null;
        if (replace) node.References.Clear();

        if (hasSingle)
        {
            var entity = FindEntity(canvas, action.EntityTarget);
            if (entity is null) return "找不到引用实体";
            var variant = FindVariant(entity, action.VariantTarget);
            if (variant is null) return "找不到引用变体";
            var version = FindVersion(variant, action.VariantVersion);
            if (!string.IsNullOrWhiteSpace(action.VariantVersion) && version is null) return "找不到引用版本";
            AddReference(node, entity.Id, variant.Id, version?.Id);
        }

        foreach (var name in many)
        {
            var entity = FindEntity(canvas, name);
            if (entity is null) return $"找不到要引用的设定「{name}」";
            var variant = entity.Variants.FirstOrDefault();
            if (variant is null) continue;
            AddReference(node, entity.Id, variant.Id, null);
        }
        return null;
    }

    /// <summary>同一「实体 + 变体 + 版本」只挂一次，避免重复提议把引用堆起来。</summary>
    private static void AddReference(WorkflowNode node, Guid entityId, Guid variantId, Guid? versionId)
    {
        if (node.References.Any(existing =>
                existing.EntityId == entityId && existing.VariantId == variantId && existing.VariantVersionId == versionId))
            return;
        node.References.Add(new NodeReference { EntityId = entityId, VariantId = variantId, VariantVersionId = versionId });
    }

    private static WorkflowEntity? FindEntity(WorkflowCanvasState canvas, string target)
    {
        var match = MatchByShortId(canvas.Entities, target, entity => entity.Id);
        if (match is not null) return match;
        var wanted = (target ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;
        var byName = canvas.Entities.Where(entity => string.Equals(entity.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static WorkflowEntityVariant? FindVariant(WorkflowEntity entity, string target)
    {
        var match = MatchByShortId(entity.Variants, target, variant => variant.Id);
        if (match is not null) return match;
        if (string.IsNullOrWhiteSpace(target)) return entity.Variants.Count == 1 ? entity.Variants[0] : null;
        var matches = entity.Variants.Where(variant => string.Equals(variant.Name, target.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static EntityVariantVersion? FindVersion(WorkflowEntityVariant variant, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var value = target.Trim();
        var match = MatchByShortId(variant.Versions, value, version => version.Id);
        if (match is not null) return match;
        var numberText = value.TrimStart('v', 'V');
        return int.TryParse(numberText, out var number)
            ? variant.Versions.FirstOrDefault(version => version.Number == number)
            : null;
    }

    private static T? MatchByShortId<T>(IEnumerable<T> items, string target, Func<T, Guid> idSelector) where T : class
    {
        var wanted = (target ?? string.Empty).Trim();
        if (wanted.Length is < 6 or > 8 || !wanted.All(Uri.IsHexDigit)) return null;
        return items.FirstOrDefault(item => idSelector(item).ToString("N").StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析模型给的节点分类。模型不会严格照用「角色/场景/分镜/道具/成品」这几个词，
    /// 常写「人物」「主角」「场景设定」「镜头」之类，所以按关键词包含判定，认不出才退回通用。
    /// 分类仍有用：章节区块内先排主线节点、后排资源节点（角色/场景/道具），
    /// 见 <see cref="WorkflowCanvasControl.IsResourceCategory"/>。
    /// </summary>
    private static NodeCategory ParseNodeCategory(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return NodeCategory.General;
        if (Hits(text, "角色", "人物", "主角", "Character")) return NodeCategory.Character;
        if (Hits(text, "场景", "Scene")) return NodeCategory.Scene;
        if (Hits(text, "分镜", "镜头", "Storyboard")) return NodeCategory.Storyboard;
        if (Hits(text, "道具", "Prop")) return NodeCategory.Prop;
        if (Hits(text, "成品", "成片", "终稿", "Product")) return NodeCategory.Product;
        return NodeCategory.General;
    }

    private static bool Hits(string text, params string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static EntityKind ParseEntityKind(string? value) => (value ?? string.Empty).Trim() switch
    {
        "场景" => EntityKind.Scene,
        "道具" => EntityKind.Prop,
        _ => EntityKind.Character
    };

    /// <summary>
    /// 把相对路径解析到工作文件夹内。必须落在工作文件夹之下，
    /// 否则返回 null——防止模型用 .. 逃逸到任意位置写文件。
    /// </summary>
    private static string? ResolveWorkspacePath(string? workspace, string? relative)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var root = Path.GetFullPath(workspace);
            if (!Directory.Exists(root)) return null;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
