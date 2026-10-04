using System.Text.Json;
using System.Text.Json.Serialization;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>一条能出视频的路来自哪儿。</summary>
public enum VideoRouteKind
{
    /// <summary>接口站的一个视频池子：一次 HTTP 调用（提交 → 轮询 → 下载）。</summary>
    Pool,

    /// <summary>一台 ComfyUI 上的一份出视频工作流：在那台机器上跑一张节点图。</summary>
    Workflow
}

/// <summary>
/// **一条能出视频的路**。这个类型存在的唯一理由：出视频现在有两种来源，
/// 而「让用户看见全部候选」与「自动抉择」都需要一个把两者摆平的统一说法。
///
/// 它**不是**新的数据模型——池子与工作流仍然是原来的那两个类，这里只是把「二选一」
/// 这件事本身变成一个可以列出来、可以记住、可以打分的东西。
/// </summary>
public sealed record VideoRoute(VideoRouteKind Kind, SiteProfile Site, SitePool? Pool, SiteWorkflow? Workflow)
{
    public static VideoRoute OfPool(SiteProfile site, SitePool pool) => new(VideoRouteKind.Pool, site, pool, null);

    public static VideoRoute OfWorkflow(SiteProfile site, SiteWorkflow workflow) =>
        new(VideoRouteKind.Workflow, site, null, workflow);

    /// <summary>
    /// 稳定键：把用户选过的那条路记下来靠它（站点 id + 池子键 / 工作流键）。
    /// **不含站点显示名与工作流标题**——那两样会被改名，改完就认不回来了。
    /// </summary>
    [JsonIgnore]
    public string Key => Kind == VideoRouteKind.Pool
        ? $"pool|{Site.Id}|{Pool?.Key}"
        : $"flow|{Site.Id}|{Workflow?.Key}";

    [JsonIgnore]
    public string Label => Kind == VideoRouteKind.Pool
        ? $"{Pool?.Label}"
        : $"{Workflow?.Title}";

    /// <summary>选择器与列表里的人话标签：带上来源类型与站点。</summary>
    [JsonIgnore]
    public string FullLabel => (Kind == VideoRouteKind.Pool ? "接口站池子 · " : "ComfyUI 工作流 · ")
        + Label
        + (Site.DisplayName.Length > 0 ? $"（{Site.DisplayName}）" : string.Empty);

    /// <summary>这一条能不能吃首帧。三态：true / false / null（看不出来）——与参考图能力同一条规矩，不猜。</summary>
    [JsonIgnore]
    public bool? CanTakeFrame { get; init; }

    /// <summary>「能不能吃首帧」这个判断是从哪儿来的。**说不出来源的能力声明等于没有**——所以单列一项。</summary>
    [JsonIgnore]
    public string FrameSource { get; init; } = string.Empty;

    /// <summary>推荐项（服务器上这个家族自己推出来的那一份）或清单里标了推荐的池子。</summary>
    [JsonIgnore]
    public bool Recommended => Workflow?.Recommended == true;

    public string Describe()
    {
        var parts = new List<string>();
        if (Kind == VideoRouteKind.Pool && Pool is { } pool) parts.Add(pool.Describe());
        else if (Workflow is { } workflow)
        {
            parts.Add(workflow.Folder.Length > 0 ? $"家族 {workflow.Folder}" : "根目录下的工作流");
            parts.Add($"节点 {workflow.NodeCount} 个");
            if (workflow.Recommended) parts.Add("这个家族的推荐项");
            if (workflow.Note.Length > 0) parts.Add(workflow.Note);
        }

        parts.Add(CanTakeFrame switch
        {
            true => "能吃首帧（图生视频）",
            false => "只能文生视频（不吃首帧）",
            null => "吃不吃首帧看不出来"
        });
        if (FrameSource.Length > 0) parts.Add("依据：" + FrameSource);
        return string.Join(" · ", parts);
    }

    public ImageSourceChoice ToChoice() => Kind == VideoRouteKind.Pool
        ? ImageSourceChoice.OfPool(new SitePoolChoice(Site, Pool!))
        : ImageSourceChoice.OfWorkflow(new SiteWorkflowChoice(Site, Workflow!));

