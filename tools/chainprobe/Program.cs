using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

// 联调 / 验收工具：能离线把产品那两条链真跑一遍。
//
//   · 导入链：拉清单 → 转换 → 体检 → 让大模型认节点 → 重转 → 落盘
//   · 出片链：提交 → 轮询 → 收产物 → 量结果
//
// 靠的是自带的**本地假 ComfyUI**（LocalComfyStub，文件末尾）：数据用本地留档的原稿与节点定义，
// 所以不依赖真机、也不吃真机的时段。它打的是**产品代码**（ComfyUiLibrary / ComfyUiExecutor /
// ComfyUiVideoProvider / ComfyUiWorkflowBinder / Mp4Concatenator），不是另写一套——
// 否则测过的不是会跑的那个。
//
// 路径一律「按仓库根推导 + 环境变量覆盖」：以前这些全是本机绝对路径，换个目录就跑不了，
// 也正是它当初只能躺在临时目录里的原因。
//   CHAINPROBE_PROJECT  桌面工程目录（默认取 Release 产物下 Projects 里最近改过的那个）
//   CHAINPROBE_COMFY    真机地址（**仓库里不写死**：那是别人租的机器。打真机的几个模式自己设）
//   CHAINPROBE_ARCHIVE  原稿留档目录（见 pull-archive；离线那几条都读它）
//   CHAINPROBE_ARTIFACT 出片链假产物用的那段 mp4（默认在本机留档里挑最小的）
var repoRoot = FindRepoRoot();
var appOutput = Path.Combine(repoRoot, "YEEYEEYEE.Desktop.Avalonia", "bin", "Release", "net10.0", "win-x64");
var project = Env("CHAINPROBE_PROJECT", NewestProjectUnder(Path.Combine(appOutput, "Projects")));
// 默认落回本机默认端口，不指向任何具体机器：地址是私事，不该跟着仓库走。
var baseUrl = Env("CHAINPROBE_COMFY", "http://127.0.0.1:8188");
var archiveDir = Env("CHAINPROBE_ARCHIVE", Path.Combine(Path.GetTempPath(), "chainprobe-archive"));
var checkpoint = "SDXL/sd_xl_base_1.0.safetensors";
var work = Path.Combine(Path.GetTempPath(), "chainprobe-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(work);

// 资产与任务库都落到临时目录：这是实测，不该往用户的项目里写东西。
Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(work, "assets"));
Environment.SetEnvironmentVariable("YEEYEEYEE_JOB_DB", Path.Combine(work, "jobs.db"));
Directory.CreateDirectory(Path.Combine(work, "assets"));
var opened = ProjectContext.Open(project);
if (opened is not null) AppPaths.UseProject(opened);
Console.WriteLine($"仓库 {repoRoot}");
Console.WriteLine($"工作目录 {work}");
Console.WriteLine($"项目 {AppPaths.Root}");
Console.WriteLine($"原稿留档 {archiveDir}（{(Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.json").Length + " 份" : "还没有，先跑 pull-archive")}）");

var config = new AiProviderConfig
{
    ComfyUiBaseUrl = baseUrl,
    ComfyUiCheckpoint = checkpoint
};

var mode = args.Length > 0 ? args[0] : "audit";
var sites = SiteCatalog.Load().Sites;
Console.WriteLine($"站点 {sites.Count} 个；ComfyUI 已配置 = {config.IsComfyUiConfigured}");

if (mode == "audit")
{
    // ── 审计：这台服务器上哪些视频工作流是**我们真能驱动的** ────────────────────────────
    // 「能驱动」= 认得出收提示词的位置（我们只绑得进提示词、负面词、画幅、种子与首帧）。
    var usable = new List<(string Site, string Folder, string Title, string Key, bool CanTakeImage, int Capacity, int Nodes, bool CanResize, bool CanSetLength, bool CanSetAspect, bool FrameDriven, bool CanSetSeconds, bool CanTakeVideo, bool CanTakeAudio)>();
    var unreadable = 0;
    var noPrompt = new List<string>();
    var frameDriven = new List<string>();
    var sourceDriven = new List<string>();
    foreach (var site in sites.Where(item => item.IsComfyUi))
    {
        foreach (var workflow in site.VideoWorkflows)
        {
            string? payload;
            try { payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile); }
            catch (Exception error) { payload = null; _ = error; }
            if (string.IsNullOrWhiteSpace(payload)) { unreadable++; continue; }

            ComfyUiWorkflowSlots slots;
            try { slots = ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots); }
            catch (Exception error) { unreadable++; _ = error; continue; }

            if (!slots.CanTextToImage)
            {
                // 不收文字、但有素材入口：这是两类正当用法，早先它们和「真的驱动不了」混在一起被挡掉了。
                // ① 只吃首帧（SVD 那一类）；② 只吃一段片子（视频修复 / 补帧超分 / 去水印那一类）。
                if (slots.IsSourceDriven) sourceDriven.Add($"{workflow.Folder}/{workflow.Title}");
                else if (slots.IsFrameDriven) frameDriven.Add($"{workflow.Folder}/{workflow.Title}");
                else noPrompt.Add($"{workflow.Folder}/{workflow.Title} :: {string.Join("；", slots.Notes)}");
                continue;
            }

            usable.Add((site.DisplayName, workflow.Folder, workflow.Title, workflow.Key, slots.CanTakeImage, slots.ImageCapacity, workflow.NodeCount, slots.CanResize, slots.CanSetLength, slots.CanSetAspect, slots.IsFrameDriven, slots.CanSetSeconds, slots.CanTakeVideo, slots.CanTakeAudio));
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== 视频工作流：能驱动的 {usable.Count} 份 / 认不出提示词的 {noPrompt.Count} 份 / 正文读不到的 {unreadable} 份 ===");
    Console.WriteLine($"    · 能改**画幅**（width/height 字面量）：{usable.Count(item => item.CanResize)} 份");
    Console.WriteLine($"    · 能改**时长**（生成侧帧数字面量）：{usable.Count(item => item.CanSetLength)} 份");
    Console.WriteLine($"    · 能改**时长**（写秒数，帧数由它自己的表达式折）：{usable.Count(item => item.CanSetSeconds)} 份");
    Console.WriteLine($"    · 能改**比例**（aspect_ratio 这类固定选项）：{usable.Count(item => item.CanSetAspect)} 份");
    Console.WriteLine($"    · **只吃首帧**不收文字（SVD 那一类，新放行的）：{frameDriven.Count} 份");
    foreach (var line in frameDriven.Take(6)) Console.WriteLine("        " + line);
    Console.WriteLine($"    · **只吃一段片子**不收文字（视频修复 / 补帧超分 / 去水印那一类，本轮放行）：{sourceDriven.Count} 份");
    foreach (var line in sourceDriven.Take(12)) Console.WriteLine("        " + line);
    Console.WriteLine($"    · 有**源视频入口**的工作流：{usable.Count(item => item.CanTakeVideo)} 份（能驱动的那批里）"
        + $"；有源音频入口的：{usable.Count(item => item.CanTakeAudio)} 份");
    foreach (var item in usable.Where(entry => entry.CanTakeAudio).OrderBy(entry => entry.Nodes))
        Console.WriteLine($"        （底图{(item.CanTakeImage ? "✓" : "✗")}）{item.Nodes} 节点 | {item.Key}");
    Console.WriteLine();
    foreach (var group in usable.GroupBy(item => item.Folder).OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"[{group.Key}] 可驱动 {group.Count()} 份：画幅 {group.Count(item => item.CanResize)}、"
            + $"时长 {group.Count(item => item.CanSetLength)}（写秒 {group.Count(item => item.CanSetSeconds)}）、比例 {group.Count(item => item.CanSetAspect)}");
    }

    Console.WriteLine();
    Console.WriteLine("=== 能改时长的（按节点数排，用它来真跑一次验证时长真的生效） ===");
    foreach (var item in usable.Where(item => item.CanSetLength).OrderBy(item => item.Nodes))
        Console.WriteLine($"  {item.Nodes} 节点 | 底图{(item.CanTakeImage ? "✓" : "✗")} | 画幅{(item.CanResize ? "✓" : "✗")} | 比例{(item.CanSetAspect ? "✓" : "✗")} | {item.Key}");

    Console.WriteLine();
    Console.WriteLine("=== 能写秒的（帧数由它自己的表达式折出来） ===");
    foreach (var item in usable.Where(item => item.CanSetSeconds).OrderBy(item => item.Nodes))
        Console.WriteLine($"  {item.Nodes} 节点 | 底图{(item.CanTakeImage ? "✓" : "✗")} | 画幅{(item.CanResize ? "✓" : "✗")} | 比例{(item.CanSetAspect ? "✓" : "✗")} | {item.Key}");

    Console.WriteLine();
    Console.WriteLine("=== 认不出提示词的前 12 份（说明为什么） ===");
    foreach (var line in noPrompt.Take(12)) Console.WriteLine("  " + line);
    return;
}

// ── 共用执行链：与 DesktopExecutionHost.Create 同样的接法，只是配置来自上面那份内存副本 ──
var http = new HttpClient
{
    BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
    Timeout = TimeSpan.FromSeconds(60)
};
var executor = new ComfyUiExecutor(http, new ComfyUiWorkflowFactory(config.ComfyUiCheckpoint).Create, config.ComfyUiClientId);
var externalProvider = new ComfyUiProvider(
    http, AssetStore.EnsureDirectory(), new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), config.ComfyUiClientId);
var execution = new SingleMachineExecutionService(executor, store: null, externalProvider);
await using var poller = new ExternalTaskPoller(execution, externalProvider, TimeSpan.FromSeconds(2));
poller.Start();

var session = new SessionContext
{
    SessionId = Guid.NewGuid(),
    UserId = ImageProviderFactory.DesktopUserId,
    ClientType = ClientType.Desktop,
    Role = MemberRole.Member,
    ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"])
};

var imagePrompt = "一位红发少女站在雪中的苏式园林月洞门前，暖黄宫灯侧光，电影质感，特写";

if (mode == "apply-audit")
{
    // 把体检的账**落到现有站点文件上**（正常情况下这一步在导入时做；服务器现在读不到，
    // 就用本地留档的原稿 + 已落盘的正文算同一次，让选择器与出片那一刻马上能看到警告）。
    // 所有 ComfyUI 站点都过一遍：一台机器一个站点文件，只处理第一个会让别的站点留着旧账。
    var d = args.Length > 1 ? args[1] : archiveDir;
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();

    // 原稿按**标题**找（留档文件名是标题），站点的键是服务器上的相对路径——对不上的那份计数保持 0。
    var draftsByTitle = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(d, "*.json"))
        draftsByTitle[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

    foreach (var site in sites.Where(item => item.IsComfyUi))
    {
        var raws = new Dictionary<string, string>(StringComparer.Ordinal);
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = 0;
        foreach (var workflow in site.Workflows)
        {
            var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
            if (string.IsNullOrWhiteSpace(payload)) continue;
            payloads[workflow.Key] = payload!;
            if (draftsByTitle.TryGetValue(workflow.Title, out var draft)) raws[workflow.Key] = draft;
            else missing++;
        }

        var report = ComfyUiImportAuditor.Inspect(raws, payloads, objectInfoNode);
        report.ApplyTo(site.Workflows);

        if (!SiteCatalog.Save(site, out var error)) { Console.WriteLine($"站点 {site.Id} 写盘失败：{error}"); continue; }
        var warned = site.Workflows.Count(item => ComfyUiWorkflowHealth.ShortMark(item).Length > 0);
        Console.WriteLine($"站点「{site.DisplayName}」：{site.Workflows.Count} 份里有 {warned} 份带上了体检记号"
            + $"（没有原稿留档、计数按 0 的：{missing} 份）");
        foreach (var item in site.Workflows.Where(entry => ComfyUiWorkflowHealth.ShortMark(entry).Length > 0)
            .OrderByDescending(entry => entry.BrokenInputs + entry.DroppedInputs + entry.UncertainInputs).Take(5))
            Console.WriteLine($"    {item.Title}{ComfyUiWorkflowHealth.ShortMark(item)}"
                + $"（丢 {item.DroppedInputs} / 断 {item.BrokenInputs} / 判不了 {item.UncertainInputs}）");
    }
    return;
}

