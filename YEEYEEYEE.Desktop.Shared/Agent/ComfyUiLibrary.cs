using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>拉取进度：已经处理完几份、共几份、正在处理哪一份。</summary>
public sealed record ComfyUiLibraryProgress(int Done, int Total, string Current)
{
    public string Describe() => Total == 0
        ? "正在读取工作流清单…"
        : $"正在转换工作流 {Done}/{Total}：{Current}";
}

/// <summary>拉下来的工作流清单（正文在内存里，落盘由 <see cref="ComfyUiLibrary.Install"/> 负责）。</summary>
public sealed record ComfyUiLibraryResult(
    string SiteId,
    string BaseUrl,
    IReadOnlyList<SiteWorkflow> Workflows,
    IReadOnlyDictionary<string, string> Payloads,
    IReadOnlyList<string> Notes)
{
    public int Converted => Workflows.Count(workflow => workflow.Converted);

    public int Failed => Workflows.Count(workflow => !workflow.Converted);
}

/// <summary>
/// 读一份已存好的工作流正文并认出槽位——用于在选择器里**事先**说清这份工作流能收到什么。
///
/// 为什么要在选之前就说：认不出收提示词的位置时，选它出图会以失败告终（那是正确的，
/// 见 <c>ComfyUiImageProvider</c>）；与其让人跑一次才知道，不如在挑选那一刻就把话说清楚。
/// </summary>
public static class ComfyUiWorkflowInspector
{
    public static (ComfyUiWorkflowSlots? Slots, string Error) Inspect(SiteProfile site, SiteWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(workflow);

        if (workflow.PayloadFile.Length == 0) return (null, "这份没有正文（导入时没转成，见它自己的说明）");

        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (payload is null) return (null, "正文文件读不到（可能被清掉了），重新导入一次即可");

        try
        {
            return (ComfyUiWorkflowBinder.Detect(payload), string.Empty);
        }
        catch (Exception error)
        {
            return (null, $"形状读不懂（{error.GetType().Name}）：{error.Message}");
        }
    }
}

/// <summary>
/// 把某台 ComfyUI 站点设为「当前那一台」。
///
/// 设置里的 ComfyUI 地址与 checkpoint 是**站点文件的投影**，不是另一份真相：
/// 一台服务器上的工作流只有它自己能跑（装了哪些节点、底模叫什么，别人不知道），
/// 所以选中某份工作流之后，配置里那两项必须跟着指向它，否则请求会打到另一台上去。
/// 这也是「多台 ComfyUI」唯一需要的机制——不需要再单独存一个「当前是哪台」。
/// </summary>
public static class ComfyUiSiteActivation
{
    public static bool Activate(SiteProfile site, out string error)
    {
        ArgumentNullException.ThrowIfNull(site);
        error = string.Empty;
        if (!site.IsComfyUi || site.BaseUrl.Length == 0)
        {
            error = $"「{site.Label}」不是一台可用的 ComfyUI 站点（缺少地址）。";
            return false;
        }

        var config = AiProviderSettings.Load();
        if (string.Equals(config.ComfyUiBaseUrl, site.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(config.ComfyUiCheckpoint, site.Checkpoint, StringComparison.OrdinalIgnoreCase))
            return true;

        config.ComfyUiBaseUrl = site.BaseUrl;
        config.ComfyUiCheckpoint = site.Checkpoint;
        if (AiProviderSettings.Save(config)) return true;

        error = "配置写不进去（配置文件可能不可写或磁盘只读），所以没法把这一台设为当前用的那一台。";
        return false;
    }
}

/// <summary>
/// 把一台 ComfyUI 服务器上 <c>userdata/workflows/</c> 里的工作流**整份**拉下来，并逐份转成 API 格式。
///
/// 这件事以前只能靠人做：在浏览器里打开 ComfyUI，一份一份右键「导出（API）」，再手工导入。
/// 而导出的全过程（实测 0 个网络请求）是**纯前端**行为——服务端没有对应的接口，
/// 所以只能把官方前端那段转换逻辑搬到我们这边（见 <see cref="ComfyUiWorkflowConversion"/>），
/// 再用两个普通 GET 把原料取回来：
///
///   · 清单：<c>GET /api/userdata?dir=workflows&amp;recurse=true&amp;full_info=true</c>
///     → <c>[{"path":"T-图像-Krea/T01-….json","size":16547,"modified":…,"created":…}]</c>
///   · 正文：<c>GET /api/userdata/&lt;整条相对路径整体转义&gt;</c>，注意 **<c>/</c> 要变成 <c>%2F</c>**——
///     按「一段一段」转义会 404（实测 11 种写法里只有整条转义能通），这是这个接口最反直觉的地方。
///   · 节点定义：<c>GET /api/object_info</c>（本例 21.8 MB，转换全程只需要它一份）。
///
/// 为什么转换结果要**落盘**而不是每次现算：<c>object_info</c> 有二十多 MB，
/// 提交前临时拉一次既慢又不稳；而工作流本身很少变，转一次存起来更划算。
/// </summary>
public static class ComfyUiLibrary
{
    /// <summary>同时抓几份正文。这台服务器 315 份，串行跑要一分多钟，6 路并行降到十几秒。</summary>
    private const int MaxParallelFetches = 6;