    /// <summary>把界面上选定的来源还原成一条路；不是出视频来源（或两者都空）时返回 null。</summary>
    public static VideoRoute? FromChoice(ImageSourceChoice? choice)
    {
        if (choice?.Pool is { } pool) return OfPool(pool.Site, pool.Pool);
        if (choice?.Workflow is { } workflow) return OfWorkflow(workflow.Site, workflow.Workflow);
        return null;
    }
}

/// <summary>自动抉择要看的那几件事实（都是调用方现场就能拿到的东西，不需要额外探测）。</summary>
public sealed record VideoRouteContext
{
    /// <summary>这一镜有没有已经出好的首帧图。</summary>
    public bool HasFirstFrame { get; init; }

    /// <summary>想要的秒数；0 表示没指定。</summary>
    public int Seconds { get; init; }

    /// <summary>这一镜的画面描述（发给模型做判断用）。</summary>
    public string Prompt { get; init; } = string.Empty;

    public string ShotTitle { get; init; } = string.Empty;
}

/// <summary>
/// 把所有「能出视频」的路列出来。
///
/// 两条刻意的做法：
/// ① **工作流按家族去重**——一台服务器上实测有 188 份视频工作流，逐个列出来不是「给选择」而是
///    把人淹掉；同一个家族里节点最少的那份（站点导入时已标成推荐）就是那个家族的正路用法。
/// ② **能力判断要说出来源**——「能吃首帧」这个结论可能来自读了工作流正文（准），
///    也可能只来自家族文件夹名里写着「图生」（是提示，不是事实）。两者混在一起会被当成同等可信，
///    所以每一条都带着 <see cref="VideoRoute.FrameSource"/>。
/// </summary>
public static class VideoRouteOptions
{
    /// <summary>同名家族里的第一份（推荐项优先）就是那个家族的代表。</summary>
    public static IReadOnlyList<VideoRoute> Build(
        IReadOnlyList<SiteProfile> sites, bool inspectWorkflowPayloads = true, int maxPayloadReads = 24)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var routes = new List<VideoRoute>();
        var reads = 0;
        foreach (var site in sites)
        {
            foreach (var pool in site.UsablePools.Where(pool => pool.IsVideo))
                routes.Add(VideoRoute.OfPool(site, pool) with
                {
                    CanTakeFrame = pool.SupportsReference,
                    FrameSource = pool.SupportsReference is null ? "池子清单里没写参考图能力" : "池子清单里写了参考图能力"
                });

            if (!site.IsComfyUi) continue;

            var families = site.VideoWorkflows
                .GroupBy(workflow => workflow.Folder.Length == 0 ? "（根目录）" : workflow.Folder)
                .OrderBy(group => group.Key, StringComparer.Ordinal);
            foreach (var family in families)
            {
                var representative = family
                    .OrderByDescending(workflow => workflow.Recommended)
                    .ThenBy(workflow => workflow.NodeCount)
                    .ThenBy(workflow => workflow.Title, StringComparer.Ordinal)
                    .First();

                var (canTakeFrame, source) = CanTakeFrameOf(representative, site, inspectWorkflowPayloads && reads < maxPayloadReads);
                if (inspectWorkflowPayloads && reads < maxPayloadReads && source.StartsWith("读了", StringComparison.Ordinal))
                    reads++;

                routes.Add(VideoRoute.OfWorkflow(site, representative) with
                {
                    CanTakeFrame = canTakeFrame,
                    FrameSource = source
                });
            }
        }

