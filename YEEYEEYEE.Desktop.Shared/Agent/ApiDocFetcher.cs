using System.Net.Http;
using System.Text.RegularExpressions;

namespace YEEYEEYEE.Desktop;

/// <summary>抓取说明网页的结果：成功时给出正文，失败时给出可读原因。</summary>
public sealed record ApiDocFetchResult(bool Ok, string Content, string Error)
{
    /// <summary>
    /// 这一次正文是**从哪来的**。页面本身能读时为空白；从前端渲染站点的脚本 / 接口描述文件里恢复出来时，
    /// 这里写清来源与代价（例如「可用模型表可能不在里面」）。界面必须如实显示它——
    /// 用户要核对的是这份正文，不说清出处就等于让他对着一段不知从哪来的文字下判断。
    /// </summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 抓取接口说明网页。只做一次 GET，不执行页面脚本——很多文档站是前端渲染的，
/// 抓不到内容时如实说明并让用户改贴文档文字，而不是拿一个空页面当「文档里没写接口」。
///
/// 但「如实说明」之前还有一步值得做：**顺着页面自己引用的东西再找一次**。
/// 前端渲染的站点，正文通常在它按路由拆出来的脚本里，或在同源的接口描述文件里——
/// 实测某个真实文档站的 HTML 只有 856 字节空壳，而它引用的 AboutView chunk（31KB）里
/// 就躺着完整的接口清单。能自动找回就省掉一次手工复制粘贴；找不回再退回「请粘贴」，
/// 一句都不会变成谎话。
///
/// 这个类**不含界面**，两端共用同一份。
/// </summary>
public static class ApiDocFetcher
{
    private static readonly HttpClient Default = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>剥完标签还剩多少可读文字才算「页面本身有正文」。</summary>
    private const int MinReadableText = 40;

    /// <summary>
    /// 从脚本里抠出来的正文，至少要被解析出几条接口才认。
    ///
    /// 这个门槛是**实测定的**，不是拍的：拿真实站点跑，入口包（179KB 框架代码）能被解析出
    /// 1 条接口——`POST /admin/images`，模型名是 `O.id`、`C.model`，全是变量名。
    /// 那明显是应用自己调接口的代码，不是文档。而按路由名匹配到的那份文档 chunk 给的是 3 条。
    /// 所以要求 ≥2：一份值得导入的文档本来就该描述不止一条生成接口；
    /// 判错的代价是不对称的——漏掉一次只是让用户手工粘贴一次，误报却会把一堆垃圾技能写进技能目录。
    /// </summary>
    private const int MinMinedOps = 2;

    /// <summary>恢复阶段的总时限：一个「读取网页」按钮不该让人等上一分钟。</summary>
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(20);

    public static async Task<ApiDocFetchResult> FetchAsync(string? url, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return new ApiDocFetchResult(false, string.Empty, "请先填写接口说明网页的地址。");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ApiDocFetchResult(false, string.Empty, "地址要以 http:// 或 https:// 开头。");

        var http = client ?? Default;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; YEEYEEYEE/1.0)");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ApiDocFetchResult(false, string.Empty, $"页面返回 {(int)response.StatusCode}，没能读到接口说明。");
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return new ApiDocFetchResult(false, string.Empty, "页面是空的（可能由前端脚本渲染）：请把文档里的接口段落粘到下面的输入框。");

            if (ApiDocAnalyzer.StripHtml(content).Trim().Length >= MinReadableText)
                return new ApiDocFetchResult(true, content, string.Empty);

            // 前端渲染的文档站：抓回来的是只有脚本与容器标签的空壳，正文里一个字都没有。
            // 先顺着页面引用的东西找一遍，能找到就省掉手工粘贴。
            if (await RecoverFromShellAsync(uri, content, http, cancellationToken).ConfigureAwait(false) is { } recovered)
                return recovered;

