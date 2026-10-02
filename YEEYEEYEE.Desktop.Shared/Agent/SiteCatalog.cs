using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 站点下的一个**池子**：一个模型乘一个档位，就是一次可执行的出图 / 出视频配置。
///
/// 为什么要有这一层：站点通常有几十个模型、每个模型又有几档分辨率，逐个建技能文件会把技能目录刷满
/// （真实的聚合站一家就有 20 个生图模型 × 3 档）。池子不是技能，是**站点下面的选项**——
/// 调用前让用户选一个，选完才拼成一次调用。所以它们只活在站点文件里，不各占一个文件。
/// </summary>
public sealed class SitePool
{
    /// <summary>模型名，原样保留文档 / 清单里的写法（含「(池6)」这类后缀，截断就变成另一个模型）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>image / video。</summary>
    public string Kind { get; set; } = "image";

    /// <summary>档位原文（1K / 2K / 4K / 720p / 5s…）；清单没声明档位时为空。</summary>
    public string Tier { get; set; } = string.Empty;

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>视频时长（秒）；图像池为 0。</summary>
    public int Seconds { get; set; }

    /// <summary>
    /// 这个池子能不能吃参考图（决定一次调用走文生图还是图生图）。
    /// **三态**：true / false / null（未知）。清单里没写这一项时是 null——
    /// 那时既不该声称它支持（会让图生图在服务端撞墙），也不该声称它不支持（会白白挡住能用的池子）。
    /// 未知就允许试，让服务端去说。
    /// </summary>
    public bool? SupportsReference { get; set; }

    public int MaxReferenceImages { get; set; }

    /// <summary>给人看的价（例如「2.5 积分」）；清单没写就空着，不猜。</summary>
    public string Price { get; set; } = string.Empty;

    /// <summary>
    /// 同一档位的**数字**单价（0 表示清单没写）。
    /// 存数字是为了能算「单价 × 张数」——出 6 张就是 6 倍的钱，那个数必须在点下去之前算得出来，
    /// 而从「2.5 积分」这种字符串里反推数字是猜。
    /// </summary>
    public double UnitPrice { get; set; }

    /// <summary>清单里被标成下线的池子：留着看得见，但不进选择器的候选。</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool IsVideo => string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase);

    /// <summary>同一站点内唯一（模型 + 档位）。</summary>
    [JsonIgnore]
    public string Key => $"{Model}|{Tier}";

    /// <summary>选择器里那一行。</summary>
    [JsonIgnore]
    public string Label => Tier.Length == 0 ? Model : $"{Model} · {Tier}";

    /// <summary>一行补充说明：价格、参考图能力、画幅。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Price.Length > 0) parts.Add(Price);
        if (Width > 0 && Height > 0) parts.Add($"{Width}×{Height}");
        if (Seconds > 0) parts.Add($"{Seconds}s");
        parts.Add(SupportsReference switch
        {
            true => MaxReferenceImages > 0 ? $"支持参考图（最多 {MaxReferenceImages} 张）" : "支持参考图",
            false => "不支持参考图",
            null => "参考图能力未知"
        });
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// 一个已登记的站点：基础地址、各类接口路径、以及它下面全部可用池子。
///
/// 一个站点一个文件（<c>skills/sites/&lt;id&gt;.json</c>），而不是每个池子一个文件——
/// 站点是用户理解与操作的单位（「这家站有哪些能用的」），池子是它下面的选项。
/// </summary>
public sealed class SiteProfile
{
    /// <summary>稳定标识（由主机名派生，例如 example）。文件名用它，改名不会搬家。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面上显示的名字；默认取主机名里有辨识度的那一段，可改。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>基础地址，含版本前缀（https://video.example.com/v1）。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>这份站点是从哪来的（文档页地址）。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>池子清单是从哪个地址拉到的；空表示来自文档里的「可用模型」表。</summary>
    public string ListSource { get; set; } = string.Empty;

    /// <summary>文生图接口路径。</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>图生图接口路径（吃参考图时走它）；清单 / 文档没写就空着。</summary>
    public string ImageEditPath { get; set; } = string.Empty;