    /// <summary>单份正文的超时；<c>object_info</c> 单独用更长的超时（它有二十多 MB）。</summary>
    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObjectInfoTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 服务器自己按模态分好的顶层文件夹名里的判据。
    /// 这些目录名（<c>A图像-Qwen生成</c> / <c>G视频-Wan图生</c> / <c>N声音生成-…</c>）就是这家服务器
    /// 的组织方式，拿它当判据比猜节点类型准；但它**不是通用的**（别家可能叫 image/video 或干脆不分），
    /// 所以判不出来时退回节点类型，并把实际用的判据如实写进 <see cref="SiteWorkflow.KindReason"/>。
    /// </summary>
    private static readonly (string Hint, string Kind)[] FolderHints =
    {
        ("视频", "video"), ("图像", "image"), ("图片", "image"), ("声音", "audio"), ("音频", "audio"),
        ("音乐", "audio"), ("工具", "other"), ("设置", "other")
    };

    /// <summary>节点类型里出现这些，就说明这份工作流在出视频。</summary>
    private static readonly string[] VideoNodeMarkers =
    {
        "SaveVideo", "VHS_VideoCombine", "CreateVideo", "PreviewVideo", "VideoCombine",
        "WanImageToVideo", "WanVideo", "WanFirstLastFrameToVideo", "LTXV", "SVD", "HunyuanVideo", "Mochi"
    };

    /// <summary>节点类型里出现这些，就说明这份工作流在出声音。</summary>
    private static readonly string[] AudioNodeMarkers =
    {
        "SaveAudio", "PreviewAudio", "VHS_Audio", "AudioToVideo", "MusicGen", "ACE_Step"
    };

    /// <summary>
    /// 拉取整份工作流目录。**全程只读**：只 GET 清单、正文与节点定义，不发任何生成请求。
    /// </summary>
    public static async Task<ComfyUiLibraryResult> FetchAsync(
        string baseUrl,
        HttpClient? http = null,
        IProgress<ComfyUiLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = ProviderImporter.NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0)
            throw new InvalidOperationException("ComfyUI 地址为空：请填形如 https://主机:端口 的地址。");

        var owned = http is null;
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var notes = new List<string>();

            var listText = await GetRequiredAsync(
                client, normalized, "userdata?dir=workflows&recurse=true&full_info=true",
                WorkflowTimeout, cancellationToken).ConfigureAwait(false);
            var paths = ParseList(listText, notes);
            if (paths.Count == 0)
                throw new InvalidOperationException(
                    $"这台 ComfyUI 的 workflows 目录里没有可读的工作流（清单返回 {listText.Length} 字节）。"
                    + "确认地址指向 ComfyUI 本身，而不是它的某个反向代理页面。");

            progress?.Report(new ComfyUiLibraryProgress(0, paths.Count, "节点定义（object_info）"));
            var objectInfoText = await GetRequiredAsync(
                client, normalized, "object_info", ObjectInfoTimeout, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(objectInfoText) is not JsonObject objectInfo)
                throw new InvalidOperationException("object_info 不是 JSON 对象：这台服务器的返回形状和预期不符。");
            notes.Add($"节点定义 {DescribeBytes(objectInfoText.Length)}（{objectInfo.Count} 种节点）");

            var workflows = new List<SiteWorkflow>();
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
            using var gate = new SemaphoreSlim(MaxParallelFetches, MaxParallelFetches);
            var done = 0;