            return new ApiDocFetchResult(false, string.Empty,
                $"只抓到 {content.Length} 字节的页面外壳（正文里没有可读文字），多半是前端渲染的站点："
                + "请在浏览器里打开该文档，把接口说明复制到下面的输入框，再点「分析这段文字」。"
                + "（也顺着页面引用的脚本与同源的接口描述文件找过一遍，没找到可解析的接口说明。）");
        }
        catch (HttpRequestException error) { return new ApiDocFetchResult(false, string.Empty, $"抓取失败：{error.Message}"); }
        catch (TaskCanceledException) { return new ApiDocFetchResult(false, string.Empty, "抓取超时：请检查网络，或把文档文字粘到下面的输入框。"); }
    }

    /// <summary>
    /// 从前端渲染的空壳里把正文找回来。两条路，按可靠性排序：
    /// ①页面自己指明的接口描述文件（Swagger UI / Redoc 的 spec 地址、link rel=alternate）——那是原文，最可信；
    /// ②页面引用的脚本里抠正文（按页面路径挑最贴合的 chunk）。
    /// 每一步都只接受**能被解析出接口**的内容：宁可退回「请粘贴」，也不把一段没用的文字冒充成文档。
    /// </summary>
    private static async Task<ApiDocFetchResult?> RecoverFromShellAsync(Uri page, string html, HttpClient http, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryBudget);
        var token = budget.Token;

        try
        {
            // ① 页面自己指明的接口描述文件。
            foreach (var candidate in ApiDocShellMining.SpecCandidatesIn(html, page))
                if (await TrySpecAsync(candidate, page, http, token).ConfigureAwait(false) is { } spec) return spec;
            foreach (var path in ApiDocShellMining.ConventionalSpecPaths)
                if (await TrySpecAsync(new Uri(page, path).GetLeftPart(UriPartial.Path), page, http, token).ConfigureAwait(false) is { } spec) return spec;

            // ② 页面引用的脚本。**只用它们找出路由 chunk，不当正文用**：
            // 入口包里装的是应用自己的框架与接口调用代码，拿它当文档会捞出「POST /admin/images」这种假接口。
            var loaded = new List<(string Url, string Text)>();
            foreach (var script in ApiDocShellMining.ScriptSourcesIn(html, page).Take(ApiDocShellMining.MaxShellScripts))
                if (await GetTextAsync(script, http, token).ConfigureAwait(false) is { } text) loaded.Add((script, text));

            // 按路由名挑最贴合的 chunk（/about → AboutView-*.js）。
            var candidates = loaded
                .SelectMany(item => ApiDocShellMining.ChunkReferencesIn(item.Text))
                .Select(raw => Uri.TryCreate(page, raw, out var absolute) ? absolute : null)
                .Where(absolute => absolute is not null && string.Equals(absolute!.Host, page.Host, StringComparison.OrdinalIgnoreCase))
                .Select(absolute => absolute!.GetLeftPart(UriPartial.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(url => !loaded.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)))
                .Select(url => (Url: url, Score: ApiDocShellMining.ScoreAgainstPage(url, page)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .Take(ApiDocShellMining.MaxChunks);

            foreach (var (url, _) in candidates)
                if (await GetTextAsync(url, http, token).ConfigureAwait(false) is { } text
                    && Mine(url, text, page) is { } hit) return hit;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            // 恢复阶段出任何问题都不该盖过原本那条「请粘贴」的提示：它已经足够说明情况了。
        }

        return null;
    }

    /// <summary>
    /// 把一段按路由名匹配到的脚本当可能含正文的文本处理：剥标签 → 解析 → **解析出的接口够多才算命中**。
    /// </summary>
    private static ApiDocFetchResult? Mine(string url, string scriptText, Uri page)
    {
        var stripped = ApiDocAnalyzer.StripHtml(scriptText);
        if (stripped.Length < MinReadableText) return null;
        if (ApiDocAnalyzer.Analyze(stripped, page.ToString()).Ops.Count < MinMinedOps) return null;

        return new ApiDocFetchResult(true, stripped, string.Empty)
        {
            Note = $"页面本身是前端渲染的（HTML 里没有正文）：这份正文是从它按路由名匹配到的脚本 "
                + $"{Path.GetFileName(url)} 里恢复出来的。接口清单来自这份脚本，"
                + "但文档里的「可用模型」表**可能不在里面**——那样池子技能会改成按接口段落里的模型名分组，"
                + "请重点核对报告里的「需要你确认」；要更准的话，还是在浏览器里打开文档、把接口段落粘进来再分析一次。"
        };
    }

    private static async Task<ApiDocFetchResult?> TrySpecAsync(string url, Uri page, HttpClient http, CancellationToken cancellationToken)
    {
        if (await GetTextAsync(url, http, cancellationToken).ConfigureAwait(false) is not { } text) return null;
        if (!ApiDocShellMining.LooksLikeApiSpec(text)) return null;
        if (!ApiDocAnalyzer.Analyze(text, page.ToString()).HasAnything) return null;

        return new ApiDocFetchResult(true, text, string.Empty)
        {
            Note = $"页面本身是前端渲染的：正文改用了它指明的接口描述文件 {url}（结构化原文，比从脚本里抠出来的更准）。"
        };
    }

    private static async Task<string?> GetTextAsync(string url, HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        // 体积上限：不能因为一个页面就顺手把几 MB 的前端包整个拉下来再解析。
        if (response.Content.Headers.ContentLength is > ApiDocShellMining.MaxBytes) return null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return text.Length > ApiDocShellMining.MaxBytes ? text[..ApiDocShellMining.MaxBytes] : text;
    }
}