if (mode == "stale")
{
    // stale [标题片段] [给几张图]：出片前那道挡（「没给满 + 空着的槽位留着失效的示例」）会说什么。
    // 判据全部调产品那套（ComfyUiWorkflowHealth.FilledMediaSlots / DescribeUnfilledStaleMedia）——
    // 这里绝不再长出一份会撒谎的影子判据（先前吃过一次亏）。
    var needle = args.Length > 1 ? args[1] : string.Empty;
    var images = args.Length > 2 ? int.Parse(args[2]) : 1;
    var blocked = 0;
    foreach (var site in sites.Where(item => item.IsComfyUi))
        foreach (var workflow in site.Workflows)
        {
            if (workflow.MissingMedia.Count == 0) continue;
            if (needle.Length > 0 && !workflow.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
            if (string.IsNullOrWhiteSpace(payload)) continue;

            var slots = ComfyUiWorkflowBinder.Detect(payload!, site.OptionValues, site.FileSlots);
            var filled = ComfyUiWorkflowHealth.FilledMediaSlots(slots, images, 0, 0);
            var message = ComfyUiWorkflowHealth.DescribeUnfilledStaleMedia(workflow, filled);
            if (message is null) continue;

            blocked++;
            Console.WriteLine($"{workflow.Title}（失效素材槽位 {workflow.MissingMedia.Count} 个；这次给了 {images} 张图）");
            foreach (var slot in workflow.MissingMedia)
                Console.WriteLine($"    · {slot.Kind} 节点 {slot.NodeId}.{slot.Input} 留着 `{slot.FileName}`（{slot.DependentOutputs} 路产物靠它）");
            Console.WriteLine("    → " + message);
        }
    Console.WriteLine($"合计 {blocked} 份会被挡住（这次按给 {images} 张图算）");
    return;
}

if (mode == "repair")
{
    // repair [输出路径]：导一张**待修清单**（markdown），供用户拿着一次把该补的补完：
    //   ① 要补到服务器上的模型/依赖（去重，带上哪些工作流要用）
    //   ② 要接的线（那份工作流自己断线的输入，逐条写「节点.输入」）
    //   ③ 失效的示例素材（不用补文件——出片时给上素材就行，给不满会被出片前那道挡拦住）
    // 数据一律来自产品体检（ComfyUiImportAuditor），这里只排版。
    var output = args.Length > 1 ? args[1] : Path.Combine(FindRepoRoot(), "待修清单.md");
    var d = archiveDir;
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();

    var draftsByTitle = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(d, "*.json"))
        draftsByTitle[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

    var site = sites.First(item => item.IsComfyUi);
    var raws = new Dictionary<string, string>(StringComparer.Ordinal);
    var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (string.IsNullOrWhiteSpace(payload)) continue;
        payloads[workflow.Key] = payload!;
        if (draftsByTitle.TryGetValue(workflow.Title, out var draft)) raws[workflow.Key] = draft;
    }

    var report = ComfyUiImportAuditor.Inspect(raws, payloads, objectInfoNode);
    var lines = new List<string>
    {
        "# 待修清单（数据来自导入体检）",
        string.Empty,
        $"站点：{SiteLabel(site.DisplayName)}（地址已隐去，这份清单要进仓库）；扫了 {report.ScannedWorkflows} 份工作流（正文合计 {report.PayloadNodes} 个节点）。",
        $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}。这份清单**随服务器上的文件变化**——补完文件、接完线之后重新导入一次就重算。",
        string.Empty
    };

    // ① 模型/依赖：这类只能补文件（我们不会替它补），所以去重后排前面。
    var resources = report.Findings
        .Where(item => item.Kind == ComfyUiFindingKind.MissingOnServer
            && item.MissingFileFlavor == ComfyUiMissingFileFlavor.Resource)
        .ToList();
    lines.Add($"## 一、要补到服务器上的文件（{resources.Count} 处，这类我们替不了）");
    lines.Add(string.Empty);
    if (resources.Count == 0) lines.Add("（没有）");
    foreach (var group in resources.GroupBy(item => item.MissingFileName, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        lines.Add($"- [ ] `{group.Key}`");
        foreach (var item in group.GroupBy(entry => entry.WorkflowTitle).Select(entry => entry.Key).OrderBy(x => x, StringComparer.Ordinal))
            lines.Add($"      - {item}");
    }
    lines.Add(string.Empty);

    // ② 断线：这类要去 ComfyUI 里把线接上（或换一份）。
    var broken = report.Findings.Where(item => item.Kind == ComfyUiFindingKind.BrokenInSource).ToList();
    var brokenWorkflows = broken.GroupBy(item => item.WorkflowKey, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count()).ToList();
    lines.Add($"## 二、要去 ComfyUI 里接的线（{broken.Count} 处，分布在 {brokenWorkflows.Count} 份工作流里）");
    lines.Add(string.Empty);
    lines.Add("接好之后重新导入一次，这份清单会重算。想绕开也行：换一份没断线的工作流出片。");
    lines.Add(string.Empty);
    if (broken.Count == 0) lines.Add("（没有）");
    foreach (var group in brokenWorkflows)
    {
        var title = group.First().WorkflowTitle;
        lines.Add($"- [ ] **{title}**（{group.Count()} 处）");
        foreach (var item in group.OrderBy(entry => entry.NodeId, StringComparer.Ordinal))
            lines.Add($"      - 节点 {item.NodeId} `{item.ClassType}.{item.Input}`"
                + (item.DependentOutputs > 0 ? $"（{item.DependentOutputs} 路产物靠它）" : "（没有产物靠它，可以先不管）"));
    }
    lines.Add(string.Empty);

    // ③ 失效的示例素材：不用补文件，出片时给上素材就行。
    var samples = report.Findings
        .Where(item => item.Kind == ComfyUiFindingKind.MissingOnServer
            && item.MissingFileFlavor != ComfyUiMissingFileFlavor.Resource)
        .ToList();
    lines.Add($"## 三、失效的示例素材（{samples.Count} 处；**不用补文件**）");
    lines.Add(string.Empty);
    lines.Add("这些槽位我们来填：出片时给上素材就能跑。**给不满**的时候，空着的槽位会留着它自己的示例，"
        + "而那份示例已经不在服务器上了——那种提交会被 ComfyUI 当场挡下（实测 400 `custom_validation_failed`），"
        + "所以出片前那道检查会先拦住并告诉你差哪个入口。");
    lines.Add(string.Empty);
    var negligible = samples.Count(item => item.DependentOutputs == 0);
    lines.Add($"其中 {negligible} 处**没有任何产物靠它**（眼下不影响出片，可以不管）。");
    lines.Add(string.Empty);
    if (samples.Count == 0) lines.Add("（没有）");
    foreach (var group in samples.Where(item => item.DependentOutputs > 0)
        .GroupBy(item => item.WorkflowKey, StringComparer.Ordinal))
    {
        var title = group.First().WorkflowTitle;
        lines.Add($"- **{title}**（{group.Count(item => item.DependentOutputs > 0)} 处要留意）");
        foreach (var item in group.Where(entry => entry.DependentOutputs > 0)
            .OrderBy(entry => entry.NodeId, StringComparer.Ordinal))
            lines.Add($"      - 节点 {item.NodeId} `{item.ClassType}.{item.Input}` 留着 `{item.MissingFileName}`"
                + $"（{item.DependentOutputs} 路产物靠它）");
    }

    var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
    File.WriteAllText(output, text);
    Console.WriteLine($"写出 {output}（{text.Length} 字符）：模型/依赖 {resources.Count} 处、断线 {broken.Count} 处、"
        + $"失效素材 {samples.Count} 处（其中 {negligible} 处没人读）");
    return;
}

if (mode == "audit-import")
{
    // 直接跑**产品里那套导入体检**（ComfyUiImportAuditor），数据用：服务器上的原稿 + 已落盘的正文 + 节点定义。
    // 目的是确认「这台机器导入时会不会弹窗打扰用户」——按现在的结论它应当**不弹**（我们丢了 0 处）。
    var d = args.Length > 1 ? args[1] : archiveDir;
    var site = sites.First(item => item.IsComfyUi);
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();

    var raws = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(d, "*.json"))
        raws[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

    var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (!string.IsNullOrWhiteSpace(payload)) payloads[workflow.Title] = payload!;
    }

    var report = ComfyUiImportAuditor.Inspect(raws, payloads, objectInfoNode);
    Console.WriteLine(report.Describe());
    Console.WriteLine();
    Console.WriteLine($"需要打扰用户（NeedsAttention）= {report.NeedsAttention}");
    foreach (var kind in Enum.GetValues<ComfyUiFindingKind>())
        Console.WriteLine($"    {kind}: {report.Count(kind)}");
    Console.WriteLine();
    Console.WriteLine("=== 我们丢了的（前 10）===");
    foreach (var finding in report.Findings.Where(item => item.Kind == ComfyUiFindingKind.DroppedByConversion).Take(10))
        Console.WriteLine("  " + finding.Label);

    // 「判断不了」：链子停在一个我担保不了的类型上（这台服务器的节点定义里没有它，或者它本来就是纯前端件）。
    // 单列出来：它**不是「我们丢了」**（原先就是误报在这一档），但也别当成没事——真缺了必填输入照样被拒收。
    var uncertain = report.Findings.Where(item => item.Kind == ComfyUiFindingKind.Unclassified).ToList();
    Console.WriteLine();
    Console.WriteLine($"=== 判断不了（{uncertain.Count} 处，前 10）===");
    foreach (var finding in uncertain.Take(10))
        Console.WriteLine("  " + finding.Label);
    if (report.UnknownTypes.Count > 0)
        Console.WriteLine("  链子上那些我不担保的类型（「让大模型认一认」的清单）："
            + string.Join("、", report.UnknownTypes.Select(item => $"{item.Type}×{item.Count}")));

    // 「原稿自己断线」影响多少份：这些工作流选它出片会缺东西，而用户在选择器上看不出来。
    var broken = report.Findings.Where(item => item.Kind == ComfyUiFindingKind.BrokenInSource).ToList();
    var brokenWorkflows = broken.Select(item => item.WorkflowKey).Distinct(StringComparer.Ordinal).ToList();
    Console.WriteLine();
    Console.WriteLine($"「原稿自己断线」{broken.Count} 处，分布在 {brokenWorkflows.Count} 份工作流里"
        + $"（占 {brokenWorkflows.Count * 100.0 / Math.Max(1, report.ScannedWorkflows):F0}%）");
    foreach (var group in broken.GroupBy(item => item.WorkflowTitle).OrderByDescending(g => g.Count()).Take(12))
        Console.WriteLine($"    {group.Count()} 处 · {group.Key}");

    // 「引用的文件这台机器上没有」：正文结构一点毛病都没有，所以上面几类查不出它来——
    // 但提交照样会被 ComfyUI 拒收。这里按「类型.输入」分组并列样本，便于核对是不是误报。
    var goneFiles = report.Findings.Where(item => item.Kind == ComfyUiFindingKind.MissingOnServer).ToList();
    var goneWorkflows = goneFiles.Select(item => item.WorkflowKey).Distinct(StringComparer.Ordinal).Count();
    var goneOffChain = goneFiles.Count(item => !item.OnExecutionChain);
    Console.WriteLine();
    Console.WriteLine($"「引用的文件这台机器上没有」{goneFiles.Count} 处，分布在 {goneWorkflows} 份工作流里"
        + $"（其中 {goneOffChain} 处所在的节点**在出片链上没人读**，眼下不影响出片）");
    foreach (var group in goneFiles
        .GroupBy(item => item.ClassType + "." + item.Input, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count()))
        Console.WriteLine($"    {group.Count(),3}  {group.Key}");
    Console.WriteLine("    样本（素材，前 5）：");
    foreach (var finding in goneFiles.Where(item => item.MissingFileFlavor == ComfyUiMissingFileFlavor.Sample).Take(5))
        Console.WriteLine($"      {finding.WorkflowTitle} | {finding.ClassType}.{finding.Input}：{finding.Detail}");
    Console.WriteLine("    全部模型/依赖（这类不由我们替换，要用户去补文件）：");
    foreach (var finding in goneFiles.Where(item => item.MissingFileFlavor == ComfyUiMissingFileFlavor.Resource))
        Console.WriteLine($"      {finding.WorkflowTitle} | {finding.ClassType}.{finding.Input}：{finding.Detail}");
    var renamed = goneFiles.Where(item => item.MissingFileFlavor == ComfyUiMissingFileFlavor.SeparatorMismatch).ToList();
    Console.WriteLine($"    分隔符写法不同的（{renamed.Count} 处，文件其实在，重挑一次就好）：");
    foreach (var finding in renamed)
        Console.WriteLine($"      {finding.WorkflowTitle} | {finding.ClassType}.{finding.Input}：{finding.Detail}");
    return;
}

if (mode == "hist")
{
    // 看服务端最近几条 history：成功与否、产物落在哪个节点的哪个桶里、那条 prompt 用的源视频是谁。
    // 为什么要它：PowerShell 的 ConvertFrom-Json 在 PS5 上只认两层，套深一点就全成空字符串。
    var text = await http.GetStringAsync("history?max_items=" + (args.Length > 1 ? args[1] : "8"));
    var root = JsonNode.Parse(text)!.AsObject();
    foreach (var pair in root)
    {
        var entry = pair.Value!.AsObject();
        var status = entry["status"]?["status_str"]?.ToString() ?? "?";
        // history 里的 prompt 是**数组** [序号, prompt_id, 图, extra, outputs]，图在 [2]。
        var prompt = (entry["prompt"] as JsonArray)?[2] as JsonObject ?? entry["prompt"] as JsonObject;
        var outputs = entry["outputs"] as JsonObject;
        var buckets = new List<string>();
        foreach (var node in outputs ?? new JsonObject())
            if (node.Value is JsonObject slots)
                foreach (var slot in slots)
                    buckets.Add(node.Key + ":" + slot.Key);
        var videos = new List<string>();
        if (prompt is not null)
            foreach (var node in prompt)
                if (node.Value?["inputs"]?["video"]?.ToString() is { Length: > 0 } name) videos.Add(name);
        Console.WriteLine($"{pair.Key[..8]} {status} 节点 {prompt?.Count ?? 0} 产物 [{string.Join(", ", buckets)}]"
            + (videos.Count > 0 ? " 源视频 " + string.Join("|", videos) : string.Empty));
    }
    return;
}