        return routes;
    }

    /// <summary>
    /// 这一条能不能吃首帧。**先读正文，读不到才退回文件夹名**——顺序不能反：
    /// 文件夹名是用户自己起的（「G视频-Wan图生」），它是个很有用的提示，但不是事实。
    /// 读正文会解一次 JSON，所以在自动抉择那条路上按份数封顶（默认 24 份），读不完的如实说「没读」。
    /// </summary>
    private static (bool? CanTakeFrame, string Source) CanTakeFrameOf(
        SiteWorkflow workflow, SiteProfile site, bool mayReadPayload)
    {
        if (mayReadPayload)
        {
            try
            {
                var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var slots = ComfyUiWorkflowBinder.Detect(payload);
                    return (slots.CanTakeImage, "读了工作流正文：它" + (slots.CanTakeImage ? "有" : "没有") + "底图入口");
                }
                return (null, "工作流正文没落盘，读不到");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or JsonException or InvalidOperationException or ArgumentException)
            {
                return (null, "工作流正文读不懂（" + error.GetType().Name + "）");
            }
        }

        var hint = HintFromName(workflow);
        return hint is null
            ? (null, "没读正文，家族名里也看不出图生还是文生")
            : (hint, "没读正文，按家族名「" + workflow.Folder + "」推的（是提示，不是事实）");
    }

    /// <summary>
    /// 从家族文件夹名与标题里看「图生 / 文生」。**只认明确的字眼**：
    /// 写着「图生」「i2v」「image to video」的按能吃首帧算，写着「文生」「t2v」的按不能算，
    /// 其余一律「看不出来」——猜错一个的代价是让用户拿到一段跟他这一镜无关的视频。
    /// </summary>
    private static bool? HintFromName(SiteWorkflow workflow)
    {
        var text = (workflow.Folder + " " + workflow.Title).ToLowerInvariant();
        // 「图生视频」里含有「图生」，所以先判图生；两边都出现的（罕见）按看不出来处理。
        var imageSide = text.Contains("图生") || text.Contains("i2v") || text.Contains("image_to_video")
            || text.Contains("image-to-video") || text.Contains("img2vid");
        var textSide = text.Contains("文生") || text.Contains("t2v") || text.Contains("text_to_video")
            || text.Contains("text-to-video") || text.Contains("txt2vid");
        if (imageSide == textSide) return null;
        return imageSide;
    }
}

/// <summary>
/// 出视频的自动抉择：两条路——**按规则挑**（本地、立刻、不花钱）与**交给模型挑**
/// （把这一镜的情况与候选清单给大模型，让它选一条，并说一句为什么）。
///
/// 为什么两种都要有：模型那一路能读懂「这一镜是人物特写，别用运镜很猛的那条」这种话，
/// 但它要一次调用、可能失败、也可能挑一个不存在的项；规则那一路永远可用、结果可预期。
/// 所以规则是**兜底**，模型是**加分项**：模型那条路一旦出任何问题，就无声地退回规则，
/// 并且如实说明「这次是按规则挑的，原因是……」，而不是假装模型挑过。
/// </summary>
public static class VideoRouteAutoPick
{
    /// <param name="Route">挑中的那一条；null 表示一条都不合适（调用方要如实说出来，不能默默拿第一条顶）。</param>
    /// <param name="Reason">为什么挑它 / 为什么挑不出来。这句话是要给用户看的，不能是空话。</param>
    /// <param name="ByModel">这一条是不是大模型挑的。</param>
    public sealed record Decision(VideoRoute? Route, string Reason, bool ByModel);

    /// <summary>按规则挑：能收首帧的优先（有首帧时）、纯文生的优先（没有首帧时）、推荐项加分、池子比工作流更可控。</summary>
    public static Decision Choose(IReadOnlyList<VideoRoute> routes, VideoRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(context);

        if (routes.Count == 0)
            return new Decision(null,
                "现在没有任何能出视频的路：既没有接口站的视频池子，也没有 ComfyUI 的视频工作流。"
                + "到「设置 → 生图与生视频」导入一个站点或一台 ComfyUI 再来。", false);

        VideoRoute? best = null;
        var bestScore = double.NegativeInfinity;
        var bestWhy = string.Empty;

        foreach (var route in routes)
        {
            var (score, why) = Score(route, context);
            if (score <= bestScore) continue;
            best = route;
            bestScore = score;
            bestWhy = why;
        }

        return best is null
            ? new Decision(null, "候选里没有一条能出视频的路。", false)
            : new Decision(best, bestWhy, false);
    }