/// <summary>
/// 前端渲染站点的「从空壳找回正文」里那些**挑选规则**：全是纯函数，不联网。
///
/// 单独放一个类、并且放在共享层，是因为这些规则必须能离线回归：
/// 「同一份页面在两端挑出不同东西」这类问题，只有靠对规则本身的断言才发现得了，
/// 靠真机点几下是发现不了的。
/// </summary>
public static class ApiDocShellMining
{
    /// <summary>最多拉几个入口脚本。</summary>
    public const int MaxShellScripts = 3;

    /// <summary>最多再试几个候选 chunk。</summary>
    public const int MaxChunks = 3;

    /// <summary>单个文件的体积上限（同时用作字符数上限：这些文件是 ASCII 为主的 JS）。</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>同源的约定接口描述位置。不暴露这一套的站点会在解析那一步被挡掉，不会误报。</summary>
    public static IReadOnlyList<string> ConventionalSpecPaths { get; } = new[]
    {
        "/openapi.json", "/swagger.json", "/v1/openapi.json", "/api/openapi.json", "/swagger/v1/swagger.json"
    };

    private static readonly Regex ScriptSrcPattern = new(
        @"<script\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase);

    private static readonly Regex SpecUrlAttributePattern = new(
        @"\b(?:spec-url|specUrl|data-spec-url)\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase);

    private static readonly Regex AlternateJsonLinkPattern = new(
        @"<link\b[^>]*\brel\s*=\s*[""']alternate[""'][^>]*>",
        RegexOptions.IgnoreCase);

    private static readonly Regex HrefInTagPattern = new(@"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);

    /// <summary>HTML 里被引号括起来的、看起来像接口描述文件的相对 / 绝对路径。</summary>
    private static readonly Regex QuotedSpecPathPattern = new(
        @"[""']([^""'\s]*(?:openapi|swagger)[^""'\s]*\.json)[""']",
        RegexOptions.IgnoreCase);

    /// <summary>脚本里引用的其它脚本（Vite / webpack 会把路由 chunk 名写在入口包里）。</summary>
    private static readonly Regex ChunkReferencePattern = new(
        @"[""']((?:\.{0,2}/)?[A-Za-z0-9_\-\./]*[A-Za-z0-9_\-]\.js)[""']");

    /// <summary>同源脚本地址（跨域的一律不取：不能因为一个页面就把别的域上的文件拉下来）。</summary>
    public static IReadOnlyList<string> ScriptSourcesIn(string? html, Uri page)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();
        var sources = new List<string>();
        foreach (Match match in ScriptSrcPattern.Matches(html))
        {
            var raw = match.Groups[1].Value.Trim();
            if (raw.Length == 0) continue;
            if (!Uri.TryCreate(page, raw, out var absolute)) continue;
            if (!string.Equals(absolute.Host, page.Host, StringComparison.OrdinalIgnoreCase)) continue;
            var url = absolute.GetLeftPart(UriPartial.Path);
            if (!sources.Contains(url, StringComparer.OrdinalIgnoreCase)) sources.Add(url);
        }
        return sources;
    }

    /// <summary>一段脚本里引用到的其它脚本地址（原样，可能是相对路径）。</summary>
    public static IReadOnlyList<string> ChunkReferencesIn(string? scriptText)
    {
        if (string.IsNullOrWhiteSpace(scriptText)) return Array.Empty<string>();
        var chunks = new List<string>();
        foreach (Match match in ChunkReferencePattern.Matches(scriptText))
        {
            var value = match.Groups[1].Value.Trim();
            if (value.Length == 0) continue;
            if (!chunks.Contains(value, StringComparer.OrdinalIgnoreCase)) chunks.Add(value);
        }
        return chunks;
    }

    /// <summary>
    /// 候选脚本与页面路径的贴合度：文件名里带上路径段才算候选（<c>/about</c> → <c>AboutView-*.js</c>）。
    /// 对不上就是 0 分、直接不取——宁可不找，也不去把几十个前端包挨个拉下来。
    /// </summary>
    public static int ScoreAgainstPage(string chunkPath, Uri page)
    {
        if (string.IsNullOrWhiteSpace(chunkPath)) return 0;
        var name = chunkPath;
        var cut = name.LastIndexOf('/');
        if (cut >= 0) name = name[(cut + 1)..];
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        var lowered = name.ToLowerInvariant();

        var score = 0;
        foreach (var token in PathTokens(page))
            if (lowered.Contains(token, StringComparison.Ordinal)) score += token.Length;
        return score;
    }

    /// <summary>页面路径里可用来认路的词（长度 &lt; 3 的丢掉：太短会撞上无关文件名）。</summary>
    public static IReadOnlyList<string> PathTokens(Uri page)
    {
        var tokens = new List<string>();
        foreach (var segment in page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Length < 3) continue;
            if (segment.Contains('.')) continue;   // 形如 /index.html 的不是页面名
            var token = segment.ToLowerInvariant();
            if (!tokens.Contains(token, StringComparer.Ordinal)) tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>页面自己指明的接口描述文件（Redoc 的 spec-url、Swagger UI 的 url、link rel=alternate、正文里提到的 openapi.json）。</summary>
    public static IReadOnlyList<string> SpecCandidatesIn(string? html, Uri page)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();
        var found = new List<string>();

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (!Uri.TryCreate(page, raw.Trim(), out var absolute)) return;
            if (!string.Equals(absolute.Host, page.Host, StringComparison.OrdinalIgnoreCase)) return;
            var url = absolute.GetLeftPart(UriPartial.Path);
            if (!found.Contains(url, StringComparer.OrdinalIgnoreCase)) found.Add(url);
        }

        foreach (Match match in SpecUrlAttributePattern.Matches(html)) Add(match.Groups[1].Value);
        foreach (Match tag in AlternateJsonLinkPattern.Matches(html))
        {
            if (!tag.Value.Contains("json", StringComparison.OrdinalIgnoreCase)) continue;
            Add(HrefInTagPattern.Match(tag.Value) is { Success: true } href ? href.Groups[1].Value : null);
        }
        foreach (Match match in QuotedSpecPathPattern.Matches(html)) Add(match.Groups[1].Value);

        return found;
    }

    /// <summary>
    /// 是不是一份接口描述（OpenAPI / Swagger JSON）。
    /// 这一条专门用来挡「站点对任何路径都返回同一个 HTML 空壳」的情况——实测有站就是这样，
    /// 不挡的话它会一路走到解析那步，最后报一句含糊的「没解析出接口」。
    /// </summary>
    public static bool LooksLikeApiSpec(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;
        return trimmed.Contains("\"openapi\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"swagger\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"paths\"", StringComparison.OrdinalIgnoreCase);
    }
}