if (mode == "picker")
{
    // 全库扫「从服务器文件里挑一个」的输入：它的候选清单是文件（按扩展名认）。
    // 目的是拿到**这一类输入到底叫什么名字、出现在哪些节点上**——要写视频/音频进去就得先有这份账。
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();
    var video = new[] { ".mp4", ".webm", ".mov", ".mkv", ".gif", ".avi" };
    var audio = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
    var hits = new Dictionary<string, (string Kind, string Sample)>(StringComparer.Ordinal);

    foreach (var nodePair in objectInfoNode)
    {
        if (nodePair.Value is not JsonObject definition) continue;
        foreach (var group in new[] { "required", "optional" })
        {
            if (definition["input"]?[group] is not JsonObject spec) continue;
            foreach (var field in spec)
            {
                if (field.Value is not JsonArray slot || slot.Count == 0) continue;
                JsonArray? options = null;
                if (slot[0] is JsonValue typeName && typeName.TryGetValue<string>(out var name) && name == "COMBO")
                    options = slot.Count > 1 ? slot[1]?["options"] as JsonArray : null;
                else if (slot[0] is JsonArray literal) options = literal;
                if (options is null || options.Count == 0) continue;

                var sample = options.Take(50).Select(item => item?.ToString() ?? string.Empty).ToList();
                var videos = sample.Where(text => video.Any(ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToList();
                var audios = sample.Where(text => audio.Any(ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToList();
                var images = sample.Where(text => text.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || text.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || text.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
                if (videos.Count == 0 && audios.Count == 0) continue;
                if (videos.Count + audios.Count < sample.Count * 3 / 4) continue; // 主体是影音文件才算
                _ = images;

                var key = nodePair.Key + "." + field.Key;
                hits[key] = (audios.Count > videos.Count ? "音频" : "视频", sample.FirstOrDefault() ?? string.Empty);
            }
        }
    }

    Console.WriteLine($"=== 影音文件选择器 {hits.Count} 个 ===");
    foreach (var pair in hits.OrderBy(item => item.Value.Kind, StringComparer.Ordinal).ThenBy(item => item.Key, StringComparer.Ordinal))
        Console.WriteLine($"  [{pair.Value.Kind}] {pair.Key}   （例如 {pair.Value.Sample}）");
    return;
}

if (mode == "combocheck")
{
    // 拿本地那份 object_info，逐份正文核对：**固定选项（COMBO）的输入值在不在它的选项清单里**。
    // 实测 U25 的 MiniMaxH3AudioConditioningT8.task_type 存的是显示用的标签
    // 「Ref2VA — 参考生音视频」，而服务端的选项只有 Ref2VA —— 提交就是一次 400。
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();
    var site = sites.First(item => item.IsComfyUi);
    var bad = new List<string>();
    var badTotal = 0;
    var comboTotal = 0;
    var scans = 0;
    var tally = new Dictionary<string, int>(StringComparer.Ordinal);
    var unmatched = new List<string>();
    var numericBad = 0;
    var numericSamples = new List<string>();

    // 两个值是不是同一个选项：数字按数值比（清单写 1.0、文件存 1 是同一个数），其余按文本比。
    static bool SameValue(JsonNode? option, JsonNode? value)
    {
        if (option is null || value is null) return false;
        var left = option.ToString();
        var right = value.ToString();
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        return double.TryParse(left, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && double.TryParse(right, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            && x.Equals(y);
    }

    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (string.IsNullOrWhiteSpace(payload)) continue;
        if (JsonNode.Parse(payload) is not JsonObject graph) continue;
        scans++;

        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var classType = node["class_type"]?.ToString() ?? string.Empty;
            if (objectInfoNode[classType] is not JsonObject definition) continue;

            foreach (var group in new[] { "required", "optional" })
            {
                if (definition["input"]?[group] is not JsonObject spec) continue;
                foreach (var field in spec)
                {
                    if (!inputs.TryGetPropertyValue(field.Key, out var value) || value is not JsonValue jsonValue) continue;
                    if (field.Value is not JsonArray slot || slot.Count == 0) continue;

                    JsonArray? options = null;
                    if (slot[0] is JsonValue typeName && typeName.TryGetValue<string>(out var name) && name == "COMBO")
                        options = slot.Count > 1 ? slot[1]?["options"] as JsonArray : null;
                    else if (slot[0] is JsonArray literal) options = literal;
                    if (options is null) continue;

                    comboTotal++;
                    if (!jsonValue.TryGetValue<string>(out var text))
                    {
                        // 非字符串（数字下标 / 布尔）写进固定选项也是错的，而且**服务端不报错**——
                        // 实测 M17 的 quality=2 让那条链跑完什么都没有。
                        // 比的是**数值**：清单写 1.0、文件存 1 是同一个数（全库 84 处这种写法），不能算错。
                        if (options.Any(option => SameValue(option, jsonValue))) continue;
                        numericBad++;
                        tally["值是数字/布尔，不在选项里（服务端不报错，只是不产出）"] =
                            tally.GetValueOrDefault("值是数字/布尔，不在选项里（服务端不报错，只是不产出）") + 1;
                        if (numericSamples.Count < 20)
                            numericSamples.Add($"{Path.GetFileNameWithoutExtension(workflow.PayloadFile)} · {classType}.{field.Key} = {jsonValue}（选项：{string.Join("/", options.Take(5).Select(item => item?.ToString()))}）");
                        continue;
                    }
                    if (options.Any(option => option?.ToString() == text)) continue;

                    badTotal++;
                    var prefixHit = options.Select(item => item?.ToString() ?? string.Empty)
                        .Where(option => option.Length > 0 && text.StartsWith(option, StringComparison.Ordinal))
                        .OrderByDescending(option => option.Length)
                        .FirstOrDefault();
                    var bucket = classType == "LoadImage" ? "LoadImage（跑之前我们会把上传的文件名写进去）"
                        : prefixHit is not null ? "值是某个合法选项后面又缀了装饰 → 能对上：" + prefixHit
                        : "对不上，得看原稿";
                    tally[bucket] = tally.GetValueOrDefault(bucket) + 1;
                    if (bad.Count < 5 || (bucket == "对不上，得看原稿" && unmatched.Count < 40))
                    {
                        var line = $"[{bucket}] {Path.GetFileNameWithoutExtension(workflow.PayloadFile)} · {classType}.{field.Key} = 「{text}」";
                        if (bucket == "对不上，得看原稿") unmatched.Add(line);
                        else bad.Add(line);
                    }
                }
            }
        }
    }

    Console.WriteLine($"扫了 {scans} 份正文，固定选项输入共 {comboTotal} 处；**值不在选项清单里的 {badTotal} 处**");
    foreach (var pair in tally.OrderByDescending(item => item.Value)) Console.WriteLine($"    {pair.Value} 处 · {pair.Key}");
    Console.WriteLine();
    foreach (var line in bad) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine($"=== 值是数字/布尔却不在选项里的（{numericBad} 处，服务端不报错、只是那个节点不产出）===");
    foreach (var line in numericSamples) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine("=== 对不上、得看原稿的（前 40）===");
    foreach (var line in unmatched) Console.WriteLine("  " + line);
    return;
}

if (mode == "combos")
{
    // 全库扫「比例/分辨率这类固定选项」的输入：同一份工作流的选项清单只有当前那一个值，
    // 但把**全库**在同一个节点类型 + 同一个输入名下出现过的值收起来，就得到了这份清单的绝大部分。
    var site = sites.First(item => item.IsComfyUi);
    var seen = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (string.IsNullOrWhiteSpace(payload)) continue;
        var graph = JsonNode.Parse(payload)!.AsObject();
        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.ToString() ?? string.Empty;
            if (classType.Length == 0 || node["inputs"] is not JsonObject inputs) continue;
            foreach (var input in inputs)
            {
                if (Array.IndexOf(new[] { "aspect_ratio", "aspect", "ratio", "resolution", "size" }, input.Key) < 0) continue;
                if (input.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                var key = classType + "." + input.Key;
                if (!seen.TryGetValue(key, out var bucket)) seen[key] = bucket = new SortedSet<string>(StringComparer.Ordinal);
                bucket.Add(text);
            }
        }
    }

    foreach (var pair in seen.OrderByDescending(item => item.Value.Count))
    {
        Console.WriteLine($"{pair.Key}（{pair.Value.Count} 个值）");
        foreach (var value in pair.Value) Console.WriteLine("    " + value);
    }
    return;
}

if (mode == "slots")
{
    // slots <工作流 Key>：只读地报这份工作流能改什么、现在写的是什么（跑真机之前先看一眼默认值）。
    var site = sites.First(item => item.IsComfyUi);
    var workflow = site.VideoWorkflows.First(item => item.Key == args[1]);
    var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile) ?? string.Empty;
    var slots = ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots);
    Console.WriteLine($"工作流 {workflow.Title}（{workflow.NodeCount} 节点）");
    Console.WriteLine($"  收文字 {slots.CanTextToImage}｜底图 {slots.CanTakeImage}（容量 {slots.ImageCapacity}）｜只吃首帧 {slots.IsFrameDriven}");
    Console.WriteLine($"  画幅 {slots.CanResize}｜时长(帧数) {slots.CanSetLength}｜时长(写秒) {slots.CanSetSeconds}｜比例 {slots.CanSetAspect}");
    foreach (var note in slots.Notes) Console.WriteLine("  · " + note);
    return;
}

if (mode == "image")
{
    // image [工作流Key或标题前缀]：给了就按**那份工作流**出图（不给走内置模板）。
    // 带着它就能在本地核「没给满就挡」这类判据——那道挡发生在提交之前，不需要服务器真的能跑。
    var site = sites.First(item => item.IsComfyUi);
    var picked = args.Length > 1
        ? site.Workflows.FirstOrDefault(item => item.Key == args[1]
            || item.Title.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
        : null;
    if (args.Length > 1 && picked is null) { Console.WriteLine("没有这份工作流：" + args[1]); return; }
    if (picked is not null)
        Console.WriteLine($"工作流 {picked.Title}（节点 {picked.NodeCount}；失效素材槽位 {picked.MissingMedia.Count} 个）");

    var provider = new ComfyUiImageProvider(execution, config, session);
    var watch = Stopwatch.StartNew();
    var result = await provider.GenerateAsync(new ImageGenerationRequest
    {
        Prompt = imagePrompt,
        Width = 768,
        Height = 512,
        Steps = 12,
        WorkflowSiteId = picked is null ? string.Empty : site.Id,
        WorkflowKey = picked?.Key ?? string.Empty,
        WorkflowPayloadFile = picked?.PayloadFile ?? string.Empty
    });
    Console.WriteLine($"出图 {watch.Elapsed.TotalSeconds:F1}s → {result.Status} {result.FilePath}");
    if (result.Status != ImageGenerationStatus.Succeeded) Console.WriteLine("  错误：" + result.Error);
    else Console.WriteLine("  文件大小 " + new FileInfo(result.FilePath).Length + " 字节");
    return;
}

if (mode == "video")
{
    // video <workflowKey> <首帧路径>
    var key = args[1];
    var frame = Path.GetFullPath(args[2]);
    var site = sites.First(item => item.IsComfyUi);
    var workflow = site.VideoWorkflows.First(item => item.Key == key);
    var choice = new SiteWorkflowChoice(site, workflow);

    var provider = VideoProviderFactory.Create(config, choice) as ComfyUiVideoProvider;
    if (provider is null) { Console.WriteLine("这条链没建出来"); return; }
    provider.Status = message => Console.WriteLine("  [状态] " + message);
    Console.WriteLine($"工作流 {workflow.Title}（节点 {workflow.NodeCount}）；它能收首帧 = {provider.TakesFirstFrame}");
    Console.WriteLine($"参考图能力：{provider.ReferenceCapacity.Description}");

    var watch = Stopwatch.StartNew();
    var seconds = args.Length > 4 ? int.Parse(args[4]) : 0;
    // PowerShell 会把空字符串参数吞掉，所以「不给」用 - 表示。
    var ratio = args.Length > 5 && args[5] != "-" ? args[5] : string.Empty;
    var megapixels = args.Length > 6 ? double.Parse(args[6]) : 0;
    // 多路素材用逗号隔开（两段音喂「双人对白」那种有两个音频口的工作流）。
    var sourceVideos = SplitSources(args.Length > 7 ? args[7] : "-");
    var sourceAudios = SplitSources(args.Length > 8 ? args[8] : "-");
    Console.WriteLine($"这一镜要的是：{seconds} 秒 · 比例「{(ratio.Length == 0 ? "跟随首帧" : ratio)}」· {(megapixels > 0 ? megapixels + "MP" : "不限像素")}"
        + (sourceVideos.Length > 0 ? $" · 源视频 {string.Join("、", sourceVideos.Select(Path.GetFileName))}" : string.Empty)
        + (sourceAudios.Length > 0 ? $" · 源音频 {string.Join("、", sourceAudios.Select(Path.GetFileName))}" : string.Empty));
    var result = await provider.GenerateAsync(new VideoGenerationRequest
    {
        Prompt = args.Length > 3 ? args[3] : "镜头缓慢推进，少女回眸，发丝与雪片轻动",
        Width = 0,
        Height = 0,
        Seconds = seconds,
        AspectRatio = ratio,
        Megapixels = megapixels,
        ReferenceImages = new[] { frame },
        SourceVideos = sourceVideos,
        SourceAudios = sourceAudios,
        WorkflowSiteId = site.Id,
        WorkflowPayloadFile = workflow.PayloadFile,
        WorkflowKey = workflow.Key
    });
    Console.WriteLine($"出视频 {watch.Elapsed.TotalSeconds:F1}s → {result.Status} {result.FilePath}");
    if (result.ReferenceNote.Length > 0) Console.WriteLine("  参考图说明：" + result.ReferenceNote);
    if (result.Note.Length > 0) Console.WriteLine("  这一镜实际生效的：\n    " + result.Note.Replace("\n", "\n    "));
    if (result.Status != VideoGenerationStatus.Succeeded) { Console.WriteLine("  错误：" + result.Error); return; }
    Console.WriteLine("  文件大小 " + new FileInfo(result.FilePath).Length + " 字节");
    Console.WriteLine("  容器头：" + JsonSerializer.Serialize(Describe(result.FilePath)));
    return;
}

if (mode == "verify")
{
    // 把 316 份原稿全拉下来，用**当前**转换器转一遍，再对每一处「缺的必填输入」分类：
    //   ① 原稿里那个输入本来就没连线        → 工作流自己缺，不是我们的问题
    //   ② 源头节点被静音(2)/绕过(4)         → 官方语义就是「接不上就删掉」，正常
    //   ③ 源头是正常节点(mode=0)            → **我们丢了**，这才是缺陷
    //   ④ 原稿里找不到那个节点（子图内部）  → 判不了，单列
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();
    var listText = await http.GetStringAsync("userdata?dir=workflows&recurse=true&full_info=true");
    var paths = new List<string>();
    foreach (var entry in JsonDocument.Parse(listText).RootElement.EnumerateArray())
    {
        var path = entry.ValueKind == JsonValueKind.String
            ? entry.GetString()
            : entry.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path!);
    }

    Console.WriteLine($"原稿 {paths.Count} 份，逐份转换…");

    // 这一支**不再自己判**「缺的必填输入该算谁的」：那种判定（转发链怎么穿、Set/Get 怎么配对、
    // 静音/绕过是什么意思）产品里已经有一整套（ComfyUiImportAuditor），探针再实现一遍只会两边打架——
    // 之前就出现过 verify 报「我们丢了 2 处」、产品体检报 0 处，逐节点核下来是探针把
    // 「名字为空的 GetNode」「输入悬空的 Reroute」误算成了我们的问题。所以账直接交给产品那套，
    // 保证「探针说的」与「导入时用户看到的」永远是同一个结论。
    var raws = new Dictionary<string, string>(StringComparer.Ordinal);
    var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
    var failed = 0;
    foreach (var path in paths)
    {
        string uiText;
        try { uiText = await http.GetStringAsync("userdata/" + Uri.EscapeDataString("workflows/" + path)); }
        catch (Exception error) { _ = error; failed++; continue; }

        try { payloads[path] = ComfyUiWorkflowConversion.Convert(uiText, objectInfoNode).ApiWorkflow.ToJsonString(); }
        catch (Exception error) { _ = error; failed++; continue; }

        raws[path] = uiText;
    }

    var report = ComfyUiImportAuditor.Inspect(raws, payloads, objectInfoNode);
    Console.WriteLine(report.Describe());
    if (failed > 0) Console.WriteLine($"（{failed} 份没能拉到或转换失败，未计入）");
    return;
}

if (mode == "inspect")
{
    // 逐节点看一份原稿：某个节点的输入/输出/控件值，以及「前端转发」链上的真实源头在哪。
    var uiPath = args[1];
    var ui = JsonNode.Parse(File.ReadAllText(uiPath))!.AsObject();
    var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    if (ui["nodes"] is JsonArray list)
        foreach (var item in list)
        {
            if (item is not JsonObject node) continue;
            var id = node["id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) nodes[id!] = node;
        }

    var linkById = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
    foreach (var row in ui["links"] as JsonArray ?? new JsonArray())
        if (row is JsonArray line && line.Count >= 5 && line[0]?.ToString() is { } lid) linkById[lid] = line;

    Console.WriteLine($"节点 {nodes.Count} 个，连线 {linkById.Count} 条");
    foreach (var want in args.Skip(2))
    {
        if (!nodes.TryGetValue(want, out var node)) { Console.WriteLine($"  [{want}] 不存在"); continue; }
        var type = node["type"]?.ToString();
        Console.WriteLine($"  [{want}] {type} mode={node["mode"]} widgets={(node["widgets_values"]?.ToJsonString() ?? "—")}");
        foreach (var input in node["inputs"] as JsonArray ?? new JsonArray())
        {
            if (input is not JsonObject slot) continue;
            var link = slot["link"]?.ToString();
            var origin = link is not null && linkById.TryGetValue(link, out var line)
                ? $"{line[1]}@{line[2]}" : "—";
            Console.WriteLine($"      入 {slot["name"]} / {slot["type"]} / link={link ?? "null"} ← {origin}");
        }
        foreach (var output in node["outputs"] as JsonArray ?? new JsonArray())
            if (output is JsonObject slot)
                Console.WriteLine($"      出 {slot["name"]} / {slot["type"]} / links={slot["links"]?.ToJsonString() ?? "[]"}");
    }

    // Set/Get 一族点名对照：名字来自 widgets_values[0] 或 title。
    Console.WriteLine("  前端 Set/Get/Reroute 全表：");
    foreach (var pair in nodes)
    {
        var type = pair.Value["type"]?.ToString() ?? string.Empty;
        var flat = new string(type.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (flat is "setnode" or "getnode" or "nodeset" or "nodeget" or "easysetnode" or "easygetnode" or "reroute"
            || type.Contains("Set", StringComparison.Ordinal) && type.Contains("Node", StringComparison.Ordinal)
            || type.Contains("Get", StringComparison.Ordinal) && type.Contains("Node", StringComparison.Ordinal))
            Console.WriteLine($"      {pair.Key} {type} 名={pair.Value["widgets_values"]?[0]?.ToString() ?? "—"} mode={pair.Value["mode"]}");
    }
    return;
}

if (mode == "verifydir")
{
    // 和 verify 同一套分类，但原稿从本地目录读（服务器没开也能跑）。
    var dir = args.Length > 1 ? args[1] : archiveDir;
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();
    var files = Directory.GetFiles(dir, "*.json").OrderBy(item => item, StringComparer.Ordinal).ToList();

    Console.WriteLine($"原稿 {files.Count} 份，逐份转换并分类…");
    var noLink = 0;      // 原稿自己就断线/没有 Set 配对 —— 工作流自身缺陷，不是我们丢的
    var muted = 0;       // 源头静音(2)/绕过(4) —— 官方语义就是删掉那一项，正常
    var dropped = 0;     // 源头是个活着的后端节点 —— 这才是我们丢的
    var unknown = 0;
    var clean = 0;
    var missingTotal = 0;
    var badFiles = 0;
    var defectSamples = new List<string>();
    var mutedSamples = new List<string>();
    var deadSamples = new List<string>();
    var unknownSamples = new List<string>();

    foreach (var file in files)
    {
        string uiText;
        try { uiText = File.ReadAllText(file); }
        catch (Exception error) { _ = error; continue; }

        string apiText;
        try { apiText = ComfyUiWorkflowConversion.Convert(uiText, objectInfoNode).ApiWorkflow.ToJsonString(); }
        catch (Exception error) { Console.WriteLine("  转不了 " + Path.GetFileName(file) + "：" + error.Message); continue; }

        var ui = JsonNode.Parse(uiText)!.AsObject();
        var uiById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (ui["nodes"] is JsonArray uiNodes)
        {
            foreach (var item in uiNodes)
            {
                if (item is not JsonObject node) continue;
                var id = item["id"]?.ToString();
                if (!string.IsNullOrEmpty(id)) uiById[id!] = node;
            }
        }

        // 把子图也一并「展开」成扁平节点表（id 规则同转换器：容器id:内部id），
        // 这样连子图内部节点缺的输入也能顺着原稿找到源头。
        var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (ui["definitions"]?["subgraphs"] is JsonArray subs)
            foreach (var item in subs)
                if (item is JsonObject def && def["id"]?.ToString() is { Length: > 0 } defId) defs[defId] = def;

        var flatNodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var flatLinks = new Dictionary<string, (string Origin, int OriginSlot)>(StringComparer.Ordinal);

        void AddLinks(JsonNode? source, string prefix)
        {
            if (source is JsonArray rows)
            {
                foreach (var row in rows)
                    if (row is JsonArray line && line.Count >= 5 && line[0]?.ToString() is { } lid)
                        flatLinks[prefix + lid] = (line[1]!.ToString(), ReadSlot(line[2]));
            }
            else if (source is JsonObject objects)
            {
                foreach (var pair in objects)
                    if (pair.Value is JsonObject row)
                    {
                        var origin = row["origin_id"]?.ToString() ?? "-20";
                        var key = origin is "-10" or "-20" ? origin : prefix + origin;
                        flatLinks[prefix + pair.Key] = (key, ReadSlot(row["origin_slot"]));
                    }
            }
        }

        void AddNodes(JsonArray? source, string prefix)
        {
            foreach (var item in source ?? new JsonArray())
            {
                if (item is not JsonObject node) continue;
                var id = node["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                flatNodes[prefix + id] = node;
                var type = node["type"]?.ToString() ?? string.Empty;
                if (!defs.TryGetValue(type, out var def)) continue;
                var inner = prefix + id + ":";
                AddLinks(def["links"], inner);
                AddNodes(def["nodes"] as JsonArray, inner);
            }
        }

        AddLinks(ui["links"], string.Empty);
        AddNodes(ui["nodes"] as JsonArray, string.Empty);

        static int ReadSlot(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        static string ParentPrefix(string key)
        {
            var cut = key.LastIndexOf(':');
            return cut < 0 ? string.Empty : key[..(cut + 1)];
        }

        // 顺「只转发」的前端节点（Reroute / Set / Get / easy 版 / Fast Bypasser）往上找真正的源头：
        //   live  = 源头是个活着的后端节点 → 我们丢了这一项
        //   muted = 源头被静音(2)或绕过(4) → 官方语义就是删掉
        //   dead  = 链子在这份原稿里本来就断了（没接线的 Reroute、没有同名 Set 的 Get）→ 工作流自身缺陷
        string Classify(string key, int guard)
        {
            if (guard > 64) return "dead";
            if (!flatNodes.TryGetValue(key, out var node)) return "dead";
            var mode = node["mode"] is JsonValue mv && mv.TryGetValue<int>(out var m) ? m : 0;
            if (mode is 2 or 4) return "muted";
            var prefix = ParentPrefix(key);
            var type = node["type"]?.ToString() ?? string.Empty;
            var flat = new string(type.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

            if (flat is "reroute" or "setnode" or "nodeset" or "easysetnode" or "easygetnode" or "getnode" or "nodeget"
                || flat.StartsWith("fastbypasser", StringComparison.Ordinal))
            {
                var target = node;
                if (flat is "getnode" or "nodeget" or "easygetnode")
                {
                    var name = (node["widgets_values"] as JsonArray)?[0]?.ToString() ?? string.Empty;
                    if (name.Length == 0) return "dead";
                    var setter = flatNodes.FirstOrDefault(item =>
                        new string((item.Value["type"]?.ToString() ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())
                            is "setnode" or "nodeset" or "easysetnode"
                        && ((item.Value["widgets_values"] as JsonArray)?[0]?.ToString() ?? string.Empty) == name);
                    if (setter.Value is null) return "dead";
                    key = setter.Key;
                    prefix = ParentPrefix(key);
                    target = setter.Value;
                }

                var raw = (target["inputs"] as JsonArray)?
                    .FirstOrDefault(item => item is JsonObject slot && slot["link"] is not null && slot["link"]!.ToString() != "null") is JsonObject hit
                    ? hit["link"]!.ToString()
                    : null;
                if (raw is null || !flatLinks.TryGetValue(prefix + raw, out var link)) return "dead";

                // 源头是「容器自己的输入」：跟着父级那条线继续往上走。
                if (link.Origin == "-10") return ClassifyThroughContainer(key, link.OriginSlot, guard);
                return Classify(link.Origin, guard + 1);
            }

            return "live";

            string ClassifyThroughContainer(string innerKey, int slot, int depth)
            {
                if (depth > 64) return "dead";
                var containerKey = ParentPrefix(innerKey).TrimEnd(':');
                var outer = ParentPrefix(containerKey);
                if (!flatNodes.TryGetValue(containerKey, out var container)) return "dead";
                if ((container["inputs"] as JsonArray) is not { } slots || slot >= slots.Count) return "dead";
                if (slots[slot] is not JsonObject input || input["link"] is null || input["link"]!.ToString() == "null") return "dead";
                var outerLinkId = input["link"]!.ToString();
                if (!flatLinks.TryGetValue(outer + outerLinkId, out var parentLink)) return "dead";
                if (parentLink.Origin == "-10") return ClassifyThroughContainer(containerKey, parentLink.OriginSlot, depth + 1);
                return Classify(parentLink.Origin, depth + 1);
            }
        }

        var api = JsonNode.Parse(apiText)!.AsObject();
        var any = false;
        foreach (var pair in api)
        {
            if (pair.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? string.Empty;
            if (classType.Length == 0) continue;
            if (objectInfoNode[classType] is not JsonObject definition) continue;
            if (definition["input"]?["required"] is not JsonObject required) continue;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (node["inputs"] is JsonObject inputs)
                foreach (var input in inputs) declared.Add(input.Key);

            foreach (var requiredInput in required)
            {
                if (declared.Contains(requiredInput.Key)) continue;
                if (declared.Any(name => name.StartsWith(requiredInput.Key + ".", StringComparison.Ordinal))) continue;

                any = true;
                missingTotal++;
                var label = $"{Path.GetFileNameWithoutExtension(file)} · {classType}.{requiredInput.Key}";
                if (!flatNodes.TryGetValue(pair.Key, out var uiNode)) { unknown++; if (unknownSamples.Count < 10) unknownSamples.Add($"{label}（节点 {pair.Key} 在原稿里找不到）"); continue; }

                var link = (uiNode["inputs"] as JsonArray)?.FirstOrDefault(item =>
                    item is JsonObject input && input["name"]?.GetValue<string>() == requiredInput.Key) is JsonObject found
                    ? found["link"]?.ToString()
                    : null;
                if (string.IsNullOrEmpty(link) || link == "null") { noLink++; continue; }

                var myKey = pair.Key;
                if (!flatLinks.TryGetValue(ParentPrefix(myKey) + link, out var row)) { noLink++; continue; }
                var sourceKey = row.Origin == "-10"
                    ? null
                    : row.Origin;
                var verdict = sourceKey is null ? "dead" : Classify(sourceKey, 0);
                var sourceType = sourceKey is not null && flatNodes.TryGetValue(sourceKey, out var sourceNode)
                    ? sourceNode["type"]?.ToString() : row.Origin;
                if (verdict == "muted")
                {
                    muted++;
                    if (mutedSamples.Count < 8) mutedSamples.Add($"{label}（源头 {sourceKey} {sourceType}）");
                }
                else if (verdict == "dead")
                {
                    noLink++;
                    if (deadSamples.Count < 8) deadSamples.Add($"{label}（源头 {sourceKey} {sourceType}，链子在这份原稿里就断着）");
                }
                else if (verdict == "live")
                {
                    dropped++;
                    if (defectSamples.Count < 20) defectSamples.Add($"{label}（源头 {sourceKey} {sourceType} mode=0）");
                }
                else unknown++;
            }
        }

        if (any) badFiles++;
        else clean++;
    }

    Console.WriteLine();
    Console.WriteLine($"{files.Count} 份里：缺必填输入 **{missingTotal} 处 / {badFiles} 份**；");
    Console.WriteLine($"其中 **我们丢了 {dropped} 处**；源头被静音/绕过（正常）{muted} 处；原稿自己断线/缺 Set（工作流自身）{noLink} 处；判不了 {unknown} 处");
    Console.WriteLine($"一份都不缺的：{clean} 份");
    Console.WriteLine();
    Console.WriteLine("=== 我们丢了的（前 20，这些才是要修的）===");
    foreach (var line in defectSamples) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine("=== 原稿自己断线的（前 8）===");
    foreach (var line in deadSamples) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine("=== 被静音/绕过那种（正常，前 8）===");
    foreach (var line in mutedSamples) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine("=== 判不了的（前 10）===");
    foreach (var line in unknownSamples) Console.WriteLine("  " + line);
    return;
}

if (mode == "reimport")
{
    // 照应用里「重新导入」的同一条路径重跑一遍：FetchAsync（拉清单+原稿+转换）→ Install（落盘）。
    // 先整份备份，出问题能原样放回去。
    var site = sites.First(item => item.IsComfyUi);
    var payloadDir = Path.Combine(AppPaths.Root!, "skills", "sites", site.Id);
    var backup = Path.Combine(work, "site-before");
    CopyDirectory(payloadDir, backup);
    Console.WriteLine($"已备份 {Directory.GetFiles(backup).Length} 份正文到 {backup}");
    Console.WriteLine($"站点：{site.DisplayName}（{site.Id}）共 {site.Workflows.Count} 份");

    var watch = Stopwatch.StartNew();
    var result = await ComfyUiLibrary.FetchAsync(
        site.BaseUrl,
        progress: new Progress<ComfyUiLibraryProgress>(item =>
        {
            if (item.Done % 50 == 0) Console.WriteLine("  " + item.Describe());
        }));
    Console.WriteLine($"拉取 + 转换用时 {watch.Elapsed.TotalSeconds:F0} 秒：{result.Workflows.Count} 份，"
        + $"转换成功 {result.Converted}、失败 {result.Failed}");
    foreach (var note in result.Notes) Console.WriteLine("  · " + note);

    var (installed, error) = ComfyUiLibrary.Install(result, site.DisplayName, site.Checkpoint, site);
    Console.WriteLine(error.Length > 0
        ? "安装失败：" + error
        : $"已安装：{installed!.Workflows.Count} 份（推荐项 {installed.Workflows.Count(w => w.Recommended)}）");
    if (installed is null) return;

    // 导入时新算出来的那张「文件选择槽」表（判据只在服务端的节点定义里，生成时读不到，所以落在这儿）。
    Console.WriteLine($"「文件选择槽」表 {installed.FileSlots.Count} 条");
    foreach (var kind in new[] { ComfyUiFileSlotKinds.Image, ComfyUiFileSlotKinds.Video, ComfyUiFileSlotKinds.Audio })
    {
        var group = installed.FileSlots.Where(pair => pair.Value == kind).Select(pair => pair.Key).OrderBy(item => item).ToList();
        Console.WriteLine($"  {kind}：{group.Count} 条" + (kind == ComfyUiFileSlotKinds.Image
            ? "　" + string.Join("、", group) : string.Empty));
    }

    // 拿那份多参工作流实地看一眼：认出来几格、额度几、说明里怎么说。
    foreach (var workflow in installed.Workflows.Where(item => item.Title.StartsWith("U23-", StringComparison.Ordinal)))
    {
        var payload = SiteCatalog.LoadPayload(installed.Id, workflow.PayloadFile) ?? string.Empty;
        var slots = ComfyUiWorkflowBinder.Detect(payload, installed.OptionValues, installed.FileSlots);
        Console.WriteLine($"工作流 {workflow.Title}：收文字 {slots.CanTextToImage}｜底图 {slots.CanTakeImage}"
            + $"（{slots.FileSlotImages.Count} 格，额度 {slots.ImageCapacity}）"
            + $"｜源视频 {slots.VideoNodeIds.Count} 个 / 源音频 {slots.AudioNodeIds.Count} 个");
        Console.WriteLine("  图槽：" + string.Join("、", slots.FileSlotImages.Select(slot => slot.NodeId + "." + slot.Input)));
        Console.WriteLine("  影音槽：" + string.Join("、",
            slots.VideoNodeIds.Select((id, i) => id + "." + slots.VideoInputs[i])
                .Concat(slots.AudioNodeIds.Select((id, i) => id + "." + slots.AudioInputs[i]))));
        foreach (var note in slots.Notes.Where(item => item.Contains("文件选择槽") || item.Contains("源视频")))
            Console.WriteLine("  · " + note);
    }
    return;
}

if (mode == "lint-ui")
{
    // 拿服务器上那份**网页格式原稿**跑一遍转换，再核转换结果的必填输入——
    // 比核存量正文更硬：存量正文是旧转换器留下的，改了转换器它不会自己变好。
    var uiPath = args[1];
    var objectInfoPath = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "object_info.json");
    var objectInfoJson = File.ReadAllText(objectInfoPath);
    var objectInfoNode = JsonNode.Parse(objectInfoJson)!.AsObject();

    var uiText = File.ReadAllText(uiPath);
    var converted = ComfyUiWorkflowConversion.Convert(uiText, objectInfoNode);
    Console.WriteLine("转换说明：" + converted.SkippedSummary);

    var api = converted.ApiWorkflow;
    var missing = 0;
    var empty = 0;
    var samples = new List<string>();
    foreach (var pair in api)
    {
        if (pair.Value is not JsonObject node) continue;
        var classType = node["class_type"] is JsonValue ct && ct.TryGetValue<string>(out var name) ? name : string.Empty;
        if (classType.Length == 0) continue;
        var inputs = node["inputs"] as JsonObject;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (inputs is null) empty++;
        else
        {
            foreach (var input in inputs) declared.Add(input.Key);
            if (inputs.Count == 0) empty++;
        }

        if (objectInfoNode[classType] is not JsonObject definition) continue;
        if (definition["input"] is not JsonObject spec) continue;
        if (spec["required"] is not JsonObject required) continue;
        foreach (var requiredInput in required)
        {
            if (declared.Contains(requiredInput.Key)) continue;
            missing++;
            if (samples.Count < 8) samples.Add($"{classType}.{requiredInput.Key}");
        }
    }

    Console.WriteLine($"转换后：节点 {api.Count} 个；**缺必填输入 {missing} 处**；inputs 为空 {empty} 个");
    foreach (var line in samples) Console.WriteLine("   " + line);
    return;
}

if (mode == "lint")
{
    // 离线体检：拿服务端的 /object_info（节点定义）逐份核正文，看每个节点的**必填输入**在不在。
    // 判据来自服务端自己：required 里列了的输入，正文里必须有一个（连线或字面量都算）——
    // 少了它，提交时就是 required_input_missing（实测 G09 的 WanVideoEmptyEmbeds 就是这样被拒的）。
    var objectInfoPath = args.Length > 1
        ? args[1]
        : Path.Combine(Path.GetTempPath(), "object_info.json");
    using var objectInfo = JsonDocument.Parse(File.ReadAllBytes(objectInfoPath));
    var site = sites.First(item => item.IsComfyUi);

    var emptyInputs = 0;
    var dangling = 0;
    var missingTotal = 0;
    var badWorkflows = new List<(string Title, string Kind, int Missing, string Sample)>();
    var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
    var total = 0;

    foreach (var workflow in site.Workflows)
    {
        string payload;
        try { payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile) ?? string.Empty; }
        catch (Exception error) { _ = error; continue; }
        if (payload.Length == 0) continue;
        total++;

        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { continue; }
        using (document)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in document.RootElement.EnumerateObject()) ids.Add(node.Name);

            // 只看**真正会被执行的分支**：从「输出没人接」的末端节点倒着走一遍，走不到的不算。
            // 为什么：源头被静音（mode=2）或被绕过（mode=4）时，官方语义本来就会把下游那个输入删掉，
            // 而那种节点往往整条分支都不参与执行——把它算成缺陷会淹掉真问题（实测 J03、G03 就是这样）。
            var origins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var hasConsumer = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in document.RootElement.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("inputs", out var ins) || ins.ValueKind != JsonValueKind.Object) continue;
                foreach (var input in ins.EnumerateObject())
                {
                    if (input.Value.ValueKind != JsonValueKind.Array || input.Value.GetArrayLength() < 1) continue;
                    if (input.Value[0].ValueKind != JsonValueKind.String) continue;
                    var origin = input.Value[0].GetString() ?? string.Empty;
                    if (origin.Length == 0) continue;
                    if (!origins.TryGetValue(node.Name, out var list)) origins[node.Name] = list = new List<string>();
                    list.Add(origin);
                    hasConsumer.Add(origin);
                }
            }

            var live = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var id in ids)
            {
                if (hasConsumer.Contains(id)) continue;
                if (live.Add(id)) queue.Enqueue(id);
            }
            while (queue.Count > 0)
            {
                if (!origins.TryGetValue(queue.Dequeue(), out var list)) continue;
                foreach (var origin in list)
                    if (live.Add(origin)) queue.Enqueue(origin);
            }

            var missing = 0;
            var samples = new List<string>();
            foreach (var node in document.RootElement.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;
                var classType = node.Value.TryGetProperty("class_type", out var ct) && ct.ValueKind == JsonValueKind.String
                    ? ct.GetString() ?? string.Empty
                    : string.Empty;
                if (classType.Length == 0) continue;

                var inputs = node.Value.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Object
                    ? ins
                    : default;

                var declared = new HashSet<string>(StringComparer.Ordinal);
                if (inputs.ValueKind == JsonValueKind.Object)
                {
                    var count = 0;
                    foreach (var input in inputs.EnumerateObject())
                    {
                        count++;
                        declared.Add(input.Name);
                        // 连线指向一个不存在的节点：也是「正文坏了」的一种。
                        if (input.Value.ValueKind == JsonValueKind.Array && input.Value.GetArrayLength() >= 1
                            && input.Value[0].ValueKind == JsonValueKind.String
                            && input.Value[0].GetString() is { Length: > 0 } target
                            && !ids.Contains(target))
                            dangling++;
                    }
                    if (count == 0) emptyInputs++;
                }
                else
                {
                    emptyInputs++;
                }

                if (!objectInfo.RootElement.TryGetProperty(classType, out var definition)) continue;
                if (!definition.TryGetProperty("input", out var inputSpec)) continue;
                if (!inputSpec.TryGetProperty("required", out var required)
                    || required.ValueKind != JsonValueKind.Object) continue;

                foreach (var requiredInput in required.EnumerateObject())
                {
                    if (declared.Contains(requiredInput.Name)) continue;
                    // 动态输入：`values` 与 `values.a` 同时存在时，服务端收的是带前缀的那个——
                    // 这是**误报**（实测 U02 的 ComfyMathExpression 就是这样，服务端照收不误）。
                    if (declared.Any(name => name.StartsWith(requiredInput.Name + ".", StringComparison.Ordinal))) continue;
                    if (!live.Contains(node.Name)) continue;   // 死分支不算
                    missing++;
                    missingTotal++;
                    byClass[classType] = byClass.GetValueOrDefault(classType) + 1;
                    if (samples.Count < 3) samples.Add($"{classType}.{requiredInput.Name}");
                }
            }

            if (missing > 0)
                badWorkflows.Add((workflow.Title, workflow.Kind, missing, string.Join("、", samples)));
        }
    }

    Console.WriteLine($"正文 {total} 份：其中**缺必填输入**的 {badWorkflows.Count} 份，缺的输入合计 {missingTotal} 处；"
        + $"inputs 为空的节点 {emptyInputs} 个；指向不存在节点的连线 {dangling} 条");
    Console.WriteLine();
    Console.WriteLine("=== 缺得最多的类（前 12）===");
    foreach (var pair in byClass.OrderByDescending(pair => pair.Value).Take(12))
        Console.WriteLine($"  {pair.Value,4} 处  {pair.Key}");
    Console.WriteLine();
    Console.WriteLine("=== 缺得最多的正文（前 12）===");
    foreach (var item in badWorkflows.OrderByDescending(item => item.Missing).Take(12))
        Console.WriteLine($"  {item.Missing,4} 处 [{item.Kind}] {item.Title}：{item.Sample}");
    return;
}

if (mode == "sweep")
{
    // 环境体检：把候选工作流挨个**投一次**，等几秒看它会不会立刻报错，然后中断。
    // 为什么要这么做：一次真正的出视频要十几分钟，而绝大多数失败在头几秒就发生了
    //（缺模型、自定义节点与环境不兼容）——先把这些筛掉，再挑一份真跑，不然十几分钟是白等。
    var site = sites.First(item => item.IsComfyUi);
    // 用法：sweep <首帧路径> [工作流 Key…]；不给 Key 就把所有「时长可改」的都体检一遍。
    var frame = args.Length > 1 ? Path.GetFullPath(args[1]) : string.Empty;
    var wanted = args.Length > 2
        ? args.Skip(2).ToList()
        : site.VideoWorkflows
            .Where(w => ComfyUiWorkflowBinder.Detect(SiteCatalog.LoadPayload(site.Id, w.PayloadFile) ?? "{}", site.OptionValues, site.FileSlots).CanSetLength)
            .Select(w => w.Key)
            .ToList();

    Console.WriteLine($"要体检 {wanted.Count} 份；每份投出去等 10 秒看有没有立刻报错。首帧："
        + (frame.Length > 0 && File.Exists(frame) ? Path.GetFileName(frame) : "（没有，按文生视频投）"));
    foreach (var key in wanted)
    {
        var workflow = site.VideoWorkflows.FirstOrDefault(item => item.Key == key);
        if (workflow is null) { Console.WriteLine($"[跳过] 找不到 {key}"); continue; }
        var template = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile) ?? string.Empty;
        var slots = ComfyUiWorkflowBinder.Detect(template, site.OptionValues, site.FileSlots);
        var inputs = new Dictionary<string, JsonElement>
        {
            ["prompt"] = JsonSerializer.SerializeToElement(slots.CanTextToImage ? "一位红发少女站在雪中，电影质感" : string.Empty),
            ["negativePrompt"] = JsonSerializer.SerializeToElement(string.Empty),
            ["workflowTemplate"] = JsonSerializer.SerializeToElement(template),
            ["workflowSlots"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(slots))
        };
        if (slots.CanTakeImage && File.Exists(frame)) inputs["referenceImages"] = JsonSerializer.SerializeToElement(new[] { frame });

        var invocation = new Invocation
        {
            Tool = "image-to-video",
            Capability = Capability.ImageToVideo,
            Channel = "comfyui",
            Inputs = inputs
        };

        var watch = Stopwatch.StartNew();
        try
        {
            var started = await execution.StartAsync(session, invocation, $"sweep-{Guid.NewGuid():N}", CancellationToken.None);
            if (string.IsNullOrWhiteSpace(started.ExternalTaskId))
            {
                Console.WriteLine($"[拒收] {workflow.Title}：{started.ErrorMessage}");
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            var update = await externalProvider.GetStatusAsync(started.ExternalTaskId, CancellationToken.None);
            var verdict = update.State switch
            {
                ExternalTaskState.Failed => "失败：" + update.ErrorMessage,
                ExternalTaskState.Succeeded => "已经跑完（这么快，多半是空跑）",
                _ => "受理了，10 秒内没报错（环境看起来是好的）"
            };
            Console.WriteLine($"[{(update.State == ExternalTaskState.Failed ? "坏" : "好")}] {workflow.NodeCount} 节点 {workflow.Title}"
                + $"（{watch.Elapsed.TotalSeconds:F1}s）{verdict}");

            if (update.State is ExternalTaskState.Running or ExternalTaskState.Queued)
            {
                // 直接打 ComfyUI 的 interrupt：**不要**走 execution.Cancel——它在这里会抛
                // KeyNotFoundException（这个体检进程的任务表与宿主不是同一份），
                // 而抛出去就意味着「任务没被中断，继续占着显卡」，实测会让后面每一份都排在它后面。
                await http.PostAsJsonAsync("interrupt", new { }, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"[异常] {workflow.Title}：{error.GetType().Name} {error.Message}");
        }
    }

    return;
}

if (mode == "concat")
{
    // concat <第一段> [第二段…]：把已经出好的段接成一段，验「串片」这一步。
    var inputs = args.Skip(1).Select(Path.GetFullPath).ToList();
    if (inputs.Count == 1) inputs.Add(inputs[0]);   // 只给一段就复制成两段（同参数必然可拼）
    var output = Path.Combine(work, "joined.mp4");
    var watch = Stopwatch.StartNew();
    var result = Mp4Concatenator.Concat(inputs, output);
    Console.WriteLine($"拼接 {watch.Elapsed.TotalSeconds:F1}s → Ok={result.Ok} {result.Error}");
    if (!result.Ok) return;
    Console.WriteLine("  输出：" + output + "（" + new FileInfo(output).Length + " 字节）");
    Console.WriteLine("  容器头：" + JsonSerializer.Serialize(Mp4Concatenator.Probe(output)));
    return;
}

if (mode is "serve" or "demolocal")
{
    // 本地假 ComfyUI：真机不在手边（或真机时段已到）时，也能把
    // 「导入 → 体检 → 让大模型认节点 → 重转 → 落盘」这条链**整条**演一遍。
    //
    // 数据全用真东西：节点定义是那台机器的 /object_info 留档（%TEMP%\object_info.json，20.8 MB），
    // 工作流正文是 316 份原稿的本地留档（raw-wf\）。另外可选塞一份**故意留了缺口**的原稿——
    // 中间夹着一段我们不认识的纯前端节点，它下游那个必填输入会整项消失。真机上「我们丢了 0 处」，
    // 弹窗根本不会出现，所以不造这一份就演不了「有问题」的那个分支。
    //
    //   serve    <端口> [crafted]  只起服务器（供真应用来导），不起就 Ctrl+C。
    //   demolocal<端口> [crafted]  起服务器并**就地**把导入整链跑一遍（用真模型），跑完退出。
    var port = args.Length > 1 ? int.Parse(args[1]) : 8188;
    var crafted = args.Length > 2 && args[2] == "crafted";
    var (drafts, objectInfoText) = LocalComfyStub.Load(archiveDir, crafted);
    LocalComfyStub.Start(port, drafts, objectInfoText, Console.WriteLine);
    var localUrl = $"http://127.0.0.1:{port}";
    Console.WriteLine($"工作流 {drafts.Count} 份" + (crafted ? "（含 1 份**故意留缺口**的演示原稿）" : "（全是真原稿）"));

    if (mode == "serve")
    {
        Console.WriteLine($"在真应用的「API 导入」里贴：{localUrl}");
        Console.WriteLine("按 Ctrl+C 结束。");
        await Task.Delay(Timeout.Infinite);
        return;
    }

    // demolocal：整链就地跑一遍。站点落到**临时工程**里，不碰用户真正的项目。
    var demoProject = Path.Combine(Path.GetTempPath(), "comfy-demo-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(demoProject);
    AppPaths.UseProject(ProjectContext.Create(demoProject, "comfy-demo"));
    Console.WriteLine($"演示工程 {AppPaths.Root}");

    var fetched = await ComfyUiLibrary.FetchAsync(localUrl, null,
        new Progress<ComfyUiLibraryProgress>(item => { if (item.Done % 100 == 0) Console.WriteLine("  " + item.Describe()); }));
    Console.WriteLine($"拉取 + 转换：{fetched.Workflows.Count} 份，成功 {fetched.Converted}、失败 {fetched.Failed}");
    Console.WriteLine("—— 导入前体检 ——");
    Console.WriteLine(fetched.Audit?.Describe() ?? "（没有体检结论）");

    if (fetched.Audit is { NeedsAttention: true })
    {
        // 这一步就是弹窗里「让大模型认一认」按下去之后跑的东西：同一份 fetched、同一个接口。
        var completer = AiProviderFactory.CreateJsonCompleter();
        if (completer is null)
        {
            Console.WriteLine("【弹窗会显示】未接入大模型，那个按钮是灰的。");
        }
        else
        {
            Console.WriteLine("【弹窗会显示】已接入大模型，可以按「让大模型认一认」。开始认…");
            var learning = await ComfyUiVirtualNodeLearner.LearnAsync(
                fetched, completer, Array.Empty<ComfyUiVirtualNodeRule>(), Console.WriteLine);
            Console.WriteLine("—— 模型认完 ——");
            Console.WriteLine(learning.Describe());
            fetched = ComfyUiLibrary.Reconvert(fetched, learning.Accepted);
            Console.WriteLine("—— 重转之后的体检 ——");
            Console.WriteLine(fetched.Audit?.Describe() ?? "（没有体检结论）");
        }
    }
    else
    {
        Console.WriteLine("【弹窗不会出现】没有「我们转换时丢掉的输入」——这正是真机的样子。");
    }

    var (site, installError) = ComfyUiLibrary.Install(fetched, "本地假服务器", string.Empty, null);
    Console.WriteLine(installError.Length > 0
        ? "落盘失败：" + installError
        : $"已落盘：{site!.Workflows.Count} 份（其中 {site.Workflows.Count(w => w.DroppedInputs > 0)} 份带「我们丢过」记号、"
          + $"{site.Workflows.Count(w => w.UncertainInputs > 0)} 份带「判断不了」记号、"
          + $"{site.Workflows.Count(w => w.BrokenInputs > 0)} 份带「自有断线」记号；学到的前端节点规则 {site.VirtualNodeRules.Count} 条）");
    return;
}

if (mode == "demogen")
{
    // demogen <端口> <工作流Key> <首帧路径> [源视频] [源音频]（不给的用 - 占位）
    // 起本地假 ComfyUI（**连生成也答**），把出片整链真跑一遍：提交 → 轮询 → 收产物 → 量结果。
    // 目的：任何改到「出片」那一段的代码，都不用等真机的时段就能自己验。
    var port = int.Parse(args[1]);
    var key = args[2];
    var frame = Path.GetFullPath(args[3]);
    // 多路素材用逗号隔开（例如两段音喂「双人对白」那份工作流的两个音频口）。
    var sourceVideos = SplitSources(args.Length > 4 ? args[4] : "-");
    var sourceAudios = SplitSources(args.Length > 5 ? args[5] : "-");

    // 产物：拿本机留档里那段真 mp4（越小越快）。它是那次实测真出出来的片子，
    // 所以「读容器头量出几秒几帧几轨」那一段也照常被走到，不是拿空字节糊过去。
    var artifact = FindArchivedMp4();
    if (artifact is null) { Console.WriteLine("本机留档里找不到可当产物的 mp4。"); return; }

    var (genDrafts, genObjectInfo) = LocalComfyStub.Load(archiveDir, false);
    LocalComfyStub.Start(port, genDrafts, genObjectInfo, Console.WriteLine, artifact, logRequests: true);

    var demoConfig = new AiProviderConfig
    {
        ComfyUiBaseUrl = $"http://127.0.0.1:{port}",
        // 占位 checkpoint：只为让 IsComfyUiConfigured 成立（走工作流其实不看它，别让它变回内存执行器）。
        ComfyUiCheckpoint = "demo.safetensors",
        ComfyUiClientId = "chainprobe"
    };

    SiteWorkflowChoice? choice = null;
    foreach (var candidate in sites.Where(item => item.IsComfyUi))
    {
        var found = candidate.VideoWorkflows.FirstOrDefault(item => item.Key == key);
        if (found is not null) { choice = new SiteWorkflowChoice(candidate, found); break; }
    }
    if (choice is null) { Console.WriteLine("没找到这份工作流（要先在站点里）：" + key); return; }
    Console.WriteLine($"用它作正文：{choice.Site.DisplayName} / {choice.Workflow.Title}");

    var genProvider = VideoProviderFactory.Create(demoConfig, choice) as ComfyUiVideoProvider;
    if (genProvider is null) { Console.WriteLine("出片这条链没建出来（多半是 ComfyUI 没配好）。"); return; }
    genProvider.Status = message => Console.WriteLine("  [状态] " + message);
    Console.WriteLine($"能收首帧 = {genProvider.TakesFirstFrame}");

    var genWatch = Stopwatch.StartNew();
    var genResult = await genProvider.GenerateAsync(new VideoGenerationRequest
    {
        Prompt = "假服务器演示：这一镜",
        // 逗号分隔可以给多张（首尾帧那一类要前一张、后一张）；用 - 占位表示不给
        // （PowerShell 会把空字符串参数吞掉，所以「不给」不能写成空串）。
        ReferenceImages = frame == "-" || frame.Length == 0
            ? Array.Empty<string>()
            : frame.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        SourceVideos = sourceVideos,
        SourceAudios = sourceAudios,
        WorkflowSiteId = choice.Site.Id,
        WorkflowPayloadFile = choice.Workflow.PayloadFile,
        WorkflowKey = choice.Workflow.Key
    });
    Console.WriteLine($"出片 {genWatch.Elapsed.TotalSeconds:F1}s → {genResult.Status} {genResult.FilePath}");
    if (genResult.Note.Length > 0) Console.WriteLine("  这一镜实际生效的：\n    " + genResult.Note.Replace("\n", "\n    "));
    if (genResult.Status != VideoGenerationStatus.Succeeded) { Console.WriteLine("  错误：" + genResult.Error); return; }
    Console.WriteLine("  文件大小 " + new FileInfo(genResult.FilePath).Length + " 字节");
    Console.WriteLine("  容器头：" + JsonSerializer.Serialize(Describe(genResult.FilePath)));
    return;
}

if (mode == "stubtest")
{
    // 最小直连：把出片链要的那几个请求**逐个**打给假服务器，每步的异常原样打出来。
    // 用来把「假服务器答得对不对」和「产品那条链用得对不对」分开。
    var port = args.Length > 1 ? int.Parse(args[1]) : 8192;
    var (probeDrafts, probeObjectInfo) = LocalComfyStub.Load(archiveDir, false);
    LocalComfyStub.Start(port, probeDrafts, probeObjectInfo, Console.WriteLine, FindArchivedMp4(), logRequests: true);

    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };

    async Task Step(string label, Func<Task<string>> run)
    {
        try { Console.WriteLine($"[{label}] OK {await run()}"); }
        catch (Exception error) { Console.WriteLine($"[{label}] 失败 {error.GetType().FullName}: {error.Message}"); }
    }

    await Step("POST prompt（PostAsJsonAsync，会走分块编码）", async () =>
    {
        using var response = await client.PostAsJsonAsync("prompt", new { prompt = new { }, client_id = "stubtest" });
        return $"HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
    });

    await Step("GET history/<id>", async () =>
    {
        var text = await client.GetStringAsync("history/abc123");
        return text.Length + " 字节：" + text[..Math.Min(120, text.Length)];
    });

    await Step("GET view（那段真 mp4）", async () =>
    {
        var bytes = await client.GetByteArrayAsync("view?filename=demo-out.mp4&subfolder=&type=output");
        return bytes.Length + " 字节";
    });

    await Step("GET object_info", async () => (await client.GetStringAsync("object_info")).Length + " 字节");
    await Step("GET userdata 清单", async () => (await client.GetStringAsync("userdata?dir=workflows&recurse=true&full_info=true")).Length + " 字节");
    return;
}

if (mode == "prompts")
{
    // prompts：把每份工作流「提示词入口认到哪儿了」列出来，供「改输入名名单」前后逐份对照。
    // 认不出的那些还会把它**身上像提示词的输入名**一起列出来 —— 名单该加什么，照这个证据定，不靠猜。
    var site = sites.First(item => item.IsComfyUi);
    var rows = new List<string>();
    var noPrompt = 0;
    var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile) ?? string.Empty;
        ComfyUiWorkflowSlots slots;
        try { slots = ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots); }
        catch (Exception error) { _ = error; rows.Add(workflow.Title + "｜读不了"); continue; }

        var positive = slots.CanTextToImage ? slots.PositiveNodeId + "." + slots.PositiveInput : "✗";
        var negative = slots.NegativeNodeId.Length > 0 ? slots.NegativeNodeId + "." + slots.NegativeInput : "✗";
        rows.Add($"{workflow.Title}｜正向 {positive}｜负面 {negative}");
        if (slots.CanTextToImage) continue;
        noPrompt++;

        // 认不出时，看看它身上有哪些「像提示词」的字符串输入名（只报名字，不报内容）。
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (System.Text.Json.Nodes.JsonNode.Parse(payload) is System.Text.Json.Nodes.JsonObject api)
            foreach (var node in api)
            {
                if (node.Value?["inputs"] is not System.Text.Json.Nodes.JsonObject inputs) continue;
                foreach (var field in inputs)
                {
                    if (field.Value is not System.Text.Json.Nodes.JsonValue value
                        || !value.TryGetValue<string>(out _)) continue;
                    var low = field.Key.ToLowerInvariant();
                    if (low.Contains("提示") || low.Contains("词") || low.Contains("prompt")
                        || low.Contains("text") || low.Contains("caption"))
                    {
                        names.Add(field.Key);
                        candidates[field.Key] = candidates.TryGetValue(field.Key, out var seen) ? seen + 1 : 1;
                    }
                }
            }
        if (names.Count > 0) rows.Add("    · 它身上像提示词的输入名：" + string.Join("、", names));
    }

    Console.WriteLine($"{site.Workflows.Count} 份里认不出正向提示词的：{noPrompt} 份");
    foreach (var pair in candidates.OrderByDescending(pair => pair.Value))
        Console.WriteLine($"  候选名 {pair.Key}：{pair.Value} 份");
    Console.WriteLine();
    foreach (var row in rows) Console.WriteLine(row);
    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "prompts.txt"), rows, System.Text.Encoding.UTF8);
    return;
}

