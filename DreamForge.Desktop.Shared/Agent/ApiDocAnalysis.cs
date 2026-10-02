using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamForge.Core;

namespace DreamForge.Desktop;

/// <summary>接口说明网页的形态。判定顺序：OpenAPI/Swagger JSON → HTML → 纯文本。</summary>
public enum ApiDocFormat { OpenApiJson, Html, Text }

/// <summary>
/// 从说明网页里解析出来的一条接口：调用方式、路径、可用模型、可选尺寸、是否异步、鉴权方式，以及命中判据。
/// 所有字段都是「文档里确实写了」的内容，解析不出来的就是空，不做推测填充。
/// </summary>
public sealed record ApiOpCandidate(
    Capability Capability,
    string Method,
    string Path,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Sizes,
    bool IsAsync,
    string AuthStyle,
    IReadOnlyList<string> Evidence)
{
    public bool IsVideo => Capability is Capability.TextToVideo or Capability.ImageToVideo;

    public bool IsImage => !IsVideo;

    public string CapabilityName => Capability switch
    {
        Capability.TextToImage => "文生图",
        Capability.ImageToImage => "图生图",
        Capability.TextToVideo => "文生视频",
        Capability.ImageToVideo => "图生视频",
        _ => Capability.ToString()
    };

    /// <summary>在没有模型名可用时，用路径尾段当作池名，保证子技能仍有可读名字。</summary>
    public string PoolName
    {
        get
        {
            if (Models.Count > 0) return Models[0];
            var tail = Path.TrimEnd('/').Split('/').LastOrDefault() ?? Path;
            return string.IsNullOrWhiteSpace(tail) ? CapabilityName : tail;
        }
    }
}

/// <summary>
/// 文档里「可用模型」表的一行：模型名（原样保留文档写法，含「(池6)」这类后缀）、
/// 类型（图像 / 视频），以及**该模型自己声明支持的**尺寸档位。
/// </summary>
public sealed record ApiModelEntry(
    Capability Capability,
    string Name,
    IReadOnlyList<string> Sizes,
    string Evidence)
{
    public bool IsVideo => Capability is Capability.TextToVideo or Capability.ImageToVideo;

    public string CapabilityName => Capability switch
    {
        Capability.TextToVideo => "文生视频",
        Capability.ImageToVideo => "图生视频",
        Capability.ImageToImage => "图生图",
        Capability.TextToImage => "文生图",
        _ => Capability.ToString()
    };
}

/// <summary>说明网页的解析报告。</summary>
public sealed record ApiDocReport(
    ApiDocFormat Format,
    string SourceUrl,
    string BaseUrl,
    string Title,
    IReadOnlyList<ApiOpCandidate> Ops,
    IReadOnlyList<ApiModelEntry> ModelTable,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<ApiOpCandidate> ImageOps => Ops.Where(op => op.IsImage).ToList();

    public IReadOnlyList<ApiOpCandidate> VideoOps => Ops.Where(op => op.IsVideo).ToList();

    public IReadOnlyList<ApiModelEntry> ImageModels => ModelTable.Where(entry => !entry.IsVideo).ToList();

    public IReadOnlyList<ApiModelEntry> VideoModels => ModelTable.Where(entry => entry.IsVideo).ToList();

    /// <summary>
    /// 来源命名空间（返工 R5）：同一份接口文档导入出来的技能共用它，文件名与技能 Id 都带这段前缀，
    /// 于是 A、B 两个来源的同名池子不会互相覆盖。取基础地址（其次来源地址）的主机名去 TLD：
    /// https://video.example.com/v1 → video-example。两者都取不到时用 manual。
    /// </summary>
    public string SourceId => ApiDocAnalyzer.SourceIdOf(SourceUrl, BaseUrl);

    /// <summary>完整规范化来源身份；覆盖前用它逐字核对归属（返工 U5）。</summary>
    public string SourceIdentity => ApiDocAnalyzer.SourceIdentityOf(SourceUrl, BaseUrl);

    /// <summary>
    /// 待确认项（返工 R6）：从原文里确认不了的信息（例如大模型给了模型名但类型判不出来）。
    /// 它们**不会**变成可执行技能，只在界面上列出来让人核对。
    /// </summary>
    public IReadOnlyList<string> PendingItems { get; init; } = Array.Empty<string>();

    public bool HasAnything => Ops.Count > 0 || ModelTable.Count > 0;
}

/// <summary>
/// 接口说明网页的解析（用户要求：给一个网页就自动建技能）。
///
/// 输入是抓取到的原始文本与来源地址，输出是「解析到的接口清单」。三条硬边界：
/// · **只报文档里写了的**：解析不出模型/尺寸就留空，绝不套用默认值冒充「文档里的内容」；
/// · **判不出用途就不放进清单**：路径既不像出图也不像出视频时报 warning，让用户手选而不是硬塞；
/// · **不联网**：抓取由调用方完成，本类只做纯文本解析，便于离线回归。
/// </summary>
public static class ApiDocAnalyzer
{
    private static readonly Regex PathPattern =
        new(@"/(?:v\d+/)?[A-Za-z0-9][A-Za-z0-9\-_/]*(?:generations?|edits?|images?|videos?|tasks?|predictions?|i2v|t2v|image2image|image2video|text2image|text2video)[A-Za-z0-9\-_/]*", RegexOptions.IgnoreCase);

