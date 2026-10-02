using System.Net.Http;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Desktop;

/// <summary>大模型修正解析的结果：成功时给出报告，失败时给出可读原因与模型原文（便于排查）。</summary>
public sealed record ApiDocRepairResult(ApiDocReport? Report, string Error, string Raw)
{
    public bool Ok => Report is not null;

    public static ApiDocRepairResult Failed(string error, string raw = "") => new(null, error, raw);
}

/// <summary>
/// 本地解析不出接口时，请大模型把文档正文整理成结构化 JSON（用户要求：智能导入失败时允许调用大模型分析修正）。
///
/// 边界：
/// · **只做「整理」，不做「补全」**：提示词反复要求只写文档里确实有的内容、模型名原样抄；解析端也会逐条校验，
///   校验不过的条目会被丢掉并说明原因，而不是照单全收；
/// · **结果必须标注来源**：报告里注明这是大模型整理的结构、需要核对，不冒充本地解析结果；
/// · **失败如实报出**：模型没返回合法 JSON、返回空结构，都给出可读原因，不拿半成品去建技能。
/// </summary>
public static class ApiDocRepair
{
    /// <summary>交给模型的正文上限：够放一份接口文档，又不至于把上下文撑爆。</summary>
    public const int MaxDocumentCharacters = 12000;

    /// <summary>每个接口最多保留几个模型 / 尺寸，防止模型把整张表都塞进一条接口里。</summary>
    private const int MaxListItems = 8;

    public const string SystemPrompt =
        "你是接口文档解析器。只输出一个 JSON 对象，不要输出解释、不要 Markdown 代码块、不要多余文字。";

