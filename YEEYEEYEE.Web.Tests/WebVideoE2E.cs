using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// 真正的端到端：起真实的 Web 进程（真 AI 配置、真项目画布），走 HTTP 上传附件 → 调技能 →
/// 由服务端自己装配参考、跑校验、提交接口站、轮询、回挂。和探测通道的区别是：这里没有反射调用产品内部方法。
/// </summary>
internal static class WebVideoE2E
{
    public static async Task Run()
    {
        var projectRoot = Environment.GetEnvironmentVariable("E2E_PROJECT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek");
        var aiConfig = Environment.GetEnvironmentVariable("YEEYEEYEE__WebAiConfigPath")
            ?? Path.Combine(Environment.CurrentDirectory, "runtime", "demo-web", "config", "web-ai-config.json");
        var shotTitle = Environment.GetEnvironmentVariable("E2E_SHOT") ?? "S05";
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_SECONDS"), out var parsedSeconds) ? parsedSeconds : 4;
        var model = Environment.GetEnvironmentVariable("E2E_MODEL")
            ?? "MiniMax-H3(933)(原生人脸)(本地部署笨很多)(池17)";
        var promptFile = Environment.GetEnvironmentVariable("E2E_PROMPT_FILE")
            ?? Path.Combine(Path.GetTempPath(), "shot-s05-4s-e2e-prompt.txt");
        var prompt = File.ReadAllText(promptFile);
        var waitMinutes = int.TryParse(Environment.GetEnvironmentVariable("E2E_WAIT_MINUTES"), out var parsedWait) ? parsedWait : 30;
        var imagePath = Environment.GetEnvironmentVariable("E2E_IMAGE")
            ?? Path.Combine(projectRoot, "assets", "shot-S05-frame.png");
        var audioPath = Path.Combine(Path.GetTempPath(), "shot-voice.wav");

        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dll = Path.Combine(AppContext.BaseDirectory, "YEEYEEYEE.Web.dll");
        var port = FreePort();
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.Environment["YEEYEEYEE__WebAiConfigPath"] = aiConfig;
        start.Environment["YEEYEEYEE__AssetDirectory"] = Path.Combine(projectRoot, "assets");
        start.Environment["YEEYEEYEE__ProjectCanvasPath"] = Path.Combine(projectRoot, "canvases", "画布1.json");
        start.Environment["YEEYEEYEE__ProjectEntitiesPath"] = Path.Combine(projectRoot, "project", "entities.json");
        start.Environment["YEEYEEYEE__UserDatabasePath"] = Path.Combine(root, "users.db");
        start.Environment["YEEYEEYEE__JobDatabasePath"] = Path.Combine(root, "jobs.db");
        start.Environment["YEEYEEYEE__ChatDatabasePath"] = Path.Combine(root, "chat.db");
        start.Environment["YEEYEEYEE__SetupToken"] = "e2e-setup";
        start.Environment["YEEYEEYEE__WebSkillApprovalMode"] = "preapproved-local-image";
        start.Environment["YEEYEEYEE__WebVideoApprovalMode"] = "preapproved-local-video";
        // Web 进程启动时强制要求 ComfyUI 地址；这条端到端只走接口站，给个不可达地址占位。
        start.Environment["ComfyUI__BaseUrl"] = "http://127.0.0.1:1";
        start.Environment["ComfyUI__Checkpoint"] = "e2e-placeholder.safetensors";
        start.Environment["YEEYEEYEE_VIDEO_WAIT_MINUTES"] = waitMinutes.ToString();

        using var server = Process.Start(start)!;
        server.OutputDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine("WEB " + line.Data); };
        server.ErrorDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine("WEB! " + line.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            using var probe = new HttpClient();
            var up = false;
            for (var index = 0; index < 120 && !up; index++)
            {
                try { up = (await probe.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (!up) await Task.Delay(200);
            }
            if (!up) throw new InvalidOperationException("Web 服务没起来");

            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(waitMinutes + 5) };
            JsonElement Json(string body, string path, HttpMethod? method = null)
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using var response = client.Send(request);
                var text = response.Content.ReadAsStringAsync().Result;
                Console.WriteLine($"HTTP {(int)response.StatusCode} {path} {Truncate(text, 300)}");
                return JsonDocument.Parse(text).RootElement.Clone();
            }

            client.DefaultRequestHeaders.Add("X-Setup-Token", "e2e-setup");
            Json("{\"username\":\"e2eadmin\",\"password\":\"Password123!\"}", "/api/auth/setup");
            client.DefaultRequestHeaders.Remove("X-Setup-Token");
            var session = Json("{}", "/api/web/chat/sessions").GetProperty("id").GetString()!;
            Console.WriteLine($"SESSION {session}");

            var images = new List<string>();
            if (File.Exists(imagePath))
            {
                // 类型必须和真实字节一致：服务端会验签名，别按 .jpg 猜。
                var uploaded = Upload(session, imagePath, Path.GetExtension(imagePath).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    _ => "image/png"
                });
                if (uploaded is { Length: > 0 }) images.Add(uploaded);
            }
            if (File.Exists(audioPath))
            {
                var uploaded = Upload(session, audioPath, "audio/wav");
                if (uploaded is { Length: > 0 }) images.Add(uploaded);
            }
            if (images.Count == 0) throw new InvalidOperationException("没有可上传的参考素材");