    private static (double Score, string Why) Score(VideoRoute route, VideoRouteContext context)
    {
        var score = 0.0;
        var why = new List<string>();

        if (context.HasFirstFrame)
        {
            switch (route.CanTakeFrame)
            {
                case true:
                    score += 3;
                    why.Add("这一镜有首帧，它吃得下首帧（图生视频）");
                    break;
                case null:
                    score += 1;
                    why.Add("这一镜有首帧，而它吃不吃首帧看不出来");
                    break;
                default:
                    score -= 3;
                    why.Add("这一镜有首帧，但它只做文生视频，首帧用不上");
                    break;
            }
        }
        else
        {
            switch (route.CanTakeFrame)
            {
                case false:
                    score += 2;
                    why.Add("这一镜还没有首帧，它正好是纯文生视频");
                    break;
                case null:
                    score += 1;
                    why.Add("这一镜还没有首帧，它吃不吃首帧看不出来");
                    break;
                default:
                    why.Add("这一镜还没有首帧，它是图生视频那条（能跑，但画面会由模型自己编）");
                    break;
            }
        }

        if (route.Recommended)
        {
            score += 1;
            why.Add("它是这个家族的推荐项");
        }

        if (route.Kind == VideoRouteKind.Pool)
        {
            // 同分时池子优先：一次 HTTP 调用就能出，不用在那台服务器上排队跑节点图。
            score += 0.5;
            why.Add("它是一次接口调用，不用在 ComfyUI 上排队");
        }

        return (score, string.Join("；", why) + "。");
    }

    /// <summary>
    /// 交给大模型挑。**任何一步不对就返回 null**（调用方据此退回规则）：
    /// 模型没得用、调用失败、回答里挑的序号不在清单里、序号不是数字——全都算「模型没挑出来」。
    /// 编一个「模型选了这个」比直接说「模型没挑出来」坏得多。
    /// </summary>
    public static async Task<Decision?> ChooseByModelAsync(
        IAiChatProvider? provider,
        IReadOnlyList<VideoRoute> routes,
        VideoRouteContext context,
        CancellationToken cancellationToken = default)
    {
        if (provider is null || routes.Count == 0) return null;

        var list = new List<string>();
        for (var index = 0; index < routes.Count; index++)
            list.Add($"{index + 1}. [{routes[index].FullLabel}] {routes[index].Describe()}");

        var system = "你在给出视频选一条路。候选是别人给的清单，你**只能从里面挑一条**，不许自己发挥。\n"
            + "回答只输出一行 JSON，不要解释、不要代码块：{\"index\": 序号, \"reason\": \"一句话理由\"}\n"
            + "判断依据：这一镜有没有已经出好的首帧图（有就该用图生视频，出来的才是这一镜动起来的样子）、"
            + "候选吃不吃首帧、以及哪一条最不容易失败。拿不准就选最省事的那一条。";

        var user = $"这一镜：{context.ShotTitle}\n"
            + $"画面：{(context.Prompt.Length > 300 ? context.Prompt[..300] + "…" : context.Prompt)}\n"
            + $"有没有首帧：{(context.HasFirstFrame ? "有" : "没有")}\n"
            + $"想要的时长：{(context.Seconds > 0 ? context.Seconds + " 秒" : "没指定")}\n"
            + "候选：\n" + string.Join("\n", list);

        try
        {
            var reply = await provider.ChatAsync(
                new[]
                {
                    new AiChatMessage { Role = "system", Content = system },
                    new AiChatMessage { Role = "user", Content = user }
                },
                cancellationToken).ConfigureAwait(false);

            var (index, reason) = ParsePick(reply, routes.Count);
            return index < 0
                ? null
                : new Decision(routes[index], reason.Length > 0 ? reason : "（模型没给理由）", true);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            or InvalidOperationException or NotSupportedException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从模型的回答里抠出「第几号」与理由。**宽容地找、严格地验**：
    /// 模型常把 JSON 包在解释或代码块里，所以先按 `"index"` 找第一个整数；
    /// 但只要那个数不在 1..count 之间就算没挑出来——宁可退回规则，也不替它猜。
    /// </summary>
    internal static (int Index, string Reason) ParsePick(string? reply, int count)
    {
        if (string.IsNullOrWhiteSpace(reply) || count <= 0) return (-1, string.Empty);

        var text = reply;
        var anchor = text.IndexOf("\"index\"", StringComparison.OrdinalIgnoreCase);
        if (anchor < 0) return (-1, string.Empty);

        var digits = new string(text[(anchor + 7)..].SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var number)) return (-1, string.Empty);
        if (number < 1 || number > count) return (-1, string.Empty);

        var reason = string.Empty;
        var reasonAnchor = text.IndexOf("\"reason\"", StringComparison.OrdinalIgnoreCase);
        if (reasonAnchor >= 0)
        {
            var colon = text.IndexOf(':', reasonAnchor);
            if (colon >= 0)
            {
                var rest = text[(colon + 1)..].TrimStart();
                if (rest.StartsWith('"'))
                {
                    var end = rest.IndexOf('"', 1);
                    if (end > 1) reason = rest[1..end];
                }
                else
                {
                    var end = rest.IndexOfAny(new[] { '\n', '}', ',' });
                    reason = (end < 0 ? rest : rest[..end]).Trim();
                }
            }
        }

        return (number - 1, reason.Trim());
    }
}

/// <summary>出视频时「要不要问一句」的三种口径。</summary>
public enum VideoRouteMode
{
    /// <summary>每次都问（默认）——花钱的事，问一句最正当。</summary>
    Ask,

