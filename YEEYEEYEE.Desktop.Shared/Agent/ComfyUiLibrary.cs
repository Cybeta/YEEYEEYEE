using System.Collections.Concurrent;
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

/// <summary>
/// 拉下来的工作流清单（正文在内存里，落盘由 <see cref="ComfyUiLibrary.Install"/> 负责）。
///
/// 除了已转好的正文，还把**原稿**与**这台的节点定义**一起带着：导入那一刻手上有这三样，
/// 就能当场体检（见 <see cref="ComfyUiImportAuditor"/>）并在用户同意后用学到的规则重转一遍
/// （见 <see cref="ComfyUiLibrary.Reconvert"/>），**不必再向那台机器要一次数据**。
/// </summary>
public sealed record ComfyUiLibraryResult(
    string SiteId,
    string BaseUrl,
    IReadOnlyList<SiteWorkflow> Workflows,
    IReadOnlyDictionary<string, string> Payloads,
    IReadOnlyList<string> Notes)
{
    /// <summary>官方导出结果不允许交给 C# 转换器重转或学习规则。</summary>
    public bool UsesOfficialFrontend { get; init; }

    /// <summary>仅导入用户选中的工作流；安装时保留同站点未选中的已有条目。</summary>
    public bool IsPartialImport { get; init; }

    public int Converted => Workflows.Count(workflow => workflow.Converted);

    public int Failed => Workflows.Count(workflow => !workflow.Converted);

    /// <summary>服务器上那一份「网页格式」的原稿，键与 <see cref="Payloads"/> 一致。体检与重转要用。</summary>
    public IReadOnlyDictionary<string, string> RawDrafts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>这台机器的节点定义（二十多 MB 的那份）。</summary>
    public JsonObject? ObjectInfo { get; init; }

    /// <summary>导入前从该站点 /system_stats 识别出的设备能力。</summary>
    public ComfyUiDeviceCapabilities DeviceCapabilities { get; init; }
        = ComfyUiDeviceCapabilities.Unknown();

    /// <summary>导入时的体检账。</summary>
    public ComfyUiImportAuditReport? Audit { get; init; }

    /// <summary>这次转换实际用上的「学来的前端节点规则」（会写进站点文件，下次导入接着用）。</summary>
    public IReadOnlyList<ComfyUiVirtualNodeRule> AppliedRules { get; init; } = Array.Empty<ComfyUiVirtualNodeRule>();
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
            // 把站点那张「见过的选项值」表一起带进去：改比例要靠它写出服务端认的值（见 SiteProfile.OptionValues）。
            // 「文件选择槽」表也一样带进去：那类底图入口（`图片1`…`图片9`）的判据只在服务端定义里。
            return (ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots), string.Empty);
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
/// 因此由 Avalonia NativeWebView 宿主复用官方前端页面，等待插件注册后调用 loadGraphData 与 graphToPrompt.output。
/// 清单与正文使用普通 GET 读取，API 格式直接保留：
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
    /// <summary>单份正文的超时；<c>object_info</c> 单独用更长的超时（它有二十多 MB）。</summary>
    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObjectInfoTimeout = TimeSpan.FromMinutes(3);

    /// <summary>「这个地址是不是 ComfyUI」的探测超时：只是问一句，不该让人干等。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

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

    /// <summary>只读取工作流路径清单，不读取正文、节点定义或初始化前端导出器。</summary>
    public static async Task<IReadOnlyList<string>> ReadManifestAsync(
        string baseUrl, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var normalized = ProviderImporter.NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0)
            throw new InvalidOperationException("ComfyUI 地址为空：请填形如 https://主机:端口 的地址。");

        var owned = http is null;
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var text = await GetRequiredAsync(
                client, normalized, ListEndpoint(), WorkflowTimeout, cancellationToken).ConfigureAwait(false);
            return ParseList(text, new List<string>());
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    /// <summary>
    /// 拉取工作流目录；selectedPaths 为 null 时保持全量读取，否则严格按路径筛选。
    /// **全程只读**：只 GET 清单、正文与节点定义，不发任何生成请求。
    /// </summary>
    public static async Task<ComfyUiLibraryResult> FetchAsync(
        string baseUrl,
        HttpClient? http = null,
        IProgress<ComfyUiLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ComfyUiFrontendExporterFactory? exporterFactory = null,
        IReadOnlyCollection<string>? selectedPaths = null)
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
                client, normalized, ListEndpoint(), WorkflowTimeout, cancellationToken).ConfigureAwait(false);
            var paths = ParseList(listText, notes);
            if (selectedPaths is not null)
            {
                var selected = selectedPaths.ToHashSet(StringComparer.Ordinal);
                if (selected.Count == 0)
                    throw new InvalidOperationException("没有选择工作流，本次不导入。");
                var available = paths.ToHashSet(StringComparer.Ordinal);
                var missing = selected.Where(path => !available.Contains(path)).ToList();
                if (missing.Count > 0)
                    throw new InvalidOperationException("所选工作流已不在服务器清单中，请重新选择：" + string.Join("、", missing));
                paths = paths.Where(selected.Contains).ToList();
                notes.Add($"仅拉取所选的 {paths.Count} 份工作流");
            }
            if (paths.Count == 0)
                throw new InvalidOperationException(
                    $"这台 ComfyUI 的 workflows 目录里没有可读的工作流（清单返回 {listText.Length} 字节）。"
                    + "确认地址指向 ComfyUI 本身，而不是它的某个反向代理页面。");

            var systemStatsText = await GetOptionalAsync(
                client, normalized, "system_stats", ProbeTimeout, cancellationToken).ConfigureAwait(false);
            var capabilities = systemStatsText is null
                ? ComfyUiDeviceCapabilities.Unknown("/system_stats 不可用")
                : ComfyUiDeviceDetector.FromSystemStats(systemStatsText);
            notes.Add(capabilities.DeviceName is { Length: > 0 } device
                ? $"设备 {device}，Nunchaku 策略：{capabilities.NunchakuQuantization}"
                : "未识别站点设备，Nunchaku 模型不自动改写");

            progress?.Report(new ComfyUiLibraryProgress(0, paths.Count, "节点定义（object_info）"));
            var objectInfoText = await GetRequiredAsync(
                client, normalized, "object_info", ObjectInfoTimeout, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(objectInfoText) is not JsonObject objectInfo)
                throw new InvalidOperationException("object_info 不是 JSON 对象：这台服务器的返回形状和预期不符。");
            notes.Add($"节点定义 {DescribeBytes(objectInfoText.Length)}（{objectInfo.Count} 种节点）");

            var workflows = new List<SiteWorkflow>();
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
            // 原稿也留着：导入那一刻的体检与「让大模型认一认」都要回原稿看那一处到底连到哪儿，
            // 事后再回来拉一遍不值得（而且那时用户已经在等了）。
            var rawDrafts = new Dictionary<string, string>(StringComparer.Ordinal);
            var results = new ConcurrentBag<(SiteWorkflow Workflow, string? Payload, string? Raw)>();
            var next = -1;
            var done = 0;
            var workerCount = SelectWorkerCount(out var workerDecision);
            notes.Add(workerDecision);
            notes.Add(workerCount == 2
                ? "使用官方 ComfyUI 前端双 worker 动态 FIFO 导出；每个 worker 独立页面，禁止生成和写请求，不应用学习规则。"
                : "使用官方 ComfyUI 前端单 worker 导出；低内存回退，禁止生成和写请求，不应用学习规则。");
            if (exporterFactory is null)
                notes.Add("未提供 Avalonia NativeWebView exporter factory；UI 工作流会明确失败，非 UI/API 工作流仍可直接读取。");

            // 一个 exporter 内部持有可变的 page/context，不能并发复用；每个 worker 独立一个 exporter，
            // 通过共享索引动态领取下一份，避免固定奇偶分配让一个 worker 提前空闲。
            var workers = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
            {
                await using var exporter = exporterFactory?.Invoke(normalized, workerId)
                    ?? new MissingComfyUiFrontendExporter();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= paths.Count) break;
                    var path = paths[index];
                    progress?.Report(new ComfyUiLibraryProgress(Volatile.Read(ref done), paths.Count, path));
                    var result = await FetchOneAsync(
                        client, normalized, path, exporter, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    var completed = Interlocked.Increment(ref done);
                    progress?.Report(new ComfyUiLibraryProgress(completed, paths.Count, result.Workflow.Title));
                }
            }, cancellationToken)).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
            foreach (var result in results.OrderBy(item => item.Workflow.Key, StringComparer.Ordinal))
            {
                workflows.Add(result.Workflow);
                if (result.Payload is not null) payloads[result.Workflow.Key] = result.Payload;
                if (result.Raw is not null) rawDrafts[result.Workflow.Key] = result.Raw;
            }

            var produced = workflows.Select(workflow => workflow.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var path in paths)
                if (!produced.Contains(path))
                    notes.Add($"清单里有这一份，但没有对应结果：{path}（内部计数不一致，请重试）");

            // 顺序稳定：按服务器上的相对路径排，重复导入时清单顺序不会跳来跳去。
            var ordered = workflows.OrderBy(workflow => workflow.Key, StringComparer.Ordinal).ToList();

            var failed = ordered.Count(workflow => !workflow.Converted);
            if (failed > 0)
                notes.Add($"{failed} 份没能转换（原因逐份记在各自条目上）；不影响其它工作流。");

            // 体检放在**导入之前**（这里就是那一刻）：手上同时有原稿、转换结果与节点定义，
            // 判断「这一项是我们丢的，还是原稿本来就没接线」全部是本地读 JSON，不发任何请求。
            // 它的结论挂在结果上（<see cref="ComfyUiLibraryResult.Audit"/>），由导入流程当场说给用户；
            // **不写进 Notes**：Notes 会跟着结果一路带到最后的报告里，而体检在「让大模型认一认」之后
            // 还要重算一遍——两处都写就会出现一份过期的账。
            var audit = ComfyUiImportAuditor.Inspect(rawDrafts, payloads, objectInfo);
            audit.ApplyTo(ordered);
            MarkRecommended(ordered);

            return new ComfyUiLibraryResult(
                SiteCatalog.IdFor(normalized), normalized, ordered, payloads, notes)
            {
                RawDrafts = rawDrafts,
                ObjectInfo = objectInfo,
                DeviceCapabilities = capabilities,
                Audit = audit,
                UsesOfficialFrontend = true,
                IsPartialImport = selectedPaths is not null
            };
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
        lock (SiteCatalog.WriteGate)
            return InstallCore(result, displayName, checkpoint, previous);
    }

    private static (SiteProfile? Site, string Error) InstallCore(
        ComfyUiLibraryResult result, string displayName, string checkpoint, SiteProfile? previous)
    {
        // 站点标识：同一台（地址一样）就是**重新导入**，沿用原来的 id，用户的取舍也跟着保住；
        // 同一个主机名但**端口不同**（一台机器上跑两个 ComfyUI）不能共用一个 id——那会互相覆盖
        // 整份工作流库。这时只给**新登记的这一台**加后缀，现有站点一个字不改（改名等于连它那份取舍一起换掉）。
        var clash = previous is not null
            && !string.Equals(previous.BaseUrl, result.BaseUrl, StringComparison.OrdinalIgnoreCase);
        var id = clash ? $"{result.SiteId}-{SiteCatalog.PortOf(result.BaseUrl)}" : previous?.Id ?? result.SiteId;
        // 撞名时那份「上一个站点」是**别的机器**，它的停用/推荐不该被搬过来。
        var carryOver = clash ? null : previous;

        var site = new SiteProfile
        {
            Id = id,
            DisplayName = displayName.Length > 0 ? displayName : SiteCatalog.DefaultDisplayNameFor(result.BaseUrl),
            BaseUrl = result.BaseUrl,
            SourceUrl = result.BaseUrl,
            Backend = "comfyui",
            Checkpoint = checkpoint,
            ListSource = "ComfyUI 的 workflows 目录",
            Workflows = new List<SiteWorkflow>(),
            OptionValues = MergeOptionValues(
                CollectOptionValues(result.Payloads),
                result.ObjectInfo is { } aspectDefinitions
                    ? ComfyUiImportAuditor.CollectAspectOptions(aspectDefinitions)
                    : new Dictionary<string, List<string>>(StringComparer.Ordinal)),
            // 「文件选择槽」表：判据在**服务端的节点定义**里；生成时读不到那二十多 MB；导入时算一次存这儿。
            // 定义拉不到时留空——binder 那边缺了这张表只会少认几处（照旧说「我没认出来」），不会说错话。
            FileSlots = result.ObjectInfo is { } definitions
                ? ComfyUiImportAuditor.CollectFileSlots(definitions, result.Payloads)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            // 这次实际用上的前端节点规则（含上次学到的、以及用户刚同意让模型认的）：落到站点上，
            // 下次导入自动接着用——同一台机器不必每导一次就再认一遍。
            VirtualNodeRules = result.AppliedRules.ToList()
        };

        // 重新导入时保住用户自己的取舍：被标成「停用」的仍然停用，用户手工改过的推荐项仍然推荐。
        // 不保的话，用户每刷新一次清单就要重新把不想要的那几十份再关一遍。
        var previousByKey = carryOver?.Workflows.ToDictionary(item => item.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, SiteWorkflow>(StringComparer.Ordinal);

        var preserved = new HashSet<string>(StringComparer.Ordinal);
        var successfulPayloads = new Dictionary<string, string>(StringComparer.Ordinal);
        var importedAt = DateTimeOffset.UtcNow;
        foreach (var original in result.Workflows)
        {
            var workflow = Clone(original);
            var attemptAt = workflow.LastImportAttemptAt ?? importedAt;
            result.Payloads.TryGetValue(workflow.Key, out var payload);
            var succeeded = workflow.Error.Length == 0 && payload is not null;
            previousByKey.TryGetValue(workflow.Key, out var old);
            if (!succeeded)
            {
                var attemptError = workflow.Error.Length > 0 ? workflow.Error : "转换结果缺少正文";
                if (old is not null && old.Converted && SiteCatalog.LoadPayload(id, old.PayloadFile) is not null)
                {
                    workflow = Clone(old);
                    preserved.Add(workflow.Key);
                }
                else
                {
                    workflow.Error = attemptError;
                    workflow.PayloadFile = string.Empty;
                    workflow.Recommended = false;
                    workflow.ImportedAt = null;
                }
                workflow.LastImportAttemptAt = attemptAt;
                workflow.LastImportError = attemptError;
            }
            else
            {
                workflow.Recommended = old?.Recommended ?? false;
                workflow.PayloadFile = SiteCatalog.PayloadFileName(workflow.Key, payload!);
                workflow.ImportedAt = importedAt;
                workflow.ConvertedAt = original.ConvertedAt == default ? attemptAt : original.ConvertedAt;
                workflow.LastImportAttemptAt = attemptAt;
                workflow.LastImportError = string.Empty;
                successfulPayloads[workflow.Key] = payload!;
            }
            if (old is not null) workflow.Enabled = old.Enabled;
            site.Workflows.Add(workflow);
        }

        // 失败回退的旧正文仍依赖旧槽位及选项定义。
        if (preserved.Count > 0 && carryOver is not null)
        {
            site.OptionValues = MergeOptionValues(carryOver.OptionValues, site.OptionValues);
            foreach (var pair in carryOver.FileSlots) site.FileSlots.TryAdd(pair.Key, pair.Value);
            if (result.AppliedRules.Count == 0) site.VirtualNodeRules = carryOver.VirtualNodeRules.ToList();
        }

        // 部分导入保留未选条目及其正文引用，避免 PrunePayloads 清掉已有文件。
        var retained = new List<SiteWorkflow>();
        if (result.IsPartialImport && carryOver is not null)
        {
            var imported = result.Workflows.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            retained = carryOver.Workflows.Where(item => !imported.Contains(item.Key)).Select(Clone).ToList();
            site.Workflows.AddRange(retained);
            site.Workflows = site.Workflows.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
            site.OptionValues = MergeOptionValues(carryOver.OptionValues, site.OptionValues);
            foreach (var pair in carryOver.FileSlots)
                site.FileSlots.TryAdd(pair.Key, pair.Value);
            if (result.AppliedRules.Count == 0) site.VirtualNodeRules = carryOver.VirtualNodeRules.ToList();
        }

        // 本次体检只作用于所选工作流，不改写未选条目的历史审计。
        result.Audit?.ApplyTo(site.Workflows.Where(item =>
            !retained.Contains(item) && !preserved.Contains(item.Key)).ToList());

        // 只有「一个推荐都没有」的组才自动补一份：用户选过的那一组不再插手。
        // 必须在体检账落盘后再算，缺失 int4 模型/依赖的工作流不能成为推荐项。
        var unchanged = site.Workflows.Where(item => retained.Contains(item) || preserved.Contains(item.Key)).ToList();
        var retainedRecommendations = unchanged.Select(item => item.Recommended).ToArray();
        MarkRecommended(site.Workflows);
        for (var index = 0; index < unchanged.Count; index++)
            unchanged[index].Recommended = retainedRecommendations[index];

        foreach (var pair in successfulPayloads)
        {
            var fileName = SiteCatalog.PayloadFileName(pair.Key, pair.Value);
            if (!SiteCatalog.SavePayload(site.Id, fileName, pair.Value, out var payloadError))
                return (null, $"工作流正文写盘失败（{pair.Key}）：{payloadError}");
        }

        if (!SiteCatalog.Save(site, out var siteError))
            return (null, $"站点文件写盘失败：{siteError}");

        SiteCatalog.PrunePayloads(site);
        return (site, string.Empty);
    }

    /// <summary>
    /// 扫一遍全部正文，把「固定选项」控件在同一个节点类型 + 同一个输入名上用过的值收成一张小表。
    ///
    /// 为什么在导入时做：合法选项清单只在服务端的 <c>object_info</c> 里（实测二十多 MB），
    /// 而每份正文只留着当前选中的那一个值；把全库出现过的值收起来，改比例时就能从里面挑一个
    /// **这台机器上真跑得通的**，而不必「照着当前值的写法把数字换掉」——那种做法会造出服务端不认的
    /// 字符串（实测 `ResolutionSelector.aspect_ratio`：当前 `16:9 (Widescreen)` 想改成 9:16，
    /// 按数字替换得到 `9:16 (Widescreen)`，合法值却是 `9:16 (Portrait Widescreen)`，提交被 400 拒）。
    ///
    /// 只收比例这三个名字：它们**一定**是这种固定选项控件（值是字面量字符串），不会误收别的输入。
    /// </summary>
    private static Dictionary<string, List<string>> CollectOptionValues(IReadOnlyDictionary<string, string> payloads)
    {
        var names = new[] { "aspect_ratio", "aspect", "ratio", "画面比例" };
        var seen = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var payload in payloads.Values)
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }
            if (parsed is not JsonObject graph) continue;

            foreach (var pair in graph)
            {
                if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
                if (node["class_type"] is not JsonValue classValue
                    || !classValue.TryGetValue<string>(out var classType)
                    || classType.Length == 0) continue;

                foreach (var name in names)
                {
                    if (inputs[name] is not JsonValue value
                        || !value.TryGetValue<string>(out var text)
                        || text.Length == 0) continue;
                    var key = classType + "." + name;
                    if (!seen.TryGetValue(key, out var bucket))
                        seen[key] = bucket = new SortedSet<string>(StringComparer.Ordinal);
                    bucket.Add(text);
                }
            }
        }

        return seen.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
    }

    private static Dictionary<string, List<string>> MergeOptionValues(
        IReadOnlyDictionary<string, List<string>> observed,
        IReadOnlyDictionary<string, List<string>> declared)
    {
        var merged = declared.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

        foreach (var pair in observed)
        {
            if (!merged.TryGetValue(pair.Key, out var values))
                merged[pair.Key] = values = new List<string>();
            foreach (var value in pair.Value)
                if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }

        return merged;
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
        foreach (var workflow in workflows.Where(workflow => !IsHealthyRecommendationCandidate(workflow)))
            workflow.Recommended = false;

        var groups = workflows
            .Where(IsHealthyRecommendationCandidate)
            .GroupBy(workflow => (workflow.Kind, workflow.Folder));

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Any(workflow => workflow.Recommended && IsHealthyRecommendationCandidate(workflow))) continue;

            var best = members
                .OrderBy(workflow => workflow.NodeCount)
                .ThenBy(workflow => workflow.Key, StringComparer.Ordinal)
                .First();
            best.Recommended = true;
        }
    }

    private static bool IsHealthyRecommendationCandidate(SiteWorkflow workflow)
        => workflow.Enabled
            && workflow.Converted
            && workflow.DroppedInputs == 0
            && workflow.BrokenInputs == 0
            && workflow.UncertainInputs == 0
            && workflow.MissingFiles == 0;

    /// <summary>抓一份工作流并转换。失败**只让这一份失败**，把原因写进它的 Error 里。</summary>
    /// <returns>第三项是原稿（网页格式）：体检与「让大模型认一认」都要回它看那一处连到哪儿。</returns>
    private static int SelectWorkerCount(out string decision)
    {
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        const long eightGiB = 8L * 1024 * 1024 * 1024;
        const long oneAndHalfGiB = 1536L * 1024 * 1024;

        var memory = GC.GetGCMemoryInfo();
        var available = memory.TotalAvailableMemoryBytes;
        var workingSet = Environment.WorkingSet;
        var lowAvailable = available > 0 && available < fourGiB;
        var elevatedProcess = workingSet > oneAndHalfGiB && available > 0 && available < eightGiB;

        if (lowAvailable || elevatedProcess)
        {
            decision = $"内存保护：可用内存约 {DescribeBytes(available)}, "
                + $"进程工作集约 {DescribeBytes(workingSet)}，并发降为 1。";
            return 1;
        }

        decision = available > 0
            ? $"内存检查通过：可用内存约 {DescribeBytes(available)}，保持双 worker。"
            : "内存检查未返回可用值，保持双 worker；如页面初始化失败将按页面级策略销毁并重建。";
        return 2;
    }

    private static async Task<(SiteWorkflow Workflow, string? Payload, string? Raw)> FetchOneAsync(
        HttpClient client,
        string baseUrl,
        string path,
        IComfyUiFrontendExporter exporter,
        CancellationToken cancellationToken)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var folder = TopFolderOf(path);
        var workflow = new SiteWorkflow
        {
            Key = path,
            Title = title,
            Folder = folder,
            LastImportAttemptAt = DateTimeOffset.UtcNow
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
            return (workflow, null, null);
        }
        catch (HttpRequestException error)
        {
            workflow.Error = "读取失败：" + error.Message;
            return (workflow, null, null);
        }

        if (content is null)
        {
            workflow.Error = "服务器没有返回这一份的内容";
            return (workflow, null, null);
        }

        try
        {
            var source = JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException("工作流不是 JSON 对象");
            var isUi = source["nodes"] is JsonArray;
            var api = isUi
                ? await exporter.ExportAsync(source, cancellationToken).ConfigureAwait(false)
                : source["prompt"] is JsonObject prompt ? prompt : source;
            if (api.Count == 0 || api.Any(pair => pair.Value is not JsonObject node
                || node["class_type"] is not JsonValue || node["inputs"] is not JsonObject))
                throw new InvalidOperationException("工作流不是有效的 API output");
            workflow.NodeCount = api.Count;
            workflow.Note = isUi ? "官方前端 graphToPrompt.output" : "API 格式直接读取";
            var (kind, reason) = Classify(folder, api);
            workflow.Kind = kind;
            workflow.KindReason = reason;
            // 正文文件现在就定下来（哪怕还没落盘）：拉取结果因此是**自洽**的，
            // 谁拿着它都能说出「这一份的正文该在哪个文件里」，而不是非要先落一次盘。
            var payload = api.ToJsonString();
            workflow.ConvertedAt = DateTimeOffset.UtcNow;
            workflow.PayloadFile = SiteCatalog.PayloadFileName(path, payload);
            return (workflow, payload, isUi ? content : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // 单份失败**只让这一份失败**，不连坐另外三百多份；但异常类型要一起报出来——
            // 一句笼统的「转换失败」会把我们自己的缺陷（比如空引用）和「这份文件引用了没装的节点」
            // 混成同一句话，前者需要修代码，后者只需要换一份工作流。
            workflow.Error = $"转换失败（{error.GetType().Name}）：{error.Message}";
            return (workflow, null, null);
        }
    }

    /// <summary>
    /// 用另一套（或多了几条）前端节点规则**重转一遍**——用户同意「让大模型认一认」之后走这里，
    /// 以及下次导入时把上次学到的规则接着用上。
    ///
    /// 为什么能离线重转：拉取结果里带着原稿与节点定义（见 <see cref="ComfyUiLibraryResult.RawDrafts"/>），
    /// 转换本身是纯计算。所以这件事**不碰网络、不重拉**，几秒钟。
    /// 重转之后**顺手把体检重算一遍**：规则有没有用，不能听模型的，要看「我们丢了」是不是真的少了。
    /// </summary>
    public static ComfyUiLibraryResult Reconvert(
        ComfyUiLibraryResult result, IReadOnlyList<ComfyUiVirtualNodeRule> rules)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rules);
        if (result.UsesOfficialFrontend || result.ObjectInfo is null || result.RawDrafts.Count == 0) return result;

        var payloads = new Dictionary<string, string>(result.Payloads, StringComparer.Ordinal);
        var workflows = new List<SiteWorkflow>(result.Workflows.Count);
        foreach (var original in result.Workflows)
        {
            // 复制一份条目再改：验证一条规则要试好几次，试错的结果**不能留在「最终要落盘的那份结果」上**。
            // 用 JSON 往返复制而不是手写字段：手写的话，以后给 SiteWorkflow 加字段就会漏掉，
            // 而这种漏测试看不出来（它影响的是「重新导入时用户自己的取舍」这类状态）。
            var workflow = Clone(original);
            workflows.Add(workflow);
            if (!result.RawDrafts.TryGetValue(workflow.Key, out var raw)) continue;
            workflow.LastImportAttemptAt = DateTimeOffset.UtcNow;
            try
            {
                var converted = ComfyUiWorkflowConversion.Convert(raw, result.ObjectInfo, result.DeviceCapabilities, rules);
                workflow.NodeCount = converted.ApiWorkflow.Count;
                workflow.Note = converted.SkippedSummary;
                var (kind, reason) = Classify(workflow.Folder, converted.ApiWorkflow);
                workflow.Kind = kind;
                workflow.KindReason = reason;
                payloads[workflow.Key] = converted.ToJson();
                workflow.PayloadFile = SiteCatalog.PayloadFileName(workflow.Key, payloads[workflow.Key]);
                workflow.Error = string.Empty;
                workflow.LastImportError = string.Empty;
                workflow.ConvertedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception error)
            {
                // 单份失败照旧只让这一份失败——但**不要**把上一次的成绩擦掉：那一份保持原样更安全。
                workflow.Error = $"重转失败（{error.GetType().Name}）：{error.Message}";
            }
        }

        var audit = ComfyUiImportAuditor.Inspect(result.RawDrafts, payloads, result.ObjectInfo);
        // 重转之后账要重新落到每一份上：之前那份结论（例如「转换丢过输入」）多半已经不成立了。
        audit.ApplyTo(workflows);
        return result with
        {
            Workflows = workflows,
            Payloads = payloads,
            AppliedRules = rules,
            Audit = audit
        };
    }

    /// <summary>按 JSON 往返复制一份工作流条目（见 <see cref="Reconvert"/> 里为什么这么做）。</summary>
    private static SiteWorkflow Clone(SiteWorkflow workflow)
        => System.Text.Json.JsonSerializer.Deserialize<SiteWorkflow>(
               System.Text.Json.JsonSerializer.Serialize(workflow))
           ?? new SiteWorkflow { Key = workflow.Key, Title = workflow.Title, Folder = workflow.Folder };

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
    private static string DescribeBytes(long bytes) => bytes >= 1024L * 1024L
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

    /// <summary>
    /// 问一句「这个地址是不是 ComfyUI」。
    ///
    /// 为什么需要它：**用户手上常常只有一个地址**（一台服务器的控制台首页），没有一整段文档。
    /// 而那种地址里往往一个能判断用途的词都没有——不是 <c>:8188</c>、域名里也没有 comfy，
    /// 于是分类器只能判成「未能判断」。实测一个真实的远程 ComfyUI 地址
    /// （<c>https://主机:8443/</c>）正是这种情形，结果就是**明明有 ComfyUI、界面上却什么都出不来**。
    ///
    /// 判不出来时就去问一句，比继续猜强：<c>/system_stats</c> 是 ComfyUI 特有的，而且**只读**
    /// （它不跑任何生成，也不改任何东西）。老版本没有 <c>/api</c> 前缀，所以两种写法都试。
    /// </summary>
    public static async Task<bool> LooksLikeComfyUiAsync(
        string baseUrl, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var normalized = ProviderImporter.NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0) return false;

        var owned = http is null;
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var text = await GetOptionalAsync(
                client, normalized, "system_stats", ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (text is null) return false;

            // 只看「是不是 JSON 且带 system / devices 这两个键」，不看具体数值——
            // 版本之间字段会增删，但这两个一直在。普通文档站回的是 HTML，解析就抛，也落在这里。
            return JsonNode.Parse(text) is JsonObject root
                && (root["system"] is not null || root["devices"] is not null);
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }
}