            var tasks = paths.Select(async path =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (workflow, payload) = await FetchOneAsync(
                        client, normalized, path, objectInfo, cancellationToken).ConfigureAwait(false);

                    // 计数器与两个集合一起进锁：并行跑的时候「读-加-写」不是原子的。
                    // 进度回调刻意放在锁**外面**（回调跑在用户的线程上，握着锁调出去容易变成死锁），
                    // 所以进度的数值在锁里先取成局部变量，避免锁外再读到半路的值。
                    int snapshot;
                    lock (workflows)
                    {
                        workflows.Add(workflow);
                        if (payload is not null) payloads[workflow.Key] = payload;
                        done++;
                        snapshot = done;
                    }
                    progress?.Report(new ComfyUiLibraryProgress(snapshot, paths.Count, workflow.Title));
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var produced = workflows.Select(workflow => workflow.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var path in paths)
                if (!produced.Contains(path))
                    notes.Add($"清单里有这一份，但没有对应结果：{path}（内部计数不一致，请重试）");

            // 顺序稳定：按服务器上的相对路径排，重复导入时清单顺序不会跳来跳去。
            var ordered = workflows.OrderBy(workflow => workflow.Key, StringComparer.Ordinal).ToList();
            MarkRecommended(ordered);

            var failed = ordered.Count(workflow => !workflow.Converted);
            if (failed > 0)
                notes.Add($"{failed} 份没能转换（原因逐份记在各自条目上）；不影响其它工作流。");

            return new ComfyUiLibraryResult(
                SiteCatalog.IdFor(normalized), normalized, ordered, payloads, notes);
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>
    /// 把拉取结果落成一个 ComfyUI 站点：正文按份写盘，再写站点清单，最后清掉孤儿正文。
    ///
    /// 写盘顺序是有意的：**先正文、后清单**。反过来的话，清单一旦写成就指向一批还不存在的文件，
    /// 那一刻的选择器会列出「有这一份、但打开是空的」条目；而先写正文再失败，最多留下几个没人引用的
    /// 文件（下次导入会被清掉），不会让界面说谎。
    /// </summary>
    public static (SiteProfile? Site, string Error) Install(
        ComfyUiLibraryResult result,
        string displayName,
        string checkpoint,
        SiteProfile? previous)
    {
        ArgumentNullException.ThrowIfNull(result);

        var site = new SiteProfile
        {
            Id = result.SiteId,
            DisplayName = displayName.Length > 0 ? displayName : SiteCatalog.DefaultDisplayNameFor(result.BaseUrl),
            BaseUrl = result.BaseUrl,
            SourceUrl = result.BaseUrl,
            Backend = "comfyui",
            Checkpoint = checkpoint,
            ListSource = "ComfyUI 的 workflows 目录",
            Workflows = new List<SiteWorkflow>()
        };

        // 重新导入时保住用户自己的取舍：被标成「停用」的仍然停用，用户手工改过的推荐项仍然推荐。
        // 不保的话，用户每刷新一次清单就要重新把不想要的那几十份再关一遍。
        var previousByKey = previous?.Workflows.ToDictionary(item => item.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, SiteWorkflow>(StringComparer.Ordinal);

        foreach (var workflow in result.Workflows)
        {
            // 先清掉「上一轮抓取时的自动结论」，下面按「用户自己的取舍 → 再补默认」重算一遍。
            // 不清的话，用户手工改选的推荐会和自动选出来的那一份**同时**挂着推荐，两个默认值。
            workflow.Recommended = false;
            if (previousByKey.TryGetValue(workflow.Key, out var old))
            {
                workflow.Enabled = old.Enabled;
                if (old.Recommended) workflow.Recommended = true;
            }
            workflow.PayloadFile = result.Payloads.ContainsKey(workflow.Key)
                ? SiteCatalog.PayloadFileName(workflow.Key)
                : string.Empty;
            site.Workflows.Add(workflow);
        }

        // 只有「一个推荐都没有」的组才自动补一份：用户选过的那一组不再插手。
        MarkRecommended(site.Workflows);

        foreach (var pair in result.Payloads)
        {
            var fileName = SiteCatalog.PayloadFileName(pair.Key);
            if (!SiteCatalog.SavePayload(site.Id, fileName, pair.Value, out var payloadError))
                return (null, $"工作流正文写盘失败（{pair.Key}）：{payloadError}");
        }

        if (!SiteCatalog.Save(site, out var siteError))
            return (null, $"站点文件写盘失败：{siteError}");

        SiteCatalog.PrunePayloads(site);
        return (site, string.Empty);
    }

    /// <summary>
    /// 给每个「种类 + 文件夹」组挑一份推荐。
    ///
    /// 为什么按文件夹分组、而不是整个种类只挑一份：文件夹就是**模型家族**
    /// （<c>A图像-Qwen生成</c> / <c>C图像-Zimage</c> / <c>D图像-Flux</c> / <c>T-图像-Krea</c>）。
    /// 在四个家族之间挑一个「最推荐」是没有依据的——它们出的图不是一个路子，
    /// 而我们（这条流水线）并不知道用户偏好哪一路。所以每个家族各推一份，让用户在自己的家族里有个默认。
    ///
    /// 组内怎么挑：先只看转成了的、没被停用的；在它们里挑**节点最少**的那一份
    /// （节点越少，能出错的地方越少，也更可能是这个家族的「正路」用法）；节点数相同时按路径排在前面。
    /// 一个组里一份都挑不出来就不标推荐——**不硬凑**：没推荐是诚实的，推一份坏的会把人带沟里。
    ///
    /// 这一组**已经有推荐项**时直接跳过（用户手工改选过的、或上一轮留下的）：
    /// 重算一次就把用户的选择冲掉，那等于每次刷新清单都要重新选一遍。
    /// </summary>
    private static void MarkRecommended(List<SiteWorkflow> workflows)
    {
        var groups = workflows
            .Where(workflow => workflow.Enabled && workflow.Converted)
            .GroupBy(workflow => (workflow.Kind, workflow.Folder));

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Any(workflow => workflow.Recommended)) continue;

            var best = members
                .OrderBy(workflow => workflow.NodeCount)
                .ThenBy(workflow => workflow.Key, StringComparer.Ordinal)
                .First();
            best.Recommended = true;
        }
    }

