using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;

internal static class VideoProbe
{
    /// <summary>
    /// 一条综合探测：multipart + reference_mode=asset，同时带图 / 音频 / 视频三类参考，
    /// 看接口站是否接受，并把任务号与状态打印出来。只发一条请求，避免误消耗额度。
    /// </summary>
    public static async Task Run()
    {
        var path = Environment.GetEnvironmentVariable("YEEYEEYEE__WebAiConfigPath")
            ?? Path.Combine(Environment.CurrentDirectory, "runtime", "demo-web", "config", "web-ai-config.json");
        var config = AiProviderSettings.Load(migrateSecrets: false, filePath: path, applyEnvironmentOverrides: false);
        var key = SecretProtector.Unprotect(config.VideoApiKey);
        var baseUrl = config.VideoEndpoint.TrimEnd('/');
        // 探测要换便宜的池子时用 PROBE_MODEL 覆盖，不去改用户的配置文件。
        var model = Environment.GetEnvironmentVariable("PROBE_MODEL") is { Length: > 0 } overrideModel
            ? overrideModel : config.VideoModel;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key ?? "");

        // 0) 余额：确认探测任务是否真的扣了费（失败会自动退回）
        using (var balance = await http.GetAsync(baseUrl + "/user/balance"))
        {
            var body = Encoding.UTF8.GetString(await balance.Content.ReadAsByteArrayAsync());
            Console.WriteLine($"BALANCE -> {(int)balance.StatusCode} {body}");
        }
        if (Environment.GetEnvironmentVariable("PROBE_ONLY_BALANCE") == "1") return;
        if (Environment.GetEnvironmentVariable("PROBE_LIST_TASKS") == "1")
        {
            // 只看清单，不提交、不扣费：用来找「提交没等到回执、但服务端其实已经建了」的任务。
            using var list = await http.GetAsync(baseUrl + "/videos");
            var text = Encoding.UTF8.GetString(await list.Content.ReadAsByteArrayAsync());
            Console.WriteLine($"TASKS -> {(int)list.StatusCode} {text}");
            return;
        }
        if (Environment.GetEnvironmentVariable("PROBE_ONLY_TASK") is { Length: > 0 } taskToCheck)
        {
            using var existing = await http.GetAsync($"{baseUrl}/videos/{taskToCheck}");
            var text = Encoding.UTF8.GetString(await existing.Content.ReadAsByteArrayAsync());
            Console.WriteLine($"TASK {taskToCheck} -> {(int)existing.StatusCode} {text}");
            // 轮询超时但服务端其实跑完了：按任务号把成品取回来，不必重跑。
            if (Environment.GetEnvironmentVariable("PROBE_DOWNLOAD") is { Length: > 0 } downloadOnly
                && text.Contains("\"status\":\"completed\""))
            {
                Directory.CreateDirectory(downloadOnly);
                var saved = Path.Combine(downloadOnly, taskToCheck + ".mp4");
                var contentUrl = $"{baseUrl}/videos/{taskToCheck}/content";
                // 接口站的 /content 今天单连接只有几十 KB/s，还会中途停住。改成多段并行 Range 拉，
                // 每段自己重试；拿不到总长度才退回单连接流式。
                async Task<byte[]?> ChunkAsync(long from, long to)
                {
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
                            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                            if (response.StatusCode is not (System.Net.HttpStatusCode.PartialContent or System.Net.HttpStatusCode.OK))
                                return null;
                            return await response.Content.ReadAsByteArrayAsync();
                        }
                        catch (Exception error) when (error is IOException or TaskCanceledException or HttpRequestException)
                        {
                            Console.WriteLine($"CHUNK {from}-{to} retry {attempt}: {error.GetType().Name}");
                        }
                    }
                    return null;
                }