if (mode == "merge")
{
    // merge <工作流 Key>：**只读**地看一眼「往这份工作流的提示词格里写这一镜描述」会写成什么
    //（模板式提示词是按节写的，这一眼就是核那件事）。不发任何提交请求。
    var site = sites.First(item => item.IsComfyUi);
    var workflow = site.VideoWorkflows.First(item => item.Key == args[1]);
    var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile) ?? string.Empty;
    var slots = ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots);
    var bound = ComfyUiWorkflowBinder.Bind(payload, slots, new ComfyUiBindValues
    {
        Prompt = "第三镜：她推开木门走进院子，午后的光从侧面切进来。",
        ImageNames = new[] { "yeeeyee-check-a.png", "yeeeyee-check-b.png" },
        VideoNames = new[] { "yeeeyee-check.mp4" }
    });
    Console.WriteLine($"工作流 {workflow.Title}｜正向 {slots.PositiveNodeId}.{slots.PositiveInput}"
        + $"｜底图 {slots.FileSlotImages.Count} 格｜源视频 {slots.VideoNodeIds.Count}");
    if (slots.PositiveNodeId.Length > 0)
    {
        var text = bound[slots.PositiveNodeId]?["inputs"]?[slots.PositiveInput]?.GetValue<string>() ?? string.Empty;
        Console.WriteLine($"---- 写进去的提示词（{text.Length} 字）----");
        Console.WriteLine(text);
        Console.WriteLine("---- 完 ----");
    }
    foreach (var slot in slots.FileSlotImages.Take(3))
        Console.WriteLine($"  图槽 {slot.NodeId}.{slot.Input} = "
            + bound[slot.NodeId]?["inputs"]?[slot.Input]?.GetValue<string>());
    return;
}