    /// <summary>出视频接口路径。</summary>
    public string VideoPath { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>鉴权方式：bearer / x-api-key / query。</summary>
    public string AuthStyle { get; set; } = "bearer";

    /// <summary>
    /// 这个站点自己的密钥。**内存里是明文，写盘时加密**（<see cref="ProtectedApiKey"/>）。
    /// 空表示没设，调用时退回设置里的图像接口密钥。
    ///
    /// 为什么密钥要挂在站点上、而不是只用设置里那一把：**一家站一把账号**。
    /// 只有一个全局密钥的话，导入第二家站会把第一家的密钥覆盖掉，第一家那批池子从此全部 401——
    /// 而池子是站点的一部分，密钥同样是。
    /// </summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 磁盘上的加密密钥（DPAPI）。
    /// 与明文分开存是为了**能分清「没设」和「解不开」**——合成一个字段的话，
    /// 换了 Windows 账户导致解不开时，界面会说「你没填过密钥」，而用户明明填过。
    /// </summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    /// <summary>磁盘上的密钥解不开（换了 Windows 账户或机器）：界面上要如实说，不能当成「没设」。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

    [JsonIgnore]
    public bool HasApiKey => ApiKey.Length > 0;

    /// <summary>密钥状态（**绝不回显完整密钥**）。</summary>
    public string DescribeApiKey() => ApiKeyUnreadable
        ? "密钥解不开（换了 Windows 账户或机器）：重新填一次"
        : ApiKey.Length > 0
            ? $"已设置：{SecretProtector.Describe(ApiKey)}"
            : "未设置：调用时会退回设置里的「图像接口密钥」";

    public List<SitePool> Pools { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>这个站点用哪把密钥：出图 / 出视频各自的字段（留空则沿用当前选中模型的密钥）。</summary>
    public string Kind => HasVideoPools && ImagePools.Count == 0 ? "video" : "image";

    [JsonIgnore]
    public IReadOnlyList<SitePool> ImagePools => Pools.Where(pool => !pool.IsVideo).ToList();

    [JsonIgnore]
    public IReadOnlyList<SitePool> VideoPools => Pools.Where(pool => pool.IsVideo).ToList();

    [JsonIgnore]
    public bool HasVideoPools => Pools.Any(pool => pool.IsVideo);

    [JsonIgnore]
    public string Label => DisplayName.Length > 0 ? DisplayName : Id;

    /// <summary>选择器里的候选：清单里没被标下线的那些。</summary>
    [JsonIgnore]
    public IReadOnlyList<SitePool> UsablePools => Pools.Where(pool => pool.Enabled).ToList();

    /// <summary>第一段说明：这家有多少池子、清单从哪来。</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            $"生图 {ImagePools.Count} 个",
            $"视频 {VideoPools.Count} 个"
        };
        var text = $"池子：{string.Join("、", parts)}";
        if (BaseUrl.Length > 0) text += $"｜{BaseUrl}";
        text += ListSource.Length > 0 ? $"｜清单来自 {ListSource}" : "｜清单来自文档的可用模型表";
        return text;
    }
}

/// <summary>用户选定的一个池子：哪一家站、哪一个池子。跑一次出图要的「用谁、用哪档、用哪把密钥」都在这两个里。</summary>
public sealed record SitePoolChoice(SiteProfile Site, SitePool Pool);

/// <summary>
/// 站点文件（<c>skills/sites/&lt;id&gt;.json</c>）的读写。
///
/// 放在技能目录下的子目录里，而不是和技能混在一起：技能是「一步一步的流程」，站点是「一家有哪些能用的」，
/// 两种东西混在一层目录里，技能管理页与装载器都得靠文件名前缀去猜，那是迟早会出错的做法。
/// </summary>
public static class SiteCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 中文（站点名、模型名里的「号池」注释）不该被转义成 \uXXXX：这些文件是给人看、给人改的。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>站点目录：技能目录下的 sites/。</summary>
    public static string Directory => Path.Combine(SkillLibrary.Directory, "sites");

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>由地址派生站点标识：取主机名里有辨识度的那一段（video.example.com → example）。</summary>
    public static string IdFor(string? url)
    {
        var host = ApiDocAnalyzer.HostOf(url);
        if (host.Length == 0) return "site";
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // 常见的功能性前缀（www / api / video / docs…）不是站点名的一部分；
        // 只在这一段存在、且后面还有一段时才跳过它，免得把「api.example.com」认成 example 之外的怪名字。
        var skip = new[] { "www", "api", "video", "img", "image", "docs", "doc", "open", "portal" };
        var start = labels.Length > 1 && skip.Contains(labels[0], StringComparer.OrdinalIgnoreCase) ? 1 : 0;
        var picked = labels.Length > start ? labels[start] : labels[0];
        return Slug(picked);
    }