    /// <summary>抓一份工作流并转换。失败**只让这一份失败**，把原因写进它的 Error 里。</summary>
    private static async Task<(SiteWorkflow Workflow, string? Payload)> FetchOneAsync(
        HttpClient client,
        string baseUrl,
        string path,
        JsonObject objectInfo,
        CancellationToken cancellationToken)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var folder = TopFolderOf(path);
        var workflow = new SiteWorkflow
        {
            Key = path,
            Title = title,
            Folder = folder
        };

        string? content;
        try
        {
            content = await GetOptionalAsync(
                client, baseUrl, ContentEndpoint(path), WorkflowTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            workflow.Error = "读取超时";
            return (workflow, null);
        }
        catch (HttpRequestException error)
        {
            workflow.Error = "读取失败：" + error.Message;
            return (workflow, null);
        }

        if (content is null)
        {
            workflow.Error = "服务器没有返回这一份的内容";
            return (workflow, null);
        }

        try
        {
            var converted = ComfyUiWorkflowConversion.Convert(content, objectInfo);
            workflow.NodeCount = converted.ApiWorkflow.Count;
            workflow.Note = converted.SkippedSummary;
            var (kind, reason) = Classify(folder, converted.ApiWorkflow);
            workflow.Kind = kind;
            workflow.KindReason = reason;
            // 正文文件现在就定下来（哪怕还没落盘）：拉取结果因此是**自洽**的，
            // 谁拿着它都能说出「这一份的正文该在哪个文件里」，而不是非要先落一次盘。
            workflow.PayloadFile = SiteCatalog.PayloadFileName(path);
            return (workflow, converted.ToJson());
        }
        catch (Exception error)
        {
            // 单份失败**只让这一份失败**，不连坐另外三百多份；但异常类型要一起报出来——
            // 一句笼统的「转换失败」会把我们自己的缺陷（比如空引用）和「这份文件引用了没装的节点」
            // 混成同一句话，前者需要修代码，后者只需要换一份工作流。
            workflow.Error = $"转换失败（{error.GetType().Name}）：{error.Message}";
            return (workflow, null);
        }
    }