    private static readonly Regex SizePattern =
        new(@"\b(4096x4096|2048x2048|1024x1024|1536x1024|1024x1536|1792x1024|1024x1792|1280x720|1920x1080|720x1280|1080x1920)\b", RegexOptions.IgnoreCase);

    private static readonly Regex TierPattern = new(@"\b([124])\s?[kK]\b");

    private static readonly Regex ResolutionPattern = new(@"(?<![0-9a-z])(480p|720p|1080p)", RegexOptions.IgnoreCase);

    private static readonly Regex ModelPattern =
        new(@"[""']?(?:model|model_name|modelname|model_id|模型)[""']?\s*[:=]\s*[""']?([A-Za-z0-9_\-\./:]{3,})[""']?", RegexOptions.IgnoreCase);

    /// <summary>
    /// 模型家族名：只认**带版本号那种**写法（flux-1-dev、seedance-2.0、gpt-image-2.5-sunburst(池6)）。
    ///
    /// 刻意**不接受光杆家族词**。真实文档里 runway / pika / wan 这些词会散落在中文说明里
    /// （「参考图,runway 图生视频必填 1 张」），光杆词一命中就成了「这条接口声明的模型」，
    /// 于是导入时把设置里的模型名写成 runway——这是在真实文档上实测到的错法。
    /// 光杆词应该由「model: xxx」这种键值行或文档的「可用模型」表来认，那两条路可靠得多；
    /// 这里只补「原型提到过版本号、但没有 model: 键」的那点漏。
    /// </summary>
    private static readonly Regex ModelFamilyPattern =
        new(@"\b((?:dall-e|gpt-image|flux|sd3|stable-diffusion|seedream|qwen-image|kolors|midjourney|veo|kling|runway|sora|wan|hunyuan|cogvideo|pika|seedance)(?:-[0-9a-z\.\-]+|[a-z0-9\.\-]*[0-9][a-z0-9\.\-]*))", RegexOptions.IgnoreCase);

    /// <summary>
    /// 紧跟模型名的后缀，是名字的一部分，不能截断：
    /// 「gpt-image-2.5-sunburst(池6)」「gpt-image-2(号池8)原生」里的括号与尾注都参与路由，少一段就是另一个模型。
    /// </summary>
    private static readonly Regex ModelNameSuffixPattern =
        new(@"^(?:[（(][^）)\s]{1,16}[）)]|[0-9a-z\.\-]*(?:原生|官方|beta|preview))+", RegexOptions.IgnoreCase);

    /// <summary>表格行：一行里至少两个竖线才算表格（markdown 表格或从网页表格转换来的文本）。</summary>
    private static readonly Regex TableCellSplit = new(@"\|");

    /// <summary>文档里出现的绝对地址，用于找写明的接口根地址。</summary>
    private static readonly Regex AbsoluteUrlPattern = new(@"https?://[^\s""'<>\)\]，。；]+", RegexOptions.IgnoreCase);

    /// <summary>地址是否以版本段结尾（…/v1）。</summary>
    private static readonly Regex VersionSegmentPattern = new(@"/v\d+$", RegexOptions.IgnoreCase);

    private static readonly Regex TableHeaderModel = new(@"^(model|model_name|模型|模型名)$", RegexOptions.IgnoreCase);

    private static readonly Regex TableHeaderKind = new(@"^(类型|种类|type|kind)$", RegexOptions.IgnoreCase);

    private static readonly Regex AsyncPattern =
        new(@"\b(task_id|taskId|job_id|jobId|request_id|poll|polling|async|异步|轮询|callback|webhook)\b", RegexOptions.IgnoreCase);

    private static readonly Regex BearerPattern = new(@"\b(bearer|authorization)\b", RegexOptions.IgnoreCase);
    private static readonly Regex XApiKeyPattern = new(@"x-api-key", RegexOptions.IgnoreCase);
    private static readonly Regex QueryKeyPattern = new(@"\b(api_key|apiKey|access_token|token)\b", RegexOptions.IgnoreCase);