    /// <summary>
    /// 按「上次用的那一个」的印记在现有站点里把池子找回来；找不到返回 null。
    ///
    /// 找不到的可能性是真实存在的：站点被删了、池子被清单刷掉了、同名不同档。
    /// 这时候**返回 null 而不是退到第一个**——退到第一个会让人以为「上次那个」还在，
    /// 而真正发出去的是另一个模型、另一个价。
    /// </summary>
    public static SitePoolChoice? Find(IReadOnlyList<SiteProfile> sites, string? siteId, string? model, string? tier)
    {
        if (sites is null || string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(model)) return null;
        var site = sites.FirstOrDefault(item => string.Equals(item.Id, siteId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (site is null) return null;
        var key = (tier ?? string.Empty).Trim();
        var pool = site.Pools.FirstOrDefault(item =>
            string.Equals(item.Model, model.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Tier, key, StringComparison.OrdinalIgnoreCase));
        return pool is null ? null : new SitePoolChoice(site, pool);
    }

    /// <summary>默认显示名：主机名本身（例如 video.example.com）；用户可以改成更好认的名字。</summary>
    public static string DefaultDisplayNameFor(string? url)
    {
        var host = ApiDocAnalyzer.HostOf(url);
        return host.Length == 0 ? "新站点" : host;
    }

    private static string Slug(string text)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
            buffer.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = buffer.ToString().Trim('-');
        return slug.Length == 0 ? "site" : slug;
    }

    /// <summary>读取全部站点；单个文件出错只记录它，不影响其它站点。</summary>
    public static (List<SiteProfile> Sites, List<string> Errors) Load()
    {
        var sites = new List<SiteProfile>();
        var errors = new List<string>();
        if (!System.IO.Directory.Exists(Directory)) return (sites, errors);

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var site = JsonSerializer.Deserialize<SiteProfile>(File.ReadAllText(path), Options);
                if (site is null || string.IsNullOrWhiteSpace(site.Id))
                {
                    errors.Add($"{Path.GetFileName(path)}：缺少 id");
                    continue;
                }
                site.Pools ??= new List<SitePool>();
                UnprotectKey(site);
                sites.Add(site);
            }
            catch (JsonException error) { errors.Add($"{Path.GetFileName(path)}：{error.Message}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(path)}：{error.Message}"); }
        }
        return (sites, errors);
    }

    /// <summary>写入一个站点（按 Id 决定文件名，重复导入是覆盖，不新增）。</summary>
    public static bool Save(SiteProfile site, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(site);
        if (string.IsNullOrWhiteSpace(site.Id)) { error = "站点缺少标识，无法保存。"; return false; }

        try
        {
            site.UpdatedAt = DateTimeOffset.Now;
            var path = Path.Combine(EnsureDirectory(), $"{site.Id}.json");
            // 密钥加密后写盘，但内存里始终留明文：**明文才是这一份的来源**。
            // 顺手把内存也换成密文的话，下一次保存会把密文再加密一遍，密钥就废了。
            site.ProtectedApiKey = site.ApiKey.Length > 0 ? SecretProtector.Protect(site.ApiKey) : string.Empty;
            site.ApiKeyUnreadable = false;
            File.WriteAllText(path, JsonSerializer.Serialize(site, Options));
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// 把磁盘上的加密密钥解回明文。解不开（换了 Windows 账户或机器）时**如实标记**，
    /// 绝不能当成「没设」——那会让「我明明填过密钥」变成一句「你没填」，用户会去反复重填。
    /// </summary>
    private static void UnprotectKey(SiteProfile site)
    {
        // `ProtectedApiKey` 这个字段只有我们写、且只写密文，所以「解不开」就等于「真的解不开」，
        // 不需要再去猜它是不是有人手工填的明文。要手工填的话走界面填 —— 界面填的会被加密后再存。
        if (string.IsNullOrWhiteSpace(site.ProtectedApiKey)) return;
        var decrypted = SecretProtector.Unprotect(site.ProtectedApiKey);
        if (decrypted is null)
        {
            site.ApiKeyUnreadable = true;
            site.ApiKey = string.Empty;
            return;
        }
        site.ApiKey = decrypted;
    }

    /// <summary>删掉一个站点文件。</summary>
    public static bool TryDelete(SiteProfile site, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(site);
        try
        {
            var path = Path.Combine(Directory, $"{site.Id}.json");
            if (!File.Exists(path)) { error = "找不到这个站点的文件（可能已被移动或删除）。"; return false; }
            File.Delete(path);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = failure.Message;
            return false;
        }
    }
}

/// <summary>
/// 从站点自己的清单接口探测池子。
///
/// 这里做的是一件事：**把「一家有哪些模型、各自什么档位」问出来**，全程只读——
/// 探测不发任何生成请求（生成要花钱、出视频还要轮询，一个"看看有什么"的动作不该顺手起任务）。
///
/// 地址候选是「候选」不是「假设」：拉到的内容必须**能被解析成池子**才认，探不到就退回文档里的
/// 可用模型表，并把实际用上的那个来源记进站点文件（下次刷新直接用它，不再重新猜）。
/// 解析器写得宽容一些——聚合站的清单形状各家不同，但只要它把模型名与档位写出来了，就该认出来。
/// </summary>
public static class SitePoolProbe
{
    /// <summary>一次最多认多少个池子：清单可能几十个模型 × 几档，全展开会把选择器淹掉。</summary>
    public const int MaxPools = 400;

    private static readonly string[] VideoNameHints =
    {
        "seedance", "veo", "kling", "runway", "sora", "minimax", "hailuo", "wan", "pika", "vidu", "firefly-video", "cogvideo"
    };

    /// <summary>
    /// 清单地址候选，按可靠性排序：站点后台的清单接口最准（它就是要给用户看模型表的那份数据），
    /// 其次是 OpenAI 兼容的 <c>/models</c>（只有模型名、没有档位）。
    /// </summary>
    public static IReadOnlyList<string> CandidateUrls(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)) return Array.Empty<string>();
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var basePart = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        var urls = new List<string>
        {
            $"{origin}/admin/api/managed-models",
            $"{origin}/admin/api/video-presets",
            $"{origin}/admin/api/models",
            $"{basePart}/models",
            $"{origin}/v1/models"
        };
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 宽容解析：把一份清单 JSON 变成池子。
    ///
    /// 只认「模型名 + 档位」这两件事实：名字取 alias / id / name / model / key / label 里第一个非空的，
    /// 档位取 resolutions / sizes / tiers 或 durations。取不到档位就留空（不猜一档），
    /// 一个条目都认不出来时返回空清单——由调用方退回文档里的模型表。
    /// </summary>
    public static IReadOnlyList<SitePool> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SitePool>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var items = ItemsOf(document.RootElement);
            var pools = new List<SitePool>();
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var model = FirstString(item, "alias", "id", "name", "model", "model_name", "key", "label");
                if (model.Length == 0) continue;

                var kindText = FirstString(item, "type", "kind", "category", "modality");
                var isVideo = kindText.Contains("video", StringComparison.OrdinalIgnoreCase)
                    || (kindText.Length == 0 && VideoNameHints.Any(hint => model.Contains(hint, StringComparison.OrdinalIgnoreCase)));
                if (kindText.Contains("image", StringComparison.OrdinalIgnoreCase)) isVideo = false;

                var durations = StringsOf(item, "durations", "duration", "seconds");
                var tiers = isVideo ? durations : StringsOf(item, "resolutions", "sizes", "tiers", "resolution");
                // 参考图能力**只报清单里真的写了的**：写了支持就是 true，写了不支持（或给了个 0）就是 false，
                // 压根没提就是 null（未知）——未知不该被当成任何一种。
                var referenceSignals = new[]
                {
                    BoolOf(item, "image_to_image"),
                    BoolOf(item, "video_reference_enabled"),
                    BoolOf(item, "audio_reference_enabled"),
                    NumberOf(item, "max_reference_images") > 0,
                    NumberOf(item, "max_images") > 0,
                    durations.Count > 0
                };
                var referenceMentioned = item.TryGetProperty("image_to_image", out _)
                    || item.TryGetProperty("max_reference_images", out _)
                    || item.TryGetProperty("max_images", out _)
                    || item.TryGetProperty("video_reference_enabled", out _);
                bool? supportsReference = referenceSignals.Any(signal => signal) ? true
                    : referenceMentioned ? false
                    : null;
                var maxReferences = (int)Math.Max(NumberOf(item, "max_reference_images"), Math.Max(NumberOf(item, "max_images"), 0));
                var enabled = !item.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
                var priceTable = PriceTableOf(item);

                if (tiers.Count == 0)
                {
                    pools.Add(Build(model, isVideo, string.Empty, 0, supportsReference, maxReferences, enabled, priceTable));
                    continue;
                }

                foreach (var tier in tiers)
                {
                    var seconds = isVideo ? SecondsOf(tier) : 0;
                    pools.Add(Build(model, isVideo, tier, seconds, supportsReference, maxReferences, enabled, priceTable));
                }
            }

            // 去重（同一模型 + 同一档位可能被两处清单各写一遍），并限流。
            var unique = new List<SitePool>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in pools)
                if (seen.Add(pool.Key)) unique.Add(pool);
            return unique.Count <= MaxPools ? unique : unique.Take(MaxPools).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<SitePool>();
        }
    }

    private static SitePool Build(string model, bool isVideo, string tier, int seconds, bool? supportsReference, int maxReferences, bool enabled, IReadOnlyDictionary<string, (string Label, double Value)> prices)
    {
        var (width, height) = isVideo ? (0, 0) : ApiDocAnalyzer.ParseSize(tier) ?? (0, 0);
        // 按档位取价；清单只给了一个价而条目没分档时，那个价就是这个条目唯一的价格。
        var price = (Label: string.Empty, Value: 0d);
        if (prices.TryGetValue(tier, out var exact)) price = exact;
        else if (prices.Count == 1 && tier.Length == 0) price = prices.Values.First();
        return new SitePool
        {
            Model = model,
            Kind = isVideo ? "video" : "image",
            Tier = tier,
            Width = width,
            Height = height,
            Seconds = seconds,
            SupportsReference = supportsReference,
            MaxReferenceImages = maxReferences,
            Price = price.Label,
            UnitPrice = price.Value,
            Enabled = enabled
        };
    }

    /// <summary>把 <c>prices</c> / <c>duration_prices</c> 这类「档位 → 数字」的表读成「档位 → 给人看的价」。</summary>
    private static IReadOnlyDictionary<string, (string Label, double Value)> PriceTableOf(JsonElement item)
    {
        var table = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "prices", "duration_prices", "prices_agent", "prices_duration" })
        {
            if (!item.TryGetProperty(name, out var prices) || prices.ValueKind != JsonValueKind.Object) continue;
            foreach (var pair in prices.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.Number) continue;
                var value = pair.Value.GetDouble();
                // 标签与数字一起留下：数字用来算「单价 × 张数」，标签用来说清这是哪一档的价。
                if (!table.ContainsKey(pair.Name)) table[pair.Name] = ($"{value:0.##} 积分", value);
            }
            if (table.Count > 0) return table;
        }
        return table;
    }

    /// <summary>清单的条目数组：<c>{"data":[…]}</c> 与直接给数组两种形状都收。</summary>
    private static IEnumerable<JsonElement> ItemsOf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<JsonElement>();
        foreach (var name in new[] { "data", "models", "items", "list", "presets" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string FirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> StringsOf(JsonElement item, params string[] names)
    {
        var values = new List<string>();
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    var text = entry.ValueKind == JsonValueKind.String ? entry.GetString()?.Trim()
                        : entry.ValueKind == JsonValueKind.Number ? entry.GetDouble().ToString("0.##")
                        : null;
                    if (!string.IsNullOrWhiteSpace(text) && !values.Contains(text, StringComparer.OrdinalIgnoreCase)) values.Add(text);
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                // 逗号 / 顿号分隔的一行（"1K,2K,4K"）也当列表。
                foreach (var text in value.GetString()!.Split(new[] { ',', '，', '、', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!values.Contains(text, StringComparer.OrdinalIgnoreCase)) values.Add(text);
            }
            if (values.Count > 0) return values;
        }
        return values;
    }

    private static double NumberOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool BoolOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>把「5s」这类时长档位读成秒数；读不出来返回 0。</summary>
    private static int SecondsOf(string tier)
    {
        var digits = new string(tier.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var seconds) ? seconds : 0;
    }
}