if (mode == "pull-archive")
{
    // pull-archive [地址] [目录]：把一台 ComfyUI 上的**全部原稿**与节点定义留档到本地。
    // 离线那几条（demolocal / demogen / audit-import / verify）都读这份留档——
    // 留档没了、真机又不在，就什么都验不了。所以真机在手时顺手留一份。
    var url = args.Length > 1 ? args[1] : baseUrl;
    var target = args.Length > 2 ? args[2] : archiveDir;
    Directory.CreateDirectory(target);

    // 直接用产品那套拉取：它已经处理了「/api 前缀先试、404 再试不带前缀」这类形状差异。
    var pulled = await ComfyUiLibrary.FetchAsync(url);
    foreach (var pair in pulled.RawDrafts)
        await File.WriteAllTextAsync(Path.Combine(target, Path.GetFileName(pair.Key)), pair.Value);

    var objectInfoPath = Path.Combine(Path.GetTempPath(), "object_info.json");
    await File.WriteAllTextAsync(objectInfoPath, pulled.ObjectInfo!.ToJsonString());
    Console.WriteLine($"留档 {pulled.RawDrafts.Count} 份原稿 → {target}");
    Console.WriteLine($"节点定义 → {objectInfoPath}（{pulled.ObjectInfo.Count} 种节点）");
    return;
}