    /// <summary>请模型整理正文。网络与配置类错误都转成可读原因，不往上抛。</summary>
    public static async Task<ApiDocRepairResult> RepairAsync(
        string? content,
        string sourceUrl,
        IAiJsonCompleter? completer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return ApiDocRepairResult.Failed("没有可分析的文档正文：请先粘贴接口说明。");
        if (completer is null) return ApiDocRepairResult.Failed("当前没有接入大模型，无法做这一步分析。");

        var (excerpt, truncated) = Excerpt(content);
        string raw;
        try
        {
            raw = await completer.CompleteJsonAsync(SystemPrompt, BuildPrompt(excerpt, sourceUrl, truncated), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ApiDocRepairResult.Failed("调用大模型失败：" + error.Message);
        }

        // 一定要把原文带去做核对（返工 R6）：模型很容易给出原文里没有的路径或模型名。
        return FromModelJson(raw, sourceUrl, excerpt, truncated);
    }

    /// <summary>构造提示词。硬性要求写全，避免模型自由发挥。</summary>
    public static string BuildPrompt(string excerpt, string sourceUrl, bool truncated) => new StringBuilder()
        .AppendLine($"下面是「{(string.IsNullOrWhiteSpace(sourceUrl) ? "这份文档" : sourceUrl)}」的接口文档正文。请整理成这样的 JSON 并**只输出这个 JSON**：")
        .AppendLine("{")
        .AppendLine("  \"baseUrl\": \"接口根地址，带版本前缀，例如 https://host/v1；文档没写就填空串\",")
        .AppendLine("  \"title\": \"文档标题，没有就填空串\",")
        .AppendLine("  \"ops\": [")
        .AppendLine("    {\"capability\": \"TextToImage | ImageToImage | TextToVideo | ImageToVideo\",")
        .AppendLine("     \"method\": \"POST\", \"path\": \"/v1/images/generations\",")
        .AppendLine("     \"models\": [\"模型名\"], \"sizes\": [\"1024x1024\"]}")
        .AppendLine("  ],")
        .AppendLine("  \"models\": [")
        .AppendLine("    {\"name\": \"模型名，原样抄\", \"kind\": \"Image 或 Video\", \"sizes\": [\"1k\", \"2k\"]}")
        .AppendLine("  ]")
        .AppendLine("}")
        .AppendLine()
        .AppendLine("硬性要求：")
        .AppendLine("1. 只写文档里确实有的内容；文档没写模型名、没写尺寸就留空数组，绝不编造或套用默认值。")
        .AppendLine("2. 只收录**生成**接口。查状态、下载结果这类路径（例如 /v1/videos/{id}、/v1/videos/{id}/content）不要收录。")
        .AppendLine("3. 文档里若有「可用模型」表，把每个模型的类型以及它自己支持的分辨率档位填进 models。")
        .AppendLine("4. 模型名必须与文档完全一致（括号与中文后缀如「(池6)」都要保留），不要规范化、不要翻译、不要漏掉后缀。")
        .AppendLine("5. capability 只能取上面列出的四个值之一。")
        .AppendLine("6. 解析不出任何东西时，ops 与 models 都填空数组。")
        .AppendLine()
        .AppendLine(truncated ? $"（正文太长，只给了前 {MaxDocumentCharacters} 个字符）" : string.Empty)
        .AppendLine("文档正文：")
        .AppendLine("---")
        .Append(excerpt)
        .ToString();

    /// <summary>
    /// 把模型返回的 JSON 校验成报告。**纯函数**，便于离线回归。
    /// 返工 R6 后逐条与原文核对，三种情况一律不进可执行技能：
    /// · **原文里找不到的路径或模型名**（虚构）直接丢掉；
    /// · **方法 / 能力 / 类型不在支持范围内**直接拒绝，**不静默改成默认值**；
    /// · **确认不了的**（例如模型名在、但类型判不出来）保留成待确认项，只在界面列出，不建技能。
    /// </summary>
    public static ApiDocRepairResult FromModelJson(string? json, string sourceUrl, string? sourceText = null, bool truncated = false)
    {
        var body = ExtractJsonObject(json);
        if (body.Length == 0) return ApiDocRepairResult.Failed("大模型没有返回 JSON 对象。", json ?? string.Empty);

        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException error) { return ApiDocRepairResult.Failed("大模型返回的 JSON 解析失败：" + error.Message, body); }

        using (document)
        {
            var root = document.RootElement;
            var ground = (sourceText ?? string.Empty).Trim();
            var notes = new List<string> { "以下结构由大模型从文档正文中整理（不是本地解析结果）：请核对后再建技能。" };
            if (ground.Length > 0) notes.Add("路径、方法声明、模型名（按完整词）与尺寸档位都已与原文逐条核对，原文里找不到或相互矛盾的一律丢弃或列为待确认。");
            var warnings = new List<string>();
            var pending = new List<string>();
            if (truncated)
                warnings.Add($"正文超过 {MaxDocumentCharacters} 字，只把前 {MaxDocumentCharacters} 字交给了模型：靠后的内容可能没有被覆盖。");

            var ops = new List<ApiOpCandidate>();
            var models = new List<ApiModelEntry>();
            var rejected = 0;

            // 接口
            if (root.TryGetProperty("ops", out var opArray) && opArray.ValueKind == JsonValueKind.Array)
                foreach (var item in opArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { rejected++; continue; }

                    var path = ReadString(item, "path").Trim();
                    if (!path.StartsWith('/')) { rejected++; warnings.Add($"丢掉了没有合法 path 的接口：{Trim(path, 40)}"); continue; }
                    if (path.Contains('{')) { rejected++; continue; }   // 详情端点，按提示词要求不该出现
                    if (ground.Length > 0 && !PathAppearsIn(ground, path))
                    {
                        rejected++;
                        warnings.Add($"原文里找不到路径 {Trim(path, 60)}，按虚构处理已丢弃。");
                        continue;
                    }

                    // 方法必须在支持范围内：不认识的一律拒绝；没写则算「确认不了」，不静默改成 POST（返工 R6）。
                    var rawMethod = ReadString(item, "method").Trim().ToUpperInvariant();
                    if (rawMethod.Length == 0)
                    {
                        rejected++;
                        pending.Add($"接口 {Trim(path, 60)} 没写方法，需人工确认后手建。");
                        continue;
                    }
                    if (rawMethod is not ("GET" or "POST" or "PUT" or "PATCH"))
                    {
                        rejected++;
                        warnings.Add($"接口 {Trim(path, 60)} 的方法 {rawMethod} 不在支持范围内（GET/POST/PUT/PATCH），已拒绝。");
                        continue;
                    }

                    // 返工 S6：支持范围内但**与原文声明矛盾**的方法也要拦下（原文 POST、模型给 PUT 属于冲突）。
                    var declaredMethod = ApiDocAnalyzer.DeclaredMethodFor(ground, path);
                    if (declaredMethod.Length > 0 && declaredMethod != rawMethod)
                    {
                        rejected++;
                        pending.Add($"接口 {Trim(path, 60)} 在原文里声明的是 {declaredMethod}，大模型给的是 {rawMethod}：冲突，需人工确认。");
                        continue;
                    }

                    var capability = ParseCapability(ReadString(item, "capability"));
                    if (capability is null)
                    {
                        rejected++;
                        pending.Add($"接口 {Trim(path, 60)} 的用途（capability）判不出来，需人工确认后手建。");
                        continue;
                    }

                    if (ops.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                    // 返工 S6：模型名要**完整词**命中原文——「flux-1」不能因为出现在「flux-1-dev」里就算命中。
                    // 归属还要限定在**这条接口所在的段落**内：两接口分别支持 1K/4K 时，
                    // 全局命中会把 4K 算到只支持 1K 的那条接口头上。
                    var block = BlockFor(ground, path);
                    var declaredModels = ReadStringArray(item, "models");
                    var opModels = new List<string>();
                    foreach (var name in declaredModels)
                    {
                        if (ground.Length == 0) { opModels.Add(name); continue; }
                        if (AppearsInToken(block, name)) { opModels.Add(name); continue; }
                        if (AppearsInToken(ground, name))
                        {
                            // 返工 S6：全文能找到、但不在**这条接口的段落**里 → 归属无依据，
                            // 列为待确认并**不写进可执行技能**（两接口分别支持不同模型/档位时会串味）。
                            pending.Add($"模型 {name} 在原文里能找到、但不在接口 {Trim(path, 40)} 的段落内：归属待确认，未写入该接口。");
                            continue;
                        }
                        warnings.Add($"原文里找不到模型 {Trim(name, 60)}（或只是更长名字的一部分），已从该接口里去掉。");
                    }

                    var declaredSizes = ReadStringArray(item, "sizes");
                    var opSizes = new List<string>();
                    foreach (var size in declaredSizes)
                    {
                        if (ground.Length == 0 || AppearsInToken(block, size)) { opSizes.Add(size); continue; }
                        if (AppearsInToken(ground, size))
                        {
                            pending.Add($"尺寸 {size} 在原文里能找到、但不在接口 {Trim(path, 40)} 的段落内：归属待确认，未写入该接口。");
                            continue;
                        }
                        warnings.Add($"接口 {Trim(path, 60)} 上的尺寸 {Trim(size, 20)} 在原文里找不到，已去掉。");
                    }
                    var evidence = new List<string> { $"大模型整理：{rawMethod} {path}" };
                    if (opModels.Count > 0) evidence.Add($"模型：{string.Join("、", opModels)}");
                    if (opSizes.Count > 0) evidence.Add($"尺寸：{string.Join("、", opSizes)}");
                    ops.Add(new ApiOpCandidate(capability.Value, rawMethod, path, opModels, opSizes, false, string.Empty, evidence));
                }

            // 模型表
            if (root.TryGetProperty("models", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array)
                foreach (var item in modelArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                    var name = ReadString(item, "name").Trim();
                    if (name.Length < 2) { rejected++; continue; }
                    if (ground.Length > 0 && !AppearsInToken(ground, name))
                    {
                        rejected++;
                        warnings.Add($"原文里找不到模型 {Trim(name, 60)}（或只是更长名字的一部分），按虚构处理已丢弃。");
                        continue;
                    }

                    // 类型必须明确：判不出来就留成待确认项，不默认当图像（返工 R6）。
                    var kind = ParseModelKind(ReadString(item, "kind"));
                    if (kind is null)
                    {
                        pending.Add($"模型 {name} 的类型（图像 / 视频）判不出来，需人工确认后手建。");
                        continue;
                    }

                    if (models.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

                    var declaredModelSizes = ReadStringArray(item, "sizes");
                    var sizes = new List<string>();
                    if (ground.Length == 0)
                    {
                        sizes = declaredModelSizes;
                    }
                    else
                    {
                        // 返工 V6：档位必须与**这个模型**在同一处（同一行，或同一段里带尺寸标签的那一行）。
                        // 只做全文命中是不够的：另一条接口段落里的 4K 会被算到这个只支持 1K 的模型头上，
                        // 而工厂优先采用模型表，于是又造出一个打不通的池子。
                        foreach (var size in declaredModelSizes)
                        {
                            if (SizeBelongsToModel(ground, name, size)) { sizes.Add(size); continue; }
                            pending.Add($"模型 {name} 的档位 {size} 在原文里没有与它同处的依据（可能写在别的接口段落里）：已去掉，需人工确认。");
                        }
                    }

                    // 返工 V6：**已核实的接口限制优先**——模型表不能给出接口并不支持的档位。
                    // 只在有原文核对时生效：没有原文就没有「已核实」的接口限制可言，此时按模型表原样收录。
                    var declaredByOps = ground.Length == 0
                        ? new List<string>()
                        : ops
                            .Where(op => op.Models.Contains(name, StringComparer.OrdinalIgnoreCase))
                            .SelectMany(op => op.Sizes)
                            .ToList();
                    if (declaredByOps.Count > 0)
                    {
                        var before = sizes.Count;
                        sizes = sizes.Where(size => declaredByOps.Contains(size, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (sizes.Count < before)
                            pending.Add($"模型 {name} 在模型表里多写了接口并不支持的档位：已按接口实际声明收敛，需人工确认。");
                    }

                    models.Add(new ApiModelEntry(kind.Value, name, sizes,
                        $"{name}｜{(kind == Capability.TextToVideo ? "视频" : "图像")}"
                            + (sizes.Count == 0 ? "｜尺寸未声明" : "｜" + string.Join("、", sizes.Select(ApiDocAnalyzer.SizeLabel)))));
                }

            if (ops.Count == 0 && models.Count == 0)
                return ApiDocRepairResult.Failed(
                    "大模型没有从这份文档里整理出可用的接口或模型"
                    + (rejected == 0 ? "。" : $"（有 {rejected} 条内容没通过校验）。")
                    + (pending.Count == 0 ? string.Empty : " 另有待确认项：" + string.Join("；", pending.Take(3)))
                    + " 请换一段更完整的接口说明，或手动选能力后自己补路径。",
                    body);

            if (rejected > 0) warnings.Add($"另有 {rejected} 条内容没通过校验，已丢掉。");
            if (pending.Count > 0) warnings.Add($"有 {pending.Count} 条内容无法确认，已列为待确认项（不会建进技能）：{string.Join("；", pending.Take(3))}");

            var (baseUrl, baseGrounded) = ResolveGroundedBaseUrl(ReadString(root, "baseUrl"), sourceUrl, ops, ground, warnings);
            if (ground.Length > 0 && baseGrounded) notes.Add("接口地址在原文或来源里有依据。");
            var title = ReadString(root, "title").Trim();
            var report = new ApiDocReport(
                ApiDocFormat.Text, sourceUrl, baseUrl, title, ops, models, notes, warnings)
            {
                PendingItems = pending
            };
            return new ApiDocRepairResult(report, string.Empty, body);
        }
    }

    /// <summary>
    /// 取某条路径所在的文本段落（返工 S6）：从它前面最近的空行起，到后面最近的空行为止。
    /// 用来把模型名与档位限定在**这条接口周围**——文档里两条接口分别支持 1K / 4K 时，
    /// 只看全文命中会把 4K 记到只支持 1K 的那条接口上。段落取不到（无空行）时退回全文。
    /// </summary>
    private static string BlockFor(string ground, string path)
    {
        if (ground.Length == 0) return ground;

        // 优先用**带方法声明**的那次出现（文档常先在目录里列一次路径、后面才写 POST）；
        // 段落取不到（无空行）时退回全文。
        var index = IndexWithMethod(ground, path);
        if (index < 0) return ground;

        var start = ground.LastIndexOf("\n\n", Math.Max(0, index), StringComparison.Ordinal);
        start = start < 0 ? 0 : start + 2;
        var end = ground.IndexOf("\n\n", index, StringComparison.Ordinal);
        if (end < 0) end = ground.Length;
        return end > start ? ground[start..end] : ground;
    }

    /// <summary>路径第一次「同一行里带方法」的出现位置；都没有则返回第一次出现的位置。</summary>
    private static int IndexWithMethod(string ground, string path)
    {
        var first = -1;
        var searchFrom = 0;
        while (searchFrom < ground.Length)
        {
            var index = ground.IndexOf(path, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return first;
            if (first < 0) first = index;

            var lineStart = ground.LastIndexOf('\n', Math.Max(0, index - 1));
            var prefix = ground[(lineStart + 1)..index].ToUpperInvariant();
            if (prefix.Contains("POST", StringComparison.Ordinal) || prefix.Contains("PUT", StringComparison.Ordinal)
                || prefix.Contains("PATCH", StringComparison.Ordinal) || prefix.Contains("GET", StringComparison.Ordinal)
                || prefix.Contains("DELETE", StringComparison.Ordinal))
                return index;

            searchFrom = index + path.Length;
        }
        return first;
    }

    /// <summary>该值是否出现在原文里（忽略大小写）。</summary>
    private static bool AppearsIn(string sourceText, string value) =>
        value.Length > 0 && sourceText.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 完整词命中（返工 S6）：`flux-1` 不能因为出现在 `flux-1-dev` 里就算命中，
    /// 前后紧邻的字符不能是字母、数字、下划线、点或连字符。
    /// </summary>
    private static bool AppearsInToken(string sourceText, string value)
    {
        if (value.Length == 0 || sourceText.Length == 0) return false;
        var index = sourceText.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 ? '\0' : sourceText[index - 1];
            var afterIndex = index + value.Length;
            var after = afterIndex >= sourceText.Length ? '\0' : sourceText[index + value.Length];
            if (!IsWordChar(before) && !IsWordChar(after)) return true;
            var next = sourceText.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase);
            if (next <= index) break;
            index = next;
        }
        return false;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-';

    /// <summary>
    /// 某个尺寸档位是否**与这个模型写在同一处**（返工 V6）。两处都算「同处」：
    /// · 模型名所在的那一行里也写了这个档位（「可用模型」表一行一个模型时最常见）；
    /// · 同一段里带尺寸标签的那一行（<c>尺寸：1k</c> / <c>分辨率 1K</c> / <c>size: 1k</c>）。
    ///
    /// 只做全文命中是不够的：文档里 A 接口支持 1K、B 接口支持 4K 时，4K 在全文里确实存在，
    /// 于是会被算到只支持 1K 的模型头上；工厂又优先采用模型表，最后又造出打不通的池子。
    /// </summary>
    private static bool SizeBelongsToModel(string ground, string model, string size)
    {
        if (ground.Length == 0 || model.Length == 0 || size.Length == 0) return false;
        foreach (var block in ground.Split("\n\n"))
        {
            var lines = block.Split('\n');
            var declaresModel = false;
            foreach (var line in lines)
            {
                if (!AppearsInToken(line, model)) continue;
                declaresModel = true;
                if (AppearsInToken(line, size)) return true;   // 同一行
            }

            if (!declaresModel) continue;
            foreach (var line in lines)
                if (HasSizeLabel(line) && AppearsInToken(line, size)) return true;   // 同段里的尺寸行
        }

        return false;
    }

    /// <summary>这一行是不是在声明尺寸（用来判断「尺寸：1k」这类同一段内相邻的档位行）。</summary>
    private static bool HasSizeLabel(string line) =>
        line.Contains("尺寸", StringComparison.Ordinal)
        || line.Contains("分辨率", StringComparison.Ordinal)
        || line.Contains("档位", StringComparison.Ordinal)
        || line.Contains("像素", StringComparison.Ordinal)
        || line.Contains("size", StringComparison.OrdinalIgnoreCase)
        || line.Contains("resolution", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 接口地址也要有依据（返工 S6）：模型给的地址主机既不是来源主机、也没在原文里出现过时，
    /// 就**不写进执行配置**，改用来源推导出的地址并如实说明——地址会带进技能的执行配置，不能凭模型一句话。
    /// </summary>
    private static (string BaseUrl, bool Grounded) ResolveGroundedBaseUrl(
        string modelBaseUrl,
        string sourceUrl,
        IReadOnlyList<ApiOpCandidate> ops,
        string ground,
        List<string> warnings)
    {
        var fallback = ResolveBaseUrl(string.Empty, sourceUrl, ops);
        var normalized = ProviderImporter.NormalizeBaseUrl(modelBaseUrl);
        if (normalized.Length == 0) return (fallback, false);

        var host = ApiDocAnalyzer.HostOf(normalized);
        var sourceHost = ApiDocAnalyzer.HostOf(sourceUrl);
        var grounded = host.Length > 0
            && (host.Equals(sourceHost, StringComparison.OrdinalIgnoreCase)
                || (ground.Length > 0 && AppearsIn(ground, host)));
        if (!grounded)
        {
            warnings.Add($"大模型给的接口地址 {normalized} 与原文/来源对不上，已改用来源推导出的 {fallback}"
                + "（没有依据的地址不写进执行配置）。");
            return (fallback, false);
        }

        var prefix = ApiDocAnalyzer.VersionPrefixOf(ops.Select(op => op.Path));
        return (HasVersionSegment(normalized) ? normalized : normalized + prefix, true);
    }

    /// <summary>
    /// 路径是否能在原文里对上：直接找得到就算；模型把版本段并进 baseUrl（路径写成 /images/generations）时也认。
    /// </summary>
    private static bool PathAppearsIn(string sourceText, string path)
    {
        if (AppearsIn(sourceText, path)) return true;
        var withoutVersion = System.Text.RegularExpressions.Regex.Replace(path, @"^/v\d+", string.Empty);
        return withoutVersion.Length > 1 && AppearsIn(sourceText, withoutVersion);
    }

    /// <summary>取正文的一段交给模型；超长时截断并如实标记。</summary>
    public static (string Excerpt, bool Truncated) Excerpt(string content)
    {
        var text = content.Replace("\r\n", "\n").Trim();
        return text.Length <= MaxDocumentCharacters
            ? (text, false)
            : (text[..MaxDocumentCharacters], true);
    }

    /// <summary>从可能带围栏或前后说明的回复里抠出 JSON 对象。</summary>
    private static string ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start < 0 || end <= start ? string.Empty : text[start..(end + 1)];
    }

    /// <summary>
    /// 基础地址：优先用模型给的（规范化并补上版本前缀），没有就从文档地址与接口路径推。
    /// 版本前缀必须保留——真实站点丢了这个前缀请求会 404。
    /// </summary>
    private static string ResolveBaseUrl(string modelBaseUrl, string sourceUrl, IReadOnlyList<ApiOpCandidate> ops)
    {
        var prefix = ApiDocAnalyzer.VersionPrefixOf(ops.Select(op => op.Path));
        var normalized = ProviderImporter.NormalizeBaseUrl(modelBaseUrl);
        if (normalized.Length > 0) return HasVersionSegment(normalized) ? normalized : normalized + prefix;

        var authority = ApiDocAnalyzer.BaseUrlOf(sourceUrl);
        return authority.Length == 0 ? string.Empty : authority + prefix;
    }

    private static bool HasVersionSegment(string url) =>
        System.Text.RegularExpressions.Regex.IsMatch(url, @"/v\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static Capability? ParseCapability(string value)
    {
        var text = value.Trim();
        if (Enum.TryParse<Capability>(text, ignoreCase: true, out var parsed)
            && parsed is Capability.TextToImage or Capability.ImageToImage or Capability.TextToVideo or Capability.ImageToVideo)
            return parsed;
        return text switch
        {
            "文生图" or "文生图片" or "文字生成图片" => Capability.TextToImage,
            "图生图" or "图片编辑" => Capability.ImageToImage,
            "文生视频" or "文字生成视频" => Capability.TextToVideo,
            "图生视频" or "图片生成视频" => Capability.ImageToVideo,
            _ => null
        };
    }

    /// <summary>模型类型：图像 / 视频；判不出来返回 null（不默认当图像，返工 R6）。</summary>
    private static Capability? ParseModelKind(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        if (Enum.TryParse<Capability>(text, ignoreCase: true, out var parsed))
        {
            if (parsed is Capability.TextToVideo or Capability.ImageToVideo) return Capability.TextToVideo;
            if (parsed is Capability.TextToImage or Capability.ImageToImage) return Capability.TextToImage;
        }

        if (text.Contains("视频", StringComparison.Ordinal) || text.Contains("video", StringComparison.OrdinalIgnoreCase))
            return Capability.TextToVideo;
        if (text.Contains("图像", StringComparison.Ordinal) || text.Contains("图片", StringComparison.Ordinal)
            || text.Contains("image", StringComparison.OrdinalIgnoreCase))
            return Capability.TextToImage;
        return null;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return values;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = (item.GetString() ?? string.Empty).Trim();
            if (text.Length == 0 || values.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
            values.Add(text);
            if (values.Count >= MaxListItems) break;
        }
        return values;
    }

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