            var nodeId = NodeIdOf(Path.Combine(projectRoot, "canvases", "画布1.json"), shotTitle);
            Console.WriteLine($"TARGET {shotTitle} {nodeId}");

            // 先验一条反面：提示词里没有台词，服务端必须 409 拒绝（这是产品校验，不是探测通道的断言）。
            var missing = Json(JsonSerializer.Serialize(new
            {
                prompt = "深夜办公室，CC 坐在显示器前。",
                model,
                seconds,
                chatSessionId = session,
                referenceAttachmentIds = images,
                targetNodeId = nodeId,
                idempotencyKey = "e2e-missing-dialogue"
            }), "/api/web/skills/web.text-to-video/invoke");
            Console.WriteLine("MISSING-DIALOGUE "
                + (missing.TryGetProperty("code", out var code) ? code.GetString() : "（没有被拒绝）"));
            // 干跑：只验启动、登录、上传与闸门，不花视频积分。
            if (Environment.GetEnvironmentVariable("E2E_DRY") == "1") { Console.WriteLine("E2E DRY DONE"); return; }

            var job = Json(JsonSerializer.Serialize(new
            {
                prompt,
                model,
                seconds,
                chatSessionId = session,
                referenceAttachmentIds = images,
                targetNodeId = nodeId,
                idempotencyKey = "e2e-real-" + Guid.NewGuid().ToString("N")
            }), "/api/web/skills/web.text-to-video/invoke");
            var jobId = job.GetProperty("jobId").GetGuid();
            Console.WriteLine($"JOB {jobId}");

            var deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
            JsonElement state;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/web/jobs/{jobId}");
                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                state = JsonDocument.Parse(text).RootElement.Clone();
                var name = state.GetProperty("state").GetString();
                Console.WriteLine($"JOB {jobId} state={name}");
                // Queued 也是在跑：服务端只是还没轮到它。
                if (name is not ("Queued" or "Pending" or "Running")) break;
                if (DateTime.UtcNow > deadline) break;
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            Console.WriteLine($"JOB FINAL {state.GetRawText()}");

            // 回挂是后台服务（每秒扫一次）做的，不是同步的：这里等它把附件写进画布。
            var canvasPath = Path.Combine(projectRoot, "canvases", "画布1.json");
            var attachments = AttachmentsOf(canvasPath, shotTitle);
            var before = attachments;
            for (var index = 0; index < 60 && attachments == before; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                attachments = AttachmentsOf(canvasPath, shotTitle);
            }
            Console.WriteLine($"CANVAS {shotTitle} attachments={attachments}");

            string? Upload(string session, string path, string type)
            {
                using var content = new ByteArrayContent(File.ReadAllBytes(path));
                content.Headers.ContentType = new MediaTypeHeaderValue(type);
                using var response = client.PostAsync(
                    $"/api/web/chat/sessions/{session}/attachments?name={Uri.EscapeDataString(Path.GetFileName(path))}",
                    content).Result;
                var text = response.Content.ReadAsStringAsync().Result;
                Console.WriteLine($"UPLOAD {Path.GetFileName(path)} -> {(int)response.StatusCode} {Truncate(text, 200)}");
                return response.IsSuccessStatusCode
                    ? JsonDocument.Parse(text).RootElement.GetProperty("id").GetString() : null;
            }
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); server.WaitForExit(); }
        }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Guid NodeIdOf(string canvasPath, string title)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(canvasPath));
        foreach (var node in document.RootElement.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray())
            if (node.GetProperty("Title").GetString()?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
                return node.GetProperty("Id").GetGuid();
        throw new InvalidOperationException("找不到分镜：" + title);
    }

    private static int AttachmentsOf(string canvasPath, string title)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(canvasPath));
        foreach (var node in document.RootElement.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray())
            if (node.GetProperty("Title").GetString()?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
                return node.GetProperty("Attachments").GetArrayLength();
        return -1;
    }
}