    /// <summary>解析一段说明文本。空输入返回空报告，不抛异常。</summary>
    public static ApiDocReport Analyze(string? content, string sourceUrl)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var source = sourceUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            warnings.Add("页面内容为空：可能抓取失败或被前端脚本渲染，请改贴文档里的文字或换一个可抓取的地址。");
            return new ApiDocReport(ApiDocFormat.Text, source, BaseUrlOf(source), string.Empty,
                Array.Empty<ApiOpCandidate>(), Array.Empty<ApiModelEntry>(), notes, warnings);
        }

        var (format, text) = Normalize(content);
        notes.Add(format switch
        {
            ApiDocFormat.OpenApiJson => "识别为 OpenAPI/Swagger 结构化描述，按 paths 解析。",
            ApiDocFormat.Html => "识别为 HTML 页面，已剥离标签后按文本解析。",
            _ => "按纯文本解析。"
        });

        var title = ExtractTitle(content, format);
        // OpenAPI 是结构化描述，接口与模型都在 paths / components 里，不走纯文本那套表解析。
        List<ApiOpCandidate> ops;
        IReadOnlyList<ApiModelEntry> models;
        if (format == ApiDocFormat.OpenApiJson)
        {
            ops = ParseOpenApi(content, string.Empty, notes, warnings);
            models = Array.Empty<ApiModelEntry>();
        }
        else
        {
            ops = ParseText(text, notes, warnings, out var parsedModels);
            models = parsedModels;
        }

        // 基础地址要在接口解析之后算：文档没写根地址时靠接口路径反推版本前缀。
        var baseUrl = ExtractBaseUrl(content, format, source, ops);

        if (ops.Count == 0 && models.Count == 0)
            warnings.Add("没有解析出可用的出图或出视频接口：请确认该页面写的是接口说明（含请求路径与字段），或手动选择能力后自己补路径。");

        var imageCount = ops.Count(op => op.IsImage);
        var videoCount = ops.Count(op => op.IsVideo);
        if (imageCount > 0 || videoCount > 0)
            notes.Add($"共解析到 {ops.Count} 条接口：出图 {imageCount} 条、出视频 {videoCount} 条。");
        if (models.Count > 0)
            notes.Add($"可用模型表里读到 {models.Count} 个模型：图像 {models.Count(entry => !entry.IsVideo)} 个、视频 {models.Count(entry => entry.IsVideo)} 个。");

        return new ApiDocReport(format, source, baseUrl, title, ops, models, notes, warnings);
    }

    /// <summary>从地址推导基础地址（去掉查询串与片段）。</summary>
    public static string BaseUrlOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return string.Empty;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>从地址里取主机名（取不到返回空串）。</summary>
    public static string HostOf(string? url) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    /// 原文里为某条路径**声明**的方法；看不出来返回空串。
    /// 返工 S6：扫描该路径的**全部出现处**（文档常先在目录里列一次路径、后面才带方法写一次），
    /// 取第一个「同一行里能看出方法」的出现处；只看第一次出现会漏判，也就无从发现模型给的方法与原文冲突。
    /// </summary>
    public static string DeclaredMethodFor(string? text, string path)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path)) return string.Empty;
        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var index = text.IndexOf(path, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return string.Empty;

            // 只在本行内往前找 12 个字符（不能跨行：上一行的 GET 不属于这条路径）。
            var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
            var from = Math.Max(lineStart + 1, index - 12);
            var prefix = text[from..index].ToUpperInvariant();
            foreach (var method in new[] { "DELETE", "PATCH", "PUT", "POST", "GET" })
                if (prefix.Contains(method, StringComparison.Ordinal)) return method;

            searchFrom = index + path.Length;
        }
        return string.Empty;
    }

    /// <summary>
    /// 来源身份（返工 U5）：完整规范化来源 = 协议 + 主机 + 端口 + 基础路径，例如 <c>https://api.example.com:8443/v1</c>。
    /// 它是**归属判定的事实依据**：写进技能文件，覆盖前逐字比对，不靠人眼看着像不像。
    /// 路径也进身份：同一主机上的 /v1 与 /v2 是两套接口，导入一套不该清理另一套。
    /// </summary>
    public static string SourceIdentityOf(string? sourceUrl, string? baseUrl)
    {
        var uri = UriOf(baseUrl) ?? UriOf(sourceUrl);
        if (uri is null) return "manual";
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString();
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}";
    }

    /// <summary>
    /// 来源命名空间（技能 Id 与文件名前缀）：可读前缀 + **身份摘要**。
    /// 返工 U5 换掉了「把非字母数字都折成横线」的旧编码——那会让 <c>a.b-example.com</c> 与
    /// <c>a-b.example.com</c> 变成同一个 <c>a-b-example-com</c>，两个来源随后互相覆盖、互相清理。
    /// 摘要取自身份原文，分隔符碰撞不可能再发生；前缀只是给人看的。
    /// </summary>
    public static string SourceIdOf(string? sourceUrl, string? baseUrl)
    {
        var identity = SourceIdentityOf(sourceUrl, baseUrl);
        if (identity == "manual") return "manual";

        // 可读前缀只取主机 + 端口 + 路径（去掉协议，免得前缀里全是横线）；
        // 摘要仍取完整身份，所以 http/https 不同也照样区分得开。
        var readable = identity.Contains("://", StringComparison.Ordinal)
            ? identity[(identity.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : identity;
        var slug = Slug(readable);
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        var digest = IdentityDigest(identity);
        return slug.Length == 0 ? digest : $"{slug}-{digest}";
    }

    /// <summary>身份摘要（8 位十六进制）：稳定、无歧义，仅用于命名与归属比对。</summary>
    private static string IdentityDigest(string identity)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string Slug(string value) =>
        new string(value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray()).Trim('-');

    private static Uri? UriOf(string? url) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ? uri : null;

    private static (ApiDocFormat Format, string Text) Normalize(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && (document.RootElement.TryGetProperty("paths", out _) || document.RootElement.TryGetProperty("openapi", out _) || document.RootElement.TryGetProperty("swagger", out _)))
                    return (ApiDocFormat.OpenApiJson, content);
            }
            catch (JsonException)
            {
                // 不是合法 JSON：按 HTML/文本继续。
            }
        }

        var looksHtml = trimmed.StartsWith('<') || Regex.IsMatch(trimmed, @"<(html|body|div|pre|code|table)\b", RegexOptions.IgnoreCase);
        return (looksHtml ? ApiDocFormat.Html : ApiDocFormat.Text, looksHtml ? StripHtml(content) : content);
    }

    /// <summary>剥离脚本、样式与标签，并解码常见实体，保留换行以便后面按行取证据。</summary>
    public static string StripHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withBreaks = Regex.Replace(withoutScripts, @"<(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        var text = Regex.Replace(withBreaks, "<[^>]+>", " ");
        text = text.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        // 压缩空白但保留换行。
        var lines = text.Split('\n').Select(line => Regex.Replace(line, @"[ \t\u00a0]+", " ").Trim());
        return string.Join("\n", lines.Where(line => line.Length > 0));
    }

    private static string ExtractTitle(string content, ApiDocFormat format)
    {
        if (format == ApiDocFormat.OpenApiJson)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var title))
                    return title.GetString() ?? string.Empty;
            }
            catch (JsonException) { }
            return string.Empty;
        }

        var match = Regex.Match(content, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (match.Success) return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
        var heading = Regex.Match(content, @"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return heading.Success ? StripHtml(heading.Groups[1].Value).Trim() : string.Empty;
    }

    private static string ExtractBaseUrl(string content, ApiDocFormat format, string sourceUrl, IReadOnlyList<ApiOpCandidate> ops)
    {
        if (format == ApiDocFormat.OpenApiJson)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0
                    && servers[0].TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
                    return url.GetString()!.TrimEnd('/');
            }
            catch (JsonException) { }
        }

        // 文档里写明的接口根地址优先，**并且必须保留版本前缀**：真实聚合站的 Base URL 是
        // https://host/v1，只取 authority 会丢掉 /v1，请求就打到 https://host/images/generations（实测 404）。
        // 先看与本页同域的地址（避免把文档里某个 CDN 链接当成接口地址），再放宽到任意地址。
        var authority = BaseUrlOf(sourceUrl);
        var sameHost = VersionedBaseUrlIn(content, authority);
        if (sameHost.Length > 0) return sameHost;
        var anyHost = VersionedBaseUrlIn(content, string.Empty);
        if (anyHost.Length > 0) return anyHost;

        // 文档没写根地址时，从接口路径反推版本前缀：/v1/images/generations → 基础地址带 /v1。
        var prefix = VersionPrefixOf(ops);
        return prefix.Length == 0 ? authority : authority + prefix;
    }

    /// <summary>找出文档里带版本段的接口根地址（如 https://host/v1）；限定域名时只看该域。</summary>
    private static string VersionedBaseUrlIn(string content, string requiredAuthority)
    {
        foreach (Match match in AbsoluteUrlPattern.Matches(content))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', '，', '。', '；', ')', '）');
            var normalized = ProviderImporter.NormalizeBaseUrl(candidate);
            if (!VersionSegmentPattern.IsMatch(normalized)) continue;
            if (requiredAuthority.Length > 0
                && !string.Equals(BaseUrlOf(normalized), requiredAuthority, StringComparison.OrdinalIgnoreCase)) continue;
            return normalized;
        }
        return string.Empty;
    }

    /// <summary>接口路径的公共版本前缀（/v1、/v2…）；取不出来返回空串。</summary>
    private static string VersionPrefixOf(IReadOnlyList<ApiOpCandidate> ops) =>
        VersionPrefixOf(ops.Select(op => op.Path));

    /// <summary>
    /// 从接口路径推版本前缀（/v1、/v2…）。文档解析与「大模型修正」都靠它把基础地址补全——
    /// 真实站点的 Base URL 是 https://host/v1，丢掉前缀请求会 404。
    /// </summary>
    public static string VersionPrefixOf(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var segment = (path ?? string.Empty).TrimStart('/').Split('/').FirstOrDefault() ?? string.Empty;
            if (Regex.IsMatch(segment, @"^v\d+$", RegexOptions.IgnoreCase)) return "/" + segment.ToLowerInvariant();
        }
        return string.Empty;
    }

    /// <summary>OpenAPI：按 paths 里的 post/put 方法解析，模型名从 requestBody 的示例与 schema 里找。</summary>
    private static List<ApiOpCandidate> ParseOpenApi(string content, string baseUrl, List<string> notes, List<string> warnings)
    {
        var ops = new List<ApiOpCandidate>();
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object) return ops;

            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var method in path.Value.EnumerateObject())
                {
                    var verb = method.Name.ToUpperInvariant();
                    if (verb is not ("POST" or "PUT" or "PATCH")) continue;
                    var capability = ClassifyPath(path.Name, path.Value.GetRawText());
                    if (capability is null)
                    {
                        warnings.Add($"路径 {path.Name} 判不出是出图还是出视频，已跳过：如确认用途可在技能里手动补。");
                        continue;
                    }

                    var evidence = new List<string> { $"OpenAPI：{verb} {path.Name}" };
                    var models = ModelsFrom(method.Value.GetRawText());
                    if (models.Count == 0) models = ModelsFrom(path.Value.GetRawText());
                    if (models.Count > 0) evidence.Add($"模型：{string.Join("、", models)}");

                    var sizes = SizesFrom(method.Value.GetRawText());
                    if (sizes.Count == 0) sizes = SizesFrom(path.Value.GetRawText());
                    if (sizes.Count > 0) evidence.Add($"尺寸：{string.Join("、", sizes)}");

                    var async = AsyncPattern.IsMatch(method.Value.GetRawText());
                    ops.Add(new ApiOpCandidate(
                        capability.Value,
                        verb,
                        path.Name,
                        models,
                        sizes,
                        async,
                        DetectAuthStyle(content),
                        evidence));
                }
            }
        }
        catch (JsonException error)
        {
            warnings.Add($"OpenAPI 解析失败：{error.Message}");
        }

        if (ops.Count > 0) notes.Add($"OpenAPI 共解析出 {ops.Count} 个提交接口。");
        return ops;
    }

    /// <summary>
    /// HTML/文本：先按路径切块，再在块内找模型、尺寸、异步与鉴权线索。
    ///
    /// 三条规则，都是被真实文档逼出来的：
    /// · **状态 / 下载端点不算生成接口**：`/v1/videos/{id}`、`/v1/videos/{id}/content` 这类路径后面紧跟 `{` 占位符，
    ///   它们只用来查状态与取结果；混进清单会凭空多出接口，还会抢走旁边模型表的归属；
    /// · **归属看最近的「路径出现处」而不是最近的「不同接口」**：同一个端点常在文档里出现多次（端点清单 + 参数章节），
    ///   按出现处归属才能把参数章节并回同一条接口；
    /// · **模型表单独解析**：文档里的「可用模型」表是模型类型与档位的权威来源，直接读表比按行距离猜准得多
    ///   （表行不参与接口上下文归属，避免出图接口读到出视频的模型）。
    /// </summary>
    private static List<ApiOpCandidate> ParseText(string text, List<string> notes, List<string> warnings, out IReadOnlyList<ApiModelEntry> modelTable)
    {
        var ops = new List<ApiOpCandidate>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var table = ParseModelTable(lines);
        modelTable = table.Entries;

        // 记下全部路径出现处（同一个端点出现多次都算）。
        var occurrences = new List<(string Path, int Line, string Method)>();
        var detailEndpoints = 0;
        for (var index = 0; index < lines.Length; index++)
            foreach (Match match in PathPattern.Matches(lines[index]))
            {
                var tail = match.Index + match.Length;
                if (tail < lines[index].Length && lines[index][tail] == '{')
                {
                    // 后面紧跟占位符（{id} 之类）：查询 / 下载用的详情端点，不是生成接口。
                    detailEndpoints++;
                    continue;
                }

                var value = match.Value.TrimEnd('.', ',', ';', ')', '。', '，', '；').TrimEnd('/');
                if (value.Length == 0) continue;
                occurrences.Add((value, index, MethodBefore(lines[index], match.Index)));
            }

        if (detailEndpoints > 0)
            notes.Add($"跳过了 {detailEndpoints} 个查询 / 下载用的详情端点（路径后带 {{id}} 这类占位符），它们不是生成接口。");

        // 去重成不同的接口，并记住每个出现处属于哪一条。
        var paths = new List<(string Path, int Line, string Method)>();
        var occurrenceOwner = new List<int>();
        var ownerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in occurrences)
        {
            if (!ownerIndex.TryGetValue(occurrence.Path, out var owner))
            {
                owner = paths.Count;
                ownerIndex[occurrence.Path] = owner;
                paths.Add(occurrence);
            }

            occurrenceOwner.Add(owner);
        }

        var pathLines = occurrences.Select(item => item.Line).ToList();
        var owned = paths.Select(_ => new List<(int Line, string Text, int Anchor)>()).ToList();
        for (var index = 0; index < lines.Length; index++)
        {
            // 模型表的行由表解析负责，不参与接口上下文。
            if (table.Lines.Contains(index)) continue;
            var owner = NearestPath(pathLines, index);
            if (owner < 0) continue;
            owned[occurrenceOwner[owner]].Add((index, lines[index], pathLines[owner]));
        }

        for (var position = 0; position < paths.Count; position++)
        {
            var (path, line, method) = paths[position];
            // 只取离「本接口的某次出现」不远的行，避免文档很长时把整段正文都当成上下文。
            var context = string.Join("\n", owned[position]
                .Where(item => Math.Abs(item.Line - item.Anchor) <= 14)
                .Select(item => item.Text));
            if (context.Length == 0) context = lines[line];
            var capability = ClassifyPath(path, context);
            if (capability is null)
            {
                warnings.Add($"路径 {path} 判不出是出图还是出视频，已跳过。");
                continue;
            }

            var models = ModelsFrom(context);
            var sizes = SizesFrom(context);
            var evidence = new List<string> { $"文档路径：{method} {path}" };
            if (models.Count > 0) evidence.Add($"模型：{string.Join("、", models)}");
            if (sizes.Count > 0) evidence.Add($"尺寸：{string.Join("、", sizes)}");

            ops.Add(new ApiOpCandidate(
                capability.Value,
                method,
                path,
                models,
                sizes,
                AsyncPattern.IsMatch(context),
                DetectAuthStyle(context),
                evidence));
        }

        notes.Add(paths.Count == 0
            ? "文本里没有找到形如 /v1/.../generations 的请求路径。"
            : $"文本里找到 {paths.Count} 个接口（共 {occurrences.Count} 处提到），其中 {ops.Count} 个判定了用途。");
        return ops;
    }

    /// <summary>路径前不远处的 HTTP 方法；看不出来就按 POST（生成类接口绝大多数是 POST）。</summary>
    private static string MethodBefore(string line, int pathIndex)
    {
        var from = Math.Max(0, pathIndex - 12);
        var prefix = line[from..pathIndex].ToUpperInvariant();
        foreach (var method in new[] { "DELETE", "PATCH", "PUT", "POST", "GET" })
            if (prefix.Contains(method, StringComparison.Ordinal)) return method;
        return "POST";
    }

    /// <summary>找出离该行最近的路径；距离相同时归前一条（路径行本身距离为 0，归自己）。</summary>
    private static int NearestPath(IReadOnlyList<int> pathLines, int line)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < pathLines.Count; index++)
        {
            var distance = Math.Abs(pathLines[index] - line);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    /// <summary>
    /// 判用途：**路径优先，正文只做兜底**。路径是接口的身份，正文里的「图生视频」这类词经常是在描述
    /// 某个可选参数（例如「runway 图生视频必填 1 张」），拿它给 `/v1/videos` 定性会把通用视频接口判成图生视频。
    /// 视频优先判断：`image2video` 同时含 image，必须先判视频。
    /// </summary>
    private static Capability? ClassifyPath(string path, string context)
    {
        var lower = path.ToLowerInvariant();
        var text = context.ToLowerInvariant();

        if (lower.Contains("image2video") || lower.Contains("img2video") || lower.Contains("i2v")) return Capability.ImageToVideo;
        if (lower.Contains("text2video") || lower.Contains("t2v")) return Capability.TextToVideo;
        if (lower.Contains("video")) return Capability.TextToVideo;
        if (lower.Contains("images/edits") || lower.Contains("image2image") || lower.Contains("img2img")) return Capability.ImageToImage;
        if (lower.Contains("images/generations") || lower.Contains("text2image")) return Capability.TextToImage;
        if (lower.Contains("generations") || lower.Contains("images"))
        {
            // 泛化路径（例如 /v2/render/generations）：正文直接写出能力名时以它为准；
            // 否则看正文提到的是图像还是视频，两边都提到就不猜。
            if (text.Contains("图生视频")) return Capability.ImageToVideo;
            if (text.Contains("文生视频")) return Capability.TextToVideo;
            if (text.Contains("图生图")) return Capability.ImageToImage;
            if (text.Contains("文生图")) return Capability.TextToImage;

            var mentionsVideo = text.Contains("视频") || text.Contains("veo") || text.Contains("kling") || text.Contains("sora") || text.Contains("seedance");
            var mentionsImage = text.Contains("图片") || text.Contains("出图") || text.Contains("图生图") || text.Contains("图像");
            if (mentionsVideo && mentionsImage) return null;
            if (mentionsVideo) return Capability.TextToVideo;
            if (mentionsImage) return Capability.TextToImage;
            return null;
        }
        return null;
    }

    private sealed record ModelTableParse(IReadOnlyList<ApiModelEntry> Entries, HashSet<int> Lines);

    /// <summary>
    /// 解析文档里的「可用模型」表。识别方式是：连续的表行（一行至少两个竖线）里存在一行表头带 model / 模型 列；
    /// 之后每行的 model 列是模型名（原样保留，含「(池6)」这类后缀），类型列给出图像 / 视频，
    /// 整行里出现的档位就是**该模型自己声明支持的**尺寸。
    ///
    /// 这条路径比按行距离猜可靠得多：文档常把所有模型列在一张表里，图像在前视频在后，
    /// 按距离猜会把图像模型算到出视频接口头上（真实文档上已经踩过）。
    /// 判不出类型的行不收录——宁可少几个池子，也不要建出调用不通的技能。
    /// </summary>
    private static ModelTableParse ParseModelTable(string[] lines)
    {
        var entries = new List<ApiModelEntry>();
        var tableLines = new HashSet<int>();
        var index = 0;
        while (index < lines.Length)
        {
            if (!IsTableRow(lines[index])) { index++; continue; }

            var block = new List<int>();
            while (index < lines.Length && IsTableRow(lines[index])) { block.Add(index); index++; }

            var rows = block.Select(lineIndex => SplitCells(lines[lineIndex])).ToList();
            if (rows.Count == 0) continue;

            // 表头只认第一行。markdown 表格的表头永远在第一行；放宽到「任意一行有 model 格」会把
            // 参数表里那行「| model | string | 必填 | … |」当成表头，于是 prompt / seconds /
            // input_reference 这些参数名会被当成模型名建出池子（真实文档上踩过）。
            var nameColumn = -1;
            for (var column = 0; column < rows[0].Count; column++)
            {
                if (!TableHeaderModel.IsMatch(rows[0][column])) continue;
                nameColumn = column;
                break;
            }

            if (nameColumn < 0) continue;
            var kindColumn = -1;
            for (var column = 0; column < rows[0].Count; column++)
                if (TableHeaderKind.IsMatch(rows[0][column])) { kindColumn = column; break; }

            var added = 0;
            for (var row = 1; row < rows.Count; row++)
            {
                var cells = rows[row];
                if (cells.Count == 0 || cells.All(IsSeparatorCell)) continue;
                var name = nameColumn >= 0 && nameColumn < cells.Count ? cells[nameColumn] : cells[0];
                if (name.Length < 2 || IsSeparatorCell(name)) continue;

                var rowText = string.Join(" ", cells);
                var kind = kindColumn >= 0 && kindColumn < cells.Count
                    ? ClassifyModelKind(cells[kindColumn])
                    : ClassifyModelKind(rowText);
                if (kind is null) continue;

                var sizes = TiersFrom(rowText);
                entries.Add(new ApiModelEntry(kind.Value, name, sizes,
                    $"{name}｜{(kind == Capability.TextToVideo ? "视频" : "图像")}"
                        + (sizes.Count == 0 ? "｜尺寸未声明" : "｜" + string.Join("、", sizes.Select(SizeLabel)))));
                added++;
            }

            if (added > 0) foreach (var lineIndex in block) tableLines.Add(lineIndex);
        }

        // 同一个模型在表里只保留第一条（有些文档会按能力分行重复列出）。
        var deduped = new List<ApiModelEntry>();
        foreach (var entry in entries)
            if (!deduped.Any(existing => string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
                deduped.Add(entry);
        return new ModelTableParse(deduped, tableLines);
    }

    private static bool IsTableRow(string line) => line.Count(ch => ch == '|') >= 2;

    private static List<string> SplitCells(string line)
    {
        var cells = TableCellSplit.Split(line).Select(cell => cell.Trim()).ToList();
        while (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
        while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    /// <summary>markdown 表格的分隔行（--- / :---:）。</summary>
    private static bool IsSeparatorCell(string cell) =>
        cell.Length > 0 && cell.All(ch => ch is '-' or ':' or ' ');

    /// <summary>模型表里的类型列：图像 / 视频；两样都写或都看不出时返回 null（不收录）。</summary>
    private static Capability? ClassifyModelKind(string text)
    {
        var lower = text.ToLowerInvariant();
        var video = lower.Contains("视频") || lower.Contains("video");
        var image = lower.Contains("图像") || lower.Contains("图片") || lower.Contains("image");
        if (video && image) return text.Contains("视频") && text.IndexOf("视频", StringComparison.Ordinal) < text.IndexOf("图", StringComparison.Ordinal)
            ? Capability.TextToVideo
            : Capability.TextToImage;
        if (video) return Capability.TextToVideo;
        if (image) return Capability.TextToImage;
        return null;
    }

    /// <summary>整行里出现的尺寸档位：1k / 2k / 4k 与 480p / 720p / 1080p，按出现顺序去重。</summary>
    private static List<string> TiersFrom(string text)
    {
        var tiers = new List<(int Index, string Value)>();
        foreach (Match match in TierPattern.Matches(text))
            tiers.Add((match.Index, match.Groups[1].Value + "k"));
        foreach (Match match in ResolutionPattern.Matches(text))
            tiers.Add((match.Index, match.Groups[1].Value.ToLowerInvariant()));
        return tiers
            .OrderBy(item => item.Index)
            .Select(item => item.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ModelsFrom(string context)
    {
        var models = new List<string>();
        foreach (Match match in ModelPattern.Matches(context))
        {
            var value = match.Groups[1].Value.Trim().Trim('"', '\'', ',');
            if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            value += SuffixAfter(context, match.Index + match.Length);
            if (!models.Contains(value, StringComparer.OrdinalIgnoreCase)) models.Add(value);
        }

        foreach (Match match in ModelFamilyPattern.Matches(context))
        {
            var value = (match.Value + SuffixAfter(context, match.Index + match.Length)).Trim();
            if (!models.Contains(value, StringComparer.OrdinalIgnoreCase)) models.Add(value);
        }

        return models.Take(8).ToList();
    }

    /// <summary>取紧跟其后的名字后缀（括号说明与尾注）——它们是模型名的一部分，截断就变成另一个模型。</summary>
    private static string SuffixAfter(string text, int index)
    {
        if (index <= 0 || index >= text.Length) return string.Empty;
        var match = ModelNameSuffixPattern.Match(text[index..]);
        return match.Success ? match.Value : string.Empty;
    }

    private static List<string> SizesFrom(string context)
    {
        var sizes = new List<string>();
        foreach (Match match in SizePattern.Matches(context))
        {
            var value = match.Value.ToLowerInvariant();
            if (!sizes.Contains(value, StringComparer.OrdinalIgnoreCase)) sizes.Add(value);
        }

        foreach (Match match in TierPattern.Matches(context))
        {
            // 档位保留文档里的写法（1k / 2k / 4k）：池子技能名要能对上「生图池1 1K」这种叫法。
            var tier = match.Groups[1].Value + "k";
            if (!sizes.Contains(tier, StringComparer.OrdinalIgnoreCase)) sizes.Add(tier);
        }

        foreach (Match match in ResolutionPattern.Matches(context))
        {
            var resolution = match.Groups[1].Value.ToLowerInvariant();
            if (!sizes.Contains(resolution, StringComparer.OrdinalIgnoreCase)) sizes.Add(resolution);
        }

        return sizes.Take(8).ToList();
    }

    private static string DetectAuthStyle(string context)
    {
        if (XApiKeyPattern.IsMatch(context)) return "x-api-key 头";
        if (BearerPattern.IsMatch(context)) return "Authorization: Bearer";
        if (QueryKeyPattern.IsMatch(context)) return "api_key 参数";
        return string.Empty;
    }

    /// <summary>把尺寸文本折算成画幅；无法折算时返回 null，由调用方保留空值。</summary>
    public static (int Width, int Height)? ParseSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var value = size.Trim().ToLowerInvariant();
        var match = Regex.Match(value, @"^(\d{3,4})x(\d{3,4})$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var width) && int.TryParse(match.Groups[2].Value, out var height))
            return (width, height);
        return value switch
        {
            "1k" => (1024, 1024),
            "2k" => (2048, 2048),
            "4k" => (4096, 4096),
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "480p" => (854, 480),
            _ => null
        };
    }

    /// <summary>
    /// 给子技能用的尺寸标签，尽量沿用文档里的叫法：显式 WxH 原样写；「720p / 1080p」这类保留分辨率叫法；
    /// 「1k / 2k」这类正方形档位写成「1K / 2K」。折算不出来就把原文照搬，不猜。
    /// </summary>
    public static string SizeLabel(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return string.Empty;
        var value = size.Trim().ToLowerInvariant();
        var parsed = ParseSize(value);
        if (parsed is null) return size.Trim();
        if (value is "480p" or "720p" or "1080p") return value;
        // 正方形且是 1024 的整数倍：写成档位（1K / 2K / 4K），与用户对「生图池1 1K」的叫法一致。
        if (parsed.Value.Width == parsed.Value.Height && parsed.Value.Width % 1024 == 0)
            return $"{parsed.Value.Width / 1024}K";
        return $"{parsed.Value.Width}x{parsed.Value.Height}";
    }

    /// <summary>把说明文本压成一段可放进技能描述的证据摘要。</summary>
    public static string Summarize(IReadOnlyList<string> evidence, int max = 200)
    {
        var text = string.Join("；", evidence);
        return text.Length <= max ? text : text[..max] + "…";
    }

    internal static string Describe(ApiOpCandidate op) => new StringBuilder()
        .Append(op.CapabilityName).Append(' ').Append(op.Method).Append(' ').Append(op.Path)
        .ToString();
}
