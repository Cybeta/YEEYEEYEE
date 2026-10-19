using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

/// <summary>
/// Web 判档的离线回归：本地坏图筛查、模型评分映射、三种失败（无模型 / 超时 / 非法返回）、
/// 结果与「任务 + 产物下标」的绑定、重复保存的幂等，以及判档前的归属与类型校验。
///
/// 模型本身用桩替代；这里钉的是规则与落位，不是模型质量。
/// </summary>
internal static class ImageQualityRegression
{
    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Image quality: " + label); }

        var root = Path.Combine(Path.GetTempPath(), "dream-image-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string FileFor(string name)
            {
                var path = Path.Combine(root, name);
                File.WriteAllBytes(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                return path;
            }

            var jobA = Guid.NewGuid();
            var jobB = Guid.NewGuid();
            var jobC = Guid.NewGuid();
            var technical = new[]
            {
                new WebQualityCandidate(jobA, 0, FileFor("a.png"), 1024, "镜头一：林夏在窗边", "不要多余手指"),
                new WebQualityCandidate(jobB, 0, FileFor("b.png"), 1024, "镜头一：林夏在窗边", "不要多余手指"),
                new WebQualityCandidate(jobC, 0, FileFor("c.png"), 1024, "镜头一：林夏在窗边", "不要多余手指")
            };
            static ImageFacts Probe(string path, int expected) => Path.GetFileName(path) switch
            {
                "a.png" => new ImageFacts(true, 1024, 1024, 576, 40, expected),
                "b.png" => new ImageFacts(true, 256, 256, 576, 40, expected),
                "c.png" => new ImageFacts(true, 1024, 1024, 576, 1, expected),
                _ => new ImageFacts(false, 0, 0, 0, 0, 0)
            };

            // 1. 本地先筛：尺寸明显偏小 / 纯色都是白档（本地判定、无分数），只有正常那张送模型。
            var first = WebQualityRunner.RunAsync(
                technical, Probe,
                () => new StubCompleter("""{"scores":[{"index":1,"score":8,"hits":[],"note":"主体清楚，手指正常"}]}"""),
                "stub-model", "没有可用的读图模型").GetAwaiter().GetResult();
            var firstByJob = first.ToDictionary(record => record.JobId);
            Assert(firstByJob[jobA].Status == WebImageQuality.StatusGraded
                && firstByJob[jobA].Source == WebImageQuality.SourceModel
                && firstByJob[jobA].Tier == "gold"
                && firstByJob[jobA].Score == 8, "模型分数要落到正确的那一格并换成金档");
            Assert(firstByJob[jobA].RuleVersion == WebImageQuality.RuleVersion, "结果要带规则版本");
            Assert(firstByJob[jobB].Source == WebImageQuality.SourceTechnical
                && firstByJob[jobB].Tier == "white"
                && firstByJob[jobB].Score is null
                && firstByJob[jobB].Reason.Contains("小很多", StringComparison.Ordinal), "尺寸偏小是本地白档且没有分数");
            Assert(firstByJob[jobC].Source == WebImageQuality.SourceTechnical
                && firstByJob[jobC].Tier == "white"
                && firstByJob[jobC].Reason.Contains("只有一个颜色", StringComparison.Ordinal), "纯色是本地白档");
            Assert(first.Select(record => record.JobId).SequenceEqual(new[] { jobA, jobB, jobC }), "结果顺序与请求一致");

            // 2. 模型回复顺序被打乱：仍按 index 映射回正确的任务与下标，不是按回复顺序。
            var d = Guid.NewGuid();
            var e = Guid.NewGuid();
            var f = Guid.NewGuid();
            var mapped = WebQualityRunner.RunAsync(
                new[]
                {
                    new WebQualityCandidate(d, 0, FileFor("d.png"), 0, "同一批", "水印"),
                    new WebQualityCandidate(e, 0, FileFor("e.png"), 0, "同一批", "水印"),
                    new WebQualityCandidate(f, 0, FileFor("f.png"), 0, "同一批", "水印")
                },
                (_, _) => new ImageFacts(true, 1024, 1024, 576, 40, 0),
                () => new StubCompleter("""
                    {"scores":[
                      {"index":3,"score":9,"hits":["水印"],"note":"右上角有水印"},
                      {"index":1,"score":4,"hits":[],"note":"主体偏小"},
                      {"index":2,"score":7,"hits":[],"note":"整体达标"}
                    ]}
                    """),
                "stub-model", "没有可用的读图模型").GetAwaiter().GetResult();
            var mappedByJob = mapped.ToDictionary(record => record.JobId);
            Assert(mappedByJob[d].Tier == "blue" && mappedByJob[d].Score == 4, "第 1 张应是 4 分蓝档");
            Assert(mappedByJob[e].Tier == "gold" && mappedByJob[e].Score == 7, "第 2 张应是 7 分金档");
            Assert(mappedByJob[f].Tier == "red" && mappedByJob[f].Score == 9
                && mappedByJob[f].NegativeHits.SequenceEqual(new[] { "水印" }), "第 3 张应是 9 分红档并带裂纹命中");

            // 3. 没有可读图模型：坏图仍按本地判，其余明确 failed + MODEL_UNAVAILABLE，绝不冒充白档。
            var noModel = WebQualityRunner.RunAsync(
                new[]
                {
                    new WebQualityCandidate(jobA, 0, FileFor("a.png"), 1024, "同一批", ""),
                    new WebQualityCandidate(jobB, 0, FileFor("b.png"), 1024, "同一批", "")
                },
                Probe, () => null, "stub-model", "没有可用的读图模型").GetAwaiter().GetResult();
            var noModelByJob = noModel.ToDictionary(record => record.JobId);
            Assert(noModelByJob[jobB].Status == WebImageQuality.StatusGraded, "坏图不依赖模型也能判");
            Assert(noModelByJob[jobA].Status == WebImageQuality.StatusFailed
                && noModelByJob[jobA].ErrorCode == WebImageQuality.ErrorModelUnavailable
                && noModelByJob[jobA].Tier is null
                && noModelByJob[jobA].Reason.Contains("没有可用的读图模型", StringComparison.Ordinal), "无模型要如实失败，不是白档");

            // 4. 超时 / 非法返回：各自一种失败，不抛到上层，也不改任务状态。
            var timeout = WebQualityRunner.RunAsync(
                new[] { new WebQualityCandidate(jobA, 0, FileFor("a.png"), 0, "同一批", "") },
                Probe, () => new StubCompleter("", new TaskCanceledException()),
                "stub-model", "没有可用的读图模型").GetAwaiter().GetResult();
            Assert(timeout[0].Status == WebImageQuality.StatusFailed
                && timeout[0].ErrorCode == WebImageQuality.ErrorModelTimeout, "超时要落成 TIMEOUT");

            var invalid = WebQualityRunner.RunAsync(
                new[] { new WebQualityCandidate(jobA, 0, FileFor("a.png"), 0, "同一批", "") },
                Probe, () => new StubCompleter("这不是 JSON"),
                "stub-model", "没有可用的读图模型").GetAwaiter().GetResult();
            Assert(invalid[0].Status == WebImageQuality.StatusFailed
                && invalid[0].ErrorCode == WebImageQuality.ErrorModelReply, "非法返回要落成 REPLY_INVALID");

            // 5. 存储幂等：同一任务同一产物下标重复保存只留一条，后写的覆盖先写的。
            var database = Path.Combine(root, "quality.db");
            var store = new WebJobQualityStore(database);
            store.Save(new[]
            {
                new WebQualityRecord { JobId = jobA, OutputIndex = 0, Status = WebImageQuality.StatusGraded, Source = WebImageQuality.SourceTechnical, Tier = "white", Reason = "旧" }
            });
            store.Save(new[]
            {
                new WebQualityRecord { JobId = jobA, OutputIndex = 0, Status = WebImageQuality.StatusGraded, Source = WebImageQuality.SourceModel, Tier = "gold", Score = 8, Reason = "新" }
            });
            var stored = store.Get(jobA);
            Assert(stored.Count == 1 && stored[0].Tier == "gold" && stored[0].Score == 8, "重复保存要覆盖同一条，不是累加");

            // 重复请求的付费闸门：判过的永不重判；失败/跳过只有明确重试才放行。
            Assert(WebImageQuality.AlreadyHandled(
                new[] { new WebQualityRecord { OutputIndex = 0, Status = WebImageQuality.StatusGraded } }, retry: false),
                "已判过档的重复请求不应再调用模型");
            Assert(WebImageQuality.AlreadyHandled(
                new[] { new WebQualityRecord { OutputIndex = 0, Status = WebImageQuality.StatusFailed } }, retry: false),
                "失败的重复请求默认不再扣费");
            Assert(!WebImageQuality.AlreadyHandled(
                new[] { new WebQualityRecord { OutputIndex = 0, Status = WebImageQuality.StatusFailed } }, retry: true),
                "用户明确重试时才允许再判失败的那一张");
            Assert(WebImageQuality.AlreadyHandled(
                new[] { new WebQualityRecord { OutputIndex = 0, Status = WebImageQuality.StatusSkipped } }, retry: false),
                "跳过的重复请求默认不再扣费");
            Assert(!WebImageQuality.AlreadyHandled(Array.Empty<WebQualityRecord>(), retry: false),
                "没有结果时才发起判档");

            // 6. 判档前的资格校验：别人的任务按不存在；非候选 / 未成功 / 非出图 / 无产物各自拒绝。
            var owner = Guid.NewGuid();
            Job Restored(Guid userId, JobState state, Capability capability, int outputs) => Job.Restore(new ExecutionResult
            {
                JobId = Guid.NewGuid(), UserId = userId, InvocationId = Guid.NewGuid(), IdempotencyKey = "quality",
                State = state, Capability = capability,
                Outputs = Enumerable.Range(0, outputs).Select(index => new AssetRef { Role = "image", Ref = $"asset://{index}.png" }).ToArray()
            });
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToImage, 1), owner, true, true) is null,
                "自己的成功候选图应通过");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToImage, 1), Guid.NewGuid(), true, true)?.Code == "JOB_NOT_FOUND",
                "别人的任务按不存在");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToImage, 1), owner, false, true)?.Code == "JOB_NOT_FOUND",
                "不在当前画布的任务按不存在");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToImage, 1), owner, true, false)?.Code == "NOT_CANDIDATE_JOB",
                "非候选任务不能判档");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Failed, Capability.TextToImage, 1), owner, true, true)?.Code == "JOB_NOT_READY",
                "未成功的任务不能判档");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToVideo, 1), owner, true, true)?.Code == "NOT_IMAGE_JOB",
                "非出图任务不能判档");
            Assert(WebImageQualityService.ValidateJob(Restored(owner, JobState.Succeeded, Capability.TextToImage, 0), owner, true, true)?.Code == "JOB_NOT_READY",
                "没有产物的任务不能判档");

            // 7. 真实 HTTP + 真实 PNG 解码：用本地 OpenAI 兼容桩走完整请求，
            //    确认图片以 data URL 送出去、回复按 index 映射、结果能落库。
            //    **这不是真实视觉模型的评分**，只是把链路与报文跑通；真实模型验收见进度文档。
            var screenshot = FindScreenshot();
            using var vision = new MockVision();
            vision.Start();
            var livePath = FileFor("live.png");
            File.Copy(screenshot, livePath, true);
            var liveConfig = new AiProviderConfig
            {
                Endpoint = new Uri(vision.BaseAddress, "v1/chat/completions").ToString(),
                UseFullUrl = true,
                Model = "mock-vision",
                SupportsImageInput = true,
                ApiKey = "test-key"
            };
            using var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var live = WebQualityRunner.RunAsync(
                new[] { new WebQualityCandidate(Guid.NewGuid(), 0, livePath, 0, "真实图片", "水印") },
                WebImageProbe.Measure,
                () => new OpenAiCompatibleProvider(liveConfig, liveHttp),
                "mock-vision", "没有可用的读图模型").GetAwaiter().GetResult();
            Assert(live[0].Status == WebImageQuality.StatusGraded
                && live[0].Tier == "red"
                && live[0].Score == 9, "真实 HTTP 判档要读回 9 分红档");
            Assert(vision.LastBody.Contains("data:image/png;base64,", StringComparison.Ordinal),
                "请求里要把图片作为 data URL 发出去");

            // 8. 能力判定：没开图片输入 / 没配模型都要说清，不能到请求时才炸。
            var unavailable = WebImageQuality.Availability(new AiProviderConfig { Endpoint = "https://example.invalid/v1", Model = "text-only" });
            Assert(!unavailable.Ok && unavailable.Message.Contains("支持图片输入", StringComparison.Ordinal), "未开图片输入要明确报配置问题");
            var ready = WebImageQuality.Availability(new AiProviderConfig { Endpoint = "https://example.invalid/v1", Model = "vision", SupportsImageInput = true });
            Assert(ready.Ok, "配置齐全时应判定为可判");
            var missing = WebImageQuality.Availability(new AiProviderConfig());
            Assert(!missing.Ok && missing.Message.Contains("没有可用的读图模型", StringComparison.Ordinal), "未配置模型要明确报配置问题");

            Console.WriteLine("Image quality regression passed: technical screening, tier mapping, out-of-order reply mapping, "
                + "model-unavailable/timeout/invalid reply honesty, idempotent storage, ownership/type validation, "
                + "model availability, and a real HTTP round-trip with a decoded PNG data URL");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubCompleter(string reply, Exception? failure = null) : IAiImageJsonCompleter
    {
        public Task<string> CompleteJsonWithImagesAsync(
            string systemPrompt, string userPrompt, IReadOnlyList<string> images, CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(reply) : Task.FromException<string>(failure);
    }

    /// <summary>仓库里现成的真实 PNG：用来验证 Web 侧确实能解码并送图，不是编一张假文件。</summary>
    private static string FindScreenshot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var path = Path.Combine(root.FullName, "docs", "screenshots", "01-start.png");
            if (File.Exists(path)) return path;
            root = root.Parent;
        }
        throw new FileNotFoundException("docs/screenshots/01-start.png 不存在");
    }

    /// <summary>本地 OpenAI 兼容桩：只回一份固定评分，用来验证真实 HTTP 报文与图片 data URL。</summary>
    private sealed class MockVision : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource lifetime = new();
        private Task? loop;

        public Uri BaseAddress { get; private set; } = null!;
        public string LastBody { get; private set; } = string.Empty;

        public void Start()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(BaseAddress.ToString());
            listener.Start();
            loop = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    LastBody = await reader.ReadToEndAsync();
                    const string inner = """{"scores":[{"index":1,"score":9,"hits":["水印"],"note":"右上角有水印"}]}""";
                    var payload = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = inner } } }
                    });
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                catch { }
                finally { context.Response.Close(); }
            }
        }

        public void Dispose()
        {
            lifetime.Cancel();
            listener.Stop();
            try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            listener.Close();
        }
    }
}