                long total;
                using (var probe = new HttpRequestMessage(HttpMethod.Get, contentUrl))
                {
                    probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
                    total = response.Content.Headers.ContentRange?.Length ?? 0;
                }
                if (total <= 0)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    await using var file = File.Create(saved);
                    await response.Content.CopyToAsync(file);
                }
                else
                {
                    const int parts = 6;
                    var span = (total + parts - 1) / parts;
                    var buffers = await Task.WhenAll(Enumerable.Range(0, parts).Select(async index =>
                    {
                        var from = index * span;
                        var to = Math.Min(total - 1, from + span - 1);
                        return from > to ? Array.Empty<byte>() : await ChunkAsync(from, to);
                    }));
                    // 有一段没拿到就整份不落盘：宁可下次重来，也不要写出一个长度对不上的坏 MP4。
                    if (buffers.Any(buffer => buffer is null))
                    {
                        Console.WriteLine("DOWNLOAD incomplete · 有分段没取到，未落盘");
                        return;
                    }
                    await using var file = File.Create(saved);
                    for (var index = 0; index < buffers.Length; index++)
                        await file.WriteAsync(buffers[index]!);
                    Console.WriteLine($"PART {parts}/{parts} 完成");
                }
                Console.WriteLine($"SAVED {saved} · {new FileInfo(saved).Length} bytes (total {total})");
            }
            return;
        }

        // 1) 模型能力（服务端自报，比猜测可靠）
        using (var models = await http.GetAsync(baseUrl + "/models"))
        {
            var body = await models.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var listAll = Environment.GetEnvironmentVariable("PROBE_LIST_MODELS") == "1";
            foreach (var entry in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                if (listAll)
                {
                    // 成本最小的一步：只读清单，不提交任务。整条原样打印，价目字段以服务端为准。
                    Console.WriteLine(entry.GetRawText());
                    continue;
                }
                if (!string.Equals(id, model, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"MODEL {id} images={entry.GetProperty("max_reference_images")} "
                    + $"audios={entry.GetProperty("max_audio_references")} videos={entry.GetProperty("max_video_references")} "
                    + $"audioRef={entry.GetProperty("audio_reference_enabled")} "
                    + $"durations={entry.GetProperty("supported_durations")}");
            }
            if (listAll) return;
        }

        // 2) 准备三类参考：一张现成图、一段合成静音 WAV、一段现成 MP4
        var assets = Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek", "assets");
        var image = Environment.GetEnvironmentVariable("PROBE_IMAGE") is { Length: > 0 } imageOverride
            ? imageOverride
            : Directory.EnumerateFiles(assets, "*.png").Where(file => new FileInfo(file).Length > 1000).OrderBy(file => new FileInfo(file).Length).First();
        var video = Environment.GetEnvironmentVariable("PROBE_VIDEO") is { Length: > 0 } videoOverride
            ? videoOverride
            : Directory.EnumerateFiles(assets, "*.mp4").OrderBy(file => new FileInfo(file).Length).First();
        var audio = Path.Combine(Path.GetTempPath(), "probe-silence.wav");
        WriteSilenceWav(audio, seconds: 1);
        Console.WriteLine($"probe image={Path.GetFileName(image)} ({new FileInfo(image).Length / 1024}KB) "
            + $"audio={new FileInfo(audio).Length / 1024}KB video={Path.GetFileName(video)} ({new FileInfo(video).Length / 1024}KB)");

        // 3) WAF 已放行多文件 multipart；1x1 占位图被判「corrupt or unsupported」，
        //    换成真实 PNG + 1 秒静音 WAV + 一段真实 MP4，验证三类 part 名与格式。
        // 各池子认的参数不完全一样（有人不收 reference_mode，有人只收 768p），
        // 所以 size / reference_mode 可由环境变量改写，空串表示这次不带。
        var sizeText = Environment.GetEnvironmentVariable("PROBE_SIZE") ?? "720x1280";
        var referenceMode = Environment.GetEnvironmentVariable("PROBE_MODE") ?? "asset";
        // PowerShell 里把变量设成空串等于删掉它，所以「这次不带」用 none 表示。
        if (sizeText == "none") sizeText = string.Empty;
        if (referenceMode == "none") referenceMode = string.Empty;
        var prompt = Environment.GetEnvironmentVariable("PROBE_PROMPT") is { Length: > 0 } promptOverride
            ? promptOverride : "capability probe: image, audio and video reference";
        using var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent("4"), "seconds" }
        };
        if (sizeText.Length > 0) form.Add(new StringContent(sizeText), "size");
        if (referenceMode.Length > 0) form.Add(new StringContent(referenceMode), "reference_mode");
        var refs = Environment.GetEnvironmentVariable("PROBE_REFS") ?? "iav";
        if (refs.Contains('i')) AddFile(form, image, "input_reference");
        if (refs.Contains('a')) AddFile(form, audio, "reference_audio");
        if (refs.Contains('v')) AddFile(form, video, "reference_video");
        using var mediaMessage = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/videos") { Content = form };
        mediaMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? "");
        string? probeId = null;
        Console.WriteLine($"POST model={model} seconds=4 refs={refs} size={(sizeText.Length > 0 ? sizeText : "(omit)")} "
            + $"reference_mode={(referenceMode.Length > 0 ? referenceMode : "(omit)")} -> {baseUrl}/videos");
        using (var response = await http.SendAsync(mediaMessage))
        {
            var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
            Console.WriteLine($"POST image+audio+video(multipart) -> {(int)response.StatusCode} {(body.Length > 400 ? body[..400] : body)}");
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                probeId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
        }
        var completed = false;
        for (var round = 0; round < 80 && probeId is not null; round++)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            using var status = await http.GetAsync($"{baseUrl}/videos/{probeId}");
            var body = Encoding.UTF8.GetString(await status.Content.ReadAsByteArrayAsync());
            var done = body.Contains("\"status\":\"completed\"");
            // 完成时打全文：里面常带 has_audio / duration 这类字段，判「有没有台词」要用。
            Console.WriteLine($"STATUS {probeId} -> {(int)status.StatusCode} "
                + (done || body.Length <= 600 ? body : body[..600]));
            if (done) { completed = true; break; }
            if (body.Contains("\"status\":\"failed\"") || body.Contains("\"error\":{\"message\"")) break;
        }
        if (!completed || probeId is null) return;
        if (Environment.GetEnvironmentVariable("PROBE_DOWNLOAD") is not { Length: > 0 } downloadDir) return;
        Directory.CreateDirectory(downloadDir);
        var target = Path.Combine(downloadDir, probeId + ".mp4");
        using (var content = await http.GetAsync($"{baseUrl}/videos/{probeId}/content"))
        {
            if (!content.IsSuccessStatusCode)
            {
                Console.WriteLine($"DOWNLOAD -> {(int)content.StatusCode}");
                return;
            }
            await using var file = File.Create(target);
            await content.Content.CopyToAsync(file);
        }
        var info = Mp4Concatenator.Probe(target);
        var bytes = File.ReadAllBytes(target);
        var hasAudio = bytes.AsSpan().IndexOf("smhd"u8) >= 0;
        Console.WriteLine($"SAVED {target} · {new FileInfo(target).Length} bytes · {info.Describe()}");
        Console.WriteLine($"AUDIO TRACK -> {(hasAudio ? "yes" : "no")}");
    }

    /// <summary>
    /// 把探测通道里已经生成好的产物，按产品自己的回挂通道挂到指定分镜。
    /// 走 <c>ProjectCanvasSceneStore.AttachJobOutputs</c>：证据（sourceFingerprints）按当前画布现算，
    /// 附件 ID 与真实任务同一条规则——**不手改画布 JSON**。
    /// </summary>
    public static void ImportOutput()
    {
        var projectRoot = Environment.GetEnvironmentVariable("PROBE_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var assetName = Environment.GetEnvironmentVariable("PROBE_ASSET") ?? "s07-dialogue-4s.mp4";
        var nodeId = Guid.Parse(Environment.GetEnvironmentVariable("PROBE_NODE") ?? "ae7268a3-d35d-436f-9a2b-f4637e351c6e");
        var providerTask = Environment.GetEnvironmentVariable("PROBE_TASK") ?? "evt-TOQISLJWATHF";
        var prompt = Environment.GetEnvironmentVariable("PROBE_PROMPT") ?? string.Empty;
        var model = Environment.GetEnvironmentVariable("PROBE_MODEL") ?? "Wan3-720p(池13)(满血)";

        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(projectRoot, "assets"));
        var project = ProjectContext.Open(projectRoot) ?? throw new InvalidOperationException("项目打不开：" + projectRoot);
        AppPaths.UseProject(project);

        var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
        var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
        var store = Activator.CreateInstance(storeType,
            Path.Combine(projectRoot, "canvases", "画布1.json"),
            Path.Combine(projectRoot, "project", "entities.json"))!;
        var target = storeType.GetMethod("GenerationTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, new object[] { nodeId })!;
        var canvas = (WorkflowCanvasState)target.GetType().GetField("Item1")!.GetValue(target)!;
        var node = (WorkflowNode)target.GetType().GetField("Item2")!.GetValue(target)!;
        var revision = (long)target.GetType().GetField("Item3")!.GetValue(target)!;
        var locate = (Func<WorkflowAttachment, string?>)assembly.GetType("YEEYEEYEE.Web.WebGenerationReferences")!
            .GetMethod("Locate", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<WorkflowAttachment, string?>));
        var snapshot = ReferenceStaleness.Snapshot(canvas, node, locate);
        var inputs = new Dictionary<string, JsonElement>
        {
            ["targetNodeId"] = JsonSerializer.SerializeToElement(nodeId),
            ["targetBaseRevision"] = JsonSerializer.SerializeToElement(revision),
            ["prompt"] = JsonSerializer.SerializeToElement(prompt),
            ["model"] = JsonSerializer.SerializeToElement(model),
            ["externalTaskId"] = JsonSerializer.SerializeToElement(providerTask),
            ["sourceFingerprints"] = JsonSerializer.SerializeToElement(snapshot)
        };
        var job = new ExecutionResult
        {
            JobId = Guid.NewGuid(),
            State = JobState.Succeeded,
            Tool = "web.text-to-video",
            Capability = Capability.TextToVideo,
            ExternalTaskId = providerTask,
            Inputs = inputs,
            Outputs = new[] { new AssetRef { Ref = "asset://" + assetName, Role = "video" } }
        };
        var result = storeType.GetMethod("AttachJobOutputs")!.Invoke(store, new object[] { job, nodeId });
        var status = result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult code ? code.StatusCode : 200;
        Console.WriteLine($"ATTACH {node.Title} · {assetName} · task={providerTask} -> {status}");
        if (status != 200) Console.WriteLine(result);
    }

    /// <summary>
    /// 跑一条真实镜头：把台词写进画布 → 按产品规则装配参考 → 提交 → 成功后回挂。
    /// 只跑一镜，参数全走环境变量，避免误消耗额度。
    /// </summary>
    public static async Task RunShot()
    {
        var projectRoot = Environment.GetEnvironmentVariable("SHOT_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var shotTitle = Environment.GetEnvironmentVariable("SHOT_TITLE") ?? "S05";
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("SHOT_SECONDS"), out var parsed) ? parsed : 15;
        var model = Environment.GetEnvironmentVariable("SHOT_MODEL") ?? "MiniMax-H3(933)(原生人脸)(本地部署笨很多)(池17)";
        var dialogue = Environment.GetEnvironmentVariable("SHOT_DIALOGUE") ?? "CC：……好像也没那么难。";
        // PowerShell 把 $env:X="" 当成「删除变量」，所以「无台词」用一个显式哨兵，避免退化成默认那句。
        if (string.Equals(dialogue, "none", StringComparison.OrdinalIgnoreCase)) dialogue = string.Empty;
        var promptFile = Environment.GetEnvironmentVariable("SHOT_PROMPT_FILE") ?? string.Empty;
        var prompt = promptFile.Length > 0 && File.Exists(promptFile)
            ? File.ReadAllText(promptFile)
            : "深夜的办公室。CC 坐在显示器前，侧头看向镜头，轻声说：“……好像也没那么难。”";
        var audioSource = Environment.GetEnvironmentVariable("SHOT_AUDIO")
            ?? Path.Combine(projectRoot, "assets", "s07-dialogue-4s.mp4");

        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(projectRoot, "assets"));
        var project = ProjectContext.Open(projectRoot) ?? throw new InvalidOperationException("项目打不开：" + projectRoot);
        AppPaths.UseProject(project);
        var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
        var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
        var canvasPath = Path.Combine(projectRoot, "canvases", "画布1.json");
        var store = Activator.CreateInstance(storeType, canvasPath, Path.Combine(projectRoot, "project", "entities.json"))!;
        var targetMethod = storeType.GetMethod("GenerationTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 1) 台词写进画布：走 ReplaceCanvas（带修订校验与备份），不手改 JSON。
        var state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
        var shot = state.Canvas.Nodes.FirstOrDefault(node => node.Title.Contains(shotTitle, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("找不到分镜：" + shotTitle);
        if (!string.Equals(shot.Parameters.GetValueOrDefault(VoiceCasting.DialogueParameter), dialogue, StringComparison.Ordinal))
        {
            shot.Parameters[VoiceCasting.DialogueParameter] = dialogue;
            var before = targetMethod.Invoke(store, new object[] { shot.Id })!;
            var revision = (long)before.GetType().GetField("Item3")!.GetValue(before)!;
            var replaced = storeType.GetMethod("ReplaceCanvas")!
                .Invoke(store, new object[] { revision, JsonSerializer.SerializeToUtf8Bytes(state) })!;
            Console.WriteLine($"DIALOGUE {shot.Title} <- {dialogue} · replace={(replaced is Microsoft.AspNetCore.Http.IStatusCodeHttpResult status ? status.StatusCode : 200)}");
        }
        else Console.WriteLine($"DIALOGUE {shot.Title} already = {dialogue}");

        var assets = Path.Combine(projectRoot, "assets");
        Directory.CreateDirectory(assets);
        var locate = (Func<WorkflowAttachment, string?>)assembly.GetType("YEEYEEYEE.Web.WebGenerationReferences")!
            .GetMethod("Locate", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<WorkflowAttachment, string?>));
        var configPath = Environment.GetEnvironmentVariable("YEEYEEYEE__WebAiConfigPath")
            ?? Path.Combine(Environment.CurrentDirectory, "runtime", "demo-web", "config", "web-ai-config.json");
        var config = AiProviderSettings.Load(migrateSecrets: false, filePath: configPath, applyEnvironmentOverrides: false);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };

        // 2) 首帧依据过期时产品会硬拒（不许补记）。先按产品规则自检，只有真的过期才重出，避免白花钱。
        var refreshMode = Environment.GetEnvironmentVariable("SHOT_REFRESH_FRAME");
        if (refreshMode != "0")
        {
            var current = targetMethod.Invoke(store, new object[] { shot.Id })!;
            var frameCanvas = (WorkflowCanvasState)current.GetType().GetField("Item1")!.GetValue(current)!;
            var frameNode = (WorkflowNode)current.GetType().GetField("Item2")!.GetValue(current)!;
            var frameRevision = (long)current.GetType().GetField("Item3")!.GetValue(current)!;
            var preflight = GenerationAudit.CheckVideoAssets(frameCanvas, frameNode, locate);
            foreach (var warning in preflight.Errors) Console.WriteLine($"PREFLIGHT {warning}");
            if (refreshMode != "force" && preflight.Errors.Count == 0)
            {
                Console.WriteLine($"FRAME ok · 当前首帧仍可作为提交依据（{Path.GetFileName(preflight.AdoptedFrame)}），跳过重出");
            }
            else
            {
                var frameReferences = ReferenceImagePicker.ResolveSlots(frameCanvas, frameNode, locate)
                    .Select(slot => slot.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
                // 诊断用：只带第一张参考，验证「多图 edits 被网关拒绝」这个猜测。
                if (Environment.GetEnvironmentVariable("SHOT_FRAME_REFS") == "1")
                    frameReferences = frameReferences.Take(1).ToArray();
                var imageConfig = AiProviderSettings.Load(migrateSecrets: false, filePath: configPath, applyEnvironmentOverrides: false);
                imageConfig.ImageApiKey = SecretProtector.Unprotect(imageConfig.ImageApiKey)
                    ?? throw new InvalidOperationException("图片密钥解不开，未重出首帧");
                var imageProvider = new OpenAiCompatibleImageProvider(imageConfig, http);
                // 出图可以换池子：某个池卡住时不用改用户的配置文件。
                var imageModel = Environment.GetEnvironmentVariable("SHOT_IMAGE_MODEL") is { Length: > 0 } modelOverride
                    ? modelOverride : imageConfig.ImageModel;
                var width = int.TryParse(Environment.GetEnvironmentVariable("SHOT_FRAME_WIDTH"), out var parsedWidth) ? parsedWidth : 720;
                var height = int.TryParse(Environment.GetEnvironmentVariable("SHOT_FRAME_HEIGHT"), out var parsedHeight) ? parsedHeight : 1280;
                Console.WriteLine($"FRAME submit model={imageModel} refs={frameReferences.Length} {width}x{height}");
                using var frameTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var frame = await imageProvider.GenerateAsync(new ImageGenerationRequest
                {
                    Prompt = frameNode.Content,
                    Model = imageModel,
                    ReferenceImages = frameReferences,
                    Width = width,
                    Height = height
                }, frameTimeout.Token);
                Console.WriteLine($"FRAME {frame.Status} file={frame.FilePath} model={frame.Model} error={frame.Error}");
                if (frame.Status != ImageGenerationStatus.Succeeded || !File.Exists(frame.FilePath))
                    throw new InvalidOperationException("首帧重出失败，未提交视频");
                if (Environment.GetEnvironmentVariable("SHOT_FRAME_ONLY") == "1")
                {
                    Console.WriteLine("FRAME only · 只验证出图，不采用、不出视频");
                    return;
                }
                var frameName = $"shot-{shotTitle}-frame{Path.GetExtension(frame.FilePath)}";
                File.Copy(frame.FilePath, Path.Combine(assets, frameName), overwrite: true);
                var frameJob = new ExecutionResult
                {
                    JobId = Guid.NewGuid(),
                    State = JobState.Succeeded,
                    Tool = "comfyui.text-to-image",
                    Capability = Capability.TextToImage,
                    Inputs = new Dictionary<string, JsonElement>
                    {
                        ["targetNodeId"] = JsonSerializer.SerializeToElement(shot.Id),
                        ["targetBaseRevision"] = JsonSerializer.SerializeToElement(frameRevision),
                        ["prompt"] = JsonSerializer.SerializeToElement(frameNode.Content),
                        ["model"] = JsonSerializer.SerializeToElement(frame.Model),
                        ["sourceFingerprints"] = JsonSerializer.SerializeToElement(ReferenceStaleness.Snapshot(frameCanvas, frameNode, locate))
                    },
                    Outputs = new[] { new AssetRef { Ref = "asset://" + frameName, Role = "image" } }
                };
                var frameAttach = storeType.GetMethod("AttachJobOutputs")!.Invoke(store, new object[] { frameJob, shot.Id });
                Console.WriteLine($"FRAME adopt {frameName} -> {(frameAttach is Microsoft.AspNetCore.Http.IStatusCodeHttpResult frameCode ? frameCode.StatusCode : 200)} {frameAttach}");
            }
        }

        // 3) 参考图按产品规则装配（首帧在前，实体引用按角色→道具→场景），容量取模型自报的 9。
        var target = targetMethod.Invoke(store, new object[] { shot.Id })!;
        var diagnosticNode = (WorkflowNode)target.GetType().GetField("Item2")!.GetValue(target)!;
        Console.WriteLine($"SHOT refs={diagnosticNode.References.Count} "
            + $"need=角色{diagnosticNode.AssetRequirements?.Character}/道具{diagnosticNode.AssetRequirements?.Prop}/场景{diagnosticNode.AssetRequirements?.Scene} "
            + $"attachments={diagnosticNode.Attachments.Count} "
            + $"fingerprints=[{string.Join(",", diagnosticNode.Attachments.Select(item => item.SourceFingerprints.Count))}]");
        var references = assembly.GetType("YEEYEEYEE.Web.WebGenerationReferences")!
            .GetMethod("ResolveVideoTarget", BindingFlags.NonPublic | BindingFlags.Static)!;
        var images = (string[])references.Invoke(null, new object[] { target, Array.Empty<string>(), 9 })!;
        Console.WriteLine($"REFS {images.Length}: {string.Join(", ", images.Select(Path.GetFileName))}");

        // 3) 参考音：把 S07 那条带台词的音频从 MP4 里解出来（系统 Media Foundation，不装 ffmpeg）。
        // SHOT_AUDIO=none 时不带音色参考：说话人不是 CC/DP（比如 S03 的用户来信），套主角音色反而错。
        var audios = Array.Empty<string>();
        if (!string.Equals(audioSource, "none", StringComparison.OrdinalIgnoreCase))
        {
            var wav = Path.Combine(Path.GetTempPath(), "shot-voice.wav");
            using (var reader = new NAudio.Wave.MediaFoundationReader(audioSource))
            using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(16000, 16, 1)))
                NAudio.Wave.WaveFileWriter.CreateWaveFile(wav, resampler);
            Console.WriteLine($"AUDIO {Path.GetFileName(audioSource)} -> {new FileInfo(wav).Length / 1024}KB wav");
            audios = new[] { wav };
        }

        // 4) 提交
        config.VideoApiKey = SecretProtector.Unprotect(config.VideoApiKey)
            ?? throw new InvalidOperationException("视频密钥解不开，未提交");
        var provider = new HttpVideoProvider(config, http);
        Console.WriteLine($"SUBMIT model={model} seconds={seconds} images={images.Length} audios={audios.Length}");
        var result = await provider.GenerateAsync(new VideoGenerationRequest
        {
            Prompt = prompt,
            Model = model,
            Seconds = seconds,
            ReferenceImages = images,
            ReferenceAudios = audios
        });
        Console.WriteLine($"RESULT {result.Status} task={result.ExternalTaskId} file={result.FilePath} note={result.Note} error={result.Error}");
        if (result.Status != VideoGenerationStatus.Succeeded || !File.Exists(result.FilePath)) return;

        // 5) 回挂：附件落进项目 assets，再走产品自己的 AttachJobOutputs。
        var assetName = Environment.GetEnvironmentVariable("SHOT_ASSET") ?? $"shot-{shotTitle}-{seconds}s.mp4";
        File.Copy(result.FilePath, Path.Combine(assets, assetName), overwrite: true);
        var fresh = targetMethod.Invoke(store, new object[] { shot.Id })!;
        var freshCanvas = (WorkflowCanvasState)fresh.GetType().GetField("Item1")!.GetValue(fresh)!;
        var freshNode = (WorkflowNode)fresh.GetType().GetField("Item2")!.GetValue(fresh)!;
        var freshRevision = (long)fresh.GetType().GetField("Item3")!.GetValue(fresh)!;
        var job = new ExecutionResult
        {
            JobId = Guid.NewGuid(),
            State = JobState.Succeeded,
            Tool = "web.text-to-video",
            Capability = Capability.TextToVideo,
            ExternalTaskId = result.ExternalTaskId,
            Inputs = new Dictionary<string, JsonElement>
            {
                ["targetNodeId"] = JsonSerializer.SerializeToElement(shot.Id),
                ["targetBaseRevision"] = JsonSerializer.SerializeToElement(freshRevision),
                ["prompt"] = JsonSerializer.SerializeToElement(prompt),
                ["model"] = JsonSerializer.SerializeToElement(model),
                ["externalTaskId"] = JsonSerializer.SerializeToElement(result.ExternalTaskId),
                ["sourceFingerprints"] = JsonSerializer.SerializeToElement(ReferenceStaleness.Snapshot(freshCanvas, freshNode, locate))
            },
            Outputs = new[] { new AssetRef { Ref = "asset://" + assetName, Role = "video" } }
        };
        var attached = storeType.GetMethod("AttachJobOutputs")!.Invoke(store, new object[] { job, shot.Id });
        Console.WriteLine($"ATTACH {shot.Title} · {assetName} -> {(attached is Microsoft.AspNetCore.Http.IStatusCodeHttpResult code ? code.StatusCode : 200)} {attached}");
    }

    /// <summary>把一版成片按分镜顺序接起来（无损拼接，不重新编码）。输出落进项目 assets。</summary>
    public static void Assemble()
    {
        var projectRoot = Environment.GetEnvironmentVariable("SHOT_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var outputName = Environment.GetEnvironmentVariable("ASSEMBLE_OUTPUT") ?? "film-draft.mp4";
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(projectRoot, "assets"));
        var project = ProjectContext.Open(projectRoot) ?? throw new InvalidOperationException("项目打不开：" + projectRoot);
        AppPaths.UseProject(project);
        var state = JsonSerializer.Deserialize<RecentCanvasState>(
            File.ReadAllBytes(Path.Combine(projectRoot, "canvases", "画布1.json")))!;
        var product = state.Canvas.Nodes.FirstOrDefault(node => node.Title.Contains("成片"))
            ?? throw new InvalidOperationException("找不到成片节点");
        var output = Path.Combine(projectRoot, "assets", outputName);
        var result = ProductVideoAssembly.Concat(state.Canvas, product, output);
        Console.WriteLine($"ASSEMBLE ok={result.Ok} file={result.FilePath} error={result.Error} note={result.Note}");
    }

    /// <summary>
    /// 从一个分镜节点上摘掉指定附件（按 asset 文件名匹配），走产品自己的 RemoveAttachment：
    /// 带修订校验与备份，不手改画布 JSON。用来清掉作废的那几版产物。
    /// </summary>
    public static void RemoveAttachments()
    {
        var projectRoot = Environment.GetEnvironmentVariable("SHOT_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var targets = (Environment.GetEnvironmentVariable("REMOVE_LIST") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Title: parts[0].Trim(), Asset: parts[1].Trim()))
            .ToArray();

        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(projectRoot, "assets"));
        var project = ProjectContext.Open(projectRoot) ?? throw new InvalidOperationException("项目打不开：" + projectRoot);
        AppPaths.UseProject(project);
        var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
        var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
        var store = Activator.CreateInstance(storeType,
            Path.Combine(projectRoot, "canvases", "画布1.json"),
            Path.Combine(projectRoot, "project", "entities.json"))!;
        var targetMethod = storeType.GetMethod("GenerationTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var removeMethod = storeType.GetMethod("RemoveAttachment")!;

        foreach (var (title, asset) in targets)
        {
            var state = JsonSerializer.Deserialize<RecentCanvasState>(
                File.ReadAllBytes(Path.Combine(projectRoot, "canvases", "画布1.json")))!;
            var node = state.Canvas.Nodes.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (node is null) { Console.WriteLine($"REMOVE MISS node {title}"); continue; }
            var attachment = node.Attachments.LastOrDefault(item =>
                string.Equals(item.Reference, "asset://" + asset, StringComparison.OrdinalIgnoreCase));
            if (attachment is null)
            {
                Console.WriteLine($"REMOVE MISS {asset} on {node.Title}（当前 {node.Attachments.Count} 个附件）");
                continue;
            }
            var current = targetMethod.Invoke(store, new object[] { node.Id })!;
            var revision = (long)current.GetType().GetField("Item3")!.GetValue(current)!;
            var result = removeMethod.Invoke(store, new object[] { revision, node.Id, attachment.Id })!;
            var status = result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult code ? code.StatusCode : 200;
            Console.WriteLine($"REMOVE {node.Title} · {asset} -> {status}");
        }
    }

    /// <summary>
    /// 批量把台词写进画布：输入是一份 { 分镜标题: 台词文本 } 的 JSON。
    /// 走 ReplaceCanvas（修订校验 + 备份），不手改画布文件；格式非法（没有冒号）会按旁白解析，这里只提示不拦。
    /// </summary>
    public static void WriteDialogues()
    {
        var projectRoot = Environment.GetEnvironmentVariable("SHOT_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var scriptPath = Environment.GetEnvironmentVariable("DIALOGUE_FILE")
            ?? throw new InvalidOperationException("缺少 DIALOGUE_FILE");
        var script = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(scriptPath))
            ?? throw new InvalidOperationException("台词文件不是 JSON 对象");

        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(projectRoot, "assets"));
        var project = ProjectContext.Open(projectRoot) ?? throw new InvalidOperationException("项目打不开：" + projectRoot);
        AppPaths.UseProject(project);
        var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
        var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
        var canvasPath = Path.Combine(projectRoot, "canvases", "画布1.json");
        var store = Activator.CreateInstance(storeType, canvasPath, Path.Combine(projectRoot, "project", "entities.json"))!;
        var state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;

        var changed = new List<string>();
        var changedIds = new List<Guid>();
        foreach (var (title, text) in script)
        {
            var node = state.Canvas.Nodes.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (node is null) { Console.WriteLine($"DIALOGUE MISS {title}"); continue; }
            var parsed = VoiceCasting.Parse(text);
            var speakers = string.Join("、", parsed.Select(line => line.Speaker.Length > 0 ? line.Speaker : "旁白"));
            Console.WriteLine($"DIALOGUE {node.Title}: {parsed.Count} 句 · 说话人={speakers}");

            if (string.Equals(node.Parameters.GetValueOrDefault(VoiceCasting.DialogueParameter), text, StringComparison.Ordinal))
            { Console.WriteLine($"DIALOGUE {node.Title} unchanged"); continue; }
            node.Parameters[VoiceCasting.DialogueParameter] = text;
            changed.Add(node.Title);
            changedIds.Add(node.Id);
        }
        if (changedIds.Count == 0) { Console.WriteLine("DIALOGUE nothing to write"); return; }

        var target = storeType.GetMethod("GenerationTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, new object[] { changedIds[0] })!;
        var revision = (long)target.GetType().GetField("Item3")!.GetValue(target)!;
        var replaced = storeType.GetMethod("ReplaceCanvas")!
            .Invoke(store, new object[] { revision, JsonSerializer.SerializeToUtf8Bytes(state) })!;
        Console.WriteLine($"DIALOGUE saved {string.Join(",", changed)} -> "
            + $"{(replaced is Microsoft.AspNetCore.Http.IStatusCodeHttpResult code ? code.StatusCode : 200)}");
    }

    private static void AddFile(MultipartFormDataContent form, string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(path));
        form.Add(part, name, Path.GetFileName(path));
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime",
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4",
        _ => "application/octet-stream"
    };

    /// <summary>合成 1 秒 16kHz 单声道静音 WAV：不需要 ffmpeg，也足够让服务端认出这是音频。</summary>
    private static void WriteSilenceWav(string path, int seconds)
    {
        const int rate = 16000;
        var samples = rate * seconds;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }
}