    /// <summary>不再问，永远用记住的那一条（= 用户说的「不用审批」）。</summary>
    Remember,

    /// <summary>不再问，每次都自动抉择（优先让大模型挑，模型挑不出来就按规则挑）。</summary>
    Auto
}

/// <summary>记住的出视频选择。**只记一个键**，键里的站点与池子/工作流都按稳定 id 存。</summary>
public sealed class VideoRoutePreference
{
    public string Mode { get; set; } = "ask";
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public VideoRouteMode ModeKind => Mode switch
    {
        "remember" => VideoRouteMode.Remember,
        "auto" => VideoRouteMode.Auto,
        _ => VideoRouteMode.Ask
    };

    public static string ModeValue(VideoRouteMode mode) => mode switch
    {
        VideoRouteMode.Remember => "remember",
        VideoRouteMode.Auto => "auto",
        _ => "ask"
    };
}

/// <summary>
/// 出视频选择的口径存在哪儿。放在用户配置目录（与更新检查缓存同一个地方）：
/// 它是「这台机器上这个人」的偏好，不该跟着项目走，也不该进仓库。
/// </summary>
public static class VideoRoutePreferenceStore
{
    private const string FileName = "video-route.json";

    public static string FileIn(string? directory = null) =>
        Path.Combine(directory ?? AppPaths.UserConfigDirectory, FileName);

    public static VideoRoutePreference Load(string? directory = null)
    {
        try
        {
            var path = FileIn(directory);
            if (!File.Exists(path)) return new VideoRoutePreference();
            return JsonSerializer.Deserialize<VideoRoutePreference>(File.ReadAllText(path))
                   ?? new VideoRoutePreference();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            // 读不出来就回到「每次都问」：这是花钱的事，宁可多问一句。
            return new VideoRoutePreference();
        }
    }

    public static void Save(VideoRoutePreference preference, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(preference);
        try
        {
            var path = FileIn(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(preference));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // 存不下就只影响「下次要不要再问」，不该让正在做的事失败。
        }
    }

    /// <summary>把记住的那条路还原出来：站点或工作流被删了就当没记住（返回 null，调用方回到问一句）。</summary>
    public static VideoRoute? Resolve(VideoRoutePreference preference, IReadOnlyList<SiteProfile> sites)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(sites);
        if (preference.Key.Length == 0) return null;

        var parts = preference.Key.Split('|');
        if (parts.Length != 3) return null;
        var site = sites.FirstOrDefault(item =>
            string.Equals(item.Id, parts[1], StringComparison.OrdinalIgnoreCase));
        if (site is null) return null;

        if (parts[0] == "pool")
        {
            var pool = site.UsablePools.FirstOrDefault(item =>
                string.Equals(item.Key, parts[2], StringComparison.OrdinalIgnoreCase));
            return pool is null ? null : VideoRoute.OfPool(site, pool);
        }

        if (parts[0] == "flow")
        {
            var workflow = site.UsableWorkflows.FirstOrDefault(item =>
                string.Equals(item.Key, parts[2], StringComparison.OrdinalIgnoreCase));
            return workflow is null ? null : VideoRoute.OfWorkflow(site, workflow);
        }

        return null;
    }
}