if (mode == "health")
{
    // health [标题片段]：把某几份工作流的体检结论**原样**打出来——选择器里看到的就是这个。
    // 光看站点文件里存了什么是看不出「用户读起来是什么样」的。
    var needle = args.Length > 1 ? args[1] : string.Empty;
    var shown = 0;
    foreach (var site in sites.Where(item => item.IsComfyUi))
        foreach (var workflow in site.Workflows.Where(item => ComfyUiWorkflowHealth.ShortMark(item).Length > 0))
        {
            if (needle.Length > 0 && !workflow.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"== [{site.Id}] {workflow.Title}（丢 {workflow.DroppedInputs} / 断 {workflow.BrokenInputs} / 判不了 {workflow.UncertainInputs}）==");
            Console.WriteLine(ComfyUiWorkflowHealth.Describe(workflow));
            Console.WriteLine();
            if (++shown >= 3) return;
        }
    if (shown == 0) Console.WriteLine("没有匹配的（或它们都没问题）");
    return;
}

if (mode == "needed")
{
    // needed [标题片段]：某几处毛病到底落在**还会被执行的**节点上，还是落在没人读的死节点上？
    // 判据与产品共用一份：`ComfyUiImportAuditor.ExecutedNodes`（从节点定义里标了 `output_node`
    // 的产物出口反向走；拿「没有下游」当出口会把悬空死节点也算活，判据就永远报「全是活的」）。
    var needle = args.Length > 1 ? args[1] : string.Empty;
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();

    var draftsByTitle = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(archiveDir, "*.json"))
        draftsByTitle[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

    var live = 0;
    var dead = 0;
    var liveSamples = new List<string>();
    var deadSamples = new List<string>();

    // 只看第一台 ComfyUI 站点：每个站点文件都是同一批工作流，全过一遍会把数翻倍。
    var site = sites.First(item => item.IsComfyUi);
    var raws = new Dictionary<string, string>(StringComparer.Ordinal);
    var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (string.IsNullOrWhiteSpace(payload)) continue;
        payloads[workflow.Key] = payload!;
        if (draftsByTitle.TryGetValue(workflow.Title, out var draft)) raws[workflow.Key] = draft;
    }

    var report = ComfyUiImportAuditor.Inspect(raws, payloads, objectInfoNode);
    var byWorkflow = report.Findings
        .Where(item => item.Kind == ComfyUiFindingKind.BrokenInSource)
        .GroupBy(item => item.WorkflowKey, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    foreach (var workflow in site.Workflows)
    {
        if (!payloads.TryGetValue(workflow.Key, out var payloadText)) continue;
        if (needle.Length > 0 && !workflow.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
        if (JsonNode.Parse(payloadText) is not JsonObject payload) continue;

        var living = ComfyUiImportAuditor.ExecutedNodes(payload, objectInfoNode);
        var total = payload.Count;
        byWorkflow.TryGetValue(workflow.Key, out var findings);

        // 带 needle 时逐份把账摆出来：能看出这个判据有没有区分度（干净的样本也该有死节点）。
        if (needle.Length > 0)
            Console.WriteLine($"{workflow.Title}：节点 {total} 个，会被执行 {living.Count}、没人用 {total - living.Count}；"
                + $"自己断线 {findings?.Count ?? 0} 处");

        foreach (var finding in findings ?? new List<ComfyUiImportFinding>())
        {
            // 活/死用的是**产品打在发现上的那个事实**（`OnExecutionChain`），不是这里另算一遍。
            if (finding.OnExecutionChain)
            {
                live++;
                if (liveSamples.Count < 8) liveSamples.Add(finding.Label);
            }
            else
            {
                dead++;
                if (deadSamples.Count < 8) deadSamples.Add(finding.Label);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"「原稿自己断线」共 {live + dead} 处：在**还会被执行的**节点上 {live} 处，在**没人用的**节点上 {dead} 处。");
    Console.WriteLine();
    Console.WriteLine("=== 落在活节点上的（这些才是真会缺东西的）===");
    foreach (var line in liveSamples) Console.WriteLine("  " + line);
    Console.WriteLine();
    Console.WriteLine("=== 落在死节点上的（没人用它，缺不缺都无所谓）===");
    foreach (var line in deadSamples) Console.WriteLine("  " + line);
    return;
}

if (mode == "imageloaders")
{
    // 底图入口是**按类名**认的（类名含 LoadImage 才算）。这里把「有 image 输入、但类名不含 LoadImage」
    // 的类型挑出来，并数一数有多少份工作流在用——那正是会被整类漏掉的底图入口。
    // 起因：H22 明明是「首尾帧图生视频」，我们却报「能收首帧 = False」，因为它用的是 MultiImageLoader。
    var objectInfoNode = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json")))!.AsObject();

    var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    var acceptsImage = new List<string>();
    foreach (var pair in objectInfoNode)
    {
        if (pair.Value is not JsonObject definition) continue;
        var found = false;
        foreach (var group in new[] { "required", "optional" })
        {
            if (definition["input"]?[group] is not JsonObject spec) continue;
            foreach (var field in spec)
            {
                if (!field.Key.StartsWith("image", StringComparison.Ordinal)) continue;
                if (field.Value is not JsonArray slot || slot.Count == 0) continue;

                // 判据与 picker 一致：**候选清单本身是一堆图片文件名**，才叫「从服务器文件里挑一张」的入口。
                // 只看「有没有 image 输入」会把一堆接收连线的处理器也算进来（实测过，那张单子长得没法看）。
                JsonArray? options = null;
                if (slot[0] is JsonValue typeName && typeName.TryGetValue<string>(out var name) && name == "COMBO")
                    options = slot.Count > 1 ? slot[1]?["options"] as JsonArray : null;
                else if (slot[0] is JsonArray literal) options = literal;
                if (options is null || options.Count == 0) continue;

                var sample = options.Take(50).Select(item => item?.ToString() ?? string.Empty).ToList();
                if (sample.Count(text => imageExtensions.Any(ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    < sample.Count * 3 / 4) continue;

                found = true;
                break;
            }
            if (found) break;
        }
        if (found) acceptsImage.Add(pair.Key);
    }

    var missed = acceptsImage
        .Where(name => !name.Contains("LoadImage", StringComparison.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    var site = sites.First(item => item.IsComfyUi);
    var usage = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var workflow in site.Workflows)
    {
        var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
        if (string.IsNullOrWhiteSpace(payload)) continue;
        foreach (var name in missed)
            if (payload!.Contains("\"" + name + "\"", StringComparison.Ordinal))
                usage[name] = usage.TryGetValue(name, out var seen) ? seen + 1 : 1;
    }

    Console.WriteLine($"有 image 输入的节点类型 {acceptsImage.Count} 种；其中类名不含 LoadImage 的 {missed.Count} 种：");
    foreach (var name in missed)
        Console.WriteLine($"    {name}（{usage.GetValueOrDefault(name)} 份工作流在用）");
    return;
}

if (mode == "missing")
{
    // 「正文里引用的文件在服务器上现在还有没有」已经做进**产品体检**（ComfyUiFindingKind.MissingOnServer），
    // 所以这里不再自己判一遍——量一遍的活儿交给 audit-import（它直接调产品那套），
    // 免得探针与产品两套判据各说各话（这种「会说假话的工具」先前吃过一次亏）。
    Console.WriteLine("这一类已并入产品体检：请用 audit-import（它调的就是产品那套判据）。");
    return;
}

Console.WriteLine("用法：audit | image | video <工作流Key> <首帧> [提示词] | concat <段…>");

static object Describe(string path) => Mp4Concatenator.Probe(path).Describe();

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
}

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } custom ? custom : fallback;

/// <summary>
/// 站点**不写字面地址**：待修清单是要进仓库的，而那台机器是别人租的——连服务商域名都不留。
/// 只给一个由地址算出来的短标签：同一个站点每次都是同一个标签（够区分多台机器），又带不走任何信息。
/// </summary>
static string SiteLabel(string display)
{
    var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(display));
    return "comfy-" + Convert.ToHexString(hash)[..4].ToLowerInvariant();
}

/// <summary>逗号分隔的多路素材；「不给」用 - 占位（PowerShell 会把空字符串参数吞掉）。</summary>
static string[] SplitSources(string value) =>
    value == "-" || value.Length == 0
        ? Array.Empty<string>()
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

/// <summary>从程序位置往上找仓库根（认 YEEYEEYEE.slnx）；找不到就用当前目录。</summary>
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "YEEYEEYEE.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

/// <summary>够用的默认工程：Release 产物下 Projects 里最近改过、且带 skills 目录的那一个。</summary>
static string NewestProjectUnder(string root)
{
    if (!Directory.Exists(root)) return root;
    var newest = new DirectoryInfo(root).GetDirectories()
        .Where(item => Directory.Exists(Path.Combine(item.FullName, "skills")))
        .OrderByDescending(item => item.LastWriteTimeUtc)
        .FirstOrDefault();
    return newest?.FullName ?? root;
}

/// <summary>出片链假产物用的那段 mp4：可用环境变量指定，否则在本机留档里挑最小的（省时间）。</summary>
static string? FindArchivedMp4()
{
    if (Environment.GetEnvironmentVariable("CHAINPROBE_ARTIFACT") is { Length: > 0 } custom && File.Exists(custom))
        return custom;
    foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "chainprobe-*"))
    {
        var assets = Path.Combine(dir, "assets");
        if (!Directory.Exists(assets)) continue;
        var found = Directory.GetFiles(assets, "*.mp4").OrderBy(path => new FileInfo(path).Length).FirstOrDefault();
        if (found is not null) return found;
    }
    return null;
}

/// <summary>
/// 一个只在 127.0.0.1 上听着的**假 ComfyUI**：够产品那两条路（导入 / 出片）真跑一遍就行。
/// 用 TcpListener 而不是 HttpListener——后者在 Windows 上要 URL ACL（非管理员常常起不来）。
///
/// 两套接口都答：
///   · 导入链（<c>/api/...</c> 与根路径都认）：workflows 清单、正文、object_info。
///   · 出片链（根路径，与真实 ComfyUI 一致）：<c>prompt</c> 收下就回 prompt_id →
///     <c>history/&lt;id&gt;</c> 报「已完成」并指出产物在哪个节点的哪个桶 → <c>view</c> 把字节吐出来。
///     产物不是编的：拿本机留档的那段**真 mp4** 当「生成结果」，所以出片下游那套
///     「读容器头量出几秒几帧几轨」也照常被走到。
///
/// <c>/ws</c> 不接：客户端连不上会退回去轮询（<c>ListenForProgressAsync</c> 里对 WebSocketException
/// 是重试几次就放弃），不影响结果，只是少一个进度百分比。
/// </summary>
static class LocalComfyStub
{
    /// <summary>那份「故意留缺口」的原稿：中间夹着我们不认识的纯前端节点，下游 samples 会整项消失。</summary>
    private const string CraftedDraft = """
    {
      "last_node_id": 4, "last_link_id": 12,
      "nodes": [
        {"id": 1, "type": "CheckpointLoaderSimple", "mode": 0, "inputs": [],
         "outputs": [{"name":"MODEL","type":"MODEL","links":[]},{"name":"CLIP","type":"CLIP","links":[]},
                     {"name":"VAE","type":"VAE","links":[12]}], "widgets_values": ["a.safetensors"]},
        {"id": 4, "type": "EmptyLatentImage", "mode": 0, "inputs": [],
         "outputs": [{"name":"LATENT","type":"LATENT","links":[10]}], "widgets_values": [512, 512]},
        {"id": 2, "type": "FancyBridge", "mode": 0,
         "inputs": [{"name":"","type":"*","link":10}],
         "outputs": [{"name":"LATENT","type":"LATENT","links":[11]}]},
        {"id": 3, "type": "VAEDecode", "mode": 0,
         "inputs": [{"name":"samples","type":"LATENT","link":11},{"name":"vae","type":"VAE","link":12}],
         "outputs": [{"name":"IMAGE","type":"IMAGE","links":[]}]}
      ],
      "links": [[10, 4, 0, 2, 0, "LATENT"], [11, 2, 0, 3, 0, "LATENT"], [12, 1, 2, 3, 1, "VAE"]]
    }
    """;

    /// <summary>「生成」出来的产物文件名：扩展名会一路带到本机落盘的资产上，所以必须是 .mp4。</summary>
    private const string OutputFileName = "demo-out.mp4";

    /// <summary>把每个请求打出来（排「到底卡在哪一跳」时用；正常跑库导入那几百份时会很吵，默认关）。</summary>
    private static Action<string>? logger;

    public static (Dictionary<string, string> Drafts, string ObjectInfo) Load(string archiveDir, bool crafted)
    {
        var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(archiveDir, "*.json"))
            drafts[Path.GetFileName(file)] = File.ReadAllText(file);
        if (crafted) drafts["T-本地演示/一份带缺口的工作流.json"] = CraftedDraft;
        var objectInfo = File.ReadAllText(Path.Combine(Path.GetTempPath(), "object_info.json"));
        return (drafts, objectInfo);
    }

    public static void Start(
        int port,
        Dictionary<string, string> drafts,
        string objectInfo,
        Action<string> log,
        string? artifactPath = null,
        bool logRequests = false)
    {
        logger = logRequests ? log : null;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        log($"本地假 ComfyUI 已监听 http://127.0.0.1:{port}"
            + (artifactPath is null ? string.Empty : "（连生成也答：产物用 " + Path.GetFileName(artifactPath) + "）"));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => Respond(client, drafts, objectInfo, artifactPath));
            }
        });
    }

    private static void Respond(
        System.Net.Sockets.TcpClient client,
        Dictionary<string, string> drafts,
        string objectInfo,
        string? artifactPath)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var head = ReadHead(stream);
                if (head.Length == 0) return;
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var target = parts.Length > 1 ? parts[1] : "/";
                var question = target.IndexOf('?');
                var path = Uri.UnescapeDataString(question >= 0 ? target[..question] : target);

                // 提交那边带正文，**必须读掉**，不然客户端会看到自己这头被重置。
                var length = 0;
                var chunked = false;
                var expectContinue = false;
                foreach (var line in lines)
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, out var parsed)) length = parsed;
                    // JsonContent 算不出长度，HttpClient 就改用分块编码——这时没有 Content-Length。
                    if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                        && value.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
                    // HttpClient 对大正文会先发 Expect: 100-continue 等我们点头。
                    // 不回这一句，它就一直在等，最后报「An error occurred while sending the request」。
                    if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase)
                        && value.Contains("100-continue", StringComparison.OrdinalIgnoreCase)) expectContinue = true;
                }
                if (expectContinue)
                {
                    var go = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(go, 0, go.Length);
                    stream.Flush();
                }
                var body = new MemoryStream();
                if (chunked) length = ReadChunked(stream, body);
                else if (length > 0)
                {
                    var buffer = new byte[length];
                    var got = 0;
                    while (got < length)
                    {
                        var read = stream.Read(buffer, got, length - got);
                        if (read <= 0) break;
                        got += read;
                    }
                    body.Write(buffer, 0, got);
                }

                // 提交上来的图里「哪个节点拿到了哪份素材」——这是「真的写进去了」唯一的硬证据，
                // 比在应用里看那句「源音频已写入：节点 N」更实。
                var bodyText = body.Length > 0 ? System.Text.Encoding.UTF8.GetString(body.ToArray()) : string.Empty;
                if (bodyText.Length > 0 && path.EndsWith("/prompt", StringComparison.Ordinal))
                    logger?.Invoke(DescribePromptMedia(bodyText));

                var (status, contentType, payload) = Route(method, path, drafts, objectInfo, artifactPath, bodyText);
                logger?.Invoke($"{method} {path} {length}B -> {status}");
                var headBytes = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                    + $"Content-Type: {contentType}\r\n"
                    + $"Content-Length: {payload.Length}\r\n"
                    + "Connection: close\r\n\r\n");
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                logger?.Invoke($"   已回 {payload.Length}B");

                // 关之前把接收缓冲读干净再关：带未读字节关 socket 会发 RST（见 ReadChunked 里那段注释）。
                try
                {
                    client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                    client.Client.ReceiveTimeout = 500;
                    var sink = new byte[4096];
                    while (client.Client.Receive(sink) > 0) { }
                }
                catch (Exception drain) { _ = drain; }
            }
            catch (Exception error)
            {
                // 别静默：服务端这边的异常正是排「是谁先断的」唯一的线索。
                logger?.Invoke("   !! " + error.GetType().Name + " " + error.Message);
            }
        }
    }

    /// <summary>读到请求头结束（那对 CRLFCRLF）为止；正文一个字节都不碰，留在流里给后面按长度读。</summary>
    private static string ReadHead(Stream stream)
    {
        var buffer = new List<byte>(1024);
        var window = new byte[4];
        var matched = 0;
        while (true)
        {
            var read = stream.ReadByte();
            if (read < 0) break;
            buffer.Add((byte)read);
            window[matched] = (byte)read;
            matched++;
            if (matched == 4)
            {
                if (window[0] == 13 && window[1] == 10 && window[2] == 13 && window[3] == 10) break;
                window[0] = window[1];
                window[1] = window[2];
                window[2] = window[3];
                matched = 3;
            }
        }
        return System.Text.Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>把分块正文读干净并攒进 <paramref name="sink"/>，返回读到的字节数。</summary>
    private static int ReadChunked(Stream stream, MemoryStream sink)
    {
        var total = 0;
        while (true)
        {
            var sizeLine = ReadLine(stream);
            if (sizeLine.Length == 0) { logger?.Invoke("   chunk <空行：正文到此>"); break; }
            if (!int.TryParse(sizeLine.Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)
                || size <= 0)
            {
                logger?.Invoke($"   chunk <结束标记 {sizeLine.Trim()}>");
                break;
            }
            var buffer = new byte[size];
            var got = 0;
            while (got < size)
            {
                var read = stream.Read(buffer, got, size - got);
                if (read <= 0) break;
                got += read;
            }
            total += got;
            sink.Write(buffer, 0, got);
            logger?.Invoke($"   chunk {sizeText(sizeLine)} -> {got}B（累计 {total}）");
            ReadLine(stream);   // 块尾那个 CRLF
        }

        // 结束标记（0\r\n\r\n）后面还剩一个 CRLF，**必须读掉**：带着未读字节关 socket，
        // Windows 会回 RST，客户端那边就变成「Error while copying content to a stream」——实测就卡在这。
        ReadLine(stream);
        return total;
    }

    private static string sizeText(string line) => line.Split(';')[0].Trim();

    private static string ReadLine(Stream stream)
    {
        var buffer = new List<byte>(64);
        while (true)
        {
            var read = stream.ReadByte();
            if (read < 0) break;
            if (read == 10) break;
            if (read != 13) buffer.Add((byte)read);
        }
        return System.Text.Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>从 multipart 正文里取这次上传的文件名——ComfyUI 上传接口的应答就是它。</summary>
    private static string? ExtractUploadName(string body)
    {
        // 注意：.NET 的 MultipartFormDataContent 写的是**不带引号**的 `filename=xxx`
        //（值里有特殊字符时才会加引号），两种都要认。
        var at = body.IndexOf("filename=", StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + "filename=".Length;
        var quoted = start < body.Length && body[start] == '"';
        if (quoted) start++;
        var end = quoted
            ? body.IndexOf('"', start)
            : body.IndexOfAny(new[] { '\r', '\n', ';' }, start);
        if (end < 0) end = body.Length;
        var name = body[start..end].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// 把提交上来的图里「哪个节点拿到了哪份素材」点出来。
    /// 这是「素材真的写进去了」唯一的硬证据——应用里那句「源音频已写入：节点 N」说的是我们**打算**写哪儿。
    /// </summary>
    private static string DescribePromptMedia(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["prompt"] is not JsonObject graph) return "   提交的图里没有 prompt 段";
            var lines = new List<string>();
            foreach (var pair in graph)
            {
                if (pair.Value?["inputs"] is not JsonObject inputs) continue;
                var type = pair.Value["class_type"]?.ToString() ?? "?";
                foreach (var slot in new[] { "image", "video", "audio", "audio_file" })
                    if (inputs[slot]?.ToString() is { Length: > 0 } value)
                        lines.Add($"节点 {pair.Key}[{type}].{slot}={value}");
            }
            return lines.Count == 0
                ? "   源素材：（一个都没写进去）"
                : string.Join(Environment.NewLine, lines.Select(item => "   源素材：" + item));
        }
        catch (Exception error) { return "   提交的图读不了：" + error.Message; }
    }

    private static (int Status, string ContentType, byte[] Body) Json(int status, string body) =>
        (status, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(body));

    private static (int Status, string ContentType, byte[] Body) Route(
        string method,
        string path,
        Dictionary<string, string> drafts,
        string objectInfo,
        string? artifactPath,
        string bodyText)
    {
        // ---------- 出片链（真实 ComfyUI 就在根路径上，所以这里也只看后缀）----------
        if (method == "POST" && path.EndsWith("/prompt", StringComparison.Ordinal))
            return Json(200, "{\"prompt_id\":\"" + Guid.NewGuid().ToString("N") + "\",\"number\":1,\"node_errors\":{}}");

        if (method == "POST" && path.EndsWith("/upload/image", StringComparison.Ordinal))
        {
            // 回**真正的文件名**（真实 ComfyUI 就是这么答的）。回一个固定名字会一路错下去——
            // 实测踩过：图片和音频共用同一个固定名，于是音频口里躺着个 png 的名字。
            var name = ExtractUploadName(bodyText) ?? "demo-upload.bin";
            return Json(200, new JsonObject { ["name"] = name, ["subfolder"] = "", ["type"] = "input" }.ToJsonString());
        }

        if (path.EndsWith("/interrupt", StringComparison.Ordinal)) return Json(200, "{}");

        if (path.EndsWith("/queue", StringComparison.Ordinal))
            return Json(200, "{\"queue_running\":[],\"queue_pending\":[]}");

        // history/<id>：**必须带那个 id 做键**，否则客户端以为还没跑完，会一路轮询。
        var historyAt = path.IndexOf("/history/", StringComparison.Ordinal);
        if (historyAt >= 0 || path.EndsWith("/history", StringComparison.Ordinal))
        {
            var id = historyAt >= 0 ? path[(historyAt + "/history/".Length)..] : "demo";
            var record = new JsonObject
            {
                [id] = new JsonObject
                {
                    ["status"] = new JsonObject
                    {
                        ["status_str"] = "success",
                        ["completed"] = true,
                        ["messages"] = new JsonArray()
                    },
                    ["outputs"] = new JsonObject
                    {
                        ["9"] = new JsonObject
                        {
                            ["videos"] = new JsonArray(new JsonObject
                            {
                                ["filename"] = OutputFileName,
                                ["subfolder"] = string.Empty,
                                ["type"] = "output"
                            })
                        }
                    }
                }
            };
            return Json(200, record.ToJsonString());
        }

        if (path.EndsWith("/view", StringComparison.Ordinal))
        {
            if (artifactPath is not null && File.Exists(artifactPath))
                return (200, "video/mp4", File.ReadAllBytes(artifactPath));
            return Json(404, "{\"error\":\"no artifact\"}");
        }

        // ---------- 导入链 ----------
        // /object_info/<名字> 先判——它才带节点名，别被下面 EndsWith("/object_info") 吃掉。
        var nodeAt = path.IndexOf("/object_info/", StringComparison.Ordinal);
        if (nodeAt >= 0)
        {
            var name = path[(nodeAt + "/object_info/".Length)..];
            if (JsonNode.Parse(objectInfo) is JsonObject all && all[name] is { } one)
                return Json(200, new JsonObject { [name] = one.DeepClone() }.ToJsonString());
            return Json(404, "{\"error\":\"no such node\"}");
        }
        if (path.EndsWith("/object_info", StringComparison.Ordinal)) return Json(200, objectInfo);
        if (path.EndsWith("/system_stats", StringComparison.Ordinal))
            return Json(200, "{\"system\":{\"comfyui_version\":\"0.3.99-local\",\"python_version\":\"local\"},\"devices\":[]}");

        var dataAt = path.IndexOf("/userdata/", StringComparison.Ordinal);
        if (dataAt >= 0)
        {
            var rest = path[(dataAt + "/userdata/".Length)..];
            if (rest.StartsWith("workflows/", StringComparison.Ordinal)) rest = rest["workflows/".Length..];
            return drafts.TryGetValue(rest, out var text) ? Json(200, text) : Json(404, "{\"error\":\"missing\"}");
        }
        if (path.EndsWith("/userdata", StringComparison.Ordinal))
        {
            var array = new JsonArray();
            foreach (var key in drafts.Keys.OrderBy(item => item, StringComparer.Ordinal))
                array.Add(new JsonObject { ["path"] = key });
            return Json(200, array.ToJsonString());
        }
        return Json(404, "{\"error\":\"not found\"}");
    }
}