    /// <summary>
    /// 判这份工作流是出图还是出视频（还是别的东西）。
    /// 先看服务器自己分的文件夹（带「视频」「图像」这类字样），文件夹看不出时再看转成 API 之后的节点类型。
    /// </summary>
    private static (string Kind, string Reason) Classify(string folder, JsonObject apiWorkflow)
    {
        foreach (var (hint, kind) in FolderHints)
        {
            if (folder.Contains(hint, StringComparison.Ordinal))
                return (kind, $"文件夹名含「{hint}」");
        }

        var types = apiWorkflow.Select(pair => ClassTypeOf(pair.Value)).Where(type => type.Length > 0).ToList();

        var video = types.FirstOrDefault(type => VideoNodeMarkers.Any(marker => type.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        if (video is not null) return ("video", $"含视频节点 {video}");

        var audio = types.FirstOrDefault(type => AudioNodeMarkers.Any(marker => type.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        if (audio is not null) return ("audio", $"含声音节点 {audio}");

        var image = types.FirstOrDefault(type => type.Contains("SaveImage", StringComparison.Ordinal)
            || type.Contains("PreviewImage", StringComparison.Ordinal));
        if (image is not null) return ("image", $"只有保存图像的节点（{image}），没有视频节点");

        return ("other", "文件夹名和节点类型都看不出用途");
    }

    /// <summary>取一个 API 节点的 class_type；形状不对时返回空串，不抛（分类不该因为一份畸形文件而失败）。</summary>
    private static string ClassTypeOf(JsonNode? node)
    {
        if (node is not JsonObject apiNode) return string.Empty;
        if (apiNode["class_type"] is not JsonValue value) return string.Empty;
        return value.TryGetValue<string>(out var type) ? type : string.Empty;
    }

    /// <summary>
    /// 清单接口的相对地址。<c>full_info=true</c> 是为了拿到 size（用来对账），
    /// <c>recurse=true</c> 才能把子目录里的工作流一起列出来。
    /// </summary>
    private static string ListEndpoint() => "userdata?dir=workflows&recurse=true&full_info=true";

    /// <summary>
    /// 正文接口的相对地址：**整条相对路径当作一个路径段**转义（<c>workflows/T-图像-Krea/T01-….json</c>
    /// → <c>workflows%2FT-%E5%9B%BE…</c>）。按段转义（保留 <c>/</c>）会 404，这是实测结论。
    /// </summary>
    private static string ContentEndpoint(string path) => "userdata/" + Uri.EscapeDataString("workflows/" + path);

    /// <summary>取顶层文件夹名；根目录下的工作流返回空串。</summary>
    private static string TopFolderOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    /// <summary>解析清单：只认 <c>.json</c>，把「有几份、跳过几份」如实报出来。</summary>
    private static List<string> ParseList(string json, List<string> notes)
    {
        var paths = new List<string>();
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("工作流清单不是 JSON：" + error.Message);
        }

        if (parsed is not JsonArray array)
            throw new InvalidOperationException("工作流清单的形状和预期不符（期望一个数组）。");

        var skipped = 0;
        foreach (var entry in array)
        {
            var path = entry?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            paths.Add(path.Replace('\\', '/'));
        }

        if (skipped > 0) notes.Add($"清单里跳过了 {skipped} 个非 .json 的条目");
        return paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// GET 一次（必须要拿到内容）。<c>/api</c> 前缀先试，404 再试不带前缀的写法——
    /// 新版 ComfyUI 把接口挪到了 <c>/api</c> 下，老版没有这一层。
    /// </summary>
    private static async Task<string> GetRequiredAsync(
        HttpClient client, string baseUrl, string relative, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var text = await GetOptionalAsync(client, baseUrl, relative, timeout, cancellationToken).ConfigureAwait(false);
        return text ?? throw new InvalidOperationException($"服务器没有返回内容：{relative}");
    }

    /// <summary>GET 一次；404 返回 null（由调用方决定是「试下一种写法」还是「报失败」）。</summary>
    private static async Task<string?> GetOptionalAsync(
        HttpClient client, string baseUrl, string relative, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "api/", string.Empty })
        {
            var url = baseUrl.TrimEnd('/') + "/" + prefix + relative;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
        }

        // 两种写法都 404：这台服务器上这条接口不在（新版在 /api 下，老版没有这一层）。
        return null;
    }

    /// <summary>把字节数说成人话（object_info 有二十多 MB，写「21778501 字节」没人读得出量级）。</summary>
    private static string DescribeBytes(int bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB";

    /// <summary>测一次「地址通不通、清单读不读得到」，用于导入前的探测。不发生成请求。</summary>
    public static async Task<(bool Ok, string Detail)> ProbeAsync(
        string baseUrl, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var normalized = ProviderImporter.NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0) return (false, "地址为空");

        var owned = http is null;
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var watch = Stopwatch.StartNew();
        try
        {
            var text = await GetOptionalAsync(
                client, normalized, ListEndpoint(), WorkflowTimeout, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            if (text is null)
                return (false, $"两种写法都试过（/api/userdata 与 /userdata），都没有工作流清单。耗时 {watch.ElapsedMilliseconds} ms");
            var notes = new List<string>();
            var paths = ParseList(text, notes);
            return (true, $"读到 {paths.Count} 份工作流（耗时 {watch.ElapsedMilliseconds} ms）");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            watch.Stop();
            return (false, $"{error.Message}（耗时 {watch.ElapsedMilliseconds} ms）");
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }
}
